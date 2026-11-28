using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace paprika.Tests;

public sealed class PagePredictionEvidenceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "paprika-teacher-evidence-" + Guid.NewGuid().ToString("N")
    );
    private readonly Store store;
    private readonly Settings settings;
    private readonly TrainingStore training;
    private readonly QualityStore quality;
    private readonly MockClassifiers network = new();
    private readonly PacketProcessor processor;
    private readonly byte[] bytes;
    private const string First =
        "First synthetic invoice with a payable balance due for this generated fixture.";
    private const string Second =
        "Second synthetic invoice with a payable balance due for this generated fixture.";

    public PagePredictionEvidenceTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["paprika:DataDirectory"] = root,
                    ["paprika:Mode"] = "live",
                    ["paprika:JevModel"] = "jev-test",
                    ["paprika:JevApiKey"] = "test-only",
                    ["paprika:OpenAiModel"] = "gpt-test",
                    ["paprika:OpenAiApiKey"] = "test-only",
                }
            )
            .Build();
        store = new(config);
        settings = new(config);
        training = new TrainingStore(store);
        quality = new QualityStore(store, training);
        var platform = new PlatformPipeline(
            store,
            training,
            quality,
            new ReferenceService(store, new ConnectorCatalog(config)),
            new FlowEngine(store),
            new CaseStore(store)
        );
        processor = new(store, new Providers(settings, store, network), new(), platform, quality);
        var path = Path.Combine(root, "example.pdf");
        Pdfs.Synthetic(path, [First, Second]);
        bytes = File.ReadAllBytes(path);
    }

    private Packet Accept(
        string model,
        bool overlay = false,
        int release = 1,
        string[]? ocr = null,
        bool differentBytes = false
    )
    {
        TrainingModelRef? local = null;
        if (overlay)
        {
            var artifact = TrainingClassifier.Fit(
                [
                    new("bill", "invoice payable balance due", "invoice"),
                    new("delivery", "delivery signed received driver", "pod"),
                ],
                .95
            );
            var payload = JsonSerializer.Serialize(artifact, Contract.Json);
            local = new("local-test-model", TrainingClassifier.Hash(payload), payload);
        }
        var packet = new Packet
        {
            Mode = "live",
            TestRun = true,
            Workflow = WorkflowRegistry.Legacy("live", model) with
            {
                ReleaseVersion = release,
                Capabilities = overlay ? new(Training: local) : null,
            },
        };
        var source = new Source
        {
            Name = "fixture.pdf",
            Extension = ".pdf",
            PageCount = 2,
        };
        Directory.CreateDirectory(Path.Combine(root, packet.Id));
        var path = store.SourcePath(packet, source);
        File.WriteAllBytes(path, differentBytes ? [.. bytes, (byte)'\n'] : bytes);
        source.Sha256 = Pdfs.SourceHash(path);
        if (ocr is not null)
            source.OcrJson = JsonSerializer.Serialize(new { openAi = ocr });
        packet.Sources.Add(source);
        Assert.Equal(200, store.Accept(packet, null).Code);
        return packet;
    }

    private PagePrediction[] Read(Packet packet)
    {
        using var c = new SqliteConnection(store.ConnectionString);
        c.Open();
        using var tx = c.BeginTransaction();
        return PagePredictionEvidence.Read(c, tx, packet);
    }

    [Fact]
    public async Task NormalLiveRetryKeepsAppliedTeacherThroughLocalOverlayAndLaterHumanReview()
    {
        var packet = Accept("gpt-test", overlay: true);
        network.FailSecondPageOnce = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => processor.Process(packet.Id, default));
        var checkpoint = Assert.Single(Read(store.Get(packet.Id)!));
        Assert.Equal("pod", checkpoint.Label);
        Assert.Equal("gpt-served-build", checkpoint.Model);

        await processor.Process(packet.Id, default);
        var ready = store.Get(packet.Id)!;
        Assert.Equal("review", ready.Status);
        Assert.All(ready.Pages, p => Assert.Equal("invoice", p.PredictedType));
        foreach (var document in ready.Documents)
            document.Type = "receipt";
        var reviewed = store.Review(ready.Id, new(ready.Revision, ready.Documents, false)).Packet!;
        Assert.Equal(2, reviewed.Revision);
        var predictions = Read(reviewed);
        Assert.Equal(2, predictions.Length);
        Assert.All(predictions, p =>
        {
            Assert.Equal("pod", p.Label);
            Assert.Equal("openai", p.Provider);
            Assert.Equal("gpt-test", p.RequestedModel);
            Assert.Equal(1, p.PredictionRevision);
            Assert.Equal(ready.Workflow!.Hash, p.WorkflowHash);
            Assert.Equal("fixture.pdf", p.SourceName);
            Assert.Equal(ready.Sources.Single().Sha256, p.SourceGroup);
            Assert.Equal(ready.Pages.Single(page => page.Id == p.PageId).Text, p.Text);
            Assert.Contains("receipt", p.Labels);
            Assert.DoesNotContain("unknown", p.Labels);
            Assert.True(p.Synthetic);
        });
        Assert.Equal(checkpoint.PageId, predictions[0].PageId);
        Assert.Equal(checkpoint.Model, predictions[0].Model);
        Assert.Null(predictions[1].Model); // The second response omitted the model and score.
        Assert.Null(predictions[1].Confidence);
        Assert.Equal(3, network.Calls); // First, transient failure, and only the outstanding page.
    }

    [Fact]
    public async Task OnlyIndependentTeachersOfTheSamePageContextAndReleaseCompare()
    {
        async Task<PagePrediction[]> Run(
            string model,
            int release = 1,
            string[]? ocr = null,
            bool differentBytes = false
        )
        {
            var packet = Accept(model, release: release, ocr: ocr, differentBytes: differentBytes);
            await processor.Process(packet.Id, default);
            return Read(store.Get(packet.Id)!);
        }
        var first = await Run("gpt-test");
        var another = await Run("jev-test");
        var sameTeacher = await Run("gpt-test");
        var otherRelease = await Run("jev-test", release: 2);
        var changedOcr = await Run(
            "jev-test",
            ocr: ["A different previous-page transcription.", first[1].Text]
        );
        var changedSource = await Run("jev-test", differentBytes: true);
        Assert.Equal("single", PagePredictionEvidence.Compare(first[0], first));
        Assert.Equal("single", PagePredictionEvidence.Compare(first[0], sameTeacher));
        Assert.Equal(
            "single",
            PagePredictionEvidence.Compare(
                first[1],
                [sameTeacher[1] with { RequestedModel = "different-alias" }]
            )
        );
        Assert.Equal("single", PagePredictionEvidence.Compare(first[0], [another[1]]));
        Assert.Equal("single", PagePredictionEvidence.Compare(first[0], otherRelease));
        Assert.Equal(first[0].Text, changedSource[0].Text);
        Assert.Equal("single", PagePredictionEvidence.Compare(first[0], changedSource));
        Assert.Equal(first[1].Text, changedOcr[1].Text);
        Assert.Equal("single", PagePredictionEvidence.Compare(first[1], changedOcr));
        Assert.Equal("disagreement", PagePredictionEvidence.Compare(first[0], another));
        Assert.Null(first[1].Model);
        Assert.Null(another[1].Model);
        Assert.Equal("agreement", PagePredictionEvidence.Compare(first[1], another));
        Assert.Equal(12, network.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HistoricalCompletedArtifactsDoNotAttributeOverlaysOrGuessTheProvider(
        bool overlay
    )
    {
        var packet = Accept("gpt-test", overlay);
        await processor.Process(packet.Id, default);
        var ready = store.Get(packet.Id)!;
        // Re-create the historical storage layout: only the original provider responses
        // and the post-assembly "predicted" snapshot existed before per-page receipts.
        RemoveReceipts(packet, "page-prediction-v1-*");
        var recovered = Read(ready);
        Assert.Equal(2, recovered.Length);
        Assert.All(recovered, p =>
        {
            Assert.Equal("pod", p.Label);
            Assert.Null(p.ComparisonKey);
            Assert.Equal("openai", p.Provider);
        });
        Assert.Equal("gpt-served-build", recovered[0].Model);
        Assert.Null(recovered[1].Model);
        RemoveReceipts(packet, "classification-*");
        var unattributed = Read(ready);
        if (overlay)
            Assert.Empty(unattributed);
        else
        {
            Assert.Equal(2, unattributed.Length);
            Assert.All(unattributed, p =>
            {
                Assert.Equal("pod", p.Label);
                Assert.Null(p.Provider);
                Assert.Null(p.Model);
                Assert.Null(p.RequestedModel);
                Assert.Null(p.ComparisonKey);
            });
        }
        RemoveReceipts(packet, "predicted-*");
        Assert.Empty(Read(ready)); // Current Page.PredictedType is never a fallback.
        Assert.Equal(2, network.Calls);
    }

    [Fact]
    public async Task DemoAndSimulationHaveNoTeacherReceipts()
    {
        var demo = Worker.Demo(store);
        await processor.Process(demo.Id, default);
        Assert.Empty(Read(store.Get(demo.Id)!));
        var simulation = store.Reprocess(demo.Id, demo.Revision, settings).Packet!;
        await processor.Process(simulation.Id, default);
        Assert.Empty(Read(store.Get(simulation.Id)!));
        using var c = new SqliteConnection(store.ConnectionString);
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM artifacts WHERE name GLOB 'page-prediction-v1-*'";
        Assert.Equal(0L, q.ExecuteScalar());
        Assert.Equal(0, network.Calls);
    }

    [Fact]
    public async Task LiveReprocessingKeepsSyntheticSourceLineageAcrossModes()
    {
        var demo = Worker.Demo(store);
        await processor.Process(demo.Id, default);
        var dataset = training.CreateDataset(new("Approved reviews"));
        var catalog = store.Workflows.Catalog();
        var original = catalog.Entries.Single(e =>
            e.Kind == "workflow"
            && e.Id == catalog.DefaultWorkflow.Id
            && e.Version == catalog.DefaultWorkflow.Version
        );
        var draft = store.Workflows.Create(
            new(
                "workflow",
                null,
                "Live review",
                "Demo lineage fixture",
                Workflow: original.Workflow! with
                {
                    Capabilities = new(ReviewCollection: new(dataset.Id)),
                }
            )
        );
        var workflow = store.Workflows.Publish(draft.Kind, draft.Id, draft.Version, draft.Revision);
        var child = store
            .Reprocess(demo.Id, demo.Revision, settings, new(workflow.Id, workflow.Version))
            .Packet!;
        Assert.Equal("live", child.Mode);
        Assert.False(child.TestRun);
        await processor.Process(child.Id, default);
        var ready = store.Get(child.Id)!;
        var predictions = Read(ready);
        Assert.Equal(demo.Pages.Count, predictions.Length);
        Assert.All(predictions, p =>
        {
            Assert.True(p.Synthetic);
            Assert.Equal("jev", p.Provider);
            Assert.Equal(demo.Sources.Single().Sha256, p.SourceGroup);
        });
        Assert.Equal(demo.Pages.Count, network.Calls);

        foreach (var doc in ready.Documents)
            doc.BusinessReference = "TEST-1";
        var approval = store.Review(
            ready.Id,
            new(ready.Revision, ready.Documents, true),
            "reviewer",
            quality: quality
        );
        Assert.Equal(200, approval.Code);
        Assert.Equal("approved", approval.Packet!.Status);
        using var db = new SqliteConnection(store.ConnectionString);
        db.Open();
        using var q = db.CreateCommand();
        q.CommandText = """
            SELECT COUNT(*) FROM training_examples WHERE dataset_id=$dataset
                AND json_extract(json,'$.synthetic') = 1
            """;
        q.Parameters.AddWithValue("$dataset", dataset.Id);
        Assert.Equal((long)demo.Pages.Count, q.ExecuteScalar());
        var export = new TeacherCandidates(store).Export(
            dataset.Id,
            new("reviewer", ["administrator"], [])
        );
        Assert.Equal(demo.Pages.Count, export.Examples.Length);
        Assert.All(export.Examples, row =>
        {
            Assert.True(row.Example.Synthetic);
            Assert.Equal("labeled-example", row.Origin);
            Assert.Null(row.TeacherReview);
        });
        Assert.True(training.Train(new("Demo review model"), dataset.Id).Artifact.Synthetic);
    }

    private void RemoveReceipts(Packet packet, string pattern)
    {
        using var c = new SqliteConnection(store.ConnectionString);
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "DELETE FROM artifacts WHERE packet_id=$id AND name GLOB $pattern";
        q.Parameters.AddWithValue("$id", packet.Id);
        q.Parameters.AddWithValue("$pattern", pattern);
        q.ExecuteNonQuery();
    }

    public void Dispose()
    {
        processor.Dispose();
        network.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }

    private sealed class MockClassifiers : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }
        public bool FailSecondPageOnce { get; set; }

        public HttpClient CreateClient(string name) => new(this, false);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var payload = json.RootElement;
            bool openAi = request.RequestUri!.AbsolutePath == "/v1/decisions";
            if (!openAi)
                Assert.Equal("/v1/systemone", request.RequestUri.AbsolutePath);
            var state = openAi
                ? payload.GetProperty("input")[1].GetProperty("content")[0].GetProperty("data")
                : payload.GetProperty("state");
            bool second = state.GetProperty("previousPage").GetString()!.Length > 0;
            Calls++;
            if (second && FailSecondPageOnce)
            {
                FailSecondPageOnce = false;
                return new(HttpStatusCode.ServiceUnavailable);
            }
            string answer = openAi
                ? second
                    ? """{"answers":[{"name":"documentType","type":"choice","choice":"pod"},{"name":"startsDocument","probability":1}]}"""
                    : """{"model":"gpt-served-build","answers":[{"name":"documentType","type":"choice","choice":"pod","confidence":0.91},{"name":"startsDocument","probability":1}]}"""
                : second
                    ? """{"answers":{"documentType":{"choice":"pod","confidence":0.93},"startsDocument":{"noul":1}}}"""
                    : """{"answers":{"documentType":{"choice":"invoice","confidence":0.91},"startsDocument":{"noul":1}}}""";
            return new(HttpStatusCode.OK) { Content = new StringContent(answer) };
        }
    }
}
