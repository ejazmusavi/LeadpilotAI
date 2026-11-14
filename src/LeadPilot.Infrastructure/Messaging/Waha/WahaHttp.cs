using System.Net;
using System.Net.Sockets;
using LeadPilot.Application.Messaging;
using LeadPilot.Infrastructure.Discovery.Website;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Messaging.Waha;

/// <summary>Which addresses a tenant-supplied WAHA base URL may resolve to.</summary>
public sealed class WahaAddressPolicy(IOptions<WahaOptions> options, IAddressPolicy publicOnly) : IAddressPolicy
{
    public bool IsAllowed(IPAddress address)
    {
        if (!options.Value.AllowPrivateHosts) return publicOnly.IsAllowed(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork) { var b = address.GetAddressBytes(); return !(b[0] == 169 && b[1] == 254); }   // link-local / cloud metadata
        return !address.IsIPv6LinkLocal;
    }
}

/// <summary>Builds the one HttpClient the adapter uses. Name resolution happens inside the connect callback so a DNS answer cannot change between check and connect.</summary>
public sealed class WahaHttpClientFactory(IDnsResolver dns, WahaAddressPolicy policy, IOptions<WahaOptions> options) : IDisposable
{
    private readonly Lazy<HttpClient> _client = new(() => Build(dns, policy, options.Value), isThreadSafe: true);
    public HttpClient Create() => _client.Value;

    private static HttpClient Build(IDnsResolver dns, IAddressPolicy policy, WahaOptions o)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(2, o.TimeoutSeconds));
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(Math.Min(10, Math.Max(2, o.TimeoutSeconds))),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2), MaxConnectionsPerServer = 16,
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                IReadOnlyList<IPAddress> addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await dns.ResolveAsync(host, ct);
                var allowed = addresses.Where(policy.IsAllowed).ToList();
                if (allowed.Count == 0 || allowed.Count != addresses.Count) throw new SsrfBlockedException($"Connection to '{host}' blocked: address not allowed for a WhatsApp gateway.");
                Exception? last = null;
                foreach (var ip in allowed)
                {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
                    catch (SocketException ex) { socket.Dispose(); last = ex; }
                    catch { socket.Dispose(); throw; }
                }
                throw last ?? new SocketException((int)SocketError.HostUnreachable);
            },
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    public void Dispose() { if (_client.IsValueCreated) _client.Value.Dispose(); }
}

/// <summary>Per-host circuit breaker: after N consecutive transient failures calls are refused for a while instead of piling up on a dead gateway.</summary>
public sealed class WahaCircuitBreaker(IOptions<WahaOptions> options, TimeProvider time)
{
    private sealed class State { public int Failures; public DateTimeOffset OpenUntil; }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    public bool IsOpen(string host)
    {
        if (!_states.TryGetValue(host, out var s)) return false;
        lock (s) return s.OpenUntil > time.GetUtcNow();
    }

    public void Success(string host) { if (_states.TryGetValue(host, out var s)) lock (s) { s.Failures = 0; s.OpenUntil = default; } }

    public void Failure(string host)
    {
        var s = _states.GetOrAdd(host, _ => new State());
        lock (s)
        {
            s.Failures++;
            if (s.Failures >= Math.Max(1, options.Value.BreakerFailureThreshold)) s.OpenUntil = time.GetUtcNow().AddSeconds(Math.Max(1, options.Value.BreakerOpenSeconds));
        }
    }
}

/// <summary>Validates a tenant-supplied base URL before it is stored or used.</summary>
public static class WahaEndpoint
{
    public static bool TryNormalize(string? raw, out string baseUrl, out string error)
    {
        baseUrl = string.Empty; error = string.Empty;
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s) || s.Length > 500) { error = "Enter the WAHA base URL, e.g. https://waha.example.com"; return false; }
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) { error = "The WAHA base URL must start with https:// (or http:// on a private network)."; return false; }
        if (!string.IsNullOrEmpty(uri.UserInfo)) { error = "Do not put credentials in the URL; use the API key field."; return false; }
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) { error = "The base URL must not contain a query string or fragment."; return false; }
        baseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }
}
