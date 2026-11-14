using Hangfire;
using Hangfire.Dashboard;
using Hangfire.MySql;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Discovery;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Discovery;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Discovery.Jobs;

public static class DiscoveryQueues { public const string Discovery = "discovery"; }

/// <summary>Hangfire entry point of a discovery job. Arguments are ids only; the tenant scope is established here and the job fails closed without a usable tenant.</summary>
public sealed class DiscoveryJobExecutor(ITenantScopeRunner scopes)
{
    [Queue(DiscoveryQueues.Discovery)]
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [30, 120], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task ExecuteAsync(Guid tenantId, Guid jobId, CancellationToken ct) =>
        scopes.RunAsync(tenantId, (sp, c) => sp.GetRequiredService<IDiscoveryJobRunner>().RunAsync(jobId, c), ct);
}

/// <summary>Daily maintenance: removes provider content past its retention period, tenant by tenant (each in its own tenant scope).</summary>
public sealed class DiscoveryRetentionJob(ITenantScopeRunner scopes, IServiceScopeFactory factory, ILogger<DiscoveryRetentionJob> logger)
{
    public const string JobId = "discovery-retention";

    [DisableConcurrentExecution(300)]
    public async Task RunAsync(CancellationToken ct)
    {
        List<Guid> tenantIds = [];
        await scopes.RunPlatformJobAsync(async (sp, c) => tenantIds = await sp.GetRequiredService<AppDbContext>().Tenants.AsNoTracking().Where(t => !t.IsPlatform).Select(t => t.Id).ToListAsync(c), ct);
        var total = 0;
        foreach (var id in tenantIds)
        {
            try { await scopes.RunAsync(id, async (sp, c) => total += await sp.GetRequiredService<IDiscoveryRetention>().PurgeExpiredAsync(c), ct); }
            catch (TenantUnavailableException) { /* suspended tenants are skipped */ }
        }
        logger.LogInformation("Discovery retention purged {Count} result(s)", total);
        _ = factory;
    }
}

public sealed class DiscoveryRetention(Services.DiscoveryRetentionStore store) : IDiscoveryRetention
{
    public Task<int> PurgeExpiredAsync(CancellationToken ct) => store.PurgeAsync(ct);
}

public sealed class HangfireDiscoveryScheduler(IBackgroundJobClient client) : IDiscoveryJobScheduler
{
    public string Enqueue(Guid tenantId, Guid jobId) => client.Enqueue<DiscoveryJobExecutor>(x => x.ExecuteAsync(tenantId, jobId, CancellationToken.None));
    public void TryDelete(string? backgroundJobId) { if (!string.IsNullOrEmpty(backgroundJobId)) client.Delete(backgroundJobId); }
}

/// <summary>Only platform operators (platform tenant roles holding Tenants.Manage or Platform.SwitchTenant) may open the Hangfire dashboard. Everyone else gets 401/403.</summary>
public sealed class PlatformDashboardAuthorizationFilter : IDashboardAsyncAuthorizationFilter
{
    public async Task<bool> AuthorizeAsync(DashboardContext context)
    {
        var http = context.GetHttpContext();
        if (http.User.Identity?.IsAuthenticated != true) return false;
        var perms = http.RequestServices.GetRequiredService<IPermissionEvaluator>();
        return await perms.HasAsync(PermissionCodes.TenantsManage, http.RequestAborted) || await perms.HasAsync(PermissionCodes.PlatformSwitchTenant, http.RequestAborted);
    }
}

public static class HangfireSetup
{
    public const string TablePrefix = "Hangfire";

    /// <summary>Registers Hangfire with MySQL storage. <paramref name="server"/> adds the processing server (Worker only); Web/Api only enqueue.</summary>
    public static IServiceCollection AddDiscoveryHangfire(this IServiceCollection services, IConfiguration config, bool server)
    {
        var cs = config.GetConnectionString("Default") ?? throw new InvalidOperationException("Connection string 'Default' is not configured.");
        // Hangfire.MySqlStorage needs user variables for its distributed locks
        if (!cs.Contains("AllowUserVariables", StringComparison.OrdinalIgnoreCase) && !cs.Contains("Allow User Variables", StringComparison.OrdinalIgnoreCase)) cs = cs.TrimEnd(';') + ";Allow User Variables=true";
        var poll = TimeSpan.FromSeconds(Math.Max(1, config.GetValue("Discovery:Hangfire:PollSeconds", 5)));

        services.AddHangfire(cfg => cfg
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseStorage(new MySqlStorage(cs, new MySqlStorageOptions { TablesPrefix = TablePrefix, QueuePollInterval = poll, PrepareSchemaIfNecessary = true })));
        services.AddScoped<DiscoveryJobExecutor>();
        services.AddScoped<DiscoveryRetentionJob>();
        services.AddSingleton<IDiscoveryJobScheduler, HangfireDiscoveryScheduler>();
        if (server)
            services.AddHangfireServer(o =>
            {
                o.Queues = [DiscoveryQueues.Discovery, Messaging.Jobs.MessagingQueues.Messaging, Campaigns.CampaignQueues.Campaigns, Ai.Jobs.AiQueues.Knowledge, "default"];
                o.WorkerCount = Math.Clamp(config.GetValue("Discovery:Hangfire:Workers", 2), 1, 16);
                o.ServerName = $"leadpilot-{Environment.MachineName}";
            });
        return services;
    }

    public static void ScheduleRecurring(IRecurringJobManager recurring)
    {
        recurring.AddOrUpdate<DiscoveryRetentionJob>(DiscoveryRetentionJob.JobId, x => x.RunAsync(CancellationToken.None), Cron.Daily(3));
        Messaging.Jobs.MessagingJobs.ScheduleRecurring(recurring);
        Campaigns.CampaignSweepJob.ScheduleRecurring(recurring);
        Ai.Jobs.KnowledgeSweepJob.ScheduleRecurring(recurring);
    }
}
