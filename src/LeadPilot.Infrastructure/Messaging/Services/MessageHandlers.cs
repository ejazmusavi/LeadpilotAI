using System.Text.Json;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenancyResultFlow = LeadPilot.Infrastructure.Services.Tenancy.ResultFlow;

namespace LeadPilot.Infrastructure.Messaging.Services;

/// <summary>Shared by both consumers: exactly-once effects through a (consumer, message id) record that is committed in the same transaction as the effects.</summary>
internal static class ConsumerIdempotency
{
    public static async Task<bool> AlreadyProcessedAsync(CrmContext crm, string consumer, Guid messageId, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        return await crm.Db.ProcessedMessages.AnyAsync(p => p.TenantId == tenantId && p.ConsumerName == consumer && p.MessageId == messageId, ct);
    }

    /// <summary>Marks the message processed and saves everything pending. A concurrent consumer that got there first makes this a no-op (its transaction already holds the effects).</summary>
    public static async Task CommitAsync(CrmContext crm, string consumer, Guid messageId, CancellationToken ct)
    {
        crm.Db.ProcessedMessages.Add(ProcessedMessage.Create(crm.TenantId, consumer, messageId, crm.Now));
        try { await crm.Db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (TenancyResultFlow.IsDuplicate(ex)) { crm.Db.ChangeTracker.Clear(); }
    }
}

/// <summary>
/// Consumer of <c>message.received</c>: links the conversation to a Contact by normalised E.164 phone (unknown numbers become leads only when the tenant setting
/// <c>messaging.auto_create_leads</c> is on), updates the conversation, detects opt-outs (suppression + consent revocation through the Phase 3 services, audited) and publishes the AI extension event.
/// </summary>
public sealed class InboundMessageHandler(
    CrmContext crm, OptOutProcessor optOut, ITenantSettingsService settings, ILeadService leads, ICampaignEvents campaigns, ILogger<InboundMessageHandler> logger) : IMessagingEventHandler
{
    public const string Name = "messaging.inbound";
    public string ConsumerName => Name;

    public async Task HandleAsync(EventEnvelope envelope, int attempt, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var payload = JsonSerializer.Deserialize<InboundReceivedPayload>(envelope.Payload) ?? throw new PoisonMessageException("payload_unreadable");
        if (await ConsumerIdempotency.AlreadyProcessedAsync(crm, Name, envelope.MessageId, ct)) { logger.LogInformation("Message {MessageId} already processed", envelope.MessageId); return; }

        // Everything is looked up with the tenant of the verified header: a forged TenantId finds nothing and the message is dead-lettered.
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == payload.MessageId && m.TenantId == tenantId, ct) ?? throw new PoisonMessageException("message_not_found_for_tenant");
        var conv = await db.Conversations.FirstOrDefaultAsync(c => c.Id == payload.ConversationId && c.TenantId == tenantId, ct) ?? throw new PoisonMessageException("conversation_not_found_for_tenant");
        var we = await db.WebhookEvents.FirstOrDefaultAsync(w => w.Id == payload.WebhookEventId && w.TenantId == tenantId, ct) ?? throw new PoisonMessageException("webhook_event_not_found_for_tenant");
        if (message.ConversationId != conv.Id || message.ChannelAccountId != payload.ChannelAccountId || conv.ChannelAccountId != payload.ChannelAccountId) throw new PoisonMessageException("ids_do_not_belong_together");

        // contact link (never guessed: a hidden @lid sender without a known phone stays unlinked)
        if (conv.ContactId is null && conv.PeerPhoneE164 is { } phone)
        {
            var contactId = await FindContactAsync(phone, ct);
            if (contactId is null && await AutoCreateEnabledAsync(ct)) contactId = await CreateLeadAsync(conv, phone, ct);
            if (contactId is { } id) conv.LinkContact(id, now);
        }
        if (conv.ContactId is { } linked) message.SetContact(linked);

        var preview = string.IsNullOrWhiteSpace(message.Body) ? "[attachment]" : message.Body;
        conv.RegisterInbound(message.OccurredAtUtc, preview, now);

        var optedOut = await ProcessOptOutAsync(conv, message, ct);
        if (!optedOut && conv.AiMode == AiMode.Active)
            db.OutboxMessages.Add(OutboxMessage.Create(tenantId, MessagingEventTypes.AiReplyRequested, JsonSerializer.Serialize(new AiReplyRequestedPayload(conv.Id, message.Id, conv.ChannelAccountId)), envelope.CorrelationId, envelope.MessageId.ToString(), now));

        we.MarkProcessed(now, optedOut ? "opt-out processed" : null);
        // a reply to a campaign message inside the attribution window (an opt-out is logged, never counted as a reply); staged into the same transaction as the idempotency record
        await campaigns.StageInboundAsync(conv.Id, message.Id, optedOut, ct);
        await ConsumerIdempotency.CommitAsync(crm, Name, envelope.MessageId, ct);
    }

