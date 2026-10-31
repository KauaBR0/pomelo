using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Pomelo.Core.Conversion;
using Pomelo.Core.Formats;
using Pomelo.Core.Media;
using static Pomelo.Interop.NativeMethods;

namespace Pomelo.Wheel;

/// <summary>
/// Topmost, never-activated overlay that is itself the OLE drop target. Windows has no global
/// "drag pasteboard" like macOS, so the wheel learns the dragged files from DragEnter: it opens
/// invisibly under the cursor, and only draws once a file drag actually enters it.
/// </summary>
internal sealed partial class WheelWindow : Window
{
    private static readonly TimeSpan ReleaseGrace = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan NoTargetsLinger = TimeSpan.FromMilliseconds(1200);

    /// <summary>Raised after the wheel closes, with the dropped files and chosen target.</summary>
    public event Action<IReadOnlyList<string>, FileFormat>? ConversionRequested;

    /// <summary>Current FFmpeg, read when files enter the wheel; null hides audio/video targets.</summary>
    public Func<FfmpegInfo?> Ffmpeg { get; set; } = () => null;

    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _hideTimer;
    private IReadOnlyList<string> _paths = [];
    private IReadOnlyList<FileFormat> _targets = [];
    private string? _hint;
    private bool _revealed;

    public WheelWindow()
    {
        InitializeComponent();
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        long style = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));

        _hideTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _hideTimer.Tick += (_, _) => Dismiss();

        DragEnter += OnDragEnter;
        DragOver += (_, e) => UpdateHover(e);
        DragLeave += (_, _) => ShowHover(-1);
        Drop += OnDrop;
    }

    /// <summary>Opens the (still invisible) wheel centred on the cursor.</summary>
    public void OpenAtCursor()
    {
        _hideTimer.Stop();
        _paths = [];
        _targets = [];
        _hint = null;
        _revealed = false;
        View.Labels = [];
        View.BeginAnimation(OpacityProperty, null);
        View.Opacity = 0;

        GetCursorPos(out POINT cursor);
        IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(monitor, ref info);
        uint dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 ? dpiX : 96;
        int size = (int)Math.Round(Width * dpi / 96.0);

        RECT work = info.rcWork;
        int x = Math.Max(work.Left, Math.Min(cursor.X - size / 2, work.Right - size));
        int y = Math.Max(work.Top, Math.Min(cursor.Y - size / 2, work.Bottom - size));

        Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, size, size, SWP_NOACTIVATE);
    }

    /// <summary>The mouse button went up. A drop on the wheel may still be in flight.</summary>
    public void EndGesture()
    {
        if (IsVisible && !_hideTimer.IsEnabled)
            HideAfter(ReleaseGrace);
    }

    public void Dismiss()
    {
        _hideTimer.Stop();
        Hide();
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!_revealed)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
            {
                // Not a file drag (text, browser link…): get out of the way of the real target.
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                Dispatcher.BeginInvoke(Dismiss);
                return;
            }
            Reveal(paths);
        }
        UpdateHover(e);
    }

    private void Reveal(string[] paths)
    {
        _revealed = true;
        _paths = paths;
        (_targets, _hint) = ConversionCatalog.For(paths, Ffmpeg());
        View.Labels = _targets.Select(t => t.Name).ToList();
        ShowHover(-1);

        if (SystemParameters.ClientAreaAnimation)
        {
            var duration = new Duration(TimeSpan.FromMilliseconds(130));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            View.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
            var scale = new DoubleAnimation(0.88, 1, duration) { EasingFunction = ease };
            ViewScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scale);
            ViewScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scale);
        }
        else
        {
            View.Opacity = 1;
        }

        if (_targets.Count == 0)
            HideAfter(NoTargetsLinger);
    }

    private void UpdateHover(DragEventArgs e)
    {
        int sector = _revealed ? View.SectorAt(e.GetPosition(View)) : -1;
        ShowHover(sector);
        e.Effects = sector >= 0 && e.AllowedEffects.HasFlag(DragDropEffects.Copy)
            ? DragDropEffects.Copy // Never Move: Shift means "move" in Explorer, and we must not delete originals.
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ShowHover(int sector)
    {
        View.Hover = sector;
        string title = _paths.Count == 1 ? "1 arquivo" : $"{_paths.Count} arquivos";
        string subtitle = sector >= 0 ? $"→ {_targets[sector].Name}"
            : _targets.Count > 0 ? "Converter para…"
            : _hint ?? "Sem conversões";
        View.SetHub(title, subtitle);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        int sector = _revealed ? View.SectorAt(e.GetPosition(View)) : -1;
        if (sector >= 0 && e.AllowedEffects.HasFlag(DragDropEffects.Copy))
        {
            e.Effects = DragDropEffects.Copy;
            var paths = _paths;
            var target = _targets[sector];
            // Return from Drop immediately: the drag source (Explorer) is blocked until we do.
            Dispatcher.BeginInvoke(() =>
            {
                Dismiss();
                ConversionRequested?.Invoke(paths, target);
            });
        }
        else
        {
            e.Effects = DragDropEffects.None;
            Dispatcher.BeginInvoke(Dismiss);
        }
        e.Handled = true;
    }

    private void HideAfter(TimeSpan delay)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = delay;
        _hideTimer.Start();
    }
}
