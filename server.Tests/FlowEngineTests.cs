using PaperworkClassifier;
using Xunit;

namespace PaperworkClassifier.Tests;

public sealed class FlowEngineTests
{
    [Fact]
    public void ArbitraryExecutableCodePropertiesAreRejectedByTypedDefinitionContract()
    {
        Assert.Throws<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<FlowDefinition>(
                """{"name":"bad","blocks":[{"id":"a","kind":"Transform","dependsOn":[],"operations":[{"kind":"Trim","source":"x","target":"y"}],"code":"arbitrary()"}]}""",
                Contract.Json
            )
        );
    }

    [Fact]
    public void EditingReusableLibraryDoesNotRewriteAcceptedEmbeddedSnapshot()
    {
        using var db = new CaseFlowDatabase();
        var library = new FlowLibrary(db.Store);
        var definition = new FlowDefinition(
            "original",
            [
                new(
                    "normalize",
                    FlowBlockKind.Transform,
                    [],
                    Operations: [new(FlowTransformKind.Trim, "value", "value")]
                ),
            ]
        );
        var saved = library.Save("shared-flow", new(0, definition), "administrator");
        var workflow = db.Store.Workflows.Clone(
            "workflow",
            "general-logistics",
            1,
            1,
            "frozen flow"
        );
        workflow = db.Store.Workflows.Edit(
            "workflow",
            workflow.Id,
            1,
            workflow with
            {
                Workflow = workflow.Workflow! with
                {
                    Capabilities = new(Flow: new(saved.Definition)),
                },
            }
        );
        workflow = db.Store.Workflows.Publish("workflow", workflow.Id, 1, workflow.Revision);
        var accepted = db.Store.Accept(new Packet(), null, new(workflow.Id, 1)).Packet!;
        var originalHash = accepted.EffectiveWorkflow.Hash;
        library.Save(
            saved.Id,
            new(saved.Revision, definition with { Name = "changed" }),
            "administrator"
        );
        var restored = db.Store.Get(accepted.Id)!;
        Assert.Equal("original", restored.EffectiveWorkflow.Capabilities!.Flow!.Definition.Name);
        Assert.Equal(originalHash, restored.EffectiveWorkflow.Hash);
        Assert.Equal("changed", Assert.Single(library.List()).Definition.Name);
        Assert.Equal(
            409,
            Assert
                .Throws<WorkflowException>(() =>
                    library.Save(saved.Id, new(saved.Revision, definition), "administrator")
                )
                .Code
        );
    }

    [Fact]
    public void InvalidCyclesForwardReferencesUnsupportedKindsAndCodeAreRejected()
    {
        Assert.Throws<WorkflowException>(() =>
            FlowValidator.Validate(
                new(
                    "cycle",
                    [
                        new("a", FlowBlockKind.Reference, ["b"]),
                        new("b", FlowBlockKind.Output, ["a"]),
                    ]
                )
            )
        );
        Assert.Throws<WorkflowException>(() =>
            FlowValidator.Validate(new("code", [new("a", (FlowBlockKind)999, [])]))
        );
        Assert.Throws<WorkflowException>(() =>
            FlowValidator.Validate(
                new(
                    "invalid",
                    [
                        new(
                            "a",
                            FlowBlockKind.Output,
                            [],
                            Operations: [new(FlowTransformKind.Trim, "x", "y")]
                        ),
                    ]
                )
            )
        );
        Assert.Throws<WorkflowException>(() =>
            FlowValidator.Validate(
                new(
                    "invalid",
                    [
                        new(
                            "a",
                            FlowBlockKind.Validate,
                            [],
                            Operations: [new(FlowTransformKind.Uppercase, "x", "y")]
                        ),
                    ]
                )
            )
        );
    }

    [Fact]
    public async Task ConditionalRoutesUseAncestorFieldAndVerifiedReferenceResults()
    {
        using var db = new CaseFlowDatabase();
        var packet = db.Packet("bol");
        packet.Status = "approved";
        db.Store.Save(packet);
        var engine = new FlowEngine(db.Store);
        var called = new List<string>();
        var definition = new FlowDefinition(
            "route",
            [
                new("reference", FlowBlockKind.Reference, []),
                new(
                    "extract",
                    FlowBlockKind.Extraction,
                    ["reference"],
                    new(FlowConditionKind.DocumentType, "bol")
                ),
                new(
                    "yes",
                    FlowBlockKind.Output,
                    ["extract"],
                    new(FlowConditionKind.FieldEquals, "ready", "yes")
                ),
                new(
                    "no",
                    FlowBlockKind.Output,
                    ["extract"],
                    new(FlowConditionKind.FieldEquals, "ready", "yes", true)
                ),
                new(
                    "verified",
                    FlowBlockKind.Quality,
                    ["reference"],
                    new(FlowConditionKind.ReferenceVerified, "S123")
                ),
            ]
        );
        var handlers = new Dictionary<FlowBlockKind, FlowBlockHandler>
        {
            [FlowBlockKind.Reference] = (c, ct) =>
                Task.FromResult(new FlowBlockResult([], ["S123"])),
            [FlowBlockKind.Extraction] = (c, ct) =>
                Task.FromResult(new FlowBlockResult(new() { ["ready"] = "yes" }, [])),
            [FlowBlockKind.Output] = (c, ct) =>
            {
                called.Add(c.Block.Id);
                return Task.FromResult(FlowBlockResult.Empty);
            },
            [FlowBlockKind.Quality] = (c, ct) =>
            {
                called.Add(c.Block.Id);
                return Task.FromResult(FlowBlockResult.Empty);
            },
        };
        var run = await engine.ExecuteAsync(packet, new(definition), handlers, default);
        Assert.Equal(new[] { "yes", "verified" }, called);
        Assert.Equal("skipped", run!.Executions.Single(e => e.BlockId == "no").Status);
        await new FlowEngine(new Store(db.Configuration)).ExecuteAsync(
            packet,
            new(definition),
            handlers,
            default
        );
        Assert.Equal(2, called.Count);
    }

    [Fact]
    public async Task FailedBlockNeedsExplicitRetryAndRestartDoesNotRepeatCompletedSideEffects()
    {
        using var db = new CaseFlowDatabase();
        var packet = db.Packet();
        packet.Status = "approved";
        db.Store.Save(packet);
        var engine = new FlowEngine(db.Store);
        var definition = new FlowDefinition(
            "retry",
            [
                new("first", FlowBlockKind.Reference, []),
                new("second", FlowBlockKind.Output, ["first"]),
            ]
        );
        int firstCalls = 0,
            attempts = 0;
        var keys = new List<string>();
        var handlers = new Dictionary<FlowBlockKind, FlowBlockHandler>
        {
            [FlowBlockKind.Reference] = (c, ct) =>
            {
                firstCalls++;
                return Task.FromResult(new FlowBlockResult(new() { ["ref"] = "stable" }, []));
            },
            [FlowBlockKind.Output] = (c, ct) =>
            {
                keys.Add(c.IdempotencyKey);
                Assert.Equal("stable", c.Fields["ref"]);
                if (++attempts == 1)
                    throw new InvalidOperationException("fixture");
                return Task.FromResult(FlowBlockResult.Empty);
            },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.ExecuteAsync(packet, new(definition), handlers, default)
        );
        await Assert.ThrowsAsync<WorkflowException>(() =>
            engine.ExecuteAsync(packet, new(definition), handlers, default)
        );
        engine.Retry(packet, new(packet.Revision, "second"));
        packet = db.Store.Get(packet.Id)!;
        var run = await new FlowEngine(new Store(db.Configuration)).ExecuteAsync(
            packet,
            new(definition),
            handlers,
            default
        );
        Assert.Equal(1, firstCalls);
        Assert.Equal(2, attempts);
        Assert.Single(keys.Distinct());
        Assert.All(run!.Executions, e => Assert.Equal("completed", e.Status));
        Assert.Equal(2, run.Executions.Single(e => e.BlockId == "second").Attempt);
    }

    [Fact]
    public async Task AcceptedDefinitionCannotChangeUnderTheSameSnapshotHash()
    {
        using var db = new CaseFlowDatabase();
        var packet = db.Packet();
        var engine = new FlowEngine(db.Store);
        var definition = new FlowDefinition("frozen", [new("a", FlowBlockKind.Reference, [])]);
        var handlers = new Dictionary<FlowBlockKind, FlowBlockHandler>
        {
            [FlowBlockKind.Reference] = (c, ct) => Task.FromResult(FlowBlockResult.Empty),
        };
        await engine.ExecuteAsync(packet, new(definition), handlers, default);
        definition.Blocks[0] = new("replacement", FlowBlockKind.Reference, []);
        await Assert.ThrowsAsync<WorkflowException>(() =>
            engine.ExecuteAsync(packet, new(definition), handlers, default)
        );
        Assert.Equal("a", Assert.Single(engine.Get(packet)!.Definition.Blocks).Id);
    }

    [Fact]
    public async Task ReusableSafeBlockPerformsRealTypedTransformationAndValidation()
    {
        using var db = new CaseFlowDatabase();
        var packet = db.Packet();
        var engine = new FlowEngine(db.Store);
        var definition = new FlowDefinition(
            "safe",
            [
                new("extract", FlowBlockKind.Extraction, []),
                new("normalize", FlowBlockKind.Transform, ["extract"], TemplateId: "uppercase"),
                new(
                    "validate",
                    FlowBlockKind.Validate,
                    ["normalize"],
                    Operations: [new(FlowTransformKind.RequireValue, "normalized", "checked")]
                ),
            ],
            [
                new(
                    "uppercase",
                    FlowBlockKind.Transform,
                    [
                        new(FlowTransformKind.Trim, "raw", "trimmed"),
                        new(FlowTransformKind.Uppercase, "trimmed", "normalized"),
                    ]
                ),
            ]
        );
        var handlers = new Dictionary<FlowBlockKind, FlowBlockHandler>
        {
            [FlowBlockKind.Extraction] = (c, ct) =>
                Task.FromResult(new FlowBlockResult(new() { ["raw"] = " abc " }, [])),
        };
        var run = await engine.ExecuteAsync(packet, new(definition), handlers, default);
        Assert.Equal(
            "ABC",
            run!.Executions.Single(e => e.BlockId == "validate").Result!.Fields["checked"]
        );
    }

    [Fact]
    public async Task ConcurrentExecutorsDoNotInvokeASideEffectTwice()
    {
        using var db = new CaseFlowDatabase();
        var packet = db.Packet();
        packet.Status = "approved";
        db.Store.Save(packet);
        var engine = new FlowEngine(db.Store);
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        int calls = 0;
        var definition = new FlowDefinition("exclusive", [new("output", FlowBlockKind.Output, [])]);
        var handlers = new Dictionary<FlowBlockKind, FlowBlockHandler>
        {
            [FlowBlockKind.Output] = async (c, ct) =>
            {
                Interlocked.Increment(ref calls);
                entered.SetResult();
                await release.Task;
                return FlowBlockResult.Empty;
            },
        };
        var first = engine.ExecuteAsync(packet, new(definition), handlers, default);
        await entered.Task;
        await Assert.ThrowsAsync<WorkflowException>(() =>
            new FlowEngine(db.Store).ExecuteAsync(packet, new(definition), handlers, default)
        );
        release.SetResult();
        await first;
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ApprovalSuspendsOutputAndNewRevisionRecomputesAncestorResults()
    {
        using var db = new CaseFlowDatabase();
        var packet = db.Packet();
        var engine = new FlowEngine(db.Store);
        var definition = new FlowDefinition(
            "approval",
            [
                new("extract", FlowBlockKind.Extraction, []),
                new("output", FlowBlockKind.Output, ["extract"]),
            ]
        );
        int extractionCalls = 0,
            outputCalls = 0;
        string extracted = "old";
        string? observed = null;
        var handlers = new Dictionary<FlowBlockKind, FlowBlockHandler>
        {
            [FlowBlockKind.Extraction] = (c, ct) =>
            {
                extractionCalls++;
                return Task.FromResult(new FlowBlockResult(new() { ["value"] = extracted }, []));
            },
            [FlowBlockKind.Output] = (c, ct) =>
            {
                outputCalls++;
                observed = c.Fields["value"];
                return Task.FromResult(FlowBlockResult.Empty);
            },
        };
        var suspended = await engine.ExecuteAsync(packet, new(definition), handlers, default);
        Assert.Equal(
            "waitingApproval",
            suspended!.Executions.Single(e => e.BlockId == "output").Status
        );
        Assert.Equal(0, outputCalls);
        await engine.ExecuteAsync(packet, new(definition), handlers, default);
        Assert.Equal(1, extractionCalls);
        packet.Revision++;
        packet.Status = "approved";
        extracted = "corrected";
        db.Store.Save(packet);
        var approved = await engine.ExecuteAsync(packet, new(definition), handlers, default);
        Assert.Equal(2, extractionCalls);
        Assert.Equal(1, outputCalls);
        Assert.Equal("corrected", observed);
        Assert.Equal(2, approved!.PacketRevision);
        var original = packet.Revision;
        packet.Revision = 1;
        Assert.Equal(
            "waitingApproval",
            engine.Get(packet)!.Executions.Single(e => e.BlockId == "output").Status
        );
        packet.Revision = original;
    }
}
