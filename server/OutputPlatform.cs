using System.Globalization;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace paprika;

public sealed record OutputPolicy(string Format = "text-json", string[]? RedactValues = null);

public sealed record OutputRegion(string PageId, int X, int Y, int Width, int Height);

public sealed record OutputRequest(
    int Revision,
    string Format,
    string[]? RedactValues = null,
    OutputRegion[]? Regions = null
);

public sealed record OutputArtifact(
    string Id,
    string PacketId,
    int Revision,
    string Format,
    string Sha256,
    string MediaType,
    string Extension,
    string[] SourceHashes,
    DateTimeOffset CreatedAt,
    bool Preview = false,
    string? RequestSha256 = null,
    string? WorkflowSha256 = null
);

public sealed record OutputTextLine(string Text, double X, double Y, double Width, double Height);

public sealed record OutputFileIdentity(
    string PacketId,
    int Revision,
    string RequestSha256,
    string BytesSha256
);

public static class OutputPlatform
{
    public static string? Validate(OutputPolicy p)
    {
        if (ValidateOptions(p.Format, p.RedactValues) is string error)
            return error;
        if (p.Format == "redacted-image-pdf")
            return "Automatic pixel-redacted PDF output requires page-specific reviewed regions. Use per-packet redacted-image-pdf output with explicit reviewed regions instead of a workflow output policy.";
        if (p.Format == "searchable-image-pdf" && p.RedactValues is { Length: > 0 })
            return "Automatic searchable image PDF cannot apply text redactions. Use a sanitized text output policy or per-packet pixel-redacted PDF with explicit reviewed regions.";
        return null;
    }

    private static string? ValidateOptions(string format, string[]? redactValues) =>
        format
            is not (
                "text-json"
                or "redacted-text-pdf"
                or "searchable-image-pdf"
                or "redacted-image-pdf"
            )
        || redactValues?.Length > 100
        || redactValues?.Any(v => string.IsNullOrEmpty(v) || v.Length > 1000) == true
            ? "Invalid output policy."
            : null;

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\")
            .Replace("(", "\\(")
            .Replace(")", "\\)")
            .Replace("\r", " ")
            .Replace("\n", " ");

    private static string N(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);

