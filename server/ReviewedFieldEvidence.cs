using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace paprika;

public sealed record ReviewedFieldEvidence(
    string PacketId,
    bool Synthetic,
    string DocumentId,
    string DefinitionId,
    int DefinitionVersion,
    string DefinitionSha256,
    string Field,
    string Kind,
    ReviewedFieldEvidence.ProviderSuggestion Suggestion,
    ReviewedFieldEvidence.FieldReview Review,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ExtractionTarget
)
{
    public sealed record ProviderSuggestion(
        string Provider,
        string RequestedModel,
        string? ReportedModel,
        string ContractVersion,
        string AnswerKey,
        string Status,
        string? Value,
        string? Quote,
        ExtractionEvidence[] Evidence
    );

    public sealed record FieldReview(
        string PreviousRunId,
        string ResultId,
        int Revision,
        string Actor,
        DateTimeOffset At,
        string? Category,
        string? SubmittedValue,
        ExtractionValue Before,
        ExtractionValue After
    );

    private sealed record ReviewEntry(
        ExtractionCorrection Correction,
        string Actor,
        DateTimeOffset At
    );

    // The caller authorizes the packet in this transaction. The dataset grants access only to
    // the reviewed page versions, not to every field or every page in that packet.
    public static ReviewedFieldEvidence[] Read(
        SqliteConnection db,
        SqliteTransaction tx,
        string packetId,
        IReadOnlyDictionary<string, string> reviewedPageTextHashes
    )
    {
        if (reviewedPageTextHashes.Count == 0 || !ExtractionStorage.Exists(db, tx))
            return [];
        using var query = db.CreateCommand();
        query.Transaction = tx;
        // Each explicit field edit saved its input packet revision before advancing it. Use the
        // document and text from that snapshot, even after a later review regroups its pages.
        query.CommandText = """
            SELECT entry.json, snapshot.json, prior.json, result.json
            FROM extraction_objects entry
            JOIN revisions snapshot ON snapshot.packet_id=$packet
              AND snapshot.revision=json_extract(entry.json,'$.correction.revision')
            JOIN extraction_objects prior ON prior.kind='run'
              AND prior.id=json_extract(entry.json,'$.previousRunId')
              AND json_extract(prior.json,'$.packetId')=$packet
            JOIN extraction_objects result ON result.kind='run'
              AND result.id=json_extract(entry.json,'$.resultId')
              AND json_extract(result.json,'$.packetId')=$packet
            WHERE entry.kind='correction' AND json_extract(entry.json,'$.packetId')=$packet
              AND json_type(entry.json,'$.correction')='object'
            ORDER BY snapshot.revision DESC, entry.rowid DESC
            """;
        query.Parameters.AddWithValue("$packet", packetId);
        var feedback = new List<ReviewedFieldEvidence>();
        var seen = new HashSet<(string AnswerKey, string Field)>();
        bool? synthetic = null;
        using var rows = query.ExecuteReader();
        while (rows.Read())
        {
            var entry = JsonSerializer.Deserialize<ReviewEntry>(rows.GetString(0), Contract.Json)!;
            var packet = JsonSerializer.Deserialize<Packet>(rows.GetString(1), Contract.Json)!;
            var prior = JsonSerializer.Deserialize<ExtractionRun>(
                rows.GetString(2),
                Contract.Json
            )!;
            var result = JsonSerializer.Deserialize<ExtractionRun>(
                rows.GetString(3),
                Contract.Json
            )!;
            var edit = entry.Correction;
            var policy = packet.EffectiveWorkflow.Capabilities?.Extraction;
            var document = packet.Documents.SingleOrDefault(d => d.Id == edit.DocumentId);
            var field = policy?.Definition?.Fields.SingleOrDefault(f => f.Name == edit.Field);
            if (
                packet.Id != packetId
                || packet.Mode != "live"
                || document == null
                || field?.Description == null
                || prior.Id != edit.RunId
                || prior.PacketRevision != packet.Revision
                || result.PacketRevision != packet.Revision + 1
                || prior.WorkflowSha256 != ExtractionStorage.Hash(packet.EffectiveWorkflow)
                || result.WorkflowSha256 != prior.WorkflowSha256
            )
                continue;
            var sha = policy!.Sha256;
            var before = FieldValue(prior, document.Id, sha, field.Name);
            var after = FieldValue(result, document.Id, sha, field.Name);
            if (before == null || after?.Status != "corrected")
                continue;
            synthetic ??= PagePredictionEvidence.Synthetic(db, tx, packet);
            if (synthetic == null)
                return [];

            var key = ExtractionAi.Key(sha, packet, document);
            // Mark newer edits even when their spans fall outside this dataset. An older edit
            // cannot become a target just because the later edit or omission isn't exportable.
            var latest = seen.Add((key, field.Name));
            var answer = ExtractionAi.Cached(db, tx, packetId, key);
            if (answer?.Fields == null || !answer.Fields.TryGetValue(field.Name, out var proposed))
                continue;
            var pages = packet.Pages.Where(p => document.PageIds.Contains(p.Id)).ToArray();
            var original = ExtractionAi.Ground(field, pages, proposed);
            var recorded = OriginalField(db, tx, prior, edit, sha, key);
            if (
                recorded?.Evidence == null
                || recorded.Status != original.Status
                || recorded.Value != original.Value
                || !recorded.Evidence.SequenceEqual(original.Evidence)
                || before.Evidence == null
                || after.Evidence == null
            )
                continue;
            var spans = original.Evidence.Concat(before.Evidence).Concat(after.Evidence).Distinct();
            if (
                !spans.Any()
                || spans.Any(e =>
                    e?.PageId == null
                    || !reviewedPageTextHashes.TryGetValue(e.PageId, out var hash)
                    || e.TextSha256 != hash
                    || !document.PageIds.Contains(e.PageId)
                    || !ExtractionPlatform.ValidEvidence(packet, e)
                )
            )
                continue;
            // A reviewer-selected span verifies the edit, but cannot ground the provider's answer.
            bool grounded = original.Evidence.Length > 0;
            feedback.Add(
                new(
                    packetId,
                    synthetic.Value,
                    document.Id,
                    policy.DefinitionId,
                    policy.Version,
                    sha,
                    field.Name,
                    field.Kind,
                    new(
                        "openai",
                        answer.RequestedModel,
                        answer.Model,
                        answer.ContractVersion,
                        answer.Key,
                        original.Status,
                        grounded ? proposed.Value : null,
                        grounded ? proposed.Quote : null,
                        original.Evidence
                    ),
                    new(
                        prior.Id,
                        result.Id,
                        result.PacketRevision,
                        entry.Actor,
                        entry.At,
                        edit.Category,
                        edit.Value,
                        before,
                        after
                    ),
                    latest && grounded && edit.Category is ("wrong-selection" or "transcription")
                        ? after.Value
                        : null
                )
            );
        }
        return feedback.ToArray();
    }

    // A replacement cache entry can match the exact input (and even give the same answer).
    // It is only evidence for this review if it already existed when the original run was
    // written. A correction inherits every other field; a partial carry inherits only the
    // listed documents. Older carry records applied to all documents in the carried run.
    private static ExtractionValue? OriginalField(
        SqliteConnection db,
        SqliteTransaction tx,
        ExtractionRun prior,
        ExtractionCorrection edit,
        string definitionSha256,
        string answerKey
    )
    {
        using var q = db.CreateCommand();
        q.Transaction = tx;
        q.CommandText = """
            WITH RECURSIVE lineage(id) AS (
                VALUES($prior)
                UNION
                SELECT json_extract(edge.json,'$.previousRunId')
                FROM lineage JOIN extraction_objects edge ON edge.kind='correction' AND edge.id=lineage.id
                WHERE json_extract(edge.json,'$.packetId')=$packet
                  AND (json_type(edge.json,'$.documentIds') IS NULL
                       OR $document IN (SELECT value FROM json_each(edge.json,'$.documentIds')))
            ), origin AS (
                SELECT min(r.rowid) AS sequence FROM lineage
                LEFT JOIN extraction_objects r ON r.kind='run' AND r.id=lineage.id
                  AND json_extract(r.json,'$.packetId')=$packet
                HAVING count(*)=count(r.rowid)
            )
            SELECT original.json FROM origin JOIN extraction_objects original ON original.rowid=origin.sequence
            JOIN extraction_objects a
              ON a.kind='ai-answer' AND a.id=$answer AND a.rowid < origin.sequence
            """;
        q.Parameters.AddWithValue("$prior", prior.Id);
        q.Parameters.AddWithValue("$packet", prior.PacketId);
        q.Parameters.AddWithValue("$document", edit.DocumentId);
        q.Parameters.AddWithValue("$answer", answerKey);
        return q.ExecuteScalar() is string json
            ? FieldValue(
                JsonSerializer.Deserialize<ExtractionRun>(json, Contract.Json)!,
                edit.DocumentId,
                definitionSha256,
                edit.Field
            )
            : null;
    }

    private static ExtractionValue? FieldValue(
        ExtractionRun run,
        string documentId,
        string definitionSha256,
        string name
    ) =>
        run
            .Results.SingleOrDefault(r =>
                r.DocumentId == documentId && r.DefinitionSha256 == definitionSha256
            )
            ?.Fields.SingleOrDefault(f => f.Name == name);
}
