using Pomelo.Core.Conversion;
using Pomelo.Core.Formats;
using Pomelo.Core.Media;

namespace Pomelo.Core.Tools;

/// <param name="Hint">Why nothing is offered, when the user can fix it (e.g. install FFmpeg).</param>
public sealed record ToolChoices(IReadOnlyList<FileTool> Tools, string? Hint);

public static class ToolCatalog
{
    /// <summary>
    /// Tools for the dropped files. Every file must be the same kind (all images, all audio or
    /// all video); folders, missing or unknown files disable the wheel.
    /// </summary>
    public static ToolChoices For(IReadOnlyCollection<string> paths, FfmpegInfo? ffmpeg)
    {
        var kinds = paths.Select(p => File.Exists(p) ? InputKind.Of(p) : null).Distinct().ToList();
        if (kinds is not [MediaKind kind] || kind == MediaKind.Document)
            return new([], null); // No document tools yet.

        if (kind == MediaKind.Image)
            return new(FileTools.ForImages, null);
        if (ffmpeg is null)
            return new([], ConversionCatalog.FfmpegRequiredHint);

        var tools = kind == MediaKind.Audio ? FileTools.ForAudio : FileTools.ForVideo;
        return new(tools.Where(t => t.IsAvailable(ffmpeg)).ToList(), null);
    }
}
