using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Xunit;

namespace paprika.Tests;

public sealed class MessagingTests : IDisposable
{
    private readonly string root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp",
        "opencode",
        "messaging-" + Guid.NewGuid().ToString("N")
    );

    private IConfiguration Config =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["paprika:DataDirectory"] = root,
                    ["paprika:AzureEndpoint"] = "https://ocr.example.test",
                    ["paprika:AzureKey"] = "test",
                    ["paprika:JevApiKey"] = "test",
                }
            )
            .Build();

    private Store Store => new(Config);

    private IHost Host(bool polling = false, IHttpClientFactory? factory = null)
    {
        var store = Store;
        return MessagingTestHost.Create(
            store,
            new Providers(new Settings(Config), store, factory ?? new NoNetwork()),
            polling: polling
        );
    }

    private static async Task Invoke(IHost host, string id, CancellationToken ct = default)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync(new ProcessPacket(id), ct);
    }

    private static async Task Deliver(IHost host, string id)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope
            .ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync(new ProcessDelivery(id));
    }

    [Fact]
    public async Task RealDispatchProcessesExactlyOnceAndPreservesApprovedReview()
    {
        var store = Store;
        var p = Worker.Demo(store);
        using var host = Host();
        await host.StartAsync();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Invoke(host, p.Id)));
        p = store.Get(p.Id)!;
        Assert.Equal("review", p.Status);
        Assert.Single(p.Audit, a => a.Action == "processed; manual review required");
        Assert.Equal(4, p.Documents.Count);
        p.Documents[0].BusinessReference = "human-approved";
        p = store.Review(p.Id, new(p.Revision, p.Documents, true)).Packet!;
        Assert.Equal("approved", p.Status);
        var approved = JsonSerializer.Serialize(p, Contract.Json);
        await Invoke(host, p.Id);
        await Invoke(host, "missing");
        Assert.Equal(approved, JsonSerializer.Serialize(store.Get(p.Id), Contract.Json));
        await host.StopAsync();
    }

    [Theory]
    [InlineData("review")]
    [InlineData("approved")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task TerminalPacketsAreNoOps(string status)
    {
        var store = Store;
        var p = Worker.Demo(store);
        p.Status = status;
        p.Error = status == "failed" ? "preserved failure" : null;
        store.Save(p);
        var before = JsonSerializer.Serialize(store.Get(p.Id), Contract.Json);
        using var host = Host();
        await host.StartAsync();
        await Invoke(host, p.Id);
        Assert.Equal(before, JsonSerializer.Serialize(store.Get(p.Id), Contract.Json));
        Assert.Null(store.NextPending());
        await host.StopAsync();
    }

    [Fact]
    public async Task ConcurrentDirectCommandsAreSerializedAndCancellationStaysTerminal()
    {
        var store = Store;
        var first = Worker.Demo(store);
        first.Mode = "live";
        first.Sources[0].OperationUrl = "https://ocr.example.test/operations/saved";
        store.Save(first);
        var second = Worker.Demo(store);
        using var pending = new PendingOcr();
        using var host = Host(factory: pending);
        await host.StartAsync();
        var active = Invoke(host, first.Id);
        await pending.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var duplicate = Invoke(host, first.Id);
        var next = Invoke(host, second.Id);
        Assert.False(duplicate.IsCompleted);
        Assert.False(next.IsCompleted);
        Assert.Equal("queued", store.Get(second.Id)!.Status);
        var cancelled = store.Cancel(first.Id, first.Revision).Packet!;
        host.Services.GetRequiredService<JobCoordinator>().Cancel(first.Id);
        await Task.WhenAll(active, duplicate, next).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, pending.Calls);
        Assert.Equal(
            JsonSerializer.Serialize(cancelled, Contract.Json),
            JsonSerializer.Serialize(store.Get(first.Id), Contract.Json)
        );
        Assert.Equal("review", store.Get(second.Id)!.Status);
        await host.StopAsync();
    }

    [Fact]
    public async Task ShutdownLeavesCheckpointAndRestartResumesWithoutResubmissionOrChangingApproval()
    {
        var store = Store;
        var approved = Worker.Demo(store);
        approved.Status = "review";
        approved.Documents = Worker.Assemble(approved.Pages);
        store.Save(approved);
        approved = store
            .Review(approved.Id, new(approved.Revision, approved.Documents, true))
            .Packet!;
        var snapshot = JsonSerializer.Serialize(approved, Contract.Json);
        var p = Worker.Demo(store);
        p.Mode = "live";
        p.Sources[0].OperationUrl = "https://ocr.example.test/operations/saved";
        store.Save(p);
        using (var pending = new PendingOcr())
        using (var host = Host(polling: true, factory: pending))
        {
            await host.StartAsync();
            await pending.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await host.StopAsync();
        }
        Assert.Equal("processing", store.Get(p.Id)!.Status);
        Assert.Equal(p.Sources[0].OperationUrl, store.Get(p.Id)!.Sources[0].OperationUrl);
        using var resumed = new CompletedOcr(p);
        using var restarted = Host(polling: true, factory: resumed);
        await restarted.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (store.Get(p.Id)!.Status == "processing")
            await Task.Delay(20, deadline.Token);
        await restarted.StopAsync();
        Assert.Equal("review", store.Get(p.Id)!.Status);
        Assert.Equal(
            p.Pages.Select(page => page.Id),
            store.Get(p.Id)!.Pages.Select(page => page.Id)
        );
        Assert.Equal(1, resumed.Calls);
        Assert.Equal(snapshot, JsonSerializer.Serialize(store.Get(approved.Id), Contract.Json));
    }

    [Theory]
    [InlineData("success", "delivered")]
    [InlineData("transient-once", "delivered")]
    [InlineData("reject", "rejected")]
    public async Task RealDeliveryDispatchPreservesDurableAttemptsAndManualRetry(
        string scenario,
        string status
    )
    {
        var store = Store;
        using var host = Host();
        await host.StartAsync();
        var p = Worker.Demo(store);
        await Invoke(host, p.Id);
        p = store.Get(p.Id)!;
        p = store.Review(p.Id, new(p.Revision, p.Documents, true)).Packet!;
        var deliveries = host.Services.GetRequiredService<DeliveryStore>();
        var d = deliveries.Queue(p.Id, new(p.Revision, scenario)).Delivery!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (deliveries.Get(d.Id)!.Status is "queued" or "processing")
            await Task.Delay(50, deadline.Token);
        Assert.Equal(status, deliveries.Get(d.Id)!.Status);
        Assert.Equal(scenario == "transient-once" ? 2 : 1, deliveries.Get(d.Id)!.Attempts);
        await Deliver(host, d.Id);
        await host.StopAsync();
        using var restarted = Host();
        await restarted.StartAsync();
        await Deliver(restarted, d.Id);
        Assert.Equal(409, deliveries.Retry(d.Id).Code);
        Assert.Equal("approved", store.Get(p.Id)!.Status);
        await restarted.StopAsync();
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("Unexpected provider call.");
    }

    private sealed class PendingOcr : HttpMessageHandler, IHttpClientFactory
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public HttpClient CreateClient(string name) => new(this, false);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Interlocked.Increment(ref Calls);
            Assert.Equal(HttpMethod.Get, request.Method);
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }
    }

    private sealed class CompletedOcr(Packet packet) : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls;

        public HttpClient CreateClient(string name) => new(this, false);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Interlocked.Increment(ref Calls);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(packet.Sources[0].OperationUrl, request.RequestUri!.AbsoluteUri);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
