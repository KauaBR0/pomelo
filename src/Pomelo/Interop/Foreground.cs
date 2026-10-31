using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Pomelo.Interop;

/// <summary>
/// After a drop, the drag source (Explorer) owns the foreground, so Windows would open our dialogs
/// behind it. Attaching to the foreground thread's input lets us legitimately take focus.
/// </summary>
internal static class Foreground
{
    public static void Take(IntPtr hwnd)
    {
        IntPtr current = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(current, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != thisThread
                        && AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(thisThread, foregroundThread, false);
        }
    }

    /// <summary>
    /// An invisible 1×1 window at the cursor, brought to the foreground, to own dialogs and the
    /// share sheet. Dispose closes it.
    /// </summary>
    public static Host CreateHost() => new();

    internal sealed class Host : IDisposable
    {
        public Window Window { get; }
        public IntPtr Handle { get; }

        public Host()
        {
            NativeMethods.GetCursorPos(out var cursor);
            Window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
                Topmost = true,
                Width = 1,
                Height = 1,
                Title = "Pomelo",
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            Handle = new WindowInteropHelper(Window).EnsureHandle();
            Window.Show();
            NativeMethods.SetWindowPos(Handle, NativeMethods.HWND_TOPMOST, cursor.X, cursor.Y, 1, 1, NativeMethods.SWP_NOACTIVATE);
            Take(Handle);
        }

        public void Dispose() => Window.Close();
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
