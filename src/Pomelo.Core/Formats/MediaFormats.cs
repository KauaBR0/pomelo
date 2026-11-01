namespace Pomelo.Core.Formats;

/// <summary>Audio and video formats handled through FFmpeg (see <see cref="Media.MediaRecipes"/>).</summary>
public static class MediaFormats
{
    public static readonly FileFormat Mp3 = new("MP3", ".mp3", [".mp3"], MediaKind.Audio);
    public static readonly FileFormat M4a = new("M4A", ".m4a", [".m4a"], MediaKind.Audio);
    public static readonly FileFormat Wav = new("WAV", ".wav", [".wav"], MediaKind.Audio);
    public static readonly FileFormat Flac = new("FLAC", ".flac", [".flac"], MediaKind.Audio);
    public static readonly FileFormat Ogg = new("OGG", ".ogg", [".ogg", ".oga"], MediaKind.Audio);
    public static readonly FileFormat Opus = new("OPUS", ".opus", [".opus"], MediaKind.Audio);
    public static readonly FileFormat Aiff = new("AIFF", ".aiff", [".aiff", ".aif"], MediaKind.Audio);
    public static readonly FileFormat Wma = new("WMA", ".wma", [".wma"], MediaKind.Audio);

    public static readonly FileFormat Mp4 = new("MP4", ".mp4", [".mp4", ".m4v"], MediaKind.Video);
    public static readonly FileFormat Mov = new("MOV", ".mov", [".mov"], MediaKind.Video);
    public static readonly FileFormat Mkv = new("MKV", ".mkv", [".mkv"], MediaKind.Video);
    public static readonly FileFormat WebM = new("WEBM", ".webm", [".webm"], MediaKind.Video);
    public static readonly FileFormat Avi = new("AVI", ".avi", [".avi"], MediaKind.Video);
    public static readonly FileFormat Wmv = new("WMV", ".wmv", [".wmv"], MediaKind.Video);
    /// <summary>Animated GIF from a video (distinct from the single-frame image GIF).</summary>
    public static readonly FileFormat AnimatedGif = new("GIF", ".gif", [".gif"], MediaKind.Video);

    /// <summary>Wheel order for audio inputs.</summary>
    public static readonly IReadOnlyList<FileFormat> AudioOutputs = [Mp3, M4a, Wav, Flac, Ogg, Opus, Aiff, Wma];

    /// <summary>Wheel order for video inputs; MP3 extracts the soundtrack.</summary>
    public static readonly IReadOnlyList<FileFormat> VideoOutputs = [Mp4, Mov, Mkv, WebM, Avi, Wmv, AnimatedGif, Mp3];

    /// <summary>Wheel order for animated GIF inputs: silent video, so no MP3 and no GIF.</summary>
    public static readonly IReadOnlyList<FileFormat> AnimatedGifOutputs = [Mp4, Mov, Mkv, WebM, Avi, Wmv];

    private static readonly HashSet<string> AudioInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".oga", ".opus", ".aiff", ".aif", ".wma",
    };

    private static readonly HashSet<string> VideoInputs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".mpg", ".mpeg",
        ".ts", ".mts", ".m2ts", ".3gp", ".ogv",
    };

    public static bool IsAudioInput(string path) => AudioInputs.Contains(Path.GetExtension(path));
    public static bool IsVideoInput(string path) => VideoInputs.Contains(Path.GetExtension(path));
}
