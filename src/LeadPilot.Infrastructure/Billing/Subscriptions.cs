using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Billing;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Domain.Billing;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Services.Tenancy;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Billing;

public static class PlanMap
{
    public static PlanDto ToDto(Plan p, int tenantCount) =>
        new(p.Id, p.Code, p.Name, p.Description, p.Cycle, p.CustomPeriodDays, p.Price, p.Currency, p.IsTrial, p.TrialDays, p.Status, p.Visibility, p.SortOrder, p.IsSample,
            p.Limits.ToDictionary(l => l.Dimension, l => l.Value), p.Features.Where(f => f.Enabled).Select(f => f.Feature).OrderBy(f => f).ToList(), tenantCount);
}

/// <summary>Plan configuration for platform administrators. Plans are data: nothing in the code knows a plan name or a price. Editing a plan changes the entitlements of every tenant on it immediately (documented).</summary>
public sealed class PlanService(AppDbContext db, PlatformDirectory directory, IClock clock, ICurrentUser user, IAuditService audit, ITenantContext tenant, ILogger<PlanService> logger) : IPlanService
{
    public async Task<IReadOnlyList<PlanDto>> ListAsync(bool includeArchived, CancellationToken ct = default)
    {
        var q = db.Plans.AsNoTracking().Include(p => p.Limits).Include(p => p.Features).AsQueryable();
        if (!includeArchived) q = q.Where(p => p.Status == PlanStatus.Active);
        var plans = await q.OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync(ct);
        var counts = await directory.TenantCountsByPlanAsync(ct);
        return plans.Select(p => PlanMap.ToDto(p, counts.GetValueOrDefault(p.Id))).ToList();
    }

