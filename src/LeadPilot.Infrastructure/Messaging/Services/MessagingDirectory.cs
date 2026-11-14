using LeadPilot.Domain.Messaging;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Messaging.Services;

/// <summary>
/// The ONLY place of the messaging module that reads across tenants (IgnoreQueryFilters, allow-listed by an architecture test). Each query exists because something must happen
/// BEFORE a tenant scope can be established: a webhook arrives knowing only its secret URL key; the outbox dispatcher and the maintenance job must find which tenants have work.
/// Callers must establish the tenant scope from the returned TenantId before touching any other data.
/// </summary>
public sealed class MessagingDirectory(AppDbContext db)
{
    public sealed record WebhookTarget(Guid TenantId, Guid AccountId, string GatewayAccountId, bool IsDeleted, string ProtectedSecret, TenantStatus TenantStatus);

    public Task<WebhookTarget?> FindWebhookTargetAsync(string webhookKey, CancellationToken ct) =>
        (from a in db.ChannelAccounts.IgnoreQueryFilters().AsNoTracking()
         where a.WebhookKey == webhookKey
         join c in db.ChannelCredentials.IgnoreQueryFilters().AsNoTracking() on a.Id equals c.ChannelAccountId
         join t in db.Tenants.AsNoTracking() on a.TenantId equals t.Id
         where c.TenantId == a.TenantId
         select new WebhookTarget(a.TenantId, a.Id, a.GatewayAccountId, a.IsDeleted, c.ProtectedWebhookSecret, t.Status)).FirstOrDefaultAsync(ct);

    public Task<List<Guid>> TenantsWithDueOutboxAsync(DateTime nowUtc, int take, CancellationToken ct) =>
        db.OutboxMessages.IgnoreQueryFilters().AsNoTracking().Where(o => o.Status == OutboxStatus.Pending && o.NextAttemptAtUtc <= nowUtc).Select(o => o.TenantId).Distinct().Take(take).ToListAsync(ct);

    public sealed record MessageWork(Guid TenantId, Guid MessageId, MessageStatus Status);

    /// <summary>Outbound messages nobody is working on: queued past their time (lost Hangfire job), stuck in Sending (crashed worker), or Unknown (awaiting reconciliation).</summary>
    public async Task<List<MessageWork>> MessagesNeedingAttentionAsync(DateTime nowUtc, TimeSpan queuedSlack, TimeSpan staleSending, int take, CancellationToken ct)
    {
        var queuedBefore = nowUtc - queuedSlack; var sendingBefore = nowUtc - staleSending;
        return await db.Messages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.Direction == MessageDirection.Outbound &&
                        ((m.Status == MessageStatus.Queued && m.NextAttemptAtUtc != null && m.NextAttemptAtUtc <= queuedBefore) ||
                         (m.Status == MessageStatus.Sending && m.SendStartedAtUtc != null && m.SendStartedAtUtc <= sendingBefore) ||
                         m.Status == MessageStatus.Unknown))
            .OrderBy(m => m.CreatedAtUtc).Take(take).Select(m => new MessageWork(m.TenantId, m.Id, m.Status)).ToListAsync(ct);
    }
}
