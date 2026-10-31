using System.Diagnostics;
using System.Text.RegularExpressions;
using Pomelo.Core.Media;

namespace Pomelo.Tests;

/// <summary>Real-FFmpeg helpers for tests; tests using them skip on machines without FFmpeg.</summary>
public static partial class TestFfmpeg
{
    private static readonly Lazy<FfmpegInfo?> Located = new(() => FfmpegLocator.Locate(null));
    public static FfmpegInfo? Info => Located.Value;

    /// <summary>Runs FFmpeg to create <paramref name="path"/> from <paramref name="inputArgs"/>.</summary>
    public static string Generate(string path, params string[] inputArgs)
    {
        Run(["-hide_banner", "-loglevel", "error", "-y", .. inputArgs, path]);
        return path;
    }

    /// <summary>Fails the test unless <paramref name="stream"/> (e.g. "0:a:0") decodes.</summary>
    public static void AssertDecodes(string path, string stream) =>
        Run(["-hide_banner", "-loglevel", "error", "-i", path, "-map", stream, "-f", "null", "-"]);

    /// <summary>FFmpeg's description of the file's streams and metadata (the "-i" banner).</summary>
    public static string Describe(string path)
    {
        var (_, stderr) = Exec(["-hide_banner", "-i", path]); // Exits 1: no output given.
        return stderr;
    }

    public static (int Width, int Height) VideoSize(string path)
    {
        var m = VideoSizePattern().Match(Describe(path));
        Assert.True(m.Success, $"no video stream in {path}");
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    private static void Run(string[] args)
    {
        var (exit, stderr) = Exec(args);
        Assert.True(exit == 0, $"ffmpeg {string.Join(' ', args)}\n{stderr}");
    }

    private static (int Exit, string Stderr) Exec(string[] args)
    {
        var start = new ProcessStartInfo(Info!.Path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stderr);
    }

    [GeneratedRegex(@"Video: .*?, (\d{2,5})x(\d{2,5})")]
    private static partial Regex VideoSizePattern();
}

public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (TestFfmpeg.Info is null)
            Skip = "FFmpeg não encontrado";
    }
}

public sealed class FfmpegTheoryAttribute : TheoryAttribute
{
    public FfmpegTheoryAttribute()
    {
        if (TestFfmpeg.Info is null)
            Skip = "FFmpeg não encontrado";
    }
}
