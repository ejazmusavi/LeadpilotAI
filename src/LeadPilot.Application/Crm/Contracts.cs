using LeadPilot.Application.Abstractions;
using LeadPilot.Domain.Crm;

namespace LeadPilot.Application.Crm;

/// <summary>Request bodies inherit this so a client-supplied TenantId is detected and rejected by the global spoof filter (it is never used).</summary>
public abstract record TenantScopedRequest : ITenantScopedRequest
{
    public Guid? TenantId { get; init; }
}

public sealed record LookupItem(Guid Id, string Name);

// ---- leads ----
public sealed record ContactInput(string? Name, string? Role, string? Phone, string? Email, DataOrigin DataOrigin = DataOrigin.Provided, bool IsPrimary = false);

public sealed record CreateLeadRequest(
    string Name, Guid? CategoryId = null, string? Country = null, string? Province = null, string? City = null, string? District = null, string? Area = null,
    string? Address = null, string? Website = null, int Confidence = 50, Guid? StageId = null, Guid? StatusId = null, Guid? OwnerUserId = null,
    LeadSourceType? SourceType = null, string? SourceName = null, string? SourceUrl = null, IReadOnlyList<ContactInput>? Contacts = null,
    IReadOnlyList<Guid>? TagIds = null, bool AllowDuplicate = false) : TenantScopedRequest;

public sealed record UpdateLeadRequest(
    string Name, Guid? CategoryId = null, string? Country = null, string? Province = null, string? City = null, string? District = null, string? Area = null,
    string? Address = null, string? Website = null, int Confidence = 50, Guid? StatusId = null, int? ExpectedVersion = null) : TenantScopedRequest;

public sealed record ChangeStageRequest(Guid StageId) : TenantScopedRequest;
public sealed record AssignLeadRequest(Guid? OwnerUserId) : TenantScopedRequest;

public sealed record LeadSourceDto(LeadSourceType Type, string? Name, string? Url, string? ExternalId, DateTime RetrievedAtUtc);

public sealed record LeadListItemDto(
    Guid Id, string Name, string? Category, string? City, string? Country, string? Website, string Stage, StageKind StageKind, string Status,
    Guid? OwnerUserId, string? OwnerName, int Score, int Confidence, bool IsArchived, string? PrimaryContactName, string? PrimaryPhone, string? PrimaryEmail,
    IReadOnlyList<string> Tags, DateTime CreatedAtUtc, DateTime? LastActivityAtUtc);

public sealed record LeadDto(
    Guid Id, string Name, Guid? CategoryId, string? CategoryName, string? Country, string? Province, string? City, string? District, string? Area, string? Address,
    string? Website, string? WebsiteDomain, Guid PipelineId, string PipelineName, Guid StageId, string StageName, StageKind StageKind, Guid StatusId, string StatusName,
    Guid? OwnerUserId, string? OwnerName, int Score, int? ScoreRuleVersion, DateTime? ScoredAtUtc, IReadOnlyList<RuleMatch> ScoreBreakdown, int Confidence,
    LeadSourceType PrimarySourceType, IReadOnlyList<LeadSourceDto> Sources, IReadOnlyList<LookupItem> Tags, int ContactCount, bool IsArchived, DateTime? ArchivedAtUtc,
    DateTime CreatedAtUtc, string? CreatedBy, DateTime? UpdatedAtUtc, DateTime? LastActivityAtUtc, int Version);

public enum ArchivedFilter { Active = 0, Archived = 1, All = 2 }
public enum LeadSort { Created = 0, Updated = 1, Name = 2, Score = 3, Stage = 4, City = 5 }

