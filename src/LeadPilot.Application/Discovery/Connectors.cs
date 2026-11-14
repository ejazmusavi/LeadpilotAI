using LeadPilot.Domain.Discovery;

namespace LeadPilot.Application.Discovery;

public static class ConnectorKeys
{
    public const string GooglePlaces = "google_places";
    public const string Website = "website";
    public const string CsvImport = "csv_import";
    public const string ManualEntry = "manual_entry";
    public const string AuthorizedDirectory = "authorized_directory";
    public const string LeadGenIntegration = "leadgen_integration";
    public const string MetaLeadAds = "meta_lead_ads";
}

public enum ConnectorKind
{
    /// <summary>Searches an external provider and produces reviewable results.</summary>
    Search = 1,
    /// <summary>Handled by an existing in-app path (CSV/Excel import, manual entry); listed for completeness, not run as a discovery job.</summary>
    InApp = 2,
    /// <summary>Documented extension point. Returns "not configured"; never returns data.</summary>
    ExtensionPoint = 3,
}

public sealed record ConnectorDescriptor(
    string Key, string DisplayName, string Description, ConnectorKind Kind, bool RequiresCredentials, bool AcceptsSeedUrls = false, bool IsTestOnly = false,
    string? InAppPath = null, string? NotConfiguredReason = null, string? Attribution = null);

public enum SourceKind { StructuredProvider = 1, WebsiteSelfPublished = 2, AuthorizedDirectory = 3, UserSupplied = 4 }

/// <summary>Where and what to search for, already expanded from the tenant's category mapping and the wizard's location fields.</summary>
public sealed record ProviderQuery(string Text, string? RegionCode, string? PlaceType, string? Country, string? Province, string? City, string? District, string? Area);

public sealed record ConnectorRequest(ProviderQuery Query, string? ApiKey, string? PageToken, int PageSize, IReadOnlyList<string> SeedUrls, decimal CostPerCallUsd, DateTime NowUtc);

/// <summary>Everything a connector reports about one place/business. Unknown values stay null: nothing is guessed or invented.</summary>
public sealed record DiscoveryCandidate
{
    public required string ExternalId { get; init; }
    public string? Name { get; init; }
    public IReadOnlyList<string> Types { get; init; } = [];
    public string? Country { get; init; }
    public string? Province { get; init; }
    public string? City { get; init; }
    public string? District { get; init; }
    public string? Area { get; init; }
    public string? Address { get; init; }
    public string? Website { get; init; }
    public string? Phone { get; init; }
    public string? Email { get; init; }
    public IReadOnlyList<string> SocialLinks { get; init; } = [];
    public string? SourceName { get; init; }
    public string? SourceUrl { get; init; }
    public string? Attribution { get; init; }
    /// <summary>Provider-reported status (e.g. OPERATIONAL, CLOSED_PERMANENTLY).</summary>
    public string? BusinessStatus { get; init; }
    public SourceKind Source { get; init; } = SourceKind.StructuredProvider;
    public DateTime RetrievedAtUtc { get; init; }
    /// <summary>Set by connectors whose provider terms limit how long content may be kept.</summary>
    public DateTime? ExpiresAtUtc { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record ConnectorCall(string Operation, int Calls, decimal EstimatedCostUsd, int? HttpStatus);

public sealed record ConnectorPage(IReadOnlyList<DiscoveryCandidate> Candidates, string? NextPageToken, IReadOnlyList<ConnectorCall> Calls, IReadOnlyList<string>? Warnings = null);

public interface IDiscoveryConnector
{
    ConnectorDescriptor Descriptor { get; }
    /// <summary>One page of results. Throws a <see cref="ConnectorException"/> subtype for every provider failure (never returns fabricated data).</summary>
    Task<ConnectorPage> SearchAsync(ConnectorRequest request, CancellationToken ct);
}

public interface IDiscoveryConnectorRegistry
{
    IReadOnlyList<ConnectorDescriptor> All { get; }
    IDiscoveryConnector? Find(string key);
}

public abstract class ConnectorException(string code, string message, bool retryable, ConnectorCall? call = null, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    /// <summary>The (possibly zero-cost) call that failed, so it is still counted.</summary>
    public ConnectorCall? Call { get; } = call;
}

public sealed class ConnectorNotConfiguredException(string message) : ConnectorException("connector_not_configured", message, retryable: false);
public sealed class ConnectorAuthException(string message, ConnectorCall? call = null) : ConnectorException("connector_auth_failed", message, retryable: false, call);
public sealed class ConnectorQuotaException(string message, ConnectorCall? call = null) : ConnectorException("provider_quota_exceeded", message, retryable: true, call);
public sealed class ConnectorTransientException(string message, ConnectorCall? call = null, Exception? inner = null) : ConnectorException("provider_unavailable", message, retryable: true, call, inner);
public sealed class ConnectorPermanentException(string message, ConnectorCall? call = null) : ConnectorException("provider_rejected_request", message, retryable: false, call);

/// <summary>Public business information read from a website. Every value is null unless it was literally present on the page.</summary>
public sealed record WebsiteInfo(
    string FinalUrl, string? Title, string? BusinessName, string? Address, IReadOnlyList<string> Phones, IReadOnlyList<string> Emails, IReadOnlyList<string> SocialLinks,
    IReadOnlyList<string> PagesRead, IReadOnlyList<string> Notes);

public enum WebsiteFetchOutcome { Fetched = 1, BlockedByRobots = 2, BlockedByPolicy = 3, NotHtml = 4, Protected = 5, Failed = 6 }

public sealed record WebsiteEnrichmentResult(WebsiteFetchOutcome Outcome, WebsiteInfo? Info, string? Reason);

/// <summary>Reads only public, robots-permitted pages through the SSRF-safe client. Never logs in, never evaluates scripts.</summary>
public interface IWebsiteEnricher
{
    Task<WebsiteEnrichmentResult> EnrichAsync(string url, CancellationToken ct);
}
