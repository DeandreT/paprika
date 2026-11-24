using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
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

    private static Settings Settings(
        string key = "test-placeholder",
        string model = "gpt-test-read"
    ) =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["OPENAI_API_KEY"] = key,
                        ["paprika:OpenAiBaseUrl"] = "https://openai.example.test/v1",
                        ["paprika:OpenAiReadModel"] = model,
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
                        model = "gpt-test-resolved",
                        usage = new
                        {
                            input_tokens = 120,
                            output_tokens = 30,
                            private_text = Text,
                        },
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
        db.Store.Save(p);
        return p;
    }

    private static SqliteConnection Open(Store store)
    {
        var c = new SqliteConnection(store.ConnectionString);
        c.Open();
        return c;
    }

    private static ExtractionAiAnswer[] Answers(Store store, Packet packet, ExtractionDefinition d)
    {
        using var c = Open(store);
        using var tx = c.BeginTransaction(deferred: true);
        return ExtractionAi.Load(c, tx, packet, d).Values.ToArray();
    }

    private static int UsageArtifacts(Store store)
    {
        using var c = Open(store);
        using var q = c.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM artifacts WHERE name LIKE 'extraction-openai-%'";
        return Convert.ToInt32(q.ExecuteScalar());
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
        var newModel = new ExtractionAi(
            Settings(model: "gpt-test-replacement"),
            db.Store,
            new Fake(_ => throw new InvalidOperationException("Cache hit called the provider."))
        );
        await newModel.PrepareConfiguredAsync(p, default);
        var cached = Assert.Single(Answers(db.Store, p, Definition));
        Assert.Equal(p.Id, cached.PacketId);
        Assert.Equal(ExtractionAi.ContractVersion, cached.ContractVersion);
        Assert.Equal("gpt-test-read", cached.RequestedModel);
        Assert.Equal("gpt-test-resolved", cached.Model);
        Assert.Equal(new ExtractionAiValue("", null), cached.Fields["carrier"]);
        var (url, body) = Assert.Single(requests);
        Assert.Equal("https://openai.example.test/v1/responses", url);
        Assert.False(body.GetProperty("store").GetBoolean());
        Assert.True(body.GetProperty("max_output_tokens").GetInt32() > 0);
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

        using var c = Open(db.Store);
        using var q = c.CreateCommand();
        q.CommandText =
            "SELECT json FROM artifacts WHERE packet_id=$id AND name LIKE 'extraction-openai-%'";
        q.Parameters.AddWithValue("$id", p.Id);
        var artifact = JsonDocument.Parse((string)q.ExecuteScalar()!).RootElement;
        Assert.Equal("openai", artifact.GetProperty("provider").GetString());
        Assert.Equal("responses", artifact.GetProperty("api").GetString());
        Assert.Equal("gpt-test-read", artifact.GetProperty("requestedModel").GetString());
        Assert.Equal("gpt-test-resolved", artifact.GetProperty("model").GetString());
        Assert.Equal(120, artifact.GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.False(artifact.GetProperty("usage").TryGetProperty("private_text", out _));
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
        db.Store.Save(p);
        await Assert.ThrowsAsync<WorkflowException>(() =>
            new ExtractionAi(Settings(), db.Store, network).PrepareConfiguredAsync(p, default)
        );
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, typeof(HttpRequestException))]
    [InlineData(HttpStatusCode.RequestTimeout, typeof(HttpRequestException))]
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
        Assert.Empty(Answers(db.Store, p, Definition));
        Assert.Equal(0, UsageArtifacts(db.Store));
    }

    [Fact]
    public async Task OrderedPagesAndTextDetermineCacheMisses()
    {
        using var db = new ExtractionTestDatabase();
        var p = Live(db);
        var document = Assert.Single(p.Documents);
        var second = new Page { Text = "Synthetic continuation" };
        p.Pages.Add(second);
        document.PageIds.Add(second.Id);
        db.Store.Save(p);
        var requests = new List<string[]>();
        var ai = new ExtractionAi(
            Settings(),
            db.Store,
            new Fake(r =>
            {
                using var body = JsonDocument.Parse(r.Content!.ReadAsStringAsync().Result);
                using var input = JsonDocument.Parse(
                    body.RootElement.GetProperty("input")[1].GetProperty("content").GetString()!
                );
                requests.Add(
                    input
                        .RootElement.GetProperty("pages")
                        .EnumerateArray()
                        .Select(x => x.GetProperty("text").GetString()!)
                        .ToArray()
                );
                return Response(Answer);
            })
        );
        await ai.PrepareConfiguredAsync(p, default);
        Assert.Single(Answers(db.Store, p, Definition));

        p.Revision++;
        document.PageIds.Reverse();
        db.Store.Save(p);
        Assert.Empty(Answers(db.Store, p, Definition));
        await ai.PrepareConfiguredAsync(p, default);
        Assert.Single(Answers(db.Store, p, Definition));

        second.Text = "Updated synthetic continuation";
        Assert.Empty(Answers(db.Store, p, Definition));
        db.Store.Save(p);
        await ai.PrepareConfiguredAsync(p, default);
        Assert.Equal(3, requests.Count);
        Assert.Equal([Text, "Synthetic continuation"], requests[0]);
        Assert.Equal(["Synthetic continuation", Text], requests[1]);
        Assert.Equal(["Updated synthetic continuation", Text], requests[2]);

        p.Revision++;
        db.Store.Save(p);
        Assert.Single(Answers(db.Store, p, Definition));
        await ai.PrepareConfiguredAsync(p, default);
        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public async Task PreparationCanUseInMemoryAssemblyOfPersistedProcessingPacket()
    {
        using var db = new ExtractionTestDatabase();
        var p = Live(db);
        var documents = p.Documents;
        p.Status = "processing";
        p.Documents = [];
        db.Store.Save(p);
        p.Documents = documents;

        var ai = new ExtractionAi(Settings(), db.Store, new Fake(_ => Response(Answer)));
        await ai.PrepareConfiguredAsync(p, default);
        Assert.Single(Answers(db.Store, p, Definition));
        Assert.Empty(db.Store.Get(p.Id)!.Documents);
    }

    [Theory]
    [InlineData("archived")]
    [InlineData("deleted")]
    [InlineData("revision")]
    [InlineData("cancelled")]
    public async Task InFlightAnswerAndUsageAreDiscardedWhenPersistedPacketChanges(string change)
    {
        using var db = new ExtractionTestDatabase();
        var p = Live(db);
        int calls = 0;
        var ai = new ExtractionAi(
            Settings(),
            db.Store,
            new Fake(_ =>
            {
                calls++;
                using var c = Open(db.Store);
                using var tx = c.BeginTransaction(deferred: false);
                using var q = c.CreateCommand();
                q.Transaction = tx;
                if (change == "archived")
                {
                    q.CommandText =
                        "CREATE TABLE operations_tombstones(packet_id TEXT PRIMARY KEY,json TEXT NOT NULL)";
                    q.ExecuteNonQuery();
                }
                q.Parameters.AddWithValue("$id", p.Id);
                q.CommandText = change switch
                {
                    "archived" => "INSERT INTO operations_tombstones VALUES($id,'{}')",
                    "deleted" => "DELETE FROM packets WHERE id=$id",
                    "revision" =>
                        "UPDATE packets SET json=json_set(json,'$.revision',1+json_extract(json,'$.revision')) WHERE id=$id",
                    _ =>
                        "UPDATE packets SET json=json_set(json,'$.status','cancelled') WHERE id=$id",
                };
                q.ExecuteNonQuery();
                tx.Commit();
                return Response(Answer);
            })
        );
        Assert.Equal(
            409,
            (
                await Assert.ThrowsAsync<WorkflowException>(() =>
                    ai.PrepareConfiguredAsync(p, default)
                )
            ).Code
        );
        Assert.Empty(Answers(db.Store, p, Definition));
        Assert.Equal(0, UsageArtifacts(db.Store));
        await Assert.ThrowsAsync<WorkflowException>(() => ai.PrepareConfiguredAsync(p, default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task EvictionAndPurgeRemoveLegacyAndOnlyTheTargetPacketsOwnedAnswers()
    {
        using var db = new ExtractionTestDatabase();
        var p = Live(db);
        var other = Live(db);
        other.Pages = p.Pages;
        other.Documents = p.Documents;
        db.Store.Save(other);

        void AddLegacy() =>
            ExtractionStorage.Add(
                db.Store,
                "ai-answer",
                "unattributable",
                new { key = "unattributable", fields = new { raw = Text } }
            );

        int CountAnswers()
        {
            using var c = Open(db.Store);
            using var q = c.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM extraction_objects WHERE kind='ai-answer'";
            return Convert.ToInt32(q.ExecuteScalar());
        }

        AddLegacy();
        Assert.Equal(1, CountAnswers());
        ExtractionAi.Initialize(db.Store);
        Assert.Equal(0, CountAnswers());
        int requests = 0;
        var ai = new ExtractionAi(
            Settings(),
            db.Store,
            new Fake(_ =>
            {
                requests++;
                return Response(Answer);
            })
        );
        await ai.PrepareConfiguredAsync(p, default);
        await ai.PrepareConfiguredAsync(other, default);
        Assert.Equal(2, requests);
        Assert.NotEqual(
            Assert.Single(Answers(db.Store, p, Definition)).Key,
            Assert.Single(Answers(db.Store, other, Definition)).Key
        );
        AddLegacy();

        using (var c = Open(db.Store))
        using (var tx = c.BeginTransaction(deferred: false))
        {
            new ExtractionRetentionParticipant(db.Store).Purge(c, tx, p);
            tx.Commit();
        }
        Assert.Empty(Answers(db.Store, p, Definition));
        Assert.Single(Answers(db.Store, other, Definition));
        Assert.Equal(1, CountAnswers());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderBodyIsBoundedEvenWithoutATrustworthyContentLength(bool shortLength)
    {
        using var db = new ExtractionTestDatabase();
        var p = Live(db);
        var ai = new ExtractionAi(
            Settings(),
            db.Store,
            new Fake(_ =>
            {
                var content = new StreamContent(
                    new NonSeekingStream(new byte[ExtractionAi.MaxResponseBytes + 1])
                );
                if (shortLength)
                    content.Headers.ContentLength = 1;
                return new(HttpStatusCode.OK) { Content = content };
            })
        );
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ai.PrepareConfiguredAsync(p, default)
        );
        Assert.Contains("size limit", error.Message);
        Assert.Empty(Answers(db.Store, p, Definition));
        Assert.Equal(0, UsageArtifacts(db.Store));
    }

    [Fact]
    public async Task OversizedDocumentNeverReachesTheProvider()
    {
        using var db = new ExtractionTestDatabase();
        var p = Live(db);
        p.Pages[0].Text = new string('A', ExtractionAi.MaxDocumentText + 1);
        db.Store.Save(p);
        var ai = new ExtractionAi(
            Settings(),
            db.Store,
            new Fake(_ =>
                throw new InvalidOperationException("Provider called for oversized input.")
            )
        );
        var error = await Assert.ThrowsAsync<WorkflowException>(() =>
            ai.PrepareConfiguredAsync(p, default)
        );
        Assert.Equal(400, error.Code);
        Assert.Empty(Answers(db.Store, p, Definition));
        Assert.Equal(0, UsageArtifacts(db.Store));
    }

    [Theory]
    [InlineData("""{"status":"incomplete","output":[]}""")]
    [InlineData(
        """{"status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"private text"}]}]}"""
    )]
    [InlineData(
        """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"private text"}]}]}"""
    )]
    public async Task IncompleteRefusedOrMalformedResponsesAreSanitizedAndNotCached(string body)
    {
        using var db = new ExtractionTestDatabase();
        var p = Live(db);
        var ai = new ExtractionAi(
            Settings(),
            db.Store,
            new Fake(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) })
        );
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ai.PrepareConfiguredAsync(p, default)
        );
        Assert.DoesNotContain("private text", error.Message);
        Assert.Empty(Answers(db.Store, p, Definition));
        Assert.Equal(0, UsageArtifacts(db.Store));
    }

    [Fact]
    public async Task RepetitiveQuoteBecomesAReviewableFieldInsteadOfPoisoningReruns()
    {
        using var db = new ExtractionTestDatabase();
        var p = Live(db);
        p.Pages[0].Text = new string('A', ExtractionAi.MaxEvidencePerField + 1);
        db.Store.Save(p);
        var ai = new ExtractionAi(
            Settings(),
            db.Store,
            new Fake(_ =>
                Response(
                    new
                    {
                        pickup = new { value = "A", quote = "A" },
                        carrier = new { value = (string?)null, quote = (string?)null },
                        driver = new { value = " ", quote = " " },
                    }
                )
            )
        );
        await ai.PrepareConfiguredAsync(p, default);
        ExtractionPlatform.RunConfigured(db.Store, p);
        var fields = Assert
            .Single(ExtractionPlatform.Current(db.Store, p)!.Results)
            .Fields.ToDictionary(f => f.Name);
        Assert.Equal("ambiguous", fields["pickup"].Status);
        Assert.Null(fields["pickup"].Value);
        Assert.Empty(fields["pickup"].Evidence);
        Assert.Equal("invalid", fields["driver"].Status);
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

    private sealed class NonSeekingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
