using System.Text.RegularExpressions;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;

namespace LeadPilot.Domain.Messaging;

/// <summary>Catalog entry: one messaging channel (WhatsApp via WAHA) enabled for a tenant. Created with the tenant's first account.</summary>
public class Channel : AuditableEntity, ITenantOwned
{
    private Channel() { }
    public Guid TenantId { get; private set; }
    public ChannelType Type { get; private set; }
    public ChannelProvider Provider { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public bool IsEnabled { get; private set; } = true;

    public static Channel Create(Guid tenantId, ChannelType type, ChannelProvider provider, string name, DateTime nowUtc) =>
        new() { TenantId = tenantId, Type = type, Provider = provider, Name = Guard.Required(name, 100, "Channel name"), CreatedAtUtc = nowUtc };
}

/// <summary>
/// One WhatsApp number = one WAHA session. <see cref="GatewayAccountId"/> (the WAHA session name) is unique across ALL tenants, and the secret
/// <see cref="WebhookKey"/> in the webhook URL is what resolves a webhook to its tenant; both are re-checked against the signed payload.
/// </summary>
public class ChannelAccount : AuditableEntity, ITenantOwned
{
    public const int HardMaxHourlyCap = 120;
    public const int HardMaxDailyCap = 1000;
    public const int HardMinDelaySeconds = 10;
    public const int DefaultHourlyCap = 30;
    public const int DefaultDailyCap = 150;
    public const int DefaultMinDelaySeconds = 30;
    private static readonly Regex SessionName = new(@"^[A-Za-z0-9][A-Za-z0-9_\-]{2,62}$", RegexOptions.Compiled);

    private ChannelAccount() { }
    public Guid TenantId { get; private set; }
    public Guid ChannelId { get; private set; }
    public string DisplayName { get; private set; } = string.Empty;
    /// <summary>WAHA session name. Globally unique (database-enforced).</summary>
    public string GatewayAccountId { get; private set; } = string.Empty;
    /// <summary>Random, unguessable key embedded in the webhook URL. Globally unique.</summary>
    public string WebhookKey { get; private set; } = string.Empty;
    public ConnectionStatus ConnectionStatus { get; private set; }
    public DateTime? ConnectionStatusAtUtc { get; private set; }
    /// <summary>WhatsApp number of the session, E.164, when the gateway reports it.</summary>
    public string? PhoneE164 { get; private set; }
    public string? PushName { get; private set; }
    public string? Engine { get; private set; }

    public bool IsPaused { get; private set; }
    public bool PausedAutomatically { get; private set; }
    public string? PauseReason { get; private set; }
    public DateTime? PausedAtUtc { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    /// <summary>While set in the future, messages to contacts without an existing conversation are refused (WhatsApp reachout timelock / message capping).</summary>
    public DateTime? NewContactBlockedUntilUtc { get; private set; }

    public int HourlyCap { get; private set; } = DefaultHourlyCap;
    public int DailyCap { get; private set; } = DefaultDailyCap;
    public int MinDelaySeconds { get; private set; } = DefaultMinDelaySeconds;
    /// <summary>Start of warm-up: the first time the session was seen WORKING. Day 1 allows 20 messages, then +20 per day until the daily cap.</summary>
    public DateTime? WarmupStartedAtUtc { get; private set; }
    public DateTime? LastSendAtUtc { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }

    public static bool IsValidSessionName(string? name) => name is not null && SessionName.IsMatch(name);

    public static ChannelAccount Create(Guid tenantId, Guid channelId, string displayName, string gatewayAccountId, string? actor, DateTime nowUtc)
    {
        if (!IsValidSessionName(gatewayAccountId)) throw new DomainException("The WAHA session name must be 3–63 characters: letters, digits, '-' and '_' (starting with a letter or digit).");
        return new ChannelAccount
        {
            TenantId = tenantId, ChannelId = channelId, DisplayName = Guard.Required(displayName, 100, "Account name"), GatewayAccountId = gatewayAccountId,
            WebhookKey = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)),
            CreatedAtUtc = nowUtc, CreatedBy = actor
        };
    }

