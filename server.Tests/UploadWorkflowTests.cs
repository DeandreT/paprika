using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PaperworkClassifier;
using PdfSharp.Pdf.IO;
using Xunit;

namespace PaperworkClassifier.Tests;

public sealed class UploadWorkflowTests
{
    const string AzureKey = "azure-placeholder-integration-only";
    const string JevKey = "jev-placeholder-integration-only";
    const string PrivateMarker = "private-provider-envelope";
    static readonly string[] Texts =
    [
        "BILL OF LADING A - Page 1 of 2",
        "BILL OF LADING A - Page 2 of 2 continuation",
        "BILL OF LADING B - Page 1 of 1",
        "Unreadable counter slip",
    ];
    static readonly byte[] Png = MakePng();

    internal static byte[] MakePng()
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] data)
        {
            var length = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            output.Write(length);
            var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            output.Write(body);
            uint crc = uint.MaxValue;
            foreach (byte b in body)
            {
                crc ^= b;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0u);
            }
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
            output.Write(length);
        }
        Chunk("IHDR", [0, 0, 0, 64, 0, 0, 0, 64, 8, 2, 0, 0, 0]);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
        {
            for (int row = 0; row < 64; row++)
            {
                zlib.WriteByte(0); // No PNG row filter; alternating black and white RGB rows.
                zlib.Write(Enumerable.Repeat((byte)(row % 2 == 0 ? 255 : 0), 64 * 3).ToArray());
            }
        }
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return output.ToArray();
    }

    [Fact]
    public async Task LiveMultipartRunsDurableWorkerReviewAndApprovedExport()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var ct = timeout.Token;
        await using var app = new LiveFactory();
        var pdfPath = Path.Combine(app.Root, "fixture.pdf");
        Pdfs.Synthetic(pdfPath, Texts[..3]);
        var pdf = await File.ReadAllBytesAsync(pdfPath, ct);
        app.Transport.ExpectedSources = [pdf, Png];
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaperworkClassifier", "1");
        client.DefaultRequestHeaders.Add("Origin", "http://localhost:4200");

        using var configResponse = await client.GetAsync("/api/config", ct);
        var config = await PublicJson(configResponse, ct);
        Assert.Equal("live", config.GetProperty("mode").GetString());
        using var multipart = Multipart(("bundle.pdf", pdf), ("slip.png", Png));
        using var uploaded = await client.PostAsync("/api/packets", multipart, ct);
        var queued = await PublicJson(uploaded, ct);
        string id = queued.GetProperty("id").GetString()!;
        Assert.Equal("queued", queued.GetProperty("status").GetString());

        JsonElement ready;
        while (true)
        {
            using var response = await client.GetAsync($"/api/packets/{id}", ct);
            ready = await PublicJson(response, ct);
            var status = ready.GetProperty("status").GetString();
            Assert.True(status != "failed", ready.GetRawText() + "\n" + app.Transport.Failure);
            if (status == "review")
                break;
            await Task.Delay(25, ct);
        }
        Assert.Null(app.Transport.Failure);
        Assert.Equal(2, app.Transport.Submissions);
        Assert.Equal(2, app.Transport.Polls);
        Assert.Equal(4, app.Transport.Classifications);
        Assert.Equal("complete", ready.GetProperty("processing").GetProperty("stage").GetString());
        var pages = ready.GetProperty("pages").Deserialize<List<Page>>(Contract.Json)!;
        var documents = ready.GetProperty("documents").Deserialize<List<Document>>(Contract.Json)!;
        Assert.Equal(
            new[] { "bundle.pdf", "bundle.pdf", "bundle.pdf", "slip.png" },
            pages.Select(p => p.SourceName)
        );
        Assert.Equal(new[] { 1, 2, 3, 1 }, pages.Select(p => p.PageNumber));
        Assert.Single(pages.Take(3).Select(p => p.SourceId).Distinct());
        Assert.NotEqual(pages[0].SourceId, pages[3].SourceId);
        Assert.Equal(Texts, pages.Select(p => p.Text));
        Assert.Equal(new[] { "bol", "bol", "unknown" }, documents.Select(d => d.Type));
        Assert.Equal(new[] { pages[0].Id, pages[1].Id }, documents[0].PageIds);
        Assert.Equal(new[] { pages[2].Id }, documents[1].PageIds);
        Assert.Equal(new[] { pages[3].Id }, documents[2].PageIds);
        Assert.Contains("Uncertain document type.", pages[3].ReviewReasons);
        Assert.Contains("Document type is unknown.", documents[2].ReviewReasons);

        // Reload through a separate Store instance: the real worker persisted its checkpoints.
        var store = new Store(app.Configuration);
        var persisted = store.Get(id)!;
        Assert.Equal("review", persisted.Status);
        Assert.All(persisted.Sources, s => Assert.Contains(PrivateMarker, s.OcrJson));
        Assert.All(persisted.Sources, s => Assert.NotNull(s.OperationUrl));
        foreach (var group in pages.GroupBy(p => p.SourceId))
        {
            using var source = await client.GetAsync(group.First().PreviewUrl, ct);
            source.EnsureSuccessStatusCode();
            Assert.Equal(
                group.First().SourceName == "bundle.pdf" ? pdf : Png,
                await source.Content.ReadAsByteArrayAsync(ct)
            );
        }
        using (var blocked = await client.GetAsync($"/api/packets/{id}/export", ct))
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        // Exercise an actual reviewer correction, then a separate approval revision.
        documents[2].Type = "receipt";
        documents[2].BusinessReference = "REVIEWED-SLIP";
        using var saved = await client.PutAsJsonAsync(
            $"/api/packets/{id}/review",
            new ReviewRequest(1, documents, false),
            ct
        );
        var review = await PublicJson(saved, ct);
        Assert.Equal("review", review.GetProperty("status").GetString());
        Assert.Equal(2, review.GetProperty("revision").GetInt32());
        using var approvedResponse = await client.PutAsJsonAsync(
            $"/api/packets/{id}/review",
            new ReviewRequest(2, documents, true),
            ct
        );
        var approved = await PublicJson(approvedResponse, ct);
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        Assert.Equal(3, approved.GetProperty("revision").GetInt32());
        using var export = await client.GetAsync($"/api/packets/{id}/export", ct);
        export.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", export.Content.Headers.ContentType!.MediaType);
        using var bytes = new MemoryStream(await export.Content.ReadAsByteArrayAsync(ct));
        using var zip = new ZipArchive(bytes);
        Assert.Equal(
            new[] { "001-bol.pdf", "002-bol.pdf", "003-receipt.pdf", "manifest.json" },
            zip.Entries.Select(e => e.FullName)
        );
        using var manifestStream = zip.GetEntry("manifest.json")!.Open();
        using var manifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: ct);
        var provenance = manifest.RootElement;
        AssertPrivate(provenance.GetRawText());
        Assert.Equal(3, provenance.GetProperty("revision").GetInt32());
        Assert.Equal(
            "simulated-jev-model",
            provenance.GetProperty("processing").GetProperty("model").GetString()
        );
        Assert.Equal(
            Providers.PromptVersion,
            provenance.GetProperty("processing").GetProperty("promptVersion").GetString()
        );
        Assert.Equal(
            Contract.TaxonomyVersion,
            provenance.GetProperty("processing").GetProperty("taxonomyVersion").GetString()
        );
        var sources = provenance.GetProperty("sources").EnumerateArray().ToArray();
        Assert.Equal(
            new[] { Hash(pdf), Hash(Png) },
            sources.Select(s => s.GetProperty("sha256").GetString())
        );
        var mapped = provenance
            .GetProperty("documents")
            .EnumerateArray()
            .SelectMany(d => d.GetProperty("pages").EnumerateArray())
            .ToArray();
        Assert.Equal(
            pages.Select(p => p.Id),
            mapped.Select(p => p.GetProperty("pageId").GetString())
        );
        Assert.Equal(
            pages.Select(p => p.SourceId),
            mapped.Select(p => p.GetProperty("sourceId").GetString())
        );
        Assert.Equal(
            pages.Select(p => p.PageNumber),
            mapped.Select(p => p.GetProperty("pageNumber").GetInt32())
        );
        foreach (
            var (name, count) in new[]
            {
                ("001-bol.pdf", 2),
                ("002-bol.pdf", 1),
                ("003-receipt.pdf", 1),
            }
        )
        {
            using var entry = zip.GetEntry(name)!.Open();
            using var buffer = new MemoryStream();
            await entry.CopyToAsync(buffer, ct);
            buffer.Position = 0;
            using var output = PdfReader.Open(buffer, PdfDocumentOpenMode.Import);
            Assert.Equal(count, output.PageCount);
            if (name == "001-bol.pdf")
            {
                Assert.Contains(
                    Texts[0],
                    output.Pages[0].Contents.Elements.GetDictionary(0)!.Stream.ToString()
                );
                Assert.Contains(
                    Texts[1],
                    output.Pages[1].Contents.Elements.GetDictionary(0)!.Stream.ToString()
                );
            }
            if (name == "002-bol.pdf")
                Assert.Contains(
                    Texts[2],
                    output.Pages[0].Contents.Elements.GetDictionary(0)!.Stream.ToString()
                );
        }
        using var listing = await client.GetAsync("/api/packets", ct);
        Assert.Single((await PublicJson(listing, ct)).EnumerateArray());
    }

    [Theory]
    [InlineData("broken.pdf")]
    [InlineData("broken.png")]
    public async Task CorruptUploadIsRejectedWithoutQueueingOrCallingProviders(string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var app = new LiveFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-PaperworkClassifier", "1");
        // A valid first source followed by a broken one also exercises partial-upload cleanup.
        using var multipart = Multipart(
            ("valid.png", Png),
            (name, name.EndsWith("pdf") ? "%PDF-1.4\ntruncated"u8.ToArray() : Png[..16])
        );
        using var response = await client.PostAsync("/api/packets", multipart, timeout.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertPrivate(await response.Content.ReadAsStringAsync(timeout.Token));
        Assert.Empty(new Store(app.Configuration).All());
        Assert.DoesNotContain(Directory.GetDirectories(app.Root), path =>
            Path.GetFileName(path) != "deliveries"
        );
        Assert.Empty(new DeliveryStore(new Store(app.Configuration)).All());
        Assert.Equal(0, app.Transport.Submissions);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, "https://foreign.example")]
    public async Task UploadEnforcesMutationHeaderAndOrigin(bool marker, string? origin)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var app = new LiveFactory();
        using var client = app.CreateClient();
        if (marker)
            client.DefaultRequestHeaders.Add("X-PaperworkClassifier", "1");
        if (origin is not null)
            client.DefaultRequestHeaders.Add("Origin", origin);
        using var multipart = Multipart(("slip.png", Png));
        using var response = await client.PostAsync("/api/packets", multipart, timeout.Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(new Store(app.Configuration).All());
        Assert.Equal(0, app.Transport.Submissions);
    }

    static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    static MultipartFormDataContent Multipart(params (string Name, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var file in files)
        {
            var content = new ByteArrayContent(file.Bytes);
            content.Headers.ContentType = new(
                file.Name.EndsWith("pdf") ? "application/pdf" : "image/png"
            );
            form.Add(content, "files", file.Name);
        }
        return form;
    }

    static async Task<JsonElement> PublicJson(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        string raw = await response.Content.ReadAsStringAsync(ct);
        AssertPrivate(raw);
        using var json = JsonDocument.Parse(raw);
        return json.RootElement.Clone();
    }

    static void AssertPrivate(string raw)
    {
        foreach (
            var forbidden in new[]
            {
                AzureKey,
                JevKey,
                PrivateMarker,
                "ocrJson",
                "operationUrl",
                "ocr.example.test",
                "Ocp-Apim-Subscription-Key",
                "Bearer ",
                "classification-",
            }
        )
            Assert.DoesNotContain(forbidden, raw);
    }

    sealed class LiveFactory : WebApplicationFactory<Program>
    {
        public string Root { get; } =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp",
                "opencode",
                "upload-workflow-" + Guid.NewGuid().ToString("N")
            );
        public SimulatedTransport Transport { get; } = new();
        public IConfiguration Configuration =>
            new ConfigurationBuilder().AddInMemoryCollection(Values).Build();
        Dictionary<string, string?> Values =>
            new()
            {
                ["Paperwork:DataDirectory"] = Root,
                ["Paperwork:Mode"] = "Live",
                ["Paperwork:AzureEndpoint"] = "https://ocr.example.test",
                ["Paperwork:AzureKey"] = AzureKey,
                ["Paperwork:JevApiKey"] = JevKey,
                ["Paperwork:JevModel"] = "integration-request-model",
            };

        public LiveFactory() => Directory.CreateDirectory(Root);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Values));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(Transport);
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(Root, true);
        }
    }

    sealed class SimulatedTransport : HttpMessageHandler, IHttpClientFactory
    {
        public byte[][] ExpectedSources { get; set; } = [];
        public int Submissions;
        public int Polls;
        public int Classifications;
        public Exception? Failure;

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("providers", name);
            return new(this, false);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            try
            {
                if (request.RequestUri!.Host == "ocr.example.test")
                {
                    Assert.Equal(
                        AzureKey,
                        request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single()
                    );
                    Assert.Null(request.Headers.Authorization);
                    if (request.Method == HttpMethod.Post)
                    {
                        Assert.Equal(
                            "/documentintelligence/documentModels/prebuilt-read:analyze",
                            request.RequestUri.AbsolutePath
                        );
                        Assert.Contains("api-version=2024-11-30", request.RequestUri.Query);
                        Assert.Contains("stringIndexType=utf16CodeUnit", request.RequestUri.Query);
                        Assert.Equal(
                            "application/octet-stream",
                            request.Content!.Headers.ContentType!.MediaType
                        );
                        int index = Submissions++;
                        Assert.Equal(
                            ExpectedSources[index],
                            await request.Content.ReadAsByteArrayAsync(ct)
                        );
                        var accepted = new HttpResponseMessage(HttpStatusCode.Accepted);
                        accepted.Headers.Add(
                            "Operation-Location",
                            $"https://ocr.example.test/operations/{index}"
                        );
                        return accepted;
                    }
                    Assert.Equal(HttpMethod.Get, request.Method);
                    int operation = int.Parse(request.RequestUri.Segments.Last());
                    Assert.Equal(Polls++, operation);
                    // Return reversed OCR pages to verify canonical page-number ordering.
                    var texts = operation == 0 ? Texts[..3] : Texts[3..];
                    return Json(
                        new
                        {
                            status = "succeeded",
                            privateMetadata = PrivateMarker,
                            analyzeResult = new
                            {
                                pages = texts
                                    .Select(
                                        (text, i) =>
                                            new
                                            {
                                                pageNumber = i + 1,
                                                lines = new[] { new { content = text } },
                                            }
                                    )
                                    .Reverse(),
                            },
                        }
                    );
                }
                Assert.Equal(
                    "https://api.typesafe.ai/v1/systemone",
                    request.RequestUri.AbsoluteUri
                );
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                Assert.Equal(JevKey, request.Headers.Authorization.Parameter);
                Assert.False(request.Headers.Contains("Ocp-Apim-Subscription-Key"));
                Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
                using var payload = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
                var root = payload.RootElement;
                Assert.Equal("integration-request-model", root.GetProperty("model").GetString());
                int page = Classifications++;
                Assert.Equal(
                    Texts[page],
                    root.GetProperty("state").GetProperty("currentPage").GetString()
                );
                Assert.Equal(
                    page is 0 or 3 ? "" : Texts[page - 1],
                    root.GetProperty("state").GetProperty("previousPage").GetString()
                );
                var questions = root.GetProperty("questions");
                Assert.Equal(
                    "choice",
                    questions.GetProperty("documentType").GetProperty("type").GetString()
                );
                Assert.Equal(
                    Contract.Types.Order(),
                    questions
                        .GetProperty("documentType")
                        .GetProperty("criteria")
                        .EnumerateObject()
                        .Select(p => p.Name)
                        .Order()
                );
                Assert.Contains(
                    "untrusted",
                    questions.GetProperty("documentType").GetProperty("instructions").GetString()
                );
                Assert.Equal(
                    "noul",
                    questions.GetProperty("startsDocument").GetProperty("type").GetString()
                );
                Assert.Contains(
                    "Same type",
                    questions.GetProperty("startsDocument").GetProperty("instructions").GetString()
                );
                string choice = page == 3 ? "unknown" : "bol";
                return Json(
                    new
                    {
                        model = "simulated-jev-model",
                        privateMetadata = PrivateMarker,
                        answers = new
                        {
                            documentType = new
                            {
                                choice,
                                confidence = page == 3 ? .6 : .96,
                                probabilities = Contract.Types.ToDictionary(
                                    t => t,
                                    t => t == choice ? .96 : .008
                                ),
                            },
                            startsDocument = new { noul = page == 1 ? .04 : .96 },
                        },
                    }
                );
            }
            catch (Exception ex)
            {
                Failure = ex;
                throw;
            }
        }

        static HttpResponseMessage Json(object value) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}
