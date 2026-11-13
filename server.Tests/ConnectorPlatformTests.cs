using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PaperworkClassifier;
using Xunit;

namespace PaperworkClassifier.Tests;

public sealed class ConnectorPlatformTests : IDisposable
{
    private readonly ConnectorTestFixture fixture = new();

    public void Dispose() => fixture.Dispose();

    [Fact]
    public async Task ActualFolderIntakeDeduplicatesAndExportsAtomically()
    {
        var intake = Path.Combine(fixture.Root, "input");
        var output = Path.Combine(fixture.Root, "output");
        Directory.CreateDirectory(intake);
        Directory.CreateDirectory(output);
        Pdfs.Synthetic(Path.Combine(intake, "synthetic.pdf"), ["Synthetic BOL 123"]);
        fixture.SetConnector("input", "folderIntake", intake);
        fixture.SetConnector("output", "folderExport", output);
        var importer = new ConnectorIntake(fixture.Store, fixture.Catalog);
        var imported = Assert.Single(await importer.ImportAsync("input", new(), default));
        Assert.Equal("imported", imported.Status);
        var duplicate = Assert.Single(await importer.ImportAsync("input", new(), default));
        Assert.Equal("duplicate", duplicate.Status);
        Assert.Equal(imported.PacketId, duplicate.PacketId);
        var packet = fixture.Ready();
        var queued = fixture.Deliveries.Queue("output", new(packet.Id, packet.Revision));
        Assert.Equal(
            queued.Id,
            fixture.Deliveries.Queue("output", new(packet.Id, packet.Revision)).Id
        );
        Assert.True(await fixture.Deliveries.ProcessNextAsync(default));
        var delivered = Assert.Single(fixture.Deliveries.All());
        Assert.Equal("delivered", delivered.Status);
        Assert.True(delivered.TransportAcknowledged);
        Assert.Equal("pending", delivered.BusinessStatus);
        var exported = Assert.Single(Directory.GetFiles(output));
        Assert.EndsWith(".zip", exported);
        Assert.Equal(delivered.ContentHash, Pdfs.SourceHash(exported));
        var receipt = fixture.Deliveries.Receipt(
            delivered.Id,
            new("accepted", "external-receipt"),
            "operator"
        );
        Assert.Equal("accepted", receipt.BusinessStatus);
        Assert.Equal("operator", receipt.ReceiptRecordedBy);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Deliveries.Receipt(delivered.Id, new("rejected", "other"))
        );
    }

    [Fact]
    public async Task ActualHttpDeliveryRecoversAmbiguousCancellationWithSameKeyAndBytes()
    {
        using var cancel = new CancellationTokenSource();
        var keys = new ConcurrentBag<string>();
        var hashes = new ConcurrentBag<string>();
        var receiver = new ConcurrentDictionary<string, string>();
        var attempt = 0;
        await using var server = new ConnectorHttpDouble(async context =>
        {
            var key = context.Request.Headers["Idempotency-Key"]!;
            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body);
            var hash = Convert
                .ToHexString(System.Security.Cryptography.SHA256.HashData(body.ToArray()))
                .ToLowerInvariant();
            keys.Add(key);
            hashes.Add(hash);
            receiver.TryAdd(key, hash);
            if (Interlocked.Increment(ref attempt) == 1)
            {
                cancel.Cancel();
                context.Response.Abort();
            }
            else
            {
                context.Response.StatusCode = 202;
            }
        });
        fixture.SetConnector("delivery", "httpDelivery", endpoint: server.Url);
        var packet = fixture.Ready();
        var job = fixture.Deliveries.Queue("delivery", new(packet.Id, packet.Revision));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Deliveries.ProcessNextAsync(cancel.Token)
        );
        var recovered = new ConnectorDeliveryStore(
            fixture.Store,
            fixture.Catalog,
            fixture.References
        );
        Assert.Equal("queued", Assert.Single(recovered.All()).Status);
        Assert.True(await recovered.ProcessNextAsync(default));
        var result = Assert.Single(recovered.All());
        Assert.Equal("delivered", result.Status);
        Assert.True(result.TransportAcknowledged);
        Assert.Equal("pending", result.BusinessStatus);
        Assert.Single(receiver);
        Assert.Equal(2, keys.Count);
        Assert.All(keys, key => Assert.Equal(job.IdempotencyKey, key));
        Assert.All(hashes, hash => Assert.Equal(job.ContentHash, hash));
    }

    [Fact]
    public async Task ReviewChangeBeforeDispatchPreventsExternalSideEffect()
    {
        var output = Path.Combine(fixture.Root, "output");
        Directory.CreateDirectory(output);
        fixture.SetConnector("out", "folderExport", output);
        var packet = fixture.Ready();
        fixture.Deliveries.Queue("out", new(packet.Id, packet.Revision));
        packet.Revision++;
        packet.Status = "review";
        fixture.Store.Save(packet);
        await fixture.Deliveries.ProcessNextAsync(default);
        Assert.Empty(Directory.GetFiles(output));
        Assert.Equal("failed", Assert.Single(fixture.Deliveries.All()).Status);
    }

    [Fact]
    public void TestRunsAndReceiversWithoutIdempotencyAreRejected()
    {
        fixture.SetConnector("http", "httpDelivery", endpoint: "http://127.0.0.1:1/never");
        fixture.Config["Paperwork:Connectors:http:SupportsIdempotency"] = "false";
        var packet = fixture.Ready();
        Assert.Throws<InvalidDataException>(() =>
            fixture.Deliveries.Queue("http", new(packet.Id, packet.Revision))
        );
        var output = Path.Combine(fixture.Root, "output");
        Directory.CreateDirectory(output);
        fixture.SetConnector("out", "folderExport", output);
        packet.TestRun = true;
        fixture.Store.Save(packet);
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Deliveries.Queue("out", new(packet.Id, packet.Revision))
        );
    }
}

