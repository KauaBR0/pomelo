using Pomelo.Core.Formats;
using Pomelo.Core.Media;

namespace Pomelo.Core.Conversion;

/// <summary>Something the wheel can do to each dropped file: a conversion or a tool.</summary>
public interface IFileOperation
{
    /// <summary>Short wheel sector text.</summary>
    string Label { get; }
    /// <summary>Shown in the wheel hub while the sector is hovered.</summary>
    string HubText { get; }
    /// <summary>Notification title once done, e.g. "Convertido para JPG".</summary>
    string DoneTitle { get; }

    /// <summary>Creates a new file beside the original or in the configured folder; never modifies the original.</summary>
    /// <returns>The path of the created file.</returns>
    string Apply(string path, ConversionOptions options, FfmpegInfo? ffmpeg, CancellationToken cancellation);
}

public sealed record ConvertOperation(FileFormat Target) : IFileOperation
{
    public string Label => Target.Name;
    public string HubText => $"→ {Target.Name}";
    public string DoneTitle => $"Convertido para {Target.Name}";

    public string Apply(string path, ConversionOptions options, FfmpegInfo? ffmpeg, CancellationToken cancellation) =>
        FileConverter.Convert(path, Target, options, ffmpeg, cancellation);
}
