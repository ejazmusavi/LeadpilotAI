using System.Text;
using System.Text.Json;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Messaging;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenancyResultFlow = LeadPilot.Infrastructure.Services.Tenancy.ResultFlow;

namespace LeadPilot.Infrastructure.Messaging.Services;

/// <summary>
/// Anonymous webhook entry point. Order matters: size cap → secret URL key resolves the account (unknown → ignored, nothing is written) → tenant must be active →
/// HMAC verified with the account's own secret (constant time) → session name in the SIGNED payload must equal the account's session → only then is the tenant scope set and data written.
/// Everything the event causes is stored in ONE transaction (webhook event + conversation + message + outbox row), so a duplicate or a crash can never leave half of it.
/// </summary>
public sealed class WebhookIngestionService(
    MessagingDirectory directory, AppDbContext db, ITenantContextAccessor tenantAccessor, CredentialVault vault, IWhatsAppGateway gateway, IClock clock,
    IMessagingJobScheduler jobs, IRequestInfo request, IOptions<MessagingOptions> options, ILogger<WebhookIngestionService> logger) : IWebhookIngestionService
{
    private const int MaxStoreAttempts = 3;

    public int MaxBodyBytes => Math.Max(1024, options.Value.MaxWebhookBodyBytes);

    public async Task<WebhookReceiveResult> ReceiveAsync(string webhookKey, byte[] body, IReadOnlyDictionary<string, string> headers, string? sourceIp, CancellationToken ct = default)
    {
        var o = options.Value;
        if (body.Length == 0) return new(WebhookReceiveStatus.Malformed, "empty body");
        if (body.Length > o.MaxWebhookBodyBytes) return new(WebhookReceiveStatus.TooLarge);
        if (!IsKeyShape(webhookKey)) { logger.LogWarning("Webhook with a malformed account key ignored"); return new(WebhookReceiveStatus.Ignored, "unknown account"); }

        var target = await directory.FindWebhookTargetAsync(webhookKey, ct);
        if (target is null || target.IsDeleted)
        {
            logger.LogWarning("Webhook for an unknown or removed account ignored (nothing stored)");
            return new(WebhookReceiveStatus.Ignored, "unknown account");
        }
        if (target.TenantStatus != TenantStatus.Active)
        {
            logger.LogWarning("Webhook for a non-active tenant {TenantId} refused", target.TenantId);
            return new(WebhookReceiveStatus.TenantUnavailable);
        }

        string secret;
        try { secret = vault.Open(target.TenantId, target.AccountId, target.ProtectedSecret); }
        catch (Exception ex) when (ex is CrossTenantViolationException or System.Security.Cryptography.CryptographicException)
        {
            logger.LogError(ex, "Webhook secret of account {AccountId} cannot be read; the account needs its credentials re-entered", target.AccountId);
            return new(WebhookReceiveStatus.Rejected, "credential unreadable");
        }

        var now = clock.UtcNow;
        var parsed = await gateway.ProcessWebhookAsync(new WebhookRequest(body, headers), secret, now, ct);
        if (!parsed.IsAccepted)
        {
            logger.LogWarning("Webhook for account {AccountId} rejected: {Reason}", target.AccountId, parsed.Rejection);
            return parsed.Rejection == WebhookRejection.MalformedPayload ? new(WebhookReceiveStatus.Malformed, parsed.Detail) : new(WebhookReceiveStatus.Rejected, parsed.Rejection.ToString());
        }
        var evt = parsed.Event!;
        if (!string.Equals(evt.AccountId, target.GatewayAccountId, StringComparison.Ordinal))
        {
            logger.LogWarning("Signed webhook names a different session than account {AccountId}; ignored", target.AccountId);
            return new(WebhookReceiveStatus.Ignored, "session mismatch");
        }

        tenantAccessor.SetTenant(target.TenantId);
        var correlation = request.CorrelationId ?? Guid.NewGuid().ToString("N");
        var payloadJson = Encoding.UTF8.GetString(body);
        var retention = TimeSpan.FromDays(Math.Max(1, o.WebhookRetentionDays));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var createdOutbox = await StoreAsync(target, evt, payloadJson, correlation, sourceIp, now, retention, ct);
                if (createdOutbox) TryTriggerDispatch();
                return new(WebhookReceiveStatus.Accepted);
            }
            catch (DbUpdateException ex) when (TenancyResultFlow.IsDuplicate(ex))
            {
                db.ChangeTracker.Clear();
                if (await db.WebhookEvents.AnyAsync(w => w.TenantId == target.TenantId && w.ChannelAccountId == target.AccountId && w.ExternalEventId == evt.ExternalEventId, ct))
                    return new(WebhookReceiveStatus.Duplicate);
                if (attempt >= MaxStoreAttempts) { logger.LogError(ex, "Webhook could not be stored after {Attempts} attempts", attempt); throw; }
                // otherwise a concurrent event created the same conversation first: run again, it is found now
            }
        }
    }

    private async Task<bool> StoreAsync(MessagingDirectory.WebhookTarget t, GatewayEvent evt, string payloadJson, string correlation, string? ip, DateTime now, TimeSpan retention, CancellationToken ct)
    {
        var we = WebhookEvent.Create(t.TenantId, t.AccountId, evt.ExternalEventId, evt.EventType, payloadJson, correlation, ip, now, retention);
        db.WebhookEvents.Add(we);
        OutboxMessage? outbox = null;

        switch (evt)
        {
            case IgnoredGatewayEvent ig:
                we.MarkIgnored(ig.Reason, now);
                break;

            case InboundMessageEvent m:
                if (await db.Messages.AnyAsync(x => x.TenantId == t.TenantId && x.ChannelAccountId == t.AccountId && x.ExternalMessageId == m.ExternalMessageId, ct))
                {
                    we.MarkIgnored("The message was already stored (message and message.any both announce it).", now);
                    break;
                }
                var conv = await db.Conversations.FirstOrDefaultAsync(c => c.TenantId == t.TenantId && c.ChannelAccountId == t.AccountId && c.ExternalChatId == m.ChatId, ct);
                if (conv is null) { conv = Conversation.Create(t.TenantId, t.AccountId, m.ChatId, m.PeerPhoneE164, m.PeerName, now); db.Conversations.Add(conv); }
                else { conv.SetPeerName(m.PeerName, now); if (m.PeerPhoneE164 is not null) conv.SetPeerPhone(m.PeerPhoneE164, now); }
                var msg = Message.Inbound(t.TenantId, conv.Id, t.AccountId, m.ExternalMessageId, m.Body, m.OccurredAt, correlation, now);
                db.Messages.Add(msg);
                foreach (var a in m.Attachments) db.MessageAttachments.Add(MessageAttachment.Describe(t.TenantId, msg.Id, a.FileName, a.MimeType, a.SizeBytes, now));
                outbox = Outbox(t, MessagingEventTypes.MessageReceived, new InboundReceivedPayload(we.Id, msg.Id, conv.Id, t.AccountId), correlation, we, now);
                break;

            case MessageAckEvent a:
                outbox = Outbox(t, MessagingEventTypes.MessageStatusChanged,
                    new StatusChangedPayload(we.Id, t.AccountId, a.ExternalMessageId, a.Ack, a.Delivery?.ToString(), a.IsError, a.OccurredAtUtc), correlation, we, now);
                break;

            case SessionStatusEvent s:
                outbox = Outbox(t, MessagingEventTypes.AccountStatusChanged,
                    new AccountStatusPayload(we.Id, t.AccountId, s.Status.ToString(), s.PhoneE164, s.PushName, s.NewContactsRestricted, s.RestrictedUntilUtc, s.RestrictionKind), correlation, we, now);
                break;
        }

        if (outbox is not null) db.OutboxMessages.Add(outbox);
        await db.SaveChangesAsync(ct);
        return outbox is not null;
    }

    private static OutboxMessage Outbox<T>(MessagingDirectory.WebhookTarget t, string type, T payload, string correlation, WebhookEvent source, DateTime now) =>
        OutboxMessage.Create(t.TenantId, type, JsonSerializer.Serialize(payload), correlation, source.Id.ToString(), now);

    private void TryTriggerDispatch()
    {
        try { jobs.EnqueueOutboxDispatch(); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not enqueue the outbox dispatch job; the recurring job will pick the event up"); }
    }

    public static bool IsKeyShape(string? key) => key is { Length: 48 } && key.All(char.IsAsciiHexDigitLower);
}