    private async Task<Guid?> FindContactAsync(string phoneE164, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        // several contacts may share a number (different leads): the primary, then the oldest, wins
        return await crm.Db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && !c.IsDeleted && c.PhoneE164 == phoneE164)
            .OrderByDescending(c => c.IsPrimary).ThenBy(c => c.CreatedAtUtc).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
    }

    private async Task<bool> AutoCreateEnabledAsync(CancellationToken ct) =>
        string.Equals(await settings.GetValueAsync(OutboundPolicy.AutoCreateLeadsSettingKey, ct), "true", StringComparison.OrdinalIgnoreCase);

    private async Task<Guid?> CreateLeadAsync(Conversation conv, string phone, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(conv.PeerName) ? phone : conv.PeerName!;
        var created = await leads.CreateFromSourceAsync(new CreateLeadRequest(name, SourceType: LeadSourceType.Other, SourceName: "WhatsApp inbound message", AllowDuplicate: true,
            Contacts: [new ContactInput(conv.PeerName, null, phone, null, DataOrigin.Provided, IsPrimary: true)]), LeadSourceType.Other, null, "Created from an inbound WhatsApp message (tenant setting messaging.auto_create_leads)", crm.Now, ct);
        if (created.IsFailure) { logger.LogWarning("Lead could not be created from an inbound message: {Error}", created.Error.Code); return null; }
        return await FindContactAsync(phone, ct);
    }

    /// <summary>Opt-out → suppression + consent revocation, shared with the AI's StopMarketingMessages tool (see <see cref="OptOutProcessor"/>).</summary>
    private async Task<bool> ProcessOptOutAsync(Conversation conv, Message message, CancellationToken ct)
    {
        var keywords = OptOutDetector.ParseList(await settings.GetValueAsync(OptOutDetector.SettingKey, ct));
        if (!OptOutDetector.IsOptOut(message.Body, keywords)) return false;
        await optOut.ApplyAsync(conv, "WhatsApp opt-out message", message.ExternalMessageId, "Detected by the opt-out keyword rule", ct);
        return true;
    }
}

/// <summary>
/// The one implementation of "this customer asked us to stop": the chat is blocked, the number is suppressed on WhatsApp and marketing consent is revoked for every matching contact. Idempotent (safe after a crash).
/// Used by the inbound keyword rule and by the AI tool StopMarketingMessages.
/// </summary>
public sealed class OptOutProcessor(CrmContext crm, ISuppressionService suppression, IConsentService consent, ILogger<OptOutProcessor> logger)
{
    public async Task ApplyAsync(Conversation conv, string suppressionNote, string? sourceRef, string consentNote, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        conv.RegisterOptOut(crm.Now);
        if (conv.PeerPhoneE164 is not { } phone)
        {
            crm.Record("Messaging.OptOut", "Conversation", conv.Id, "An opt-out arrived from a hidden WhatsApp id with no known phone number: the conversation is blocked from further messages, but no number could be suppressed.");
            return;
        }

        var added = await suppression.AddAsync(new AddSuppressionRequest(SuppressionChannel.WhatsApp, phone, SuppressionReason.OptOut, suppressionNote), ct);
        if (added.IsFailure && added.Error.Code != "conflict") logger.LogWarning("Suppression for an opt-out failed: {Error}", added.Error.Code);

        var contactIds = await crm.Db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && !c.IsDeleted && c.PhoneE164 == phone).Select(c => c.Id).ToListAsync(ct);
        foreach (var contactId in contactIds)
        {
            var latest = await crm.Db.ConsentRecords.AsNoTracking().Where(r => r.TenantId == tenantId && r.ContactId == contactId && r.Channel == ConsentChannel.WhatsApp && r.Purpose == ConsentPurpose.Marketing)
                .OrderByDescending(r => r.OccurredAtUtc).ThenByDescending(r => r.IsRevocation).Select(r => (ConsentStatus?)r.Status).FirstOrDefaultAsync(ct);
            if (latest == ConsentStatus.Revoked) continue;
            await consent.RecordAsync(contactId, new RecordConsentRequest(ConsentChannel.WhatsApp, ConsentPurpose.Marketing, ConsentStatus.Revoked, suppressionNote, sourceRef, consentNote), ct);
        }
        crm.Record("Messaging.OptOut", "Conversation", conv.Id, $"Opt-out from {CrmContext.Mask(phone)}: number suppressed on WhatsApp, marketing consent revoked for {contactIds.Count} contact(s).");
    }
}

