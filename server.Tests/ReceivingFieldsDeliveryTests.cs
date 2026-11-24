using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using paprika;
using Xunit;

namespace paprika.Tests;

public sealed class ReceivingFieldsDeliveryTests
{
    private const string PrintedCarrier = "Example Receivng Freight";
    private const string ReviewedCarrier = "Example Receiving Freight";
    private static readonly (string Name, string Label, string PrintedValue, int Page)[] Fields =
    [
        ("shipment_id", "Shipment ID", "SYN-SHIP-1001", 1),
        ("bol", "BOL", "SYN-BOL-2002", 1),
        ("po", "PO", "00003003", 1),
        ("carrier", "Carrier", PrintedCarrier, 1),
        ("pickup_address", "Pickup address", "101 Example Way, Testville", 2),
        ("delivery_address", "Delivery address", "202 Sample Road, Testville", 2),
    ];

    [Fact]
    public async Task ApprovedReceivingFieldsReachIdempotentReceiverWithReviewedValueAndEvidence()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = timeout.Token;
        using var firstAttempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempts = new ConcurrentQueue<(string Key, string? Hash, byte[] Bytes)>();
        var records = new ConcurrentDictionary<string, ReceivingRecord>();
        var requestCount = 0;
        await using var receiver = new ConnectorHttpDouble(async context =>
        {
            var key = context.Request.Headers["Idempotency-Key"]!;
            using var body = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(body, ct);
            var bytes = body.ToArray();
            var declaredHash = context.Request.Headers["X-Content-SHA256"];
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (string.IsNullOrWhiteSpace(key) || hash != declaredHash)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }
            using var json = JsonDocument.Parse(ReadFields(bytes));
            var values = json
                .RootElement.GetProperty("documents")[0]
                .GetProperty("fields")
                .EnumerateArray()
                .ToDictionary(
                    f => f.GetProperty("name").GetString()!,
                    f => f.GetProperty("value").GetString()
                );
            var record = records.GetOrAdd(key, _ =>
                new(hash, values, "synthetic-receipt-" + Guid.NewGuid().ToString("N"))
            );
            if (record.Hash != hash)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Conflict;
                return;
            }
            attempts.Enqueue((key, declaredHash, bytes));
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                // The business record exists, but the sender loses the acknowledgement.
                firstAttempt.Cancel();
                context.Response.Abort();
            }
            else
                context.Response.StatusCode = (int)HttpStatusCode.Accepted;
        });

        await using var app = new ReceivingFactory(receiver.Url);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-paprika", "1");
        var store = app.Services.GetRequiredService<Store>();
        using var definition = GetType()
            .Assembly.GetManifestResourceStream("receiving-fields.json")!;
        using var definitionBody = new StreamContent(definition);
        definitionBody.Headers.ContentType = new("application/json");
        using var created = await client.PostAsync(
            "/api/extraction/definitions",
            definitionBody,
            ct
        );
        var registered = await Body<ExtractionDefinitionResponse>(created, ct);
        var workflow = PublishReceivingWorkflow(store, registered);
        var packet = await Upload(client, app.Root, workflow, ct);
        Assert.False(packet.TestRun);
        Assert.Equal("simulation", packet.Mode);
        Assert.Equal(workflow.Id, packet.Workflow.Id);
        Assert.Equal(workflow.Version, packet.Workflow.Version);
        var doc = Assert.Single(packet.Documents);
        Assert.Equal("bol", doc.Type);
        Assert.Equal(2, doc.PageIds.Count);

        var packetUrl = $"/api/packets/{packet.Id}";
        using var extractedResponse = await client.GetAsync($"/api/extraction/{packet.Id}", ct);
        var originalRun = Assert.Single(
            (await Body<ExtractionHistoryResponse>(extractedResponse, ct)).Runs
        );
        Assert.Equal(packet.Revision, originalRun.PacketRevision);
        var original = Assert.Single(originalRun.Results).Fields.Single(f => f.Name == "carrier");
        Assert.Equal("extracted", original.Status);
        Assert.Equal(PrintedCarrier, original.Value);
        Assert.Single(original.Evidence);

        using var correctionResponse = await client.PostAsJsonAsync(
            $"/api/extraction/{packet.Id}/corrections",
            new ExtractionCorrection(
                packet.Revision,
                originalRun.Id,
                doc.Id,
                "carrier",
                ReviewedCarrier,
                "Standardized the carrier spelling and retained the printed value as evidence.",
                original.Evidence
            ),
            ct
        );
        var correction = await Body<ExtractionCorrectionResponse>(correctionResponse, ct);
        Assert.Equal("review", correction.Packet.Status);
        Assert.Equal(packet.Revision + 1, correction.Packet.Revision);
        var corrected = Assert
            .Single(correction.Run.Results)
            .Fields.Single(f => f.Name == "carrier");
        Assert.Equal("corrected", corrected.Status);
        Assert.Equal(ReviewedCarrier, corrected.Value);
        Assert.Equal(original.Evidence, corrected.Evidence);

        using (var unapproved = await client.GetAsync($"{packetUrl}/export", ct))
            Assert.Equal(HttpStatusCode.Conflict, unapproved.StatusCode);
        using var approvedResponse = await client.PutAsJsonAsync(
            $"{packetUrl}/review",
            new ReviewRequest(correction.Packet.Revision, correction.Packet.Documents, true),
            ct
        );
        var approved = await Body<PacketResponse>(approvedResponse, ct);
        Assert.Equal("approved", approved.Status);
        Assert.Equal(correction.Packet.Revision + 1, approved.Revision);
        Assert.Equal(packet.Workflow.Hash, approved.Workflow.Hash);
        using var export = await client.GetAsync($"{packetUrl}/export", ct);
        export.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", export.Content.Headers.ContentType!.MediaType);
        var approvedFields = ReadFields(await export.Content.ReadAsByteArrayAsync(ct));
        AssertFields(approvedFields, approved, registered);

        using var queuedResponse = await client.PostAsJsonAsync(
            $"/api/connectors/receiving/packets/{approved.Id}/deliveries",
            new ConnectorDeliveryRequest(approved.Id, approved.Revision),
            ct
        );
        var queued = await Body<ConnectorDelivery>(queuedResponse, ct);
        var deliveries = app.Services.GetRequiredService<ConnectorDeliveryStore>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            deliveries.ProcessNextAsync(firstAttempt.Token)
        );
        Assert.Single(attempts);
        var businessRecord = Assert.Single(records);
        Assert.Equal(queued.IdempotencyKey, businessRecord.Key);

        var recovered = new ConnectorDeliveryStore(
            store,
            app.Services.GetRequiredService<ConnectorCatalog>(),
            app.Services.GetRequiredService<ReferenceService>()
        );
        Assert.Equal("queued", Assert.Single(recovered.All(approved.Id)).Status);
        Assert.True(await recovered.ProcessNextAsync(ct));
        var delivered = Assert.Single(recovered.All(approved.Id));
        Assert.Equal(queued.Id, delivered.Id);
        Assert.Equal(approved.Id, delivered.PacketId);
        Assert.Equal(approved.Revision, delivered.Revision);
        Assert.Equal(approved.Workflow.Hash, delivered.WorkflowHash);
        Assert.Equal("delivered", delivered.Status);
        Assert.True(delivered.TransportAcknowledged);
        Assert.Equal("pending", delivered.BusinessStatus);
        Assert.Null(delivered.ReceiptId);

        var requests = attempts.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(requests[0].Bytes, requests[1].Bytes);
        Assert.All(requests, request =>
        {
            Assert.Equal(queued.IdempotencyKey, request.Key);
            Assert.Equal(queued.ContentHash, request.Hash);
            Assert.Equal(
                queued.ContentHash,
                Convert.ToHexStringLower(SHA256.HashData(request.Bytes))
            );
        });
        Assert.Equal(approvedFields, ReadFields(requests[1].Bytes));
        Assert.Same(businessRecord.Value, Assert.Single(records).Value);
        foreach (var field in Fields)
            Assert.Equal(
                ApprovedValue(field.Name, field.PrintedValue),
                businessRecord.Value.Fields[field.Name]
            );

        using var receiptResponse = await client.PostAsJsonAsync(
            $"/api/connectors/deliveries/{delivered.Id}/receipt",
            new ConnectorReceiptRequest("accepted", businessRecord.Value.ReceiptId),
            ct
        );
        var receipt = await Body<ConnectorDelivery>(receiptResponse, ct);
        Assert.Equal("accepted", receipt.BusinessStatus);
        Assert.Equal(businessRecord.Value.ReceiptId, receipt.ReceiptId);
        Assert.Equal("local", receipt.ReceiptRecordedBy);
        Assert.Equal(receipt, Assert.Single(recovered.All(approved.Id)));
    }

    private static CatalogEntry PublishReceivingWorkflow(
        Store store,
        ExtractionDefinitionResponse definition
    )
    {
        var entries = store.Workflows.Catalog().Entries;
        var release = entries.Single(e => e.Kind == "release" && e.Id == "receiving-documents");
        store.Workflows.Publish(release.Kind, release.Id, release.Version, release.Revision);
        var draft = entries.Single(e => e.Kind == "workflow" && e.Id == "receiving");
        var edited = store.Workflows.Edit(
            draft.Kind,
            draft.Id,
            draft.Version,
            draft with
            {
                Workflow = draft.Workflow! with
                {
                    // This synthetic packet has shipment paperwork but no separate receipt document.
                    RequiredIdentifierKinds = [],
                    Capabilities = new(
                        Extraction: new(
                            definition.Definition.Id,
                            definition.Definition.Version,
                            definition.Sha256
                        )
                    ),
                },
            }
        );
        return store.Workflows.Publish(edited.Kind, edited.Id, edited.Version, edited.Revision);
    }

    private static async Task<PacketResponse> Upload(
        HttpClient client,
        string root,
        CatalogEntry workflow,
        CancellationToken ct
    )
    {
        var path = Path.Combine(root, "synthetic-receiving.pdf");
        var texts = Enumerable
            .Range(1, 2)
            .Select(page =>
                $"ARTIFICIAL BILL OF LADING\nPage {page} of 2\n"
                + string.Join(
                    '\n',
                    Fields.Where(f => f.Page == page).Select(f => $"{f.Label}: {f.PrintedValue}")
                )
            )
            .ToArray();
        Pdfs.Synthetic(path, texts);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(path, ct));
        file.Headers.ContentType = new("application/pdf");
        form.Add(file, "files", Path.GetFileName(path));
        form.Add(new StringContent(workflow.Id), "workflowId");
        form.Add(new StringContent(workflow.Version.ToString()), "workflowVersion");
        using var uploaded = await client.PostAsync("/api/packets", form, ct);
        var packet = await Body<PacketResponse>(uploaded, ct);
        while (packet.Status is "queued" or "processing")
        {
            await Task.Delay(40, ct);
            using var response = await client.GetAsync($"/api/packets/{packet.Id}", ct);
            packet = await Body<PacketResponse>(response, ct);
        }
        Assert.True(packet.Status == "review", $"Packet reached {packet.Status}: {packet.Error}");
        return packet;
    }

    private static string ApprovedValue(string name, string printedValue) =>
        name == "carrier" ? ReviewedCarrier : printedValue;

    private static byte[] ReadFields(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var entry = Assert.Single(zip.Entries, e => e.FullName == "fields.json").Open();
        using var copy = new MemoryStream();
        entry.CopyTo(copy);
        return copy.ToArray();
    }

    private static void AssertFields(
        byte[] fieldsBytes,
        PacketResponse packet,
        ExtractionDefinitionResponse definition
    )
    {
        using var json = JsonDocument.Parse(fieldsBytes);
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(packet.Id, root.GetProperty("packetId").GetString());
        Assert.Equal(packet.Revision, root.GetProperty("revision").GetInt32());
        Assert.Equal(packet.Workflow.Hash, root.GetProperty("workflowHash").GetString());
        var identity = root.GetProperty("definition");
        Assert.Equal(definition.Definition.Id, identity.GetProperty("id").GetString());
        Assert.Equal(definition.Definition.Version, identity.GetProperty("version").GetInt32());
        Assert.Equal(definition.Sha256, identity.GetProperty("sha256").GetString());
        var document = Assert.Single(root.GetProperty("documents").EnumerateArray());
        var approvedDocument = Assert.Single(packet.Documents);
        Assert.Equal(approvedDocument.Id, document.GetProperty("documentId").GetString());
        Assert.Equal(approvedDocument.Type, document.GetProperty("type").GetString());
        var fields = document.GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(Fields.Length, fields.Length);
        foreach (var (name, _, printedValue, pageNumber) in Fields)
        {
            var field = Assert.Single(fields, f => f.GetProperty("name").GetString() == name);
            Assert.Equal("text", field.GetProperty("kind").GetString());
            Assert.Equal(ApprovedValue(name, printedValue), field.GetProperty("value").GetString());
            Assert.Equal(
                name == "carrier" ? "corrected" : "extracted",
                field.GetProperty("status").GetString()
            );
            var evidence = Assert.Single(field.GetProperty("evidence").EnumerateArray());
            Assert.Equal(5, evidence.EnumerateObject().Count());
            var page = packet.Pages.Single(p => p.PageNumber == pageNumber);
            var offset = page.Text.IndexOf(printedValue, StringComparison.Ordinal);
            Assert.True(offset >= 0, $"Missing printed value for {name} on page {pageNumber}.");
            Assert.Equal(page.Id, evidence.GetProperty("pageId").GetString());
            Assert.Equal(page.SourceId, evidence.GetProperty("sourceId").GetString());
            Assert.Equal(pageNumber, evidence.GetProperty("pageNumber").GetInt32());
            Assert.Equal(offset, evidence.GetProperty("offset").GetInt32());
            Assert.Equal(printedValue.Length, evidence.GetProperty("length").GetInt32());
        }
    }

    private static async Task<T> Body<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct);
        Assert.True(
            response.IsSuccessStatusCode,
            $"{response.RequestMessage?.RequestUri}: {(int)response.StatusCode} {raw}"
        );
        return JsonSerializer.Deserialize<T>(raw, Contract.Json)!;
    }

    private sealed record ReceivingRecord(
        string Hash,
        Dictionary<string, string?> Fields,
        string ReceiptId
    );

    private sealed class ReceivingFactory(string endpoint) : WebApplicationFactory<Program>
    {
        public string Root { get; } =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp",
                "opencode",
                "receiving-fields-" + Guid.NewGuid().ToString("N")
            );

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
                            ["paprika:Connectors:receiving:Kind"] = "httpDelivery",
                            ["paprika:Connectors:receiving:Endpoint"] = endpoint,
                            ["paprika:Connectors:receiving:SupportsIdempotency"] = "true",
                        }
                    )
            );
            builder.ConfigureServices(services =>
            {
                // Only the connector pump is driven manually so the first send can be cancelled.
                var worker = services.Single(d =>
                    d.ServiceType == typeof(IHostedService)
                    && d.ImplementationType == typeof(ConnectorWorker)
                );
                services.Remove(worker);
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new NoProviderNetwork());
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
        }
    }

    private sealed class NoProviderNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException($"Unexpected provider request: {name}.");
    }
}
