using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace paprika;

public sealed class TrainingStore
{
    public const string DefaultDatasetId = "default";
    private const string DefaultDatasetName = "Legacy";
    private readonly Store store;

    public TrainingStore(Store store)
    {
        this.store = store;
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: false);
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            SELECT count(*) > 0 AND count(*) FILTER (WHERE name='dataset_id') = 0
            FROM pragma_table_info('training_examples')
            """;
        bool migrate = Convert.ToBoolean(q.ExecuteScalar());
        if (migrate)
        {
            q.CommandText = "ALTER TABLE training_examples RENAME TO training_examples_legacy";
            q.ExecuteNonQuery();
        }
        q.CommandText = """
            CREATE TABLE IF NOT EXISTS training_datasets(
                id TEXT PRIMARY KEY NOT NULL, name TEXT NOT NULL, name_key TEXT NOT NULL UNIQUE
            );
            INSERT INTO training_datasets(id,name,name_key) VALUES($default,$name,$nameKey)
                ON CONFLICT(id) DO NOTHING;
            CREATE TABLE IF NOT EXISTS training_examples(
                dataset_id TEXT NOT NULL REFERENCES training_datasets(id),
                id TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY(dataset_id,id)
            );
            CREATE TABLE IF NOT EXISTS training_models(id TEXT PRIMARY KEY,json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS training_predictions(
                packet_id TEXT NOT NULL,page_id TEXT NOT NULL,model_hash TEXT NOT NULL,
                json TEXT NOT NULL,PRIMARY KEY(packet_id,page_id,model_hash)
            );
            """;
        q.Parameters.AddWithValue("$default", DefaultDatasetId);
        q.Parameters.AddWithValue("$name", DefaultDatasetName);
        q.Parameters.AddWithValue("$nameKey", DefaultDatasetName.ToUpperInvariant());
        q.ExecuteNonQuery();
        if (migrate)
        {
            q.CommandText = """
                INSERT INTO training_examples(dataset_id,id,json)
                    SELECT $default,id,json FROM training_examples_legacy;
                DROP TABLE training_examples_legacy;
                """;
            q.ExecuteNonQuery();
        }
        TeacherCandidates.Initialize(c, tx);
        tx.Commit();
    }

    public TrainingDataset[] Datasets()
    {
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT id,name FROM training_datasets ORDER BY id <> $default,name_key,id";
        q.Parameters.AddWithValue("$default", DefaultDatasetId);
        using var r = q.ExecuteReader();
        var datasets = new List<TrainingDataset>();
        while (r.Read())
            datasets.Add(new(r.GetString(0), r.GetString(1)));
        return datasets.ToArray();
    }

    public TrainingDataset CreateDataset(TrainingDatasetRequest request) =>
        SaveDatasetName(Guid.NewGuid().ToString("N"), request.Name, expectedName: null);

    public TrainingDataset RenameDataset(string datasetId, TrainingDatasetRenameRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ExpectedName))
            throw new WorkflowException(400, "Supply the current dataset name when renaming it.");
        return SaveDatasetName(datasetId, request.Name, request.ExpectedName);
    }

    private TrainingDataset SaveDatasetName(
        string datasetId,
        string proposedName,
        string? expectedName
    )
    {
        string? name = proposedName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 120 || name.Any(char.IsControl))
            throw new WorkflowException(
                400,
                "Dataset name must contain 1–120 printable characters."
            );

        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: false);
        if (expectedName is not null)
        {
            var current = RequireDataset(c, tx, datasetId);
            if (datasetId == DefaultDatasetId)
                throw new WorkflowException(409, "The Legacy dataset cannot be renamed.");
            if (current.Name == name)
                return current;
            if (current.Name != expectedName)
                throw new WorkflowException(
                    409,
                    "The dataset name changed. Refresh before renaming it."
                );
        }
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = expectedName is null
            ? "INSERT INTO training_datasets(id,name,name_key) VALUES($id,$name,$nameKey)"
            : "UPDATE training_datasets SET name=$name,name_key=$nameKey WHERE id=$id";
        q.Parameters.AddWithValue("$id", datasetId);
        q.Parameters.AddWithValue("$name", name);
        q.Parameters.AddWithValue("$nameKey", name.ToUpperInvariant());
        try
        {
            q.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == 2067) // SQLITE_CONSTRAINT_UNIQUE
        {
            throw new WorkflowException(409, "A training dataset already uses that name.");
        }
        tx.Commit();
        return new(datasetId, name);
    }

    public static TrainingDataset RequireDataset(
        SqliteConnection c,
        SqliteTransaction tx,
        string datasetId
    )
    {
        if (
            string.IsNullOrWhiteSpace(datasetId)
            || datasetId.Length > 80
            || !datasetId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
        )
            throw new WorkflowException(400, "A valid training dataset ID is required.");
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT name FROM training_datasets WHERE id=$id";
        q.Parameters.AddWithValue("$id", datasetId);
        return q.ExecuteScalar() is string name
            ? new(datasetId, name)
            : throw new WorkflowException(404, "Training dataset not found.");
    }

    public TrainingExample[] Examples(string datasetId = DefaultDatasetId)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: true);
        RequireDataset(c, tx, datasetId);
        var examples = ReadExamples(c, tx, datasetId);
        tx.Commit();
        return examples;
    }

    public TrainingExampleSummary[] ExampleSummaries(string datasetId)
    {
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: true);
        RequireDataset(c, tx, datasetId);
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            """
                SELECT id,json_extract(json,'$.label'),coalesce(json_extract(json,'$.synthetic'),1)
                FROM training_examples WHERE dataset_id=$dataset
                """
            + ActiveExampleFilter(c, tx)
            + " ORDER BY id";
        q.Parameters.AddWithValue("$dataset", datasetId);
        using var r = q.ExecuteReader();
        var rows = new List<TrainingExampleSummary>();
        while (r.Read())
            rows.Add(new(r.GetString(0), r.GetString(1), r.GetBoolean(2)));
        return rows.ToArray();
    }

    internal static TrainingExample[] ReadExamples(
        SqliteConnection c,
        SqliteTransaction tx,
        string datasetId
    )
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT json FROM training_examples WHERE dataset_id=$dataset"
            + ActiveExampleFilter(c, tx)
            + " ORDER BY id";
        q.Parameters.AddWithValue("$dataset", datasetId);
        using var r = q.ExecuteReader();
        var rows = new List<TrainingExample>();
        while (r.Read())
            rows.Add(JsonSerializer.Deserialize<TrainingExample>(r.GetString(0), Contract.Json)!);
        return rows.ToArray();
    }

    // Manually imported examples may reference external packet IDs. Only an actual
    // local archive/purge tombstone hides those rows; packet-less imports remain portable.
    private static string ActiveExampleFilter(SqliteConnection c, SqliteTransaction tx) =>
        OperationsPlatformStore.TableExists(c, tx, "operations_tombstones")
            ? " AND NOT EXISTS(SELECT 1 FROM operations_tombstones a WHERE a.packet_id=json_extract(training_examples.json,'$.packetId'))"
            : "";

    public int Import(TrainingExample[] examples, string datasetId = DefaultDatasetId)
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
        RequireDataset(c, tx, datasetId);
        foreach (var e in examples)
            Insert(c, tx, e, datasetId);
        tx.Commit();
        return examples.Length;
    }

    // Caller resolves the dataset once with RequireDataset in the same transaction.
    internal static void Insert(
        SqliteConnection c,
        SqliteTransaction tx,
        TrainingExample example,
        string datasetId
    )
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT json FROM training_examples WHERE dataset_id=$dataset AND id=$id";
        q.Parameters.AddWithValue("$dataset", datasetId);
        q.Parameters.AddWithValue("$id", example.Id);
        string json = JsonSerializer.Serialize(example, Contract.Json);
        if (q.ExecuteScalar() is string existing)
        {
            // Earlier stored rows may omit optional provenance fields that now
            // serialize as null. An equivalent replay keeps the original bytes.
            if (
                existing != json
                && JsonSerializer.Deserialize<TrainingExample>(existing, Contract.Json) != example
            )
                throw new WorkflowException(
                    409,
                    "An immutable training example already uses that ID."
                );
            return;
        }
        // Permit replays of any pre-upgrade manual ID, including a formerly arbitrary
        // teacher: prefix. Only the generated teacher:<SHA-256> namespace is reserved.
        const string teacherPrefix = "teacher:";
        if (
            example.Id.Length == teacherPrefix.Length + 64
            && example.Id.StartsWith(teacherPrefix, StringComparison.Ordinal)
            && !example.Id.AsSpan(teacherPrefix.Length).ContainsAnyExcept("0123456789abcdef")
        )
            throw new WorkflowException(
                400,
                "Teacher example IDs are reserved for reviewed predictions."
            );
        q.CommandText =
            "INSERT INTO training_examples(dataset_id,id,json) VALUES($dataset,$id,$json)";
        q.Parameters.AddWithValue("$json", json);
        q.ExecuteNonQuery();
    }

    public TrainingModelResponse Train(TrainRequest request, string datasetId = DefaultDatasetId)
    {
        if (request.DatasetId is { } id && id != datasetId)
            throw new WorkflowException(
                400,
                "The model request does not match the selected dataset."
            );
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120)
            throw new WorkflowException(400, "Model name must contain 1–120 characters.");
        // The retention check must see the model before any contributing packet can be purged.
        using var c = store.Open();
        using var tx = c.BeginTransaction(deferred: false);
        var dataset = RequireDataset(c, tx, datasetId);
        var examples = ReadExamples(c, tx, datasetId);
        var reviewed = TeacherCandidates.Reviewed(c, tx, datasetId);
        var teachers = reviewed.Select(TeacherCandidates.Example).ToArray();
        var artifact = TrainingClassifier.Fit(examples, request.Threshold, teachers) with
        {
            Dataset = dataset,
            DatasetSnapshotHash =
                reviewed.Length == 0
                    ? SnapshotHash(examples)
                    : SnapshotHash(new { Examples = examples, TeacherReviews = reviewed }),
            DatasetExampleCount = examples.Length + teachers.Length,
        };
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
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "INSERT INTO training_models(id,json) VALUES($id,$json)";
        q.Parameters.AddWithValue("$id", model.Id);
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(model, Contract.Json));
        q.ExecuteNonQuery();
        TeacherCandidates.RecordModelSources(c, tx, model.Id, reviewed);
        tx.Commit();
        return model;
    }

    // SHA-256 of the complete JSON array in persisted ID order, before deduplication or splitting.
    private static string SnapshotHash<T>(T examples)
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        JsonSerializer.Serialize(stream, examples, Contract.Json);
        stream.FlushFinalBlock();
        return Convert.ToHexStringLower(hash.Hash!);
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
        var capabilities = after.EffectiveWorkflow.Capabilities;
        var collection = capabilities?.ReviewCollection;
        if (
            collection is null
            && capabilities?.Training is { } training
            && Resolve(training).Dataset is { Id: not DefaultDatasetId }
        )
            return;
        string datasetId = collection is null ? DefaultDatasetId : collection.DatasetId;
        RequireDataset(c, tx, datasetId);
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
                ),
                datasetId
            );
        }
    }
}
