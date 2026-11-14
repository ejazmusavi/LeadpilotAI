using LeadPilot.Domain.Messaging;

namespace LeadPilot.Application.Messaging;

// ----------------------------------------------------------------------------------------------------------------------------
// Provider-independent WhatsApp gateway port. Nothing in this file (or anywhere outside Infrastructure/Messaging/Waha) knows WAHA's
// endpoints, header names or payload shapes. WAHA is the only adapter today; the official Business Platform could be another.
// ----------------------------------------------------------------------------------------------------------------------------

/// <summary>Everything an adapter needs to talk to one account. Built per call from the account's own decrypted credentials; adapters hold no tenant state.</summary>
public sealed record GatewayConnection(string BaseUrl, string ApiKey, string AccountId);

public enum GatewaySessionStatus { Unknown = 0, Stopped = 1, Starting = 2, ScanQrCode = 3, Working = 4, Failed = 5 }

public sealed record GatewaySession(string AccountId, GatewaySessionStatus Status, string? PhoneE164, string? PushName, string? Engine, string? RawStatus);

/// <summary>Where and how the gateway must deliver webhooks for an account.</summary>
public sealed record GatewayWebhookRegistration(string Url, string Secret, IReadOnlyList<string> Events, bool EnableAckTracking);

/// <summary>QR to scan: either the raw value (render it ourselves) or a ready PNG, whichever the gateway returned.</summary>
public sealed record GatewayQrCode(string? RawValue, byte[]? PngImage);

public sealed record GatewaySendRequest(string ChatId, string Text, string? ReplyToExternalId = null);

public sealed record GatewaySendResult(string ExternalMessageId, DateTime? TimestampUtc);

/// <summary>State of one message as the gateway sees it. <see cref="Delivery"/> is null while pending/unknown.</summary>
public sealed record GatewayMessageInfo(string ExternalMessageId, bool FromMe, string? Body, DateTime? TimestampUtc, MessageStatus? Delivery, int? Ack);

public sealed record GatewayTemplateSend(string ChatId, string TemplateBody, IReadOnlyDictionary<string, string> Variables);

public enum GatewayFailureKind
{
    /// <summary>The request never reached the gateway (DNS/connect failure). Nothing was sent: safe to retry.</summary>
    Unreachable = 1,
    /// <summary>Gateway answered 429 before processing. Nothing was sent: safe to retry later.</summary>
    RateLimited = 2,
    /// <summary>Timeout, connection reset after sending, 5xx or an unreadable answer: the message MAY have been sent.</summary>
    OutcomeUnknown = 3,
    /// <summary>401/403: wrong or revoked API key.</summary>
    Unauthorized = 4,
    /// <summary>404: session or chat does not exist.</summary>
    NotFound = 5,
    /// <summary>Other 4xx: the gateway refused the request (bad chat id, session not working …). Nothing was sent.</summary>
    Rejected = 6,
    /// <summary>WhatsApp refused a first message to a new contact (reachout timelock 463 / message capping 475).</summary>
    NewContactRestricted = 7,
    /// <summary>The configured base URL is not allowed (private/unsafe address, bad scheme).</summary>
    UnsafeEndpoint = 8,
}

/// <summary>Typed gateway failure. Callers decide retry policy from <see cref="Kind"/>, never from message text.</summary>
public sealed class GatewayException(GatewayFailureKind kind, string message, int? httpStatus = null, Exception? inner = null) : Exception(message, inner)
{
    public GatewayFailureKind Kind { get; } = kind;
    public int? HttpStatus { get; } = httpStatus;
    /// <summary>True when nothing was sent, so repeating the call cannot duplicate a message.</summary>
    public bool SafeToRetry => Kind is GatewayFailureKind.Unreachable or GatewayFailureKind.RateLimited;
    /// <summary>True when the request may have been processed (the only dangerous case for sends).</summary>
    public bool OutcomeUnknown => Kind == GatewayFailureKind.OutcomeUnknown;
    /// <summary>Failures that say something is wrong with the account/session itself and count toward auto-pause.</summary>
    public bool CountsAgainstAccount => Kind is GatewayFailureKind.Unauthorized or GatewayFailureKind.NotFound or GatewayFailureKind.OutcomeUnknown or GatewayFailureKind.Unreachable;
}

// ---- Webhooks ----

/// <summary>A webhook exactly as received: raw bytes (the signature covers these bytes) and the transport headers the adapter may need.</summary>
public sealed record WebhookRequest(byte[] Body, IReadOnlyDictionary<string, string> Headers);

public enum WebhookRejection { MissingSignature = 1, InvalidSignature, StaleTimestamp, MalformedPayload, UnsupportedAlgorithm }

