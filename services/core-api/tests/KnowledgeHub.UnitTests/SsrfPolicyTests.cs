using System.Net;
using KnowledgeHub.Domain;
using KnowledgeHub.Infrastructure.Metadata;
using Xunit;

namespace KnowledgeHub.UnitTests;

/// <summary>
/// The blocked-URL suite (Doc 02 §5.4, NFR-SEC): every way of pointing the fetcher at the server's own network
/// must be refused, and ordinary public addresses must still work.
/// </summary>
public class SsrfPolicyTests
{
    private static readonly SsrfPolicy Policy = new();

    public static TheoryData<string> BlockedUrls() =>
    [
        // Loopback, in every spelling
        "http://127.0.0.1/", "http://127.1.2.3/", "http://localhost/", "http://LOCALHOST/", "http://localhost./",
        "http://app.localhost/", "http://[::1]/", "http://[::ffff:127.0.0.1]/", "http://2130706433/", "http://0x7f.0.0.1/",
        "http://0177.0.0.1/", "http://127.1/",
        // Private ranges (RFC 1918) and their edges
        "http://10.0.0.1/", "http://10.255.255.255/", "http://172.16.0.1/", "http://172.31.255.255/",
        "http://192.168.0.1/", "http://192.168.255.255/",
        // Link-local and cloud metadata
        "http://169.254.169.254/latest/meta-data/", "http://169.254.0.1/", "http://[fe80::1]/",
        "http://metadata.google.internal/", "http://[fd00:ec2::254]/",
        // Carrier-grade NAT, unspecified, multicast, broadcast, reserved
        "http://100.64.0.1/", "http://100.127.255.255/", "http://0.0.0.0/", "http://0/", "http://[::]/",
        "http://224.0.0.1/", "http://239.255.255.255/", "http://[ff02::1]/", "http://255.255.255.255/", "http://240.0.0.1/",
        "http://198.18.0.1/", "http://192.0.2.1/",
        // IPv6 forms that carry an IPv4 address or a private range
        "http://[::ffff:10.0.0.1]/", "http://[::ffff:169.254.169.254]/", "http://[64:ff9b::7f00:1]/", "http://[2002:7f00:1::]/",
        "http://[2002:a9fe:a9fe::]/", "http://[fc00::1]/", "http://[fd12:3456::1]/", "http://[::127.0.0.1]/",
        // Internal-looking names
        "http://printer.local/", "http://db.internal/", "http://host.localdomain/",
        // Schemes
        "ftp://example.com/", "file:///etc/passwd", "gopher://example.com/", "ws://example.com/", "data:text/html,hi",
        "javascript:alert(1)",
        // Ports other than 80 and 443
        "http://example.com:8080/", "https://example.com:8443/", "http://example.com:22/", "http://example.com:3306/",
        "http://example.com:5432/", "http://example.com:6379/", "https://example.com:81/x",
        // Credentials in the URL
        "http://user:pass@example.com/", "https://admin@example.com/",
    ];

    [Theory]
    [MemberData(nameof(BlockedUrls))]
    public void Refuses(string url)
    {
        // A spelling .NET refuses to parse can never reach the fetcher, which is just as safe.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        Assert.Equal(MetadataError.BlockedUrl, Policy.CheckUri(uri));
    }

    [Theory]
    [InlineData("http://2130706433/", "127.0.0.1")]
    [InlineData("http://0x7f.0.0.1/", "127.0.0.1")]
    [InlineData("http://0177.0.0.1/", "127.0.0.1")]
    [InlineData("http://127.1/", "127.0.0.1")]
    [InlineData("http://0/", "0.0.0.0")]
    public void Numeric_spellings_are_normalised_before_they_are_judged(string url, string expectedHost) =>
        Assert.Equal(expectedHost, new Uri(url).IdnHost);

    public static TheoryData<string> AllowedUrls() =>
    [
        "http://example.com/", "https://example.com/path?q=1#frag", "https://www.youtube.com/watch?v=abc",
        "https://example.com:443/", "http://example.com:80/", "https://sub.domain.example.co.uk/a/b",
        "http://93.184.216.34/", "https://[2606:2800:220:1:248:1893:25c8:1946]/", "https://xn--bcher-kva.example/",
        "http://172.15.255.255/", "http://172.32.0.1/", "http://100.63.255.255/", "http://100.128.0.1/",
        "http://8.8.8.8/", "https://1.1.1.1/", "http://192.169.0.1/", "http://11.0.0.1/",
    ];

    [Theory]
    [MemberData(nameof(AllowedUrls))]
    public void Allows(string url) => Assert.Null(Policy.CheckUri(new Uri(url)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("::")]
    [InlineData("0.0.0.0")]
    [InlineData("fe80::abcd")]
    [InlineData("fd00::1")]
    [InlineData("ff05::2")]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    [InlineData("2001:db8::1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:c0a8:101::")]
    public void Refuses_addresses_that_a_name_might_resolve_to(string ip) =>
        Assert.False(Policy.IsAddressAllowed(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("93.184.216.34")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:808:808::")]
    public void Allows_public_addresses(string ip) =>
        Assert.True(Policy.IsAddressAllowed(IPAddress.Parse(ip)));
}

/// <summary>The connection step: names that resolve to private addresses (DNS tricks) never get a socket.</summary>
public class SafeConnectorTests
{
    private sealed class FakeResolver(Func<int, IPAddress[]> answer) : IHostResolver
    {
        public int Calls;
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Task.FromResult(answer(Interlocked.Increment(ref Calls)));
    }

    [Fact]
    public async Task Refuses_a_name_that_resolves_to_a_private_address()
    {
        var connector = new SafeConnector(new SsrfPolicy(), new FakeResolver(_ => [IPAddress.Parse("10.0.0.5")]));
        await Assert.ThrowsAsync<SsrfBlockedException>(async () =>
            await connector.ConnectAsync(new DnsEndPoint("evil.example", 80), CancellationToken.None));
    }

    [Fact]
    public async Task Refuses_when_any_answer_is_private_even_if_others_are_public()
    {
        var connector = new SafeConnector(new SsrfPolicy(),
            new FakeResolver(_ => [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("169.254.169.254")]));
        await Assert.ThrowsAsync<SsrfBlockedException>(async () =>
            await connector.ConnectAsync(new DnsEndPoint("mixed.example", 80), CancellationToken.None));
    }

    [Fact]
    public async Task Refuses_an_empty_answer()
    {
        var connector = new SafeConnector(new SsrfPolicy(), new FakeResolver(_ => []));
        await Assert.ThrowsAsync<System.Net.Sockets.SocketException>(async () =>
            await connector.ConnectAsync(new DnsEndPoint("nowhere.example", 80), CancellationToken.None));
    }
}
