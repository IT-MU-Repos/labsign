using LabSign.Core;
using Microsoft.Win32;

namespace LabSign;

/// <summary>
/// Per-user (HKCU, no admin) registration, refreshed on every start so it follows the exe if it
/// is moved: the <c>labsign://</c> protocol the LIMS page uses to launch it, and autostart.
/// </summary>
internal static class WindowsIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Register()
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
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            run.SetValue("LabSign", $"\"{exe}\"");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Write($"Could not register the labsign:// protocol or autostart: {ex.Message}");
        }
    }

    public static bool StartsWithWindows
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            return run?.GetValue("LabSign") is not null;
        }
    }

    public static void SetStartsWithWindows(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } exe)
            run.SetValue("LabSign", $"\"{exe}\"");
        else
            run.DeleteValue("LabSign", throwOnMissingValue: false);
    }
}
