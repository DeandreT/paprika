using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace paprika;

public sealed class TrainingStore
{
    private readonly Store store;

    public TrainingStore(Store store)
    {
        this.store = store;
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "CREATE TABLE IF NOT EXISTS training_examples(id TEXT PRIMARY KEY,json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS training_models(id TEXT PRIMARY KEY,json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS training_predictions(packet_id TEXT NOT NULL,page_id TEXT NOT NULL,model_hash TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(packet_id,page_id,model_hash));";
        q.ExecuteNonQuery();
    }

    public TrainingExample[] Examples()
    {
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT json FROM training_examples ORDER BY id";
        using var r = q.ExecuteReader();
        var rows = new List<TrainingExample>();
        while (r.Read())
            rows.Add(JsonSerializer.Deserialize<TrainingExample>(r.GetString(0), Contract.Json)!);
        return rows.ToArray();
    }

    public int Import(TrainingExample[] examples)
    {
        if (examples is null || examples.Length is < 1 or > 10000)
            throw new WorkflowException(400, "Import 1–10000 labeled examples.");
        foreach (var e in examples)
            if (
                e is null
                || string.IsNullOrWhiteSpace(e.Id)
                || e.Id.Length > 150
                || string.IsNullOrWhiteSpace(e.Text)
                || e.Text.Length > 100000
                || string.IsNullOrWhiteSpace(e.Label)
                || e.Label.Length > 80
                || string.IsNullOrWhiteSpace(e.Provenance)
                || e.Provenance.Length > 500
                || e.SourcePage is { Length: > 300 }
            )
                throw new WorkflowException(
                    400,
                    "Each example requires a bounded ID, text, label and provenance."
                );
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        foreach (var e in examples)
            Insert(c, tx, e);
        tx.Commit();
        return examples.Length;
    }

