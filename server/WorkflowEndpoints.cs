namespace PaperworkClassifier;

public sealed record CatalogRevisionRequest(int Revision);

public sealed record CatalogCloneRequest(int Revision, string Name);

public static class WorkflowEndpoints
{
    public static WorkflowSelection Selection(IFormCollection form)
    {
        string? Value(string key)
        {
            if (form[key].Count > 1)
                throw new WorkflowException(400, $"Supply {key} once.");
            return form[key].Count == 0 ? null : form[key][0];
        }
        int? Number(string key)
        {
            var value = Value(key);
            if (value is null)
                return null;
            if (!int.TryParse(value, out var number) || number < 1)
                throw new WorkflowException(400, $"{key} must be a positive integer.");
            return number;
        }
        var id = Value("workflowId");
        var version = Number("workflowVersion");
        var revision = Number("workflowRevision");
        var rawTest = Value("testRun");
        bool test = false;
        if (rawTest is not null && !bool.TryParse(rawTest, out test))
            throw new WorkflowException(400, "testRun must be true or false.");
        if ((id is null) != (version is null) || id is not null && string.IsNullOrWhiteSpace(id))
            throw new WorkflowException(400, "Workflow ID and version must be supplied together.");
        if (test && (id is null || revision is null))
            throw new WorkflowException(
                400,
                "Preview requires a saved workflow ID, version and revision."
            );
        if (!test && revision is not null)
            throw new WorkflowException(400, "workflowRevision is only valid for draft previews.");
        return new(id, version, test, revision);
    }

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

    public static void MapWorkflows(this WebApplication app)
    {
        var api = app.MapGroup("/api/workflows")
            .WithTags("Workflows")
            .WithMetadata(
                new Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute(typeof(ApiError), 403),
                new Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute(typeof(ApiError), 500)
            );
        api.MapGet("", (Store store) => store.Workflows.Catalog())
            .WithName("GetWorkflows")
            .WithSummary("Get the versioned catalog, default and non-test run counts");
        Describe(
            api.MapPost("/catalog", (CreateCatalogRequest request, Store store) =>
                Result(() => store.Workflows.Create(request))
            ),
            "CreateCatalogEntry",
            "Create a validated draft definition, release or workflow"
        );
        Describe(
            api.MapPut(
                "/catalog/{kind}/{id}/{version:int}",
                (string kind, string id, int version, CatalogEntry request, Store store) =>
                    Result(() => store.Workflows.Edit(kind, id, version, request))
            ),
            "EditCatalogEntry",
            "Edit an exact draft revision"
        );
        Describe(
            api.MapPost(
                "/catalog/{kind}/{id}/{version:int}/publish",
                (
                    string kind,
                    string id,
                    int version,
                    CatalogRevisionRequest request,
                    Store store
                ) => Result(() => store.Workflows.Publish(kind, id, version, request.Revision))
            ),
            "PublishCatalogEntry",
            "Publish a draft with exact published dependencies"
        );
        Describe(
            api.MapPost(
                "/catalog/{kind}/{id}/{version:int}/archive",
                (
                    string kind,
                    string id,
                    int version,
                    CatalogRevisionRequest request,
                    Store store
                ) => Result(() => store.Workflows.Archive(kind, id, version, request.Revision))
            ),
            "ArchiveCatalogEntry",
            "Archive a version while retaining historical access"
        );
        Describe(
            api.MapPost(
                "/catalog/{kind}/{id}/{version:int}/versions",
                (
                    string kind,
                    string id,
                    int version,
                    CatalogRevisionRequest request,
                    Store store
                ) => Result(() => store.Workflows.NewVersion(kind, id, version, request.Revision))
            ),
            "NewCatalogVersion",
            "Copy a version into a new draft version of the same identity"
        );
        Describe(
            api.MapPost(
                "/catalog/{kind}/{id}/{version:int}/clone",
                (string kind, string id, int version, CatalogCloneRequest request, Store store) =>
                    Result(() =>
                        store.Workflows.Clone(kind, id, version, request.Revision, request.Name)
                    )
            ),
            "CloneCatalogEntry",
            "Copy a version into a new draft identity"
        );
        api.MapPut("/default", (VersionRef request, Store store) =>
                Result(() => store.Workflows.SetDefault(request))
            )
            .WithName("SetDefaultWorkflow")
            .WithSummary("Select a published default workflow")
            .Produces<VersionRef>()
            .Produces<ApiError>(400)
            .Produces<ApiError>(404)
            .Produces<ApiError>(409);
    }

    private static void Describe(RouteHandlerBuilder route, string name, string summary) =>
        route
            .WithName(name)
            .WithSummary(summary)
            .Produces<CatalogEntry>()
            .Produces<ApiError>(400)
            .Produces<ApiError>(404)
            .Produces<ApiError>(409);
}
