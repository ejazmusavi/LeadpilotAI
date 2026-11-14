using Hangfire;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Conversion;
using LeadPilot.Domain.Conversion;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Conversion;

/// <summary>Anchor type for tests that need the Infrastructure assembly of this module.</summary>
public static class ConversionJobsMarker;

public static class ConversionQueues { public const string Conversion = "conversion"; }

/// <summary>
/// Hangfire entry points (the one scheduler). Every minute: the demo reminders that are due. Every five minutes: trial expiry notices and expiry. Both find the tenants with work, then run each tenant's work inside
/// its own validated tenant scope; the services they call are idempotent, so overlapping or repeated runs are harmless.
/// </summary>
public sealed class ConversionSweepJob(ITenantScopeRunner scopes, IClock clock, ILogger<ConversionSweepJob> logger)
{
    public const string RemindersId = "demo-reminders", TrialsId = "trial-lifecycle";

    [Queue(ConversionQueues.Conversion)]
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunRemindersAsync(CancellationToken ct)
    {
        List<Guid> tenants = [];
        await scopes.RunPlatformJobAsync(async (sp, c) => tenants = await sp.GetRequiredService<ConversionDirectory>().TenantsWithDueRemindersAsync(clock.UtcNow, c), ct);
        foreach (var id in tenants)
        {
            try { await scopes.RunAsync(id, (sp, c) => sp.GetRequiredService<IDemoReminderService>().ProcessDueAsync(c), ct); }
            catch (TenantUnavailableException) { }
            catch (Exception ex) { logger.LogError(ex, "Demo reminder sweep failed for tenant {TenantId}", id); }
        }
    }

    [Queue(ConversionQueues.Conversion)]
    [DisableConcurrentExecution(300)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunTrialsAsync(CancellationToken ct)
    {
        List<Guid> tenants = [];
        await scopes.RunPlatformJobAsync(async (sp, c) => tenants = await sp.GetRequiredService<ConversionDirectory>().TenantsWithEndingTrialsAsync(clock.UtcNow, c), ct);
        foreach (var id in tenants)
        {
            try { await scopes.RunAsync(id, (sp, c) => sp.GetRequiredService<ITrialLifecycleService>().SweepAsync(c), ct); }
            catch (TenantUnavailableException) { }
            catch (Exception ex) { logger.LogError(ex, "Trial lifecycle sweep failed for tenant {TenantId}", id); }
        }
    }

    public static void ScheduleRecurring(IRecurringJobManager recurring)
    {
        recurring.AddOrUpdate<ConversionSweepJob>(RemindersId, x => x.RunRemindersAsync(CancellationToken.None), Cron.Minutely());
        recurring.AddOrUpdate<ConversionSweepJob>(TrialsId, x => x.RunTrialsAsync(CancellationToken.None), "*/5 * * * *");
    }
}

/// <summary>Cross-tenant ids needed BEFORE a tenant scope exists (the sweeps). Returns ids only; all work then runs inside its own tenant scope.</summary>
internal sealed class ConversionDirectory(AppDbContext db)
{
    public async Task<List<Guid>> TenantsWithDueRemindersAsync(DateTime now, CancellationToken ct) =>
        await db.DemoReminders.IgnoreQueryFilters().AsNoTracking().Where(r => (r.Status == ReminderStatus.Scheduled || r.Status == ReminderStatus.Processing) && r.DueAtUtc <= now).Select(r => r.TenantId).Distinct().Take(500).ToListAsync(ct);

    public async Task<List<Guid>> TenantsWithEndingTrialsAsync(DateTime now, CancellationToken ct)
    {
        var horizon = now.AddDays(31);
        return await db.Trials.IgnoreQueryFilters().AsNoTracking().Where(t => t.Status == TrialStatus.Active && t.ExpiresAtUtc != null && t.ExpiresAtUtc <= horizon).Select(t => t.TenantId).Distinct().Take(500).ToListAsync(ct);
    }
}
