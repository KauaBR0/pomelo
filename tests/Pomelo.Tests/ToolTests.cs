using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pomelo.Core.Conversion;
using Pomelo.Core.Media;
using Pomelo.Core.Tools;
using static Pomelo.Tests.TestFfmpeg;

namespace Pomelo.Tests;

public sealed class ToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pomelo-tools-").FullName;
    private readonly ConversionOptions _options = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string At(string name) => Path.Combine(_dir, name);

    // ---- Catalog

    [Fact]
    public void Images_get_the_image_tools()
    {
        File.WriteAllBytes(At("a.png"), []);
        File.WriteAllBytes(At("b.heic"), []);

        Assert.Equal(FileTools.ForImages, ToolCatalog.For([At("a.png"), At("b.heic")], null).Tools);
    }

    [Fact]
    public void Mixed_kinds_get_no_tools_and_no_ffmpeg_hint()
    {
        File.WriteAllBytes(At("a.png"), []);
        File.WriteAllBytes(At("b.mp3"), []);

        var choices = ToolCatalog.For([At("a.png"), At("b.mp3")], null);

        Assert.Empty(choices.Tools);
        Assert.Null(choices.Hint);
    }

    [Fact]
    public void Media_without_ffmpeg_says_ffmpeg_is_required()
    {
        File.WriteAllBytes(At("clip.mp4"), []);

        var choices = ToolCatalog.For([At("clip.mp4")], null);

        Assert.Empty(choices.Tools);
        Assert.Equal(ConversionCatalog.FfmpegRequiredHint, choices.Hint);
    }

    [Fact]
    public void Video_tools_needing_a_missing_encoder_are_hidden_but_stream_copy_tools_remain()
    {
        File.WriteAllBytes(At("clip.mkv"), []);
        var noX264 = new FfmpegInfo("ffmpeg.exe", "test", new HashSet<string> { "aac" });

        Assert.Equal([FileTools.Mute, FileTools.StripVideo], ToolCatalog.For([At("clip.mkv")], noX264).Tools);
    }

    // ---- Image tools

    [Fact]
    public void Half_keeps_the_format_and_halves_the_size()
    {
        string png = SavePng("photo.png", 40, 20, (_, _) => 0xFF336699u);

        string output = FileTools.Half.Apply(png, _options, null, default);

        Assert.Equal(At("photo - 50%.png"), output);
        Assert.Equal((20, 10), Size(output));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rotation_moves_the_top_left_corner_to_the_expected_side(bool clockwise)
    {
        // Black marker at the top-left of a white 20x10 image.
        string png = SavePng("photo.png", 20, 10, (x, y) => x < 3 && y < 3 ? 0xFF000000u : 0xFFFFFFFFu);
        var tool = clockwise ? FileTools.RotateRight : FileTools.RotateLeft;

        string output = tool.Apply(png, _options, null, default);

        Assert.Equal(At("photo - girado.png"), output);
        Assert.Equal((10, 20), Size(output));
        uint[] px = Pixels(output);
        // Clockwise: top-left → top-right. Counter-clockwise: top-left → bottom-left.
        int marker = clockwise ? 1 * 10 + 8 : 18 * 10 + 1;
        Assert.Equal(0xFF000000u, px[marker]);
    }

    [Fact]
    public void Strip_removes_exif_from_jpeg()
    {
        var metadata = new BitmapMetadata("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=271}", "PhoneMaker"); // EXIF "Make"
        string jpg = At("phone.jpg");
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Bitmap(8, 8, (_, _) => 0xFF808080u), null, metadata, null));
        using (var file = File.Create(jpg))
            encoder.Save(file);
        Assert.Equal("PhoneMaker", ReadQuery(jpg, "/app1/ifd/{ushort=271}"));

        string output = FileTools.StripImage.Apply(jpg, _options, null, default);

        Assert.Equal(At("phone - sem metadados.jpg"), output);
        Assert.Null(ReadQuery(output, "/app1/ifd/{ushort=271}"));
    }

    [Fact]
    public void Compress_makes_a_jpeg_no_larger_than_the_limit()
    {
        string png = SavePng("poster.png", 3000, 1500, (x, _) => 0xFF000000u | (uint)(x % 256));

        string output = FileTools.CompressImage.Apply(png, _options, null, default);

        Assert.Equal(At("poster - comprimido.jpg"), output);
        Assert.Equal((FileTools.CompressedImageMaxSide, FileTools.CompressedImageMaxSide / 2), Size(output));
    }

    // ---- Audio tools

    [FfmpegFact]
    public void Normalize_keeps_the_format_and_outputs_48khz()
    {
        string flac = Generate(At("voice.flac"), "-f", "lavfi", "-i", "sine=frequency=300:duration=2:sample_rate=44100");

        string output = FileTools.Normalize.Apply(flac, _options, Info, default);

        Assert.Equal(At("voice - normalizado.flac"), output);
        Assert.Contains("48000 Hz", Describe(output));
    }

    [FfmpegFact]
    public void Mono_downmixes_stereo()
    {
        string wav = Generate(At("stereo.wav"), "-f", "lavfi", "-i", "sine=duration=1", "-ac", "2");
        Assert.Contains("stereo", Describe(wav));

        string output = FileTools.Mono.Apply(wav, _options, Info, default);

        Assert.Equal(At("stereo - mono.wav"), output);
        Assert.Contains("mono", Describe(output));
    }

    [FfmpegFact]
    public void Compress_audio_makes_a_128k_mp3()
    {
        string wav = Generate(At("song.wav"), "-f", "lavfi", "-i", "sine=duration=1");

        string output = FileTools.CompressAudio.Apply(wav, _options, Info, default);

        Assert.Equal(At("song - comprimido.mp3"), output);
        Assert.Contains("128 kb/s", Describe(output));
    }

    [FfmpegFact]
    public void Strip_audio_removes_tags_without_reencoding()
    {
        string mp3 = Generate(At("song.mp3"), "-f", "lavfi", "-i", "sine=duration=1", "-metadata", "title=Segredo");
        Assert.Contains("Segredo", Describe(mp3));

        string output = FileTools.StripAudio.Apply(mp3, _options, Info, default);

        Assert.Equal(At("song - sem metadados.mp3"), output);
        Assert.DoesNotContain("Segredo", Describe(output));
        AssertDecodes(output, "0:a:0");
    }

    // ---- Video tools

    [FfmpegTheory]
    [InlineData(2000, 1200, 1800, 1080)] // Landscape: height capped.
    [InlineData(1200, 2000, 1080, 1800)] // Portrait: width (the short side) capped.
    [InlineData(640, 360, 640, 360)]     // Small: untouched.
    public void Compress_video_caps_the_short_side_at_1080(int width, int height, int expectedWidth, int expectedHeight)
    {
        string clip = Generate(At("clip.mov"), "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=5:duration=1",
            "-f", "lavfi", "-i", "sine=duration=1", "-shortest", "-c:v", "libx264", "-preset", "ultrafast");

        string output = FileTools.CompressVideo.Apply(clip, _options, Info, default);

        Assert.Equal(At("clip - comprimido.mp4"), output);
        Assert.Equal((expectedWidth, expectedHeight), VideoSize(output));
        AssertDecodes(output, "0:a:0");
    }

    [FfmpegFact]
    public void Rotate_video_swaps_dimensions()
    {
        string clip = Generate(At("clip.mp4"), "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=5:duration=1");

        string output = FileTools.RotateVideo.Apply(clip, _options, Info, default);

        Assert.Equal(At("clip - girado.mp4"), output);
        Assert.Equal((90, 160), VideoSize(output));
    }

    [FfmpegFact]
    public void Mute_keeps_the_container_and_drops_audio()
    {
        string clip = Generate(At("clip.mkv"), "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=5:duration=1",
            "-f", "lavfi", "-i", "sine=duration=1", "-shortest");
        Assert.Contains("Audio:", Describe(clip));

        string output = FileTools.Mute.Apply(clip, _options, Info, default);

        Assert.Equal(At("clip - sem áudio.mkv"), output);
        Assert.DoesNotContain("Audio:", Describe(output));
        AssertDecodes(output, "0:v:0");
    }

    [FfmpegFact]
    public void Strip_video_removes_tags_and_keeps_both_streams()
    {
        string clip = Generate(At("clip.mp4"), "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=5:duration=1",
            "-f", "lavfi", "-i", "sine=duration=1", "-shortest", "-metadata", "title=Segredo");
        Assert.Contains("Segredo", Describe(clip));

        string output = FileTools.StripVideo.Apply(clip, _options, Info, default);

        Assert.Equal(At("clip - sem metadados.mp4"), output);
        Assert.DoesNotContain("Segredo", Describe(output));
        AssertDecodes(output, "0:v:0");
        AssertDecodes(output, "0:a:0");
    }

    // ---- Image helpers

    private string SavePng(string name, int w, int h, Func<int, int, uint> argb)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Bitmap(w, h, argb)));
        string path = At(name);
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

    private static BitmapFrame Frame(string path) =>
        BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];

    private static (int, int) Size(string path)
    {
        var frame = Frame(path);
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static uint[] Pixels(string path)
    {
        var bgra = new FormatConvertedBitmap(Frame(path), PixelFormats.Bgra32, null, 0);
        var px = new uint[bgra.PixelWidth * bgra.PixelHeight];
        bgra.CopyPixels(px, bgra.PixelWidth * 4, 0);
        return px;
    }

    private static object? ReadQuery(string path, string query) =>
        Frame(path).Metadata is BitmapMetadata m && m.ContainsQuery(query) ? m.GetQuery(query) : null;
}
