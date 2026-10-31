using System.Runtime.InteropServices;

namespace Pomelo.Core.Settings;

public static class OutputFolders
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <returns>The folder for converted files, or null to write beside each original.</returns>
    public static string? Resolve(AppSettings settings) => settings.OutputLocation switch
    {
        OutputLocation.Downloads => Downloads(),
        OutputLocation.Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        OutputLocation.CustomFolder when !string.IsNullOrWhiteSpace(settings.CustomOutputFolder) => settings.CustomOutputFolder,
        _ => null,
    };

    /// <summary>The user's Downloads folder, honouring a relocated folder.</summary>
    public static string Downloads()
    {
        int hr = SHGetKnownFolderPath(DownloadsFolderId, 0, IntPtr.Zero, out IntPtr path);
        try
        {
            Marshal.ThrowExceptionForHR(hr);
            return Marshal.PtrToStringUni(path)!;
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
