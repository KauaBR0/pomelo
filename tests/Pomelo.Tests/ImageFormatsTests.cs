using Pomelo.Core.Formats;

namespace Pomelo.Tests;

public sealed class ImageFormatsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pomelo-formats-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, []);
        return path;
    }

    private static string[] Names(IReadOnlyList<ImageFormat> formats) => formats.Select(f => f.Name).ToArray();

    [Fact]
    public void Same_format_inputs_do_not_offer_their_own_format()
    {
        Assert.Equal(["JPG", "TIFF", "BMP", "GIF"], Names(ImageFormats.TargetsFor([Touch("a.png"), Touch("b.PNG")])));
        Assert.Equal(["PNG", "TIFF", "BMP", "GIF"], Names(ImageFormats.TargetsFor([Touch("c.jpeg"), Touch("d.jpg")])));
    }

    [Fact]
    public void Mixed_formats_offer_every_output()
    {
        Assert.Equal(Names(ImageFormats.Outputs), Names(ImageFormats.TargetsFor([Touch("a.png"), Touch("b.jpg")])));
    }

    [Fact]
    public void Any_non_image_folder_or_missing_file_disables_conversion()
    {
        string image = Touch("a.png");
        Assert.Empty(ImageFormats.TargetsFor([image, Touch("notes.txt")]));
        Assert.Empty(ImageFormats.TargetsFor([image, Directory.CreateDirectory(Path.Combine(_dir, "folder.png")).FullName]));
        Assert.Empty(ImageFormats.TargetsFor([image, Path.Combine(_dir, "gone.png")]));
        Assert.Empty(ImageFormats.TargetsFor([]));
    }
}
