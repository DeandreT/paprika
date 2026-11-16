using SkiaSharp;

namespace paprika;

public sealed record PreprocessingPolicy(
    int Rotation = 0,
    bool Grayscale = false,
    double Contrast = 1
);

public sealed record PreprocessingRequest(
    int Revision,
    string SourceId,
    PreprocessingPolicy Policy,
    int PageNumber = 1
);

public sealed record PreprocessingDerivative(
    string Id,
    string PacketId,
    string SourceId,
    string OriginalSha256,
    string Sha256,
    PreprocessingPolicy Policy,
    int Width,
    int Height,
    DateTimeOffset CreatedAt,
    int PageNumber = 1,
    int PacketRevision = 0,
    string? WorkflowSha256 = null
);

public static class PreprocessingPlatform
{
    public static string? Validate(PreprocessingPolicy p) =>
        p.Rotation is not (0 or 90 or 180 or 270)
        || !double.IsFinite(p.Contrast)
        || p.Contrast < .25
        || p.Contrast > 4
            ? "Rotation must be 0/90/180/270 and contrast 0.25–4."
            : null;

    public static SKBitmap Decode(string path)
    {
        using var input = File.OpenRead(path);
        using var codec = SKCodec.Create(input);
        if (
            codec == null
            || codec.Info.Width < 1
            || codec.Info.Height < 1
            || (long)codec.Info.Width * codec.Info.Height > 40_000_000
        )
            throw new ArgumentException(
                "Unsupported image or image exceeds 40 megapixels. PDF rasterization is not installed."
            );
        var bitmap = new SKBitmap(
            codec.Info.Width,
            codec.Info.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Premul
        );
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new ArgumentException("Image decode failed.");
        }
        return bitmap;
    }

    public static SKBitmap Transform(SKBitmap image, PreprocessingPolicy policy)
    {
        if (Validate(policy) is string error)
            throw new ArgumentException(error);
        bool swap = policy.Rotation is 90 or 270;
        var result = new SKBitmap(
            swap ? image.Height : image.Width,
            swap ? image.Width : image.Height
        );
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            var c = image.GetPixel(x, y);
            double r = c.Red,
                g = c.Green,
                b = c.Blue;
            if (policy.Grayscale)
                r = g = b = .2126 * r + .7152 * g + .0722 * b;
            byte Adjust(double v) =>
                (byte)Math.Clamp(Math.Round((v - 127.5) * policy.Contrast + 127.5), 0, 255);
            var (dx, dy) = policy.Rotation switch
            {
                90 => (image.Height - 1 - y, x),
                180 => (image.Width - 1 - x, image.Height - 1 - y),
                270 => (y, image.Width - 1 - x),
                _ => (x, y),
            };
            result.SetPixel(dx, dy, new SKColor(Adjust(r), Adjust(g), Adjust(b), c.Alpha));
        }
        return result;
    }

    public static string DerivativePath(Store store, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("Invalid derivative ID.");
        return Path.Combine(store.Root, "derivatives", id + ".png");
    }

    public static PreprocessingDerivative Create(
        Store store,
        Packet packet,
        Source source,
        PreprocessingPolicy policy,
        Microsoft.Data.Sqlite.SqliteConnection? connection = null,
        Microsoft.Data.Sqlite.SqliteTransaction? transaction = null,
        int pageNumber = 1
    )
    {
        using var image = PreprocessingPdfRaster.Load(store, packet, source, pageNumber);
        using var result = Transform(image, policy);
        using var encoded = result.Encode(SKEncodedImageFormat.Png, 100);
        var id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.Combine(store.Root, "derivatives"));
        var path = DerivativePath(store, id);
        using (var file = new FileStream(path, FileMode.CreateNew))
            encoded.SaveTo(file);
        var derivative = new PreprocessingDerivative(
            id,
            packet.Id,
            source.Id,
            Pdfs.SourceHash(store.SourcePath(packet, source)),
            Pdfs.SourceHash(path),
            policy,
            result.Width,
            result.Height,
            DateTimeOffset.UtcNow,
            pageNumber,
            packet.Revision,
            ExtractionStorage.Hash(packet.EffectiveWorkflow)
        );
        if (connection == null)
            ExtractionStorage.Add(store, "derivative", id, derivative);
        else
        {
            using var q = connection.CreateCommand();
            q.Transaction = transaction;
            ExtractionStorage.Insert(q, "derivative", id, derivative);
        }
        return derivative;
    }
}

public static class PreprocessingEndpoints
{
    public static void MapPreprocessingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/preprocessing/{packetId}", (string packetId, Store s) =>
                ExtractionStorage
                    .List<PreprocessingDerivative>(s, "derivative")
                    .Where(d => d.PacketId == packetId)
            )
            .WithOperationsPacketAccess("packetId")
            .WithName("GetPacketDerivatives")
            .Produces<PreprocessingDerivative[]>();
        app.MapPost(
                "/api/preprocessing/{packetId}",
                (
                    string packetId,
                    PreprocessingRequest request,
                    Store s,
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
                    if (p.Revision != request.Revision)
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
                    var source = p.Sources.FirstOrDefault(x => x.Id == request.SourceId);
                    if (source == null)
                        return Results.NotFound();
                    try
                    {
                        var derivative = PreprocessingPlatform.Create(
                            s,
                            p,
                            source,
                            request.Policy,
                            connection,
                            transaction,
                            request.PageNumber
                        );
                        transaction.Commit();
                        return Results.Ok(derivative);
                    }
                    catch (ArgumentException e)
                    {
                        return Results.BadRequest(new { error = e.Message });
                    }
                }
            )
            .WithOperationsPacketAccess("packetId", requireClaim: true)
            .WithName("CreatePacketDerivative")
            .Produces<PreprocessingDerivative>()
            .Produces<ApiError>(400)
            .Produces(409)
            .Produces(404);
        app.MapGet(
                "/api/preprocessing/{packetId}/derivatives/{id}/image",
                (string packetId, string id, Store s) =>
                    ExtractionStorage.Get<PreprocessingDerivative>(s, "derivative", id)?.PacketId
                    != packetId
                        ? Results.NotFound()
                        : Results.File(PreprocessingPlatform.DerivativePath(s, id), "image/png")
            )
            .WithOperationsPacketAccess("packetId")
            .WithName("DownloadPacketDerivative")
            .Produces<byte[]>(200, "image/png")
            .Produces(404);
    }
}
