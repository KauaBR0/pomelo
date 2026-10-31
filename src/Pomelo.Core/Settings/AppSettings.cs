namespace Pomelo.Core.Settings;

/// <summary>Key tapped during a drag to open a wheel.</summary>
public enum WheelKey { Shift, Ctrl, Alt }

public enum OutputLocation { BesideOriginal, Downloads, Desktop, CustomFolder }

/// <summary>
/// User preferences persisted by <see cref="SettingsStore"/>. "Start with Windows" is not here:
/// its source of truth is the registry (see the app's StartupRegistration).
/// </summary>
public sealed record AppSettings
{
    public const int MinJpegQuality = 1;
    public const int MaxJpegQuality = 100;

    public bool WheelEnabled { get; init; } = true;
    /// <summary>Opens the conversion wheel. Always differs from <see cref="ToolsKey"/>.</summary>
    public WheelKey ConvertKey { get; init; } = WheelKey.Shift;
    /// <summary>Opens the tools wheel. Always differs from <see cref="ConvertKey"/>.</summary>
    public WheelKey ToolsKey { get; init; } = WheelKey.Ctrl;
    public OutputLocation OutputLocation { get; init; } = OutputLocation.BesideOriginal;
    public string? CustomOutputFolder { get; init; }
    public int JpegQuality { get; init; } = 92;
    public bool NotifyOnCompletion { get; init; } = true;
    /// <summary>ffmpeg.exe chosen by the user; null searches the usual places.</summary>
    public string? FfmpegPath { get; init; }
    /// <summary>The "still running in the tray" hint is shown once, the first time the window is closed.</summary>
    public bool TrayHintShown { get; init; }
}
