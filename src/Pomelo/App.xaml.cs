using System.Windows;
using Pomelo.Conversion;
using Pomelo.Input;
using Pomelo.Wheel;

namespace Pomelo;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private GestureMonitor? _gestures;
    private ConversionWorker? _worker;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Two instances would both hook input and open two wheels.
        _singleInstance = new Mutex(true, @"Local\Pomelo.SingleInstance", out bool first);
        if (!first)
        {
            Shutdown();
            return;
        }

        var wheel = new WheelWindow();
        _worker = new ConversionWorker(Dispatcher);
        _gestures = new GestureMonitor(Dispatcher);

        var main = new MainWindow(enabled =>
        {
            _gestures.Enabled = enabled;
            if (!enabled) wheel.Dismiss();
        });
        MainWindow = main;

        _gestures.WheelRequested += wheel.OpenAtCursor;
        _gestures.GestureEnded += wheel.EndGesture;
        _gestures.Cancelled += wheel.Dismiss;
        wheel.ConversionRequested += _worker.Enqueue;
        _worker.Completed += main.Report;

        main.Closed += (_, _) => wheel.Close();
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _gestures?.Dispose();
        _worker?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
