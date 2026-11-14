using System.Data;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Billing;
using LeadPilot.Domain.Billing;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Messaging;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Billing;

/// <summary>
/// THE metering service. It does not keep a second set of counters (they would drift from the truth): it reads the existing source-of-truth rows and ledgers of the ACTIVE tenant (messages, the AI usage ledger,
/// the discovery usage ledger, workflow executions, leads, campaigns, memberships, channel accounts, knowledge documents). Stock dimensions count what exists now; period dimensions count the rows created inside the
/// subscription period window (UTC instants; tenant time zones are display-only). Idempotent by construction: re-measuring never changes anything.
/// </summary>
public sealed class UsageMeter(AppDbContext db, ITenantContext tenant, IClock clock)
{
    public async Task<long> MeasureAsync(UsageDimension d, DateTime from, DateTime to, CancellationToken ct)
    {
        var t = tenant.RequireTenantId(); var now = clock.UtcNow;
        switch (d)
        {
            case UsageDimension.Users:
                return await db.UserTenants.AsNoTracking().CountAsync(m => m.TenantId == t && m.Status == MembershipStatus.Active, ct)
                     + await db.TenantInvitations.AsNoTracking().CountAsync(i => i.TenantId == t && i.Status == InvitationStatus.Pending && i.ExpiresAtUtc > now, ct);
            case UsageDimension.MessagingAccounts:
                return await db.ChannelAccounts.AsNoTracking().CountAsync(a => a.TenantId == t && !a.IsDeleted, ct);
            case UsageDimension.MonthlyMessages:
                return await db.Messages.AsNoTracking().CountAsync(m => m.TenantId == t && m.Direction == MessageDirection.Outbound && m.Status != MessageStatus.Failed && m.CreatedAtUtc >= from && m.CreatedAtUtc < to, ct);
            case UsageDimension.MonthlyAiTokens:
                return await db.AiUsageEntries.AsNoTracking().Where(e => e.TenantId == t && e.OccurredAtUtc >= from && e.OccurredAtUtc < to).SumAsync(e => (long?)e.InputTokens + e.OutputTokens, ct) ?? 0;
            case UsageDimension.Leads:
                return await db.Leads.AsNoTracking().CountAsync(l => l.TenantId == t && !l.IsArchived, ct);
            case UsageDimension.Campaigns:
                return await db.Campaigns.AsNoTracking().CountAsync(c => c.TenantId == t && c.Status != CampaignStatus.Completed && c.Status != CampaignStatus.Cancelled && c.Status != CampaignStatus.Failed, ct);
            case UsageDimension.KnowledgeStorageBytes:
                return await db.DocumentVersions.AsNoTracking().Where(v => v.TenantId == t).SumAsync(v => (long?)v.SizeBytes, ct) ?? 0;
            case UsageDimension.MonthlyAutomationRuns:
                return await db.WorkflowExecutions.AsNoTracking().CountAsync(e => e.TenantId == t && e.CreatedAtUtc >= from && e.CreatedAtUtc < to, ct);
            case UsageDimension.MonthlyDiscoveryCalls:
                return await db.DiscoveryUsageEntries.AsNoTracking().Where(e => e.TenantId == t && e.OccurredAtUtc >= from && e.OccurredAtUtc < to).SumAsync(e => (long?)e.Calls, ct) ?? 0;
            default: throw new ArgumentOutOfRangeException(nameof(d));
        }
    }

    /// <summary>Quantity currently held by in-flight calls (AI, discovery) that are not in their ledger yet.</summary>
    public async Task<long> HeldAsync(UsageDimension d, CancellationToken ct)
    {
        var t = tenant.RequireTenantId(); var now = clock.UtcNow;
        return await db.UsageReservations.AsNoTracking().Where(r => r.TenantId == t && r.Dimension == d && r.ExpiresAtUtc > now).SumAsync(r => (long?)r.Quantity, ct) ?? 0;
    }
}

