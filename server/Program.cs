using System.Net;
using Microsoft.AspNetCore.Http.Features;
using paprika;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UsePacketMessaging();
var hosting = new HostingPolicy(builder.Configuration);
builder.WebHost.UseUrls(hosting.ListenUrl);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 26 * 1024 * 1024);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 26 * 1024 * 1024);
builder.Services.AddSingleton<Store>();
builder.Services.AddSingleton<Settings>();
builder.Services.AddSingleton<Providers>();
builder.Services.AddSingleton<ExtractionAi>();
builder.Services.AddSingleton<JobCoordinator>();
builder.Services.AddDeliverySimulation();
builder.Services.AddApiDescription();
builder.Services.AddOperationsPlatform(builder.Configuration);
builder.Services.AddQualityPlatform();
builder.Services.AddSingleton<PlatformPipeline>();
builder.Services.AddReferencePlatform();
builder.Services.AddConnectorPlatform();
builder.Services.AddSingleton<CaseStore>();
builder.Services.AddSingleton<FlowEngine>();
builder.Services.AddSingleton<FlowLibrary>();
builder.Services.AddSingleton<IOperationsRetentionParticipant, CaseFlowRetention>();
builder.Services.AddSingleton<IOperationsRetentionParticipant, ExtractionRetentionParticipant>();
builder
    .Services.AddHttpClient("providers", c => c.Timeout = TimeSpan.FromSeconds(90))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// Provider payloads and request URIs are private; do not enable HTTP request logging.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.None);
var app = builder.Build();

// Initialize all additive schemas before accepting requests, including retention previews.
_ = app.Services.GetRequiredService<QualityStore>();
_ = app.Services.GetRequiredService<ReferenceService>();
_ = app.Services.GetRequiredService<ConnectorDeliveryStore>();
_ = app.Services.GetRequiredService<FlowLibrary>();
_ = app.Services.GetRequiredService<FlowEngine>();
_ = app.Services.GetRequiredService<CaseStore>();
ExtractionAi.Initialize(app.Services.GetRequiredService<Store>());

// Orchestrators may probe by pod IP, not an application hostname. Only these exact
// GETs return a status without inspecting identity, disclosing details or serving data.
app.Use(
    async (context, next) =>
    {
        var path = context.Request.Path.Value;
        if (HttpMethods.IsGet(context.Request.Method) && path is "/health/live" or "/health/ready")
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode =
                path == "/health/live"
                || context.RequestServices.GetRequiredService<Store>().Ready()
                    ? StatusCodes.Status200OK
                    : StatusCodes.Status503ServiceUnavailable;
            return;
        }
        await next();
    }
);
app.Use(
    async (context, next) =>
    {
        if (hosting.Reject(context) is { } hostError)
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = hostError });
            return;
        }
        if (
            context.Request.Method is not ("GET" or "HEAD" or "OPTIONS")
            && context.Request.Headers["X-paprika"] != "1"
            && context.Request.Headers["X-PaperworkClassifier"] != "1"
        )
        {
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "X-paprika: 1 is required." });
            return;
        }
        try
        {
            await next();
        }
        catch (WorkflowException ex)
        {
            context.Response.StatusCode = ex.Code;
            await context.Response.WriteAsJsonAsync(new ApiError(ex.Message));
        }
        catch (BadHttpRequestException ex)
        {
            context.Response.StatusCode = ex.StatusCode;
            await context.Response.WriteAsJsonAsync(
                new { error = "Invalid or oversized request." }
            );
        }
        catch (InvalidDataException)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid upload data." });
        }
        catch (Exception)
        {
            if (context.Response.HasStarted)
                throw;
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new { error = "The local operation failed." });
        }
    }
);
app.UseOperationsPlatform();
app.UsePreviewEngineAssets();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapOpenApi();
var api = app.MapGroup("/api")
    .WithTags("paprika")
    .WithMetadata(
        new Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute(typeof(ApiError), 403),
        new Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute(typeof(ApiError), 500)
    );
api.MapGet("/health", () => TypedResults.Ok(new HealthResponse("ok")))
    .WithName("GetHealth")
    .WithSummary("Check API availability");
