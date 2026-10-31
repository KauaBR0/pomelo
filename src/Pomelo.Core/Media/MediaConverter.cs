using System.Diagnostics;
using Pomelo.Core.Formats;
using Pomelo.Core.Conversion;
using Pomelo.Core.IO;

namespace Pomelo.Core.Media;

/// <summary>
/// Audio/video conversion through an external FFmpeg process. FFmpeg writes a "~pomelo-*.part"
/// file in the output folder, which is renamed to a unique final name only on success;
/// failures and cancellation delete it.
/// </summary>
public static class MediaConverter
{
    private const int StderrLinesKept = 20;

    /// <returns>The path of the created file.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled; FFmpeg was stopped.</exception>
    public static string Convert(string sourcePath, FileFormat target, ConversionOptions options, FfmpegInfo ffmpeg,
        CancellationToken cancellation = default) =>
        Apply(sourcePath, MediaRecipes.For(target), Path.GetFileNameWithoutExtension(sourcePath), target.Extension,
            options, ffmpeg, cancellation);

    /// <summary>Runs <paramref name="recipe"/> and publishes "<paramref name="outputName"/><paramref name="extension"/>", or "outputName (n)ext".</summary>
    /// <returns>The path of the created file.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled; FFmpeg was stopped.</exception>
    public static string Apply(string sourcePath, MediaRecipe recipe, string outputName, string extension,
        ConversionOptions options, FfmpegInfo ffmpeg, CancellationToken cancellation = default)
    {
        string source = Path.GetFullPath(sourcePath);
        string directory = options.OutputDirectory ?? Path.GetDirectoryName(source)!;
        if (!Directory.Exists(directory))
            throw new ConversionException($"A pasta de saída não existe: {directory}");
        if (!ffmpeg.HasEncoders(recipe.Encoders))
            throw new ConversionException($"O FFmpeg em uso não tem o codificador necessário ({string.Join(", ", recipe.Encoders)}).");

        string temp = Path.Combine(directory, $"~pomelo-{Guid.NewGuid():N}.part");
        try
        {
            Run(ffmpeg.Path, source, recipe, temp, cancellation);
            return UniqueFileWriter.MoveIntoPlace(temp, directory, outputName, extension);
        }
        finally
        {
            File.Delete(temp); // No-op after a successful move.
        }
    }

    private static void Run(string ffmpeg, string source, MediaRecipe recipe, string output, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();

        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (string arg in (string[])["-hide_banner", "-nostdin", "-loglevel", "error", "-y", "-i", source])
            start.ArgumentList.Add(arg);
        foreach (string arg in recipe.OutputArguments)
            start.ArgumentList.Add(arg);
        foreach (string arg in (string[])["-f", recipe.Muxer, output])
            start.ArgumentList.Add(arg);

        var stderr = new Queue<string>();
        using var process = new Process { StartInfo = start };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;
            lock (stderr)
            {
                stderr.Enqueue(e.Data.Trim());
                if (stderr.Count > StderrLinesKept)
                    stderr.Dequeue();
            }
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ConversionException($"Não foi possível iniciar o FFmpeg ({ffmpeg}): {ex.Message}", ex);
        }
        process.BeginErrorReadLine();
        TryLowerPriority(process);

        using (cancellation.Register(() => Kill(process)))
            process.WaitForExit(); // Also drains the async stderr reader.

        cancellation.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new ConversionException(Describe(stderr.ToArray()));
    }

    /// <summary>Turns FFmpeg's last error lines into a user-facing message.</summary>
    private static string Describe(string[] lines)
    {
        string all = string.Join('\n', lines);
        if (all.Contains("matches no streams", StringComparison.Ordinal))
            return "O arquivo não tem a faixa de áudio ou vídeo que este formato precisa.";
        if (all.Contains("Invalid data found when processing input", StringComparison.Ordinal))
            return "O arquivo está corrompido ou não é um formato de mídia reconhecido.";
        return lines.Length > 0 ? $"FFmpeg: {lines[^1]}" : "O FFmpeg falhou sem dar detalhes.";
    }

    /// <summary>Keeps the desktop responsive during long encodes.</summary>
    private static void TryLowerPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
