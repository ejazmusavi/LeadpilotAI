using Hangfire;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Messaging.Services;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Messaging.Jobs;

public static class MessagingQueues { public const string Messaging = "messaging"; }

/// <summary>
/// Hangfire entry points of the send pipeline. Arguments are ids only; the tenant scope is established here and fails closed. Hangfire's own automatic retry is OFF:
/// a blindly repeated job could send twice. Retries are decided by <see cref="OutboundSendRunner"/> (safe cases only) and the maintenance sweep.
/// </summary>
public sealed class MessageSendJob(ITenantScopeRunner scopes)
{
    [Queue(MessagingQueues.Messaging)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task SendAsync(Guid tenantId, Guid messageId, CancellationToken ct) =>
        scopes.RunAsync(tenantId, (sp, c) => sp.GetRequiredService<IOutboundSendRunner>().SendAsync(messageId, c), ct);

    [Queue(MessagingQueues.Messaging)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task ReconcileAsync(Guid tenantId, Guid messageId, CancellationToken ct) =>
        scopes.RunAsync(tenantId, (sp, c) => sp.GetRequiredService<IOutboundSendRunner>().ReconcileAsync(messageId, c), ct);
}

/// <summary>Publishes pending outbox rows. Runs once a minute (recurring) and right after a webhook committed (so latency is seconds, not a minute).</summary>
public sealed class OutboxDispatchJob(IOutboxDispatcher dispatcher)
{
    public const string RecurringId = "messaging-outbox";

    [Queue(MessagingQueues.Messaging)]
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(CancellationToken ct) => await dispatcher.DispatchAsync(ct);
}

/// <summary>
/// Every minute: re-schedules outbound messages nobody is working on (lost job, crashed worker) and asks the gateway about Unknown ones.
/// Daily: purges webhook payloads past their retention (the rows stay so a late redelivery is still recognised), old published outbox rows and old consumer-idempotency records.
/// </summary>
public sealed class MessagingMaintenanceJob(ITenantScopeRunner scopes, IServiceScopeFactory factory, IMessagingJobScheduler jobs, IClock clock, IOptions<MessagingOptions> options, ILogger<MessagingMaintenanceJob> logger)
{
    public const string SweepId = "messaging-sweep";
    public const string RetentionId = "messaging-retention";

    [Queue(MessagingQueues.Messaging)]
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task SweepAsync(CancellationToken ct)
    {
        List<MessagingDirectory.MessageWork> work;
        using (var scope = factory.CreateScope())
            work = await scope.ServiceProvider.GetRequiredService<MessagingDirectory>().MessagesNeedingAttentionAsync(clock.UtcNow, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(Math.Max(1, options.Value.StaleSendingMinutes)), 200, ct);
        foreach (var w in work)
        {
            if (w.Status == MessageStatus.Queued) jobs.EnqueueSend(w.TenantId, w.MessageId);
            else jobs.EnqueueReconcile(w.TenantId, w.MessageId, TimeSpan.Zero);
        }
        if (work.Count > 0) logger.LogInformation("Messaging sweep re-scheduled {Count} message(s)", work.Count);
    }

    [Queue(MessagingQueues.Messaging)]
    [DisableConcurrentExecution(600)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RetentionAsync(CancellationToken ct)
    {
        List<Guid> tenantIds = [];
        await scopes.RunPlatformJobAsync(async (sp, c) => tenantIds = await sp.GetRequiredService<AppDbContext>().Tenants.AsNoTracking().Where(t => !t.IsPlatform).Select(t => t.Id).ToListAsync(c), ct);
        var purged = 0;
        foreach (var id in tenantIds)
        {
            try
            {
                await scopes.RunAsync(id, async (sp, c) =>
                {
                    var db = sp.GetRequiredService<AppDbContext>(); var now = clock.UtcNow;
                    purged += await db.WebhookEvents.Where(w => w.TenantId == id && w.ExpiresAtUtc <= now && w.PayloadJson != "{}").ExecuteUpdateAsync(s => s.SetProperty(w => w.PayloadJson, "{}"), c);
                    await db.OutboxMessages.Where(o => o.TenantId == id && o.Status == OutboxStatus.Published && o.PublishedAtUtc < now.AddDays(-7)).ExecuteDeleteAsync(c);
                    await db.ProcessedMessages.Where(p => p.TenantId == id && p.ProcessedAtUtc < now.AddDays(-30)).ExecuteDeleteAsync(c);
                }, ct);
            }
            catch (TenantUnavailableException) { /* suspended tenants are skipped */ }
        }
        logger.LogInformation("Messaging retention purged {Count} webhook payload(s)", purged);
    }
}

public static class MessagingJobs
{
    public static void ScheduleRecurring(IRecurringJobManager recurring)
    {
        recurring.AddOrUpdate<OutboxDispatchJob>(OutboxDispatchJob.RecurringId, x => x.RunAsync(CancellationToken.None), Cron.Minutely());
        recurring.AddOrUpdate<MessagingMaintenanceJob>(MessagingMaintenanceJob.SweepId, x => x.SweepAsync(CancellationToken.None), Cron.Minutely());
        recurring.AddOrUpdate<MessagingMaintenanceJob>(MessagingMaintenanceJob.RetentionId, x => x.RetentionAsync(CancellationToken.None), Cron.Daily(3, 30));
    }
}

public sealed class HangfireMessagingScheduler(IBackgroundJobClient client) : IMessagingJobScheduler
{
    private static long _lastDispatchTicks;

    public string EnqueueSend(Guid tenantId, Guid messageId, TimeSpan? delay = null) =>
        delay is { } d && d > TimeSpan.Zero
            ? client.Schedule<MessageSendJob>(x => x.SendAsync(tenantId, messageId, CancellationToken.None), d)
            : client.Enqueue<MessageSendJob>(x => x.SendAsync(tenantId, messageId, CancellationToken.None));

    public string EnqueueReconcile(Guid tenantId, Guid messageId, TimeSpan delay) =>
        delay > TimeSpan.Zero
            ? client.Schedule<MessageSendJob>(x => x.ReconcileAsync(tenantId, messageId, CancellationToken.None), delay)
            : client.Enqueue<MessageSendJob>(x => x.ReconcileAsync(tenantId, messageId, CancellationToken.None));

    /// <summary>At most one trigger per two seconds per process: a burst of webhooks must not create a burst of jobs (the job drains everything that is due).</summary>
    public void EnqueueOutboxDispatch()
    {
        var now = DateTime.UtcNow.Ticks; var last = Interlocked.Read(ref _lastDispatchTicks);
        if (now - last < TimeSpan.FromSeconds(2).Ticks || Interlocked.CompareExchange(ref _lastDispatchTicks, now, last) != last) return;
        client.Enqueue<OutboxDispatchJob>(x => x.RunAsync(CancellationToken.None));
    }
}
