namespace paprika;

public sealed class PlatformPipeline(
    Store store,
    TrainingStore training,
    QualityStore quality,
    ReferenceService references,
    FlowEngine flows,
    CaseStore cases,
    ExtractionAi? ai = null
)
{
    public async Task BeforeAssemblyAsync(Packet packet, CancellationToken ct)
    {
        training.Apply(packet);
        await RecognitionPlatform.ApplyAsync(store, packet, ct);
    }

    public async Task<QualityDecision> AfterAssemblyAsync(Packet packet, CancellationToken ct)
    {
        if (packet.EffectiveWorkflow.Capabilities?.Flow is not null)
            await flows.ExecuteAsync(packet, Handlers(), PrepareFlowExtractionAsync, ct);
        else
        {
            if (ai is not null)
                await ai.PrepareConfiguredAsync(packet, ct);
            ExtractionPlatform.RunConfigured(store, packet);
            await references.ProcessAsync(packet, ct);
            if (packet.EffectiveWorkflow.Capabilities?.Case is not null)
                cases.Collate(packet, VerifiedCaseReference(packet));
        }
        return quality.Route(packet, Blockers(packet));
    }

    public async Task AfterApprovalAsync(Packet packet, CancellationToken ct)
    {
        var current = store.Get(packet.Id);
        if (current is null || current.Status != "approved" || current.Revision != packet.Revision)
            return;
        // Approval requires exact coverage from existing evidence. Continuing an approved flow
        // must neither acquire new AI values nor bypass the approval check.
        ExtractionPlatform.RunConfigured(store, current);
        var extractionBlockers = ExtractionPlatform.Blockers(store, current);
        if (extractionBlockers.Length > 0)
            throw new WorkflowException(409, extractionBlockers[0]);
        if (current.EffectiveWorkflow.Capabilities?.Flow is not null)
            await flows.ExecuteAsync(current, Handlers(), ct);
        else
        {
            if (current.EffectiveWorkflow.Capabilities?.Case is not null)
                cases.Collate(current, VerifiedCaseReference(current));
            if (current.EffectiveWorkflow.Capabilities?.Output is { } output)
                SaveOutput(
                    current,
                    output,
                    $"approved-output:{current.Id}:{current.Revision}:{current.EffectiveWorkflow.Hash}"
                );
        }
    }

    private Task PrepareFlowExtractionAsync(Packet packet, CancellationToken ct)
    {
        if (
            packet.EffectiveWorkflow.Capabilities?.Extraction?.Definition is not { } definition
            || !ExtractionAi.Uses(definition)
        )
            return Task.CompletedTask;
        if (ai is null)
            throw new InvalidOperationException("AI extraction service is unavailable.");
        return ai.PrepareConfiguredAsync(packet, ct);
    }

    private IReadOnlyDictionary<FlowBlockKind, FlowBlockHandler> Handlers() =>
        new Dictionary<FlowBlockKind, FlowBlockHandler>
        {
            [FlowBlockKind.Extraction] = (context, _) =>
            {
                ExtractionPlatform.RunConfigured(store, context.Packet);
                var run = CurrentExtraction(context.Packet);
                var fields = new Dictionary<string, string>();
                if (run is not null)
                {
                    // Quote grounding verifies provenance, not that the model picked the
                    // right text. Only expose an AI value to automation after review.
                    var unreviewed = run
                        .Definition.Fields.Where(f => f.Description is not null)
                        .Select(f => f.Name)
                        .ToHashSet(StringComparer.Ordinal);
                    foreach (
                        var group in run
                            .Results.SelectMany(r => r.Fields)
                            .Where(f =>
                                f.Value is not null
                                && (
                                    f.Status == "corrected"
                                    || f.Status == "extracted"
                                        && (
                                            context.Packet.Status == "approved"
                                            || !unreviewed.Contains(f.Name)
                                        )
                                )
                            )
                            .GroupBy(f => f.Name)
                    )
                    {
                        var values = group.Select(f => f.Value!).Distinct().ToArray();
                        if (values.Length == 1)
                            fields[group.Key] = values[0];
                    }
                }
                return Task.FromResult(new FlowBlockResult(fields, []));
            },
            [FlowBlockKind.Reference] = async (context, ct) =>
            {
                await references.ProcessAsync(context.Packet, ct);
                var verified = CurrentReferences(context.Packet)
                    .Where(r => r.Status == "verified" && !r.Synthetic)
                    .Select(r => r.Reference)
                    .Distinct()
                    .ToArray();
                return new(
                    new()
                    {
                        ["references.verified"] = verified.Length.ToString(
                            System.Globalization.CultureInfo.InvariantCulture
                        ),
                    },
                    verified
                );
            },
            [FlowBlockKind.Quality] = (context, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var decision = quality.Route(context.Packet, Blockers(context.Packet));
                return Task.FromResult(
                    new FlowBlockResult(
                        new()
                        {
                            ["quality.route"] = decision.AutoApprove ? "automatic" : "review",
                            ["quality.items"] = decision.Items.Length.ToString(
                                System.Globalization.CultureInfo.InvariantCulture
                            ),
                        },
                        []
                    )
                );
            },
            [FlowBlockKind.Case] = (context, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var proposal = cases.Collate(context.Packet, VerifiedCaseReference(context.Packet));
                return Task.FromResult(
                    new FlowBlockResult(
                        new()
                        {
                            ["case.reference"] = proposal.BusinessReference,
                            ["case.collated"] = proposal.AutoCollated ? "true" : "false",
                        },
                        []
                    )
                );
            },
            [FlowBlockKind.Output] = (context, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (context.Packet.Status != "approved")
                    throw new FlowAwaitingApprovalException();
                var policy = context.Packet.EffectiveWorkflow.Capabilities?.Output
                    ?? throw new WorkflowException(
                        400,
                        "An output block requires an output policy."
                    );
                var artifact = SaveOutput(context.Packet, policy, context.IdempotencyKey);
                return Task.FromResult(
                    new FlowBlockResult(
                        new() { ["output.id"] = artifact.Id, ["output.sha256"] = artifact.Sha256 },
                        []
                    )
                );
            },
        };

    private OutputArtifact SaveOutput(Packet packet, OutputPolicy policy, string key)
    {
        // The output service binds the idempotency key to exact request, revision and source hashes.
        ExtractionStorage.Initialize(store);
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        var current = Store.Read(c, tx, packet.Id);
        if (current is null || current.Status != "approved" || current.Revision != packet.Revision)
            throw new WorkflowException(409, "Approval changed before output generation.");
        PlatformReleaseGate.Ensure(c, tx, current, new ConnectorCatalog(store.Configuration));
        var artifact = OutputPlatform.Save(
            store,
            current,
            new(current.Revision, policy.Format, policy.RedactValues),
            c,
            tx,
            key
        );
        tx.Commit();
        return artifact;
    }

    private ReferenceResult[] CurrentReferences(Packet packet) =>
        references
            .Results(packet.Id)
            .Where(r =>
                r.Revision == packet.Revision
                && r.WorkflowHash == packet.EffectiveWorkflow.Hash
                && packet.Documents.Any(d =>
                    d.Id == r.DocumentId && d.BusinessReference == r.Reference
                )
            )
            .ToArray();

    private CaseVerifiedReference? VerifiedCaseReference(Packet packet)
    {
        var matches = CurrentReferences(packet)
            .Where(r => r.Status == "verified" && !r.Synthetic)
            .ToArray();
        var values = matches
            .Select(r => r.Reference)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return
            values.Length == 1
            && matches.Select(r => r.DocumentId).Distinct().Count() == packet.Documents.Count
            ? new(values[0], string.Join(";", matches.Select(r => r.ResultHash)))
            : null;
    }

    private ExtractionRun? CurrentExtraction(Packet packet) =>
        ExtractionPlatform.Current(
            store,
            packet,
            packet.EffectiveWorkflow.Capabilities?.Extraction?.Sha256
        );

    private string[] Blockers(Packet packet)
    {
        var blockers = new List<string>();
        if (
            packet.EffectiveWorkflow.Capabilities?.Reference?.RequireVerified == true
            && !references.IsVerified(packet)
        )
            blockers.Add("Required business references have not been verified for this revision.");
        if (CurrentReferences(packet).Any(r => r.Synthetic))
            blockers.Add("Synthetic reference evidence requires manual review.");
        blockers.AddRange(ExtractionPlatform.Blockers(store, packet));
        return blockers.Distinct().ToArray();
    }
}
