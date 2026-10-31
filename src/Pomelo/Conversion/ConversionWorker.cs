using System.Collections.Concurrent;
using System.Windows.Threading;
using Pomelo.Core;
using Pomelo.Core.Formats;
using Pomelo.Core.Imaging;

namespace Pomelo.Conversion;

internal sealed record ConversionResult(string Source, string? Output, string? Error);

internal sealed record ConversionBatch(IReadOnlyList<string> Paths, ImageFormat Target, ConversionOptions Options);

/// <summary>
/// Runs conversions one file at a time on a dedicated STA thread (WPF imaging is
/// thread-affine), so the wheel and input hooks never wait on encoding.
/// A failed file is reported and does not stop the rest of the batch.
/// </summary>
internal sealed class ConversionWorker : IDisposable
{
    /// <summary>Raised on the UI thread after each file.</summary>
    public event Action<ConversionResult>? FileCompleted;
    /// <summary>Raised on the UI thread once every file of a batch is done.</summary>
    public event Action<ConversionBatch, IReadOnlyList<ConversionResult>>? BatchCompleted;

    private readonly Dispatcher _dispatcher;
    private readonly BlockingCollection<ConversionBatch> _queue = new();
    private readonly Thread _thread;

    public ConversionWorker(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _thread = new Thread(Run) { IsBackground = true, Name = "Pomelo conversions" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Enqueue(ConversionBatch batch) => _queue.Add(batch);

    private void Run()
    {
        foreach (var batch in _queue.GetConsumingEnumerable())
        {
            var results = new List<ConversionResult>(batch.Paths.Count);
            foreach (string path in batch.Paths)
            {
                ConversionResult result;
                try
                {
                    result = new(path, ImageConverter.Convert(path, batch.Target, batch.Options), null);
                }
                catch (Exception ex)
                {
                    // Per-file isolation: a corrupt or huge input must not take down the batch or the app.
                    result = new(path, null, ex is ConversionException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
                }
                results.Add(result);
                _dispatcher.BeginInvoke(() => FileCompleted?.Invoke(result));
            }
            _dispatcher.BeginInvoke(() => BatchCompleted?.Invoke(batch, results));
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }
}
