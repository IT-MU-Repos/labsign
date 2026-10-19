namespace LabSign.Core;

/// <summary>
/// A plain append-only log for support ("what happened to report X?"). Never given a PIN.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>Null until the app sets it, so tests and tools log nowhere.</summary>
    public static string? FilePath { get; set; }

    public static void Write(string message)
    {
        var path = FilePath;
        if (path is null) return;
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 5 * 1024 * 1024)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Logging must never break signing.
        }
    }
}
