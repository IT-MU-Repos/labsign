using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using LabSign.Core;
using LabSign.Core.Token;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LabSign.Desktop;

internal static class Program
{
    /// <summary>
    /// Started at login (Windows Run key / Linux autostart entry), by hand, or by a web page
    /// through <c>labsign://start</c>. A second launch while one is running just exits: the
    /// page only needs one listening.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\LabSign.SingleInstance", out var isFirst);
        if (!isFirst) return 0;

        var dataFolder = LabSignConfig.DataFolder();
        Directory.CreateDirectory(dataFolder);
        Log.FilePath = Path.Combine(dataFolder, "labsign.log");
        var configPath = Path.Combine(dataFolder, "config.json");
        LabSignConfig.EnsureExists(configPath);
        var config = LabSignConfig.Load(configPath);

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        Log.Write($"LabSign {version} starting on port {config.Port} ({Environment.OSVersion})");
        var integration = PlatformIntegration.Create(dataFolder);
        integration.Register();

        using var token = new Pkcs11TokenProvider(config.Pkcs11LibraryPath, config.CertificateFallbackPath);
        var interaction = new DesktopInteraction(config.StampImagePath);
        var app = LabSignApi.Build(config, token, interaction, version, builder =>
        {
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, config.Port));
            // The app owns SIGTERM/SIGINT (below); the host's console lifetime would compete for them.
            builder.Services.AddSingleton<IHostLifetime, DesktopHostLifetime>();
        });

        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (IOException ex)
        {
            Log.Write($"Could not listen on 127.0.0.1:{config.Port}: {ex.Message}");
            return 1;
        }

        App.Context = new TrayContext(token, version, dataFolder, integration);

        // SIGTERM (logout, the Linux installer, systemd) must end the whole app. The web host
        // stops itself on it, but the UI loop would carry on without an API behind it.
        using var onTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ShutdownOnSignal);
        using var onInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ShutdownOnSignal);

        var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);

        Log.Write("UI closed");
        try
        {
            // On the thread pool: this thread still carries Avalonia's synchronization context,
            // which no longer pumps once the UI loop has ended, so an await here would never resume.
            Task.Run(() => app.StopAsync()).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            Log.Write("The API did not stop in time");
        }
        Log.Write("LabSign stopped");
        return exitCode;
    }

    private static void ShutdownOnSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        Log.Write($"{context.Signal} received, shutting down");
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Log.Write("Closing the UI");
            App.Lifetime?.Shutdown();
        });
        // Whatever the UI does, a request to terminate must end the process.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            Log.Write("Shutdown did not finish in time, exiting");
            Environment.Exit(0);
        });
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

/// <summary>A host lifetime that leaves process signals to the desktop app.</summary>
internal sealed class DesktopHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
