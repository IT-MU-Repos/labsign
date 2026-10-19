using System.Diagnostics;

namespace LabSign.Core;

/// <summary>
/// Per-user desktop integration on Linux, the counterpart of the Windows HKCU registration:
/// the <c>labsign://</c> link the web apps use to start LabSign (an XDG desktop entry handling
/// <c>x-scheme-handler/labsign</c>) and autostart at login (<c>~/.config/autostart</c>).
/// Refreshed on every start so it follows the binary if it moves. No root needed.
/// </summary>
public sealed class LinuxIntegration(string dataHome, string configHome)
{
    public const string DesktopFileName = "labsign.desktop";

    /// <summary>XDG_DATA_HOME / XDG_CONFIG_HOME, defaulting to ~/.local/share and ~/.config.</summary>
    public static LinuxIntegration ForCurrentUser()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        return new LinuxIntegration(
            Env("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share"),
            Env("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"));
    }

    public string ApplicationEntry => Path.Combine(dataHome, "applications", DesktopFileName);

    public string AutostartEntry => Path.Combine(configHome, "autostart", DesktopFileName);

    public static string Entry(string executable, string? icon, bool handlesLinks) =>
        string.Join('\n', new[]
        {
            "[Desktop Entry]",
            "Type=Application",
            "Name=LabSign",
            "Comment=Sign LIMS report PDFs with the SafeNet USB token",
            $"Exec=\"{executable}\"{(handlesLinks ? " %u" : "")}",
            icon is null ? null : $"Icon={icon}",
            "Terminal=false",
            "Categories=Office;",
            handlesLinks ? "MimeType=x-scheme-handler/labsign;" : null,
            handlesLinks ? "NoDisplay=false" : "X-GNOME-Autostart-enabled=true",
        }.Where(line => line is not null)) + "\n";

    /// <summary>Writes the link handler and (unless switched off) the autostart entry.</summary>
    public void Register(string executable, string? icon, bool runCommands = true)
    {
        Write(ApplicationEntry, Entry(executable, icon, handlesLinks: true));
        if (!File.Exists(AutostartEntry + ".disabled"))
            Write(AutostartEntry, Entry(executable, icon, handlesLinks: false));
        if (!runCommands) return;
        // Best effort: without these the entry still works once the desktop rescans.
        Run("xdg-mime", "default", DesktopFileName, "x-scheme-handler/labsign");
        Run("update-desktop-database", Path.GetDirectoryName(ApplicationEntry)!);
    }

    public bool StartsAtLogin => File.Exists(AutostartEntry);

    /// <summary>Turning autostart off leaves a marker so the next start does not re-enable it.</summary>
    public void SetStartsAtLogin(bool enabled, string executable, string? icon)
    {
        var marker = AutostartEntry + ".disabled";
        if (enabled)
        {
            if (File.Exists(marker)) File.Delete(marker);
            Write(AutostartEntry, Entry(executable, icon, handlesLinks: false));
        }
        else
        {
            if (File.Exists(AutostartEntry)) File.Delete(AutostartEntry);
            Write(marker, "");
        }
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Run(string command, params string[] args)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(command, args)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            process?.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Log.Write($"{command} not run: {ex.Message}");
        }
    }
}
