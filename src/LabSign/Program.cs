using System.Net;
using System.Reflection;
using LabSign.Core;
using LabSign.Core.Token;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace LabSign;

internal static class Program
{
    /// <summary>
    /// Started by Windows at login (Run key), by hand, or by the LIMS page through
    /// <c>labsign://start</c>. A second launch while one is running just exits: the page only
    /// needs one listening.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\LabSign.SingleInstance", out var isFirst);
        if (!isFirst) return;

        var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LabSign");
        Directory.CreateDirectory(dataFolder);
        Log.FilePath = Path.Combine(dataFolder, "labsign.log");
        var configPath = Path.Combine(dataFolder, "config.json");
        LabSignConfig.EnsureExists(configPath);
        var config = LabSignConfig.Load(configPath);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        Log.Write($"LabSign {version} starting on port {config.Port}");
        WindowsIntegration.Register();

        ApplicationConfiguration.Initialize();
        // The PIN dialog is shown from a Kestrel thread, so it needs a way back onto this one.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var ui = SynchronizationContext.Current!;

        using var token = new Pkcs11TokenProvider(config.Pkcs11LibraryPath, config.CertificateFallbackPath);
        var interaction = new DesktopInteraction(ui, config.StampImagePath);
        var app = LabSignApi.Build(config, token, interaction, version, builder =>
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, config.Port)));

        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (IOException ex)
        {
            Log.Write($"Could not listen on 127.0.0.1:{config.Port}: {ex.Message}");
            MessageBox.Show($"LabSign could not start: port {config.Port} is already in use.\n\n{ex.Message}",
                "LabSign", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.Run(new TrayContext(token, version, dataFolder));
        app.StopAsync().GetAwaiter().GetResult();
        Log.Write("LabSign stopped");
    }
}
