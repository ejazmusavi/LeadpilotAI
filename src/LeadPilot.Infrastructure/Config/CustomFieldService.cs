using LeadPilot.Application.Config;
using LeadPilot.Domain.Config;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Config;

public sealed class CustomFieldService(CrmContext crm, ILogger<CustomFieldService> logger) : ICustomFieldService
{
    public async Task<IReadOnlyList<CustomFieldDto>> ListAsync(CustomEntityType? entityType, bool includeInactive, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var q = crm.Db.CustomFieldDefinitions.AsNoTracking().Where(d => d.TenantId == tenantId);
        if (entityType is { } t) q = q.Where(d => d.EntityType == t);
        if (!includeInactive) q = q.Where(d => d.IsActive);
        var rows = await q.OrderBy(d => d.EntityType).ThenBy(d => d.SortOrder).ThenBy(d => d.Label).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var counts = await crm.Db.CustomFieldValues.AsNoTracking().Where(v => v.TenantId == tenantId && ids.Contains(v.FieldId)).GroupBy(v => v.FieldId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return rows.Select(r => Map(r, counts.GetValueOrDefault(r.Id))).ToList();
    }

    public Task<Result<CustomFieldDto>> CreateAsync(SaveCustomFieldRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            if (await crm.Db.CustomFieldDefinitions.CountAsync(d => d.TenantId == tenantId && d.EntityType == r.EntityType, ct) >= CustomFieldDefinition.MaxFieldsPerEntityType)
                return Result.Failure<CustomFieldDto>(Error.Validation($"At most {CustomFieldDefinition.MaxFieldsPerEntityType} custom fields per record type."));
            var f = CustomFieldDefinition.Create(tenantId, r.EntityType, r.Key, r.Label, r.Type, r.Options, r.IsRequired, r.MinValue, r.MaxValue, r.Pattern, r.HelpText, r.IsFilterable, r.SortOrder, crm.Now, crm.Actor);
            if (await crm.Db.CustomFieldDefinitions.AnyAsync(d => d.TenantId == tenantId && d.EntityType == f.EntityType && d.Key == f.Key, ct)) return Result.Failure<CustomFieldDto>(Error.Conflict($"A field with the key '{f.Key}' already exists."));
            crm.Db.CustomFieldDefinitions.Add(f);
            crm.Record("CustomField.Created", "CustomFieldDefinition", f.Id, $"Custom field '{f.Label}' ({f.Key}, {f.Type}) created for {f.EntityType}.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(Map(f, 0));
        }, logger);

    public Task<Result<CustomFieldDto>> UpdateAsync(Guid id, SaveCustomFieldRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var f = await crm.Db.CustomFieldDefinitions.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
            if (f is null) return Result.Failure<CustomFieldDto>(Error.NotFound("Custom field"));
            if (r.ExpectedVersion is { } v && v != f.Version) return Result.Failure<CustomFieldDto>(Error.Conflict("The field was changed by someone else. Reload it and try again."));
            if (!string.Equals(r.Key, f.Key, StringComparison.OrdinalIgnoreCase) || r.Type != f.Type || r.EntityType != f.EntityType)
                return Result.Failure<CustomFieldDto>(Error.Validation("The key, the type and the record type of a field cannot change. Create a new field instead."));
            f.Update(r.Label, r.Options, r.IsRequired, r.MinValue, r.MaxValue, r.Pattern, r.HelpText, r.IsFilterable, r.SortOrder, crm.Now, crm.Actor);
            crm.Record("CustomField.Updated", "CustomFieldDefinition", f.Id, $"Custom field '{f.Label}' ({f.Key}) updated. Existing values are kept as they are.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(Map(f, await crm.Db.CustomFieldValues.CountAsync(x => x.TenantId == tenantId && x.FieldId == f.Id, ct)));
        }, logger);

    public Task<Result<CustomFieldDto>> SetActiveAsync(Guid id, bool active, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var f = await crm.Db.CustomFieldDefinitions.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
            if (f is null) return Result.Failure<CustomFieldDto>(Error.NotFound("Custom field"));
            f.SetActive(active, crm.Now, crm.Actor);
            crm.Record(active ? "CustomField.Activated" : "CustomField.Deactivated", "CustomFieldDefinition", f.Id, $"Custom field '{f.Label}' ({f.Key}) {(active ? "activated" : "deactivated; its values are kept")}.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(Map(f, await crm.Db.CustomFieldValues.CountAsync(x => x.TenantId == tenantId && x.FieldId == f.Id, ct)));
        }, logger);

    public async Task<IReadOnlyList<string>> ValidateAsync(CustomEntityType entityType, IReadOnlyDictionary<string, string?> values, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var defs = await crm.Db.CustomFieldDefinitions.AsNoTracking().Where(d => d.TenantId == tenantId && d.EntityType == entityType && d.IsActive).ToListAsync(ct);
        var given = values.ToDictionary(kv => kv.Key.Trim().ToLowerInvariant(), kv => kv.Value, StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var k in given.Keys.Where(k => defs.All(d => d.Key != k))) errors.Add($"Unknown custom field '{(k.Length > 40 ? k[..40] : k)}'.");
        foreach (var d in defs) { d.Normalize(given.GetValueOrDefault(d.Key), out var err); if (err is not null) errors.Add(err); }
        return errors;
    }

    public async Task<Result<IReadOnlyList<CustomFieldValueDto>>> GetValuesAsync(CustomEntityType entityType, Guid entityId, CancellationToken ct = default)
    {
        if (!await EntityExistsAsync(entityType, entityId, ct)) return Result.Failure<IReadOnlyList<CustomFieldValueDto>>(Error.NotFound(entityType.ToString()));
        return Result.Success(await ValuesAsync(entityType, entityId, ct));
    }

    public Task<Result<IReadOnlyList<CustomFieldValueDto>>> SetValuesAsync(CustomEntityType entityType, Guid entityId, IReadOnlyDictionary<string, string?> values, CancellationToken ct = default) =>
        CrmFlow.GuardAsync<IReadOnlyList<CustomFieldValueDto>>(async () =>
        {
            var tenantId = crm.TenantId;
            if (!await EntityExistsAsync(entityType, entityId, ct)) return Result.Failure<IReadOnlyList<CustomFieldValueDto>>(Error.NotFound(entityType.ToString()));
            var defs = await crm.Db.CustomFieldDefinitions.Where(d => d.TenantId == tenantId && d.EntityType == entityType).ToListAsync(ct);
            var byKey = defs.ToDictionary(d => d.Key, StringComparer.Ordinal);
            var unknown = values.Keys.Where(k => !byKey.TryGetValue(k.Trim().ToLowerInvariant(), out var d) || !d.IsActive).ToList();
            if (unknown.Count > 0) return Result.Failure<IReadOnlyList<CustomFieldValueDto>>(Error.Validation($"Unknown custom field(s): {string.Join(", ", unknown.Select(u => u.Length > 40 ? u[..40] : u))}."));
            var given = values.ToDictionary(kv => kv.Key.Trim().ToLowerInvariant(), kv => kv.Value, StringComparer.Ordinal);
            var existing = await crm.Db.CustomFieldValues.Where(v => v.TenantId == tenantId && v.EntityType == entityType && v.EntityId == entityId).ToListAsync(ct);
            var errors = new List<string>();
            var changes = new List<(CustomFieldDefinition Def, CustomFieldValueData? Data)>();
            foreach (var d in defs.Where(d => d.IsActive))
            {
                var has = given.TryGetValue(d.Key, out var raw);
                // required fields: checked for every active field whenever this record is saved with at least one value, but only when the field is mentioned or has no value yet
                if (!has && existing.Any(e => e.FieldId == d.Id)) continue;
                if (!has && !d.IsRequired) continue;
                var data = d.Normalize(raw, out var err);
                if (err is not null) { errors.Add(err); continue; }
                changes.Add((d, data));
            }
            if (errors.Count > 0) return Result.Failure<IReadOnlyList<CustomFieldValueDto>>(Error.Validation(string.Join(" ", errors)));
            foreach (var (d, data) in changes)
            {
                var cur = existing.FirstOrDefault(e => e.FieldId == d.Id);
                if (data is null) { if (cur is not null) crm.Db.CustomFieldValues.Remove(cur); }
                else if (cur is null) crm.Db.CustomFieldValues.Add(CustomFieldValue.Create(tenantId, d, entityId, data, crm.Now));
                else cur.Apply(data, crm.Now);
            }
            if (changes.Count > 0)
            {
                crm.Record("CustomField.ValuesSet", "CustomFieldValue", entityId, $"{changes.Count} custom field value(s) of a {entityType.ToString().ToLowerInvariant()} changed ({string.Join(", ", changes.Select(c => c.Def.Key))}).");
                if (entityType == CustomEntityType.Lead) crm.Activity(entityId, Domain.Crm.LeadActivityType.Updated, "Custom fields updated.");
            }
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await ValuesAsync(entityType, entityId, ct));
        }, logger);

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>> DisplayValuesAsync(CustomEntityType entityType, IReadOnlyCollection<Guid> entityIds, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var result = new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
        if (entityIds.Count == 0) return result;
        var defs = await crm.Db.CustomFieldDefinitions.AsNoTracking().Where(d => d.TenantId == tenantId && d.EntityType == entityType && d.IsActive).ToDictionaryAsync(d => d.Id, d => d.Key, ct);
        if (defs.Count == 0) return result;
        foreach (var chunk in entityIds.Distinct().Chunk(1000))
        {
            var rows = await crm.Db.CustomFieldValues.AsNoTracking().Where(v => v.TenantId == tenantId && v.EntityType == entityType && chunk.Contains(v.EntityId)).Select(v => new { v.EntityId, v.FieldId, v.Display }).ToListAsync(ct);
            foreach (var g in rows.GroupBy(r => r.EntityId))
                result[g.Key] = g.Where(r => defs.ContainsKey(r.FieldId)).ToDictionary(r => defs[r.FieldId], r => r.Display);
        }
        return result;
    }

