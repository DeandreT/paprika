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

public sealed class ExtractionAiIntakeTests
{
    [Fact]
    public async Task ManualAiRunRequiresOptInAndFillsMissingCoverageWithoutOverwritingCorrections()
    {
        using var app = new Host();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-paprika", "1");
        var store = app.Services.GetRequiredService<Store>();
        var definition = new ExtractionDefinition(
            "pickup",
            1,
            [new("address", Description: "Pickup address")],
            [],
            "invoice"
        );
        using var definitionResponse = await client.PostAsJsonAsync(
            "/api/extraction/definitions",
            definition
        );
        definitionResponse.EnsureSuccessStatusCode();
        var digest = ExtractionStorage.Hash(definition);
        var packet = new Packet { Mode = "live", Status = "review" };
        var page1 = new Page { Text = "Pickup at 15 Example Rd." };
        var page2 = new Page { Text = "Pickup at 15 Example Rd." };
        var corrected = new Document { Type = "invoice", PageIds = [page1.Id] };
        var changed = new Document { Type = "invoice", PageIds = [page2.Id] };
        packet.Pages.AddRange([page1, page2]);
        packet.Documents.AddRange([corrected, changed]);
        store.Save(packet);
        var url = $"/api/extraction/{packet.Id}/run";
        var req = new ExtractionRunRequest(packet.Revision, definition.Id, definition.Version);
        using (var unauthorized = await client.PostAsJsonAsync(url, req))
        {
            Assert.Equal(HttpStatusCode.Conflict, unauthorized.StatusCode);
            Assert.Equal(0, app.Transport.Calls);
        }

        packet.Workflow = WorkflowRegistry.Legacy("live", "gpt-6-luna") with
        {
            Capabilities = new(
                Extraction: new(definition.Id, definition.Version, digest, definition)
            ),
        };
        store.Save(packet);
        var carried = new ExtractionResult(
            corrected.Id,
            digest,
            [new("address", "Verified dock address", "corrected", [])],
            []
        );
        var partial = new ExtractionRun(
            Guid.NewGuid().ToString("N"),
            packet.Id,
            packet.Revision,
            DateTimeOffset.UtcNow,
            definition,
            [carried],
            ExtractionStorage.Hash(packet.EffectiveWorkflow)
        );
        ExtractionStorage.Add(store, "run", partial.Id, partial);

        using var response = await client.PostAsJsonAsync(url, req);
        response.EnsureSuccessStatusCode();
        var run = await response.Content.ReadFromJsonAsync<ExtractionRun>();
        Assert.NotNull(run);
        Assert.Equal(
            "Verified dock address",
            run.Results.Single(r => r.DocumentId == corrected.Id).Fields.Single().Value
        );
        var result = run.Results.Single(r => r.DocumentId == changed.Id).Fields.Single();
        Assert.Equal("15 Example Rd.", result.Value);
        Assert.Equal(page2.Id, Assert.Single(result.Evidence).PageId);
        Assert.Equal(1, app.Transport.Calls);
    }

    private sealed class Host : WebApplicationFactory<Program>
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(),
            "paprika-ai-intake-" + Guid.NewGuid().ToString("N")
        );
        public Fake Transport { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(
                (_, cfg) =>
                    cfg.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["paprika:DataDirectory"] = root,
                            ["paprika:Operations:Mode"] = "local",
                            ["paprika:OpenAiApiKey"] = "mock-not-a-secret",
                            ["paprika:OpenAiBaseUrl"] = "https://mock-provider.example/v1",
                        }
                    )
            );
            builder.ConfigureServices(services =>
                services
                    .AddHttpClient("providers")
                    .ConfigurePrimaryHttpMessageHandler(() => Transport)
            );
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing)
                return;
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private sealed class Fake : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Assert.Equal(
                "https://mock-provider.example/v1/responses",
                request.RequestUri!.ToString()
            );
            Calls++;
            var fields = JsonSerializer.Serialize(
                new
                {
                    address = new { value = "15 Example Rd.", quote = "Pickup at 15 Example Rd." },
                }
            );
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        new
                        {
                            status = "completed",
                            output = new[]
                            {
                                new
                                {
                                    type = "message",
                                    content = new[] { new { type = "output_text", text = fields } },
                                },
                            },
                        }
                    ),
                }
            );
        }
    }
}