    public async Task<Result<PlanDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var p = await db.Plans.AsNoTracking().Include(x => x.Limits).Include(x => x.Features).FirstOrDefaultAsync(x => x.Id == id, ct);
        return p is null ? Result.Failure<PlanDto>(Error.NotFound("Plan")) : Result.Success(PlanMap.ToDto(p, (await directory.TenantCountsByPlanAsync(ct)).GetValueOrDefault(id)));
    }

    public Task<Result<PlanDto>> CreateAsync(SavePlanRequest r, CancellationToken ct = default) => ResultFlow.GuardAsync(async () =>
    {
        if (Validate(r) is { } bad) return Result.Failure<PlanDto>(bad);
        var code = Plan.NormalizeCode(r.Code);
        if (await db.Plans.AnyAsync(p => p.Code == code, ct)) return Result.Failure<PlanDto>(Error.Conflict($"A plan with the code '{code}' already exists."));
        var plan = Plan.Create(code, r.Name, r.Description, r.Cycle, r.CustomPeriodDays, r.Price, r.Currency, r.IsTrial, r.TrialDays, r.Visibility, r.SortOrder, false, clock.UtcNow, user.Email);
        Apply(plan, r);
        db.Plans.Add(plan);
        audit.Record(new AuditEntry("Platform.Plan.Created", "Plan", plan.Id.ToString(), $"Plan '{plan.Name}' ({plan.Code}) created: {plan.Price:0.00} {plan.Currency} per {plan.Cycle}.", tenant.TenantId));
        await db.SaveChangesAsync(ct);
        return Result.Success(PlanMap.ToDto(plan, 0));
    }, logger);

    public Task<Result<PlanDto>> UpdateAsync(Guid id, SavePlanRequest r, CancellationToken ct = default) => ResultFlow.GuardAsync(async () =>
    {
        if (Validate(r) is { } bad) return Result.Failure<PlanDto>(bad);
        var plan = await db.Plans.Include(p => p.Limits).Include(p => p.Features).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return Result.Failure<PlanDto>(Error.NotFound("Plan"));
        var now = clock.UtcNow;
        var before = $"{plan.Price:0.00} {plan.Currency} / {plan.Cycle}";
        plan.Configure(r.Name, r.Description, r.Cycle, r.CustomPeriodDays, r.Price, r.Currency, r.IsTrial, r.TrialDays, r.Visibility, r.SortOrder, now, user.Email);
        plan.ClearSampleMark();   // once the owner edits a sample plan it is theirs
        Apply(plan, r);
        audit.Record(new AuditEntry("Platform.Plan.Updated", "Plan", plan.Id.ToString(), $"Plan '{plan.Name}' updated (was {before}, now {plan.Price:0.00} {plan.Currency} / {plan.Cycle}); limits and features replaced.", tenant.TenantId));
        await db.SaveChangesAsync(ct);
        return Result.Success(PlanMap.ToDto(plan, (await directory.TenantCountsByPlanAsync(ct)).GetValueOrDefault(id)));
    }, logger);

    private static Error? Validate(SavePlanRequest r)
    {
        foreach (var (d, v) in r.Limits ?? [])
        {
            if (!Enum.IsDefined(d)) return Error.Validation("Unknown limit.");
            if (v is < 0) return Error.Validation("A limit cannot be negative (leave it empty for unlimited, 0 for not included).");
        }
        foreach (var f in r.Features ?? []) if (!Features.Exists(f)) return Error.Validation($"Unknown feature '{f}'.");
        return null;
    }

    private static void Apply(Plan plan, SavePlanRequest r)
    {
        var wanted = r.Limits ?? [];
        foreach (var existing in plan.Limits.Where(l => !wanted.ContainsKey(l.Dimension)).ToList()) plan.Limits.Remove(existing);
        foreach (var (d, v) in wanted) plan.SetLimit(d, v);
        var features = (r.Features ?? []).ToHashSet();
        foreach (var f in Features.All) plan.SetFeature(f.Key, features.Contains(f.Key));
    }

    public Task<Result<PlanDto>> ArchiveAsync(Guid id, CancellationToken ct = default) => ResultFlow.GuardAsync(async () =>
    {
        var plan = await db.Plans.Include(p => p.Limits).Include(p => p.Features).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return Result.Failure<PlanDto>(Error.NotFound("Plan"));
        var id1 = id.ToString();
        if (await db.PlatformSettings.AnyAsync(s => (s.Key == PlatformSettingKeys.DefaultPlanId || s.Key == PlatformSettingKeys.RegistrationPlanId) && s.Value == id1, ct))
            return Result.Failure<PlanDto>(Error.Conflict("This plan is the default or the sign-up plan in the platform settings. Choose another plan there first."));
        plan.Archive(clock.UtcNow, user.Email);
        audit.Record(new AuditEntry("Platform.Plan.Archived", "Plan", id1, $"Plan '{plan.Name}' archived: it can no longer be chosen; tenants already on it keep it.", tenant.TenantId));
        await db.SaveChangesAsync(ct);
        return Result.Success(PlanMap.ToDto(plan, (await directory.TenantCountsByPlanAsync(ct)).GetValueOrDefault(id)));
    }, logger);

    public Task<Result<PlanDto>> RestoreAsync(Guid id, CancellationToken ct = default) => ResultFlow.GuardAsync(async () =>
    {
        var plan = await db.Plans.Include(p => p.Limits).Include(p => p.Features).FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return Result.Failure<PlanDto>(Error.NotFound("Plan"));
        plan.Restore(clock.UtcNow, user.Email);
        audit.Record(new AuditEntry("Platform.Plan.Restored", "Plan", id.ToString(), $"Plan '{plan.Name}' restored.", tenant.TenantId));
        await db.SaveChangesAsync(ct);
        return Result.Success(PlanMap.ToDto(plan, (await directory.TenantCountsByPlanAsync(ct)).GetValueOrDefault(id)));
    }, logger);
}

/// <summary>The tenant's own billing view and plan-change flow. Every query names the tenant; ids of other workspaces are "not found".</summary>
public sealed class BillingService(AppDbContext db, ITenantContext tenant, IClock clock, ICurrentUser user, IEntitlementService entitlements, InvoiceIssuer issuer, IAuditService audit, ILogger<BillingService> logger) : IBillingService
{
    public const string ProrationPolicy = "Upgrades take effect when the new invoice is verified as paid: the new plan's period starts then, and unused time on the current paid period is credited (price × remaining ÷ total, same currency only). " +
        "Downgrades are scheduled for the end of the current period and only allowed when your current usage fits the new plan; nothing is refunded.";

