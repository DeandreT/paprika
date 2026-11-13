using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaperworkClassifier;
using Wolverine;
using Xunit;

namespace PaperworkClassifier.Tests;

public sealed class OperationsContinuationMessagingTests
{
    private static Packet ReviewPacket(Store store)
    {
        var page = new Page { Text = "synthetic continuation output", PredictedType = "unknown" };
        var packet = new Packet
        {
            Status = "review",
            Pages = [page],
            Documents = [new Document { PageIds = [page.Id] }],
            Workflow = WorkflowRegistry.Legacy() with
            {
                Capabilities = new(
                    Output: new("text-json"),
                    Flow: new(new("Approved output", [new("output", FlowBlockKind.Output, [])]))
                ),
            },
        };
        store.Save(packet);
        return packet;
    }

    private static async Task Wait(Func<Task<bool>> ready, int seconds = 40)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!await ready())
            await Task.Delay(100, timeout.Token);
    }

    private static string WorkId(Store store, string packetId)
    {
        using var c = new SqliteConnection(store.ConnectionString);
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT work_id FROM queue_work WHERE kind='continuation' AND id=$id";
        q.Parameters.AddWithValue("$id", packetId);
        return (string)q.ExecuteScalar()!;
    }

    [Fact]
    public async Task ApprovalCommitsDurableContinuationAndProducesExactlyOneOutputWithDuplicateAndStaleNoOps()
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaperworkClassifier", "1");
        var store = factory.Services.GetRequiredService<Store>();
        var packet = ReviewPacket(store);
        var response = await client.PutAsJsonAsync(
            $"/api/packets/{packet.Id}/review",
            new ReviewRequest(packet.Revision, packet.Documents, true)
        );
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var approved = store.Get(packet.Id)!;
        Assert.Equal("approved", approved.Status);
        Assert.Equal(2, approved.Revision);
        await Wait(async () =>
            (await client.GetFromJsonAsync<OutputArtifact[]>($"/api/outputs/{packet.Id}"))!.Length
            == 1
        );
        var output = Assert.Single(
            (await client.GetFromJsonAsync<OutputArtifact[]>($"/api/outputs/{packet.Id}"))!
        );
        Assert.Equal(approved.Revision, output.Revision);
        Assert.False(output.Preview);
        var workId = WorkId(store, packet.Id);
        await using var scope = factory.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        await Task.WhenAll(
            Enumerable
                .Range(0, 6)
                .Select(_ =>
                    bus.InvokeAsync(new ContinuePlatformFlow(packet.Id, approved.Revision, workId))
                )
        );
        await bus.InvokeAsync(new ContinuePlatformFlow(packet.Id, approved.Revision - 1, workId));
        await bus.InvokeAsync(
            new ContinuePlatformFlow(packet.Id, approved.Revision, "obsolete-generation")
        );
        await bus.InvokeAsync(new ContinuePlatformFlow("missing", approved.Revision));
        Assert.Single(
            (await client.GetFromJsonAsync<OutputArtifact[]>($"/api/outputs/{packet.Id}"))!
        );
        Assert.Equal("approved", store.Get(packet.Id)!.Status);
        Assert.Equal(
            "completed",
            factory
                .Services.GetRequiredService<FlowEngine>()
                .Get(approved)!
                .Executions.Single()
                .Status
        );
        var messaging = (await client.GetFromJsonAsync<MessagingSummary>("/api/messaging"))!;
        Assert.Contains(messaging.Queues, q => q.Name == "continuation");
        Assert.Empty(messaging.DeadLetters);
    }

    [Fact]
    public async Task FailedApprovedContinuationIsSanitizedVisibleAndReplayableWithoutDowngradingApproval()
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaperworkClassifier", "1");
        var store = factory.Services.GetRequiredService<Store>();
        var packet = ReviewPacket(store);
        var obstacle = Path.Combine(store.Root, "outputs");
        File.WriteAllText(obstacle, "synthetic-private-path-marker");
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await client.PutAsJsonAsync(
                    $"/api/packets/{packet.Id}/review",
                    new ReviewRequest(packet.Revision, packet.Documents, true)
                )
            ).StatusCode
        );
        DeadLetterSummary? letter = null;
        await Wait(async () =>
        {
            var summary = (await client.GetFromJsonAsync<MessagingSummary>("/api/messaging"))!;
            letter = summary.DeadLetters.SingleOrDefault(l =>
                l.PacketId == packet.Id && l.MessageType == nameof(ContinuePlatformFlow)
            );
            return letter is not null;
        });
        Assert.Equal(2, letter!.PacketRevision);
        Assert.DoesNotContain(store.Root, letter.Reason);
        Assert.DoesNotContain("synthetic-private-path-marker", letter.Reason);
        var approved = store.Get(packet.Id)!;
        Assert.Equal("approved", approved.Status);
        Assert.Equal(2, approved.Revision);
        Assert.Null(approved.Error);
        var oldWorkId = WorkId(store, packet.Id);
        // Even if a terminal state row is inconsistent, the actual retained current-
        // generation dead letter still protects the packet from retention.
        using (var connection = new SqliteConnection(store.ConnectionString))
        {
            connection.Open();
            using var q = connection.CreateCommand();
            q.CommandText = "UPDATE continuation_work SET state='completed' WHERE packet_id=$id";
            q.Parameters.AddWithValue("$id", packet.Id);
            q.ExecuteNonQuery();
            Assert.Contains(
                factory
                    .Services.GetRequiredService<OperationsPlatformStore>()
                    .Preview()
                    .Single(p => p.PacketId == packet.Id)
                    .Reasons,
                r => r.Contains("replayable durable work")
            );
            q.CommandText = "UPDATE continuation_work SET state='failed' WHERE packet_id=$id";
            q.ExecuteNonQuery();
        }
        File.Delete(obstacle);
        var replay = await client.PostAsync($"/api/messaging/deadletters/{letter.Id}/replay", null);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal("approved", store.Get(packet.Id)!.Status);
        Assert.NotEqual(oldWorkId, WorkId(store, packet.Id));
        await Wait(async () =>
            (await client.GetFromJsonAsync<OutputArtifact[]>($"/api/outputs/{packet.Id}"))!.Length
            == 1
        );
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await client.PostAsync($"/api/messaging/deadletters/{letter.Id}/replay", null)
            ).StatusCode
        );
        await using var scope = factory.Services.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync(new ContinuePlatformFlow(packet.Id, approved.Revision, oldWorkId));
        Assert.Single(
            (await client.GetFromJsonAsync<OutputArtifact[]>($"/api/outputs/{packet.Id}"))!
        );
        Assert.Equal("approved", store.Get(packet.Id)!.Status);
    }

    [Fact]
    public async Task DeadLetterReplayRejectsChangedApprovedRevision()
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaperworkClassifier", "1");
        var store = factory.Services.GetRequiredService<Store>();
        var packet = ReviewPacket(store);
        File.WriteAllText(Path.Combine(store.Root, "outputs"), "synthetic obstacle");
        await client.PutAsJsonAsync(
            $"/api/packets/{packet.Id}/review",
            new ReviewRequest(packet.Revision, packet.Documents, true)
        );
        DeadLetterSummary? letter = null;
        await Wait(async () =>
        {
            letter = (
                await client.GetFromJsonAsync<MessagingSummary>("/api/messaging")
            )!.DeadLetters.SingleOrDefault(l => l.PacketId == packet.Id);
            return letter is not null;
        });
        var changed = store.Get(packet.Id)!;
        changed.Revision++;
        store.Save(changed);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (
                await client.PostAsync($"/api/messaging/deadletters/{letter!.Id}/replay", null)
            ).StatusCode
        );
        Assert.Equal(changed.Revision, store.Get(packet.Id)!.Revision);
    }

    [Fact]
    public void RetentionIgnoresTerminalGenerationButProtectsActiveContinuationAndRemovesTerminalRowsOnPurge()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp",
            "opencode",
            "operations-tests",
            "continuation-retention-" + Guid.NewGuid().ToString("N")
        );
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Paperwork:DataDirectory"] = root,
                        ["Paperwork:Operations:Retention:ExecutionEnabled"] = "true",
                        ["Paperwork:Operations:Retention:PermanentDeletionEnabled"] = "true",
                    }
                )
                .Build();
            var store = new Store(config);
            var packet = new Packet
            {
                Status = "approved",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-120),
            };
            store.Save(packet);
            using var c = new SqliteConnection(store.ConnectionString);
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText =
                "INSERT INTO queue_work VALUES('packet',$id,'terminal-packet-generation'); CREATE TABLE continuation_work(packet_id TEXT PRIMARY KEY,revision INTEGER,work_id TEXT,state TEXT,error TEXT); INSERT INTO continuation_work VALUES($id,1,'continuation-generation','queued',NULL); INSERT INTO queue_work VALUES('continuation',$id,'continuation-generation')";
            q.Parameters.AddWithValue("$id", packet.Id);
            q.ExecuteNonQuery();
            var operations = new OperationsPlatformStore(store, config);
            var admin = new OperationsIdentity("local", ["administrator"], []);
            Assert.False(Assert.Single(operations.Preview()).Eligible);
            q.CommandText = "UPDATE continuation_work SET state='completed' WHERE packet_id=$id";
            q.ExecuteNonQuery();
            Assert.True(Assert.Single(operations.Preview()).Eligible);
            operations.Archive(new(packet.Id, packet.Revision, true), admin);
            Assert.True(operations.PurgePlan(packet.Id).Eligible);
            operations.Purge(new(packet.Id, packet.Revision, true), admin);
            q.CommandText = "SELECT COUNT(*) FROM queue_work WHERE id=$id";
            Assert.Equal(0L, q.ExecuteScalar());
            q.CommandText = "SELECT COUNT(*) FROM continuation_work WHERE packet_id=$id";
            Assert.Equal(0L, q.ExecuteScalar());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RetentionRecognizesRealDurableStorageAndAllowsDependencyFreeTerminalPacket()
    {
        await using var factory = new Factory();
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<Store>();
        var packet = new Packet
        {
            Status = "approved",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-120),
        };
        store.Save(packet);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Paperwork:Operations:Retention:ExecutionEnabled"] = "true",
                }
            )
            .Build();
        var operations = new OperationsPlatformStore(
            store,
            config,
            factory.Services.GetServices<IOperationsRetentionParticipant>()
        );
        var admin = new OperationsIdentity("local", ["administrator"], []);
        Assert.True(operations.Preview().Single(p => p.PacketId == packet.Id).Eligible);
        operations.Archive(new(packet.Id, packet.Revision, true), admin);
        var plan = operations.PurgePlan(packet.Id);
        Assert.True(plan.Eligible, string.Join(" ", plan.Blockers));
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp",
            "opencode",
            "operations-tests",
            "continuation-api-" + Guid.NewGuid().ToString("N")
        );

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(
                (_, config) =>
                    config.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["Paperwork:DataDirectory"] = root }
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
            SqliteConnection.ClearAllPools();
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
            throw new InvalidOperationException(
                "External requests are forbidden in continuation tests."
            );
    }
}
