using System.Windows.Media.Imaging;
using Pomelo.Core.Conversion;
using Pomelo.Core.Formats;
using Pomelo.Core.Imaging;
using Pomelo.Core.Media;

namespace Pomelo.Core.Tools;

/// <summary>An edit applied to a copy of each file, saved as "name - suffix.ext".</summary>
public abstract class FileTool(string label, string hubText, string doneTitle, string suffix, MediaKind inputKind)
    : IFileOperation
{
    public string Label => label;
    public string HubText => hubText;
    public string DoneTitle => doneTitle;
    /// <summary>The kind of file this tool accepts.</summary>
    public MediaKind InputKind => inputKind;

    public abstract bool IsAvailable(FfmpegInfo? ffmpeg);

    public abstract string Apply(string path, ConversionOptions options, FfmpegInfo? ffmpeg, CancellationToken cancellation);

    protected string OutputName(string path) => $"{Path.GetFileNameWithoutExtension(path)} - {suffix}";
}

/// <param name="output">Format of the edited copy, chosen per input file.</param>
/// <param name="jpegQuality">Overrides the user's JPEG quality (e.g. for compression).</param>
public sealed class ImageTool(
    string label, string hubText, string doneTitle, string suffix,
    Func<string, FileFormat> output, Func<BitmapSource, BitmapSource> transform, int? jpegQuality = null)
    : FileTool(label, hubText, doneTitle, suffix, MediaKind.Image)
{
    public override bool IsAvailable(FfmpegInfo? ffmpeg) => true;

    public override string Apply(string path, ConversionOptions options, FfmpegInfo? ffmpeg, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var effective = jpegQuality is { } quality ? options with { JpegQuality = quality } : options;
        return ImageConverter.Transform(path, output(path), transform, OutputName(path), effective);
    }
}

/// <param name="encoders">Encoders needed regardless of the input; per-file needs are checked when applied.</param>
/// <param name="build">FFmpeg recipe and output extension for a given input file.</param>
public sealed class MediaTool(
    string label, string hubText, string doneTitle, string suffix, MediaKind inputKind,
    IReadOnlyList<string> encoders, Func<string, (MediaRecipe Recipe, string Extension)> build)
    : FileTool(label, hubText, doneTitle, suffix, inputKind)
{
    public override bool IsAvailable(FfmpegInfo? ffmpeg) => ffmpeg is not null && ffmpeg.HasEncoders(encoders);

    public override string Apply(string path, ConversionOptions options, FfmpegInfo? ffmpeg, CancellationToken cancellation)
    {
        if (ffmpeg is null)
            throw new ConversionException("Áudio e vídeo precisam do FFmpeg. Configure-o na janela do Pomelo.");
        var (recipe, extension) = build(path);
        return MediaConverter.Apply(path, recipe, OutputName(path), extension, options, ffmpeg, cancellation);
    }
}
