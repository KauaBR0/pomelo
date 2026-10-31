using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using Pomelo.Conversion;

namespace Pomelo;

internal sealed partial class MainWindow : Window
{
    private sealed record LogEntry(string Text, string? RevealPath)
    {
        public override string ToString() => Text;
    }

    public MainWindow(Action<bool> setEnabled)
    {
        InitializeComponent();
        EnabledBox.Checked += (_, _) => setEnabled(true);
        EnabledBox.Unchecked += (_, _) => setEnabled(false);
    }

    public void Report(ConversionResult result)
    {
        string time = DateTime.Now.ToString("HH:mm:ss");
        var entry = result.Error is null
            ? new LogEntry($"{time}  ✓ {Path.GetFileName(result.Source)} → {Path.GetFileName(result.Output)}", result.Output)
            : new LogEntry($"{time}  ✗ {Path.GetFileName(result.Source)}: {result.Error}", result.Source);
        Log.Items.Insert(0, entry);
    }

    private void OnLogDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Log.SelectedItem is LogEntry { RevealPath: { } path } && File.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }
}
