using Pomelo.Interop;
using Forms = System.Windows.Forms;

namespace Pomelo.Tray;

/// <summary>Notification-area icon: open, toggle the wheel, quit, and conversion notifications.</summary>
internal sealed class TrayIcon : IDisposable
{
    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public event Action<bool>? WheelToggled;

    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _wheelItem;
    private readonly System.Drawing.Icon _image;
    private string? _notificationRevealPath;
    private string? _conversionStatus;
    private readonly List<ActionStatus> _actionStatuses = [];

    private sealed class ActionStatus(TrayIcon owner, string text) : IDisposable
    {
        public string Text { get; } = text;
        public void Dispose()
        {
            owner._actionStatuses.Remove(this);
            owner.RefreshStatus();
        }
    }

    public TrayIcon()
    {
        using (var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Pomelo.ico")).Stream)
            _image = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);

        var open = new Forms.ToolStripMenuItem("Abrir Pomelo", null, (_, _) => OpenRequested?.Invoke());
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        _wheelItem = new Forms.ToolStripMenuItem("Roda ativada") { CheckOnClick = true };
        _wheelItem.Click += (_, _) => WheelToggled?.Invoke(_wheelItem.Checked);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange([open, _wheelItem, new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem("Sair", null, (_, _) => ExitRequested?.Invoke())]);

        _icon = new Forms.NotifyIcon { Icon = _image, Text = "Pomelo", ContextMenuStrip = menu };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                OpenRequested?.Invoke();
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_notificationRevealPath is { } path)
                Shell.Reveal(path);
            else
                OpenRequested?.Invoke();
        };
        _icon.Visible = true;
    }

    public void SetWheelEnabled(bool enabled) => _wheelItem.Checked = enabled;

    /// <summary>Conversion progress remains available while independent actions run.</summary>
    public void SetStatus(string? status)
    {
        _conversionStatus = status;
        RefreshStatus();
    }

    public IDisposable BeginActionStatus(string text)
    {
        var status = new ActionStatus(this, text);
        _actionStatuses.Add(status);
        RefreshStatus();
        return status;
    }

    private void RefreshStatus()
    {
        string? status = _actionStatuses.LastOrDefault()?.Text ?? _conversionStatus;
        string text = status is null ? "Pomelo" : $"Pomelo — {status}";
        _icon.Text = text.Length <= 127 ? text : text[..126] + "…"; // NotifyIcon limit.
    }

    /// <param name="revealPath">Selected in Explorer when the notification is clicked; null opens Pomelo.</param>
    public void Notify(string title, string text, string? revealPath)
    {
        _notificationRevealPath = revealPath;
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        _icon.Visible = false; // Otherwise a dead icon lingers until the user hovers it.
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _image.Dispose();
    }
}
