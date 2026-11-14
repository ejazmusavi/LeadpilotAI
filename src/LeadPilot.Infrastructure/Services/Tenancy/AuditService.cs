using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Services.Tenancy;

public sealed class AuditService(AppDbContext db, ITenantContext tenant, ITenantContextAccessor accessor, ICurrentUser user, IRequestInfo request, IClock clock) : IAuditService
{
    public void Record(AuditEntry e)
    {
        var target = e.TargetTenantId ?? tenant.RequireTenantId();
        db.AuditLogs.Add(AuditLog.Create(target, tenant.TenantId, e.ActorUserId ?? user.UserId, e.ActorEmail ?? user.Email, e.Action, e.EntityType, e.EntityId,
            e.Summary, request.IpAddress, request.CorrelationId, clock.UtcNow));
    }

    public async Task RecordAndSaveAsync(AuditEntry e, CancellationToken ct = default)
    {
        Record(e);
        // An audit entry for another tenant (platform action, sign-in before a tenant is chosen) is the one sanctioned cross-tenant write.
        using var _ = e.TargetTenantId is not null && e.TargetTenantId != tenant.TenantId ? accessor.BeginPlatformScope() : null;
        await db.SaveChangesAsync(ct);
    }

    public async Task<PagedResult<AuditLogDto>> QueryAsync(AuditQuery q, CancellationToken ct = default)
    {
        var tenantId = tenant.RequireTenantId();
        var page = Math.Max(1, q.Page);
        var size = Math.Clamp(q.PageSize, 1, 100);
        var query = db.AuditLogs.AsNoTracking().Where(a => a.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(q.Action)) query = query.Where(a => a.Action.Contains(q.Action.Trim()));
        if (q.FromUtc is { } from) query = query.Where(a => a.CreatedAtUtc >= from);
        if (q.ToUtc is { } to) query = query.Where(a => a.CreatedAtUtc < to);
        var total = await query.LongCountAsync(ct);
        var items = await query.OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id).Skip((page - 1) * size).Take(size)
            .Select(a => new AuditLogDto(a.Id, a.CreatedAtUtc, a.Action, a.EntityType, a.EntityId, a.Summary, a.ActorEmail, a.ActorTenantId, a.IpAddress, a.CorrelationId))
            .ToListAsync(ct);
        return new PagedResult<AuditLogDto>(items, page, size, total);
    }
}
