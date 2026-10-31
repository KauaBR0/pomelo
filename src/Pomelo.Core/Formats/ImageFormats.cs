namespace Pomelo.Core.Formats;

public static class ImageFormats
{
    public static readonly FileFormat Jpeg = new("JPG", ".jpg", [".jpg", ".jpeg", ".jfif", ".jpe"], MediaKind.Image);
    public static readonly FileFormat Png = new("PNG", ".png", [".png"], MediaKind.Image);
    public static readonly FileFormat Tiff = new("TIFF", ".tiff", [".tif", ".tiff"], MediaKind.Image);
    public static readonly FileFormat Bmp = new("BMP", ".bmp", [".bmp", ".dib"], MediaKind.Image);
    public static readonly FileFormat Gif = new("GIF", ".gif", [".gif"], MediaKind.Image);

    /// <summary>Wheel order.</summary>
    public static readonly IReadOnlyList<FileFormat> Outputs = [Jpeg, Png, Tiff, Bmp, Gif];

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
    /// The format an edited copy keeps: the input's own format when Pomelo can write it,
    /// otherwise PNG for formats that may carry transparency (WebP, ICO) and JPG for photos (HEIC, AVIF…).
    /// </summary>
    public static FileFormat SameOrClosest(string path)
    {
        if (Outputs.FirstOrDefault(f => f.Matches(path)) is { } same)
            return same;
        return Path.GetExtension(path).ToLowerInvariant() is ".webp" or ".ico" ? Png : Jpeg;
    }
}
