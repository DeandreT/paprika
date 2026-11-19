using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using paprika;
using Xunit;

namespace paprika.Tests;

public sealed class PlatformReleaseIntegrationTests
{
    [Fact]
    public async Task ApprovedExportContainsCurrentExtractionAndVerifiedReferenceEvidenceAndEditBlocksBothDownloads()
    {
        await using var app = new ReleaseFactory();
        using var client = app.Client();
        var setup = await app.ConfiguredPacket(client);
        var reference = Assert.Single(
            await Get<ReferenceResult[]>(client, $"/api/references/packets/{setup.Packet.Id}")
        );
        await Post<ReferenceResult>(
            client,
            $"/api/references/packets/{setup.Packet.Id}/verify",
            new ReferenceVerifyRequest(
                setup.Packet.Id,
                setup.Packet.Revision,
                reference.DocumentId,
                reference.ResultHash,
                "artificial-order-record"
            )
        );
        var approved = await Review(client, setup.Packet, true);
        string download = Download(approved);
        using var pdf = await client.GetAsync(download);
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith(
            "%PDF-",
            Encoding.ASCII.GetString(await pdf.Content.ReadAsByteArrayAsync())
        );
        using var export = await client.GetAsync($"/api/packets/{approved.Id}/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var zipBytes = new MemoryStream(await export.Content.ReadAsByteArrayAsync());
        using var zip = new ZipArchive(zipBytes);
        using (var embeddedPdf = zip.Entries.Single(e => e.Name.EndsWith(".pdf")).Open())
        using (var buffer = new MemoryStream())
        {
            await embeddedPdf.CopyToAsync(buffer);
            Assert.Equal(await pdf.Content.ReadAsByteArrayAsync(), buffer.ToArray());
        }
        Assert.Equal(
            zipBytes.ToArray(),
            await client.GetByteArrayAsync($"/api/packets/{approved.Id}/export")
        );
        var platform = await ReadEntry(zip, "platform.json");
        using var evidence = JsonDocument.Parse(platform);
        var root = evidence.RootElement;
        Assert.Equal(approved.Id, root.GetProperty("packetId").GetString());
        Assert.Equal(approved.Revision, root.GetProperty("revision").GetInt32());
        Assert.Equal(approved.Workflow.Hash, root.GetProperty("workflowHash").GetString());
        var verified = Assert.Single(root.GetProperty("referenceEvidence").EnumerateArray());
        Assert.Equal("verified", verified.GetProperty("status").GetString());
        Assert.Equal(approved.Revision, verified.GetProperty("revision").GetInt32());
        Assert.Equal(setup.Dataset.Hash, verified.GetProperty("datasetHash").GetString());
        Assert.True(verified.GetProperty("synthetic").GetBoolean());
        var extraction = Assert.Single(root.GetProperty("extractionRuns").EnumerateArray());
        Assert.Equal(approved.Revision, extraction.GetProperty("packetRevision").GetInt32());
        Assert.Equal(
            ExtractionStorage.Hash(approved.Workflow),
            extraction.GetProperty("workflowSha256").GetString()
        );
        var result = Assert.Single(extraction.GetProperty("results").EnumerateArray());
        Assert.Equal(
            Assert.Single(approved.Documents).Id,
            result.GetProperty("documentId").GetString()
        );
        Assert.Equal(setup.Definition.Sha256, result.GetProperty("definitionSha256").GetString());
        var field = Assert.Single(result.GetProperty("fields").EnumerateArray());
        Assert.Equal("125.50", field.GetProperty("value").GetString());
        Assert.Equal("extracted", field.GetProperty("status").GetString());
        Assert.NotEmpty(field.GetProperty("evidence").EnumerateArray());
        using var manifest = JsonDocument.Parse(await ReadEntry(zip, "manifest.json"));
        Assert.Equal(approved.Revision, manifest.RootElement.GetProperty("revision").GetInt32());
        foreach (var json in new[] { platform, manifest.RootElement.GetRawText() })
        {
            Assert.DoesNotContain(ReleaseFactory.PrivateMarker, json);
            Assert.DoesNotContain("ocrJson", json);
            Assert.DoesNotContain("operationUrl", json);
            Assert.DoesNotContain("AzureKey", json);
            Assert.DoesNotContain("JevApiKey", json);
        }
        approved.Documents[0].BusinessReference = "ARTIFICIAL-CORRECTED-UNVERIFIED";
        var edited = await Review(client, approved, false);
        Assert.Equal("review", edited.Status);
        Assert.Equal(approved.Revision + 1, edited.Revision);
        await Blocked(client, $"/api/packets/{approved.Id}/export");
        await Blocked(client, download);
        // Historical successful ZIP bytes remain an exact approved-revision artifact.
        Assert.Equal(approved.Revision, evidence.RootElement.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task CommittedBundleReceiptRejectsMissingOrModifiedArtifactInsteadOfRegenerating()
    {
        await using var app = new ReleaseFactory();
        using var client = app.Client();
        var store = app.Services.GetRequiredService<Store>();
        var packet = Worker.Demo(store);
        packet.Status = "review";
        packet.Documents = Worker.Assemble(packet.Pages);
        store.Save(packet);
        var approved = store
            .Review(packet.Id, new(packet.Revision, packet.Documents, true))
            .Packet!;
        var url = $"/api/packets/{packet.Id}/export";
        var original = await client.GetByteArrayAsync(url);
        var dir = Path.Combine(store.Root, packet.Id, "exports", approved.Revision.ToString());
        var path = Assert.Single(Directory.GetFiles(dir, "*.zip"));
        File.WriteAllBytes(path, [1, 2, 3]);
        using (var response = await client.GetAsync(url))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        File.Delete(path);
        using (var response = await client.GetAsync(url))
            Assert.False(response.IsSuccessStatusCode);
        Assert.False(File.Exists(path));
        File.WriteAllBytes(path, original);
        Assert.Equal(original, await client.GetByteArrayAsync(url));
    }

    [Fact]
    public async Task ApprovalAloneCannotReleaseConfiguredButUnverifiedBusinessEvidence()
    {
        await using var app = new ReleaseFactory();
        using var client = app.Client();
        var setup = await app.ConfiguredPacket(client);
        var approved = await Review(client, setup.Packet, true);
        Assert.Equal("approved", approved.Status);
        await Blocked(client, $"/api/packets/{approved.Id}/export");
        await Blocked(client, Download(approved));
        var reference = Assert.Single(
            await Post<ReferenceResult[]>(
                client,
                $"/api/references/packets/{approved.Id}/lookup",
                new ReferenceLookupRequest(approved.Id, approved.Revision)
            )
        );
        Assert.NotEqual("verified", reference.Status);
        await Post<ReferenceResult>(
            client,
            $"/api/references/packets/{approved.Id}/verify",
            new ReferenceVerifyRequest(
                approved.Id,
                approved.Revision,
                reference.DocumentId,
                reference.ResultHash,
                "artificial-order-record"
            )
        );
        using var released = await client.GetAsync($"/api/packets/{approved.Id}/export");
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
    }

    [Fact]
    public async Task ApprovedOutputKeepsWriterLeaseUntilBytesAreReadyThenWaitingReviewInvalidatesFutureExport()
    {
        await using var app = new ReleaseFactory();
        using var client = app.Client();
        var setup = await app.ConfiguredPacket(client);
        var reference = Assert.Single(
            await Get<ReferenceResult[]>(client, $"/api/references/packets/{setup.Packet.Id}")
        );
        await Post<ReferenceResult>(
            client,
            $"/api/references/packets/{setup.Packet.Id}/verify",
            new ReferenceVerifyRequest(
                setup.Packet.Id,
                setup.Packet.Revision,
                reference.DocumentId,
                reference.ResultHash,
                "artificial-order-record"
            )
        );
        var approved = await Review(client, setup.Packet, true);
        var store = app.Services.GetRequiredService<Store>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var output = Task.Run(() => store.ApprovedOutput(approved.Id, packet =>
            {
                // Nested read-only release checks must work while the outer immediate transaction is held.
                var bytes = Pdfs.Export(packet, store);
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(15)))
                    throw new TimeoutException("Test failed to release output callback.");
                return bytes;
            }));
        Task<HttpResponseMessage>? edit = null;
        try
        {
            Assert.True(
                entered.Wait(TimeSpan.FromSeconds(10)),
                "Approved output never reached its callback."
            );
            approved.Documents[0].BusinessReference = "ARTIFICIAL-CONCURRENT-CORRECTION";
            edit = client.PutAsJsonAsync(
                $"/api/packets/{approved.Id}/review",
                new ReviewRequest(approved.Revision, approved.Documents, false)
            );
            // A live HTTP write must remain pending while byte generation owns the immediate lease.
            var first = await Task.WhenAny(edit, Task.Delay(250));
            Assert.NotSame(edit, first);
            Assert.False(output.IsCompleted);
        }
        finally
        {
            release.Set();
        }
        var bytes = await output.WaitAsync(TimeSpan.FromSeconds(20));
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
        using (var manifest = JsonDocument.Parse(await ReadEntry(zip, "manifest.json")))
            Assert.Equal(
                approved.Revision,
                manifest.RootElement.GetProperty("revision").GetInt32()
            );
        using var response = await edit!.WaitAsync(TimeSpan.FromSeconds(20));
        var changed = await Body<PacketResponse>(response);
        Assert.Equal("review", changed.Status);
        Assert.Equal(approved.Revision + 1, changed.Revision);
        await Blocked(client, $"/api/packets/{approved.Id}/export");
        await Blocked(client, Download(approved));
    }

    private static string Download(PacketResponse packet) =>
        $"/api/packets/{packet.Id}/documents/{Assert.Single(packet.Documents).Id}/download";

    private static async Task Blocked(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private static async Task<string> ReadEntry(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static async Task<PacketResponse> Review(
        HttpClient client,
        PacketResponse packet,
        bool approve
    )
    {
        using var response = await client.PutAsJsonAsync(
            $"/api/packets/{packet.Id}/review",
            new ReviewRequest(packet.Revision, packet.Documents, approve)
        );
        return await Body<PacketResponse>(response);
    }

    private static async Task<T> Get<T>(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        return await Body<T>(response);
    }

    private static async Task<T> Post<T>(HttpClient client, string url, object body)
    {
        using var response = await client.PostAsJsonAsync(url, body);
        return await Body<T>(response);
    }

    private static async Task<T> Body<T>(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.IsSuccessStatusCode,
            $"{response.RequestMessage?.RequestUri}: {(int)response.StatusCode} {json}"
        );
        return JsonSerializer.Deserialize<T>(json, Contract.Json)!;
    }

    private sealed record Configured(
        PacketResponse Packet,
        ReferenceDataset Dataset,
        ExtractionDefinitionResponse Definition
    );

    private sealed class ReleaseFactory : WebApplicationFactory<Program>
    {
        public const string PrivateMarker = "ARTIFICIAL-private-provider-configuration-marker";
        public string Root { get; } =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp",
                "opencode",
                "release-integration-" + Guid.NewGuid().ToString("N")
            );

        public ReleaseFactory() => Directory.CreateDirectory(Root);

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration(
                (_, config) =>
                    config.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["paprika:DataDirectory"] = Path.Combine(Root, "data"),
                            ["paprika:Mode"] = "simulation",
                            ["paprika:Operations:Mode"] = "local",
                            ["paprika:AzureKey"] = PrivateMarker,
                            ["paprika:JevApiKey"] = PrivateMarker,
                        }
                    )
            );

