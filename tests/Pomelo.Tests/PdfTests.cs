using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pomelo.Core.Conversion;
using Pomelo.Core.Documents;
using Pomelo.Core.Formats;
using Windows.Data.Pdf;
using Windows.Storage;

namespace Pomelo.Tests;

public sealed class PdfTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pomelo-pdf-").FullName;
    private readonly ConversionOptions _options = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A4 in DIPs (1/96 inch), as Windows' PDF engine reports page sizes.</summary>
    private const double A4Short = 793.7, A4Long = 1122.5;

    [Fact]
    public void Images_become_one_a4_pdf_with_a_page_per_image_in_drop_order()
    {
        string red = Save("red.png", 120, 80, 96, 0xFFFF0000, new PngBitmapEncoder());
        string blue = Save("blue.jpg", 300, 600, 300, 0xFF0000FF, new JpegBitmapEncoder());

        string pdf = PdfWriter.Combine([red, blue], _options);

        Assert.Equal(Path.Combine(_dir, "red.pdf"), pdf);
        var document = Open(pdf);
        Assert.Equal(2u, document.PageCount);
        AssertSize(A4Long, A4Short, document.GetPage(0).Size); // Wide image: landscape.
        AssertSize(A4Short, A4Long, document.GetPage(1).Size); // Tall image: portrait.

        string pages = PdfPages.Convert(pdf, ImageFormats.Png, _options);
        Assert.Equal(Path.Combine(_dir, "red"), pages);
        Assert.Equal(["página 01.png", "página 02.png"], Directory.GetFiles(pages).Select(Path.GetFileName).Order());
        AssertNear(0xFF0000, Center(Path.Combine(pages, "página 01.png")));
        AssertNear(0x0000FF, Center(Path.Combine(pages, "página 02.png")));
        Assert.True(File.Exists(red) && File.Exists(blue));
    }

    [Fact]
    public void Small_images_keep_their_size_and_large_ones_shrink_into_the_margin_centred()
    {
        // 96 × 48 px at 96 DPI = 1 × 0.5 inch → 200 × 100 px when the page is rendered at 200 DPI.
        string small = Save("small.bmp", 96, 48, 96, 0xFF00A000, new BmpBitmapEncoder());
        // 3000 × 4000 px at 72 DPI ≈ 1 × 1.4 m: must shrink to the 1 cm margins of a portrait page.
        string large = Save("large.png", 3000, 4000, 72, 0xFFFF0000, new PngBitmapEncoder());

        string pages = PdfPages.Convert(PdfWriter.Combine([small, large], _options), ImageFormats.Png, _options);

        var (sx, sy, sw, sh, pw, ph) = Extent(Path.Combine(pages, "página 01.png"));
        Assert.InRange(sw, 198, 202);
        Assert.InRange(sh, 98, 102);
        Assert.InRange(sx - (pw - sx - sw), -2, 2); // Centred horizontally…
        Assert.InRange(sy - (ph - sy - sh), -2, 2); // …and vertically.

        var (lx, _, lw, _, lpw, _) = Extent(Path.Combine(pages, "página 02.png"));
        double margin = 1 / 2.54 * PdfPages.RenderDpi; // 1 cm in pixels.
        Assert.InRange(lx, margin - 2, margin + 2); // Width-limited: touches both side margins.
        Assert.InRange(lpw - lx - lw, margin - 2, margin + 2);
    }

    [Fact]
    public void Transparent_areas_become_white_on_the_page()
    {
        string clear = Save("clear.png", 40, 40, 96, 0x00FF0000, new PngBitmapEncoder());

        string page = PdfPages.Convert(PdfWriter.Combine([clear], _options), ImageFormats.Png, _options);

        AssertNear(0xFFFFFF, Center(page));
    }

    [Theory]
    [InlineData("PNG")]
    [InlineData("JPG")]
    public void A_single_page_becomes_one_image_at_200_dpi_beside_the_pdf(string name)
    {
        var target = DocumentFormats.PdfPageOutputs.Single(f => f.Name == name);
        string pdf = PdfWriter.Combine([Save("doc.bmp", 96, 48, 96, 0xFF00A000, new BmpBitmapEncoder())], _options);

        string image = PdfPages.Convert(pdf, target, _options);

        Assert.Equal(Path.Combine(_dir, "doc" + target.Extension), image);
        var frame = BitmapDecoder.Create(new Uri(image), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.InRange(frame.PixelWidth, 2337, 2340); // Landscape A4 (297 × 210 mm) at 200 DPI.
        Assert.InRange(frame.PixelHeight, 1652, 1655);
        AssertNear(0x00A000, Center(image));
    }

    [Fact]
    public void Existing_names_are_never_replaced()
    {
        string pdf = PdfWriter.Combine([Save("a.png", 10, 10, 96, 0xFF000000, new PngBitmapEncoder())], _options);
        string again = PdfWriter.Combine([Path.Combine(_dir, "a.png")], _options);
        Assert.Equal(Path.Combine(_dir, "a (1).pdf"), again);

        string twoPages = PdfWriter.Combine([Path.Combine(_dir, "a.png"), Path.Combine(_dir, "a.png")], _options);
        Directory.CreateDirectory(Path.Combine(_dir, "a (2)"));
        Assert.Equal(Path.Combine(_dir, "a (2) (1)"), PdfPages.Convert(twoPages, ImageFormats.Png, _options));
        Assert.True(File.Exists(pdf));
    }

    [Fact]
    public void A_broken_image_fails_the_whole_pdf_without_leaving_files()
    {
        string good = Save("good.png", 10, 10, 96, 0xFF000000, new PngBitmapEncoder());
        string broken = Path.Combine(_dir, "broken.png");
        File.WriteAllText(broken, "not an image");
        string[] before = Directory.GetFileSystemEntries(_dir);

        var error = Assert.Throws<ConversionException>(() => PdfWriter.Combine([good, broken], _options));

        Assert.StartsWith("broken.png:", error.Message);
        Assert.Equal(before, Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void A_damaged_pdf_reports_a_readable_error_without_leaving_files()
    {
        string fake = Path.Combine(_dir, "fake.pdf");
        File.WriteAllText(fake, "%PDF-1.4 not really");

        Assert.Throws<ConversionException>(() => PdfPages.Convert(fake, ImageFormats.Png, _options));
        Assert.Equal([fake], Directory.GetFileSystemEntries(_dir));
    }

    private string Save(string name, int width, int height, double dpi, uint argb, BitmapEncoder encoder)
    {
        uint[] pixels = Enumerable.Repeat(argb, width * height).ToArray();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null, pixels, width * 4)));
        string path = Path.Combine(_dir, name);
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }

    private static PdfDocument Open(string path) =>
        PdfDocument.LoadFromFileAsync(StorageFile.GetFileFromPathAsync(path).AsTask().Result).AsTask().Result;

    private static void AssertSize(double width, double height, Windows.Foundation.Size size)
    {
        Assert.InRange(size.Width, width - 0.5, width + 0.5);
        Assert.InRange(size.Height, height - 0.5, height + 0.5);
    }

    private static uint Center(string path)
    {
        var frame = new FormatConvertedBitmap(
            BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
        var pixel = new uint[1];
        frame.CopyPixels(new System.Windows.Int32Rect(frame.PixelWidth / 2, frame.PixelHeight / 2, 1, 1), pixel, 4, 0);
        return pixel[0] & 0xFFFFFF;
    }

    /// <summary>Bounding box of non-white pixels, plus the page size, in pixels.</summary>
    private static (int X, int Y, int Width, int Height, int PageWidth, int PageHeight) Extent(string path)
    {
        var frame = new FormatConvertedBitmap(
            BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
        int w = frame.PixelWidth, h = frame.PixelHeight;
        var pixels = new uint[w * h];
        frame.CopyPixels(pixels, w * 4, 0);
        int left = w, top = h, right = -1, bottom = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                uint p = pixels[y * w + x];
                if ((p & 0xFF) > 200 && (p >> 8 & 0xFF) > 200 && (p >> 16 & 0xFF) > 200)
                    continue; // White.
                (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
            }
        return (left, top, right - left + 1, bottom - top + 1, w, h);
    }

    private static void AssertNear(uint expected, uint actual)
    {
        for (int shift = 0; shift <= 16; shift += 8)
            Assert.InRange((int)(actual >> shift & 0xFF), (int)(expected >> shift & 0xFF) - 12, (int)(expected >> shift & 0xFF) + 12);
    }
}
