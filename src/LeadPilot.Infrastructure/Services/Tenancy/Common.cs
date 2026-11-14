using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Services.Tenancy;

internal static class TenantQueries
{
    public static TenantDto ToDto(Tenant t, int members) =>
        new(t.Id, t.Name, t.Slug, t.Status.ToString(), t.IsPlatform, t.Industry, t.Timezone, t.Locale, t.CreatedAtUtc, t.SuspendedAtUtc, t.SuspensionReason, members);

    /// <summary>Permission codes per role, restricted to the given tenant (explicit predicate on top of the global filter).</summary>
    public static async Task<Dictionary<Guid, List<string>>> PermissionCodesByRoleAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> roleIds, CancellationToken ct)
    {
        var rows = await (from rp in db.RolePermissions.AsNoTracking()
                          join p in db.Permissions.AsNoTracking() on rp.PermissionId equals p.Id
                          where rp.TenantId == tenantId && roleIds.Contains(rp.RoleId)
                          select new { rp.RoleId, p.Code }).ToListAsync(ct);
        return rows.GroupBy(r => r.RoleId).ToDictionary(g => g.Key, g => g.Select(r => r.Code).ToList());
    }

    /// <summary>Active members who currently hold Users.Manage in the tenant (used to protect the last administrator).</summary>
    public static async Task<HashSet<Guid>> UsersManageHoldersAsync(AppDbContext db, Guid tenantId, CancellationToken ct)
    {
        var ids = await (from m in db.UserTenants.AsNoTracking()
                         join ur in db.TenantUserRoles.AsNoTracking() on m.UserId equals ur.UserId
                         join rp in db.RolePermissions.AsNoTracking() on ur.RoleId equals rp.RoleId
                         join p in db.Permissions.AsNoTracking() on rp.PermissionId equals p.Id
                         where m.TenantId == tenantId && ur.TenantId == tenantId && rp.TenantId == tenantId
                               && m.Status == MembershipStatus.Active && p.Code == PermissionCodes.UsersManage
                         select m.UserId).Distinct().ToListAsync(ct);
        return ids.ToHashSet();
    }

    public static Result Fail(DomainException ex) => Result.Failure(Error.Validation(ex.Message));
}

internal static class ResultFlow
{
    /// <summary>Runs a unit of work, translating domain-rule violations into validation results.</summary>
    public static async Task<Result<T>> GuardAsync<T>(Func<Task<Result<T>>> work, ILogger logger)
    {
        try { return await work(); }
        catch (DomainException ex) { return Result.Failure<T>(Error.Validation(ex.Message)); }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            logger.LogInformation(ex, "Duplicate key rejected");
            return Result.Failure<T>(Error.Conflict("That record already exists."));
        }
    }

    public static bool IsDuplicate(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        while (inner is not null)
        {
            if (inner.GetType().Name.Contains("MySql") && inner.Message.Contains("Duplicate entry", StringComparison.OrdinalIgnoreCase)) return true;
            inner = inner.InnerException;
        }
        return false;
    }
}
