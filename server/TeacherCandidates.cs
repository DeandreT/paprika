using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace paprika;

public sealed record CandidateImportRequest(string[] PacketIds) : TrainingDatasetScopedRequest;

public sealed record CandidateReview(
    string Id,
    string Version,
    string Decision,
    string? Label = null
);

public sealed record CandidateReviewRequest(CandidateReview[] Decisions)
    : TrainingDatasetScopedRequest;

public sealed record CandidateReviewResult(int Reviewed);

public sealed record TeacherObservation(
    string PacketId,
    string PageId,
    int? Revision,
    string SourceName,
    string SourceGroup,
    int PageNumber,
    string TextSha256,
    string? WorkflowHash,
    string ProposedLabel,
    double? Confidence,
    string? Provider,
    string? Model,
    string? RequestedModel,
    string Provenance,
    bool Synthetic
);

public sealed record TeacherCandidate(
    string Id,
    string Version,
    string Comparison,
    string Status,
    string? Label,
    string? Reviewer,
    DateTimeOffset? ReviewedAt,
    string Text,
    string[] Labels,
    TeacherObservation[] Predictions
);

public sealed record TeacherLabelExport(
    TrainingExample Example,
    string GroupId,
    string Split,
    string Origin,
    TeacherCandidate? TeacherReview
);

public sealed record ReviewedDatasetExport(
    string Format,
    TrainingDataset Dataset,
    TeacherLabelExport[] Examples,
    ReviewedFieldEvidence[] FieldFeedback
);

