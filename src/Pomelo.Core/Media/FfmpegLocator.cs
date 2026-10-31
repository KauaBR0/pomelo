using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Pomelo.Core.Media;

/// <summary>
/// Finds a working ffmpeg.exe without downloading anything. Order: the configured path, a copy
/// shipped next to Pomelo, PATH, then the usual winget / Scoop / Chocolatey locations.
/// </summary>
public static partial class FfmpegLocator
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public static FfmpegInfo? Locate(string? configuredPath)
    {
        foreach (string candidate in Candidates(configuredPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate) && Probe(candidate) is { } info)
                return info;
        }
        return null;
    }

    /// <returns>Null when the file does not run as FFmpeg.</returns>
    public static FfmpegInfo? Probe(string path)
    {
        string? version = Run(path, "-version");
        string? encoders = Run(path, "-hide_banner", "-encoders");
        if (version is null || encoders is null || !version.StartsWith("ffmpeg version", StringComparison.Ordinal))
            return null;

        var names = new HashSet<string>(StringComparer.Ordinal);
        bool table = false;
        foreach (string line in encoders.Split('\n'))
        {
            if (line.TrimStart().StartsWith("------", StringComparison.Ordinal))
                table = true;
            else if (table && EncoderLine().Match(line) is { Success: true } m)
                names.Add(m.Groups[1].Value);
        }

        // "ffmpeg version 7.1-essentials_build-www.gyan.dev …" → "7.1"; git builds ("N-118…") stay whole.
        string token = version.Split('\n')[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(2) ?? "?";
        string release = token.Split('-')[0];
        string shortVersion = release.Length > 0 && char.IsDigit(release[0]) ? release : token;
        return new FfmpegInfo(Path.GetFullPath(path), shortVersion, names);
    }

    private static IEnumerable<string> Candidates(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            yield return configuredPath;

        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string full;
            try { full = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe"); }
            catch (ArgumentException) { continue; } // Malformed PATH entry.
            yield return full;
        }

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");

        // winget installs started from another process don't update our PATH until restart.
        foreach (string exe in WingetPackageExecutables(Path.Combine(local, "Microsoft", "WinGet", "Packages")))
            yield return exe;

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "ffmpeg.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin", "ffmpeg.exe");
    }

    /// <summary>Newest first. Unreadable folders are skipped: a scan must never crash the search.</summary>
    private static List<string> WingetPackageExecutables(string packages)
    {
        try
        {
            if (!Directory.Exists(packages))
                return [];
            return Directory.EnumerateDirectories(packages, "*FFmpeg*")
                .SelectMany(d => Directory.EnumerateFiles(d, "ffmpeg.exe", SearchOption.AllDirectories))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? Run(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(ProbeTimeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 ? stdout.Result : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // Not an executable we can start.
        }
    }

    /// <summary>" V....D libx264   description" → "libx264".</summary>
    [GeneratedRegex(@"^\s*[VAS][\.A-Z]{5}\s+(\S+)")]
    private static partial Regex EncoderLine();
}
