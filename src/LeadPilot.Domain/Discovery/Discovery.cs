using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;

namespace LeadPilot.Domain.Discovery;

public enum DiscoveryJobStatus { Queued = 1, Running = 2, Completed = 3, Failed = 4, Cancelled = 5 }
public enum ReviewStatus { Pending = 1, Approved = 2, Rejected = 3 }
public enum ConfidenceLevel { Low = 1, Medium = 2, High = 3 }
public enum ApprovalAction { Created = 1, Merged = 2 }
public enum EnrichmentStatus { NotRequested = 0, Done = 1, Skipped = 2, Failed = 3 }

/// <summary>How a stored value was obtained. Anything not <see cref="Corroborated"/> is shown as unverified in the UI.</summary>
public enum Verification { Unverified = 0, ProviderReported = 1, SelfPublished = 2, Corroborated = 3 }

/// <summary>One user-initiated search (location + category + connector). Processed by a Hangfire job inside the tenant scope.</summary>
public class DiscoveryJob : AuditableEntity, ITenantOwned
{
    private DiscoveryJob() { }
    public Guid TenantId { get; private set; }
    public string ConnectorType { get; private set; } = string.Empty;
    public DiscoveryJobStatus Status { get; private set; } = DiscoveryJobStatus.Queued;
    public string? Country { get; private set; }
    public string? Province { get; private set; }
    public string? City { get; private set; }
    public string? District { get; private set; }
    public string? Area { get; private set; }
    public Guid? CategoryId { get; private set; }
    public string? Keywords { get; private set; }
    /// <summary>Newline separated public URLs (website connector only).</summary>
    public string? SeedUrls { get; private set; }
    public int MaxResults { get; private set; }
    public bool EnrichWebsites { get; private set; }
    public Guid? RequestedByUserId { get; private set; }
    public string? HangfireJobId { get; private set; }

