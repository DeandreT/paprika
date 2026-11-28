using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using paprika;
using Xunit;

namespace paprika.Tests;

public sealed class OperationsApiTests
{
    private const string Token = "synthetic-operations-api-token-for-tests-only-12345";

    [Fact]
    public async Task FieldCorrectionRequiresWorkflowAccessAndClaimForThePacketRevision()
    {
        await using var factory = new OperationsFactory(enableRetention: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-paprika", "1");
        var store = factory.Services.GetRequiredService<Store>();
        var page = new Page { Text = "Invoice: lNV-001" };
        var packet = new Packet
        {
            Status = "review",
            Pages = [page],
            Documents = [new Document { Type = "invoice", PageIds = [page.Id] }],
        };
        store.Save(packet);
        var original = ExtractionPlatform.Extract(packet, ExtractionPlatformTests.Definition());
        ExtractionStorage.Add(store, "run", original.Id, original);
        var request = new ExtractionCorrection(
            packet.Revision,
            original.Id,
            packet.Documents[0].Id,
            "invoice",
            "INV-001",
            "Correct the OCR spelling.",
            Assert.Single(original.Results).Fields.Single(f => f.Name == "invoice").Evidence,
            Category: "transcription"
        );
        var url = $"/api/extraction/{packet.Id}";
        using var anonymous = await client.PostAsJsonAsync($"{url}/corrections", request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        var foreign = new Packet
        {
            Status = "review",
            Workflow = WorkflowRegistry.Legacy() with { Id = "foreign-workflow" },
        };
        store.Save(foreign);
        var privateUrl = $"/api/extraction/{foreign.Id}";
        using var hidden = await client.GetAsync(privateUrl);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var denied = await client.PostAsJsonAsync($"{privateUrl}/corrections", request);
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        var archived = new Packet
        {
            Status = "approved",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-120),
        };
        store.Save(archived);
        factory
            .Services.GetRequiredService<OperationsPlatformStore>()
            .Archive(
                new(archived.Id, archived.Revision, true),
                new("admin", ["administrator"], [])
            );
        var archivedUrl = $"/api/extraction/{archived.Id}";
        using var archivedHistory = await client.GetAsync(archivedUrl);
        Assert.Equal(HttpStatusCode.NotFound, archivedHistory.StatusCode);
        using var archivedWrite = await client.PostAsJsonAsync(
            $"{archivedUrl}/corrections",
            request
        );
        Assert.Equal(HttpStatusCode.NotFound, archivedWrite.StatusCode);
        using var unclaimed = await client.PostAsJsonAsync($"{url}/corrections", request);
        Assert.Equal(HttpStatusCode.Conflict, unclaimed.StatusCode);
        var before = (await client.GetFromJsonAsync<ExtractionHistoryResponse>(url))!;
        Assert.Empty(before.Corrections);

        var tasks = (
            await client.GetFromJsonAsync<OperationsTask[]>("/api/operations-platform/tasks")
        )!;
        var task = Assert.Single(tasks, t =>
            t.PacketId == packet.Id && t.PacketRevision == packet.Revision
        );
        using var claim = await client.PostAsJsonAsync(
            $"/api/operations-platform/tasks/{task.Id}/claim",
            new OperationsTaskChange(task.Revision)
        );
        claim.EnsureSuccessStatusCode();
        using var success = await client.PostAsJsonAsync($"{url}/corrections", request);
        success.EnsureSuccessStatusCode();
        var after = (await client.GetFromJsonAsync<ExtractionHistoryResponse>(url))!;
        Assert.Null(after.CurrentConfiguredRunId);
        Assert.Equal("alice", Assert.Single(after.Corrections).GetProperty("actor").GetString());
    }

    [Fact]
    public async Task SharedReviewRequiresClaimAndCompletesThenReopensNextRevision()
    {
        await using var factory = new OperationsFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-paprika", "1");
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        var store = factory.Services.GetRequiredService<Store>();
        var page = new Page();
        var packet = new Packet
        {
            Status = "review",
            Pages = [page],
            Documents = [new Document { PageIds = [page.Id] }],
        };
        store.Save(packet);
        var review = new ReviewRequest(packet.Revision, packet.Documents, false);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.PutAsJsonAsync($"/api/packets/{packet.Id}/review", review)).StatusCode
        );
        var task = Assert.Single(
            (await client.GetFromJsonAsync<OperationsTask[]>("/api/operations-platform/tasks"))!
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PostAsJsonAsync(
                    $"/api/operations-platform/tasks/{task.Id}/claim",
                    new OperationsTaskChange(task.Revision)
                )
            ).StatusCode
        );
        var saved = await client.PutAsJsonAsync($"/api/packets/{packet.Id}/review", review);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var tasks = (
            await client.GetFromJsonAsync<OperationsTask[]>("/api/operations-platform/tasks")
        )!;
        Assert.Equal("completed", tasks.Single(t => t.Id == task.Id).State);
        Assert.Equal("review saved", tasks.Single(t => t.Id == task.Id).Outcome);
        Assert.Equal(2, tasks.Single(t => t.State == "available").PacketRevision);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await client.PutAsJsonAsync(
                    $"/api/packets/{packet.Id}/review",
                    review with
                    {
                        Revision = 2,
                    }
                )
            ).StatusCode
        );
        var next = tasks.Single(t => t.State == "available");
        await client.PostAsJsonAsync(
            $"/api/operations-platform/tasks/{next.Id}/claim",
            new OperationsTaskChange(next.Revision)
        );
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PutAsJsonAsync(
                    $"/api/packets/{packet.Id}/review",
                    review with
                    {
                        Revision = 2,
                        Approve = true,
                    }
                )
            ).StatusCode
        );
        // A later approved-packet correction is a deliberate new task, not an auth bypass.
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PostAsJsonAsync(
                    "/api/operations-platform/tasks/open",
                    new OperationsOpenReview(packet.Id, 3)
                )
            ).StatusCode
        );
        Assert.Contains(
            (await client.GetFromJsonAsync<OperationsTask[]>("/api/operations-platform/tasks"))!,
            t => t.PacketRevision == 3 && t.State == "available"
        );
    }

    [Fact]
    public async Task SharedPacketListsAndEveryLegacyPacketResourceRespectWorkflowBoundary()
    {
        await using var factory = new OperationsFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        var store = factory.Services.GetRequiredService<Store>();
        var allowed = new Packet { Status = "review" };
        var denied = new Packet
        {
            Status = "review",
            Workflow = WorkflowRegistry.Legacy() with { Id = "foreign-workflow" },
        };
        store.Save(allowed);
        store.Save(denied);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/config")).StatusCode);
        var packets = (await client.GetFromJsonAsync<PacketResponse[]>("/api/packets"))!;
        Assert.Equal(allowed.Id, Assert.Single(packets).Id);
        foreach (
            var suffix in new[]
            {
                "",
                "/history",
                "/sources/private-source",
                "/export",
                "/documents/private-document/download",
            }
        )
            Assert.Equal(
                HttpStatusCode.NotFound,
                (await client.GetAsync($"/api/packets/{denied.Id}{suffix}")).StatusCode
            );
        var catalog = (await client.GetFromJsonAsync<WorkflowCatalogResponse>("/api/workflows"))!;
        Assert.All(catalog.Entries.Where(e => e.Kind == "workflow"), e =>
            Assert.Equal("general-logistics", e.Id)
        );
        Assert.DoesNotContain(catalog.Entries, e =>
            e.Id is "invoice-processing" or "receiving" or "invoice-documents"
        );
        Assert.All(catalog.Stats, s =>
            Assert.Contains(s.Id, new[] { "general-logistics", "legacy-logistics" })
        );
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/operations-platform/retention")).StatusCode
        );
    }

    [Fact]
    public async Task SharedCatalogReportsOnlyClassifiersUsedByReadablePublishedLiveWorkflows()
    {
        await using var factory = new OperationsFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", Token);
        var registry = factory.Services.GetRequiredService<Store>().Workflows;
        var all = registry.Catalog();
        Assert.True(all.CanUpload);
        var jev = all.ClassifierProviders.Single(p => p.Id == "jev");
        var openai = all.ClassifierProviders.Single(p => p.Id == "openai");
        var copy = registry.Clone("workflow", "general-logistics", 1, 1, "Outside the allowlist");
        void SaveLive(CatalogEntry entry, ClassifierProviderInfo classifier)
        {
            entry = registry.Edit(
                entry.Kind,
                entry.Id,
                entry.Version,
                entry with
                {
                    Workflow = entry.Workflow! with
                    {
                        Mode = "live",
                        ProviderProfile = classifier.Id,
                        Model = classifier.Model,
                    },
                }
            );
            registry.Publish(entry.Kind, entry.Id, entry.Version, entry.Revision);
        }
        SaveLive(copy, openai);
        SaveLive(registry.NewVersion("workflow", "general-logistics", 1, 1), jev);
        // Older installations may be missing the profile entirely on a published live workflow.
        using (
            var c = new Microsoft.Data.Sqlite.SqliteConnection(
                factory.Services.GetRequiredService<Store>().ConnectionString
            )
        )
        {
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText = """
                UPDATE workflow_catalog SET json=json_remove(json,'$.workflow.providerProfile')
                WHERE kind='workflow' AND id='general-logistics' AND version=2
                """;
            Assert.Equal(1, q.ExecuteNonQuery());
        }
        var limited = (await client.GetFromJsonAsync<WorkflowCatalogResponse>("/api/workflows"))!;
        Assert.Equal(jev, Assert.Single(limited.ClassifierProviders));
        Assert.Equal(
            jev.Id,
            limited
                .Entries.Single(e => e.Kind == "workflow" && e.Version == 2)
                .ResolvedClassifierProvider
        );
        Assert.False(limited.CanUpload);
        Assert.Empty(limited.ProviderProfiles);
        Assert.Empty(limited.LiveModel);
    }

    [Fact]
    public async Task CookieSignInAuthenticatesExistingApisAndSignOutRevokesBrowserCookie()
    {
        await using var factory = new OperationsFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-paprika", "1");
        var session = (
            await client.GetFromJsonAsync<OperationsSessionResponse>(
                "/api/operations-platform/session"
            )
        )!;
        Assert.False(session.Authenticated);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/packets")).StatusCode
        );
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/operations-platform/session"
        )
        {
            Content = JsonContent.Create(new { }),
        };
        request.Headers.Authorization = new("Bearer", Token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(Token, await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/packets")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.DeleteAsync("/api/operations-platform/session")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/packets")).StatusCode
        );
    }

    private sealed class OperationsFactory(bool enableRetention = false)
        : WebApplicationFactory<Program>
    {
        private readonly string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp",
            "opencode",
            "operations-tests",
            "api-" + Guid.NewGuid().ToString("N")
        );

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(
                (_, config) =>
                    config.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["paprika:DataDirectory"] = root,
                            ["paprika:Operations:Mode"] = "shared",
                            ["paprika:Operations:Retention:ExecutionEnabled"] =
                                enableRetention.ToString(),
                            ["paprika:Operations:Users:0:Name"] = "alice",
                            ["paprika:Operations:Users:0:Token"] = Token,
                            ["paprika:Operations:Users:0:Roles:0"] = "reviewer",
                            ["paprika:Operations:Users:0:Workflows:0"] = "legacy-logistics",
                            ["paprika:Operations:Users:0:Workflows:1"] = "general-logistics",
                        }
                    )
            );
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory, NoNetwork>();
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private sealed class NoNetwork : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, false);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            throw new InvalidOperationException("Network access is forbidden in operations tests.");
    }
}
