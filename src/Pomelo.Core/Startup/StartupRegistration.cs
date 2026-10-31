using Microsoft.Win32;

namespace Pomelo.Core.Startup;

/// <summary>
/// Per-user "start with Windows" via HKCU\...\Run (no admin rights needed). Also honours the
/// Task Manager / Settings "Startup apps" toggle stored in Explorer\StartupApproved\Run.
/// </summary>
public sealed class StartupRegistration(
    string valueName,
    string command,
    string runKeyPath = StartupRegistration.RunKeyPath,
    string approvedKeyPath = StartupRegistration.ApprovedKeyPath)
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public static StartupRegistration ForCurrentExecutable() =>
        new("Pomelo", $"\"{Environment.ProcessPath}\" --background");

    public string Command { get; } = command;

    /// <summary>True only when Windows will actually launch this command at sign-in.</summary>
    public bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(runKeyPath);
            if (run?.GetValue(valueName) is not string value || !string.Equals(value, Command, StringComparison.OrdinalIgnoreCase))
                return false;

            // First byte of the approval blob: even = enabled, odd = disabled by the user. Missing = enabled.
            using var approved = Registry.CurrentUser.OpenSubKey(approvedKeyPath);
            return approved?.GetValue(valueName) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var run = Registry.CurrentUser.CreateSubKey(runKeyPath);
            run.SetValue(valueName, Command, RegistryValueKind.String);
        }
        else
        {
            using var run = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
            run?.DeleteValue(valueName, throwOnMissingValue: false);
        }

        // An explicit choice in Pomelo supersedes an earlier Task Manager toggle.
        using var approved = Registry.CurrentUser.OpenSubKey(approvedKeyPath, writable: true);
        approved?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