// Candidates and decisions live outside training_examples. Train and export project the
// same currently reviewed rows inside their own transaction; no labeled copies to synchronize.
public sealed class TeacherCandidates(Store store)
{
    internal static void Initialize(SqliteConnection db, SqliteTransaction tx)
    {
        using var q = db.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            CREATE TABLE IF NOT EXISTS teacher_observations(
                dataset_id TEXT NOT NULL REFERENCES training_datasets(id),
                packet_id TEXT NOT NULL, page_id TEXT NOT NULL,
                candidate_id TEXT NOT NULL, json TEXT NOT NULL,
                PRIMARY KEY(dataset_id,packet_id,page_id)
            );
            CREATE INDEX IF NOT EXISTS teacher_packet ON teacher_observations(packet_id);
            CREATE INDEX IF NOT EXISTS teacher_candidate ON teacher_observations(dataset_id,candidate_id);
            CREATE TABLE IF NOT EXISTS teacher_reviews(
                dataset_id TEXT NOT NULL, candidate_id TEXT NOT NULL, membership TEXT NOT NULL,
                revision INTEGER NOT NULL, status TEXT NOT NULL, label TEXT, actor TEXT NOT NULL,
                reviewed_at TEXT NOT NULL, PRIMARY KEY(dataset_id,candidate_id)
            );
            CREATE TABLE IF NOT EXISTS teacher_model_sources(
                packet_id TEXT NOT NULL, model_id TEXT NOT NULL,
                PRIMARY KEY(packet_id,model_id)
            );
            """;
        q.ExecuteNonQuery();
    }

    public int Import(string datasetId, CandidateImportRequest request, OperationsIdentity identity)
    {
        RequireAdmin(identity);
        if (
            request.PacketIds is not { Length: >= 1 and <= 100 }
            || request.PacketIds.Any(string.IsNullOrWhiteSpace)
        )
            throw new WorkflowException(400, "Select 1–100 source packets.");
        using var db = store.Open();
        using var tx = db.BeginTransaction(deferred: false);
        TrainingStore.RequireDataset(db, tx, datasetId);
        int imported = 0;
        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO teacher_observations(dataset_id,packet_id,page_id,candidate_id,json)
            VALUES($dataset,$packet,$page,$id,$json)
            ON CONFLICT(dataset_id,packet_id,page_id) DO NOTHING
            """;
        insert.Parameters.AddWithValue("$dataset", datasetId);
        var packetKey = insert.Parameters.Add("$packet", SqliteType.Text);
        var pageKey = insert.Parameters.Add("$page", SqliteType.Text);
        var groupKey = insert.Parameters.Add("$id", SqliteType.Text);
        var json = insert.Parameters.Add("$json", SqliteType.Text);
        foreach (string packetId in request.PacketIds.Distinct(StringComparer.Ordinal))
        {
            var packet = Store.Read(db, tx, packetId);
            if (
                packet is null
                || !identity.CanRead(packet.EffectiveWorkflow.Id)
                || OperationsPlatformStore.IsArchived(db, tx, packetId)
            )
                throw new WorkflowException(404, "Source packet not found.");
            if (packet.Status is not ("review" or "approved"))
                throw new WorkflowException(409, "Source packet must have completed processing.");
            foreach (var prediction in PagePredictionEvidence.Read(db, tx, packet))
            {
                if (string.IsNullOrWhiteSpace(prediction.Text) || prediction.Labels.Length == 0)
                    continue;
                packetKey.Value = prediction.PacketId;
                pageKey.Value = prediction.PageId;
                groupKey.Value = TrainingClassifier.Hash(
                    prediction.ComparisonKey is { } key
                        ? "comparable:" + key
                        : "observation:"
                            + JsonSerializer.Serialize(new[] { packetId, prediction.PageId })
                );
                json.Value = JsonSerializer.Serialize(prediction, Contract.Json);
                imported += insert.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return imported;
    }

    public TeacherCandidate[] List(string datasetId, OperationsIdentity identity)
    {
        RequireAdmin(identity);
        using var db = store.Open();
        using var tx = db.BeginTransaction(deferred: true);
        TrainingStore.RequireDataset(db, tx, datasetId);
        return Read(db, tx, datasetId);
    }

    public int Review(string datasetId, CandidateReviewRequest request, OperationsIdentity identity)
    {
        RequireAdmin(identity);
        var changes = request.Decisions;
        if (
            changes is not { Length: >= 1 and <= 100 }
            || changes.Any(d => d is null)
            || changes.DistinctBy(d => d.Id).Count() != changes.Length
        )
            throw new WorkflowException(400, "Select 1–100 distinct candidates to review.");
        using var db = store.Open();
        using var tx = db.BeginTransaction(deferred: false);
        TrainingStore.RequireDataset(db, tx, datasetId);
        var candidates = Read(db, tx, datasetId, changes.Select(d => d.Id).ToArray())
            .ToDictionary(c => c.Id);
        foreach (var decision in changes)
        {
            if (
                string.IsNullOrEmpty(decision.Id)
                || !candidates.TryGetValue(decision.Id, out var row)
            )
                throw new WorkflowException(404, "Candidate not found.");
            if (decision.Version != row.Version)
                throw new WorkflowException(
                    409,
                    "The evidence or review changed. Refresh the candidates."
                );
            var label = decision.Label;
            bool proposed = row.Predictions.Any(p => p.ProposedLabel == label);
            bool valid = decision.Decision switch
            {
                "rejected" => label is null,
                "accepted" => row.Labels.Contains(label) && proposed,
                "corrected" => row.Labels.Contains(label) && !proposed,
                _ => false,
            };
            if (!valid)
                throw new WorkflowException(
                    400,
                    "Choose a proposed label to accept, another allowed label to correct, or reject without a label."
                );
            using var q = db.CreateCommand();
            q.Transaction = tx;
            q.CommandText = """
                INSERT INTO teacher_reviews(dataset_id,candidate_id,membership,revision,status,label,actor,reviewed_at)
                VALUES($dataset,$id,$members,1,$status,$label,$actor,$at)
                ON CONFLICT(dataset_id,candidate_id) DO UPDATE SET membership=excluded.membership,
                    revision=revision+1,status=excluded.status,label=excluded.label,
                    actor=excluded.actor,reviewed_at=excluded.reviewed_at
                """;
            q.Parameters.AddWithValue("$dataset", datasetId);
            q.Parameters.AddWithValue("$id", row.Id);
            q.Parameters.AddWithValue("$members", Membership(row.Predictions));
            q.Parameters.AddWithValue("$status", decision.Decision);
            q.Parameters.AddWithValue("$label", (object?)label ?? DBNull.Value);
            q.Parameters.AddWithValue("$actor", identity.Name);
            q.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            q.ExecuteNonQuery();
        }
        tx.Commit();
        return changes.Length;
    }

    private static void RequireAdmin(OperationsIdentity identity)
    {
        if (!identity.Administrator || string.IsNullOrWhiteSpace(identity.Name))
            throw new WorkflowException(
                403,
                "Training administration requires administrator access."
            );
    }

    private static string Membership(TeacherObservation[] rows) =>
        TrainingClassifier.Hash(
            JsonSerializer.Serialize(rows.Select(p => new[] { p.PacketId, p.PageId }))
        );

    private sealed record ObservationRow(
        string Id,
        PagePrediction Prediction,
        string? Membership,
        int Revision,
        string? Status,
        string? Label,
        string? Reviewer,
        DateTimeOffset? ReviewedAt
    );

    internal static TeacherCandidate[] Read(
        SqliteConnection db,
        SqliteTransaction tx,
        string datasetId,
        string[]? ids = null
    )
    {
        using var q = db.CreateCommand();
        q.Transaction = tx;
        q.Parameters.AddWithValue("$dataset", datasetId);
        // Standalone TrainingStore users need not have initialized the operations platform.
        // In particular, a manual-only fit must not depend on the packet lifecycle store.
        q.CommandText = "SELECT 1 FROM teacher_observations WHERE dataset_id=$dataset LIMIT 1";
        if (q.ExecuteScalar() is null)
            return [];
        q.CommandText = """
            SELECT o.candidate_id,o.json,r.membership,coalesce(r.revision,0),r.status,r.label,r.actor,r.reviewed_at
            FROM teacher_observations o JOIN packets p ON p.id=o.packet_id
            LEFT JOIN teacher_reviews r ON r.dataset_id=o.dataset_id AND r.candidate_id=o.candidate_id
            WHERE o.dataset_id=$dataset
                AND NOT EXISTS(SELECT 1 FROM operations_tombstones a WHERE a.packet_id=o.packet_id)
            """;
        if (ids is not null)
        {
            q.CommandText += " AND o.candidate_id IN (SELECT value FROM json_each($ids))";
            q.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(ids));
        }
        q.CommandText += " ORDER BY o.candidate_id,o.packet_id,o.page_id";
        using var reader = q.ExecuteReader();
        var observations = new List<ObservationRow>();
        while (reader.Read())
            observations.Add(
                new(
                    reader.GetString(0),
                    JsonSerializer.Deserialize<PagePrediction>(reader.GetString(1), Contract.Json)!,
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetDateTimeOffset(7)
                )
            );
        return observations
            .GroupBy(o => o.Id)
            .Select(ToCandidate)
            .OrderBy(c => c.Comparison == "disagreement" ? 0 : 1)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static TeacherCandidate ToCandidate(IGrouping<string, ObservationRow> rows)
    {
        var first = rows.First();
        var evidence = rows.Select(r => r.Prediction).ToArray();
        var predictions = evidence
            .Select(p => new TeacherObservation(
                p.PacketId,
                p.PageId,
                p.PredictionRevision,
                p.SourceName,
                p.SourceGroup,
                p.PageNumber,
                p.TextSha256,
                p.WorkflowHash,
                p.Label,
                p.Confidence,
                p.Provider,
                p.Model,
                p.RequestedModel,
                p.Provenance,
                p.Synthetic
            ))
            .ToArray();
        var membership = Membership(predictions);
        bool reviewed = first.Membership == membership;
        var comparison = "single";
        foreach (var observation in evidence)
        {
            var result = PagePredictionEvidence.Compare(observation, evidence);
            if (result == "disagreement")
            {
                comparison = result;
                break;
            }
            if (result == "agreement")
                comparison = result;
        }
        return new(
            rows.Key,
            $"{first.Revision}:{membership}",
            comparison,
            reviewed ? first.Status! : "pending",
            reviewed ? first.Label : null,
            reviewed ? first.Reviewer : null,
            reviewed ? first.ReviewedAt : null,
            first.Prediction.Text,
            first.Prediction.Labels,
            predictions
        );
    }

    internal static TrainingExample Example(TeacherCandidate candidate)
    {
        var source = candidate.Predictions[0];
        return new(
            "teacher:" + candidate.Id,
            candidate.Text,
            candidate.Label!,
            source.Synthetic,
            $"Reviewed teacher label: {candidate.Status} by {candidate.Reviewer}",
            source.PacketId,
            source.Revision,
            $"{source.SourceGroup}:{source.PageNumber}",
            candidate.ReviewedAt
        )
        {
            ReviewedSources = candidate
                .Predictions.Select(p => new TrainingPageRef(p.PacketId, p.PageId))
                .ToArray(),
        };
    }

    internal static TeacherCandidate[] Reviewed(
        SqliteConnection db,
        SqliteTransaction tx,
        string datasetId
    ) => Read(db, tx, datasetId).Where(c => c.Status is "accepted" or "corrected").ToArray();

    internal static void RecordModelSources(
        SqliteConnection db,
        SqliteTransaction tx,
        string modelId,
        TeacherCandidate[] reviewed
    )
    {
        using var q = db.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "INSERT INTO teacher_model_sources(packet_id,model_id) VALUES($packet,$model)";
        q.Parameters.AddWithValue("$model", modelId);
        var packet = q.Parameters.Add("$packet", SqliteType.Text);
        foreach (
            string id in reviewed.SelectMany(c => c.Predictions).Select(p => p.PacketId).Distinct()
        )
        {
            packet.Value = id;
            q.ExecuteNonQuery();
        }
    }

    public ReviewedDatasetExport Export(string datasetId, OperationsIdentity identity)
    {
        RequireAdmin(identity);
        using var db = store.Open();
        using var tx = db.BeginTransaction(deferred: true);
        var dataset = TrainingStore.RequireDataset(db, tx, datasetId);
        var reviewed = Reviewed(db, tx, datasetId);
        var teachers = reviewed.ToDictionary(row => Example(row).Id);
        var manual = TrainingStore.ReadExamples(db, tx, datasetId);
        var examples = TrainingClassifier
            .SplitExamples(manual, reviewed.Select(Example).ToArray())
            .Select(r => new TeacherLabelExport(
                r.Example,
                r.GroupId,
                r.Split,
                r.TeacherReviewed ? "reviewed-teacher" : "labeled-example",
                r.TeacherReviewed ? teachers[r.Example.Id] : null
            ))
            .ToArray();
        // Only the immutable provider receipts bind a reviewed candidate to exact pages.
        // Imported example IDs and provenance are operator-supplied and cannot select fields.
        var pages = reviewed.SelectMany(c => c.Predictions);
        var fields = pages
            .GroupBy(p => p.PacketId)
            .SelectMany(group =>
                ReviewedFieldEvidence.Read(
                    db,
                    tx,
                    group.Key,
                    group
                        .GroupBy(p => p.PageId)
                        .Where(g => g.Select(p => p.TextSha256).Distinct().Count() == 1)
                        .ToDictionary(g => g.Key, g => g.First().TextSha256)
                )
            )
            .ToArray();
        return new("paprika.reviewed-page-labels.v1", dataset, examples, fields);
    }
}
