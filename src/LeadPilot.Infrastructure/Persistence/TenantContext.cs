using LeadPilot.Application.Abstractions;

namespace LeadPilot.Infrastructure.Persistence;

/// <summary>Scoped (per request / per job) tenant identity. Starts empty: no tenant means no access.</summary>
public sealed class TenantContext : ITenantContext, ITenantContextAccessor
{
    private int _platformDepth;

    public Guid? TenantId { get; private set; }
    public bool IsPlatformScope => _platformDepth > 0;

    public Guid RequireTenantId() => TenantId ?? throw new TenantContextMissingException();

    public void SetTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty) throw new TenantContextMissingException("An empty tenant id is not a tenant.");
        TenantId = tenantId;
    }

    public void Clear() => TenantId = null;

    public IDisposable BeginPlatformScope()
    {
        _platformDepth++;
        return new PlatformScope(this);
    }

    private sealed class PlatformScope(TenantContext owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._platformDepth--;
        }
    }
}
