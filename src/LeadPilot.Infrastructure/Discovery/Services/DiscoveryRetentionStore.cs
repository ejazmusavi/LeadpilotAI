using LeadPilot.Application.Abstractions;
using LeadPilot.Domain.Discovery;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Discovery.Services;

/// <summary>Purges provider content of the ACTIVE tenant's results whose retention period ended. The external id, review status and lead link are kept.</summary>
public sealed class DiscoveryRetentionStore(AppDbContext db, ITenantContext tenant, IClock clock)
{
    public async Task<int> PurgeAsync(CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId(); var now = clock.UtcNow;
        var expired = await db.DiscoveryResults.Where(r => r.TenantId == tenantId && !r.ContentPurged && r.ExpiresAtUtc != null && r.ExpiresAtUtc < now).Take(500).ToListAsync(ct);
        foreach (var r in expired) r.PurgeContent(now);
        if (expired.Count > 0) await db.SaveChangesAsync(ct);
        return expired.Count;
    }
}
