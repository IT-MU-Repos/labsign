using System.Runtime.Versioning;
using LabSign.Core;
using Microsoft.Win32;

namespace LabSign.Desktop;

/// <summary>The <c>labsign://</c> link and start-at-login, per OS. Per user; no admin rights.</summary>
internal interface IPlatformIntegration
{
    void Register();
    bool StartsAtLogin { get; }
    void SetStartsAtLogin(bool enabled);
}

internal static class PlatformIntegration
{
    public static IPlatformIntegration Create(string dataFolder) => OperatingSystem.IsWindows()
        ? new WindowsIntegration()
        : new LinuxDesktopIntegration(dataFolder);
}

/// <summary>HKCU: the URL protocol and the Run key. Refreshed every start so it follows the exe.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsIntegration : IPlatformIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public void Register()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            using (var protocol = Registry.CurrentUser.CreateSubKey(@"Software\Classes\labsign"))
            {
                protocol.SetValue("", "URL:LabSign");
                protocol.SetValue("URL Protocol", "");
                using var command = protocol.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{exe}\" \"%1\"");
            }
            if (!AutostartDisabled)
            {
                using var run = Registry.CurrentUser.CreateSubKey(RunKey);
                run.SetValue("LabSign", $"\"{exe}\"");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Write($"Could not register the labsign:// protocol or autostart: {ex.Message}");
        }
    }

    public bool StartsAtLogin
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            return run?.GetValue("LabSign") is not null;
        }
    }

    // Remembered so the next start does not switch autostart back on.
    private static bool AutostartDisabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\LabSign");
            return key?.GetValue("AutostartDisabled") is 1;
        }
    }

    public void SetStartsAtLogin(bool enabled)
    {
        using (var settings = Registry.CurrentUser.CreateSubKey(@"Software\LabSign"))
            settings.SetValue("AutostartDisabled", enabled ? 0 : 1, RegistryValueKind.DWord);
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe)
            run.SetValue("LabSign", $"\"{exe}\"");
        else
            run.DeleteValue("LabSign", throwOnMissingValue: false);
    }
}

/// <summary>XDG desktop entries (see <see cref="LinuxIntegration"/>), with the icon written beside the config.</summary>
internal sealed class LinuxDesktopIntegration(string dataFolder) : IPlatformIntegration
{
    private readonly LinuxIntegration _xdg = LinuxIntegration.ForCurrentUser();
    private string Icon => Path.Combine(dataFolder, "labsign.png");

    public void Register()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        try
        {
            File.WriteAllBytes(Icon, AppIcon.Png(256));
            _xdg.Register(exe, Icon);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Log.Write($"Could not register the labsign:// link or autostart: {ex.Message}");
        }
    }

    public bool StartsAtLogin => _xdg.StartsAtLogin;

    public void SetStartsAtLogin(bool enabled)
    {
        if (Environment.ProcessPath is { } exe) _xdg.SetStartsAtLogin(enabled, exe, Icon);
    }
}