/// <summary>Puts a tenant on a subscription. Used when a tenant is created, by the start-up backfill of existing tenants and, defensively, by the entitlement service for a tenant that has none yet.</summary>
public static class SubscriptionProvisioner
{
    /// <summary>The plan new / existing tenants start on (platform setting <c>billing.default_plan_id</c>). Null means no plan is configured: nothing is enforced.</summary>
    public static async Task<Plan?> DefaultPlanAsync(AppDbContext db, CancellationToken ct)
    {
        var raw = await db.PlatformSettings.AsNoTracking().Where(s => s.Key == PlatformSettingKeys.DefaultPlanId).Select(s => s.Value).FirstOrDefaultAsync(ct);
        return Guid.TryParse(raw, out var id) ? await db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) : null;
    }

    /// <summary>Adds the subscription to <paramref name="db"/> (the caller saves). Free and trial plans start by themselves; a priced plan is recorded as a platform grant for one period (the first renewal invoice then bills it).</summary>
    public static TenantSubscription Create(Guid tenantId, Plan plan, DateTime now, string reason, string? actor)
    {
        if (plan.Price == 0) return TenantSubscription.StartFreeOrTrial(tenantId, plan, now, actor);
        return TenantSubscription.CreateGranted(tenantId, plan, now, plan.PeriodEnd(now), reason, now, actor);
    }

    public static async Task<bool> EnsureAsync(AppDbContext db, Guid tenantId, DateTime now, string reason, string? actor, CancellationToken ct)
    {
        // runs in the tenant's own scope (the query filter names that tenant), or for a brand-new tenant that cannot have one yet
        if (await db.TenantSubscriptions.AnyAsync(s => s.TenantId == tenantId, ct)) return false;
        var plan = await DefaultPlanAsync(db, ct);
        if (plan is null) return false;
        db.TenantSubscriptions.Add(Create(tenantId, plan, now, reason, actor));
        db.SubscriptionEvents.Add(SubscriptionEvent.Create(tenantId, "Started", $"Subscription started on plan '{plan.Name}' ({reason}).", null, plan.Id, actor, now));
        return true;
    }
}

public sealed class EntitlementService(AppDbContext db, ITenantContext tenant, IClock clock, UsageMeter meter, IServiceScopeFactory scopes, ILogger<EntitlementService> logger) : IEntitlementService
{
    private static readonly HashSet<UsageDimension> SensitiveDimensions = [UsageDimension.MonthlyMessages, UsageDimension.MonthlyAiTokens, UsageDimension.MonthlyDiscoveryCalls, UsageDimension.MessagingAccounts];
    private static readonly HashSet<string> SensitiveFeatures = [Features.Ai, Features.Discovery, Features.Campaigns, Features.Workflows];
    private static readonly int[] Thresholds = [80, 100];

    private sealed record Ctx(Guid TenantId, bool Exempt, bool Locked, TenantSubscription? Sub, Plan? Plan, List<OverrideView> Overrides, string? PendingPlanName);

