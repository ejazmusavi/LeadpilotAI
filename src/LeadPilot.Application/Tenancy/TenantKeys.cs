using LeadPilot.Application.Abstractions;

namespace LeadPilot.Application.Tenancy;

/// <summary>Builds tenant-scoped cache keys: <c>t:{tenantId}:{area}:{key}</c>. The only sanctioned way to build a key; fails closed without a tenant.</summary>
public static class TenantCacheKeys
{
    public static string For(ITenantContext tenant, string area, string key) => For(tenant.RequireTenantId(), area, key);

    public static string For(Guid tenantId, string area, string key)
    {
        if (tenantId == Guid.Empty) throw new TenantContextMissingException();
        if (string.IsNullOrWhiteSpace(area) || area.Contains(':')) throw new ArgumentException("Cache area must be non-empty and must not contain ':'.", nameof(area));
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Cache key must be non-empty.", nameof(key));
        return $"t:{tenantId:N}:{area}:{key}";
    }
}

/// <summary>Builds tenant-scoped storage paths: <c>tenants/{tenantId}/...</c>. Rejects traversal, absolute paths and odd characters.</summary>
public static class TenantPaths
{
    public static string For(ITenantContext tenant, string relativePath) => For(tenant.RequireTenantId(), relativePath);

    public static string For(Guid tenantId, string relativePath)
    {
        if (tenantId == Guid.Empty) throw new TenantContextMissingException();
        if (string.IsNullOrWhiteSpace(relativePath)) throw new ArgumentException("A relative path is required.", nameof(relativePath));
        var normalized = relativePath.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Contains('\0'))
            throw new ArgumentException("Path must be relative.", nameof(relativePath));
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
            throw new ArgumentException("Path traversal is not allowed.", nameof(relativePath));
        return $"tenants/{tenantId:N}/{string.Join('/', segments)}";
    }
}
