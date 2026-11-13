using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using PaperworkClassifier;
using Xunit;

namespace PaperworkClassifier.Tests;

public sealed class ExtractionPlatformTests
{
    [Fact]
    public void ActualSplitReviewRegeneratesCoverageAndApprovalAndReleaseRejectUncheckedRequiredValue()
    {
        using var db = new ExtractionTestDatabase();
        var p = db.Packet("Invoice: A");
        var source = p.Sources[0];
        var continuation = new Page
        {
            SourceId = source.Id,
            SourceName = source.Name,
            PageNumber = 2,
            Text = "Unlabeled page",
            PredictedType = "invoice",
        };
        p.Pages.Add(continuation);
        p.Documents[0].PageIds.Add(continuation.Id);
        source.PageCount = 2;
        Pdfs.Synthetic(db.Store.SourcePath(p, source), p.Pages.Select(page => page.Text).ToArray());
        var definition = new ExtractionDefinition(
            "required-split",
            1,
            [new("invoice", Label: "Invoice", Required: true)],
            [],
            "invoice"
        );
        p.Workflow = p.EffectiveWorkflow with
        {
            Capabilities = new(
                Extraction: new(
                    definition.Id,
                    definition.Version,
                    ExtractionStorage.Hash(definition),
                    definition
                )
            ),
        };
        db.Store.Accept(p, null);
        ExtractionPlatform.RunConfigured(db.Store, p);
        var split = new List<Document>
        {
            new()
            {
                Id = p.Documents[0].Id,
                Type = "invoice",
                PageIds = [p.Pages[0].Id],
            },
            new() { Type = "invoice", PageIds = [continuation.Id] },
        };
        var saved = db.Store.Review(p.Id, new(p.Revision, split, false));
        Assert.Equal(200, saved.Code);
        var reviewed = saved.Packet!;
        var run = ExtractionPlatform.Current(db.Store, reviewed)!;
        Assert.Equal(2, run.Results.Length);
        Assert.Equal(
            "missing",
            run.Results.Single(r => r.DocumentId == split[1].Id).Fields[0].Status
        );
        var rejected = db.Store.Review(p.Id, new(reviewed.Revision, reviewed.Documents, true));
        Assert.Equal(409, rejected.Code);
        Assert.Equal(reviewed.Revision, db.Store.Get(p.Id)!.Revision);
        Assert.Equal("review", db.Store.Get(p.Id)!.Status);
        Assert.Throws<WorkflowException>(() => PlatformReleaseGate.Ensure(db.Store, reviewed));
        Assert.Equal(
            200,
            (
                (IStatusCodeHttpResult)
                    ExtractionPlatform.Correct(
                        db.Store,
                        p.Id,
                        new(
                            reviewed.Revision,
                            run.Id,
                            split[1].Id,
                            "invoice",
                            "B",
                            "Verified missing label from source",
                            []
                        )
                    )
            ).StatusCode
        );
        var corrected = db.Store.Get(p.Id)!;
        var approved = db.Store.Review(p.Id, new(corrected.Revision, corrected.Documents, true));
        Assert.Equal(200, approved.Code);
        Assert.Equal("approved", approved.Packet!.Status);
        PlatformReleaseGate.Ensure(db.Store, approved.Packet);
        Assert.Equal(
            "B",
            ExtractionPlatform
                .Current(db.Store, approved.Packet)!
                .Results.Single(r => r.DocumentId == split[1].Id)
                .Fields[0]
                .Value
        );
    }

