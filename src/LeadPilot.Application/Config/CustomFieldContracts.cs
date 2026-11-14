using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Config;
using LeadPilot.Shared.Results;

namespace LeadPilot.Application.Config;

public sealed record CustomFieldDto(
    Guid Id, CustomEntityType EntityType, string Key, string Label, CustomFieldType Type, IReadOnlyList<string> Options, bool IsRequired,
    decimal? MinValue, decimal? MaxValue, string? Pattern, string? HelpText, bool IsFilterable, bool IsActive, int SortOrder, int Version, int ValueCount);

public sealed record SaveCustomFieldRequest(
    CustomEntityType EntityType, string Key, string Label, CustomFieldType Type, IReadOnlyList<string>? Options, bool IsRequired,
    decimal? MinValue, decimal? MaxValue, string? Pattern, string? HelpText, bool IsFilterable, int SortOrder = 0, int? ExpectedVersion = null) : TenantScopedRequest;

/// <summary>One field with the value the record currently holds (null = none). <see cref="Raw"/> is what an edit form should show; <see cref="Display"/> what people read.</summary>
public sealed record CustomFieldValueDto(CustomFieldDto Field, string? Raw, string? Display);

public sealed record CustomFieldFilter(string Key, string Op, string Value);

public interface ICustomFieldService
{
    Task<IReadOnlyList<CustomFieldDto>> ListAsync(CustomEntityType? entityType, bool includeInactive, CancellationToken ct = default);
    Task<Result<CustomFieldDto>> CreateAsync(SaveCustomFieldRequest request, CancellationToken ct = default);
    /// <summary>Key, record type and field type never change; everything else does.</summary>
    Task<Result<CustomFieldDto>> UpdateAsync(Guid id, SaveCustomFieldRequest request, CancellationToken ct = default);
    Task<Result<CustomFieldDto>> SetActiveAsync(Guid id, bool active, CancellationToken ct = default);
    Task<Result<IReadOnlyList<CustomFieldValueDto>>> GetValuesAsync(CustomEntityType entityType, Guid entityId, CancellationToken ct = default);
    /// <summary>Validates every given key against its field (type, options, range, pattern, required) and stores them all or none. A null value clears the field. Fields not mentioned are left alone.</summary>
    Task<Result<IReadOnlyList<CustomFieldValueDto>>> SetValuesAsync(CustomEntityType entityType, Guid entityId, IReadOnlyDictionary<string, string?> values, CancellationToken ct = default);
    /// <summary>Display values per record for CSV export: record id → (field key → display).</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>> DisplayValuesAsync(CustomEntityType entityType, IReadOnlyCollection<Guid> entityIds, CancellationToken ct = default);
}
