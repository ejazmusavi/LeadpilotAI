using LeadPilot.Application.Crm;
using LeadPilot.Domain.Config;
using LeadPilot.Shared.Results;

namespace LeadPilot.Application.Config;

public enum ConflictPolicy { KeepMine = 1, UseThePack = 2 }

/// <summary>What happens to one pack item. The preview lists exactly these; applying performs exactly these.</summary>
public enum PackOutcome
{
    /// <summary>Nothing like it exists: it will be created.</summary>
    Create = 1,
    /// <summary>It came from this pack, you did not change it, the pack did: it will be updated.</summary>
    Update,
    /// <summary>Already matches the pack: nothing to do.</summary>
    Unchanged,
    /// <summary>The same thing already exists (and equals the pack): it is only linked to the pack from now on.</summary>
    Link,
    /// <summary>You changed it (or it differs from the pack and was not created by it): your version is kept.</summary>
    KeepYours,
    /// <summary>You changed it AND the pack changed it, and you chose the pack's version: it is overwritten (audited).</summary>
    Overwrite,
    /// <summary>It came from this pack but you deleted it: it stays deleted.</summary>
    LeaveDeleted,
    /// <summary>It came from this pack, you deleted it, and you chose to restore it.</summary>
    Recreate,
    /// <summary>It cannot be applied here (reason given); the rest of the pack is unaffected.</summary>
    Blocked,
}

public sealed record PackFieldChange(string Field, string? Before, string? After);

public sealed record PackItemDto(string Type, string Key, string Name, PackOutcome Outcome, bool IsConflict, string? Reason, IReadOnlyList<PackFieldChange> Changes, Guid? EntityId);

public sealed record PackCounts(int Create, int Update, int Unchanged, int Link, int KeepYours, int Overwrite, int LeaveDeleted, int Recreate, int Blocked, int Conflicts);

public sealed record PackPlanDto(string PackKey, int PackVersion, string PackName, bool IsSample, string? Notice, PackApplyMode Mode, IReadOnlyList<PackItemDto> Items, PackCounts Counts, IReadOnlyList<string> Notes);

public sealed record PackApplyResultDto(PackPlanDto Plan, Guid ApplicationId, DateTime AppliedAtUtc);

public enum PackResolution { Keep = 1, Replace = 2 }

/// <summary>Options of a preview/apply. <see cref="Resolutions"/> overrides the policy for single items ("type:key").</summary>
public sealed record ApplyPackOptions(
    ConflictPolicy Policy = ConflictPolicy.KeepMine, IReadOnlyDictionary<string, PackResolution>? Resolutions = null,
    IReadOnlyCollection<string>? Sections = null, string? DefaultPipelineKey = null) : TenantScopedRequest;

public sealed record PackSummaryDto(
    Guid Id, string Key, int Version, string Name, string? Description, string? Industry, PackScope Scope, bool IsPublished, bool IsSample, string? Notice, int ItemCount,
    IReadOnlyDictionary<string, int> Sections, int? AppliedVersion, DateTime? AppliedAtUtc, bool UpgradeAvailable);

public sealed record PackApplicationDto(Guid Id, string PackKey, int PackVersion, string PackName, PackApplyMode Mode, string ConflictPolicy, string SummaryJson, string? AppliedBy, DateTime AppliedAtUtc);

public sealed record PackExportRequest(string Key, string Name, string? Description, string? Industry, int Version = 1, IReadOnlyCollection<string>? Sections = null) : TenantScopedRequest;

public sealed record UploadPackRequest(string Json, bool Publish = false) : TenantScopedRequest;

public sealed record PackExportDto(string Json, string FileName, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<string> Warnings);

public sealed record PackValidationDto(bool IsValid, IReadOnlyList<PackProblem> Problems, string? Key, int? Version, string? Name, int? ItemCount);

public static class PackSections
{
    public const string Categories = "categories", Pipelines = "pipelines", CustomFields = "customFields", ScoringRules = "scoringRules", MessageTemplates = "messageTemplates",
        Agents = "agents", Catalog = "catalog", Knowledge = "knowledgeBase", Workflows = "workflows", Discovery = "discovery";
    public static readonly IReadOnlyList<string> All = [Categories, Pipelines, CustomFields, ScoringRules, MessageTemplates, Agents, Catalog, Knowledge, Workflows, Discovery];
}

public interface IPackService
{
    /// <summary>The gallery: builtin packs, published platform packs and this workspace's private packs (newest version of each key).</summary>
    Task<IReadOnlyList<PackSummaryDto>> ListAsync(CancellationToken ct = default);
    Task<Result<PackSummaryDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<string>> GetContentAsync(Guid id, CancellationToken ct = default);
    /// <summary>Dry run: what applying the pack would do, with nothing written.</summary>
    Task<Result<PackPlanDto>> PreviewAsync(Guid id, ApplyPackOptions options, CancellationToken ct = default);
    /// <summary>Applies the pack in ONE transaction (all or nothing), audited and recorded in the history.</summary>
    Task<Result<PackApplyResultDto>> ApplyAsync(Guid id, ApplyPackOptions options, CancellationToken ct = default);
    Task<IReadOnlyList<PackApplicationDto>> HistoryAsync(CancellationToken ct = default);
    Task<PackValidationDto> ValidateAsync(string json, CancellationToken ct = default);
    /// <summary>Stores (or adds a new version of) a private pack of this workspace after validating it.</summary>
    Task<Result<PackSummaryDto>> UploadPrivateAsync(string json, CancellationToken ct = default);
    Task<Result> DeletePrivateAsync(Guid id, CancellationToken ct = default);
    Task<Result<PackExportDto>> ExportAsync(PackExportRequest request, CancellationToken ct = default);
}

/// <summary>Platform administrators: publish packs that every workspace can apply.</summary>
public interface IPlatformPackService
{
    Task<IReadOnlyList<PackSummaryDto>> ListAsync(CancellationToken ct = default);
    Task<Result<PackSummaryDto>> PublishAsync(string json, bool publish, CancellationToken ct = default);
    Task<Result<PackSummaryDto>> SetPublishedAsync(Guid id, bool published, CancellationToken ct = default);
}
