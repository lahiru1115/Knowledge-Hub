using System.Net;
using System.Net.Sockets;
using KnowledgeHub.Domain;

namespace KnowledgeHub.Infrastructure.Metadata;

/// <summary>
/// The rules for which addresses the server may fetch (Doc 02 §5.4). The fetcher asks this before every request and
/// before every connection, so a redirect or a DNS answer cannot lead it somewhere private.
/// </summary>
public interface ISsrfPolicy
{
    /// <summary>Checks the parts of a URL: scheme, port, credentials, obviously internal host names.</summary>
    MetadataError? CheckUri(Uri uri);

    /// <summary>True when the server may connect to this address.</summary>
    bool IsAddressAllowed(IPAddress address);
}

public sealed class SsrfPolicy : ISsrfPolicy
{
    public MetadataError? CheckUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri) return MetadataError.BlockedUrl;
        if (uri.Scheme is not ("http" or "https")) return MetadataError.BlockedUrl;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return MetadataError.BlockedUrl;
        if (uri.Port is not (80 or 443)) return MetadataError.BlockedUrl;
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) return MetadataError.BlockedUrl;
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".internal", StringComparison.Ordinal)
            || host.EndsWith(".localdomain", StringComparison.Ordinal))
            return MetadataError.BlockedUrl;
        // A literal address is judged now; a name is judged when it is resolved, at connection time.
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal) && !IsAddressAllowed(literal))
            return MetadataError.BlockedUrl;
        return null;
    }

    public bool IsAddressAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork) return !InAny(address, V4Blocked);
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (InAny(address, V6Blocked)) return false;
        // Prefixes that carry an IPv4 address: judge the embedded address too.
        var b = address.GetAddressBytes();
        if (InRange(address, "64:ff9b::", 96) || InRange(address, "64:ff9b:1::", 48))   // NAT64
            return IsAddressAllowed(new IPAddress(b[12..16]));
        if (InRange(address, "2002::", 16))                                              // 6to4
            return IsAddressAllowed(new IPAddress(b[2..6]));
        return true;
    }

    private static readonly (string Net, int Bits)[] V4Blocked =
    [
        ("0.0.0.0", 8), ("10.0.0.0", 8), ("100.64.0.0", 10), ("127.0.0.0", 8), ("169.254.0.0", 16),
        ("172.16.0.0", 12), ("192.0.0.0", 24), ("192.0.2.0", 24), ("192.88.99.0", 24), ("192.168.0.0", 16),
        ("198.18.0.0", 15), ("198.51.100.0", 24), ("203.0.113.0", 24), ("224.0.0.0", 4), ("240.0.0.0", 4),
    ];

    private static readonly (string Net, int Bits)[] V6Blocked =
    [
        ("::", 128), ("::1", 128), ("::", 96) /* IPv4-compatible */, ("100::", 64), ("2001::", 32) /* Teredo */,
        ("2001:db8::", 32), ("fc00::", 7), ("fe80::", 10), ("fec0::", 10), ("ff00::", 8),
    ];

    private static bool InAny(IPAddress a, (string Net, int Bits)[] ranges)
    {
        foreach (var (net, bits) in ranges)
            if (InRange(a, net, bits)) return true;
        return false;
    }

    private static bool InRange(IPAddress address, string net, int bits)
    {
        var a = address.GetAddressBytes();
        var n = IPAddress.Parse(net).GetAddressBytes();
        if (a.Length != n.Length) return false;
        var whole = bits / 8;
        for (var i = 0; i < whole; i++)
            if (a[i] != n[i]) return false;
        var rest = bits % 8;
        if (rest == 0) return true;
        var mask = (byte)(0xFF << (8 - rest));
        return (a[whole] & mask) == (n[whole] & mask);
    }
}

/// <summary>Raised from the connection step when a name resolves to an address the policy refuses.</summary>
public sealed class SsrfBlockedException(string message) : Exception(message);
