using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace paprika;

internal sealed record AppliedPagePrediction(
    Page Page,
    Page? Previous,
    string Provider,
    string? Model,
    string? RequestedModel,
    double? Confidence
);

public sealed record PagePrediction(
    string PacketId,
    string PageId,
    string SourceId,
    string SourceName,
    string SourceGroup,
    int PageNumber,
    string TextSha256,
    int? PredictionRevision,
    string? WorkflowHash,
    string? Provider,
    string? Model,
    string? RequestedModel,
    string Label,
    double? Confidence,
    bool Synthetic,
    string? ComparisonKey,
    string Provenance,
    string[] Labels
)
{
    // Attached on read after checking the digest. No second OCR text copy in the receipt.
    public string Text { get; init; } = "";
}

public static class PagePredictionEvidence
{
    private const string Prefix = "page-prediction-v1-";

    private static string TextHash(string text) =>
        ArtifactStorage.Hash(Encoding.UTF8.GetBytes(text));

    private static string? SourceHash(Source source) =>
        source.Sha256 is { Length: 64 } hash && hash.All(char.IsAsciiHexDigit)
            ? hash.ToLowerInvariant()
            : null;

    private static string SourceGroup(Packet packet, Source source) =>
        SourceHash(source) ?? $"packet:{packet.Id}:source:{source.Id}";

    // Reprocessing copies the original sources and may change the mode/test flag.
    // Parent packets are retained while a child exists, including on older installations.
    internal static bool? Synthetic(SqliteConnection c, SqliteTransaction tx, Packet packet)
    {
        if (packet.TestRun || packet.Mode == "demo")
            return true;
        var visited = new HashSet<string> { packet.Id };
        var parent = packet.ParentPacketId;
        while (parent is not null)
        {
            using var q = c.CreateCommand();
            q.Transaction = tx;
            q.CommandText = """
                SELECT json_extract(json,'$.mode'), json_extract(json,'$.testRun'),
                       json_extract(json,'$.parentPacketId') FROM packets WHERE id=$id
                """;
            q.Parameters.AddWithValue("$id", parent);
            using var row = q.ExecuteReader();
            if (!visited.Add(parent) || !row.Read() || row.IsDBNull(0))
                return null;
            if (row.GetString(0) == "demo" || !row.IsDBNull(1) && row.GetBoolean(1))
                return true;
            parent = row.IsDBNull(2) ? null : row.GetString(2);
        }
        return false;
    }

