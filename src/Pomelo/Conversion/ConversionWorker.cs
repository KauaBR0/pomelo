using System.Collections.Concurrent;
using System.Windows.Threading;
using Pomelo.Core.Conversion;
using Pomelo.Core.Media;

namespace Pomelo.Conversion;

internal sealed record ConversionResult(string Source, string? Output, string? Error);

internal sealed record ConversionBatch(IReadOnlyList<string> Paths, IFileOperation Operation, ConversionOptions Options, FfmpegInfo? Ffmpeg);

/// <summary>
/// Runs conversions one file at a time on a dedicated STA thread (WPF imaging is
/// thread-affine), so the wheel and input hooks never wait on encoding.
/// A failed file is reported and does not stop the rest of the batch.
/// Disposing stops a running FFmpeg and abandons queued work.
/// </summary>
internal sealed class ConversionWorker : IDisposable
{
    /// <summary>Raised on the UI thread before each file: (source, 1-based index, batch size).</summary>
    public event Action<string, int, int>? FileStarted;
    /// <summary>Raised on the UI thread after each file.</summary>
    public event Action<ConversionResult>? FileCompleted;
    /// <summary>Raised on the UI thread once every file of a batch is done.</summary>
    public event Action<ConversionBatch, IReadOnlyList<ConversionResult>>? BatchCompleted;

    private readonly Dispatcher _dispatcher;
    private readonly BlockingCollection<ConversionBatch> _queue = new();
    private readonly CancellationTokenSource _shutdown = new();
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
        try
        {
            foreach (var batch in _queue.GetConsumingEnumerable(_shutdown.Token))
                RunBatch(batch);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private void RunBatch(ConversionBatch batch)
    {
        var results = new List<ConversionResult>(batch.Paths.Count);
        for (int i = 0; i < batch.Paths.Count; i++)
        {
            string path = batch.Paths[i];
            int index = i + 1;
            _dispatcher.BeginInvoke(() => FileStarted?.Invoke(path, index, batch.Paths.Count));

            ConversionResult result;
            try
            {
                result = new(path, batch.Operation.Apply(path, batch.Options, batch.Ffmpeg, _shutdown.Token), null);
            }
            catch (OperationCanceledException)
            {
                throw;
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

    public void Dispose()
    {
        _shutdown.Cancel();
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
        _shutdown.Dispose();
    }
}
