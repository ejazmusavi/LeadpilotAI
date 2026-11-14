using System.ComponentModel.DataAnnotations;
using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Crm;

namespace LeadPilot.Web.Models;

/// <summary>What the signed-in user may do with leads; drives which buttons are rendered (the server re-checks every action).</summary>
public sealed record LeadAbilities(bool Create, bool Update, bool Delete, bool Assign, bool Import, bool Export, bool Consent)
{
    public static readonly LeadAbilities None = new(false, false, false, false, false, false, false);
}

public class LeadListViewModel
{
    public required LeadQuery Query { get; set; }
    public required PagedResult<LeadListItemDto> Result { get; set; }
    public required CrmLookupsDto Lookups { get; set; }
    public LeadAbilities Can { get; set; } = LeadAbilities.None;
    public IDictionary<string, string?> QueryValues => LeadFilterQuery.ToValues(Query);
}

public static class LeadFilterQuery
{
    /// <summary>The current filters as route values (no page), reused by the pager, sort links and export.</summary>
    public static IDictionary<string, string?> ToValues(LeadQuery q)
    {
        var d = new Dictionary<string, string?>();
        void Add(string k, object? v) { if (v is not null && !(v is string s && s.Length == 0) && !(v is bool b && !b)) d[k] = v is bool ? "true" : v is DateTime dt ? dt.ToString("yyyy-MM-dd") : v.ToString(); }
        Add("search", q.Search); Add("categoryId", q.CategoryId); Add("statusId", q.StatusId); Add("stageId", q.StageId); Add("stageKind", q.StageKind);
        Add("ownerUserId", q.OwnerUserId); Add("unassigned", q.Unassigned); Add("tagId", q.TagId); Add("country", q.Country); Add("province", q.Province); Add("city", q.City);
        Add("minScore", q.MinScore); Add("maxScore", q.MaxScore); Add("sourceType", q.SourceType); Add("createdFromUtc", q.CreatedFromUtc); Add("createdToUtc", q.CreatedToUtc);
        if (q.Archived != ArchivedFilter.Active) d["archived"] = q.Archived.ToString();
        if (q.Sort != LeadSort.Created) d["sort"] = q.Sort.ToString();
        if (!q.Descending) d["descending"] = "false";
        if (q.PageSize != 25) d["pageSize"] = q.PageSize.ToString();
        return d;
    }
}

public class LeadFormModel
{
    public Guid? Id { get; set; }
    [Required, StringLength(300), Display(Name = "Organization name")] public string Name { get; set; } = string.Empty;
    [Display(Name = "Category")] public Guid? CategoryId { get; set; }
    [StringLength(100)] public string? Country { get; set; }
    [StringLength(100), Display(Name = "Province / state")] public string? Province { get; set; }
    [StringLength(100)] public string? City { get; set; }
    [StringLength(100)] public string? District { get; set; }
    [StringLength(100)] public string? Area { get; set; }
    [StringLength(500)] public string? Address { get; set; }
    [StringLength(500)] public string? Website { get; set; }
    [Range(0, 100), Display(Name = "Data confidence (0-100)")] public int Confidence { get; set; } = 50;
    [Display(Name = "Status")] public Guid? StatusId { get; set; }
    [Display(Name = "Pipeline stage")] public Guid? StageId { get; set; }
    [Display(Name = "Owner")] public Guid? OwnerUserId { get; set; }
    public int? Version { get; set; }
    // first contact (create only)
    [StringLength(200), Display(Name = "Contact name")] public string? ContactName { get; set; }
    [StringLength(100), Display(Name = "Contact role")] public string? ContactRole { get; set; }
    [StringLength(40), Display(Name = "Contact phone")] public string? ContactPhone { get; set; }
    [StringLength(256), Display(Name = "Contact e-mail")] public string? ContactEmail { get; set; }
    [StringLength(200), Display(Name = "Source note")] public string? SourceName { get; set; }
    public bool AllowDuplicate { get; set; }
    public bool ShowDuplicateOverride { get; set; }
    public CrmLookupsDto Lookups { get; set; } = new([], [], null, [], []);
    public bool CanAssign { get; set; }
}

public class LeadDetailsViewModel
{
    public required LeadDto Lead { get; set; }
    public string Tab { get; set; } = "overview";
    public LeadAbilities Can { get; set; } = LeadAbilities.None;
    public required CrmLookupsDto Lookups { get; set; }
    public IReadOnlyList<StageDto> Stages { get; set; } = [];
    public IReadOnlyList<ContactDto> Contacts { get; set; } = [];
    public PagedResult<ActivityDto>? Activities { get; set; }
    public PagedResult<NoteDto>? Notes { get; set; }
    public PagedResult<ConsentRecordDto>? ConsentHistory { get; set; }
    public ContactFormModel ContactForm { get; set; } = new();
    public ConsentFormModel ConsentForm { get; set; } = new();
    public NoteFormModel NoteForm { get; set; } = new();
}

