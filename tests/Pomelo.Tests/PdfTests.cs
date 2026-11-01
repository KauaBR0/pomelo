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

    [Fact]
    public void Images_become_one_pdf_with_a_page_per_image_in_drop_order_sized_by_dpi()
    {
        string red = Save("red.png", 120, 80, 96, 0xFFFF0000, new PngBitmapEncoder());
        string blue = Save("blue.jpg", 600, 300, 300, 0xFF0000FF, new JpegBitmapEncoder());

        string pdf = PdfWriter.Combine([red, blue], _options);

        Assert.Equal(Path.Combine(_dir, "red.pdf"), pdf);
        var document = Open(pdf);
        Assert.Equal(2u, document.PageCount);
        AssertSize(120, 80, document.GetPage(0).Size); // 96 DPI: 1 px = 1 DIP.
        AssertSize(192, 96, document.GetPage(1).Size); // 300 DPI: 600 px = 2 inches.

        string pages = PdfPages.Convert(pdf, ImageFormats.Png, _options);
        Assert.Equal(Path.Combine(_dir, "red"), pages);
        Assert.Equal(["página 01.png", "página 02.png"], Directory.GetFiles(pages).Select(Path.GetFileName).Order());
        AssertNear(0xFF0000, Center(Path.Combine(pages, "página 01.png")));
        AssertNear(0x0000FF, Center(Path.Combine(pages, "página 02.png")));
        Assert.True(File.Exists(red) && File.Exists(blue));
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
        Assert.Equal((200, 100), (frame.PixelWidth, frame.PixelHeight)); // 1 × 0.5 inch at 200 DPI.
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

    private static void AssertNear(uint expected, uint actual)
    {
        for (int shift = 0; shift <= 16; shift += 8)
            Assert.InRange((int)(actual >> shift & 0xFF), (int)(expected >> shift & 0xFF) - 12, (int)(expected >> shift & 0xFF) + 12);
    }
}
