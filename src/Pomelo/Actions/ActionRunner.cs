using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using Pomelo.Conversion;
using Pomelo.Core.Actions;
using Pomelo.Core.Settings;
using Pomelo.Interop;
using Pomelo.Shelf;
using Pomelo.Tray;
using Forms = System.Windows.Forms;

namespace Pomelo.Actions;

/// <param name="FilesOnly">Hidden when the selection contains a folder.</param>
internal sealed record FileAction(string Label, string HubText, bool FilesOnly, Func<IReadOnlyList<string>, Task> Run);

/// <summary>
/// The actions wheel: things done to the dragged items themselves (not to a converted copy).
/// Works for any files and folders. Runs on the UI thread; slow work goes to the thread pool.
/// </summary>
internal sealed class ActionRunner
{
    private readonly Func<AppSettings> _settings;
    private readonly Action<AppSettings> _apply;
    private readonly TrayIcon _tray;
    private readonly MainWindow _main;
    private readonly ShelfWindow _shelf;
    private readonly CancellationToken _shutdown;
    private readonly IReadOnlyList<FileAction> _all;
    private readonly List<Task> _backgroundWork = [];

    /// <summary>Called after cancellation; wait only for background work, never for UI continuations.</summary>
    public void WaitForShutdown()
    {
        try
        {
            Task.WhenAll(_backgroundWork).Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Cancellation or a transfer error; the action's own handler reports failures while running.
        }
    }

    private async Task<T> InBackground<T>(Func<T> work)
    {
        Task<T> task = Task.Run(work, _shutdown);
        _backgroundWork.Add(task);
        try { return await task; }
        finally { _backgroundWork.Remove(task); }
    }

    public ActionRunner(Func<AppSettings> settings, Action<AppSettings> apply, TrayIcon tray, MainWindow main,
        ShelfWindow shelf, CancellationToken shutdown)
    {
        _settings = settings;
        _apply = apply;
        _tray = tray;
        _main = main;
        _shelf = shelf;
        _shutdown = shutdown;
        _all =
        [
            new("Caminho", "Copiar o caminho", FilesOnly: false, CopyPathsAsync),
            new("Copiar", "Copiar para colar com Ctrl+V", FilesOnly: false, CopyItemsAsync),
            new("Copiar em…", "Copiar para uma pasta", FilesOnly: false, paths => TransferAsync(paths, TransferMode.Copy)),
            new("Mover…", "Mover para uma pasta", FilesOnly: false, paths => TransferAsync(paths, TransferMode.Move)),
            new("Terminal", "Abrir o terminal na pasta", FilesOnly: false, OpenTerminalAsync),
            new("Compartilhar", "Compartilhar (Windows)", FilesOnly: true, ShareAsync),
            new("SHA-256", "Copiar o hash SHA-256", FilesOnly: true, CopyHashesAsync),
            new("Flutuar", "Guardar na prateleira", FilesOnly: false, FloatAsync),
        ];
    }