    [Fact]
    public void PartialReviewCarryRegeneratesChangedSplitCoveragePreservesCorrectionAndBlocksMissingRequiredField()
    {
        using var db = new ExtractionTestDatabase();
        var p = db.Packet("Invoice: A");
        var source = p.Sources[0];
        var second = new Page
        {
            SourceId = source.Id,
            SourceName = source.Name,
            PageNumber = 2,
            Text = "Invoice: B",
            PredictedType = "invoice",
        };
        var third = new Page
        {
            SourceId = source.Id,
            SourceName = source.Name,
            PageNumber = 3,
            Text = "Unlabeled continuation",
            PredictedType = "invoice",
        };
        p.Pages.AddRange([second, third]);
        source.PageCount = 3;
        Pdfs.Synthetic(db.Store.SourcePath(p, source), p.Pages.Select(page => page.Text).ToArray());
        p.Documents.Add(new Document { Type = "invoice", PageIds = [second.Id, third.Id] });
        var definition = new ExtractionDefinition(
            "required-invoice",
            1,
            [new("invoice", Label: "Invoice", Required: true)],
            [],
            "invoice"
        );
        p.Workflow = p.EffectiveWorkflow with
        {
            Capabilities = new(
                Extraction: new(
                    definition.Id,
                    definition.Version,
                    ExtractionStorage.Hash(definition),
                    definition
                )
            ),
        };
        db.Store.Accept(p, null);
        ExtractionPlatform.RunConfigured(db.Store, p);
        var initial = ExtractionPlatform.Current(db.Store, p)!;
        Assert.Empty(ExtractionPlatform.Blockers(p, initial));
        ExtractionPlatform.Correct(
            db.Store,
            p.Id,
            new(
                p.Revision,
                initial.Id,
                p.Documents[0].Id,
                "invoice",
                "A-CORRECT",
                "Verified original document",
                initial.Results[0].Fields[0].Evidence
            )
        );
        var before = db.Store.Get(p.Id)!;
        var after = JsonSerializer.Deserialize<Packet>(
            JsonSerializer.Serialize(before, Contract.Json),
            Contract.Json
        )!;
        after.Revision++;
        after.Documents[1].PageIds = [second.Id];
        after.Documents.Add(new Document { Type = "invoice", PageIds = [third.Id] });
        using var c = new SqliteConnection(db.Store.ConnectionString);
        c.Open();
        using var tx = c.BeginTransaction();
        ExtractionPlatform.OnReview(c, tx, before, after, "reviewer");
        var partial = ExtractionPlatform.Current(c, tx, after)!;
        Assert.Single(partial.Results);
        Assert.Contains(ExtractionPlatform.Blockers(after, partial), b => b.Contains("coverage"));
        var completed = ExtractionPlatform.RunConfigured(c, tx, after)!;
        Assert.Equal(3, completed.Results.Length);
        var preserved = completed.Results.Single(r => r.DocumentId == after.Documents[0].Id).Fields[
            0
        ];
        Assert.Equal("corrected", preserved.Status);
        Assert.Equal("A-CORRECT", preserved.Value);
        Assert.Equal(
            "B",
            completed.Results.Single(r => r.DocumentId == after.Documents[1].Id).Fields[0].Value
        );
        Assert.Equal(
            "missing",
            completed.Results.Single(r => r.DocumentId == after.Documents[2].Id).Fields[0].Status
        );
        Assert.Contains(ExtractionPlatform.Blockers(after, completed), b =>
            b.Contains("invoice") && b.Contains(after.Documents[2].Id)
        );
        Assert.Throws<WorkflowException>(() => ExtractionPlatform.EnsureComplete(c, tx, after));
        after.Status = "approved";
        Assert.Throws<WorkflowException>(() => ExtractionPlatform.EnsureConfigured(c, tx, after));
        var cleared = completed with
        {
            Results = completed
                .Results.Select(r =>
                    r.DocumentId != after.Documents[2].Id
                        ? r
                        : r with
                        {
                            Fields = [r.Fields[0] with { Value = null, Status = "corrected" }],
                        }
                )
                .ToArray(),
        };
        Assert.NotEmpty(ExtractionPlatform.Blockers(after, cleared));
        var valid = completed with
        {
            Id = Guid.NewGuid().ToString("N"),
            Results = completed
                .Results.Select(r =>
                    r.DocumentId != after.Documents[2].Id
                        ? r
                        : r with
                        {
                            Fields = [r.Fields[0] with { Value = "C", Status = "corrected" }],
                        }
                )
                .ToArray(),
        };
        using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText =
            "INSERT INTO extraction_objects(kind,id,json) VALUES('run',$id,$json)";
        command.Parameters.AddWithValue("$id", valid.Id);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(valid, Contract.Json));
        command.ExecuteNonQuery();
        ExtractionPlatform.EnsureComplete(c, tx, after);
        Assert.Empty(ExtractionPlatform.Blockers(after, valid));
        tx.Commit();
    }

    [Fact]
    public void DirectLegacyReviewWithoutExtractionSchemaRemainsSupported()
    {
        using var db = new ExtractionTestDatabase();
        var p = db.Packet("legacy");
        db.Store.Accept(p, null);
        using (var c = new SqliteConnection(db.Store.ConnectionString))
        {
            c.Open();
            using var command = c.CreateCommand();
            command.CommandText = "DROP TABLE extraction_objects";
            command.ExecuteNonQuery();
        }
        var result = db.Store.Review(p.Id, new(p.Revision, p.Documents, false));
        Assert.Equal(200, result.Code);
        using var connection = new SqliteConnection(db.Store.ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        Assert.Null(ExtractionPlatform.Current(connection, transaction, result.Packet!));
    }

    [Fact]
    public void SyntheticFormNormalizesValuesAndExtractsQuotedDelimitedRowsWithEvidence()
    {
        using var db = new ExtractionTestDatabase();
        var p = db.Packet(
            "Invoice: INV-22\nTotal: 1,234.50\nDate: 11/13/2026\nSKU|Qty|Description\nA1|2|\"Blue | large\"\nB2|3|Small\n\nEnd"
        );
        var definition = Definition();
        var run = ExtractionPlatform.Extract(p, definition);
        var result = Assert.Single(run.Results);
        Assert.Equal("INV-22", result.Fields[0].Value);
        Assert.Equal("1234.50", result.Fields[1].Value);
        Assert.Equal("2026-11-13", result.Fields[2].Value);
        Assert.Equal("missing", result.Fields[3].Status);
        var rows = Assert.Single(result.Tables).Rows;
        Assert.Equal(2, rows.Length);
        Assert.Equal("Blue | large", rows[0].Cells[2].Value);
        foreach (
            var span in result
                .Fields.SelectMany(f => f.Evidence)
                .Concat(rows.SelectMany(r => r.Cells.SelectMany(c => c.Evidence)))
        )
            Assert.True(ExtractionPlatform.ValidEvidence(p, span));
        p.Pages[0].Text += "\nTotal: 3.00";
        Assert.Equal(
            "ambiguous",
            ExtractionPlatform.Extract(p, definition).Results[0].Fields[1].Status
        );
    }

    [Fact]
    public void CorrectionRejectsStaleRevisionAndEvidencePreservesOriginalAndCarriesOnUnchangedReview()
    {
        using var db = new ExtractionTestDatabase();
        var p = db.Packet("Invoice: WRONG\nTotal: 12.00\nDate: 2026-11-13");
        db.Store.Accept(p, null);
        var definition = Definition();
        var run = ExtractionPlatform.Extract(p, definition);
        ExtractionStorage.Add(db.Store, "run", run.Id, run);
        var evidence = run.Results[0].Fields[0].Evidence;
        var correction = new ExtractionCorrection(
            p.Revision,
            run.Id,
            p.Documents[0].Id,
            "invoice",
            "RIGHT",
            "Verified against source",
            evidence
        );
        Assert.Equal(
            409,
            (
                (IStatusCodeHttpResult)
                    ExtractionPlatform.Correct(db.Store, p.Id, correction with { Revision = 99 })
            ).StatusCode
        );
        Assert.Equal(
            400,
            (
                (IStatusCodeHttpResult)
                    ExtractionPlatform.Correct(
                        db.Store,
                        p.Id,
                        correction with
                        {
                            Evidence = [evidence[0] with { TextSha256 = "stale" }],
                        }
                    )
            ).StatusCode
        );
        Assert.Equal(
            200,
            (
                (IStatusCodeHttpResult)
                    ExtractionPlatform.Correct(db.Store, p.Id, correction, "reviewer-a")
            ).StatusCode
        );
        var corrected = db.Store.Get(p.Id)!;
        Assert.Equal(2, corrected.Revision);
        Assert.Equal("review", corrected.Status);
        Assert.Equal(
            "WRONG",
            ExtractionStorage
                .Get<ExtractionRun>(db.Store, "run", run.Id)!
                .Results[0]
                .Fields[0]
                .Value
        );
        Assert.Equal(
            "RIGHT",
            ExtractionPlatform.Current(db.Store, corrected)!.Results[0].Fields[0].Value
        );
        var after = JsonSerializer.Deserialize<Packet>(
            JsonSerializer.Serialize(corrected, Contract.Json),
            Contract.Json
        )!;
        after.Revision++;
        after.Status = "approved";
        using var c = new SqliteConnection(db.Store.ConnectionString);
        c.Open();
        using var tx = c.BeginTransaction();
        ExtractionPlatform.OnReview(c, tx, corrected, after, "reviewer-a");
        tx.Commit();
        Assert.Equal(
            "corrected",
            ExtractionPlatform.Current(db.Store, after)!.Results[0].Fields[0].Status
        );
        var changed = JsonSerializer.Deserialize<Packet>(
            JsonSerializer.Serialize(after, Contract.Json),
            Contract.Json
        )!;
        changed.Revision++;
        changed.Documents[0].Type = "bol";
        using var tx2 = c.BeginTransaction();
        ExtractionPlatform.OnReview(c, tx2, after, changed, "reviewer-a");
        tx2.Commit();
        Assert.Null(ExtractionPlatform.Current(db.Store, changed));
    }

    [Fact]
    public void ExactSnapshotFreezeRejectsChangedDefinitionsAndIsIndependentOfCallerMutation()
    {
        using var db = new ExtractionTestDatabase();
        var definition = Definition();
        var policy = new ExtractionPolicy(
            definition.Id,
            definition.Version,
            ExtractionStorage.Hash(definition),
            definition
        );
        using var c = new SqliteConnection(db.Store.ConnectionString);
        c.Open();
        using var tx = c.BeginTransaction();
        var frozen = ExtractionPlatform.Freeze(c, tx, policy);
        definition.Fields[0] = definition.Fields[0] with { Label = "Changed" };
        Assert.Equal("Invoice", frozen.Definition!.Fields[0].Label);
        Assert.Throws<WorkflowException>(() => ExtractionPlatform.Freeze(c, tx, policy));
    }

    internal static ExtractionDefinition Definition() =>
        new(
            "invoice-v1",
            1,
            [
                new("invoice", Label: "Invoice", Required: true),
                new("total", "number", Label: "Total"),
                new("date", "date", Label: "Date"),
                new("missing", Label: "Missing"),
            ],
            [new("items", "SKU|Qty|Description", ["sku", "qty", "description"])]
        );

    [Fact]
    public void ConfiguredRetryPreservesHumanCorrectionAtSameRevision()
    {
        using var db = new ExtractionTestDatabase();
        var p = db.Packet("Invoice: WRONG\nTotal: 20\nDate: 2026-11-13");
        var d = Definition();
        p.Workflow = p.EffectiveWorkflow with
        {
            Capabilities = new(Extraction: new(d.Id, d.Version, ExtractionStorage.Hash(d), d)),
        };
        db.Store.Accept(p, null);
        ExtractionPlatform.RunConfigured(db.Store, p);
        var run = ExtractionPlatform.Current(db.Store, p)!;
        ExtractionPlatform.Correct(
            db.Store,
            p.Id,
            new(
                p.Revision,
                run.Id,
                p.Documents[0].Id,
                "invoice",
                "RIGHT",
                "Human source check",
                run.Results[0].Fields[0].Evidence
            )
        );
        var corrected = db.Store.Get(p.Id)!;
        ExtractionPlatform.RunConfigured(db.Store, corrected);
        Assert.Equal(
            "RIGHT",
            ExtractionPlatform.Current(db.Store, corrected)!.Results[0].Fields[0].Value
        );
        Assert.Equal(2, ExtractionStorage.List<ExtractionRun>(db.Store, "run").Length);
    }
}

internal sealed class ExtractionTestDatabase : IDisposable
{
    public string Root { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp",
            "opencode",
            "extraction-test-" + Guid.NewGuid().ToString("N")
        );
    public Store Store { get; }

    public ExtractionTestDatabase()
    {
        Store = new Store(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["DataDirectory"] = Root })
                .Build()
        );
        ExtractionStorage.Initialize(Store);
    }

    public Packet Packet(string text)
    {
        var packet = new Packet { Status = "review" };
        var source = new Source
        {
            Name = "form.pdf",
            Extension = ".pdf",
            PageCount = 1,
        };
        packet.Sources.Add(source);
        Directory.CreateDirectory(Path.Combine(Root, packet.Id));
        Pdfs.Synthetic(Store.SourcePath(packet, source), [text]);
        source.Sha256 = Pdfs.SourceHash(Store.SourcePath(packet, source));
        var page = new Page
        {
            SourceId = source.Id,
            SourceName = source.Name,
            PageNumber = 1,
            Text = text,
            PredictedType = "invoice",
        };
        packet.Pages.Add(page);
        packet.Documents.Add(new Document { Type = "invoice", PageIds = [page.Id] });
        return packet;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(Root, true);
    }
}
