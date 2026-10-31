namespace Pomelo.Core.IO;

public static class UniqueFileWriter
{
    private const int ErrorFileExists = unchecked((int)0x80070050);
    private const int ErrorAlreadyExists = unchecked((int)0x800700B7);

    /// <summary>
    /// Writes <paramref name="content"/> to "name.ext", or "name (n).ext" when taken.
    /// Never replaces an existing file; returns the path written.
    /// </summary>
    public static string Write(string directory, string baseName, string extension, ReadOnlySpan<byte> content)
    {
        for (int n = 0; ; n++)
        {
            string name = n == 0 ? baseName + extension : $"{baseName} ({n}){extension}";
            string path = Path.Combine(directory, name);
            if (Path.Exists(path))
                continue;

            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (IOException ex) when (ex.HResult is ErrorFileExists or ErrorAlreadyExists)
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
    }
}
