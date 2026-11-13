using System.Security.Cryptography;
using System.Text.Json;

namespace PaperworkClassifier;

public sealed record FlowLibraryEntry(
    string Id,
    int Revision,
    FlowDefinition Definition,
    string Hash,
    string Actor
);

public sealed record FlowSaveRequest(int Revision, FlowDefinition Definition);

public sealed class FlowLibrary
{
    private readonly Store store;

    public FlowLibrary(Store store)
    {
        this.store = store;
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "CREATE TABLE IF NOT EXISTS flow_library(id TEXT PRIMARY KEY,revision INTEGER NOT NULL,json TEXT NOT NULL)";
        q.ExecuteNonQuery();
    }

    public FlowLibraryEntry[] List()
    {
        using var c = store.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT json FROM flow_library ORDER BY rowid DESC";
        using var r = q.ExecuteReader();
        var list = new List<FlowLibraryEntry>();
        while (r.Read())
            list.Add(JsonSerializer.Deserialize<FlowLibraryEntry>(r.GetString(0), Contract.Json)!);
        return list.ToArray();
    }

    public FlowLibraryEntry Save(string id, FlowSaveRequest request, string actor)
    {
        FlowValidator.Validate(request.Definition);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100)
            throw new WorkflowException(400, "A bounded flow identity is required.");
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        using var q = c.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT revision FROM flow_library WHERE id=$id";
        q.Parameters.AddWithValue("$id", id);
        var revision = q.ExecuteScalar();
        if ((revision is null ? 0 : Convert.ToInt32(revision)) != request.Revision)
            throw new WorkflowException(409, "Flow revision changed. Reload the library.");
        var hash = Convert
            .ToHexString(
                SHA256.HashData(
                    JsonSerializer.SerializeToUtf8Bytes(request.Definition, Contract.Json)
                )
            )
            .ToLowerInvariant();
        var entry = new FlowLibraryEntry(id, request.Revision + 1, request.Definition, hash, actor);
        q.CommandText =
            "INSERT INTO flow_library VALUES($id,$r,$j) ON CONFLICT(id) DO UPDATE SET revision=excluded.revision,json=excluded.json";
        q.Parameters.AddWithValue("$r", entry.Revision);
        q.Parameters.AddWithValue("$j", JsonSerializer.Serialize(entry, Contract.Json));
        q.ExecuteNonQuery();
        tx.Commit();
        return entry;
    }
}

public static class FlowEndpoints
{
    private static IResult Result<T>(Func<T> action)
    {
        try
        {
            return Results.Ok(action());
        }
        catch (WorkflowException ex)
        {
            return Results.Json(new ApiError(ex.Message), statusCode: ex.Code);
        }
    }

    public static void MapFlowEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/flows").WithTags("Flows");
        api.MapGet(
                "/library",
                (HttpContext context, OperationsAccess access, FlowLibrary library) =>
                    Result(() =>
                    {
                        if (!OperationsAccess.Identity(context).Administrator)
                            throw new WorkflowException(403, "Administrative access is required.");
                        return library.List();
                    })
            )
            .WithName("GetFlowLibrary")
            .WithSummary("Get reusable typed flow definitions")
            .Produces<FlowLibraryEntry[]>();
        api.MapPut(
                "/library/{id}",
                (
                    string id,
                    FlowSaveRequest request,
                    HttpContext context,
                    OperationsAccess access,
                    FlowLibrary library
                ) =>
                    Result(() =>
                    {
                        var identity = OperationsAccess.Identity(context);
                        if (!identity.Administrator)
                            throw new WorkflowException(403, "Administrative access is required.");
                        return library.Save(id, request, identity.Name);
                    })
            )
            .WithName("SaveFlowDefinition")
            .WithSummary(
                "Validate and revision-check a reusable flow; accepted snapshots remain unchanged"
            )
            .Produces<FlowLibraryEntry>()
            .Produces<ApiError>(400)
            .Produces<ApiError>(409);
        api.MapGet(
                "/{packetId}",
                (
                    string packetId,
                    HttpContext context,
                    OperationsAccess access,
                    Store store,
                    FlowEngine engine
                ) =>
                    Result(() =>
                    {
                        var packet = store.Get(packetId)
                            ?? throw new WorkflowException(404, "Packet not found.");
                        if (
                            !OperationsAccess.Identity(context).CanRead(packet.EffectiveWorkflow.Id)
                        )
                            throw new WorkflowException(403, "Workflow access is required.");
                        return engine.Get(packet);
                    })
            )
            .WithName("GetPacketFlow")
            .WithSummary("Get immutable flow definition and durable block progress")
            .Produces<FlowRun>()
            .WithOperationsPacketAccess();
        api.MapPost(
                "/{packetId}/retry",
                (
                    string packetId,
                    FlowRetryRequest request,
                    HttpContext context,
                    OperationsAccess access,
                    Store store,
                    FlowEngine engine
                ) =>
                    Result(() =>
                    {
                        var packet = store.Get(packetId)
                            ?? throw new WorkflowException(404, "Packet not found.");
                        if (
                            !OperationsAccess
                                .Identity(context)
                                .CanReview(packet.EffectiveWorkflow.Id)
                        )
                            throw new WorkflowException(403, "Workflow review access is required.");
                        return engine.Retry(
                            packet,
                            request,
                            OperationsAccess.Identity(context).Name
                        );
                    })
            )
            .WithName("RetryFlowBlock")
            .WithSummary("Explicitly release a failed or abandoned block for durable recovery")
            .Produces<FlowRun>()
            .Produces<ApiError>(409)
            .WithOperationsPacketAccess(requireClaim: true);
    }
}
