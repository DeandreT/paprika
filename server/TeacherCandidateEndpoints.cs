namespace paprika;

public static class TeacherCandidateEndpoints
{
    // Use the named dataset group's role and selector checks for every route carrying text.
    public static void MapTeacherCandidates(this RouteGroupBuilder datasets)
    {
        var group = datasets.MapGroup("/{datasetId}");
        group.AddEndpointFilter((context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return next(context);
        });
        group.MapGet("/candidates", (string datasetId, TeacherCandidates store, HttpContext context) =>
                store.List(datasetId, OperationsAccess.Identity(context)))
            .WithName("ListTeacherCandidates");
        group.MapPost("/candidates/import", (string datasetId, CandidateImportRequest request, TeacherCandidates store, HttpContext context) =>
                new TrainingImportResponse(store.Import(datasetId, request, OperationsAccess.Identity(context))))
            .WithName("ImportTeacherCandidates");
        group.MapPost("/candidates/review", (string datasetId, CandidateReviewRequest request, TeacherCandidates store, HttpContext context) =>
                new CandidateReviewResult(store.Review(datasetId, request, OperationsAccess.Identity(context))))
            .WithName("ReviewTeacherCandidates").Produces<ApiError>(409);
        group.MapGet("/export", (string datasetId, TeacherCandidates store, HttpContext context) =>
            {
                var export = store.Export(datasetId, OperationsAccess.Identity(context));
                context.Response.Headers.ContentDisposition = $"attachment; filename=\"paprika-{export.Dataset.Id}-reviewed.json\"";
                return export;
            }).WithName("ExportReviewedPageLabels");
    }
}
