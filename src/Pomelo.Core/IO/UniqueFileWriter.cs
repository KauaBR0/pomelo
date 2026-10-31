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
        foreach (string path in Names(directory, baseName, extension))
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
        foreach (string path in Names(directory, baseName, extension))
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

    private static IEnumerable<string> Names(string directory, string baseName, string extension)
    {
        for (int n = 0; ; n++)
        {
            string path = Path.Combine(directory, n == 0 ? baseName + extension : $"{baseName} ({n}){extension}");
            if (!Path.Exists(path))
                yield return path;
        }
    }

    private static bool IsTaken(IOException ex) => ex.HResult is ErrorFileExists or ErrorAlreadyExists;
}
