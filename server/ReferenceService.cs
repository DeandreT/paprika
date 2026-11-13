using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PaperworkClassifier;

public sealed class ReferenceService
{
    private readonly Store store;
    private readonly ConnectorCatalog connectors;
    private readonly OperationsPlatformStore? operations;

    public ReferenceService(
        Store store,
        ConnectorCatalog connectors,
        OperationsPlatformStore? operations = null
    )
    {
        this.store = store;
        this.connectors = connectors;
        this.operations = operations;
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "CREATE TABLE IF NOT EXISTS reference_datasets(id TEXT NOT NULL,version INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(id,version)); CREATE TABLE IF NOT EXISTS reference_results(packet_id TEXT NOT NULL,revision INTEGER NOT NULL,document_id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(packet_id,revision,document_id));";
        q.ExecuteNonQuery();
        ReferencePolicies.Initialize(c);
        using var tx = c.BeginTransaction();
        using var reset = c.CreateCommand();
        reset.Transaction = tx;
        reset.CommandText = "DELETE FROM reference_connector_bindings";
        reset.ExecuteNonQuery();
        foreach (var connector in connectors.All().Where(item => item.Kind == "httpLookup"))
        {
            // Validate the endpoint without sending anything. Persist only its public binding hash.
            var pair = connectors.Request(connector.Id, "httpLookup", HttpMethod.Post);
            pair.Client.Dispose();
            pair.Request.Dispose();
            using var insert = c.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO reference_connector_bindings VALUES($id,$hash)";
            insert.Parameters.AddWithValue("$id", connector.Id);
            insert.Parameters.AddWithValue("$hash", connectors.BindingHash(connector.Id));
            insert.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public ReferenceDataset[] Datasets()
    {
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT json FROM reference_datasets ORDER BY id,version DESC";
        using var r = q.ExecuteReader();
        var result = new List<ReferenceDataset>();
        while (r.Read())
            result.Add(
                JsonSerializer.Deserialize<ReferenceDataset>(r.GetString(0), Contract.Json)!
            );
        return result.ToArray();
    }

    public ReferenceDataset Create(ReferenceDatasetRequest request, string? actor = null)
    {
        if (
            !System.Text.RegularExpressions.Regex.IsMatch(request.Id ?? "", "^[a-zA-Z0-9_-]{1,80}$")
            || string.IsNullOrWhiteSpace(request.Name)
            || request.Name.Length > 200
            || request.Entries is not { Length: > 0 and <= 10000 }
            || request.Entries.Any(e =>
                e is null
                || string.IsNullOrWhiteSpace(e.Reference)
                || e.Reference.Length > 500
                || string.IsNullOrWhiteSpace(e.RecordId)
                || e.RecordId.Length > 200
                || e.Label is null
                || e.Label.Length > 500
            )
        )
            throw new InvalidDataException(
                "Dataset requires an ID, name and 1–10000 bounded records."
            );
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT COALESCE(MAX(version),0)+1 FROM reference_datasets WHERE id=$id";
        q.Parameters.AddWithValue("$id", request.Id!);
        var version = Convert.ToInt32(q.ExecuteScalar());
        var entries = request.Entries.Select(e => e with { }).ToArray();
        var dataset = new ReferenceDataset(
            request.Id!,
            version,
            request.Name,
            request.Synthetic,
            ReferencePolicies.Hash(
                new
                {
                    request.Id,
                    version,
                    request.Name,
                    request.Synthetic,
                    entries,
                }
            ),
            DateTimeOffset.UtcNow,
            entries,
            actor
        );
        q.CommandText = "INSERT INTO reference_datasets VALUES($id,$version,$json)";
        q.Parameters.AddWithValue("$version", version);
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(dataset, Contract.Json));
        q.ExecuteNonQuery();
        tx.Commit();
        return dataset;
    }

    public ReferenceResult[] Results(string packetId)
    {
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "SELECT json FROM reference_results WHERE packet_id=$id ORDER BY revision DESC,document_id";
        q.Parameters.AddWithValue("$id", packetId);
        using var r = q.ExecuteReader();
        var rows = new List<ReferenceResult>();
        while (r.Read())
            rows.Add(JsonSerializer.Deserialize<ReferenceResult>(r.GetString(0), Contract.Json)!);
        return rows.ToArray();
    }

    public async Task ProcessAsync(Packet packet, CancellationToken ct)
    {
        if (packet.EffectiveWorkflow.Capabilities?.Reference is null)
            return;
        await LookupAsync(new(packet.Id, packet.Revision), ct, packet);
    }

    public async Task<ReferenceResult[]> LookupAsync(
        ReferenceLookupRequest request,
        CancellationToken ct,
        Packet? processingPacket = null,
        string? actor = null
    )
    {
        var packet = processingPacket
            ?? store.Get(request.PacketId)
            ?? throw new KeyNotFoundException("Packet not found.");
        if (packet.Revision != request.Revision)
            throw new InvalidOperationException("Revision changed.");
        var policy = packet.EffectiveWorkflow.Capabilities?.Reference;
        if (policy is null || ReferencePolicies.Validate(policy) is not null)
            throw new InvalidDataException("Packet has no valid pinned reference policy.");
        var rows = new List<ReferenceResult>();
        var inferredReferences = new Dictionary<string, string>();
        foreach (
            var doc in packet.Documents.Where(d =>
                request.DocumentId is null || d.Id == request.DocumentId
            )
        )
        {
            var reference = doc.BusinessReference;
            string[] candidateReferences = [];
            if (string.IsNullOrWhiteSpace(reference))
            {
                candidateReferences = packet
                    .IdentifierCandidates.Where(candidate =>
                        candidate.PageIds.Any(doc.PageIds.Contains)
                        && (
                            policy.IdentifierKinds is null
                            || policy.IdentifierKinds.Contains(candidate.Kind)
                        )
                    )
                    .Select(candidate => candidate.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 500)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20)
                    .ToArray();
                if (candidateReferences.Length == 1)
                    reference = candidateReferences[0];
            }
            ReferenceEntry[] matches = [];
            string provenance = "unavailable",
                hash = policy.DatasetHash ?? "",
                status = "unavailable";
            bool synthetic = false;
            try
            {
                if (candidateReferences.Length > 1)
                {
                    provenance = "identifier-candidates";
                    status = "ambiguous";
                }
                else
                {
                    if (policy.Mode == "local")
                    {
                        var data = Datasets()
                            .SingleOrDefault(d =>
                                d.Id == policy.DatasetId
                                && d.Version == policy.DatasetVersion
                                && d.Hash == policy.DatasetHash
                            );
                        if (data is null || ReferencePolicies.DatasetHash(data) != data.Hash)
                            throw new InvalidDataException();
                        matches = data
                            .Entries.Where(e =>
                                string.Equals(
                                    e.Reference,
                                    reference,
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                            .ToArray();
                        provenance = $"local:{data.Id}:{data.Version}";
                        hash = data.Hash;
                        synthetic = data.Synthetic;
                    }
                    else
                    {
                        if (
                            policy.ConnectorBindingHash is null
                            || policy.ConnectorBindingHash
                                != connectors.BindingHash(policy.ConnectorId!)
                        )
                            throw new InvalidDataException(
                                "Pinned HTTP reference destination changed."
                            );
                        matches = await connectors.LookupAsync(policy.ConnectorId!, reference, ct);
                        provenance =
                            "http:"
                            + policy.ConnectorId
                            + ":"
                            + connectors.BindingHash(policy.ConnectorId!);
                        hash = ReferencePolicies.Hash(matches);
                    }
                    status = matches.Length switch
                    {
                        0 => "notFound",
                        1 => "candidate",
                        _ => "ambiguous",
                    };
                    if (
                        matches.Length == 1
                        && processingPacket is not null
                        && packet.Status is "queued" or "processing"
                        && string.IsNullOrWhiteSpace(doc.BusinessReference)
                    )
                    {
                        doc.BusinessReference = reference;
                        inferredReferences.Add(doc.Id, reference);
                    }
                    if (
                        matches.Length == 1
                        && policy.AutoVerify
                        && !synthetic
                        && doc.BusinessReference == reference
                    )
                        status = "verified";
                }
            }
            catch (Exception ex)
                when (ex
                        is HttpRequestException
                            or InvalidDataException
                            or TaskCanceledException
                            or JsonException
                ) { }
            var identity = new
            {
                packetId = packet.Id,
                packet.Revision,
                documentId = doc.Id,
                reference,
                provenance,
                hash,
                matches,
                candidateReferences,
                documentHash = ReferencePolicies.Hash(new { doc.Type, doc.PageIds }),
                workflowHash = packet.EffectiveWorkflow.Hash,
            };
            rows.Add(
                new(
                    packet.Id,
                    packet.Revision,
                    doc.Id,
                    reference,
                    status,
                    matches,
                    provenance,
                    hash,
                    synthetic,
                    ReferencePolicies.Hash(identity),
                    DateTimeOffset.UtcNow,
                    packet.EffectiveWorkflow.Hash,
                    status == "verified" ? "reference-policy:auto" : null,
                    candidateReferences,
                    ReferencePolicies.Hash(new { doc.Type, doc.PageIds })
                )
            );
        }
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var current = Store.Read(c, tx, packet.Id);
        if (
            current is null
            || current.Revision != request.Revision
            || current.EffectiveWorkflow.Hash != packet.EffectiveWorkflow.Hash
            || current.Status == "cancelled"
        )
            throw new InvalidOperationException("Revision changed during lookup.");
        if (
            actor is not null
            && operations?.ValidateReview(c, tx, current, actor) is string claimError
        )
            throw new InvalidOperationException(claimError);
        if (inferredReferences.Count > 0)
        {
            if (current.Status is not ("queued" or "processing"))
                throw new InvalidOperationException("Packet is no longer processing.");
            foreach (var pair in inferredReferences)
            {
                var assembled = packet.Documents.Single(doc => doc.Id == pair.Key);
                var persisted = current.Documents.SingleOrDefault(doc => doc.Id == pair.Key);
                if (
                    persisted is null
                    || persisted.Type != assembled.Type
                    || !persisted.PageIds.SequenceEqual(assembled.PageIds)
                    || !string.IsNullOrWhiteSpace(persisted.BusinessReference)
                        && persisted.BusinessReference != pair.Value
                )
                    throw new InvalidOperationException("Document assembly changed during lookup.");
                persisted.BusinessReference = pair.Value;
            }
            Store.Write(c, tx, current);
        }
        foreach (var row in rows)
        {
            using var q = c.CreateCommand();
            q.Transaction = tx;
            q.CommandText =
                "INSERT INTO reference_results VALUES($id,$revision,$document,$json) ON CONFLICT(packet_id,revision,document_id) DO UPDATE SET json=CASE WHEN json_extract(reference_results.json,'$.status')='verified' AND json_extract(reference_results.json,'$.resultHash')=json_extract(excluded.json,'$.resultHash') THEN reference_results.json ELSE excluded.json END";
            q.Parameters.AddWithValue("$id", row.PacketId);
            q.Parameters.AddWithValue("$revision", row.Revision);
            q.Parameters.AddWithValue("$document", row.DocumentId);
            q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(row, Contract.Json));
            q.ExecuteNonQuery();
        }
        tx.Commit();
        return Results(packet.Id).Where(r => r.Revision == packet.Revision).ToArray();
    }

    public ReferenceResult Verify(ReferenceVerifyRequest request, string? actor = null)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var packet = Store.Read(c, tx, request.PacketId)
            ?? throw new KeyNotFoundException("Packet not found.");
        if (packet.Revision != request.Revision)
            throw new InvalidOperationException("Revision changed.");
        if (
            actor is not null
            && operations?.ValidateReview(c, tx, packet, actor) is string claimError
        )
            throw new InvalidOperationException(claimError);
        var row = Results(packet.Id)
            .SingleOrDefault(r =>
                r.Revision == request.Revision && r.DocumentId == request.DocumentId
            )
            ?? throw new InvalidOperationException("Lookup first.");
        var doc = packet.Documents.SingleOrDefault(d => d.Id == request.DocumentId);
        var policy = packet.EffectiveWorkflow.Capabilities?.Reference;
        if (
            policy?.Mode == "http"
            && (
                policy.ConnectorBindingHash is null
                || policy.ConnectorBindingHash != connectors.BindingHash(policy.ConnectorId!)
                || row.Provenance
                    != "http:"
                        + policy.ConnectorId
                        + ":"
                        + connectors.BindingHash(policy.ConnectorId!)
            )
        )
            throw new InvalidOperationException(
                "Lookup destination configuration changed. Run lookup again."
            );
        if (
            doc is null
            || doc.BusinessReference != row.Reference
            || row.DocumentHash != ReferencePolicies.Hash(new { doc.Type, doc.PageIds })
            || row.WorkflowHash != packet.EffectiveWorkflow.Hash
            || row.ResultHash != request.ResultHash
            || row.Matches.Count(m => m.RecordId == request.RecordId) != 1
            || row.Status == "unavailable"
        )
            throw new InvalidOperationException("Lookup is stale or selected record is invalid.");
        row = row with
        {
            Status = "verified",
            Matches = row.Matches.Where(m => m.RecordId == request.RecordId).ToArray(),
            VerifiedBy = actor,
        };
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "UPDATE reference_results SET json=$json WHERE packet_id=$id AND revision=$revision AND document_id=$document";
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(row, Contract.Json));
        q.Parameters.AddWithValue("$id", row.PacketId);
        q.Parameters.AddWithValue("$revision", row.Revision);
        q.Parameters.AddWithValue("$document", row.DocumentId);
        q.ExecuteNonQuery();
        tx.Commit();
        return row;
    }

    public bool IsVerified(Packet packet, bool allowSynthetic = true)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: true);
        return IsVerified(c, tx, packet, allowSynthetic, connectors);
    }

    public bool IsPolicyCurrent(Packet packet)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: true);
        return ReferencePolicies.IsCurrentBinding(
            c,
            tx,
            packet.EffectiveWorkflow.Capabilities?.Reference,
            connectors
        );
    }

    public static bool IsVerified(
        SqliteConnection c,
        SqliteTransaction tx,
        Packet packet,
        bool allowSynthetic = false,
        ConnectorCatalog? config = null
    )
    {
        var policy = packet.EffectiveWorkflow.Capabilities?.Reference;
        if (
            policy is null
            || packet.Documents.Count == 0
            || !ReferencePolicies.IsCurrentBinding(c, tx, policy, config)
        )
            return false;
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT json FROM reference_results WHERE packet_id=$id AND revision=$revision";
        q.Parameters.AddWithValue("$id", packet.Id);
        q.Parameters.AddWithValue("$revision", packet.Revision);
        using var reader = q.ExecuteReader();
        var rows = new List<ReferenceResult>();
        while (reader.Read())
            rows.Add(
                JsonSerializer.Deserialize<ReferenceResult>(reader.GetString(0), Contract.Json)!
            );
        var provenance =
            policy.Mode == "http"
                ? "http:" + policy.ConnectorId + ":" + policy.ConnectorBindingHash
                : $"local:{policy.DatasetId}:{policy.DatasetVersion}";
        return packet.Documents.All(doc =>
            rows.Any(row =>
                row.DocumentId == doc.Id
                && row.DocumentHash == ReferencePolicies.Hash(new { doc.Type, doc.PageIds })
                && row.Reference == doc.BusinessReference
                && row.WorkflowHash == packet.EffectiveWorkflow.Hash
                && row.Status == "verified"
                && (allowSynthetic || !row.Synthetic)
                && row.Provenance == provenance
                && (policy.Mode != "local" || row.DatasetHash == policy.DatasetHash)
            )
        );
    }

    public static void OnReview(
        SqliteConnection c,
        SqliteTransaction tx,
        Packet before,
        Packet after,
        string actor
    )
    {
        if (
            before.EffectiveWorkflow.Hash != after.EffectiveWorkflow.Hash
            || before.Id != after.Id
            || before.Revision == after.Revision
        )
            return;
        ReferencePolicies.Initialize(c, tx);
        using var read = c.CreateCommand();
        read.Transaction = tx;
        read.CommandText =
            "SELECT json FROM reference_results WHERE packet_id=$id AND revision=$revision";
        read.Parameters.AddWithValue("$id", before.Id);
        read.Parameters.AddWithValue("$revision", before.Revision);
        using var reader = read.ExecuteReader();
        var rows = new List<ReferenceResult>();
        while (reader.Read())
            rows.Add(
                JsonSerializer.Deserialize<ReferenceResult>(reader.GetString(0), Contract.Json)!
            );
        reader.Close();
        foreach (
            var row in rows.Where(row =>
                row.Status == "verified" && row.WorkflowHash == before.EffectiveWorkflow.Hash
            )
        )
        {
            var original = before.Documents.SingleOrDefault(doc => doc.Id == row.DocumentId);
            var updated = after.Documents.SingleOrDefault(doc => doc.Id == row.DocumentId);
            if (
                original is null
                || updated is null
                || original.Type != updated.Type
                || original.BusinessReference != updated.BusinessReference
                || row.Reference != updated.BusinessReference
                || !original.PageIds.SequenceEqual(updated.PageIds)
            )
                continue;
            var carried = row with
            {
                Revision = after.Revision,
                ResultHash = ReferencePolicies.Hash(
                    new
                    {
                        packetId = after.Id,
                        after.Revision,
                        documentId = updated.Id,
                        updated.BusinessReference,
                        row.Provenance,
                        row.DatasetHash,
                        row.Matches,
                        workflowHash = after.EffectiveWorkflow.Hash,
                    }
                ),
                At = DateTimeOffset.UtcNow,
            };
            using var write = c.CreateCommand();
            write.Transaction = tx;
            write.CommandText =
                "INSERT OR IGNORE INTO reference_results VALUES($id,$revision,$document,$json)";
            write.Parameters.AddWithValue("$id", after.Id);
            write.Parameters.AddWithValue("$revision", after.Revision);
            write.Parameters.AddWithValue("$document", updated.Id);
            write.Parameters.AddWithValue(
                "$json",
                JsonSerializer.Serialize(carried, Contract.Json)
            );
            write.ExecuteNonQuery();
        }
    }

    public void RestoreDerivedReferences(Packet packet)
    {
        if (packet.Status is not ("queued" or "processing"))
            return;
        var policy = packet.EffectiveWorkflow.Capabilities?.Reference;
        if (policy is null || !IsPolicyCurrent(packet))
            return;
        var rows = Results(packet.Id)
            .Where(row =>
                row.Revision == packet.Revision
                && row.WorkflowHash == packet.EffectiveWorkflow.Hash
                && row.Status is "candidate" or "verified"
                && row.Matches.Length == 1
                && (policy.Mode != "local" || row.DatasetHash == policy.DatasetHash)
            )
            .ToArray();
        foreach (
            var document in packet.Documents.Where(doc =>
                string.IsNullOrWhiteSpace(doc.BusinessReference)
            )
        )
        {
            var hash = ReferencePolicies.Hash(new { document.Type, document.PageIds });
            var row = rows.SingleOrDefault(row =>
                row.DocumentId == document.Id && row.DocumentHash == hash
            );
            if (
                row is not null
                && string.Equals(
                    row.Reference,
                    row.Matches[0].Reference,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                document.BusinessReference = row.Reference;
        }
    }
}
