using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SkiaSharp;
using Xunit;

namespace paprika.Tests;

public sealed class OpenAiProviderTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "paprika-openai-" + Guid.NewGuid().ToString("N")
    );

    private IConfiguration Config =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["paprika:DataDirectory"] = root,
                    ["OPENAI_API_KEY"] = "test-placeholder",
                    ["paprika:OpenAiBaseUrl"] = "https://openai.example.test/v1",
                }
            )
            .Build();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }

    [Fact]
    public async Task ScannedPagesAreReadWithVisionOnceAndEmbeddedTextIsFree()
    {
        var settings = new Settings(Config);
        Assert.True(settings.OcrConfigured && settings.ClassifierConfigured);
        Assert.Equal("gpt-6-luna", settings.Model);
        var store = new Store(Config);
        var packet = Worker.Demo(store);
        var image = new Source
        {
            Name = "scan.png",
            Extension = ".png",
            PageCount = 1,
        };
        using (var bitmap = new SKBitmap(200, 200))
        {
            bitmap.Erase(SKColors.White);
            using var data = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(store.SourcePath(packet, image), data.ToArray());
        }
        packet.Sources.Add(image);
        store.Save(packet);
        var requests = new List<(string Url, JsonElement Body)>();
        var providers = new Providers(
            settings,
            store,
            new Fake(request =>
            {
                requests.Add(
                    (
                        request.RequestUri!.ToString(),
                        JsonDocument
                            .Parse(request.Content!.ReadAsStringAsync().Result)
                            .RootElement.Clone()
                    )
                );
                return Json(
                    new
                    {
                        status = "completed",
                        model = "gpt-6-luna",
                        output = new[]
                        {
                            new
                            {
                                type = "message",
                                content = new[]
                                {
                                    new
                                    {
                                        type = "output_text",
                                        text = "PROOF OF DELIVERY\nShipment #: TEST-A",
                                    },
                                },
                            },
                        },
                    }
                );
            })
        );

        var embedded = await providers.Ocr(packet, packet.Sources[0], default);
        Assert.Equal(packet.Sources[0].PageCount, embedded.Count);
        Assert.Empty(requests);

        var scanned = await providers.Ocr(packet, image, default);
        Assert.Equal("PROOF OF DELIVERY\nShipment #: TEST-A", Assert.Single(scanned));
        var (url, body) = Assert.Single(requests);
        Assert.Equal("https://openai.example.test/v1/responses", url);
        Assert.False(body.GetProperty("store").GetBoolean());
        Assert.StartsWith(
            "data:image/jpeg;base64,",
            body.GetProperty("input")[0]
                .GetProperty("content")[1]
                .GetProperty("image_url")
                .GetString()
        );
        Assert.Equal(scanned, await providers.Ocr(packet, image, default));
        Assert.Single(requests);
    }

    [Fact]
    public async Task DecisionsApiClassifiesTypeAndBoundary()
    {
        var store = new Store(Config);
        var packet = Worker.Demo(store);
        packet.Mode = "live";
        packet.Workflow = WorkflowRegistry.Legacy("live", "gpt-6-luna") with
        {
            ProviderProfile = "openai",
        };
        JsonElement sent = default;
        var providers = new Providers(
            new Settings(Config),
            store,
            new Fake(request =>
            {
                Assert.Equal(
                    "https://openai.example.test/v1/decisions",
                    request.RequestUri!.ToString()
                );
                sent = JsonDocument
                    .Parse(request.Content!.ReadAsStringAsync().Result)
                    .RootElement.Clone();
                return Json(
                    new
                    {
                        model = "gpt-6-luna",
                        answers = new object[]
                        {
                            new
                            {
                                type = "choice",
                                name = "documentType",
                                choice = "pod",
                                confidence = 0.91,
                            },
                            new
                            {
                                type = "predicate",
                                name = "startsDocument",
                                probability = 0.2,
                            },
                        },
                    }
                );
            })
        );
        var page = packet.Pages[1];
        await providers.Classify(packet, page, packet.Pages[0], default);
        Assert.Equal("pod", page.PredictedType);
        Assert.Equal(0.91, page.Confidence);
        Assert.Equal(0.2, page.StartProbability);
        Assert.Equal("gpt-6-luna", packet.Processing!.Model);
        var questions = sent.GetProperty("questions");
        Assert.Equal("choice", questions[0].GetProperty("type").GetString());
        Assert.Contains(questions[0].GetProperty("choices").EnumerateArray(), c =>
            c.GetProperty("value").GetString() == "unknown"
        );
        Assert.Equal("predicate", questions[1].GetProperty("type").GetString());
    }

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };

    private sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> send)
        : HttpMessageHandler,
            IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, false);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(send(request));
    }
}
