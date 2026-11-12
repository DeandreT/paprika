using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using PaperworkClassifier;
using PdfSharp.Pdf.IO;
using Xunit;

namespace PaperworkClassifier.Tests;

public sealed class BackendTests : IDisposable
{
    readonly string root = Path.Combine(
        Path.GetTempPath(),
        "paperwork-tests-" + Guid.NewGuid().ToString("N")
    );
    IConfiguration Config =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    { "Paperwork:DataDirectory", root },
                    { "Paperwork:AzureEndpoint", "https://ocr.example.test" },
                    { "Paperwork:AzureKey", "test-placeholder" },
                    { "Paperwork:JevApiKey", "test-placeholder" },
                }
            )
            .Build();
    Store Store => new(Config);

    Packet Ready()
    {
        var s = Store;
        var p = Worker.Demo(s);
        p.Documents = Worker.Assemble(p.Pages);
        p.Status = "review";
        s.Save(p);
        return p;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }

    [Fact]
    public void FixtureIsRealFivePagePdf()
    {
        var p = Ready();
        using var pdf = PdfReader.Open(
            Store.SourcePath(p, p.Sources[0]),
            PdfDocumentOpenMode.Import
        );
        Assert.Equal(5, pdf.PageCount);
        Assert.Equal(4, p.Documents.Count);
        Assert.Equal(2, p.Documents[0].PageIds.Count);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("lost")]
    [InlineData("foreign")]
    [InlineData("empty")]
    [InlineData("type")]
    [InlineData("duplicate-document")]
    [InlineData("null-document")]
    [InlineData("null-page")]
    public void ReviewRejectsInvalidMapping(string kind)
    {
        var p = Ready();
        switch (kind)
        {
            case "null-document":
                p.Documents.Add(null!);
                break;
            case "null-page":
                p.Documents[0].PageIds[0] = null!;
                break;
            case "duplicate":
                p.Documents[0].PageIds.Add(p.Pages[0].Id);
                break;
            case "lost":
                p.Documents[0].PageIds.RemoveAt(0);
                break;
            case "foreign":
                p.Documents[0].PageIds[0] = "missing";
                break;
            case "empty":
                p.Documents[0].PageIds = [];
                break;
            case "type":
                p.Documents[0].Type = "not-supported";
                break;
            case "duplicate-document":
                p.Documents[1].Id = p.Documents[0].Id;
                break;
        }
        Assert.Equal(400, Store.Review(p.Id, new(p.Revision, p.Documents, true)).Code);
        Assert.Equal(1, Store.Get(p.Id)!.Revision);
    }

    [Fact]
    public void StaleReviewAndApprovedEdit()
    {
        var s = Store;
        var p = Ready();
        var approved = s.Review(p.Id, new(1, p.Documents, true));
        Assert.Equal("approved", approved.Packet!.Status);
        Assert.Equal(409, s.Review(p.Id, new(1, p.Documents, false)).Code);
        var changed = s.Review(p.Id, new(2, p.Documents, true));
        Assert.Equal("review", changed.Packet!.Status);
        Assert.Equal(3, changed.Packet.Revision);
        Assert.Equal("approval invalidated; review saved", changed.Packet.Audit.Last().Action);
        Assert.Equal("approved", s.Review(p.Id, new(3, p.Documents, true)).Packet!.Status);
    }

    [Fact]
    public void ReviewReasonsAreServerOwned()
    {
        var p = Ready();
        p.Documents[0].ReviewReasons = ["arbitrary client content"];
        var result = Store.Review(p.Id, new(1, p.Documents, false));
        Assert.DoesNotContain(
            "arbitrary client content",
            result.Packet!.Documents[0].ReviewReasons
        );
        Assert.Contains("Document type is unknown.", result.Packet.Documents[^1].ReviewReasons);
    }

    [Fact]
    public void ExportPreservesExplicitReorderingAndMapping()
    {
        var s = Store;
        var p = Ready();
        p.Documents =
        [
            new() { Type = "bol", PageIds = [p.Pages[4].Id, p.Pages[0].Id] },
            new()
            {
                Type = "packing_list",
                PageIds = [p.Pages[3].Id, p.Pages[2].Id, p.Pages[1].Id],
            },
        ];
        p = s.Review(p.Id, new(1, p.Documents, true)).Packet!;
        using var stream = new MemoryStream(Pdfs.Export(p, s));
        using var zip = new ZipArchive(stream);
        Assert.Equal(3, zip.Entries.Count);
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        Assert.Equal(
            Pdfs.SourceHash(s.SourcePath(p, p.Sources[0])),
            manifest.RootElement.GetProperty("sources")[0].GetProperty("sha256").GetString()
        );
        Assert.Equal(
            "synthetic-demo",
            manifest.RootElement.GetProperty("processing").GetProperty("model").GetString()
        );
        Assert.Equal(
            "synthetic-demo-1",
            manifest.RootElement.GetProperty("processing").GetProperty("promptVersion").GetString()
        );
        Assert.Equal(
            Contract.TaxonomyVersion,
            manifest
                .RootElement.GetProperty("processing")
                .GetProperty("taxonomyVersion")
                .GetString()
        );
        Assert.Equal(
            5,
            manifest
                .RootElement.GetProperty("documents")[0]
                .GetProperty("pages")[0]
                .GetProperty("pageNumber")
                .GetInt32()
        );
        using var pdfStream = new MemoryStream();
        zip.GetEntry("001-bol.pdf")!.Open().CopyTo(pdfStream);
        pdfStream.Position = 0;
        using var pdf = PdfReader.Open(pdfStream, PdfDocumentOpenMode.Import);
        Assert.Equal(2, pdf.PageCount);
        Assert.Contains(
            "Counter slip",
            pdf.Pages[0].Contents.Elements.GetDictionary(0)!.Stream.ToString()
        );
        Assert.Contains(
            "BILL OF LADING",
            pdf.Pages[1].Contents.Elements.GetDictionary(0)!.Stream.ToString()
        );
    }

    [Fact]
    public async Task RestartRecoversProcessingJob()
    {
        var s = Store;
        var p = Worker.Demo(s);
        p.Status = "processing";
        s.Save(p);
        var fresh = Store;
        using var worker = MessagingTestHost.Create(
            fresh,
            new Providers(
                new Settings(Config),
                fresh,
                new FakeFactory(_ => throw new Exception("Demo must never call providers"))
            )
        );
        await worker.StartAsync(default);
        try
        {
            // First dispatch can compile a handler while other test hosts are starting.
            // Wait for the durable outcome; a two-second startup timing is not a contract.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (fresh.Get(p.Id)!.Status is "queued" or "processing")
                await Task.Delay(20, deadline.Token);
        }
        finally
        {
            await worker.StopAsync(default);
        }
        Assert.Equal("review", fresh.Get(p.Id)!.Status);
        Assert.Equal(5, fresh.Get(p.Id)!.Pages.Count);
        Assert.Equal("synthetic-demo", fresh.Get(p.Id)!.Processing!.Model);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("canceled")]
    [InlineData("cancelled")]
    public async Task OcrTerminalFailureClearsSavedOperation(string status)
    {
        var s = Store;
        var p = Ready();
        p.Sources[0].OperationUrl = "https://ocr.example.test/operations/saved";
        s.Save(p);
        int calls = 0;
        var providers = new Providers(
            new Settings(Config),
            s,
            new FakeFactory(request =>
            {
                calls++;
                Assert.Equal(HttpMethod.Get, request.Method);
                return Json(new { status, error = new { message = "private provider details" } });
            })
        );
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            providers.Ocr(p, p.Sources[0], default)
        );
        Assert.Equal("OCR processing failed.", ex.Message);
        Assert.Equal(1, calls);
        Assert.Null(Store.Get(p.Id)!.Sources[0].OperationUrl);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task CredentialFailureDoesNotResubmitOrDiscardSavedOperation(HttpStatusCode status)
    {
        var s = Store;
        var p = Ready();
        const string operation = "https://ocr.example.test/operations/saved";
        p.Sources[0].OperationUrl = operation;
        s.Save(p);
        int calls = 0;
        var providers = new Providers(
            new Settings(Config),
            s,
            new FakeFactory(request =>
            {
                calls++;
                Assert.Equal(HttpMethod.Get, request.Method);
                return new(status) { Content = new StringContent("private provider details") };
            })
        );
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            providers.Ocr(p, p.Sources[0], default)
        );
        Assert.Contains("credentials", ex.Message);
        Assert.DoesNotContain("private", ex.Message);
        Assert.Equal(1, calls);
        Assert.Equal(operation, Store.Get(p.Id)!.Sources[0].OperationUrl);
    }

    [Fact]
    public async Task OcrResumesPersistedOperationAndCachesResult()
    {
        var s = Store;
        var p = Ready();
        var source = p.Sources[0];
        source.OperationUrl = "https://ocr.example.test/operations/saved";
        s.Save(p);
        int calls = 0;
        var factory = new FakeFactory(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            return Json(
                new
                {
                    status = "succeeded",
                    analyzeResult = new
                    {
                        pages = Enumerable
                            .Range(1, 5)
                            .Select(i => new
                            {
                                pageNumber = i,
                                lines = new[] { new { content = $"page {i}" } },
                            }),
                    },
                }
            );
        });
        var fresh = Store;
        var reloaded = fresh.Get(p.Id)!;
        var providers = new Providers(new Settings(Config), fresh, factory);
        Assert.Equal(5, (await providers.Ocr(reloaded, reloaded.Sources[0], default)).Count);
        Assert.Equal(5, (await providers.Ocr(reloaded, reloaded.Sources[0], default)).Count);
        Assert.Equal(1, calls);
        Assert.NotNull(Store.Get(p.Id)!.Sources[0].OcrJson);
    }

    [Fact]
    public async Task OcrRejectsFreeTierPartialPageSet()
    {
        var s = Store;
        var p = Ready();
        p.Sources[0].OcrJson = JsonSerializer.Serialize(
            new
            {
                status = "succeeded",
                analyzeResult = new
                {
                    pages = new[]
                    {
                        new { pageNumber = 1, lines = Array.Empty<object>() },
                        new { pageNumber = 2, lines = Array.Empty<object>() },
                    },
                },
            }
        );
        var providers = new Providers(
            new Settings(Config),
            s,
            new FakeFactory(_ => throw new Exception())
        );
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            providers.Ocr(p, p.Sources[0], default)
        );
        Assert.Contains("incomplete", ex.Message);
    }

    [Fact]
    public async Task OcrDoesNotSendKeyToForeignOperation()
    {
        var s = Store;
        var p = Ready();
        p.Sources[0].OperationUrl = "https://attacker.example/operation";
        var providers = new Providers(
            new Settings(Config),
            s,
            new FakeFactory(_ => throw new Exception("Must not send"))
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            providers.Ocr(p, p.Sources[0], default)
        );
    }

    [Fact]
    public async Task ClassifierValidatesProbabilities()
    {
        var s = Store;
        var p = Ready();
        var providers = new Providers(
            new Settings(Config),
            s,
            new FakeFactory(_ =>
                Json(
                    new
                    {
                        model = "fake",
                        answers = new
                        {
                            documentType = new { choice = "bol", confidence = 1.2 },
                            startsDocument = new { noul = .5 },
                        },
                    }
                )
            )
        );
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            providers.Classify(p, p.Pages[0], null, default)
        );
    }

    [Fact]
    public void AssemblyUsesBoundariesRatherThanTypes()
    {
        var a = new Page { PredictedType = "bol", StartProbability = 1 };
        var b = new Page { PredictedType = "pod", StartProbability = .1 };
        var c = new Page { PredictedType = "pod", StartProbability = .9 };
        var docs = Worker.Assemble([a, b, c]);
        Assert.Equal(2, docs.Count);
        Assert.Equal(2, docs[0].PageIds.Count);
        Assert.Contains("Page predictions differ within this document.", docs[0].ReviewReasons);
    }

    static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };

    sealed class FakeFactory(Func<HttpRequestMessage, HttpResponseMessage> send)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(send));
    }

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(send(request));
    }
}
