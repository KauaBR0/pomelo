using Pomelo.Core.Conversion;
using Pomelo.Core.Formats;
using Pomelo.Core.Imaging;
using Pomelo.Core.Media;

namespace Pomelo.Core.Tools;

/// <summary>The tools wheel, per kind of input, in wheel order.</summary>
public static class FileTools
{
    // ---- Images (WIC). Re-encoding drops EXIF/GPS/XMP; orientation is baked into the pixels.

    public static readonly ImageTool Half = new("50%", "Reduzir 50%", "Reduzido 50%", "50%",
        ImageFormats.SameOrClosest, image => ImageConverter.Scale(image, 0.5));

    public static readonly ImageTool RotateRight = new("Girar ↻", "Girar 90° horário", "Girado", "girado",
        ImageFormats.SameOrClosest, image => ImageConverter.Rotate(image, 90));

    public static readonly ImageTool RotateLeft = new("Girar ↺", "Girar 90° anti-horário", "Girado", "girado",
        ImageFormats.SameOrClosest, image => ImageConverter.Rotate(image, 270));

    public static readonly ImageTool StripImage = new("Limpar", "Remover EXIF e GPS", "Metadados removidos", "sem metadados",
        ImageFormats.SameOrClosest, image => image);

    public const int CompressedImageMaxSide = 2048;
    public const int CompressedImageQuality = 70;

    public static readonly ImageTool CompressImage = new("Comprimir", "JPG menor, até 2048 px", "Comprimido", "comprimido",
        _ => ImageFormats.Jpeg, image => ImageConverter.FitWithin(image, CompressedImageMaxSide), CompressedImageQuality);

    // ---- Audio (FFmpeg)

    public static readonly MediaTool Normalize = new("Normalizar", "Normalizar volume", "Volume normalizado", "normalizado",
        MediaKind.Audio, [], path => SameAudio(path, "-af", "loudnorm=I=-16:TP=-1.5:LRA=11", "-ar", "48000"));

    public static readonly MediaTool Mono = new("Mono", "Converter para mono", "Convertido para mono", "mono",
        MediaKind.Audio, [], path => SameAudio(path, "-ac", "1"));

    public static readonly MediaTool CompressAudio = new("Comprimir", "MP3 128 kbps", "Comprimido", "comprimido",
        MediaKind.Audio, ["libmp3lame"],
        _ => (new MediaRecipe("mp3", ["libmp3lame"], [.. MediaRecipes.AudioMap, "-c:a", "libmp3lame", "-b:a", "128k"]), ".mp3"));

    public static readonly MediaTool StripAudio = new("Limpar", "Remover metadados e capa", "Metadados removidos", "sem metadados",
        MediaKind.Audio, [], path => StreamCopy(path, ["-map", "0:a", "-c", "copy", "-map_metadata", "-1", "-fflags", "+bitexact"]));

    // ---- Video (FFmpeg)

    /// <summary>Caps the shorter side at 1080 px (landscape or portrait), keeping even dimensions.</summary>
    private const string Max1080 =
        "scale='if(gt(iw,ih),-2,min(1080,trunc(iw/2)*2))':'if(gt(iw,ih),min(1080,trunc(ih/2)*2),-2)'";

    public static readonly MediaTool CompressVideo = new("Comprimir", "MP4 menor, até 1080p", "Comprimido", "comprimido",
        MediaKind.Video, ["libx264", "aac"],
        _ => (new MediaRecipe("mp4", ["libx264", "aac"],
            [.. MediaRecipes.VideoMap, "-vf", Max1080, "-c:v", "libx264", "-preset", "medium", "-crf", "28", "-pix_fmt", "yuv420p",
             "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart"]), ".mp4"));

    public static readonly MediaTool RotateVideo = new("Girar ↻", "Girar 90° horário", "Girado", "girado",
        MediaKind.Video, ["libx264", "aac"],
        _ => (new MediaRecipe("mp4", ["libx264", "aac"],
            [.. MediaRecipes.VideoMap, "-vf", "transpose=1," + MediaRecipes.EvenSize, "-c:v", "libx264", "-preset", "medium", "-crf", "20",
             "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]), ".mp4"));

    public static readonly MediaTool Mute = new("Sem áudio", "Remover o áudio", "Áudio removido", "sem áudio",
        MediaKind.Video, [], path => StreamCopy(path, ["-map", "0:v", "-c", "copy", "-an", "-sn", "-dn"]));

    public static readonly MediaTool StripVideo = new("Limpar", "Remover metadados", "Metadados removidos", "sem metadados",
        MediaKind.Video, [],
        path => StreamCopy(path, ["-map", "0:v?", "-map", "0:a?", "-map", "0:s?", "-c", "copy",
                                  "-map_metadata", "-1", "-map_chapters", "-1", "-fflags", "+bitexact"]));

    public static readonly IReadOnlyList<FileTool> ForImages = [Half, RotateRight, RotateLeft, StripImage, CompressImage];
    public static readonly IReadOnlyList<FileTool> ForAudio = [Normalize, Mono, CompressAudio, StripAudio];
    public static readonly IReadOnlyList<FileTool> ForVideo = [CompressVideo, RotateVideo, Mute, StripVideo];

    /// <summary>Re-encodes to the input's own audio format (M4A for raw AAC) with extra options.</summary>
    private static (MediaRecipe, string) SameAudio(string path, params string[] extra)
    {
        var format = MediaFormats.AudioOutputs.FirstOrDefault(f => f.Matches(path));
        var recipe = MediaRecipes.For(format ?? MediaFormats.M4a);
        string extension = format is null ? MediaFormats.M4a.Extension : Path.GetExtension(path).ToLowerInvariant();
        return (recipe with { OutputArguments = [.. recipe.OutputArguments, .. extra] }, extension);
    }

    /// <summary>Lossless: copies streams into the same container type.</summary>
    private static (MediaRecipe, string) StreamCopy(string path, string[] arguments)
    {
        string muxer = MediaRecipes.SameContainerMuxer(path)
            ?? throw new ConversionException($"Este tipo de arquivo ({Path.GetExtension(path)}) não é suportado por esta ferramenta.");
        return (new MediaRecipe(muxer, [], arguments), Path.GetExtension(path).ToLowerInvariant());
    }
}
