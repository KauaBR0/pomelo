using System.Diagnostics;
using Pomelo.Core.Conversion;
using Pomelo.Core.IO;

namespace Pomelo.Core.Actions;

public enum TransferMode { Copy, Move }

/// <param name="Destination">Where the item ended up; null when it failed.</param>
public sealed record TransferResult(string Source, string? Destination, string? Error);

/// <summary>
/// Copies or moves files and folders into a folder. Never replaces anything: a name already
/// taken becomes "name (1)". Moves across drives copy then delete. Each item succeeds or fails
/// on its own.
/// </summary>
public static class FileTransfer
{
    public static IReadOnlyList<TransferResult> Run(IReadOnlyList<string> paths, string destination, TransferMode mode,
        CancellationToken cancellation = default)
    {
        if (!Directory.Exists(destination))
            throw new ConversionException($"A pasta de destino não existe: {destination}");
        string target = Path.GetFullPath(destination);

        var results = new List<TransferResult>(paths.Count);
        foreach (string path in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                results.Add(new(path, TransferOne(Path.GetFullPath(path), target, mode, cancellation), null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConversionException)
            {
                results.Add(new(path, null, ex.Message));
            }
        }
        return results;
    }

    private static string TransferOne(string source, string destination, TransferMode mode, CancellationToken cancellation)
    {
        bool isFolder = Directory.Exists(source);
        if (!isFolder && !File.Exists(source))
            throw new ConversionException("O item não existe mais.");

        string parent = Path.GetDirectoryName(source.TrimEnd(Path.DirectorySeparatorChar)) ?? "";
        if (mode == TransferMode.Move && SamePath(parent, destination))
            throw new ConversionException("O item já está nesta pasta.");
        if (isFolder && IsSameOrInside(destination, source))
            throw new ConversionException("Não dá para colocar uma pasta dentro dela mesma.");

        string name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar));
        string baseName = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        string extension = isFolder ? "" : Path.GetExtension(name);
        bool sameDrive = SamePath(Path.GetPathRoot(source)!, Path.GetPathRoot(destination)!);

        foreach (string candidate in UniqueFileWriter.Candidates(destination, baseName, extension))
        {
            try
            {
                if (!isFolder && mode == TransferMode.Copy)
                    File.Copy(source, candidate, overwrite: false);
                else if (!isFolder)
                    File.Move(source, candidate, overwrite: false); // Copies + deletes across drives.
                else if (mode == TransferMode.Move && sameDrive)
                    Directory.Move(source, candidate); // Rename: instant and atomic.
                else
                {
                    CopyFolder(source, candidate, cancellation);
                    if (mode == TransferMode.Move)
                        Directory.Delete(source, recursive: true);
                }
                return candidate;
            }
            catch (IOException ex) when (UniqueFileWriter.IsTaken(ex))
            {
                // Name taken between the check and the write: try the next one.
            }
        }
        throw new UnreachableException();
    }

    /// <summary>
    /// Copies a folder tree into a new folder, removing the partial copy if anything fails.
    /// Linked folders (junctions, symlinks) are not followed.
    /// </summary>
    private static void CopyFolder(string source, string destination, CancellationToken cancellation)
    {
        if (Directory.Exists(destination))
            throw new IOException("Name taken.", unchecked((int)0x800700B7));
        Directory.CreateDirectory(destination);
        try
        {
            CopyContents(source, destination, cancellation);
        }
        catch
        {
            Directory.Delete(destination, recursive: true);
            throw;
        }
    }

    private static void CopyContents(string source, string destination, CancellationToken cancellation)
    {
        foreach (string file in Directory.EnumerateFiles(source))
        {
            cancellation.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }
        foreach (string folder in Directory.EnumerateDirectories(source))
        {
            if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            string copy = Directory.CreateDirectory(Path.Combine(destination, Path.GetFileName(folder))).FullName;
            CopyContents(folder, copy, cancellation);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrInside(string path, string folder)
    {
        string f = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        return SamePath(path, folder) || (Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar)
            .StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }
}