        public HttpClient Client()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-paprika", "1");
            return client;
        }

        public async Task<Configured> ConfiguredPacket(HttpClient client)
        {
            var data = await Post<ReferenceDataset>(
                client,
                "/api/references/datasets",
                new ReferenceDatasetRequest(
                    "artificial-orders",
                    "ARTIFICIAL export acceptance",
                    true,
                    [
                        new(
                            "ORDER-9001",
                            "artificial-order-record",
                            "ARTIFICIAL; no customer evidence"
                        ),
                    ]
                )
            );
            var definition = await Post<ExtractionDefinitionResponse>(
                client,
                "/api/extraction/definitions",
                new ExtractionDefinition(
                    "artificial-total",
                    1,
                    [
                        new(
                            "total",
                            "number",
                            Pattern: @"Total:\s*(?<value>[0-9]+\.[0-9]{2})",
                            Required: true
                        ),
                    ],
                    [],
                    "invoice"
                )
            );
            var catalog = await Get<WorkflowCatalogResponse>(client, "/api/workflows");
            var baseline = catalog.Entries.Single(e =>
                e.Kind == "workflow" && e.Id == "general-logistics" && e.Version == 1
            );
            var config = baseline.Workflow! with
            {
                Mode = "simulation",
                Model = Simulation.Model,
                DefaultSplit = new("separate"),
                IdentifierKinds = ["order"],
                Capabilities = new(
                    Reference: new(
                        "local",
                        data.Id,
                        data.Version,
                        data.Hash,
                        RequireVerified: true
                    ),
                    Extraction: new(definition.Definition.Id, 1, definition.Sha256)
                ),
            };
            var draft = await Post<CatalogEntry>(
                client,
                "/api/workflows/catalog",
                new CreateCatalogRequest(
                    "workflow",
                    "release-" + Guid.NewGuid().ToString("N"),
                    "ARTIFICIAL release acceptance",
                    "Isolated fixture",
                    Workflow: config
                )
            );
            var published = await Post<CatalogEntry>(
                client,
                $"/api/workflows/catalog/workflow/{draft.Id}/{draft.Version}/publish",
                new RevisionRequest(draft.Revision)
            );
            return new(
                await Upload(
                    client,
                    "INVOICE\nOrder: ORDER-9001\nTotal: 125.50\nARTIFICIAL release fixture",
                    new(published.Id, published.Version)
                ),
                data,
                definition
            );
        }

        public async Task<PacketResponse> Upload(
            HttpClient client,
            string text,
            VersionRef? workflow = null
        )
        {
            string path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".pdf");
            Pdfs.Synthetic(path, [text]);
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(await File.ReadAllBytesAsync(path));
            file.Headers.ContentType = new("application/pdf");
            form.Add(file, "files", "ARTIFICIAL-release-fixture.pdf");
            if (workflow is not null)
            {
                form.Add(new StringContent(workflow.Id), "workflowId");
                form.Add(new StringContent(workflow.Version.ToString()), "workflowVersion");
            }
            using var uploaded = await client.PostAsync("/api/packets", form);
            var packet = await Body<PacketResponse>(uploaded);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (packet.Status is "queued" or "processing")
            {
                await Task.Delay(40, timeout.Token);
                packet = await Get<PacketResponse>(client, $"/api/packets/{packet.Id}");
            }
            Assert.Equal("review", packet.Status);
            return packet;
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
        }
    }
}
