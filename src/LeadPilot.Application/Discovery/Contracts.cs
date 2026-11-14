using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Discovery;
using LeadPilot.Shared.Results;

namespace LeadPilot.Application.Discovery;

public sealed class DiscoveryOptions
{
    public const string Section = "Discovery";
    public int PageSize { get; set; } = 20;
    public int MaxPagesPerQuery { get; set; } = 5;
    public int MaxResultsPerJob { get; set; } = 200;
    public int DefaultMonthlyCallLimit { get; set; } = 1000;
    /// <summary>ESTIMATE used for the usage page when a tenant sets no cost of its own. Check Google's current price list; this is not billing data.</summary>
    public decimal DefaultCostPerCallUsd { get; set; } = 0.035m;
    public int PageDelayMs { get; set; } = 500;
    /// <summary>Operating choice for provider content held while it awaits review (see docs/INTEGRATIONS.md). place_id is kept without limit.</summary>
    public int GoogleContentRetentionDays { get; set; } = 30;
    public int StaleRunningMinutes { get; set; } = 30;
    public int MaxBulkItems { get; set; } = 100;
    public GooglePlacesOptions GooglePlaces { get; set; } = new();
    public WebsiteFetchOptions Website { get; set; } = new();
}

public sealed class GooglePlacesOptions
{
    public string BaseUrl { get; set; } = "https://places.googleapis.com";
    public int TimeoutSeconds { get; set; } = 20;
}

public sealed class WebsiteFetchOptions
{
    public string UserAgent { get; set; } = "LeadPilotAI-Bot/1.0 (+https://leadpilot.ai/bot; business-directory research; honours robots.txt)";
    /// <summary>Token matched against robots.txt groups.</summary>
    public string RobotsToken { get; set; } = "LeadPilotAI-Bot";
    public int MaxBytes { get; set; } = 1_000_000;
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxRedirects { get; set; } = 3;
    public int MaxPagesPerSite { get; set; } = 2;
    public int MaxWebsitesPerJob { get; set; } = 100;
}

// ---------- DTOs ----------
public sealed record DiscoveryJobDto(
    Guid Id, string ConnectorType, string ConnectorName, DiscoveryJobStatus Status, string? Country, string? Province, string? City, string? District, string? Area, Guid? CategoryId, string? CategoryName,
    string? Keywords, int MaxResults, bool EnrichWebsites, int PagesFetched, int ResultsFound, int ResultsNew, int ResultsAlreadyKnown, int DuplicatesFlagged, int ApiCalls, decimal EstimatedCostUsd,
    int Attempts, bool CancelRequested, string? Warnings, string? ErrorCode, string? ErrorMessage, bool ErrorRetryable, int Progress, int PendingResults, DateTime CreatedAtUtc, string? CreatedBy, DateTime? StartedAtUtc, DateTime? CompletedAtUtc);

public sealed record CreateDiscoveryJobRequest(
    string ConnectorType, string? Country, string? Province, string? City, string? District, string? Area, Guid? CategoryId, string? Keywords, int MaxResults = 40, bool EnrichWebsites = true,
    string? SeedUrls = null) : TenantScopedRequest;

