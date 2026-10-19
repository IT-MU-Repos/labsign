using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabSign.Core;

/// <summary>
/// Settings read from <c>%APPDATA%\LabSign\config.json</c>. Every field has a default, so a
/// missing or partial file is fine; an unreadable one falls back to the defaults entirely.
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
        // Local development (apps/lims vite dev server).
        "http://localhost:5010",
        "http://127.0.0.1:5010",
    ];

    public int Port { get; set; } = DefaultPort;

    public List<string> AllowedOrigins { get; set; } = [.. DefaultAllowedOrigins];

    /// <summary>The SafeNet Authentication Client PKCS#11 module, as in LIMS_SOURCE Signing.vb.</summary>
    public string Pkcs11LibraryPath { get; set; } = @"C:\Windows\System32\eTPKCS11.dll";

    /// <summary>Used only when the token holds no certificate matching its key (the legacy setup).</summary>
    public string CertificateFallbackPath { get; set; } = @"C:\LIMS-Data\Security\signature.cer";

    /// <summary>The handwritten signature the stamp is drawn on. Without it the stamp is text only.</summary>
    public string StampImagePath { get; set; } = @"C:\LIMS-Data\Security\signature.png";

    /// <summary>RFC 3161 timestamp server. Empty means no timestamp, which is what legacy did.</summary>
    public string TsaUrl { get; set; } = "";

    public string DefaultReason { get; set; } = "Report approved";

    public string Location { get; set; } = "";

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
