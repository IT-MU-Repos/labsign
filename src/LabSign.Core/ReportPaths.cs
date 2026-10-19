namespace LabSign.Core;

/// <summary>
/// The only files LabSign will read, write or delete: a report in
/// <c>…\Reports\&lt;Type&gt;\Pending\&lt;name&gt;</c> and its twin in <c>…\Reports\&lt;Type&gt;\Signed\&lt;name&gt;</c>
/// (SignReports.vb reportFolder). The paths arrive from a web page, so they are checked here rather
/// than trusted: nothing outside a Reports tree, no traversal, and the signed path must be the exact
/// sibling of the pending one.
/// </summary>
public static class ReportPaths
{
    private static readonly char[] Separators = ['\\', '/'];
    private static readonly char[] BadNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    public static bool TryValidate(string? filename, string? pendingPath, string? signedPath, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(filename) || string.IsNullOrWhiteSpace(pendingPath) || string.IsNullOrWhiteSpace(signedPath))
        {
            error = "filename, pending_path and signed_path are all required.";
            return false;
        }
        if (filename.IndexOfAny(BadNameChars) >= 0 || filename is "." or ".." || filename.Trim() != filename)
        {
            error = $"'{filename}' is not a plain file name.";
            return false;
        }

        var pending = Split(pendingPath);
        var signed = Split(signedPath);
        if (pending is null || signed is null)
        {
            error = "Paths may not contain '.' or '..' segments.";
            return false;
        }
        if (!IsReportFile(pending, "Pending", filename))
        {
            error = $"pending_path must end in \\Reports\\<Type>\\Pending\\{filename}.";
            return false;
        }
        if (!IsReportFile(signed, "Signed", filename))
        {
            error = $"signed_path must end in \\Reports\\<Type>\\Signed\\{filename}.";
            return false;
        }
        // Same root, same type folder: only the Pending/Signed segment may differ.
        var same = pending.Length == signed.Length;
        for (var i = 0; same && i < pending.Length; i++)
        {
            if (i == pending.Length - 2) continue;
            same = string.Equals(pending[i], signed[i], StringComparison.OrdinalIgnoreCase);
        }
        if (!same)
        {
            error = "signed_path must be the Signed sibling of pending_path.";
            return false;
        }
        return true;
    }

    /// <summary>Splits on either separator; null when any segment is '.' or '..'.</summary>
    private static string[]? Split(string path)
    {
        var parts = path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => p is "." or "..") ? null : parts;
    }

    private static bool IsReportFile(string[] parts, string folder, string filename) =>
        parts.Length >= 5
        && string.Equals(parts[^1], filename, StringComparison.OrdinalIgnoreCase)
        && string.Equals(parts[^2], folder, StringComparison.OrdinalIgnoreCase)
        && string.Equals(parts[^4], "Reports", StringComparison.OrdinalIgnoreCase);
}
