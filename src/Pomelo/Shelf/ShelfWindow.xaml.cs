using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Pomelo.Core.Shelf;
using Pomelo.Interop;
using static Pomelo.Interop.NativeMethods;

namespace Pomelo.Shelf;

/// <summary>
/// "Flutuar": a floating shelf that holds files and folders (by path, never copies) so they can
/// be dragged somewhere later, together or one by one. Dragging out only ever offers Copy, so a
/// drop never removes an original. Never takes focus away from the app being used.
/// </summary>
internal sealed partial class ShelfWindow : Window
{
    private static readonly Brush IdleEdge = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly Brush DropEdge = Frozen(Color.FromRgb(0xA9, 0x8B, 0xFF));
    private static readonly Brush RowHover = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));

    private readonly ShelfItems _items = new();
    private bool _draggingOut;
    private bool _placed;
    private Point? _pressed;
    private Func<IReadOnlyList<string>>? _pressedPaths;

    public ShelfWindow()
    {
        InitializeComponent();
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));

        Header.MouseLeftButtonDown += (_, _) => DragMove();
        CloseButton.Click += (_, _) => Empty();
        DragAll.PreviewMouseLeftButtonDown += (_, e) => Press(e, () => _items.Available());
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseLeftButtonUp += (_, _) => _pressed = null;
        MouseEnter += (_, _) => Refresh(); // Catch items moved or deleted while the shelf sat idle.
        // Grows downward with each item; never under the taskbar. Deferred: SizeChanged fires before
        // the native window takes its new size.
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(KeepInWorkArea, System.Windows.Threading.DispatcherPriority.Background);

        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => Panel.BorderBrush = IdleEdge;
        Drop += OnDrop;
    }

    /// <summary>Puts <paramref name="paths"/> on the shelf and shows it.</summary>
    public void Add(IReadOnlyList<string> paths)
    {
        _items.Add(paths);
        Refresh();
        if (!IsVisible)
        {
            Show();
            if (!_placed)
                PlaceNearCursor();
        }
    }

    private void Empty()
    {
        _items.Clear();
        Hide();
    }

    private void Refresh()
    {
        if (_items.Count == 0)
        {
            Hide();
            return;
        }
        var available = _items.Available();
        CountText.Text = _items.Count == 1 ? "1 item" : $"{_items.Count} itens";
        DragAll.IsEnabled = available.Count > 0;
        DragAll.Opacity = available.Count > 0 ? 1 : 0.45;
        DragAllTitle.Text = available.Count switch
        {
            0 => "Nada disponível para arrastar",
            1 when _items.Count == 1 => "Arraste daqui",
            _ when available.Count == _items.Count => $"Arraste os {available.Count} daqui",
            _ => $"Arraste os {available.Count} disponíveis",
        };

        List.Children.Clear();
        foreach (string path in _items.Paths)
            List.Children.Add(Row(path, available.Contains(path)));
    }

    private FrameworkElement Row(string path, bool available)
    {
        string name = Path.GetFileName(path) is { Length: > 0 } file ? file : path; // Drive roots have no name.
        var remove = new Button
        {
            Content = "✕", FontSize = 10, Style = (Style)Resources["IconButton"],
            ToolTip = "Tirar da prateleira (o arquivo não é apagado)",
        };
        remove.Click += (_, _) =>
        {
            _items.Remove(path);
            Refresh();
        };
        DockPanel.SetDock(remove, Dock.Right);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5 });
        if (!available)
            text.Children.Add(new TextBlock { Text = "não encontrado (movido ou apagado)", FontSize = 10.5, Foreground = Brushes.Salmon });

        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 0), Opacity = available ? 1 : 0.6 };
        row.Children.Add(remove);
        row.Children.Add(new Image
        {
            Source = available ? Shell.SmallIcon(path) : null, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0),
        });
        row.Children.Add(text);

        var border = new Border
        {
            Child = row, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 4, 2, 4),
            Background = Brushes.Transparent, ToolTip = path, Cursor = available ? Cursors.Hand : null,
        };
        border.MouseEnter += (_, _) => border.Background = RowHover;
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        if (available)
            border.PreviewMouseLeftButtonDown += (_, e) => Press(e, () => [path]);
        return border;
    }

    private void Press(MouseButtonEventArgs e, Func<IReadOnlyList<string>> paths)
    {
        if (e.OriginalSource is DependencyObject source && FindButton(source))
            return; // The remove/close buttons handle their own clicks.
        _pressed = e.GetPosition(this);
        _pressedPaths = paths;
    }

    private static bool FindButton(DependencyObject? element)
    {
        for (; element is not null; element = element is Visual ? VisualTreeHelper.GetParent(element) : null)
            if (element is Button)
                return true;
        return false;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed is not { } start || _pressedPaths is null || e.LeftButton != MouseButtonState.Pressed)
            return;
        Vector moved = e.GetPosition(this) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _pressed = null;
        DragOut(_pressedPaths().Where(Path.Exists).ToArray());
    }

    private void DragOut(string[] paths)
    {
        if (paths.Length == 0)
            return;
        var data = new DataObject(DataFormats.FileDrop, paths);
        // Explorer copies (DROPEFFECT_COPY) rather than moves, even across folders on one drive.
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        _draggingOut = true;
        try
        {
            DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        }
        catch (COMException)
        {
            // The drop target failed; the shelf keeps its items.
        }
        finally
        {
            _draggingOut = false;
            Refresh();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool accept = !_draggingOut && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 }
                      && e.AllowedEffects.HasFlag(DragDropEffects.Copy);
        // Copy (not Link/Move): the source keeps its files, and Explorer shows a "+" cursor.
        e.Effects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        Panel.BorderBrush = accept ? DropEdge : IdleEdge;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        Panel.BorderBrush = IdleEdge;
        if (_draggingOut || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        // Return from Drop at once: the drag source (Explorer) waits until we do.
        Dispatcher.BeginInvoke(() => Add(paths));
    }

    /// <summary>First appearance: bottom-right of the work area of the monitor under the cursor.</summary>
    private void PlaceNearCursor()
    {
        GetCursorPos(out POINT cursor);
        IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(monitor, ref info);
        uint dpi = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 ? dpiX : 96;
        double scale = dpi / 96.0;

        int x = info.rcWork.Right - (int)Math.Ceiling(ActualWidth * scale);
        int y = info.rcWork.Bottom - (int)Math.Ceiling(ActualHeight * scale);
        SetWindowPos(new WindowInteropHelper(this).Handle, HWND_TOPMOST, x, y, 0, 0, SWP_NOACTIVATE | SWP_NOSIZE);
        _placed = true;
    }

    /// <summary>Moves the shelf up (never resizes it) when its bottom passes the monitor's work area.</summary>
    private void KeepInWorkArea()
    {
        if (!_placed)
            return;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        GetWindowRect(hwnd, out RECT window);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST), ref info);
        int overflow = window.Bottom - info.rcWork.Bottom;
        if (overflow > 0)
            SetWindowPos(hwnd, HWND_TOPMOST, window.Left, Math.Max(info.rcWork.Top, window.Top - overflow), 0, 0,
                SWP_NOACTIVATE | SWP_NOSIZE);
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