    private async Task<Ctx> LoadAsync(CancellationToken ct)
    {
        var t = tenant.RequireTenantId();
        var tn = await db.Tenants.AsNoTracking().Where(x => x.Id == t).Select(x => new { x.IsPlatform, x.IsSelfRegistered, x.OwnerEmailVerifiedAtUtc }).FirstOrDefaultAsync(ct)
                 ?? throw new TenantUnavailableException("The tenant does not exist.");
        if (tn.IsPlatform) return new Ctx(t, true, false, null, null, [], null);
        var locked = tn.IsSelfRegistered && tn.OwnerEmailVerifiedAtUtc is null;
        var sub = await db.TenantSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == t, ct);
        if (sub is null)
        {
            await EnsureSubscriptionInOwnScopeAsync(t, ct);
            sub = await db.TenantSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == t, ct);
        }
        if (sub is null) return new Ctx(t, false, locked, null, null, [], null);   // no plan configured anywhere: nothing to enforce
        var plan = await db.Plans.AsNoTracking().Include(p => p.Limits).Include(p => p.Features).FirstAsync(p => p.Id == sub.PlanId, ct);
        var overrides = (await db.TenantEntitlementOverrides.AsNoTracking().Where(o => o.TenantId == t).ToListAsync(ct))
            .Select(o => new OverrideView(o.Key, o.Kind, o.LimitValue, o.Unlimited, o.FeatureEnabled, o.ExpiresAtUtc, o.Reason)).ToList();
        string? pending = sub.PendingPlanId is { } pid ? await db.Plans.AsNoTracking().Where(p => p.Id == pid).Select(p => p.Name).FirstOrDefaultAsync(ct) : null;
        return new Ctx(t, false, locked, sub, plan, overrides, pending);
    }

    private async Task EnsureSubscriptionInOwnScopeAsync(Guid tenantId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(tenantId);
            var own = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (await SubscriptionProvisioner.EnsureAsync(own, tenantId, clock.UtcNow, "Default plan assigned automatically", null, ct)) await own.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("Duplicate", StringComparison.OrdinalIgnoreCase) == true) { /* a concurrent request created it */ }
    }

    private static IReadOnlyDictionary<UsageDimension, long?> PlanLimits(Plan? plan) =>
        plan is null ? new Dictionary<UsageDimension, long?>() : plan.Limits.ToDictionary(l => l.Dimension, l => l.Value);

    private EffectiveLimit Effective(Ctx c, UsageDimension d) =>
        c.Plan is null ? new EffectiveLimit(d, null, LimitSource.NoSubscription) : EntitlementEvaluator.Limit(d, PlanLimits(c.Plan), c.Overrides, clock.UtcNow);

    private static Error? Gate(Ctx c, bool sensitive)
    {
        if (c.Exempt) return null;
        if (c.Sub is not null && !c.Sub.GrantsAccess) return EntitlementErrors.Inactive();
        if (sensitive && c.Locked) return EntitlementErrors.Unverified();
        return null;
    }

    private (DateTime From, DateTime To) Window(Ctx c) =>
        c.Sub is null ? (DateTime.UnixEpoch, clock.UtcNow.AddTicks(1)) : UsageWindow.For(c.Sub.PeriodStartUtc, c.Sub.PeriodEndUtc, clock.UtcNow);

    private async Task LockAsync(Guid tenantId, CancellationToken ct)
    {
        // The subscription row is the per-tenant lock: whoever holds it is the only one measuring and inserting for this tenant.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT Id FROM TenantSubscriptions WHERE TenantId = {tenantId} FOR UPDATE", ct);
    }

    public async Task<Result<T>> RunWithinLimitAsync<T>(UsageDimension dimension, long quantity, Func<CancellationToken, Task<Result<T>>> action, CancellationToken ct = default)
    {
        var c = await LoadAsync(ct);
        if (Gate(c, SensitiveDimensions.Contains(dimension)) is { } gate) return Result.Failure<T>(gate);
        var eff = Effective(c, dimension);
        if (c.Exempt || c.Sub is null || eff.Limit is null) return await action(ct);   // unlimited: no lock, no extra cost
        if (eff.Limit == 0) { await RecordNoticeAsync(c, dimension, 100, 0, 0, ct); return Result.Failure<T>(EntitlementErrors.Limit(dimension, 0, 0, quantity)); }

        var (from, to) = Window(c);
        var owns = db.Database.CurrentTransaction is null;
        var tx = owns ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct) : null;
        long used;
        try
        {
            await LockAsync(c.TenantId, ct);
            used = await meter.MeasureAsync(dimension, from, to, ct) + await meter.HeldAsync(dimension, ct);
            if (!EntitlementEvaluator.Allows(eff.Limit, used, quantity))
            {
                if (tx is not null) await tx.RollbackAsync(ct);
                await RecordNoticeAsync(c, dimension, 100, used, eff.Limit.Value, ct);
                return Result.Failure<T>(EntitlementErrors.Limit(dimension, eff.Limit, used, quantity));
            }
            var result = await action(ct);
            if (result.IsFailure) { if (tx is not null) await tx.RollbackAsync(ct); return result; }
            if (tx is not null) await tx.CommitAsync(ct);
            await EvaluateThresholdsAsync(c, dimension, used + quantity, eff.Limit.Value, ct);
            return result;
        }
        catch
        {
            if (tx is not null) { try { await tx.RollbackAsync(CancellationToken.None); } catch { /* connection already gone */ } }
            throw;
        }
        finally { if (tx is not null) await tx.DisposeAsync(); }
    }

    public async Task<Result<IAsyncDisposable>> HoldAsync(UsageDimension dimension, long estimate, CancellationToken ct = default)
    {
        var c = await LoadAsync(ct);
        if (Gate(c, SensitiveDimensions.Contains(dimension)) is { } gate) return Result.Failure<IAsyncDisposable>(gate);
        var eff = Effective(c, dimension);
        if (c.Exempt || c.Sub is null || eff.Limit is null) return Result.Success<IAsyncDisposable>(NoHold.Instance);
        if (eff.Limit == 0) return Result.Failure<IAsyncDisposable>(EntitlementErrors.Limit(dimension, 0, 0, 1));

        var (from, to) = Window(c);
        var owns = db.Database.CurrentTransaction is null;
        var tx = owns ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct) : null;
        try
        {
            await LockAsync(c.TenantId, ct);
            var used = await meter.MeasureAsync(dimension, from, to, ct) + await meter.HeldAsync(dimension, ct);
            if (!EntitlementEvaluator.Allows(eff.Limit, used, 1))
            {
                if (tx is not null) await tx.RollbackAsync(ct);
                await RecordNoticeAsync(c, dimension, 100, used, eff.Limit.Value, ct);
                return Result.Failure<IAsyncDisposable>(EntitlementErrors.Limit(dimension, eff.Limit, used, 1));
            }
            var id = Guid.NewGuid(); var now = clock.UtcNow; var expires = now.AddMinutes(10);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO UsageReservations (Id, TenantId, Dimension, Quantity, ExpiresAtUtc, CreatedAtUtc) VALUES ({id}, {c.TenantId}, {(int)dimension}, {Math.Max(1, estimate)}, {expires}, {now})", ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return Result.Success<IAsyncDisposable>(new Hold(db, logger, id, c.TenantId));
        }
        catch
        {
            if (tx is not null) { try { await tx.RollbackAsync(CancellationToken.None); } catch { } }
            throw;
        }
        finally { if (tx is not null) await tx.DisposeAsync(); }
    }

    private sealed class NoHold : IAsyncDisposable
    {
        public static readonly NoHold Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Hold(AppDbContext db, ILogger logger, Guid id, Guid tenantId) : IAsyncDisposable
    {
        private bool _released;
        public async ValueTask DisposeAsync()
        {
            if (_released) return;
            _released = true;
            try { await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM UsageReservations WHERE Id = {id} AND TenantId = {tenantId}"); }
            catch (Exception ex) { logger.LogWarning(ex, "A usage hold could not be released; it expires on its own"); }
        }
    }

    public async Task<Result> RequireFeatureAsync(string feature, CancellationToken ct = default)
    {
        var c = await LoadAsync(ct);
        if (c.Exempt || c.Plan is null) return Result.Success();
        if (Gate(c, SensitiveFeatures.Contains(feature)) is { } gate) return Result.Failure(gate);
        var planFeatures = c.Plan.Features.Where(f => f.Enabled).Select(f => f.Feature).ToHashSet();
        return EntitlementEvaluator.Feature(feature, planFeatures, c.Overrides, clock.UtcNow) ? Result.Success() : Result.Failure(EntitlementErrors.Feature(feature));
    }

    public async Task<IReadOnlyDictionary<UsageDimension, long>> MeasureAllAsync(CancellationToken ct = default)
    {
        var c = await LoadAsync(ct);
        var (from, to) = Window(c);
        var map = new Dictionary<UsageDimension, long>();
        foreach (var d in DimensionCatalog.All) map[d.Dimension] = await meter.MeasureAsync(d.Dimension, from, to, ct);
        return map;
    }

    public async Task<EntitlementOverview> OverviewAsync(CancellationToken ct = default)
    {
        var c = await LoadAsync(ct);
        if (c.Exempt) return new EntitlementOverview(null, [], [], [], false, true);
        var usage = await MeasureAllAsync(ct);
        var lines = DimensionCatalog.All.Select(i =>
        {
            var e = Effective(c, i.Dimension); var used = usage[i.Dimension];
            return new UsageLine(i.Dimension, i.Label, i.Unit, i.IsPeriodic, used, e.Limit, e.Source, e.OverrideReason, e.OverrideExpiresAtUtc,
                e.Limit is null ? null : e.Limit == 0 ? (used > 0 ? 100 : 0) : Math.Round(used * 100.0 / e.Limit.Value, 1), i.UiFactor);
        }).ToList();
        var planFeatures = c.Plan?.Features.Where(f => f.Enabled).Select(f => f.Feature).ToHashSet() ?? [];
        var features = Features.All.Select(f => new FeatureLine(f.Key, f.Label,
            c.Plan is null || EntitlementEvaluator.Feature(f.Key, planFeatures, c.Overrides, clock.UtcNow), c.Overrides.Any(o => o.Kind == OverrideKind.Feature && o.Key == f.Key && (o.ExpiresAtUtc is null || o.ExpiresAtUtc > clock.UtcNow)))).ToList();
        SubscriptionDto? dto = c.Sub is null || c.Plan is null ? null : new SubscriptionDto(c.Plan.Id, c.Plan.Name, c.Sub.State, c.Sub.PeriodStartUtc, c.Sub.PeriodEndUtc, c.Sub.TrialEndsAtUtc, c.Sub.GraceEndsAtUtc,
            c.Sub.PendingPlanId, c.PendingPlanName, c.Plan.Price, c.Plan.Currency, c.Plan.Cycle, c.Sub.GrantReason);
        var t = c.TenantId; var start = c.Sub?.PeriodStartUtc;
        var notices = start is null ? [] : (await db.UsageNotices.AsNoTracking().Where(n => n.TenantId == t && n.PeriodStartUtc == start).OrderByDescending(n => n.CreatedAtUtc).Take(20).ToListAsync(ct))
            .Select(n => new UsageNoticeDto(n.Dimension, DimensionCatalog.Get(n.Dimension).Label, n.Threshold, n.Used, n.Limit, n.PeriodStartUtc, n.CreatedAtUtc)).ToList();
        return new EntitlementOverview(dto, lines, features, notices, c.Locked, false);
    }

    private Task RecordNoticeAsync(Ctx c, UsageDimension d, int threshold, long used, long limit, CancellationToken ct) =>
        c.Sub is null ? Task.CompletedTask : InsertNoticeAsync(c, d, threshold, used, limit, ct);

    private async Task EvaluateThresholdsAsync(Ctx c, UsageDimension d, long used, long limit, CancellationToken ct)
    {
        if (limit <= 0 || c.Sub is null) return;
        foreach (var th in Thresholds)
            if (used * 100 >= limit * th) await InsertNoticeAsync(c, d, th, used, limit, ct);
    }

    /// <summary>Records a 80 % / 100 % notice once per (tenant, dimension, period, threshold). Best effort and idempotent: the unique index turns a repeat or a race into a no-op, and a failure never fails the caller's request.</summary>
    private async Task InsertNoticeAsync(Ctx c, UsageDimension d, int threshold, long used, long limit, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(c.TenantId);
            var own = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var period = c.Sub!.PeriodStartUtc;
            if (await own.UsageNotices.AnyAsync(n => n.TenantId == c.TenantId && n.Dimension == d && n.PeriodStartUtc == period && n.Threshold == threshold, ct)) return;
            own.UsageNotices.Add(UsageNotice.Create(c.TenantId, d, period, threshold, used, limit, clock.UtcNow));
            await own.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) { /* another request recorded it first */ }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "A usage notice could not be recorded"); }
    }
}
