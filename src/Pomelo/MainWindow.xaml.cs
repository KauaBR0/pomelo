using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Pomelo.Conversion;
using Pomelo.Core.Media;
using Pomelo.Core.Settings;
using Pomelo.Core.Startup;
using Pomelo.Interop;
using Pomelo.Wheel;

namespace Pomelo;

/// <summary>Settings and activity log. Closing hides it to the tray unless the app is exiting.</summary>
internal sealed partial class MainWindow : Window
{
    /// <summary>Raised with the full new settings after any user change in this window.</summary>
    public event Action<AppSettings>? SettingsChanged;
    /// <summary>Raised when the user closes the window and it hides to the tray.</summary>
    public event Action? HiddenToTray;
    /// <summary>The user asked to search for FFmpeg again.</summary>
    public event Action? FfmpegRecheckRequested;
    /// <summary>The user asked to install FFmpeg via winget.</summary>
    public event Action? FfmpegInstallRequested;

    /// <summary>Set before Application.Shutdown so closing really closes.</summary>
    public bool AllowClose { get; set; }

    private sealed record LogEntry(string Text, string? RevealPath)
    {
        public override string ToString() => Text;
    }

    private readonly StartupRegistration _startup;
    private AppSettings _settings;
    private bool _loading;

    public MainWindow(AppSettings settings, StartupRegistration startup)
    {
        InitializeComponent();
        _startup = startup;
        _settings = settings;
        foreach (var box in (System.Windows.Controls.ComboBox[])[ConvertKeyBox, ToolsKeyBox, ActionsKeyBox])
            box.ItemsSource = Enum.GetValues<WheelKey>();
        ShowSettings(settings);

        // Checked/Unchecked (not Click) so keyboard, mouse and UI Automation toggles all count.
        OnToggled(WheelEnabledBox, on => Update(s => s with { WheelEnabled = on }));
        OnToggled(NotifyBox, on => Update(s => s with { NotifyOnCompletion = on }));
        // Picking another wheel's key swaps the two, so each wheel keeps its own key.
        OnKeyPicked(ConvertKeyBox, WheelMode.Convert);
        OnKeyPicked(ToolsKeyBox, WheelMode.Tools);
        OnKeyPicked(ActionsKeyBox, WheelMode.Actions);
        QualitySlider.ValueChanged += (_, e) => Update(s => s with { JpegQuality = (int)e.NewValue });

        BesideBox.Checked += (_, _) => Update(s => s with { OutputLocation = OutputLocation.BesideOriginal });
        DownloadsBox.Checked += (_, _) => Update(s => s with { OutputLocation = OutputLocation.Downloads });
        DesktopBox.Checked += (_, _) => Update(s => s with { OutputLocation = OutputLocation.Desktop });
        CustomBox.Checked += (_, _) =>
        {
            if (_loading)
                return;
            if (string.IsNullOrEmpty(_settings.CustomOutputFolder))
                PickCustomFolder(); // Nothing to use yet: ask now, or fall back to the previous choice.
            else
                Update(s => s with { OutputLocation = OutputLocation.CustomFolder });
        };
        PickFolderButton.Click += (_, _) => PickCustomFolder();
        FfmpegPickButton.Click += (_, _) => PickFfmpeg();
        FfmpegInstallButton.Click += (_, _) => FfmpegInstallRequested?.Invoke();
        FfmpegRecheckButton.Click += (_, _) => FfmpegRecheckRequested?.Invoke();

        OnToggled(StartupBox, SetStartup);
        Activated += (_, _) => RefreshStartup(); // Task Manager can change it while we run.
    }

    /// <summary>Reflects settings changed elsewhere (e.g. the tray menu) without echoing them back.</summary>
    public void ShowSettings(AppSettings settings)
    {
        _settings = settings;
        _loading = true;
        try
        {
            WheelEnabledBox.IsChecked = settings.WheelEnabled;
            ConvertKeyBox.SelectedItem = settings.ConvertKey;
            ToolsKeyBox.SelectedItem = settings.ToolsKey;
            ActionsKeyBox.SelectedItem = settings.ActionsKey;
            QualitySlider.Value = settings.JpegQuality;
            NotifyBox.IsChecked = settings.NotifyOnCompletion;
            CustomFolderText.Text = settings.CustomOutputFolder ?? "Outra pasta…";
            BesideBox.IsChecked = settings.OutputLocation == OutputLocation.BesideOriginal;
            DownloadsBox.IsChecked = settings.OutputLocation == OutputLocation.Downloads;
            DesktopBox.IsChecked = settings.OutputLocation == OutputLocation.Desktop;
            CustomBox.IsChecked = settings.OutputLocation == OutputLocation.CustomFolder;
            RefreshStartup();
        }
        finally
        {
            _loading = false;
        }
        HowTo.Text = "Comece a arrastar arquivos no Explorer ou na Área de Trabalho e toque em " +
                     $"{settings.ConvertKey} para converter o formato, {settings.ToolsKey} para as ferramentas " +
                     $"(reduzir, girar, comprimir…) ou {settings.ActionsKey} para as ações (copiar caminho, mover, " +
                     "compartilhar…). Solte sobre a opção desejada; Esc cancela.";
    }

