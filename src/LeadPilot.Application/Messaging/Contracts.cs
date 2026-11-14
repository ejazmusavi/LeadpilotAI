using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Shared.Results;

namespace LeadPilot.Application.Messaging;

// ---------- DTOs ----------

public sealed record ChannelAccountDto(
    Guid Id, string DisplayName, string GatewayAccountId, ConnectionStatus Status, DateTime? StatusAtUtc, string? PhoneE164, string? PushName, string? Engine,
    bool IsPaused, bool PausedAutomatically, string? PauseReason, int ConsecutiveFailures, int HourlyCap, int DailyCap, int MinDelaySeconds, int DailyLimitToday,
    int SentLastHour, int SentLast24Hours, DateTime? NewContactBlockedUntilUtc, DateTime CreatedAtUtc, string BaseUrlHost, string KeyHint, int UnacknowledgedNotifications);

/// <summary>Setup data only for people who may manage channels: the webhook address WAHA must call (contains the account's secret key).</summary>
public sealed record ChannelAccountSetupDto(ChannelAccountDto Account, string WebhookPath, IReadOnlyList<string> Events, bool AckTrackingRequiresWebjsFlag);

public sealed record AccountNotificationDto(Guid Id, NotificationKind Kind, string Message, DateTime CreatedAtUtc, DateTime? AcknowledgedAtUtc);

public sealed record ConnectionTestResult(bool Ok, string Message, bool SessionExists, GatewaySessionStatus? SessionStatus);

public sealed record QrCodeDto(string? RawValue, string? PngBase64, GatewaySessionStatus Status);

public sealed record CreateChannelAccountRequest(string DisplayName, string BaseUrl, string ApiKey, string SessionName, string? WebhookSecret, int? HourlyCap = null, int? DailyCap = null, int? MinDelaySeconds = null) : TenantScopedRequest;
public sealed record TestConnectionRequest(string BaseUrl, string ApiKey, string? SessionName) : TenantScopedRequest;
public sealed record UpdateChannelAccountRequest(string DisplayName, int HourlyCap, int DailyCap, int MinDelaySeconds) : TenantScopedRequest;
public sealed record RotateCredentialsRequest(string BaseUrl, string ApiKey, string? WebhookSecret) : TenantScopedRequest;