    public static byte[] Pdf(
        IReadOnlyList<(
            int Width,
            int Height,
            byte[]? Jpeg,
            OutputTextLine[] Lines,
            bool Invisible
        )> pages
    )
    {
        // A new document is constructed; no imported source objects, metadata, attachments, or hidden content.
        var objects = new List<byte[]>
        {
            Array.Empty<byte>(),
            Array.Empty<byte>(),
            Encoding.ASCII.GetBytes("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
        };
        var kids = new List<int>();
        foreach (var page in pages)
        {
            int pageId = objects.Count + 1,
                contentId = pageId + 1,
                imageId = pageId + 2;
            kids.Add(pageId);
            objects.Add(
                Encoding.ASCII.GetBytes(
                    $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {page.Width} {page.Height}] /Resources << /Font << /F1 3 0 R >> {(page.Jpeg == null ? "" : $"/XObject << /I {imageId} 0 R >>")} >> /Contents {contentId} 0 R >>"
                )
            );
            var content = new StringBuilder();
            if (page.Jpeg != null)
                content.Append($"q {page.Width} 0 0 {page.Height} 0 0 cm /I Do Q\n");
            foreach (var line in page.Lines)
            {
                // Text matrix maps OCR line box to the actual page. Standard-font output is Latin-1 only.
                if (line.Text.Any(c => c > 255))
                    throw new ArgumentException(
                        "PDF text output currently supports Latin-1; use text-json for Unicode."
                    );
                double size = Math.Max(1, line.Height * .8),
                    scale = line.Text.Length == 0
                        ? 100
                        : Math.Clamp(line.Width / (line.Text.Length * size * .5) * 100, 1, 1000);
                content.Append(
                    $"BT /F1 {N(size)} Tf {(page.Invisible ? 3 : 0)} Tr {N(scale)} Tz 1 0 0 1 {N(line.X)} {N(page.Height - line.Y - line.Height)} Tm ({Escape(line.Text)}) Tj ET\n"
                );
            }
            objects.Add(Stream(Encoding.Latin1.GetBytes(content.ToString()), ""));
            if (page.Jpeg != null)
                objects.Add(
                    Stream(
                        page.Jpeg,
                        $"/Type /XObject /Subtype /Image /Width {page.Width} /Height {page.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode"
                    )
                );
        }
        objects[0] = Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>");
        objects[1] = Encoding.ASCII.GetBytes(
            $"<< /Type /Pages /Count {kids.Count} /Kids [{string.Join(" ", kids.Select(id => $"{id} 0 R"))}] >>"
        );
        using var output = new MemoryStream();
        void Write(string s) => output.Write(Encoding.ASCII.GetBytes(s));
        Write("%PDF-1.4\n");
        var offsets = new List<long> { 0 };
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            Write($"{i + 1} 0 obj\n");
            output.Write(objects[i]);
            Write("\nendobj\n");
        }
        long xref = output.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
            Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer << /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();
    }

    private static byte[] Stream(byte[] bytes, string attrs)
    {
        using var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes($"<< /Length {bytes.Length} {attrs} >>\nstream\n"));
        output.Write(bytes);
        output.Write("\nendstream"u8);
        return output.ToArray();
    }

    public static OutputTextLine[] OcrLines(Source source, Page page, int width, int height)
    {
        if (source.OcrJson == null)
            throw new ArgumentException(
                "Searchable output requires provider OCR line polygons; plain OCR text is insufficient."
            );
        using var json = JsonDocument.Parse(source.OcrJson);
        var root = json.RootElement;
        if (root.TryGetProperty("analyzeResult", out var analyze))
            root = analyze;
        var providerPage = root.GetProperty("pages")
            .EnumerateArray()
            .FirstOrDefault(p => p.GetProperty("pageNumber").GetInt32() == page.PageNumber);
        if (providerPage.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("OCR page geometry is missing.");
        double sx = width / providerPage.GetProperty("width").GetDouble(),
            sy = height / providerPage.GetProperty("height").GetDouble();
        if (!double.IsFinite(sx) || !double.IsFinite(sy) || sx <= 0 || sy <= 0)
            throw new ArgumentException("Invalid OCR page dimensions.");
        var lines = new List<OutputTextLine>();
        foreach (var line in providerPage.GetProperty("lines").EnumerateArray())
        {
            var polygon = line.GetProperty("polygon")
                .EnumerateArray()
                .Select(v => v.GetDouble())
                .ToArray();
            if (polygon.Length != 8 || polygon.Any(v => !double.IsFinite(v)))
                throw new ArgumentException("Invalid OCR polygon.");
            var xs = polygon.Where((_, i) => i % 2 == 0).ToArray();
            var ys = polygon.Where((_, i) => i % 2 == 1).ToArray();
            lines.Add(
                new(
                    line.GetProperty("content").GetString()!,
                    xs.Min() * sx,
                    ys.Min() * sy,
                    (xs.Max() - xs.Min()) * sx,
                    (ys.Max() - ys.Min()) * sy
                )
            );
        }
        if (lines.Count == 0)
            throw new ArgumentException("No positioned OCR lines available.");
        return lines.ToArray();
    }

    public static byte[] Generate(Store store, Packet packet, OutputRequest request)
    {
        if (request.Revision != packet.Revision)
            throw new ArgumentException("Output request revision does not match the packet.");
        if (
            packet.EffectiveWorkflow.Capabilities?.Output?.RedactValues is
            { Length: > 0 } configuredRedactions
        )
            request = request with
            {
                RedactValues = configuredRedactions
                    .Concat(request.RedactValues ?? [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            };
        if (ValidateOptions(request.Format, request.RedactValues) is string error)
            throw new ArgumentException(error);
        if (request.Regions?.Any(r => !packet.Pages.Any(p => p.Id == r.PageId)) == true)
            throw new ArgumentException("Redaction references an unknown page.");
        if (request.Format == "redacted-image-pdf" && request.Regions is not { Length: > 0 })
            throw new ArgumentException(
                "Pixel-redacted PDF requires at least one explicit redaction region."
            );
        string Redact(string text)
        {
            foreach (var value in request.RedactValues ?? [])
                text = text.Replace(value, "[REDACTED]", StringComparison.OrdinalIgnoreCase);
            return text;
        }
        if (request.Format == "text-json")
            return JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    packetId = packet.Id,
                    packet.Revision,
                    pages = packet.Pages.Select(p => new { pageId = p.Id, text = Redact(p.Text) }),
                },
                Contract.Json
            );
        var pages = new List<(int, int, byte[]?, OutputTextLine[], bool)>();
        long rasterPixels = 0;
        foreach (var page in packet.Pages)
        {
            if (request.Format == "redacted-text-pdf")
            {
                var lines = Redact(page.Text).Split('\n');
                foreach (var chunk in lines.Chunk(45))
                    pages.Add(
                        (
                            612,
                            792,
                            null,
                            chunk
                                .Select(
                                    (text, i) =>
                                        new OutputTextLine(
                                            text,
                                            36,
                                            36 + i * 16,
                                            Math.Min(540, text.Length * 7),
                                            14
                                        )
                                )
                                .ToArray(),
                            false
                        )
                    );
                continue;
            }
            var source = packet.Sources.Single(s => s.Id == page.SourceId);
            using var image = PreprocessingPdfRaster.Load(store, packet, source, page.PageNumber);
            rasterPixels += (long)image.Width * image.Height;
            if (rasterPixels > 100_000_000)
                throw new ArgumentException(
                    "Output exceeds the 100-megapixel aggregate raster limit."
                );
            OutputTextLine[] text = [];
            if (request.Format == "searchable-image-pdf")
            {
                if (request.RedactValues is { Length: > 0 } || request.Regions is { Length: > 0 })
                    throw new ArgumentException(
                        "Use redacted-image-pdf to remove image content; searchable output does not redact."
                    );
                text = OcrLines(source, page, image.Width, image.Height);
            }
            else
            {
                if (request.RedactValues is { Length: > 0 })
                    throw new ArgumentException(
                        "Image redaction needs explicit pixel regions, not text-only replacements."
                    );
                var regions = (request.Regions ?? []).Where(r => r.PageId == page.Id).ToArray();
                foreach (var r in regions)
                {
                    if (
                        r.X < 0
                        || r.Y < 0
                        || r.Width <= 0
                        || r.Height <= 0
                        || r.X > image.Width - r.Width
                        || r.Y > image.Height - r.Height
                    )
                        throw new ArgumentException("Redaction region lies outside source image.");
                    // Pixel replacement happens before encoding; original image streams are never copied.
                    for (int y = r.Y; y < r.Y + r.Height; y++)
                    for (int x = r.X; x < r.X + r.Width; x++)
                        image.SetPixel(x, y, SKColors.Black);
                }
            }
            using var jpg = image.Encode(SKEncodedImageFormat.Jpeg, 95);
            pages.Add(
                (
                    image.Width,
                    image.Height,
                    jpg.ToArray(),
                    text,
                    request.Format == "searchable-image-pdf"
                )
            );
        }
        return Pdf(pages);
    }

    public static string PathFor(Store store, string id, string extension)
    {
        if (!Guid.TryParseExact(id, "N", out _) || extension is not ("pdf" or "json"))
            throw new ArgumentException("Invalid output identity.");
        return Path.Combine(store.Root, "outputs", id + "." + extension);
    }

    public static OutputArtifact Save(
        Store store,
        Packet packet,
        OutputRequest request,
        Microsoft.Data.Sqlite.SqliteConnection? connection = null,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction = null,
        string? idempotencyKey = null
    )
    {
        if (packet.Status == "approved")
        {
            if (connection != null && transaction != null)
                PlatformReleaseGate.Ensure(
                    connection,
                    transaction,
                    packet,
                    new ConnectorCatalog(store.Configuration)
                );
            else
                PlatformReleaseGate.Ensure(store, packet);
        }
        var id =
            idempotencyKey == null
                ? Guid.NewGuid().ToString("N")
                : new Guid(
                    System
                        .Security.Cryptography.SHA256.HashData(
                            Encoding.UTF8.GetBytes(
                                packet.Id + ":" + packet.Revision + ":" + idempotencyKey
                            )
                        )
                        .AsSpan(0, 16)
                ).ToString("N");
        var requestHash = ExtractionStorage.Hash(
            new
            {
                request,
                sources = packet
                    .Sources.Select(source => new
                    {
                        hash = Pdfs.SourceHash(store.SourcePath(packet, source)),
                        ocr = ExtractionStorage.TextHash(source.OcrJson ?? ""),
                    })
                    .ToArray(),
                pages = packet.Pages.Select(p => new
                {
                    p.Id,
                    p.PageNumber,
                    text = ExtractionStorage.TextHash(p.Text),
                }),
                workflow = ExtractionStorage.Hash(packet.EffectiveWorkflow),
            }
        );
        var existing =
            connection != null && transaction != null
                ? ExtractionStorage.Get<OutputArtifact>(connection, transaction, "output", id)
                : ExtractionStorage.Get<OutputArtifact>(store, "output", id);
        if (existing != null)
        {
            if (existing.RequestSha256 != requestHash)
                throw new ArgumentException(
                    "Output idempotency key already used for a different request."
                );
            if (
                !File.Exists(PathFor(store, id, existing.Extension))
                || Pdfs.SourceHash(PathFor(store, id, existing.Extension)) != existing.Sha256
            )
                throw new ArgumentException("Stored output failed integrity validation.");
            return existing;
        }
        var bytes = Generate(store, packet, request);
        var ext = request.Format == "text-json" ? "json" : "pdf";
        Directory.CreateDirectory(Path.Combine(store.Root, "outputs"));
        var path = PathFor(store, id, ext);
        var identity = new OutputFileIdentity(
            packet.Id,
            packet.Revision,
            requestHash,
            Convert
                .ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                .ToLowerInvariant()
        );
        var identityPath = Path.Combine(store.Root, "outputs", id + ".identity.json");
        if (File.Exists(identityPath))
        {
            OutputFileIdentity? retained;
            try
            {
                retained = JsonSerializer.Deserialize<OutputFileIdentity>(
                    File.ReadAllBytes(identityPath),
                    Contract.Json
                );
            }
            catch (JsonException)
            {
                throw new ArgumentException(
                    "Output retry identity is invalid; manual recovery is required."
                );
            }
            if (retained != identity)
                throw new ArgumentException(
                    "Output retry identity differs from the original request."
                );
        }
        else
        {
            if (File.Exists(path))
                throw new ArgumentException(
                    "Output file has no verifiable request identity; manual recovery is required."
                );
            using var identityFile = new FileStream(identityPath, FileMode.CreateNew);
            identityFile.Write(JsonSerializer.SerializeToUtf8Bytes(identity, Contract.Json));
            identityFile.Flush(true);
        }
        if (File.Exists(path))
        {
            if (Pdfs.SourceHash(path) != identity.BytesSha256)
                throw new ArgumentException(
                    "Output retry file does not match the requested artifact."
                );
        }
        else
            using (var file = new FileStream(path, FileMode.CreateNew))
                file.Write(bytes);
        var artifact = new OutputArtifact(
            id,
            packet.Id,
            packet.Revision,
            request.Format,
            Pdfs.SourceHash(path),
            ext == "json" ? "application/json" : "application/pdf",
            ext,
            packet
                .Sources.Select(source => Pdfs.SourceHash(store.SourcePath(packet, source)))
                .ToArray(),
            DateTimeOffset.UtcNow,
            packet.Status != "approved",
            requestHash,
            ExtractionStorage.Hash(packet.EffectiveWorkflow)
        );
        if (connection == null)
            ExtractionStorage.Add(store, "output", id, artifact);
        else
        {
            using var q = connection.CreateCommand();
            q.Transaction = transaction;
            ExtractionStorage.Insert(q, "output", id, artifact);
        }
        return artifact;
    }
}

