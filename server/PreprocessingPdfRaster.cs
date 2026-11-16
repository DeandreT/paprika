using System.Runtime.InteropServices;
using Docnet.Core;
using Docnet.Core.Models;
using SkiaSharp;

namespace paprika;

public static class PreprocessingPdfRaster
{
    private static readonly object Gate = new();
    public const string Renderer = "Docnet.Core-2.6.0/PDFium viewport-1600x2200";

    public static SKBitmap Load(Store store, Packet packet, Source source, int pageNumber = 1)
    {
        var path = store.SourcePath(packet, source);
        if (source.Extension != ".pdf")
        {
            if (pageNumber != 1)
                throw new ArgumentException("Image source has one page.");
            return PreprocessingPlatform.Decode(path);
        }
        if (new FileInfo(path).Length > 64 * 1024 * 1024)
            throw new ArgumentException("PDF raster input exceeds 64 MiB.");
        lock (Gate)
        {
            using var document = DocLib.Instance.GetDocReader(
                File.ReadAllBytes(path),
                new PageDimensions(1600, 2200)
            );
            if (
                document.GetPageCount() > 250
                || pageNumber < 1
                || pageNumber > document.GetPageCount()
            )
                throw new ArgumentException("Invalid PDF page or page limit exceeded.");
            using var page = document.GetPageReader(pageNumber - 1);
            int width = page.GetPageWidth(),
                height = page.GetPageHeight();
            if (width < 1 || height < 1 || (long)width * height > 4_000_000)
                throw new ArgumentException("Rendered PDF page exceeds pixel limit.");
            var bytes = page.GetImage();
            if (bytes.Length != checked(width * height * 4))
                throw new ArgumentException("PDF renderer returned invalid pixels.");
            using var raw = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            Marshal.Copy(bytes, 0, raw.GetPixels(), bytes.Length);
            var result = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
            using var canvas = new SKCanvas(result);
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(raw, 0, 0);
            return result;
        }
    }
}
