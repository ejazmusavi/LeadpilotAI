using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Tenancy;

/// <summary>
/// Decides whether a user may act inside a tenant. Runs on every request (cookie and JWT): a claim is a request, never proof.
/// Needs rows of all tenants by definition (no tenant is active yet), so it uses allow-listed IgnoreQueryFilters with explicit predicates.
/// </summary>
public sealed class MembershipResolver(AppDbContext db) : IMembershipResolver
{
    public async Task<TenantAccessResult> ResolveAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
    {
        var active = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => (bool?)u.IsActive).FirstOrDefaultAsync(ct);
        if (active != true) return new(TenantAccessOutcome.UserInactive, tenantId, null, false);

        var tenant = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => new { t.Name, t.Status }).FirstOrDefaultAsync(ct);
        if (tenant is null) return new(TenantAccessOutcome.TenantNotFound, tenantId, null, false);

        var isMember = await db.UserTenants.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(m => m.UserId == userId && m.TenantId == tenantId && m.Status == MembershipStatus.Active, ct);
        var viaPlatform = false;
        if (!isMember)
        {
            viaPlatform = await HasPlatformSwitchAsync(userId, ct);
            if (!viaPlatform) return new(TenantAccessOutcome.NotAMember, tenantId, null, false); // do not reveal the tenant to non-members
        }

        if (tenant.Status != TenantStatus.Active) return new(TenantAccessOutcome.TenantSuspended, tenantId, tenant.Name, viaPlatform);
        return new(TenantAccessOutcome.Granted, tenantId, tenant.Name, viaPlatform);
    }

    public async Task<Guid?> GetDefaultTenantIdAsync(Guid userId, CancellationToken ct = default)
    {
        var q = from m in db.UserTenants.IgnoreQueryFilters().AsNoTracking()
                join t in db.Tenants.AsNoTracking() on m.TenantId equals t.Id
                where m.UserId == userId && m.Status == MembershipStatus.Active && t.Status == TenantStatus.Active
                orderby m.IsDefault descending, m.JoinedAtUtc
                select (Guid?)m.TenantId;
        return await q.FirstOrDefaultAsync(ct);
    }

    public async Task<bool> HasPlatformSwitchAsync(Guid userId, CancellationToken ct = default)
    {
        var q = from m in db.UserTenants.IgnoreQueryFilters().AsNoTracking()
                join t in db.Tenants.AsNoTracking() on m.TenantId equals t.Id
                join ur in db.TenantUserRoles.IgnoreQueryFilters().AsNoTracking() on new { m.TenantId, m.UserId } equals new { ur.TenantId, ur.UserId }
                join rp in db.RolePermissions.IgnoreQueryFilters().AsNoTracking() on ur.RoleId equals rp.RoleId
                join p in db.Permissions.AsNoTracking() on rp.PermissionId equals p.Id
                where m.UserId == userId && m.Status == MembershipStatus.Active && t.IsPlatform && t.Status == TenantStatus.Active
                      && p.Code == PermissionCodes.PlatformSwitchTenant
                select 1;
        return await q.AnyAsync(ct);
    }
}
