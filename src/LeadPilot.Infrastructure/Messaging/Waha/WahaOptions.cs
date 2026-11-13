namespace LeadPilot.Infrastructure.Messaging.Waha;

/// <summary>Settings of the WAHA adapter (section <c>Messaging:Waha</c>). Nothing here is tenant data; credentials are per account and encrypted.</summary>
public sealed class WahaOptions
{
    public const string Section = "Messaging:Waha";
    public const string HttpClientName = "waha";

    public int TimeoutSeconds { get; set; } = 20;
    /// <summary>Extra attempts for READ/management calls only (never sends) after a transient failure.</summary>
    public int ReadRetries { get; set; } = 2;
    public int RetryBaseDelayMs { get; set; } = 300;
    public int BreakerFailureThreshold { get; set; } = 5;
    public int BreakerOpenSeconds { get; set; } = 30;
    /// <summary>
    /// false (default): a tenant-supplied base URL may only point to PUBLIC addresses (SSRF protection in a multi-tenant SaaS).
    /// true: loopback/private hosts are allowed (a WAHA container next to the app, development, tests). Link-local/metadata addresses are always refused.
    /// </summary>
    public bool AllowPrivateHosts { get; set; }
    /// <summary>How far X-Webhook-Timestamp may deviate. WAHA retries a failed delivery for several minutes, so this must exceed its retry horizon.</summary>
    public int WebhookToleranceMinutes { get; set; } = 15;
    /// <summary>Turn on WEBJS ack events (config.webjs.tagsEventsOn). WAHA warns this can affect stability; other engines ignore it.</summary>
    public bool EnableAckTracking { get; set; } = true;
}
