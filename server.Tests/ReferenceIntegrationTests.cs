using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using paprika;
using Xunit;

namespace paprika.Tests;

public sealed class ReferenceIntegrationTests
{
    [Fact]
    public async Task CompletedReferenceBlockCrashRestoresOnlyExactDerivedDocumentIdentity()
    {
        using var fixture = new ConnectorTestFixture();
        var data = fixture.References.Create(
            new(
                "recovery",
                "ARTIFICIAL recovery",
                true,
                [new("ORDER-9001", "record-9001", "ARTIFICIAL")]
            )
        );
        var packet = fixture.Ready(new("local", data.Id, data.Version, data.Hash));
        packet.Status = "processing";
        packet.IdentifierCandidates =
        [
            new("order", "ORDER-9001", packet.Documents[0].PageIds, "ARTIFICIAL visible text"),
        ];
        fixture.Store.Save(packet);
        var flow = new FlowPolicy(
            new("Reference recovery", [new("lookup", FlowBlockKind.Reference, [])])
        );
        var calls = 0;
        var handlers = new Dictionary<FlowBlockKind, FlowBlockHandler>
        {
            [FlowBlockKind.Reference] = async (context, ct) =>
            {
                calls++;
                await fixture.References.ProcessAsync(context.Packet, ct);
                return FlowBlockResult.Empty;
            },
        };
        var engine = new FlowEngine(fixture.Store);
        await engine.ExecuteAsync(packet, flow, handlers, default);
        Assert.Equal("completed", Assert.Single(engine.Get(packet)!.Executions).Status);
        // No final processing Save: simulate process death after block completion.
        var checkpoint = fixture.Store.Get(packet.Id)!;
        Assert.Equal("ORDER-9001", checkpoint.Documents[0].BusinessReference);
        var restored = JsonSerializer.Deserialize<Packet>(
            JsonSerializer.Serialize(checkpoint, Contract.Json),
            Contract.Json
        )!;
        restored.Documents[0].BusinessReference = "";
        fixture.References.RestoreDerivedReferences(restored);
        Assert.Equal("ORDER-9001", restored.Documents[0].BusinessReference);
        await new FlowEngine(fixture.Store).ExecuteAsync(restored, flow, handlers, default);
        Assert.Equal(1, calls);
        var mismatch = JsonSerializer.Deserialize<Packet>(
            JsonSerializer.Serialize(checkpoint, Contract.Json),
            Contract.Json
        )!;
        mismatch.Documents[0].BusinessReference = "";
        mismatch.Documents[0].PageIds.Reverse();
        fixture.References.RestoreDerivedReferences(mismatch);
        Assert.Equal("", mismatch.Documents[0].BusinessReference);
        mismatch = JsonSerializer.Deserialize<Packet>(
            JsonSerializer.Serialize(checkpoint, Contract.Json),
            Contract.Json
        )!;
        mismatch.Documents[0].BusinessReference = "";
        mismatch.Revision++;
        fixture.References.RestoreDerivedReferences(mismatch);
        Assert.Equal("", mismatch.Documents[0].BusinessReference);
    }

