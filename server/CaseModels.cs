namespace paprika;

public sealed record CasePolicy(
    string CustomerId,
    string[] ExpectedDocumentTypes,
    bool AutoCollate = false
);

public sealed record CaseProvenance(
    string SourceId,
    string SourceName,
    string? Sha256,
    string[] PageIds
);

public sealed record CaseMember(
    string PacketId,
    int PacketRevision,
    string DocumentId,
    string DocumentType,
    string WorkflowHash,
    CaseProvenance[] Sources,
    DateTimeOffset AddedAt,
    string AddedBy
);

public sealed record CaseAudit(DateTimeOffset At, string Actor, string Action, int Revision);

public sealed record CaseChecklist(string DocumentType, int Count, bool Present);

public sealed record CaseRecord(
    string Id,
    int Revision,
    string CustomerId,
    string WorkflowId,
    bool TestRun,
    string BusinessReference,
    bool ReferenceVerified,
    string? VerificationEvidence,
    string[] ExpectedDocumentTypes,
    CaseMember[] Members,
    CaseAudit[] Audit
)
{
    public CaseChecklist[] Checklist =>
        ExpectedDocumentTypes
            .Select(t => new CaseChecklist(
                t,
                Members.Count(m => m.DocumentType == t),
                Members.Any(m => m.DocumentType == t)
            ))
            .ToArray();
    public bool Complete => Checklist.All(c => c.Present);
}

public sealed record CaseCreateRequest(
    string PacketId,
    int PacketRevision,
    string BusinessReference,
    string[] DocumentIds
);

public sealed record CaseMembershipRequest(
    int Revision,
    string PacketId,
    int PacketRevision,
    string[] DocumentIds,
    bool Refresh = false
);

public sealed record CaseEditRequest(int Revision, string[] ExpectedDocumentTypes);

public sealed record CaseCollationProposal(
    string PacketId,
    string BusinessReference,
    string[] CandidateCaseIds,
    bool AutoCollated,
    string Reason
);

public sealed record CaseVerifiedReference(string Value, string Evidence);

public static class CaseValidator
{
    public static void Validate(CasePolicy policy)
    {
        if (
            policy is null
            || string.IsNullOrWhiteSpace(policy.CustomerId)
            || policy.CustomerId.Length > 100
            || policy.ExpectedDocumentTypes is null
            || policy.ExpectedDocumentTypes.Length is < 1 or > 64
            || policy.ExpectedDocumentTypes.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 80)
            || policy.ExpectedDocumentTypes.Distinct().Count()
                != policy.ExpectedDocumentTypes.Length
        )
            throw new WorkflowException(
                400,
                "A customer scope and unique bounded expected document types are required."
            );
    }
}