    /// <summary>Actions valid for every dragged item; empty if any item no longer exists.</summary>
    public IReadOnlyList<FileAction> For(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || !paths.All(Path.Exists))
            return [];
        bool allFiles = paths.All(File.Exists);
        return _all.Where(a => !a.FilesOnly || allFiles).ToList();
    }

    /// <summary>Fire-and-forget from the wheel; failures become a notification instead of a crash.</summary>
    public async void Run(FileAction action, IReadOnlyList<string> paths)
    {
        try
        {
            await action.Run(paths);
        }
        catch (OperationCanceledException)
        {
            // Pomelo is exiting.
        }
        catch (Exception ex)
        {
            Notify("Falha", $"{action.HubText}: {ex.Message}", null);
        }
    }

    private Task CopyPathsAsync(IReadOnlyList<string> paths)
    {
        SetClipboard(new Forms.DataObject(Forms.DataFormats.UnicodeText, string.Join(Environment.NewLine, paths)));
        Notify(paths.Count == 1 ? "Caminho copiado" : $"{paths.Count} caminhos copiados",
            paths.Count == 1 ? paths[0] : "Cole com Ctrl+V.", null);
        return Task.CompletedTask;
    }

    private Task FloatAsync(IReadOnlyList<string> paths)
    {
        _shelf.Add(paths);
        return Task.CompletedTask;
    }

    private Task CopyItemsAsync(IReadOnlyList<string> paths)
    {
        var files = new StringCollection();
        files.AddRange(paths.ToArray());
        var data = new Forms.DataObject();
        data.SetFileDropList(files);
        // Paste copies (DROPEFFECT_COPY) rather than moves.
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        SetClipboard(data);
        Notify("Pronto para colar", paths.Count == 1
            ? $"{Path.GetFileName(paths[0])}: cole com Ctrl+V no Explorer ou em outro app."
            : $"{paths.Count} itens: cole com Ctrl+V no Explorer ou em outro app.", null);
        return Task.CompletedTask;
    }

    private async Task TransferAsync(IReadOnlyList<string> paths, TransferMode mode)
    {
        string verb = mode == TransferMode.Copy ? "Copiar" : "Mover";
        string? folder = PickFolder($"{verb} {Describe(paths)} para…", _settings().LastTransferFolder);
        if (folder is null)
            return;
        _apply(_settings() with { LastTransferFolder = folder });

        using var status = _tray.BeginActionStatus(mode == TransferMode.Copy ? "copiando…" : "movendo…");
        var results = await InBackground(() => FileTransfer.Run(paths, folder, mode, _shutdown));
        foreach (var r in results)
            _main.Report(new ConversionResult(r.Source, r.Destination, r.Error));

        var done = results.Where(r => r.Destination is not null).ToList();
        var failed = results.Where(r => r.Destination is null).ToList();
        var warnings = done.Where(r => r.Error is not null).ToList();
        string where = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } name ? name : folder;
        string past = mode == TransferMode.Copy ? "Copiado" : "Movido";
        (string title, string text) = (done.Count, failed.Count) switch
        {
            (1, 0) => ($"{past} para {where}", Path.GetFileName(done[0].Destination!)),
            (_, 0) => ($"{past} para {where}", $"{done.Count} itens"),
            (0, 1) => ("Falha", $"{Path.GetFileName(failed[0].Source)}: {failed[0].Error}"),
            _ => ($"{done.Count} prontos, {failed.Count} com falha", "Abra o Pomelo para ver os detalhes."),
        };
        if (warnings.Count > 0 && failed.Count == 0)
        {
            title = $"{done.Count} copiado(s), com aviso";
            text = warnings.Count == 1 ? warnings[0].Error! : "Abra o Pomelo para ver os detalhes.";
        }
        Notify(title, text, done.LastOrDefault()?.Destination);
    }

    private Task OpenTerminalAsync(IReadOnlyList<string> paths)
    {
        string first = paths[0];
        string folder = Directory.Exists(first) ? first : Path.GetDirectoryName(first)!;
        try
        {
            var terminal = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
            terminal.ArgumentList.Add("-d");
            terminal.ArgumentList.Add(folder.Replace(";", "\\;"));
            Process.Start(terminal)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No Windows Terminal: a PowerShell console in that folder.
            Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = folder })?.Dispose();
        }
        return Task.CompletedTask;
    }

    private Task ShareAsync(IReadOnlyList<string> paths) => ShareSheet.ShowAsync(paths, _shutdown);

    private async Task CopyHashesAsync(IReadOnlyList<string> paths)
    {
        using var status = _tray.BeginActionStatus("calculando SHA-256…");
        var hashes = await InBackground(() => paths.Select(p => (p, Checksums.Sha256(p, _shutdown))).ToList());
        SetClipboard(new Forms.DataObject(Forms.DataFormats.UnicodeText, Checksums.Format(hashes)));
        Notify("SHA-256 copiado", paths.Count == 1 ? $"{Path.GetFileName(paths[0])}: {hashes[0].Item2[..16]}…" : $"{paths.Count} arquivos", null);
    }

    private static string? PickFolder(string title, string? start)
    {
        using var host = Foreground.CreateHost();
        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = start is not null && Directory.Exists(start) ? start : "",
        };
        return dialog.ShowDialog(host.Window) == true ? dialog.FolderName : null;
    }

    /// <summary>Retries: another app may hold the clipboard open for a moment.</summary>
    private static void SetClipboard(Forms.DataObject data) =>
        Forms.Clipboard.SetDataObject(data, copy: true, retryTimes: 10, retryDelay: 100);

    private static string Describe(IReadOnlyList<string> paths) =>
        paths.Count == 1 ? $"\"{Path.GetFileName(Path.TrimEndingDirectorySeparator(paths[0]))}\"" : $"{paths.Count} itens";

    private void Notify(string title, string text, string? reveal)
    {
        if (_settings().NotifyOnCompletion)
            _tray.Notify(title, text, reveal);
    }
}
