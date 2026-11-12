using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Runtime;
using Xunit;

namespace PaperworkClassifier.Tests;

public sealed class MessagingVisibilityTests : IDisposable
{
    private readonly string root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp",
        "opencode",
        "messaging-visibility-" + Guid.NewGuid().ToString("N")
    );
    private readonly DateTimeOffset firstScheduledAt = DateTimeOffset.UtcNow.AddDays(-1);

    private (Store Store, IHost Host) CreateHost()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["Paperwork:DataDirectory"] = root }
            )
            .Build();
        var store = new Store(config);
        return (
            store,
            MessagingTestHost.Create(
                store,
                new Providers(new Settings(config), store, new NoNetwork())
            )
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EligibleLimitIsAppliedAfterPagingPastSupersededAndClaimedLetters(
        bool reverseInsertion
    )
    {
        var (store, host) = CreateHost();
        using (host)
        {
            await host.StartAsync();
            var eligible = new Dictionary<int, Guid>();
            var stale = new List<Guid>();
            var order = Enumerable.Range(0, 302);
            if (reverseInsertion)
                order = order.Reverse();
            foreach (var index in order)
            {
                var disposition =
                    index < 101 ? "superseded"
                    : index < 201 ? "claimed"
                    : "current";
                var letter = await Seed(host, store, index, disposition);
                if (disposition == "current")
                    eligible.Add(index, letter.Id);
                else
                    stale.Add(letter.Id);
            }
            var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
            var letters = runtime.Storage.DeadLetters;
            var firstPage = await letters.QueryAsync(
                new DeadLetterEnvelopeQuery { PageNumber = 1, PageSize = 100 },
                default
            );
            Assert.Equal(100, firstPage.Envelopes.Count);
            Assert.All(firstPage.Envelopes, letter => Assert.Contains(letter.Id, stale));

            var operations = host.Services.GetRequiredService<MessagingOperations>();
            var expected = eligible
                .OrderBy(pair => pair.Key)
                .Take(100)
                .Select(pair => pair.Value.ToString())
                .ToArray();
            var summary = await operations.Read(default);
            Assert.Equal(expected, summary.DeadLetters.Select(letter => letter.Id));
            Assert.Contains(summary.DeadLetters, letter => letter.PacketId is not null);
            Assert.Contains(summary.DeadLetters, letter => letter.DeliveryId is not null);
            Assert.DoesNotContain(
                eligible[301].ToString(),
                summary.DeadLetters.Select(letter => letter.Id)
            );
            // Archival occurs after scanning, so removing two full pages cannot skip later results.
            foreach (var id in stale)
                Assert.NotNull((await letters.DeadLetterEnvelopeByIdAsync(id))!.ArchivedAt);
            Assert.Equal(
                expected,
                (await operations.Read(default)).DeadLetters.Select(letter => letter.Id)
            );
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task FailedArchivalDoesNotHideEligibleLettersAndNextReadReconcilesIt()
    {
        var (store, host) = CreateHost();
        using (host)
        {
            await host.StartAsync();
            var stale = new List<Guid>();
            for (var index = 0; index < 201; index++)
                stale.Add(
                    (await Seed(host, store, index, index < 101 ? "superseded" : "claimed")).Id
                );
            var eligible = await Seed(host, store, 201, "current");
            Sql(
                "CREATE TRIGGER stop_archive BEFORE UPDATE OF archived_at ON wolverine_dead_letters BEGIN SELECT RAISE(ABORT, 'test archival unavailable'); END;"
            );
            var operations = host.Services.GetRequiredService<MessagingOperations>();
            var letters = host.Services.GetRequiredService<IWolverineRuntime>().Storage.DeadLetters;
            for (var attempt = 0; attempt < 2; attempt++)
                Assert.Equal(
                    eligible.Id.ToString(),
                    Assert.Single((await operations.Read(default)).DeadLetters).Id
                );
            foreach (var id in stale)
                Assert.Null((await letters.DeadLetterEnvelopeByIdAsync(id))!.ArchivedAt);

            Sql("DROP TRIGGER stop_archive;");
            Assert.Equal(
                eligible.Id.ToString(),
                Assert.Single((await operations.Read(default)).DeadLetters).Id
            );
            foreach (var id in stale)
                Assert.NotNull((await letters.DeadLetterEnvelopeByIdAsync(id))!.ArchivedAt);
            Assert.Null((await letters.DeadLetterEnvelopeByIdAsync(eligible.Id))!.ArchivedAt);
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task NonFailedCurrentAndUnknownGenerationsAreHiddenButNeverArchived()
    {
        var (store, host) = CreateHost();
        using (host)
        {
            await host.StartAsync();
            var preserved = new List<Envelope>();
            foreach (
                var status in new[] { "queued", "processing", "review", "approved", "cancelled" }
            )
                preserved.Add(await Seed(host, store, preserved.Count * 2, "current", status));
            foreach (var status in new[] { "queued", "processing", "delivered", "rejected" })
                preserved.Add(await Seed(host, store, preserved.Count * 2 + 1, "current", status));
            preserved.Add(await Seed(host, store, 40, "missing-generation"));
            preserved.Add(await Seed(host, store, 41, "missing-generation"));
            preserved.Add(await Seed(host, store, 42, "missing-work-id"));
            preserved.Add(await Seed(host, store, 43, "missing-work-id"));
            var archived = await Seed(host, store, 44, "current");
            var letters = host.Services.GetRequiredService<IWolverineRuntime>().Storage.DeadLetters;
            await letters.ArchiveMatchingAsync(new DeadLetterEnvelopeQuery([archived.Id]), default);
            var operations = host.Services.GetRequiredService<MessagingOperations>();
            Assert.Empty((await operations.Read(default)).DeadLetters);
            foreach (var envelope in preserved)
                Assert.Null((await letters.DeadLetterEnvelopeByIdAsync(envelope.Id))!.ArchivedAt);

            // A finalizing current generation may become failed without changing its work ID.
            Sql(
                "UPDATE packets SET json=json_set(json,'$.status','failed') WHERE json_extract(json,'$.status')='processing'; UPDATE deliveries SET response=json_set(response,'$.status','failed') WHERE json_extract(response,'$.status')='processing';"
            );
            var visible = (await operations.Read(default)).DeadLetters;
            Assert.Equal(2, visible.Length);
            Assert.Single(visible, letter => letter.PacketId is not null);
            Assert.Single(visible, letter => letter.DeliveryId is not null);
            Assert.DoesNotContain(archived.Id.ToString(), visible.Select(letter => letter.Id));
            await host.StopAsync();
        }
    }

    private async Task<Envelope> Seed(
        IHost host,
        Store store,
        int index,
        string disposition,
        string status = "failed"
    )
    {
        var packet = new Packet { Status = status };
        store.Save(packet);
        var delivery = index % 2 != 0;
        var kind = delivery ? "delivery" : "packet";
        var id = delivery ? Guid.NewGuid().ToString("N") : packet.Id;
        var workId = disposition == "missing-work-id" ? null : Guid.NewGuid().ToString("N");
        using (var c = Open())
        using (var tx = c.BeginTransaction())
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            if (delivery)
            {
                var response = new DeliveryResponse(
                    id,
                    packet.Id,
                    packet.Revision,
                    status,
                    "simulation",
                    "success",
                    1,
                    firstScheduledAt,
                    firstScheduledAt,
                    null,
                    null,
                    null,
                    null
                );
                q.CommandText =
                    "INSERT INTO deliveries VALUES($id,$packet,$revision,$response,$snapshot)";
                q.Parameters.AddWithValue("$id", id);
                q.Parameters.AddWithValue("$packet", packet.Id);
                q.Parameters.AddWithValue("$revision", packet.Revision);
                q.Parameters.AddWithValue(
                    "$response",
                    JsonSerializer.Serialize(response, Contract.Json)
                );
                q.Parameters.AddWithValue(
                    "$snapshot",
                    JsonSerializer.Serialize(packet, Contract.Json)
                );
                q.ExecuteNonQuery();
                q.Parameters.Clear();
            }
            if (disposition != "missing-generation")
            {
                q.CommandText = "INSERT INTO queue_work VALUES($kind,$id,$work)";
                q.Parameters.AddWithValue("$kind", kind);
                q.Parameters.AddWithValue("$id", id);
                q.Parameters.AddWithValue(
                    "$work",
                    disposition == "superseded" || workId is null
                        ? Guid.NewGuid().ToString("N")
                        : workId
                );
                q.ExecuteNonQuery();
            }
            tx.Commit();
        }
        object message = delivery ? new ProcessDelivery(id, workId) : new ProcessPacket(id, workId);
        var envelope = new Envelope(message)
        {
            Data = JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), Contract.Json),
            Destination = new Uri(delivery ? "local://delivery/" : "local://processing/"),
            ContentType = "application/json",
            ScheduledTime = firstScheduledAt.AddSeconds(index),
        };
        // Use the production public helper and Wolverine's persistence builder: these are genuine DL rows.
        await host
            .Services.GetRequiredService<WorkFailureRecorder>()
            .PersistDeadLetter(
                host.Services.GetRequiredService<IWolverineRuntime>(),
                envelope,
                new PermanentWorkException()
            );
        if (disposition == "claimed")
        {
            using var c = Open();
            using var q = c.CreateCommand();
            q.CommandText = "INSERT INTO replayed_letters VALUES($id,$at)";
            q.Parameters.AddWithValue("$id", envelope.Id.ToString());
            q.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            q.ExecuteNonQuery();
        }
        return envelope;
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

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("Unexpected network.");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
