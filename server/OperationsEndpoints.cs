namespace PaperworkClassifier;

public sealed record OperationsPolicy(
    int ReviewSlaHours = 24,
    int DefaultPriority = 0,
    int LeaseSeconds = 900
)
{
    public static string? Validate(OperationsPolicy? policy) =>
        policy is null ? null
        : policy.ReviewSlaHours is < 1 or > 8760
        || policy.DefaultPriority is < 0 or > 100
        || policy.LeaseSeconds is < 30 or > 3600
            ? "Operations requires SLA hours 1–8760, priority 0–100 and lease seconds 30–3600."
        : null;
}

public sealed record OperationsSessionResponse(
    string Mode,
    bool Authenticated,
    OperationsIdentity? Identity
);

public sealed record OperationsOpenReview(string PacketId, int Revision);

public sealed record OperationsRetentionResponse(
    OperationsRetentionPolicy Policy,
    string ExecutionMode,
    OperationsRetentionCandidate[] Candidates
);

public static class OperationsEndpoints
{
    public static RouteGroupBuilder MapOperationsPlatform(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/operations-platform").WithTags("Operations platform");
        group.MapGet("/session", (HttpContext c, OperationsAccess a) =>
            new OperationsSessionResponse(
                a.Shared ? "shared" : "local",
                true,
                OperationsAccess.Identity(c)
            )
        );
        group.MapPost("/session", (HttpContext c, OperationsAccess a) =>
        {
            var identity = OperationsAccess.Identity(c);
            a.SignIn(c, identity);
            return Results.Ok(
                new OperationsSessionResponse(a.Shared ? "shared" : "local", true, identity)
            );
        });
        group.MapDelete("/session", (HttpContext c, OperationsAccess a) =>
        {
            a.SignOut(c);
            return Results.NoContent();
        });
        group.MapGet("/tasks", (HttpContext c, OperationsPlatformStore s, string? packetId) =>
            s.Tasks(OperationsAccess.Identity(c))
                .Where(t => packetId is null || t.PacketId == packetId)
                .ToArray()
        );
        group.MapPost(
            "/tasks/open",
            (OperationsOpenReview request, HttpContext c, OperationsPlatformStore s) =>
                Run(() =>
                    s.OpenReview(request.PacketId, request.Revision, OperationsAccess.Identity(c))
                )
        );
        group.MapGet("/stats", (HttpContext c, OperationsPlatformStore s) =>
        {
            var tasks = s.Tasks(OperationsAccess.Identity(c));
            return new OperationsTaskStats(
                tasks.Count,
                tasks.Count(t => t.State == "available"),
                tasks.Count(t => t.State == "claimed"),
                tasks.Count(t => t.State == "completed"),
                tasks.Count(t =>
                    t.State is "available" or "claimed" && t.DueAt < DateTimeOffset.UtcNow
                )
            );
        });
        foreach (var action in new[] { "claim", "renew", "release", "complete" })
        {
            var operation = action;
            group.MapPost(
                "/tasks/{id}/" + action,
                (
                    string id,
                    OperationsTaskChange request,
                    HttpContext c,
                    OperationsPlatformStore s
                ) => Run(() => s.Change(id, request, OperationsAccess.Identity(c), operation))
            );
        }
        group.MapPut(
            "/tasks/{id}/schedule",
            (string id, OperationsTaskSchedule request, HttpContext c, OperationsPlatformStore s) =>
                Run(() => s.Schedule(id, request, OperationsAccess.Identity(c)))
        );
        group.MapGet("/retention", (HttpContext c, OperationsPlatformStore s) =>
            Admin(c, () =>
                new OperationsRetentionResponse(
                    s.RetentionPolicy,
                    "explicit logical archive; physical purge separately controlled",
                    s.Preview().ToArray()
                )
            )
        );
        group.MapGet("/retention/tombstones", (HttpContext c, OperationsPlatformStore s) =>
            Admin(c, () => s.Tombstones())
        );
        group.MapGet("/retention/audit", (HttpContext c, OperationsPlatformStore s) =>
            Admin(c, () => s.RetentionAudit())
        );
        group.MapPost(
            "/retention/execute",
            (OperationsRetentionRequest request, HttpContext c, OperationsPlatformStore s) =>
                Run(() => s.Archive(request, OperationsAccess.Identity(c)))
        );
        group.MapGet(
            "/retention/{id}/purge-plan",
            (string id, HttpContext c, OperationsPlatformStore s) => Admin(c, () => s.PurgePlan(id))
        );
        group.MapPost(
            "/retention/purge",
            (OperationsPurgeRequest request, HttpContext c, OperationsPlatformStore s) =>
                Run(() => s.Purge(request, OperationsAccess.Identity(c)))
        );
        return group;
    }

    private static IResult Admin<T>(HttpContext context, Func<T> action) =>
        OperationsAccess.Identity(context).Administrator
            ? Run(action)
            : Results.Json(new ApiError("Administrator access required."), statusCode: 403);

    private static IResult Run<T>(Func<T> action)
    {
        try
        {
            return Results.Ok(action());
        }
        catch (OperationsConflict ex)
        {
            return Results.Json(new ApiError(ex.Message), statusCode: ex.StatusCode);
        }
    }
}
