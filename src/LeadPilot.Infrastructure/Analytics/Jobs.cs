using Hangfire;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Analytics;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Analytics;

public static class AnalyticsQueues { public const string Analytics = "analytics"; }

/// <summary>
/// Hangfire entry points of the analytics module (the one scheduler). Every ten minutes an incremental refresh, every night a full rebuild. Both find the workspaces that have leads (ids only, one cross-tenant query),
/// then refresh each inside its own validated tenant scope; the refresh recomputes from raw rows, so repeated or overlapping runs are harmless.
/// </summary>
public sealed class AnalyticsJob(ITenantScopeRunner scopes, ILogger<AnalyticsJob> logger)
{
    public const string RefreshId = "analytics-refresh", RebuildId = "analytics-rebuild";

    [Queue(AnalyticsQueues.Analytics)]
    [DisableConcurrentExecution(900)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task RefreshAsync(CancellationToken ct) => RunAsync(false, ct);

    [Queue(AnalyticsQueues.Analytics)]
    [DisableConcurrentExecution(3600)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task RebuildAsync(CancellationToken ct) => RunAsync(true, ct);

    public async Task RunAsync(bool full, CancellationToken ct)
    {
        List<Guid> tenants = [];
        await scopes.RunPlatformJobAsync(async (sp, c) => tenants = await sp.GetRequiredService<AnalyticsDirectory>().TenantsWithLeadsAsync(c), ct);
        foreach (var id in tenants)
        {
            try { await scopes.RunAsync(id, (sp, c) => sp.GetRequiredService<IAnalyticsRollupService>().RefreshAsync(full, c), ct); }
            catch (TenantUnavailableException) { /* suspended workspaces are skipped */ }
            catch (Exception ex) { logger.LogError(ex, "Analytics refresh failed for tenant {TenantId}", id); }
        }
    }

    public static void ScheduleRecurring(IRecurringJobManager recurring)
    {
        recurring.AddOrUpdate<AnalyticsJob>(RefreshId, x => x.RefreshAsync(CancellationToken.None), "*/10 * * * *");
        recurring.AddOrUpdate<AnalyticsJob>(RebuildId, x => x.RebuildAsync(CancellationToken.None), Cron.Daily(3, 30));
    }
}

/// <summary>Cross-tenant ids needed BEFORE a tenant scope exists. Returns ids only.</summary>
public sealed class AnalyticsDirectory(AppDbContext db)
{
    public async Task<List<Guid>> TenantsWithLeadsAsync(CancellationToken ct) =>
        await db.Leads.IgnoreQueryFilters().AsNoTracking().Select(l => l.TenantId).Distinct().Take(5000).ToListAsync(ct);
}

public static class AnalyticsServiceCollection
{
    public static IServiceCollection AddAnalytics(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AnalyticsOptions>(config.GetSection(AnalyticsOptions.Section));
        services.AddSingleton<AnalyticsCache>();
        services.AddScoped<IAnalyticsService, AnalyticsService>();
        services.AddScoped<IAnalyticsRollupService, AnalyticsRollupService>();
        services.AddScoped<AnalyticsDirectory>();
        services.AddScoped<AnalyticsJob>();
        return services;
    }
}
