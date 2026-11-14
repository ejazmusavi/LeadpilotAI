using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Tenancy;

/// <summary>
/// Effective permissions = union of the permissions of the user's roles in the tenant.
/// A platform user operating inside a tenant they are not a member of (audited switch) carries the permissions of their platform roles.
/// </summary>
public sealed class PermissionEvaluator(AppDbContext db, ITenantContext tenant, ICurrentUser currentUser, IMembershipResolver membership) : IPermissionEvaluator
{
    private (Guid User, Guid Tenant, PermissionSet Set)? _cache;

    public async Task<PermissionSet> GetCurrentAsync(CancellationToken ct = default)
    {
        if (currentUser.UserId is not { } userId || tenant.TenantId is not { } tenantId) return PermissionSet.Empty; // fail closed
        if (_cache is { } c && c.User == userId && c.Tenant == tenantId) return c.Set;
        var set = await GetAsync(userId, tenantId, ct);
        _cache = (userId, tenantId, set);
        return set;
    }

    public async Task<bool> HasAsync(string permissionCode, CancellationToken ct = default) => (await GetCurrentAsync(ct)).Has(permissionCode);

    public async Task<PermissionSet> GetAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
    {
        var isMember = await db.UserTenants.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(m => m.UserId == userId && m.TenantId == tenantId && m.Status == MembershipStatus.Active, ct);

        IQueryable<string> codes;
        if (isMember)
        {
            codes = from ur in db.TenantUserRoles.IgnoreQueryFilters().AsNoTracking()
                    join rp in db.RolePermissions.IgnoreQueryFilters().AsNoTracking() on ur.RoleId equals rp.RoleId
                    join p in db.Permissions.AsNoTracking() on rp.PermissionId equals p.Id
                    where ur.UserId == userId && ur.TenantId == tenantId && rp.TenantId == tenantId
                    select p.Code;
        }
        else if (await membership.HasPlatformSwitchAsync(userId, ct))
        {
            codes = from m in db.UserTenants.IgnoreQueryFilters().AsNoTracking()
                    join t in db.Tenants.AsNoTracking() on m.TenantId equals t.Id
                    join ur in db.TenantUserRoles.IgnoreQueryFilters().AsNoTracking() on new { m.TenantId, m.UserId } equals new { ur.TenantId, ur.UserId }
                    join rp in db.RolePermissions.IgnoreQueryFilters().AsNoTracking() on ur.RoleId equals rp.RoleId
                    join p in db.Permissions.AsNoTracking() on rp.PermissionId equals p.Id
                    where m.UserId == userId && m.Status == MembershipStatus.Active && t.IsPlatform
                    select p.Code;
        }
        else return PermissionSet.Empty;

        return new PermissionSet(await codes.Distinct().ToListAsync(ct));
    }
}
