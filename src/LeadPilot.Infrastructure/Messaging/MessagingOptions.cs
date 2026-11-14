namespace LeadPilot.Infrastructure.Messaging;

/// <summary>Messaging settings (section <c>Messaging</c>). Conservative defaults; none of them is tenant data.</summary>
public sealed class MessagingOptions
{
    public const string Section = "Messaging";

    public int MaxWebhookBodyBytes { get; set; } = 256 * 1024;
    public int WebhookRetentionDays { get; set; } = 30;
    public int OutboxBatchSize { get; set; } = 50;
    public int OutboxMaxAttempts { get; set; } = 12;
    /// <summary>How long a claimed outbox row stays invisible to other dispatchers while it is being published.</summary>
    public int OutboxLeaseSeconds { get; set; } = 60;
    /// <summary>Consecutive failed sends that pause an account automatically.</summary>
    public int AutoPauseFailureThreshold { get; set; } = 3;
    /// <summary>A message stuck in "Sending" (worker crash) older than this is reconciled with the gateway.</summary>
    public int StaleSendingMinutes { get; set; } = 3;
    /// <summary>A message the gateway never confirmed is declared failed (never re-sent) after this long.</summary>
    public int ReconcileGraceMinutes { get; set; } = 10;
    /// <summary>Hard ceiling for all accounts of one tenant together (messages in the last 24 h). 0 disables.</summary>
    public int PerTenantDailyLimit { get; set; } = 1000;
    /// <summary>Show "typing…" before a reply (WAHA's anti-blocking recommendation). Milliseconds per character, capped; 0 disables.</summary>
    public int TypingMillisecondsPerChar { get; set; } = 40;
    public int TypingMaxMilliseconds { get; set; } = 4000;
    /// <summary>Marks the conversation as read in WhatsApp before replying (recommended by WAHA).</summary>
    public bool SendSeenBeforeReply { get; set; } = true;
    public int InboundAckMatchAttempts { get; set; } = 4;
}

/// <summary>RabbitMQ connection and topology. <c>Host</c> empty = not configured: events stay in the outbox and the health check is not registered.</summary>
public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";

    public string? Host { get; set; }
    public int Port { get; set; } = 5672;
    public string UserName { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";
    public bool UseTls { get; set; }
    /// <summary>Exchange name and queue-name prefix. Tests use a unique prefix per run to stay isolated.</summary>
    public string Prefix { get; set; } = "leadpilot.messaging";
    public static readonly int[] DefaultRetryDelaysSeconds = [5, 30, 120, 600];
    /// <summary>
    /// Delay before attempt 2, 3, … of a failing message. After the last delay one more attempt is made, then the message is dead-lettered.
    /// Null/empty = <see cref="DefaultRetryDelaysSeconds"/>. (Deliberately NOT initialised: configuration binding APPENDS array values to an existing array, which would silently turn "1,1,1" into "5,30,120,600,1,1,1".)
    /// </summary>
    public int[]? RetryDelaysSeconds { get; set; }
    public ushort Prefetch { get; set; } = 10;
    public int PublishTimeoutSeconds { get; set; } = 10;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
    public string Exchange => Prefix;
    public IReadOnlyList<int> RetryDelays => RetryDelaysSeconds is { Length: > 0 } r ? r : DefaultRetryDelaysSeconds;
    public int MaxAttempts => RetryDelays.Count + 1;
}
