using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Shared.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Services.Tenancy;

public sealed class RoleService(
    AppDbContext db, ITenantContext tenant, IAuditService audit, IClock clock, IPermissionEvaluator evaluator,
    IValidator<SaveRoleRequest> validator, ILogger<RoleService> logger) : IRoleService
{
    public async Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var roles = await db.TenantRoles.AsNoTracking().Where(r => r.TenantId == tenantId).OrderByDescending(r => r.IsSystem).ThenBy(r => r.Name).ToListAsync(ct);
        var perms = await db.RolePermissions.AsNoTracking().Where(p => p.TenantId == tenantId).GroupBy(p => p.RoleId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var users = await db.TenantUserRoles.AsNoTracking().Where(u => u.TenantId == tenantId).GroupBy(u => u.RoleId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return roles.Select(r => new RoleDto(r.Id, r.Name, r.Description, r.IsSystem, perms.GetValueOrDefault(r.Id), users.GetValueOrDefault(r.Id))).ToList();
    }

    public async Task<Result<RoleDetailDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var role = await db.TenantRoles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        return role is null ? Result.Failure<RoleDetailDto>(Error.NotFound("Role")) : Result.Success(await Detail(role, tenantId, ct));
    }

    public Task<Result<RoleDetailDto>> CreateAsync(SaveRoleRequest request, CancellationToken ct = default) =>
        ResultFlow.GuardAsync(async () =>
        {
            var tenantId = tenant.RequireTenantId();
            if (await validator.CheckAsync(request, ct) is { } invalid) return Result.Failure<RoleDetailDto>(invalid.Error);
            var codes = request.Permissions.Distinct().ToList();
            if (await CheckPermissions(tenantId, codes, current: [], ct) is { } problem) return Result.Failure<RoleDetailDto>(problem);
            var name = request.Name.Trim();
            if (await db.TenantRoles.AnyAsync(r => r.TenantId == tenantId && r.Name == name, ct)) return Result.Failure<RoleDetailDto>(Error.Conflict($"A role named '{name}' already exists."));

            var role = Role.Create(tenantId, name, request.Description, isSystem: false, clock.UtcNow);
            db.TenantRoles.Add(role);
            var catalog = await db.Permissions.Where(p => codes.Contains(p.Code)).ToListAsync(ct);
            foreach (var p in catalog) db.RolePermissions.Add(RolePermission.Create(tenantId, role.Id, p.Id));
            audit.Record(new AuditEntry("Role.Created", "Role", role.Id.ToString(), $"Role '{role.Name}' created with {codes.Count} permission(s)."));
            await db.SaveChangesAsync(ct);
            return Result.Success(await Detail(role, tenantId, ct));
        }, logger);

    public Task<Result<RoleDetailDto>> UpdateAsync(Guid id, SaveRoleRequest request, CancellationToken ct = default) =>
        ResultFlow.GuardAsync(async () =>
        {
            var tenantId = tenant.RequireTenantId();
            var role = await db.TenantRoles.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct); // explicit ownership check
            if (role is null) return Result.Failure<RoleDetailDto>(Error.NotFound("Role"));
            if (role.IsSystem) return Result.Failure<RoleDetailDto>(Error.Conflict("System roles cannot be modified. Create a custom role instead."));
            if (await validator.CheckAsync(request, ct) is { } invalid) return Result.Failure<RoleDetailDto>(invalid.Error);

            var wanted = request.Permissions.Distinct().ToList();
            var links = await db.RolePermissions.Where(rp => rp.TenantId == tenantId && rp.RoleId == id).ToListAsync(ct);
            var currentCodes = (await TenantQueries.PermissionCodesByRoleAsync(db, tenantId, [id], ct)).GetValueOrDefault(id) ?? [];
            if (await CheckPermissions(tenantId, wanted, currentCodes, ct) is { } problem) return Result.Failure<RoleDetailDto>(problem);

            var name = request.Name.Trim();
            if (await db.TenantRoles.AnyAsync(r => r.TenantId == tenantId && r.Name == name && r.Id != id, ct)) return Result.Failure<RoleDetailDto>(Error.Conflict($"A role named '{name}' already exists."));
            role.Rename(name, request.Description, clock.UtcNow);

            var catalog = await db.Permissions.Where(p => wanted.Contains(p.Code)).ToListAsync(ct);
            foreach (var stale in links.Where(l => catalog.All(p => p.Id != l.PermissionId))) db.RolePermissions.Remove(stale);
            foreach (var p in catalog.Where(p => links.All(l => l.PermissionId != p.Id))) db.RolePermissions.Add(RolePermission.Create(tenantId, id, p.Id));

            audit.Record(new AuditEntry("Role.Updated", "Role", id.ToString(), $"Role '{role.Name}' updated ({wanted.Count} permission(s))."));
            await db.SaveChangesAsync(ct);
            return Result.Success(await Detail(role, tenantId, ct));
        }, logger);

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var role = await db.TenantRoles.FirstOrDefaultAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        if (role is null) return Result.Failure(Error.NotFound("Role"));
        if (role.IsSystem) return Result.Failure(Error.Conflict("System roles cannot be deleted."));
        var assigned = await db.TenantUserRoles.CountAsync(u => u.TenantId == tenantId && u.RoleId == id, ct);
        if (assigned > 0) return Result.Failure(Error.Conflict($"The role is assigned to {assigned} user(s). Reassign them first."));
        if (await db.TenantInvitations.AnyAsync(i => i.TenantId == tenantId && i.RoleId == id && i.Status == InvitationStatus.Pending, ct))
            return Result.Failure(Error.Conflict("Pending invitations use this role. Revoke them first."));

        // Historic (accepted/revoked) invitations keep a restrict FK; detach by deleting them with the role they referenced.
        db.TenantInvitations.RemoveRange(await db.TenantInvitations.Where(i => i.TenantId == tenantId && i.RoleId == id).ToListAsync(ct));
        db.TenantRoles.Remove(role);
        audit.Record(new AuditEntry("Role.Deleted", "Role", id.ToString(), $"Role '{role.Name}' deleted."));
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<PermissionDto>> ListAssignablePermissionsAsync(CancellationToken ct = default)
    {
        var isPlatform = await db.Tenants.AsNoTracking().Where(t => t.Id == tenant.RequireTenantId()).Select(t => t.IsPlatform).FirstOrDefaultAsync(ct);
        return PermissionCatalog.All.Where(p => isPlatform || !p.IsPlatform).Select(p => new PermissionDto(p.Code, p.Name, p.Group, p.Description, p.IsPlatform)).ToList();
    }

    /// <summary>Valid codes for this tenant, and the actor may only add/remove permissions they hold themselves (no privilege escalation).</summary>
    private async Task<Error?> CheckPermissions(Guid tenantId, IReadOnlyCollection<string> wanted, IReadOnlyCollection<string> current, CancellationToken ct)
    {
        var isPlatform = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.IsPlatform).FirstOrDefaultAsync(ct);
        var invalid = PermissionSet.InvalidFor(wanted, isPlatform);
        if (invalid.Count > 0) return Error.Validation($"Unknown or not assignable permission(s): {string.Join(", ", invalid)}.");
        var changed = wanted.Except(current).Concat(current.Except(wanted)).ToList();
        var actor = await evaluator.GetCurrentAsync(ct);
        var blocked = actor.Ungrantable(changed);
        return blocked.Count > 0 ? Error.Forbidden($"You cannot grant or revoke permissions you do not hold: {string.Join(", ", blocked)}.") : null;
    }

    private async Task<RoleDetailDto> Detail(Role role, Guid tenantId, CancellationToken ct)
    {
        var codes = (await TenantQueries.PermissionCodesByRoleAsync(db, tenantId, [role.Id], ct)).GetValueOrDefault(role.Id) ?? [];
        var users = await db.TenantUserRoles.CountAsync(u => u.TenantId == tenantId && u.RoleId == role.Id, ct);
        return new RoleDetailDto(role.Id, role.Name, role.Description, role.IsSystem, codes.Order(StringComparer.Ordinal).ToList(), users);
    }
}
