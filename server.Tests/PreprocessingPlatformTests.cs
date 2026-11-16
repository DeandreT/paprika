using paprika;
using SkiaSharp;
using Xunit;

namespace paprika.Tests;

public sealed class PreprocessingPlatformTests
{
    [Fact]
    public void RotationGrayscaleAndContrastProduceRealChangedPixelsWithoutChangingOriginal()
    {
        using var image = new SKBitmap(2, 3);
        image.Erase(SKColors.White);
        image.SetPixel(0, 0, SKColors.Red);
        using var result = PreprocessingPlatform.Transform(image, new(90, true, 2));
        Assert.Equal(3, result.Width);
        Assert.Equal(2, result.Height);
        var c = result.GetPixel(2, 0);
        Assert.Equal(c.Red, c.Green);
        Assert.Equal(c.Green, c.Blue);
        Assert.Equal(SKColors.Red, image.GetPixel(0, 0));
        Assert.NotNull(PreprocessingPlatform.Validate(new(45)));
        Assert.NotNull(PreprocessingPlatform.Validate(new(Contrast: double.NaN)));
    }
}