    internal static void Insert(SqliteConnection c, SqliteTransaction tx, TrainingExample example)
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT json FROM training_examples WHERE id=$id";
        q.Parameters.AddWithValue("$id", example.Id);
        string json = JsonSerializer.Serialize(example, Contract.Json);
        if (q.ExecuteScalar() is string existing)
        {
            if (existing != json)
                throw new WorkflowException(
                    409,
                    "An immutable training example already uses that ID."
                );
            return;
        }
        q.CommandText = "INSERT INTO training_examples(id,json) VALUES($id,$json)";
        q.Parameters.AddWithValue("$json", json);
        q.ExecuteNonQuery();
    }

    public TrainingModelResponse Train(TrainRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120)
            throw new WorkflowException(400, "Model name must contain 1–120 characters.");
        var artifact = TrainingClassifier.Fit(Examples(), request.Threshold);
        string payload = JsonSerializer.Serialize(artifact, Contract.Json);
        var model = new TrainingModelResponse(
            Guid.NewGuid().ToString("N"),
            request.Name,
            TrainingClassifier.Hash(payload),
            false,
            null,
            DateTimeOffset.UtcNow,
            artifact
        );
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText = "INSERT INTO training_models(id,json) VALUES($id,$json)";
        q.Parameters.AddWithValue("$id", model.Id);
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(model, Contract.Json));
        q.ExecuteNonQuery();
        return model;
    }

    public TrainingModelResponse[] Models()
    {
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT json FROM training_models ORDER BY rowid DESC";
        using var r = q.ExecuteReader();
        var models = new List<TrainingModelResponse>();
        while (r.Read())
            models.Add(
                JsonSerializer.Deserialize<TrainingModelResponse>(r.GetString(0), Contract.Json)!
            );
        return models.ToArray();
    }

    public TrainingModelResponse Approve(string id, string hash, string actor)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT json FROM training_models WHERE id=$id";
        q.Parameters.AddWithValue("$id", id);
        var model = q.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<TrainingModelResponse>(json, Contract.Json)!
            : throw new WorkflowException(404, "Training model not found.");
        if (model.Hash != hash)
            throw new WorkflowException(409, "Model hash changed.");
        if (string.IsNullOrWhiteSpace(actor))
            throw new WorkflowException(403, "A server-resolved actor is required.");
        if (model.Artifact.Synthetic || model.Artifact.Evaluation.Status != "held-out")
            throw new WorkflowException(
                400,
                "Only a real-data model with held-out evaluation can be approved."
            );
        model = model with { Approved = true, ApprovedBy = actor };
        q.CommandText = "UPDATE training_models SET json=$json WHERE id=$id";
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(model, Contract.Json));
        q.ExecuteNonQuery();
        tx.Commit();
        return model;
    }

    public TrainingModelRef Freeze(TrainingModelRef reference)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var frozen = Freeze(c, tx, reference);
        tx.Commit();
        return frozen;
    }

    public static TrainingModelRef Freeze(
        SqliteConnection c,
        SqliteTransaction tx,
        TrainingModelRef reference
    )
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT json FROM training_models WHERE id=$id";
        q.Parameters.AddWithValue("$id", reference.Id);
        var model = q.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<TrainingModelResponse>(json, Contract.Json)!
            : throw new WorkflowException(400, "Referenced training model does not exist.");
        string payload = JsonSerializer.Serialize(model.Artifact, Contract.Json);
        if (
            (reference.Hash is not null && reference.Hash != model.Hash)
            || (reference.Payload is not null && reference.Payload != payload)
        )
            throw new WorkflowException(
                409,
                "Training artifact does not match its retained model."
            );
        if (reference.Payload is not null && reference.Approved && !model.Approved)
            throw new WorkflowException(
                400,
                "Frozen approval is not backed by the retained model."
            );
        // Approval acquired after publication must not silently activate an existing snapshot.
        return new(
            model.Id,
            model.Hash,
            payload,
            reference.Payload is null ? model.Approved : reference.Approved
        );
    }

    public static TrainingArtifact Resolve(TrainingModelRef reference)
    {
        if (
            reference.Payload is null
            || reference.Hash is null
            || reference.Payload.Length > 16_000_000
            || TrainingClassifier.Hash(reference.Payload) != reference.Hash
        )
            throw new WorkflowException(
                400,
                "A hash-verified frozen training artifact is required."
            );
        TrainingArtifact model;
        try
        {
            model = JsonSerializer.Deserialize<TrainingArtifact>(reference.Payload, Contract.Json)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new WorkflowException(400, "Training artifact is invalid.");
        }
        if (model.Algorithm != TrainingClassifier.Algorithm || model.Labels.Length < 2)
            throw new WorkflowException(400, "Training algorithm or labels are invalid.");
        return model;
    }

    public void Apply(Packet packet)
    {
        var reference = packet.EffectiveWorkflow.Capabilities?.Training;
        if (reference is null)
            return;
        var model = Resolve(reference);
        if (model.Labels.Any(l => !packet.EffectiveWorkflow.Types.Any(t => t.Id == l)))
            throw new WorkflowException(
                400,
                "Training labels must belong to the workflow release."
            );
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        foreach (var page in packet.Pages)
        {
            var prediction = TrainingClassifier.Predict(model, page.Text);
            page.PredictedType = prediction.Label;
            page.Confidence = prediction.Score;
            page.ReviewReasons.RemoveAll(r =>
                r
                    is "Low classification score."
                        or "Uncertain document type."
                        or "No unique type rule matched; manual classification required."
                        or "Explicit continuation/page numbering suggests continuation; verify boundary."
                        or "New document by file boundary, first-page marker, or absence of explicit continuation; same type alone does not merge pages."
                        or "Heuristic simulation only: no AI/OCR; illustrative rule scores are not calibrated probabilities. Manual review required."
            );
            if (prediction.Label == "unknown")
                page.ReviewReasons.Add("Trained classifier found insufficient text evidence.");
            using var q = c.CreateCommand();
            q.Transaction = tx;
            q.CommandText =
                "INSERT OR IGNORE INTO training_predictions(packet_id,page_id,model_hash,json) VALUES($p,$page,$hash,$json)";
            q.Parameters.AddWithValue("$p", packet.Id);
            q.Parameters.AddWithValue("$page", page.Id);
            q.Parameters.AddWithValue("$hash", reference.Hash!);
            q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(prediction, Contract.Json));
            q.ExecuteNonQuery();
        }
        tx.Commit();
    }

    internal void Capture(
        SqliteConnection c,
        SqliteTransaction tx,
        Packet before,
        Packet after,
        string actor
    )
    {
        if (after.Status != "approved")
            return;
        foreach (var doc in after.Documents.Where(d => d.Type != "unknown"))
        foreach (var id in doc.PageIds)
        {
            var page = before.Pages.Single(p => p.Id == id);
            if (string.IsNullOrWhiteSpace(page.Text))
                continue;
            Insert(
                c,
                tx,
                new(
                    $"review:{after.Id}:{after.Revision}:{id}",
                    page.Text,
                    doc.Type,
                    after.TestRun || after.Mode == "demo",
                    $"approved review by {actor}",
                    after.Id,
                    after.Revision,
                    before.Sources.SingleOrDefault(s => s.Id == page.SourceId)?.Sha256 is { } hash
                        ? $"{hash}:{page.PageNumber}"
                        : $"{after.Id}:{page.Id}",
                    after
                        .Audit.LastOrDefault(a =>
                            a.Revision == after.Revision && a.Action == "approved"
                        )
                        ?.At
                        ?? after.CreatedAt
                )
            );
        }
    }
}
