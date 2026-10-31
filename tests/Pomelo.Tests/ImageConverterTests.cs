using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pomelo.Core.Conversion;
using Pomelo.Core.Formats;
using Pomelo.Core.Imaging;

namespace Pomelo.Tests;

public sealed class ImageConverterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pomelo-tests-").FullName;

    public void Dispose()
    {
        // ColorContext has no Dispose; release its URI-backed profile handle before removing fixtures.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Jpeg_output_composites_transparency_onto_white()
    {
        // Left half fully transparent, right half opaque red.
        string png = SavePng("half.png", 16, 16, (x, _) => x < 8 ? 0x00000000u : 0xFFFF0000u);

        string jpg = ImageConverter.Convert(png, ImageFormats.Jpeg);

        uint[] px = ReadBgra(jpg, out int w, out _);
        AssertNear(0xFFFFFF, px[8 * w + 2]);
        AssertNear(0xFF0000, px[8 * w + 13]);
    }

    public static TheoryData<string> AllOutputs() => new(ImageFormats.Outputs.Select(f => f.Name));

    [Theory]
    [MemberData(nameof(AllOutputs))]
    public void Rgba_png_converts_to_every_output_with_same_size(string name)
    {
        var target = ImageFormats.Outputs.Single(f => f.Name == name);
        string png = SavePng("photo.png", 37, 21, (x, y) => (uint)(0x80 << 24 | x * 6 << 16 | y * 12 << 8 | 0x40));

        string output = ImageConverter.Convert(png, target);

        Assert.Equal(target.Extension, Path.GetExtension(output));
        var frame = BitmapDecoder.Create(new Uri(output), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal((37, 21), (frame.PixelWidth, frame.PixelHeight));
    }

    [Fact]
    public void Exif_orientation_is_baked_into_pixels()
    {
        // 20x10 white image with a black marker at the top-left; orientation 6 = rotate 90° clockwise.
        var source = Bitmap(20, 10, (x, y) => x < 3 && y < 3 ? 0xFF000000u : 0xFFFFFFFFu);
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
        var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        string jpg = Path.Combine(_dir, "phone.jpg");
        using (var file = File.Create(jpg))
            encoder.Save(file);

        string png = ImageConverter.Convert(jpg, ImageFormats.Png);

        uint[] px = ReadBgra(png, out int w, out int h);
        Assert.Equal((10, 20), (w, h));
        AssertNear(0x000000, px[1 * w + (w - 2)]); // marker now top-right
        AssertNear(0xFFFFFF, px[1 * w + 1]);
    }

    [Fact]
    public void Existing_output_is_never_replaced()
    {
        string png = SavePng("a.png", 4, 4, (_, _) => 0xFF00FF00u);
        string existing = Path.Combine(_dir, "a.jpg");
        File.WriteAllText(existing, "keep me");

        string output = ImageConverter.Convert(png, ImageFormats.Jpeg);

        Assert.Equal(Path.Combine(_dir, "a (1).jpg"), output);
        Assert.Equal("keep me", File.ReadAllText(existing));
    }

    [Fact]
    public void Output_directory_option_is_used_and_keeps_the_original_name()
    {
        string png = SavePng("vacation.png", 4, 4, (_, _) => 0xFF00FF00u);
        string elsewhere = Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;

        string output = ImageConverter.Convert(png, ImageFormats.Jpeg, new ConversionOptions(elsewhere));

        Assert.Equal(Path.Combine(elsewhere, "vacation.jpg"), output);
    }

    [Fact]
    public void Missing_output_directory_fails_without_writing_anything()
    {
        string png = SavePng("a.png", 4, 4, (_, _) => 0xFF00FF00u);

        Assert.Throws<ConversionException>(() =>
            ImageConverter.Convert(png, ImageFormats.Jpeg, new ConversionOptions(Path.Combine(_dir, "gone"))));
        Assert.Equal([png], Directory.GetFiles(_dir));
    }

    [Fact]
    public void Jpeg_quality_option_changes_the_encoding()
    {
        var noise = new Random(7);
        string png = SavePng("noise.png", 128, 128, (_, _) => 0xFF000000u | (uint)noise.Next(0x1000000));

        long low = new FileInfo(ImageConverter.Convert(png, ImageFormats.Jpeg, new ConversionOptions(JpegQuality: 20))).Length;
        long high = new FileInfo(ImageConverter.Convert(png, ImageFormats.Jpeg, new ConversionOptions(JpegQuality: 95))).Length;

        Assert.True(low * 2 < high, $"quality 20 = {low} bytes, quality 95 = {high} bytes");
    }

    [Fact]
    public void Multi_page_input_is_rejected_without_writing_anything()
    {
        var encoder = new TiffBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Bitmap(4, 4, (_, _) => 0xFFFFFFFFu)));
        encoder.Frames.Add(BitmapFrame.Create(Bitmap(4, 4, (_, _) => 0xFF000000u)));
        string tiff = Path.Combine(_dir, "pages.tiff");
        using (var file = File.Create(tiff))
            encoder.Save(file);

        Assert.Throws<ConversionException>(() => ImageConverter.Convert(tiff, ImageFormats.Png));
        Assert.Equal([tiff], Directory.GetFiles(_dir));
    }

    [Fact]
    public void Corrupt_input_reports_a_conversion_error_without_writing_anything()
    {
        string fake = Path.Combine(_dir, "fake.png");
        File.WriteAllText(fake, "not an image");

        Assert.Throws<ConversionException>(() => ImageConverter.Convert(fake, ImageFormats.Jpeg));
        Assert.Equal([fake], Directory.GetFiles(_dir));
    }

    [Theory]
    [InlineData("JPG")]
    [InlineData("PNG")]
    public void Cmyk_profile_is_converted_not_embedded_into_rgb_pixels(string name)
    {
        string profilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "spool", "drivers", "color", "RSWOP.icm");
        var profile = new ColorContext(new Uri(profilePath));
        byte[] pixels = Enumerable.Repeat(new byte[] { 255, 0, 0, 0 }, 16 * 16).SelectMany(p => p).ToArray();
        var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Cmyk32, null, pixels, 16 * 4);
        string source = SaveProfiledTiff("print.tiff", bitmap, profile);

        string output = ImageConverter.Convert(source, ImageFormats.Outputs.Single(f => f.Name == name));

        var frame = BitmapDecoder.Create(new Uri(output), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Assert.True(frame.ColorContexts is null || frame.ColorContexts.Count == 0);
        // Independent LittleCMS (Pillow ImageCms), RSWOP.icm perceptual cyan -> sRGB.
        AssertNear(0x009FD7, ReadBgra(output, out _, out _)[0]);
    }

    [Theory]
    [InlineData("BMP")]
    [InlineData("GIF")]
    public void Indexed_rgb_with_a_profile_converts_to_unprofiled_outputs(string name)
    {
        var palette = new BitmapPalette([Colors.Red, Colors.Blue]);
        byte[] pixels = Enumerable.Repeat(new byte[] { 0, 1 }, 8 * 16).SelectMany(p => p).ToArray();
        var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Indexed8, palette, pixels, 16);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, null, new([new ColorContext(PixelFormats.Bgra32)])));
        string source = Path.Combine(_dir, "indexed.png");
        using (var file = File.Create(source))
            encoder.Save(file);

        string output = ImageConverter.Convert(source, ImageFormats.Outputs.Single(f => f.Name == name));

        uint[] decoded = ReadBgra(output, out _, out _);
        AssertNear(0xFF0000, decoded[0]);
        AssertNear(0x0000FF, decoded[1]);
    }

    [Theory]
    [InlineData("JPG")]
    [InlineData("PNG")]
    public void Gray_profile_is_converted_before_rgb_encoding(string name)
    {
        string profilePath = Path.Combine(_dir, "gray.icc");
        File.WriteAllBytes(profilePath, GrayProfile());
        var bitmap = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Gray8, null,
            Enumerable.Repeat((byte)128, 256).ToArray(), 16);
        string source = SaveProfiledTiff("gray.tiff", bitmap, new ColorContext(new Uri(profilePath)));

        string output = ImageConverter.Convert(source, ImageFormats.Outputs.Single(f => f.Name == name));

        var frame = BitmapDecoder.Create(new Uri(output), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        Assert.True(frame.ColorContexts is null || frame.ColorContexts.Count == 0);
        AssertNear(0x818181, ReadBgra(output, out _, out _)[0]);
    }

    private string SaveProfiledTiff(string name, BitmapSource bitmap, ColorContext profile)
    {
        var encoder = new TiffBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, null, new([profile])));
        string path = Path.Combine(_dir, name);
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }

    // Minimal ICC v2 gray display profile: D50 white point and gamma 2.2, no external fixture.
    private static byte[] GrayProfile()
    {
        byte[] profile = new byte[336];
        void Number(int offset, uint value) =>
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(offset, 4), value);
        void Text(int offset, string value) => System.Text.Encoding.ASCII.GetBytes(value).CopyTo(profile, offset);
        Number(0, 336);
        Number(8, 0x02100000);
        Text(12, "mntrGRAYXYZ ");
        profile[24] = 7; profile[25] = 234; // 2026-01-01 (ICC requires a valid creation date).
        profile[27] = 1; profile[29] = 1;
        Text(36, "acspMSFT");
        Number(64, 0);
        Number(68, 0xF6D6);
        Number(72, 0x10000);
        Number(76, 0xD32D);
        Number(128, 4);
        Text(132, "wtpt"); Number(136, 180); Number(140, 20);
        Text(144, "kTRC"); Number(148, 200); Number(152, 14);
        Text(156, "desc"); Number(160, 216); Number(164, 95);
        Text(168, "cprt"); Number(172, 312); Number(176, 22);
        Text(180, "XYZ ");
        Number(188, 0xF6D6); Number(192, 0x10000); Number(196, 0xD32D);
        Text(200, "curv"); Number(208, 1);
        profile[212] = 2; profile[213] = 51; // u8Fixed8 gamma 2.199.
        Text(216, "desc"); Number(224, 5); Text(228, "Gray\0");
        Text(312, "text"); Text(320, "Public domain\0");
        return profile;
    }

    private string SavePng(string name, int w, int h, Func<int, int, uint> argb)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Bitmap(w, h, argb)));
        string path = Path.Combine(_dir, name);
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }

    private static BitmapSource Bitmap(int w, int h, Func<int, int, uint> argb)
    {
        var px = new uint[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = argb(x, y);
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
    }

    private static uint[] ReadBgra(string path, out int w, out int h)
    {
        var frame = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        w = bgra.PixelWidth;
        h = bgra.PixelHeight;
        var px = new uint[w * h];
        bgra.CopyPixels(px, w * 4, 0);
        return px;
    }

    /// <summary>Compares RGB with tolerance for JPEG loss.</summary>
    private static void AssertNear(uint expectedRgb, uint actualArgb)
    {
        for (int shift = 0; shift <= 16; shift += 8)
        {
            int e = (int)(expectedRgb >> shift & 0xFF), a = (int)(actualArgb >> shift & 0xFF);
            Assert.True(Math.Abs(e - a) <= 24, $"expected ~#{expectedRgb:X6}, got #{actualArgb & 0xFFFFFF:X6}");
        }
    }
}
