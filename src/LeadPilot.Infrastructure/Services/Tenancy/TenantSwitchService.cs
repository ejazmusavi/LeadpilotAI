using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Services.Tenancy;

public sealed class TenantSwitchService(AppDbContext db, IMembershipResolver membership, IAuditService audit, ICurrentUser currentUser) : ITenantSwitchService
{
    public async Task<IReadOnlyList<AccessibleTenantDto>> ListAccessibleAsync(Guid userId, Guid? currentTenantId, CancellationToken ct = default)
    {
        var memberIds = await db.UserTenants.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active).Select(m => m.TenantId).ToListAsync(ct);
        var platform = await membership.HasPlatformSwitchAsync(userId, ct);
        var q = db.Tenants.AsNoTracking().AsQueryable();
        q = platform ? q : q.Where(t => memberIds.Contains(t.Id));
        var tenants = await q.OrderBy(t => t.Name).ToListAsync(ct);
        return tenants.Select(t => new AccessibleTenantDto(t.Id, t.Name, t.Slug, t.Status.ToString(), !memberIds.Contains(t.Id), t.Id == currentTenantId)).ToList();
    }

    public async Task<Result<SwitchTenantResult>> SwitchAsync(Guid userId, Guid targetTenantId, Guid? currentTenantId, CancellationToken ct = default)
    {
        var access = await membership.ResolveAsync(userId, targetTenantId, ct);
        switch (access.Outcome)
        {
            case TenantAccessOutcome.TenantNotFound:
            case TenantAccessOutcome.NotAMember: // same answer for "does not exist" and "not yours": no tenant enumeration
                return Result.Failure<SwitchTenantResult>(Error.NotFound("Tenant"));
            case TenantAccessOutcome.UserInactive:
                return Result.Failure<SwitchTenantResult>(Error.Forbidden("This account is disabled."));
            case TenantAccessOutcome.TenantSuspended:
                return Result.Failure<SwitchTenantResult>(Error.Forbidden("This workspace is suspended."));
        }

        var how = access.ViaPlatformAccess ? "using platform access" : "as a member";
        await audit.RecordAndSaveAsync(new AuditEntry("Tenant.Switched", "Tenant", targetTenantId.ToString(),
            $"{currentUser.Email ?? userId.ToString()} switched into '{access.TenantName}' {how}" + (currentTenantId is { } from && from != targetTenantId ? $" (from tenant {from})." : "."),
            targetTenantId, userId, currentUser.Email), ct);
        return Result.Success(new SwitchTenantResult(targetTenantId, access.TenantName!, access.ViaPlatformAccess));
    }
}