    public async Task<BillingOverview> OverviewAsync(CancellationToken ct = default)
    {
        var t = tenant.RequireTenantId();
        var ent = await entitlements.OverviewAsync(ct);
        var open = await db.Invoices.AsNoTracking().Include(i => i.Lines).Where(i => i.TenantId == t && i.Status == InvoiceStatus.Issued).OrderBy(i => i.DueAtUtc).ToListAsync(ct);
        var dtos = await MapAsync(open, ct);
        var change = await db.PlanChangeRequests.AsNoTracking().Where(p => p.TenantId == t && p.IsOpen).OrderByDescending(p => p.CreatedAtUtc).FirstOrDefaultAsync(ct);
        return new BillingOverview(ent, dtos, change is null ? null : (await MapChangesAsync([change], ct))[0], await db.Invoices.CountAsync(i => i.TenantId == t && i.Status != InvoiceStatus.Draft, ct));
    }

    private async Task<List<InvoiceDto>> MapAsync(List<Invoice> invoices, CancellationToken ct)
    {
        var t = tenant.RequireTenantId();
        var planIds = invoices.Select(i => i.PlanId).Distinct().ToList(); var ids = invoices.Select(i => i.Id).ToList();
        var names = await db.Plans.AsNoTracking().Where(p => planIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var reported = (await db.PaymentReports.AsNoTracking().Where(r => r.TenantId == t && r.Status == PaymentReportStatus.Open && ids.Contains(r.InvoiceId)).Select(r => r.InvoiceId).ToListAsync(ct)).ToHashSet();
        return invoices.Select(i => InvoiceMap.ToDto(i, names.GetValueOrDefault(i.PlanId) ?? "", reported.Contains(i.Id))).ToList();
    }

    public async Task<PagedResult<InvoiceDto>> ListInvoicesAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var t = tenant.RequireTenantId(); page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        var q = db.Invoices.AsNoTracking().Include(i => i.Lines).Where(i => i.TenantId == t && i.Status != InvoiceStatus.Draft);
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(i => i.IssuedAtUtc).ThenByDescending(i => i.Number).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<InvoiceDto>(await MapAsync(rows, ct), page, pageSize, total);
    }

