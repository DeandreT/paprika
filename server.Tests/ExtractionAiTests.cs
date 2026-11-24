using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace paprika.Tests;

public sealed class ExtractionAiTests
{
    private const string Text = "Pickup at 12 Main St, Springfield\nCarrier: ACME Freight";

    private static readonly ExtractionDefinition Definition = new(
        "load-ai",
        1,
        [
            new("pickup", Description: "Pickup address"),
            new("carrier", Description: "Carrier name"),
            new("driver", Description: "Driver name", Required: true),
            new("reference", Label: "Carrier"),
        ],
        [],
        "invoice"
    );

    private static Settings Settings(string key = "test-placeholder") =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["OPENAI_API_KEY"] = key,
                        ["paprika:OpenAiBaseUrl"] = "https://openai.example.test/v1",
                        ["paprika:OpenAiReadModel"] = "gpt-test-read",
                    }
                )
                .Build()
        );

    private static object Answer =>
        new
        {
            pickup = new
            {
                value = "12 Main St, Springfield",
                quote = "Pickup at 12 Main St, Springfield",
            },
            carrier = new { value = "Globex", quote = "Carrier: Globex" },
            driver = new { value = (string?)null, quote = (string?)null },
        };

    private static HttpResponseMessage Response(object answer) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    new
                    {
                        status = "completed",
                        usage = new { input_tokens = 120, output_tokens = 30 },
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
                                        text = JsonSerializer.Serialize(answer),
                                    },
                                },
                            },
                        },
                    }
                )
            ),
        };

    private static Packet Live(ExtractionTestDatabase db)
    {
        var p = db.Packet(Text);
        p.Mode = "live";
        p.Workflow = p.EffectiveWorkflow with
        {
            Capabilities = new(
                Extraction: new(
                    Definition.Id,
                    Definition.Version,
                    ExtractionStorage.Hash(Definition),
                    Definition
                )
            ),
        };
        return p;
    }

    [Fact]
    public async Task GroundedValuesAreExtractedAndUngroundedValuesHaveNoEvidence()
    {
        using var db = new ExtractionTestDatabase();
        var p = Live(db);
        var requests = new List<(string Url, JsonElement Body)>();
        var ai = new ExtractionAi(
            Settings(),
            db.Store,
            new Fake(r =>
            {
                requests.Add(
                    (
                        r.RequestUri!.ToString(),
                        JsonDocument
                            .Parse(r.Content!.ReadAsStringAsync().Result)
                            .RootElement.Clone()
                    )
                );
                return Response(Answer);
            })
        );
        await ai.PrepareConfiguredAsync(p, default);
        await ai.PrepareConfiguredAsync(p, default); // cached by page text
        var (url, body) = Assert.Single(requests);
        Assert.Equal("https://openai.example.test/v1/responses", url);
        Assert.False(body.GetProperty("store").GetBoolean());
        Assert.Equal("gpt-test-read", body.GetProperty("model").GetString());
        var format = body.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.Equal(
            ["pickup", "carrier", "driver"],
            format
                .GetProperty("schema")
                .GetProperty("properties")
                .EnumerateObject()
                .Select(x => x.Name)
        );
        Assert.Equal(
            Text,
            JsonDocument
                .Parse(body.GetProperty("input")[1].GetProperty("content").GetString()!)
                .RootElement.GetProperty("pages")[0]
                .GetProperty("text")
                .GetString()
        );

        ExtractionPlatform.RunConfigured(db.Store, p);
        var run = ExtractionPlatform.Current(db.Store, p, ExtractionStorage.Hash(Definition))!;
        var fields = Assert.Single(run.Results).Fields.ToDictionary(f => f.Name);
        var pickup = fields["pickup"];
        Assert.Equal("extracted", pickup.Status);
        Assert.Equal("12 Main St, Springfield", pickup.Value);
        var evidence = Assert.Single(pickup.Evidence);
        Assert.Equal(p.Pages[0].Id, evidence.PageId);
        Assert.Equal(Text.IndexOf("12 Main", StringComparison.Ordinal), evidence.Offset);
        Assert.Equal("12 Main St, Springfield".Length, evidence.Length);
        Assert.Equal("12 Main St, Springfield", evidence.Text);
        Assert.Equal(ExtractionStorage.TextHash(Text), evidence.TextSha256);
        Assert.True(ExtractionPlatform.ValidEvidence(p, evidence));
        Assert.Equal(("invalid", null), (fields["carrier"].Status, fields["carrier"].Value));
        Assert.Empty(fields["carrier"].Evidence);
        Assert.Equal("missing", fields["driver"].Status);
        Assert.Empty(fields["driver"].Evidence);
        Assert.Equal("ACME Freight", fields["reference"].Value);
        Assert.Contains(ExtractionPlatform.Blockers(db.Store, p), b =>
            b.Contains("carrier", StringComparison.Ordinal)
        );

        using var c = new Microsoft.Data.Sqlite.SqliteConnection(db.Store.ConnectionString);
        c.Open();
        using var q = c.CreateCommand();
        q.CommandText =
            "SELECT json FROM artifacts WHERE packet_id=$id AND name LIKE 'extraction-openai-%'";
        q.Parameters.AddWithValue("$id", p.Id);
        var artifact = JsonDocument.Parse((string)q.ExecuteScalar()!).RootElement;
        Assert.Equal("openai", artifact.GetProperty("provider").GetString());
        Assert.Equal("responses", artifact.GetProperty("api").GetString());
        Assert.Equal("gpt-test-read", artifact.GetProperty("model").GetString());
        Assert.Equal(120, artifact.GetProperty("usage").GetProperty("input_tokens").GetInt32());
    }

    [Fact]
    public async Task UnaskedDocumentsStayUncoveredAndOfflineOrKeylessRunsFailWithoutNetwork()
    {
        using var db = new ExtractionTestDatabase();
        var p = Live(db);
        ExtractionPlatform.RunConfigured(db.Store, p);
        Assert.NotEmpty(ExtractionPlatform.Blockers(db.Store, p));
        Assert.Throws<ArgumentException>(() => ExtractionPlatform.Extract(p, Definition));

        var network = new Fake(_ => throw new InvalidOperationException("network called"));
        var keyless = await Assert.ThrowsAsync<WorkflowException>(() =>
            new ExtractionAi(Settings(""), db.Store, network).PrepareConfiguredAsync(p, default)
        );
        Assert.Contains("OpenAI credentials", keyless.Message);
        p.Mode = "simulation";
        await Assert.ThrowsAsync<WorkflowException>(() =>
            new ExtractionAi(Settings(), db.Store, network).PrepareConfiguredAsync(p, default)
        );
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(HttpRequestException))]
    [InlineData(HttpStatusCode.BadGateway, typeof(HttpRequestException))]
    [InlineData(HttpStatusCode.Unauthorized, typeof(InvalidOperationException))]
    public async Task ProviderFailuresMirrorProviderHandling(HttpStatusCode status, Type expected)
    {
        using var db = new ExtractionTestDatabase();
        var p = Live(db);
        var ai = new ExtractionAi(
            Settings(),
            db.Store,
            new Fake(_ => new(status) { Content = new StringContent("secret detail") })
        );
        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            ai.PrepareConfiguredAsync(p, default)
        );
        Assert.IsType(expected, error);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public void DescriptionIsBoundedExclusiveAndHashed()
    {
        Assert.Null(ExtractionPlatform.ValidateDefinition(Definition));
        Assert.NotNull(
            ExtractionPlatform.ValidateDefinition(
                Definition with
                {
                    Fields = [new("x", Description: new string('a', 501))],
                }
            )
        );
        Assert.NotNull(
            ExtractionPlatform.ValidateDefinition(
                Definition with
                {
                    Fields = [new("x", Description: " ")],
                }
            )
        );
        Assert.NotNull(
            ExtractionPlatform.ValidateDefinition(
                Definition with
                {
                    Fields = [new("x", Label: "X", Description: "x")],
                }
            )
        );
        Assert.NotEqual(
            ExtractionStorage.Hash(Definition),
            ExtractionStorage.Hash(
                Definition with
                {
                    Fields =
                    [
                        new("pickup", Description: "Delivery address"),
                        .. Definition.Fields[1..],
                    ],
                }
            )
        );
        // Definitions without AI fields keep the hash they had before descriptions existed.
        Assert.DoesNotContain(
            "description",
            JsonSerializer.Serialize(new ExtractionField("x", Label: "X"), Contract.Json)
        );
    }

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
