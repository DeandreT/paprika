using SkiaSharp;

namespace PaperworkClassifier;

public sealed record RecognitionTemplate(
    string Id,
    int Version,
    string Label,
    string SourceSha256,
    string FeatureAlgorithm,
    float[] Features,
    DateTimeOffset CreatedAt,
    int PageNumber = 1
);

public sealed record RecognitionTemplateRef(
    string Id,
    int Version,
    string Sha256,
    RecognitionTemplate? Template = null
);

public sealed record RecognitionPolicy(
    RecognitionTemplateRef[] Templates,
    double MinimumScore = .85,
    double MinimumMargin = .05,
    bool OverridePrediction = false
);

public sealed record RecognitionTrainingRequest(
    int Revision,
    string SourceId,
    string Id,
    int Version,
    string Label,
    string? DerivativeId = null,
    int PageNumber = 1
);

public sealed record RecognitionRequest(int Revision, RecognitionPolicy Policy);

public sealed record RecognitionResult(
    string Id,
    string PacketId,
    string PageId,
    string Label,
    double Score,
    double Margin,
    string SourceSha256,
    RecognitionTemplateRef[] Templates,
    string Algorithm,
    DateTimeOffset CreatedAt,
    int PacketRevision = 0,
    string? WorkflowSha256 = null,
    string? Renderer = null
);

public sealed record RecognitionTemplateResponse(RecognitionTemplate Template, string Sha256);

public static class RecognitionPlatform
{
    public const string Algorithm = "gray-grid-32-v1";

    // Actual visual intensity features, including whitespace and layout. Never uses OCR keywords.
    public static float[] Features(SKBitmap image)
    {
        var features = new float[32 * 32];
        for (int gy = 0; gy < 32; gy++)
        for (int gx = 0; gx < 32; gx++)
        {
            double sum = 0;
            int count = 0;
            for (
                int y = gy * image.Height / 32;
                y < Math.Max(gy * image.Height / 32 + 1, (gy + 1) * image.Height / 32);
                y++
            )
            for (
                int x = gx * image.Width / 32;
                x < Math.Max(gx * image.Width / 32 + 1, (gx + 1) * image.Width / 32);
                x++
            )
            {
                var c = image.GetPixel(Math.Min(x, image.Width - 1), Math.Min(y, image.Height - 1));
                sum +=
                    (1 - (.2126 * c.Red + .7152 * c.Green + .0722 * c.Blue) / 255) * c.Alpha / 255;
                count++;
            }
            features[gy * 32 + gx] = (float)(sum / count);
        }
        return features;
    }

