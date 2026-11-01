using Pomelo.Core.Documents;
using Pomelo.Core.Formats;
using Pomelo.Core.Imaging;
using Pomelo.Core.Media;

namespace Pomelo.Core.Conversion;

/// <summary>Routes a conversion to WIC (images), Windows' PDF engine (PDF pages) or FFmpeg (audio/video).</summary>
public static class FileConverter
{
    /// <returns>The path of the created file (or folder, for a PDF with several pages).</returns>
    public static string Convert(string sourcePath, FileFormat target, ConversionOptions options, FfmpegInfo? ffmpeg,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (DocumentFormats.Pdf.Matches(sourcePath))
            return PdfPages.Convert(sourcePath, target, options, cancellation);
        if (target == DocumentFormats.Pdf)
            return PdfWriter.Combine([sourcePath], options, cancellation);
        if (target.Kind == MediaKind.Image)
            return ImageConverter.Convert(sourcePath, target, options);
        if (ffmpeg is null)
            throw new ConversionException("Áudio e vídeo precisam do FFmpeg. Configure-o na janela do Pomelo.");
        return MediaConverter.Convert(sourcePath, target, options, ffmpeg, cancellation);
    }
}