    public void Rename(string displayName, DateTime nowUtc) { DisplayName = Guard.Required(displayName, 100, "Account name"); UpdatedAtUtc = nowUtc; }

    public void SetLimits(int hourly, int daily, int minDelaySeconds, DateTime nowUtc)
    {
        if (hourly is < 1 or > HardMaxHourlyCap) throw new DomainException($"The hourly cap must be between 1 and {HardMaxHourlyCap}.");
        if (daily is < 1 or > HardMaxDailyCap) throw new DomainException($"The daily cap must be between 1 and {HardMaxDailyCap}.");
        if (daily < hourly) throw new DomainException("The daily cap cannot be lower than the hourly cap.");
        if (minDelaySeconds is < HardMinDelaySeconds or > 3600) throw new DomainException($"The minimum delay between messages must be between {HardMinDelaySeconds} and 3600 seconds.");
        HourlyCap = hourly; DailyCap = daily; MinDelaySeconds = minDelaySeconds; UpdatedAtUtc = nowUtc;
    }

    /// <summary>Applies a gateway status. Returns true when the stored status changed.</summary>
    public bool ApplyConnection(ConnectionStatus status, string? phoneE164, string? pushName, string? engine, DateTime nowUtc)
    {
        var changed = ConnectionStatus != status;
        ConnectionStatus = status; ConnectionStatusAtUtc = nowUtc;
        if (phoneE164 is not null) PhoneE164 = phoneE164;
        if (pushName is not null) PushName = Opt(pushName, 200);
        if (engine is not null) Engine = Opt(engine, 30);
        if (status == ConnectionStatus.Working && WarmupStartedAtUtc is null) WarmupStartedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        return changed;
    }

    public void Pause(string reason, bool automatic, DateTime nowUtc)
    {
        IsPaused = true; PausedAutomatically = automatic; PauseReason = Opt(reason, 300); PausedAtUtc = nowUtc; UpdatedAtUtc = nowUtc;
    }

    public void Resume(DateTime nowUtc)
    {
        IsPaused = false; PausedAutomatically = false; PauseReason = null; PausedAtUtc = null; ConsecutiveFailures = 0; UpdatedAtUtc = nowUtc;
    }

    public int RegisterFailure(DateTime nowUtc) { ConsecutiveFailures++; UpdatedAtUtc = nowUtc; return ConsecutiveFailures; }
    public void RegisterSuccess(DateTime nowUtc) { if (ConsecutiveFailures != 0) ConsecutiveFailures = 0; LastSendAtUtc = nowUtc; UpdatedAtUtc = nowUtc; }
    public void BlockNewContacts(DateTime? untilUtc, DateTime nowUtc) { NewContactBlockedUntilUtc = untilUtc; UpdatedAtUtc = nowUtc; }
    public void Remove(DateTime nowUtc) { IsDeleted = true; DeletedAtUtc = nowUtc; UpdatedAtUtc = nowUtc; IsPaused = true; PauseReason = "Account removed"; }

    private static string? Opt(string? v, int max) { var t = v?.Trim(); return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t; }
}

/// <summary>Encrypted gateway credentials of one account (ISecretProtector, per-tenant data-protection payload). Never returned to clients.</summary>
public class ChannelCredential : AuditableEntity, ITenantOwned
{
    private ChannelCredential() { }
    public Guid TenantId { get; private set; }
    public Guid ChannelAccountId { get; private set; }
    public string ProtectedBaseUrl { get; private set; } = string.Empty;
    public string ProtectedApiKey { get; private set; } = string.Empty;
    public string ProtectedWebhookSecret { get; private set; } = string.Empty;
    /// <summary>Last characters of the API key for display ("…a1b2").</summary>
    public string KeyHint { get; private set; } = string.Empty;

    public static ChannelCredential Create(Guid tenantId, Guid accountId, string protectedBaseUrl, string protectedApiKey, string protectedWebhookSecret, string keyHint, DateTime nowUtc) =>
        new() { TenantId = tenantId, ChannelAccountId = accountId, ProtectedBaseUrl = protectedBaseUrl, ProtectedApiKey = protectedApiKey, ProtectedWebhookSecret = protectedWebhookSecret, KeyHint = keyHint, CreatedAtUtc = nowUtc };

