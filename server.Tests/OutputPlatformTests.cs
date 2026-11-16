using System.Text;
using paprika;
using SkiaSharp;
using UglyToad.PdfPig;
using Xunit;

namespace paprika.Tests;

public sealed class OutputPlatformTests
{
    [Fact]
    public void AutomaticPoliciesRejectRegionlessPixelRedactionAndSearchableTextRedaction()
    {
        Assert.Contains("per-packet", OutputPlatform.Validate(new("redacted-image-pdf"))!);
        Assert.Contains("reviewed regions", OutputPlatform.Validate(new("redacted-image-pdf"))!);
        Assert.NotNull(OutputPlatform.Validate(new("searchable-image-pdf", ["SECRET"])));
        Assert.Null(OutputPlatform.Validate(new("searchable-image-pdf")));
        Assert.Null(OutputPlatform.Validate(new("searchable-image-pdf", [])));
        Assert.Null(OutputPlatform.Validate(new("text-json", ["SECRET"])));
        Assert.Null(OutputPlatform.Validate(new("redacted-text-pdf", ["SECRET"])));
        using var db = new ExtractionTestDatabase();
        var packet = db.Packet("SECRET");
        var bytes = OutputPlatform.Generate(
            db.Store,
            packet,
            new(
                packet.Revision,
                "redacted-image-pdf",
                Regions: [new(packet.Pages[0].Id, 0, 0, 100, 100)]
            )
        );
        using var pdf = PdfDocument.Open(bytes);
        Assert.Empty(pdf.GetPage(1).Text);
    }

