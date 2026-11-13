using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Services.Tenancy;

/// <summary>Members of the CURRENT tenant. Every lookup is bounded by tenant id explicitly; a user of another tenant is simply "not found".</summary>
public sealed class UserManagementService(
    AppDbContext db, ITenantContext tenant, ICurrentUser currentUser, IAuditService audit, IClock clock, IPermissionEvaluator evaluator,
    ILogger<UserManagementService> logger) : IUserManagementService
{
    public async Task<PagedResult<MemberDto>> ListAsync(string? search, int page, int pageSize, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var q = from m in db.UserTenants.AsNoTracking()
                join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                where m.TenantId == tenantId
                select new { m, u };
        if (!string.IsNullOrWhiteSpace(search)) { var s = search.Trim(); q = q.Where(x => x.u.Email!.Contains(s) || x.u.DisplayName.Contains(s)); }
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderBy(x => x.u.DisplayName).ThenBy(x => x.u.Email).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var roles = await RolesFor(tenantId, rows.Select(r => r.u.Id).ToList(), ct);
        var items = rows.Select(r => new MemberDto(r.u.Id, r.u.Email ?? "", r.u.DisplayName, r.m.Status == MembershipStatus.Active && r.u.IsActive,
            roles.GetValueOrDefault(r.u.Id) ?? [], r.m.JoinedAtUtc, r.u.LastLoginAtUtc)).ToList();
        return new PagedResult<MemberDto>(items, page, pageSize, total);
    }

    public async Task<Result<MemberDto>> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        return await Load(tenantId, userId, ct) is { } dto ? Result.Success(dto) : Result.Failure<MemberDto>(Error.NotFound("User"));
    }

    public async Task<Result<MemberDto>> SetRolesAsync(Guid userId, SetUserRolesRequest request, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var membership = await db.UserTenants.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.UserId == userId, ct);
        if (membership is null) return Result.Failure<MemberDto>(Error.NotFound("User"));

        var wantedIds = request.RoleIds.Distinct().ToList();
        // Every requested role must belong to THIS tenant; ids of other tenants are indistinguishable from unknown ones.
        var wantedRoles = await db.TenantRoles.AsNoTracking().Where(r => r.TenantId == tenantId && wantedIds.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
        if (wantedRoles.Count != wantedIds.Count) return Result.Failure<MemberDto>(Error.Validation("One or more roles do not exist."));

        var existing = await db.TenantUserRoles.Where(ur => ur.TenantId == tenantId && ur.UserId == userId).ToListAsync(ct);
        var added = wantedRoles.Except(existing.Select(e => e.RoleId)).ToList();
        var removed = existing.Where(e => !wantedRoles.Contains(e.RoleId)).ToList();

        // Privilege-escalation guard: you can only hand out or take away roles whose permissions you hold.
        var changedIds = added.Concat(removed.Select(r => r.RoleId)).ToList();
        if (changedIds.Count > 0)
        {
            var perms = await TenantQueries.PermissionCodesByRoleAsync(db, tenantId, changedIds, ct);
            var blocked = (await evaluator.GetCurrentAsync(ct)).Ungrantable(perms.Values.SelectMany(p => p));
            if (blocked.Count > 0) return Result.Failure<MemberDto>(Error.Forbidden($"You cannot change roles carrying permissions you do not hold: {string.Join(", ", blocked)}."));
        }

        // Keep at least one active administrator.
        var holders = await TenantQueries.UsersManageHoldersAsync(db, tenantId, ct);
        if (holders.Contains(userId) && holders.Count == 1)
        {
            var afterCodes = await TenantQueries.PermissionCodesByRoleAsync(db, tenantId, wantedRoles, ct);
            if (!afterCodes.Values.SelectMany(c => c).Contains(PermissionCodes.UsersManage))
                return Result.Failure<MemberDto>(Error.Conflict("At least one active user must keep the Users.Manage permission."));
        }

        db.TenantUserRoles.RemoveRange(removed);
        foreach (var roleId in added) db.TenantUserRoles.Add(UserRole.Create(tenantId, userId, roleId, currentUser.UserId, clock.UtcNow));
        audit.Record(new AuditEntry("User.RolesChanged", "User", userId.ToString(), $"Roles changed for user {userId}: +{added.Count} / -{removed.Count}."));
        await db.SaveChangesAsync(ct);
        return Result.Success((await Load(tenantId, userId, ct))!);
    }

    public async Task<Result> SetActiveAsync(Guid userId, bool active, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var m = await db.UserTenants.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == userId, ct);
        if (m is null) return Result.Failure(Error.NotFound("User"));
        if (!active && await LastAdminOrSelf(tenantId, userId, ct) is { } blocked) return Result.Failure(blocked);
        m.SetStatus(active ? MembershipStatus.Active : MembershipStatus.Disabled);
        audit.Record(new AuditEntry(active ? "User.Enabled" : "User.Disabled", "User", userId.ToString(), $"User {userId} {(active ? "re-enabled" : "disabled")} in this workspace."));
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> RemoveAsync(Guid userId, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var m = await db.UserTenants.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == userId, ct);
        if (m is null) return Result.Failure(Error.NotFound("User"));
        if (await LastAdminOrSelf(tenantId, userId, ct) is { } blocked) return Result.Failure(blocked);
        db.TenantUserRoles.RemoveRange(await db.TenantUserRoles.Where(ur => ur.TenantId == tenantId && ur.UserId == userId).ToListAsync(ct));
        db.UserTenants.Remove(m);
        audit.Record(new AuditEntry("User.Removed", "User", userId.ToString(), $"User {userId} removed from this workspace."));
        await db.SaveChangesAsync(ct);
        logger.LogInformation("User {UserId} removed from tenant {TenantId}", userId, tenantId);
        return Result.Success();
    }

    private async Task<Error?> LastAdminOrSelf(Guid tenantId, Guid userId, CancellationToken ct)
    {
        if (currentUser.UserId == userId) return Error.Conflict("You cannot disable or remove your own membership.");
        var holders = await TenantQueries.UsersManageHoldersAsync(db, tenantId, ct);
        return holders.Contains(userId) && holders.Count == 1 ? Error.Conflict("At least one active user must keep the Users.Manage permission.") : null;
    }

    private async Task<MemberDto?> Load(Guid tenantId, Guid userId, CancellationToken ct)
    {
        var row = await (from m in db.UserTenants.AsNoTracking()
                         join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                         where m.TenantId == tenantId && m.UserId == userId
                         select new { m, u }).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var roles = await RolesFor(tenantId, [userId], ct);
        return new MemberDto(row.u.Id, row.u.Email ?? "", row.u.DisplayName, row.m.Status == MembershipStatus.Active && row.u.IsActive, roles.GetValueOrDefault(userId) ?? [], row.m.JoinedAtUtc, row.u.LastLoginAtUtc);
    }

    private async Task<Dictionary<Guid, List<RoleRef>>> RolesFor(Guid tenantId, IReadOnlyCollection<Guid> userIds, CancellationToken ct)
    {
        var rows = await (from ur in db.TenantUserRoles.AsNoTracking()
                          join r in db.TenantRoles.AsNoTracking() on ur.RoleId equals r.Id
                          where ur.TenantId == tenantId && r.TenantId == tenantId && userIds.Contains(ur.UserId)
                          orderby r.Name
                          select new { ur.UserId, r.Id, r.Name }).ToListAsync(ct);
        return rows.GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => g.Select(r => new RoleRef(r.Id, r.Name)).ToList());
    }
}
