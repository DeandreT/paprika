using Microsoft.AspNetCore.Http.HttpResults;

namespace PaperworkClassifier;

public static class ConnectorEndpoints
{
    public static IServiceCollection AddConnectorPlatform(this IServiceCollection services)
    {
        services.AddSingleton<ConnectorCatalog>();
        services.AddSingleton<ConnectorIntake>();
        services.AddSingleton<ConnectorDeliveryStore>();
        services.AddHostedService<ConnectorWorker>();
        services.AddSingleton<IOperationsRetentionParticipant, ConnectorRetentionParticipant>();
        return services;
    }

    public static IEndpointRouteBuilder MapConnectorPlatform(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/connectors").WithTags("Connectors");
        api.MapGet("/", (ConnectorCatalog catalog) => TypedResults.Ok(catalog.All()))
            .WithName("ListConnectors")
            .WithSummary(
                "List configured connector IDs and kinds without paths, endpoints or credentials."
            );
        api.MapPost(
                "/{id}/import",
                async (
                    string id,
                    ConnectorImportRequest request,
                    ConnectorIntake intake,
                    HttpContext context,
                    CancellationToken ct
                ) =>
                    await PlatformReply.RunAsync(() =>
                        intake.ImportAsync(id, request, ct, OperationsAccess.Actor(context))
                    )
            )
            .WithName("ImportConnectorFolder")
            .WithSummary(
                "Explicitly scan at most 100 files from the approved configured root; imports are content-idempotent."
            );
        api.MapPost(
                "/{id}/packets/{packetId}/deliveries",
                (
                    string id,
                    string packetId,
                    ConnectorDeliveryRequest request,
                    ConnectorDeliveryStore deliveries,
                    HttpContext context
                ) =>
                    PlatformReply.Run(() =>
                        deliveries.Queue(
                            id,
                            request with
                            {
                                PacketId = packetId,
                            },
                            OperationsAccess.Identity(context).Name
                        )
                    )
            )
            .WithName("QueueConnectorDelivery")
            .WithSummary("Durably queue actual export bytes from the exact approved revision.")
            .WithOperationsPacketAccess();
        api.MapGet("/deliveries", (string? packetId, ConnectorDeliveryStore deliveries) =>
                TypedResults.Ok(deliveries.All(packetId))
            )
            .WithName("ListConnectorDeliveries");
        api.MapPost("/deliveries/{id}/retry", (string id, ConnectorDeliveryStore deliveries) =>
                PlatformReply.Run(() => deliveries.Retry(id))
            )
            .WithName("RetryConnectorDelivery")
            .WithSummary(
                "Retry actual delivery with its original immutable bytes and idempotency key."
            );
        api.MapPost(
                "/deliveries/{id}/receipt",
                (
                    string id,
                    ConnectorReceiptRequest request,
                    ConnectorDeliveryStore deliveries,
                    HttpContext context
                ) =>
                    PlatformReply.Run(() =>
                        deliveries.Receipt(id, request, OperationsAccess.Identity(context).Name)
                    )
            )
            .WithName("RecordConnectorBusinessReceipt")
            .WithSummary(
                "Record a separately received business receipt; a transport ACK is not business acceptance."
            );
        return app;
    }
}
