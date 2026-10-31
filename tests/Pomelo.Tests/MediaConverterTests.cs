using System.Diagnostics;
using Pomelo.Core.Conversion;
using Pomelo.Core.Formats;
using Pomelo.Core.Media;

namespace Pomelo.Tests;

/// <summary>Skips (instead of failing) on machines without FFmpeg.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (MediaConverterTests.Ffmpeg is null)
            Skip = "FFmpeg não encontrado";
    }
}

public sealed class FfmpegTheoryAttribute : TheoryAttribute
{
    public FfmpegTheoryAttribute()
    {
        if (MediaConverterTests.Ffmpeg is null)
            Skip = "FFmpeg não encontrado";
    }
}

public sealed class MediaConverterTests : IDisposable
{
    private static readonly Lazy<FfmpegInfo?> Located = new(() => FfmpegLocator.Locate(null));
    public static FfmpegInfo? Ffmpeg => Located.Value;

    private readonly string _dir = Directory.CreateTempSubdirectory("pomelo-media-").FullName;
    private readonly ConversionOptions _options = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    public static TheoryData<string> AudioOutputs() => new(MediaFormats.AudioOutputs.Select(f => f.Name));
    public static TheoryData<string> VideoOutputs() => new(MediaFormats.VideoOutputs.Select(f => f.Name));

    [FfmpegTheory]
    [MemberData(nameof(AudioOutputs))]
    public void Audio_converts_to_every_audio_output(string name)
    {
        var target = MediaFormats.AudioOutputs.Single(f => f.Name == name);
        string tone = Generate("tone.mka", "-f", "lavfi", "-i", "sine=frequency=440:duration=1"); // Matches no output name.

        string output = MediaConverter.Convert(tone, target, _options, Ffmpeg!);

        Assert.Equal(Path.Combine(_dir, "tone" + target.Extension), output);
        AssertDecodes(output, "0:a:0");
    }

    /// <summary>161x121 is odd on purpose: H.264/yuv420p need even sizes, which the recipes must fix.</summary>
    [FfmpegTheory]
    [MemberData(nameof(VideoOutputs))]
    public void Odd_sized_video_with_sound_converts_to_every_video_output(string name)
    {
        var target = MediaFormats.VideoOutputs.Single(f => f.Name == name);
        string clip = Clip("clip.mkv", withAudio: true);

        string output = MediaConverter.Convert(clip, target, _options, Ffmpeg!);

        Assert.Equal(target.Extension, Path.GetExtension(output));
        AssertDecodes(output, target.Kind == MediaKind.Audio ? "0:a:0" : "0:v:0");
        if (target != MediaFormats.AnimatedGif && target.Kind == MediaKind.Video)
            AssertDecodes(output, "0:a:0"); // Soundtrack kept.
    }

    [FfmpegFact]
    public void Extracting_audio_from_a_silent_video_fails_cleanly()
    {
        string clip = Clip("silent.mp4", withAudio: false);

        var error = Assert.Throws<ConversionException>(() => MediaConverter.Convert(clip, MediaFormats.Mp3, _options, Ffmpeg!));

        Assert.Contains("faixa de áudio", error.Message);
        Assert.Equal([clip], Directory.GetFiles(_dir));
    }

    [FfmpegFact]
    public void Existing_output_is_never_replaced()
    {
        string tone = Generate("song.wav", "-f", "lavfi", "-i", "sine=duration=1");
        string existing = Path.Combine(_dir, "song.mp3");
        File.WriteAllText(existing, "keep me");

        string output = MediaConverter.Convert(tone, MediaFormats.Mp3, _options, Ffmpeg!);

        Assert.Equal(Path.Combine(_dir, "song (1).mp3"), output);
        Assert.Equal("keep me", File.ReadAllText(existing));
    }

    [FfmpegFact]
    public void Cancelling_stops_ffmpeg_and_leaves_no_partial_file()
    {
        string clip = Generate("long.mp4", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=30",
            "-c:v", "libx264", "-preset", "ultrafast");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        var watch = Stopwatch.StartNew();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            MediaConverter.Convert(clip, MediaFormats.WebM, _options, Ffmpeg!, cancel.Token));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.Equal([clip], Directory.GetFiles(_dir));
    }

    [FfmpegFact]
    public void Corrupt_input_reports_a_readable_error()
    {
        string fake = Path.Combine(_dir, "fake.mp4");
        File.WriteAllText(fake, "not a video");

        var error = Assert.Throws<ConversionException>(() => MediaConverter.Convert(fake, MediaFormats.Mp3, _options, Ffmpeg!));

        Assert.DoesNotContain("~pomelo", error.Message);
        Assert.Equal([fake], Directory.GetFiles(_dir));
    }

    private string Clip(string name, bool withAudio)
    {
        string[] video = ["-f", "lavfi", "-i", "testsrc2=size=161x121:rate=15:duration=1"];
        string[] audio = withAudio ? ["-f", "lavfi", "-i", "sine=duration=1", "-shortest"] : [];
        return Generate(name, [.. video, .. audio, "-pix_fmt", "yuv444p"]);
    }

    private string Generate(string name, params string[] inputArgs)
    {
        string path = Path.Combine(_dir, name);
        RunFfmpeg(["-hide_banner", "-loglevel", "error", "-y", .. inputArgs, path]);
        return path;
    }

    private static void AssertDecodes(string path, string stream) =>
        RunFfmpeg(["-hide_banner", "-loglevel", "error", "-i", path, "-map", stream, "-f", "null", "-"]);

    private static void RunFfmpeg(string[] args)
    {
        var start = new ProcessStartInfo(Ffmpeg!.Path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"ffmpeg {string.Join(' ', args)}\n{stderr}");
    }
}
