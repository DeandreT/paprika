namespace paprika;

public static class CaseEndpoints
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

    public static void MapCaseEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/cases").WithTags("Cases");
        api.MapGet(
                "",
                (string? packetId, HttpContext context, OperationsAccess access, CaseStore cases) =>
                    Result(() =>
                    {
                        var identity = OperationsAccess.Identity(context);
                        return cases
                            .List()
                            .Where(c =>
                                cases.Accessible(c, identity.CanRead)
                                && (packetId is null || c.Members.Any(m => m.PacketId == packetId))
                            )
                            .ToArray();
                    })
            )
            .WithName("GetCases")
            .WithSummary(
                "List authorized cross-submission cases and expected-document completeness"
            )
            .Produces<CaseRecord[]>()
            .WithOperationsScopedAccess();
        api.MapGet(
                "/{id}",
                (string id, HttpContext context, OperationsAccess access, CaseStore cases) =>
                    Result(() =>
                    {
                        var record = cases.Get(id);
                        if (!cases.Accessible(record, OperationsAccess.Identity(context).CanRead))
                            throw new WorkflowException(403, "Case workflow access is required.");
                        return record;
                    })
            )
            .WithName("GetCase")
            .WithSummary("Get case membership and retained source provenance")
            .Produces<CaseRecord>()
            .WithOperationsScopedAccess();
        api.MapGet("/{id}/history", (string id, HttpContext context, CaseStore cases) => Result(
                    () =>
                    {
                        var record = cases.Get(id);
                        if (!cases.Accessible(record, OperationsAccess.Identity(context).CanRead))
                            throw new WorkflowException(403, "Case workflow access is required.");
                        return cases
                            .History(id)
                            .Where(r =>
                                cases.Accessible(r, OperationsAccess.Identity(context).CanRead)
                            )
                            .ToArray();
                    }
                ))
            .WithName("GetCaseHistory")
            .WithSummary("Get immutable previous case revisions and their source provenance")
            .Produces<CaseRecord[]>()
            .WithOperationsScopedAccess();
        api.MapGet(
                "/proposals/{packetId}",
                (
                    string packetId,
                    HttpContext context,
                    OperationsAccess access,
                    Store store,
                    CaseStore cases
                ) =>
                    Result(() =>
                    {
                        var packet = store.Get(packetId)
                            ?? throw new WorkflowException(404, "Packet not found.");
                        if (
                            !OperationsAccess.Identity(context).CanRead(packet.EffectiveWorkflow.Id)
                        )
                            throw new WorkflowException(403, "Workflow access is required.");
                        var proposal = cases.Proposal(packetId);
                        var visible = cases
                            .List()
                            .Where(r =>
                                cases.Accessible(r, OperationsAccess.Identity(context).CanRead)
                            )
                            .Select(r => r.Id)
                            .ToHashSet();
                        return proposal is null
                            ? null
                            : proposal with
                            {
                                CandidateCaseIds = proposal
                                    .CandidateCaseIds.Where(visible.Contains)
                                    .ToArray(),
                            };
                    })
            )
            .WithName("GetCaseProposal")
            .WithSummary("Get a proposed or verified automatic case association")
            .Produces<CaseCollationProposal>()
            .WithOperationsPacketAccess();
        api.MapPost(
                "",
                (
                    CaseCreateRequest request,
                    HttpContext context,
                    OperationsAccess access,
                    CaseStore cases
                ) =>
                    Result(() =>
                    {
                        var identity = OperationsAccess.Identity(context);
                        return cases.Create(request, identity.Name, identity.CanReview);
                    })
            )
            .WithName("CreateCase")
            .WithSummary("Create an explicitly scoped case with selected document membership")
            .Produces<CaseRecord>()
            .Produces<ApiError>(409)
            .WithOperationsScopedAccess();
        api.MapPost(
                "/{id}/members",
                (
                    string id,
                    CaseMembershipRequest request,
                    HttpContext context,
                    OperationsAccess access,
                    CaseStore cases
                ) =>
                    Result(() =>
                    {
                        var identity = OperationsAccess.Identity(context);
                        return cases.AddMembers(id, request, identity.Name, identity.CanReview);
                    })
            )
            .WithName("AddCaseMembers")
            .WithSummary("Add explicit membership after checking case and packet revisions")
            .Produces<CaseRecord>()
            .Produces<ApiError>(409)
            .WithOperationsScopedAccess();
        api.MapPost(
                "/{id}/remove-members",
                (
                    string id,
                    CaseMembershipRequest request,
                    HttpContext context,
                    OperationsAccess access,
                    CaseStore cases
                ) =>
                    Result(() =>
                    {
                        var identity = OperationsAccess.Identity(context);
                        return cases.RemoveMembers(id, request, identity.Name, identity.CanReview);
                    })
            )
            .WithName("RemoveCaseMembers")
            .WithSummary("Remove explicit membership with revision checks")
            .Produces<CaseRecord>()
            .Produces<ApiError>(409)
            .WithOperationsScopedAccess();
        api.MapPut(
                "/{id}",
                (
                    string id,
                    CaseEditRequest request,
                    HttpContext context,
                    OperationsAccess access,
                    CaseStore cases
                ) =>
                    Result(() =>
                    {
                        var identity = OperationsAccess.Identity(context);
                        return cases.Edit(id, request, identity.Name, identity.CanReview);
                    })
            )
            .WithName("EditCaseChecklist")
            .WithSummary("Revision-check expected document checklist edits")
            .Produces<CaseRecord>()
            .Produces<ApiError>(409)
            .WithOperationsScopedAccess();
    }
}