api.MapGet("/config", (Settings s) =>
        new ConfigResponse(
            s.Mode,
            s.OcrConfigured,
            s.ClassifierConfigured,
            [
                new("bol", "Bill of lading"),
                new("pod", "Proof of delivery"),
                new("packing_list", "Packing list"),
                new("invoice", "Invoice"),
                new("receipt", "Receipt"),
                new("unknown", "Unknown"),
            ],
            25
        )
    )
    .WithName("GetConfiguration")
    .WithSummary("Get taxonomy and provider readiness without secrets");
api.MapGet("/packets", (Store s) => s.All().Select(Public))
    .WithName("ListPackets")
    .WithSummary("List local packets, newest first")
    .Produces<PacketResponse[]>();
api.MapGet("/packets/{id}", (string id, Store s) =>
        s.Get(id) is { } p
            ? Results.Ok(Public(p))
            : Results.NotFound(new { error = "Packet not found." })
    )
    .WithName("GetPacket")
    .WithSummary("Get packet status and current review revision")
    .Produces<PacketResponse>()
    .Produces<ApiError>(404);
api.MapGet("/packets/{id}/history", (string id, Store store) =>
        store.History(id) is { } history
            ? Results.Ok(history)
            : Results.NotFound(new ApiError("Packet not found."))
    )
    .WithName("GetPacketHistory")
    .WithSummary("Read saved review snapshots and the current revision")
    .Produces<List<HistoryResponse>>()
    .Produces<ApiError>(404);
api.MapGet(
        "/packets/{id}/documents/{documentId}/download",
        (string id, string documentId, Store store) =>
        {
            return store.WithApprovedRevision<IResult>(
                id,
                (db, tx, packet) =>
                {
                    var document = packet.Documents.SingleOrDefault(d => d.Id == documentId);
                    if (document is null)
                        return Results.NotFound(new ApiError("Document not found."));
                    return Results.File(
                        Pdfs.ApprovedDocument(db, tx, packet, document, store),
                        "application/pdf",
                        $"{document.Type}-{document.Id}-r{packet.Revision}.pdf"
                    );
                },
                packet =>
                {
                    if (!packet.Documents.Any(d => d.Id == documentId))
                        throw new WorkflowException(404, "Document not found.");
                }
            );
        }
    )
    .WithName("DownloadDocument")
    .WithSummary("Download one approved document PDF")
    .Produces(200, contentType: "application/pdf")
    .Produces<ApiError>(404)
    .Produces<ApiError>(409);
api.MapPost("/packets/demo", (Store store) => Results.Ok(Public(Worker.Demo(store))))
    .WithName("CreateDemoPacket")
    .WithSummary("Queue a synthetic packet without provider calls")
    .Produces<PacketResponse>();
