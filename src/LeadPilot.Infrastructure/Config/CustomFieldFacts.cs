using System.Text.Json;
using LeadPilot.Domain.Config;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Config;

/// <summary>Loads typed custom field values as facts for scoring rules and workflow conditions: number → decimal, yes/no → bool, text/select/date → string (dates are ISO), multi-select → list.</summary>
internal static class CustomFieldFacts
{
    public static async Task<Dictionary<Guid, IReadOnlyDictionary<string, object?>>> LoadAsync(AppDbContext db, Guid tenantId, CustomEntityType type, IReadOnlyCollection<Guid> entityIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid, IReadOnlyDictionary<string, object?>>();
        if (entityIds.Count == 0) return result;
        var defs = await db.CustomFieldDefinitions.AsNoTracking().Where(d => d.TenantId == tenantId && d.EntityType == type && d.IsActive).ToDictionaryAsync(d => d.Id, ct);
        if (defs.Count == 0) return result;
        var values = await db.CustomFieldValues.AsNoTracking().Where(v => v.TenantId == tenantId && v.EntityType == type && entityIds.Contains(v.EntityId)).ToListAsync(ct);
        foreach (var g in values.GroupBy(v => v.EntityId))
        {
            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var v in g)
                if (defs.TryGetValue(v.FieldId, out var f)) d[f.Key] = Typed(f, v);
            result[g.Key] = d;
        }
        return result;
    }

    public static object? Typed(CustomFieldDefinition f, CustomFieldValue v) => f.Type switch
    {
        CustomFieldType.Number => v.NumberValue,
        CustomFieldType.Bool => v.BoolValue,
        CustomFieldType.Date => v.DateValue?.ToString("yyyy-MM-dd"),
        CustomFieldType.MultiSelect => (IReadOnlyCollection<string>?)(v.TextValue is null ? null : JsonSerializer.Deserialize<List<string>>(v.TextValue)),
        _ => v.TextValue,
    };
}
