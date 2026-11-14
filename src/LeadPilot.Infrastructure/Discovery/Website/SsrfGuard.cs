using System.Net;
using System.Net.Sockets;

namespace LeadPilot.Infrastructure.Discovery.Website;

public sealed class SsrfBlockedException(string message) : Exception(message);

/// <summary>Decides whether an address may be contacted. The default policy allows PUBLIC addresses only.</summary>
public interface IAddressPolicy
{
    bool IsAllowed(IPAddress address);
}

public sealed class PublicOnlyAddressPolicy : IAddressPolicy
{
    public bool IsAllowed(IPAddress address) => IpAddressClassifier.IsPublic(address);
}

public interface IDnsResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct);
}

public sealed class SystemDnsResolver : IDnsResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct) => await Dns.GetHostAddressesAsync(host, ct);
}

public static class IpAddressClassifier
{
    /// <summary>True only for globally routable unicast addresses. Loopback, private, link-local (incl. the 169.254.169.254 metadata endpoint), CGNAT, multicast, reserved, documentation and
    /// transition ranges (IPv4-mapped, NAT64, 6to4, Teredo) are all rejected; embedded IPv4 addresses are classified as IPv4.</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6) return IsPublicV4(ip.MapToIPv4().GetAddressBytes());
            var b = ip.GetAddressBytes();
            if (ip.Equals(IPAddress.IPv6None) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6Loopback)) return false;
            if (b[0] == 0xFF) return false;                                        // multicast
            if ((b[0] & 0xFE) == 0xFC) return false;                               // fc00::/7 unique local (incl. fd00:ec2::254)
            if (b[0] == 0xFE && (b[1] & 0xC0) is 0x80 or 0xC0) return false;      // fe80::/10 link-local, fec0::/10 site-local
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0D && b[3] == 0xB8) return false;   // 2001:db8::/32 documentation
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return false;   // 2001::/32 Teredo
            if (b[0] == 0x20 && b[1] == 0x02) return IsPublicV4([b[2], b[3], b[4], b[5]]);     // 2002::/16 6to4 embeds IPv4
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
                return IsPublicV4([b[12], b[13], b[14], b[15]]);                   // 64:ff9b::/96 NAT64 embeds IPv4
            if (b[0] == 0x01 && b[1] == 0x00 && b[2] == 0 && b[3] == 0 && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0) return false;  // 100::/64 discard
            if (b.Take(12).All(x => x == 0)) return false;                         // ::a.b.c.d IPv4-compatible (deprecated)
            return (b[0] & 0xE0) == 0x20;                                          // only 2000::/3 global unicast is allowed
        }
        return ip.AddressFamily == AddressFamily.InterNetwork && IsPublicV4(ip.GetAddressBytes());
    }

    private static bool IsPublicV4(byte[] a)
    {
        var (b0, b1, b2) = (a[0], a[1], a[2]);
        if (b0 == 0 || b0 == 10 || b0 == 127) return false;                        // this network, private, loopback
        if (b0 == 100 && b1 is >= 64 and <= 127) return false;                     // CGNAT
        if (b0 == 169 && b1 == 254) return false;                                  // link-local incl. cloud metadata
        if (b0 == 172 && b1 is >= 16 and <= 31) return false;                      // private
        if (b0 == 192 && b1 == 0 && b2 is 0 or 2) return false;                    // IETF protocol / TEST-NET-1
        if (b0 == 192 && b1 == 88 && b2 == 99) return false;                       // 6to4 relay
        if (b0 == 192 && b1 == 168) return false;                                  // private
        if (b0 == 198 && b1 is 18 or 19) return false;                             // benchmarking
        if (b0 == 198 && b1 == 51 && b2 == 100) return false;                      // TEST-NET-2
        if (b0 == 203 && b1 == 0 && b2 == 113) return false;                       // TEST-NET-3
        if (b0 >= 224) return false;                                               // multicast, reserved, broadcast
        return true;
    }
}

/// <summary>Validates a URL BEFORE any request (and again for every redirect hop): scheme, port, credentials, host name and every address it resolves to.</summary>
public sealed class UrlGuard(IDnsResolver dns, IAddressPolicy policy)
{
    private static readonly string[] BlockedSuffixes = [".local", ".localhost", ".internal", ".intranet", ".lan", ".home", ".corp", ".private"];
    private static readonly string[] BlockedNames = ["localhost", "metadata", "metadata.google.internal", "instance-data"];

    public async Task<Uri> ValidateAsync(Uri uri, CancellationToken ct)
    {
        if (!uri.IsAbsoluteUri) throw new SsrfBlockedException("The address is not absolute.");
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) throw new SsrfBlockedException($"Scheme '{uri.Scheme}' is not allowed.");
        if (!string.IsNullOrEmpty(uri.UserInfo)) throw new SsrfBlockedException("Addresses with embedded credentials are not allowed.");
        if (uri.Port is not (80 or 443)) throw new SsrfBlockedException($"Port {uri.Port} is not allowed; only the default web ports are fetched.");
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) throw new SsrfBlockedException("The address has no host.");
        if (BlockedNames.Contains(host) || BlockedSuffixes.Any(host.EndsWith)) throw new SsrfBlockedException($"Host '{host}' is internal.");

        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            if (!policy.IsAllowed(literal)) throw new SsrfBlockedException($"Address {literal} is not publicly routable.");
            return uri;
        }
        if (!host.Contains('.')) throw new SsrfBlockedException($"Host '{host}' is a single-label (internal) name.");
        IReadOnlyList<IPAddress> addresses;
        try { addresses = await dns.ResolveAsync(host, ct); }
        catch (SocketException) { throw new SsrfBlockedException($"Host '{host}' could not be resolved."); }
        if (addresses.Count == 0) throw new SsrfBlockedException($"Host '{host}' could not be resolved.");
        // Every answer must be public: a name that also resolves to an internal address is a rebinding / split-horizon risk.
        if (addresses.Any(a => !policy.IsAllowed(a))) throw new SsrfBlockedException($"Host '{host}' resolves to a non-public address.");
        return uri;
    }
}