public sealed record ConversationQuery
{
    public Guid? AccountId { get; init; }
    public ConversationStatus? Status { get; init; }
    public bool UnreadOnly { get; init; }
    public bool LinkedOnly { get; init; }
    public bool UnlinkedOnly { get; init; }
    public Guid? AssignedUserId { get; init; }
    public Guid? ContactId { get; init; }
    public string? Search { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}

public sealed record ConversationListItemDto(
    Guid Id, Guid ChannelAccountId, string AccountName, string? PeerPhoneE164, string? PeerName, Guid? ContactId, string? ContactName, Guid? LeadId, string? LeadName,
    ConversationStatus Status, AiMode AiMode, Guid? AssignedUserId, string? AssignedUserName, DateTime? LastMessageAtUtc, string? LastMessagePreview, int UnreadCount);

public sealed record ConversationDto(
    ConversationListItemDto Summary, DateTime? LastInboundAtUtc, DateTime? LastOutboundAtUtc, bool CanReply, string? CannotReplyReason, MessagePurpose ReplyPurpose, int ServiceWindowHours, bool InsideServiceWindow);

public sealed record AttachmentDto(Guid Id, string? FileName, string MimeType, long? SizeBytes, AttachmentStatus Status, string? RejectionReason);

public sealed record MessageDto(
    Guid Id, MessageDirection Direction, MessagePurpose? Purpose, MessageStatus Status, string Body, DateTime OccurredAtUtc, DateTime? SentAtUtc, DateTime? DeliveredAtUtc, DateTime? ReadAtUtc,
    string? FailureCode, string? FailureMessage, string? ExternalMessageId, string CorrelationId, string? CreatedByName, IReadOnlyList<AttachmentDto> Attachments);

public sealed record ReplyRequest(string? Text, Guid? TemplateId = null, string? IdempotencyKey = null) : TenantScopedRequest;

/// <summary>Send a message to a contact on a connected account (Messages.Send). Outside a customer-initiated conversation this is marketing and needs consent.</summary>
public sealed record SendMessageRequest(Guid ContactId, Guid ChannelAccountId, string? Text, Guid? TemplateId = null, string? IdempotencyKey = null) : TenantScopedRequest;

public sealed record SetAiModeRequest(AiMode Mode) : TenantScopedRequest;
public sealed record AssignConversationRequest(Guid? UserId) : TenantScopedRequest;

public sealed record SaveTemplateRequest(string Name, string Body, TemplateCategory Category) : TenantScopedRequest;
public sealed record TemplateDto(Guid Id, string Name, string Body, TemplateCategory Category, bool IsActive, IReadOnlyList<string> Variables, DateTime CreatedAtUtc);
public sealed record TemplatePreviewDto(string Rendered, IReadOnlyList<string> Variables);

public sealed record WebhookEventQuery
{
    public Guid? AccountId { get; init; }
    public WebhookEventStatus? Status { get; init; }
    public string? EventType { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}

public sealed record WebhookEventDto(Guid Id, Guid ChannelAccountId, string AccountName, string ExternalEventId, string EventType, WebhookEventStatus Status, string? Note, string CorrelationId, DateTime ReceivedAtUtc, DateTime? ProcessedAtUtc);
public sealed record WebhookEventStatsDto(int Received, int Processed, int Ignored, int Failed, int PendingOutbox, int FailedOutbox);

public sealed record DeadLetterQueueDto(string Queue, string Source, long Messages, bool Available, string? Note);
public sealed record DeadLetterMessageDto(string Queue, string? TenantId, string? MessageId, string? Type, string? Reason, string? CorrelationId, int Attempts, DateTime? DeadLetteredAtUtc);

public sealed record MessagingStatusDto(bool BrokerConfigured, bool BrokerReachable, string? BrokerNote, int PendingOutbox, int FailedOutbox);

// ---------- Service interfaces (Web and Api call the same implementations) ----------

public interface IChannelAccountService
{
    Task<IReadOnlyList<ChannelAccountDto>> ListAsync(CancellationToken ct = default);
    Task<Result<ChannelAccountDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<ChannelAccountSetupDto>> GetSetupAsync(Guid id, CancellationToken ct = default);
    Task<ConnectionTestResult> TestConnectionAsync(TestConnectionRequest request, CancellationToken ct = default);
    Task<Result<ChannelAccountDto>> CreateAsync(CreateChannelAccountRequest request, CancellationToken ct = default);
    Task<Result<ChannelAccountDto>> UpdateAsync(Guid id, UpdateChannelAccountRequest request, CancellationToken ct = default);
    Task<Result<ChannelAccountDto>> RotateCredentialsAsync(Guid id, RotateCredentialsRequest request, CancellationToken ct = default);
    /// <summary>Creates/starts the WAHA session with our webhook registered. Idempotent.</summary>
    Task<Result<ChannelAccountDto>> ConnectAsync(Guid id, CancellationToken ct = default);
    Task<Result<ChannelAccountDto>> RefreshStatusAsync(Guid id, CancellationToken ct = default);
    Task<Result<QrCodeDto>> GetQrAsync(Guid id, CancellationToken ct = default);
    Task<Result<ChannelAccountDto>> DisconnectAsync(Guid id, bool logout, CancellationToken ct = default);
    Task<Result<ChannelAccountDto>> PauseAsync(Guid id, CancellationToken ct = default);
    Task<Result<ChannelAccountDto>> ResumeAsync(Guid id, CancellationToken ct = default);
    Task<Result> RemoveAsync(Guid id, CancellationToken ct = default);
    Task<Result<IReadOnlyList<AccountNotificationDto>>> NotificationsAsync(Guid id, CancellationToken ct = default);
    Task<Result> AcknowledgeNotificationsAsync(Guid id, CancellationToken ct = default);
}

public interface IConversationService
{
    Task<PagedResult<ConversationListItemDto>> ListAsync(ConversationQuery query, CancellationToken ct = default);
    Task<Result<ConversationDto>> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Oldest first within the requested page; page 1 is the newest page.</summary>
    Task<Result<PagedResult<MessageDto>>> MessagesAsync(Guid id, int page, int pageSize, CancellationToken ct = default);
    Task<Result<MessageDto>> ReplyAsync(Guid id, ReplyRequest request, CancellationToken ct = default);
    Task<Result> MarkReadAsync(Guid id, CancellationToken ct = default);
    Task<Result<ConversationDto>> SetStatusAsync(Guid id, ConversationStatus status, CancellationToken ct = default);
    Task<Result<ConversationDto>> SetAiModeAsync(Guid id, AiMode mode, CancellationToken ct = default);
    Task<Result<ConversationDto>> AssignAsync(Guid id, Guid? userId, CancellationToken ct = default);
}

public interface IOutboundMessageService
{
    Task<Result<MessageDto>> SendToContactAsync(SendMessageRequest request, CancellationToken ct = default);
    Task<Result<MessageDto>> GetMessageAsync(Guid messageId, CancellationToken ct = default);
}

public interface IMessageTemplateService
{
    Task<IReadOnlyList<TemplateDto>> ListAsync(bool activeOnly, CancellationToken ct = default);
    Task<Result<TemplateDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<TemplateDto>> CreateAsync(SaveTemplateRequest request, CancellationToken ct = default);
    Task<Result<TemplateDto>> UpdateAsync(Guid id, SaveTemplateRequest request, CancellationToken ct = default);
    Task<Result<TemplateDto>> SetActiveAsync(Guid id, bool active, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<Result<TemplatePreviewDto>> PreviewAsync(Guid id, Guid? contactId, CancellationToken ct = default);
}

public interface IWebhookEventService
{
    Task<PagedResult<WebhookEventDto>> ListAsync(WebhookEventQuery query, CancellationToken ct = default);
    Task<WebhookEventStatsDto> StatsAsync(CancellationToken ct = default);
    Task<MessagingStatusDto> StatusAsync(CancellationToken ct = default);
}

/// <summary>Dead-letter visibility for PLATFORM operators only: the queues are shared by all tenants, so tenant users must never see them.</summary>
public interface IDeadLetterService
{
    Task<IReadOnlyList<DeadLetterQueueDto>> QueuesAsync(CancellationToken ct = default);
    /// <summary>Reads (and puts back) up to <paramref name="max"/> messages: headers only, never the payload.</summary>
    Task<IReadOnlyList<DeadLetterMessageDto>> PeekAsync(string queue, int max, CancellationToken ct = default);
}

public enum WebhookReceiveStatus { Accepted = 1, Duplicate, Ignored, Rejected, TooLarge, Malformed, TenantUnavailable }

public sealed record WebhookReceiveResult(WebhookReceiveStatus Status, string? Detail = null);

/// <summary>Anonymous webhook entry point. Authenticates by HMAC, resolves tenant and account, stores exactly once with an outbox row, answers fast.</summary>
public interface IWebhookIngestionService
{
    /// <summary>Largest body the endpoint reads; bigger requests answer 413 without being buffered.</summary>
    int MaxBodyBytes { get; }
    Task<WebhookReceiveResult> ReceiveAsync(string webhookKey, byte[] body, IReadOnlyDictionary<string, string> headers, string? sourceIp, CancellationToken ct = default);
}

/// <summary>Envelope of every messaging event on RabbitMQ.</summary>
public sealed record EventEnvelope(Guid MessageId, Guid TenantId, string CorrelationId, string? CausationId, string Type, DateTime OccurredAtUtc, int SchemaVersion, string Payload);

public static class MessagingEventTypes
{
    public const string MessageReceived = "message.received";
    public const string MessageStatusChanged = "message.status";
    public const string AccountStatusChanged = "account.status";
    /// <summary>Extension point for Phase 7/8: published when an inbound message arrives in a conversation whose AiMode is Active. Nothing consumes it yet.</summary>
    public const string AiReplyRequested = "ai.reply.requested";
    public static readonly IReadOnlyList<string> All = [MessageReceived, MessageStatusChanged, AccountStatusChanged, AiReplyRequested];
}

public sealed record InboundReceivedPayload(Guid WebhookEventId, Guid MessageId, Guid ConversationId, Guid ChannelAccountId);
public sealed record StatusChangedPayload(Guid WebhookEventId, Guid ChannelAccountId, string ExternalMessageId, int Ack, string? DeliveryStatus, bool IsError, DateTime? OccurredAtUtc);
public sealed record AccountStatusPayload(Guid WebhookEventId, Guid ChannelAccountId, string Status, string? PhoneE164, string? PushName, bool NewContactsRestricted, DateTime? RestrictedUntilUtc, string? RestrictionKind);
public sealed record AiReplyRequestedPayload(Guid ConversationId, Guid MessageId, Guid ChannelAccountId);

/// <summary>Raised by consumers for messages that can never succeed (forged tenant, unknown ids): the host dead-letters them without retrying.</summary>
public sealed class PoisonMessageException(string reason) : Exception(reason);

/// <summary>Business logic of the RabbitMQ consumers. Called inside the tenant scope established from the verified TenantId header.</summary>
public interface IMessagingEventHandler
{
    string ConsumerName { get; }
    /// <param name="attempt">1-based delivery attempt (retries re-deliver the same message with a higher number).</param>
    Task HandleAsync(EventEnvelope envelope, int attempt, CancellationToken ct);
}

public interface IOutboxDispatcher
{
    /// <summary>Publishes due outbox rows of every tenant (each in its own tenant scope). Returns how many were published.</summary>
    Task<int> DispatchAsync(CancellationToken ct = default);
}

public interface IEventPublisher
{
    bool IsConfigured { get; }
    Task PublishAsync(EventEnvelope envelope, CancellationToken ct);
    /// <summary>Can a healthy connection to the broker be held right now?</summary>
    Task<bool> PingAsync(CancellationToken ct);
}

/// <summary>Schedules the Hangfire jobs of the messaging module (replaced by a recorder in tests).</summary>
public interface IMessagingJobScheduler
{
    string EnqueueSend(Guid tenantId, Guid messageId, TimeSpan? delay = null);
    void EnqueueOutboxDispatch();
    string EnqueueReconcile(Guid tenantId, Guid messageId, TimeSpan delay);
}

/// <summary>Runs the send/reconcile jobs inside a tenant scope.</summary>
public interface IOutboundSendRunner
{
    Task SendAsync(Guid messageId, CancellationToken ct = default);
    Task ReconcileAsync(Guid messageId, CancellationToken ct = default);
}

/// <summary>Auto-pause logic shared by the send runner and the status consumer.</summary>
public interface IAccountHealthService
{
    Task RecordSendFailureAsync(Guid accountId, string reason, CancellationToken ct = default);
    Task RecordSendSuccessAsync(Guid accountId, CancellationToken ct = default);
    Task ApplySessionStatusAsync(Guid accountId, AccountStatusPayload status, CancellationToken ct = default);
}
