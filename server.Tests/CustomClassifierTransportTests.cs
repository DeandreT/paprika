using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace paprika.Tests;

public sealed class CustomClassifierTransportTests : IDisposable
{
    private const string Prefix = "paprika:CustomClassifier:";
    private const string Endpoint = "https://classifier.example.test/v1/classify";
    private const string Model = "dispatch-classifier";
    private const string Key = "test-custom-key";
    private const string Instructions = "Distinguish dispatch sheets from other paperwork.";
    private const int ResponseLimit = 64 * 1024;
    private const string ValidResult =
        """{"schemaVersion":1,"model":"dispatch-classifier","documentType":"dispatch_sheet","confidence":0.9,"startProbability":0.2}""";

    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "paprika-custom-classifier-" + Guid.NewGuid().ToString("N")
    );
    private readonly IConfiguration config;
    private readonly Settings settings;
    private readonly Store store;

    public CustomClassifierTransportTests()
    {
        config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["paprika:DataDirectory"] = root,
                    ["paprika:JevApiKey"] = "test-jev-key",
                    ["paprika:OpenAiApiKey"] = "test-openai-key",
                    [Prefix + "Endpoint"] = Endpoint,
                    [Prefix + "Model"] = Model,
                    [Prefix + "ApiKey"] = Key,
                }
            )
            .Build();
        settings = new Settings(config);
        store = new Store(config);
    }

    private Packet Live() =>
        new()
        {
            Mode = "live",
            Workflow = WorkflowRegistry.Legacy("live", Model) with
            {
                ProviderProfile = "custom",
                ClassifierBindingHash = settings.CustomClassifierBindingHash,
                LegacyRules = false,
                Instructions = Instructions,
                Types =
                [
                    new(
                        "dispatch_sheet",
                        1,
                        "Dispatch sheet",
                        "Route paperwork",
                        ["route"],
                        "Check stop numbers.",
                        new()
                    ),
                    new("unknown", 1, "Unknown", "Insufficient evidence", [], "", new()),
                ],
            },
        };

    [Fact]
    public async Task PostsThePinnedModelAndWorkflowTypesWithBearerAuth()
    {
        var packet = Live();
        var page = new Page { Text = "Dispatch sheet" };
        using var handler = new Capture();
        await new Providers(settings, store, handler).Classify(packet, page, null, default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(Endpoint, request.Url);
        Assert.Equal("application/json", request.MediaType);
        Assert.Equal("Bearer", request.Scheme);
        Assert.Equal(Key, request.Token);
        Assert.Equal(1, request.Body.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Model, request.Body.GetProperty("model").GetString());
        Assert.Equal(Instructions, request.Body.GetProperty("instructions").GetString());
        var types = request
            .Body.GetProperty("types")
            .EnumerateArray()
            .ToDictionary(
                t => t.GetProperty("id").GetString()!,
                t => t.GetProperty("description").GetString()
            );
        Assert.Equal(2, types.Count);
        Assert.Equal(
            "Dispatch sheet\nRoute paperwork\nCheck stop numbers.\nKeywords: route",
            types["dispatch_sheet"]
        );
        Assert.Contains("Insufficient evidence", types["unknown"]);
        Assert.Equal("dispatch_sheet", page.PredictedType);
        Assert.Equal(0.9, page.Confidence);
        Assert.Equal(Model, packet.Processing!.Model);
    }

    [Fact]
    public async Task SendsPreviousTextOnlyWithinTheSameSourceAndForcesFirstPageBoundaries()
    {
        var packet = Live();
        var first = new Page { SourceId = "one", Text = "First dispatch page" };
        var continuation = new Page { SourceId = "one", Text = "Next dispatch page" };
        var other = new Page { SourceId = "two", Text = "Different source" };
        using var handler = new Capture();
        var providers = new Providers(settings, store, handler);
        await providers.Classify(packet, first, null, default);
        await providers.Classify(packet, continuation, first, default);
        await providers.Classify(packet, other, continuation, default);

        Assert.Collection(
            handler.Requests,
            r => AssertState(r, first.Text, ""),
            r => AssertState(r, continuation.Text, first.Text),
            r => AssertState(r, other.Text, "")
        );
        Assert.DoesNotContain(continuation.Text, handler.Requests[2].Body.GetRawText());
        Assert.Equal(1d, first.StartProbability);
        Assert.Equal(0.2, continuation.StartProbability);
        Assert.Equal(1d, other.StartProbability);
    }

    [Theory]
    [InlineData(ResponseLimit, false, ResponseLimit)]
    [InlineData(ResponseLimit * 2, false, ResponseLimit + 1)]
    [InlineData(ResponseLimit * 2, true, 0)]
    public async Task BoundsTheResponseWithOrWithoutContentLength(
        int size,
        bool declaresLength,
        int bytesRead
    )
    {
        var packet = Live();
        var page = new Page();
        using var stream = new NonSeekableResponse(
            Encoding.UTF8.GetBytes(ValidResult.PadRight(size))
        );
        using var handler = new Capture(() =>
        {
            var content = new StreamContent(stream);
            if (declaresLength)
                content.Headers.ContentLength = size;
            return content;
        });
        var providers = new Providers(settings, store, handler);
        if (size <= ResponseLimit)
        {
            await providers.Classify(packet, page, null, default);
            Assert.Equal("dispatch_sheet", page.PredictedType);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                providers.Classify(packet, page, null, default)
            );
            Assert.Contains("64 KiB", error.Message);
            Assert.Empty(Artifacts(packet));
        }
        Assert.Single(handler.Requests);
        Assert.Equal(bytesRead, stream.BytesConsumed);
    }

    [Theory]
    [InlineData(null, "{")]
    [InlineData("schemaVersion", "2")]
    [InlineData("model", "\"another-model\"")]
    [InlineData("documentType", "\"invoice\"")]
    [InlineData("confidence", "1.01")]
    [InlineData("confidence", "1e400")]
    [InlineData("startProbability", "\"0.2\"")]
    [InlineData("startProbability", null)]
    public async Task RejectsInvalidResultsBeforeUpdatingThePageOrWritingAnArtifact(
        string? property,
        string? value
    )
    {
        string body;
        if (property is null)
            body = value!;
        else
        {
            var result = JsonNode.Parse(ValidResult)!.AsObject();
            if (value is null)
                result.Remove(property);
            else
                result[property] = JsonNode.Parse(value);
            body = result.ToJsonString();
        }

        var packet = Live();
        var page = new Page();
        using var handler = new Capture(() => JsonContent(body));
        var providers = new Providers(settings, store, handler);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            providers.Classify(packet, page, null, default)
        );
        Assert.Contains("invalid v1 result", error.Message);
        Assert.Single(handler.Requests);
        Assert.Equal("unknown", page.PredictedType);
        Assert.Null(packet.Processing);
        Assert.Empty(Artifacts(packet));
    }

    [Theory]
    [InlineData("Endpoint", "https://other.example.test/v1/classify")]
    [InlineData("Model", "new-dispatch-classifier")]
    [InlineData("ApiKey", null)]
    [InlineData(null, null)]
    public async Task MissingOrStaleBindingsAndRemovedKeysNeverSend(
        string? setting,
        string? replacement
    )
    {
        var packet = Live();
        using var handler = new Capture();
        var providers = new Providers(settings, store, handler);
        if (setting is null)
            packet.Workflow = packet.Workflow! with { ClassifierBindingHash = null };
        else
            config[Prefix + setting] = replacement;

        var error = await Assert.ThrowsAsync<WorkflowException>(() =>
            providers.Classify(packet, new Page(), null, default)
        );
        Assert.Equal(400, error.Code);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task KeyRotationUsesTheNewKeyWithTheSameRetainedBinding()
    {
        var packet = Live();
        var binding = packet.Workflow!.ClassifierBindingHash;
        Assert.NotNull(binding);
        store.Save(packet);
        using var handler = new Capture();
        var providers = new Providers(settings, store, handler);
        await providers.Classify(packet, new Page(), null, default);

        const string rotatedKey = "test-rotated-custom-key";
        config[Prefix + "ApiKey"] = rotatedKey;
        var resumed = store.Get(packet.Id)!;
        Assert.Equal(binding, resumed.Workflow!.ClassifierBindingHash);
        Assert.Equal(binding, settings.CustomClassifierBindingHash);
        await providers.Classify(resumed, new Page(), null, default);

        Assert.Equal([Key, rotatedKey], handler.Requests.Select(r => r.Token));
        Assert.All(handler.Requests, r => Assert.Equal(Endpoint, r.Url));
        Assert.Equal(Model, resumed.Processing!.Model);
    }

    [Fact]
    public async Task ClassificationArtifactKeepsOnlyValidatedResultFields()
    {
        var packet = Live();
        var page = new Page { Text = "Sensitive page text" };
        var result = JsonNode.Parse(ValidResult)!.AsObject();
        result["echo"] = new JsonObject { ["page"] = page.Text, ["credential"] = Key };
        result["debug"] = "arbitrary provider data";
        using var handler = new Capture(() => JsonContent(result.ToJsonString()));
        await new Providers(settings, store, handler).Classify(packet, page, null, default);

        var raw = Assert.Single(Artifacts(packet));
        Assert.DoesNotContain(page.Text, raw);
        Assert.DoesNotContain(Key, raw);
        Assert.DoesNotContain("arbitrary provider data", raw);
        using var artifact = JsonDocument.Parse(raw);
        var body = artifact.RootElement;
        Assert.Equal("custom", body.GetProperty("provider").GetString());
        Assert.Equal(packet.Workflow!.Hash, body.GetProperty("workflowHash").GetString());
        var saved = body.GetProperty("response");
        Assert.Equal(4, saved.EnumerateObject().Count());
        Assert.Equal(Model, saved.GetProperty("model").GetString());
        Assert.Equal("dispatch_sheet", saved.GetProperty("documentType").GetString());
        Assert.Equal(0.9, saved.GetProperty("confidence").GetDouble());
        Assert.Equal(0.2, saved.GetProperty("startProbability").GetDouble());
    }

    private static void AssertState(Request request, string current, string previous)
    {
        var state = request.Body.GetProperty("state");
        Assert.Equal(current, state.GetProperty("currentPage").GetString());
        Assert.Equal(previous, state.GetProperty("previousPage").GetString());
    }

    private static StringContent JsonContent(string value) =>
        new(value, Encoding.UTF8, "application/json");

    private List<string> Artifacts(Packet packet)
    {
        using var connection = new SqliteConnection(store.ConnectionString);
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT json FROM artifacts WHERE packet_id=$id";
        query.Parameters.AddWithValue("$id", packet.Id);
        using var rows = query.ExecuteReader();
        var artifacts = new List<string>();
        while (rows.Read())
            artifacts.Add(rows.GetString(0));
        return artifacts;
    }

    private sealed record Request(
        HttpMethod Method,
        string Url,
        string? MediaType,
        string? Scheme,
        string? Token,
        JsonElement Body
    );

    private sealed class Capture(Func<HttpContent>? response = null)
        : HttpMessageHandler,
            IHttpClientFactory
    {
        public List<Request> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(this, false);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Requests.Add(
                new(
                    request.Method,
                    request.RequestUri!.AbsoluteUri,
                    request.Content.Headers.ContentType?.MediaType,
                    request.Headers.Authorization?.Scheme,
                    request.Headers.Authorization?.Parameter,
                    json.RootElement.Clone()
                )
            );
            return new(HttpStatusCode.OK)
            {
                Content = response?.Invoke() ?? JsonContent(ValidResult),
            };
        }
    }

    private sealed class NonSeekableResponse(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
        public int BytesConsumed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing && CanRead)
                BytesConsumed = (int)Position;
            base.Dispose(disposing);
        }
    }

    public void Dispose()
    {
        using var connection = new SqliteConnection(store.ConnectionString);
        SqliteConnection.ClearPool(connection);
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
