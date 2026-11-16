using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using paprika;
using Xunit;

namespace paprika.Tests;

public sealed class PlatformQualityIntegrationTests
{
    [Fact]
    public async Task UploadWithoutCapabilitiesRemainsManualAndExportIsBlocked()
    {
        await using var app = new QualityFactory();
        using var client = app.Client();
        var packet = await app.Upload(client, "INVOICE payment due");
        Assert.Equal("review", packet.Status);
        Assert.Null(packet.Workflow.Capabilities);
        using var export = await client.GetAsync($"/api/packets/{packet.Id}/export");
        Assert.Equal(HttpStatusCode.Conflict, export.StatusCode);
        var quality = await Get<QualityPacketResponse>(client, $"/api/quality/packets/{packet.Id}");
        Assert.Empty(quality.Tasks);
        Assert.False(quality.Decision?.AutoApprove ?? false);
    }

    [Fact]
    public async Task RealHostAppliesFrozenModelToUploadedBytesAndSamplesFinalApprovedRevision()
    {
        await using var app = new QualityFactory();
        using var client = app.Client();
        var model = await TrainFixture(client, approve: true);
        var workflow = await CreateWorkflow(client, model.Id, automate: true);
        var packet = await app.Upload(
            client,
            "remittance balance payable ledger account settlement",
            workflow
        );
        Assert.Equal("approved", packet.Status);
        Assert.False(packet.TestRun);
        Assert.Equal("simulation", packet.Mode);
        Assert.Equal(1, packet.Revision);
        Assert.Equal("invoice", Assert.Single(packet.Pages).PredictedType);
        Assert.Equal(model.Hash, packet.Workflow.Capabilities!.Training!.Hash);
        Assert.Equal(
            JsonSerializer.Serialize(model.Artifact, Contract.Json),
            packet.Workflow.Capabilities.Training.Payload
        );
        var quality = await Get<QualityPacketResponse>(client, $"/api/quality/packets/{packet.Id}");
        Assert.True(quality.Decision!.AutoApprove);
        Assert.True(quality.Decision.Sampled);
        var task = Assert.Single(quality.Tasks);
        Assert.Equal("approved", task.Snapshot.Status);
        Assert.Equal(packet.Revision, task.PacketRevision);
        Assert.Equal(packet.Workflow.Hash, task.Snapshot.Workflow.Hash);
        using (var response = await client.GetAsync($"/api/quality/packets/{packet.Id}"))
        {
            string body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("ocrJson", body);
            Assert.DoesNotContain("operationUrl", body);
            using var publicSnapshot = JsonDocument.Parse(body);
            Assert.False(
                publicSnapshot
                    .RootElement.GetProperty("tasks")[0]
                    .GetProperty("snapshot")
                    .TryGetProperty("sources", out _)
            );
        }
        var examples = await Get<TrainingExample[]>(client, "/api/training/examples");
        Assert.DoesNotContain(examples, e => e.PacketId == packet.Id); // No machine self-label feedback.
        using var c = new SqliteConnection(
            app.Services.GetRequiredService<Store>().ConnectionString
        );
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "SELECT json FROM artifacts WHERE packet_id=$id AND name LIKE 'predicted-%'";
        q.Parameters.AddWithValue("$id", packet.Id);
        using var prediction = JsonDocument.Parse((string)q.ExecuteScalar()!);
        Assert.Equal("review", prediction.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, prediction.RootElement.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task QaIsIndependentAndFailureInvalidatesOnlyItsCurrentApprovedRevision()
    {
        await using var app = new QualityFactory();
        using var client = app.Client();
        using var automatedActor = app.Client("automation");
        var model = await TrainFixture(client, true);
        var workflow = await CreateWorkflow(client, model.Id, true);
        app.Services.GetRequiredService<IConfiguration>()[
            "paprika:Operations:Users:2:Workflows:0"
        ] = workflow.Id;
        using var scopedReviewer = app.Client("reviewer");
        var packet = await app.Upload(
            client,
            "remittance balance payable ledger account settlement",
            workflow
        );
        var task = Assert.Single(
            (await Get<QualityPacketResponse>(client, $"/api/quality/packets/{packet.Id}")).Tasks
        );
        string frozen = JsonSerializer.Serialize(task.Snapshot, Contract.Json);
        using (
            var self = await automatedActor.PostAsJsonAsync(
                $"/api/quality/qa/{task.Id}/complete",
                new QualityQaRequest(task.Revision, "pass")
            )
        )
            Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        var visible = await Get<QualityQaTaskResponse[]>(scopedReviewer, "/api/quality/qa");
        Assert.Contains(visible, t => t.Id == task.Id);
        using (var privateModels = await scopedReviewer.GetAsync("/api/training/models"))
            Assert.Equal(HttpStatusCode.Forbidden, privateModels.StatusCode);
        var completed = await Post<QualityQaTaskResponse>(
            scopedReviewer,
            $"/api/quality/qa/{task.Id}/complete",
            new QualityQaRequest(
                task.Revision,
                "fail",
                "Artificial acceptance fixture: incorrect label."
            )
        );
        Assert.Equal("completed", completed.Status);
        var reopened = await Get<PacketResponse>(client, $"/api/packets/{packet.Id}");
        Assert.Equal("review", reopened.Status);
        Assert.Equal(packet.Revision + 1, reopened.Revision);
        Assert.Equal(frozen, JsonSerializer.Serialize(completed.Snapshot, Contract.Json));
        using (var export = await client.GetAsync($"/api/packets/{packet.Id}/export"))
            Assert.Equal(HttpStatusCode.Conflict, export.StatusCode);
        using (
            var stale = await client.PutAsJsonAsync(
                $"/api/packets/{packet.Id}/review",
                new ReviewRequest(packet.Revision, packet.Documents, true)
            )
        )
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using (
            var duplicate = await client.PostAsJsonAsync(
                $"/api/quality/qa/{task.Id}/complete",
                new QualityQaRequest(task.Revision, "pass")
            )
        )
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // Failure of an older immutable sample must not reopen a subsequently reviewed approval.
        var later = await app.Upload(
            client,
            "remittance balance payable ledger account settlement",
            workflow
        );
        var oldTask = Assert.Single(
            (await Get<QualityPacketResponse>(client, $"/api/quality/packets/{later.Id}")).Tasks
        );
        await Claim(client, later);
        var edited = await Put<PacketResponse>(
            client,
            $"/api/packets/{later.Id}/review",
            new ReviewRequest(later.Revision, later.Documents, false)
        );
        await Claim(client, edited);
        var approved = await Put<PacketResponse>(
            client,
            $"/api/packets/{later.Id}/review",
            new ReviewRequest(edited.Revision, edited.Documents, true)
        );
        Assert.Equal("approved", approved.Status);
        await Post<QualityQaTaskResponse>(
            client,
            $"/api/quality/qa/{oldTask.Id}/complete",
            new QualityQaRequest(oldTask.Revision, "fail")
        );
        var unchanged = await Get<PacketResponse>(client, $"/api/packets/{later.Id}");
        Assert.Equal(approved.Revision, unchanged.Revision);
        Assert.Equal("approved", unchanged.Status);
    }

    [Fact]
    public async Task PublishedModelPayloadHashAndUnapprovedStateStayFrozenAfterLaterApproval()
    {
        await using var app = new QualityFactory();
        using var client = app.Client();
        var model = await TrainFixture(client, false);
        var workflow = await CreateWorkflow(client, model.Id, false);
        var first = await app.Upload(client, "remittance balance payable", workflow);
        Assert.False(first.Workflow.Capabilities!.Training!.Approved);
        await Post<TrainingModelResponse>(
            client,
            $"/api/training/models/{model.Id}/approve",
            new TrainingApprovalRequest(model.Hash)
        );
        var second = await app.Upload(client, "remittance balance payable", workflow);
        Assert.Equal("review", second.Status);
        Assert.Equal(first.Workflow.Hash, second.Workflow.Hash);
        Assert.Equal(first.Workflow.Capabilities.Training, second.Workflow.Capabilities!.Training);
        var entries = (await Get<WorkflowCatalogResponse>(client, "/api/workflows")).Entries;
        var published = entries.Single(e =>
            e.Id == workflow.Id && e.Kind == "workflow" && e.Version == workflow.Version
        );
        using (
            var mutate = await client.PutAsJsonAsync(
                $"/api/workflows/catalog/workflow/{workflow.Id}/{workflow.Version}",
                published with
                {
                    Workflow = published.Workflow! with
                    {
                        Capabilities = published.Workflow.Capabilities! with
                        {
                            Training = new(model.Id, "forged-hash"),
                        },
                    },
                }
            )
        )
            Assert.Equal(HttpStatusCode.Conflict, mutate.StatusCode);
        var next = await Post<CatalogEntry>(
            client,
            $"/api/workflows/catalog/workflow/{workflow.Id}/{workflow.Version}/versions",
            new RevisionRequest(published.Revision)
        );
        next = await Put<CatalogEntry>(
            client,
            $"/api/workflows/catalog/workflow/{next.Id}/{next.Version}",
            next with
            {
                Workflow = next.Workflow! with
                {
                    Capabilities = next.Workflow.Capabilities! with { Training = new(model.Id) },
                },
            }
        );
        next = await Post<CatalogEntry>(
            client,
            $"/api/workflows/catalog/workflow/{next.Id}/{next.Version}/publish",
            new RevisionRequest(next.Revision)
        );
        var rebound = await app.Upload(
            client,
            "remittance balance payable",
            new(next.Id, next.Version)
        );
        Assert.True(rebound.Workflow.Capabilities!.Training!.Approved);
        Assert.NotEqual(first.Workflow.Hash, rebound.Workflow.Hash);
        Assert.Equal(
            first.Workflow.Hash,
            (await Get<PacketResponse>(client, $"/api/packets/{first.Id}")).Workflow.Hash
        );
    }

    [Fact]
    public void SameSourceCorrectionsAndNearIdenticalPageVariantsNeverInflateHeldout()
    {
        var rows = Enumerable
            .Range(1, 60)
            .SelectMany(revision =>
                new[]
                {
                    new TrainingExample(
                        "review:packet:" + revision + ":invoice-page",
                        $"Invoice amount adjusted {revision}",
                        revision == 60 ? "invoice" : "pod",
                        false,
                        "isolated correction fixture",
                        "packet",
                        revision,
                        "sourcehash:1"
                    ),
                    new TrainingExample(
                        "review:packet:" + revision + ":pod-page",
                        $"Delivery signed updated {revision}",
                        "pod",
                        false,
                        "isolated correction fixture",
                        "packet",
                        revision,
                        "sourcehash:2"
                    ),
                }
            )
            .ToArray();
        var model = TrainingClassifier.Fit(rows, .95);
        Assert.Equal(2, model.TrainingExamples);
        Assert.Equal("not evaluated", model.Evaluation.Status);
        // Legacy capture IDs without SourcePage still identify the same physical page across revisions.
        var legacy = TrainingClassifier.Fit(
            rows.Select(e => e with { SourcePage = null }).ToArray(),
            .95
        );
        Assert.Equal(2, legacy.TrainingExamples);
        Assert.Equal("not evaluated", legacy.Evaluation.Status);
        var mixed = TrainingClassifier.Fit(
            rows.Select((e, i) => i % 3 == 0 ? e with { SourcePage = null } : e).ToArray(),
            .95
        );
        Assert.Equal(2, mixed.TrainingExamples);
        Assert.Equal("not evaluated", mixed.Evaluation.Status);
    }

    [Fact]
    public void CaseWhitespaceAndPunctuationDuplicatesCannotBecomeIndependentTestSamples()
    {
        var rows = Enumerable
            .Range(0, 50)
            .SelectMany(i =>
                new[]
                {
                    new TrainingExample(
                        "invoice" + i,
                        i % 2 == 0 ? "Invoice,  PAYMENT! due." : "invoice payment due",
                        "invoice"
                    ),
                    new TrainingExample(
                        "pod" + i,
                        i % 2 == 0 ? "Delivery, SIGNED! received." : "delivery signed received",
                        "pod"
                    ),
                }
            )
            .ToArray();
        var model = TrainingClassifier.Fit(rows, .95);
        Assert.Equal(2, model.TrainingExamples);
        Assert.Null(model.Evaluation.Accuracy);
        Assert.Equal("not evaluated", model.Evaluation.Status);
    }

    private static async Task<TrainingModelResponse> TrainFixture(HttpClient client, bool approve)
    {
        // Artificial fixture intentionally exercises the operational/non-test metadata branch in an
        // isolated host. Synthetic=false is test branch input, NEVER evidence of real model accuracy.
        var rows = Enumerable
            .Range(0, 200)
            .SelectMany(i =>
                new[]
                {
                    new TrainingExample(
                        "inv-" + i,
                        $"remittance balance payable ledger account settlement invrow{i}",
                        "invoice",
                        false,
                        "ARTIFICIAL ACCEPTANCE FIXTURE; not operational accuracy evidence",
                        SourcePage: "artificial-invoice-page-" + i
                    ),
                    new TrainingExample(
                        "pod-" + i,
                        $"consignee autograph arrival freight delivered receipt podrow{i}",
                        "pod",
                        false,
                        "ARTIFICIAL ACCEPTANCE FIXTURE; not operational accuracy evidence",
                        SourcePage: "artificial-pod-page-" + i
                    ),
                }
            )
            .ToArray();
        await Post<TrainingImportResponse>(
            client,
            "/api/training/examples",
            new TrainingImportRequest(rows)
        );
        var model = await Post<TrainingModelResponse>(
            client,
            "/api/training/models",
            new TrainRequest("ARTIFICIAL isolated eligibility fixture")
        );
        Assert.Equal("held-out", model.Artifact.Evaluation.Status);
        Assert.True(model.Artifact.Evaluation.Examples >= 50);
        if (approve)
            model = await Post<TrainingModelResponse>(
                client,
                $"/api/training/models/{model.Id}/approve",
                new TrainingApprovalRequest(model.Hash)
            );
        return model;
    }

    private static async Task<VersionRef> CreateWorkflow(
        HttpClient client,
        string modelId,
        bool automate
    )
    {
        var catalog = await Get<WorkflowCatalogResponse>(client, "/api/workflows");
        var original = catalog.Entries.Single(e =>
            e.Kind == "workflow" && e.Id == "general-logistics" && e.Version == 1
        );
        var draft = await Post<CatalogEntry>(
            client,
            "/api/workflows/catalog",
            new CreateCatalogRequest(
                "workflow",
                "quality-" + Guid.NewGuid().ToString("N"),
                "Isolated quality acceptance fixture",
                "Artificial fixture only",
                Workflow: original.Workflow! with
                {
                    DefaultSplit = new("separate"),
                    Capabilities = new(
                        Quality: new(Mode: "selective", AutoApprove: automate, SampleRate: 1),
                        Training: new(modelId)
                    ),
                }
            )
        );
        var published = await Post<CatalogEntry>(
            client,
            $"/api/workflows/catalog/workflow/{draft.Id}/{draft.Version}/publish",
            new RevisionRequest(draft.Revision)
        );
        return new(published.Id, published.Version);
    }

    private static async Task<T> Get<T>(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        return await Read<T>(response);
    }

    private static async Task Claim(HttpClient client, PacketResponse packet)
    {
        var task = await Post<OperationsTask>(
            client,
            "/api/operations-platform/tasks/open",
            new OperationsOpenReview(packet.Id, packet.Revision)
        );
        await Post<OperationsTask>(
            client,
            $"/api/operations-platform/tasks/{task.Id}/claim",
            new OperationsTaskChange(task.Revision)
        );
    }

    private static async Task<T> Post<T>(HttpClient client, string url, object body)
    {
        using var response = await client.PostAsJsonAsync(url, body);
        return await Read<T>(response);
    }

    private static async Task<T> Put<T>(HttpClient client, string url, object body)
    {
        using var response = await client.PutAsJsonAsync(url, body);
        return await Read<T>(response);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.IsSuccessStatusCode,
            $"{response.RequestMessage?.RequestUri}: {(int)response.StatusCode} {body}"
        );
        return JsonSerializer.Deserialize<T>(body, Contract.Json)!;
    }

    private sealed class QualityFactory : WebApplicationFactory<Program>
    {
        public string Root { get; } =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp",
                "opencode",
                "quality-integration-" + Guid.NewGuid().ToString("N")
            );

        public QualityFactory() => Directory.CreateDirectory(Root);

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration(
                (_, config) =>
                    config.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["paprika:DataDirectory"] = Root,
                            ["paprika:Mode"] = "simulation",
                            ["paprika:Operations:Mode"] = "shared",
                            ["paprika:Operations:Users:0:Name"] = "independent",
                            ["paprika:Operations:Users:0:Roles:0"] = "administrator",
                            ["paprika:Operations:Users:0:Token"] = Token("independent"),
                            ["paprika:Operations:Users:1:Name"] = "automation",
                            ["paprika:Operations:Users:1:Roles:0"] = "administrator",
                            ["paprika:Operations:Users:1:Token"] = Token("automation"),
                            ["paprika:Operations:Users:2:Name"] = "reviewer",
                            ["paprika:Operations:Users:2:Roles:0"] = "reviewer",
                            ["paprika:Operations:Users:2:Token"] = Token("reviewer"),
                        }
                    )
            );

        private static string Token(string actor) =>
            "artificial-acceptance-test-token-not-a-secret-" + actor;

        public HttpClient Client(string actor = "independent")
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-paprika", "1");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                Token(actor)
            );
            return client;
        }

        public async Task<PacketResponse> Upload(
            HttpClient client,
            string text,
            VersionRef? workflow = null
        )
        {
            string path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".pdf");
            Pdfs.Synthetic(path, [text]);
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(await File.ReadAllBytesAsync(path));
            file.Headers.ContentType = new("application/pdf");
            form.Add(file, "files", "ARTIFICIAL-acceptance-fixture.pdf");
            if (workflow is not null)
            {
                form.Add(new StringContent(workflow.Id), "workflowId");
                form.Add(new StringContent(workflow.Version.ToString()), "workflowVersion");
            }
            using var uploaded = await client.PostAsync("/api/packets", form);
            var packet = await Read<PacketResponse>(uploaded);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            while (packet.Status is "queued" or "processing")
            {
                await Task.Delay(30, timeout.Token);
                packet = await Get<PacketResponse>(client, $"/api/packets/{packet.Id}");
            }
            Assert.NotEqual("failed", packet.Status);
            return packet;
        }

        public override async ValueTask DisposeAsync()
        {
            // Capture before disposing DI; await host shutdown before releasing its SQLite pool.
            string connectionString = Services.GetRequiredService<Store>().ConnectionString;
            await base.DisposeAsync();
            using var pool = new SqliteConnection(connectionString);
            for (int attempt = 0; attempt < 10; attempt++)
            {
                // Do not clear unrelated factories' pools while the full suite runs in parallel.
                SqliteConnection.ClearPool(pool);
                try
                {
                    if (Directory.Exists(Root))
                        Directory.Delete(Root, true);
                    return;
                }
                catch (Exception error)
                    when (attempt < 9 && error is IOException or UnauthorizedAccessException)
                {
                    // Windows may briefly retain a just-closed SQLite/WAL or scanner handle.
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100 * (attempt + 1), 500)));
                }
            }
        }
    }
}
