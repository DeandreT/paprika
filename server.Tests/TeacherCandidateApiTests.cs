using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace paprika.Tests;

public sealed class TeacherCandidateApiTests
{
    [Fact]
    public async Task OriginalTeachersRequireExplicitAtomicReviewBeforeFitOrDownload()
    {
        await using var host = new Host();
        using var client = host.Client();
        var dataset = await Post<TrainingDataset>(
            client,
            "/api/training/datasets",
            new { name = "Teacher comparison" }
        );
        string api = "/api/training/datasets/" + dataset.Id;
        const string invoiceText = "Invoice: account balance payable in full";
        const string deliveryText = "Driver signature proof of delivery";
        var jev = await host.Process("jev", invoiceText, "invoice");
        var openAi = await host.Process("openai", invoiceText, "receipt");
        var delivery = await host.Process("openai", deliveryText, "pod");
        // A later reviewer decision must not overwrite the original proposed page type.
        jev.Documents[0].Type = "packing_list";
        using (
            var edit = await client.PutAsJsonAsync(
                $"/api/packets/{jev.Id}/review",
                new ReviewRequest(jev.Revision, jev.Documents, false)
            )
        )
            edit.EnsureSuccessStatusCode();

        Task<TrainingImportResponse> Import(params string[] ids) =>
            Post<TrainingImportResponse>(
                client,
                api + "/candidates/import",
                new CandidateImportRequest(ids)
            );
        async Task<TeacherCandidate[]> List() =>
            (await client.GetFromJsonAsync<TeacherCandidate[]>(api + "/candidates"))!;
        Task<HttpResponseMessage> Review(params CandidateReview[] decisions) =>
            client.PostAsJsonAsync(
                api + "/candidates/review",
                new CandidateReviewRequest(decisions)
            );
        Assert.Equal(2, (await Import(jev.Id, delivery.Id)).Imported);
        var single = (await List()).Single(c => c.Text == invoiceText);
        Assert.Equal("single", single.Comparison);
        Assert.Equal("invoice", Assert.Single(single.Predictions).ProposedLabel);
        using (
            var accept = await Review(
                new CandidateReview(single.Id, single.Version, "accepted", "invoice")
            )
        )
            accept.EnsureSuccessStatusCode();
        Assert.Equal(1, (await Import(openAi.Id)).Imported);
        Assert.Equal(0, (await Import(jev.Id, openAi.Id, delivery.Id)).Imported);
        var rows = await List();
        Assert.Equal(2, rows.Length);
        var disagreement = rows[0];
        Assert.Equal("disagreement", disagreement.Comparison);
        Assert.Equal("pending", disagreement.Status); // New evidence needs a new review.
        Assert.All(disagreement.Predictions, p => Assert.Equal(1, p.Revision));
        Assert.Contains(disagreement.Predictions, p =>
            p.Provider == "jev" && p.ProposedLabel == "invoice"
        );
        Assert.Contains(disagreement.Predictions, p =>
            p.Provider == "openai" && p.ProposedLabel == "receipt"
        );
        Assert.Empty(
            (await client.GetFromJsonAsync<ReviewedDatasetExport>(api + "/export"))!.Examples
        );
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (
                await client.PostAsJsonAsync(api + "/models", new TrainRequest("Pending only"))
            ).StatusCode
        );
        // Batch rollback includes the first valid item, even when the second is stale.
        var pod = new CandidateReview(rows[1].Id, rows[1].Version, "accepted", "pod");
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await Review(pod, new(single.Id, single.Version, "accepted", "invoice"))).StatusCode
        );
        Assert.All(await List(), c => Assert.Equal("pending", c.Status));
        var correct = new CandidateReview(
            disagreement.Id,
            disagreement.Version,
            "corrected",
            "bol"
        );
        var competing = await Task.WhenAll(Review(pod, correct), Review(pod, correct));
        Assert.Single(competing, r => r.IsSuccessStatusCode);
        Assert.Single(competing, r => r.StatusCode == HttpStatusCode.Conflict);
        var model = await Post<TrainingModelResponse>(
            client,
            api + "/models",
            new TrainRequest("Reviewed teachers")
        );
        Assert.False(model.Approved);
        Assert.True(model.Artifact.Synthetic);
        Assert.Equal(2, model.Artifact.DatasetExampleCount);
        Assert.Equal(0, model.Artifact.Evaluation.Examples);
        Assert.Equal("not evaluated", model.Artifact.Evaluation.Status);
        using var download = await client.GetAsync(api + "/export");
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        var export = (await download.Content.ReadFromJsonAsync<ReviewedDatasetExport>())!;
        Assert.Equal(dataset, export.Dataset);
        Assert.Equal(2, export.Examples.Length);
        Assert.All(export.Examples, e =>
        {
            Assert.Equal("train", e.Split);
            Assert.Equal("reviewed-teacher", e.Origin);
            Assert.Equal("local", e.TeacherReview!.Reviewer);
            Assert.True(e.Example.Synthetic);
        });
        var reviewed = export.Examples.Single(e => e.Example.Label == "bol");
        Assert.Equal("corrected", reviewed.TeacherReview!.Status);
        Assert.Equal(2, reviewed.TeacherReview.Predictions.Length);
        Assert.Empty(
            (
                await client.GetFromJsonAsync<ReviewedDatasetExport>(
                    "/api/training/datasets/default/export"
                )
            )!.Examples
        );
        Assert.Empty((await client.GetFromJsonAsync<TrainingExample[]>(api + "/examples"))!);
        using (
            var invalid = await client.PostAsJsonAsync(
                api + "/examples",
                new TrainingImportRequest([reviewed.Example])
            )
        )
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (
            var reject = await Review(
                new CandidateReview(disagreement.Id, reviewed.TeacherReview.Version, "rejected")
            )
        )
            reject.EnsureSuccessStatusCode();
        Assert.Single(
            (await client.GetFromJsonAsync<ReviewedDatasetExport>(api + "/export"))!.Examples
        );
        using var db = new SqliteConnection(
            host.Services.GetRequiredService<Store>().ConnectionString
        );
        db.Open();
        using var tx = db.BeginTransaction();
        var retention = new TeacherCandidateRetention();
        Assert.NotEmpty(retention.Blockers(db, tx, jev));
        Assert.NotEmpty(retention.Blockers(db, tx, openAi));
        Assert.Equal(3, host.Transport.Calls);
    }

    [Fact]
    public async Task ArchiveRemovesTrainingContentAndSharedReviewersCannotExport()
    {
        await using var host = new Host();
        using var client = host.Client();
        const string api = "/api/training/datasets/default";
        var packet = await host.Process("jev", "Synthetic shipping instruction", "bol");
        await Post<TrainingImportResponse>(
            client,
            api + "/candidates/import",
            new CandidateImportRequest([packet.Id])
        );
        var candidate = Assert.Single(
            (await client.GetFromJsonAsync<TeacherCandidate[]>(api + "/candidates"))!
        );
        var change = new CandidateReviewRequest([
            new(candidate.Id, candidate.Version, "accepted", "bol"),
        ]);
        await Post<CandidateReviewResult>(client, api + "/candidates/review", change);
        var store = host.Services.GetRequiredService<Store>();
        var old = DateTimeOffset.UtcNow.AddDays(-180);
        packet.CreatedAt = old;
        packet.Status = "approved";
        packet.Audit = [new(old, "approved", packet.Revision, "fixture")];
        store.Save(packet);
        var admin = new OperationsIdentity("local", ["administrator"], []);
        host.Services.GetRequiredService<OperationsPlatformStore>()
            .Archive(new(packet.Id, packet.Revision, true), admin);
        Assert.Empty((await client.GetFromJsonAsync<TeacherCandidate[]>(api + "/candidates"))!);
        Assert.Empty(
            (await client.GetFromJsonAsync<ReviewedDatasetExport>(api + "/export"))!.Examples
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync(api + "/candidates/review", change)).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await client.PostAsJsonAsync(
                    api + "/candidates/import",
                    new CandidateImportRequest([packet.Id])
                )
            ).StatusCode
        );
        var config = host.Services.GetRequiredService<IConfiguration>();
        config["paprika:Operations:Mode"] = "shared";
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync(api + "/export")).StatusCode
        );
        client.DefaultRequestHeaders.Authorization = new("Bearer", Host.TestToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(api + "/export")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync(api + "/candidates")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync(api + "/candidates/review", change)).StatusCode
        );
        Assert.Equal(1, host.Transport.Calls);
    }

    private static async Task<T> Post<T>(HttpClient client, string url, object body)
    {
        using var response = await client.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed class Host : WebApplicationFactory<Program>
    {
        public const string TestToken = "synthetic-reviewer-fixture-not-a-secret";
        private readonly string root = Path.Combine(
            Path.GetTempPath(),
            "paprika-teachers-" + Guid.NewGuid().ToString("N")
        );
        public TeacherTransport Transport { get; } = new();

        public HttpClient Client()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-paprika", "1");
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(
                (_, cfg) =>
                    cfg.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["paprika:DataDirectory"] = root,
                            ["paprika:Operations:Mode"] = "local",
                            ["paprika:Operations:Retention:ExecutionEnabled"] = "true",
                            ["paprika:Operations:Users:0:Token"] = TestToken,
                            ["paprika:Operations:Users:0:Name"] = "packet reviewer",
                            ["paprika:Operations:Users:0:Roles:0"] = "reviewer",
                            ["paprika:Operations:Users:0:Workflows:0"] = "legacy-logistics",
                            ["paprika:OpenAiBaseUrl"] = "https://fixture.example.test/v1",
                            ["paprika:OpenAiApiKey"] = "fixture",
                            ["paprika:JevApiKey"] = "fixture",
                            ["paprika:AzureEndpoint"] = "",
                        }
                    )
            );
            builder.ConfigureServices(s =>
                s.AddHttpClient("providers").ConfigurePrimaryHttpMessageHandler(() => Transport)
            );
        }

        internal async Task<Packet> Process(string provider, string text, string proposed)
        {
            var store = Services.GetRequiredService<Store>();
            var packet = new Packet
            {
                Mode = "live",
                TestRun = true,
                Workflow = WorkflowRegistry.Legacy(
                    "live",
                    provider == "jev" ? "jev-fixture" : "gpt-fixture"
                ) with
                {
                    ProviderProfile = provider,
                },
            };
            var source = new Source
            {
                Name = "fixture.pdf",
                Extension = ".pdf",
                PageCount = 1,
                OcrJson = JsonSerializer.Serialize(new { openAi = new[] { text } }),
            };
            var path = store.SourcePath(packet, source);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Both teachers get the exact same synthetic bytes, not merely the same file name.
            var original = Path.Combine(root, TrainingClassifier.Hash(text) + ".pdf");
            if (!File.Exists(original))
                Pdfs.Synthetic(original, [text]);
            File.Copy(original, path);
            source.Sha256 = Pdfs.SourceHash(path);
            packet.Sources.Add(source);
            store.Save(packet);
            Transport.Proposed = proposed;
            await Services.GetRequiredService<PacketProcessor>().Process(packet.Id, default);
            return store.Get(packet.Id)!;
        }

        public override async ValueTask DisposeAsync()
        {
            var connection = Services.GetRequiredService<Store>().ConnectionString;
            await base.DisposeAsync();
            using var pool = new SqliteConnection(connection);
            SqliteConnection.ClearPool(pool);
            Directory.Delete(root, true);
        }
    }

    private sealed class TeacherTransport : HttpMessageHandler
    {
        public string Proposed { get; set; } = "";
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Calls++;
            object payload = request.RequestUri!.AbsolutePath switch
            {
                "/v1/systemone" => new
                {
                    model = "jev-fixture-result",
                    answers = new
                    {
                        documentType = new { choice = Proposed, confidence = .95 },
                        startsDocument = new { noul = 1 },
                    },
                },
                "/v1/decisions" => new
                {
                    model = "gpt-fixture-result",
                    answers = new object[]
                    {
                        new
                        {
                            name = "documentType",
                            type = "choice",
                            choice = Proposed,
                            confidence = .95,
                        },
                        new { name = "startsDocument", probability = 1 },
                    },
                },
                _ => throw new InvalidOperationException("Unexpected provider request."),
            };
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload) }
            );
        }
    }
}
