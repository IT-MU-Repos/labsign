namespace LabSign.Core;

/// <summary>
/// Who may talk to the service. Mirrors relims-print-service's require_allowed_origin: a request
/// that carries an Origin must be on the allowlist, and it is rejected before anything happens.
/// CORS alone only hides the response from the page; the side effect would already be done.
/// </summary>
public static class RequestGuards
{
    /// <summary>A browser sends Origin on cross-site requests; curl and local tools send none.</summary>
    public static bool IsOriginAllowed(string? origin, IEnumerable<string> allowed) =>
        string.IsNullOrEmpty(origin) || allowed.Contains(origin, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rejects DNS rebinding: a hostile page that resolves its own name to 127.0.0.1 still sends
    /// its own name in Host, so only loopback names are served.
    /// </summary>
    public static bool IsHostAllowed(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var name = host;
        if (name.StartsWith('['))
        {
            var close = name.IndexOf(']');
            name = close > 0 ? name[..(close + 1)] : name;
        }
        else
        {
            var colon = name.LastIndexOf(':');
            if (colon >= 0) name = name[..colon];
        }
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name == "127.0.0.1"
            || name == "[::1]";
    }
}
