using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using LabSign.Core.Token;

namespace LabSign.Desktop;

/// <summary>Everything the tray needs, handed over by <see cref="Program"/> before the app starts.</summary>
internal sealed record TrayContext(ITokenProvider Token, string Version, string DataFolder, IPlatformIntegration Integration);

/// <summary>
/// A tray-only app: no main window, just the notification-area icon (Windows) or status icon
/// (Linux, StatusNotifierItem) with status, the log folder, start-at-login and exit. The PIN
/// window is opened on demand by <see cref="DesktopInteraction"/>.
/// </summary>
internal sealed class App : Application
{
    public static TrayContext? Context { get; set; }

    /// <summary>Captured for the signal handler, which runs off the UI thread.</summary>
    public static IClassicDesktopStyleApplicationLifetime? Lifetime { get; private set; }

    private NativeMenuItem? _tokenItem;
    private NativeMenuItem? _autostartItem;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Context is { } context)
        {
            Lifetime = desktop;
            var menu = new NativeMenu();
            menu.Add(new NativeMenuItem($"LabSign {context.Version}") { IsEnabled = false });
            _tokenItem = new NativeMenuItem("Token: …") { IsEnabled = false };
            menu.Add(_tokenItem);
            menu.Add(new NativeMenuItemSeparator());

            var openFolder = new NativeMenuItem("Open log and settings folder");
            openFolder.Click += (_, _) => OpenFolder(context.DataFolder);
            menu.Add(openFolder);

            _autostartItem = new NativeMenuItem("Start at login")
            {
                ToggleType = NativeMenuItemToggleType.CheckBox,
                IsChecked = context.Integration.StartsAtLogin,
            };
            _autostartItem.Click += (_, _) =>
            {
                context.Integration.SetStartsAtLogin(!context.Integration.StartsAtLogin);
                _autostartItem.IsChecked = context.Integration.StartsAtLogin;
            };
            menu.Add(_autostartItem);
            menu.Add(new NativeMenuItemSeparator());

            var exit = new NativeMenuItem("Exit");
            exit.Click += (_, _) => desktop.Shutdown();
            menu.Add(exit);
            menu.NeedsUpdate += (_, _) => Refresh(context);

            var tray = new TrayIcon
            {
                Icon = new WindowIcon(new MemoryStream(AppIcon.Png(64))),
                ToolTipText = "LabSign: report signing",
                Menu = menu,
                IsVisible = true,
            };
            TrayIcon.SetIcons(this, [tray]);
            Refresh(context);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void Refresh(TrayContext context)
    {
        var status = context.Token.GetStatus();
        if (_tokenItem is not null)
            _tokenItem.Header = status.Present ? $"Token: {status.Label} ({status.Subject})" : $"Token: {status.Error}";
        if (_autostartItem is not null)
            _autostartItem.IsChecked = context.Integration.StartsAtLogin;
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", [folder]) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Core.Log.Write($"Could not open {folder}: {ex.Message}");
        }
    }
}
