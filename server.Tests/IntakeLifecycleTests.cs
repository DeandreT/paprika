using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace paprika.Tests;

public sealed class IntakeLifecycleTests : IDisposable
{
    private readonly string root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp",
        "opencode",
        "intake-" + Guid.NewGuid().ToString("N")
    );

    private IConfiguration Config(string mode = "simulation") =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["paprika:DataDirectory"] = root,
                    ["paprika:Mode"] = mode,
                    ["paprika:AzureEndpoint"] = "https://ocr.example.test",
                    ["paprika:AzureKey"] = "test",
                    ["paprika:JevApiKey"] = "test",
                }
            )
            .Build();

    private Store Store => new(Config());

    private Packet Intake(string name = "file.pdf", string hash = "abc")
    {
        var p = new Packet();
        AddSource(p, name, hash, 1);
        return p;
    }

    private void AddSource(Packet p, string name, string text, int pages)
    {
        var source = new Source
        {
            Name = name,
            Extension = ".pdf",
            PageCount = pages,
        };
        var directory = Path.Combine(root, p.Id);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, source.Id + ".pdf");
        Pdfs.Synthetic(path, Enumerable.Repeat(text, pages).ToList());
        source.Sha256 = Pdfs.SourceHash(path);
        p.Sources.Add(source);
    }

    [Fact]
    public async Task IdempotencyBindingIsAtomicAcrossStoresAndSurvivesRecreation()
    {
        _ = Store;
        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, 12).Select(_ => Task.Run(() => Store.Accept(Intake(), "retry-key")))
        );
        Assert.All(outcomes, o => Assert.Equal(200, o.Code));
        Assert.Single(outcomes.Select(o => o.Packet!.Id).Distinct());
        var store = Store;
        Assert.Single(store.All());
        Assert.Equal(outcomes[0].Packet!.Id, store.Accept(Intake(), "retry-key").Packet!.Id);
        Assert.Equal(409, store.Accept(Intake(hash: "changed"), "retry-key").Code);
        Assert.Equal(409, store.Accept(Intake(name: "renamed.pdf"), "retry-key").Code);
        var differentMode = Intake();
        differentMode.Mode = "live";
        Assert.Equal(409, store.Accept(differentMode, "retry-key").Code);
        var differentCount = Intake();
        differentCount.Sources[0].PageCount = 2;
        Assert.Equal(409, store.Accept(differentCount, "retry-key").Code);
        store.Accept(Intake(), "another-key");
        store.Accept(Intake(), null);
        store.Accept(Intake(), null);
        Assert.Equal(4, store.All().Count);
    }

    [Fact]
    public void OrderedSourcesArePartOfFingerprint()
    {
        var p = Intake();
        AddSource(p, "second.pdf", "def", 2);
        Assert.Equal(200, Store.Accept(p, "ordered").Code);
        var other = Intake();
        AddSource(other, "second.pdf", "def", 2);
        other.Sources.Reverse();
        Assert.Equal(409, Store.Accept(other, "ordered").Code);
    }

    [Fact]
    public void ReprocessPreservesApprovedParentOriginalsAndExport()
    {
        var store = Store;
        var original = Worker.Demo(store);
        original.Documents = Worker.Assemble(original.Pages);
        original.Status = "review";
        original.Sources[0].OcrJson = "old config OCR";
        store.Save(original);
        var approved = store
            .Review(original.Id, new(original.Revision, original.Documents, true))
            .Packet!;
        var snapshot = JsonSerializer.Serialize(approved, Contract.Json);
        var bytes = File.ReadAllBytes(store.SourcePath(approved, approved.Sources[0]));
        var export = Pdfs.Export(approved, store);
        var result = store.Reprocess(approved.Id, approved.Revision, new Settings(Config()));
        Assert.Equal(200, result.Code);
        var next = result.Packet!;
        Assert.Equal("queued", next.Status);
        Assert.Equal("simulation", next.Mode);
        Assert.Equal(approved.Id, next.ParentPacketId);
        Assert.Equal(2, next.RunNumber);
        Assert.NotEqual(approved.Id, next.Id);
        Assert.NotEqual(approved.Sources[0].Id, next.Sources[0].Id);
        Assert.Equal(bytes, File.ReadAllBytes(store.SourcePath(next, next.Sources[0])));
        Assert.Null(next.Sources[0].OcrJson);
        Assert.Null(next.Sources[0].OperationUrl);
        Assert.Empty(next.Pages);
        Assert.Empty(next.Documents);
        Assert.Equal(snapshot, JsonSerializer.Serialize(Store.Get(approved.Id), Contract.Json));
        // ZIP timestamps may differ; compare every immutable member instead of archive metadata.
        using var before = new System.IO.Compression.ZipArchive(new MemoryStream(export));
        using var after = new System.IO.Compression.ZipArchive(
            new MemoryStream(Pdfs.Export(Store.Get(approved.Id)!, store))
        );
        foreach (var entry in before.Entries)
        {
            using var a = new MemoryStream();
            using var b = new MemoryStream();
            entry.Open().CopyTo(a);
            after.GetEntry(entry.FullName)!.Open().CopyTo(b);
            // PDF render metadata may be regenerated; provenance must remain identical.
            if (!entry.FullName.EndsWith(".pdf"))
                Assert.Equal(a.ToArray(), b.ToArray());
        }
        Assert.Equal(
            409,
            store.Reprocess(approved.Id, approved.Revision - 1, new Settings(Config())).Code
        );
        Assert.Equal(409, store.Reprocess(next.Id, next.Revision, new Settings(Config())).Code);
    }

    [Fact]
    public void CancelIsDurableAndRejectsStaleWorkerWrites()
    {
        var store = Store;
        var p = Worker.Demo(store);
        Assert.Equal(409, store.Cancel(p.Id, 8).Code);
        var cancelled = Store.Cancel(p.Id, p.Revision).Packet!;
        Assert.Equal("cancelled", cancelled.Status);
        Assert.Equal(2, cancelled.Revision);
        p.Status = "review";
        p.Audit.Add(new(DateTimeOffset.UtcNow, "stale worker", 1));
        store.Save(p);
        var persisted = Store.Get(p.Id)!;
        Assert.Equal(
            JsonSerializer.Serialize(cancelled, Contract.Json),
            JsonSerializer.Serialize(persisted, Contract.Json)
        );
        Assert.Null(Store.NextPending());
        Assert.Equal(409, store.Cancel(p.Id, 2).Code);
        Assert.Equal(200, store.Reprocess(p.Id, 2, new Settings(Config())).Code);
    }

    [Fact]
    public async Task CancellationInterruptsPendingExternalCallAndPreventsDownstreamCalls()
    {
        var store = Store;
        var p = Worker.Demo(store);
        p.Mode = "live";
        p.Pages.Clear();
        store.Save(p);
        var handler = new PendingProvider();
        var jobs = new JobCoordinator();
        using var worker = MessagingTestHost.Create(
            store,
            new Providers(new Settings(Config("live")), store, handler),
            jobs
        );
        await worker.StartAsync(default);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cancelled = Store.Cancel(p.Id, 1).Packet!;
        jobs.Cancel(p.Id);
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(default);
        Assert.Equal(1, handler.Calls);
        Assert.Equal("cancelled", Store.Get(p.Id)!.Status);
        Assert.Equal(cancelled.Audit, Store.Get(p.Id)!.Audit);
        using var restarted = MessagingTestHost.Create(
            Store,
            new Providers(new Settings(Config("live")), Store, handler)
        );
        await restarted.StartAsync(default);
        await restarted.StopAsync(default);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DurableCancellationStaysTerminalWhenShutdownInterruptsJob()
    {
        var store = Store;
        var p = Worker.Demo(store);
        p.Mode = "live";
        p.Pages.Clear();
        store.Save(p);
        var handler = new PendingProvider();
        using var worker = MessagingTestHost.Create(
            store,
            new Providers(new Settings(Config("live")), store, handler)
        );
        await worker.StartAsync(default);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(200, Store.Cancel(p.Id, 1).Code);
        await worker.StopAsync(default);
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, handler.Calls);
        Assert.Equal("cancelled", Store.Get(p.Id)!.Status);
    }

    [Fact]
    public void IdentifierCandidatesRequireLabelsPreserveAmbiguityAndDoNotAssignBusinessReference()
    {
        var pages = new List<Page>
        {
            new()
            {
                Text =
                    "123456 99999\nLoad #: LD-1\nShipment ID: SH-2\nOrder number: ORD-3\nBOL: B-4\nReceipt # R-5\nLoad: LD-2",
            },
            new() { Text = "Load #: LD-1\nBill of lading - B-4\nOrder: " + new string('X', 65) },
        };
        var candidates = Identifiers.Extract(pages);
        Assert.Equal(6, candidates.Count);
        Assert.Equal(2, candidates.Single(c => c.Value == "LD-1").PageIds.Count);
        Assert.Equal(2, candidates.Single(c => c.Kind == "bol").PageIds.Count);
        Assert.All(candidates, c =>
        {
            Assert.InRange(c.Value.Length, 1, 64);
            Assert.InRange(c.Evidence.Length, 1, 300);
        });
        Assert.Contains("Load", candidates[0].Evidence);
        Assert.All(Worker.Assemble(pages), d => Assert.Equal("", d.BusinessReference));
        var p = new Packet { Pages = pages, IdentifierCandidates = candidates };
        Store.Save(p);
        Assert.Equal(6, Store.Get(p.Id)!.IdentifierCandidates.Count);
        Assert.Equal(1, JsonSerializer.Deserialize<Packet>("{}", Contract.Json)!.RunNumber);
    }

    [Fact]
    public async Task UploadReplayCleansOnlyItsStagingAndLifecycleRoutesUsePacketContract()
    {
        using var factory = new Factory(root);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-paprika", "1");
        var fixture = Path.Combine(root, "fixture.pdf");
        Directory.CreateDirectory(root);
        Pdfs.Synthetic(fixture, ["BOL: TEST-1"]);
        var bytes = File.ReadAllBytes(fixture);
        async Task<HttpResponseMessage> Upload(string? key, string name = "sample.pdf")
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/packets");
            if (key is not null)
                request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
            var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(bytes), "files", name);
            request.Content = content;
            return await client.SendAsync(request);
        }
        using var accepted = await Upload("key");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var p = (await accepted.Content.ReadFromJsonAsync<PacketResponse>())!;
        using var replay = await Upload("key");
        Assert.Equal(p.Id, (await replay.Content.ReadFromJsonAsync<PacketResponse>())!.Id);
        using var conflict = await Upload("key", "different.pdf");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.NotNull((await conflict.Content.ReadFromJsonAsync<ApiError>())!.Error);
        Assert.Single(Directory.GetDirectories(root), path =>
            Path.GetFileName(path) != "deliveries"
        );
        using var invalid = await Upload(new string('k', 129));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var cancelledResponse = await client.PostAsJsonAsync(
            $"/api/packets/{p.Id}/cancel",
            new { revision = p.Revision }
        );
        var cancelled = (await cancelledResponse.Content.ReadFromJsonAsync<PacketResponse>())!;
        Assert.Equal("cancelled", cancelled.Status);
        using var nextResponse = await client.PostAsJsonAsync(
            $"/api/packets/{p.Id}/reprocess",
            new { revision = cancelled.Revision }
        );
        var next = (await nextResponse.Content.ReadFromJsonAsync<PacketResponse>())!;
        Assert.Equal(p.Id, next.ParentPacketId);
        Assert.Equal(2, next.RunNumber);
        Assert.Empty(next.IdentifierCandidates);
        client.DefaultRequestHeaders.Remove("X-paprika");
        using var forbidden = await client.PostAsJsonAsync(
            $"/api/packets/{next.Id}/cancel",
            new { revision = 1 }
        );
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public void ReprocessKeepsOriginalModeWhenServerModeChanges()
    {
        var store = Store;
        var p = Worker.Demo(store);
        p.Status = "failed";
        store.Save(p);
        var missingCredentials = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["paprika:Mode"] = "live" })
            .Build();
        Assert.Equal(200, store.Reprocess(p.Id, p.Revision, new Settings(missingCredentials)).Code);
        Assert.Equal(2, store.All().Count);
        var live = store.Reprocess(p.Id, p.Revision, new Settings(Config("live"))).Packet!;
        Assert.Equal("simulation", live.Mode);
        Assert.Empty(live.Pages);
        var demo = store.Reprocess(p.Id, p.Revision, new Settings(Config("demo"))).Packet!;
        Assert.Equal("simulation", demo.Mode);
        Assert.NotEqual(live.Sources[0].Id, demo.Sources[0].Id);
    }

    [Fact]
    public async Task OpenApiDocumentsOptionalIdempotencyAndLifecycleContracts()
    {
        using var factory = new Factory(root);
        using var client = factory.CreateClient();
        using var spec = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = spec.RootElement.GetProperty("paths");
        var upload = paths.GetProperty("/api/packets").GetProperty("post");
        var key = upload
            .GetProperty("parameters")
            .EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "Idempotency-Key");
        Assert.False(key.TryGetProperty("required", out var required) && required.GetBoolean());
        Assert.Equal(128, key.GetProperty("schema").GetProperty("maxLength").GetInt32());
        Assert.True(upload.GetProperty("responses").TryGetProperty("409", out _));
        foreach (var route in new[] { "cancel", "reprocess" })
        {
            var operation = paths.GetProperty($"/api/packets/{{id}}/{route}").GetProperty("post");
            Assert.True(operation.GetProperty("responses").TryGetProperty("409", out _));
            Assert.Contains(operation.GetProperty("parameters").EnumerateArray(), p =>
                p.GetProperty("name").GetString() == "X-paprika"
                && p.GetProperty("required").GetBoolean()
            );
        }
        var properties = spec
            .RootElement.GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("PacketResponse")
            .GetProperty("properties");
        foreach (var name in new[] { "parentPacketId", "runNumber", "identifierCandidates" })
            Assert.True(properties.TryGetProperty(name, out _));
    }

    private sealed class Factory(string data) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(
                (_, config) =>
                    config.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["paprika:DataDirectory"] = data,
                            ["paprika:Mode"] = "live",
                            ["paprika:AzureEndpoint"] = "https://ocr.example.test",
                            ["paprika:AzureKey"] = "test",
                            ["paprika:JevApiKey"] = "test",
                        }
                    )
            );
            builder.ConfigureServices(services =>
                services.AddSingleton<IHttpClientFactory>(new PendingProvider())
            );
        }
    }

    private sealed class PendingProvider : HttpMessageHandler, IHttpClientFactory
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;

        public HttpClient CreateClient(string name) => new(this, false);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
            throw new InvalidOperationException();
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
