using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pomelo.Core.Conversion;
using Pomelo.Core.Formats;
using Pomelo.Core.Imaging;
using Pomelo.Core.IO;

namespace Pomelo.Core.Documents;

/// <summary>
/// Combines images into one PDF, one image per page, in the given order. Each page is the
/// image's own size (from its DPI, 96 when unknown), so nothing is cropped or stretched.
/// Photos are stored as JPEG; everything else losslessly (Flate). The PDF is written to a
/// "~pomelo-*.part" file and only renamed once complete.
/// </summary>
public static class PdfWriter
{
    /// <summary>PDF viewers reject pages larger than 200 inches (14 400 pt).</summary>
    private const double MaxPageSide = 14_400;

    /// <returns>The path of the created PDF, named after the first image.</returns>
    public static string Combine(IReadOnlyList<string> images, ConversionOptions options, CancellationToken cancellation = default)
    {
        if (images.Count == 0)
            throw new ArgumentException("Nenhuma imagem.", nameof(images));
        string first = Path.GetFullPath(images[0]);
        string directory = options.OutputDirectory ?? Path.GetDirectoryName(first)!;
        if (!Directory.Exists(directory))
            throw new ConversionException($"A pasta de saída não existe: {directory}");

        string temp = Path.Combine(directory, $"~pomelo-{Guid.NewGuid():N}.part");
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                Write(file, images, options, cancellation);
            return UniqueFileWriter.MoveIntoPlace(temp, directory, Path.GetFileNameWithoutExtension(first), DocumentFormats.Pdf.Extension);
        }
        finally
        {
            File.Delete(temp); // No-op after a successful move.
        }
    }

    // Object numbers: 1 catalog, 2 page tree, then per page i: 3+3i page, 4+3i contents, 5+3i image.
    private static void Write(Stream output, IReadOnlyList<string> images, ConversionOptions options, CancellationToken cancellation)
    {
        var pdf = new PdfStream(output);
        pdf.Raw("%PDF-1.4\n%\xE2\xE3\xCF\xD3\n"u8);

        for (int i = 0; i < images.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            int page = 3 + 3 * i, contents = page + 1, picture = page + 2;
            var (image, width, height) = LoadPage(images[i], options);

            pdf.Object(page, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Num(width)} {Num(height)}] " +
                             $"/Resources << /XObject << /Im0 {picture} 0 R >> /ProcSet [/PDF /ImageC] >> /Contents {contents} 0 R >>");
            pdf.Stream(contents, "", Encoding.ASCII.GetBytes($"q {Num(width)} 0 0 {Num(height)} 0 0 cm /Im0 Do Q"));
            pdf.Stream(picture, $"/Type /XObject /Subtype /Image /Width {image.PixelWidth} /Height {image.PixelHeight} " +
                                $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /{image.Filter}", image.Data);
        }

        pdf.Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        string kids = string.Join(' ', Enumerable.Range(0, images.Count).Select(i => $"{3 + 3 * i} 0 R"));
        pdf.Object(2, $"<< /Type /Pages /Kids [{kids}] /Count {images.Count} >>");
        pdf.Finish(objectCount: 2 + 3 * images.Count);
    }

    private sealed record PageImage(int PixelWidth, int PixelHeight, string Filter, byte[] Data);

    private static (PageImage Image, double Width, double Height) LoadPage(string path, ConversionOptions options)
    {
        BitmapSource bitmap;
        try
        {
            bitmap = ImageConverter.LoadOpaqueSrgb(path);
        }
        catch (ConversionException ex)
        {
            throw new ConversionException($"{Path.GetFileName(path)}: {ex.Message}", ex);
        }

        int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
        double width = w * 72 / Dpi(bitmap.DpiX), height = h * 72 / Dpi(bitmap.DpiY);
        double shrink = Math.Min(1, MaxPageSide / Math.Max(width, height));

        // Photos stay JPEG-sized; screenshots, drawings and transparency-flattened art stay sharp.
        PageImage image = ImageFormats.SameOrClosest(path) == ImageFormats.Jpeg
            ? new(w, h, "DCTDecode", ImageConverter.Encode(bitmap, ImageFormats.Jpeg, options))
            : new(w, h, "FlateDecode", Deflate(Rgb(bitmap)));
        return (image, width * shrink, height * shrink);
    }

    /// <summary>Bgr32 → packed RGB rows, as PDF's DeviceRGB expects.</summary>
    private static byte[] Rgb(BitmapSource bgr32)
    {
        int w = bgr32.PixelWidth, h = bgr32.PixelHeight, stride = checked(w * 4);
        byte[] bgrx = new byte[checked(stride * h)];
        bgr32.CopyPixels(bgrx, stride, 0);
        byte[] rgb = new byte[checked(w * h * 3)];
        for (int s = 0, d = 0; s < bgrx.Length; s += 4, d += 3)
        {
            rgb[d] = bgrx[s + 2];
            rgb[d + 1] = bgrx[s + 1];
            rgb[d + 2] = bgrx[s];
        }
        return rgb;
    }

    private static byte[] Deflate(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(data);
        return buffer.ToArray();
    }

    /// <summary>Unknown or absurd DPI (0, 1, 100 000…) falls back to 96, the screen default.</summary>
    private static double Dpi(double dpi) => dpi is >= 36 and <= 2400 ? dpi : 96;

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Writes numbered objects and the cross-reference table that locates them.</summary>
    private sealed class PdfStream(Stream output)
    {
        private readonly Dictionary<int, long> _offsets = [];

        public void Raw(ReadOnlySpan<byte> bytes) => output.Write(bytes);

        private void Ascii(string text) => output.Write(Encoding.ASCII.GetBytes(text));

        public void Object(int number, string body)
        {
            _offsets[number] = output.Position;
            Ascii($"{number} 0 obj\n{body}\nendobj\n");
        }

        public void Stream(int number, string dictionary, byte[] data)
        {
            _offsets[number] = output.Position;
            Ascii($"{number} 0 obj\n<< {dictionary} /Length {data.Length} >>\nstream\n");
            output.Write(data);
            Ascii("\nendstream\nendobj\n");
        }

        public void Finish(int objectCount)
        {
            long xref = output.Position;
            var table = new StringBuilder($"xref\n0 {objectCount + 1}\n0000000000 65535 f \n");
            for (int n = 1; n <= objectCount; n++)
                table.Append(CultureInfo.InvariantCulture, $"{_offsets[n]:D10} 00000 n \n");
            table.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objectCount + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            Ascii(table.ToString());
        }
    }
}