    [Fact]
    public void DeterministicFileSurvivesTransactionRollbackAndExactRetryRecoversWithoutOverwriting()
    {
        using var db = new ExtractionTestDatabase();
        var p = db.Packet("Invoice: RETRY");
        var request = new OutputRequest(p.Revision, "text-json");
        string id;
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection(db.Store.ConnectionString))
        {
            c.Open();
            using var tx = c.BeginTransaction();
            id = OutputPlatform.Save(db.Store, p, request, c, tx, "crash-retry").Id;
            tx.Rollback();
        }
        Assert.Empty(ExtractionStorage.List<OutputArtifact>(db.Store, "output"));
        Assert.Throws<ArgumentException>(() =>
            OutputPlatform.Save(
                db.Store,
                p,
                request with
                {
                    RedactValues = ["absent-value"],
                },
                idempotencyKey: "crash-retry"
            )
        );
        var artifact = OutputPlatform.Save(db.Store, p, request, idempotencyKey: "crash-retry");
        Assert.Equal(id, artifact.Id);
        Assert.Single(ExtractionStorage.List<OutputArtifact>(db.Store, "output"));
        Assert.Single(Directory.GetFiles(Path.Combine(db.Root, "outputs"), "*.json"), path =>
            !path.EndsWith(".identity.json")
        );
        File.WriteAllText(OutputPlatform.PathFor(db.Store, id, "json"), "different bytes");
        Assert.Throws<ArgumentException>(() =>
            OutputPlatform.Save(db.Store, p, request, idempotencyKey: "crash-retry")
        );
    }

    [Fact]
    public void DecodedPngSourceCanBeSearchableOrPixelRedactedWithoutImportingOriginal()
    {
        using var db = new ExtractionTestDatabase();
        var p = db.Packet("IMAGE-SECRET");
        var source = p.Sources[0];
        source.Extension = ".png";
        using (var image = RecognitionPlatformTests.Image(true))
        using (var encoded = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var file = File.Create(db.Store.SourcePath(p, source)))
            encoded.SaveTo(file);
        source.OcrJson =
            "{\"analyzeResult\":{\"pages\":[{\"pageNumber\":1,\"width\":128,\"height\":128,\"lines\":[{\"content\":\"IMAGE-SECRET\",\"polygon\":[8,16,40,16,40,40,8,40]}]}]}}";
        var searchable = OutputPlatform.Generate(
            db.Store,
            p,
            new(p.Revision, "searchable-image-pdf")
        );
        using var searchablePdf = PdfDocument.Open(searchable);
        Assert.Contains("IMAGE-SECRET", searchablePdf.GetPage(1).Text);
        var redacted = OutputPlatform.Generate(
            db.Store,
            p,
            new(p.Revision, "redacted-image-pdf", Regions: [new(p.Pages[0].Id, 0, 0, 128, 128)])
        );
        using var redactedPdf = PdfDocument.Open(redacted);
        Assert.Empty(redactedPdf.GetPage(1).Text);
        Assert.DoesNotContain("IMAGE-SECRET", Encoding.Latin1.GetString(redacted));
        Assert.Throws<ArgumentException>(() =>
            OutputPlatform.Generate(
                db.Store,
                p,
                new(p.Revision, "redacted-image-pdf", Regions: [new("not-a-page", 0, 0, 1, 1)])
            )
        );
    }

    [Fact]
    public void ReconstructedRedactedPdfExtractedTextContainsNoSensitiveString()
    {
        using var db = new ExtractionTestDatabase();
        var p = db.Packet("Invoice: SECRET-123\nTotal: 12.00");
        var bytes = OutputPlatform.Generate(
            db.Store,
            p,
            new(p.Revision, "redacted-text-pdf", ["SECRET-123"])
        );
        using var pdf = PdfDocument.Open(bytes);
        var text = string.Join("\n", pdf.GetPages().Select(page => page.Text));
        Assert.Contains("[REDACTED]", text);
        Assert.DoesNotContain("SECRET-123", text);
        Assert.DoesNotContain("SECRET-123", Encoding.Latin1.GetString(bytes));
    }

    [Fact]
    public void PdfRasterRedactionActuallyRemovesUnderlyingTextAndReencodesPixels()
    {
        using var db = new ExtractionTestDatabase();
        var p = db.Packet("SECRET-PDF-TEXT\nPublic content");
        using var rendered = PreprocessingPdfRaster.Load(db.Store, p, p.Sources[0]);
        Assert.InRange(rendered.Width * rendered.Height, 1, 4_000_000);
        var bytes = OutputPlatform.Generate(
            db.Store,
            p,
            new(
                p.Revision,
                "redacted-image-pdf",
                Regions: [new(p.Pages[0].Id, 0, 0, rendered.Width, rendered.Height)]
            )
        );
        using var pdf = PdfDocument.Open(bytes);
        Assert.Empty(pdf.GetPage(1).Text);
        Assert.DoesNotContain("SECRET-PDF-TEXT", Encoding.Latin1.GetString(bytes));
        var path = Path.Combine(db.Root, "redacted-check.pdf");
        File.WriteAllBytes(path, bytes);
        var source = new Source
        {
            Id = "redacted-check",
            Extension = ".pdf",
            Name = "check.pdf",
            PageCount = 1,
        };
        File.Copy(path, db.Store.SourcePath(p, source));
        using var redacted = PreprocessingPdfRaster.Load(db.Store, p, source);
        var pixel = redacted.GetPixel(redacted.Width / 2, redacted.Height / 2);
        Assert.InRange(pixel.Red, 0, 10);
        Assert.InRange(pixel.Green, 0, 10);
        Assert.InRange(pixel.Blue, 0, 10);
    }

    [Fact]
    public void SearchableImagePdfContainsPositionedInvisibleActualOcrTextAndIdempotentArtifacts()
    {
        using var db = new ExtractionTestDatabase();
        var p = db.Packet("INVOICE-44");
        var source = p.Sources[0];
        source.OcrJson =
            "{\"analyzeResult\":{\"pages\":[{\"pageNumber\":1,\"width\":612,\"height\":792,\"lines\":[{\"content\":\"INVOICE-44\",\"polygon\":[50,40,200,40,200,60,50,60]}]}]}}";
        var request = new OutputRequest(p.Revision, "searchable-image-pdf");
        var bytes = OutputPlatform.Generate(db.Store, p, request);
        using var pdf = PdfDocument.Open(bytes);
        Assert.Contains("INVOICE-44", pdf.GetPage(1).Text);
        Assert.Contains("3 Tr", Encoding.Latin1.GetString(bytes));
        Assert.True(pdf.GetPage(1).Letters[0].BoundingBox.Left > 0);
        var a = OutputPlatform.Save(db.Store, p, request, idempotencyKey: "retry");
        var b = OutputPlatform.Save(db.Store, p, request, idempotencyKey: "retry");
        Assert.Equal(a.Id, b.Id);
        Assert.Single(ExtractionStorage.List<OutputArtifact>(db.Store, "output"));
        Assert.True(a.Preview);
        Assert.Throws<ArgumentException>(() =>
            OutputPlatform.Save(
                db.Store,
                p,
                request with
                {
                    Format = "text-json",
                },
                idempotencyKey: "retry"
            )
        );
    }
}
