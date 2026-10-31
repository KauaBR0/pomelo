using System.Buffers;
using Pomelo.Core.Conversion;
using Pomelo.Core.IO;

namespace Pomelo.Core.Actions;

public enum TransferMode { Copy, Move }

/// <param name="Destination">Where the item ended up; null when it failed.</param>
/// <param name="Error">Why it failed; with a <paramref name="Destination"/>, a warning about a finished transfer.</param>
public sealed record TransferResult(string Source, string? Destination, string? Error);

/// <summary>
/// Copies or moves files and folders into a folder. Never replaces anything: a name already
/// taken becomes "name (1)". Copies are built under a "~pomelo-*.part" name and renamed only when
/// complete, so an interrupted copy never looks finished. A move within one volume is a rename;
/// across volumes it copies, then removes the source only after everything is in place.
/// Each item succeeds or fails on its own.
/// </summary>
public static class FileTransfer
{
    public static IReadOnlyList<TransferResult> Run(IReadOnlyList<string> paths, string destination, TransferMode mode,
        CancellationToken cancellation = default) =>
        Run(paths, destination, mode, cancellation, assumeDifferentVolumes: false);

    /// <param name="assumeDifferentVolumes">Tests: exercise the cross-volume path on one disk.</param>
    internal static IReadOnlyList<TransferResult> Run(IReadOnlyList<string> paths, string destination, TransferMode mode,
        CancellationToken cancellation, bool assumeDifferentVolumes)
    {
        if (!Directory.Exists(destination))
            throw new ConversionException($"A pasta de destino não existe: {destination}");
        var target = new Location(Path.GetFullPath(destination));

        var results = new List<TransferResult>(paths.Count);
        foreach (string path in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                results.Add(TransferOne(Path.GetFullPath(path), target, mode, assumeDifferentVolumes, cancellation));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConversionException)
            {
                results.Add(new(path, null, ex.Message));
            }
        }
        return results;
    }

    /// <summary>A path plus its canonical form (junctions, subst and 8.3 names resolved).</summary>
    private readonly record struct Location(string Path)
    {
        public string Real { get; } = RealPath.Of(Path);
    }

    private sealed record CopiedFile(string Path, long Length, DateTime LastWriteTimeUtc);
    private sealed class CopyManifest
    {
        public List<CopiedFile> Files { get; } = [];
        public List<string> Folders { get; } = [];
    }

    private static TransferResult TransferOne(string sourcePath, Location target, TransferMode mode,
        bool assumeDifferentVolumes, CancellationToken cancellation)
    {
        bool isFolder = Directory.Exists(sourcePath);
        if (!isFolder && !File.Exists(sourcePath))
            throw new ConversionException("O item não existe mais.");

        var source = new Location(sourcePath);
        bool isRoot = isFolder && RealPath.Same(Path.GetPathRoot(sourcePath)!, sourcePath);
        if (isRoot && mode == TransferMode.Move)
            throw new ConversionException("Não dá para mover a raiz de um disco.");
        if (mode == TransferMode.Move && !isRoot
            && RealPath.Same(RealPath.Of(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(sourcePath))!), target.Real))
            throw new ConversionException("O item já está nesta pasta.");
        if (isFolder && RealPath.IsSameOrInside(target.Real, source.Real))
            throw new ConversionException("Não dá para colocar uma pasta dentro dela mesma.");

        string name = isRoot ? RootName(sourcePath) : Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath));
        string baseName = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        string extension = isFolder ? "" : Path.GetExtension(name);

        // A rename needs the same volume *and* the same spelling of the root (Directory.Move compares roots as text).
        bool rename = mode == TransferMode.Move && !assumeDifferentVolumes
                      && RealPath.Same(Path.GetPathRoot(source.Real)!, Path.GetPathRoot(target.Real)!)
                      && RealPath.Same(Path.GetPathRoot(sourcePath)!, Path.GetPathRoot(target.Path)!);
        if (rename)
        {
            string moved = isFolder
                ? MoveFolderIntoPlace(sourcePath, target.Path, baseName)
                : UniqueFileWriter.MoveIntoPlace(sourcePath, target.Path, baseName, extension);
            return new(sourcePath, moved, null);
        }

        if (isFolder && mode == TransferMode.Move && ContainsLinks(sourcePath))
            throw new ConversionException("A pasta contém links (atalhos de pasta); mova-a pelo Explorer.");

        var manifest = mode == TransferMode.Move ? new CopyManifest() : null;
        int skippedLinks = 0;
        string published = isFolder
            ? PublishFolderCopy(sourcePath, target.Path, baseName, ref skippedLinks, cancellation, manifest)
            : PublishFileCopy(sourcePath, target.Path, baseName, extension, cancellation, manifest);

        string? warning = skippedLinks > 0 ? $"{skippedLinks} link(s) de pasta não foram copiados." : null;
        if (manifest is not null)
            warning = DeleteSource(manifest);
        return new(sourcePath, published, warning);
    }

    private static string PublishFileCopy(string source, string destination, string baseName, string extension,
        CancellationToken cancellation, CopyManifest? manifest)
    {
        string temp = TempName(destination);
        try
        {
            CopyFile(source, temp, cancellation, manifest);
            cancellation.ThrowIfCancellationRequested();
            return UniqueFileWriter.MoveIntoPlace(temp, destination, baseName, extension);
        }
        catch
        {
            DeleteQuietly(temp);
            throw;
        }
    }

    private static string PublishFolderCopy(string source, string destination, string baseName, ref int skippedLinks,
        CancellationToken cancellation, CopyManifest? manifest)
    {
        string temp = TempName(destination);
        Directory.CreateDirectory(temp);
        try
        {
            CopyContents(source, temp, ref skippedLinks, cancellation, manifest);
            cancellation.ThrowIfCancellationRequested();
            return MoveFolderIntoPlace(temp, destination, baseName);
        }
        catch
        {
            DeleteQuietly(temp); // Never let cleanup trouble replace the real error.
            throw;
        }
    }

    /// <summary>Folder links (junctions, symlinks) are not followed; cloud placeholders are copied normally.</summary>
    private static void CopyContents(string source, string destination, ref int skippedLinks, CancellationToken cancellation,
        CopyManifest? manifest)
    {
        cancellation.ThrowIfCancellationRequested();
        manifest?.Folders.Add(source);
        foreach (string file in Directory.GetFiles(source))
        {
            cancellation.ThrowIfCancellationRequested();
            CopyFile(file, Path.Combine(destination, Path.GetFileName(file)), cancellation, manifest);
        }
        foreach (string folder in Directory.GetDirectories(source))
        {
            if (new DirectoryInfo(folder).LinkTarget is not null)
            {
                skippedLinks++;
                continue;
            }
            string copy = Directory.CreateDirectory(Path.Combine(destination, Path.GetFileName(folder))).FullName;
            CopyContents(folder, copy, ref skippedLinks, cancellation, manifest);
        }
    }

    private static bool ContainsLinks(string folder)
    {
        if (new DirectoryInfo(folder).LinkTarget is not null)
            return true;
        foreach (string child in Directory.EnumerateDirectories(folder))
            if (ContainsLinks(child))
                return true;
        return false;
    }

    private static void CopyFile(string source, string destination, CancellationToken cancellation, CopyManifest? manifest)
    {
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
            try
            {
                int count;
                while ((count = input.Read(buffer)) != 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, count);
                }
                manifest?.Files.Add(new(source, input.Length, File.GetLastWriteTimeUtc(source)));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        File.SetAttributes(destination, File.GetAttributes(source) & (FileAttributes.ReadOnly |
            FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive));
    }

    private static string MoveFolderIntoPlace(string folder, string destination, string baseName)
    {
        foreach (string candidate in UniqueFileWriter.Candidates(destination, baseName, ""))
        {
            try
            {
                Directory.Move(folder, candidate);
                return candidate;
            }
            catch (IOException ex) when (UniqueFileWriter.IsTaken(ex))
            {
                // Taken between the check and the rename: try the next name.
            }
        }
        throw new System.Diagnostics.UnreachableException();
    }

    /// <summary>Remove only copied, unchanged entries. New source entries must survive a move.</summary>
    private static string? DeleteSource(CopyManifest manifest)
    {
        try
        {
            foreach (var file in manifest.Files)
            {
                var current = new FileInfo(file.Path);
                if (!current.Exists || current.Length != file.Length || current.LastWriteTimeUtc != file.LastWriteTimeUtc)
                    throw new IOException("Um arquivo de origem mudou durante a cópia; ele foi mantido.");
                if (current.IsReadOnly)
                    current.IsReadOnly = false;
                File.Delete(file.Path);
            }
            for (int i = manifest.Folders.Count - 1; i >= 0; i--)
            {
                string folder = manifest.Folders[i];
                File.SetAttributes(folder, File.GetAttributes(folder) & ~FileAttributes.ReadOnly);
                Directory.Delete(folder, recursive: false);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"A cópia foi concluída, mas parte da origem foi mantida: {ex.Message}";
        }
    }

    private static void ClearReadOnly(string path, bool isFolder)
    {
        var entries = isFolder
            ? new DirectoryInfo(path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Append(new DirectoryInfo(path))
            : [new FileInfo(path)];
        foreach (var entry in entries)
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                entry.Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            bool isFolder = Directory.Exists(path);
            if (!isFolder && !File.Exists(path))
                return;
            ClearReadOnly(path, isFolder);
            if (isFolder)
                Directory.Delete(path, recursive: true);
            else
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a visibly named "~pomelo-*.part" may remain.
        }
    }

    private static string TempName(string destination) => Path.Combine(destination, $"~pomelo-{Guid.NewGuid():N}.part");

    /// <summary>"E:\" → "Pendrive (E)" or "Disco E"; "\\server\share\" → "share".</summary>
    internal static string RootName(string root)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(root);
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal))
            return trimmed[(trimmed.LastIndexOf('\\') + 1)..];
        string letter = trimmed.TrimEnd('\\', '/', ':');
        string label = "";
        try
        {
            label = new DriveInfo(root).VolumeLabel;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not ready (e.g. empty card reader): fall back to the letter.
        }
        return label.Length > 0 ? $"{label} ({letter})" : $"Disco {letter}";
    }
}
