using Pomelo.Core.Formats;
using Pomelo.Core.Media;

namespace Pomelo.Core.Conversion;

/// <param name="Hint">Why nothing is offered, when the user can fix it (e.g. install FFmpeg).</param>
public sealed record ConversionChoices(IReadOnlyList<FileFormat> Targets, string? Hint);

public static class ConversionCatalog
{
    public const string FfmpegRequiredHint = "Requer FFmpeg";

    /// <summary>Wheel order for still images; PDF combines every dropped image into one file.</summary>
    private static readonly IReadOnlyList<FileFormat> ImageTargets = [.. ImageFormats.Outputs, DocumentFormats.Pdf];

    /// <summary>
    /// Targets valid for every path (intersection), in the first file's wheel order. A target
    /// is omitted when every input already has that format. Audio/video targets the FFmpeg
    /// build cannot encode are omitted. An animated GIF counts as silent video. Images can also
    /// become one PDF; a PDF's pages can become images. Any folder, missing or unknown file
    /// disables conversion.
    /// </summary>
    public static ConversionChoices For(IReadOnlyCollection<string> paths, FfmpegInfo? ffmpeg)
    {
        IReadOnlyList<FileFormat>? common = null;
        bool needsFfmpeg = false, hasNonMedia = false;

        foreach (string path in paths)
        {
            if (!File.Exists(path))
                return new([], null);

            IReadOnlyList<FileFormat> targets;
            bool animated = GifAnimation.IsAnimated(path);
            if (ImageFormats.IsInput(path) && !animated)
            {
                hasNonMedia = true;
                targets = ImageTargets;
            }
            else if (DocumentFormats.Pdf.Matches(path))
            {
                hasNonMedia = true;
                targets = DocumentFormats.PdfPageOutputs;
            }
            else if (animated || MediaFormats.IsVideoInput(path) || MediaFormats.IsAudioInput(path))
            {
                if (ffmpeg is null)
                {
                    needsFfmpeg = true;
                    continue;
                }
                var candidates = animated ? MediaFormats.AnimatedGifOutputs
                    : MediaFormats.IsVideoInput(path) ? MediaFormats.VideoOutputs : MediaFormats.AudioOutputs;
                targets = candidates.Where(f => ffmpeg.HasEncoders(MediaRecipes.For(f).Encoders)).ToList();
            }
            else
            {
                return new([], null);
            }

            common = common is null ? targets : common.Where(targets.Contains).ToList();
        }

        if (needsFfmpeg)
            return new([], hasNonMedia ? null : FfmpegRequiredHint); // Images/PDFs and media never share a target.
        if (common is null)
            return new([], null);
        return new(common.Where(f => !paths.All(f.Matches)).ToList(), null);
    }
}
