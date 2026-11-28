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
using paprika;
using Xunit;

namespace paprika.Tests;

public sealed class ApprovedFieldExportTests
{
    private const string InvoiceText =
        "Invoice: ARTIFICIAL-ORIGINAL\nTotal: 1,234.50\nDate: 11/13/2026\nPaid: YES";
    private const string Order = "ARTIFICIAL-ORDER";

    [Fact]
    public async Task FieldsUseLatestConfiguredRunForExactApprovalAndContainOnlyContractData()
    {
        await using var app = new ExportFactory();
        using var client = app.CreateClient();
        var definition = Definition();
        var approved = Approve(app.Store, app.AddPacket(definition, includeExcludedDocument: true));
        var original = ExtractionPlatform.Current(app.Store, approved)!;
        var latest = AddInvoiceRun(app.Store, original, "ARTIFICIAL-CORRECTED");
        // Insert competing runs after the selected run so newest-row-only lookup is not enough.
        AddInvoiceRun(
            app.Store,
            latest with
            {
                PacketRevision = approved.Revision - 1,
            },
            "ARTIFICIAL-STALE-REVISION"
        );
        AddInvoiceRun(
            app.Store,
            latest with
            {
                WorkflowSha256 = ExtractionStorage.Hash(
                    approved.EffectiveWorkflow with
                    {
                        Instructions = "Different workflow snapshot",
                    }
                ),
            },
            "ARTIFICIAL-WRONG-WORKFLOW"
        );
        var adHoc = definition with { Id = "artificial-ad-hoc" };
        AddInvoiceRun(app.Store, ExtractionPlatform.Extract(approved, adHoc), "ARTIFICIAL-AD-HOC");
        var changedDefinition = definition with
        {
            Fields = [.. definition.Fields[..^1], new("optional", "date", Label: "Other date")],
        };
        AddInvoiceRun(
            app.Store,
            ExtractionPlatform.Extract(approved, changedDefinition),
            "ARTIFICIAL-WRONG-DEFINITION"
        );

        var bytes = await client.GetByteArrayAsync(ExportUrl(approved));
        using var zip = new ZipArchive(new MemoryStream(bytes));
        using var fields = ReadJson(zip, "fields.json");
        var root = fields.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(approved.Id, root.GetProperty("packetId").GetString());
        Assert.Equal(approved.Revision, root.GetProperty("revision").GetInt32());
        Assert.Equal(approved.EffectiveWorkflow.Hash, root.GetProperty("workflowHash").GetString());
        var pinned = root.GetProperty("definition");
        Assert.Equal(definition.Id, pinned.GetProperty("id").GetString());
        Assert.Equal(definition.Version, pinned.GetProperty("version").GetInt32());
        Assert.Equal(ExtractionStorage.Hash(definition), pinned.GetProperty("sha256").GetString());

        var documents = root.GetProperty("documents")
            .EnumerateArray()
            .ToDictionary(d => d.GetProperty("documentId").GetString()!);
        Assert.Equal(2, documents.Count);
        var invoice = documents[approved.Documents[0].Id];
        Assert.Equal("invoice", invoice.GetProperty("type").GetString());
        var excluded = documents[approved.Documents[1].Id];
        Assert.Equal("bol", excluded.GetProperty("type").GetString());
        Assert.Empty(excluded.GetProperty("fields").EnumerateArray());

        var extracted = invoice
            .GetProperty("fields")
            .EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!);
        Assert.Equal(5, extracted.Count);
        AssertField(extracted["invoice"], "text", "ARTIFICIAL-CORRECTED", "corrected");
        AssertField(extracted["total"], "number", "1234.50", "extracted");
        AssertField(extracted["date"], "date", "2026-11-13", "extracted");
        AssertField(extracted["paid"], "boolean", "true", "extracted");
        AssertField(extracted["optional"], "date", null, "missing");
        Assert.Empty(extracted["optional"].GetProperty("evidence").EnumerateArray());
        var evidence = Assert.Single(extracted["invoice"].GetProperty("evidence").EnumerateArray());
        Assert.Equal<string>(
            ["length", "offset", "pageId", "pageNumber", "sourceId"],
            evidence.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
        );
        Assert.Equal(approved.Pages[0].Id, evidence.GetProperty("pageId").GetString());
        Assert.Equal(approved.Sources[0].Id, evidence.GetProperty("sourceId").GetString());
        Assert.Equal(1, evidence.GetProperty("pageNumber").GetInt32());
        Assert.Equal("Invoice: ".Length, evidence.GetProperty("offset").GetInt32());
        Assert.Equal("ARTIFICIAL-ORIGINAL".Length, evidence.GetProperty("length").GetInt32());
        Assert.DoesNotContain("ARTIFICIAL-ORIGINAL", root.GetRawText());
        Assert.NotNull(zip.GetEntry("platform.json"));
        Assert.NotNull(zip.GetEntry("manifest.json"));
        Assert.Equal(2, zip.Entries.Count(entry => entry.Name.EndsWith(".pdf")));