    private void OnKeyPicked(System.Windows.Controls.ComboBox box, WheelMode wheel) =>
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is WheelKey key)
                Update(s => WithKey(s, wheel, key));
        };

    private static AppSettings WithKey(AppSettings settings, WheelMode wheel, WheelKey key)
    {
        WheelKey[] keys = [settings.ConvertKey, settings.ToolsKey, settings.ActionsKey];
        int other = Array.IndexOf(keys, key);
        if (other >= 0)
            keys[other] = keys[(int)wheel];
        keys[(int)wheel] = key;
        return settings with { ConvertKey = keys[0], ToolsKey = keys[1], ActionsKey = keys[2] };
    }

    /// <param name="info">The FFmpeg in use, or null.</param>
    /// <param name="busy">A search or install is running.</param>
    public void ShowFfmpeg(FfmpegInfo? info, bool busy)
    {
        FfmpegStatus.Text = busy ? "Procurando o FFmpeg…"
            : info is null ? "FFmpeg não encontrado. Áudio e vídeo ficam indisponíveis até instalar ou escolher o ffmpeg.exe."
            : $"FFmpeg {info.Version}: {info.Path}";
        FfmpegInstallButton.IsEnabled = !busy && info is null;
        FfmpegRecheckButton.IsEnabled = !busy;
        FfmpegPickButton.IsEnabled = !busy;
    }

    public void Report(ConversionResult result)
    {
        string time = DateTime.Now.ToString("HH:mm:ss");
        var entry = result.Error is null
            ? new LogEntry($"{time}  ✓ {Path.GetFileName(result.Source)} → {result.Output}", result.Output)
            : new LogEntry($"{time}  ✗ {Path.GetFileName(result.Source)}: {result.Error}", result.Source);
        Log.Items.Insert(0, entry);
    }

    /// <summary>Shows, restores and focuses the window.</summary>
    public void Present()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void Update(Func<AppSettings, AppSettings> change)
    {
        if (_loading)
            return;
        var updated = change(_settings);
        if (updated == _settings)
            return;
        ShowSettings(updated);
        SettingsChanged?.Invoke(updated);
    }

    private void PickFfmpeg()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Escolha o ffmpeg.exe",
            Filter = "ffmpeg.exe|ffmpeg.exe|Programas (*.exe)|*.exe",
            FileName = _settings.FfmpegPath ?? "",
        };
        if (dialog.ShowDialog(this) == true)
            Update(s => s with { FfmpegPath = dialog.FileName });
    }

    private void PickCustomFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Pasta para os arquivos convertidos",
            InitialDirectory = _settings.CustomOutputFolder ?? "",
        };
        if (dialog.ShowDialog(this) == true)
            Update(s => s with { OutputLocation = OutputLocation.CustomFolder, CustomOutputFolder = dialog.FolderName });
        else
            ShowSettings(_settings); // Undo a radio selection that has no folder behind it.
    }

    private void SetStartup(bool enabled)
    {
        try
        {
            _startup.SetEnabled(enabled);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            MessageBox.Show(this, $"Não foi possível alterar a inicialização com o Windows.\n\n{ex.Message}", "Pomelo",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        RefreshStartup();
    }

    private void RefreshStartup()
    {
        bool wasLoading = _loading;
        _loading = true;
        StartupBox.IsChecked = _startup.IsEnabled;
        _loading = wasLoading;
    }

    private void OnToggled(System.Windows.Controls.CheckBox box, Action<bool> handler)
    {
        box.Checked += (_, _) => { if (!_loading) handler(true); };
        box.Unchecked += (_, _) => { if (!_loading) handler(false); };
    }

    private void OnLogDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Log.SelectedItem is LogEntry { RevealPath: { } path })
            Shell.Reveal(path);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
        }
        base.OnClosing(e);
    }
}
