using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace paprika;

public sealed record ExtractionField(
    string Name,
    string Kind = "text",
    string? Pattern = null,
    string? Label = null,
    bool Required = false,
    // Omitted when null so existing definition hashes stay unchanged.
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    )] string? Description = null
);

public sealed record ExtractionTable(
    string Name,
    string Header,
    string[] Columns,
    char Delimiter = '|'
);

public sealed record ExtractionDefinition(
    string Id,
    int Version,
    ExtractionField[] Fields,
    ExtractionTable[] Tables,
    string? DocumentType = null
);

public sealed record ExtractionPolicy(
    string DefinitionId,
    int Version,
    string Sha256,
    ExtractionDefinition? Definition = null
);

public sealed record ExtractionEvidence(
    string PageId,
    int Offset,
    int Length,
    string Text,
    string TextSha256
);

public sealed record ExtractionValue(
    string Name,
    string? Value,
    string Status,
    ExtractionEvidence[] Evidence
);

public sealed record ExtractionRow(int Row, ExtractionValue[] Cells);

public sealed record ExtractionTableResult(string Name, ExtractionRow[] Rows);

public sealed record ExtractionResult(
    string DocumentId,
    string DefinitionSha256,
    ExtractionValue[] Fields,
    ExtractionTableResult[] Tables
);

public sealed record ExtractionRun(
    string Id,
    string PacketId,
    int PacketRevision,
    DateTimeOffset CreatedAt,
    ExtractionDefinition Definition,
    ExtractionResult[] Results,
    string WorkflowSha256
);

public sealed record ExtractionRunRequest(int Revision, string DefinitionId, int Version);

public sealed record ExtractionCorrection(
    int Revision,
    string RunId,
    string DocumentId,
    string Field,
    string? Value,
    string Reason,
    ExtractionEvidence[] Evidence,
    string? Category = null
);

// Shared platform persistence uses jobs.sqlite and never accepts paths from callers.
public sealed record ExtractionDefinitionResponse(ExtractionDefinition Definition, string Sha256);

public sealed record ExtractionHistoryResponse(
    ExtractionRun[] Runs,
    JsonElement[] Corrections,
    string? CurrentConfiguredRunId = null
);

public sealed record ExtractionCorrectionResponse(PacketResponse Packet, ExtractionRun Run);

public static class ExtractionStorage
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        Store,
        object
    > Initialized = new();
    private static readonly object InitializationGate = new();

    public static bool Exists(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type='table' AND name='extraction_objects'";
        return command.ExecuteScalar() != null;
    }

    public static string Hash<T>(T value) =>
        Convert
            .ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Contract.Json)))
            .ToLowerInvariant();

    public static string TextHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static void Initialize(Store store)
    {
        lock (InitializationGate)
        {
            if (Initialized.TryGetValue(store, out _))
                return;
            using var c = store.Open();
            using var q = c.CreateCommand();
            q.CommandText =
                "CREATE TABLE IF NOT EXISTS extraction_objects(kind TEXT NOT NULL,id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(kind,id));";
            q.ExecuteNonQuery();
            Initialized.Add(store, new object());
        }
    }

    public static T? Get<T>(Store store, string kind, string id)
    {
        Initialize(store);
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT json FROM extraction_objects WHERE kind=$kind AND id=$id";
        q.Parameters.AddWithValue("$kind", kind);
        q.Parameters.AddWithValue("$id", id);
        return q.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<T>(json, Contract.Json)
            : default;
    }

    public static T? Get<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string kind,
        string id
    )
    {
        using var q = connection.CreateCommand();
        q.Transaction = transaction;
        q.CommandText = "SELECT json FROM extraction_objects WHERE kind=$kind AND id=$id";
        q.Parameters.AddWithValue("$kind", kind);
        q.Parameters.AddWithValue("$id", id);
        return q.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<T>(json, Contract.Json)
            : default;
    }

    public static T[] List<T>(Store store, string kind)
    {
        Initialize(store);
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT json FROM extraction_objects WHERE kind=$kind ORDER BY rowid";
        q.Parameters.AddWithValue("$kind", kind);
        return ReadList<T>(q);
    }

    internal static T[] ListForPacket<T>(
        SqliteConnection c,
        SqliteTransaction tx,
        string kind,
        string packetId
    )
    {
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT json FROM extraction_objects WHERE kind=$kind AND json_extract(json,'$.packetId')=$packet ORDER BY rowid";
        q.Parameters.AddWithValue("$kind", kind);
        q.Parameters.AddWithValue("$packet", packetId);
        return ReadList<T>(q);
    }

    private static T[] ReadList<T>(SqliteCommand q)
    {
        using var r = q.ExecuteReader();
        var values = new List<T>();
        while (r.Read())
            values.Add(JsonSerializer.Deserialize<T>(r.GetString(0), Contract.Json)!);
        return values.ToArray();
    }

    public static void Add<T>(Store store, string kind, string id, T value)
    {
        Initialize(store);
        using var c = store.Open();
        using var q = c.CreateCommand();
        Insert(q, kind, id, value);
    }

    internal static void Insert<T>(SqliteCommand q, string kind, string id, T value)
    {
        q.CommandText = "INSERT INTO extraction_objects(kind,id,json) VALUES($kind,$id,$json)";
        q.Parameters.Clear();
        q.Parameters.AddWithValue("$kind", kind);
        q.Parameters.AddWithValue("$id", id);
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, Contract.Json));
        q.ExecuteNonQuery();
    }
}

