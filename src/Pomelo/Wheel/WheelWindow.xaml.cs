using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Pomelo.Core.Conversion;
using Pomelo.Core.Formats;
using Pomelo.Core.Media;
using Pomelo.Core.Tools;
using Pomelo.Actions;
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

    /// <summary>Raised after the wheel closes, with the dropped files and the chosen conversion or tool.</summary>
    public event Action<IReadOnlyList<string>, IFileOperation>? OperationRequested;
    /// <summary>Raised after the wheel closes, with the dropped items and the chosen action.</summary>
    public event Action<IReadOnlyList<string>, FileAction>? ActionRequested;
    /// <summary>Raised after the wheel closes when "Editar textos" was chosen for a single PDF.</summary>
    public event Action<string>? PdfEditRequested;

    /// <summary>Current FFmpeg, read when files enter the wheel; null hides audio/video targets.</summary>
    public Func<FfmpegInfo?> Ffmpeg { get; set; } = () => null;
    /// <summary>Actions offered for the dropped items.</summary>
    public Func<IReadOnlyList<string>, IReadOnlyList<FileAction>> Actions { get; set; } = _ => [];

    /// <summary>One sector: its text and what dropping on it does.</summary>
    private sealed record WheelItem(string Label, string HubText, Action<IReadOnlyList<string>> Execute);

    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _hideTimer;
    private IReadOnlyList<string> _paths = [];
    private IReadOnlyList<WheelItem> _items = [];
    private string? _hint;
    private string _title = "";
    private bool _acceptingDrop;
    private bool _revealed;
    private WheelMode _mode;
    private bool _inGesture; // Between a key press and the mouse button going up.

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

    /// <summary>Opens <paramref name="mode"/>'s wheel under the cursor, or switches to it if a wheel is open.</summary>
    public void Request(WheelMode mode)
    {
        if (IsVisible && _inGesture)
            SwitchTo(mode);
        else
            OpenAtCursor(mode);
    }

    /// <summary>Opens the (still invisible) wheel centred on the cursor.</summary>
    private void OpenAtCursor(WheelMode mode)
    {
        _hideTimer.Stop();
        _mode = mode;
        _inGesture = true;
        _paths = [];
        _items = [];
        _hint = null;
        _revealed = false;
        _acceptingDrop = false;
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

        // Let WPF process the monitor's DPI change before applying the final pixel rectangle.
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE);
        Show();
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, size, size, SWP_NOACTIVATE);
    }

    /// <summary>The mouse button went up. A drop on the wheel may still be in flight.</summary>
    public void EndGesture()
    {
        _inGesture = false;
        if (!_revealed)
            Dismiss();
        else if (IsVisible && !_hideTimer.IsEnabled)
            HideAfter(ReleaseGrace);
    }

    public void Dismiss()
    {
        _inGesture = false;
        _acceptingDrop = false;
        _hideTimer.Stop();
        Hide();
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!_inGesture)
        {
            // The grace period belongs only to the previous drag's pending Drop.
            _acceptingDrop = false;
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            Dispatcher.BeginInvoke(Dismiss);
            return;
        }
        _acceptingDrop = true;
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
        _title = paths.Length == 1
            ? (Directory.Exists(paths[0]) ? "1 pasta" : "1 arquivo")
            : (paths.Any(Directory.Exists) ? $"{paths.Length} itens" : $"{paths.Length} arquivos");
        LoadOperations();

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
    }

    private void SwitchTo(WheelMode mode)
    {
        if (mode == _mode)
            return;
        _mode = mode;
        if (!_revealed)
            return; // Files not known yet; DragEnter will load this mode.
        _hideTimer.Stop(); // Only the "nothing to offer" linger can be running mid-drag.
        LoadOperations();
    }

    private void LoadOperations()
    {
        switch (_mode)
        {
            case WheelMode.Convert:
                var conversions = ConversionCatalog.For(_paths, Ffmpeg());
                _items = FromOperations(conversions.Targets.Select(ConvertOperation.For));
                _hint = conversions.Hint;
                break;
            case WheelMode.Tools:
                if (_paths is [var pdf] && DocumentFormats.Pdf.Matches(pdf) && File.Exists(pdf))
                {
                    // Interactive, so not an IFileOperation: it opens a window instead of producing a file.
                    _items = [new WheelItem("Editar textos", "Editar os textos do PDF", p => PdfEditRequested?.Invoke(p[0]))];
                    _hint = null;
                    break;
                }
                var tools = ToolCatalog.For(_paths, Ffmpeg());
                _items = FromOperations(tools.Tools);
                _hint = tools.Hint;
                break;
            default:
                _items = Actions(_paths).Select(a => new WheelItem(a.Label, a.HubText, p => ActionRequested?.Invoke(p, a))).ToList();
                _hint = null;
                break;
        }
        View.Highlight = _mode switch
        {
            WheelMode.Convert => WheelView.ConvertAccent,
            WheelMode.Tools => WheelView.ToolsAccent,
            _ => WheelView.ActionsAccent,
        };
        View.Labels = _items.Select(i => i.Label).ToList();
        ShowHover(-1);
        if (_items.Count == 0)
            HideAfter(NoTargetsLinger);
    }

    private List<WheelItem> FromOperations(IEnumerable<IFileOperation> operations) =>
        operations.Select(o => new WheelItem(o.Label, o.HubText, paths => OperationRequested?.Invoke(paths, o))).ToList();

    private void UpdateHover(DragEventArgs e)
    {
        int sector = _revealed && _acceptingDrop ? View.SectorAt(e.GetPosition(View)) : -1;
        ShowHover(sector);
        e.Effects = sector >= 0 && e.AllowedEffects.HasFlag(DragDropEffects.Copy)
            ? DragDropEffects.Copy // Never Move: Shift means "move" in Explorer, and we must not delete originals.
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void ShowHover(int sector)
    {
        View.Hover = sector;
        string subtitle = sector >= 0 ? _items[sector].HubText
            : _items.Count > 0 ? _mode switch { WheelMode.Convert => "Converter para…", WheelMode.Tools => "Ferramentas", _ => "Ações" }
            : _hint ?? _mode switch { WheelMode.Convert => "Sem conversões", WheelMode.Tools => "Sem ferramentas", _ => "Sem ações" };
        View.SetHub(_title, subtitle);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        int sector = _revealed && _acceptingDrop ? View.SectorAt(e.GetPosition(View)) : -1;
        if (sector >= 0 && e.AllowedEffects.HasFlag(DragDropEffects.Copy))
        {
            e.Effects = DragDropEffects.Copy;
            var paths = _paths;
            var item = _items[sector];
            // Return from Drop immediately: the drag source (Explorer) is blocked until we do.
            Dispatcher.BeginInvoke(() =>
            {
                Dismiss();
                item.Execute(paths);
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
