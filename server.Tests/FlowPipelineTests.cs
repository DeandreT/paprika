using System.Text.Json;
using PaperworkClassifier;
using Xunit;

namespace PaperworkClassifier.Tests;

public sealed class FlowPipelineTests
{
    [Fact]
    public async Task RealPipelineExtractsBranchesWaitsForApprovalAndGeneratesOneExactRevisionOutput()
    {
        using var db = new CaseFlowDatabase();
        var packet = db.Packet();
        var source = packet.Sources.Single();
        source.Extension = ".pdf";
        Directory.CreateDirectory(Path.Combine(db.Store.Root, packet.Id));
        Pdfs.Synthetic(db.Store.SourcePath(packet, source), ["Shipment: shipment-123"]);
        source.Sha256 = Pdfs.SourceHash(db.Store.SourcePath(packet, source));
        packet.Pages.Single().Text = "Shipment: shipment-123";
        var extraction = new ExtractionDefinition(
            "flow-shipment",
            1,
            [new("shipment", Pattern: "Shipment: (?<value>[a-z0-9-]+)", Required: true)],
            []
        );
        var flow = new FlowDefinition(
            "Shipment flow",
            [
                new("extract", FlowBlockKind.Extraction, []),
                new(
                    "normalize",
                    FlowBlockKind.Transform,
                    ["extract"],
                    Operations: [new(FlowTransformKind.Uppercase, "shipment", "normalized")]
                ),
                new(
                    "case",
                    FlowBlockKind.Case,
                    ["normalize"],
                    new(FlowConditionKind.DocumentType, "bol")
                ),
                new("quality", FlowBlockKind.Quality, ["case"]),
                new(
                    "other-route",
                    FlowBlockKind.Validate,
                    ["normalize"],
                    new(FlowConditionKind.FieldEquals, "normalized", "OTHER"),
                    Operations: [new(FlowTransformKind.RequireValue, "missing", "checked")]
                ),
                new(
                    "output",
                    FlowBlockKind.Output,
                    ["quality"],
                    new(FlowConditionKind.FieldEquals, "normalized", "SHIPMENT-123")
                ),
            ]
        );
        packet.Workflow = packet.EffectiveWorkflow with
        {
            Capabilities = new(
                Flow: new(flow),
                Case: new("customer", ["bol", "pod"], true),
                Extraction: new(
                    extraction.Id,
                    extraction.Version,
                    ExtractionStorage.Hash(extraction),
                    extraction
                ),
                Output: new()
            ),
        };
        db.Store.Save(packet);
        var training = new TrainingStore(db.Store);
        var quality = new QualityStore(db.Store, training);
        var references = new ReferenceService(db.Store, new ConnectorCatalog(db.Configuration));
        var engine = new FlowEngine(db.Store);
        var cases = new CaseStore(db.Store);
        var pipeline = new PlatformPipeline(db.Store, training, quality, references, engine, cases);

        var decision = await pipeline.AfterAssemblyAsync(packet, default);
        Assert.False(decision.AutoApprove);
        var before = engine.Get(packet)!;
        Assert.Equal(
            "SHIPMENT-123",
            before.Executions.Single(e => e.BlockId == "normalize").Result!.Fields["normalized"]
        );
        Assert.Equal("skipped", before.Executions.Single(e => e.BlockId == "other-route").Status);
        Assert.Equal(
            "waitingApproval",
            before.Executions.Single(e => e.BlockId == "output").Status
        );
        Assert.Empty(ExtractionStorage.List<OutputArtifact>(db.Store, "output"));
        Assert.False(cases.Proposal(packet.Id)!.AutoCollated);

        // Approval creates a new exact revision. The continuation uses the real main pipeline, not custom callbacks.
        var reviewed = db.Store.Review(packet.Id, new(packet.Revision, packet.Documents, true));
        Assert.Equal(200, reviewed.Code);
        var approved = reviewed.Packet!;
        await ContinuePlatformFlowHandler.Handle(
            new(approved.Id, approved.Revision),
            db.Store,
            pipeline,
            default
        );
        var output = Assert.Single(ExtractionStorage.List<OutputArtifact>(db.Store, "output"));
        Assert.Equal(approved.Revision, output.Revision);
        Assert.False(output.Preview);
        Assert.Equal(
            "completed",
            engine.Get(approved)!.Executions.Single(e => e.BlockId == "output").Status
        );
        using var json = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                OutputPlatform.PathFor(db.Store, output.Id, output.Extension)
            )
        );
        Assert.Contains("shipment-123", json.RootElement.GetRawText());
        await ContinuePlatformFlowHandler.Handle(
            new(approved.Id, approved.Revision),
            db.Store,
            pipeline,
            default
        );
        Assert.Single(ExtractionStorage.List<OutputArtifact>(db.Store, "output"));
        await ContinuePlatformFlowHandler.Handle(
            new(approved.Id, approved.Revision - 1),
            db.Store,
            pipeline,
            default
        );
        Assert.Single(ExtractionStorage.List<OutputArtifact>(db.Store, "output"));
    }
}