public sealed record WebhookProcessingResult(WebhookRejection? Rejection, GatewayEvent? Event, string? Detail)
{
    public bool IsAccepted => Rejection is null && Event is not null;
    public static WebhookProcessingResult Rejected(WebhookRejection r, string? detail = null) => new(r, null, detail);
    public static WebhookProcessingResult Accepted(GatewayEvent e) => new(null, e, null);
}

public sealed record AttachmentInfo(string? FileName, string? MimeType, long? SizeBytes, bool HasUrl);

/// <summary>Normalised gateway event. <see cref="ExternalEventId"/> is the gateway's event id or a deterministic hash when there is none.</summary>
public abstract record GatewayEvent(string ExternalEventId, string EventType, string AccountId, DateTime? OccurredAtUtc);

public sealed record InboundMessageEvent(string ExternalEventId, string EventType, string AccountId, DateTime OccurredAt, string ExternalMessageId, string ChatId, string? PeerPhoneE164, string? PeerName,
    string Body, IReadOnlyList<AttachmentInfo> Attachments) : GatewayEvent(ExternalEventId, EventType, AccountId, OccurredAt);

/// <summary><paramref name="Delivery"/> is Sent/Delivered/Read, or null for PENDING; <paramref name="IsError"/> for ack -1.</summary>
public sealed record MessageAckEvent(string ExternalEventId, string EventType, string AccountId, DateTime? OccurredAt, string ExternalMessageId, string ChatId, int Ack, MessageStatus? Delivery, bool IsError)
    : GatewayEvent(ExternalEventId, EventType, AccountId, OccurredAt);

public sealed record SessionStatusEvent(string ExternalEventId, string EventType, string AccountId, DateTime? OccurredAt, GatewaySessionStatus Status, string? PhoneE164, string? PushName,
    bool NewContactsRestricted, DateTime? RestrictedUntilUtc, string? RestrictionKind) : GatewayEvent(ExternalEventId, EventType, AccountId, OccurredAt);

/// <summary>A valid, signed event we deliberately do not act on (reactions, groups, labels …).</summary>
public sealed record IgnoredGatewayEvent(string ExternalEventId, string EventType, string AccountId, string Reason) : GatewayEvent(ExternalEventId, EventType, AccountId, null);

public interface IWhatsAppGateway
{
    /// <summary>Sessions known to the gateway (also the cheapest "are these credentials valid" call).</summary>
    Task<IReadOnlyList<GatewaySession>> GetAccountsAsync(GatewayConnection connection, CancellationToken ct = default);
    Task<GatewaySession?> GetAccountStatusAsync(GatewayConnection connection, CancellationToken ct = default);
    /// <summary>Creates (or reconfigures) the session with our webhook and starts it. Idempotent.</summary>
    Task<GatewaySession> ConnectAccountAsync(GatewayConnection connection, GatewayWebhookRegistration webhook, CancellationToken ct = default);
    Task<GatewayQrCode> GetQrCodeAsync(GatewayConnection connection, CancellationToken ct = default);
    /// <summary><paramref name="logout"/> true unlinks the phone (a new QR is needed); false only stops the session.</summary>
    Task DisconnectAccountAsync(GatewayConnection connection, bool logout, CancellationToken ct = default);

    /// <summary>Sends one text. NEVER retried by the adapter: a repeated call can create a second message (see <see cref="GatewayException.OutcomeUnknown"/>).</summary>
    Task<GatewaySendResult> SendTextAsync(GatewayConnection connection, GatewaySendRequest request, CancellationToken ct = default);
    /// <summary>The gateway has no official templates: the body is rendered locally (<c>TemplateRenderer</c>) and sent as text.</summary>
    Task<GatewaySendResult> SendTemplateAsync(GatewayConnection connection, GatewayTemplateSend request, CancellationToken ct = default);
    Task MarkAsReadAsync(GatewayConnection connection, string chatId, IReadOnlyList<string> externalMessageIds, CancellationToken ct = default);
    Task SetTypingAsync(GatewayConnection connection, string chatId, bool typing, CancellationToken ct = default);

    /// <summary>Delivery state of one message by its gateway id; null when the gateway does not know it.</summary>
    Task<GatewayMessageInfo?> GetMessageStatusAsync(GatewayConnection connection, string chatId, string externalMessageId, CancellationToken ct = default);
    /// <summary>Looks for a message we sent to <paramref name="chatId"/> since <paramref name="sinceUtc"/> with exactly this text (crash-window reconciliation). Null when none is found.</summary>
    Task<GatewayMessageInfo?> FindSentMessageAsync(GatewayConnection connection, string chatId, DateTime sinceUtc, string body, CancellationToken ct = default);

    /// <summary>Verifies the signature and normalises the payload. Never throws for hostile input: a bad request is a <see cref="WebhookProcessingResult"/> with a rejection.</summary>
    Task<WebhookProcessingResult> ProcessWebhookAsync(WebhookRequest request, string secret, DateTime nowUtc, CancellationToken ct = default);
}
