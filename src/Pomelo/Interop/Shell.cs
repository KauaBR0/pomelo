using System.Diagnostics;

namespace Pomelo.Interop;

internal static class Shell
{
    /// <summary>Opens Explorer with <paramref name="path"/> selected. No-op if it no longer exists.</summary>
    public static void Reveal(string path)
    {
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }
}