public static class OutputEndpoints
{
    public static void MapOutputEndpoints(this WebApplication app)
    {
        app.MapGet("/api/outputs/{packetId}", (string packetId, Store s) =>
                ExtractionStorage
                    .List<OutputArtifact>(s, "output")
                    .Where(o => o.PacketId == packetId)
            )
            .WithOperationsPacketAccess("packetId")
            .WithName("GetPacketOutputs")
            .Produces<OutputArtifact[]>();
        app.MapPost("/api/outputs/{packetId}", (string packetId, OutputRequest r, Store s) =>
            {
                ExtractionStorage.Initialize(s);
                using var connection = s.Open();
                using var transaction = connection.BeginTransaction();
                var p = Store.Read(connection, transaction, packetId);
                if (p == null)
                    return Results.NotFound();
                if (p.Revision != r.Revision)
                    return Results.Conflict();
                if (p.Status != "approved")
                    return Results.Conflict(
                        new ApiError("Explicit approval is required before generating outputs.")
                    );
                try
                {
                    PlatformReleaseGate.Ensure(
                        connection,
                        transaction,
                        p,
                        new ConnectorCatalog(s.Configuration)
                    );
                    var artifact = OutputPlatform.Save(s, p, r, connection, transaction);
                    transaction.Commit();
                    return Results.Ok(artifact);
                }
                catch (WorkflowException e)
                {
                    return Results.Conflict(new ApiError(e.Message));
                }
                catch (Exception e)
                    when (e
                            is ArgumentException
                                or InvalidOperationException
                                or KeyNotFoundException
                                or JsonException
                    )
                {
                    return Results.BadRequest(
                        new ApiError(
                            e is ArgumentException
                                ? e.Message
                                : "Output could not be generated. Check the format, OCR geometry and image inputs."
                        )
                    );
                }
            })
            .WithOperationsPacketAccess("packetId")
            .WithName("CreatePacketOutput")
            .Produces<OutputArtifact>()
            .Produces<ApiError>(400)
            .Produces(409)
            .Produces(404);
        app.MapGet("/api/outputs/{packetId}/files/{id}", (string packetId, string id, Store s) =>
            {
                ExtractionStorage.Initialize(s);
                using var connection = s.Open();
                using var transaction = connection.BeginTransaction();
                var o = ExtractionStorage.Get<OutputArtifact>(
                    connection,
                    transaction,
                    "output",
                    id
                );
                var packet = Store.Read(connection, transaction, packetId);
                if (
                    o?.PacketId == packetId
                    && (packet?.Status != "approved" || packet.Revision != o.Revision || o.Preview)
                )
                    return Results.Conflict(
                        new ApiError(
                            "Only outputs from the current explicitly approved revision can be downloaded."
                        )
                    );
                if (o?.PacketId != packetId || packet == null)
                    return Results.NotFound();
                try
                {
                    PlatformReleaseGate.Ensure(
                        connection,
                        transaction,
                        packet,
                        new ConnectorCatalog(s.Configuration)
                    );
                }
                catch (WorkflowException e)
                {
                    return Results.Conflict(new ApiError(e.Message));
                }
                var bytes = File.ReadAllBytes(OutputPlatform.PathFor(s, id, o.Extension));
                if (
                    Convert
                        .ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                        .ToLowerInvariant() != o.Sha256
                )
                    return Results.Conflict(new ApiError("Output integrity validation failed."));
                transaction.Commit();
                return Results.File(bytes, o.MediaType, $"{o.Format}.{o.Extension}");
            })
            .WithOperationsPacketAccess("packetId")
            .WithName("DownloadPacketOutput")
            .Produces<byte[]>(200, "application/pdf", "application/json")
            .Produces(404);
    }
}