    public static double Similarity(float[] a, float[] b)
    {
        double dot = 0,
            aa = 0,
            bb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            aa += a[i] * a[i];
            bb += b[i] * b[i];
        }
        return aa == 0 || bb == 0 ? 0 : Math.Clamp(dot / Math.Sqrt(aa * bb), 0, 1);
    }

    public static string? Validate(RecognitionPolicy p)
    {
        if (
            p.Templates is not { Length: > 0 and <= 100 }
            || !double.IsFinite(p.MinimumScore)
            || !double.IsFinite(p.MinimumMargin)
            || p.MinimumScore is < 0 or > 1
            || p.MinimumMargin is < 0 or > 1
        )
            return "Invalid recognition thresholds or templates.";
        foreach (var r in p.Templates)
        {
            if (r is null)
                return "Invalid template reference.";
            var t = r.Template;
            if (
                t == null
                || t.Id != r.Id
                || t.Version != r.Version
                || ExtractionStorage.Hash(t) != r.Sha256
                || t.FeatureAlgorithm != Algorithm
                || t.Features is not { Length: 1024 }
                || t.Features.Any(v => !float.IsFinite(v) || v < 0 || v > 1)
                || string.IsNullOrWhiteSpace(t.Label)
            )
                return "Template must contain exact immutable image features and hash.";
        }
        return null;
    }

    public static RecognitionPolicy Freeze(Store store, RecognitionPolicy p)
    {
        if (p.Templates == null)
            throw new WorkflowException(400, "Templates required.");
        var result = p with
        {
            Templates = p
                .Templates.Select(r =>
                {
                    var t = ExtractionStorage.Get<RecognitionTemplate>(
                        store,
                        "template",
                        $"{r.Id}:{r.Version}"
                    ) ?? throw new WorkflowException(400, "Template version not found.");
                    if (
                        ExtractionStorage.Hash(t) != r.Sha256
                        || (r.Template != null && ExtractionStorage.Hash(r.Template) != r.Sha256)
                    )
                        throw new WorkflowException(400, "Template hash mismatch.");
                    return r with { Template = t };
                })
                .ToArray(),
        };
        if (Validate(result) is string error)
            throw new WorkflowException(400, error);
        return result;
    }

    public static RecognitionPolicy Freeze(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction transaction,
        RecognitionPolicy p
    )
    {
        if (p.Templates == null)
            throw new WorkflowException(400, "Templates required.");
        var frozen = p with
        {
            Templates = p
                .Templates.Select(r =>
                {
                    if (r.Template != null)
                        return r with
                        {
                            Template =
                                System.Text.Json.JsonSerializer.Deserialize<RecognitionTemplate>(
                                    System.Text.Json.JsonSerializer.Serialize(
                                        r.Template,
                                        Contract.Json
                                    ),
                                    Contract.Json
                                )!,
                        };
                    using var q = connection.CreateCommand();
                    q.Transaction = transaction;
                    q.CommandText =
                        "SELECT json FROM extraction_objects WHERE kind='template' AND id=$id";
                    q.Parameters.AddWithValue("$id", $"{r.Id}:{r.Version}");
                    try
                    {
                        return r with
                        {
                            Template = q.ExecuteScalar() is string json
                                ? System.Text.Json.JsonSerializer.Deserialize<RecognitionTemplate>(
                                    json,
                                    Contract.Json
                                )
                                : null,
                        };
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteErrorCode == 1)
                    {
                        throw new WorkflowException(400, "Template version not found.");
                    }
                })
                .ToArray(),
        };
        if (Validate(frozen) is string error)
            throw new WorkflowException(400, error);
        return frozen;
    }

    public static (string Label, double Score, double Margin) Compare(
        float[] features,
        RecognitionPolicy p
    )
    {
        if (Validate(p) is string error)
            throw new ArgumentException(error);
        var scores = p
            .Templates.GroupBy(t => t.Template!.Label)
            .Select(g =>
                (Label: g.Key, Score: g.Max(t => Similarity(features, t.Template!.Features)))
            )
            .OrderByDescending(x => x.Score)
            .ToArray();
        var best = scores[0];
        double margin = best.Score - (scores.Length > 1 ? scores[1].Score : 0);
        return (
            best.Score >= p.MinimumScore && margin >= p.MinimumMargin ? best.Label : "unknown",
            best.Score,
            margin
        );
    }

    public static Task ApplyAsync(Store store, Packet packet, CancellationToken token) =>
        ApplyAsync(store, packet, packet.EffectiveWorkflow.Capabilities?.Recognition, token);

    public static Task ApplyAsync(
        Store store,
        Packet packet,
        RecognitionPolicy? policy,
        CancellationToken token,
        Microsoft.Data.Sqlite.SqliteConnection? connection = null,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction = null
    )
    {
        var preprocessing = packet.EffectiveWorkflow.Capabilities?.Preprocessing;
        if (policy == null && preprocessing == null)
            return Task.CompletedTask;
        var derivatives = new Dictionary<string, PreprocessingDerivative>();
        if (preprocessing != null)
            foreach (var page in packet.Pages)
            {
                token.ThrowIfCancellationRequested();
                var source = packet.Sources.Single(s => s.Id == page.SourceId);
                derivatives[page.Id] = PreprocessingPlatform.Create(
                    store,
                    packet,
                    source,
                    preprocessing,
                    connection,
                    transaction,
                    page.PageNumber
                );
            }
        if (policy == null)
            return Task.CompletedTask;
        foreach (var page in packet.Pages)
        {
            token.ThrowIfCancellationRequested();
            var source = packet.Sources.Single(s => s.Id == page.SourceId);
            var path = derivatives.TryGetValue(page.Id, out var derivative)
                ? PreprocessingPlatform.DerivativePath(store, derivative.Id)
                : store.SourcePath(packet, source);
            using var bitmap =
                derivative != null
                    ? PreprocessingPlatform.Decode(path)
                    : PreprocessingPdfRaster.Load(store, packet, source, page.PageNumber);
            var result = Compare(Features(bitmap), policy);
            if (!packet.EffectiveWorkflow.Types.Any(t => t.Id == result.Label))
                result.Label = "unknown";
            var provenance = new RecognitionResult(
                Guid.NewGuid().ToString("N"),
                packet.Id,
                page.Id,
                result.Label,
                result.Score,
                result.Margin,
                Pdfs.SourceHash(path),
                policy.Templates,
                Algorithm,
                DateTimeOffset.UtcNow,
                packet.Revision,
                ExtractionStorage.Hash(packet.EffectiveWorkflow),
                source.Extension == ".pdf" ? PreprocessingPdfRaster.Renderer : "SkiaSharp-3.119.1"
            );
            if (connection == null)
                ExtractionStorage.Add(store, "recognition", provenance.Id, provenance);
            else
            {
                using var q = connection.CreateCommand();
                q.Transaction = transaction;
                ExtractionStorage.Insert(q, "recognition", provenance.Id, provenance);
            }
            if (result.Label == "unknown")
                page.ReviewReasons.Add("Visual template score or margin is below threshold.");
            if (policy.OverridePrediction)
            {
                page.PredictedType = result.Label;
                page.Confidence = result.Score;
            }
        }
        return Task.CompletedTask;
    }
}