    [Fact]
    public async Task RealPdfPipelineLookupVerifyApproveCarryAndEditInvalidation()
    {
        await using var app = new ReferenceFactory();
        using var client = app.Client();
        var data = await Dataset(client, true);
        var workflow = await Workflow(
            client,
            new("local", data.Id, data.Version, data.Hash, RequireVerified: true)
        );
        var packet = await Upload(client, app, workflow);
        Assert.Equal("ORDER-9001", Assert.Single(packet.Documents).BusinessReference);
        var candidate = Assert.Single(
            await Read<ReferenceResult[]>(client, $"/api/references/packets/{packet.Id}")
        );
        Assert.Equal("candidate", candidate.Status);
        Assert.True(candidate.Synthetic);
        Assert.Equal(packet.Workflow.Hash, candidate.WorkflowHash);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await client.GetAsync($"/api/packets/{packet.Id}/export")).StatusCode
        );
        var bad = await client.PostAsJsonAsync(
            $"/api/references/packets/{packet.Id}/verify",
            new ReferenceVerifyRequest(
                packet.Id,
                packet.Revision,
                candidate.DocumentId,
                "stale-hash",
                "record-9001"
            )
        );
        Assert.Equal(HttpStatusCode.Conflict, bad.StatusCode);
        using var verifiedResponse = await client.PostAsJsonAsync(
            $"/api/references/packets/{packet.Id}/verify",
            new ReferenceVerifyRequest(
                packet.Id,
                packet.Revision,
                candidate.DocumentId,
                candidate.ResultHash,
                "record-9001"
            )
        );
        var verified = await Body<ReferenceResult>(verifiedResponse);
        Assert.Equal("local", verified.VerifiedBy);
        var approved = await Approve(client, packet);
        var carried = (
            await Read<ReferenceResult[]>(client, $"/api/references/packets/{packet.Id}")
        ).Single(row => row.Revision == approved.Revision);
        Assert.Equal("verified", carried.Status);
        Assert.NotEqual(verified.ResultHash, carried.ResultHash);
        Assert.Equal(verified.VerifiedBy, carried.VerifiedBy);
        using (var export = await client.GetAsync($"/api/packets/{packet.Id}/export"))
        {
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
            using var bytes = new MemoryStream(await export.Content.ReadAsByteArrayAsync());
            using var zip = new ZipArchive(bytes);
            Assert.Contains(zip.Entries, entry => entry.FullName.EndsWith(".pdf"));
            using var evidence = zip.GetEntry("platform.json")!.Open();
            using var json = await JsonDocument.ParseAsync(evidence);
            Assert.Contains("\"synthetic\":true", json.RootElement.GetRawText());
        }
        using (
            var actual = await client.PostAsJsonAsync(
                $"/api/connectors/out/packets/{packet.Id}/deliveries",
                new ConnectorDeliveryRequest(packet.Id, approved.Revision)
            )
        )
            Assert.Equal(HttpStatusCode.Conflict, actual.StatusCode);
        approved.Documents[0].BusinessReference = "EDITED-UNVERIFIED";
        using var editResponse = await client.PutAsJsonAsync(
            $"/api/packets/{packet.Id}/review",
            new ReviewRequest(approved.Revision, approved.Documents, false)
        );
        var edited = await Body<PacketResponse>(editResponse);
        Assert.Equal("review", edited.Status);
        // Reapproval alone cannot carry a verification for a changed reference.
        var reapproved = await Approve(client, edited);
        Assert.DoesNotContain(
            await Read<ReferenceResult[]>(client, $"/api/references/packets/{packet.Id}"),
            row => row.Revision == reapproved.Revision && row.Status == "verified"
        );
        using var blocked = await client.GetAsync($"/api/packets/{packet.Id}/export");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("Verify", await blocked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConfiguredFolderActualExportRetryReceiptAndOriginalSimulationRemainSeparate()
    {
        await using var app = new ReferenceFactory();
        using var client = app.Client();
        var data = await Dataset(client, false);
        var workflow = await Workflow(
            client,
            new("local", data.Id, data.Version, data.Hash, RequireVerified: true, AutoVerify: true)
        );
        var packet = await Upload(client, app, workflow);
        var evidence = Assert.Single(
            await Read<ReferenceResult[]>(client, $"/api/references/packets/{packet.Id}")
        );
        Assert.Equal("verified", evidence.Status);
        Assert.False(evidence.Synthetic);
        packet = await Approve(client, packet);
        using var queueResponse = await client.PostAsJsonAsync(
            $"/api/connectors/out/packets/{packet.Id}/deliveries",
            new ConnectorDeliveryRequest(packet.Id, packet.Revision)
        );
        var queued = await Body<ConnectorDelivery>(queueResponse);
        Assert.False(queued.SyntheticReferenceEvidence);
        Assert.Equal("local", queued.RequestedBy);
        var deliveries = app.Services.GetRequiredService<ConnectorDeliveryStore>();
        Directory.Delete(app.Output);
        await deliveries.ProcessNextAsync(default);
        Assert.Equal("failed", Assert.Single(deliveries.All()).Status);
        Directory.CreateDirectory(app.Output);
        using var retryResponse = await client.PostAsJsonAsync(
            $"/api/connectors/deliveries/{queued.Id}/retry",
            new { }
        );
        var retried = await Body<ConnectorDelivery>(retryResponse);
        Assert.Equal(queued.IdempotencyKey, retried.IdempotencyKey);
        Assert.Equal(queued.ContentHash, retried.ContentHash);
        await deliveries.ProcessNextAsync(default);
        var delivered = Assert.Single(
            await Read<ConnectorDelivery[]>(client, "/api/connectors/deliveries")
        );
        Assert.True(delivered.TransportAcknowledged);
        Assert.Equal("pending", delivered.BusinessStatus);
        var target = Assert.Single(Directory.GetFiles(app.Output));
        Assert.Equal(delivered.ContentHash, Pdfs.SourceHash(target));
        using (var zip = ZipFile.OpenRead(target))
        {
            Assert.Contains(zip.Entries, entry => entry.FullName.EndsWith(".pdf"));
            using var stream = zip.GetEntry("reference-provenance.json")!.Open();
            using var json = await JsonDocument.ParseAsync(stream);
            Assert.False(json.RootElement.GetProperty("synthetic").GetBoolean());
        }
        using (
            var invalidReceipt = await client.PostAsJsonAsync(
                $"/api/connectors/deliveries/{queued.Id}/receipt",
                new ConnectorReceiptRequest("maybe", "receipt")
            )
        )
            Assert.Equal(HttpStatusCode.BadRequest, invalidReceipt.StatusCode);
        using (
            var receiptResponse = await client.PostAsJsonAsync(
                $"/api/connectors/deliveries/{queued.Id}/receipt",
                new ConnectorReceiptRequest("accepted", "independent-receipt")
            )
        )
            Assert.Equal(
                "accepted",
                (await Body<ConnectorDelivery>(receiptResponse)).BusinessStatus
            );
        using (
            var immutable = await client.PostAsJsonAsync(
                $"/api/connectors/deliveries/{queued.Id}/receipt",
                new ConnectorReceiptRequest("rejected", "changed")
            )
        )
            Assert.Equal(HttpStatusCode.Conflict, immutable.StatusCode);
        // The original simulator remains a distinct queue/artifact path.
        using var simulationResponse = await client.PostAsJsonAsync(
            $"/api/packets/{packet.Id}/deliveries",
            new QueueDeliveryRequest(packet.Revision)
        );
        var simulation = await Body<DeliveryResponse>(simulationResponse);
        Assert.Equal("simulation", simulation.Mode);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        DeliveryResponse result;
        do
        {
            await Task.Delay(40, timeout.Token);
            result = Assert.Single(
                await Read<DeliveryResponse[]>(client, $"/api/packets/{packet.Id}/deliveries")
            );
        } while (result.Status is "queued" or "processing");
        Assert.Equal("delivered", result.Status);
        Assert.NotNull(result.ArtifactUrl);
        using var artifact = await client.GetAsync(result.ArtifactUrl);
        Assert.Equal(HttpStatusCode.OK, artifact.StatusCode);
        var previewWorkflow = await Workflow(
            client,
            new("local", data.Id, data.Version, data.Hash, RequireVerified: true, AutoVerify: true),
            publishVersion: false
        );
        var testPacket = await Upload(client, app, previewWorkflow, testRun: true);
        testPacket = await Approve(client, testPacket);
        using var testBlocked = await client.PostAsJsonAsync(
            $"/api/connectors/out/packets/{testPacket.Id}/deliveries",
            new ConnectorDeliveryRequest(testPacket.Id, testPacket.Revision)
        );
        Assert.Equal(HttpStatusCode.Conflict, testBlocked.StatusCode);
    }

    [Fact]
    public async Task HttpReferenceSnapshotPinsConfiguredBindingAndDoesNotFollowReboundEndpoint()
    {
        var calls = 0;
        await using var server = new ConnectorHttpDouble(async context =>
        {
            Interlocked.Increment(ref calls);
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(
                "[{\"reference\":\"ORDER-9001\",\"recordId\":\"remote-9001\",\"label\":\"ARTIFICIAL loopback fixture\"}]"u8.ToArray()
            );
        });
        await using var app = new ReferenceFactory(server.Url);
        using var client = app.Client();
        var workflow = await Workflow(
            client,
            new("http", ConnectorId: "lookup", RequireVerified: true)
        );
        var packet = await Upload(client, app, workflow);
        var pinned = packet.Workflow.Capabilities!.Reference!;
        Assert.NotNull(pinned.ConnectorBindingHash);
        Assert.Equal(
            app.Services.GetRequiredService<ConnectorCatalog>().BindingHash("lookup"),
            pinned.ConnectorBindingHash
        );
        Assert.Equal(1, calls);
        var candidate = Assert.Single(
            await Read<ReferenceResult[]>(client, $"/api/references/packets/{packet.Id}")
        );
        Assert.Equal("candidate", candidate.Status);
        var config = app.Services.GetRequiredService<IConfiguration>();
        config["paprika:Connectors:lookup:Endpoint"] =
            "http://127.0.0.1:1/rebound-must-never-be-called";
        using (
            var staleVerify = await client.PostAsJsonAsync(
                $"/api/references/packets/{packet.Id}/verify",
                new ReferenceVerifyRequest(
                    packet.Id,
                    packet.Revision,
                    candidate.DocumentId,
                    candidate.ResultHash,
                    "remote-9001"
                )
            )
        )
            Assert.Equal(HttpStatusCode.Conflict, staleVerify.StatusCode);
        using var lookupResponse = await client.PostAsJsonAsync(
            $"/api/references/packets/{packet.Id}/lookup",
            new ReferenceLookupRequest(packet.Id, packet.Revision)
        );
        var unavailable = Assert.Single(await Body<ReferenceResult[]>(lookupResponse));
        Assert.Equal("unavailable", unavailable.Status);
        Assert.Equal(1, calls);
    }

    private static async Task<ReferenceDataset> Dataset(HttpClient client, bool synthetic)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/references/datasets",
            new ReferenceDatasetRequest(
                "orders",
                "ARTIFICIAL isolated integration fixture",
                synthetic,
                [new("ORDER-9001", "record-9001", "ARTIFICIAL record; no actual customer data")]
            )
        );
        return await Body<ReferenceDataset>(response);
    }

    private static async Task<CatalogEntry> Workflow(
        HttpClient client,
        ReferencePolicy policy,
        bool publishVersion = true
    )
    {
        var catalog = await Read<WorkflowCatalogResponse>(client, "/api/workflows");
        var baseline = catalog.Entries.Single(entry =>
            entry.Kind == "workflow" && entry.Id == "general-logistics" && entry.Version == 1
        );
        var config = baseline.Workflow! with
        {
            Mode = "simulation",
            Model = "local-simulation-1",
            DefaultSplit = new("separate"),
            IdentifierKinds = ["order"],
            RequiredIdentifierKinds = [],
            Capabilities = new(Reference: policy),
        };
        using var created = await client.PostAsJsonAsync(
            "/api/workflows/catalog",
            new CreateCatalogRequest(
                "workflow",
                "reference-" + Guid.NewGuid().ToString("N"),
                "ARTIFICIAL reference integration",
                "Isolated acceptance fixture",
                Workflow: config
            )
        );
        var draft = await Body<CatalogEntry>(created);
        if (!publishVersion)
            return draft;
        using var publish = await client.PostAsJsonAsync(
            $"/api/workflows/catalog/workflow/{draft.Id}/{draft.Version}/publish",
            new { draft.Revision }
        );
        return await Body<CatalogEntry>(publish);
    }

    private static async Task<PacketResponse> Upload(
        HttpClient client,
        ReferenceFactory app,
        CatalogEntry workflow,
        bool testRun = false
    )
    {
        var path = Path.Combine(app.Root, Guid.NewGuid().ToString("N") + ".pdf");
        Pdfs.Synthetic(path, ["INVOICE\nOrder: ORDER-9001\nARTIFICIAL acceptance fixture"]);
        using var multipart = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(path));
        file.Headers.ContentType = new("application/pdf");
        multipart.Add(file, "files", "ARTIFICIAL-reference.pdf");
        multipart.Add(new StringContent(workflow.Id), "workflowId");
        multipart.Add(new StringContent(workflow.Version.ToString()), "workflowVersion");
        multipart.Add(new StringContent(testRun.ToString()), "testRun");
        if (testRun)
            multipart.Add(new StringContent(workflow.Revision.ToString()), "workflowRevision");
        using var response = await client.PostAsync("/api/packets", multipart);
        var packet = await Body<PacketResponse>(response);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (packet.Status is "queued" or "processing")
        {
            await Task.Delay(40, timeout.Token);
            packet = await Read<PacketResponse>(client, $"/api/packets/{packet.Id}");
        }
        Assert.Equal("review", packet.Status);
        return packet;
    }

    private static async Task<PacketResponse> Approve(HttpClient client, PacketResponse packet)
    {
        using var response = await client.PutAsJsonAsync(
            $"/api/packets/{packet.Id}/review",
            new ReviewRequest(packet.Revision, packet.Documents, true)
        );
        var approved = await Body<PacketResponse>(response);
        Assert.Equal("approved", approved.Status);
        return approved;
    }

    private static async Task<T> Read<T>(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        return await Body<T>(response);
    }

    private static async Task<T> Body<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, Contract.Json)!;
    }

    private sealed class ReferenceFactory : WebApplicationFactory<Program>
    {
        public string Root { get; } =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp",
                "opencode",
                "reference-integration-" + Guid.NewGuid().ToString("N")
            );
        public string Output => Path.Combine(Root, "output");
        private readonly string? lookup;

        public ReferenceFactory(string? lookup = null)
        {
            this.lookup = lookup;
            Directory.CreateDirectory(Output);
        }

        public HttpClient Client()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-paprika", "1");
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(
                (_, config) =>
                    config.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["paprika:DataDirectory"] = Path.Combine(Root, "data"),
                            ["paprika:Mode"] = "simulation",
                            ["paprika:Operations:Mode"] = "local",
                            ["Logging:LogLevel:Default"] = "Warning",
                            ["paprika:Connectors:out:Kind"] = "folderExport",
                            ["paprika:Connectors:out:Root"] = Output,
                            ["paprika:Connectors:lookup:Kind"] = lookup is null
                                ? null
                                : "httpLookup",
                            ["paprika:Connectors:lookup:Endpoint"] = lookup,
                        }
                    )
            );
            builder.ConfigureServices(services =>
            {
                foreach (
                    var descriptor in services
                        .Where(d =>
                            d.ServiceType == typeof(IHostedService)
                            && d.ImplementationType == typeof(ConnectorWorker)
                        )
                        .ToArray()
                )
                    services.Remove(descriptor);
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
        }
    }
}
