using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Messaging.Services;

/// <summary>
/// Transactional-outbox relay. Finds the tenants that have due rows, then publishes each tenant's rows inside that tenant's scope.
/// Rows are claimed with a short lease (atomic UPDATE) so concurrent dispatchers never publish the same row twice at the same time; a crash between publish and "mark published"
/// republishes the row (same message id), which the idempotent consumers absorb. Failures back off exponentially and park the row as Failed after the maximum number of attempts.
/// </summary>
public sealed class OutboxDispatcher(ITenantScopeRunner scopes, IServiceScopeFactory factory, IEventPublisher publisher, IClock clock, IOptions<MessagingOptions> options, ILogger<OutboxDispatcher> logger) : IOutboxDispatcher
{
    public async Task<int> DispatchAsync(CancellationToken ct = default)
    {
        if (!publisher.IsConfigured) { logger.LogDebug("RabbitMQ is not configured; the outbox is not dispatched"); return 0; }
        var o = options.Value;
        List<Guid> tenants = [];
        using (var scope = factory.CreateScope())
            tenants = await scope.ServiceProvider.GetRequiredService<MessagingDirectory>().TenantsWithDueOutboxAsync(clock.UtcNow, 100, ct);

        var published = 0;
        foreach (var tenantId in tenants)
        {
            try { await scopes.RunAsync(tenantId, async (sp, c) => published += await DispatchTenantAsync(sp.GetRequiredService<AppDbContext>(), tenantId, o, c), ct); }
            catch (TenantUnavailableException) { /* suspended or deleted tenant: its events wait in the outbox */ }
        }
        if (published > 0) logger.LogInformation("Outbox: published {Count} event(s)", published);
        return published;
    }

    private async Task<int> DispatchTenantAsync(AppDbContext db, Guid tenantId, MessagingOptions o, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var rows = await db.OutboxMessages.AsNoTracking().Where(x => x.TenantId == tenantId && x.Status == OutboxStatus.Pending && x.NextAttemptAtUtc <= now)
            .OrderBy(x => x.CreatedAtUtc).Select(x => x.Id).Take(Math.Max(1, o.OutboxBatchSize)).ToListAsync(ct);
        var count = 0;
        foreach (var id in rows)
        {
            // atomic lease: only the dispatcher that moves NextAttemptAtUtc forward publishes this row
            var lease = now.AddSeconds(Math.Max(10, o.OutboxLeaseSeconds));
            var claimed = await db.OutboxMessages.Where(x => x.Id == id && x.TenantId == tenantId && x.Status == OutboxStatus.Pending && x.NextAttemptAtUtc <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAtUtc, lease), ct);
            if (claimed == 0) continue;
            var row = await db.OutboxMessages.FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            try
            {
                await publisher.PublishAsync(new EventEnvelope(row.Id, tenantId, row.CorrelationId, row.CausationId, row.Type, row.CreatedAtUtc, 1, row.PayloadJson), ct);
                row.MarkPublished(clock.UtcNow);
                count++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Outbox row {OutboxId} ({Type}) could not be published (attempt {Attempt})", id, row.Type, row.Attempts + 1);
                row.MarkAttemptFailed(ex.Message, o.OutboxMaxAttempts, clock.UtcNow);
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        return count;
    }
}
