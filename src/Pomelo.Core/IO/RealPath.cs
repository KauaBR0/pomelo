using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pomelo.Core.IO;

/// <summary>
/// Canonical location of an existing file or folder, resolving junctions, symlinks, subst
/// drives, 8.3 names and letter case, so two spellings of one place compare equal.
/// </summary>
public static class RealPath
{
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000; // Required to open folders.
    private const uint VOLUME_NAME_DOS = 0x0;
    private const uint VOLUME_NAME_GUID = 0x1;

    /// <returns>"\\?\Volume{…}\…" when the volume has a GUID (local disks), otherwise the
    /// final DOS/UNC path; the full path if the item cannot be opened.</returns>
    public static string Of(string path)
    {
        using SafeFileHandle handle = CreateFileW(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            FileMode.Open, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid)
            return Path.GetFullPath(path);
        return Final(handle, VOLUME_NAME_GUID) ?? Final(handle, VOLUME_NAME_DOS) ?? Path.GetFullPath(path);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies anywhere below it.</summary>
    public static bool IsSameOrInside(string path, string folder) =>
        WithSeparator(path).StartsWith(WithSeparator(folder), StringComparison.OrdinalIgnoreCase);

    public static bool Same(string a, string b) =>
        string.Equals(WithSeparator(a), WithSeparator(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Roots ("C:\") already end with a separator; adding another would break prefix tests.</summary>
    private static string WithSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private static string? Final(SafeFileHandle handle, uint flags)
    {
        var buffer = new char[1024];
        while (true)
        {
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, flags);
            if (length == 0)
                return null;
            if (length < buffer.Length)
                return new string(buffer, 0, (int)length);
            buffer = new char[length + 1];
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security,
        FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);
}