    public async Task<Result<InvoiceDto>> GetInvoiceAsync(Guid id, CancellationToken ct = default)
    {
        var t = tenant.RequireTenantId();
        var i = await db.Invoices.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id && x.TenantId == t && x.Status != InvoiceStatus.Draft, ct);
        return i is null ? Result.Failure<InvoiceDto>(Error.NotFound("Invoice")) : Result.Success((await MapAsync([i], ct))[0]);
    }

    public async Task<IReadOnlyList<BillingPaymentDto>> ListPaymentsAsync(CancellationToken ct = default)
    {
        var t = tenant.RequireTenantId();
        var rows = await (from p in db.BillingPayments.AsNoTracking() join i in db.Invoices.AsNoTracking() on p.InvoiceId equals i.Id
                          where p.TenantId == t && i.TenantId == t orderby p.PaidAtUtc descending
                          select new BillingPaymentDto(p.Id, p.InvoiceId, i.Number, p.Amount, p.Currency, p.Method, p.Source, p.EvidenceReference, p.PaidAtUtc, p.VerifiedAtUtc)).Take(200).ToListAsync(ct);
        return rows;
    }

    public Task<Result<InvoiceDto>> ReportPaymentAsync(Guid invoiceId, PaymentReportRequest r, CancellationToken ct = default) => ResultFlow.GuardAsync(async () =>
    {
        var t = tenant.RequireTenantId(); var now = clock.UtcNow;
        var inv = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == t && i.Status != InvoiceStatus.Draft, ct);
        if (inv is null) return Result.Failure<InvoiceDto>(Error.NotFound("Invoice"));
        if (inv.Status != InvoiceStatus.Issued) return Result.Failure<InvoiceDto>(Error.Conflict($"This invoice is {inv.Status}; there is nothing to report."));
        if (r.PaidOn is { } on && on.ToUniversalTime() > now.AddDays(1)) return Result.Failure<InvoiceDto>(Error.Validation("The payment date cannot be in the future."));
        db.PaymentReports.Add(PaymentReport.Create(t, invoiceId, r.EvidenceReference, r.PaidOn?.ToUniversalTime(), r.Note, user.UserId, now, user.Email));
        audit.Record(new AuditEntry("Billing.PaymentReported", "Invoice", invoiceId.ToString(), $"Payment reported for invoice {inv.Number} (transfer reference {r.EvidenceReference.Trim()}). It is NOT a payment until the platform verifies it."));
        await db.SaveChangesAsync(ct);
        return await GetInvoiceAsync(invoiceId, ct);
    }, logger);

    public async Task<IReadOnlyList<PlanDto>> AvailablePlansAsync(CancellationToken ct = default)
    {
        var plans = await db.Plans.AsNoTracking().Include(p => p.Limits).Include(p => p.Features).Where(p => p.Status == PlanStatus.Active && p.Visibility == PlanVisibility.Public && !p.IsTrial)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync(ct);
        return plans.Select(p => PlanMap.ToDto(p, 0)).ToList();
    }

    private sealed record Eval(TenantSubscription Sub, Plan From, Plan To, PlanChangeKind Kind, decimal Credit, decimal Total, IReadOnlyList<Exceedance> Exceeds);

    private async Task<Result<Eval>> EvaluateAsync(Guid planId, CancellationToken ct)
    {
        var t = tenant.RequireTenantId(); var now = clock.UtcNow;
        var sub = await db.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == t, ct);
        if (sub is null) return Result.Failure<Eval>(Error.Validation("This workspace has no subscription to change."));
        var from = await db.Plans.AsNoTracking().Include(p => p.Limits).FirstAsync(p => p.Id == sub.PlanId, ct);
        var to = await db.Plans.AsNoTracking().Include(p => p.Limits).FirstOrDefaultAsync(p => p.Id == planId && p.Status == PlanStatus.Active && p.Visibility == PlanVisibility.Public && !p.IsTrial, ct);
        if (to is null) return Result.Failure<Eval>(Error.NotFound("Plan"));
        if (to.Id == from.Id) return Result.Failure<Eval>(Error.Validation("You are already on this plan."));
        var sameCurrency = to.Currency == from.Currency;
        var upgrade = !sameCurrency ? to.Price > 0 : to.PricePer30Days(now) > from.PricePer30Days(now);
        var kind = upgrade ? PlanChangeKind.Upgrade : PlanChangeKind.Downgrade;
        decimal credit = 0;
        if (upgrade && sameCurrency && !from.IsTrial && from.Price > 0 && sub.LastPaymentId is not null) credit = Proration.Credit(from.Price, sub.PeriodStartUtc, sub.PeriodEndUtc, now);
        var total = Math.Max(0, to.Price - credit);
        IReadOnlyList<Exceedance> exceeds = [];
        if (!upgrade)
        {
            var usage = await entitlements.MeasureAllAsync(ct);
            var overrides = (await db.TenantEntitlementOverrides.AsNoTracking().Where(o => o.TenantId == t).ToListAsync(ct)).Select(o => new OverrideView(o.Key, o.Kind, o.LimitValue, o.Unlimited, o.FeatureEnabled, o.ExpiresAtUtc, o.Reason)).ToList();
            var targetLimits = DimensionCatalog.All.ToDictionary(i => i.Dimension, i => EntitlementEvaluator.Limit(i.Dimension, to.Limits.ToDictionary(l => l.Dimension, l => l.Value), overrides, now).Limit);
            exceeds = DowngradeCheck.Exceeding(usage, targetLimits);
        }
        return Result.Success(new Eval(sub, from, to, kind, credit, total, exceeds));
    }

    public async Task<Result<PlanChangePreview>> PreviewPlanChangeAsync(Guid planId, CancellationToken ct = default)
    {
        var e = await EvaluateAsync(planId, ct);
        if (e.IsFailure) return Result.Failure<PlanChangePreview>(e.Error);
        var v = e.Value; var blocked = v.Exceeds.Count > 0 ? "Your current usage is above what the new plan allows: reduce it first." : null;
        return Result.Success(new PlanChangePreview(v.From.Id, v.From.Name, v.To.Id, v.To.Name, v.Kind, v.To.Price, v.Credit, v.Total, v.To.Currency, blocked is null, blocked, v.Exceeds, ProrationPolicy));
    }

    public Task<Result<PlanChangeDto>> RequestPlanChangeAsync(Guid planId, CancellationToken ct = default) => ResultFlow.GuardAsync(async () =>
    {
        var t = tenant.RequireTenantId(); var now = clock.UtcNow;
        if (await db.PlanChangeRequests.AnyAsync(p => p.TenantId == t && p.IsOpen, ct)) return Result.Failure<PlanChangeDto>(Error.Conflict("There is already an open plan change. Cancel it first."));
        var ev = await EvaluateAsync(planId, ct);
        if (ev.IsFailure) return Result.Failure<PlanChangeDto>(ev.Error);
        var v = ev.Value;
        if (v.Exceeds.Count > 0)
            return Result.Failure<PlanChangeDto>(Error.Conflict("The downgrade is blocked because your usage exceeds the new plan: " + string.Join("; ", v.Exceeds.Select(x => $"{x.Label} {Fmt(x.Used, x.UiFactor, x.Unit)} > {Fmt(x.Limit, x.UiFactor, x.Unit)}")) + ". Reduce it, then try again."));
        var sub = v.Sub;
        if (v.Kind == PlanChangeKind.Downgrade)
        {
            var pc = PlanChangeRequest.Create(t, v.From.Id, v.To.Id, v.Kind, PlanChangeStatus.Scheduled, sub.PeriodEndUtc, 0, null, user.UserId, now, user.Email);
            db.PlanChangeRequests.Add(pc);
            sub.SchedulePlanChange(v.To.Id, pc.Id, now);
            db.SubscriptionEvents.Add(SubscriptionEvent.Create(t, "DowngradeScheduled", $"Downgrade to '{v.To.Name}' scheduled for {sub.PeriodEndUtc:yyyy-MM-dd}.", v.From.Id, v.To.Id, user.Email, now));
            audit.Record(new AuditEntry("Billing.DowngradeScheduled", "PlanChangeRequest", pc.Id.ToString(), $"Downgrade from '{v.From.Name}' to '{v.To.Name}' scheduled for {sub.PeriodEndUtc:yyyy-MM-dd}."));
            await db.SaveChangesAsync(ct);
            return Result.Success((await MapChangesAsync([pc], ct))[0]);
        }

        var change = PlanChangeRequest.Create(t, v.From.Id, v.To.Id, v.Kind, PlanChangeStatus.AwaitingPayment, null, v.Credit, v.From.Currency, user.UserId, now, user.Email);
        if (v.Total == 0)
        {
            // the credit from an already verified payment covers the whole price: nothing is owed, so there is no payment to verify
            change.Close(PlanChangeStatus.Applied, "Covered by the credit for unused time", now);
            db.PlanChangeRequests.Add(change);
            sub.SwitchPlanKeepingPeriod(v.To.Id, now);
            db.SubscriptionEvents.Add(SubscriptionEvent.Create(t, "PlanChanged", $"Upgraded to '{v.To.Name}' (covered by the credit of {v.Credit:0.00} {v.From.Currency}).", v.From.Id, v.To.Id, user.Email, now));
            audit.Record(new AuditEntry("Billing.PlanChanged", "PlanChangeRequest", change.Id.ToString(), $"Upgraded from '{v.From.Name}' to '{v.To.Name}' without payment: covered by the credit for unused time."));
            await db.SaveChangesAsync(ct);
            return Result.Success((await MapChangesAsync([change], ct))[0]);
        }

        db.PlanChangeRequests.Add(change);
        var end = v.To.PeriodEnd(now);
        var lines = new List<InvoiceLineSpec> { new($"{v.To.Name} — {v.To.Cycle.ToString().ToLowerInvariant()} plan, {now:yyyy-MM-dd} to {end:yyyy-MM-dd}", 1, v.To.Price) };
        if (v.Credit > 0) lines.Add(new($"Credit for unused time on '{v.From.Name}'", 1, -v.Credit));
        var invoice = await issuer.CreateAndIssueAsync(v.To, InvoiceKind.PlanChange, now, end, lines, $"change:{change.Id:N}", change.Id, ct);
        change.AttachInvoice(invoice.Id, now);
        audit.Record(new AuditEntry("Billing.UpgradeRequested", "PlanChangeRequest", change.Id.ToString(), $"Upgrade from '{v.From.Name}' to '{v.To.Name}' requested; invoice {invoice.Number} issued for {invoice.Total:0.00} {invoice.Currency}. The plan changes when the payment is verified."));
        await db.SaveChangesAsync(ct);
        return Result.Success((await MapChangesAsync([change], ct))[0]);
    }, logger);

    private static string Fmt(long v, long factor, string unit) => factor > 1 ? $"{v / (double)factor:0.##} {unit}" : $"{v:N0} {unit}";

    public async Task<Result> CancelPlanChangeAsync(Guid id, CancellationToken ct = default)
    {
        var r = await ResultFlow.GuardAsync<bool>(async () =>
        {
            var t = tenant.RequireTenantId(); var now = clock.UtcNow;
            var pc = await db.PlanChangeRequests.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == t, ct);
            if (pc is null) return Result.Failure<bool>(Error.NotFound("Plan change"));
            if (!pc.IsOpen) return Result.Failure<bool>(Error.Conflict("This plan change is already closed."));
            if (pc.Status == PlanChangeStatus.Scheduled)
            {
                var sub = await db.TenantSubscriptions.FirstAsync(s => s.TenantId == t, ct);
                if (sub.PendingChangeRequestId == pc.Id) sub.ClearPendingChange(now);
            }
            else if (pc.InvoiceId is { } invId)
            {
                var inv = await db.Invoices.FirstOrDefaultAsync(i => i.Id == invId && i.TenantId == t, ct);
                if (inv is { Status: InvoiceStatus.Issued }) inv.Void("Plan change cancelled by the tenant", now);
            }
            pc.Close(PlanChangeStatus.Cancelled, "Cancelled by the tenant", now);
            audit.Record(new AuditEntry("Billing.PlanChangeCancelled", "PlanChangeRequest", id.ToString(), "Plan change cancelled."));
            await db.SaveChangesAsync(ct);
            return Result.Success(true);
        }, logger);
        return r.IsSuccess ? Result.Success() : Result.Failure(r.Error);
    }

    public async Task<IReadOnlyList<PlanChangeDto>> ListPlanChangesAsync(CancellationToken ct = default)
    {
        var t = tenant.RequireTenantId();
        return await MapChangesAsync(await db.PlanChangeRequests.AsNoTracking().Where(p => p.TenantId == t).OrderByDescending(p => p.CreatedAtUtc).Take(50).ToListAsync(ct), ct);
    }

    private async Task<List<PlanChangeDto>> MapChangesAsync(List<PlanChangeRequest> rows, CancellationToken ct)
    {
        var ids = rows.SelectMany(r => new[] { r.FromPlanId, r.ToPlanId }).Distinct().ToList();
        var names = await db.Plans.AsNoTracking().Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return rows.Select(r => new PlanChangeDto(r.Id, r.FromPlanId, names.GetValueOrDefault(r.FromPlanId) ?? "", r.ToPlanId, names.GetValueOrDefault(r.ToPlanId) ?? "", r.Kind, r.Status, r.InvoiceId, r.EffectiveAtUtc, r.CreditAmount, r.Currency, r.CreatedAtUtc, r.CloseReason)).ToList();
    }
}

