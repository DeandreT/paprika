using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace paprika.Tests;

public sealed class ClassifierRoutingTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "paprika-routing-" + Guid.NewGuid().ToString("N")
    );
    private readonly IConfiguration config;

    public ClassifierRoutingTests()
    {
        config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["paprika:DataDirectory"] = root,
                    ["paprika:JevApiKey"] = "test-jev",
                    ["paprika:OpenAiApiKey"] = "test-openai",
                    ["paprika:JevModel"] = "custom-jev",
                    ["paprika:OpenAiModel"] = "custom-openai",
                    ["paprika:OpenAiBaseUrl"] = "https://openai.example.test/v1",
                }
            )
            .Build();
    }

    private static Packet Live(string profile, string model) =>
        new()
        {
            Mode = "live",
            Workflow = WorkflowRegistry.Legacy("live", model) with { ProviderProfile = profile },
        };

    [Theory]
    [InlineData("jev", "custom-jev", "jev")]
    [InlineData("openai", "custom-openai", "openai")]
    [InlineData("default", "custom-openai", "openai")]
    [InlineData("default", "custom-jev", "jev")]
    [InlineData("default", "gpt-historical", "openai")]
    [InlineData("default", "jev-historical", "jev")]
    [InlineData("openai", "pinned-before-model-change", "openai")]
    public async Task BothKeysUseOnlyThePinnedProviderAndModel(
        string profile,
        string model,
        string expected
    )
    {
        var packet = Live(profile, model);
        using var handler = new Capture();
        var providers = new Providers(new Settings(config), new Store(config), handler);
        var page = new Page { Text = "Invoice" };
        await providers.Classify(packet, page, null, default);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(
            expected == "jev"
                ? "https://api.typesafe.ai/v1/systemone"
                : "https://openai.example.test/v1/decisions",
            request.Url
        );
        Assert.Equal("test-" + expected, request.Token);
        Assert.Equal(model, request.Model);
        Assert.Equal("invoice", page.PredictedType);
        Assert.Equal(profile, packet.Workflow!.ProviderProfile);
    }

    [Theory]
    [InlineData("openai", "custom-openai", "paprika:OpenAiApiKey")]
    [InlineData("jev", "custom-jev", "paprika:JevApiKey")]
    [InlineData("default", "gpt-historical", "paprika:OpenAiApiKey")]
    public async Task RemovingSelectedKeyNeverSendsToOtherProvider(
        string profile,
        string model,
        string key
    )
    {
        var packet = Live(profile, model);
        var store = new Store(config);
        using var handler = new Capture();
        var providers = new Providers(new Settings(config), store, handler);
        config[key] = "";
        var error = await Assert.ThrowsAsync<WorkflowException>(() =>
            providers.Classify(packet, new Page(), null, default)
        );
        Assert.Equal(400, error.Code);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("unrecognized", "custom-jev", "custom-openai")]
    [InlineData("SHARED", "SHARED", "SHARED")]
    [InlineData("gpt-inconsistent", "gpt-inconsistent", "custom-openai")]
    [InlineData("jev-inconsistent", "custom-jev", "jev-inconsistent")]
    public async Task AmbiguousOrUnknownLegacySnapshotDoesNotDispatch(
        string model,
        string jev,
        string openAi
    )
    {
        config["paprika:JevModel"] = jev;
        config["paprika:OpenAiModel"] = openAi;
        var store = new Store(config);
        using var handler = new Capture();
        var providers = new Providers(new Settings(config), store, handler);
        var packet = Live("default", model);
        await Assert.ThrowsAsync<WorkflowException>(() =>
            providers.Classify(packet, new Page(), null, default)
        );
        Assert.Empty(handler.Requests);
        Assert.Equal(400, store.Accept(packet, null).Code);
        Assert.Empty(store.All());
    }

    [Theory]
    [InlineData("openai", "custom-jev")]
    [InlineData("openai", "gpt-no-longer-approved")]
    [InlineData("jev", "gpt-6-luna")]
    public void IntakeAndReprocessRejectWrongModelBeforeQueuing(string provider, string model)
    {
        var store = new Store(config);
        var packet = Live(provider, model);
        Assert.Equal(400, store.Accept(packet, null).Code);
        Assert.Null(store.Get(packet.Id));
        packet.Status = "approved";
        store.Save(packet);
        Assert.Equal(400, store.Reprocess(packet.Id, packet.Revision, new Settings(config)).Code);
        Assert.Null(store.NextPending());
    }

    [Fact]
    public void RetainedLegacySnapshotOnlyGetsPinnedOnNewRunAndRequiresItsOwnCredentials()
    {
        var store = new Store(config);
        var settings = new Settings(config);
        var packet = Live("default", "custom-openai");
        packet.Status = "approved";
        store.Save(packet);
        var original = store.Get(packet.Id)!.Workflow!;

        config["paprika:OpenAiApiKey"] = "";
        config["paprika:AzureEndpoint"] = "https://ocr.example.test";
        config["paprika:AzureKey"] = "test-azure";
        Assert.True(settings.OcrConfigured);
        Assert.Equal(400, store.Reprocess(packet.Id, packet.Revision, settings).Code);
        Assert.Equal(400, store.Accept(Live("openai", "custom-openai"), null).Code);
        Assert.Null(store.NextPending());

        config["paprika:OpenAiApiKey"] = "restored-test-key";
        var child = store.Reprocess(packet.Id, packet.Revision, settings).Packet!;
        Assert.Equal("openai", child.Workflow!.ProviderProfile);
        Assert.Equal(original.Model, child.Workflow.Model);
        Assert.NotEqual(original.Hash, child.Workflow.Hash);
        var parent = store.Get(packet.Id)!;
        Assert.Equal("default", parent.Workflow!.ProviderProfile);
        Assert.Equal(original.Hash, parent.Workflow.Hash);
    }

    [Fact]
    public void JevClassifierRequiresSeparateOcrCredentials()
    {
        config["paprika:OpenAiApiKey"] = "";
        var store = new Store(config);
        var packet = Live("jev", "custom-jev");
        var rejected = store.Accept(packet, null);
        Assert.Equal(400, rejected.Code);
        Assert.Contains("OCR", rejected.Error);
        Assert.Null(store.NextPending());
        config["paprika:AzureEndpoint"] = "https://ocr.example.test";
        config["paprika:AzureKey"] = "test-azure";
        Assert.Equal(200, store.Accept(packet, null).Code);
    }

    [Theory]
    [InlineData(null, "test-jev", "jev", "custom-jev")]
    [InlineData(null, "", "openai", "custom-openai")]
    [InlineData("custom-openai", "test-jev", "openai", "custom-openai")]
    public void NoSnapshotUsesRecordedModelBeforeLegacyPreferenceAndPinsTheChild(
        string? recorded,
        string jevKey,
        string expectedProvider,
        string expectedModel
    )
    {
        config["paprika:JevApiKey"] = jevKey;
        var store = new Store(config);
        var parent = new Packet
        {
            Mode = "live",
            Status = "review",
            Processing = new("complete", recorded),
        };
        store.Save(parent);
        var child = store.Reprocess(parent.Id, parent.Revision, new Settings(config)).Packet!;
        Assert.Equal(expectedProvider, child.Workflow!.ProviderProfile);
        Assert.Equal(expectedModel, child.Workflow.Model);
        Assert.Null(store.Get(parent.Id)!.Workflow);
    }

    [Fact]
    public async Task FirstAttemptCheckpointsHistoricalBindingEvenIfProviderIsUnavailable()
    {
        var packet = new Packet { Mode = "live", Status = "processing" };
        var store = new Store(config);
        store.Save(packet);
        using var handler = new Capture(HttpStatusCode.ServiceUnavailable);
        var providers = new Providers(new Settings(config), store, handler);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            providers.Classify(packet, new Page(), null, default)
        );
        var resumed = store.Get(packet.Id)!;
        Assert.Equal("jev", resumed.Workflow!.ProviderProfile);
        Assert.Equal("custom-jev", resumed.Workflow.Model);
        config["paprika:JevApiKey"] = "";
        await Assert.ThrowsAsync<WorkflowException>(() =>
            providers.Classify(resumed, new Page(), null, default)
        );
        Assert.Single(handler.Requests);
    }

    private sealed class Capture(HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler,
            IHttpClientFactory
    {
        public List<(string Url, string? Token, string Model)> Requests { get; } = [];

        public HttpClient CreateClient(string name) => new(this, false);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var url = request.RequestUri!;
            Requests.Add(
                (
                    url.AbsoluteUri,
                    request.Headers.Authorization?.Parameter,
                    json.RootElement.GetProperty("model").GetString()!
                )
            );
            string response = url.AbsolutePath.EndsWith("/decisions", StringComparison.Ordinal)
                ? """{"answers":[{"type":"choice","name":"documentType","choice":"invoice","confidence":0.9},{"name":"startsDocument","probability":1}]}"""
                : """{"answers":{"documentType":{"choice":"invoice","confidence":0.9},"startsDocument":{"noul":1}}}""";
            return new(status) { Content = new StringContent(response) };
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
