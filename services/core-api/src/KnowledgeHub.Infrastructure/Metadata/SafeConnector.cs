using System.Net;
using System.Net.Sockets;

namespace KnowledgeHub.Infrastructure.Metadata;

/// <summary>Turns a host name into addresses. A seam so tests can simulate DNS answers, including hostile ones.</summary>
public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class SystemHostResolver : IHostResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) => Dns.GetHostAddressesAsync(host, ct);
}

/// <summary>
/// Opens the TCP connection for the fetcher. It resolves the name once, refuses if ANY answer is not allowed, and
/// connects to one of the addresses it just checked. Because the socket goes to the checked IP, a DNS server that
/// answers differently the second time (DNS rebinding) gets no second chance.
/// </summary>
public sealed class SafeConnector(ISsrfPolicy policy, IHostResolver resolver)
{
    public async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken ct)
    {
        var addresses = await resolver.ResolveAsync(endpoint.Host, ct);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        foreach (var a in addresses)
            if (!policy.IsAddressAllowed(a))
                throw new SsrfBlockedException($"'{endpoint.Host}' resolves to an address that is not allowed.");

        Exception? last = null;
        foreach (var a in addresses)
        {
            var socket = new Socket(a.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(a, endpoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception e) when (e is SocketException)
            {
                socket.Dispose();
                last = e;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }
}
