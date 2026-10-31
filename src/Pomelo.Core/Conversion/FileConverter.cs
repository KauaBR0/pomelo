using Pomelo.Core.Formats;
using Pomelo.Core.Imaging;
using Pomelo.Core.Media;

namespace Pomelo.Core.Conversion;

/// <summary>Routes a conversion to WIC (images) or FFmpeg (audio/video) by the target's kind.</summary>
public static class FileConverter
{
    /// <returns>The path of the created file.</returns>
    public static string Convert(string sourcePath, FileFormat target, ConversionOptions options, FfmpegInfo? ffmpeg,
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (target.Kind == MediaKind.Image)
            return ImageConverter.Convert(sourcePath, target, options);
        if (ffmpeg is null)
            throw new ConversionException("Áudio e vídeo precisam do FFmpeg. Configure-o na janela do Pomelo.");
        return MediaConverter.Convert(sourcePath, target, options, ffmpeg, cancellation);
    }
}
