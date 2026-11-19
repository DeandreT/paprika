using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Xunit;

namespace paprika.Tests;

public sealed class DurabilityTests : IDisposable
{
    private readonly string root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp",
        "opencode",
        "durability-" + Guid.NewGuid().ToString("N")
    );
    private IConfiguration Config =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["paprika:DataDirectory"] = root }
            )
            .Build();

    private Store NewStore() => new(Config);

    private IHost Host(Store store) =>
        MessagingTestHost.Create(
            store,
            new Providers(new Settings(Config), store, new NoNetwork())
        );

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("Unexpected network.");
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "jobs.sqlite"),
            }.ToString()
        );
        c.Open();
        return c;
    }

    private void Sql(string sql)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = sql;
        q.ExecuteNonQuery();
    }

    private long Count(string table)
    {
        using var c = Open();
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM " + table;
        return (long)q.ExecuteScalar()!;
    }

    private static async Task Until(Func<bool> predicate, int seconds = 30)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!predicate())
            await Task.Delay(30, deadline.Token);
    }

    private static Packet Approved(Store store)
    {
        // Prepare pre-existing approved history, not a queued request.
        var p = new Packet
        {
            Status = "review",
            Mode = "demo",
            Name = "Durability fixture",
        };
        var source = new Source
        {
            Name = "fixture.pdf",
            Extension = ".pdf",
            PageCount = 1,
        };
        p.Sources.Add(source);
        Directory.CreateDirectory(Path.Combine(store.Root, p.Id));
        Pdfs.Synthetic(store.SourcePath(p, source), ["BILL OF LADING TEST"]);
        source.Sha256 = Pdfs.SourceHash(store.SourcePath(p, source));
        var page = Worker.MakePage(p, source, 1, "BILL OF LADING TEST");
        page.PredictedType = "bol";
        p.Pages.Add(page);
        p.Documents = Worker.Assemble(p.Pages);
        store.Save(p);
        return store.Review(p.Id, new(p.Revision, p.Documents, true)).Packet!;
    }

    [Fact]
    public async Task AcceptanceDeliveryAndRetryRollbackBusinessStateWhenEnvelopeInsertFails()
    {
        var store = NewStore();
        using var host = Host(store);
        await host.StartAsync();
        var approved = Approved(store);
        var failed = new Packet { Status = "failed" };
        store.Save(failed);
        Sql(
            "CREATE TRIGGER reject_envelope BEFORE INSERT ON wolverine_incoming_envelopes BEGIN SELECT RAISE(ABORT, 'test rollback'); END;"
        );
        var p = new Packet();
        Assert.ThrowsAny<Exception>(() => store.Accept(p, "atomic-key", new WorkflowSelection()));
        Assert.Null(store.Get(p.Id));
        Assert.Equal(0, Count("intake_keys"));
        Assert.ThrowsAny<Exception>(() => store.Retry(failed.Id));
        Assert.Equal("failed", store.Get(failed.Id)!.Status);
        Assert.ThrowsAny<Exception>(() =>
            host
                .Services.GetRequiredService<DeliveryStore>()
                .Queue(approved.Id, new(approved.Revision))
        );
        Assert.Equal(0, Count("deliveries"));
        Assert.Equal(0, Count("queue_work"));
        Assert.Equal(0, Count("wolverine_incoming_envelopes"));
        Sql("DROP TRIGGER reject_envelope;");
        Assert.Equal(200, store.Accept(p, "atomic-key", new WorkflowSelection()).Code);
        Assert.Equal("general-logistics", store.Get(p.Id)!.Workflow!.Id);
        await Until(() => store.Get(p.Id)!.Status == "review");
        Assert.Equal(200, store.Accept(new Packet(), "atomic-key", new WorkflowSelection()).Code);
        Assert.Equal(1, Count("queue_work"));
        await host.StopAsync();
    }

    [Fact]
    public async Task ReprocessAndDeliveryRetryRollbackStateAndStagingWithEnvelope()
    {
        var store = NewStore();
        var p = Approved(store);
        var d = new DeliveryStore(store).Queue(p.Id, new(p.Revision)).Delivery!;
        Sql("UPDATE deliveries SET response=json_set(response,'$.status','failed');");
        using var host = Host(store);
        await host.StartAsync();
        var directories = Directory.GetDirectories(root).Order().ToArray();
        var deliveries = host.Services.GetRequiredService<DeliveryStore>();
        Sql(
            "CREATE TRIGGER reject_envelope BEFORE INSERT ON wolverine_incoming_envelopes BEGIN SELECT RAISE(ABORT, 'test rollback'); END;"
        );
        Assert.ThrowsAny<Exception>(() => store.Reprocess(p.Id, p.Revision, new Settings(Config)));
        Assert.Equal(directories, Directory.GetDirectories(root).Order().ToArray());
        Assert.Single(store.All());
        Assert.Equal("approved", store.Get(p.Id)!.Status);
        Assert.ThrowsAny<Exception>(() => deliveries.Retry(d.Id));
        Assert.Equal("failed", deliveries.Get(d.Id)!.Status);
        Assert.Equal(0, Count("queue_work"));
        Assert.Equal(0, Count("wolverine_incoming_envelopes"));
        Sql("DROP TRIGGER reject_envelope;");
        Assert.Equal(200, deliveries.Retry(d.Id).Code);
        await Until(() => deliveries.Get(d.Id)!.Status == "delivered");
        await host.StopAsync();
    }

    [Fact]
    public async Task ScheduledRetriesSurviveRestartExhaustIntoRealDeadLetterAndReplayOnce()
    {
        var store = NewStore();
        var approved = Approved(store);
        var history = JsonSerializer.Serialize(store.History(approved.Id), Contract.Json);
        string id;
        using (var host = Host(store))
        {
            await host.StartAsync();
            var deliveries = host.Services.GetRequiredService<DeliveryStore>();
            id = deliveries
                .Queue(approved.Id, new(approved.Revision, "transient-always"))
                .Delivery!.Id;
            await Until(() => deliveries.Get(id)!.Attempts >= 1);
            var summary = await host
                .Services.GetRequiredService<MessagingOperations>()
                .Read(default);
            Assert.True(summary.Queues.Single(q => q.Name == "delivery").Pending >= 1);
            await host.StopAsync();
        }
        store = NewStore();
        Guid firstLetter;
        using (var host = Host(store))
        {
            await host.StartAsync();
            var deliveries = host.Services.GetRequiredService<DeliveryStore>();
            var operations = host.Services.GetRequiredService<MessagingOperations>();
            try
            {
                await Until(() => deliveries.Get(id)!.Status == "failed");
            }
            catch (OperationCanceledException)
            {
                Assert.Fail(
                    $"{JsonSerializer.Serialize(deliveries.Get(id))}; inbox={Count("wolverine_incoming_envelopes")}; dead={Count("wolverine_dead_letters")}"
                );
            }
            await Until(() =>
                operations.Read(default).GetAwaiter().GetResult().DeadLetters.Length == 1
            );
            var summary = await operations.Read(default);
            var letter = Assert.Single(summary.DeadLetters);
            firstLetter = Guid.Parse(letter.Id);
            Assert.NotEqual(default, letter.FailedAt);
            Assert.Equal(id, letter.DeliveryId);
            Assert.Null(letter.PacketId);
            Assert.Equal("ProcessDelivery", letter.MessageType);
            Assert.Equal(4, deliveries.Get(id)!.Attempts);
            Assert.Equal(0, summary.Queues.Single(q => q.Name == "delivery").Pending);
            Assert.DoesNotContain("Exception", JsonSerializer.Serialize(summary));
            Assert.Equal(
                history,
                JsonSerializer.Serialize(store.History(approved.Id), Contract.Json)
            );
            Assert.NotNull(
                await host
                    .Services.GetRequiredService<IWolverineRuntime>()
                    .Storage.DeadLetters.DeadLetterEnvelopeByIdAsync(firstLetter)
            );
            await host.StopAsync();
        }
        store = NewStore();
        using (var host = Host(store))
        {
            await host.StartAsync();
            var deliveries = host.Services.GetRequiredService<DeliveryStore>();
            var operations = host.Services.GetRequiredService<MessagingOperations>();
            Assert.Equal(
                firstLetter.ToString(),
                Assert.Single((await operations.Read(default)).DeadLetters).Id
            );
            Assert.Equal(200, await operations.Replay(firstLetter, default));
            Assert.Equal(409, await operations.Replay(firstLetter, default));
            await Until(() => deliveries.Get(id)!.Status == "failed");
            await Until(() =>
                operations.Read(default).GetAwaiter().GetResult().DeadLetters.Length == 1
            );
            Assert.NotEqual(
                firstLetter.ToString(),
                Assert.Single((await operations.Read(default)).DeadLetters).Id
            );
            Assert.Equal(8, deliveries.Get(id)!.Attempts);
            Assert.Equal(409, await operations.Replay(firstLetter, default));
            Assert.Equal(404, await operations.Replay(Guid.NewGuid(), default));
            Assert.Equal(
                history,
                JsonSerializer.Serialize(store.History(approved.Id), Contract.Json)
            );
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task LegacyPendingRowsMigrateOnceAndOldDeliveryDatabaseAndArtifactRemain()
    {
        var legacyStore = NewStore();
        var pending = Worker.Demo(legacyStore);
        var approved = Approved(legacyStore);
        var old = new DeliveryStore(legacyStore);
        var completed = old.Queue(approved.Id, new(approved.Revision)).Delivery!;
        old.Process(completed.Id);
        var artifact = File.ReadAllBytes(old.ArtifactPath(completed));
        var other = Approved(legacyStore);
        var queued = old.Queue(other.Id, new(other.Revision)).Delivery!;
        var legacy = Path.Combine(root, "deliveries", "outbox.sqlite");
        using (var c = Open())
        using (var q = c.CreateCommand())
        {
            q.CommandText =
                "ATTACH DATABASE $legacy AS old; CREATE TABLE old.deliveries AS SELECT * FROM main.deliveries; DROP TABLE main.deliveries; DETACH DATABASE old;";
            q.Parameters.AddWithValue("$legacy", legacy);
            q.ExecuteNonQuery();
        }
        var history = JsonSerializer.Serialize(legacyStore.History(approved.Id), Contract.Json);
        for (var start = 0; start < 2; start++)
        {
            var store = NewStore();
            using var host = Host(store);
            await host.StartAsync();
            var deliveries = host.Services.GetRequiredService<DeliveryStore>();
            await Until(() =>
                store.Get(pending.Id)!.Status == "review"
                && deliveries.Get(queued.Id)!.Status == "delivered"
            );
            Assert.Single(store.Get(pending.Id)!.Audit, a =>
                a.Action == "processed; manual review required"
            );
            Assert.Equal(1, deliveries.Get(queued.Id)!.Attempts);
            Assert.Equal(2, Count("queue_work"));
            Assert.Equal(artifact, File.ReadAllBytes(deliveries.ArtifactPath(completed)));
            Assert.Equal(
                history,
                JsonSerializer.Serialize(store.History(approved.Id), Contract.Json)
            );
            Assert.True(File.Exists(legacy));
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task DurableDuplicateCommandsDoNotRepeatApplicationEffects()
    {
        var store = NewStore();
        using var host = Host(store);
        await host.StartAsync();
        var packet = Worker.Demo(store);
        using var scope = host.Services.CreateScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        for (int i = 0; i < 8; i++)
            await bus.SendAsync(new ProcessPacket(packet.Id));
        await Until(() => store.Get(packet.Id)!.Status == "review");
        packet = store.Get(packet.Id)!;
        Assert.Single(packet.Audit, a => a.Action == "processed; manual review required");
        packet = store.Review(packet.Id, new(packet.Revision, packet.Documents, true)).Packet!;
        var before = JsonSerializer.Serialize(packet, Contract.Json);
        await bus.SendAsync(new ProcessPacket(packet.Id));
        var deliveries = host.Services.GetRequiredService<DeliveryStore>();
        var d = deliveries.Queue(packet.Id, new(packet.Revision, "transient-once")).Delivery!;
        await Until(() => deliveries.Get(d.Id)!.Status == "delivered");
        for (int i = 0; i < 8; i++)
            await bus.SendAsync(new ProcessDelivery(d.Id));
        await Until(() =>
            host
                .Services.GetRequiredService<MessagingOperations>()
                .Read(default)
                .GetAwaiter()
                .GetResult()
                .Queues.All(q => q.Pending == 0)
        );
        Assert.Equal(2, deliveries.Get(d.Id)!.Attempts);
        Assert.Equal(before, JsonSerializer.Serialize(store.Get(packet.Id), Contract.Json));
        await host.StopAsync();
    }

    [Fact]
    public async Task FailureCheckpointBeforeDeadLetterWriteRecoversWithoutResurrection()
    {
        var store = NewStore();
        var p = new Packet { Mode = "live" }; // No configured providers: permanent, sanitized failure.
        AddFixtureSource(store, p);
        using (var host = Host(store))
        {
            await host.StartAsync();
            Sql(
                "CREATE TRIGGER stop_fault_write BEFORE INSERT ON wolverine_dead_letters BEGIN SELECT RAISE(ABORT, 'test crash window'); END;"
            );
            store.Accept(p, null);
            await Until(() => store.Get(p.Id)!.Status == "failed");
            await host.StopAsync();
            Assert.Equal(0, Count("wolverine_dead_letters"));
            Assert.True(Count("wolverine_incoming_envelopes") >= 1);
        }
        Sql("DROP TRIGGER stop_fault_write;");
        store = NewStore();
        using (var host = Host(store))
        {
            await host.StartAsync();
            var operations = host.Services.GetRequiredService<MessagingOperations>();
            await Until(() =>
                operations.Read(default).GetAwaiter().GetResult().DeadLetters.Length == 1
            );
            Assert.Equal("failed", store.Get(p.Id)!.Status);
            Assert.Single(store.Get(p.Id)!.Audit, a =>
                a.Action == "processing failed; durable dead letter"
            );
            var letter = Assert.Single((await operations.Read(default)).DeadLetters);
            Assert.Equal(p.Id, letter.PacketId);
            Assert.Equal(
                0,
                (await operations.Read(default)).Queues.Single(q => q.Name == "processing").Pending
            );
            // Repair the test fixture, then replay through the actual durable API.
            p = store.Get(p.Id)!;
            p.Mode = "demo";
            store.Save(p);
            Assert.Equal(200, await operations.Replay(Guid.Parse(letter.Id), default));
            Assert.Equal(409, await operations.Replay(Guid.Parse(letter.Id), default));
            await Until(() => store.Get(p.Id)!.Status == "review");
            Assert.Equal(409, await operations.Replay(Guid.Parse(letter.Id), default));
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task PacketReplayRejectsCancelledApprovedAndStaleGenerations()
    {
        var store = NewStore();
        using var host = Host(store);
        await host.StartAsync();
        var p = new Packet { Mode = "live" };
        AddFixtureSource(store, p);
        store.Accept(p, null);
        var operations = host.Services.GetRequiredService<MessagingOperations>();
        await Until(() =>
            operations.Read(default).GetAwaiter().GetResult().DeadLetters.Length == 1
        );
        var letter = Assert.Single((await operations.Read(default)).DeadLetters);
        var id = Guid.Parse(letter.Id);
        p = store.Get(p.Id)!;
        p.Status = "approved";
        store.Save(p);
        Assert.Equal(409, await operations.Replay(id, default));
        p.Status = "queued";
        store.Save(p);
        Assert.Equal(409, await operations.Replay(id, default));
        store.Cancel(p.Id, p.Revision);
        Assert.Equal(409, await operations.Replay(id, default));
        using var scope = host.Services.CreateScope();
        await scope
            .ServiceProvider.GetRequiredService<IMessageBus>()
            .SendAsync(new ProcessPacket(p.Id));
        await Until(() =>
            operations.Read(default).GetAwaiter().GetResult().Queues.All(q => q.Pending == 0)
        );
        Assert.Equal("cancelled", store.Get(p.Id)!.Status);
        await host.StopAsync();
    }

    private static void AddFixtureSource(Store store, Packet p)
    {
        var source = new Source { Extension = ".pdf", PageCount = 1 };
        var path = store.SourcePath(p, source);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Pdfs.Synthetic(path, ["Synthetic failure-test fixture"]);
        source.Sha256 = Pdfs.SourceHash(path);
        p.Sources.Add(source);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, 4)]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    public async Task ProviderFailuresUseBoundedWolverineRetriesAndCanReplayAfterRepair(
        HttpStatusCode status,
        int attempts
    )
    {
        var store = NewStore();
        var p = Worker.Demo(store);
        p.Mode = "live";
        p.Sources[0].OperationUrl = "https://ocr.example.test/operations/saved";
        store.Save(p);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["paprika:AzureEndpoint"] = "https://ocr.example.test",
                    ["paprika:AzureKey"] = "test",
                    ["paprika:JevApiKey"] = "test",
                }
            )
            .Build();
        using var transport = new FailingProvider(status, p);
        using var host = MessagingTestHost.Create(
            store,
            new Providers(new Settings(config), store, transport)
        );
        await host.StartAsync();
        var operations = host.Services.GetRequiredService<MessagingOperations>();
        await Until(() =>
            operations.Read(default).GetAwaiter().GetResult().DeadLetters.Length == 1
        );
        Assert.Equal(attempts, transport.Calls);
        Assert.Equal("failed", store.Get(p.Id)!.Status);
        var letter = Assert.Single((await operations.Read(default)).DeadLetters);
        Assert.DoesNotContain(
            "private-provider-marker",
            JsonSerializer.Serialize(await operations.Read(default))
        );
        Assert.DoesNotContain("private-provider-marker", store.Get(p.Id)!.Error!);
        transport.Repaired = true;
        Assert.Equal(200, await operations.Replay(Guid.Parse(letter.Id), default));
        await Until(() => store.Get(p.Id)!.Status == "review");
        Assert.Equal(attempts + 1, transport.Calls);
        Assert.Equal(
            p.Pages.Select(page => page.Id),
            store.Get(p.Id)!.Pages.Select(page => page.Id)
        );
        Assert.Equal(409, await operations.Replay(Guid.Parse(letter.Id), default));
        await host.StopAsync();
    }

    private sealed class FailingProvider(HttpStatusCode status, Packet packet)
        : HttpMessageHandler,
            IHttpClientFactory
    {
        public int Calls;
        public bool Repaired;

        public HttpClient CreateClient(string name) => new(this, false);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Interlocked.Increment(ref Calls);
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(
                !Repaired
                    ? new HttpResponseMessage(status)
                    {
                        Content = new StringContent("private-provider-marker"),
                    }
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(
                                new
                                {
                                    status = "succeeded",
                                    analyzeResult = new
                                    {
                                        pages = packet.Pages.Select(p => new
                                        {
                                            pageNumber = p.PageNumber,
                                            lines = new[] { new { content = p.Text } },
                                        }),
                                    },
                                }
                            )
                        ),
                    }
            );
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