internal sealed class ConnectorTestFixture : IDisposable
{
    public string Root { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp",
            "opencode",
            "connector-tests-" + Guid.NewGuid().ToString("N")
        );
    public IConfigurationRoot Config { get; }
    public Store Store { get; }
    public ConnectorCatalog Catalog { get; }
    public ReferenceService References { get; }
    public ConnectorDeliveryStore Deliveries { get; }

    public ConnectorTestFixture()
    {
        Config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    { "Paperwork:DataDirectory", Path.Combine(Root, "data") },
                }
            )
            .Build();
        Store = new(Config);
        Catalog = new(Config);
        References = new(Store, Catalog);
        Deliveries = new(Store, Catalog, References);
    }

    public void SetConnector(string id, string kind, string? root = null, string? endpoint = null)
    {
        Config[$"Paperwork:Connectors:{id}:Kind"] = kind;
        Config[$"Paperwork:Connectors:{id}:Root"] = root;
        Config[$"Paperwork:Connectors:{id}:Endpoint"] = endpoint;
        Config[$"Paperwork:Connectors:{id}:SupportsIdempotency"] = "true";
    }

    public Packet Ready(ReferencePolicy? policy = null)
    {
        var packet = Worker.Demo(Store);
        packet.Documents = Worker.Assemble(packet.Pages);
        packet.Documents = [packet.Documents[0]];
        packet.Pages = packet.Pages.Where(p => packet.Documents[0].PageIds.Contains(p.Id)).ToList();
        packet.Status = "approved";
        packet.Workflow = WorkflowRegistry.Legacy("simulation") with
        {
            Capabilities = policy is null ? null : new(Reference: policy),
            Hash = ReferencePolicies.Hash(new { policy }),
        };
        Store.Save(packet);
        return packet;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root))
            Directory.Delete(Root, true);
    }
}

internal sealed class ConnectorHttpDouble : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly Task loop;
    public string Url { get; }

    public ConnectorHttpDouble(Func<HttpListenerContext, Task> handle)
    {
        var portListener = new TcpListener(IPAddress.Loopback, 0);
        portListener.Start();
        var port = ((IPEndPoint)portListener.LocalEndpoint).Port;
        portListener.Stop();
        Url = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(Url);
        listener.Start();
        loop = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    break;
                }
                try
                {
                    await handle(context);
                }
                catch (Exception ex)
                    when (ex is HttpListenerException or IOException or ObjectDisposedException) { }
                finally
                {
                    try
                    {
                        context.Response.Close();
                    }
                    catch (Exception ex)
                        when (ex
                                is ObjectDisposedException
                                    or InvalidOperationException
                                    or HttpListenerException
                        ) { }
                }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        listener.Stop();
        listener.Close();
        await loop;
    }
}
