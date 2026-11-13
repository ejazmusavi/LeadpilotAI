using LeadPilot.Application.Common;
using LeadPilot.Domain.Crm;
using LeadPilot.Shared.Results;

namespace LeadPilot.Application.Crm;

/// <summary>Leads of the CURRENT tenant. Edge policies check the permission; every method also verifies tenant ownership of each id it is given.</summary>
public interface ILeadService
{
    Task<PagedResult<LeadListItemDto>> ListAsync(LeadQuery query, CancellationToken ct = default);
    Task<Result<LeadDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<LeadDto>> CreateAsync(CreateLeadRequest request, CancellationToken ct = default);
    /// <summary>For server-side intake only (discovery approval); the HTTP API cannot reach it. Same duplicate check, pipeline defaults, contacts and scoring as CreateAsync.</summary>
    Task<Result<LeadDto>> CreateFromSourceAsync(CreateLeadRequest request, LeadSourceType source, string? externalId, string? attribution, DateTime? retrievedAtUtc, CancellationToken ct = default);
    Task<Result<LeadDto>> UpdateAsync(Guid id, UpdateLeadRequest request, CancellationToken ct = default);
    Task<Result> ArchiveAsync(Guid id, CancellationToken ct = default);
    Task<Result> RestoreAsync(Guid id, CancellationToken ct = default);
    Task<Result<LeadDto>> ChangeStageAsync(Guid id, Guid stageId, CancellationToken ct = default);
    Task<Result<LeadDto>> AssignAsync(Guid id, Guid? ownerUserId, CancellationToken ct = default);
    Task<Result<PagedResult<ActivityDto>>> ActivitiesAsync(Guid id, int page, int pageSize, CancellationToken ct = default);
    Task<IReadOnlyList<DuplicateMatch>> FindDuplicatesAsync(DuplicateProbe probe, CancellationToken ct = default);
    Task<IReadOnlyList<OwnerDto>> ListOwnersAsync(CancellationToken ct = default);
    Task<CrmLookupsDto> LookupsAsync(CancellationToken ct = default);
    Task<Result<BoardDto>> BoardAsync(Guid? pipelineId, int cardsPerColumn, CancellationToken ct = default);

    Task<BulkResult> BulkAssignAsync(BulkAssignRequest request, CancellationToken ct = default);
    Task<BulkResult> BulkTagAsync(BulkTagRequest request, CancellationToken ct = default);
    Task<BulkResult> BulkStageAsync(BulkStageRequest request, CancellationToken ct = default);
    Task<BulkResult> BulkArchiveAsync(BulkArchiveRequest request, CancellationToken ct = default);
}

public interface IContactService
{
    Task<Result<IReadOnlyList<ContactDto>>> ListAsync(Guid leadId, CancellationToken ct = default);
    Task<Result<ContactDto>> GetAsync(Guid contactId, CancellationToken ct = default);
    Task<Result<ContactDto>> AddAsync(Guid leadId, SaveContactRequest request, CancellationToken ct = default);
    Task<Result<ContactDto>> UpdateAsync(Guid contactId, SaveContactRequest request, CancellationToken ct = default);
    Task<Result> RemoveAsync(Guid contactId, CancellationToken ct = default);
}

/// <summary>Append-only consent history. There is deliberately no update or delete.</summary>
public interface IConsentService
{
    Task<Result<PagedResult<ConsentRecordDto>>> HistoryForContactAsync(Guid contactId, int page, int pageSize, CancellationToken ct = default);
    Task<Result<PagedResult<ConsentRecordDto>>> HistoryForLeadAsync(Guid leadId, int page, int pageSize, CancellationToken ct = default);
    Task<Result<ConsentRecordDto>> RecordAsync(Guid contactId, RecordConsentRequest request, CancellationToken ct = default);
}

public interface ISuppressionService
{
    Task<PagedResult<SuppressionDto>> ListAsync(SuppressionQuery query, CancellationToken ct = default);
    Task<Result<SuppressionDto>> AddAsync(AddSuppressionRequest request, CancellationToken ct = default);
    Task<Result> RemoveAsync(Guid id, CancellationToken ct = default);
}

