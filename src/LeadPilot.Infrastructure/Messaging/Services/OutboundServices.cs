using System.Text.Json;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenancyResultFlow = LeadPilot.Infrastructure.Services.Tenancy.ResultFlow;

namespace LeadPilot.Infrastructure.Messaging.Services;

/// <summary>What the outbound validator needs: either an existing conversation or (contact, account) to open one.</summary>
internal sealed record QueueRequest(Guid? ConversationId, Guid? ContactId, Guid? AccountId, string? Text, Guid? TemplateId, string? IdempotencyKey, bool ReplyContext)
{
    /// <summary>Campaign sends are ALWAYS marketing, even inside a customer-initiated service window: consent is required.</summary>
    public MessagePurpose? ForcedPurpose { get; init; }
    public Guid? CampaignId { get; init; }
    public Guid? CampaignRecipientId { get; init; }
    /// <summary>The text was rendered by the caller (campaign content builder); <see cref="TemplateId"/> is then only a reference.</summary>
    public bool TextIsRendered { get; init; }
    /// <summary>Test send to an allow-listed internal number that is not a contact (always a Service-purpose message labelled as a test; suppression and opt-out still apply).</summary>
    public string? TestPhoneE164 { get; init; }
}

/// <summary>
/// The one place an outbound message is validated and queued. Order: account state → conversation → idempotency → template → compliance (suppression, opt-out, marketing eligibility)
/// → new-contact restriction → queue limits → persist Message(Queued) + status event + audit in one transaction → schedule the send job.
/// Marketing eligibility is checked again immediately before the actual send by <see cref="OutboundSendRunner"/>.
/// </summary>
public sealed class OutboundQueueService(CrmContext crm, IContactEligibilityService eligibility, ITenantSettingsService settings, IMessagingJobScheduler jobs, IRequestInfo request, IOptions<MessagingOptions> options, ILogger<OutboundQueueService> logger)
{
    internal async Task<Result<Message>> QueueAsync(QueueRequest q, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;

        // --- conversation / account / contact
        Conversation? conv = null; Contact? contact = null; ChannelAccount? account;
        if (q.ConversationId is { } cid)
        {
            conv = await db.Conversations.FirstOrDefaultAsync(c => c.Id == cid && c.TenantId == tenantId, ct);
            if (conv is null) return Result.Failure<Message>(Error.NotFound("Conversation"));
            account = await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == conv.ChannelAccountId && a.TenantId == tenantId, ct);
            if (conv.ContactId is { } ccid) contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == ccid && c.TenantId == tenantId, ct);
        }
        else if (q.TestPhoneE164 is not null)
        {
            account = await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == q.AccountId && a.TenantId == tenantId, ct);
        }
        else
        {
            contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == q.ContactId && c.TenantId == tenantId, ct);
            if (contact is null || contact.IsDeleted) return Result.Failure<Message>(Error.NotFound("Contact"));
            account = await db.ChannelAccounts.FirstOrDefaultAsync(a => a.Id == q.AccountId && a.TenantId == tenantId, ct);
        }
        if (account is null || account.IsDeleted) return Result.Failure<Message>(Error.NotFound("WhatsApp account"));
        if (account.IsPaused) return Result.Failure<Message>(new Error("account_paused", "This WhatsApp account is paused" + (account.PauseReason is null ? "." : ": " + account.PauseReason)));
        if (account.ConnectionStatus != ConnectionStatus.Working) return Result.Failure<Message>(new Error("account_not_connected", "This WhatsApp account is not connected right now. Reconnect it on the WhatsApp accounts page."));

        string? chatId;
        if (conv is null)
        {
            var phone = q.TestPhoneE164 ?? contact!.PhoneE164;
            chatId = ChatIds.FromE164(phone);
            if (chatId is null) return Result.Failure<Message>(Error.Validation(q.TestPhoneE164 is null ? "The contact has no valid phone number." : "The test number is not a valid phone number."));
            conv = await db.Conversations.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.ChannelAccountId == account.Id && c.ExternalChatId == chatId, ct);
            if (conv is null) { conv = Conversation.Create(tenantId, account.Id, chatId, phone, contact?.Name, now); if (contact is not null) conv.LinkContact(contact.Id, now); db.Conversations.Add(conv); }
            else if (conv.ContactId is null && contact is not null) conv.LinkContact(contact.Id, now);
        }
        else chatId = conv.ExternalChatId;
        if (!ChatIds.IsDirectChat(chatId)) return Result.Failure<Message>(Error.Validation("Only direct chats can be messaged."));

        // --- idempotency: the same key never creates a second message
        var key = IdempotencyKeys.Normalize(q.IdempotencyKey);
        var existing = await db.Messages.FirstOrDefaultAsync(m => m.TenantId == tenantId && m.IdempotencyKey == key, ct);

        // --- text / template
        var text = q.Text?.Trim();
        if (q.TemplateId is { } tid && !q.TextIsRendered)
        {
            var template = await db.MessageTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tid && t.TenantId == tenantId, ct);
            if (template is null) return Result.Failure<Message>(Error.NotFound("Message template"));
            if (!template.IsActive) return Result.Failure<Message>(Error.Validation("That template is switched off."));
            try { text = TemplateRenderer.Render(template.Body, TemplateValues(contact, conv)); }
            catch (TemplateRenderException ex) { return Result.Failure<Message>(Error.Validation("The template could not be filled in: " + ex.Message)); }
        }
        if (string.IsNullOrWhiteSpace(text)) return Result.Failure<Message>(Error.Validation("Write a message or choose a template."));
        if (text.Length > Message.MaxBodyLength) return Result.Failure<Message>(Error.Validation($"A message can have at most {Message.MaxBodyLength} characters."));
        if (existing is not null)
            return existing.Body == text && existing.ChannelAccountId == account.Id ? Result.Success(existing) : Result.Failure<Message>(Error.Conflict("That idempotency key was already used for a different message."));

        // --- compliance
        var window = OutboundPolicy.ParseWindow(await settings.GetValueAsync(OutboundPolicy.WindowSettingKey, ct));
        var purpose = q.ForcedPurpose ?? OutboundPolicy.PurposeFor(conv.LastInboundAtUtc, now, window);
        var denied = await CheckComplianceAsync(conv, contact, purpose, ct);
        if (denied is not null) return Result.Failure<Message>(denied);
        if (conv.LastInboundAtUtc is null && account.NewContactBlockedUntilUtc > now)
            return Result.Failure<Message>(new Error("new_contacts_blocked", $"WhatsApp is not accepting messages to new contacts from this number until {account.NewContactBlockedUntilUtc:u}. Existing conversations still work."));

        // --- queue limits (the send runner enforces pacing; here we only refuse to build an unbounded queue)
        var limit = await LimitReachedAsync(account, now, ct);
        if (limit is not null) return Result.Failure<Message>(limit);

        var message = Message.Outbound(tenantId, conv.Id, account.Id, contact?.Id, purpose, text, key, request.CorrelationId ?? Guid.NewGuid().ToString("N"), q.TemplateId, crm.UserId, now);
        if (q.CampaignId is { } campaignId) message.SetCampaign(campaignId, q.CampaignRecipientId);
        db.Messages.Add(message);
        db.MessageStatusEvents.Add(MessageStatusEvent.Create(tenantId, message.Id, MessageStatus.Queued, "queue", null, true, now, now));
        conv.RegisterOutbound(now, text, now);
        crm.Record("Message.Queued", "Message", message.Id, $"{purpose} message queued to {CrmContext.Mask(conv.PeerPhoneE164 ?? conv.ExternalChatId)} on '{account.DisplayName}'.");
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (TenancyResultFlow.IsDuplicate(ex))
        {
            // a concurrent request with the same idempotency key won: return its message
            db.ChangeTracker.Clear();
            var winner = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.TenantId == tenantId && m.IdempotencyKey == key, ct);
            if (winner is not null && winner.Body == text) return Result.Success(winner);
            throw;
        }

        try { jobs.EnqueueSend(tenantId, message.Id); }
        catch (Exception ex) { logger.LogWarning(ex, "Send job for message {MessageId} could not be scheduled; the maintenance job will pick it up", message.Id); }
        return Result.Success(message);
    }

    /// <summary>What the inbox shows next to the reply box: may the user reply now, and under which purpose.</summary>
    internal async Task<(bool CanReply, string? Reason, MessagePurpose Purpose, TimeSpan Window, bool InsideWindow)> ReplyStateAsync(Conversation conv, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var window = OutboundPolicy.ParseWindow(await settings.GetValueAsync(OutboundPolicy.WindowSettingKey, ct));
        var purpose = OutboundPolicy.PurposeFor(conv.LastInboundAtUtc, now, window);
        var inside = purpose == MessagePurpose.Service;
        var account = await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == conv.ChannelAccountId && a.TenantId == tenantId, ct);
        if (account is null || account.IsDeleted) return (false, "The WhatsApp account of this conversation was removed.", purpose, window, inside);
        if (account.IsPaused) return (false, "The WhatsApp account is paused" + (account.PauseReason is null ? "." : ": " + account.PauseReason), purpose, window, inside);
        if (account.ConnectionStatus != ConnectionStatus.Working) return (false, "The WhatsApp account is not connected.", purpose, window, inside);
        Contact? contact = conv.ContactId is { } id ? await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct) : null;
        var denied = await CheckComplianceAsync(conv, contact, purpose, ct);
        if (denied is not null) return (false, denied.Message, purpose, window, inside);
        if (conv.LastInboundAtUtc is null && account.NewContactBlockedUntilUtc > now) return (false, "WhatsApp is not accepting messages to new contacts from this number right now.", purpose, window, inside);
        return (true, null, purpose, window, inside);
    }

    private async Task<Error?> CheckComplianceAsync(Conversation conv, Contact? contact, MessagePurpose purpose, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        if (conv.OptedOutAtUtc is not null) return new Error("opted_out", "This person asked to stop receiving messages. Nothing can be sent to this chat.");
        // Suppression blocks EVERYTHING (marketing and service): after a STOP the number is not messaged at all. Lift it on the suppression list if that is intended.
        var number = conv.PeerPhoneE164 ?? contact?.PhoneE164;
        if (number is not null && await db.SuppressionEntries.AnyAsync(s => s.TenantId == tenantId && s.ValueNormalized == number && (s.Channel == SuppressionChannel.Any || s.Channel == SuppressionChannel.WhatsApp), ct))
            return new Error("suppressed", "This number is on the suppression list (it opted out or was blocked). Nothing can be sent to it.");
        if (purpose == MessagePurpose.Marketing)
        {
            if (contact is null) return new Error("not_eligible", "Nothing can be sent outside a conversation the customer started unless the number belongs to a contact with recorded marketing consent.");
            var decision = await eligibility.CheckAsync(contact.Id, ConsentChannel.WhatsApp, ConsentPurpose.Marketing, ct);
            if (!decision.IsEligible) return new Error("not_eligible", "Not allowed to message this contact: " + string.Join(" ", decision.Reasons.Select(EligibilityDecision.Explain)));
        }
        return null;
    }

    private async Task<Error?> LimitReachedAsync(ChannelAccount account, DateTime now, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var facts = await SendRateFactsLoader.LoadAsync(db, account, now, ct);
        var queued = await db.Messages.CountAsync(m => m.TenantId == tenantId && m.ChannelAccountId == account.Id && m.Direction == MessageDirection.Outbound && m.Status == MessageStatus.Queued, ct);
        var daily = SendRatePolicy.DailyLimitToday(account.DailyCap, account.WarmupStartedAtUtc, now);
        if (facts.SentLast24Hours + queued >= daily) return new Error("rate_limited", $"This account reached today's sending limit ({daily}, warm-up and caps included). It resumes as older messages leave the 24-hour window.");
        if (queued >= 200) return new Error("rate_limited", "Too many messages are waiting to be sent from this account.");
        var tenantLimit = options.Value.PerTenantDailyLimit;
        if (tenantLimit > 0)
        {
            var dayAgo = now.AddHours(-24);
            var used = await db.Messages.CountAsync(m => m.TenantId == tenantId && m.Direction == MessageDirection.Outbound && m.Status != MessageStatus.Failed && m.CreatedAtUtc >= dayAgo, ct);
            if (used >= tenantLimit) return new Error("rate_limited", $"The workspace reached its daily limit of {tenantLimit} outbound messages.");
        }
        return null;
    }

    internal static Dictionary<string, string> TemplateValues(Contact? contact, Conversation conv) => TemplateValues(contact?.Name ?? conv.PeerName, contact?.PhoneE164 ?? conv.PeerPhoneE164);

    internal static Dictionary<string, string> TemplateValues(string? name, string? phone) =>
        new() { ["name"] = name ?? string.Empty, ["first_name"] = TemplateRenderer.FirstName(name), ["phone"] = phone ?? string.Empty, ["business_name"] = string.Empty };
}

