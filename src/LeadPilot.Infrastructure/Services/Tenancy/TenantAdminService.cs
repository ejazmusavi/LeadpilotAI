using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Tenancy;
using LeadPilot.Shared.Results;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Services.Tenancy;

/// <summary>Platform-level tenant lifecycle. Authorization (Tenants.Manage) is enforced at the edge; every operation is audited against the target tenant.</summary>
public sealed class TenantAdminService(
    AppDbContext db, ITenantContextAccessor accessor, IAuditService audit, IClock clock, InvitationIssuer issuer,
    IValidator<CreateTenantRequest> validator, ICurrentUser user, ILogger<TenantAdminService> logger) : ITenantAdminService
{
    public async Task<PagedResult<TenantDto>> ListAsync(string? search, TenantStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var q = db.Tenants.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search)) { var s = search.Trim(); q = q.Where(t => t.Name.Contains(s) || t.Slug.Contains(s)); }
        if (status is { } st) q = q.Where(t => t.Status == st);
        var total = await q.LongCountAsync(ct);
        var tenants = await q.OrderBy(t => t.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var ids = tenants.Select(t => t.Id).ToList();
        var counts = await db.UserTenants.IgnoreQueryFilters().AsNoTracking().Where(m => ids.Contains(m.TenantId) && m.Status == MembershipStatus.Active)
            .GroupBy(m => m.TenantId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return new PagedResult<TenantDto>(tenants.Select(t => TenantQueries.ToDto(t, counts.GetValueOrDefault(t.Id))).ToList(), page, pageSize, total);
    }

    public async Task<Result<TenantDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return t is null ? Result.Failure<TenantDto>(Error.NotFound("Tenant")) : Result.Success(TenantQueries.ToDto(t, await MemberCount(id, ct)));
    }

    public Task<Result<CreateTenantResult>> CreateAsync(CreateTenantRequest request, CancellationToken ct = default) =>
        ResultFlow.GuardAsync(async () =>
        {
            if (await validator.CheckAsync(request, ct) is { } invalid) return Result.Failure<CreateTenantResult>(invalid.Error);
            var now = clock.UtcNow;
            var slug = string.IsNullOrWhiteSpace(request.Slug) ? Tenant.SlugFromName(request.Name) : request.Slug;
            var tenant = Tenant.Create(request.Name, slug, request.Industry, request.Timezone, request.Locale, now);
            if (await db.Tenants.AnyAsync(t => t.Slug == tenant.Slug, ct)) return Result.Failure<CreateTenantResult>(Error.Conflict($"The slug '{tenant.Slug}' is already in use."));

            using var _ = accessor.BeginPlatformScope(); // provisioning writes rows owned by the new tenant
            db.Tenants.Add(tenant);
            var permissions = await RoleProvisioner.SyncPermissionsAsync(db, ct);
            await RoleProvisioner.SyncSystemRolesAsync(db, tenant, permissions, now, ct);
            if (!tenant.IsPlatform) Services.Crm.CrmDefaults.AddAll(db, tenant.Id, now); // default pipeline, lead statuses, scoring rules

            (TenantInvitation, string)? invite = null;
            if (!string.IsNullOrWhiteSpace(request.AdminEmail))
            {
                // The Tenant Administrator role is created above but not yet saved: add the invitation after the first save.
                await db.SaveChangesAsync(ct);
                var adminRole = await db.TenantRoles.IgnoreQueryFilters().AsNoTracking().FirstAsync(r => r.TenantId == tenant.Id && r.Name == SystemRoles.TenantAdministrator, ct);
                invite = issuer.Create(tenant.Id, request.AdminEmail, adminRole.Id, user.UserId);
            }
            audit.Record(new AuditEntry("Tenant.Created", "Tenant", tenant.Id.ToString(), $"Tenant '{tenant.Name}' ({tenant.Slug}) created.", tenant.Id));
            await db.SaveChangesAsync(ct);

            var invited = false;
            if (invite is { } inv)
            {
                try { await issuer.SendAsync(tenant.Name, SystemRoles.TenantAdministrator, inv.Item1, inv.Item2, user.Email, ct); invited = true; }
                catch (Exception ex) { logger.LogError(ex, "Administrator invitation e-mail for tenant {TenantId} could not be sent", tenant.Id); }
            }
            return Result.Success(new CreateTenantResult(TenantQueries.ToDto(tenant, 0), invited));
        }, logger);

    public Task<Result<TenantDto>> UpdateAsync(Guid id, UpdateTenantProfileRequest request, CancellationToken ct = default) =>
        ResultFlow.GuardAsync(async () =>
        {
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (tenant is null) return Result.Failure<TenantDto>(Error.NotFound("Tenant"));
            using var _ = accessor.BeginPlatformScope();
            tenant.UpdateProfile(request.Name, request.Industry, request.Timezone, request.Locale, clock.UtcNow);
            audit.Record(new AuditEntry("Tenant.Updated", "Tenant", id.ToString(), $"Tenant '{tenant.Name}' profile updated.", id));
            await db.SaveChangesAsync(ct);
            return Result.Success(TenantQueries.ToDto(tenant, await MemberCount(id, ct)));
        }, logger);

    public Task<Result<TenantDto>> SuspendAsync(Guid id, string? reason, CancellationToken ct = default) =>
        ResultFlow.GuardAsync(async () =>
        {
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (tenant is null) return Result.Failure<TenantDto>(Error.NotFound("Tenant"));
            using var _ = accessor.BeginPlatformScope();
            tenant.Suspend(reason, clock.UtcNow);
            audit.Record(new AuditEntry("Tenant.Suspended", "Tenant", id.ToString(), $"Tenant '{tenant.Name}' suspended. Reason: {tenant.SuspensionReason ?? "n/a"}", id));
            // Active API sessions are cut off at the next request (membership resolver); refresh tokens are revoked so they cannot be renewed either.
            var now = clock.UtcNow;
            foreach (var t in await db.RefreshTokens.Where(r => r.TenantId == id && r.RevokedAtUtc == null).ToListAsync(ct)) t.Revoke(now);
            await db.SaveChangesAsync(ct);
            return Result.Success(TenantQueries.ToDto(tenant, await MemberCount(id, ct)));
        }, logger);

    public Task<Result<TenantDto>> ActivateAsync(Guid id, CancellationToken ct = default) =>
        ResultFlow.GuardAsync(async () =>
        {
            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (tenant is null) return Result.Failure<TenantDto>(Error.NotFound("Tenant"));
            using var _ = accessor.BeginPlatformScope();
            tenant.Activate(clock.UtcNow);
            audit.Record(new AuditEntry("Tenant.Activated", "Tenant", id.ToString(), $"Tenant '{tenant.Name}' activated.", id));
            await db.SaveChangesAsync(ct);
            return Result.Success(TenantQueries.ToDto(tenant, await MemberCount(id, ct)));
        }, logger);

    private Task<int> MemberCount(Guid tenantId, CancellationToken ct) =>
        db.UserTenants.IgnoreQueryFilters().AsNoTracking().CountAsync(m => m.TenantId == tenantId && m.Status == MembershipStatus.Active, ct);
}
