using System.Windows;
using Pomelo.Conversion;
using Pomelo.Core.Formats;
using Pomelo.Core.Imaging;
using Pomelo.Core.Settings;
using Pomelo.Core.Startup;
using Pomelo.Input;
using Pomelo.Interop;
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
    private TrayIcon? _tray;
    private WheelWindow _wheel = null!;
    private MainWindow _main = null!;

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

        _gestures.WheelRequested += _wheel.OpenAtCursor;
        _gestures.GestureEnded += _wheel.EndGesture;
        _gestures.Cancelled += _wheel.Dismiss;
        _wheel.ConversionRequested += (paths, target) => _worker.Enqueue(
            new ConversionBatch(paths, target, new ConversionOptions(OutputFolders.Resolve(_settings), _settings.JpegQuality)));
        _worker.FileCompleted += _main.Report;
        _worker.BatchCompleted += NotifyBatch;

        _main.SettingsChanged += Apply;
        _main.HiddenToTray += OnHiddenToTray;
        _tray.OpenRequested += _main.Present;
        _tray.WheelToggled += enabled => Apply(_settings with { WheelEnabled = enabled });
        _tray.ExitRequested += Quit;

        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activate,
            (_, _) => Dispatcher.BeginInvoke(_main.Present), null, Timeout.Infinite, executeOnlyOnce: false);

        ApplyRuntime(_settings);
        if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
            _main.Present();
    }

    private void Apply(AppSettings settings)
    {
        _settings = settings;
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
        _gestures.TriggerKey = settings.TriggerKey;
        _tray!.SetWheelEnabled(settings.WheelEnabled);
        if (!settings.WheelEnabled)
            _wheel.Dismiss();
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
        string format = batch.Target.Name;
        string? reveal = converted.LastOrDefault()?.Output;

        (string title, string text) = (converted.Count, failed.Count) switch
        {
            (1, 0) => ($"Convertido para {format}", Path.GetFileName(reveal!)),
            (_, 0) => ($"{converted.Count} arquivos convertidos para {format}", Path.GetDirectoryName(reveal!)!),
            (0, 1) => ("Falha na conversão", $"{Path.GetFileName(failed[0].Source)}: {failed[0].Error}"),
            (0, _) => ($"{failed.Count} arquivos falharam", "Abra o Pomelo para ver os detalhes."),
            _ => ($"{converted.Count} convertidos, {failed.Count} com falha", "Abra o Pomelo para ver os detalhes."),
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
        _activateWait?.Unregister(null);
        _tray?.Dispose();
        _gestures?.Dispose();
        _worker?.Dispose();
        _activate?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
