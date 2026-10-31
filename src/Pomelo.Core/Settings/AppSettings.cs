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
    // The three wheel keys are always distinct (enforced by SettingsStore and the settings UI).
    /// <summary>Opens the conversion wheel.</summary>
    public WheelKey ConvertKey { get; init; } = WheelKey.Shift;
    /// <summary>Opens the tools wheel.</summary>
    public WheelKey ToolsKey { get; init; } = WheelKey.Ctrl;
    /// <summary>Opens the actions wheel.</summary>
    public WheelKey ActionsKey { get; init; } = WheelKey.Alt;
    public OutputLocation OutputLocation { get; init; } = OutputLocation.BesideOriginal;
    public string? CustomOutputFolder { get; init; }
    public int JpegQuality { get; init; } = 92;
    public bool NotifyOnCompletion { get; init; } = true;
    /// <summary>ffmpeg.exe chosen by the user; null searches the usual places.</summary>
    public string? FfmpegPath { get; init; }
    /// <summary>Last folder picked by "Copiar em…" / "Mover…"; the next picker starts there.</summary>
    public string? LastTransferFolder { get; init; }
    /// <summary>The "still running in the tray" hint is shown once, the first time the window is closed.</summary>
    public bool TrayHintShown { get; init; }
}
