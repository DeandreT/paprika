using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace PaperworkClassifier;

public sealed class QualityStore
{
    private readonly Store store;
    private readonly TrainingStore training;

    public QualityStore(Store store, TrainingStore training)
    {
        this.store = store;
        this.training = training;
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "CREATE TABLE IF NOT EXISTS quality_decisions(packet_id TEXT NOT NULL,revision INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(packet_id,revision)); CREATE TABLE IF NOT EXISTS quality_qa(id TEXT PRIMARY KEY,packet_id TEXT NOT NULL,packet_revision INTEGER NOT NULL,revision INTEGER NOT NULL,json TEXT NOT NULL,UNIQUE(packet_id,packet_revision));";
        q.ExecuteNonQuery();
    }

    public static void Validate(QualityPolicy? policy, TrainingModelRef? reference) =>
        QualityValidation.Validate(policy, reference);

    public static bool Sample(string packetId, string workflowHash, QualityPolicy policy)
    {
        string hash = TrainingClassifier.Hash(
            policy.SampleSeed + "\n" + workflowHash + "\n" + packetId
        );
        // Exact 52-bit fraction, independent of classification scores and review routing.
        double fraction =
            ulong.Parse(hash[..13], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            / 4503599627370496d;
        return fraction < policy.SampleRate;
    }

    public QualityDecision Route(Packet packet, IReadOnlyCollection<string>? blockers = null)
    {
        var policy = packet.EffectiveWorkflow.Capabilities?.Quality;
        if (policy is null)
            return new(
                packet.Id,
                packet.Revision,
                "manual",
                false,
                false,
                null,
                [new("packet", packet.Id, "Manual approval required.")],
                DateTimeOffset.UtcNow
            );
        var reference = packet.EffectiveWorkflow.Capabilities?.Training;
        Validate(policy, reference);
        var items = new List<QualityItem>();
        if (policy.Mode == "manual")
            items.Add(new("packet", packet.Id, "Manual approval required by policy."));
        bool trained = reference is not null;
        foreach (var page in packet.Pages)
        {
            if (
                !trained
                || page.PredictedType == "unknown"
                || page.Confidence < policy.Threshold
                || !double.IsFinite(page.Confidence)
            )
                items.Add(
                    new(
                        "page",
                        page.Id,
                        !trained
                            ? "Uncalibrated provider/rule scores require review."
                            : "Unknown type or trained-model score below routing threshold."
                    )
                );
            foreach (string reason in page.ReviewReasons.Where(r => !Disclaimer(r)))
                items.Add(new("page", page.Id, reason));
        }
        foreach (var document in packet.Documents)
        {
            var type = packet.EffectiveWorkflow.Types.SingleOrDefault(t => t.Id == document.Type);
            var split = type?.Split ?? packet.EffectiveWorkflow.DefaultSplit;
            if (split.Mode == "inherit")
                split = packet.EffectiveWorkflow.DefaultSplit;
            // Text-classifier evaluation does not calibrate probabilistic boundaries.
            if (split.Mode is not ("separate" or "fixed"))
                items.Add(
                    new(
                        "document",
                        document.Id,
                        "Boundary policy requires manual verification; text-model evaluation does not validate boundaries."
                    )
                );
            if (document.Type == "unknown")
                items.Add(new("document", document.Id, "Document type is unknown."));
            foreach (string reason in document.ReviewReasons.Where(r => !Disclaimer(r)))
                items.Add(new("document", document.Id, reason));
        }
        if (packet.Pages.Count == 0 || packet.Documents.Count == 0)
            items.Add(
                new("packet", packet.Id, "Packet has no complete extracted pages/documents.")
            );
        if (Contract.Validate(packet, new(packet.Revision, packet.Documents, false)) is { } invalid)
            items.Add(new("packet", packet.Id, invalid));
        foreach (var blocker in blockers ?? [])
            items.Add(new("validation", packet.Id, blocker));
        if (packet.TestRun || packet.Mode == "demo" || policy.SyntheticEvaluation)
            items.Add(
                new(
                    "packet",
                    packet.Id,
                    "Synthetic/test evaluation cannot operationally autoapprove."
                )
            );
        if (reference is not null && TrainingStore.Resolve(reference).Synthetic)
            items.Add(
                new("packet", packet.Id, "Synthetic training cannot operationally autoapprove.")
            );
        // Presence of a frozen model is insufficient: require the actual Apply checkpoint for every page.
        if (policy.AutoApprove && reference is not null)
        {
            using var c = store.Open();
            using var q = c.CreateCommand();
            q.CommandText =
                "SELECT page_id,json FROM training_predictions WHERE packet_id=$p AND model_hash=$h";
            q.Parameters.AddWithValue("$p", packet.Id);
            q.Parameters.AddWithValue("$h", reference.Hash!);
            using var reader = q.ExecuteReader();
            var applied = new Dictionary<string, TrainingPrediction>();
            while (reader.Read())
                applied[reader.GetString(0)] = JsonSerializer.Deserialize<TrainingPrediction>(
                    reader.GetString(1),
                    Contract.Json
                )!;
            if (
                packet.Pages.Any(p =>
                    !applied.TryGetValue(p.Id, out var prediction)
                    || prediction.Label != p.PredictedType
                    || prediction.Score != p.Confidence
                )
            )
                items.Add(
                    new(
                        "packet",
                        packet.Id,
                        "Frozen trained-model predictions have not been applied to every current page."
                    )
                );
        }
        var decision = new QualityDecision(
            packet.Id,
            packet.Revision,
            policy.Mode,
            policy.AutoApprove && items.Count == 0,
            Sample(packet.Id, packet.EffectiveWorkflow.Hash, policy),
            reference?.Hash,
            items.Distinct().ToArray(),
            DateTimeOffset.UtcNow
        );
        return decision;
    }

    public void OnProcessed(
        SqliteConnection c,
        SqliteTransaction tx,
        Packet packet,
        QualityDecision decision
    )
    {
        if (
            decision.PacketId != packet.Id
            || decision.Revision != packet.Revision
            || packet.Status is not ("review" or "approved")
        )
            throw new WorkflowException(
                409,
                "Quality decision must match the finalized packet revision."
            );
        if (packet.Status == "approved" && !decision.AutoApprove)
            throw new WorkflowException(
                409,
                "Quality decision does not authorize automated approval."
            );
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "INSERT INTO quality_decisions(packet_id,revision,json) VALUES($id,$r,$json) ON CONFLICT(packet_id,revision) DO UPDATE SET json=excluded.json";
        q.Parameters.AddWithValue("$id", packet.Id);
        q.Parameters.AddWithValue("$r", packet.Revision);
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(decision, Contract.Json));
        q.ExecuteNonQuery();
        if (packet.Status == "approved" && decision.AutoApprove && decision.Sampled)
            CreateQa(c, tx, packet, "automation");
        // Machine predictions are deliberately not fed back as human training labels.
    }

