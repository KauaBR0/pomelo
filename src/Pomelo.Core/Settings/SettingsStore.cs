using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pomelo.Core.Settings;

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pomelo", "settings.json");

    public string Path { get; } = path;

    /// <summary>Defaults when the file is missing or unreadable; out-of-range values are clamped.</summary>
    public AppSettings Load()
    {
        AppSettings? settings = null;
        try
        {
            using var stream = File.OpenRead(Path);
            settings = JsonSerializer.Deserialize<AppSettings>(stream, Json);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
        }

        settings ??= new AppSettings();
        // Invalid keys fall back to their defaults; duplicates keep the earlier wheel's key and the
        // later wheel takes a free one, so each wheel always has its own key.
        WheelKey[] wanted =
        [
            Enum.IsDefined(settings.ConvertKey) ? settings.ConvertKey : WheelKey.Shift,
            Enum.IsDefined(settings.ToolsKey) ? settings.ToolsKey : WheelKey.Ctrl,
            Enum.IsDefined(settings.ActionsKey) ? settings.ActionsKey : WheelKey.Alt,
        ];
        var free = new Queue<WheelKey>(Enum.GetValues<WheelKey>().Except(wanted));
        var keys = new WheelKey[wanted.Length];
        for (int i = 0; i < wanted.Length; i++)
            keys[i] = keys[..i].Contains(wanted[i]) ? free.Dequeue() : wanted[i];

        return settings with
        {
            JpegQuality = Math.Clamp(settings.JpegQuality, AppSettings.MinJpegQuality, AppSettings.MaxJpegQuality),
            OutputLocation = Enum.IsDefined(settings.OutputLocation) ? settings.OutputLocation : OutputLocation.BesideOriginal,
            ConvertKey = keys[0],
            ToolsKey = keys[1],
            ActionsKey = keys[2],
        };
    }

    /// <summary>Writes a temp file then swaps it in, so a crash never leaves a half-written file.</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temp = Path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(settings, Json));
        File.Move(temp, Path, overwrite: true);
    }
}