    public void Replace(string protectedBaseUrl, string protectedApiKey, string protectedWebhookSecret, string keyHint, DateTime nowUtc)
    {
        ProtectedBaseUrl = protectedBaseUrl; ProtectedApiKey = protectedApiKey; ProtectedWebhookSecret = protectedWebhookSecret; KeyHint = keyHint; UpdatedAtUtc = nowUtc;
    }
}

/// <summary>A chat with one WhatsApp number on one account. Separate from Lead, Contact and Customer; it only points at a Contact.</summary>
public class Conversation : AuditableEntity, ITenantOwned
{
    private Conversation() { }
    public Guid TenantId { get; private set; }
    public Guid ChannelAccountId { get; private set; }
    /// <summary>Gateway chat id of the peer (provider specific text). Unique per account.</summary>
    public string ExternalChatId { get; private set; } = string.Empty;
    public string? PeerPhoneE164 { get; private set; }
    public string? PeerName { get; private set; }
    public Guid? ContactId { get; private set; }
    public ConversationStatus Status { get; private set; } = ConversationStatus.Open;
    public AiMode AiMode { get; private set; } = AiMode.Paused;
    public Guid? AssignedUserId { get; private set; }
    public DateTime? LastMessageAtUtc { get; private set; }
    public DateTime? LastInboundAtUtc { get; private set; }
    public DateTime? LastOutboundAtUtc { get; private set; }
    public string? LastMessagePreview { get; private set; }
    public int UnreadCount { get; private set; }
    /// <summary>Set when the peer sent an opt-out message. Blocks every outbound message to this chat (needed for hidden @lid senders whose number cannot be suppressed).</summary>
    public DateTime? OptedOutAtUtc { get; private set; }
    /// <summary>Why the conversation was handed to a person (set by an escalation or a takeover, cleared when the AI is resumed).</summary>
    public string? EscalationReason { get; private set; }
    public DateTime? EscalatedAtUtc { get; private set; }
    public DateTime? AiModeChangedAtUtc { get; private set; }
    /// <summary>Incremented on EVERY change of <see cref="AiMode"/>. An AI generation remembers the value it started with; if it changed, a person or a rule intervened and the AI must not send.</summary>
    public int AiEpoch { get; private set; }

    public static Conversation Create(Guid tenantId, Guid accountId, string externalChatId, string? peerPhoneE164, string? peerName, DateTime nowUtc) =>
        new() { TenantId = tenantId, ChannelAccountId = accountId, ExternalChatId = Guard.Required(externalChatId, 100, "Chat id"), PeerPhoneE164 = peerPhoneE164, PeerName = Trim(peerName, 200), CreatedAtUtc = nowUtc };

    public void LinkContact(Guid contactId, DateTime nowUtc) { ContactId = contactId; UpdatedAtUtc = nowUtc; }
    public void SetPeerPhone(string e164, DateTime nowUtc) { PeerPhoneE164 ??= e164; UpdatedAtUtc = nowUtc; }
    public void RegisterOptOut(DateTime nowUtc) { OptedOutAtUtc ??= nowUtc; UpdatedAtUtc = nowUtc; }
    public void SetPeerName(string? name, DateTime nowUtc) { if (Trim(name, 200) is { } n && n != PeerName) { PeerName = n; UpdatedAtUtc = nowUtc; } }

    public void RegisterInbound(DateTime messageAtUtc, string preview, DateTime nowUtc)
    {
        if (LastInboundAtUtc is null || messageAtUtc > LastInboundAtUtc) LastInboundAtUtc = messageAtUtc;
        if (LastMessageAtUtc is null || messageAtUtc >= LastMessageAtUtc) { LastMessageAtUtc = messageAtUtc; LastMessagePreview = Trim(preview, 200); }
        UnreadCount++;
        Status = ConversationStatus.Open;
        UpdatedAtUtc = nowUtc;
    }

