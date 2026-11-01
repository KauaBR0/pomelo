using Pomelo.Core.Conversion;
using Pomelo.Core.Formats;
using Pomelo.Core.Media;

namespace Pomelo.Tests;

public sealed class ConversionCatalogTests : IDisposable
{
    private static readonly FfmpegInfo FullFfmpeg = new("ffmpeg.exe", "test", new HashSet<string>
    {
        "libmp3lame", "aac", "pcm_s16le", "flac", "libvorbis", "libopus", "pcm_s16be", "wmav2",
        "libx264", "libvpx-vp9", "mpeg4", "wmv2", "gif",
    });

    private readonly string _dir = Directory.CreateTempSubdirectory("pomelo-catalog-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, []);
        return path;
    }

    private static string[] Names(ConversionChoices choices) => choices.Targets.Select(f => f.Name).ToArray();

    [Fact]
    public void Same_format_inputs_do_not_offer_their_own_format()
    {
        Assert.Equal(["JPG", "TIFF", "BMP", "GIF", "PDF"], Names(ConversionCatalog.For([Touch("a.png"), Touch("b.PNG")], null)));
        Assert.Equal(["M4A", "WAV", "FLAC", "OGG", "OPUS", "AIFF", "WMA"],
            Names(ConversionCatalog.For([Touch("a.mp3"), Touch("b.mp3")], FullFfmpeg)));
    }

    [Fact]
    public void Mixed_image_formats_offer_every_image_output_and_pdf()
    {
        Assert.Equal(["JPG", "PNG", "TIFF", "BMP", "GIF", "PDF"],
            Names(ConversionCatalog.For([Touch("a.png"), Touch("b.jpg")], null)));
    }

    [Fact]
    public void Pdf_pages_become_jpg_or_png_alone_or_with_images()
    {
        string pdf = Touch("doc.PDF");
        Assert.Equal(["JPG", "PNG"], Names(ConversionCatalog.For([pdf], null)));
        Assert.Equal(["JPG", "PNG"], Names(ConversionCatalog.For([pdf, Touch("a.heic")], null)));
        Assert.Empty(ConversionCatalog.For([pdf, Touch("clip.mp4")], FullFfmpeg).Targets);
        Assert.Null(ConversionCatalog.For([pdf, Touch("song.mp3")], null).Hint); // FFmpeg would not help.
    }

    [Fact]
    public void Audio_and_video_together_offer_only_their_shared_target()
    {
        Assert.Equal(["MP3"], Names(ConversionCatalog.For([Touch("song.wav"), Touch("clip.mp4")], FullFfmpeg)));
    }

    [Fact]
    public void Video_offers_video_formats_and_soundtrack_extraction()
    {
        Assert.Equal(["MOV", "MKV", "WEBM", "AVI", "WMV", "GIF", "MP3"],
            Names(ConversionCatalog.For([Touch("clip.mp4")], FullFfmpeg)));
    }

    [Fact]
    public void Media_without_ffmpeg_offers_nothing_and_says_why()
    {
        var choices = ConversionCatalog.For([Touch("song.flac")], null);

        Assert.Empty(choices.Targets);
        Assert.Equal(ConversionCatalog.FfmpegRequiredHint, choices.Hint);
    }

    [Fact]
    public void Ffmpeg_hint_is_not_shown_when_images_are_mixed_in()
    {
        // Even with FFmpeg, an image and a video share no target, so installing it would not help.
        Assert.Null(ConversionCatalog.For([Touch("a.png"), Touch("clip.mp4")], null).Hint);
    }

    [Fact]
    public void Targets_whose_encoder_is_missing_from_the_ffmpeg_build_are_hidden()
    {
        var noVp9 = FullFfmpeg with { Encoders = FullFfmpeg.Encoders.Where(e => e != "libvpx-vp9").ToHashSet() };

        Assert.DoesNotContain("WEBM", Names(ConversionCatalog.For([Touch("clip.mov")], noVp9)));
    }

    [Fact]
    public void Any_unknown_file_folder_or_missing_path_disables_conversion()
    {
        string image = Touch("a.png");
        Assert.Empty(ConversionCatalog.For([image, Touch("notes.txt")], FullFfmpeg).Targets);
        Assert.Empty(ConversionCatalog.For([image, Directory.CreateDirectory(Path.Combine(_dir, "folder.png")).FullName], FullFfmpeg).Targets);
        Assert.Empty(ConversionCatalog.For([image, Path.Combine(_dir, "gone.png")], FullFfmpeg).Targets);
        Assert.Empty(ConversionCatalog.For([], FullFfmpeg).Targets);
    }

    [Fact]
    public void Animated_gif_offers_silent_video_formats_and_still_gif_stays_an_image()
    {
        string animated = Gif("anim.gif", frames: 2);

        Assert.Equal(["MP4", "MOV", "MKV", "WEBM", "AVI", "WMV"], Names(ConversionCatalog.For([animated], FullFfmpeg)));
        Assert.Equal(["JPG", "PNG", "TIFF", "BMP", "PDF"], Names(ConversionCatalog.For([Gif("still.gif", frames: 1)], FullFfmpeg)));
        Assert.Equal(["MP4", "MOV", "MKV", "WEBM", "AVI", "WMV"], Names(ConversionCatalog.For([animated, Touch("clip.mp4")], FullFfmpeg)));
        Assert.Equal(ConversionCatalog.FfmpegRequiredHint, ConversionCatalog.For([animated], null).Hint);
    }

    [Fact]
    public void Gif_frame_count_is_read_from_the_block_structure()
    {
        Assert.True(GifAnimation.IsAnimated(Gif("a.gif", frames: 3, globalTable: true, extensions: true)));
        Assert.False(GifAnimation.IsAnimated(Gif("b.gif", frames: 1, globalTable: true, extensions: true)));

        string truncated = Gif("c.gif", frames: 2);
        File.WriteAllBytes(truncated, File.ReadAllBytes(truncated)[..30]);
        Assert.False(GifAnimation.IsAnimated(truncated));
        Assert.False(GifAnimation.IsAnimated(Touch("empty.gif")));

        string renamed = Path.Combine(_dir, "anim.png");
        File.Copy(Gif("d.gif", frames: 2), renamed);
        Assert.False(GifAnimation.IsAnimated(renamed)); // Only .gif files are treated as GIF.
    }

    /// <summary>Hand-built GIF89a: 1x1 frames with optional colour table and graphic-control/comment extensions.</summary>
    private string Gif(string name, int frames, bool globalTable = false, bool extensions = false)
    {
        var bytes = new List<byte>();
        bytes.AddRange("GIF89a"u8.ToArray());
        bytes.AddRange([1, 0, 1, 0, (byte)(globalTable ? 0x81 : 0x00), 0, 0]); // 1x1, 4-colour table when set.
        if (globalTable)
            bytes.AddRange(new byte[12]);
        for (int i = 0; i < frames; i++)
        {
            if (extensions)
            {
                bytes.AddRange([0x21, 0xF9, 4, 0, 10, 0, 0, 0]); // Graphic control: 100 ms.
                bytes.AddRange([0x21, 0xFE, 3, (byte)'h', (byte)'i', (byte)'!', 0]); // Comment.
            }
            bytes.AddRange([0x2C, 0, 0, 0, 0, 1, 0, 1, 0, (byte)(globalTable ? 0x00 : 0x80)]);
            if (!globalTable)
                bytes.AddRange(new byte[6]); // Local 2-colour table.
            bytes.AddRange([2, 2, 0x4C, 0x01, 0]); // LZW min size 2, one data sub-block, terminator.
        }
        bytes.Add(0x3B);
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }
}
