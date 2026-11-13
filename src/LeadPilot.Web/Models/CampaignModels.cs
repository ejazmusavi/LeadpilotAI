using System.ComponentModel.DataAnnotations;
using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Crm;

namespace LeadPilot.Web.Models;

public class CampaignListViewModel
{
    public PagedResult<CampaignListItemDto> Result { get; set; } = default!;
    public CampaignQuery Query { get; set; } = new();
    public IDictionary<string, string?> QueryValues { get; set; } = new Dictionary<string, string?>();
    public bool CanCreate { get; set; }
    public CampaignSettingsDto Settings { get; set; } = default!;
}

public class CampaignDetailsViewModel
{
    public CampaignDto Campaign { get; set; } = default!;
    public string Tab { get; set; } = "recipients";
    public PagedResult<RecipientDto>? Recipients { get; set; }
    public PagedResult<ExecutionLogDto>? Log { get; set; }
    public PagedResult<ConversionDto>? Conversions { get; set; }
    public RecipientQuery RecipientQuery { get; set; } = new();
    public IDictionary<string, string?> QueryValues { get; set; } = new Dictionary<string, string?>();
    public bool CanUpdate { get; set; } public bool CanExecute { get; set; } public bool CanApprove { get; set; } public bool CanCancel { get; set; } public bool CanDelete { get; set; } public bool CanCreate { get; set; }
    public CampaignSettingsDto Settings { get; set; } = default!;
    public DryRunDto? DryRun { get; set; }
    public string LocalSchedule { get; set; } = "";
    public string TimeZone { get; set; } = "UTC";
}

public class CampaignStatsViewModel { public CampaignDto Campaign { get; set; } = default!; public CampaignStatsDto Stats { get; set; } = default!; }

public class CampaignFormModel
{
    public Guid? Id { get; set; }
    [Required, StringLength(150)] public string Name { get; set; } = string.Empty;
    [StringLength(500)] public string? Description { get; set; }
    public CampaignType Type { get; set; } = CampaignType.Manual;
    public Guid? TemplateId { get; set; }
    public Guid? ChannelAccountId { get; set; }
    public bool IncludeOptOutFooter { get; set; } = true;
    /// <summary>Local date and time in the workspace time zone (datetime-local), converted to UTC on save.</summary>
    public string? ScheduledLocal { get; set; }
    public int? FollowUpDelayDays { get; set; }
    // audience
    public List<Guid> StatusIds { get; set; } = []; public List<Guid> StageIds { get; set; } = []; public List<Guid> CategoryIds { get; set; } = [];
    public List<Guid> TagIds { get; set; } = []; public List<Guid> ExcludeTagIds { get; set; } = []; public List<Guid> OwnerUserIds { get; set; } = [];
    public bool Unassigned { get; set; }
    public string? Countries { get; set; } public string? Provinces { get; set; } public string? Cities { get; set; }
    public List<LeadSourceType> SourceTypes { get; set; } = [];
    [Range(0, 100)] public int? MinScore { get; set; } [Range(0, 100)] public int? MaxScore { get; set; }
    [Range(0, 3650)] public int? LastContactedBeforeDays { get; set; }
    public bool NeverContacted { get; set; }
    /// <summary>"", "yes" or "no".</summary>
    public string? HasReply { get; set; }
    public bool OnePerLead { get; set; } = true;
    public Guid? ParentCampaignId { get; set; }
    public FollowUpRule FollowUpRule { get; set; } = FollowUpRule.DeliveredNotReplied;
    [Range(0, 365)] public int FollowUpAfterDays { get; set; } = 3;
    public List<Guid> AdvancedStageIds { get; set; } = [];
    // limits
    [Range(1, Campaign.HardMaxBatchSize)] public int BatchSize { get; set; } = Campaign.DefaultBatchSize;
    [Range(Campaign.HardMinIntervalSeconds, 86400)] public int MinIntervalSeconds { get; set; } = Campaign.DefaultMinIntervalSeconds;
    [Range(Campaign.HardMinIntervalSeconds, 86400)] public int MaxIntervalSeconds { get; set; } = Campaign.DefaultMaxIntervalSeconds;
    [Range(1, Campaign.HardMaxRecipients)] public int MaxRecipients { get; set; } = Campaign.DefaultMaxRecipients;
    [Range(1, 1000)] public int? DailyLimit { get; set; }
    [Range(1, 100)] public int? FailureThresholdPercent { get; set; }
    [Range(0, 365)] public int? CooldownDays { get; set; }

