using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace paprika.Tests;

public sealed class ReviewedFieldEvidenceTests
{
    // Include a surrogate pair before both amounts: span offsets are UTF-16, not byte offsets.
    private const string Text = "📦 Fixture\nEstimate: 1,200.00\nBilled: 1,300.00\nRef: TEST-001";
    private static readonly ExtractionDefinition Definition = new(
        "fixture-invoice",
        1,
        [
            new("amount", "number", Description: "Billed amount"),
            new("reference", Description: "Invoice reference"),
            new("legacy", Description: "Alternate reference"),
            new("unreviewed", Description: "Other reference"),
        ],
        [],
        "invoice"
    );

    [Fact]
    public async Task DownloadSeparatesReviewedGroundedFieldHistoryFromPageLabels()
    {
        await using var host = new Host();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-paprika", "1");
        var store = host.Services.GetRequiredService<Store>();
        var packet = await ProcessInvoice(host, store);
        var page = Assert.Single(packet.Pages);
        var document = Assert.Single(packet.Documents);
        var run = ExtractionPlatform.Current(store, packet)!;
        var estimate = Span(page, "1,200.00");
        var billed = Span(page, "1,300.00");
        Assert.Equal(estimate, Assert.Single(run.Results.Single().Fields[0].Evidence));
        Assert.Equal(2, host.Provider.Calls);

        async Task Correct(
            string field,
            string? value,
            string? category,
            ExtractionEvidence[] spans
        )
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/extraction/{packet.Id}/corrections",
                new ExtractionCorrection(
                    run.PacketRevision,
                    run.Id,
                    document.Id,
                    field,
                    value,
                    "Checked the synthetic invoice.",
                    spans,
                    category
                )
            );
            response.EnsureSuccessStatusCode();
            run = (await response.Content.ReadFromJsonAsync<ExtractionCorrectionResponse>())!.Run;
        }

        await Correct("amount", " 1300.00 ", "wrong-selection", [billed]);
        using var created = await client.PostAsJsonAsync(
            "/api/training/datasets",
            new TrainingDatasetRequest("Synthetic field review")
        );
        created.EnsureSuccessStatusCode();
        var dataset = (await created.Content.ReadFromJsonAsync<TrainingDataset>())!;
        string api = $"/api/training/datasets/{dataset.Id}";
        using var imported = await client.PostAsJsonAsync(
            api + "/candidates/import",
            new CandidateImportRequest([packet.Id])
        );
        imported.EnsureSuccessStatusCode();
        var pending = (await client.GetFromJsonAsync<ReviewedDatasetExport>(api + "/export"))!;
        Assert.Empty(pending.Examples);
        Assert.Empty(pending.FieldFeedback);
        var candidate = Assert.Single(
            (await client.GetFromJsonAsync<TeacherCandidate[]>(api + "/candidates"))!
        );
        Assert.Equal("invoice", Assert.Single(candidate.Predictions).ProposedLabel);
        using var accepted = await client.PostAsJsonAsync(
            api + "/candidates/review",
            new CandidateReviewRequest([
                new(candidate.Id, candidate.Version, "accepted", "invoice"),
            ])
        );
        accepted.EnsureSuccessStatusCode();

        var first = (await client.GetFromJsonAsync<ReviewedDatasetExport>(api + "/export"))!;
        Assert.Equal("1300.00", Assert.Single(first.FieldFeedback).ExtractionTarget);
        await Correct("amount", "1320.00", "transcription", []);
        await Correct("reference", null, "omit", []);
        await Correct("legacy", "CORRECTED-LEGACY", null, []);
        // A later packet review changes the document type. The historical AI answer is bound to
        // the original input, including the document type; export must use that input revision.
        document.Type = "receipt";
        using var edit = await client.PutAsJsonAsync(
            $"/api/packets/{packet.Id}/review",
            new ReviewRequest(run.PacketRevision, [document], false)
        );
        edit.EnsureSuccessStatusCode();

        var wire = await client.GetFromJsonAsync<JsonElement>(api + "/export");
        var export = wire.Deserialize<ReviewedDatasetExport>(Contract.Json)!;
        var example = Assert.Single(export.Examples).Example;
        Assert.Equal("invoice", example.Label);
        Assert.Equal(Text, example.Text);
        Assert.True(example.Synthetic);
        Assert.Equal(4, export.FieldFeedback.Length);
        Assert.All(export.FieldFeedback, f =>
        {
            Assert.True(f.Synthetic);
            Assert.Equal(packet.Id, f.PacketId);
            Assert.Equal(document.Id, f.DocumentId);
            Assert.Equal(ExtractionStorage.Hash(Definition), f.DefinitionSha256);
            Assert.Equal("openai", f.Suggestion.Provider);
            Assert.Equal("fixture-reader", f.Suggestion.RequestedModel);
            Assert.Equal("fixture-reader-resolved", f.Suggestion.ReportedModel);
            Assert.Equal("local", f.Review.Actor);
        });
        var selection = Assert.Single(export.FieldFeedback, f =>
            f.Review.Category == "wrong-selection"
        );
        var transcription = Assert.Single(export.FieldFeedback, f =>
            f.Review.Category == "transcription"
        );
        foreach (var f in new[] { selection, transcription })
        {
            Assert.Equal("1,200.00", f.Suggestion.Value);
            Assert.Equal("Estimate: 1,200.00", f.Suggestion.Quote);
            Assert.Equal(estimate, Assert.Single(f.Suggestion.Evidence));
            Assert.Equal(billed, Assert.Single(f.Review.After.Evidence));
        }
        Assert.Equal(" 1300.00 ", selection.Review.SubmittedValue);
        Assert.Equal("1300.00", selection.Review.After.Value);
        Assert.Equal(selection.Review.ResultId, transcription.Review.PreviousRunId);
        Assert.Equal("1320.00", transcription.ExtractionTarget);
        Assert.Equal(billed, Assert.Single(transcription.Review.Before.Evidence));
        var omitted = Assert.Single(export.FieldFeedback, f => f.Review.Category == "omit");
        Assert.Null(omitted.Review.After.Value);
        Assert.Equal(Span(page, "TEST-001"), Assert.Single(omitted.Review.After.Evidence));
        Assert.Equal("TEST-001", omitted.Suggestion.Value);
        var legacy = Assert.Single(export.FieldFeedback, f => f.Review.Category == null);
        Assert.Equal("CORRECTED-LEGACY", legacy.Review.After.Value);
        Assert.DoesNotContain(export.FieldFeedback, f => f.Field == "unreviewed");
        // Only the latest categorized field value is an extraction target. In particular,
        // a business omission has no target property, not a null-valued absence label.
        var target = Assert.Single(
            wire.GetProperty("fieldFeedback")
                .EnumerateArray()
                .Where(f => f.TryGetProperty("extractionTarget", out _))
        );
        Assert.Equal(
            "transcription",
            target.GetProperty("review").GetProperty("category").GetString()
        );
        var isolated = (
            await client.GetFromJsonAsync<ReviewedDatasetExport>(
                "/api/training/datasets/default/export"
            )
        )!;
        Assert.Empty(isolated.FieldFeedback);
        Assert.Empty(isolated.Examples);

        using var db = new SqliteConnection(store.ConnectionString);
        db.Open();
        using var tx = db.BeginTransaction();
        Assert.Empty(
            ReviewedFieldEvidence.Read(
                db,
                tx,
                packet.Id,
                new Dictionary<string, string> { [page.Id] = "stale" }
            )
        );
        using var remove = db.CreateCommand();
        remove.Transaction = tx;
        remove.CommandText = "DELETE FROM extraction_objects WHERE kind='ai-answer' AND id=$key";
        remove.Parameters.AddWithValue("$key", selection.Suggestion.AnswerKey);
        Assert.Equal(1, remove.ExecuteNonQuery());
        Assert.Empty(
            ReviewedFieldEvidence.Read(
                db,
                tx,
                packet.Id,
                new Dictionary<string, string> { [page.Id] = estimate.TextSha256 }
            )
        );
        tx.Rollback();
        Assert.Equal(2, host.Provider.Calls);
    }

    private static ExtractionEvidence Span(Page page, string text) =>
        ExtractionPlatform.Evidence(
            page,
            page.Text.IndexOf(text, StringComparison.Ordinal),
            text.Length
        );

    private static async Task<Packet> ProcessInvoice(Host host, Store store)
    {
        var workflow = WorkflowRegistry.Legacy("live", "gpt-fixture") with
        {
            ProviderProfile = "openai",
            Capabilities = new(
                Extraction: new(
                    Definition.Id,
                    Definition.Version,
                    ExtractionStorage.Hash(Definition),
                    Definition
                )
            ),
        };
        var packet = new Packet
        {
            Mode = "live",
            TestRun = true,
            Workflow = workflow,
        };
        var source = new Source
        {
            Name = "synthetic-invoice.pdf",
            Extension = ".pdf",
            PageCount = 1,
            OcrJson = JsonSerializer.Serialize(new { openAi = new[] { Text } }),
        };
        packet.Sources.Add(source);
        var path = store.SourcePath(packet, source);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Pdfs.Synthetic(path, ["Estimate: 1,200.00; Billed: 1,300.00; Ref: TEST-001"]);
        source.Sha256 = Pdfs.SourceHash(path);
        store.Save(packet);
        await host.Services.GetRequiredService<PacketProcessor>().Process(packet.Id, default);
        return store.Get(packet.Id)!;
    }

    private sealed class Host : WebApplicationFactory<Program>
    {
        private readonly string root = Path.Combine(
            Path.GetTempPath(),
            $"paprika-reviewed-fields-{Guid.NewGuid():N}"
        );
        public ProviderFixture Provider { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration(
                (_, config) =>
                    config.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["paprika:DataDirectory"] = root,
                            ["paprika:Operations:Mode"] = "local",
                            ["paprika:AzureEndpoint"] = "",
                            ["paprika:OpenAiApiKey"] = "test-placeholder",
                            ["paprika:OpenAiBaseUrl"] = "https://fixture.example.test/v1",
                            ["paprika:OpenAiReadModel"] = "fixture-reader",
                            ["paprika:OpenAiModel"] = "gpt-fixture",
                        }
                    )
            );
            builder.ConfigureServices(s =>
                s.AddHttpClient("providers").ConfigurePrimaryHttpMessageHandler(() => Provider)
            );
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private sealed class ProviderFixture : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        )
        {
            Calls++;
            Assert.Equal("fixture.example.test", request.RequestUri!.Host);
            if (request.RequestUri.AbsolutePath == "/v1/decisions")
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            """
                            {"model":"gpt-fixture","answers":[{"name":"documentType","type":"choice","choice":"invoice","confidence":0.9},{"name":"startsDocument","probability":1}]}
                            """
                        ),
                    }
                );
            Assert.Equal("/v1/responses", request.RequestUri.AbsolutePath);
            var reference = new { value = "TEST-001", quote = "Ref: TEST-001" };
            var values = JsonSerializer.Serialize(
                new
                {
                    amount = new { value = "1,200.00", quote = "Estimate: 1,200.00" },
                    reference,
                    legacy = reference,
                    unreviewed = reference,
                }
            );
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        new
                        {
                            status = "completed",
                            model = "fixture-reader-resolved",
                            output = new[]
                            {
                                new
                                {
                                    type = "message",
                                    content = new[] { new { type = "output_text", text = values } },
                                },
                            },
                        }
                    ),
                }
            );
        }
    }
}
