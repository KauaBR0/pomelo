using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Pomelo.Interop;

/// <summary>
/// The Windows share sheet (Nearby sharing, apps) through the documented desktop API,
/// IDataTransferManagerInterop. Explorer's own "Share" context-menu handler refuses to run
/// outside Explorer (ERROR_INVALID_WINDOW_HANDLE for any owner window), so it is not used.
/// </summary>
internal static class ShareSheet
{
    private static readonly TimeSpan MaxOpen = TimeSpan.FromMinutes(10);

    /// <summary>Completes when the user shares or dismisses the sheet (which closes with its owner).</summary>
    public static async Task ShowAsync(IReadOnlyList<string> files, CancellationToken cancellation)
    {
        var items = new List<IStorageItem>(files.Count);
        foreach (string file in files)
            items.Add(await StorageFile.GetFileFromPathAsync(file));

        using var host = Foreground.CreateHost();
        var manager = DataTransferManagerInterop.GetForWindow(host.Handle);
        var finished = new TaskCompletionSource();

        void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs e)
        {
            DataPackage data = e.Request.Data;
            data.Properties.Title = files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} arquivos";
            data.SetStorageItems(items);
            data.ShareCompleted += (_, _) => finished.TrySetResult();
            data.ShareCanceled += (_, _) => finished.TrySetResult();
        }

        manager.DataRequested += OnDataRequested;
        try
        {
            DataTransferManagerInterop.ShowShareUIForWindow(host.Handle);
            await finished.Task.WaitAsync(MaxOpen, cancellation);
        }
        catch (TimeoutException)
        {
            // Left open and forgotten: release the host window.
        }
        finally
        {
            manager.DataRequested -= OnDataRequested;
        }
    }
}