public class ContactFormModel
{
    public Guid? Id { get; set; }
    public Guid LeadId { get; set; }
    [StringLength(200)] public string? Name { get; set; }
    [StringLength(100)] public string? Role { get; set; }
    [StringLength(40)] public string? Phone { get; set; }
    [StringLength(256), Display(Name = "E-mail")] public string? Email { get; set; }
    [Display(Name = "Data origin")] public DataOrigin DataOrigin { get; set; } = DataOrigin.Provided;
    [Display(Name = "Primary contact")] public bool IsPrimary { get; set; }
}

public class ConsentFormModel
{
    [Required, Display(Name = "Contact")] public Guid? ContactId { get; set; }
    public ConsentChannel Channel { get; set; } = ConsentChannel.WhatsApp;
    public ConsentPurpose Purpose { get; set; } = ConsentPurpose.Marketing;
    public ConsentStatus Status { get; set; } = ConsentStatus.Granted;
    [Required, StringLength(200), Display(Name = "Source (how was it given or withdrawn?)")] public string Source { get; set; } = string.Empty;
    [StringLength(500), Display(Name = "Evidence reference")] public string? EvidenceRef { get; set; }
    [StringLength(1000)] public string? Note { get; set; }
    [Display(Name = "Date and time (UTC, optional)")] public DateTime? OccurredAtUtc { get; set; }
}

public class NoteFormModel
{
    [Required, StringLength(4000), Display(Name = "Note")] public string Body { get; set; } = string.Empty;
}

public class BoardViewModel
{
    public required BoardDto Board { get; set; }
    public required IReadOnlyList<PipelineDto> Pipelines { get; set; }
    public bool CanUpdate { get; set; }
}

public class ImportUploadModel
{
    [Display(Name = "File (.csv or .xlsx)")] public IFormFile? File { get; set; }
}

public class ImportMapViewModel
{
    public required ImportPreviewDto Preview { get; set; }
    public required CrmLookupsDto Lookups { get; set; }
    public bool CanAssign { get; set; }
    public DuplicatePolicy DuplicatePolicy { get; set; } = DuplicatePolicy.Skip;
    public bool CreateMissingCategories { get; set; }
    public Guid? DefaultCategoryId { get; set; }
    public Guid? DefaultOwnerUserId { get; set; }
    public string? SourceName { get; set; }
    public int MaxRows { get; set; }
    public List<ImportField?> Fields { get; set; } = [];
}

public class ImportResultViewModel
{
    public required ImportJobDto Job { get; set; }
    public required PagedResult<ImportRowErrorDto> Errors { get; set; }
    public ImportErrorKind? Kind { get; set; }
}

public class ImportListViewModel
{
    public required PagedResult<ImportJobDto> Result { get; set; }
    public int MaxRows { get; set; }
    public long MaxBytes { get; set; }
}

public class SuppressionViewModel
{
    public required SuppressionQuery Query { get; set; }
    public required PagedResult<SuppressionDto> Result { get; set; }
    public SuppressionFormModel Form { get; set; } = new();
}

public class SuppressionFormModel
{
    public SuppressionChannel Channel { get; set; } = SuppressionChannel.Any;
    [Required, StringLength(320), Display(Name = "Phone number or e-mail")] public string Value { get; set; } = string.Empty;
    public SuppressionReason Reason { get; set; } = SuppressionReason.OptOut;
    [StringLength(500)] public string? Note { get; set; }
}

public class ScoringViewModel
{
    public required ScoringRuleSetDto Active { get; set; }
    public required IReadOnlyList<ScoringRuleSetDto> Versions { get; set; }
    public ScoringRuleFormModel Form { get; set; } = new();
}

public class ScoringRuleFormModel
{
    public ScoreDimension Dimension { get; set; } = ScoreDimension.WebsiteAvailability;
    [Required, StringLength(200)] public string Target { get; set; } = string.Empty;
    [Range(-100, 100)] public int Points { get; set; } = 10;
    [StringLength(200)] public string? Description { get; set; }
}

public class PipelinesViewModel
{
    public required IReadOnlyList<PipelineDto> Pipelines { get; set; }
    public PipelineFormModel NewPipeline { get; set; } = new();
}

public class PipelineFormModel { [Required, StringLength(100), Display(Name = "Pipeline name")] public string Name { get; set; } = string.Empty; }

public class StageFormModel
{
    [Required, StringLength(100), Display(Name = "Stage name")] public string Name { get; set; } = string.Empty;
    public StageKind Kind { get; set; } = StageKind.Open;
}

public class TagsViewModel
{
    public required IReadOnlyList<TagDto> Tags { get; set; }
    public bool CanUpdate { get; set; }
    public bool CanDelete { get; set; }
    [Required, StringLength(50), Display(Name = "Tag name")] public string NewName { get; set; } = string.Empty;
}

public class CategoriesViewModel
{
    public required IReadOnlyList<CategoryDto> Categories { get; set; }
    public required IReadOnlyList<StatusDto> Statuses { get; set; }
}
