using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace paprika;

public sealed class Store
{
    public string Root { get; }
    internal IConfiguration Configuration { get; }
    private readonly object gate = new();
    internal DurableQueue? Queue { get; private set; }
    public WorkflowRegistry Workflows { get; }
    internal ArtifactStorage PacketArtifacts { get; }
    public string ConnectionString =>
        new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Root, "jobs.sqlite"),
        }.ToString();

    public Store(IConfiguration config, DurableQueue? queue = null)
    {
        Configuration = config;
        Queue = queue;
        Root = AppPaths.DataRoot(config);
        Directory.CreateDirectory(Root);
        using var c = Open();
        PacketArtifacts = new ArtifactStorage(Root, config, c);
        using var q = c.CreateCommand();
        q.CommandText =
            "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS packets(id TEXT PRIMARY KEY, json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS revisions(packet_id TEXT, revision INTEGER, json TEXT NOT NULL, PRIMARY KEY(packet_id,revision)); CREATE TABLE IF NOT EXISTS artifacts(packet_id TEXT, name TEXT, json TEXT NOT NULL, PRIMARY KEY(packet_id,name)); CREATE TABLE IF NOT EXISTS intake_keys(key TEXT PRIMARY KEY, fingerprint TEXT NOT NULL, packet_id TEXT NOT NULL); CREATE INDEX IF NOT EXISTS packets_status ON packets(json_extract(json,'$.status'));";
        q.ExecuteNonQuery();
        q.CommandText =
            "CREATE TABLE IF NOT EXISTS queue_work(kind TEXT NOT NULL,id TEXT NOT NULL,work_id TEXT NOT NULL,PRIMARY KEY(kind,id)); CREATE TABLE IF NOT EXISTS replayed_letters(id TEXT PRIMARY KEY,queued_at TEXT NOT NULL);";
        q.ExecuteNonQuery();
        Workflows = new WorkflowRegistry(this, new Settings(config));
        Workflows.Initialize();
    }

    internal SqliteConnection Open()
    {
        var c = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(Root, "jobs.sqlite"),
            }.ToString()
        );
        c.Open();
        return c;
    }

    public string SourcePath(Packet p, Source s) => PacketArtifacts.SourcePath(p, s);

    internal bool Ready()
    {
        try
        {
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(Root, "jobs.sqlite"),
                    Mode = SqliteOpenMode.ReadOnly,
                    DefaultTimeout = 1,
                }.ToString()
            );
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT 1 FROM packets LIMIT 1";
            query.ExecuteScalar(); // An initialized, empty packet table is ready too.
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public List<Packet> All()
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT json FROM packets ORDER BY rowid DESC";
        using var r = q.ExecuteReader();
        var a = new List<Packet>();
        while (r.Read())
            a.Add(JsonSerializer.Deserialize<Packet>(r.GetString(0), Contract.Json)!);
        return a;
    }

    public Packet? Get(string id)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT json FROM packets WHERE id=$id";
        q.Parameters.AddWithValue("$id", id);
        var j = q.ExecuteScalar() as string;
        return j is null ? null : JsonSerializer.Deserialize<Packet>(j, Contract.Json);
    }

    public T ApprovedOutput<T>(
        string id,
        Func<Packet, T> generate,
        Action<Packet>? validate = null
    ) => WithApprovedRevision(id, (_, _, p) => generate(p), validate);

    internal T WithApprovedRevision<T>(
        string id,
        Func<SqliteConnection, SqliteTransaction, Packet, T> generate,
        Action<Packet>? validate = null
    )
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var packet = Read(c, tx, id) ?? throw new WorkflowException(404, "Packet not found.");
        validate?.Invoke(packet);
        if (packet.Status != "approved")
            throw new WorkflowException(409, "Approve this revision before export.");
        PlatformReleaseGate.Ensure(c, tx, packet, new ConnectorCatalog(Configuration));
        var output = generate(c, tx, packet);
        tx.Commit();
        return output;
    }

    public Packet? NextPending()
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "SELECT json FROM packets WHERE json_extract(json,'$.status') IN ('queued','processing') ORDER BY rowid LIMIT 1";
        return q.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<Packet>(json, Contract.Json)
            : null;
    }

    private static WorkflowSnapshot RetainedWorkflowForNewRun(Packet packet, Settings settings)
    {
        var workflow = packet.EffectiveWorkflow;
        if (workflow.Mode != "live")
            return workflow;
        var (profile, model) = settings.ClassifierBinding(packet);
        var provider = WorkflowRegistry.RequireBoundLiveClassifier(
            settings,
            profile,
            model,
            workflow.ClassifierBindingHash
        );
        return workflow.ProviderProfile == provider && workflow.Model == model
            ? workflow
            : WorkflowRegistry.WithHash(
                workflow with
                {
                    ProviderProfile = provider,
                    Model = model,
                }
            );
    }

    public (Packet? Packet, int Code, string? Error) Accept(
        Packet packet,
        string? key,
        WorkflowSelection? selection = null
    )
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand();
        q.Transaction = tx;
        string Fingerprint(string mode) =>
            Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            Mode = mode,
                            Files = packet.Sources.Select(s => new
                            {
                                s.Name,
                                s.Sha256,
                                s.PageCount,
                            }),
                        },
                        Contract.Json
                    )
                )
            );
        if (key is not null)
        {
            q.CommandText = "SELECT fingerprint, packet_id FROM intake_keys WHERE key=$key";
            q.Parameters.AddWithValue("$key", key);
            using var reader = q.ExecuteReader();
            if (reader.Read())
            {
                var savedFingerprint = reader.GetString(0);
                var id = reader.GetString(1);
                reader.Close();
                var original = Read(c, tx, id)!;
                var matches =
                    savedFingerprint == Fingerprint(original.Mode)
                    && (selection is not null || packet.Mode == original.Mode)
                    && (
                        selection is null
                        || (
                            selection.TestRun == original.TestRun
                            && (
                                selection.WorkflowId is null
                                || selection.WorkflowId == original.EffectiveWorkflow.Id
                            )
                            && (
                                selection.WorkflowVersion is null
                                || selection.WorkflowVersion == original.EffectiveWorkflow.Version
                            )
                            && (
                                selection.WorkflowRevision is null
                                || selection.WorkflowRevision
                                    == original.IntakeSelection?.WorkflowRevision
                            )
                        )
                    );
                tx.Commit();
                return matches
                    ? (original, 200, null)
                    : (
                        null,
                        409,
                        "Idempotency key was already used for different content or workflow selection."
                    );
            }
        }
        try
        {
            packet.Workflow = selection is null
                ? RetainedWorkflowForNewRun(packet, new Settings(Configuration))
                : Workflows.Resolve(c, tx, selection);
            packet.Mode = packet.Workflow.Mode;
            packet.TestRun = selection?.TestRun ?? packet.TestRun;
            packet.IntakeSelection = selection;
        }
        catch (WorkflowException ex)
        {
            return (null, ex.Code, ex.Message);
        }
        var fingerprint = Fingerprint(packet.Mode);
        PacketArtifacts.PublishOriginals(packet);
        q.Parameters.Clear();
        q.CommandText = "INSERT INTO packets VALUES($id,$json)";
        q.Parameters.AddWithValue("$id", packet.Id);
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(packet, Contract.Json));
        q.ExecuteNonQuery();
        if (key is not null)
        {
            q.Parameters.Clear();
            q.CommandText = "INSERT INTO intake_keys VALUES($key,$fingerprint,$id)";
            q.Parameters.AddWithValue("$key", key);
            q.Parameters.AddWithValue("$fingerprint", fingerprint);
            q.Parameters.AddWithValue("$id", packet.Id);
            q.ExecuteNonQuery();
        }
        Commit(tx, new ProcessPacket(packet.Id));
        return (packet, 200, null);
    }

    public (Packet? Packet, int Code, string? Error) Cancel(string id, int revision)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT json FROM packets WHERE id=$id";
        q.Parameters.AddWithValue("$id", id);
        if (q.ExecuteScalar() is not string json)
            return (null, 404, "Packet not found.");
        var p = JsonSerializer.Deserialize<Packet>(json, Contract.Json)!;
        if (p.Revision != revision)
            return (null, 409, "Revision changed. Reload the packet.");
        if (p.Status is not ("queued" or "processing"))
            return (null, 409, "Only queued or processing packets can be cancelled.");
        p.Status = "cancelled";
        p.Revision++;
        p.Processing = new("cancelled", p.Processing?.Model);
        p.Audit.Add(new(DateTimeOffset.UtcNow, "cancellation requested", p.Revision));
        q.CommandText = "UPDATE packets SET json=$json WHERE id=$id";
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(p, Contract.Json));
        q.ExecuteNonQuery();
        tx.Commit();
        return (p, 200, null);
    }

    public (Packet? Packet, int Code, string? Error) Reprocess(
        string id,
        int revision,
        Settings settings,
        WorkflowSelection? selection = null
    )
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT json FROM packets WHERE id=$id";
        q.Parameters.AddWithValue("$id", id);
        if (q.ExecuteScalar() is not string json)
            return (null, 404, "Packet not found.");
        var parent = JsonSerializer.Deserialize<Packet>(json, Contract.Json)!;
        if (parent.Revision != revision)
            return (null, 409, "Revision changed. Reload the packet.");
        if (parent.Status is "queued" or "processing")
            return (null, 409, "Active packets cannot be reprocessed.");
        WorkflowSnapshot workflow;
        try
        {
            workflow = selection is null
                ? RetainedWorkflowForNewRun(parent, settings)
                : Workflows.Resolve(c, tx, selection);
        }
        catch (WorkflowException ex)
        {
            return (null, ex.Code, ex.Message);
        }
        var p = new Packet
        {
            Name = parent.Name,
            Mode = workflow.Mode == "demo" ? "simulation" : workflow.Mode,
            Workflow = workflow.Mode == "demo" ? WorkflowRegistry.Legacy("simulation") : workflow,
            TestRun = selection?.TestRun ?? parent.TestRun,
            IntakeSelection = selection ?? parent.IntakeSelection,
            ParentPacketId = parent.Id,
            RunNumber = parent.RunNumber + 1,
        };
        Directory.CreateDirectory(Path.Combine(Root, p.Id));
        try
        {
            foreach (var original in parent.Sources)
            {
                // New run deliberately starts without OCR/configuration checkpoints.
                var source = new Source
                {
                    Name = original.Name,
                    Extension = original.Extension,
                    PageCount = original.PageCount,
                };
                File.Copy(SourcePath(parent, original), SourcePath(p, source));
                source.Sha256 = Pdfs.SourceHash(SourcePath(p, source));
                p.Sources.Add(source);
            }
            p.Audit.Add(
                new(DateTimeOffset.UtcNow, "new run from original source bytes", p.Revision)
            );
            PacketArtifacts.PublishOriginals(p);
            q.Parameters.Clear();
            q.CommandText = "INSERT INTO packets VALUES($id,$json)";
            q.Parameters.AddWithValue("$id", p.Id);
            q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(p, Contract.Json));
            q.ExecuteNonQuery();
            Commit(tx, new ProcessPacket(p.Id));
            return (p, 200, null);
        }
        catch
        {
            if (Get(p.Id) is null && Directory.Exists(Path.Combine(Root, p.Id)))
                Directory.Delete(Path.Combine(Root, p.Id), true);
            throw;
        }
    }

    public List<HistoryResponse>? History(string id)
    {
        lock (gate)
        {
            var current = Get(id);
            if (current is null)
                return null;
            using var c = Open();
            using var q = c.CreateCommand();
            q.CommandText = "SELECT json FROM revisions WHERE packet_id=$id ORDER BY revision";
            q.Parameters.AddWithValue("$id", id);
            using var reader = q.ExecuteReader();
            var snapshots = new List<Packet>();
            while (reader.Read())
                snapshots.Add(
                    JsonSerializer.Deserialize<Packet>(reader.GetString(0), Contract.Json)!
                );
            snapshots.Add(current);
            return snapshots
                .OrderBy(p => p.Revision)
                .Select(p =>
                {
                    var audit = p.Audit.LastOrDefault(a => a.Revision == p.Revision);
                    return new HistoryResponse(
                        p.Revision,
                        p.Status,
                        audit?.At ?? p.CreatedAt,
                        p.Documents,
                        audit?.Action ?? "packet created",
                        p.EffectiveWorkflow,
                        p.TestRun
                    );
                })
                .ToList();
        }
    }

    public void Save(Packet p)
    {
        lock (gate)
        {
            using var c = Open();
            using var q = c.CreateCommand();
            q.CommandText =
                "INSERT INTO packets VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET json=$json WHERE json_extract(packets.json,'$.status') != 'cancelled'";
            q.Parameters.AddWithValue("$id", p.Id);
            q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(p, Contract.Json));
            q.ExecuteNonQuery();
        }
    }

    public void Artifact(string id, string name, string json)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "INSERT INTO artifacts VALUES($id,$name,$json)";
        q.Parameters.AddWithValue("$id", id);
        q.Parameters.AddWithValue("$name", name + "-" + Guid.NewGuid().ToString("N"));
        q.Parameters.AddWithValue("$json", json);
        q.ExecuteNonQuery();
    }

    public void CompleteProcessing(
        Packet packet,
        string prediction,
        QualityStore? quality,
        QualityDecision? decision
    )
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var current = Read(c, tx, packet.Id);
        if (
            current is null
            || current.Status is not ("queued" or "processing")
            || current.Revision != packet.Revision
        )
            return;
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "INSERT INTO artifacts(packet_id,name,json) VALUES($id,$name,$json)";
        q.Parameters.AddWithValue("$id", packet.Id);
        q.Parameters.AddWithValue("$name", "predicted-" + Guid.NewGuid().ToString("N"));
        q.Parameters.AddWithValue("$json", prediction);
        q.ExecuteNonQuery();
        Write(c, tx, packet);
        if (decision is not null)
            quality?.OnProcessed(c, tx, packet, decision);
        if (packet.Status == "approved" && NeedsApprovedContinuation(packet))
            Commit(tx, new ContinuePlatformFlow(packet.Id, packet.Revision));
        else
            tx.Commit();
    }

    private static bool NeedsApprovedContinuation(Packet packet) =>
        packet.EffectiveWorkflow.Capabilities is { } capabilities
        && (
            capabilities.Flow is not null
            || capabilities.Output is not null
            || capabilities.Case is not null
        );

    public (Packet? Packet, int Code, string? Error) Retry(string id)
    {
        lock (gate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            var p = Read(c, tx, id);
            if (p is null)
                return (null, 404, "Packet not found.");
            if (p.Status != "failed")
                return (null, 409, "Only failed packets can be retried.");
            p.Status = "queued";
            p.Error = null;
            p.Audit.Add(new(DateTimeOffset.UtcNow, "retry requested", p.Revision));
            Write(c, tx, p);
            Commit(tx, new ProcessPacket(p.Id));
            return (p, 200, null);
        }
    }

    internal void EnableMessaging(DurableQueue queue) => Queue = queue;

    internal void Commit(SqliteTransaction tx, object message)
    {
        if (Queue is null)
            tx.Commit(); // Standalone business-store unit tests only; the host always registers DurableQueue.
        else
            Queue.Commit(tx, message);
    }

    internal static Packet? Read(SqliteConnection c, SqliteTransaction tx, string id)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT json FROM packets WHERE id=$id";
        q.Parameters.AddWithValue("$id", id);
        return q.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<Packet>(json, Contract.Json)
            : null;
    }

    internal static void Write(SqliteConnection c, SqliteTransaction tx, Packet p)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "UPDATE packets SET json=$json WHERE id=$id AND json_extract(json,'$.status') != 'cancelled'";
        q.Parameters.AddWithValue("$id", p.Id);
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(p, Contract.Json));
        q.ExecuteNonQuery();
    }

    public (Packet? Packet, int Code, string? Error) Review(
        string id,
        ReviewRequest r,
        string? actor = null,
        OperationsPlatformStore? operations = null,
        QualityStore? quality = null
    )
    {
        lock (gate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            var p = Read(c, tx, id);
            if (p is null)
                return (null, 404, "Packet not found.");
            if (p.Revision != r.Revision)
                return (null, 409, "Revision changed. Reload the packet.");
            if (p.Status is not ("review" or "approved"))
                return (null, 409, "Packet is not ready for review.");
            if (operations?.ValidateReview(c, tx, p, actor ?? "local") is { } claimError)
                return (null, 409, claimError);
            var error = Contract.Validate(p, r);
            if (error != null)
                return (null, 400, error);
            // Downstream systems need a shipment for every classified document; unknown pages stay unassigned.
            if (
                r.Approve
                && p.EffectiveWorkflow.RequiredIdentifierKinds.Contains("shipment")
                && r.Documents!.Any(d =>
                    d.Type != "unknown" && string.IsNullOrWhiteSpace(d.BusinessReference)
                )
            )
                return (null, 400, "Add a Shipment ID to every document before approving.");
            var before = JsonSerializer.Deserialize<Packet>(
                JsonSerializer.Serialize(p, Contract.Json),
                Contract.Json
            )!;
            // Snapshot every review revision before mutation; immutable original prediction is a separate artifact.
            using var q = c.CreateCommand();
            q.Transaction = tx;
            q.CommandText = "INSERT OR IGNORE INTO revisions VALUES($id,$revision,$json)";
            q.Parameters.AddWithValue("$id", p.Id);
            q.Parameters.AddWithValue("$revision", p.Revision);
            q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(p, Contract.Json));
            q.ExecuteNonQuery();
            bool previouslyApproved = p.Status == "approved";
            p.Documents = r.Documents!;
            foreach (var d in p.Documents)
            {
                d.ReviewReasons = [];
                if (d.Type == "unknown")
                    d.ReviewReasons.Add("Document type is unknown.");
                if (
                    d.PageIds.Select(pid => p.Pages.Single(x => x.Id == pid).PredictedType)
                        .Distinct()
                        .Count() > 1
                )
                    d.ReviewReasons.Add("Page predictions differ within this document.");
            }
            WorkflowAssembly.RequiredIdentifiers(p);
            p.Revision++;
            p.Status = r.Approve && !previouslyApproved ? "approved" : "review";
            p.Audit.Add(
                new(
                    DateTimeOffset.UtcNow,
                    p.Status == "approved" ? "approved"
                        : previouslyApproved ? "approval invalidated; review saved"
                        : "review saved",
                    p.Revision,
                    actor
                )
            );
            ReferenceService.OnReview(c, tx, before, p, actor ?? "local");
            ExtractionPlatform.OnReview(c, tx, before, p, actor ?? "local");
            try
            {
                ExtractionPlatform.RunConfigured(c, tx, p);
                if (r.Approve)
                    ExtractionPlatform.EnsureComplete(c, tx, p);
            }
            catch (WorkflowException exception)
            {
                return (null, exception.Code, exception.Message);
            }
            quality?.OnReview(c, tx, before, p, actor ?? "local");
            q.Parameters.Clear();
            q.CommandText = "UPDATE packets SET json=$json WHERE id=$id";
            q.Parameters.AddWithValue("$id", id);
            q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(p, Contract.Json));
            q.ExecuteNonQuery();
            operations?.RecordReview(
                c,
                tx,
                p.Id,
                r.Revision,
                actor ?? "local",
                p.Status == "approved" ? "approved" : "review saved"
            );
            if (p.Status == "approved" && NeedsApprovedContinuation(p))
                Commit(tx, new ContinuePlatformFlow(p.Id, p.Revision));
            else
                tx.Commit();
            return (p, 200, null);
        }
    }
}
