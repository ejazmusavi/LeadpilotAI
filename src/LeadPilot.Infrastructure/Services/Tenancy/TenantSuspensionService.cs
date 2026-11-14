using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Services.Tenancy;

/// <summary>THE suspension path (manual, abuse and non-payment). Blocks sign-in, API, sending and jobs through the Phase 2 tenant status; refresh tokens are revoked so sessions cannot be renewed.</summary>
public sealed class TenantSuspensionService(AppDbContext db, ITenantContextAccessor accessor, IAuditService audit, IClock clock, ILogger<TenantSuspensionService> logger) : ITenantSuspensionService
{
    public Task<Result<TenantDto>> SuspendAsync(Guid id, string? reason, SuspensionKind kind, CancellationToken ct = default) =>
        ResultFlow.GuardAsync(async () =>
        {
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (tenant is null) return Result.Failure<TenantDto>(Error.NotFound("Tenant"));
            using var _ = accessor.BeginPlatformScope();
            tenant.Suspend(reason, clock.UtcNow);
            var state = await db.TenantBillingStates.FirstOrDefaultAsync(x => x.TenantId == id, ct);
            if (state is null) { state = LeadPilot.Domain.Billing.TenantBillingState.Create(id, clock.UtcNow); db.TenantBillingStates.Add(state); }
            state.SetSuspensionKind(kind, clock.UtcNow);
            audit.Record(new AuditEntry("Tenant.Suspended", "Tenant", id.ToString(), $"Tenant '{tenant.Name}' suspended ({kind}). Reason: {tenant.SuspensionReason ?? "n/a"}", id));
            // Active API sessions are cut off at the next request (membership resolver); refresh tokens are revoked so they cannot be renewed either.
            var now = clock.UtcNow;
            foreach (var t in await db.RefreshTokens.Where(r => r.TenantId == id && r.RevokedAtUtc == null).ToListAsync(ct)) t.Revoke(now);
            await db.SaveChangesAsync(ct);
            return Result.Success(TenantQueries.ToDto(tenant, await db.UserTenants.IgnoreQueryFilters().AsNoTracking().CountAsync(m => m.TenantId == id && m.Status == MembershipStatus.Active, ct)));
        }, logger);
}
