using Microsoft.Win32;
using Pomelo.Core.Startup;

namespace Pomelo.Tests;

/// <summary>Uses a throwaway HKCU key instead of the real Run keys.</summary>
public sealed class StartupRegistrationTests : IDisposable
{
    private const string Command = "\"C:\\Apps\\Pomelo\\Pomelo.exe\" --background";
    private readonly string _root = $@"Software\PomeloTests-{Guid.NewGuid():N}";
    private string RunKey => _root + @"\Run";
    private string ApprovedKey => _root + @"\Approved";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);

    private StartupRegistration Create(string command = Command) => new("Pomelo", command, RunKey, ApprovedKey);

    [Fact]
    public void Enable_registers_the_command_and_disable_removes_it()
    {
        var startup = Create();
        Assert.False(startup.IsEnabled);

        startup.SetEnabled(true);
        Assert.True(startup.IsEnabled);
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
            Assert.Equal(Command, run!.GetValue("Pomelo"));

        startup.SetEnabled(false);
        Assert.False(startup.IsEnabled);
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey))
            Assert.Null(run!.GetValue("Pomelo"));
    }

    [Fact]
    public void Entry_for_another_copy_of_the_app_is_not_reported_as_ours()
    {
        Create("\"C:\\Old\\Pomelo.exe\" --background").SetEnabled(true);

        Assert.False(Create().IsEnabled);
    }

    [Fact]
    public void Task_manager_disable_is_honoured_until_the_user_enables_again_in_pomelo()
    {
        var startup = Create();
        startup.SetEnabled(true);
        using (var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey))
            approved.SetValue("Pomelo", new byte[] { 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);

        Assert.False(startup.IsEnabled);

        startup.SetEnabled(true);
        Assert.True(startup.IsEnabled);
    }
}