api.MapPost("/packets", async (HttpRequest request, Settings settings, Store store) =>
    {
        string? key = null;
        if (request.Headers.TryGetValue("Idempotency-Key", out var keys))
        {
            if (
                keys.Count != 1
                || keys[0] is not { Length: > 0 and <= 128 } value
                || value.Any(c => c < ' ' || c > '~')
            )
                return Results.BadRequest(
                    new ApiError("Idempotency-Key must contain 1–128 printable ASCII characters.")
                );
            key = value;
        }
        if (!request.HasFormContentType)
            return Results.BadRequest(
                new { error = "Upload multipart files using the files field." }
            );
        var form = await request.ReadFormAsync();
        WorkflowSelection selection;
        try
        {
            selection = WorkflowEndpoints.Selection(form);
        }
        catch (WorkflowException ex)
        {
            return Results.Json(new ApiError(ex.Message), statusCode: ex.Code);
        }
        var files = form.Files.Where(f => f.Name == "files").ToArray();
        if (
            files.Length == 0
            || files.Length > 50
            || files.Any(f => f.Length == 0)
            || files.Sum(f => f.Length) > 25L * 1024 * 1024
        )
            return Results.BadRequest(
                new { error = "Upload 1–50 nonempty files, totaling at most 25 MB." }
            );
        var p = new Packet
        {
            Name =
                Path.GetFileName(files[0].FileName)
                + (files.Length > 1 ? $" + {files.Length - 1} files" : ""),
            Mode = settings.Mode,
        };
        Directory.CreateDirectory(Path.Combine(store.Root, p.Id));
        try
        {
            foreach (var f in files)
            {
                string name = Path.GetFileName(f.FileName.Replace('\\', '/'));
                string extension = Path.GetExtension(name).ToLowerInvariant();
                if (extension is not (".pdf" or ".png" or ".jpg" or ".jpeg"))
                    throw new InvalidDataException();
                var source = new Source { Name = name, Extension = extension };
                await using (
                    var output = new FileStream(
                        store.SourcePath(p, source),
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None
                    )
                )
                {
                    await f.CopyToAsync(output);
                }
                source.PageCount = Pdfs.Validate(store.SourcePath(p, source), extension);
                source.Sha256 = Pdfs.SourceHash(store.SourcePath(p, source));
                p.Sources.Add(source);
            }
            if (p.Sources.Sum(s => s.PageCount) > 250)
                throw new InvalidDataException();
            var accepted = store.Accept(p, key, selection);
            if (accepted.Packet?.Id != p.Id)
                Directory.Delete(Path.Combine(store.Root, p.Id), true);
            return accepted.Packet is { } saved
                ? Results.Ok(Public(saved))
                : Results.Json(new ApiError(accepted.Error!), statusCode: accepted.Code);
        }
        catch (Exception ex)
        {
            if (store.Get(p.Id) is null && Directory.Exists(Path.Combine(store.Root, p.Id)))
                Directory.Delete(Path.Combine(store.Root, p.Id), true);
            if (ex is WorkflowException)
                throw;
            return Results.BadRequest(
                new
                {
                    error = "Files must be valid, unencrypted PDF, PNG or JPG, with 1–250 total pages and supported image dimensions.",
                }
            );
        }
    })
    .DisableAntiforgery()
    .WithName("UploadPacket")
    .WithSummary("Upload and queue a simulation or live paperwork packet")
    .Accepts<UploadRequest>("multipart/form-data")
    .Produces<PacketResponse>()
    .Produces<ApiError>(400)
    .Produces<ApiError>(404)
    .Produces<ApiError>(409)
    .Produces<ApiError>(413)
    .Produces(415);
api.MapPut(
        "/packets/{id}/review",
        (
            string id,
            ReviewRequest review,
            Store store,
            HttpContext context,
            OperationsPlatformStore operations,
            QualityStore quality
        ) =>
        {
            var result = store.Review(
                id,
                review,
                OperationsAccess.Actor(context),
                operations,
                quality
            );
            return result.Packet is { } p
                ? Results.Ok(Public(p))
                : Results.Json(new { error = result.Error }, statusCode: result.Code);
        }
    )
    .WithName("SaveReview")
    .WithSummary("Save or approve a complete review revision")
    .WithDescription(
        "Send the current revision and every page exactly once in nonempty documents. Stale revisions return 409. Editing a previously approved packet invalidates its approval even when approve=true; approve the new revision separately. Client reviewReasons are not authoritative."
    )
    .Produces<PacketResponse>()
    .Produces<ApiError>(400)
    .Produces<ApiError>(404)
    .Produces<ApiError>(409)
    .Produces<ApiError>(413)
    .Produces(415);
api.MapGet("/packets/{id}/sources/{sourceId}", (string id, string sourceId, Store store) =>
    {
        var p = store.Get(id);
        var s = p?.Sources.SingleOrDefault(x => x.Id == sourceId);
        return p is null || s is null
            ? Results.NotFound(new { error = "Source not found." })
            : Results.File(
                store.PacketArtifacts.OriginalBytes(p, s),
                s.Extension == ".pdf" ? "application/pdf"
                    : s.Extension == ".png" ? "image/png"
                    : "image/jpeg",
                enableRangeProcessing: true
            );
    })
    .WithName("GetSource")
    .WithSummary("Read an original source PDF or image")
    .Produces(
        200,
        contentType: "application/pdf",
        additionalContentTypes: ["image/png", "image/jpeg"]
    )
    .Produces(
        206,
        contentType: "application/pdf",
        additionalContentTypes: ["image/png", "image/jpeg"]
    )
    .Produces(416)
    .Produces<ApiError>(404);