public class DiscoveryJobQuery
{
    public DiscoveryJobStatus? Status { get; set; }
    public string? ConnectorType { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public sealed record SearchHistoryItemDto(Guid LastJobId, string ConnectorType, string ConnectorName, string Location, string? CategoryName, string? Keywords, int Runs, DateTime LastRunAtUtc, DiscoveryJobStatus LastStatus, int TotalResults);

public sealed record DiscoveryResultDto(
    Guid Id, Guid JobId, string ConnectorType, string ExternalId, string? Name, Guid? CategoryId, string? CategoryLabel, string? Country, string? Province, string? City, string? District, string? Area,
    string? Address, string? Website, string? PublicPhone, string? PublicEmail, IReadOnlyList<string> SocialLinks, string? SourceName, string? SourceUrl, string? Attribution, DateTime RetrievedAtUtc,
    DateTime? ExpiresAtUtc, bool ContentPurged, DateTime? LastVerifiedAtUtc, int Confidence, ConfidenceLevel ConfidenceLevel, string? ResearchNotes, IReadOnlyDictionary<string, string> Provenance,
    EnrichmentStatus Enrichment, ReviewStatus ReviewStatus, DateTime? ReviewedAtUtc, string? ReviewNote, Guid? DuplicateLeadId, string? DuplicateLeadName, string? DuplicateReasons, Guid? LeadId, ApprovalAction? Approval);

public class DiscoveryResultQuery
{
    public Guid? JobId { get; set; }
    public ReviewStatus? Status { get; set; } = ReviewStatus.Pending;
    public int? MinConfidence { get; set; }
    public bool? HasWebsite { get; set; }
    public bool? HasPhone { get; set; }
    public bool? PossibleDuplicate { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public enum DuplicateAction { Merge = 1, CreateNew = 2 }

public sealed record ApproveResultRequest(Guid? CategoryId = null, Guid? OwnerUserId = null, DuplicateAction OnDuplicate = DuplicateAction.Merge) : TenantScopedRequest;
public sealed record RejectResultRequest(string? Note = null) : TenantScopedRequest;
public sealed record BulkReviewRequest(IReadOnlyList<Guid> ResultIds, Guid? OwnerUserId = null, string? Note = null, DuplicateAction OnDuplicate = DuplicateAction.Merge) : TenantScopedRequest;
public sealed record ApprovalOutcomeDto(Guid ResultId, Guid LeadId, ApprovalAction Action);
public sealed record BulkReviewItem(Guid ResultId, bool Success, string? Error, Guid? LeadId, ApprovalAction? Action);
public sealed record BulkReviewResult(int Requested, int Succeeded, int Failed, IReadOnlyList<BulkReviewItem> Items);

public sealed record ConnectorStatusDto(
    string Key, string DisplayName, string Description, ConnectorKind Kind, bool RequiresCredentials, bool AcceptsSeedUrls, bool IsTestOnly, string? InAppPath, string? NotConfiguredReason,
    string? Attribution, bool IsEnabled, bool HasCredential, string? KeyHint, bool IsReady, string StatusText, int? MonthlyCallLimit, int EffectiveMonthlyCallLimit, decimal? MonthlyBudgetUsd,
    decimal? CostPerCallUsd, decimal EffectiveCostPerCallUsd, int CallsThisMonth, decimal CostThisMonth);

public sealed record SaveConnectorSettingsRequest(string? ApiKey = null, bool ClearApiKey = false, bool? Enabled = null, int? MonthlyCallLimit = null, decimal? MonthlyBudgetUsd = null,
    decimal? CostPerCallUsd = null, bool UpdateLimits = false) : TenantScopedRequest;

public sealed record UsageEntryDto(Guid Id, string ConnectorType, Guid? JobId, string Operation, int Calls, decimal EstimatedCostUsd, int? HttpStatus, DateTime OccurredAtUtc);
public sealed record UsageSummaryDto(int PeriodKey, IReadOnlyList<ConnectorStatusDto> Connectors, PagedResult<UsageEntryDto> Recent);

public sealed record CategoryMappingDto(Guid Id, Guid CategoryId, string CategoryName, IReadOnlyList<string> SearchTerms, string? PlaceType, bool IsActive);
public sealed record SaveCategoryMappingRequest(Guid CategoryId, string SearchTerms, string? PlaceType = null) : TenantScopedRequest;
public sealed record LocationPresetDto(Guid Id, string Label, string Country, string? Province, string? City, string? District, string? Area);
public sealed record SaveLocationPresetRequest(string Country, string? Province = null, string? City = null, string? District = null, string? Area = null) : TenantScopedRequest;
public sealed record StarterPackDto(string Key, string Name, string Description, int Categories, int Locations, bool Installed);
public sealed record InstallPackResult(int CategoriesCreated, int MappingsCreated, int LocationsCreated);

// ---------- ports ----------
public interface IDiscoveryJobService
{
    Task<PagedResult<DiscoveryJobDto>> ListAsync(DiscoveryJobQuery query, CancellationToken ct = default);
    Task<Result<DiscoveryJobDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<DiscoveryJobDto>> CreateAsync(CreateDiscoveryJobRequest request, CancellationToken ct = default);
    Task<Result<DiscoveryJobDto>> CancelAsync(Guid id, CancellationToken ct = default);
    /// <summary>Re-runs a failed or cancelled search, resuming from its checkpoint. Idempotent: results already stored are never duplicated.</summary>
    Task<Result<DiscoveryJobDto>> RetryAsync(Guid id, CancellationToken ct = default);
    /// <summary>Starts a NEW search with the same parameters (search history "run again").</summary>
    Task<Result<DiscoveryJobDto>> RerunAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<SearchHistoryItemDto>> HistoryAsync(int page, int pageSize, CancellationToken ct = default);
}

public interface IDiscoveryResultService
{
    Task<PagedResult<DiscoveryResultDto>> ListAsync(DiscoveryResultQuery query, CancellationToken ct = default);
    Task<Result<DiscoveryResultDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<ApprovalOutcomeDto>> ApproveAsync(Guid id, ApproveResultRequest request, CancellationToken ct = default);
    Task<Result<DiscoveryResultDto>> RejectAsync(Guid id, RejectResultRequest request, CancellationToken ct = default);
    Task<BulkReviewResult> BulkApproveAsync(BulkReviewRequest request, CancellationToken ct = default);
    Task<BulkReviewResult> BulkRejectAsync(BulkReviewRequest request, CancellationToken ct = default);
}

public interface IDiscoveryConnectorService
{
    Task<IReadOnlyList<ConnectorStatusDto>> ListAsync(CancellationToken ct = default);
    Task<Result<ConnectorStatusDto>> SaveSettingsAsync(string connectorType, SaveConnectorSettingsRequest request, CancellationToken ct = default);
    Task<UsageSummaryDto> UsageAsync(int page, int pageSize, CancellationToken ct = default);
}

public interface IDiscoveryCatalogService
{
    Task<IReadOnlyList<CategoryMappingDto>> ListMappingsAsync(CancellationToken ct = default);
    Task<Result<CategoryMappingDto>> SaveMappingAsync(SaveCategoryMappingRequest request, CancellationToken ct = default);
    Task<Result<CategoryMappingDto>> SetMappingActiveAsync(Guid id, bool active, CancellationToken ct = default);
    Task<IReadOnlyList<LocationPresetDto>> ListLocationsAsync(CancellationToken ct = default);
    Task<Result<LocationPresetDto>> AddLocationAsync(SaveLocationPresetRequest request, CancellationToken ct = default);
    Task<Result> DeleteLocationAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<StarterPackDto>> ListPacksAsync(CancellationToken ct = default);
    Task<Result<InstallPackResult>> InstallPackAsync(string key, CancellationToken ct = default);
}

/// <summary>Queues the background execution of a discovery job (Hangfire in production).</summary>
public interface IDiscoveryJobScheduler
{
    string Enqueue(Guid tenantId, Guid jobId);
    void TryDelete(string? backgroundJobId);
}

/// <summary>Executes one discovery job. Must run inside a tenant scope; fails closed otherwise.</summary>
public interface IDiscoveryJobRunner
{
    Task RunAsync(Guid jobId, CancellationToken ct);
}

/// <summary>Removes provider content whose retention period has ended (all tenants; platform job).</summary>
public interface IDiscoveryRetention
{
    Task<int> PurgeExpiredAsync(CancellationToken ct);
}

// ---------- starter packs (default data, not code paths) ----------
public sealed record StarterCategory(string Name, string[] Terms, string? PlaceType);
public sealed record StarterLocation(string Country, string? Province, string? City);
public sealed record StarterPack(string Key, string Name, string Description, IReadOnlyList<StarterCategory> Categories, IReadOnlyList<StarterLocation> Locations);

public static class DiscoveryStarterPacks
{
    public const string EducationPakistan = "education-pakistan";

    public static readonly IReadOnlyList<StarterPack> All =
    [
        new(EducationPakistan, "Education — Pakistan", "School and college categories with Pakistani cities. Everything installed here can be edited or deactivated.",
        [
            new("Private schools", ["private school"], "school"),
            new("Primary schools", ["primary school"], "primary_school"),
            new("Secondary schools", ["secondary school", "high school"], "secondary_school"),
            new("Higher secondary schools", ["higher secondary school", "intermediate college"], null),
            new("Colleges", ["college"], null),
            new("Academies", ["academy", "coaching academy"], null),
            new("Educational institutions", ["educational institute", "education center"], null),
        ],
        [
            new("Pakistan", "Sindh", "Karachi"), new("Pakistan", "Punjab", "Lahore"), new("Pakistan", "Islamabad Capital Territory", "Islamabad"), new("Pakistan", "Punjab", "Rawalpindi"),
            new("Pakistan", "Punjab", "Faisalabad"), new("Pakistan", "Punjab", "Multan"), new("Pakistan", "Khyber Pakhtunkhwa", "Peshawar"), new("Pakistan", "Balochistan", "Quetta"),
            new("Pakistan", "Sindh", "Hyderabad"), new("Pakistan", "Punjab", "Sialkot"), new("Pakistan", "Punjab", "Gujranwala"),
        ]),
    ];

    public static StarterPack? Find(string key) => All.FirstOrDefault(p => p.Key == key);

    /// <summary>Tenants whose industry is "Education" receive this pack automatically the first time discovery is opened.</summary>
    public static string? AutoPackForIndustry(string? industry) =>
        string.Equals(industry?.Trim(), "Education", StringComparison.OrdinalIgnoreCase) ? EducationPakistan : null;
}