        Assert.Equal(bytes, await client.GetByteArrayAsync(ExportUrl(approved)));
        Assert.Equal(Digest(bytes), Assert.Single(ZipReceipts(app.Store, approved)).Sha256);
    }

    [Fact]
    public async Task ApprovalAndReleaseGatesApplyBeforeReturningFieldsEvenWithExistingReceipt()
    {
        await using var app = new ExportFactory();
        using var client = app.CreateClient();
        var references = app.Services.GetRequiredService<ReferenceService>();
        var dataset = references.Create(
            new("artificial-orders", "Artificial orders", true, [new(Order, "fixture", "Fixture")])
        );
        var packet = app.AddPacket(
            Definition(),
            reference: new(
                "local",
                dataset.Id,
                dataset.Version,
                dataset.Hash,
                RequireVerified: true
            )
        );
        await AssertBlocked(client, packet, "Approve");
        var approved = Approve(app.Store, packet);
        await AssertBlocked(client, approved, "Verify");
        Assert.Empty(ZipReceipts(app.Store, approved));

        var result = Assert.Single(
            await references.LookupAsync(
                new(approved.Id, approved.Revision),
                CancellationToken.None
            )
        );
        references.Verify(
            new(approved.Id, approved.Revision, result.DocumentId, result.ResultHash, "fixture")
        );
        var bytes = await client.GetByteArrayAsync(ExportUrl(approved));
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
            Assert.NotNull(zip.GetEntry("fields.json"));
        var receipt = Assert.Single(ZipReceipts(app.Store, approved));

        var run = ExtractionPlatform.Current(app.Store, approved)!;
        var invoice = Assert.Single(run.Results);
        var invalid = run with
        {
            Id = Guid.NewGuid().ToString("N"),
            Results =
            [
                invoice with
                {
                    Fields = invoice
                        .Fields.Select(f =>
                            f.Name == "invoice"
                                ? f with
                                {
                                    Value = null,
                                    Status = "missing",
                                    Evidence = [],
                                }
                                : f
                        )
                        .ToArray(),
                },
            ],
        };
        ExtractionStorage.Add(app.Store, "run", invalid.Id, invalid);
        await AssertBlocked(client, approved, "invoice");
        Assert.Equal(receipt, Assert.Single(ZipReceipts(app.Store, approved)));

        approved.Documents[0].BusinessReference = "ARTIFICIAL-CHANGED-ORDER";
        var edit = app.Store.Review(approved.Id, new(approved.Revision, approved.Documents, false));
        Assert.Equal(200, edit.Code);
        var edited = Assert.IsType<Packet>(edit.Packet);
        Assert.Equal(approved.Revision + 1, edited.Revision);
        await AssertBlocked(client, edited, "Approve");
    }

    [Theory]
    [InlineData("omit")]
    [InlineData(null)]
    public async Task OptionalClearCarriesToApprovalAndExportsCorrectedNullWithOriginalEvidence(
        string? category
    )
    {
        await using var app = new ExportFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-paprika", "1");
        var packet = Approve(app.Store, app.AddPacket(Definition()));
        var run = ExtractionPlatform.Current(app.Store, packet)!;
        var original = Assert.Single(run.Results).Fields.Single(f => f.Name == "paid");
        Assert.Equal("true", original.Value);
        using var corrected = await client.PostAsJsonAsync(
            $"/api/extraction/{packet.Id}/corrections",
            new ExtractionCorrection(
                packet.Revision,
                run.Id,
                packet.Documents[0].Id,
                "paid",
                null,
                "Omit the optional payment field.",
                category == null ? original.Evidence : [],
                Category: category
            )
        );
        corrected.EnsureSuccessStatusCode();
        await AssertBlocked(client, packet, "Approve");
        var history = (
            await client.GetFromJsonAsync<ExtractionHistoryResponse>($"/api/extraction/{packet.Id}")
        )!;
        var omission = Assert.Single(history.Corrections);
        Assert.Equal(
            category,
            omission.GetProperty("correction").GetProperty("category").GetString()
        );
        Assert.Equal(JsonValueKind.Null, omission.GetProperty("normalizedValue").ValueKind);
        Assert.Equal("true", omission.GetProperty("before").GetProperty("value").GetString());
        var omitted = omission.GetProperty("after").Deserialize<ExtractionValue>(Contract.Json)!;
        Assert.Null(omitted.Value);
        Assert.Equal(original.Evidence, omitted.Evidence);
        var approved = Approve(app.Store, app.Store.Get(packet.Id)!);
        using var zip = new ZipArchive(
            new MemoryStream(await client.GetByteArrayAsync(ExportUrl(approved)))
        );
        using var fields = ReadJson(zip, "fields.json");
        Assert.Equal(1, fields.RootElement.GetProperty("schemaVersion").GetInt32());
        var paid = Assert
            .Single(fields.RootElement.GetProperty("documents").EnumerateArray())
            .GetProperty("fields")
            .EnumerateArray()
            .Single(f => f.GetProperty("name").GetString() == "paid");
        AssertField(paid, "boolean", null, "corrected");
        var evidence = Assert.Single(paid.GetProperty("evidence").EnumerateArray());
        var span = Assert.Single(original.Evidence);
        Assert.Equal(span.PageId, evidence.GetProperty("pageId").GetString());
        Assert.Equal(span.Offset, evidence.GetProperty("offset").GetInt32());
        Assert.Equal(span.Length, evidence.GetProperty("length").GetInt32());
    }

    [Fact]
    public async Task ExportWithFieldsDoesNotReuseOrOverwritePreFieldsBundleReceipt()
    {
        await using var app = new ExportFactory();
        using var client = app.CreateClient();
        var approved = Approve(app.Store, app.AddPacket(Definition()));
        var (legacy, legacyPath, legacyBytes) = SeedPreFieldsReceipt(app.Store, approved);
        Assert.Equal(legacy, Assert.Single(ZipReceipts(app.Store, approved)));

        var upgraded = await client.GetByteArrayAsync(ExportUrl(approved));
        using var zip = new ZipArchive(new MemoryStream(upgraded));
        using var fields = ReadJson(zip, "fields.json");
        using var platform = ReadJson(zip, "platform.json");
        var snapshotHash = ExtractionStorage.Hash(
            new { platform = platform.RootElement, fields = fields.RootElement }
        );
        Assert.Equal(legacyBytes, File.ReadAllBytes(legacyPath));
        using (var legacyZip = new ZipArchive(new MemoryStream(legacyBytes)))
            Assert.Null(legacyZip.GetEntry("fields.json"));
        var receipts = ZipReceipts(app.Store, approved);
        Assert.Contains(legacy, receipts);
        var canonical = Assert.Single(receipts, r => r != legacy);
        Assert.EndsWith($"/packet-fields-v1-{snapshotHash}.zip", canonical.Key);
        Assert.Equal(Digest(upgraded), canonical.Sha256);
        Assert.Equal(upgraded, await client.GetByteArrayAsync(ExportUrl(approved)));
        Assert.Equal(receipts, ZipReceipts(app.Store, approved));
    }

    [Fact]
    public async Task ApprovedPacketWithoutConfiguredExtractionOmitsFieldsEvenWithAdHocResults()
    {
        await using var app = new ExportFactory();
        using var client = app.CreateClient();
        var approved = Approve(app.Store, app.AddPacket());
        var adHoc = ExtractionPlatform.Extract(approved, Definition());
        ExtractionStorage.Add(app.Store, "run", adHoc.Id, adHoc);

        using var zip = new ZipArchive(
            new MemoryStream(await client.GetByteArrayAsync(ExportUrl(approved)))
        );
        Assert.Null(zip.GetEntry("fields.json"));
        Assert.NotNull(zip.GetEntry("manifest.json"));
    }

    internal static ExtractionDefinition Definition() =>
        new(
            "artificial-invoice",
            1,
            [
                new("invoice", Label: "Invoice", Required: true),
                new("total", "number", Label: "Total"),
                new("date", "date", Label: "Date"),
                new("paid", "boolean", Label: "Paid"),
                new("optional", "date", Label: "Optional date"),
            ],
            [],
            "invoice"
        );

    internal static ExtractionRun AddInvoiceRun(Store store, ExtractionRun run, string invoice)
    {
        var result = Assert.Single(run.Results);
        var added = run with
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = run.CreatedAt.AddTicks(1),
            Results =
            [
                result with
                {
                    Fields = result
                        .Fields.Select(f =>
                            f.Name == "invoice"
                                ? f with
                                {
                                    Value = invoice,
                                    Status = "corrected",
                                }
                                : f
                        )
                        .ToArray(),
                },
            ],
        };
        ExtractionStorage.Add(store, "run", added.Id, added);
        return added;
    }

    private static void AssertField(JsonElement field, string kind, string? value, string status)
    {
        Assert.Equal<string>(
            ["evidence", "kind", "name", "status", "value"],
            field.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
        );
        Assert.Equal(kind, field.GetProperty("kind").GetString());
        Assert.Equal(value, field.GetProperty("value").GetString());
        Assert.Equal(status, field.GetProperty("status").GetString());
    }

    private static Packet Approve(Store store, Packet packet)
    {
        var review = store.Review(packet.Id, new(packet.Revision, packet.Documents, true));
        Assert.Equal(200, review.Code);
        var approved = Assert.IsType<Packet>(review.Packet);
        Assert.Equal("approved", approved.Status);
        return approved;
    }

    private static string ExportUrl(Packet packet) => $"/api/packets/{packet.Id}/export";

    private static async Task AssertBlocked(HttpClient client, Packet packet, string reason)
    {
        using var response = await client.GetAsync(ExportUrl(packet));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(reason, await response.Content.ReadAsStringAsync());
    }

    private static JsonDocument ReadJson(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        return JsonDocument.Parse(stream);
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record Receipt(string Key, string Sha256);

    private static Receipt[] ZipReceipts(Store store, Packet packet)
    {
        using var db = new SqliteConnection(store.ConnectionString);
        db.Open();
        using var q = db.CreateCommand();
        q.CommandText =
            "SELECT key,sha256 FROM export_receipts WHERE packet_id=$id AND key LIKE '%.zip' ORDER BY key";
        q.Parameters.AddWithValue("$id", packet.Id);
        using var rows = q.ExecuteReader();
        var receipts = new List<Receipt>();
        while (rows.Read())
            receipts.Add(new(rows.GetString(0), rows.GetString(1)));
        return receipts.ToArray();
    }

    private static (Receipt Receipt, string Path, byte[] Bytes) SeedPreFieldsReceipt(
        Store store,
        Packet approved
    )
    {
        // Re-create the old evidence-hash receipt using a ZIP without the new entry.
        using var current = new ZipArchive(new MemoryStream(Pdfs.Export(approved, store)));
        using var platformBytes = new MemoryStream();
        using (var platform = current.GetEntry("platform.json")!.Open())
            platform.CopyTo(platformBytes);
        using var buffer = new MemoryStream();
        using (var legacy = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            foreach (var entry in current.Entries.Where(e => e.Name != "fields.json"))
            {
                using var input = entry.Open();
                using var output = legacy.CreateEntry(entry.FullName).Open();
                input.CopyTo(output);
            }
        }
        var bytes = buffer.ToArray();
        var digest = Digest(bytes);
        var stem = $"packet-{Digest(platformBytes.ToArray())}";
        var receipt = new Receipt(
            $"packets/{approved.Id}/exports/{approved.Revision}/{stem}.zip",
            digest
        );
        var directory = Path.Combine(
            store.Root,
            approved.Id,
            "exports",
            approved.Revision.ToString()
        );
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{stem}-{digest}.zip");
        File.WriteAllBytes(path, bytes);
        using var db = new SqliteConnection(store.ConnectionString);
        db.Open();
        using var insert = db.CreateCommand();
        insert.CommandText = "INSERT INTO export_receipts VALUES($id,$key,$digest)";
        insert.Parameters.AddWithValue("$id", approved.Id);
        insert.Parameters.AddWithValue("$key", receipt.Key);
        insert.Parameters.AddWithValue("$digest", receipt.Sha256);
        insert.ExecuteNonQuery();
        return (receipt, path, bytes);
    }

    internal sealed class ExportFactory : WebApplicationFactory<Program>
    {
        private readonly string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp",
            "opencode",
            "approved-field-export-" + Guid.NewGuid().ToString("N")
        );

        public Store Store => Services.GetRequiredService<Store>();

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureAppConfiguration(
                (_, config) =>
                    config.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["paprika:DataDirectory"] = root,
                            ["paprika:Mode"] = "simulation",
                            ["paprika:Operations:Mode"] = "local",
                        }
                    )
            );

        public Packet AddPacket(
            ExtractionDefinition? definition = null,
            bool includeExcludedDocument = false,
            ReferencePolicy? reference = null,
            string invoiceText = InvoiceText
        )
        {
            var workflow = WorkflowRegistry.Legacy() with
            {
                Hash = "",
                Capabilities = definition is null
                    ? null
                    : new(
                        Reference: reference,
                        Extraction: new(
                            definition.Id,
                            definition.Version,
                            ExtractionStorage.Hash(definition),
                            definition
                        )
                    ),
            };
            var packet = new Packet
            {
                Status = "review",
                Workflow = workflow with { Hash = ExtractionStorage.Hash(workflow) },
            };
            var source = new Source { Name = "ARTIFICIAL.pdf", Extension = ".pdf" };
            packet.Sources.Add(source);
            AddPage("invoice", invoiceText);
            if (includeExcludedDocument)
                AddPage("bol", "Invoice: ARTIFICIAL-EXCLUDED");
            source.PageCount = packet.Pages.Count;
            var path = Store.SourcePath(packet, source);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            Pdfs.Synthetic(path, packet.Pages.Select(p => p.Text).ToArray());
            source.Sha256 = Pdfs.SourceHash(path);
            Store.Save(packet);
            return packet;

            void AddPage(string type, string text)
            {
                var page = new Page
                {
                    SourceId = source.Id,
                    SourceName = source.Name,
                    PageNumber = packet.Pages.Count + 1,
                    PredictedType = type,
                    Text = text,
                };
                packet.Pages.Add(page);
                packet.Documents.Add(
                    new Document
                    {
                        Type = type,
                        PageIds = [page.Id],
                        BusinessReference = Order,
                    }
                );
            }
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