api.MapGet("/packets/{id}/export", (string id, Store store) =>
    {
        return store.WithApprovedRevision<IResult>(id, (db, tx, p) =>
            Results.File(
                Pdfs.ApprovedExport(db, tx, p, store),
                "application/zip",
                $"packet-{p.Id}-r{p.Revision}.zip"
            )
        );
    })
    .WithName("ExportPacket")
    .WithSummary("Download approved document PDFs and provenance manifest as ZIP")
    .Produces(200, contentType: "application/zip")
    .Produces<ApiError>(404)
    .Produces<ApiError>(409);
api.MapPost("/packets/{id}/retry", (string id, Store store) =>
    {
        var result = store.Retry(id);
        return result.Packet is { } p
            ? Results.Ok(Public(p))
            : Results.Json(new { error = result.Error }, statusCode: result.Code);
    })
    .WithName("RetryPacket")
    .WithSummary("Requeue a failed packet using saved processing checkpoints")
    .Produces<PacketResponse>()
    .Produces<ApiError>(404)
    .Produces<ApiError>(409);

api.MapPost(
        "/packets/{id}/reprocess",
        (
            string id,
            ReprocessRequest request,
            Store store,
            Settings settings,
            HttpContext context
        ) =>
        {
            if ((request.WorkflowId is null) != (request.WorkflowVersion is null))
                return Results.BadRequest(
                    new ApiError("Workflow ID and version must be supplied together.")
                );
            if (
                request.WorkflowId is { } workflowId
                && !OperationsAccess.Identity(context).CanReview(workflowId)
            )
                return Results.Json(
                    new ApiError(
                        "Workflow reviewer access is required for the selected destination."
                    ),
                    statusCode: 403
                );
            var selection = request.WorkflowId is null
                ? null
                : new WorkflowSelection(request.WorkflowId, request.WorkflowVersion);
            var result = store.Reprocess(id, request.Revision, settings, selection);
            return result.Packet is { } p
                ? Results.Ok(Public(p))
                : Results.Json(new ApiError(result.Error!), statusCode: result.Code);
        }
    )
    .WithName("ReprocessPacket")
    .WithSummary(
        "Create a linked run retaining its snapshot, or explicitly selecting a published workflow"
    )
    .Produces<PacketResponse>()
    .Produces<ApiError>(400)
    .Produces<ApiError>(404)
    .Produces<ApiError>(409)
    .Produces<ApiError>(413)
    .Produces(415);
api.MapPost(
        "/packets/{id}/cancel",
        (string id, RevisionRequest request, Store store, JobCoordinator jobs) =>
        {
            var result = store.Cancel(id, request.Revision);
            if (result.Packet is { } p)
            {
                jobs.Cancel(id);
                return Results.Ok(Public(p));
            }
            return Results.Json(new ApiError(result.Error!), statusCode: result.Code);
        }
    )
    .WithName("CancelPacket")
    .WithSummary("Cancel a queued or processing run without removing its originals or audit")
    .Produces<PacketResponse>()
    .Produces<ApiError>(400)
    .Produces<ApiError>(404)
    .Produces<ApiError>(409)
    .Produces<ApiError>(413)
    .Produces(415);

// Reserved service paths must never return the SPA for a missing endpoint.
app.MapDeliverySimulation();
app.MapMessaging();
app.MapWorkflows();
app.MapQualityPlatform();
api.MapOperationsPlatform();
app.MapReferencePlatform();
app.MapConnectorPlatform();
app.MapExtractionEndpoints();
app.MapRecognitionEndpoints();
app.MapPreprocessingEndpoints();
app.MapOutputEndpoints();
app.MapCaseEndpoints();
app.MapFlowEndpoints();
app.MapFallback("/api/{**path}", () => Results.NotFound()).ExcludeFromDescription();
app.MapFallback("/openapi/{**path}", () => Results.NotFound()).ExcludeFromDescription();
app.MapFallbackToFile("index.html").ExcludeFromDescription();
app.Run();
static PacketResponse Public(Packet p) => PacketResponse.From(p);

public partial class Program;
