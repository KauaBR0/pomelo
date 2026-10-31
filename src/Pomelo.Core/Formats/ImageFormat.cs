namespace Pomelo.Core.Formats;

/// <summary>An image format Pomelo can write, plus the extensions that already are that format.</summary>
public sealed record ImageFormat(string Name, string Extension, IReadOnlyList<string> Aliases)
{
    public bool Matches(string path) =>
        Aliases.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}

public static class ImageFormats
{
    public static readonly ImageFormat Jpeg = new("JPG", ".jpg", [".jpg", ".jpeg", ".jfif", ".jpe"]);
    public static readonly ImageFormat Png = new("PNG", ".png", [".png"]);
    public static readonly ImageFormat Tiff = new("TIFF", ".tiff", [".tif", ".tiff"]);
    public static readonly ImageFormat Bmp = new("BMP", ".bmp", [".bmp", ".dib"]);
    public static readonly ImageFormat Gif = new("GIF", ".gif", [".gif"]);

    /// <summary>Wheel order.</summary>
    public static readonly IReadOnlyList<ImageFormat> Outputs = [Jpeg, Png, Tiff, Bmp, Gif];

    /// <summary>
    /// Extensions decoded by WIC. WebP/HEIC/AVIF depend on Microsoft Store codec extensions;
    /// a missing codec is reported per file by <see cref="Imaging.ImageConverter"/>.
    /// </summary>
    private static readonly HashSet<string> Inputs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".jpe", ".png", ".tif", ".tiff", ".bmp", ".dib", ".gif",
        ".ico", ".jxr", ".wdp", ".hdp", ".webp", ".heic", ".heif", ".avif",
    };

    public static bool IsInput(string path) => Inputs.Contains(Path.GetExtension(path));

    /// <summary>
    /// Targets valid for every path. Empty when any path is not a readable image file.
    /// A target is omitted only when every input already has that format.
    /// </summary>
    public static IReadOnlyList<ImageFormat> TargetsFor(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0 || !paths.All(p => IsInput(p) && File.Exists(p)))
            return [];
        return Outputs.Where(f => !paths.All(f.Matches)).ToList();
    }
}
