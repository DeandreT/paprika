using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using paprika;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using Xunit;

namespace paprika.Tests;

public sealed class SimulationTests
{
    [Theory]
    [InlineData(null, "simulation")]
    [InlineData("Simulation", "simulation")]
    [InlineData("Demo", "demo")]
    [InlineData("Live", "live")]
    public void ModesAndReadinessAreIndependent(string? mode, string expected)
    {
        var settings = new Settings(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["paprika:Mode"] = mode })
                .Build()
        );
        Assert.Equal(expected, settings.Mode);
        Assert.False(settings.OcrConfigured);
        Assert.False(settings.ClassifierConfigured);
    }

    [Theory]
    [InlineData("Demo", HttpStatusCode.OK)]
    [InlineData("Live", HttpStatusCode.BadRequest)]
    public async Task SeededWorkflowModeControlsUploadReadiness(
        string mode,
        HttpStatusCode expected
    )
    {
        await using var app = new Factory(false, mode);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-paprika", "1");
        using var upload = new MultipartFormDataContent();
        upload.Add(new ByteArrayContent(UploadWorkflowTests.MakePng()), "files", "image.png");
        using var response = await client.PostAsync("/api/packets", upload);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(
            expected == HttpStatusCode.OK ? 1 : 0,
            app.Services.GetRequiredService<Store>().All().Count
        );
        Assert.Equal(0, app.Network.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualUploadsUseLocalTextAndRulesWithNoNetwork(bool bogusCredentials)
    {
        await using var app = new Factory(bogusCredentials);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-paprika", "1");
        var config = await client.GetFromJsonAsync<ConfigResponse>("/api/config");
        Assert.Equal("simulation", config!.Mode);
        Assert.Equal(bogusCredentials, config.OcrConfigured);
        Assert.Equal(bogusCredentials, config.ClassifierConfigured);
        var fixture = Path.Combine(app.Root, "fixture.pdf");
        Pdfs.Synthetic(
            fixture,
            [
                "BILL OF LADING A\nPage 1 of 2",
                "BILL OF LADING A\nPage 2 of 2 continuation",
                "BILL OF LADING B",
                "BILL OF LADING C",
                "PACKING LIST",
                "PROOF OF DELIVERY",
                "INVOICE",
                "RECEIPT",
                "",
                "unrecognized text",
            ]
        );
        var second = Path.Combine(app.Root, "second.pdf");
        Pdfs.Synthetic(second, ["BILL OF LADING continuation Page 2 of 2"]);
        var scanned = Path.Combine(app.Root, "scanned.pdf");
        using (var pdf = new PdfSharp.Pdf.PdfDocument())
        {
            var page = pdf.AddPage();
            using var image = XImage.FromStream(new MemoryStream(UploadWorkflowTests.MakePng()));
            using (var graphics = XGraphics.FromPdfPage(page))
                graphics.DrawImage(image, 10, 10, 100, 100);
            pdf.Save(scanned);
        }
        using var upload = new MultipartFormDataContent();
        upload.Add(
            new ByteArrayContent(await File.ReadAllBytesAsync(fixture)),
            "files",
            "fixture.pdf"
        );
        upload.Add(
            new ByteArrayContent(await File.ReadAllBytesAsync(second)),
            "files",
            "second.pdf"
        );
        upload.Add(new ByteArrayContent(UploadWorkflowTests.MakePng()), "files", "image.png");
        upload.Add(
            new ByteArrayContent(await File.ReadAllBytesAsync(scanned)),
            "files",
            "scanned.pdf"
        );
        using var response = await client.PostAsync("/api/packets", upload);
        response.EnsureSuccessStatusCode();
        var queued = (await response.Content.ReadFromJsonAsync<PacketResponse>())!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        PacketResponse ready;
        do
        {
            await Task.Delay(25, timeout.Token);
            ready = (
                await client.GetFromJsonAsync<PacketResponse>(
                    $"/api/packets/{queued.Id}",
                    timeout.Token
                )
            )!;
            Assert.NotEqual("failed", ready.Status);
        } while (ready.Status != "review");
        Assert.Equal("simulation", ready.Mode);
        Assert.Equal(Simulation.Model, ready.Processing!.Model);
        Assert.Equal(
            new[]
            {
                "bol",
                "bol",
                "bol",
                "bol",
                "packing_list",
                "pod",
                "invoice",
                "receipt",
                "unknown",
                "unknown",
                "bol",
                "unknown",
                "unknown",
            },
            ready.Pages.Select(p => p.PredictedType)
        );
        Assert.Equal(12, ready.Documents.Count);
        Assert.Equal(ready.Pages.Take(2).Select(p => p.Id), ready.Documents[0].PageIds);
        Assert.Equal(ready.Pages.Select(p => p.Id), ready.Documents.SelectMany(d => d.PageIds));
        Assert.All(ready.Pages, p =>
            Assert.Contains(p.ReviewReasons, r =>
                r.Contains("no AI/OCR") && r.Contains("not calibrated")
            )
        );
        foreach (var index in new[] { 8, 11, 12 })
        {
            Assert.True(string.IsNullOrWhiteSpace(ready.Pages[index].Text));
            Assert.Contains(ready.Pages[index].ReviewReasons, r =>
                r.Contains("No OCR in simulation")
            );
        }
        Assert.Equal(1, ready.Pages[10].StartProbability);
        var store = app.Services.GetRequiredService<Store>();
        var persisted = store.Get(ready.Id)!;
        Assert.Equal(Simulation.PromptVersion, persisted.PromptVersion);
        Assert.Equal(Contract.TaxonomyVersion, persisted.TaxonomyVersion);
        Assert.All(persisted.Sources, s =>
        {
            Assert.NotNull(s.Sha256);
            Assert.Null(s.OcrJson);
            Assert.Null(s.OperationUrl);
        });
        var predictions = JsonSerializer.Serialize(ready.Pages, Contract.Json);
        Simulation.Process(persisted, store);
        Assert.Equal(predictions, JsonSerializer.Serialize(persisted.Pages, Contract.Json));
        Assert.Equal(0, app.Network.Calls);

        var doc = ready.Documents[0];
        var url = $"/api/packets/{ready.Id}/documents/{doc.Id}/download";
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(url)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (
                await client.GetAsync($"/api/packets/{ready.Id}/documents/missing/download")
            ).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync("/api/packets/missing/documents/missing/download")).StatusCode
        );
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync("/api/packets/missing/history")).StatusCode
        );
        var firstHistory = (
            await client.GetFromJsonAsync<List<HistoryResponse>>($"/api/packets/{ready.Id}/history")
        )!;
        Assert.Single(firstHistory);
        var immutable = JsonSerializer.Serialize(firstHistory[0], Contract.Json);
        doc.BusinessReference = "reviewed-reference";
        using var approved = await client.PutAsJsonAsync(
            $"/api/packets/{ready.Id}/review",
            new ReviewRequest(1, ready.Documents, true)
        );
        approved.EnsureSuccessStatusCode();
        using var download = await client.GetAsync(url);
        download.EnsureSuccessStatusCode();
        Assert.Equal("application/pdf", download.Content.Headers.ContentType!.MediaType);
        using (var buffer = new MemoryStream(await download.Content.ReadAsByteArrayAsync()))
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(buffer))
        {
            Assert.Equal(2, pdf.NumberOfPages);
            Assert.Contains("Page 1 of 2", pdf.GetPage(1).Text);
            Assert.Contains("Page 2 of 2", pdf.GetPage(2).Text);
        }
        using var export = await client.GetAsync($"/api/packets/{ready.Id}/export");
        using var zip = new ZipArchive(
            new MemoryStream(await export.Content.ReadAsByteArrayAsync())
        );
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        Assert.Equal("simulation", manifest.RootElement.GetProperty("mode").GetString());
        Assert.Equal(
            Simulation.Model,
            manifest.RootElement.GetProperty("processing").GetProperty("model").GetString()
        );
        using var edit = await client.PutAsJsonAsync(
            $"/api/packets/{ready.Id}/review",
            new ReviewRequest(2, ready.Documents, false)
        );
        edit.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync(url)).StatusCode);
        using var historyResponse = await client.GetAsync($"/api/packets/{ready.Id}/history");
        var raw = await historyResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("ocrJson", raw);
        Assert.DoesNotContain("operationUrl", raw);
        Assert.DoesNotContain("bogus-secret", raw);
        var history = JsonSerializer.Deserialize<List<HistoryResponse>>(raw, Contract.Json)!;
        Assert.Equal(new[] { 1, 2, 3 }, history.Select(h => h.Revision));
        Assert.Equal(new[] { "review", "approved", "review" }, history.Select(h => h.Status));
        Assert.Equal(immutable, JsonSerializer.Serialize(history[0], Contract.Json));
        Assert.Equal("approved", history[1].Action);
        Assert.Equal("reviewed-reference", history[1].Documents[0].BusinessReference);
        Assert.Equal(
            predictions,
            JsonSerializer.Serialize(store.Get(ready.Id)!.Pages, Contract.Json)
        );
        Assert.Equal(0, app.Network.Calls);
    }

    [Fact]
    public async Task PartialCheckpointResumesInOriginalOrderAndSanitizesExtractionFailures()
    {
        await using var app = new Factory(false);
        var store = new Store(app.Configuration);
        var packet = new Packet { Mode = "simulation" };
        var source = new Source
        {
            Name = "packet.pdf",
            Extension = ".pdf",
            PageCount = 3,
        };
        packet.Sources.Add(source);
        Directory.CreateDirectory(Path.Combine(store.Root, packet.Id));
        Pdfs.Synthetic(store.SourcePath(packet, source), ["INVOICE", "RECEIPT", "PACKING LIST"]);
        Simulation.Process(packet, store);
        var expected = JsonSerializer.Serialize(packet.Pages[1], Contract.Json);
        packet.Pages = [packet.Pages[1]];
        store.Save(packet);
        var resumed = store.Get(packet.Id)!;
        Simulation.Process(resumed, store);
        Assert.Equal(new[] { 1, 2, 3 }, resumed.Pages.Select(p => p.PageNumber));
        Assert.Equal(expected, JsonSerializer.Serialize(resumed.Pages[1], Contract.Json));
        Assert.Equal(3, resumed.Pages.Select(p => p.Id).Distinct().Count());
        resumed.Pages.RemoveAt(2);
        await File.WriteAllTextAsync(
            store.SourcePath(packet, source),
            "private malformed PDF data"
        );
        var error = Assert.Throws<InvalidOperationException>(() =>
            Simulation.Process(resumed, store)
        );
        Assert.Equal(
            "Local simulation PDF text extraction failed. No OCR was performed.",
            error.Message
        );
        Assert.Equal(2, resumed.Pages.Count);
    }

    private sealed class Factory(bool bogusCredentials, string? mode = null)
        : WebApplicationFactory<Program>
    {
        public string Root { get; } =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp",
                "opencode",
                "simulation-" + Guid.NewGuid().ToString("N")
            );
        public RejectNetwork Network { get; } = new();
        private Dictionary<string, string?> Values =>
            new()
            {
                ["paprika:DataDirectory"] = Root,
                ["paprika:Mode"] = mode,
                ["paprika:AzureEndpoint"] = bogusCredentials ? "https://invalid.example" : "",
                ["paprika:AzureKey"] = bogusCredentials ? "bogus-secret" : "",
                ["paprika:JevApiKey"] = bogusCredentials ? "bogus-secret" : "",
            };
        public IConfiguration Configuration =>
            new ConfigurationBuilder().AddInMemoryCollection(Values).Build();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(Root);
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(Values));
            builder.ConfigureServices(s =>
            {
                s.RemoveAll<IHttpClientFactory>();
                s.AddSingleton<IHttpClientFactory>(Network);
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

    private sealed class RejectNetwork : IHttpClientFactory
    {
        public int Calls;

        public HttpClient CreateClient(string name)
        {
            Calls++;
            throw new InvalidOperationException("Simulation must not create provider clients.");
        }
    }
}