public static class ExtractionPlatform
{
    public static string? ValidateDefinition(ExtractionDefinition d)
    {
        if (
            !Regex.IsMatch(d.Id ?? "", "^[a-zA-Z0-9_-]{1,80}$")
            || d.Version < 1
            || d.Fields is null
            || d.Tables is null
            || d.Fields.Length > 100
            || d.Tables.Length > 20
            || d.Fields.Any(f => f is null)
            || d.Tables.Any(t => t is null)
        )
            return "Invalid definition identity or size.";
        if (
            d.Fields.Select(f => f.Name).Distinct().Count() != d.Fields.Length
            || d.Fields.Any(f =>
                string.IsNullOrWhiteSpace(f.Name)
                || f.Name.Length > 100
                || f.Kind is not ("text" or "number" or "date" or "boolean")
                || new[] { f.Pattern, f.Label, f.Description }.Count(x => x != null) != 1
                || f.Label?.Length > 100
                || f.Pattern?.Length > 1000
                || f.Description?.Length > 500
                || (f.Label != null && string.IsNullOrWhiteSpace(f.Label))
                || (f.Pattern != null && string.IsNullOrWhiteSpace(f.Pattern))
                || (f.Description != null && string.IsNullOrWhiteSpace(f.Description))
            )
        )
            return "Fields need unique names, a supported kind, and one bounded pattern, label or AI description.";
        try
        {
            foreach (var f in d.Fields.Where(f => f.Pattern != null))
                _ = new Regex(
                    f.Pattern!,
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100)
                );
        }
        catch (ArgumentException)
        {
            return "Invalid regular expression.";
        }
        if (
            d.Tables.Select(t => t.Name).Distinct().Count() != d.Tables.Length
            || d.Tables.Any(t =>
                string.IsNullOrWhiteSpace(t.Name)
                || string.IsNullOrWhiteSpace(t.Header)
                || t.Header.Length > 500
                || t.Columns is not { Length: > 0 and <= 50 }
                || t.Columns.Distinct().Count() != t.Columns.Length
                || t.Columns.Any(c => string.IsNullOrWhiteSpace(c) || c.Length > 100)
                || t.Delimiter is not ('|' or ',' or '\t' or ';')
            )
        )
            return "Invalid delimited table definition.";
        return null;
    }

    public static ExtractionPolicy Freeze(Store store, ExtractionPolicy p)
    {
        var d = ExtractionStorage.Get<ExtractionDefinition>(
            store,
            "definition",
            $"{p.DefinitionId}:{p.Version}"
        ) ?? throw new WorkflowException(400, "Extraction definition version not found.");
        if (ValidateDefinition(d) is string error)
            throw new WorkflowException(400, error);
        if (
            ExtractionStorage.Hash(d) != p.Sha256
            || (p.Definition != null && ExtractionStorage.Hash(p.Definition) != p.Sha256)
        )
            throw new WorkflowException(400, "Extraction definition hash mismatch.");
        return p with { Definition = d };
    }

    public static ExtractionPolicy Freeze(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExtractionPolicy p
    )
    {
        if (p.Definition != null)
        {
            if (Validate(p) is string error)
                throw new WorkflowException(400, error);
            return p with
            {
                Definition = JsonSerializer.Deserialize<ExtractionDefinition>(
                    JsonSerializer.Serialize(p.Definition, Contract.Json),
                    Contract.Json
                )!,
            };
        }
        using var q = connection.CreateCommand();
        q.Transaction = transaction;
        q.CommandText = "SELECT json FROM extraction_objects WHERE kind='definition' AND id=$id";
        q.Parameters.AddWithValue("$id", $"{p.DefinitionId}:{p.Version}");
        ExtractionDefinition? definition;
        try
        {
            definition = q.ExecuteScalar() is string json
                ? JsonSerializer.Deserialize<ExtractionDefinition>(json, Contract.Json)
                : null;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 1)
        {
            throw new WorkflowException(400, "Extraction definition version not found.");
        }
        var frozen = p with { Definition = definition };
        if (Validate(frozen) is string problem)
            throw new WorkflowException(400, problem);
        return frozen;
    }

    public static string? Validate(ExtractionPolicy p) =>
        p.Definition is null
        || ValidateDefinition(p.Definition) != null
        || p.Definition.Id != p.DefinitionId
        || p.Definition.Version != p.Version
        || ExtractionStorage.Hash(p.Definition) != p.Sha256
            ? "Extraction policy must contain its exact immutable definition."
            : null;

    public static string? Normalize(string value, string kind) =>
        kind switch
        {
            "number" => decimal.TryParse(
                value.Trim(),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var n
            )
                ? n.ToString(CultureInfo.InvariantCulture)
                : null,
            "date" => DateOnly.TryParseExact(
                value.Trim(),
                ["yyyy-MM-dd", "MM/dd/yyyy"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var d
            )
                ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null,
            "boolean" => value.Trim().ToLowerInvariant() switch
            {
                "yes" or "true" => "true",
                "no" or "false" => "false",
                _ => null,
            },
            _ => value.Trim(),
        };

    public static ExtractionEvidence Evidence(Page p, int offset, int length) =>
        new(
            p.Id,
            offset,
            length,
            p.Text.Substring(offset, length),
            ExtractionStorage.TextHash(p.Text)
        );

    public static bool ValidEvidence(Packet p, ExtractionEvidence e) =>
        p.Pages.FirstOrDefault(x => x.Id == e.PageId) is Page page
        && e.Offset >= 0
        && e.Length >= 0
        && e.Offset <= page.Text.Length - e.Length
        && page.Text.Substring(e.Offset, e.Length) == e.Text
        && ExtractionStorage.TextHash(page.Text) == e.TextSha256;

    public static ExtractionRun Extract(
        Packet packet,
        ExtractionDefinition definition,
        IReadOnlyDictionary<string, ExtractionAiAnswer>? ai = null
    )
    {
        if (ValidateDefinition(definition) is string error)
            throw new ArgumentException(error);
        var results = new List<ExtractionResult>();
        foreach (
            var doc in packet.Documents.Where(d =>
                definition.DocumentType == null || d.Type == definition.DocumentType
            )
        )
        {
            var pages = packet.Pages.Where(p => doc.PageIds.Contains(p.Id)).ToArray();
            var fields = definition
                .Fields.Select(f =>
                {
                    if (f.Description != null)
                        return ExtractionAi.Ground(
                            f,
                            pages,
                            (
                                ai?.GetValueOrDefault(doc.Id)
                                ?? throw new ArgumentException(
                                    "AI field extraction has not run for this document."
                                )
                            ).Fields.GetValueOrDefault(f.Name)
                        );
                    var evidence = new List<ExtractionEvidence>();
                    var pattern = f.Pattern
                        ?? Regex.Escape(f.Label!) + @"\s*[:=]\s*(?<value>[^\r\n]+)";
                    var regex = new Regex(
                        pattern,
                        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                        TimeSpan.FromMilliseconds(100)
                    );
                    foreach (var page in pages)
                    {
                        if (page.Text.Length > 2_000_000)
                            throw new ArgumentException("Page text exceeds extraction limit.");
                        foreach (Match m in regex.Matches(page.Text))
                        {
                            var g = m.Groups["value"].Success ? m.Groups["value"] : m.Groups[0];
                            evidence.Add(Evidence(page, g.Index, g.Length));
                            if (evidence.Count > 1000)
                                throw new ArgumentException("Too many field candidates.");
                        }
                    }
                    var values = evidence
                        .Select(e => Normalize(e.Text, f.Kind))
                        .Distinct()
                        .ToArray();
                    return new ExtractionValue(
                        f.Name,
                        values.Length == 1 ? values[0] : null,
                        values.Length == 0 ? "missing"
                            : values.Length > 1 ? "ambiguous"
                            : values[0] is null ? "invalid"
                            : "extracted",
                        evidence.ToArray()
                    );
                })
                .ToArray();
            var tables = definition
                .Tables.Select(t =>
                {
                    var rows = new List<ExtractionRow>();
                    foreach (var page in pages)
                    {
                        bool active = false;
                        int offset = 0;
                        foreach (var raw in page.Text.Split('\n'))
                        {
                            var line = raw.TrimEnd('\r');
                            if (line.Trim() == t.Header.Trim())
                                active = true;
                            else if (active)
                            {
                                var cells = Delimited(line, t.Delimiter);
                                if (
                                    cells.Count != t.Columns.Length
                                    || string.IsNullOrWhiteSpace(line)
                                )
                                    active = false;
                                else
                                    rows.Add(
                                        new(
                                            rows.Count + 1,
                                            cells
                                                .Select(
                                                    (cell, i) =>
                                                        new ExtractionValue(
                                                            t.Columns[i],
                                                            cell.Value.Trim(),
                                                            "extracted",
                                                            [
                                                                Evidence(
                                                                    page,
                                                                    offset + cell.Offset,
                                                                    cell.Length
                                                                ),
                                                            ]
                                                        )
                                                )
                                                .ToArray()
                                        )
                                    );
                            }
                            offset += raw.Length + 1;
                        }
                    }
                    return new ExtractionTableResult(t.Name, rows.ToArray());
                })
                .ToArray();
            results.Add(new(doc.Id, ExtractionStorage.Hash(definition), fields, tables));
        }
        return new(
            Guid.NewGuid().ToString("N"),
            packet.Id,
            packet.Revision,
            DateTimeOffset.UtcNow,
            definition,
            results.ToArray(),
            ExtractionStorage.Hash(packet.EffectiveWorkflow)
        );
    }

    // RFC-style quoted cells, doubled quotes and delimiter escaping; spans refer to the original OCR text.
    public static List<(string Value, int Offset, int Length)> Delimited(
        string line,
        char delimiter
    )
    {
        var result = new List<(string, int, int)>();
        int start = 0;
        bool quoted = false;
        var value = new StringBuilder();
        for (int i = 0; i <= line.Length; i++)
        {
            if (i == line.Length || (line[i] == delimiter && !quoted))
            {
                result.Add((value.ToString(), start, i - start));
                value.Clear();
                start = i + 1;
                continue;
            }
            if (line[i] == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    value.Append('"');
                    i++;
                }
                else
                    quoted = !quoted;
            }
            else
                value.Append(line[i]);
        }
        return quoted ? [] : result;
    }

    public static void RunConfigured(Store store, Packet packet)
    {
        if (packet.EffectiveWorkflow.Capabilities?.Extraction == null)
            return;
        ExtractionStorage.Initialize(store);
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        RunConfigured(c, tx, packet);
        tx.Commit();
    }

    public static ExtractionRun? RunConfigured(
        SqliteConnection c,
        SqliteTransaction tx,
        Packet packet
    )
    {
        var policy = packet.EffectiveWorkflow.Capabilities?.Extraction;
        if (policy == null)
            return null;
        if (Validate(policy) is string error)
            throw new WorkflowException(409, error);
        if (!ExtractionStorage.Exists(c, tx))
        {
            using var create = c.CreateCommand();
            create.Transaction = tx;
            create.CommandText =
                "CREATE TABLE extraction_objects(kind TEXT NOT NULL,id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(kind,id))";
            create.ExecuteNonQuery();
        }
        var current = Current(c, tx, packet, policy.Sha256);
        var documents = MatchingDocuments(packet, policy.Definition!).ToArray();
        var reusable = (current?.Results ?? [])
            .Where(r =>
                documents.Any(d => d.Id == r.DocumentId)
                && CompleteShape(r, policy.Definition!, policy.Sha256)
            )
            .GroupBy(r => r.DocumentId)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single());
        ExtractionRun run;
        if (
            current != null
            && reusable.Count == documents.Length
            && current.Results.Length == documents.Length
        )
            run = current;
        else
        {
            // Extract only missing/invalidated documents. Never overwrite a carried human correction.
            var missing = documents.Where(d => !reusable.ContainsKey(d.Id)).ToList();
            var answers = ExtractionAi.Load(c, tx, packet, policy.Definition!);
            // Documents still waiting for an AI answer stay uncovered, which blocks approval until
            // the async pipeline or a manual extraction run asks the model.
            if (ExtractionAi.Uses(policy.Definition!))
                missing = missing.Where(d => answers.ContainsKey(d.Id)).ToList();
            var subset = new Packet
            {
                Id = packet.Id,
                Revision = packet.Revision,
                Workflow = packet.EffectiveWorkflow,
                Documents = missing,
                Pages = packet.Pages,
            };
            var extracted = Extract(subset, policy.Definition!, answers);
            run = extracted with
            {
                Results = documents
                    .Select(d =>
                        reusable.GetValueOrDefault(d.Id)
                        ?? extracted.Results.SingleOrDefault(r => r.DocumentId == d.Id)
                    )
                    .OfType<ExtractionResult>()
                    .ToArray(),
            };
            using var q = c.CreateCommand();
            q.Transaction = tx;
            ExtractionStorage.Insert(q, "run", run.Id, run);
            if (current != null && reusable.Count > 0)
                RecordCarry(
                    q,
                    current,
                    run,
                    reusable.Keys.ToArray(),
                    "system",
                    "unchanged document extraction reused in configured run"
                );
        }
        foreach (var reason in Blockers(packet, run))
        foreach (var document in documents)
            if (!document.ReviewReasons.Contains(reason))
                document.ReviewReasons.Add(reason);
        return run;
    }

    private static IEnumerable<Document> MatchingDocuments(
        Packet packet,
        ExtractionDefinition definition
    ) =>
        packet.Documents.Where(d =>
            definition.DocumentType == null || definition.DocumentType == d.Type
        );

    internal static bool CompleteShape(
        ExtractionResult result,
        ExtractionDefinition definition,
        string sha
    ) =>
        result.DefinitionSha256 == sha
        && result.Fields.Length == definition.Fields.Length
        && definition.Fields.All(f => result.Fields.Count(v => v.Name == f.Name) == 1);

    public static string[] Blockers(Store store, Packet packet)
    {
        var policy = packet.EffectiveWorkflow.Capabilities?.Extraction;
        return policy == null ? [] : Blockers(packet, Current(store, packet, policy.Sha256));
    }

    public static string[] Blockers(SqliteConnection c, SqliteTransaction tx, Packet packet)
    {
        var policy = packet.EffectiveWorkflow.Capabilities?.Extraction;
        return policy == null ? [] : Blockers(packet, Current(c, tx, packet, policy.Sha256));
    }

    public static string[] Blockers(Packet packet, ExtractionRun? run)
    {
        var policy = packet.EffectiveWorkflow.Capabilities?.Extraction;
        if (policy == null)
            return [];
        if (Validate(policy) is string invalid)
            return [invalid];
        var definition = policy.Definition!;
        var documents = MatchingDocuments(packet, definition).ToArray();
        if (documents.Length == 0)
            return [];
        if (
            run == null
            || run.PacketId != packet.Id
            || run.PacketRevision != packet.Revision
            || run.WorkflowSha256 != ExtractionStorage.Hash(packet.EffectiveWorkflow)
            || ExtractionStorage.Hash(run.Definition) != policy.Sha256
        )
            return
            [
                "Configured extraction has not completed for this exact revision and definition.",
            ];
        var blockers = new List<string>();
        if (run.Results.Any(r => !documents.Any(d => d.Id == r.DocumentId)))
            blockers.Add(
                "Configured extraction contains results outside the current document coverage."
            );
        foreach (var document in documents)
        {
            var results = run.Results.Where(r => r.DocumentId == document.Id).ToArray();
            if (results.Length != 1 || results[0].DefinitionSha256 != policy.Sha256)
            {
                blockers.Add(
                    $"Configured extraction is missing exact coverage for document {document.Id}."
                );
                continue;
            }
            var result = results[0];
            foreach (var field in definition.Fields)
            {
                var values = result.Fields.Where(v => v.Name == field.Name).ToArray();
                if (values.Length != 1)
                {
                    blockers.Add(
                        $"Extraction field {field.Name} has not been checked for document {document.Id}."
                    );
                    continue;
                }
                var value = values[0];
                bool success =
                    value.Status is "extracted" or "corrected"
                    && !string.IsNullOrWhiteSpace(value.Value)
                    && Normalize(value.Value, field.Kind) != null;
                if (
                    (!success && field.Required)
                    || value.Status is "ambiguous" or "invalid"
                    || value.Status is not ("extracted" or "corrected" or "missing")
                    || value.Evidence.Any(e =>
                        !document.PageIds.Contains(e.PageId) || !ValidEvidence(packet, e)
                    )
                )
                    blockers.Add(
                        $"Extraction field {field.Name} requires review for document {document.Id}."
                    );
            }
        }
        return blockers.Distinct().ToArray();
    }

    public static void EnsureComplete(SqliteConnection c, SqliteTransaction tx, Packet packet)
    {
        var blockers = Blockers(c, tx, packet);
        if (blockers.Length > 0)
            throw new WorkflowException(409, blockers[0]);
    }

    public static void EnsureConfigured(SqliteConnection c, SqliteTransaction tx, Packet packet)
    {
        RunConfigured(c, tx, packet);
        EnsureComplete(c, tx, packet);
    }

    public static ExtractionRun? Current(
        Store store,
        Packet packet,
        string? definitionSha256 = null
    )
    {
        ExtractionStorage.Initialize(store);
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        return Current(c, tx, packet, definitionSha256);
    }

    public static ExtractionRun? Current(
        SqliteConnection c,
        SqliteTransaction tx,
        Packet packet,
        string? definitionSha256 = null
    )
    {
        if (!ExtractionStorage.Exists(c, tx))
            return null;
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT json FROM extraction_objects WHERE kind='run' AND json_extract(json,'$.packetId')=$packet AND json_extract(json,'$.packetRevision')=$revision ORDER BY rowid DESC";
        q.Parameters.AddWithValue("$packet", packet.Id);
        q.Parameters.AddWithValue("$revision", packet.Revision);
        using var reader = q.ExecuteReader();
        var workflow = ExtractionStorage.Hash(packet.EffectiveWorkflow);
        while (reader.Read())
        {
            var run = JsonSerializer.Deserialize<ExtractionRun>(
                reader.GetString(0),
                Contract.Json
            )!;
            if (
                run.WorkflowSha256 == workflow
                && (
                    definitionSha256 == null
                    || ExtractionStorage.Hash(run.Definition) == definitionSha256
                )
            )
                return run;
        }
        return null;
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
            ExtractionStorage.Hash(before.EffectiveWorkflow)
            != ExtractionStorage.Hash(after.EffectiveWorkflow)
        )
            return;
        CarryUnchangedRuns(c, tx, before, after.Documents, after.Revision, actor);
    }

    private static void CarryUnchangedRuns(
        SqliteConnection c,
        SqliteTransaction tx,
        Packet before,
        List<Document> nextDocuments,
        int nextRevision,
        string actor,
        string? excludedDefinition = null
    )
    {
        if (!ExtractionStorage.Exists(c, tx))
            return;
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "SELECT json FROM extraction_objects WHERE kind='run' AND json_extract(json,'$.packetId')=$packet AND json_extract(json,'$.packetRevision')=$revision ORDER BY rowid DESC";
        q.Parameters.AddWithValue("$packet", before.Id);
        q.Parameters.AddWithValue("$revision", before.Revision);
        var runs = new List<ExtractionRun>();
        using (var reader = q.ExecuteReader())
            while (reader.Read())
                runs.Add(
                    JsonSerializer.Deserialize<ExtractionRun>(reader.GetString(0), Contract.Json)!
                );
        var workflow = ExtractionStorage.Hash(before.EffectiveWorkflow);
        foreach (
            var group in runs.Where(r => r.WorkflowSha256 == workflow)
                .GroupBy(r => ExtractionStorage.Hash(r.Definition))
        )
        {
            if (group.Key == excludedDefinition)
                continue;
            var run = group.First();
            var unchanged = run
                .Results.Where(r =>
                    before.Documents.FirstOrDefault(d => d.Id == r.DocumentId) is Document old
                    && nextDocuments.FirstOrDefault(d => d.Id == r.DocumentId) is Document next
                    && old.Type == next.Type
                    && old.PageIds.SequenceEqual(next.PageIds)
                )
                .ToArray();
            if (unchanged.Length == 0)
                continue;
            var carried = run with
            {
                Id = Guid.NewGuid().ToString("N"),
                PacketRevision = nextRevision,
                CreatedAt = DateTimeOffset.UtcNow,
                Results = unchanged,
            };
            ExtractionStorage.Insert(q, "run", carried.Id, carried);
            RecordCarry(
                q,
                run,
                carried,
                unchanged.Select(r => r.DocumentId).ToArray(),
                actor,
                "unchanged document extraction carried to reviewed revision"
            );
        }
    }

    private static void RecordCarry(
        SqliteCommand q,
        ExtractionRun previous,
        ExtractionRun result,
        string[] documentIds,
        string actor,
        string action
    ) =>
        ExtractionStorage.Insert(
            q,
            "correction",
            result.Id,
            new
            {
                packetId = result.PacketId,
                previousRunId = previous.Id,
                resultId = result.Id,
                documentIds,
                actor,
                at = result.CreatedAt,
                action,
            }
        );

    private static string? ValidateFeedback(
        ExtractionCorrection correction,
        ExtractionValue before,
        string kind,
        string? normalized
    )
    {
        switch (correction.Category)
        {
            case null:
                return null;
            case "wrong-selection":
                if (string.IsNullOrWhiteSpace(normalized))
                    return "Selecting a different source requires a replacement value.";
                if (
                    correction.Evidence is not [{ Length: > 0 } selected]
                    || correction.Evidence.SequenceEqual(before.Evidence)
                )
                    return "Select one changed, nonempty source span for this field.";
                var selectedValue = Normalize(selected.Text, kind);
                // Number normalization retains printed decimal scale; 1.50 and 1.5 are equal.
                if (
                    selectedValue == normalized
                    || (
                        kind == "number"
                        && selectedValue != null
                        && decimal.Parse(selectedValue, CultureInfo.InvariantCulture)
                            == decimal.Parse(normalized, CultureInfo.InvariantCulture)
                    )
                )
                    return null;
                return "The replacement value must match the selected source span.";
            case "transcription":
            case "omit":
                if (correction.Category == "transcription" && string.IsNullOrWhiteSpace(normalized))
                    return "A transcription correction requires a replacement value.";
                if (correction.Category == "omit" && correction.Value != null)
                    return "Omitting a field requires a null value.";
                if (
                    correction.Evidence.Length != 0
                    && !correction.Evidence.SequenceEqual(before.Evidence)
                )
                    return "This correction keeps the field's current source evidence.";
                return null;
            default:
                return "Unknown field correction category.";
        }
    }

    public static IResult Correct(
        Store store,
        string packetId,
        ExtractionCorrection correction,
        string actor = "local",
        OperationsPlatformStore? operations = null
    )
    {
        ExtractionStorage.Initialize(store);
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var packet = Store.Read(c, tx, packetId);
        if (
            packet is null
            || (operations != null && OperationsPlatformStore.IsArchived(c, tx, packetId))
        )
            return Results.NotFound();
        if (packet.Revision != correction.Revision)
            return Results.Conflict(new { error = "Revision changed. Reload the packet." });
        if (operations?.ValidateReview(c, tx, packet, actor) is string claimError)
            return Results.Conflict(new { error = claimError });
        if (packet.Status is not ("review" or "approved"))
            return Results.Conflict(new { error = "Packet is not ready for correction." });
        var run = string.IsNullOrEmpty(correction.RunId)
            ? null
            : ExtractionStorage.Get<ExtractionRun>(c, tx, "run", correction.RunId);
        var definitionHash = run == null ? null : ExtractionStorage.Hash(run.Definition);
        var document = packet.Documents.FirstOrDefault(d => d.Id == correction.DocumentId);
        var target = run?.Results.FirstOrDefault(r => r.DocumentId == correction.DocumentId);
        var before = target?.Fields.FirstOrDefault(f => f.Name == correction.Field);
        var field = run?.Definition.Fields.FirstOrDefault(f => f.Name == correction.Field);
        if (
            run is null
            || run.PacketId != packetId
            || run.PacketRevision != packet.Revision
            || document is null
            || target?.DefinitionSha256 != definitionHash
            || before is null
            || field is null
            || Current(c, tx, packet, definitionHash)?.Id != run.Id
        )
            return Results.Conflict(
                new { error = "Extraction result is stale or field is unknown." }
            );
        if (
            string.IsNullOrWhiteSpace(correction.Reason)
            || correction.Reason.Length > 1000
            || correction.Value?.Length > 10000
            || correction.Evidence is null
        )
            return Results.BadRequest(
                new { error = "Correction needs a reason and current evidence spans." }
            );
        var evidence = correction.Category is "transcription" or "omit"
            ? before.Evidence
            : correction.Evidence;
        if (
            evidence.Any(e =>
                e is null || !document.PageIds.Contains(e.PageId) || !ValidEvidence(packet, e)
            )
        )
            return Results.BadRequest(new { error = "Evidence must match the current document." });
        var normalized = correction.Value == null ? null : Normalize(correction.Value, field.Kind);
        if (correction.Value != null && normalized == null)
            return Results.BadRequest(new { error = "Value does not match field kind." });
        if (ValidateFeedback(correction, before, field.Kind, normalized) is string error)
            return Results.BadRequest(new { error });
        var after = new ExtractionValue(field.Name, normalized, "corrected", evidence);
        var result = run with
        {
            Id = Guid.NewGuid().ToString("N"),
            PacketRevision = packet.Revision + 1,
            CreatedAt = DateTimeOffset.UtcNow,
            Results = run
                .Results.Select(r =>
                    r.DocumentId != correction.DocumentId
                        ? r
                        : r with
                        {
                            Fields = r
                                .Fields.Select(f => f.Name == correction.Field ? after : f)
                                .ToArray(),
                        }
                )
                .ToArray(),
        };
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText =
            "INSERT OR IGNORE INTO revisions(packet_id,revision,json) VALUES($packet,$revision,$json)";
        q.Parameters.AddWithValue("$packet", packet.Id);
        q.Parameters.AddWithValue("$revision", packet.Revision);
        q.Parameters.AddWithValue("$json", JsonSerializer.Serialize(packet, Contract.Json));
        q.ExecuteNonQuery();
        // A correction advances the whole packet. Keep the effective results of other
        // definitions, including any configured human corrections on an ad-hoc run.
        CarryUnchangedRuns(
            c,
            tx,
            packet,
            packet.Documents,
            result.PacketRevision,
            actor,
            definitionHash
        );
        ExtractionStorage.Insert(
            q,
            "correction",
            result.Id,
            new
            {
                packetId,
                previousRunId = run.Id,
                correction,
                fieldKind = field.Kind,
                normalizedValue = normalized,
                before,
                after,
                resultId = result.Id,
                at = result.CreatedAt,
                actor,
            }
        );
        ExtractionStorage.Insert(q, "run", result.Id, result);
        packet.Revision++;
        packet.Status = "review";
        packet.Audit.Add(
            new(
                DateTimeOffset.UtcNow,
                "extracted field corrected; review required",
                packet.Revision,
                actor
            )
        );
        Store.Write(c, tx, packet);
        tx.Commit();
        return Results.Ok(new ExtractionCorrectionResponse(PacketResponse.From(packet), result));
    }
}

