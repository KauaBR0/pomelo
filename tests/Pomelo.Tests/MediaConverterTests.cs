using System.Diagnostics;
using Pomelo.Core.Conversion;
using Pomelo.Core.Formats;
using Pomelo.Core.Media;
using static Pomelo.Tests.TestFfmpeg;

namespace Pomelo.Tests;

public sealed class MediaConverterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pomelo-media-").FullName;
    private readonly ConversionOptions _options = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    public static TheoryData<string> AudioOutputs() => new(MediaFormats.AudioOutputs.Select(f => f.Name));
    public static TheoryData<string> VideoOutputs() => new(MediaFormats.VideoOutputs.Select(f => f.Name));

    [FfmpegTheory("pcm_s16le", "libmp3lame", "aac", "flac", "libvorbis", "libopus", "pcm_s16be", "wmav2")]
    [MemberData(nameof(AudioOutputs))]
    public void Audio_converts_to_every_audio_output(string name)
    {
        var target = MediaFormats.AudioOutputs.Single(f => f.Name == name);
        string tone = Generate(At("tone.mka"), "-f", "lavfi", "-i", "sine=frequency=440:duration=1", "-c:a", "pcm_s16le");

        string output = MediaConverter.Convert(tone, target, _options, Info!);

        Assert.Equal(At("tone" + target.Extension), output);
        AssertDecodes(output, "0:a:0");
    }

    /// <summary>161x121 is odd on purpose: H.264/yuv420p need even sizes, which the recipes must fix.</summary>
    [FfmpegTheory("ffv1", "pcm_s16le", "libx264", "aac", "libvpx-vp9", "libopus", "mpeg4", "libmp3lame", "wmv2", "wmav2", "gif")]
    [MemberData(nameof(VideoOutputs))]
    public void Odd_sized_video_with_sound_converts_to_every_video_output(string name)
    {
        var target = MediaFormats.VideoOutputs.Single(f => f.Name == name);
        string clip = Clip("clip.mkv", withAudio: true);

        string output = MediaConverter.Convert(clip, target, _options, Info!);

        Assert.Equal(target.Extension, Path.GetExtension(output));
        AssertDecodes(output, target.Kind == MediaKind.Audio ? "0:a:0" : "0:v:0");
        if (target != MediaFormats.AnimatedGif && target.Kind == MediaKind.Video)
            AssertDecodes(output, "0:a:0"); // Soundtrack kept.
    }

    [FfmpegFact("libx264", "libmp3lame")]
    public void Extracting_audio_from_a_silent_video_fails_cleanly()
    {
        string clip = Clip("silent.mp4", withAudio: false);

        var error = Assert.Throws<ConversionException>(() => MediaConverter.Convert(clip, MediaFormats.Mp3, _options, Info!));

        Assert.Contains("faixa de áudio", error.Message);
        Assert.Equal([clip], Directory.GetFiles(_dir));
    }

    [FfmpegFact("pcm_s16le", "libmp3lame")]
    public void Existing_output_is_never_replaced()
    {
        string tone = Generate(At("song.wav"), "-f", "lavfi", "-i", "sine=duration=1");
        File.WriteAllText(At("song.mp3"), "keep me");

        string output = MediaConverter.Convert(tone, MediaFormats.Mp3, _options, Info!);

        Assert.Equal(At("song (1).mp3"), output);
        Assert.Equal("keep me", File.ReadAllText(At("song.mp3")));
    }

    [FfmpegFact("libx264", "libvpx-vp9", "libopus")]
    public void Cancelling_stops_ffmpeg_and_leaves_no_partial_file()
    {
        string clip = Generate(At("long.mp4"), "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=30",
            "-c:v", "libx264", "-preset", "ultrafast");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        var watch = Stopwatch.StartNew();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            MediaConverter.Convert(clip, MediaFormats.WebM, _options, Info!, cancel.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Equal([clip], Directory.GetFiles(_dir));
    }

    [FfmpegFact("libmp3lame")]
    public void Corrupt_input_reports_a_readable_error()
    {
        string fake = At("fake.mp4");
        File.WriteAllText(fake, "not a video");

        var error = Assert.Throws<ConversionException>(() => MediaConverter.Convert(fake, MediaFormats.Mp3, _options, Info!));

        Assert.DoesNotContain("~pomelo", error.Message);
        Assert.Equal([fake], Directory.GetFiles(_dir));
    }

    [FfmpegTheory("ffv1", "pcm_s16le", "wmv2", "wmav2")]
    [InlineData(false)]
    [InlineData(true)]
    public void Windows_media_downmixes_surround_audio_to_stereo(bool video)
    {
        string[] picture = video
            ? ["-f", "lavfi", "-i", "testsrc2=size=160x90:rate=5:duration=1"] : [];
        string source = Generate(At("surround.mkv"), [.. picture, "-f", "lavfi", "-i",
            "anullsrc=channel_layout=5.1:sample_rate=48000", "-t", "1",
            "-c:a", "pcm_s16le", .. (video ? new[] { "-c:v", "ffv1" } : Array.Empty<string>())]);

        string output = MediaConverter.Convert(source, video ? MediaFormats.Wmv : MediaFormats.Wma, _options, Info!);

        Assert.Contains("stereo", Describe(output));
        AssertDecodes(output, "0:a:0");
        if (video)
            AssertDecodes(output, "0:v:0");
    }

    private string At(string name) => Path.Combine(_dir, name);

    private string Clip(string name, bool withAudio)
    {
        string[] video = ["-f", "lavfi", "-i", "testsrc2=size=161x121:rate=15:duration=1"];
        string[] audio = withAudio ? ["-f", "lavfi", "-i", "sine=duration=1", "-shortest"] : [];
        return Generate(At(name), [.. video, .. audio, "-c:v",
            Path.GetExtension(name) == ".mp4" ? "libx264" : "ffv1", "-c:a", "pcm_s16le", "-pix_fmt", "yuv444p"]);
    }
}