    internal static void Write(
        SqliteConnection c,
        SqliteTransaction tx,
        Packet packet,
        AppliedPagePrediction applied
    )
    {
        var page = applied.Page;
        var source = packet.Sources.Single(s => s.Id == page.SourceId);
        var workflow = packet.EffectiveWorkflow;
        if (Synthetic(c, tx, packet) is not bool synthetic)
            return;
        var textHash = TextHash(page.Text);
        var previousText = applied.Previous?.SourceId == source.Id ? applied.Previous.Text : "";
        // A workflow may change its provider without changing the classification task.
        // Match the exact input, released taxonomy, and instructions, not the file name or label.
        var comparison = SourceHash(source) is not { } hash
            ? null
            : ArtifactStorage.Hash(
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        hash,
                        page.PageNumber,
                        textHash,
                        previousTextHash = TextHash(previousText),
                        packet.PromptVersion,
                        workflow.ReleaseId,
                        workflow.ReleaseVersion,
                        workflow.LegacyRules,
                        workflow.Types,
                        workflow.Instructions,
                        synthetic,
                    },
                    Contract.Json
                )
            );
        var receipt = new PagePrediction(
            packet.Id,
            page.Id,
            source.Id,
            source.Name,
            SourceGroup(packet, source),
            page.PageNumber,
            textHash,
            packet.Revision,
            workflow.Hash,
            applied.Provider,
            applied.Model,
            applied.RequestedModel,
            page.PredictedType,
            applied.Confidence,
            synthetic,
            comparison,
            "Validated classifier response",
            AllowedLabels(workflow)
        );
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "INSERT INTO artifacts(packet_id,name,json) VALUES($id,$name,$json)";
        q.Parameters.AddWithValue("$id", packet.Id);
        q.Parameters.AddWithValue("$name", Prefix + page.Id);
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(receipt, Contract.Json));
        q.ExecuteNonQuery();
    }

    public static PagePrediction[] Read(SqliteConnection c, SqliteTransaction tx, Packet packet)
    {
        if (packet.Mode != "live")
            return [];
        var saved = new Dictionary<string, PagePrediction>(StringComparer.Ordinal);
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT json FROM artifacts WHERE packet_id=$id AND name GLOB $prefix";
            q.Parameters.AddWithValue("$id", packet.Id);
            q.Parameters.AddWithValue("$prefix", Prefix + "*");
            using var rows = q.ExecuteReader();
            while (rows.Read())
            {
                var receipt = JsonSerializer.Deserialize<PagePrediction>(
                    rows.GetString(0),
                    Contract.Json
                )!;
                saved.Add(receipt.PageId, receipt);
            }
        }
        if (packet.Pages.Any(p => !saved.ContainsKey(p.Id)))
            ReadLegacy(c, tx, packet, saved);
        var result = new List<PagePrediction>();
        foreach (var page in packet.Pages)
        {
            if (
                saved.TryGetValue(page.Id, out var receipt)
                && receipt.PacketId == packet.Id
                && receipt.SourceId == page.SourceId
                && receipt.PageNumber == page.PageNumber
                && packet.Sources.SingleOrDefault(s => s.Id == page.SourceId) is { } source
                && receipt.SourceGroup == SourceGroup(packet, source)
                && receipt.TextSha256 == TextHash(page.Text)
            )
                result.Add(receipt with { Text = page.Text });
        }
        return result.ToArray();
    }

    public static bool Independent(PagePrediction a, PagePrediction b) =>
        a.ComparisonKey is not null
        && a.ComparisonKey == b.ComparisonKey
        && a.PacketId != b.PacketId
        && !string.IsNullOrWhiteSpace(a.Provider)
        && !string.IsNullOrWhiteSpace(b.Provider)
        && (
            a.Provider != b.Provider
            || !string.IsNullOrWhiteSpace(a.Model)
                && !string.IsNullOrWhiteSpace(b.Model)
                && a.Model != b.Model
        );

    public static string Compare(
        PagePrediction prediction,
        IEnumerable<PagePrediction> accessibleEvidence
    )
    {
        bool agreement = false;
        foreach (var other in accessibleEvidence.Where(p => Independent(prediction, p)))
        {
            if (other.Label != prediction.Label)
                return "disagreement";
            agreement = true;
        }
        return agreement ? "agreement" : "single";
    }

    private static string[] AllowedLabels(WorkflowSnapshot workflow) =>
        workflow.Types.Select(t => t.Id).Where(id => id != "unknown").ToArray();

    private static void ReadLegacy(
        SqliteConnection c,
        SqliteTransaction tx,
        Packet packet,
        Dictionary<string, PagePrediction> saved
    )
    {
        // Only a completed immutable prediction, never a review revision or current Page,
        // can supply the legacy page membership, original text and classification taxonomy.
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT json FROM artifacts WHERE packet_id=$id AND name GLOB 'predicted-*' LIMIT 2";
        q.Parameters.AddWithValue("$id", packet.Id);
        Packet original;
        int? revision;
        using (var rows = q.ExecuteReader())
        {
            if (!rows.Read())
                return;
            using var json = JsonDocument.Parse(rows.GetString(0));
            if (rows.Read())
                return;
            original = json.RootElement.Deserialize<Packet>(Contract.Json)!;
            revision = json.RootElement.TryGetProperty("revision", out var rev)
                ? rev.GetInt32()
                : null;
        }
        if (
            original.Id != packet.Id
            || original.Mode != "live"
            || original.Status != "review"
            || original.Processing?.Stage != "complete"
            || original.Workflow is null && original.TaxonomyVersion != Contract.TaxonomyVersion
            || Synthetic(c, tx, original) is not bool synthetic
        )
            return;
        var workflow = original.EffectiveWorkflow;
        var overlay =
            workflow.Capabilities?.Training is not null
            || workflow.Capabilities?.Recognition?.OverridePrediction == true;
        foreach (var page in original.Pages.Where(p => !saved.ContainsKey(p.Id)))
        {
            if (original.Sources.SingleOrDefault(s => s.Id == page.SourceId) is not { } source)
                continue;
            var raw = LegacyResponse(c, tx, original, page);
            if (overlay && raw is null)
                continue;
            if (!overlay && raw is not null && raw.Label != page.PredictedType)
                continue;
            var label = raw?.Label ?? page.PredictedType;
            if (!workflow.Types.Any(t => t.Id == label))
                continue;
            saved[page.Id] = new(
                original.Id,
                page.Id,
                source.Id,
                source.Name,
                SourceGroup(original, source),
                page.PageNumber,
                TextHash(page.Text),
                revision,
                original.Workflow?.Hash,
                raw?.Provider,
                raw?.Model,
                raw?.RequestedModel,
                label,
                raw is null ? page.Confidence : raw.Confidence,
                synthetic,
                null, // Historical receipts did not bind the exact classifier input/context.
                raw is not null
                    ? "Retained classifier response"
                    : "Legacy prediction snapshot; classifier attribution unavailable",
                AllowedLabels(workflow)
            );
        }
    }

    private sealed record LegacyChoice(
        string Label,
        double? Confidence,
        string? Provider,
        string? Model,
        string? RequestedModel
    );

    private static LegacyChoice? LegacyResponse(
        SqliteConnection c,
        SqliteTransaction tx,
        Packet original,
        Page page
    )
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT json FROM artifacts WHERE packet_id=$id AND name GLOB $prefix LIMIT 2";
        q.Parameters.AddWithValue("$id", original.Id);
        // Page IDs are generated locally; do not interpret legacy data as a glob pattern.
        if (!Guid.TryParseExact(page.Id, "N", out _))
            return null;
        q.Parameters.AddWithValue("$prefix", $"classification-{page.Id}-*");
        using var rows = q.ExecuteReader();
        if (!rows.Read())
            return null;
        using var json = JsonDocument.Parse(rows.GetString(0));
        if (rows.Read())
            return null; // Earlier failed/retried attempts are not independent teachers.
        var root = json.RootElement;
        if (!Object(root, "response", out var response))
            return null;
        var workflowHash = Object(root, "workflow", out var workflow)
            ? String(workflow, "hash")
            : String(root, "workflowHash");
        if (workflowHash is not null && workflowHash != original.EffectiveWorkflow.Hash)
            return null;
        var provider = String(root, "provider");
        var api = String(root, "api");
        string? label;
        JsonElement choice;
        if (api == "paprika-classifier-v1" && provider == "custom")
        {
            choice = response;
            label = String(choice, "documentType");
        }
        else if (api == "openai-decisions" && provider is null or "openai")
        {
            provider = "openai";
            if (
                !response.TryGetProperty("answers", out var answers)
                || answers.ValueKind != JsonValueKind.Array
            )
                return null;
            var choices = answers
                .EnumerateArray()
                .Where(a => String(a, "name") == "documentType")
                .Take(2)
                .ToArray();
            if (choices.Length != 1 || String(choices[0], "type") != "choice")
                return null;
            choice = choices[0];
            label = String(choice, "choice");
        }
        else if (
            api is null
            && provider is null or "jev"
            && Object(response, "answers", out var jevAnswers)
            && Object(jevAnswers, "documentType", out choice)
        )
            label = String(choice, "choice");
        else
            return null;
        if (label is null || !original.EffectiveWorkflow.Types.Any(t => t.Id == label))
            return null;
        double? confidence = null;
        if (choice.TryGetProperty("confidence", out var score))
        {
            if (
                score.ValueKind != JsonValueKind.Number
                || !score.TryGetDouble(out var n)
                || !double.IsFinite(n)
                || n is < 0 or > 1
            )
                return null;
            confidence = n;
        }
        return new(
            label,
            confidence,
            provider,
            String(response, "model"),
            String(root, "requestedModel")
        );
    }

    private static bool Object(JsonElement element, string key, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(key, out value)
            && value.ValueKind == JsonValueKind.Object;
    }

    private static string? String(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
