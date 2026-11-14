using LeadPilot.Domain.Campaigns;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Campaigns;

/// <summary>
/// Cross-tenant lookup for the sweep job: which tenants have campaigns that need a push. The ONLY campaign code allowed to ignore the tenant filter (allow-listed by an architecture test);
/// it returns tenant ids only, and every action then runs inside <see cref="ITenantScopeRunner"/> for that tenant.
/// </summary>
public sealed class CampaignDirectory(AppDbContext db)
{
    public async Task<List<Guid>> TenantsNeedingAttentionAsync(DateTime nowUtc, int staleMinutes, CancellationToken ct)
    {
        var grace = nowUtc.AddMinutes(-Math.Max(0, staleMinutes));
        return await db.Campaigns.IgnoreQueryFilters().AsNoTracking()
            .Where(c => (c.Status == CampaignStatus.Scheduled && ((c.ScheduledAtUtc != null && c.ScheduledAtUtc <= nowUtc) || (c.ScheduledAtUtc == null && c.FollowUpDelayDays != null)))
                     || (c.Status == CampaignStatus.Running && (c.LeaseUntilUtc == null || c.LeaseUntilUtc < nowUtc) && (c.NextRunAtUtc == null || c.NextRunAtUtc < grace)))
            .Select(c => c.TenantId).Distinct().ToListAsync(ct);
    }
}
