using Hangfire;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace LeadPilot.Infrastructure.Campaigns;

public static class CampaignQueues { public const string Campaigns = "campaigns"; }

/// <summary>
/// Hangfire entry points of the campaign module (the only scheduler). Arguments are ids only; the tenant scope is established here and fails closed.
/// Start and tick are idempotent (atomic transition, lease), so Hangfire's bounded automatic retry is safe: a repeated job finds nothing to do or continues from the recipients' statuses.
/// </summary>
public sealed class CampaignJob(ITenantScopeRunner scopes)
{
    [Queue(CampaignQueues.Campaigns)]
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [20, 60], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task StartAsync(Guid tenantId, Guid campaignId, CancellationToken ct) =>
        scopes.RunAsync(tenantId, (sp, c) => sp.GetRequiredService<ICampaignRunner>().StartAsync(campaignId, c), ct);

    [Queue(CampaignQueues.Campaigns)]
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [20, 60], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task TickAsync(Guid tenantId, Guid campaignId, CancellationToken ct) =>
        scopes.RunAsync(tenantId, (sp, c) => sp.GetRequiredService<ICampaignRunner>().TickAsync(campaignId, c), ct);
}

/// <summary>Every minute: finds tenants with due or stuck campaigns (one cross-tenant query returning ids only) and pushes them inside their own tenant scope. This is the crash-recovery path.</summary>
public sealed class CampaignSweepJob(ITenantScopeRunner scopes, IClock clock, IOptions<CampaignOptions> options, ILogger<CampaignSweepJob> logger)
{
    public const string RecurringId = "campaign-sweep";

    [Queue(CampaignQueues.Campaigns)]
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(CancellationToken ct)
    {
        List<Guid> tenants = [];
        await scopes.RunPlatformJobAsync(async (sp, c) => tenants = await sp.GetRequiredService<CampaignDirectory>().TenantsNeedingAttentionAsync(clock.UtcNow, options.Value.StaleTickMinutes, c), ct);
        var pushed = 0;
        foreach (var id in tenants)
        {
            try { await scopes.RunAsync(id, async (sp, c) => pushed += await sp.GetRequiredService<ICampaignRunner>().SweepAsync(c), ct); }
            catch (TenantUnavailableException) { /* suspended tenants are skipped */ }
            catch (Exception ex) { logger.LogError(ex, "Campaign sweep failed for tenant {TenantId}", id); }
        }
        if (pushed > 0) logger.LogInformation("Campaign sweep pushed {Count} campaign job(s)", pushed);
    }

    public static void ScheduleRecurring(IRecurringJobManager recurring) =>
        recurring.AddOrUpdate<CampaignSweepJob>(RecurringId, x => x.RunAsync(CancellationToken.None), Cron.Minutely());
}

public sealed class HangfireCampaignScheduler(IBackgroundJobClient client) : ICampaignJobScheduler
{
    public string ScheduleStart(Guid tenantId, Guid campaignId, DateTime atUtc)
    {
        var delay = atUtc - DateTime.UtcNow;
        return delay > TimeSpan.FromSeconds(1)
            ? client.Schedule<CampaignJob>(x => x.StartAsync(tenantId, campaignId, CancellationToken.None), delay)
            : client.Enqueue<CampaignJob>(x => x.StartAsync(tenantId, campaignId, CancellationToken.None));
    }

    public void EnqueueTick(Guid tenantId, Guid campaignId, TimeSpan? delay = null)
    {
        if (delay is { } d && d > TimeSpan.Zero) client.Schedule<CampaignJob>(x => x.TickAsync(tenantId, campaignId, CancellationToken.None), d);
        else client.Enqueue<CampaignJob>(x => x.TickAsync(tenantId, campaignId, CancellationToken.None));
    }

    public void Delete(string? jobId) { if (!string.IsNullOrEmpty(jobId)) client.Delete(jobId); }
}

/// <summary>
/// Consumer of <c>campaign.recipient</c> / <c>campaign.status</c> (RabbitMQ, durable): recomputes the stats rollup of the campaign from the raw rows (idempotent by construction, plus the
/// (consumer, message id) record) and, when a campaign completed, lets its follow-ups be scheduled at once instead of waiting for the sweep.
/// </summary>
public sealed class CampaignEventHandler(CampaignCore core, CrmContext crm, ICampaignRunner runner) : IMessagingEventHandler
{
    public const string Name = "campaigns.events";
    public string ConsumerName => Name;

    public async Task HandleAsync(EventEnvelope envelope, int attempt, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        if (await crm.Db.ProcessedMessages.AnyAsync(p => p.TenantId == tenantId && p.ConsumerName == Name && p.MessageId == envelope.MessageId, ct)) return;
        Guid campaignId; var completed = false;
        switch (envelope.Type)
        {
            case CampaignEventTypes.RecipientChanged:
                campaignId = (JsonSerializer.Deserialize<CampaignRecipientChangedPayload>(envelope.Payload) ?? throw new PoisonMessageException("payload_unreadable")).CampaignId; break;
            case CampaignEventTypes.StatusChanged:
                var p = JsonSerializer.Deserialize<CampaignStatusChangedPayload>(envelope.Payload) ?? throw new PoisonMessageException("payload_unreadable");
                campaignId = p.CampaignId; completed = p.Status == nameof(CampaignStatus.Completed); break;
            default: throw new PoisonMessageException("unexpected_event_type");
        }
        // the id must belong to the tenant of the verified header: a forged tenant finds nothing and the message is dead-lettered
        if (!await crm.Db.Campaigns.AnyAsync(c => c.Id == campaignId && c.TenantId == tenantId, ct)) throw new PoisonMessageException("campaign_not_found_for_tenant");
        await core.RecomputeRollupAsync(campaignId, ct);
        if (completed) await runner.SweepAsync(ct);
        crm.Db.ProcessedMessages.Add(Domain.Messaging.ProcessedMessage.Create(tenantId, Name, envelope.MessageId, crm.Now));
        try { await crm.Db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Services.Tenancy.ResultFlow.IsDuplicate(ex)) { crm.Db.ChangeTracker.Clear(); }
    }
}

public static class CampaignServiceCollection
{
    public static IServiceCollection AddCampaigns(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<CampaignOptions>(config.GetSection(CampaignOptions.Section));
        services.AddScoped<CampaignSettingsStore>();
        services.AddScoped<CampaignCore>();
        services.AddScoped<AudienceScanner>();
        services.AddScoped<AudienceSnapshotBuilder>();
        services.AddScoped<CampaignDirectory>();
        services.AddScoped<ICampaignChannel, WhatsAppCampaignChannel>();
        services.AddScoped<ICampaignSendGuard, CampaignSendGuard>();
        services.AddScoped<ICampaignEvents, CampaignEvents>();
        services.AddScoped<CampaignService>();
        services.AddScoped<ICampaignService>(sp => sp.GetRequiredService<CampaignService>());
        services.AddScoped<ICampaignAudienceService, CampaignAudienceService>();
        services.AddScoped<ICampaignSettingsService, CampaignSettingsService>();
        services.AddScoped<ICampaignRunner, CampaignRunner>();
        services.AddScoped<IMessagingEventHandler, CampaignEventHandler>();
        services.AddSingleton<ICampaignJobScheduler, HangfireCampaignScheduler>();
        services.AddScoped<CampaignJob>();
        services.AddScoped<CampaignSweepJob>();
        return services;
    }
}