public static class ExtractionEndpoints
{
    public static void MapExtractionEndpoints(this WebApplication app)
    {
        ExtractionStorage.Initialize(app.Services.GetRequiredService<Store>());
        app.MapGet("/api/extraction/definitions", (Store s) =>
                ExtractionStorage
                    .List<ExtractionDefinition>(s, "definition")
                    .Select(d => new { definition = d, sha256 = ExtractionStorage.Hash(d) })
            )
            .WithName("ListExtractionDefinitions")
            .Produces<ExtractionDefinitionResponse[]>();
        app.MapPost("/api/extraction/definitions", (ExtractionDefinition d, Store s) =>
            {
                if (ExtractionPlatform.ValidateDefinition(d) is string error)
                    return Results.BadRequest(new { error });
                try
                {
                    ExtractionStorage.Add(s, "definition", $"{d.Id}:{d.Version}", d);
                    return Results.Ok(new { definition = d, sha256 = ExtractionStorage.Hash(d) });
                }
                catch (SqliteException e) when (e.SqliteErrorCode == 19)
                {
                    return Results.Conflict(new { error = "Definition versions are immutable." });
                }
            })
            .WithName("CreateExtractionDefinition")
            .Produces<ExtractionDefinitionResponse>()
            .Produces<ApiError>(400)
            .Produces<ApiError>(409);
        app.MapGet("/api/extraction/{packetId}", (string packetId, Store s) =>
            {
                using var connection = s.Open();
                using var transaction = connection.BeginTransaction(deferred: true);
                var packet = Store.Read(connection, transaction, packetId);
                if (
                    packet == null
                    || OperationsPlatformStore.IsArchived(connection, transaction, packetId)
                )
                    return Results.NotFound();
                var policy = packet.EffectiveWorkflow.Capabilities?.Extraction;
                var current =
                    policy == null
                        ? null
                        : ExtractionPlatform.Current(
                            connection,
                            transaction,
                            packet,
                            policy.Sha256
                        );
                return Results.Ok(
                    new ExtractionHistoryResponse(
                        ExtractionStorage.ListForPacket<ExtractionRun>(
                            connection,
                            transaction,
                            "run",
                            packetId
                        ),
                        ExtractionStorage.ListForPacket<JsonElement>(
                            connection,
                            transaction,
                            "correction",
                            packetId
                        ),
                        current?.Id
                    )
                );
            })
            .WithOperationsPacketAccess("packetId")
            .WithName("GetPacketExtraction")
            .Produces<ExtractionHistoryResponse>()
            .Produces(404);
        app.MapPost(
                "/api/extraction/{packetId}/run",
                async (
                    string packetId,
                    ExtractionRunRequest request,
                    Store s,
                    OperationsPlatformStore operations,
                    ExtractionAi ai,
                    HttpContext context,
                    CancellationToken ct
                ) =>
                {
                    ExtractionStorage.Initialize(s);
                    // Check both authorization and workflow opt-in before sending text off-server.
                    // Recheck mutable state after the network call, before adding extraction coverage.
                    Packet before;
                    ExtractionDefinition definition;
                    HashSet<string>? alreadyCovered = null;
                    var actor = OperationsAccess.Actor(context);
                    using (var read = s.Open())
                    using (var check = read.BeginTransaction())
                    {
                        if (Store.Read(read, check, packetId) is not { } current)
                            return Results.NotFound();
                        if (current.Revision != request.Revision)
                            return Results.Conflict();
                        if (
                            operations.ValidateMutation(read, check, current, actor) is string error
                        )
                            return Results.Conflict(new { error });
                        var selected = ExtractionStorage.Get<ExtractionDefinition>(
                            read,
                            check,
                            "definition",
                            $"{request.DefinitionId}:{request.Version}"
                        );
                        if (selected is null)
                            return Results.NotFound();
                        if (ExtractionPlatform.ValidateDefinition(selected) is string invalid)
                            return Results.BadRequest(new { error = invalid });
                        if (
                            ExtractionAi.Uses(selected)
                            && current.EffectiveWorkflow.Capabilities?.Extraction?.Sha256
                                != ExtractionStorage.Hash(selected)
                        )
                            return Results.Conflict(
                                new
                                {
                                    error = "Select this AI field definition in the packet's workflow before sending its text to OpenAI.",
                                }
                            );
                        before = current;
                        definition = selected;
                        var sha = ExtractionStorage.Hash(selected);
                        if (
                            ExtractionAi.Uses(selected)
                            && ExtractionPlatform.Current(read, check, current, sha) is { } run
                        )
                            alreadyCovered = run
                                .Results.Where(r =>
                                    ExtractionPlatform.CompleteShape(r, selected, sha)
                                )
                                .GroupBy(r => r.DocumentId)
                                .Where(group => group.Count() == 1)
                                .Select(group => group.Key)
                                .ToHashSet(StringComparer.Ordinal);
                    }
                    try
                    {
                        await ai.PrepareAsync(before, definition, ct, alreadyCovered);
                    }
                    catch (Exception e)
                        when (!ct.IsCancellationRequested
                            && e
                                is HttpRequestException
                                    or InvalidOperationException
                                    or IOException
                                    or TimeoutException
                                    or TaskCanceledException
                        )
                    {
                        return Results.Json(
                            new
                            {
                                error = "AI field extraction failed. Check the provider configuration and availability, then retry.",
                            },
                            statusCode: 502
                        );
                    }
                    using var connection = s.Open();
                    using var transaction = connection.BeginTransaction();
                    var p = Store.Read(connection, transaction, packetId);
                    if (p == null)
                        return Results.NotFound();
                    if (p.Revision != request.Revision)
                        return Results.Conflict();
                    if (
                        operations.ValidateMutation(connection, transaction, p, actor)
                        is string claimError
                    )
                        return Results.Conflict(new { error = claimError });
                    try
                    {
                        if (
                            p.EffectiveWorkflow.Capabilities?.Extraction?.Sha256
                            == ExtractionStorage.Hash(definition)
                        )
                        {
                            var configured = ExtractionPlatform.RunConfigured(
                                connection,
                                transaction,
                                p
                            );
                            transaction.Commit();
                            return Results.Ok(configured);
                        }
                        var run = ExtractionPlatform.Extract(p, definition);
                        using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        ExtractionStorage.Insert(command, "run", run.Id, run);
                        transaction.Commit();
                        return Results.Ok(run);
                    }
                    catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
                    {
                        return Results.BadRequest(new { error = e.Message });
                    }
                }
            )
            .WithOperationsPacketAccess("packetId", requireClaim: true)
            .WithName("RunPacketExtraction")
            .Produces<ExtractionRun>()
            .Produces<ApiError>(400)
            .Produces<ApiError>(502)
            .Produces(409)
            .Produces(404);
        app.MapPost(
                "/api/extraction/{packetId}/corrections",
                (
                    string packetId,
                    ExtractionCorrection r,
                    Store s,
                    HttpContext context,
                    OperationsPlatformStore operations
                ) =>
                    ExtractionPlatform.Correct(
                        s,
                        packetId,
                        r,
                        OperationsAccess.Identity(context).Name,
                        operations
                    )
            )
            .WithOperationsPacketAccess("packetId", requireClaim: true)
            .WithName("CorrectPacketExtraction")
            .Produces<ExtractionCorrectionResponse>()
            .Produces<ApiError>(400)
            .Produces<ApiError>(409)
            .Produces(404);
    }
}
