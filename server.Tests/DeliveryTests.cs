using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PaperworkClassifier;
using Xunit;

namespace PaperworkClassifier.Tests;

public sealed class DeliveryTests : IDisposable
{
    [Fact]
    public async Task DeliveryOpenApiDescribesBinaryArtifactAndSimulationContracts()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Paperwork:DataDirectory"] = root;
        builder.Host.UsePacketMessaging();
        builder.Services.AddSingleton<JobCoordinator>();
        builder.Services.AddSingleton<Settings>();
        builder.Services.AddSingleton<Providers>();
        builder.Services.AddHttpClient();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(Store);
        builder.Services.AddApiDescription();
        builder.Services.AddDeliverySimulation();
        await using var app = builder.Build();
        app.MapOpenApi();
        app.MapDeliverySimulation();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var json = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = json.RootElement.GetProperty("paths");
        var download = paths
            .GetProperty("/api/deliveries/{deliveryId}/artifact")
            .GetProperty("get");
        Assert.Equal(
            "binary",
            download
                .GetProperty("responses")
                .GetProperty("200")
                .GetProperty("content")
                .GetProperty("application/zip")
                .GetProperty("schema")
                .GetProperty("format")
                .GetString()
        );
        var queue = paths.GetProperty("/api/packets/{id}/deliveries").GetProperty("post");
        Assert.Equal("QueueDelivery", queue.GetProperty("operationId").GetString());
        Assert.True(queue.GetProperty("responses").TryGetProperty("409", out _));
        var schemas = json.RootElement.GetProperty("components").GetProperty("schemas");
        var scenario = schemas
            .GetProperty("QueueDeliveryRequest")
            .GetProperty("properties")
            .GetProperty("scenario");
        Assert.Equal("success", scenario.GetProperty("default").GetString());
        Assert.Equal(4, scenario.GetProperty("enum").GetArrayLength());
        var mode = schemas
            .GetProperty("DeliveryResponse")
            .GetProperty("properties")
            .GetProperty("mode");
        Assert.Equal("simulation", mode.GetProperty("enum")[0].GetString());
        using var operations = JsonDocument.Parse(await client.GetStringAsync("/api/operations"));
        Assert.Equal("local", operations.RootElement.GetProperty("mode").GetString());
        Assert.Equal(
            0,
            operations.RootElement.GetProperty("packets").GetProperty("total").GetInt32()
        );
        await app.StopAsync();
    }

    private readonly string root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp",
        "opencode",
        "delivery-tests-" + Guid.NewGuid().ToString("N")
    );
    private Store Store =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Paperwork:DataDirectory"] = root }
                )
                .Build()
        );

    private Packet Approved()
    {
        var store = Store;
        var packet = Worker.Demo(store);
        packet.Documents = Worker.Assemble(packet.Pages);
        packet.Status = "review";
        packet.Documents[0].BusinessReference = "reviewed-ref";
        packet.Documents[1].BusinessReference = "reviewed-ref";
        store.Save(packet);
        return store.Review(packet.Id, new(packet.Revision, packet.Documents, true)).Packet!;
    }

    [Fact]
    public void QueueIsDurableIdempotentAndRequiresApproval()
    {
        var p = Approved();
        var a = new DeliveryStore(Store).Queue(p.Id, new(p.Revision));
        var fresh = new DeliveryStore(Store);
        Assert.Equal(a.Delivery!.Id, fresh.Queue(p.Id, new(p.Revision)).Delivery!.Id);
        Assert.Equal(409, fresh.Queue(p.Id, new(p.Revision, "reject")).Code);
        Assert.Equal(409, fresh.Queue(p.Id, new(p.Revision + 1)).Code);
        Assert.Equal(404, fresh.Queue("missing", new(1)).Code);
        Assert.Equal(400, fresh.Queue(p.Id, new(p.Revision, "bad")).Code);
        var edited = Store.Review(p.Id, new(p.Revision, p.Documents, false)).Packet!;
        Assert.Equal(409, fresh.Queue(p.Id, new(edited.Revision)).Code);
        Assert.Equal(a.Delivery.Id, fresh.Queue(p.Id, new(p.Revision)).Delivery!.Id);
    }

    [Fact]
    public void QueuedRevisionStaysImmutableAndNewRevisionSupersedes()
    {
        var p = Approved();
        var deliveries = new DeliveryStore(Store);
        var first = deliveries.Queue(p.Id, new(p.Revision)).Delivery!;
        p.Documents[0].BusinessReference = "changed";
        var edited = Store.Review(p.Id, new(p.Revision, p.Documents, false)).Packet!;
        Assert.True(deliveries.Process(first.Id));
        first = deliveries.Get(first.Id)!;
        var originalBytes = File.ReadAllBytes(deliveries.ArtifactPath(first));
        using (var zip = ZipFile.OpenRead(deliveries.ArtifactPath(first)))
        using (var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
        {
            Assert.Equal(first.Revision, manifest.RootElement.GetProperty("revision").GetInt32());
            Assert.Equal(
                "reviewed-ref",
                manifest
                    .RootElement.GetProperty("documents")[0]
                    .GetProperty("businessReference")
                    .GetString()
            );
            var groups = manifest
                .RootElement.GetProperty("businessGroups")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(
                2,
                groups
                    .Single(g => !g.GetProperty("unassigned").GetBoolean())
                    .GetProperty("documentIds")
                    .GetArrayLength()
            );
            Assert.True(
                groups
                    .Single(g => g.GetProperty("unassigned").GetBoolean())
                    .GetProperty("businessReference")
                    .ValueKind == JsonValueKind.Null
            );
        }
        var approved = Store.Review(p.Id, new(edited.Revision, edited.Documents, true)).Packet!;
        var second = deliveries.Queue(p.Id, new(approved.Revision)).Delivery!;
        Assert.Equal(first.Id, second.SupersedesDeliveryId);
        Assert.True(deliveries.Process(second.Id));
        Assert.Equal(originalBytes, File.ReadAllBytes(deliveries.ArtifactPath(first)));
        Assert.Equal(second.Id, deliveries.All(p.Id)[0].Id);
    }

    [Fact]
    public void TransientFailurePropagatesAndReceiptAndChecksumAreExplicit()
    {
        var p = Approved();
        var deliveries = new DeliveryStore(Store);
        var d = deliveries.Queue(p.Id, new(p.Revision, "transient-once")).Delivery!;
        Assert.Throws<IOException>(() => deliveries.Process(d.Id));
        Assert.Equal("processing", deliveries.Get(d.Id)!.Status);
        Assert.Equal(409, deliveries.Retry(d.Id).Code);
        Assert.True(new DeliveryStore(Store).Process(d.Id));
        d = deliveries.Get(d.Id)!;
        Assert.Equal("delivered", d.Status);
        Assert.Equal(2, d.Attempts);
        Assert.Equal("simulation", d.Mode);
        Assert.Equal(Pdfs.SourceHash(deliveries.ArtifactPath(d)), d.Sha256);
        using var zip = ZipFile.OpenRead(deliveries.ArtifactPath(d));
        using var receipt = JsonDocument.Parse(zip.GetEntry("delivery-receipt.json")!.Open());
        Assert.Equal(d.Id, receipt.RootElement.GetProperty("deliveryId").GetString());
        Assert.Equal(d.Revision, receipt.RootElement.GetProperty("revision").GetInt32());
        Assert.Equal("simulation", receipt.RootElement.GetProperty("transport").GetString());
        Assert.False(receipt.RootElement.GetProperty("externalSystemContacted").GetBoolean());
        Assert.Equal("approved", Store.Get(p.Id)!.Status);
    }

    [Fact]
    public void RejectionIsTerminalAndMetricsAreAggregated()
    {
        var p = Approved();
        var deliveries = new DeliveryStore(Store);
        var d = deliveries.Queue(p.Id, new(p.Revision, "reject")).Delivery!;
        deliveries.Process(d.Id);
        Assert.Equal("rejected", deliveries.Get(d.Id)!.Status);
        Assert.Equal(409, deliveries.Retry(d.Id).Code);
        var counts = OperationsResponse.Read(Store, deliveries);
        Assert.Equal(1, counts.Packets.Total);
        Assert.Equal(1, counts.Packets.Approved);
        Assert.Equal(1, counts.Deliveries.Rejected);
        Assert.Equal(p.Pages.Count, counts.Pages);
        Assert.Equal(p.Documents.Count, counts.Documents);
    }

    [Fact]
    public void RestartReconcilesCommittedArtifactWithoutReexport()
    {
        var p = Approved();
        var deliveries = new DeliveryStore(Store);
        var d = deliveries.Queue(p.Id, new(p.Revision)).Delivery!;
        deliveries.Process(d.Id);
        d = deliveries.Get(d.Id)!;
        var original = File.ReadAllBytes(deliveries.ArtifactPath(d));
        using (
            var c = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = Path.Combine(root, "jobs.sqlite"),
                }.ToString()
            )
        )
        {
            c.Open();
            using var q = c.CreateCommand();
            q.CommandText = "UPDATE deliveries SET response=$response WHERE id=$id";
            q.Parameters.AddWithValue("$id", d.Id);
            q.Parameters.AddWithValue(
                "$response",
                JsonSerializer.Serialize(
                    d with
                    {
                        Status = "processing",
                        Sha256 = null,
                        ArtifactUrl = null,
                    },
                    Contract.Json
                )
            );
            q.ExecuteNonQuery();
        }
        var restarted = new DeliveryStore(Store);
        Assert.True(restarted.Process(d.Id));
        Assert.Equal("delivered", restarted.Get(d.Id)!.Status);
        Assert.Equal(2, restarted.Get(d.Id)!.Attempts);
        Assert.Equal(original, File.ReadAllBytes(restarted.ArtifactPath(d)));
        Assert.Equal(d.Sha256, restarted.Get(d.Id)!.Sha256);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
