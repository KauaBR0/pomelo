using Pomelo.Core.Formats;

namespace Pomelo.Core.Media;

/// <param name="Muxer">FFmpeg "-f" container (outputs go to a temp file without the real extension).</param>
/// <param name="Encoders">Encoders the FFmpeg build must have; formats lacking one are not offered.</param>
/// <param name="OutputArguments">Options placed between the input and the output.</param>
public sealed record MediaRecipe(string Muxer, IReadOnlyList<string> Encoders, IReadOnlyList<string> OutputArguments);

public static class MediaRecipes
{
    /// <summary>H.264 and most players need yuv420p, which needs even dimensions.</summary>
    private const string EvenSize = "scale=trunc(iw/2)*2:trunc(ih/2)*2";

    private const string GifFilter =
        "fps=12,scale='min(640,iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4";

    // First video stream + first audio stream if any: skips cover art, subtitles and data tracks.
    private static readonly string[] VideoMap = ["-map", "0:v:0", "-map", "0:a:0?", "-sn", "-dn"];
    private static readonly string[] AudioMap = ["-map", "0:a:0", "-vn", "-sn", "-dn"];

    private static readonly string[] H264Aac =
        [.. VideoMap, "-vf", EvenSize, "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
         "-c:a", "aac", "-b:a", "192k"];

    public static MediaRecipe For(FileFormat format) => format switch
    {
        _ when format == MediaFormats.Mp3 => new("mp3", ["libmp3lame"], [.. AudioMap, "-c:a", "libmp3lame", "-q:a", "2"]),
        _ when format == MediaFormats.M4a => new("ipod", ["aac"], [.. AudioMap, "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]),
        _ when format == MediaFormats.Wav => new("wav", ["pcm_s16le"], [.. AudioMap, "-c:a", "pcm_s16le"]),
        _ when format == MediaFormats.Flac => new("flac", ["flac"], [.. AudioMap, "-c:a", "flac"]),
        _ when format == MediaFormats.Ogg => new("ogg", ["libvorbis"], [.. AudioMap, "-c:a", "libvorbis", "-q:a", "5"]),
        _ when format == MediaFormats.Opus => new("opus", ["libopus"], [.. AudioMap, "-c:a", "libopus", "-b:a", "128k"]),
        _ when format == MediaFormats.Aiff => new("aiff", ["pcm_s16be"], [.. AudioMap, "-c:a", "pcm_s16be"]),
        _ when format == MediaFormats.Wma => new("asf", ["wmav2"], [.. AudioMap, "-c:a", "wmav2", "-b:a", "192k"]),

        _ when format == MediaFormats.Mp4 => new("mp4", ["libx264", "aac"], [.. H264Aac, "-movflags", "+faststart"]),
        _ when format == MediaFormats.Mov => new("mov", ["libx264", "aac"], [.. H264Aac, "-movflags", "+faststart"]),
        _ when format == MediaFormats.Mkv => new("matroska", ["libx264", "aac"], H264Aac),
        _ when format == MediaFormats.WebM => new("webm", ["libvpx-vp9", "libopus"],
            [.. VideoMap, "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-deadline", "good", "-cpu-used", "4",
             "-pix_fmt", "yuv420p", "-c:a", "libopus", "-b:a", "128k"]),
        _ when format == MediaFormats.Avi => new("avi", ["mpeg4", "libmp3lame"],
            [.. VideoMap, "-vf", EvenSize, "-c:v", "mpeg4", "-q:v", "3", "-pix_fmt", "yuv420p", "-c:a", "libmp3lame", "-q:a", "2"]),
        _ when format == MediaFormats.Wmv => new("asf", ["wmv2", "wmav2"],
            [.. VideoMap, "-vf", EvenSize, "-c:v", "wmv2", "-q:v", "3", "-c:a", "wmav2", "-b:a", "192k"]),
        _ when format == MediaFormats.AnimatedGif => new("gif", ["gif"],
            ["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", GifFilter, "-loop", "0"]),

        _ => throw new ArgumentOutOfRangeException(nameof(format), format.Name, "Formato sem receita FFmpeg."),
    };
}
