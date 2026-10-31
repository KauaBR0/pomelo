namespace Pomelo.Core.Media;

/// <summary>An FFmpeg executable that answered a probe, with the encoders its build includes.</summary>
public sealed record FfmpegInfo(string Path, string Version, IReadOnlySet<string> Encoders)
{
    public bool HasEncoders(IEnumerable<string> names) => names.All(Encoders.Contains);
}