public static class RecognitionEndpoints
{
    public static void MapRecognitionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/recognition/templates", (Store s) =>
                ExtractionStorage
                    .List<RecognitionTemplate>(s, "template")
                    .Select(t => new { template = t, sha256 = ExtractionStorage.Hash(t) })
            )
            .WithName("ListRecognitionTemplates")
            .Produces<RecognitionTemplateResponse[]>();
        app.MapPost("/api/recognition/templates", (RecognitionTrainingRequest r, Store s) =>
            {
                var p = s.All()
                    .SingleOrDefault(p => p.Sources.Any(source => source.Id == r.SourceId));
                if (p == null)
                    return Results.NotFound();
                if (p.Revision != r.Revision)
                    return Results.Conflict();
                if (
                    r.Version < 1
                    || !System.Text.RegularExpressions.Regex.IsMatch(
                        r.Id ?? "",
                        "^[a-zA-Z0-9_-]{1,80}$"
                    )
                    || !p.EffectiveWorkflow.Types.Any(t => t.Id == r.Label)
                )
                    return Results.BadRequest(
                        new
                        {
                            error = "Valid template identity, version and workflow label required.",
                        }
                    );
                var source = p.Sources.Single(source => source.Id == r.SourceId);
                string path = s.SourcePath(p, source);
                if (r.DerivativeId != null)
                {
                    var d = ExtractionStorage.Get<PreprocessingDerivative>(
                        s,
                        "derivative",
                        r.DerivativeId
                    );
                    if (
                        d?.PacketId != p.Id
                        || d.SourceId != source.Id
                        || d.PageNumber != r.PageNumber
                    )
                        return Results.BadRequest();
                    path = PreprocessingPlatform.DerivativePath(s, d.Id);
                }
                try
                {
                    using var bitmap =
                        r.DerivativeId == null
                            ? PreprocessingPdfRaster.Load(s, p, source, r.PageNumber)
                            : PreprocessingPlatform.Decode(path);
                    var template = new RecognitionTemplate(
                        r.Id!,
                        r.Version,
                        r.Label,
                        Pdfs.SourceHash(path),
                        RecognitionPlatform.Algorithm,
                        RecognitionPlatform.Features(bitmap),
                        DateTimeOffset.UtcNow,
                        r.PageNumber
                    );
                    ExtractionStorage.Add(s, "template", $"{r.Id}:{r.Version}", template);
                    return Results.Ok(new { template, sha256 = ExtractionStorage.Hash(template) });
                }
                catch (Microsoft.Data.Sqlite.SqliteException e) when (e.SqliteErrorCode == 19)
                {
                    return Results.Conflict(new { error = "Template versions are immutable." });
                }
                catch (ArgumentException e)
                {
                    return Results.BadRequest(new { error = e.Message });
                }
            })
            .WithName("CreateRecognitionTemplate")
            .Produces<RecognitionTemplateResponse>()
            .Produces<ApiError>(400)
            .Produces<ApiError>(409)
            .Produces(404);
        app.MapGet("/api/recognition/{packetId}", (string packetId, Store s) =>
                ExtractionStorage
                    .List<RecognitionResult>(s, "recognition")
                    .Where(r => r.PacketId == packetId)
            )
            .WithOperationsPacketAccess("packetId")
            .WithName("GetPacketRecognition")
            .Produces<RecognitionResult[]>();
        app.MapPost(
                "/api/recognition/{packetId}/run",
                async (
                    string packetId,
                    RecognitionRequest r,
                    Store s,
                    CancellationToken ct,
                    OperationsPlatformStore operations,
                    HttpContext context
                ) =>
                {
                    ExtractionStorage.Initialize(s);
                    using var connection = s.Open();
                    using var transaction = connection.BeginTransaction();
                    var p = Store.Read(connection, transaction, packetId);
                    if (p == null)
                        return Results.NotFound();
                    if (p.Revision != r.Revision)
                        return Results.Conflict();
                    if (
                        operations.ValidateMutation(
                            connection,
                            transaction,
                            p,
                            OperationsAccess.Actor(context)
                        )
                        is string claimError
                    )
                        return Results.Conflict(new { error = claimError });
                    try
                    {
                        var policy = RecognitionPlatform.Freeze(
                            connection,
                            transaction,
                            r.Policy
                        ) with
                        {
                            OverridePrediction = false,
                        };
                        await RecognitionPlatform.ApplyAsync(
                            s,
                            p,
                            policy,
                            ct,
                            connection,
                            transaction
                        );
                        transaction.Commit();
                        return Results.Ok(
                            ExtractionStorage
                                .List<RecognitionResult>(s, "recognition")
                                .Where(x => x.PacketId == packetId)
                        );
                    }
                    catch (WorkflowException e)
                    {
                        return Results.BadRequest(new { error = e.Message });
                    }
                }
            )
            .WithOperationsPacketAccess("packetId", requireClaim: true)
            .WithName("RunPacketRecognition")
            .Produces<RecognitionResult[]>()
            .Produces<ApiError>(400)
            .Produces(409)
            .Produces(404);
    }
}
