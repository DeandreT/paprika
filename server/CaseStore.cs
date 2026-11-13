using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PaperworkClassifier;

public sealed class CaseStore
{
    private readonly Store store;
    private readonly OperationsPlatformStore? operations;

    public CaseStore(Store store, OperationsPlatformStore? operations = null)
    {
        this.store = store;
        this.operations = operations;
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "CREATE TABLE IF NOT EXISTS platform_cases(id TEXT PRIMARY KEY,revision INTEGER NOT NULL,json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS platform_case_history(case_id TEXT NOT NULL,revision INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(case_id,revision)); CREATE TABLE IF NOT EXISTS case_members(packet_id TEXT NOT NULL,document_id TEXT NOT NULL,case_id TEXT NOT NULL,PRIMARY KEY(packet_id,document_id)); CREATE TABLE IF NOT EXISTS case_proposals(packet_id TEXT PRIMARY KEY,json TEXT NOT NULL);";
        q.ExecuteNonQuery();
    }

    public CaseRecord[] List()
    {
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT json FROM platform_cases ORDER BY rowid DESC";
        using var r = q.ExecuteReader();
        var cases = new List<CaseRecord>();
        while (r.Read())
            cases.Add(JsonSerializer.Deserialize<CaseRecord>(r.GetString(0), Contract.Json)!);
        return cases.ToArray();
    }

