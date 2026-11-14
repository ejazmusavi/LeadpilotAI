using LeadPilot.Application.Security;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Tenancy;

/// <summary>Creates and re-synchronises the default system roles of a tenant from <see cref="SystemRoles"/>. Caller saves.</summary>
public static class RoleProvisioner
{
    /// <summary>Permission catalog → database (insert missing, refresh metadata). Caller saves.</summary>
    public static async Task<Dictionary<string, Permission>> SyncPermissionsAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = await db.Permissions.ToDictionaryAsync(p => p.Code, ct);
        var order = 0;
        foreach (var def in PermissionCatalog.All)
        {
            order += 10;
            if (existing.TryGetValue(def.Code, out var p)) p.Refresh(def.Name, def.Description, def.Group, def.IsPlatform, order);
            else
            {
                p = Permission.Create(def.Code, def.Name, def.Description, def.Group, def.IsPlatform, order);
                db.Permissions.Add(p);
                existing[def.Code] = p;
            }
        }
        return existing;
    }

    /// <summary>Ensures every default system role exists for the tenant and carries exactly its default permissions. Needs the caller to be in a platform scope.</summary>
    public static async Task SyncSystemRolesAsync(AppDbContext db, Tenant tenant, IReadOnlyDictionary<string, Permission> permissions, DateTime nowUtc, CancellationToken ct)
    {
        // Allow-listed IgnoreQueryFilters: provisioning must see the target tenant's roles regardless of the active tenant.
        var roles = await db.TenantRoles.IgnoreQueryFilters().Where(r => r.TenantId == tenant.Id && r.IsSystem).ToListAsync(ct);
        var roleIds = roles.Select(r => r.Id).ToList();
        var links = await db.RolePermissions.IgnoreQueryFilters().Where(rp => rp.TenantId == tenant.Id && roleIds.Contains(rp.RoleId)).ToListAsync(ct);

        foreach (var def in SystemRoles.For(tenant.IsPlatform))
        {
            var role = roles.FirstOrDefault(r => r.Name == def.Name);
            if (role is null)
            {
                role = Role.Create(tenant.Id, def.Name, def.Description, isSystem: true, nowUtc);
                db.TenantRoles.Add(role);
            }

            var wanted = def.Permissions.Where(permissions.ContainsKey).Select(c => permissions[c]).ToList();
            var current = links.Where(l => l.RoleId == role.Id).ToList();
            foreach (var stale in current.Where(l => wanted.All(w => w.Id != l.PermissionId))) db.RolePermissions.Remove(stale);
            foreach (var missing in wanted.Where(w => current.All(l => l.PermissionId != w.Id)))
                db.RolePermissions.Add(RolePermission.Create(tenant.Id, role.Id, missing.Id));
        }
    }
}
