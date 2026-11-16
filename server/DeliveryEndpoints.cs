using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.OpenApi;

namespace paprika;

public static class DeliveryEndpoints
{
    public static IServiceCollection AddDeliverySimulation(this IServiceCollection services)
    {
        services.AddSingleton<DeliveryStore>();
        services.AddOpenApi("v1", options =>
        {
            options.AddSchemaTransformer(
                (schema, context, cancellationToken) =>
                {
                    if (
                        context.JsonTypeInfo.Type == typeof(QueueDeliveryRequest)
                        || context.JsonTypeInfo.Type == typeof(DeliveryResponse)
                    )
                    {
                        void Values(string property, params string[] values)
                        {
                            if (
                                schema.Properties?.TryGetValue(property, out var field) == true
                                && field is OpenApiSchema s
                            )
                                s.Enum = values
                                    .Select(v =>
                                        (System.Text.Json.Nodes.JsonNode)
                                            System.Text.Json.Nodes.JsonValue.Create(v)!
                                    )
                                    .ToList();
                        }
                        Values(
                            "scenario",
                            "success",
                            "transient-once",
                            "transient-always",
                            "reject"
                        );
                        Values("mode", "simulation");
                        Values("status", "queued", "processing", "delivered", "failed", "rejected");
                        if (
                            context.JsonTypeInfo.Type == typeof(QueueDeliveryRequest)
                            && schema.Properties?["scenario"] is OpenApiSchema scenario
                        )
                            scenario.Default = System.Text.Json.Nodes.JsonValue.Create("success");
                    }
                    return Task.CompletedTask;
                }
            );
            options.AddOperationTransformer(
                (operation, context, cancellationToken) =>
                {
                    if (operation.OperationId == "DownloadDeliveryArtifact")
                        ((OpenApiResponse)operation.Responses!["200"]).Content = new Dictionary<
                            string,
                            OpenApiMediaType
                        >
                        {
                            ["application/zip"] = new()
                            {
                                Schema = new OpenApiSchema
                                {
                                    Type = JsonSchemaType.String,
                                    Format = "binary",
                                },
                            },
                        };
                    return Task.CompletedTask;
                }
            );
        });
        return services;
    }

    public static IEndpointRouteBuilder MapDeliverySimulation(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .WithMetadata(
                new Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute(typeof(ApiError), 403),
                new Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute(typeof(ApiError), 500)
            );
        api.MapPost(
                "/packets/{id}/deliveries",
                (string id, QueueDeliveryRequest request, DeliveryStore deliveries) =>
                    Respond(deliveries.Queue(id, request))
            )
            .WithName("QueueDelivery")
            .WithTags("Delivery")
            .WithSummary("Queue an immutable approved revision for local simulation only.");
        api.MapGet(
                "/packets/{id}/deliveries",
                Results<Ok<DeliveryResponse[]>, NotFound<ApiError>> (
                    string id,
                    Store store,
                    DeliveryStore deliveries
                ) =>
                    store.Get(id) is null
                        ? TypedResults.NotFound(new ApiError("Packet not found."))
                        : TypedResults.Ok(deliveries.All(id))
            )
            .WithName("ListPacketDeliveries")
            .WithTags("Delivery");
        api.MapPost(
                "/deliveries/{deliveryId}/retry",
                (string deliveryId, DeliveryStore deliveries) =>
                    Respond(deliveries.Retry(deliveryId))
            )
            .WithName("RetryDelivery")
            .WithTags("Delivery")
            .WithSummary("Manually retry a failed simulation; business rejections are terminal.");
        api.MapGet(
                "/deliveries/{deliveryId}/artifact",
                Results<PhysicalFileHttpResult, NotFound<ApiError>, Conflict<ApiError>> (
                    string deliveryId,
                    DeliveryStore deliveries
                ) =>
                {
                    var d = deliveries.Get(deliveryId);
                    if (d is null)
                        return TypedResults.NotFound(new ApiError("Delivery not found."));
                    if (d.Status != "delivered")
                        return TypedResults.Conflict(
                            new ApiError("The simulation artifact is not delivered.")
                        );
                    if (!File.Exists(deliveries.ArtifactPath(d)))
                        return TypedResults.NotFound(new ApiError("Local artifact not found."));
                    return TypedResults.PhysicalFile(
                        deliveries.ArtifactPath(d),
                        "application/zip",
                        $"simulation-{d.Id}.zip"
                    );
                }
            )
            .WithName("DownloadDeliveryArtifact")
            .WithTags("Delivery")
            .Produces(200, contentType: "application/zip");
        api.MapGet("/operations", (Store store, DeliveryStore deliveries) =>
                TypedResults.Ok(OperationsResponse.Read(store, deliveries))
            )
            .WithName("GetOperations")
            .WithTags("Operations")
            .WithSummary("Local aggregate counts without document text or business identifiers.");
        return app;
    }

    private static Results<
        Ok<DeliveryResponse>,
        BadRequest<ApiError>,
        NotFound<ApiError>,
        Conflict<ApiError>
    > Respond(DeliveryResult result) =>
        result.Code switch
        {
            200 => TypedResults.Ok(result.Delivery!),
            400 => TypedResults.BadRequest(new ApiError(result.Error!)),
            404 => TypedResults.NotFound(new ApiError(result.Error!)),
            _ => TypedResults.Conflict(new ApiError(result.Error!)),
        };
}
