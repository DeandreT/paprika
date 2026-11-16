using paprika;
using SkiaSharp;
using Xunit;

namespace paprika.Tests;

public sealed class RecognitionPlatformTests
{
    [Fact]
    public void ActualImageFeaturesDistinguishLabeledLayoutsAndRejectMarginTieAndBlank()
    {
        using var left = Image(true);
        using var right = Image(false);
        using var blank = new SKBitmap(128, 128);
        using (var canvas = new SKCanvas(blank))
            canvas.Clear(SKColors.White);
        var a = Template("left", RecognitionPlatform.Features(left));
        var b = Template("right", RecognitionPlatform.Features(right));
        var policy = new RecognitionPolicy([Reference(a), Reference(b)], .8, .1);
        Assert.Equal(
            "left",
            RecognitionPlatform.Compare(RecognitionPlatform.Features(left), policy).Label
        );
        Assert.Equal(
            "right",
            RecognitionPlatform.Compare(RecognitionPlatform.Features(right), policy).Label
        );
        Assert.Equal(
            "unknown",
            RecognitionPlatform.Compare(RecognitionPlatform.Features(blank), policy).Label
        );
        var tied = b with { Features = a.Features };
        Assert.Equal(
            "unknown",
            RecognitionPlatform
                .Compare(a.Features, policy with { Templates = [Reference(a), Reference(tied)] })
                .Label
        );
        Assert.Null(RecognitionPlatform.Validate(policy));
        Assert.NotNull(
            RecognitionPlatform.Validate(
                policy with
                {
                    Templates = [Reference(a) with { Sha256 = "wrong" }],
                }
            )
        );
    }

    [Fact]
    public async Task ConfiguredPreprocessingAutomaticallyCreatesHashedImmutablePdfDerivativeWithoutRecognition()
    {
        using var db = new ExtractionTestDatabase();
        var packet = db.Packet("Invoice: ORIGINAL");
        var originalHash = Pdfs.SourceHash(db.Store.SourcePath(packet, packet.Sources[0]));
        packet.Workflow = packet.EffectiveWorkflow with
        {
            Capabilities = new(Preprocessing: new(90, true, 1.5)),
        };
        await RecognitionPlatform.ApplyAsync(db.Store, packet, CancellationToken.None);
        var derivative = Assert.Single(
            ExtractionStorage.List<PreprocessingDerivative>(db.Store, "derivative")
        );
        Assert.Equal(originalHash, derivative.OriginalSha256);
        Assert.Equal(originalHash, Pdfs.SourceHash(db.Store.SourcePath(packet, packet.Sources[0])));
        Assert.Equal(
            derivative.Sha256,
            Pdfs.SourceHash(PreprocessingPlatform.DerivativePath(db.Store, derivative.Id))
        );
        Assert.True(derivative.Width > derivative.Height);
    }

    internal static SKBitmap Image(bool left)
    {
        var bitmap = new SKBitmap(128, 128);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black };
        canvas.DrawRect(left ? 8 : 80, 16, 32, 96, paint);
        return bitmap;
    }

    private static RecognitionTemplate Template(string label, float[] features) =>
        new(
            label,
            1,
            label,
            "synthetic-image",
            RecognitionPlatform.Algorithm,
            features,
            DateTimeOffset.UtcNow
        );

    private static RecognitionTemplateRef Reference(RecognitionTemplate t) =>
        new(t.Id, t.Version, ExtractionStorage.Hash(t), t);
}
