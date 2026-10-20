using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabSign.Core;

/// <summary>
/// Settings read from <c>config.json</c> in <see cref="DataFolder"/>
/// (<c>%APPDATA%\LabSign</c> on Windows, <c>~/.config/labsign</c> on Linux). Every field has a
/// default, so a missing or partial file is fine; an unreadable one falls back to the defaults.
/// </summary>
public sealed class LabSignConfig
{
    public const int DefaultPort = 5578; // 5577 is the ReLIMS print service.

    /// <summary>Same list as relims-print-service's DEFAULT_ALLOWED_ORIGINS, plus the LIMS dev server.</summary>
    public static readonly IReadOnlyList<string> DefaultAllowedOrigins =
    [
        // Production.
        "https://labserve.net",
        "https://www.labserve.net",
        "https://www.it-mu.co.za",
        "https://apps.it-mu.co.za",
        "https://eacl.it-mu.co.za",
        // Staging / test.
        "https://staging.labserve.net",
        "https://staging.it-mu.co.za",
        "https://test.labserve.net",
        "https://test.it-mu.co.za",
        // Local development (apps/lims and apps/mule vite dev servers).
        "http://localhost:5010",
        "http://127.0.0.1:5010",
        "http://localhost:5013",
        "http://127.0.0.1:5013",
    ];

    public int Port { get; set; } = DefaultPort;

    public List<string> AllowedOrigins { get; set; } = [.. DefaultAllowedOrigins];

    /// <summary>
    /// The SafeNet Authentication Client PKCS#11 module: <c>eTPKCS11.dll</c> on Windows (as in
    /// LIMS_SOURCE Signing.vb), <c>libeToken.so</c> from SAC for Linux.
    /// </summary>
    public string Pkcs11LibraryPath { get; set; } = DefaultPkcs11LibraryPath();

    /// <summary>Used only when the token holds no certificate matching its key (the legacy setup).</summary>
    public string CertificateFallbackPath { get; set; } = OperatingSystem.IsWindows()
        ? @"C:\LIMS-Data\Security\signature.cer"
        : Path.Combine(DataFolder(), "signature.cer");

    /// <summary>The handwritten signature the stamp is drawn on. Without it the stamp is text only.</summary>
    public string StampImagePath { get; set; } = OperatingSystem.IsWindows()
        ? @"C:\LIMS-Data\Security\signature.png"
        : Path.Combine(DataFolder(), "signature.png");

    /// <summary>RFC 3161 timestamp server. Empty means no timestamp, which is what legacy did.</summary>
    public string TsaUrl { get; set; } = "";

    public string DefaultReason { get; set; } = "Report approved";

    /// <summary>
    /// The LIMS share flow (/sign-batch, /finalize), which reads and writes report folders on a
    /// lab file server. Off by default: report signing now goes through the file manager.
    /// </summary>
    public bool ShareSigningEnabled { get; set; }

    public string Location { get; set; } = "";

    /// <summary>Where SAC for Linux installs its module, most likely first.</summary>
    public static readonly IReadOnlyList<string> LinuxPkcs11Candidates =
    [
        "/usr/lib/libeToken.so",
        "/usr/lib64/libeToken.so",
        "/usr/lib/x86_64-linux-gnu/libeToken.so",
        "/usr/local/lib/libeToken.so",
    ];

    public static string DefaultPkcs11LibraryPath(Func<string, bool>? exists = null)
    {
        if (OperatingSystem.IsWindows()) return @"C:\Windows\System32\eTPKCS11.dll";
        exists ??= File.Exists;
        return LinuxPkcs11Candidates.FirstOrDefault(exists) ?? LinuxPkcs11Candidates[0];
    }

    /// <summary>Config, log and the signature files: per user, no admin rights needed.</summary>
    public static string DataFolder()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LabSign");
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var root = string.IsNullOrEmpty(xdg)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : xdg;
        return Path.Combine(root, "labsign");
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static LabSignConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<LabSignConfig>(File.ReadAllText(path), JsonOptions);
                if (loaded is not null)
                {
                    loaded.AllowedOrigins = loaded.AllowedOrigins?.Where(o => !string.IsNullOrWhiteSpace(o)).ToList() ?? [];
                    if (loaded.AllowedOrigins.Count == 0) loaded.AllowedOrigins = [.. DefaultAllowedOrigins];
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write($"Config at {path} is unreadable, using defaults: {ex.Message}");
        }
        return new LabSignConfig();
    }

    /// <summary>Writes the defaults out once, so there is a file to edit.</summary>
    public static void EnsureExists(string path)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new LabSignConfig(), JsonOptions));
    }
}
