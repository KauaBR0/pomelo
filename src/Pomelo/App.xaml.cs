using System.Diagnostics;
using System.Windows;
using Pomelo.Actions;
using Pomelo.Conversion;
using Pomelo.Core.Conversion;
using Pomelo.Core.Media;
using Pomelo.Core.Settings;
using Pomelo.Core.Startup;
using Pomelo.Input;
using Pomelo.Interop;
using Pomelo.Shelf;
using Pomelo.Tray;
using Pomelo.Wheel;

namespace Pomelo;

/// <summary>
/// Lives in the tray. "--background" (used by start-with-Windows) starts without the window;
/// launching again while running opens the existing window instead of a second instance.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = @"Local\Pomelo.SingleInstance";
    private const string ActivateEventName = @"Local\Pomelo.Activate";

    private Mutex? _singleInstance;
    private EventWaitHandle? _activate;
    private RegisteredWaitHandle? _activateWait;
    private SettingsStore _store = null!;
    private AppSettings _settings = null!;
    private GestureMonitor? _gestures;
    private ConversionWorker? _worker;
    private ActionRunner? _actions;
    private TrayIcon? _tray;
    private WheelWindow _wheel = null!;
    private MainWindow _main = null!;
    private volatile FfmpegInfo? _ffmpeg;
    private int _ffmpegSearch; // Ignores results of a superseded search.
    private readonly CancellationTokenSource _shutdown = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _singleInstance = new Mutex(true, InstanceName, out bool first);
        if (!first)
        {
            ForegroundRights.GrantToAnyProcess();
            _activate.Set();
            Shutdown();
            return;
        }

        _store = new SettingsStore(SettingsStore.DefaultPath);
        _settings = _store.Load();

        _wheel = new WheelWindow();
        _worker = new ConversionWorker(Dispatcher);
        _gestures = new GestureMonitor(Dispatcher);
        _tray = new TrayIcon();
        _main = new MainWindow(_settings, StartupRegistration.ForCurrentExecutable());
        MainWindow = _main;

        _gestures.WheelRequested += _wheel.Request;
        _gestures.GestureEnded += _wheel.EndGesture;
        _gestures.Cancelled += _wheel.Dismiss;
        _wheel.Ffmpeg = () => _ffmpeg;
        _wheel.OperationRequested += (paths, operation) => _worker.Enqueue(new ConversionBatch(paths, operation,
            new ConversionOptions(OutputFolders.Resolve(_settings), _settings.JpegQuality), _ffmpeg));
        _actions = new ActionRunner(() => _settings, Apply, _tray, _main, new ShelfWindow(), _shutdown.Token);
        _wheel.Actions = _actions.For;
        _wheel.ActionRequested += (paths, action) => _actions.Run(action, paths);
        _worker.FileStarted += (path, index, count) =>
            _tray.SetStatus(count == 1 ? $"processando {Path.GetFileName(path)}" : $"processando {Path.GetFileName(path)} ({index}/{count})");
        _worker.FileCompleted += _main.Report;
        _worker.BatchCompleted += (batch, results) =>
        {
            _tray.SetStatus(null);
            NotifyBatch(batch, results);
        };

        _main.SettingsChanged += Apply;
        _main.HiddenToTray += OnHiddenToTray;
        _main.FfmpegRecheckRequested += DetectFfmpeg;
        _main.FfmpegInstallRequested += InstallFfmpeg;
        _tray.OpenRequested += _main.Present;
        _tray.WheelToggled += enabled => Apply(_settings with { WheelEnabled = enabled });
        _tray.ExitRequested += Quit;

        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activate,
            (_, _) => Dispatcher.BeginInvoke(_main.Present), null, Timeout.Infinite, executeOnlyOnce: false);

        ApplyRuntime(_settings);
        DetectFfmpeg();
        if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
            _main.Present();
    }

    private void Apply(AppSettings settings)
    {
        bool ffmpegChanged = settings.FfmpegPath != _settings.FfmpegPath;
        _settings = settings;
        if (ffmpegChanged)
            DetectFfmpeg();
        ApplyRuntime(settings);
        _main.ShowSettings(settings);
        try
        {
            _store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Não foi possível salvar as configurações em {_store.Path}.\n\n{ex.Message}", "Pomelo",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyRuntime(AppSettings settings)
    {
        _gestures!.Enabled = settings.WheelEnabled;
        _gestures.SetKeys(settings.ConvertKey, settings.ToolsKey, settings.ActionsKey);
        _tray!.SetWheelEnabled(settings.WheelEnabled);
        if (!settings.WheelEnabled)
            _wheel.Dismiss();
    }

    private async void DetectFfmpeg()
    {
        int search = ++_ffmpegSearch;
        string? configured = _settings.FfmpegPath;
        _main.ShowFfmpeg(_ffmpeg, busy: true);
        FfmpegInfo? found = await Task.Run(() => FfmpegLocator.Locate(configured));
        if (search != _ffmpegSearch)
            return;
        _ffmpeg = found;
        _main.ShowFfmpeg(found, busy: false);
    }

    /// <summary>Opens winget in a visible console (user-initiated), then searches again.</summary>
    private async void InstallFfmpeg()
    {
        _main.ShowFfmpeg(null, busy: true);
        try
        {
            using var winget = Process.Start(new ProcessStartInfo("winget",
                "install --id Gyan.FFmpeg.Essentials --exact --source winget --accept-package-agreements --accept-source-agreements")
            {
                UseShellExecute = true,
            })!;
            await winget.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("O winget não está disponível neste Windows. Baixe o FFmpeg em https://ffmpeg.org e use " +
                            "\"Escolher ffmpeg.exe…\".", "Pomelo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        DetectFfmpeg();
    }

    private void OnHiddenToTray()
    {
        if (_settings.TrayHintShown)
            return;
        _tray!.Notify("O Pomelo continua rodando", "A roda segue ativa. Use o ícone da bandeja para abrir ou sair.", null);
        Apply(_settings with { TrayHintShown = true });
    }

    private void NotifyBatch(ConversionBatch batch, IReadOnlyList<ConversionResult> results)
    {
        if (!_settings.NotifyOnCompletion)
            return;

        var converted = results.Where(r => r.Output is not null).ToList();
        var failed = results.Where(r => r.Error is not null).ToList();
        string done = batch.Operation.DoneTitle;
        string? reveal = converted.LastOrDefault()?.Output;

        (string title, string text) = (converted.Count, failed.Count) switch
        {
            (1, 0) => (done, Path.GetFileName(reveal!)),
            (_, 0) => ($"{done}: {converted.Count} arquivos", Path.GetDirectoryName(reveal!)!),
            (0, 1) => ("Falha", $"{Path.GetFileName(failed[0].Source)}: {failed[0].Error}"),
            (0, _) => ($"{failed.Count} arquivos falharam", "Abra o Pomelo para ver os detalhes."),
            _ => ($"{converted.Count} prontos, {failed.Count} com falha", "Abra o Pomelo para ver os detalhes."),
        };
        _tray!.Notify(title, text, reveal);
    }

    private void Quit()
    {
        _main.AllowClose = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        _actions?.WaitForShutdown();
        _activateWait?.Unregister(null);
        _tray?.Dispose();
        _gestures?.Dispose();
        _worker?.Dispose();
        _activate?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
