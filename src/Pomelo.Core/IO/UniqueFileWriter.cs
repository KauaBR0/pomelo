using System.Diagnostics;

namespace Pomelo.Core.IO;

/// <summary>Publishes results as "name.ext", or "name (n).ext" when taken. Never replaces a file.</summary>
public static class UniqueFileWriter
{
    private const int ErrorFileExists = unchecked((int)0x80070050);
    private const int ErrorAlreadyExists = unchecked((int)0x800700B7);

    /// <returns>The path written.</returns>
    public static string Write(string directory, string baseName, string extension, ReadOnlySpan<byte> content)
    {
        foreach (string path in Candidates(directory, baseName, extension))
        {
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException ex) when (IsTaken(ex))
            {
                continue; // Created by someone else between the check and the open.
            }

            try
            {
                using (stream)
                    stream.Write(content);
                return path;
            }
            catch
            {
                File.Delete(path);
                throw;
            }
        }
        throw new UnreachableException();
    }

    /// <summary>Renames a finished temp file (same folder) to its final unique name.</summary>
    /// <returns>The final path.</returns>
    public static string MoveIntoPlace(string tempPath, string directory, string baseName, string extension)
    {
        foreach (string path in Candidates(directory, baseName, extension))
        {
            try
            {
                File.Move(tempPath, path, overwrite: false);
                return path;
            }
            catch (IOException ex) when (IsTaken(ex))
            {
            }
        }
        throw new UnreachableException();
    }

    /// <summary>Renames a finished folder (same volume) to "name", or "name (n)" when taken.</summary>
    /// <returns>The final path.</returns>
    public static string MoveFolderIntoPlace(string folder, string directory, string baseName)
    {
        foreach (string candidate in Candidates(directory, baseName, ""))
        {
            try
            {
                Directory.Move(folder, candidate);
                return candidate;
            }
            catch (IOException ex) when (IsTaken(ex))
            {
                // Taken between the check and the rename: try the next name.
            }
        }
        throw new UnreachableException();
    }

    /// <summary>"name.ext", "name (1).ext", … skipping names that already exist (files or folders).</summary>
    internal static IEnumerable<string> Candidates(string directory, string baseName, string extension)
    {
        for (int n = 0; ; n++)
        {
            string path = Path.Combine(directory, n == 0 ? baseName + extension : $"{baseName} ({n}){extension}");
            if (!Path.Exists(path))
                yield return path;
        }
    }

    /// <summary>The target was created by someone else between the existence check and the write.</summary>
    internal static bool IsTaken(IOException ex) => ex.HResult is ErrorFileExists or ErrorAlreadyExists;
}