public class LeadQuery
{
    public string? Search { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? StatusId { get; set; }
    public Guid? StageId { get; set; }
    public StageKind? StageKind { get; set; }
    public Guid? OwnerUserId { get; set; }
    public bool Unassigned { get; set; }
    public Guid? TagId { get; set; }
    public string? Country { get; set; }
    public string? Province { get; set; }
    public string? City { get; set; }
    public int? MinScore { get; set; }
    public int? MaxScore { get; set; }
    public LeadSourceType? SourceType { get; set; }
    public DateTime? CreatedFromUtc { get; set; }
    public DateTime? CreatedToUtc { get; set; }
    public ArchivedFilter Archived { get; set; } = ArchivedFilter.Active;
    public LeadSort Sort { get; set; } = LeadSort.Created;
    public bool Descending { get; set; } = true;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;

    public LeadQuery Clone() => (LeadQuery)MemberwiseClone();
}

public sealed record DuplicateProbe(string? Name, string? City, string? Website, IReadOnlyList<string> PhonesE164, Guid? ExcludeLeadId = null);
public sealed record DuplicateMatch(Guid LeadId, string LeadName, string? City, bool IsArchived, IReadOnlyList<string> Reasons)
{
    public const string ReasonNameCity = "same name and city";
    public const string ReasonDomain = "same website domain";
    public const string ReasonPhone = "same phone number";
}

public sealed record DuplicateCheckRequest(string? Name, string? City, string? Website, IReadOnlyList<string>? Phones) : TenantScopedRequest;

public sealed record BulkItemResult(Guid LeadId, bool Success, string? Error);
public sealed record BulkResult(int Requested, int Succeeded, int Failed, IReadOnlyList<BulkItemResult> Items);
public sealed record BulkAssignRequest(IReadOnlyList<Guid> LeadIds, Guid? OwnerUserId) : TenantScopedRequest;
public sealed record BulkTagRequest(IReadOnlyList<Guid> LeadIds, Guid TagId, bool Remove = false) : TenantScopedRequest;
public sealed record BulkStageRequest(IReadOnlyList<Guid> LeadIds, Guid StageId) : TenantScopedRequest;
public sealed record BulkArchiveRequest(IReadOnlyList<Guid> LeadIds, bool Restore = false) : TenantScopedRequest;

public sealed record ActivityDto(Guid Id, LeadActivityType Type, string Summary, string? ActorName, DateTime OccurredAtUtc);

public sealed record OwnerDto(Guid UserId, string DisplayName, string Email);

// ---- contacts, consent ----
public sealed record ConsentStateDto(ConsentChannel Channel, ConsentPurpose Purpose, ConsentStatus Status, DateTime OccurredAtUtc, string Source);

public sealed record ContactDto(
    Guid Id, Guid LeadId, string? Name, string? Role, string? Phone, string? PhoneRaw, string? Email, DataOrigin DataOrigin, DateTime? PhoneVerifiedAtUtc,
    bool IsPrimary, IReadOnlyList<ConsentStateDto> Consent);

public sealed record SaveContactRequest(string? Name, string? Role, string? Phone, string? Email, DataOrigin DataOrigin = DataOrigin.Provided, bool IsPrimary = false) : TenantScopedRequest;

public sealed record RecordConsentRequest(
    ConsentChannel Channel, ConsentPurpose Purpose, ConsentStatus Status, string Source, string? EvidenceRef = null, string? Note = null, DateTime? OccurredAtUtc = null) : TenantScopedRequest;

public sealed record ConsentRecordDto(
    Guid Id, Guid ContactId, string? ContactName, ConsentChannel Channel, ConsentPurpose Purpose, ConsentStatus Status, string Source, string? EvidenceRef, string? Note,
    DateTime OccurredAtUtc, DateTime RecordedAtUtc, string? RecordedByName);

public sealed record SuppressionDto(Guid Id, SuppressionChannel Channel, string Value, SuppressionReason Reason, string? Note, DateTime CreatedAtUtc, string? CreatedByName);
public sealed record AddSuppressionRequest(SuppressionChannel Channel, string Value, SuppressionReason Reason, string? Note = null) : TenantScopedRequest;

public class SuppressionQuery
{
    public string? Search { get; set; }
    public SuppressionChannel? Channel { get; set; }
    public SuppressionReason? Reason { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public class AudienceQuery
{
    public ConsentChannel Channel { get; set; } = ConsentChannel.WhatsApp;
    public ConsentPurpose Purpose { get; set; } = ConsentPurpose.Marketing;
    public Guid? CategoryId { get; set; }
    public Guid? StageId { get; set; }
    public Guid? TagId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public int? MinScore { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}

public sealed record EligibleContactDto(Guid ContactId, Guid LeadId, string LeadName, string? ContactName, string Value, int LeadScore);

// ---- notes, tags ----
public sealed record NoteDto(Guid Id, Guid LeadId, string Body, string? AuthorName, DateTime CreatedAtUtc);
public sealed record AddNoteRequest(string Body) : TenantScopedRequest;
public sealed record TagDto(Guid Id, string Name, int LeadCount);
public sealed record SaveTagRequest(string Name) : TenantScopedRequest;
public sealed record SetLeadTagsRequest(IReadOnlyList<Guid> TagIds) : TenantScopedRequest;

// ---- pipelines & catalog ----
public sealed record StageDto(Guid Id, Guid PipelineId, string Name, int Position, StageKind Kind, int LeadCount);
public sealed record PipelineDto(Guid Id, string Name, bool IsDefault, bool IsActive, IReadOnlyList<StageDto> Stages, int LeadCount);
public sealed record SavePipelineRequest(string Name) : TenantScopedRequest;
public sealed record SaveStageRequest(string Name, StageKind Kind = StageKind.Open) : TenantScopedRequest;
public sealed record MoveStageRequest(int Delta) : TenantScopedRequest;

public sealed record BoardCardDto(Guid Id, string Name, string? City, string? Category, int Score, string? OwnerName, string? PrimaryPhone);
public sealed record BoardColumnDto(StageDto Stage, IReadOnlyList<BoardCardDto> Cards);
public sealed record BoardDto(PipelineDto Pipeline, IReadOnlyList<BoardColumnDto> Columns, int CardsPerColumn);

public sealed record CategoryDto(Guid Id, string Name, bool IsActive, int LeadCount);
public sealed record SaveCategoryRequest(string Name) : TenantScopedRequest;
public sealed record StatusDto(Guid Id, string Name, bool IsClosed, bool IsDefault, bool IsActive, int SortOrder, int LeadCount);
public sealed record SaveStatusRequest(string Name, bool IsClosed = false) : TenantScopedRequest;

public sealed record CrmLookupsDto(
    IReadOnlyList<LookupItem> Categories, IReadOnlyList<LookupItem> Statuses, PipelineDto? DefaultPipeline, IReadOnlyList<LookupItem> Tags, IReadOnlyList<OwnerDto> Owners);

// ---- scoring ----
public sealed record ScoringRuleDto(Guid Id, ScoreDimension Dimension, string Target, int Points, string? Description, bool IsEnabled);
public sealed record ScoringRuleSetDto(Guid Id, int Version, bool IsActive, string? Note, DateTime CreatedAtUtc, string? CreatedBy, IReadOnlyList<ScoringRuleDto> Rules);
public sealed record ScoringRuleInput(ScoreDimension Dimension, string Target, int Points, string? Description = null, bool IsEnabled = true);
public sealed record PublishScoringRulesRequest(IReadOnlyList<ScoringRuleInput> Rules, string? Note = null) : TenantScopedRequest;
public sealed record RescoreResult(int Processed, int Changed, int RuleVersion);
public sealed record ScoreHistoryDto(int Score, int RuleVersion, DateTime CalculatedAtUtc, IReadOnlyList<RuleMatch> Matches);

// ---- imports ----
public sealed record ImportJobDto(
    Guid Id, string FileName, ImportFileType FileType, long FileSizeBytes, ImportStatus Status, DuplicatePolicy DuplicatePolicy, int TotalRows, int CreatedCount, int MergedCount,
    int SkippedDuplicateCount, int PendingDuplicateCount, int ErrorCount, string? FailureReason, DateTime CreatedAtUtc, string? CreatedBy, DateTime? CompletedAtUtc);

public sealed record ImportColumnDto(int Index, string Header, ImportField? SuggestedField);
public sealed record ImportPreviewDto(ImportJobDto Job, IReadOnlyList<ImportColumnDto> Columns, IReadOnlyList<IReadOnlyList<string>> SampleRows);

public sealed record RunImportRequest(
    IReadOnlyList<ColumnMapping> Mapping, DuplicatePolicy DuplicatePolicy = DuplicatePolicy.Skip, bool CreateMissingCategories = false,
    Guid? DefaultCategoryId = null, Guid? DefaultOwnerUserId = null, string? SourceName = null) : TenantScopedRequest;

public sealed record ImportRowErrorDto(
    Guid Id, int RowNumber, ImportErrorKind Kind, string? Column, string Message, Guid? DuplicateLeadId, string? DuplicateLeadName, DuplicateResolution? Resolution);

public sealed record ResolveDuplicateRequest(bool Merge) : TenantScopedRequest;

public sealed record UploadedFile(string FileName, long Length, Stream Content);

// ---- export ----
public sealed record ExportFile(string FileName, string ContentType, byte[] Content, int RowCount);
public enum ExportFormat { Csv = 1, Xlsx = 2 }

// ---- opportunities ----
public sealed record OpportunityDto(Guid Id, Guid LeadId, string Name, decimal Value, string Currency, DateTime? ExpectedCloseDateUtc, Guid? OwnerUserId, string? OwnerName,
    Guid StageId, string StageName, OpportunityStatus Status, DateTime CreatedAtUtc);
public sealed record SaveOpportunityRequest(string Name, decimal Value, string Currency = "USD", DateTime? ExpectedCloseDateUtc = null, Guid? OwnerUserId = null) : TenantScopedRequest;
public sealed record OpportunityStageRequest(Guid StageId) : TenantScopedRequest;

// ---- lead intake (discovery approval; the only non-client path that may write LeadSourceType.Discovery) ----
public sealed record IntakeContact(string? Phone, string? Email, string? Name = null);

public sealed record LeadIntakeRequest(
    string Name, Guid? CategoryId, string? Country, string? Province, string? City, string? District, string? Area, string? Address, string? Website, int Confidence,
    LeadSourceType SourceType, string? ExternalId, string? SourceName, string? SourceUrl, string? Attribution, IReadOnlyList<IntakeContact> Contacts, Guid? OwnerUserId,
    bool MergeIfDuplicate, DateTime RetrievedAtUtc);

public enum IntakeAction { Created = 1, Merged = 2 }
public sealed record LeadIntakeResult(Guid LeadId, IntakeAction Action, IReadOnlyList<string> MergedFields);
