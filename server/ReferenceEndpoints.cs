using Microsoft.AspNetCore.Http.HttpResults;

namespace paprika;

public static class ReferenceEndpoints
{
    public static IServiceCollection AddReferencePlatform(this IServiceCollection services)
    {
        services.AddSingleton<ConnectorCatalog>();
        services.AddSingleton<ReferenceService>();
        services.AddSingleton<IOperationsRetentionParticipant, ReferenceRetentionParticipant>();
        return services;
    }

    public static IEndpointRouteBuilder MapReferencePlatform(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/references").WithTags("References");
        api.MapGet("/datasets", (ReferenceService references) =>
                TypedResults.Ok(references.Datasets())
            )
            .WithName("ListReferenceDatasets")
            .WithSummary(
                "List immutable local dataset versions, including explicit synthetic provenance."
            );
        api.MapPost(
                "/datasets",
                (
                    ReferenceDatasetRequest request,
                    ReferenceService references,
                    HttpContext context
                ) =>
                    PlatformReply.Run(() =>
                        references.Create(request, OperationsAccess.Identity(context).Name)
                    )
            )
            .WithName("CreateReferenceDataset")
            .WithSummary(
                "Create an immutable dataset version; existing versions cannot be edited."
            );
        api.MapGet("/packets/{packetId}", (string packetId, ReferenceService references) =>
                TypedResults.Ok(references.Results(packetId))
            )
            .WithName("GetPacketReferenceResults")
            .WithOperationsPacketAccess();
        api.MapPost(
                "/packets/{packetId}/lookup",
                async (
                    string packetId,
                    ReferenceLookupRequest request,
                    ReferenceService references,
                    HttpContext context,
                    CancellationToken ct
                ) =>
                    await PlatformReply.RunAsync(() =>
                        references.LookupAsync(
                            request with
                            {
                                PacketId = packetId,
                            },
                            ct,
                            actor: OperationsAccess.Actor(context)
                        )
                    )
            )
            .WithName("LookupPacketReferences")
            .WithSummary("Lookup exact document references against the packet's pinned policy.")
            .WithOperationsPacketAccess(requireClaim: true);
        api.MapPost(
                "/packets/{packetId}/verify",
                (
                    string packetId,
                    ReferenceVerifyRequest request,
                    ReferenceService references,
                    HttpContext context
                ) =>
                    PlatformReply.Run(() =>
                        references.Verify(
                            request with
                            {
                                PacketId = packetId,
                            },
                            OperationsAccess.Identity(context).Name
                        )
                    )
            )
            .WithName("VerifyPacketReference")
            .WithSummary(
                "Record a reviewer-selected match for the exact packet revision and result hash."
            )
            .WithOperationsPacketAccess(requireClaim: true);
        return app;
    }
}

public static class PlatformReply
{
    public static Results<
        Ok<T>,
        BadRequest<ApiError>,
        NotFound<ApiError>,
        Conflict<ApiError>
    > Run<T>(Func<T> action)
    {
        try
        {
            return TypedResults.Ok(action());
        }
        catch (KeyNotFoundException ex)
        {
            return TypedResults.NotFound(new ApiError(ex.Message));
        }
        catch (InvalidDataException ex)
        {
            return TypedResults.BadRequest(new ApiError(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.Conflict(new ApiError(ex.Message));
        }
    }

    public static async Task<
        Results<Ok<T>, BadRequest<ApiError>, NotFound<ApiError>, Conflict<ApiError>>
    > RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return TypedResults.Ok(await action());
        }
        catch (KeyNotFoundException ex)
        {
            return TypedResults.NotFound(new ApiError(ex.Message));
        }
        catch (InvalidDataException ex)
        {
            return TypedResults.BadRequest(new ApiError(ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.Conflict(new ApiError(ex.Message));
        }
    }
}