    public void RegisterOutbound(DateTime messageAtUtc, string preview, DateTime nowUtc)
    {
        if (LastOutboundAtUtc is null || messageAtUtc > LastOutboundAtUtc) LastOutboundAtUtc = messageAtUtc;
        if (LastMessageAtUtc is null || messageAtUtc >= LastMessageAtUtc) { LastMessageAtUtc = messageAtUtc; LastMessagePreview = Trim(preview, 200); }
        UpdatedAtUtc = nowUtc;
    }

    public void MarkRead(DateTime nowUtc) { UnreadCount = 0; UpdatedAtUtc = nowUtc; }
    public void SetStatus(ConversationStatus status, DateTime nowUtc) { Status = status; UpdatedAtUtc = nowUtc; }
    public void SetAiMode(AiMode mode, DateTime nowUtc)
    {
        if (mode != AiMode) { AiEpoch++; AiModeChangedAtUtc = nowUtc; }
        AiMode = mode; UpdatedAtUtc = nowUtc;
        if (mode == AiMode.Active) { EscalationReason = null; EscalatedAtUtc = null; }
    }

    /// <summary>Hands the conversation to a person: the AI stops answering until it is resumed.</summary>
    public void Escalate(string reason, DateTime nowUtc)
    {
        SetAiMode(AiMode.HumanControl, nowUtc);
        EscalationReason = string.IsNullOrWhiteSpace(reason) ? "escalated" : Trim(reason, 200); EscalatedAtUtc = nowUtc;
    }
    public void Assign(Guid? userId, DateTime nowUtc) { AssignedUserId = userId; UpdatedAtUtc = nowUtc; }

    /// <summary>True when the customer wrote within the last <paramref name="window"/>: replies are then service messages.</summary>
    public bool IsInsideServiceWindow(DateTime nowUtc, TimeSpan window) => LastInboundAtUtc is { } t && nowUtc - t <= window;

    private static string? Trim(string? v, int max) { var t = v?.Trim(); return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t; }
}

/// <summary>Append-only history of who a conversation was assigned to.</summary>
public class ConversationAssignment : Entity, ITenantOwned, IAppendOnly
{
    private ConversationAssignment() { }
    public Guid TenantId { get; private set; }
    public Guid ConversationId { get; private set; }
    public Guid? AssignedUserId { get; private set; }
    public Guid? AssignedByUserId { get; private set; }
    public DateTime AssignedAtUtc { get; private set; }

    public static ConversationAssignment Create(Guid tenantId, Guid conversationId, Guid? assignedTo, Guid? by, DateTime nowUtc) =>
        new() { TenantId = tenantId, ConversationId = conversationId, AssignedUserId = assignedTo, AssignedByUserId = by, AssignedAtUtc = nowUtc };
}