    private static bool Disclaimer(string reason) =>
        reason
            is "Manual approval required."
                or "Manual approval required; model scores are not calibrated."
                or "Manual approval required; simulation scores are not calibrated."
        || reason.StartsWith("Simulation only;", StringComparison.Ordinal);

    public void OnReview(
        SqliteConnection c,
        SqliteTransaction tx,
        Packet before,
        Packet after,
        string actor
    )
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new WorkflowException(403, "A server-resolved reviewer is required.");
        if (actor != "automation")
            training.Capture(c, tx, before, after, actor);
        var policy = after.EffectiveWorkflow.Capabilities?.Quality;
        if (
            policy is not null
            && after.Status == "approved"
            && Sample(after.Id, after.EffectiveWorkflow.Hash, policy)
        )
            CreateQa(c, tx, after, actor);
    }

    private static void CreateQa(
        SqliteConnection c,
        SqliteTransaction tx,
        Packet packet,
        string actor
    )
    {
        var snapshot = JsonSerializer.Deserialize<Packet>(
            JsonSerializer.Serialize(packet, Contract.Json),
            Contract.Json
        )!;
        var task = new QualityQaTask(
            Guid.NewGuid().ToString("N"),
            packet.Id,
            packet.Revision,
            1,
            "pending",
            actor,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            snapshot
        );
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "INSERT OR IGNORE INTO quality_qa(id,packet_id,packet_revision,revision,json) VALUES($id,$p,$pr,1,$json)";
        q.Parameters.AddWithValue("$id", task.Id);
        q.Parameters.AddWithValue("$p", packet.Id);
        q.Parameters.AddWithValue("$pr", packet.Revision);
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(task, Contract.Json));
        q.ExecuteNonQuery();
    }

    public QualityQaTask[] Tasks(string? packetId = null)
    {
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "SELECT json FROM quality_qa"
            + (packetId is null ? "" : " WHERE packet_id=$p")
            + " ORDER BY rowid DESC";
        if (packetId is not null)
            q.Parameters.AddWithValue("$p", packetId);
        using var r = q.ExecuteReader();
        var tasks = new List<QualityQaTask>();
        while (r.Read())
            tasks.Add(JsonSerializer.Deserialize<QualityQaTask>(r.GetString(0), Contract.Json)!);
        return tasks.ToArray();
    }

    public QualityPacketResponse Get(string packetId)
    {
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "SELECT json FROM quality_decisions WHERE packet_id=$p ORDER BY revision DESC LIMIT 1";
        q.Parameters.AddWithValue("$p", packetId);
        var decision = q.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<QualityDecision>(json, Contract.Json)
            : null;
        return new(decision, Tasks(packetId).Select(QualityQaTaskResponse.From).ToArray());
    }

    public QualityQaTask Complete(
        string id,
        QualityQaRequest request,
        string actor,
        bool requireCurrentPacket = false
    )
    {
        if (
            request.Verdict is not ("pass" or "fail")
            || request.Note is null
            || request.Note.Length > 2000
        )
            throw new WorkflowException(
                400,
                "QA requires pass/fail and a note of at most 2000 characters."
            );
        if (string.IsNullOrWhiteSpace(actor))
            throw new WorkflowException(403, "A server-resolved reviewer is required.");
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT json FROM quality_qa WHERE id=$id";
        q.Parameters.AddWithValue("$id", id);
        var task = q.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<QualityQaTask>(json, Contract.Json)!
            : throw new WorkflowException(404, "QA task not found.");
        if (requireCurrentPacket)
        {
            if (Store.Read(c, tx, task.PacketId) is null)
                throw new WorkflowException(404, "QA task not found.");
            using var archive = c.CreateCommand();
            archive.Transaction = tx;
            archive.CommandText = "SELECT 1 FROM operations_tombstones WHERE packet_id=$packet";
            archive.Parameters.AddWithValue("$packet", task.PacketId);
            if (archive.ExecuteScalar() is not null)
                throw new WorkflowException(404, "QA task not found.");
        }
        if (task.Revision != request.Revision || task.Status != "pending")
            throw new WorkflowException(409, "QA task changed or is already complete.");
        if (
            (
                task.Snapshot.EffectiveWorkflow.Capabilities?.Quality?.RequireIndependentReviewer
                ?? true
            )
            && actor == task.PrimaryActor
        )
            throw new WorkflowException(
                403,
                "An independent reviewer must complete this QA sample."
            );
        var completed = task with
        {
            Revision = task.Revision + 1,
            Status = "completed",
            Reviewer = actor,
            Verdict = request.Verdict,
            Note = request.Note,
        };
        q.CommandText =
            "UPDATE quality_qa SET revision=$next,json=$json WHERE id=$id AND revision=$expected";
        q.Parameters.AddWithValue("$next", completed.Revision);
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(completed, Contract.Json));
        q.Parameters.AddWithValue("$expected", request.Revision);
        if (q.ExecuteNonQuery() != 1)
            throw new WorkflowException(409, "QA task changed.");
        // Failure must reopen the current approved revision, but never overwrite a later edit.
        if (
            request.Verdict == "fail"
            && Store.Read(c, tx, task.PacketId) is { Status: "approved" } packet
            && packet.Revision == task.PacketRevision
        )
        {
            using var snapshot = c.CreateCommand();
            snapshot.Transaction = tx;
            snapshot.CommandText =
                "INSERT OR IGNORE INTO revisions(packet_id,revision,json) VALUES($p,$r,$json)";
            snapshot.Parameters.AddWithValue("$p", packet.Id);
            snapshot.Parameters.AddWithValue("$r", packet.Revision);
            snapshot.Parameters.AddWithValue(
                "$json",
                JsonSerializer.Serialize(packet, Contract.Json)
            );
            snapshot.ExecuteNonQuery();
            packet.Status = "review";
            packet.Revision++;
            packet.Audit.Add(
                new(
                    DateTimeOffset.UtcNow,
                    "Independent QA failed; review required",
                    packet.Revision,
                    actor
                )
            );
            Store.Write(c, tx, packet);
        }
        tx.Commit();
        return completed;
    }
}
