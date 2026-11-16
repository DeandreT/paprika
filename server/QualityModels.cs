namespace paprika;

public sealed record QualityPolicy(
    string Mode = "manual",
    bool AutoApprove = false,
    double Threshold = .95,
    double SampleRate = .1,
    string SampleSeed = "quality-v1",
    int MinimumHeldOut = 50,
    double MaximumErrorUpperBound = .1,
    bool SyntheticEvaluation = false,
    bool RequireIndependentReviewer = true
)
{
    public static string? Validate(QualityPolicy? policy)
    {
        try
        {
            QualityValidation.Validate(policy, null, false);
            return null;
        }
        catch (WorkflowException ex)
        {
            return ex.Message;
        }
    }
}

public sealed record QualityItem(string Scope, string Id, string Reason);

public sealed record QualityDecision(
    string PacketId,
    int Revision,
    string Mode,
    bool AutoApprove,
    bool Sampled,
    string? ModelHash,
    QualityItem[] Items,
    DateTimeOffset At
);

public sealed record QualityQaTask(
    string Id,
    string PacketId,
    int PacketRevision,
    int Revision,
    string Status,
    string PrimaryActor,
    string? Reviewer,
    string? Verdict,
    string? Note,
    DateTimeOffset CreatedAt,
    Packet Snapshot
);

public sealed record QualityQaRequest(int Revision, string Verdict, string Note = "");

public sealed record QualityQaTaskResponse(
    string Id,
    string PacketId,
    int PacketRevision,
    int Revision,
    string Status,
    string PrimaryActor,
    string? Reviewer,
    string? Verdict,
    string? Note,
    DateTimeOffset CreatedAt,
    PacketResponse Snapshot
)
{
    public static QualityQaTaskResponse From(QualityQaTask task) =>
        new(
            task.Id,
            task.PacketId,
            task.PacketRevision,
            task.Revision,
            task.Status,
            task.PrimaryActor,
            task.Reviewer,
            task.Verdict,
            task.Note,
            task.CreatedAt,
            PacketResponse.From(task.Snapshot)
        );
}

public sealed record QualityPacketResponse(
    QualityDecision? Decision,
    QualityQaTaskResponse[] Tasks
);

public sealed record QualityValidationRequest(QualityPolicy? Quality, TrainingModelRef? Training);

public static class QualityValidation
{
    public static void Validate(
        QualityPolicy? policy,
        TrainingModelRef? training,
        bool validateArtifact = true
    )
    {
        if (policy is null)
            return;
        if (
            policy.Mode is not ("manual" or "selective")
            || !double.IsFinite(policy.Threshold)
            || policy.Threshold < .5
            || policy.Threshold > 1
            || !double.IsFinite(policy.SampleRate)
            || policy.SampleRate < 0
            || policy.SampleRate > 1
            || string.IsNullOrWhiteSpace(policy.SampleSeed)
            || policy.SampleSeed.Length > 200
            || policy.MinimumHeldOut is < 1 or > 1000000
            || !double.IsFinite(policy.MaximumErrorUpperBound)
            || policy.MaximumErrorUpperBound <= 0
            || policy.MaximumErrorUpperBound > .25
        )
            throw new WorkflowException(
                400,
                "Quality mode, threshold, sample rate, seed or evaluation criteria are invalid."
            );
        if (!policy.AutoApprove)
            return;
        if (policy.Mode != "selective" || policy.SyntheticEvaluation)
            throw new WorkflowException(
                400,
                "Automation requires selective mode and cannot use synthetic evaluation policy."
            );
        if (!policy.RequireIndependentReviewer || policy.SampleRate <= 0)
            throw new WorkflowException(
                400,
                "Automated approval requires independent QA sampling above zero."
            );
        if (!validateArtifact)
            return;
        if (training is null || !training.Approved)
            throw new WorkflowException(
                400,
                "Automation requires an explicitly approved frozen trained model."
            );
        var model = TrainingStore.Resolve(training);
        var evaluation = model.Evaluation;
        if (
            model.Synthetic
            || evaluation.Status != "held-out"
            || evaluation.Examples < policy.MinimumHeldOut
            || evaluation.ErrorUpperBound is null
            || evaluation.ErrorUpperBound > policy.MaximumErrorUpperBound
            || policy.Threshold != evaluation.Threshold
        )
            throw new WorkflowException(
                400,
                "The model does not meet the real-data held-out sample, threshold and error-bound criteria."
            );
        if (policy.SampleRate <= 0)
            throw new WorkflowException(
                400,
                "Automated approval requires independent QA sampling above zero."
            );
    }
}
