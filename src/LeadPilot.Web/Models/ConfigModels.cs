using System.ComponentModel.DataAnnotations;
using LeadPilot.Application.Config;
using LeadPilot.Domain.Config;

namespace LeadPilot.Web.Models;

public class PackGalleryViewModel
{
    public IReadOnlyList<PackSummaryDto> Packs { get; set; } = [];
    public bool CanManage { get; set; }
    public bool CanExport { get; set; }
}

public class PackImportViewModel
{
    public required PackSummaryDto Pack { get; set; }
    public IReadOnlyList<(string Key, string Name)> Pipelines { get; set; } = [];
    public List<string> Sections { get; set; } = [];
    public ConflictPolicy Policy { get; set; } = ConflictPolicy.KeepMine;
    public string? DefaultPipelineKey { get; set; }
    public Dictionary<string, string> Res { get; set; } = new();
    public PackPlanDto? Plan { get; set; }
    public PackApplyResultDto? Applied { get; set; }

    public ApplyPackOptions ToOptions() => new(Policy,
        Res.Where(r => r.Value is "keep" or "replace").ToDictionary(r => r.Key, r => r.Value == "replace" ? PackResolution.Replace : PackResolution.Keep),
        Sections.Count == 0 ? null : Sections, string.IsNullOrWhiteSpace(DefaultPipelineKey) ? null : DefaultPipelineKey);
}

public class PackHistoryViewModel { public IReadOnlyList<PackApplicationDto> History { get; set; } = []; }

public class PackManageViewModel
{
    public IReadOnlyList<PackSummaryDto> Private { get; set; } = [];
    public PackExportDto? Export { get; set; }
    public PackValidationDto? Validation { get; set; }
    public string ExportKey { get; set; } = "";
    public string ExportName { get; set; } = "";
}

public class PlatformPacksViewModel { public IReadOnlyList<PackSummaryDto> Packs { get; set; } = []; }

public class CustomFieldsViewModel
{
    public IReadOnlyList<CustomFieldDto> Fields { get; set; } = [];
    public CustomEntityType Entity { get; set; } = CustomEntityType.Lead;
    public CustomFieldEditModel Form { get; set; } = new();
    public Guid? EditingId { get; set; }
}

public class CustomFieldEditModel
{
    [Required] public CustomEntityType EntityType { get; set; } = CustomEntityType.Lead;
    [Required, StringLength(40), Display(Name = "Key")] public string Key { get; set; } = string.Empty;
    [Required, StringLength(100)] public string Label { get; set; } = string.Empty;
    [Required] public CustomFieldType Type { get; set; } = CustomFieldType.Text;
    [Display(Name = "Options (one per line)")] public string? Options { get; set; }
    public bool IsRequired { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    [StringLength(200)] public string? Pattern { get; set; }
    [StringLength(200), Display(Name = "Help text")] public string? HelpText { get; set; }
    public bool IsFilterable { get; set; }
    public int SortOrder { get; set; }
    public int? ExpectedVersion { get; set; }

    public SaveCustomFieldRequest ToRequest() => new(EntityType, Key, Label, Type,
        (Options ?? "").Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), IsRequired, MinValue, MaxValue, Pattern, HelpText, IsFilterable, SortOrder, ExpectedVersion);
}