    public int PagesFetched { get; private set; }
    public int ResultsFound { get; private set; }
    public int ResultsNew { get; private set; }
    public int ResultsAlreadyKnown { get; private set; }
    public int DuplicatesFlagged { get; private set; }
    public int ApiCalls { get; private set; }
    public decimal EstimatedCostUsd { get; private set; }
    public int Attempts { get; private set; }
    /// <summary>Opaque resume point (term index + page token) so a retry continues instead of paying for the same pages again.</summary>
    public string? Checkpoint { get; private set; }
    public bool CancelRequested { get; private set; }
    /// <summary>Non-fatal notes of the run (e.g. a seed website that robots.txt did not allow), newest last.</summary>
    public string? Warnings { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool ErrorRetryable { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    /// <summary>Optimistic concurrency / atomic start token.</summary>
    public int Version { get; private set; } = 1;

    public static DiscoveryJob Create(Guid tenantId, string connectorType, string? country, string? province, string? city, string? district, string? area,
        Guid? categoryId, string? keywords, string? seedUrls, int maxResults, bool enrich, Guid? userId, string? actor, DateTime nowUtc)
    {
        if (maxResults is < 1 or > 500) throw new DomainException("Maximum results must be between 1 and 500.");
        return new DiscoveryJob
        {
            TenantId = tenantId, ConnectorType = Guard.Required(connectorType, 50, "Connector"), Country = Guard.Optional(country, 100, "Country"), Province = Guard.Optional(province, 100, "Province"),
            City = Guard.Optional(city, 100, "City"), District = Guard.Optional(district, 100, "District"), Area = Guard.Optional(area, 100, "Area"), CategoryId = categoryId,
            Keywords = Guard.Optional(keywords, 300, "Keywords"), SeedUrls = Guard.Optional(seedUrls, 4000, "URLs"), MaxResults = maxResults, EnrichWebsites = enrich,
            RequestedByUserId = userId, CreatedAtUtc = nowUtc, CreatedBy = actor
        };
    }

    public void SetHangfireId(string? id) => HangfireJobId = id;

    public bool CanStart(DateTime nowUtc, TimeSpan staleAfter) =>
        Status == DiscoveryJobStatus.Queued || (Status == DiscoveryJobStatus.Running && StartedAtUtc is { } s && nowUtc - s > staleAfter);

    public void Start(DateTime nowUtc)
    {
        Status = DiscoveryJobStatus.Running; StartedAtUtc = nowUtc; CompletedAtUtc = null; Attempts++; ErrorCode = null; ErrorMessage = null; ErrorRetryable = false; Version++;
    }

    public void Progress(int pages, int found, int created, int known, int duplicates, string? checkpoint)
    {
        PagesFetched += pages; ResultsFound += found; ResultsNew += created; ResultsAlreadyKnown += known; DuplicatesFlagged += duplicates; Checkpoint = checkpoint;
    }

    public void AddWarning(string warning)
    {
        var w = string.IsNullOrEmpty(Warnings) ? warning : Warnings + "\n" + warning;
        Warnings = w.Length > 1000 ? w[..1000] : w;
    }

    public void AddUsage(int calls, decimal cost) { ApiCalls += calls; EstimatedCostUsd += cost; }

    public void Complete(DateTime nowUtc) { Status = DiscoveryJobStatus.Completed; CompletedAtUtc = nowUtc; Checkpoint = null; Version++; }

    public void Fail(string code, string message, bool retryable, DateTime nowUtc)
    {
        Status = DiscoveryJobStatus.Failed; ErrorCode = Guard.Required(code, 50, "Error code");
        var m = message.Length > 480 ? message[..480] : message;
        ErrorMessage = m; ErrorRetryable = retryable; CompletedAtUtc = nowUtc; Version++;
    }

    /// <summary>Unexpected infrastructure error: back to the queue so Hangfire's automatic retry picks it up again.</summary>
    public void Requeue(string message) { Status = DiscoveryJobStatus.Queued; ErrorMessage = message.Length > 480 ? message[..480] : message; Version++; }

    public void RequestCancel(DateTime nowUtc)
    {
        if (Status is DiscoveryJobStatus.Completed or DiscoveryJobStatus.Failed or DiscoveryJobStatus.Cancelled) throw new DomainException("Only queued or running searches can be cancelled.");
        CancelRequested = true;
        if (Status == DiscoveryJobStatus.Queued) { Status = DiscoveryJobStatus.Cancelled; CompletedAtUtc = nowUtc; }
        Version++;
    }

    public void MarkCancelled(DateTime nowUtc) { Status = DiscoveryJobStatus.Cancelled; CompletedAtUtc = nowUtc; Version++; }

    public void PrepareRetry()
    {
        if (Status != DiscoveryJobStatus.Failed && Status != DiscoveryJobStatus.Cancelled) throw new DomainException("Only failed or cancelled searches can be retried.");
        Status = DiscoveryJobStatus.Queued; CancelRequested = false; CompletedAtUtc = null; Version++;
    }
}

/// <summary>A normalized candidate awaiting human review. (TenantId, ConnectorType, ExternalId) is unique, so retries and repeated searches never duplicate it.</summary>
public class DiscoveryResult : AuditableEntity, ITenantOwned
{
    private DiscoveryResult() { }
    public Guid TenantId { get; private set; }
    public Guid JobId { get; private set; }
    public string ConnectorType { get; private set; } = string.Empty;
    public string ExternalId { get; private set; } = string.Empty;
    public string? Name { get; private set; }
    public Guid? CategoryId { get; private set; }
    public string? CategoryLabel { get; private set; }
    public string? Country { get; private set; }
    public string? Province { get; private set; }
    public string? City { get; private set; }
    public string? District { get; private set; }
    public string? Area { get; private set; }
    public string? Address { get; private set; }
    public string? Website { get; private set; }
    public string? PublicPhone { get; private set; }
    public string? PublicEmail { get; private set; }
    /// <summary>JSON array of public social profile URLs found on the business's own website.</summary>
    public string? SocialLinks { get; private set; }
    public string? SourceName { get; private set; }
    public string? SourceUrl { get; private set; }
    public string? Attribution { get; private set; }
    public DateTime RetrievedAtUtc { get; private set; }
    /// <summary>Provider terms: after this moment the provider-sourced content must be removed (the external id is kept).</summary>
    public DateTime? ExpiresAtUtc { get; private set; }
    public bool ContentPurged { get; private set; }
    public DateTime? LastVerifiedAtUtc { get; private set; }
    public int Confidence { get; private set; }
    public ConfidenceLevel ConfidenceLevel { get; private set; }
    public string? ResearchNotes { get; private set; }
    /// <summary>JSON object field → <see cref="Verification"/> name.</summary>
    public string? Provenance { get; private set; }
    public EnrichmentStatus Enrichment { get; private set; }
    public ReviewStatus ReviewStatus { get; private set; } = ReviewStatus.Pending;
    public Guid? ReviewedByUserId { get; private set; }
    public DateTime? ReviewedAtUtc { get; private set; }
    public string? ReviewNote { get; private set; }
    public Guid? DuplicateLeadId { get; private set; }
    public string? DuplicateReasons { get; private set; }
    public Guid? LeadId { get; private set; }
    public ApprovalAction? Approval { get; private set; }

    public sealed record Data(string ConnectorType, string ExternalId, string? Name, Guid? CategoryId, string? CategoryLabel, string? Country, string? Province, string? City, string? District,
        string? Area, string? Address, string? Website, string? PublicPhone, string? PublicEmail, string? SocialLinks, string? SourceName, string? SourceUrl, string? Attribution,
        DateTime RetrievedAtUtc, DateTime? ExpiresAtUtc, DateTime? LastVerifiedAtUtc, int Confidence, string? ResearchNotes, string? Provenance, EnrichmentStatus Enrichment);

    public static DiscoveryResult Create(Guid tenantId, Guid jobId, Data d, Guid? duplicateLeadId, string? duplicateReasons, DateTime nowUtc)
    {
        if (d.Confidence is < 0 or > 100) throw new DomainException("Confidence must be between 0 and 100.");
        return new DiscoveryResult
        {
            TenantId = tenantId, JobId = jobId, ConnectorType = Guard.Required(d.ConnectorType, 50, "Connector"), ExternalId = Guard.Required(d.ExternalId, 300, "External id"),
            Name = Guard.Optional(d.Name, 300, "Name"), CategoryId = d.CategoryId, CategoryLabel = Guard.Optional(d.CategoryLabel, 300, "Category"), Country = Guard.Optional(d.Country, 100, "Country"),
            Province = Guard.Optional(d.Province, 100, "Province"), City = Guard.Optional(d.City, 100, "City"), District = Guard.Optional(d.District, 100, "District"), Area = Guard.Optional(d.Area, 100, "Area"),
            Address = Guard.Optional(d.Address, 500, "Address"), Website = Guard.Optional(d.Website, 500, "Website"), PublicPhone = Guard.Optional(d.PublicPhone, 40, "Phone"),
            PublicEmail = Guard.Optional(d.PublicEmail, 256, "E-mail"), SocialLinks = d.SocialLinks, SourceName = Guard.Optional(d.SourceName, 200, "Source"),
            SourceUrl = Guard.Optional(d.SourceUrl, 500, "Source URL"), Attribution = Guard.Optional(d.Attribution, 500, "Attribution"), RetrievedAtUtc = d.RetrievedAtUtc,
            ExpiresAtUtc = d.ExpiresAtUtc, LastVerifiedAtUtc = d.LastVerifiedAtUtc, Confidence = d.Confidence, ConfidenceLevel = LevelFor(d.Confidence),
            ResearchNotes = d.ResearchNotes is { Length: > 2000 } n ? n[..2000] : d.ResearchNotes, Provenance = d.Provenance, Enrichment = d.Enrichment,
            DuplicateLeadId = duplicateLeadId, DuplicateReasons = Guard.Optional(duplicateReasons, 300, "Duplicate reasons"), CreatedAtUtc = nowUtc
        };
    }

    public static ConfidenceLevel LevelFor(int confidence) => confidence >= 75 ? ConfidenceLevel.High : confidence >= 50 ? ConfidenceLevel.Medium : ConfidenceLevel.Low;

    public void SetDuplicate(Guid? leadId, string? reasons) { DuplicateLeadId = leadId; DuplicateReasons = reasons; }

    public void Approve(Guid leadId, ApprovalAction action, Guid? userId, DateTime nowUtc)
    {
        if (ReviewStatus != ReviewStatus.Pending) throw new DomainException($"This result was already {ReviewStatus.ToString().ToLowerInvariant()}.");
        if (ContentPurged) throw new DomainException("The provider's retention period for this result has ended and its content was removed. Run the search again.");
        ReviewStatus = ReviewStatus.Approved; LeadId = leadId; Approval = action; ReviewedByUserId = userId; ReviewedAtUtc = nowUtc; UpdatedAtUtc = nowUtc;
    }

    public void Reject(string? note, Guid? userId, DateTime nowUtc)
    {
        if (ReviewStatus != ReviewStatus.Pending) throw new DomainException($"This result was already {ReviewStatus.ToString().ToLowerInvariant()}.");
        ReviewStatus = ReviewStatus.Rejected; ReviewNote = Guard.Optional(note, 500, "Note"); ReviewedByUserId = userId; ReviewedAtUtc = nowUtc; UpdatedAtUtc = nowUtc;
    }

    /// <summary>Removes provider content after its retention period. Keeps the external id (allowed long-term) so the same place is never offered twice.</summary>
    public void PurgeContent(DateTime nowUtc)
    {
        Name = null; CategoryLabel = null; Address = null; Website = null; PublicPhone = null; PublicEmail = null; SocialLinks = null; ResearchNotes = null; Provenance = null;
        Area = null; District = null; SourceUrl = null; ContentPurged = true; UpdatedAtUtc = nowUtc;
    }
}

/// <summary>Tenant-owned connector settings. The API key is stored only in protected form.</summary>
public class DiscoveryConnectorConfig : AuditableEntity, ITenantOwned
{
    private DiscoveryConnectorConfig() { }
    public Guid TenantId { get; private set; }
    public string ConnectorType { get; private set; } = string.Empty;
    public bool IsEnabled { get; private set; } = true;
    public string? ProtectedApiKey { get; private set; }
    public string? KeyHint { get; private set; }
    public int? MonthlyCallLimit { get; private set; }
    public decimal? MonthlyBudgetUsd { get; private set; }
    public decimal? CostPerCallUsd { get; private set; }

    public static DiscoveryConnectorConfig Create(Guid tenantId, string connectorType, DateTime nowUtc) =>
        new() { TenantId = tenantId, ConnectorType = Guard.Required(connectorType, 50, "Connector"), CreatedAtUtc = nowUtc };

    public void SetKey(string protectedKey, string hint, DateTime nowUtc) { ProtectedApiKey = protectedKey; KeyHint = hint; UpdatedAtUtc = nowUtc; }
    public void ClearKey(DateTime nowUtc) { ProtectedApiKey = null; KeyHint = null; UpdatedAtUtc = nowUtc; }
    public void SetEnabled(bool enabled, DateTime nowUtc) { IsEnabled = enabled; UpdatedAtUtc = nowUtc; }

    public void SetLimits(int? calls, decimal? budget, decimal? costPerCall, DateTime nowUtc)
    {
        if (calls is < 0 or > 10_000_000) throw new DomainException("Monthly call limit must be between 0 and 10,000,000.");
        if (budget is < 0 or > 10_000_000m) throw new DomainException("Monthly budget must be between 0 and 10,000,000.");
        if (costPerCall is < 0 or > 100m) throw new DomainException("Cost per call must be between 0 and 100.");
        MonthlyCallLimit = calls; MonthlyBudgetUsd = budget; CostPerCallUsd = costPerCall; UpdatedAtUtc = nowUtc;
    }
}

/// <summary>One billable (or counted) connector call. Append-only ledger; monthly usage is the sum for a period.</summary>
public class DiscoveryUsageEntry : Entity, ITenantOwned, IAppendOnly
{
    private DiscoveryUsageEntry() { }
    public Guid TenantId { get; private set; }
    public Guid? JobId { get; private set; }
    public string ConnectorType { get; private set; } = string.Empty;
    public string Operation { get; private set; } = string.Empty;
    public int Calls { get; private set; }
    public decimal EstimatedCostUsd { get; private set; }
    public int? HttpStatus { get; private set; }
    /// <summary>yyyymm of the UTC month the call happened in.</summary>
    public int PeriodKey { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    public static int PeriodOf(DateTime utc) => utc.Year * 100 + utc.Month;

    public static DiscoveryUsageEntry Create(Guid tenantId, Guid? jobId, string connectorType, string operation, int calls, decimal cost, int? httpStatus, DateTime nowUtc) =>
        new() { TenantId = tenantId, JobId = jobId, ConnectorType = connectorType, Operation = operation.Length > 100 ? operation[..100] : operation, Calls = calls, EstimatedCostUsd = cost, HttpStatus = httpStatus, PeriodKey = PeriodOf(nowUtc), OccurredAtUtc = nowUtc };
}

/// <summary>Maps a tenant's lead category to provider search terms. Entirely tenant data: nothing about schools (or any vertical) is hard-coded.</summary>
public class DiscoveryCategoryMapping : AuditableEntity, ITenantOwned
{
    private DiscoveryCategoryMapping() { }
    public Guid TenantId { get; private set; }
    public Guid CategoryId { get; private set; }
    /// <summary>Search phrases, one per line (e.g. "private school").</summary>
    public string SearchTerms { get; private set; } = string.Empty;
    /// <summary>Optional provider place type (Google "Table A" type such as primary_school).</summary>
    public string? PlaceType { get; private set; }
    public bool IsActive { get; private set; } = true;

    public static DiscoveryCategoryMapping Create(Guid tenantId, Guid categoryId, string terms, string? placeType, DateTime nowUtc)
    {
        var m = new DiscoveryCategoryMapping { TenantId = tenantId, CategoryId = categoryId, CreatedAtUtc = nowUtc };
        m.Update(terms, placeType, nowUtc, touch: false);
        return m;
    }

    public void Update(string terms, string? placeType, DateTime nowUtc, bool touch = true)
    {
        var list = (terms ?? string.Empty).Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) throw new DomainException("Enter at least one search phrase.");
        if (list.Count > 5) throw new DomainException("At most 5 search phrases per category.");
        if (list.Any(t => t.Length > 100)) throw new DomainException("Search phrases must be 100 characters or fewer.");
        SearchTerms = string.Join('\n', list);
        PlaceType = Guard.Optional(placeType, 60, "Place type");
        if (touch) UpdatedAtUtc = nowUtc;
    }

    public IReadOnlyList<string> Terms => SearchTerms.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    public void SetActive(bool active, DateTime nowUtc) { IsActive = active; UpdatedAtUtc = nowUtc; }
}

/// <summary>A saved search location (tenant data) offered in the discovery wizard.</summary>
public class DiscoveryLocationPreset : AuditableEntity, ITenantOwned
{
    private DiscoveryLocationPreset() { }
    public Guid TenantId { get; private set; }
    public string Country { get; private set; } = string.Empty;
    public string? Province { get; private set; }
    public string? City { get; private set; }
    public string? District { get; private set; }
    public string? Area { get; private set; }
    public string Label { get; private set; } = string.Empty;

    public static DiscoveryLocationPreset Create(Guid tenantId, string country, string? province, string? city, string? district, string? area, DateTime nowUtc)
    {
        var p = new DiscoveryLocationPreset
        {
            TenantId = tenantId, Country = Guard.Required(country, 100, "Country"), Province = Guard.Optional(province, 100, "Province"), City = Guard.Optional(city, 100, "City"),
            District = Guard.Optional(district, 100, "District"), Area = Guard.Optional(area, 100, "Area"), CreatedAtUtc = nowUtc
        };
        p.Label = string.Join(", ", new[] { p.Area, p.District, p.City, p.Province, p.Country }.Where(x => !string.IsNullOrEmpty(x)));
        return p;
    }
}