/// <summary>
/// The renewal / grace / suspension step for ONE tenant (the Hangfire job calls it inside that tenant's scope; every step is idempotent so repeated or overlapping runs change nothing). Money rules: a priced period
/// is only ever started by a verified payment (see <see cref="BillingPaymentVerifier"/>); this service issues the invoice, rolls a period that was paid in advance forward, lets a FREE plan renew itself, and
/// moves unpaid subscriptions through grace to suspension.
/// </summary>
public sealed class BillingLifecycleService(AppDbContext db, ITenantContext tenant, IClock clock, IPlatformSettings settings, InvoiceIssuer issuer, IEntitlementService entitlements, ITenantAdminService admin, IAuditService audit, ILogger<BillingLifecycleService> logger) : IBillingLifecycleService
{
    public async Task<LifecycleOutcome> ProcessAsync(CancellationToken ct = default)
    {
        var t = tenant.RequireTenantId(); var now = clock.UtcNow;
        var sub = await db.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == t, ct);
        if (sub is null || sub.State is SubscriptionState.Suspended or SubscriptionState.Cancelled) return LifecycleOutcome.NothingToDo;
        var plan = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == sub.PlanId, ct);
        var s = await settings.GetAllAsync(ct);

        if (sub.State == SubscriptionState.PastDue)
        {
            // a period that was paid after it went past due is rolled over below; otherwise wait out the grace period, then suspend
            if (await TryRollOverAsync(sub, ct)) return LifecycleOutcome.NothingToDo;
            if (sub.GraceEndsAtUtc is { } g && now >= g)
            {
                var res = await admin.SuspendAsync(t, $"Subscription unpaid since {sub.PeriodEndUtc:yyyy-MM-dd} (grace period of {s.GraceDays} days ended).", SuspensionKind.NonPayment, ct);
                if (res.IsFailure) { logger.LogWarning("Could not suspend tenant {TenantId} for non-payment: {Error}", t, res.Error.Message); return LifecycleOutcome.NothingToDo; }
                sub.MarkSuspended(now);
                db.SubscriptionEvents.Add(SubscriptionEvent.Create(t, "Suspended", "Suspended for non-payment after the grace period.", sub.PlanId, null, "system", now));
                await db.SaveChangesAsync(ct);
                return LifecycleOutcome.Suspended;
            }
            return LifecycleOutcome.NothingToDo;
        }

        var due = sub.PeriodEndUtc;
        if (now >= due && await TryRollOverAsync(sub, ct)) return LifecycleOutcome.NothingToDo;

        // Which plan renews: a scheduled downgrade wins, but only if the tenant's usage still fits it.
        var targetId = sub.PendingPlanId ?? plan.Id; var outcome = LifecycleOutcome.NothingToDo;
        if (sub.PendingPlanId is { } pending && now >= due - TimeSpan.FromDays(s.RenewalLeadDays))
        {
            var target = await db.Plans.AsNoTracking().Include(p => p.Limits).FirstAsync(p => p.Id == pending, ct);
            var usage = await entitlements.MeasureAllAsync(ct);
            var exceeds = DowngradeCheck.Exceeding(usage, target.Limits.ToDictionary(l => l.Dimension, l => l.Value));
            if (exceeds.Count > 0)
            {
                var pcId = sub.PendingChangeRequestId; sub.ClearPendingChange(now); targetId = plan.Id;
                if (pcId is { } id && await db.PlanChangeRequests.FirstOrDefaultAsync(p => p.Id == id && p.TenantId == t, ct) is { IsOpen: true } pc)
                    pc.Close(PlanChangeStatus.Cancelled, "Usage exceeds the target plan: " + string.Join(", ", exceeds.Select(e => e.Label)), now);
                db.SubscriptionEvents.Add(SubscriptionEvent.Create(t, "DowngradeCancelled", "The scheduled downgrade was cancelled because usage now exceeds the target plan: " + string.Join(", ", exceeds.Select(e => e.Label)) + ".", plan.Id, pending, "system", now));
                await db.SaveChangesAsync(ct);
                outcome = LifecycleOutcome.PendingDowngradeCancelled;
            }
        }
        var next = targetId == plan.Id ? plan : await db.Plans.AsNoTracking().FirstAsync(p => p.Id == targetId, ct);

        if (plan.IsTrial)
        {
            // a trial is never renewed: when it ends the workspace must choose a plan (grace period first)
            if (now >= due) return await MoveToGraceAsync(sub, s.GraceDays, "Trial ended", ct);
            return outcome;
        }

        if (next.Price == 0)
        {
            if (now < due) return outcome;
            var start = now - due > TimeSpan.FromDays(1) ? now : due;
            sub.RenewFree(next, start, next.PeriodEnd(start), now);
            db.SubscriptionEvents.Add(SubscriptionEvent.Create(t, "Renewed", $"Free plan '{next.Name}' renewed.", plan.Id, next.Id, "system", now));
            await db.SaveChangesAsync(ct);
            return LifecycleOutcome.FreeRenewed;
        }

        if (now >= due - TimeSpan.FromDays(s.RenewalLeadDays))
        {
            var key = $"renewal:{due:yyyyMMddHHmmss}";
            var existed = await db.Invoices.AnyAsync(i => i.TenantId == t && i.IdempotencyKey == key, ct);
            var end = next.PeriodEnd(due);
            var invoice = await issuer.CreateAndIssueAsync(next, InvoiceKind.Renewal, due, end, [new($"{next.Name} — {next.Cycle.ToString().ToLowerInvariant()} plan, {due:yyyy-MM-dd} to {end:yyyy-MM-dd}", 1, next.Price)], key, sub.PendingChangeRequestId, ct);
            if (!existed)
            {
                audit.Record(new AuditEntry("Billing.RenewalInvoiceIssued", "Invoice", invoice.Id.ToString(), $"Renewal invoice {invoice.Number} issued for {invoice.Total:0.00} {invoice.Currency} (period {due:yyyy-MM-dd} to {end:yyyy-MM-dd})."));
                await db.SaveChangesAsync(ct);
                outcome = LifecycleOutcome.RenewalInvoiceIssued;
            }
            if (now >= due && invoice.Status == InvoiceStatus.Issued) return await MoveToGraceAsync(sub, s.GraceDays, $"Invoice {invoice.Number} is unpaid", ct);
        }
        return outcome;
    }

    /// <summary>The next period was paid (verified) before the current one ended: move into it now that the current one is over.</summary>
    private async Task<bool> TryRollOverAsync(TenantSubscription sub, CancellationToken ct)
    {
        var t = sub.TenantId; var now = clock.UtcNow;
        if (now < sub.PeriodEndUtc && sub.State != SubscriptionState.PastDue) return false;
        var inv = await db.Invoices.AsNoTracking().Where(i => i.TenantId == t && i.Kind == InvoiceKind.Renewal && i.Status == InvoiceStatus.Paid && i.PeriodStartUtc >= sub.PeriodEndUtc && i.PeriodStartUtc <= now)
            .OrderBy(i => i.PeriodStartUtc).FirstOrDefaultAsync(ct);
        if (inv is null) return false;
        var payment = await db.BillingPayments.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == t && p.InvoiceId == inv.Id, ct);
        if (payment is null) return false;   // paid status without a verified payment row never rolls anything over
        var plan = await db.Plans.AsNoTracking().FirstAsync(p => p.Id == inv.PlanId, ct);
        var from = sub.PlanId;
        sub.RollOverToPaidPeriod(payment.Id, plan, inv.PeriodStartUtc, inv.PeriodEndUtc > now ? inv.PeriodEndUtc : plan.PeriodEnd(now), now);
        db.SubscriptionEvents.Add(SubscriptionEvent.Create(t, "Renewed", $"Moved into the period paid by invoice {inv.Number}.", from, plan.Id, "system", now));
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<LifecycleOutcome> MoveToGraceAsync(TenantSubscription sub, int graceDays, string why, CancellationToken ct)
    {
        var now = clock.UtcNow;
        sub.MarkPastDue(sub.PeriodEndUtc.AddDays(graceDays), now);
        db.SubscriptionEvents.Add(SubscriptionEvent.Create(sub.TenantId, "PastDue", $"{why}; access continues until {sub.GraceEndsAtUtc:yyyy-MM-dd} (grace period).", sub.PlanId, null, "system", now));
        audit.Record(new AuditEntry("Billing.PastDue", "TenantSubscription", sub.Id.ToString(), $"{why}. Grace period until {sub.GraceEndsAtUtc:yyyy-MM-dd}."));
        await db.SaveChangesAsync(ct);
        return LifecycleOutcome.MovedToGrace;
    }
}
