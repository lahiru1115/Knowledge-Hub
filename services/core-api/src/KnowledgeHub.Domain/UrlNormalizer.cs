using System.Web;

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

        var query = HttpUtility.ParseQueryString(uri.Query);
        var kept = query.AllKeys
            .Where(k => k is not null && !k.StartsWith("utm_", StringComparison.OrdinalIgnoreCase))
            .SelectMany(k => (query.GetValues(k) ?? []).Select(v => $"{Uri.EscapeDataString(k!)}={Uri.EscapeDataString(v)}"))
            .ToList();

        var path = uri.AbsolutePath.TrimEnd('/');
        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        normalized = $"{uri.Scheme}://{uri.IdnHost.ToLowerInvariant()}{port}{path}{(kept.Count > 0 ? "?" + string.Join('&', kept) : "")}";
        return true;
    }

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
