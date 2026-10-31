using System.Collections.Concurrent;
using System.Windows.Threading;
using Pomelo.Core;
using Pomelo.Core.Formats;
using Pomelo.Core.Imaging;

namespace Pomelo.Conversion;

internal sealed record ConversionResult(string Source, string? Output, string? Error);

/// <summary>
/// Runs conversions one file at a time on a dedicated STA thread (WPF imaging is
/// thread-affine), so the wheel and input hooks never wait on encoding.
/// A failed file is reported and does not stop the rest of the batch.
/// </summary>
internal sealed class ConversionWorker : IDisposable
{
    public event Action<ConversionResult>? Completed;

    private readonly Dispatcher _dispatcher;
    private readonly BlockingCollection<(string Path, ImageFormat Target)> _queue = new();
    private readonly Thread _thread;

    public ConversionWorker(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _thread = new Thread(Run) { IsBackground = true, Name = "Pomelo conversions" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Enqueue(IEnumerable<string> paths, ImageFormat target)
    {
        foreach (string path in paths)
            _queue.Add((path, target));
    }

    private void Run()
    {
        foreach (var (path, target) in _queue.GetConsumingEnumerable())
        {
            ConversionResult result;
            try
            {
                result = new(path, ImageConverter.Convert(path, target), null);
            }
            catch (Exception ex)
            {
                // Per-file isolation: a corrupt or huge input must not take down the batch or the app.
                result = new(path, null, ex is ConversionException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
            }
            _dispatcher.BeginInvoke(() => Completed?.Invoke(result));
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}
