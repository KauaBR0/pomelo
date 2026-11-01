using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pomelo.Interop;

internal static class Shell
{
    /// <summary>Opens Explorer with <paramref name="path"/> selected. No-op if it no longer exists.</summary>
    public static void Reveal(string path)
    {
        if (Path.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }

    /// <summary>The small icon Explorer shows for <paramref name="path"/>; null if it has none.</summary>
    public static ImageSource? SmallIcon(string path)
    {
        var info = new SHFILEINFOW();
        if (SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), SHGFI_ICON | SHGFI_SMALLICON) == 0
            || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            var icon = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            icon.Freeze();
            return icon;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attributes, ref SHFILEINFOW info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
