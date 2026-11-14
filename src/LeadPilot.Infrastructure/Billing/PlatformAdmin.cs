using Hangfire;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Billing;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Billing;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Services.Tenancy;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Billing;

/// <summary>
/// Every cross-tenant READ of the platform billing module lives here (the only allow-listed file of Phase 12 besides the seeder): per-plan tenant counts, the tenant list with plan and usage, the payment queue, the
/// invoice list, the platform audit view and the ids the Hangfire job needs before a tenant scope exists. Writes never happen here: they run inside the target tenant's scope.
/// </summary>
public sealed class PlatformDirectory(AppDbContext db)
{
    public async Task<Dictionary<Guid, int>> TenantCountsByPlanAsync(CancellationToken ct) =>
        await db.TenantSubscriptions.IgnoreQueryFilters().AsNoTracking().GroupBy(s => s.PlanId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);

    public async Task<List<Guid>> TenantsNeedingLifecycleAsync(DateTime now, int leadDays, CancellationToken ct)
    {
        var horizon = now.AddDays(leadDays);
        return await db.TenantSubscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => (s.State == SubscriptionState.PastDue || ((s.State == SubscriptionState.Trialing || s.State == SubscriptionState.Active) && s.PeriodEndUtc <= horizon)))
            .Select(s => s.TenantId).Take(1000).ToListAsync(ct);
    }

    public async Task<int> PurgeExpiredReservationsAsync(DateTime now, CancellationToken ct) =>
        await db.UsageReservations.IgnoreQueryFilters().Where(r => r.ExpiresAtUtc < now).ExecuteDeleteAsync(ct);

    public async Task<PagedResult<PlatformTenantRow>> TenantsAsync(string? search, TenantStatus? status, int page, int pageSize, DateTime now, CancellationToken ct)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var q = db.Tenants.AsNoTracking().Where(t => !t.IsPlatform);
        if (!string.IsNullOrWhiteSpace(search)) { var s = search.Trim(); q = q.Where(t => t.Name.Contains(s) || t.Slug.Contains(s)); }
        if (status is { } st) q = q.Where(t => t.Status == st);
        var total = await q.LongCountAsync(ct);
        var tenants = await q.OrderBy(t => t.Name).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<PlatformTenantRow>(await RowsAsync(tenants, now, ct), page, pageSize, total);
    }

    public async Task<PlatformTenantRow?> TenantAsync(Guid id, DateTime now, CancellationToken ct)
    {
        var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.IsPlatform, ct);
        return t is null ? null : (await RowsAsync([t], now, ct))[0];
    }

    private async Task<List<PlatformTenantRow>> RowsAsync(List<Tenant> tenants, DateTime now, CancellationToken ct)
    {
        var ids = tenants.Select(t => t.Id).ToList();
        var subs = await db.TenantSubscriptions.IgnoreQueryFilters().AsNoTracking().Where(s => ids.Contains(s.TenantId)).ToDictionaryAsync(s => s.TenantId, ct);
        var planIds = subs.Values.Select(s => s.PlanId).Distinct().ToList();
        var plans = await db.Plans.AsNoTracking().Include(p => p.Limits).Where(p => planIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var users = await db.UserTenants.IgnoreQueryFilters().AsNoTracking().Where(m => ids.Contains(m.TenantId) && m.Status == MembershipStatus.Active).GroupBy(m => m.TenantId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var leads = await db.Leads.IgnoreQueryFilters().AsNoTracking().Where(l => ids.Contains(l.TenantId) && !l.IsArchived).GroupBy(l => l.TenantId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var overrides = await db.TenantEntitlementOverrides.IgnoreQueryFilters().AsNoTracking().Where(o => ids.Contains(o.TenantId) && o.Kind == OverrideKind.Limit).ToListAsync(ct);
        return tenants.Select(t =>
        {
            subs.TryGetValue(t.Id, out var sub); Plan? plan = sub is null ? null : plans.GetValueOrDefault(sub.PlanId);
            var views = overrides.Where(o => o.TenantId == t.Id).Select(o => new OverrideView(o.Key, o.Kind, o.LimitValue, o.Unlimited, o.FeatureEnabled, o.ExpiresAtUtc, o.Reason)).ToList();
            long? Lim(UsageDimension d) => plan is null ? null : EntitlementEvaluator.Limit(d, plan.Limits.ToDictionary(l => l.Dimension, l => l.Value), views, now).Limit;
            return new PlatformTenantRow(t.Id, t.Name, t.Slug, t.Status, t.SuspensionKind, plan?.Name, sub?.State, sub?.PeriodEndUtc, users.GetValueOrDefault(t.Id), leads.GetValueOrDefault(t.Id),
                Lim(UsageDimension.Leads), Lim(UsageDimension.Users), t.IsSelfRegistered, !t.IsSelfRegistered || t.OwnerEmailVerifiedAtUtc is not null, t.CreatedAtUtc);
        }).ToList();
    }

    public async Task<PagedResult<QueueItem>> InvoicesAsync(InvoiceStatus? status, bool reportedFirst, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var q = db.Invoices.IgnoreQueryFilters().AsNoTracking().Where(i => i.Status != InvoiceStatus.Draft);
        if (status is { } st) q = q.Where(i => i.Status == st);
        var total = await q.LongCountAsync(ct);
        var ordered = reportedFirst
            ? q.OrderByDescending(i => db.PaymentReports.IgnoreQueryFilters().Any(r => r.InvoiceId == i.Id && r.Status == PaymentReportStatus.Open)).ThenBy(i => i.DueAtUtc)
            : q.OrderByDescending(i => i.IssuedAtUtc).ThenByDescending(i => i.Number);
        var rows = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var tids = rows.Select(r => r.TenantId).Distinct().ToList(); var iids = rows.Select(r => r.Id).ToList();
        var names = await db.Tenants.AsNoTracking().Where(t => tids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var reports = (await db.PaymentReports.IgnoreQueryFilters().AsNoTracking().Where(r => iids.Contains(r.InvoiceId) && r.Status == PaymentReportStatus.Open).OrderBy(r => r.CreatedAtUtc).ToListAsync(ct))
            .GroupBy(r => r.InvoiceId).ToDictionary(g => g.Key, g => g.Last());
        return new PagedResult<QueueItem>(rows.Select(i => new QueueItem(i.TenantId, names.GetValueOrDefault(i.TenantId) ?? "", i.Id, i.Number, i.Total, i.Currency, i.DueAtUtc, i.IssuedAtUtc,
            reports.GetValueOrDefault(i.Id)?.EvidenceReference, reports.GetValueOrDefault(i.Id)?.CreatedAtUtc, i.Kind)).ToList(), page, pageSize, total);
    }

    public async Task<PagedResult<PlatformAuditRow>> AuditAsync(string? action, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var q = db.AuditLogs.IgnoreQueryFilters().AsNoTracking().Where(a => a.Action.StartsWith("Platform.") || a.Action.StartsWith("Tenant."));
        if (!string.IsNullOrWhiteSpace(action)) { var f = action.Trim(); q = q.Where(a => a.Action.Contains(f)); }
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var tids = rows.Select(r => r.TenantId).Distinct().ToList();
        var names = await db.Tenants.AsNoTracking().Where(t => tids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        return new PagedResult<PlatformAuditRow>(rows.Select(a => new PlatformAuditRow(a.CreatedAtUtc, a.Action, a.EntityType, a.Summary, a.ActorEmail, a.TenantId, names.GetValueOrDefault(a.TenantId), a.IpAddress)).ToList(), page, pageSize, total);
    }
}

/// <summary>
/// Platform administration of subscriptions. EVERY method first checks the Platform.* permission of the caller (defence in depth next to the controller policies), then acts on the target tenant inside an
/// operator scope of THAT tenant (tenant predicates and the save guard apply as usual; suspended tenants are reachable because reactivation is the point). A tenant role can never reach this service: Platform.*
/// permissions can only be granted to roles of the platform tenant.
/// </summary>
public sealed class PlatformBillingService(PlatformDirectory directory, ITenantScopeRunner scopes, IPermissionEvaluator permissions, IClock clock, ICurrentUser user) : IPlatformBillingService
{
    private async Task<Error?> RequireAsync(string permission, CancellationToken ct) =>
        await permissions.HasAsync(permission, ct) ? null : Error.Forbidden();

    public async Task<PagedResult<PlatformTenantRow>> ListTenantsAsync(string? search, TenantStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        if (await RequireAsync(PermissionCodes.PlatformTenantsManage, ct) is not null) return new PagedResult<PlatformTenantRow>([], 1, pageSize, 0);
        return await directory.TenantsAsync(search, status, page, pageSize, clock.UtcNow, ct);
    }

    public async Task<Result<PlatformTenantDetail>> GetTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (await RequireAsync(PermissionCodes.PlatformTenantsManage, ct) is { } denied) return Result.Failure<PlatformTenantDetail>(denied);
        return await DetailAsync(tenantId, ct);
    }

    private async Task<Result<PlatformTenantDetail>> DetailAsync(Guid tenantId, CancellationToken ct)
    {
        var row = await directory.TenantAsync(tenantId, clock.UtcNow, ct);
        if (row is null) return Result.Failure<PlatformTenantDetail>(Error.NotFound("Tenant"));
        EntitlementOverview? ent = null; List<OverrideDto> overrides = []; List<InvoiceDto> invoices = []; List<SubscriptionEventDto> events = []; string? reason = null;
        await scopes.RunForOperatorAsync(tenantId, async (sp, c) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            ent = await sp.GetRequiredService<IEntitlementService>().OverviewAsync(c);
            overrides = (await db.TenantEntitlementOverrides.AsNoTracking().Where(o => o.TenantId == tenantId).OrderBy(o => o.Key).ToListAsync(c))
                .Select(o => new OverrideDto(o.Key, o.Kind, o.LimitValue, o.Unlimited, o.FeatureEnabled, o.ExpiresAtUtc, o.Reason, o.CreatedAtUtc)).ToList();
            invoices = (await sp.GetRequiredService<IBillingService>().ListInvoicesAsync(1, 20, c)).Items.ToList();
            events = (await db.SubscriptionEvents.AsNoTracking().Where(e => e.TenantId == tenantId).OrderByDescending(e => e.OccurredAtUtc).Take(30).ToListAsync(c))
                .Select(e => new SubscriptionEventDto(e.Type, e.Summary, e.OccurredAtUtc, e.Actor)).ToList();
            reason = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.SuspensionReason).FirstOrDefaultAsync(c);
        }, ct);
        return Result.Success(new PlatformTenantDetail(row, ent!, overrides, invoices, events, reason));
    }

    private async Task<Result<PlatformTenantDetail>> ActAsync(Guid tenantId, string permission, Func<IServiceProvider, CancellationToken, Task<Error?>> act, CancellationToken ct)
    {
        if (await RequireAsync(permission, ct) is { } denied) return Result.Failure<PlatformTenantDetail>(denied);
        if (await directory.TenantAsync(tenantId, clock.UtcNow, ct) is null) return Result.Failure<PlatformTenantDetail>(Error.NotFound("Tenant"));
        Error? error = null;
        try { await scopes.RunForOperatorAsync(tenantId, async (sp, c) => error = await act(sp, c), ct); }
        catch (LeadPilot.Domain.Common.DomainException ex) { return Result.Failure<PlatformTenantDetail>(Error.Validation(ex.Message)); }
        return error is not null ? Result.Failure<PlatformTenantDetail>(error) : await DetailAsync(tenantId, ct);
    }

    public Task<Result<PlatformTenantDetail>> GrantPlanAsync(Guid tenantId, GrantPlanRequest r, CancellationToken ct = default) => ActAsync(tenantId, PermissionCodes.PlatformTenantsManage, async (sp, c) =>
    {
        var db = sp.GetRequiredService<AppDbContext>(); var now = clock.UtcNow;
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("A reason is required for a grant.");
        var plan = await db.Plans.FirstOrDefaultAsync(p => p.Id == r.PlanId, c);
        if (plan is null) return Error.NotFound("Plan");
        var end = r.PeriodEndUtc?.ToUniversalTime() ?? plan.PeriodEnd(now);
        if (end <= now) return Error.Validation("A grant must end in the future.");
        var sub = await db.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId, c);
        var from = sub?.PlanId;
        if (sub is null) { sub = TenantSubscription.CreateGranted(tenantId, plan, now, end, r.Reason, now, user.Email); db.TenantSubscriptions.Add(sub); }
        else sub.Grant(plan, now, end, r.Reason, now, user.Email);
        db.SubscriptionEvents.Add(SubscriptionEvent.Create(tenantId, "Granted", $"Plan '{plan.Name}' granted by the platform until {end:yyyy-MM-dd}: {r.Reason.Trim()}", from, plan.Id, user.Email, now));
        sp.GetRequiredService<IAuditService>().Record(new AuditEntry("Platform.Plan.Granted", "TenantSubscription", sub.Id.ToString(), $"Plan '{plan.Name}' granted until {end:yyyy-MM-dd} without payment. Reason: {r.Reason.Trim()}", tenantId));
        await db.SaveChangesAsync(c);
        return null;
    }, ct);

    public Task<Result<PlatformTenantDetail>> SetLimitOverrideAsync(Guid tenantId, SetLimitOverrideRequest r, CancellationToken ct = default) => ActAsync(tenantId, PermissionCodes.PlatformTenantsManage, async (sp, c) =>
    {
        var db = sp.GetRequiredService<AppDbContext>(); var now = clock.UtcNow;
        if (!Enum.IsDefined(r.Dimension)) return Error.Validation("Unknown limit.");
        if (r.ExpiresAtUtc is { } e && e.ToUniversalTime() <= now) return Error.Validation("An override must expire in the future (or never).");
        var key = r.Dimension.ToString();
        var existing = await db.TenantEntitlementOverrides.FirstOrDefaultAsync(o => o.TenantId == tenantId && o.Key == key, c);
        var before = existing is null ? "plan limit" : existing.Unlimited ? "unlimited" : existing.LimitValue?.ToString() ?? "n/a";
        if (existing is null) db.TenantEntitlementOverrides.Add(TenantEntitlementOverride.ForLimit(tenantId, r.Dimension, r.Value, r.Unlimited, r.ExpiresAtUtc?.ToUniversalTime(), r.Reason, user.UserId, now, user.Email));
        else existing.SetLimit(r.Value, r.Unlimited, r.ExpiresAtUtc?.ToUniversalTime(), r.Reason, user.UserId, now, user.Email);
        sp.GetRequiredService<IAuditService>().Record(new AuditEntry("Platform.Override.Set", "TenantEntitlementOverride", key, $"Limit '{DimensionCatalog.Get(r.Dimension).Label}' overridden: {before} → {(r.Unlimited ? "unlimited" : r.Value?.ToString())}{(r.ExpiresAtUtc is { } x ? $" until {x:yyyy-MM-dd}" : "")}. Reason: {r.Reason.Trim()}", tenantId));
        await db.SaveChangesAsync(c);
        return null;
    }, ct);

    public Task<Result<PlatformTenantDetail>> SetFeatureOverrideAsync(Guid tenantId, SetFeatureOverrideRequest r, CancellationToken ct = default) => ActAsync(tenantId, PermissionCodes.PlatformTenantsManage, async (sp, c) =>
    {
        var db = sp.GetRequiredService<AppDbContext>(); var now = clock.UtcNow;
        if (!Features.Exists(r.Feature)) return Error.Validation("Unknown feature.");
        if (r.ExpiresAtUtc is { } e && e.ToUniversalTime() <= now) return Error.Validation("An override must expire in the future (or never).");
        var existing = await db.TenantEntitlementOverrides.FirstOrDefaultAsync(o => o.TenantId == tenantId && o.Key == r.Feature, c);
        if (existing is null) db.TenantEntitlementOverrides.Add(TenantEntitlementOverride.ForFeature(tenantId, r.Feature, r.Enabled, r.ExpiresAtUtc?.ToUniversalTime(), r.Reason, user.UserId, now, user.Email));
        else existing.SetFeature(r.Enabled, r.ExpiresAtUtc?.ToUniversalTime(), r.Reason, user.UserId, now, user.Email);
        sp.GetRequiredService<IAuditService>().Record(new AuditEntry("Platform.Override.Set", "TenantEntitlementOverride", r.Feature, $"Feature '{r.Feature}' forced {(r.Enabled ? "on" : "off")}. Reason: {r.Reason.Trim()}", tenantId));
        await db.SaveChangesAsync(c);
        return null;
    }, ct);

    public Task<Result<PlatformTenantDetail>> RemoveOverrideAsync(Guid tenantId, string key, CancellationToken ct = default) => ActAsync(tenantId, PermissionCodes.PlatformTenantsManage, async (sp, c) =>
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var existing = await db.TenantEntitlementOverrides.FirstOrDefaultAsync(o => o.TenantId == tenantId && o.Key == key, c);
        if (existing is null) return Error.NotFound("Override");
        db.TenantEntitlementOverrides.Remove(existing);
        sp.GetRequiredService<IAuditService>().Record(new AuditEntry("Platform.Override.Removed", "TenantEntitlementOverride", key, $"Override '{key}' removed: the plan applies again.", tenantId));
        await db.SaveChangesAsync(c);
        return null;
    }, ct);

    public Task<Result<PlatformTenantDetail>> SuspendAsync(Guid tenantId, SuspendTenantBillingRequest r, CancellationToken ct = default) => ActAsync(tenantId, PermissionCodes.PlatformTenantsManage, async (sp, c) =>
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("A reason is required to suspend a tenant.");
        var res = await sp.GetRequiredService<ITenantAdminService>().SuspendAsync(tenantId, r.Reason, r.Kind, c);
        return res.IsFailure ? res.Error : null;
    }, ct);

    public Task<Result<PlatformTenantDetail>> ReactivateAsync(Guid tenantId, CancellationToken ct = default) => ActAsync(tenantId, PermissionCodes.PlatformTenantsManage, async (sp, c) =>
    {
        var res = await sp.GetRequiredService<ITenantAdminService>().ActivateAsync(tenantId, c);
        if (res.IsFailure) return res.Error;
        var db = sp.GetRequiredService<AppDbContext>(); var now = clock.UtcNow;
        var sub = await db.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId, c);
        if (sub is { State: SubscriptionState.Suspended })
        {
            var grace = await sp.GetRequiredService<IPlatformSettings>().GetIntAsync(PlatformSettingKeys.GraceDays, 7, c);
            sub.Reinstate(now.AddDays(Math.Max(1, grace)), now);
            db.SubscriptionEvents.Add(SubscriptionEvent.Create(tenantId, "Reinstated", $"Reactivated by the platform; subscription is now {sub.State}.", sub.PlanId, null, user.Email, now));
            await db.SaveChangesAsync(c);
        }
        return null;
    }, ct);

    public async Task<PagedResult<QueueItem>> VerificationQueueAsync(int page, int pageSize, CancellationToken ct = default)
    {
        if (await RequireAsync(PermissionCodes.PlatformBillingVerify, ct) is not null) return new PagedResult<QueueItem>([], 1, pageSize, 0);
        return await directory.InvoicesAsync(InvoiceStatus.Issued, true, page, pageSize, ct);
    }

    public async Task<PagedResult<QueueItem>> InvoicesAsync(InvoiceStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        if (await RequireAsync(PermissionCodes.PlatformBillingVerify, ct) is not null) return new PagedResult<QueueItem>([], 1, pageSize, 0);
        return await directory.InvoicesAsync(status, false, page, pageSize, ct);
    }

    public async Task<Result<InvoiceDto>> VerifyPaymentAsync(Guid tenantId, Guid invoiceId, VerifyBillingPaymentRequest r, CancellationToken ct = default)
    {
        if (await RequireAsync(PermissionCodes.PlatformBillingVerify, ct) is { } denied) return Result.Failure<InvoiceDto>(denied);
        if (user.UserId is not { } verifier) return Result.Failure<InvoiceDto>(Error.Forbidden("A person must verify a payment."));
        Result<InvoiceDto>? result = null;
        try
        {
            await scopes.RunForOperatorAsync(tenantId, async (sp, c) =>
            {
                var applied = await sp.GetRequiredService<BillingPaymentVerifier>().ApplyAsync(invoiceId, r.Amount, r.Currency, r.Method, VerificationSource.Manual, r.EvidenceReference, verifier, (r.PaidAtUtc ?? clock.UtcNow).ToUniversalTime(), r.Notes, c);
                result = applied.IsFailure ? Result.Failure<InvoiceDto>(applied.Error) : await sp.GetRequiredService<IBillingService>().GetInvoiceAsync(invoiceId, c);
            }, ct);
        }
        catch (TenantUnavailableException) { return Result.Failure<InvoiceDto>(Error.NotFound("Invoice")); }
        return result ?? Result.Failure<InvoiceDto>(Error.NotFound("Invoice"));
    }

    public async Task<Result<InvoiceDto>> VoidInvoiceAsync(Guid tenantId, Guid invoiceId, string reason, CancellationToken ct = default)
    {
        if (await RequireAsync(PermissionCodes.PlatformBillingVerify, ct) is { } denied) return Result.Failure<InvoiceDto>(denied);
        if (string.IsNullOrWhiteSpace(reason)) return Result.Failure<InvoiceDto>(Error.Validation("A reason is required to void an invoice."));
        Result<InvoiceDto>? result = null;
        try
        {
            await scopes.RunForOperatorAsync(tenantId, async (sp, c) =>
            {
                var db = sp.GetRequiredService<AppDbContext>(); var now = clock.UtcNow;
                var inv = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId, c);
                if (inv is null) { result = Result.Failure<InvoiceDto>(Error.NotFound("Invoice")); return; }
                if (inv.Status != InvoiceStatus.Issued) { result = Result.Failure<InvoiceDto>(Error.Conflict($"A {inv.Status} invoice cannot be voided.")); return; }
                inv.Void(reason, now);
                if (inv.PlanChangeRequestId is { } pc && await db.PlanChangeRequests.FirstOrDefaultAsync(p => p.Id == pc && p.TenantId == tenantId, c) is { IsOpen: true } req) req.Close(PlanChangeStatus.Cancelled, "Invoice voided", now);
                sp.GetRequiredService<IAuditService>().Record(new AuditEntry("Platform.Invoice.Voided", "Invoice", invoiceId.ToString(), $"Invoice {inv.Number} voided. Reason: {reason.Trim()}", tenantId));
                await db.SaveChangesAsync(c);
                result = await sp.GetRequiredService<IBillingService>().GetInvoiceAsync(invoiceId, c);
            }, ct);
        }
        catch (TenantUnavailableException) { return Result.Failure<InvoiceDto>(Error.NotFound("Invoice")); }
        catch (LeadPilot.Domain.Common.DomainException ex) { return Result.Failure<InvoiceDto>(Error.Validation(ex.Message)); }
        return result ?? Result.Failure<InvoiceDto>(Error.NotFound("Invoice"));
    }

    public async Task<Result<InvoiceDto>> GetInvoiceAsync(Guid tenantId, Guid invoiceId, CancellationToken ct = default)
    {
        if (await RequireAsync(PermissionCodes.PlatformBillingVerify, ct) is { } denied) return Result.Failure<InvoiceDto>(denied);
        Result<InvoiceDto>? result = null;
        try { await scopes.RunForOperatorAsync(tenantId, async (sp, c) => result = await sp.GetRequiredService<IBillingService>().GetInvoiceAsync(invoiceId, c), ct); }
        catch (TenantUnavailableException) { return Result.Failure<InvoiceDto>(Error.NotFound("Invoice")); }
        return result ?? Result.Failure<InvoiceDto>(Error.NotFound("Invoice"));
    }

    public async Task<PagedResult<PlatformAuditRow>> AuditAsync(string? action, int page, int pageSize, CancellationToken ct = default)
    {
        if (await RequireAsync(PermissionCodes.PlatformAuditView, ct) is not null) return new PagedResult<PlatformAuditRow>([], 1, pageSize, 0);
        return await directory.AuditAsync(action, page, pageSize, ct);
    }
}

