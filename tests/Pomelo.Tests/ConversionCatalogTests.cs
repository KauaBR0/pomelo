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
        Assert.Equal(["JPG", "TIFF", "BMP", "GIF"], Names(ConversionCatalog.For([Touch("a.png"), Touch("b.PNG")], null)));
        Assert.Equal(["M4A", "WAV", "FLAC", "OGG", "OPUS", "AIFF", "WMA"],
            Names(ConversionCatalog.For([Touch("a.mp3"), Touch("b.mp3")], FullFfmpeg)));
    }

    [Fact]
    public void Mixed_image_formats_offer_every_image_output()
    {
        Assert.Equal(Names(new(ImageFormats.Outputs, null)),
            Names(ConversionCatalog.For([Touch("a.png"), Touch("b.jpg")], null)));
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
}
