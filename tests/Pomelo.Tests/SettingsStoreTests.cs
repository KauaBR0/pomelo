using Pomelo.Core.Settings;

namespace Pomelo.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pomelo-settings-").FullName;
    private string FilePath => Path.Combine(_dir, "nested", "settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Round_trips_every_setting_and_creates_the_folder()
    {
        var store = new SettingsStore(FilePath);
        var settings = new AppSettings
        {
            WheelEnabled = false,
            TriggerKey = TriggerKey.Alt,
            OutputLocation = OutputLocation.CustomFolder,
            CustomOutputFolder = @"D:\Convertidos",
            JpegQuality = 75,
            NotifyOnCompletion = false,
            TrayHintShown = true,
        };

        store.Save(settings);

        Assert.Equal(settings, new SettingsStore(FilePath).Load());
        Assert.Equal([FilePath], Directory.GetFiles(Path.GetDirectoryName(FilePath)!));
    }

    [Fact]
    public void Missing_file_loads_defaults() =>
        Assert.Equal(new AppSettings(), new SettingsStore(FilePath).Load());

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "TriggerKey": "CapsLock" }""")]
    public void Unreadable_file_loads_defaults(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, content);

        Assert.Equal(new AppSettings(), new SettingsStore(FilePath).Load());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(250, 100)]
    public void Out_of_range_jpeg_quality_is_clamped(int stored, int expected)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, $$"""{ "JpegQuality": {{stored}}, "TriggerKey": "Ctrl" }""");

        var loaded = new SettingsStore(FilePath).Load();

        Assert.Equal(expected, loaded.JpegQuality);
        Assert.Equal(TriggerKey.Ctrl, loaded.TriggerKey); // Other values survive the clamp.
    }
}