    private async Task<IReadOnlyList<CustomFieldValueDto>> ValuesAsync(CustomEntityType entityType, Guid entityId, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var defs = await crm.Db.CustomFieldDefinitions.AsNoTracking().Where(d => d.TenantId == tenantId && d.EntityType == entityType && d.IsActive).OrderBy(d => d.SortOrder).ThenBy(d => d.Label).ToListAsync(ct);
        var vals = await crm.Db.CustomFieldValues.AsNoTracking().Where(v => v.TenantId == tenantId && v.EntityType == entityType && v.EntityId == entityId).ToListAsync(ct);
        return defs.Select(d =>
        {
            var v = vals.FirstOrDefault(x => x.FieldId == d.Id);
            return new CustomFieldValueDto(Map(d, 0), v is null ? null : Raw(d, v), v?.Display);
        }).ToList();
    }

    private static string Raw(CustomFieldDefinition d, CustomFieldValue v) => d.Type switch
    {
        CustomFieldType.Number => v.NumberValue?.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        CustomFieldType.Date => v.DateValue?.ToString("yyyy-MM-dd") ?? string.Empty,
        CustomFieldType.Bool => v.BoolValue is true ? "true" : "false",
        CustomFieldType.MultiSelect => string.Join(", ", System.Text.Json.JsonSerializer.Deserialize<List<string>>(v.TextValue ?? "[]") ?? []),
        _ => v.TextValue ?? string.Empty,
    };

    private Task<bool> EntityExistsAsync(CustomEntityType type, Guid id, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var db = crm.Db;
        return type switch
        {
            CustomEntityType.Lead => db.Leads.AnyAsync(x => x.Id == id && x.TenantId == tenantId, ct),
            CustomEntityType.Contact => db.Contacts.AnyAsync(x => x.Id == id && x.TenantId == tenantId, ct),
            CustomEntityType.Opportunity => db.Opportunities.AnyAsync(x => x.Id == id && x.TenantId == tenantId, ct),
            CustomEntityType.Customer => db.Customers.AnyAsync(x => x.Id == id && x.TenantId == tenantId, ct),
            _ => Task.FromResult(false),
        };
    }

    private static CustomFieldDto Map(CustomFieldDefinition f, int count) =>
        new(f.Id, f.EntityType, f.Key, f.Label, f.Type, f.Options, f.IsRequired, f.MinValue, f.MaxValue, f.Pattern, f.HelpText, f.IsFilterable, f.IsActive, f.SortOrder, f.Version, count);
}
