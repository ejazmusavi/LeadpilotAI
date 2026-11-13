namespace LeadPilot.Domain.Messaging;

public enum ChannelType { WhatsApp = 1 }

/// <summary>Gateway product behind a channel. Only WAHA exists; the port (IWhatsAppGateway) keeps the door open for the official Business Platform.</summary>
public enum ChannelProvider { Waha = 1 }

/// <summary>Connection state reported by the gateway (WAHA session.status), independent of our own pause switch.</summary>
public enum ConnectionStatus { Unknown = 0, Stopped = 1, Starting = 2, ScanQrCode = 3, Working = 4, Failed = 5 }

public enum MessageDirection { Inbound = 1, Outbound = 2 }

/// <summary>Why an outbound message may be sent: a reply inside a customer-initiated conversation, or marketing (needs consent).</summary>
public enum MessagePurpose { Service = 1, Marketing = 2 }

/// <summary>
/// Lifecycle of a message. Outbound: Queued → Sending → (Sent → Delivered → Read) | Failed | Unknown. Inbound messages are Received.
/// Delivery ranks (Sent &lt; Delivered &lt; Read) only ever move forward: see <c>MessageStatusRules</c>.
/// </summary>
public enum MessageStatus { Queued = 1, Sending = 2, Unknown = 3, Sent = 4, Delivered = 5, Read = 6, Failed = 7, Received = 8 }

public enum ConversationStatus { Open = 1, Closed = 2 }

/// <summary>Who answers in a conversation. Phase 5 only stores it; AI replies arrive in Phase 7 and the human hand-off UI in Phase 8.</summary>
public enum AiMode { Active = 1, Paused = 2, HumanControl = 3 }

public enum WebhookEventStatus { Received = 1, Processed = 2, Ignored = 3, Failed = 4 }

public enum OutboxStatus { Pending = 1, Published = 2, Failed = 3 }

public enum AttachmentStatus { NotDownloaded = 1, Rejected = 2 }

public enum TemplateCategory { Service = 1, Marketing = 2 }

public enum NotificationKind { AccountPaused = 1, AccountResumed = 2, SessionDisconnected = 3, ReachoutTimelock = 4, MessageCapping = 5 }