/// <summary>
/// Executes one queued message. The no-duplicate guarantee: (1) the Queued→Sending claim is one atomic UPDATE, so a retried or concurrent job finds nothing to claim;
/// (2) the gateway call is never repeated automatically: failures that provably sent nothing re-queue with backoff, anything that MAY have been sent becomes Unknown;
/// (3) Unknown (and a crash between "gateway accepted" and "id saved") is resolved by <see cref="ReconcileAsync"/> against the gateway's own message list, and a message the gateway
/// never confirms is marked Failed ("not confirmed") and is never re-sent by the system. This is the documented choice: a missed message is visible and re-sendable by a person, a duplicate to a customer is not undoable.
/// </summary>
public sealed class OutboundSendRunner(
    CrmContext crm, GatewayConnections connections, IWhatsAppGateway gateway, IContactEligibilityService eligibility, IAccountHealthService health, IMessagingJobScheduler jobs,
    ICampaignSendGuard campaignGuard, ICampaignEvents campaignEvents, IOptions<MessagingOptions> options, ILogger<OutboundSendRunner> logger) : IOutboundSendRunner
{
    public async Task SendAsync(Guid messageId, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var o = options.Value; var now = crm.Now;
        var message = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId && m.TenantId == tenantId && m.Direction == MessageDirection.Outbound, ct);
        if (message is null) { logger.LogWarning("Send job for unknown message {MessageId} ignored", messageId); return; }
        if (message.Status != MessageStatus.Queued) { logger.LogInformation("Message {MessageId} is {Status}; nothing to send", messageId, message.Status); return; }
        if (message.NextAttemptAtUtc is { } due && due > now.AddSeconds(1)) return;   // an early duplicate job: the job scheduled for the due time (or the maintenance sweep) handles it

        // campaign messages: is the campaign still running, the kill switch off, the recipient still waiting for exactly this message? (the guard also records the send-time eligibility decision)
        if (message.CampaignRecipientId is not null)
        {
            var block = await campaignGuard.CheckBeforeSendAsync(message, ct);
            if (block is not null) { await FailAsync(messageId, block.Code, block.Reason, ct); return; }
        }

        var loaded = await connections.LoadAsync(message.ChannelAccountId, tracked: false, ct);
        if (loaded is null) { await FailAsync(messageId, "account_missing", "The WhatsApp account no longer exists.", ct); return; }
        var (account, conn) = loaded.Value;
        var conv = await db.Conversations.AsNoTracking().FirstAsync(c => c.Id == message.ConversationId && c.TenantId == tenantId, ct);

        // the state of the world may have changed since the message was queued
        if (account.IsPaused) { await FailAsync(messageId, "account_paused", "The account was paused before this message was sent.", ct); return; }
        if (account.ConnectionStatus != ConnectionStatus.Working) { await FailAsync(messageId, "account_not_connected", "The WhatsApp session is not connected.", ct); return; }
        if (conv.OptedOutAtUtc is not null) { await FailAsync(messageId, "opted_out", "The person opted out before this message was sent.", ct); return; }
        var number = conv.PeerPhoneE164;
        if (number is not null && await db.SuppressionEntries.AnyAsync(s => s.TenantId == tenantId && s.ValueNormalized == number && (s.Channel == SuppressionChannel.Any || s.Channel == SuppressionChannel.WhatsApp), ct))
        { await FailAsync(messageId, "suppressed", "The number was suppressed before this message was sent.", ct); return; }
        if (message.Purpose == MessagePurpose.Marketing)
        {
            // consent is validated again immediately before sending, not only when the message was queued
            if (message.ContactId is not { } contactId) { await FailAsync(messageId, "not_eligible", "Marketing needs a contact with consent.", ct); return; }
            var decision = await eligibility.CheckAsync(contactId, ConsentChannel.WhatsApp, ConsentPurpose.Marketing, ct);
            if (!decision.IsEligible) { await FailAsync(messageId, "not_eligible", "No longer eligible: " + string.Join(" ", decision.Reasons.Select(EligibilityDecision.Explain)), ct); return; }
        }
        if (conv.LastInboundAtUtc is null && account.NewContactBlockedUntilUtc > now) { await FailAsync(messageId, "new_contacts_blocked", "WhatsApp is not accepting messages to new contacts from this number right now.", ct); return; }

        // pacing: min delay, hourly and daily caps, warm-up. Not allowed yet → stay Queued and come back at the allowed time.
        var facts = await SendRateFactsLoader.LoadAsync(db, await db.ChannelAccounts.AsNoTracking().FirstAsync(a => a.Id == account.Id && a.TenantId == tenantId, ct), now, ct);
        var rate = SendRatePolicy.Evaluate(facts, now);
        if (!rate.CanSendNow)
        {
            var at = rate.RetryAtUtc!.Value + SendRatePolicy.Jitter(account.MinDelaySeconds, Random.Shared.NextDouble());
            await db.Messages.Where(m => m.Id == messageId && m.TenantId == tenantId && m.Status == MessageStatus.Queued).ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAtUtc, at), ct);
            jobs.EnqueueSend(tenantId, messageId, at - now);
            logger.LogInformation("Message {MessageId} delayed until {At:u} ({Block})", messageId, at, rate.Block);
            return;
        }

        // atomic claim: exactly one worker moves Queued → Sending; a retried or concurrent job gets 0 rows and stops.
        var claimed = await db.Messages.Where(m => m.Id == messageId && m.TenantId == tenantId && m.Status == MessageStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MessageStatus.Sending).SetProperty(m => m.SendStartedAtUtc, now).SetProperty(m => m.SendAttempts, m => m.SendAttempts + 1).SetProperty(m => m.Version, m => m.Version + 1), ct);
        if (claimed == 0) return;

        GatewaySendResult result;
        try
        {
            await PrepareAsync(conn, conv, message, ct);
            result = await gateway.SendTextAsync(conn, new GatewaySendRequest(conv.ExternalChatId, message.Body), ct);
        }
        catch (GatewayException ex) { await HandleFailureAsync(message, account.Id, ex, ct); return; }
        catch (OperationCanceledException) { await MarkUnknownAsync(messageId, "The worker was stopped while sending; the outcome is not known.", CancellationToken.None); throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while sending message {MessageId}", messageId);
            await MarkUnknownAsync(messageId, "Unexpected error while sending; the outcome is not known.", CancellationToken.None);
            return;
        }

        // (a crash right here leaves the message in Sending: ReconcileAsync resolves it from the gateway's message list)
        await RecordSentAsync(messageId, result.ExternalMessageId, result.TimestampUtc ?? crm.Now, "send", ct);
        await health.RecordSendSuccessAsync(account.Id, ct);
    }

    /// <summary>Resolves Unknown / stuck-Sending messages. Found at the gateway → Sent. Not found → wait, then Failed("not confirmed"). Never re-sends.</summary>
    public async Task ReconcileAsync(Guid messageId, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now; var o = options.Value;
        var message = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId && m.TenantId == tenantId && m.Direction == MessageDirection.Outbound, ct);
        if (message is null || message.Status is not (MessageStatus.Unknown or MessageStatus.Sending)) return;
        var started = message.SendStartedAtUtc ?? message.CreatedAtUtc;
        if (message.Status == MessageStatus.Sending && now - started < TimeSpan.FromMinutes(Math.Max(1, o.StaleSendingMinutes))) return;   // another worker may still be sending it; the maintenance sweep comes back

        var loaded = await connections.LoadAsync(message.ChannelAccountId, tracked: false, ct);
        var conv = await db.Conversations.AsNoTracking().FirstAsync(c => c.Id == message.ConversationId && c.TenantId == tenantId, ct);
        GatewayMessageInfo? found = null; var lookupWorked = false;
        if (loaded is not null)
        {
            try { found = await gateway.FindSentMessageAsync(loaded.Value.Connection, conv.ExternalChatId, started.AddSeconds(-30), message.Body, ct); lookupWorked = true; }
            catch (GatewayException ex) { logger.LogWarning("Reconciliation lookup for message {MessageId} failed: {Kind}", messageId, ex.Kind); }
        }
        if (found is not null)
        {
            await RecordSentAsync(messageId, found.ExternalMessageId, found.TimestampUtc ?? now, "reconcile", ct);
            if (found.Delivery is { } d) await ApplyDeliveryAsync(messageId, d, found.TimestampUtc ?? now, found.Ack, ct);
            logger.LogInformation("Message {MessageId} reconciled: the gateway had sent it", messageId);
            return;
        }
        if (now - started < TimeSpan.FromMinutes(Math.Max(1, o.ReconcileGraceMinutes))) return;   // keep waiting; the maintenance sweep asks again

        var reason = lookupWorked
            ? "The gateway has no record of this message. It was not re-sent automatically; send it again if it is still needed."
            : "The gateway could not be asked whether this message was sent. It was not re-sent automatically; check the conversation on the phone before sending again.";
        await FailAsync(messageId, "delivery_unconfirmed", reason, ct);
        await health.RecordSendFailureAsync(message.ChannelAccountId, "delivery could not be confirmed", ct);
    }

    // ------------------------------------------------------------------

    private async Task PrepareAsync(GatewayConnection conn, Conversation conv, Message message, CancellationToken ct)
    {
        var o = options.Value;
        // WAHA's anti-blocking guidance: mark the chat as seen, show typing for a human-like moment, then send. All best effort: a failure here never blocks or repeats the send.
        try
        {
            if (o.SendSeenBeforeReply && conv.UnreadCount > 0 && conv.LastInboundAtUtc is not null) await gateway.MarkAsReadAsync(conn, conv.ExternalChatId, [], ct);
            var typing = Math.Min(o.TypingMaxMilliseconds, o.TypingMillisecondsPerChar * message.Body.Length);
            if (typing > 0)
            {
                await gateway.SetTypingAsync(conn, conv.ExternalChatId, true, ct);
                await Task.Delay(typing, ct);
                await gateway.SetTypingAsync(conn, conv.ExternalChatId, false, ct);
            }
        }
        catch (GatewayException ex) { logger.LogDebug("Seen/typing simulation failed ({Kind}); sending anyway", ex.Kind); }
    }

    private async Task HandleFailureAsync(Message message, Guid accountId, GatewayException ex, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var id = message.Id; var now = crm.Now;
        switch (ex.Kind)
        {
            case GatewayFailureKind.OutcomeUnknown:
                await MarkUnknownAsync(id, ex.Message, ct);
                jobs.EnqueueReconcile(tenantId, id, TimeSpan.FromSeconds(30));
                await health.RecordSendFailureAsync(accountId, "the gateway did not confirm a send", ct);
                return;
            case GatewayFailureKind.Unreachable or GatewayFailureKind.RateLimited:
                var attempts = await db.Messages.AsNoTracking().Where(m => m.Id == id && m.TenantId == tenantId).Select(m => m.SendAttempts).FirstAsync(ct);
                if (attempts >= SendRetryPolicy.MaxSafeRetries) { await FailAsync(id, "gateway_unavailable", "The WhatsApp gateway stayed unavailable; the message was not sent.", ct); await health.RecordSendFailureAsync(accountId, "gateway unavailable", ct); return; }
                var delay = ex.Kind == GatewayFailureKind.RateLimited ? TimeSpan.FromMinutes(2) : SendRetryPolicy.Backoff(attempts);
                await db.Messages.Where(m => m.Id == id && m.TenantId == tenantId && m.Status == MessageStatus.Sending)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MessageStatus.Queued).SetProperty(m => m.NextAttemptAtUtc, now + delay).SetProperty(m => m.FailureMessage, "Gateway unavailable; retrying.").SetProperty(m => m.Version, m => m.Version + 1), ct);
                jobs.EnqueueSend(tenantId, id, delay);   // nothing was sent, so repeating the call cannot duplicate
                return;
            case GatewayFailureKind.NewContactRestricted:
                await FailAsync(id, "new_contacts_blocked", ex.Message, ct);
                await health.ApplySessionStatusAsync(accountId, new AccountStatusPayload(Guid.Empty, accountId, nameof(GatewaySessionStatus.Working), null, null, true, now.AddHours(24), "reachout_timelock"), ct);
                return;
            case GatewayFailureKind.Unauthorized or GatewayFailureKind.NotFound:
                await FailAsync(id, ex.Kind == GatewayFailureKind.Unauthorized ? "gateway_unauthorized" : "session_not_found", ex.Message, ct);
                await health.RecordSendFailureAsync(accountId, ex.Message, ct);
                return;
            default:
                await FailAsync(id, "rejected", ex.Message, ct);
                return;
        }
    }

    private async Task RecordSentAsync(Guid messageId, string externalId, DateTime sentAt, string source, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        db.ChangeTracker.Clear();
        var m = await db.Messages.FirstAsync(x => x.Id == messageId && x.TenantId == tenantId, ct);
        if (m.Status is MessageStatus.Sending or MessageStatus.Unknown or MessageStatus.Queued) m.MarkSent(externalId, sentAt);
        db.MessageStatusEvents.Add(MessageStatusEvent.Create(tenantId, messageId, MessageStatus.Sent, source, 1, true, sentAt, now));
        crm.Record("Message.Sent", "Message", messageId, $"Message accepted by the gateway ({source}).");
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (TenancyResultFlow.IsDuplicate(ex))
        {
            // the gateway id is already attached to another row of this account: keep this message Unknown for a person instead of double-linking
            db.ChangeTracker.Clear();
            await MarkUnknownAsync(messageId, "The gateway id was already linked to another message.", ct);
            return;
        }
        await NotifyCampaignAsync(m.CampaignRecipientId, messageId, ct);
    }

    /// <summary>Campaign messages report their outcome to campaign tracking (recipient status, usage ledger, rollup event). Best effort: the campaign tick repairs anything missed.</summary>
    private async Task NotifyCampaignAsync(Guid? recipientId, Guid messageId, CancellationToken ct)
    {
        if (recipientId is null) return;
        try { await campaignEvents.OnOutboundChangedAsync(messageId, ct: ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Campaign tracking of message {MessageId} failed; the campaign tick will repair it", messageId); }
    }

    private async Task ApplyDeliveryAsync(Guid messageId, MessageStatus status, DateTime at, int? ack, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        db.ChangeTracker.Clear();
        var m = await db.Messages.FirstAsync(x => x.Id == messageId && x.TenantId == tenantId, ct);
        var applied = m.ApplyDelivery(status, at);
        db.MessageStatusEvents.Add(MessageStatusEvent.Create(tenantId, messageId, status, "reconcile", ack, applied, at, crm.Now));
        await db.SaveChangesAsync(ct);
        await NotifyCampaignAsync(m.CampaignRecipientId, messageId, ct);
    }

    private async Task MarkUnknownAsync(Guid messageId, string reason, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var r = reason.Length > 500 ? reason[..500] : reason;
        await db.Messages.Where(m => m.Id == messageId && m.TenantId == tenantId && (m.Status == MessageStatus.Sending || m.Status == MessageStatus.Queued))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MessageStatus.Unknown).SetProperty(m => m.FailureCode, "outcome_unknown").SetProperty(m => m.FailureMessage, r).SetProperty(m => m.Version, m => m.Version + 1), ct);
        db.ChangeTracker.Clear();
        db.MessageStatusEvents.Add(MessageStatusEvent.Create(tenantId, messageId, MessageStatus.Unknown, "send", null, true, now, now));
        crm.Record("Message.OutcomeUnknown", "Message", messageId, "The gateway did not confirm whether the message was sent; it will be reconciled and is never re-sent automatically.");
        await db.SaveChangesAsync(ct);
    }

    private async Task FailAsync(Guid messageId, string code, string reason, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        db.ChangeTracker.Clear();
        var m = await db.Messages.FirstOrDefaultAsync(x => x.Id == messageId && x.TenantId == tenantId, ct);
        if (m is null || m.Status is MessageStatus.Sent or MessageStatus.Delivered or MessageStatus.Read or MessageStatus.Failed) return;
        m.MarkFailed(code, reason, now);
        db.MessageStatusEvents.Add(MessageStatusEvent.Create(tenantId, messageId, MessageStatus.Failed, "send", null, true, now, now));
        crm.Record("Message.Failed", "Message", messageId, $"Message not sent ({code}): {reason}");
        await db.SaveChangesAsync(ct);
        await NotifyCampaignAsync(m.CampaignRecipientId, messageId, ct);
    }
}
