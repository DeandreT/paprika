using System.Text.Json;
using System.Text.Json.Serialization;

namespace paprika;

public static class QualityEndpoints
{
    public static IServiceCollection AddQualityPlatform(this IServiceCollection services) =>
        services
            .AddSingleton<TrainingStore>()
            .AddSingleton<QualityStore>()
            .AddSingleton<IOperationsRetentionParticipant, QualityRetentionParticipant>();

    public static IEndpointRouteBuilder MapQualityPlatform(
        this IEndpointRouteBuilder endpoints,
        Func<HttpContext, string>? resolveActor = null
    )
    {
        resolveActor ??= context => OperationsAccess.Identity(context).Name;
        var quality = endpoints.MapGroup("/api/quality").WithTags("Quality");
        quality
            .MapGet(
                "/packets/{packetId}",
                (string packetId, HttpContext context, QualityStore store, Store packets) =>
                    Run(() =>
                    {
                        var packet = packets.Get(packetId)
                            ?? throw new WorkflowException(404, "Packet not found.");
                        if (
                            !OperationsAccess.Identity(context).CanRead(packet.EffectiveWorkflow.Id)
                        )
                            throw new WorkflowException(403, "Workflow access is required.");
                        return Results.Ok(store.Get(packetId));
                    })
            )
            .WithName("GetPacketQuality")
            .Produces<QualityPacketResponse>()
            .Produces<ApiError>(403)
            .Produces<ApiError>(404)
            .WithOperationsPacketAccess("packetId", requireClaim: false);
        quality
            .MapGet(
                "/qa",
                (
                    string? packetId,
                    HttpContext context,
                    QualityStore store,
                    Store packets,
                    OperationsPlatformStore operations
                ) =>
                    store
                        .Tasks(packetId)
                        .Where(t =>
                            !operations.IsArchived(t.PacketId)
                            && packets.Get(t.PacketId) is { } packet
                            && OperationsAccess
                                .Identity(context)
                                .CanRead(packet.EffectiveWorkflow.Id)
                        )
                        .Select(QualityQaTaskResponse.From)
                        .ToArray()
            )
            .WithName("ListQualityQa")
            .Produces<QualityQaTaskResponse[]>()
            .WithOperationsScopedAccess();
        quality
            .MapPost("/validate", (QualityValidationRequest request, TrainingStore training) => Run(
                    () =>
                    {
                        var frozen = request.Training is null
                            ? null
                            : training.Freeze(request.Training);
                        QualityValidation.Validate(request.Quality, frozen);
                        return Results.Ok(new QualityValidationRequest(request.Quality, frozen));
                    }
                ))
            .WithName("ValidateQualityPolicy")
            .Produces<QualityValidationRequest>()
            .Produces<ApiError>(400)
            .Produces<ApiError>(409);
        quality
            .MapPost(
                "/qa/{id}/complete",
                (
                    string id,
                    QualityQaRequest request,
                    HttpContext context,
                    QualityStore store,
                    Store packets,
                    OperationsPlatformStore operations
                ) =>
                    Run(() =>
                    {
                        var task = store.Tasks().SingleOrDefault(t => t.Id == id)
                            ?? throw new WorkflowException(404, "QA task not found.");
                        if (
                            operations.IsArchived(task.PacketId)
                            || packets.Get(task.PacketId) is not { } packet
                        )
                            throw new WorkflowException(404, "QA task not found.");
                        if (
                            !OperationsAccess
                                .Identity(context)
                                .CanReview(packet.EffectiveWorkflow.Id)
                        )
                            throw new WorkflowException(
                                403,
                                "Workflow reviewer access is required."
                            );
                        return Results.Ok(
                            QualityQaTaskResponse.From(
                                store.Complete(
                                    id,
                                    request,
                                    resolveActor(context),
                                    requireCurrentPacket: true
                                )
                            )
                        );
                    })
            )
            .WithName("CompleteQualityQa")
            .Produces<QualityQaTaskResponse>()
            .Produces<ApiError>(400)
            .Produces<ApiError>(403)
            .Produces<ApiError>(404)
            .Produces<ApiError>(409)
            .WithOperationsScopedAccess();
        var trainingGroup = endpoints.MapGroup("/api/training").WithTags("Training");
        // The URL is authoritative. Do not silently ignore an explicit selector and
        // accidentally read/write Legacy (or a different named dataset).
        trainingGroup.AddEndpointFilter(
            (context, next) =>
            {
                var request = context.HttpContext.Request;
                var scope = context
                    .Arguments.OfType<TrainingDatasetScopedRequest>()
                    .FirstOrDefault();
                var target =
                    request.RouteValues["datasetId"] as string ?? TrainingStore.DefaultDatasetId;
                if (
                    request.Query.ContainsKey("datasetId")
                    || scope?.DatasetId is { } id && id != target
                )
                    return ValueTask.FromResult<object?>(
                        Results.BadRequest(new ApiError("Select the training dataset in the URL."))
                    );
                return next(context);
            }
        );
        var datasets = trainingGroup.MapGroup("/datasets");
        datasets
            .MapGet("", (TrainingStore store) => store.Datasets())
            .WithName("ListTrainingDatasets")
            .WithSummary("List classifier dataset IDs and current names")
            .Produces<TrainingDataset[]>();
        datasets
            .MapPost("", (TrainingDatasetRequest request, TrainingStore store) =>
                PlatformReply.Run(() => store.CreateDataset(request))
            )
            .WithName("CreateTrainingDataset")
            .WithSummary("Create a named classifier dataset");
        datasets
            .MapPut(
                "/{datasetId}",
                (string datasetId, TrainingDatasetRenameRequest request, TrainingStore store) =>
                    PlatformReply.Run(() => store.RenameDataset(datasetId, request))
            )
            .WithName("RenameTrainingDataset")
            .WithSummary("Rename a classifier dataset without changing its ID or retained models");
        datasets
            .MapGet("/{datasetId}/examples", (string datasetId, TrainingStore store) =>
                PlatformReply.Run(() => store.Examples(datasetId))
            )
            .WithName("ListDatasetTrainingExamples")
            .WithSummary("List immutable examples belonging to this classifier dataset");
        datasets
            .MapGet("/{datasetId}/examples/summary", (string datasetId, TrainingStore store) =>
                PlatformReply.Run(() => store.ExampleSummaries(datasetId))
            )
            .WithName("ListDatasetTrainingExampleSummaries")
            .WithSummary("List example IDs, labels and synthetic flags without document text");
        datasets
            .MapPost(
                "/{datasetId}/examples",
                (string datasetId, TrainingImportRequest request, TrainingStore store) =>
                    PlatformReply.Run(() =>
                        new TrainingImportResponse(store.Import(request.Examples, datasetId))
                    )
            )
            .WithName("ImportDatasetTrainingExamples")
            .WithSummary("Import immutable examples into this classifier dataset");
        datasets
            .MapPost(
                "/{datasetId}/models",
                (string datasetId, TrainRequest request, TrainingStore store) =>
                    PlatformReply.Run(() => store.Train(request, datasetId))
            )
            .WithName("TrainDatasetTextModel")
            .WithSummary("Train and evaluate a local classifier on this dataset alone");
        trainingGroup
            .MapGet("/examples", (TrainingStore store) => store.Examples())
            .WithName("ListTrainingExamples")
            .WithSummary("List examples in the legacy classifier dataset")
            .Produces<TrainingExample[]>();
        trainingGroup
            .MapPost("/examples", (TrainingImportRequest request, TrainingStore store) =>
                Run(() => Results.Ok(new TrainingImportResponse(store.Import(request.Examples))))
            )
            .WithName("ImportTrainingExamples")
            .WithSummary("Import immutable examples into the legacy classifier dataset")
            .Produces<TrainingImportResponse>()
            .Produces<ApiError>(400)
            .Produces<ApiError>(409);
        trainingGroup
            .MapGet("/models", (TrainingStore store) => store.Models())
            .WithName("ListTrainingModels")
            .Produces<TrainingModelResponse[]>();
        trainingGroup
            .MapPost("/models", (TrainRequest request, TrainingStore store) =>
                Run(() => Results.Ok(store.Train(request)))
            )
            .WithName("TrainTextModel")
            .WithSummary("Train a local classifier on the legacy dataset alone")
            .Produces<TrainingModelResponse>()
            .Produces<ApiError>(400);
        trainingGroup
            .MapPost(
                "/models/{id}/approve",
                (
                    string id,
                    TrainingApprovalRequest request,
                    HttpContext context,
                    TrainingStore store
                ) => Run(() => Results.Ok(store.Approve(id, request.Hash, resolveActor(context))))
            )
            .WithName("ApproveTextModel")
            .Produces<TrainingModelResponse>()
            .Produces<ApiError>(400)
            .Produces<ApiError>(403)
            .Produces<ApiError>(404)
            .Produces<ApiError>(409);
        trainingGroup
            .MapPost("/freeze", (TrainingModelRef request, TrainingStore store) =>
                Run(() => Results.Ok(store.Freeze(request)))
            )
            .WithName("FreezeTextModel")
            .Produces<TrainingModelRef>()
            .Produces<ApiError>(400)
            .Produces<ApiError>(409);
        return endpoints;
    }

    private static IResult Run(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (WorkflowException ex)
        {
            return Results.Json(new ApiError(ex.Message), statusCode: ex.Code);
        }
    }
}

public sealed record TrainingImportResponse(int Imported);

// Optional assertion for clients carrying the dataset ID alongside a request.
// Omission is valid; an explicit null/empty value is not a default selection.
public abstract record TrainingDatasetScopedRequest
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DatasetId
    {
        get;
        init =>
            field = !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new JsonException("An explicit dataset ID cannot be empty.");
    }
}
