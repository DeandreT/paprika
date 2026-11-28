using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;
using static paprika.Tests.ApprovedFieldExportTests;

namespace paprika.Tests;

public sealed class FieldCorrectionApiTests
{
    [Fact]
    public async Task WrongSelectionRequiresOneChangedVerifiedSpanFromTheSameDocument()
    {
        await using var app = new ExportFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-paprika", "1");
        var packet = app.AddPacket(
            Definition(),
            includeExcludedDocument: true,
            invoiceText: "Invoice: ARTIFICIAL\nTotal: 8.00\nAmount due: 2,450.50"
        );
        ExtractionPlatform.RunConfigured(app.Store, packet);
        var run = ExtractionPlatform.Current(app.Store, packet)!;
        var original = Field(run, "total");
        var page = packet.Pages[0];
        var amount = Span(page, "2,450.50");
        var otherDocument = Span(packet.Pages[1], "ARTIFICIAL-EXCLUDED");
        var request = Feedback(run, "total", "wrong-selection", " 2,450.5 ", amount);
        var url = $"/api/extraction/{packet.Id}";
        async Task Reject(
            ExtractionCorrection invalid,
            HttpStatusCode expected = HttpStatusCode.BadRequest
        )
        {
            using var rejected = await client.PostAsJsonAsync($"{url}/corrections", invalid);
            Assert.True(
                rejected.StatusCode == expected,
                $"{JsonSerializer.Serialize(invalid, Contract.Json)}: expected {expected}, received {rejected.StatusCode}."
            );
        }
        await Reject(request with { Evidence = [] });
        await Reject(request with { Evidence = [amount, amount] });
        await Reject(request with { Evidence = original.Evidence, Value = original.Value });
        await Reject(
            request with
            {
                Evidence = [ExtractionPlatform.Evidence(page, amount.Offset, 0)],
            }
        );
        await Reject(request with { Evidence = [amount with { Offset = page.Text.Length }] });
        await Reject(request with { Evidence = [amount with { TextSha256 = "stale" }] });
        await Reject(request with { Evidence = [amount with { Text = "2450.50" }] });
        await Reject(
            Feedback(run, "invoice", "wrong-selection", otherDocument.Text, otherDocument)
        );
        await Reject(request with { Value = "8.00" });
        await Reject(request with { Category = "unknown" });
        await Reject(Feedback(run, "invoice", "omit", ""));
        await Reject(Feedback(run, "total", "omit", null, amount));
        await Reject(Feedback(run, "total", "transcription", "10", amount));
        await Reject(Feedback(run, "invoice", "transcription", " "));
        await Reject(Feedback(run, "total", "transcription", "not a number"));
        // The earlier run still exists at this exact revision and definition.
        var latest = run with { Id = Guid.NewGuid().ToString("N") };
        ExtractionStorage.Add(app.Store, "run", latest.Id, latest);
        var currentRequest = request with { RunId = latest.Id };
        await Reject(request, HttpStatusCode.Conflict);
        await Reject(
            currentRequest with
            {
                Revision = packet.Revision - 1,
            },
            HttpStatusCode.Conflict
        );
        var before = (await client.GetFromJsonAsync<ExtractionHistoryResponse>(url))!;
        Assert.Empty(before.Corrections);
        Assert.Equal(latest.Id, before.CurrentConfiguredRunId);
        Assert.Equal(packet.Revision, app.Store.Get(packet.Id)!.Revision);

        var changed = await Correct(client, url, currentRequest);
        Assert.Equal(packet.Revision + 1, changed.Packet.Revision);
        Assert.Equal("review", changed.Packet.Status);
        var effective = Field(changed.Run, "total");
        Assert.Equal("2450.5", effective.Value);
        Assert.Equal("corrected", effective.Status);
        Assert.Equal(amount, Assert.Single(effective.Evidence));
        var history = (await client.GetFromJsonAsync<ExtractionHistoryResponse>(url))!;
        Assert.Equal(changed.Run.Id, history.CurrentConfiguredRunId);
        Assert.Equal<string>([run.Id, latest.Id, changed.Run.Id], history.Runs.Select(r => r.Id));
        AssertValue(original, Field(history.Runs[1], "total"));
        var row = Assert.Single(history.Corrections);
        Assert.Equal(packet.Id, row.GetProperty("packetId").GetString());
        Assert.Equal(latest.Id, row.GetProperty("previousRunId").GetString());
        Assert.Equal(changed.Run.Id, row.GetProperty("resultId").GetString());
        Assert.Equal("local", row.GetProperty("actor").GetString());
        Assert.Equal(changed.Run.CreatedAt, row.GetProperty("at").GetDateTimeOffset());
        Assert.True(
            JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(currentRequest, Contract.Json),
                row.GetProperty("correction")
            )
        );
        Assert.Equal("number", row.GetProperty("fieldKind").GetString());
        Assert.Equal(effective.Value, row.GetProperty("normalizedValue").GetString());
        AssertValue(
            original,
            row.GetProperty("before").Deserialize<ExtractionValue>(Contract.Json)!
        );
        AssertValue(
            effective,
            row.GetProperty("after").Deserialize<ExtractionValue>(Contract.Json)!
        );
    }

    [Fact]
    public async Task ResolvingAnAmbiguousCandidateThenTranscribingItPreservesTheChosenSource()
    {
        await using var app = new ExportFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-paprika", "1");
        var packet = app.AddPacket(Definition(), invoiceText: "Invoice: COPY\nInvoice: lNV-002");
        ExtractionPlatform.RunConfigured(app.Store, packet);
        var run = ExtractionPlatform.Current(app.Store, packet)!;
        var original = Field(run, "invoice");
        Assert.Equal("ambiguous", original.Status);
        Assert.Equal(2, original.Evidence.Length);
        var selected = original.Evidence[1];
        var url = $"/api/extraction/{packet.Id}";
        var selection = Feedback(run, "invoice", "wrong-selection", selected.Text, selected);
        var resolved = await Correct(client, url, selection);
        var transcription = selection with
        {
            Revision = resolved.Packet.Revision,
            RunId = resolved.Run.Id,
            Category = "transcription",
            Value = "INV-002",
            Reason = "OCR read the initial I as a lowercase L.",
            Evidence = [],
        };
        var corrected = await Correct(client, url, transcription);
        var effective = Field(corrected.Run, "invoice");
        Assert.Equal("INV-002", effective.Value);
        Assert.Equal(selected, Assert.Single(effective.Evidence));
        Assert.Equal("lNV-002", selected.Text);

        var history = (await client.GetFromJsonAsync<ExtractionHistoryResponse>(url))!;
        AssertValue(original, Field(history.Runs[0], "invoice"));
        Assert.Equal(2, history.Corrections.Length);
        var previous = history.Corrections[0];
        var row = history.Corrections[1];
        Assert.Equal(
            previous.GetProperty("resultId").GetString(),
            row.GetProperty("previousRunId").GetString()
        );
        AssertValue(
            Field(resolved.Run, "invoice"),
            row.GetProperty("before").Deserialize<ExtractionValue>(Contract.Json)!
        );
        AssertValue(
            effective,
            row.GetProperty("after").Deserialize<ExtractionValue>(Contract.Json)!
        );
        var raw = row.GetProperty("correction");
        Assert.Equal("transcription", raw.GetProperty("category").GetString());
        Assert.Empty(raw.GetProperty("evidence").EnumerateArray());

        using var reprocessed = await client.PostAsJsonAsync(
            $"/api/packets/{packet.Id}/reprocess",
            new ReprocessRequest(corrected.Packet.Revision)
        );
        reprocessed.EnsureSuccessStatusCode();
        var child = (await reprocessed.Content.ReadFromJsonAsync<PacketResponse>())!;
        Assert.Equal(packet.Id, child.ParentPacketId);
        var fresh = (
            await client.GetFromJsonAsync<ExtractionHistoryResponse>($"/api/extraction/{child.Id}")
        )!;
        Assert.Empty(fresh.Corrections);
        Assert.DoesNotContain(fresh.Runs.SelectMany(r => r.Results).SelectMany(r => r.Fields), f =>
            f.Status == "corrected"
        );
        var retained = (await client.GetFromJsonAsync<ExtractionHistoryResponse>(url))!;
        Assert.Equal(corrected.Run.Id, retained.CurrentConfiguredRunId);
        Assert.Equal(row.GetRawText(), retained.Corrections[1].GetRawText());
    }

    [Fact]
    public async Task RequiredOmissionSavesTheDecisionButBlocksApproval()
    {
        await using var app = new ExportFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-paprika", "1");
        var packet = app.AddPacket(Definition());
        ExtractionPlatform.RunConfigured(app.Store, packet);
        var run = ExtractionPlatform.Current(app.Store, packet)!;
        var original = Field(run, "invoice");
        var omitted = await Correct(
            client,
            $"/api/extraction/{packet.Id}",
            Feedback(run, "invoice", "omit", null, original.Evidence)
        );
        var effective = Field(omitted.Run, "invoice");
        Assert.Null(effective.Value);
        Assert.Equal("corrected", effective.Status);
        Assert.Equal(original.Evidence, effective.Evidence);
        using var approval = await client.PutAsJsonAsync(
            $"/api/packets/{packet.Id}/review",
            new ReviewRequest(omitted.Packet.Revision, omitted.Packet.Documents, true)
        );
        Assert.Equal(HttpStatusCode.Conflict, approval.StatusCode);
        Assert.Contains("invoice", await approval.Content.ReadAsStringAsync());
        Assert.Equal(omitted.Packet.Revision, app.Store.Get(packet.Id)!.Revision);
        using var export = await client.GetAsync($"/api/packets/{packet.Id}/export");
        Assert.Equal(HttpStatusCode.Conflict, export.StatusCode);
    }

    [Fact]
    public async Task LegacyAdHocRequestCarriesConfiguredCorrectionAndReadsPreCategoryHistory()
    {
        await using var app = new ExportFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-paprika", "1");
        var packet = app.AddPacket(Definition());
        var url = $"/api/extraction/{packet.Id}";
        var empty = (await client.GetFromJsonAsync<ExtractionHistoryResponse>(url))!;
        Assert.Null(empty.CurrentConfiguredRunId);
        ExtractionPlatform.RunConfigured(app.Store, packet);
        var original = ExtractionPlatform.Current(app.Store, packet)!;
        packet.Revision++;
        app.Store.Save(packet);
        var historic = AddInvoiceRun(
            app.Store,
            original with
            {
                PacketRevision = packet.Revision,
            },
            "HISTORIC"
        );
        // This is the on-disk shape written before field correction categories existed.
        var oldRow = new
        {
            packetId = packet.Id,
            previousRunId = original.Id,
            resultId = historic.Id,
            correction = new
            {
                revision = original.PacketRevision,
                runId = original.Id,
                documentId = packet.Documents[0].Id,
                field = "invoice",
                value = "HISTORIC",
                reason = "Verified against source.",
                evidence = Field(original, "invoice").Evidence,
            },
            at = historic.CreatedAt,
            actor = "historical reviewer",
        };
        ExtractionStorage.Add(app.Store, "correction", historic.Id, oldRow);
        var adHoc = ExtractionPlatform.Extract(packet, Definition() with { Id = "ad-hoc" });
        ExtractionStorage.Add(app.Store, "run", adHoc.Id, adHoc);
        var before = (await client.GetFromJsonAsync<ExtractionHistoryResponse>(url))!;
        Assert.Equal(historic.Id, before.CurrentConfiguredRunId);
        Assert.Equal(adHoc.Id, before.Runs.Last().Id);

        // Send the original wire contract explicitly: no category and freely supplied evidence.
        using var response = await client.PostAsJsonAsync(
            $"{url}/corrections",
            new
            {
                revision = packet.Revision,
                runId = adHoc.Id,
                documentId = packet.Documents[0].Id,
                field = "invoice",
                value = "MANUAL",
                reason = "Manual ad hoc entry.",
                evidence = Array.Empty<ExtractionEvidence>(),
            }
        );
        response.EnsureSuccessStatusCode();
        var corrected = (await response.Content.ReadFromJsonAsync<ExtractionCorrectionResponse>())!;
        Assert.Equal("MANUAL", Field(corrected.Run, "invoice").Value);
        Assert.Empty(Field(corrected.Run, "invoice").Evidence);
        var history = (await client.GetFromJsonAsync<ExtractionHistoryResponse>(url))!;
        Assert.True(
            JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(oldRow, Contract.Json),
                history.Corrections[0]
            )
        );
        var legacy = Assert
            .Single(history.Corrections, r =>
                r.GetProperty("resultId").GetString() == corrected.Run.Id
            )
            .GetProperty("correction");
        Assert.True(
            !legacy.TryGetProperty("category", out var category)
                || category.ValueKind == JsonValueKind.Null
        );
        var carried = history.Runs.Single(r => r.Id == history.CurrentConfiguredRunId);
        Assert.Equal(corrected.Packet.Revision, carried.PacketRevision);
        Assert.Equal(original.Definition.Id, carried.Definition.Id);
        AssertValue(Field(historic, "invoice"), Field(carried, "invoice"));
        var carry = Assert.Single(history.Corrections, r =>
            r.GetProperty("resultId").GetString() == carried.Id
        );
        Assert.Equal(historic.Id, carry.GetProperty("previousRunId").GetString());
        Assert.False(carry.TryGetProperty("correction", out _));
        Assert.False(string.IsNullOrWhiteSpace(carry.GetProperty("action").GetString()));
        Assert.Equal(
            packet.Documents[0].Id,
            Assert.Single(carry.GetProperty("documentIds").EnumerateArray()).GetString()
        );
    }

    [Fact]
    public async Task RetentionParticipantPurgesOnlyTheSelectedPacketsFieldFeedbackOnCommit()
    {
        await using var app = new ExportFactory();
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-paprika", "1");
        async Task<Packet> WithFeedback()
        {
            var packet = app.AddPacket(Definition());
            ExtractionPlatform.RunConfigured(app.Store, packet);
            var run = ExtractionPlatform.Current(app.Store, packet)!;
            await Correct(
                client,
                $"/api/extraction/{packet.Id}",
                Feedback(run, "invoice", "transcription", "REVIEWED")
            );
            return app.Store.Get(packet.Id)!;
        }
        var target = await WithFeedback();
        var retained = await WithFeedback();
        var retention = new ExtractionRetentionParticipant(app.Store);
        using var connection = new SqliteConnection(app.Store.ConnectionString);
        connection.Open();
        using (var tx = connection.BeginTransaction())
        {
            retention.Purge(connection, tx, target);
            tx.Rollback();
        }
        var restored = (
            await client.GetFromJsonAsync<ExtractionHistoryResponse>($"/api/extraction/{target.Id}")
        )!;
        Assert.Single(restored.Corrections);
        using (var tx = connection.BeginTransaction())
        {
            retention.Purge(connection, tx, target);
            tx.Commit();
        }
        var purged = (
            await client.GetFromJsonAsync<ExtractionHistoryResponse>($"/api/extraction/{target.Id}")
        )!;
        Assert.Empty(purged.Corrections);
        Assert.Empty(purged.Runs);
        var other = (
            await client.GetFromJsonAsync<ExtractionHistoryResponse>(
                $"/api/extraction/{retained.Id}"
            )
        )!;
        Assert.Equal(
            "transcription",
            Assert
                .Single(other.Corrections)
                .GetProperty("correction")
                .GetProperty("category")
                .GetString()
        );
        Assert.Equal(
            "REVIEWED",
            Field(other.Runs.Single(r => r.Id == other.CurrentConfiguredRunId), "invoice").Value
        );
    }

    private static ExtractionCorrection Feedback(
        ExtractionRun run,
        string field,
        string category,
        string? value,
        params ExtractionEvidence[] evidence
    ) =>
        new(
            run.PacketRevision,
            run.Id,
            Assert.Single(run.Results).DocumentId,
            field,
            value,
            "Verified against the printed source.",
            evidence,
            category
        );

    private static ExtractionEvidence Span(Page page, string text) =>
        ExtractionPlatform.Evidence(
            page,
            page.Text.IndexOf(text, StringComparison.Ordinal),
            text.Length
        );

    private static ExtractionValue Field(ExtractionRun run, string name) =>
        Assert.Single(Assert.Single(run.Results).Fields, f => f.Name == name);

    private static void AssertValue(ExtractionValue expected, ExtractionValue actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Value, actual.Value);
        Assert.Equal(expected.Evidence, actual.Evidence);
    }

    private static async Task<ExtractionCorrectionResponse> Correct(
        HttpClient client,
        string url,
        ExtractionCorrection request
    )
    {
        using var response = await client.PostAsJsonAsync($"{url}/corrections", request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ExtractionCorrectionResponse>())!;
    }
}