    public static List<string> Split(string? v) => (v ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public AudienceDefinition ToAudience() => new()
    {
        StatusIds = StatusIds, StageIds = StageIds, CategoryIds = CategoryIds, TagIds = TagIds, ExcludeTagIds = ExcludeTagIds, OwnerUserIds = OwnerUserIds, Unassigned = Unassigned,
        Countries = Split(Countries), Provinces = Split(Provinces), Cities = Split(Cities), SourceTypes = SourceTypes, MinScore = MinScore, MaxScore = MaxScore,
        LastContactedBeforeDays = LastContactedBeforeDays, NeverContacted = NeverContacted, HasReply = HasReply switch { "yes" => true, "no" => false, _ => null }, OnePerLead = OnePerLead,
        FollowUp = ParentCampaignId is { } p && Type is CampaignType.FollowUp or CampaignType.ReEngagement ? new FollowUpSpec(p, FollowUpRule, FollowUpAfterDays, AdvancedStageIds) : null,
    };

    public static CampaignFormModel From(CampaignDto c, string? localSchedule)
    {
        var a = c.Audience;
        return new CampaignFormModel
        {
            Id = c.Id, Name = c.Name, Description = c.Description, Type = c.Type, TemplateId = c.TemplateId, ChannelAccountId = c.ChannelAccountId, IncludeOptOutFooter = c.IncludeOptOutFooter, ScheduledLocal = localSchedule,
            FollowUpDelayDays = c.FollowUpDelayDays, StatusIds = a.StatusIds.ToList(), StageIds = a.StageIds.ToList(), CategoryIds = a.CategoryIds.ToList(), TagIds = a.TagIds.ToList(), ExcludeTagIds = a.ExcludeTagIds.ToList(),
            OwnerUserIds = a.OwnerUserIds.ToList(), Unassigned = a.Unassigned, Countries = string.Join(", ", a.Countries), Provinces = string.Join(", ", a.Provinces), Cities = string.Join(", ", a.Cities), SourceTypes = a.SourceTypes.ToList(),
            MinScore = a.MinScore, MaxScore = a.MaxScore, LastContactedBeforeDays = a.LastContactedBeforeDays, NeverContacted = a.NeverContacted, HasReply = a.HasReply switch { true => "yes", false => "no", _ => "" }, OnePerLead = a.OnePerLead,
            ParentCampaignId = a.FollowUp?.ParentCampaignId, FollowUpRule = a.FollowUp?.Rule ?? FollowUpRule.DeliveredNotReplied, FollowUpAfterDays = a.FollowUp?.AfterDays ?? 3, AdvancedStageIds = a.FollowUp?.AdvancedStageIds?.ToList() ?? [],
            BatchSize = c.BatchSize, MinIntervalSeconds = c.MinIntervalSeconds, MaxIntervalSeconds = c.MaxIntervalSeconds, MaxRecipients = c.MaxRecipients, DailyLimit = c.DailyLimit, FailureThresholdPercent = c.FailureThresholdPercent, CooldownDays = c.CooldownDays,
        };
    }
}

public class CampaignLookups
{
    public IReadOnlyList<TemplateDto> Templates { get; set; } = [];
    public IReadOnlyList<ChannelAccountDto> Accounts { get; set; } = [];
    public IReadOnlyList<StatusDto> Statuses { get; set; } = [];
    public IReadOnlyList<StageDto> Stages { get; set; } = [];
    public IReadOnlyList<CategoryDto> Categories { get; set; } = [];
    public IReadOnlyList<TagDto> Tags { get; set; } = [];
    public IReadOnlyList<LeadPilot.Application.Contracts.MemberDto> Users { get; set; } = [];
    public IReadOnlyList<CampaignListItemDto> ParentCandidates { get; set; } = [];
}

public class CampaignFormViewModel { public CampaignFormModel Form { get; set; } = new(); public CampaignLookups Lookups { get; set; } = new(); public CampaignSettingsDto Settings { get; set; } = default!; public string TimeZone { get; set; } = "UTC"; public bool CanPreview { get; set; } }

public class CampaignSettingsFormModel
{
    [Range(0, 365)] public int CooldownDays { get; set; }
    public bool ApprovalRequired { get; set; }
    public bool CreatorCannotApprove { get; set; } = true;
    [Range(1, 90)] public int AttributionWindowDays { get; set; }
    [Range(1, 100)] public int FailureThresholdPercent { get; set; }
    [Range(1, 1000)] public int FailureMinSample { get; set; }
    [Required, StringLength(200)] public string OptOutFooter { get; set; } = string.Empty;
    [Range(1, 1000000)] public int? MonthlyMessageLimit { get; set; }
    public string? TestNumbers { get; set; }
}

public class CampaignSettingsViewModel { public CampaignSettingsDto Settings { get; set; } = default!; public CampaignSettingsFormModel Form { get; set; } = new(); public bool CanManage { get; set; } }
