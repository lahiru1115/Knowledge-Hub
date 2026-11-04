namespace KnowledgeHub.Domain;

/// <summary>
/// URL rules for resources: only http and https, up to 2,048 characters (FR-RES-01), and a normal form used to
/// spot duplicates (FR-RES-11): lowercase scheme and host, no fragment, no default port, no trailing slash,
/// no <c>utm_*</c> tracking parameters.
/// </summary>
public static class UrlNormalizer
{
    public const int MaxLength = 2048;

    public static bool IsValid(string? url) => TryParse(url, out _);

    public static bool TryNormalize(string? url, out string normalized)
    {
        normalized = "";
        if (!TryParse(url, out var uri)) return false;

        // Split by hand rather than with a name/value collection: that drops parts with no name ("?abc") and groups
        // repeated names, so different links could look like duplicates. Order is kept; only utm_* parts go.
        var kept = new List<string>();
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var name = Decode(eq < 0 ? part : part[..eq]);
            if (name.StartsWith("utm_", StringComparison.OrdinalIgnoreCase)) continue;
            kept.Add(eq < 0 ? Uri.EscapeDataString(name) : $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(Decode(part[(eq + 1)..]))}");
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        normalized = $"{uri.Scheme}://{uri.IdnHost.ToLowerInvariant()}{port}{path}{(kept.Count > 0 ? "?" + string.Join('&', kept) : "")}";
        return true;
    }

    private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    private static bool TryParse(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxLength) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("http" or "https")) return false;
        if (string.IsNullOrEmpty(parsed.Host)) return false;
        try
        {
            // Reading it is what fails for a host that is not a valid internationalised name; the rest of the
            // code reads it freely, so a URL like that must be refused here.
            _ = parsed.IdnHost;
        }
        catch (UriFormatException)
        {
            return false;
        }

        uri = parsed;
        return true;
    }
}
