using System.Net;
using System.Net.Sockets;
using LeadPilot.Application.Discovery;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Discovery.Website;

/// <summary>The ONLY way website fetching may obtain an <see cref="HttpClient"/> (enforced by an architecture test).</summary>
public interface ISafeHttpClientFactory
{
    HttpClient Create();
}

/// <summary>
/// HTTP client whose sockets can only connect to addresses the <see cref="IAddressPolicy"/> allows. Name resolution happens INSIDE the connect callback and the socket connects to the
/// validated address itself, so a DNS answer cannot change between "check" and "connect" (DNS rebinding). Redirects are never followed by the handler (the fetcher validates every hop),
/// no proxy, no cookies, no credentials, short timeouts.
/// </summary>
public sealed class SafeHttpClientFactory(IDnsResolver dns, IAddressPolicy policy, IOptions<DiscoveryOptions> options) : ISafeHttpClientFactory, IDisposable
{
    private readonly Lazy<HttpClient> _client = new(() => Build(dns, policy, options.Value.Website), isThreadSafe: true);

    public HttpClient Create() => _client.Value;

    private static HttpClient Build(IDnsResolver dns, IAddressPolicy policy, WebsiteFetchOptions o)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1, o.TimeoutSeconds)), PooledConnectionLifetime = TimeSpan.FromMinutes(2), MaxConnectionsPerServer = 4,
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                IReadOnlyList<IPAddress> addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await dns.ResolveAsync(host, ct);
                var allowed = addresses.Where(policy.IsAllowed).ToList();
                if (allowed.Count == 0 || allowed.Count != addresses.Count) throw new SsrfBlockedException($"Connection to '{host}' blocked: it resolves to a non-public address.");
                Exception? last = null;
                foreach (var ip in allowed)
                {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
                    catch (Exception ex) when (ex is SocketException) { socket.Dispose(); last = ex; }
                    catch { socket.Dispose(); throw; }
                }
                throw last ?? new SocketException((int)SocketError.HostUnreachable);
            },
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(Math.Max(2, o.TimeoutSeconds + 5)) };
    }

    public void Dispose() { if (_client.IsValueCreated) _client.Value.Dispose(); }
}