/// <summary>One WhatsApp message in a conversation, inbound or outbound.</summary>
public class Message : Entity, ITenantOwned
{
    public const int MaxBodyLength = 4096;
    private Message() { }
    public Guid TenantId { get; private set; }
    public Guid ConversationId { get; private set; }
    public Guid ChannelAccountId { get; private set; }
    public Guid? ContactId { get; private set; }
    public MessageDirection Direction { get; private set; }
    public MessagePurpose? Purpose { get; private set; }
    public MessageStatus Status { get; private set; }
    /// <summary>Gateway message id (WAHA "true_&lt;chat&gt;_&lt;HEX&gt;"). Unique per (tenant, account); null until an outbound message is accepted by the gateway.</summary>
    public string? ExternalMessageId { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public string CorrelationId { get; private set; } = string.Empty;
    /// <summary>Caller-supplied (or generated) key; a second request with the same key returns this message instead of creating another. Outbound only.</summary>
    public string? IdempotencyKey { get; private set; }
    public Guid? TemplateId { get; private set; }
    public Guid? CreatedByUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    /// <summary>Gateway timestamp for inbound messages.</summary>
    public DateTime OccurredAtUtc { get; private set; }
    public int SendAttempts { get; private set; }
    public DateTime? NextAttemptAtUtc { get; private set; }
    public DateTime? SendStartedAtUtc { get; private set; }
    public DateTime? SentAtUtc { get; private set; }
    public DateTime? DeliveredAtUtc { get; private set; }
    public DateTime? ReadAtUtc { get; private set; }
    public DateTime? FailedAtUtc { get; private set; }
    public string? FailureCode { get; private set; }
    public string? FailureMessage { get; private set; }
    public int Version { get; private set; } = 1;
    /// <summary>Set when the message belongs to a campaign. <see cref="CampaignRecipientId"/> is null for a test send (which is never part of a campaign's audience or statistics).</summary>
    public Guid? CampaignId { get; private set; }
    public Guid? CampaignRecipientId { get; private set; }
    /// <summary>Written by the AI agent (never by a person or a campaign). The send runner re-checks that the AI may still speak in the conversation (<see cref="AiEpoch"/>) immediately before sending.</summary>
    public bool IsAiGenerated { get; private set; }
    public Guid? AiInteractionId { get; private set; }
    public int? AiEpoch { get; private set; }

    public static Message Inbound(Guid tenantId, Guid conversationId, Guid accountId, string externalId, string body, DateTime occurredAtUtc, string correlationId, DateTime nowUtc) =>
        new()
        {
            TenantId = tenantId, ConversationId = conversationId, ChannelAccountId = accountId, Direction = MessageDirection.Inbound, Status = MessageStatus.Received,
            ExternalMessageId = Guard.Required(externalId, 200, "Message id"), Body = Truncate(body), CorrelationId = correlationId, CreatedAtUtc = nowUtc, OccurredAtUtc = occurredAtUtc
        };

    public static Message Outbound(Guid tenantId, Guid conversationId, Guid accountId, Guid? contactId, MessagePurpose purpose, string body, string idempotencyKey, string correlationId,
        Guid? templateId, Guid? userId, DateTime nowUtc)
    {
        var text = body?.Trim();
        if (string.IsNullOrEmpty(text)) throw new DomainException("A message cannot be empty.");
        if (text.Length > MaxBodyLength) throw new DomainException($"A message can have at most {MaxBodyLength} characters.");
        return new Message
        {
            TenantId = tenantId, ConversationId = conversationId, ChannelAccountId = accountId, ContactId = contactId, Direction = MessageDirection.Outbound, Purpose = purpose,
            Status = MessageStatus.Queued, Body = text, IdempotencyKey = Guard.Required(idempotencyKey, 100, "Idempotency key"), CorrelationId = correlationId, TemplateId = templateId,
            CreatedByUserId = userId, CreatedAtUtc = nowUtc, OccurredAtUtc = nowUtc, NextAttemptAtUtc = nowUtc
        };
    }

    private static string Truncate(string? body) => (body ?? string.Empty).Length > MaxBodyLength ? body![..MaxBodyLength] : body ?? string.Empty;

    public void SetContact(Guid contactId) => ContactId = contactId;
    public void MarkAiGenerated(Guid interactionId, int epoch) { IsAiGenerated = true; AiInteractionId = interactionId; AiEpoch = epoch; }
    public void SetCampaign(Guid campaignId, Guid? recipientId) { CampaignId = campaignId; CampaignRecipientId = recipientId; }

    // ---- outbound lifecycle (the send runner performs the atomic Queued → Sending claim in SQL; these mirror it for tracked entities) ----
    public void MarkSent(string externalId, DateTime nowUtc)
    {
        ExternalMessageId = Guard.Required(externalId, 200, "Message id"); Status = MessageStatus.Sent; SentAtUtc ??= nowUtc; NextAttemptAtUtc = null; FailureCode = null; FailureMessage = null; Version++;
    }

    public void MarkUnknown(string reason, DateTime nowUtc) { Status = MessageStatus.Unknown; FailureCode = "outcome_unknown"; FailureMessage = Cut(reason, 500); NextAttemptAtUtc = null; FailedAtUtc = null; Version++; _ = nowUtc; }

    public void MarkFailed(string code, string message, DateTime nowUtc)
    {
        Status = MessageStatus.Failed; FailureCode = Cut(code, 50); FailureMessage = Cut(message, 500); FailedAtUtc = nowUtc; NextAttemptAtUtc = null; Version++;
    }

    public void Requeue(DateTime nextAttemptUtc, string? note) { Status = MessageStatus.Queued; NextAttemptAtUtc = nextAttemptUtc; FailureMessage = Cut(note, 500); Version++; }

    /// <summary>Applies a delivery status monotonically (Sent &lt; Delivered &lt; Read). Returns true when the status moved forward.</summary>
    public bool ApplyDelivery(MessageStatus status, DateTime atUtc)
    {
        if (Direction != MessageDirection.Outbound || status is not (MessageStatus.Sent or MessageStatus.Delivered or MessageStatus.Read)) return false;
        if (Status is MessageStatus.Queued or MessageStatus.Sending or MessageStatus.Failed) return false; // acks only make sense for messages the gateway accepted
        if (Rank(status) <= Rank(Status)) { StampTime(status, atUtc); return false; }
        Status = status; StampTime(status, atUtc); Version++;
        return true;
    }

    private void StampTime(MessageStatus status, DateTime atUtc)
    {
        switch (status)
        {
            case MessageStatus.Sent: SentAtUtc ??= atUtc; break;
            case MessageStatus.Delivered: DeliveredAtUtc ??= atUtc; SentAtUtc ??= atUtc; break;
            case MessageStatus.Read: ReadAtUtc ??= atUtc; DeliveredAtUtc ??= atUtc; SentAtUtc ??= atUtc; break;
        }
    }

    /// <summary>Position in the delivery progression; Unknown counts as "gateway acceptance not confirmed yet" (below Sent).</summary>
    public static int Rank(MessageStatus s) => s switch { MessageStatus.Sent => 1, MessageStatus.Delivered => 2, MessageStatus.Read => 3, _ => 0 };

    private static string? Cut(string? v, int max) => v is null ? null : v.Length > max ? v[..max] : v;
}

/// <summary>Metadata of a file that came with a message. Content is NOT downloaded in Phase 5; <see cref="StorageKey"/> stays empty until a later phase stores it through ITenantFileStore.</summary>
public class MessageAttachment : Entity, ITenantOwned
{
    public const long MaxSizeBytes = 16 * 1024 * 1024;
    public static readonly string[] AllowedMimePrefixes = ["image/", "audio/", "video/", "application/pdf", "text/plain", "application/vnd.openxmlformats-officedocument", "application/msword", "application/vnd.ms-"];
    private MessageAttachment() { }
    public Guid TenantId { get; private set; }
    public Guid MessageId { get; private set; }
    public string? FileName { get; private set; }
    public string MimeType { get; private set; } = string.Empty;
    public long? SizeBytes { get; private set; }
    public AttachmentStatus Status { get; private set; }
    public string? RejectionReason { get; private set; }
    public string? StorageKey { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static bool IsAllowedMime(string? mime) => !string.IsNullOrWhiteSpace(mime) && AllowedMimePrefixes.Any(p => mime.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Records the metadata and applies the size/type policy (never throws for hostile input: a rejected attachment is stored as Rejected).</summary>
    public static MessageAttachment Describe(Guid tenantId, Guid messageId, string? fileName, string? mime, long? size, DateTime nowUtc)
    {
        var safeName = string.IsNullOrWhiteSpace(fileName) ? null : Path.GetFileName(fileName.Replace('\\', '/').Trim());
        if (safeName is { Length: > 200 }) safeName = safeName[..200];
        var m = string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime.Trim();
        if (m.Length > 100) m = m[..100];
        var a = new MessageAttachment { TenantId = tenantId, MessageId = messageId, FileName = safeName, MimeType = m, SizeBytes = size, CreatedAtUtc = nowUtc, Status = AttachmentStatus.NotDownloaded };
        if (!IsAllowedMime(m)) { a.Status = AttachmentStatus.Rejected; a.RejectionReason = "File type is not allowed."; }
        else if (size is > MaxSizeBytes) { a.Status = AttachmentStatus.Rejected; a.RejectionReason = "File is larger than the 16 MB limit."; }
        return a;
    }
}

/// <summary>Append-only record of every status a message went through (send result, acks, reconciliation).</summary>
public class MessageStatusEvent : Entity, ITenantOwned, IAppendOnly
{
    private MessageStatusEvent() { }
    public Guid TenantId { get; private set; }
    public Guid MessageId { get; private set; }
    public MessageStatus Status { get; private set; }
    /// <summary>"send", "ack", "reconcile" …</summary>
    public string Source { get; private set; } = string.Empty;
    public int? GatewayAck { get; private set; }
    /// <summary>False when the event arrived too late to change the message (e.g. DEVICE after READ).</summary>
    public bool Applied { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
    public DateTime RecordedAtUtc { get; private set; }

    public static MessageStatusEvent Create(Guid tenantId, Guid messageId, MessageStatus status, string source, int? gatewayAck, bool applied, DateTime occurredAtUtc, DateTime nowUtc) =>
        new() { TenantId = tenantId, MessageId = messageId, Status = status, Source = Guard.Required(source, 30, "Source"), GatewayAck = gatewayAck, Applied = applied, OccurredAtUtc = occurredAtUtc, RecordedAtUtc = nowUtc };
}

/// <summary>The raw webhook as received (after signature verification), kept for the retention period. Unique per (account, external event id).</summary>
public class WebhookEvent : Entity, ITenantOwned
{
    private WebhookEvent() { }
    public Guid TenantId { get; private set; }
    public Guid ChannelAccountId { get; private set; }
    /// <summary>WAHA event id ("evt_…") or, when absent, the SHA-256 of the raw body (identical redeliveries hash identically).</summary>
    public string ExternalEventId { get; private set; } = string.Empty;
    public string EventType { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public WebhookEventStatus Status { get; private set; } = WebhookEventStatus.Received;
    public string? Note { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public string? SourceIp { get; private set; }
    public DateTime ReceivedAtUtc { get; private set; }
    public DateTime? ProcessedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }

    public static WebhookEvent Create(Guid tenantId, Guid accountId, string externalEventId, string eventType, string payloadJson, string correlationId, string? ip, DateTime nowUtc, TimeSpan retention) =>
        new()
        {
            TenantId = tenantId, ChannelAccountId = accountId, ExternalEventId = Guard.Required(externalEventId, 128, "Event id"), EventType = Guard.Required(eventType, 60, "Event type"),
            PayloadJson = payloadJson, CorrelationId = correlationId, SourceIp = ip is { Length: > 64 } ? ip[..64] : ip, ReceivedAtUtc = nowUtc, ExpiresAtUtc = nowUtc + retention
        };

    public void MarkProcessed(DateTime nowUtc, string? note = null) { Status = WebhookEventStatus.Processed; ProcessedAtUtc = nowUtc; Note = Cut(note); }
    public void MarkIgnored(string note, DateTime nowUtc) { Status = WebhookEventStatus.Ignored; ProcessedAtUtc = nowUtc; Note = Cut(note); }
    public void MarkFailed(string note, DateTime nowUtc) { Status = WebhookEventStatus.Failed; ProcessedAtUtc = nowUtc; Note = Cut(note); }
    /// <summary>Retention purge: drops the payload but keeps the row (and its uniqueness) so a late redelivery is still recognised as a duplicate.</summary>
    public void PurgePayload() { PayloadJson = "{}"; }
    private static string? Cut(string? v) => v is null ? null : v.Length > 500 ? v[..500] : v;
}

/// <summary>Tenant-managed text template. WAHA has no official templates: it is rendered locally ({{name}} placeholders) and sent as plain text.</summary>
public class MessageTemplate : AuditableEntity, ITenantOwned
{
    private MessageTemplate() { }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public TemplateCategory Category { get; private set; }
    public bool IsActive { get; private set; } = true;

    public static MessageTemplate Create(Guid tenantId, string name, string body, TemplateCategory category, string? actor, DateTime nowUtc)
    {
        var t = new MessageTemplate { TenantId = tenantId, CreatedAtUtc = nowUtc, CreatedBy = actor };
        t.Set(name, body, category, nowUtc, touch: false);
        return t;
    }

    public void Update(string name, string body, TemplateCategory category, string? actor, DateTime nowUtc) { Set(name, body, category, nowUtc, touch: true); UpdatedBy = actor; }
    public void SetActive(bool active, DateTime nowUtc) { IsActive = active; UpdatedAtUtc = nowUtc; }

    private void Set(string name, string body, TemplateCategory category, DateTime nowUtc, bool touch)
    {
        Name = Guard.Required(name, 100, "Template name");
        var b = body?.Trim();
        if (string.IsNullOrEmpty(b)) throw new DomainException("The template text is required.");
        if (b.Length > Message.MaxBodyLength) throw new DomainException($"A template can have at most {Message.MaxBodyLength} characters.");
        Body = b; Category = category;
        if (touch) UpdatedAtUtc = nowUtc;
    }
}

/// <summary>Transactional outbox row: written in the same transaction as the business change, published to RabbitMQ by the dispatcher job.</summary>
public class OutboxMessage : Entity, ITenantOwned
{
    private OutboxMessage() { }
    public Guid TenantId { get; private set; }
    /// <summary>Event type and routing key, e.g. "message.received".</summary>
    public string Type { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public string CorrelationId { get; private set; } = string.Empty;
    public string? CausationId { get; private set; }
    public OutboxStatus Status { get; private set; } = OutboxStatus.Pending;
    public int Attempts { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime NextAttemptAtUtc { get; private set; }
    public DateTime? PublishedAtUtc { get; private set; }
    public string? LastError { get; private set; }

    public static OutboxMessage Create(Guid tenantId, string type, string payloadJson, string correlationId, string? causationId, DateTime nowUtc) =>
        new() { TenantId = tenantId, Type = Guard.Required(type, 60, "Event type"), PayloadJson = payloadJson, CorrelationId = correlationId, CausationId = causationId, CreatedAtUtc = nowUtc, NextAttemptAtUtc = nowUtc };

    public void MarkPublished(DateTime nowUtc) { Status = OutboxStatus.Published; PublishedAtUtc = nowUtc; LastError = null; }

    /// <summary>Bounded exponential backoff (5 s · 2^n, max 15 min); after <paramref name="maxAttempts"/> the row is parked as Failed for an operator.</summary>
    public void MarkAttemptFailed(string error, int maxAttempts, DateTime nowUtc)
    {
        Attempts++; LastError = error.Length > 500 ? error[..500] : error;
        if (Attempts >= maxAttempts) { Status = OutboxStatus.Failed; return; }
        NextAttemptAtUtc = nowUtc + BackoffFor(Attempts);
    }

    public static TimeSpan BackoffFor(int attempts) => TimeSpan.FromSeconds(Math.Min(900, 5 * Math.Pow(2, Math.Max(0, attempts - 1))));
}

/// <summary>Consumer idempotency record: a (consumer, message id) pair is processed at most once.</summary>
public class ProcessedMessage : Entity, ITenantOwned
{
    private ProcessedMessage() { }
    public Guid TenantId { get; private set; }
    public string ConsumerName { get; private set; } = string.Empty;
    public Guid MessageId { get; private set; }
    public DateTime ProcessedAtUtc { get; private set; }

    public static ProcessedMessage Create(Guid tenantId, string consumer, Guid messageId, DateTime nowUtc) =>
        new() { TenantId = tenantId, ConsumerName = Guard.Required(consumer, 100, "Consumer"), MessageId = messageId, ProcessedAtUtc = nowUtc };
}

/// <summary>Notice for the account page: auto-pause, disconnect, WhatsApp restrictions.</summary>
public class ChannelAccountNotification : Entity, ITenantOwned
{
    private ChannelAccountNotification() { }
    public Guid TenantId { get; private set; }
    public Guid ChannelAccountId { get; private set; }
    public NotificationKind Kind { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? AcknowledgedAtUtc { get; private set; }

    public static ChannelAccountNotification Create(Guid tenantId, Guid accountId, NotificationKind kind, string message, DateTime nowUtc) =>
        new() { TenantId = tenantId, ChannelAccountId = accountId, Kind = kind, Message = message.Length > 500 ? message[..500] : message, CreatedAtUtc = nowUtc };

    public void Acknowledge(DateTime nowUtc) { AcknowledgedAtUtc ??= nowUtc; }
}
