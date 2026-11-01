using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Pomelo.Core.Conversion;
using Pomelo.Core.Formats;
using Pomelo.Core.Imaging;
using Pomelo.Core.IO;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Pomelo.Core.Documents;

/// <summary>
/// Renders each PDF page to an image with Windows' built-in PDF engine (Windows.Data.Pdf),
/// at 200 DPI on white. One page → "doc.png"; several → a folder "doc" holding
/// "página 01.png"…, built as "~pomelo-*.part" and renamed only once every page is written.
/// </summary>
public static class PdfPages
{
    public const double RenderDpi = 200;

    /// <summary>Keeps posters and CAD sheets within memory and image-viewer limits.</summary>
    public const int MaxPageSide = 10_000;

    private const int ErrorWrongPassword = unchecked((int)0x8007052B);

    /// <returns>The created image, or the folder holding one image per page.</returns>
    public static string Convert(string pdfPath, FileFormat target, ConversionOptions options, CancellationToken cancellation = default)
    {
        if (target != ImageFormats.Jpeg && target != ImageFormats.Png)
            throw new ArgumentOutOfRangeException(nameof(target), target.Name, "PDF vira JPG ou PNG.");
        string source = Path.GetFullPath(pdfPath);
        string directory = options.OutputDirectory ?? Path.GetDirectoryName(source)!;
        if (!Directory.Exists(directory))
            throw new ConversionException($"A pasta de saída não existe: {directory}");
        string name = Path.GetFileNameWithoutExtension(source);

        PdfDocument document = Open(source);
        if (document.PageCount == 1)
            return UniqueFileWriter.Write(directory, name, target.Extension, Render(document, 0, target, options));

        string temp = Path.Combine(directory, $"~pomelo-{Guid.NewGuid():N}.part");
        Directory.CreateDirectory(temp);
        try
        {
            int digits = Math.Max(2, document.PageCount.ToString().Length);
            for (uint i = 0; i < document.PageCount; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                string page = $"página {(i + 1).ToString().PadLeft(digits, '0')}{target.Extension}";
                File.WriteAllBytes(Path.Combine(temp, page), Render(document, i, target, options));
            }
            cancellation.ThrowIfCancellationRequested();
            return UniqueFileWriter.MoveFolderIntoPlace(temp, directory, name);
        }
        catch
        {
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException) { /* Best effort: a "~pomelo-*.part" folder may remain. */ }
            throw;
        }
    }

    /// <summary>Page count as Windows' own PDF engine sees it (an independent check on files PDFium wrote).</summary>
    /// <exception cref="ConversionException">Windows cannot read the file.</exception>
    internal static uint CountPages(string path) => Open(path).PageCount;

    private static PdfDocument Open(string path)
    {
        try
        {
            StorageFile file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            var document = PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult();
            if (document.PageCount == 0)
                throw new ConversionException("O PDF não tem páginas.");
            return document;
        }
        catch (Exception ex) when (ex.HResult == ErrorWrongPassword)
        {
            throw new ConversionException("O PDF é protegido por senha.", ex);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException or FileNotFoundException)
        {
            throw new ConversionException("O Windows não consegue ler este PDF (arquivo danificado ou não é um PDF).", ex);
        }
    }

    private static byte[] Render(PdfDocument document, uint index, FileFormat target, ConversionOptions options)
    {
        using PdfPage page = document.GetPage(index);
        // page.Size is in DIPs (1/96 inch).
        double scale = RenderDpi / 96;
        double width = page.Size.Width * scale, height = page.Size.Height * scale;
        double fit = Math.Min(1, MaxPageSide / Math.Max(width, height));
        var render = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(width * fit)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(height * fit)),
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
        };

        using var stream = new InMemoryRandomAccessStream();
        page.RenderToStreamAsync(stream, render).AsTask().GetAwaiter().GetResult(); // PNG by default.
        using var png = new MemoryStream();
        stream.AsStreamForRead().CopyTo(png);
        if (target == ImageFormats.Png)
            return png.ToArray();

        png.Position = 0;
        var frame = BitmapDecoder.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        return ImageConverter.Encode(frame, target, options);
    }
}
