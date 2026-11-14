using System.Collections.Concurrent;
using LeadPilot.Application.Tenancy;
using Microsoft.Extensions.Caching.Memory;

namespace LeadPilot.Infrastructure.Analytics;

/// <summary>
/// In-process, size-bounded cache of analytics results. Keys are built ONLY with <see cref="TenantCacheKeys"/> (<c>t:{tenant}:analytics:v{version}:{key}</c>), so a key can never exist without a tenant and two tenants can never
/// share one. Entries live for a short TTL; <see cref="Invalidate"/> bumps the tenant's version so every entry of that tenant becomes unreachable at once (the rollup refresh does this). Each process (Web, Api) has its own
/// cache — the key also contains the rollup's refresh stamp, so a refresh done by the Worker invalidates other processes' entries on their next read of the stamp.
/// </summary>
public sealed class AnalyticsCache
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 2000 });
    private readonly ConcurrentDictionary<Guid, int> _versions = new();

    public const string Area = "analytics";

    public string Key(Guid tenantId, string key) => TenantCacheKeys.For(tenantId, Area, $"v{_versions.GetValueOrDefault(tenantId)}:{key}");

    public async Task<T> GetOrAddAsync<T>(Guid tenantId, string key, TimeSpan ttl, Func<Task<T>> factory)
    {
        var k = Key(tenantId, key);
        if (ttl > TimeSpan.Zero && _cache.TryGetValue(k, out var hit) && hit is T cached) return cached;
        var value = await factory();
        if (ttl > TimeSpan.Zero) _cache.Set(k, value, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = ttl });
        return value;
    }

    public void Invalidate(Guid tenantId) => _versions.AddOrUpdate(tenantId, 1, (_, v) => v + 1);

    public bool Contains(Guid tenantId, string key) => _cache.TryGetValue(Key(tenantId, key), out _);
}
