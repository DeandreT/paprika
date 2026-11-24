using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using paprika;
using Xunit;

namespace paprika.Tests;

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableAiExtractionRetriesOnlyMissingAnswersAndPreservesCompletedBlocks(
        bool exhaust
    )
    {
        using var db = new CaseFlowDatabase();
        int firstCalls = 0,
            secondCalls = 0;
        using var provider = new FakeAi((page, _) =>
        {
            if (page.StartsWith("first", StringComparison.Ordinal))
                Interlocked.Increment(ref firstCalls);
            else if (Interlocked.Increment(ref secondCalls) <= (exhaust ? 4 : 1))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            return Task.FromResult(GroundedAnswer());
        });
        await using var host = new FlowHost(db.Store.Root, provider);
        var store = host.Services.GetRequiredService<Store>();
        var engine = host.Services.GetRequiredService<FlowEngine>();
        var messaging = host.Services.GetRequiredService<MessagingOperations>();
        var packet = AiPacket(store, "failed");
        Assert.Equal(200, store.Retry(packet.Id).Code);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        if (exhaust)
        {
            DeadLetterSummary? letter = null;
            while (letter is null)
            {
                letter = (await messaging.Read(timeout.Token)).DeadLetters.SingleOrDefault();
                if (letter is null)
                    await Task.Delay(50, timeout.Token);
            }
            Assert.Equal(packet.Id, letter.PacketId);
            Assert.Contains("bounded automatic retries", letter.Reason);
            Assert.Equal("failed", store.Get(packet.Id)!.Status);
            var pending = engine.Get(packet)!.Executions.Single(e => e.BlockId == "extract");
            Assert.Equal("pending", pending.Status);
            Assert.Equal(4, pending.Attempt);
            Assert.Null(pending.Error);
            Assert.Equal(1, Volatile.Read(ref firstCalls));
            Assert.Equal(4, Volatile.Read(ref secondCalls));
            Assert.Equal(200, await messaging.Replay(Guid.Parse(letter.Id), timeout.Token));
        }

        while (store.Get(packet.Id)!.Status != "review")
            await Task.Delay(50, timeout.Token);
        var review = store.Get(packet.Id)!;
        var beforeApproval = engine.Get(review)!;
        Assert.Equal(1, beforeApproval.Executions.Single(e => e.BlockId == "case").Attempt);
        var extraction = beforeApproval.Executions.Single(e => e.BlockId == "extract");
        Assert.Equal("completed", extraction.Status);
        Assert.Equal(exhaust ? 5 : 2, extraction.Attempt);
        Assert.DoesNotContain("carrier", extraction.Result!.Fields.Keys);
        Assert.Equal("skipped", beforeApproval.Executions.Single(e => e.BlockId == "use").Status);
        Assert.Equal(1, Volatile.Read(ref firstCalls));
        Assert.Equal(exhaust ? 5 : 2, Volatile.Read(ref secondCalls));
        Assert.Empty((await messaging.Read(timeout.Token)).DeadLetters);

        // A covered review must not depend on whether unreviewed provider answers are still cached.
        using (var connection = new SqliteConnection(store.ConnectionString))
        {
            connection.Open();
            using var removeCache = connection.CreateCommand();
            removeCache.CommandText =
                "DELETE FROM extraction_objects WHERE kind='ai-answer' AND json_extract(json,'$.packetId')=$id";
            removeCache.Parameters.AddWithValue("$id", packet.Id);
            Assert.Equal(2, removeCache.ExecuteNonQuery());
        }

        var result = store.Review(review.Id, new(review.Revision, review.Documents, true));
        Assert.Equal(200, result.Code);
        var approved = result.Packet!;
        while (
            engine.Get(approved)?.Executions.Any(e => e.BlockId == "use" && e.Status == "completed")
            != true
        )
            await Task.Delay(50, timeout.Token);
        Assert.Equal(
            "ACME",
            engine
                .Get(approved)!
                .Executions.Single(e => e.BlockId == "use")
                .Result!.Fields["reviewed"]
        );
        Assert.Equal(1, Volatile.Read(ref firstCalls));
        Assert.Equal(exhaust ? 5 : 2, Volatile.Read(ref secondCalls));
    }

    [Fact]
    public async Task AiPreparationCancellationPropagatesAndResumesButPermanentFailureNeedsBlockRetry()
    {
        using var db = new CaseFlowDatabase();
        var packet = AiPacket(db.Store, "processing");
        packet.Documents = WorkflowAssembly.Assemble(packet);
        db.Store.Save(packet);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pending = new FakeAi(async (_, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return GroundedAnswer();
        });
        using var rejected = new FakeAi((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))
        );
        var settings = new Settings(
            new ConfigurationBuilder().AddInMemoryCollection(ProviderSettings()).Build()
        );
        var engine = new FlowEngine(db.Store);
        var training = new TrainingStore(db.Store);
        var quality = new QualityStore(db.Store, training);
        var references = new ReferenceService(db.Store, new ConnectorCatalog(db.Configuration));
        var cases = new CaseStore(db.Store);
        PlatformPipeline Pipeline(FakeAi provider) =>
            new(
                db.Store,
                training,
                quality,
                references,
                engine,
                cases,
                new(settings, db.Store, provider)
            );

        using var cancelled = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var active = Pipeline(pending).AfterAssemblyAsync(packet, cancelled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        var first = engine.Get(packet)!.Executions.Single(e => e.BlockId == "extract");
        Assert.Equal("pending", first.Status);
        Assert.Equal(1, first.Attempt);
        Assert.Null(first.Error);

        var pipeline = Pipeline(rejected);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.AfterAssemblyAsync(packet, default)
        );
        var retry = engine.Get(packet)!.Executions.Single(e => e.BlockId == "extract");
        Assert.Equal("failed", retry.Status);
        Assert.Equal(2, retry.Attempt);
        Assert.Equal(first.IdempotencyKey, retry.IdempotencyKey);
        await Assert.ThrowsAsync<WorkflowException>(() =>
            pipeline.AfterAssemblyAsync(packet, default)
        );
        Assert.Equal(1, engine.Get(packet)!.Executions.Single(e => e.BlockId == "case").Attempt);
    }

    private static Packet AiPacket(Store store, string status)
    {
        var extraction = new ExtractionDefinition(
            "ai-flow",
            1,
            [new("carrier", Description: "Carrier name", Required: true)],
            []
        );
        var packet = new Packet
        {
            Mode = "live",
            Status = status,
            Workflow = WorkflowRegistry.Legacy("live") with
            {
                RequiredIdentifierKinds = [],
                Capabilities = new(
                    Case: new("customer", ["bol"]),
                    Extraction: new(
                        extraction.Id,
                        extraction.Version,
                        ExtractionStorage.Hash(extraction),
                        extraction
                    ),
                    Flow: new(
                        new(
                            "AI field flow",
                            [
                                new("case", FlowBlockKind.Case, []),
                                new("extract", FlowBlockKind.Extraction, ["case"]),
                                new(
                                    "use",
                                    FlowBlockKind.Transform,
                                    ["extract"],
                                    new(FlowConditionKind.FieldEquals, "carrier", "ACME"),
                                    Operations: [new(FlowTransformKind.Trim, "carrier", "reviewed")]
                                ),
                            ]
                        )
                    )
                ),
            },
        };
        foreach (string label in new[] { "first", "second" })
        {
            string text = $"{label}\nCarrier: ACME";
            var source = new Source
            {
                Name = $"{label}.pdf",
                PageCount = 1,
                OcrJson = JsonSerializer.Serialize(
                    new
                    {
                        analyzeResult = new
                        {
                            pages = new[]
                            {
                                new { pageNumber = 1, lines = new[] { new { content = text } } },
                            },
                        },
                    }
                ),
            };
            var page = Worker.MakePage(packet, source, 1, text);
            page.PredictedType = "bol";
            page.ReviewReasons.Add("Classified before retry.");
            packet.Sources.Add(source);
            packet.Pages.Add(page);
        }
        store.Save(packet);
        return packet;
    }

    private static HttpResponseMessage GroundedAnswer() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"{\"carrier\":{\"value\":\"ACME\",\"quote\":\"Carrier: ACME\"}}"}]}]}
                """
            ),
        };

    private static Dictionary<string, string?> ProviderSettings() =>
        new()
        {
            ["paprika:AzureEndpoint"] = "https://ocr.example.test",
            ["paprika:AzureKey"] = "test-placeholder",
            ["paprika:OpenAiApiKey"] = "test-placeholder",
            ["paprika:OpenAiBaseUrl"] = "https://ai.example.test/v1/",
        };

    private sealed class FlowHost(string root, FakeAi provider) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var values = ProviderSettings();
                values["paprika:DataDirectory"] = root;
                config.AddInMemoryCollection(values);
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(provider);
            });
        }
    }

    private sealed class FakeAi(Func<string, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler,
            IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, false);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Assert.Equal("https://ai.example.test/v1/responses", request.RequestUri?.ToString());
            using var body = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken)
            );
            using var input = JsonDocument.Parse(
                body.RootElement.GetProperty("input")[1].GetProperty("content").GetString()!
            );
            return await send(
                input.RootElement.GetProperty("pages")[0].GetProperty("text").GetString()!,
                cancellationToken
            );
        }
    }
}