/// <summary>Consumer of <c>message.status</c> (acks → monotonic delivery status) and <c>account.status</c> (session state, auto-pause, WhatsApp restrictions).</summary>
public sealed class StatusEventHandler(CrmContext crm, IAccountHealthService health, ICampaignEvents campaigns, IOptions<MessagingOptions> options, ILogger<StatusEventHandler> logger) : IMessagingEventHandler
{
    public const string Name = "messaging.status";
    public string ConsumerName => Name;

    public async Task HandleAsync(EventEnvelope envelope, int attempt, CancellationToken ct)
    {
        if (await ConsumerIdempotency.AlreadyProcessedAsync(crm, Name, envelope.MessageId, ct)) return;
        switch (envelope.Type)
        {
            case MessagingEventTypes.MessageStatusChanged: await HandleAckAsync(envelope, attempt, ct); break;
            case MessagingEventTypes.AccountStatusChanged: await HandleSessionAsync(envelope, ct); break;
            default: throw new PoisonMessageException("unexpected_event_type");
        }
    }

    private async Task HandleAckAsync(EventEnvelope envelope, int attempt, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var p = JsonSerializer.Deserialize<StatusChangedPayload>(envelope.Payload) ?? throw new PoisonMessageException("payload_unreadable");
        var we = await db.WebhookEvents.FirstOrDefaultAsync(w => w.Id == p.WebhookEventId && w.TenantId == tenantId, ct) ?? throw new PoisonMessageException("webhook_event_not_found_for_tenant");
        if (!await db.ChannelAccounts.AnyAsync(a => a.Id == p.ChannelAccountId && a.TenantId == tenantId, ct)) throw new PoisonMessageException("account_not_found_for_tenant");

        var message = await db.Messages.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.ChannelAccountId == p.ChannelAccountId && m.ExternalMessageId == p.ExternalMessageId && m.Direction == MessageDirection.Outbound, ct);
        if (message is null)
        {
            // The ack can overtake the send job that stores the gateway id: retry a few times, then give up quietly (it may be a message sent from the phone itself).
            if (attempt < Math.Max(1, options.Value.InboundAckMatchAttempts)) throw new InvalidOperationException("The acknowledged message is not stored yet.");
            we.MarkIgnored("No outbound message with this gateway id.", now);
            await ConsumerIdempotency.CommitAsync(crm, Name, envelope.MessageId, ct);
            return;
        }

        var delivery = Enum.TryParse<MessageStatus>(p.DeliveryStatus, out var d) ? d : (MessageStatus?)null;
        var at = p.OccurredAtUtc ?? now;
        var applied = false;
        if (delivery is { } status) applied = message.ApplyDelivery(status, at);
        else if (p.IsError && message.Status is MessageStatus.Queued or MessageStatus.Sending or MessageStatus.Unknown or MessageStatus.Sent)
        {
            message.MarkFailed("gateway_error", "WhatsApp reported an error for this message.", now);
            applied = true;
        }
        if (delivery is not null || p.IsError)
            db.MessageStatusEvents.Add(MessageStatusEvent.Create(tenantId, message.Id, delivery ?? MessageStatus.Failed, "ack", p.Ack, applied, at, now));
        we.MarkProcessed(now, applied ? null : "no change (older or equal status)");
        if (message.CampaignRecipientId is not null) await campaigns.OnOutboundChangedAsync(message.Id, save: false, ct: ct);   // same transaction as the idempotency record
        await ConsumerIdempotency.CommitAsync(crm, Name, envelope.MessageId, ct);
    }

    private async Task HandleSessionAsync(EventEnvelope envelope, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var p = JsonSerializer.Deserialize<AccountStatusPayload>(envelope.Payload) ?? throw new PoisonMessageException("payload_unreadable");
        var we = await crm.Db.WebhookEvents.FirstOrDefaultAsync(w => w.Id == p.WebhookEventId && w.TenantId == tenantId, ct) ?? throw new PoisonMessageException("webhook_event_not_found_for_tenant");
        await health.ApplySessionStatusAsync(p.ChannelAccountId, p, ct);
        we.MarkProcessed(crm.Now);
        await ConsumerIdempotency.CommitAsync(crm, Name, envelope.MessageId, ct);
        logger.LogInformation("Session status {Status} applied to account {AccountId}", p.Status, p.ChannelAccountId);
    }
}