    public CaseRecord Get(string id)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        return Read(c, tx, id);
    }

    public bool Accessible(CaseRecord record, Func<string, bool> canAccess)
    {
        if (!canAccess(record.WorkflowId))
            return false;
        foreach (var packetId in record.Members.Select(m => m.PacketId).Distinct())
        {
            var packet = store.Get(packetId);
            if (
                packet is null
                || operations?.IsArchived(packetId) == true
                || !canAccess(packet.EffectiveWorkflow.Id)
            )
                return false;
        }
        return true;
    }

    public CaseRecord[] History(string id)
    {
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "SELECT json FROM platform_case_history WHERE case_id=$id ORDER BY revision";
        q.Parameters.AddWithValue("$id", id);
        using var r = q.ExecuteReader();
        var revisions = new List<CaseRecord>();
        while (r.Read())
            revisions.Add(JsonSerializer.Deserialize<CaseRecord>(r.GetString(0), Contract.Json)!);
        return revisions.ToArray();
    }

    private static CaseRecord Read(SqliteConnection c, SqliteTransaction tx, string id)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT json FROM platform_cases WHERE id=$id";
        q.Parameters.AddWithValue("$id", id);
        return q.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<CaseRecord>(json, Contract.Json)!
            : throw new WorkflowException(404, "Case not found.");
    }

    private static CasePolicy Policy(Packet packet)
    {
        var policy = packet.EffectiveWorkflow.Capabilities?.Case
            ?? throw new WorkflowException(400, "Cases are not enabled in this packet snapshot.");
        CaseValidator.Validate(policy);
        if (
            policy.ExpectedDocumentTypes.Any(t =>
                !packet.EffectiveWorkflow.Types.Any(type => type.Id == t)
            )
        )
            throw new WorkflowException(
                400,
                "Expected case document types must belong to the accepted taxonomy."
            );
        return policy;
    }

    public CaseRecord Create(CaseCreateRequest request, string actor, Func<string, bool> canReview)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var packet = Packet(c, tx, request.PacketId, request.PacketRevision, canReview);
        ValidateClaim(c, tx, packet, actor);
        var policy = Policy(packet);
        if (
            string.IsNullOrWhiteSpace(request.BusinessReference)
            || request.BusinessReference.Length > 500
        )
            throw new WorkflowException(400, "A bounded business reference is required.");
        var record = new CaseRecord(
            Guid.NewGuid().ToString("N"),
            1,
            policy.CustomerId,
            packet.EffectiveWorkflow.Id,
            packet.TestRun,
            request.BusinessReference.Trim(),
            false,
            null,
            [.. policy.ExpectedDocumentTypes],
            [],
            [new(DateTimeOffset.UtcNow, actor, "case created", 1)]
        );
        record = record with { Members = Add(c, tx, record, packet, request.DocumentIds, actor) };
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "INSERT INTO platform_cases VALUES($id,1,$json)";
        q.Parameters.AddWithValue("$id", record.Id);
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(record, Contract.Json));
        q.ExecuteNonQuery();
        tx.Commit();
        return record;
    }

    public CaseRecord AddMembers(
        string id,
        CaseMembershipRequest request,
        string actor,
        Func<string, bool> canReview
    )
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var record = Read(c, tx, id);
        Check(c, tx, record, request.Revision, actor, canReview);
        var packet = Packet(c, tx, request.PacketId, request.PacketRevision, canReview);
        ValidateClaim(c, tx, packet, actor);
        var members = Add(c, tx, record, packet, request.DocumentIds, actor, request.Refresh);
        var saved = record with
        {
            Revision = record.Revision + 1,
            Members = members,
            Audit =
            [
                .. record.Audit,
                new(
                    DateTimeOffset.UtcNow,
                    actor,
                    request.Refresh ? "document source revision refreshed" : "documents added",
                    record.Revision + 1
                ),
            ],
        };
        Save(c, tx, saved, request.Revision);
        tx.Commit();
        return saved;
    }

    public CaseRecord RemoveMembers(
        string id,
        CaseMembershipRequest request,
        string actor,
        Func<string, bool> canReview
    )
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var record = Read(c, tx, id);
        Check(c, tx, record, request.Revision, actor, canReview);
        var packet = Packet(c, tx, request.PacketId, request.PacketRevision, canReview);
        Compatible(record, packet);
        ValidateClaim(c, tx, packet, actor);
        if (
            request.DocumentIds is not { Length: > 0 and <= 1000 }
            || request.DocumentIds.Distinct().Count() != request.DocumentIds.Length
            || request.DocumentIds.Any(d =>
                !record.Members.Any(m => m.PacketId == packet.Id && m.DocumentId == d)
            )
        )
            throw new WorkflowException(400, "Select existing case members to remove.");
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "DELETE FROM case_members WHERE packet_id=$p AND document_id=$d AND case_id=$c";
        foreach (var document in request.DocumentIds)
        {
            q.Parameters.Clear();
            q.Parameters.AddWithValue("$p", packet.Id);
            q.Parameters.AddWithValue("$d", document);
            q.Parameters.AddWithValue("$c", id);
            q.ExecuteNonQuery();
        }
        var saved = record with
        {
            Revision = record.Revision + 1,
            Members = record
                .Members.Where(m =>
                    m.PacketId != packet.Id || !request.DocumentIds.Contains(m.DocumentId)
                )
                .ToArray(),
            Audit =
            [
                .. record.Audit,
                new(DateTimeOffset.UtcNow, actor, "documents removed", record.Revision + 1),
            ],
        };
        Save(c, tx, saved, request.Revision);
        tx.Commit();
        return saved;
    }

    public CaseRecord Edit(
        string id,
        CaseEditRequest request,
        string actor,
        Func<string, bool> canReview
    )
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var record = Read(c, tx, id);
        Check(c, tx, record, request.Revision, actor, canReview);
        CaseValidator.Validate(new(record.CustomerId, request.ExpectedDocumentTypes));
        // A checklist may only use types available in every source member's accepted taxonomy.
        foreach (var packetId in record.Members.Select(m => m.PacketId).Distinct())
        {
            var packet = Store.Read(c, tx, packetId)
                ?? throw new WorkflowException(409, "A member packet is unavailable.");
            ValidateClaim(c, tx, packet, actor);
            if (!canReview(packet.EffectiveWorkflow.Id))
                throw new WorkflowException(403, "Case workflow access is required.");
            if (
                request.ExpectedDocumentTypes.Any(t =>
                    !packet.EffectiveWorkflow.Types.Any(type => type.Id == t)
                )
            )
                throw new WorkflowException(
                    400,
                    "Checklist types must belong to the source taxonomies."
                );
        }
        var saved = record with
        {
            Revision = record.Revision + 1,
            ExpectedDocumentTypes = [.. request.ExpectedDocumentTypes],
            Audit =
            [
                .. record.Audit,
                new(
                    DateTimeOffset.UtcNow,
                    actor,
                    "expected documents updated",
                    record.Revision + 1
                ),
            ],
        };
        Save(c, tx, saved, request.Revision);
        tx.Commit();
        return saved;
    }

    private Packet Packet(
        SqliteConnection c,
        SqliteTransaction tx,
        string id,
        int revision,
        Func<string, bool> canReview
    )
    {
        var packet = Store.Read(c, tx, id) ?? throw new WorkflowException(404, "Packet not found.");
        if (operations?.IsArchived(id) == true)
            throw new WorkflowException(404, "Packet not found.");
        if (!canReview(packet.EffectiveWorkflow.Id))
            throw new WorkflowException(403, "Packet workflow access is required.");
        if (packet.Revision != revision)
            throw new WorkflowException(409, "Packet revision changed. Reload the packet.");
        return packet;
    }

    private void ValidateClaim(
        SqliteConnection c,
        SqliteTransaction tx,
        Packet packet,
        string actor
    )
    {
        if (operations?.ValidateMutation(c, tx, packet, actor) is { } error)
            throw new WorkflowException(409, error);
    }

    private void Check(
        SqliteConnection c,
        SqliteTransaction tx,
        CaseRecord record,
        int revision,
        string actor,
        Func<string, bool> canReview
    )
    {
        if (!canReview(record.WorkflowId))
            throw new WorkflowException(403, "Case workflow access is required.");
        if (record.Revision != revision)
            throw new WorkflowException(409, "Case revision changed. Reload the case.");
        foreach (var packetId in record.Members.Select(m => m.PacketId).Distinct())
        {
            var packet = Store.Read(c, tx, packetId)
                ?? throw new WorkflowException(404, "A member packet is unavailable.");
            if (operations?.IsArchived(packetId) == true)
                throw new WorkflowException(404, "A member packet is unavailable.");
            if (!canReview(packet.EffectiveWorkflow.Id))
                throw new WorkflowException(
                    403,
                    "Review access to every member workflow is required."
                );
            Compatible(record, packet);
            ValidateClaim(c, tx, packet, actor);
        }
    }

    private static void Compatible(CaseRecord record, Packet packet)
    {
        var policy = Policy(packet);
        if (
            record.CustomerId != policy.CustomerId
            || record.WorkflowId != packet.EffectiveWorkflow.Id
            || record.TestRun != packet.TestRun
        )
            throw new WorkflowException(
                409,
                "Case membership requires the same customer, workflow and test scope."
            );
    }

    private static CaseMember[] Add(
        SqliteConnection c,
        SqliteTransaction tx,
        CaseRecord record,
        Packet packet,
        string[] documentIds,
        string actor,
        bool refresh = false
    )
    {
        Compatible(record, packet);
        if (
            documentIds is not { Length: > 0 and <= 1000 }
            || documentIds.Distinct().Count() != documentIds.Length
            || documentIds.Any(id => !packet.Documents.Any(d => d.Id == id))
        )
            throw new WorkflowException(
                400,
                "Select unique existing documents for explicit case membership."
            );
        var members = record.Members.ToList();
        using var q = c.CreateCommand();
        q.Transaction = tx;
        foreach (var documentId in documentIds)
        {
            q.Parameters.Clear();
            q.CommandText =
                "SELECT case_id FROM case_members WHERE packet_id=$p AND document_id=$d";
            q.Parameters.AddWithValue("$p", packet.Id);
            q.Parameters.AddWithValue("$d", documentId);
            var owner = q.ExecuteScalar() as string;
            if (owner is not null && (!refresh || owner != record.Id))
                throw new WorkflowException(409, "A selected document already belongs to a case.");
            if (refresh && owner != record.Id)
                throw new WorkflowException(409, "Only existing case members can be refreshed.");
            if (refresh)
                members.RemoveAll(m => m.PacketId == packet.Id && m.DocumentId == documentId);
            var document = packet.Documents.Single(d => d.Id == documentId);
            var pages = packet.Pages.Where(p => document.PageIds.Contains(p.Id)).ToArray();
            if (
                pages.Length == 0
                || pages.Length != document.PageIds.Count
                || pages.Any(p => !packet.Sources.Any(s => s.Id == p.SourceId))
            )
                throw new WorkflowException(400, "Document source provenance is incomplete.");
            var provenance = pages
                .GroupBy(p => p.SourceId)
                .Select(g =>
                {
                    var source = packet.Sources.Single(s => s.Id == g.Key);
                    return new CaseProvenance(
                        source.Id,
                        source.Name,
                        source.Sha256,
                        g.Select(p => p.Id).ToArray()
                    );
                })
                .ToArray();
            members.Add(
                new(
                    packet.Id,
                    packet.Revision,
                    document.Id,
                    document.Type,
                    packet.EffectiveWorkflow.Hash,
                    provenance,
                    DateTimeOffset.UtcNow,
                    actor
                )
            );
            q.CommandText = "INSERT OR IGNORE INTO case_members VALUES($p,$d,$c)";
            q.Parameters.AddWithValue("$c", record.Id);
            q.ExecuteNonQuery();
        }
        return members.ToArray();
    }

    private static void Save(
        SqliteConnection c,
        SqliteTransaction tx,
        CaseRecord record,
        int expected
    )
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "INSERT OR IGNORE INTO platform_case_history SELECT id,revision,json FROM platform_cases WHERE id=$id AND revision=$expected";
        q.Parameters.AddWithValue("$id", record.Id);
        q.Parameters.AddWithValue("$expected", expected);
        q.ExecuteNonQuery();
        q.CommandText =
            "UPDATE platform_cases SET revision=$r,json=$j WHERE id=$id AND revision=$expected";
        q.Parameters.AddWithValue("$r", record.Revision);
        q.Parameters.AddWithValue("$j", JsonSerializer.Serialize(record, Contract.Json));
        if (q.ExecuteNonQuery() != 1)
            throw new WorkflowException(409, "Case revision changed.");
    }

    public CaseCollationProposal? Proposal(string packetId)
    {
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT json FROM case_proposals WHERE packet_id=$p";
        q.Parameters.AddWithValue("$p", packetId);
        return q.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<CaseCollationProposal>(json, Contract.Json)
            : null;
    }

    // Only trusted reference-handler wiring supplies verified evidence; no HTTP request can mark a reference verified.
    public CaseCollationProposal Collate(Packet packet, CaseVerifiedReference? verifiedReference)
    {
        var policy = Policy(packet);
        var verified =
            verifiedReference is { Value.Length: > 0 and <= 500, Evidence.Length: > 0 and <= 2000 }
            && !string.IsNullOrWhiteSpace(verifiedReference.Value)
            && !string.IsNullOrWhiteSpace(verifiedReference.Evidence);
        var reference = verified
            ? verifiedReference!.Value.Trim()
            : packet
                .Documents.Select(d => d.BusinessReference)
                .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r))
                ?.Trim()
                ?? "";
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var current = Packet(c, tx, packet.Id, packet.Revision, _ => true);
        var candidates = ListIn(c, tx)
            .Where(r =>
                r.CustomerId == policy.CustomerId
                && r.WorkflowId == current.EffectiveWorkflow.Id
                && r.TestRun == current.TestRun
                && r.BusinessReference == reference
            )
            .ToArray();
        bool auto =
            verified
            && policy.AutoCollate
            && candidates.Length <= 1
            && candidates.All(r => Accessible(r, _ => true))
            && packet.Documents.Count > 0;
        if (auto)
        {
            var record = candidates.SingleOrDefault();
            if (record is null)
            {
                record = new(
                    Guid.NewGuid().ToString("N"),
                    1,
                    policy.CustomerId,
                    packet.EffectiveWorkflow.Id,
                    packet.TestRun,
                    reference,
                    true,
                    verifiedReference!.Evidence,
                    [.. policy.ExpectedDocumentTypes],
                    [],
                    [new(DateTimeOffset.UtcNow, "flow", "verified case created", 1)]
                );
                using var insert = c.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO platform_cases VALUES($id,1,$j)";
                insert.Parameters.AddWithValue("$id", record.Id);
                insert.Parameters.AddWithValue(
                    "$j",
                    JsonSerializer.Serialize(record, Contract.Json)
                );
                insert.ExecuteNonQuery();
            }
            // An existing unverified case must be explicitly reviewed, never silently promoted.
            if (
                !record.ReferenceVerified
                || record.Members.Any(m =>
                    m.PacketId == packet.Id && !current.Documents.Any(d => d.Id == m.DocumentId)
                )
            )
                auto = false;
            else
            {
                var stale = record
                    .Members.Where(m =>
                        m.PacketId == packet.Id && m.PacketRevision != current.Revision
                    )
                    .Select(m => m.DocumentId)
                    .ToArray();
                if (stale.Length > 0)
                {
                    var members = Add(c, tx, record, current, stale, "flow", true);
                    var updated = record with
                    {
                        Revision = record.Revision + 1,
                        Members = members,
                        Audit =
                        [
                            .. record.Audit,
                            new(
                                DateTimeOffset.UtcNow,
                                "flow",
                                "verified source revision refreshed",
                                record.Revision + 1
                            ),
                        ],
                    };
                    Save(c, tx, updated, record.Revision);
                    record = updated;
                }
                var remaining = packet
                    .Documents.Where(d =>
                        !record.Members.Any(m => m.PacketId == packet.Id && m.DocumentId == d.Id)
                    )
                    .Select(d => d.Id)
                    .ToArray();
                if (remaining.Length > 0)
                {
                    var members = Add(c, tx, record, current, remaining, "flow");
                    var updated = record with
                    {
                        Revision = record.Revision + 1,
                        Members = members,
                        Audit =
                        [
                            .. record.Audit,
                            new(
                                DateTimeOffset.UtcNow,
                                "flow",
                                "verified documents collated",
                                record.Revision + 1
                            ),
                        ],
                    };
                    Save(c, tx, updated, record.Revision);
                    record = updated;
                }
                candidates = [record];
            }
        }
        var proposal = new CaseCollationProposal(
            packet.Id,
            reference,
            candidates.Select(r => r.Id).ToArray(),
            auto,
            auto
                ? "Verified reference and compatible scope."
                : "Explicit membership review required; reference is unverified, automatic collation is disabled, or the match is ambiguous."
        );
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "INSERT INTO case_proposals VALUES($p,$j) ON CONFLICT(packet_id) DO UPDATE SET json=excluded.json";
        q.Parameters.AddWithValue("$p", packet.Id);
        q.Parameters.AddWithValue("$j", JsonSerializer.Serialize(proposal, Contract.Json));
        q.ExecuteNonQuery();
        tx.Commit();
        return proposal;
    }

    private static CaseRecord[] ListIn(SqliteConnection c, SqliteTransaction tx)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT json FROM platform_cases";
        using var r = q.ExecuteReader();
        var records = new List<CaseRecord>();
        while (r.Read())
            records.Add(JsonSerializer.Deserialize<CaseRecord>(r.GetString(0), Contract.Json)!);
        return records.ToArray();
    }
}