public static class BillingQueues { public const string Billing = "billing"; }

/// <summary>
/// Hangfire entry points of the billing module (the one scheduler). Hourly: find the tenants whose subscription needs a step (renewal due within the lead time, past due), then run each tenant's lifecycle inside its own
/// validated tenant scope. The lifecycle service is idempotent, so repeated or overlapping runs are harmless. Also purges expired usage holds.
/// </summary>
public sealed class BillingJob(ITenantScopeRunner scopes, IClock clock, ILogger<BillingJob> logger)
{
    public const string LifecycleId = "billing-lifecycle";

    [Queue(BillingQueues.Billing)]
    [DisableConcurrentExecution(900)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(CancellationToken ct)
    {
        List<Guid> tenants = [];
        await scopes.RunPlatformJobAsync(async (sp, c) =>
        {
            var lead = await sp.GetRequiredService<IPlatformSettings>().GetIntAsync(PlatformSettingKeys.RenewalLeadDays, 7, c);
            var dir = sp.GetRequiredService<PlatformDirectory>();
            await dir.PurgeExpiredReservationsAsync(clock.UtcNow, c);
            tenants = await dir.TenantsNeedingLifecycleAsync(clock.UtcNow, lead, c);
        }, ct);
        foreach (var id in tenants)
        {
            try { await scopes.RunAsync(id, (sp, c) => sp.GetRequiredService<IBillingLifecycleService>().ProcessAsync(c), ct); }
            catch (TenantUnavailableException) { /* suspended tenants are skipped: only a payment or an operator brings them back */ }
            catch (Exception ex) { logger.LogError(ex, "Billing lifecycle failed for tenant {TenantId}", id); }
        }
    }

    public static void ScheduleRecurring(IRecurringJobManager recurring) =>
        recurring.AddOrUpdate<BillingJob>(LifecycleId, x => x.RunAsync(CancellationToken.None), "7 * * * *");
}