/// <summary>The single gate that decides whether a contact may be contacted. Lead score never enters the decision.</summary>
public interface IContactEligibilityService
{
    Task<EligibilityDecision> CheckAsync(Guid contactId, ConsentChannel channel, ConsentPurpose purpose, CancellationToken ct = default);
    /// <summary>Contacts that pass the gate, computed in the database: suppressed, unconsented, revoked, invalid, removed and archived-lead contacts never appear.</summary>
    Task<PagedResult<EligibleContactDto>> QueryAudienceAsync(AudienceQuery query, CancellationToken ct = default);
}

public interface INoteService
{
    Task<Result<PagedResult<NoteDto>>> ListAsync(Guid leadId, int page, int pageSize, CancellationToken ct = default);
    Task<Result<NoteDto>> AddAsync(Guid leadId, AddNoteRequest request, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid noteId, CancellationToken ct = default);
}

public interface ITagService
{
    Task<IReadOnlyList<TagDto>> ListAsync(CancellationToken ct = default);
    Task<Result<TagDto>> CreateAsync(SaveTagRequest request, CancellationToken ct = default);
    Task<Result<TagDto>> RenameAsync(Guid id, SaveTagRequest request, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<Result<IReadOnlyList<LookupItem>>> SetLeadTagsAsync(Guid leadId, SetLeadTagsRequest request, CancellationToken ct = default);
}

public interface IPipelineService
{
    Task<IReadOnlyList<PipelineDto>> ListAsync(CancellationToken ct = default);
    Task<Result<PipelineDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<PipelineDto>> CreateAsync(SavePipelineRequest request, CancellationToken ct = default);
    Task<Result<PipelineDto>> RenameAsync(Guid id, SavePipelineRequest request, CancellationToken ct = default);
    Task<Result<PipelineDto>> SetDefaultAsync(Guid id, CancellationToken ct = default);
    Task<Result<PipelineDto>> SetActiveAsync(Guid id, bool active, CancellationToken ct = default);
    Task<Result<PipelineDto>> AddStageAsync(Guid pipelineId, SaveStageRequest request, CancellationToken ct = default);
    Task<Result<PipelineDto>> UpdateStageAsync(Guid pipelineId, Guid stageId, SaveStageRequest request, CancellationToken ct = default);
    Task<Result<PipelineDto>> MoveStageAsync(Guid pipelineId, Guid stageId, int delta, CancellationToken ct = default);
    Task<Result<PipelineDto>> DeleteStageAsync(Guid pipelineId, Guid stageId, CancellationToken ct = default);
}

/// <summary>Business categories and lead statuses (tenant data).</summary>
public interface ILeadCatalogService
{
    Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(CancellationToken ct = default);
    Task<Result<CategoryDto>> CreateCategoryAsync(SaveCategoryRequest request, CancellationToken ct = default);
    Task<Result<CategoryDto>> RenameCategoryAsync(Guid id, SaveCategoryRequest request, CancellationToken ct = default);
    Task<Result<CategoryDto>> SetCategoryActiveAsync(Guid id, bool active, CancellationToken ct = default);
    Task<IReadOnlyList<StatusDto>> ListStatusesAsync(CancellationToken ct = default);
    Task<Result<StatusDto>> CreateStatusAsync(SaveStatusRequest request, CancellationToken ct = default);
    Task<Result<StatusDto>> UpdateStatusAsync(Guid id, SaveStatusRequest request, CancellationToken ct = default);
    Task<Result<StatusDto>> SetStatusActiveAsync(Guid id, bool active, CancellationToken ct = default);
}

public interface IScoringService
{
    Task<ScoringRuleSetDto> GetActiveAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ScoringRuleSetDto>> ListVersionsAsync(CancellationToken ct = default);
    Task<Result<ScoringRuleSetDto>> PublishAsync(PublishScoringRulesRequest request, CancellationToken ct = default);
    Task<Result<LeadDto>> RescoreLeadAsync(Guid leadId, CancellationToken ct = default);
    Task<RescoreResult> RescoreAllAsync(CancellationToken ct = default);
    Task<Result<IReadOnlyList<ScoreHistoryDto>>> HistoryAsync(Guid leadId, int take, CancellationToken ct = default);
}

public interface ILeadImportService
{
    Task<PagedResult<ImportJobDto>> ListAsync(int page, int pageSize, CancellationToken ct = default);
    Task<Result<ImportJobDto>> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Validates and stores the file (size, type, row and column limits) and proposes a column mapping. Creates no leads.</summary>
    Task<Result<ImportPreviewDto>> UploadAsync(UploadedFile file, CancellationToken ct = default);
    Task<Result<ImportPreviewDto>> PreviewAsync(Guid id, CancellationToken ct = default);
    Task<Result<ImportJobDto>> RunAsync(Guid id, RunImportRequest request, CancellationToken ct = default);
    Task<Result<PagedResult<ImportRowErrorDto>>> ErrorsAsync(Guid id, ImportErrorKind? kind, int page, int pageSize, CancellationToken ct = default);
    Task<Result<ExportFile>> ErrorReportAsync(Guid id, CancellationToken ct = default);
    Task<Result<ImportJobDto>> ResolveDuplicateAsync(Guid jobId, Guid errorId, ResolveDuplicateRequest request, CancellationToken ct = default);
}

public interface ILeadExportService
{
    /// <summary>Exports the leads matching <paramref name="query"/> (paging fields ignored) up to the configured limit. Audited.</summary>
    Task<Result<ExportFile>> ExportAsync(LeadQuery query, ExportFormat format, CancellationToken ct = default);
}

/// <summary>Recomputes stored lead scores from the active rules (called after anything that feeds a rule changes). Never throws for unknown ids.</summary>
public interface ILeadScorer
{
    Task RescoreAsync(IReadOnlyCollection<Guid> leadIds, CancellationToken ct = default);
}

public interface IOpportunityService
{
    Task<Result<IReadOnlyList<OpportunityDto>>> ListForLeadAsync(Guid leadId, CancellationToken ct = default);
    Task<Result<OpportunityDto>> CreateAsync(Guid leadId, SaveOpportunityRequest request, CancellationToken ct = default);
    Task<Result<OpportunityDto>> UpdateAsync(Guid id, SaveOpportunityRequest request, CancellationToken ct = default);
    Task<Result<OpportunityDto>> ChangeStageAsync(Guid id, Guid stageId, CancellationToken ct = default);
}

/// <summary>Reads rows (first row = header) from an uploaded spreadsheet. Never evaluates formulas.</summary>
public interface ITabularFileReader
{
    IEnumerable<string[]> ReadRows(Stream content, ImportFileType type);
}

/// <summary>Tenant-scoped file storage. Keys are tenant-relative; callers never pass raw paths.</summary>
public interface ITenantFileStore
{
    Task<string> SaveAsync(string area, string fileName, Stream content, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public sealed class CrmOptions
{
    public const string Section = "Crm";
    public long MaxImportFileBytes { get; set; } = 5 * 1024 * 1024;
    public int MaxImportRows { get; set; } = 5000;
    public int MaxExportRows { get; set; } = 10_000;
    public int MaxBulkItems { get; set; } = 500;
    /// <summary>Sum of uncompressed XLSX parts; protects against zip bombs.</summary>
    public long MaxXlsxUncompressedBytes { get; set; } = 50L * 1024 * 1024;
    public string StorageRoot { get; set; } = "data/files";
    /// <summary>Tenant setting key holding the calling code (e.g. 966) used to expand local phone numbers.</summary>
    public const string DefaultCountryCodeSetting = "Crm.DefaultCountryCode";
}

/// <summary>Creates or merges a lead from an external source (discovery approval). Reuses the CRM duplicate detector, pipeline defaults, contacts and scoring. Contacts always get DataOrigin.Public and NO consent record.</summary>
public interface ILeadIntakeService
{
    Task<Result<LeadIntakeResult>> IntakeAsync(LeadIntakeRequest request, CancellationToken ct = default);
}
