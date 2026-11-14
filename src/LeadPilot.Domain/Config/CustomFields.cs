using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LeadPilot.Domain.Common;

namespace LeadPilot.Domain.Config;

/// <summary>The record kinds a tenant can add typed custom fields to.</summary>
public enum CustomEntityType { Lead = 1, Contact = 2, Opportunity = 3, Customer = 4 }

public enum CustomFieldType { Text = 1, Number = 2, Date = 3, Select = 4, MultiSelect = 5, Bool = 6 }

/// <summary>The checked, typed form of one custom field value, ready to store. <see cref="Search"/> is a canonical lower-case string used by filters; <see cref="Display"/> is what people (and CSV exports) see.</summary>
public sealed record CustomFieldValueData(string? Text, decimal? Number, DateTime? Date, bool? Bool, string Search, string Display);

/// <summary>
/// A typed custom field a tenant defined for leads, contacts, opportunities or customers. Pure tenant data: the platform ships no field of its own
/// (industry packs create them). Key and type never change once values may exist; everything else can be edited.
/// </summary>
public class CustomFieldDefinition : AuditableEntity, ITenantOwned
{
    public const int MaxFieldsPerEntityType = 40;
    public const int MaxTextLength = 500;
    public const int MaxOptions = 50;

    private CustomFieldDefinition() { }
    public Guid TenantId { get; private set; }
    public CustomEntityType EntityType { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Label { get; private set; } = string.Empty;
    public CustomFieldType Type { get; private set; }
    /// <summary>Select / MultiSelect: JSON array of the allowed values.</summary>
    public string? OptionsJson { get; private set; }
    public bool IsRequired { get; private set; }
    /// <summary>Number: inclusive range. Text: MaxValue = maximum length.</summary>
    public decimal? MinValue { get; private set; }
    public decimal? MaxValue { get; private set; }
    /// <summary>Text only: the whole value must match this regular expression (evaluated with the non-backtracking engine).</summary>
    public string? Pattern { get; private set; }
    public string? HelpText { get; private set; }
    /// <summary>Offered as a filter on the list pages and accepted by the API filters.</summary>
    public bool IsFilterable { get; private set; }
    public bool IsActive { get; private set; } = true;
    public int SortOrder { get; private set; }
    public int Version { get; private set; } = 1;

    public IReadOnlyList<string> Options => OptionsJson is null ? [] : JsonSerializer.Deserialize<List<string>>(OptionsJson) ?? [];

    public static readonly Regex KeyRule = new("^[a-z][a-z0-9_]{0,39}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static CustomFieldDefinition Create(Guid tenantId, CustomEntityType entity, string key, string label, CustomFieldType type, IEnumerable<string>? options, bool required,
        decimal? min, decimal? max, string? pattern, string? help, bool filterable, int sortOrder, DateTime nowUtc, string? actor)
    {
        if (!Enum.IsDefined(entity)) throw new DomainException("Unknown record type.");
        if (!Enum.IsDefined(type)) throw new DomainException("Unknown field type.");
        var k = (key ?? string.Empty).Trim().ToLowerInvariant();
        if (!KeyRule.IsMatch(k)) throw new DomainException("The key may contain lower-case letters, digits and underscores, starts with a letter and has at most 40 characters.");
        var f = new CustomFieldDefinition { TenantId = tenantId, EntityType = entity, Key = k, Type = type, SortOrder = sortOrder, CreatedAtUtc = nowUtc, CreatedBy = actor };
        f.Set(label, options, required, min, max, pattern, help, filterable);
        return f;
    }

    public void Update(string label, IEnumerable<string>? options, bool required, decimal? min, decimal? max, string? pattern, string? help, bool filterable, int sortOrder, DateTime nowUtc, string? actor)
    {
        Set(label, options, required, min, max, pattern, help, filterable);
        SortOrder = sortOrder; UpdatedAtUtc = nowUtc; UpdatedBy = actor; Version++;
    }

    public void SetActive(bool active, DateTime nowUtc, string? actor) { IsActive = active; UpdatedAtUtc = nowUtc; UpdatedBy = actor; Version++; }

    private void Set(string label, IEnumerable<string>? options, bool required, decimal? min, decimal? max, string? pattern, string? help, bool filterable)
    {
        var l = label?.Trim();
        if (string.IsNullOrEmpty(l) || l.Length > 100) throw new DomainException("A label of 1 to 100 characters is required.");
        Label = l;
        var opts = (options ?? []).Select(o => o?.Trim() ?? string.Empty).Where(o => o.Length > 0).ToList();
        if (opts.Count != opts.Distinct(StringComparer.OrdinalIgnoreCase).Count()) throw new DomainException("The options must be different from each other.");
        if (opts.Any(o => o.Length > 80 || o.Contains('|'))) throw new DomainException("An option may have at most 80 characters and must not contain '|'.");
        if (Type is CustomFieldType.Select or CustomFieldType.MultiSelect)
        {
            if (opts.Count is < 2 or > MaxOptions) throw new DomainException($"A select field needs 2 to {MaxOptions} options.");
            OptionsJson = JsonSerializer.Serialize(opts);
        }
        else { if (opts.Count > 0) throw new DomainException("Only select fields have options."); OptionsJson = null; }
        IsRequired = required;
        if (Type is CustomFieldType.Number or CustomFieldType.Text)
        {
            if (min is not null && max is not null && min > max) throw new DomainException("The minimum must not be greater than the maximum.");
            if (Type == CustomFieldType.Text && (min is not null || (max is not null && (max < 1 || max > MaxTextLength || max != decimal.Truncate(max.Value))))) throw new DomainException($"A text field can only have a maximum length between 1 and {MaxTextLength}.");
            if (Math.Abs(min ?? 0) > 1_000_000_000_000m || Math.Abs(max ?? 0) > 1_000_000_000_000m) throw new DomainException("The range is too large.");
            MinValue = min; MaxValue = max;
        }
        else { if (min is not null || max is not null) throw new DomainException("A range only applies to number and text fields."); MinValue = null; MaxValue = null; }
        var p = pattern?.Trim();
        if (string.IsNullOrEmpty(p)) Pattern = null;
        else
        {
            if (Type != CustomFieldType.Text) throw new DomainException("A pattern only applies to text fields.");
            if (p.Length > 200) throw new DomainException("The pattern may have at most 200 characters.");
            try { _ = new Regex(p, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { throw new DomainException("The pattern is not a supported regular expression (look-around and back-references are not available)."); }
            Pattern = p;
        }
        var h = help?.Trim();
        if (h is { Length: > 200 }) throw new DomainException("The help text may have at most 200 characters.");
        HelpText = string.IsNullOrEmpty(h) ? null : h;
        IsFilterable = filterable;
    }

    /// <summary>Checks one raw (text) value against the type and rules. Empty = no value (an error when the field is required). Returns null for "no value".</summary>
    public CustomFieldValueData? Normalize(string? raw, out string? error)
    {
        error = null;
        var v = raw?.Trim();
        if (string.IsNullOrEmpty(v))
        {
            if (IsRequired) error = $"{Label} is required.";
            return null;
        }
        switch (Type)
        {
            case CustomFieldType.Text:
                if (v.Length > Math.Min(MaxTextLength, (int)(MaxValue ?? MaxTextLength))) { error = $"{Label} must be {Math.Min(MaxTextLength, (int)(MaxValue ?? MaxTextLength))} characters or fewer."; return null; }
                if (v.Any(char.IsControl)) { error = $"{Label} must not contain control characters."; return null; }
                if (Pattern is not null && !Matches(Pattern, v)) { error = $"{Label} does not have the expected format."; return null; }
                return new(v, null, null, null, v.ToLowerInvariant(), v);
            case CustomFieldType.Number:
                if (!decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) || Math.Abs(n) > 1_000_000_000_000m) { error = $"{Label} must be a number."; return null; }
                n = decimal.Round(n, 4);
                if (MinValue is { } mn && n < mn) { error = $"{Label} must be at least {mn.ToString(CultureInfo.InvariantCulture)}."; return null; }
                if (MaxValue is { } mx && n > mx) { error = $"{Label} must be at most {mx.ToString(CultureInfo.InvariantCulture)}."; return null; }
                var ns = n.ToString("0.####", CultureInfo.InvariantCulture);
                return new(null, n, null, null, ns, ns);
            case CustomFieldType.Date:
                if (!DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) { error = $"{Label} must be a date (yyyy-MM-dd)."; return null; }
                var ds = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return new(null, null, d, null, ds, ds);
            case CustomFieldType.Bool:
                bool? b = v.ToLowerInvariant() switch { "true" or "yes" or "y" or "1" or "on" => true, "false" or "no" or "n" or "0" or "off" => false, _ => null };
                if (b is null) { error = $"{Label} must be yes or no."; return null; }
                return new(null, null, null, b, b.Value ? "true" : "false", b.Value ? "Yes" : "No");
            case CustomFieldType.Select:
                var opt = Options.FirstOrDefault(o => string.Equals(o, v, StringComparison.OrdinalIgnoreCase));
                if (opt is null) { error = $"{Label} must be one of: {string.Join(", ", Options)}."; return null; }
                return new(opt, null, null, null, opt.ToLowerInvariant(), opt);
            case CustomFieldType.MultiSelect:
                var parts = v.StartsWith('[') ? ParseJsonList(v) : v.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (parts is null) { error = $"{Label} is not a valid list."; return null; }
                var chosen = new List<string>();
                foreach (var p in parts)
                {
                    var o = Options.FirstOrDefault(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));
                    if (o is null) { error = $"{Label}: '{(p.Length > 40 ? p[..40] : p)}' is not one of: {string.Join(", ", Options)}."; return null; }
                    if (!chosen.Contains(o)) chosen.Add(o);
                }
                if (chosen.Count == 0) { if (IsRequired) error = $"{Label} is required."; return null; }
                return new(JsonSerializer.Serialize(chosen), null, null, null, "|" + string.Join("|", chosen.Select(c => c.ToLowerInvariant())) + "|", string.Join(", ", chosen));
            default: error = "Unknown field type."; return null;
        }
    }

    private static bool Matches(string pattern, string value)
    {
        try { return Regex.IsMatch(value, "^(?:" + pattern + ")$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static List<string>? ParseJsonList(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json)?.Select(s => s?.Trim() ?? string.Empty).Where(s => s.Length > 0).ToList(); }
        catch (JsonException) { return null; }
    }
}

/// <summary>One value of one custom field on one record. Unique per (tenant, field, record). Typed columns make ranges and sorting work in SQL.</summary>
public class CustomFieldValue : Entity, ITenantOwned
{
    private CustomFieldValue() { }
    public Guid TenantId { get; private set; }
    public Guid FieldId { get; private set; }
    public CustomEntityType EntityType { get; private set; }
    public Guid EntityId { get; private set; }
    public string? TextValue { get; private set; }
    public decimal? NumberValue { get; private set; }
    public DateTime? DateValue { get; private set; }
    public bool? BoolValue { get; private set; }
    /// <summary>Canonical lower-case form used by equality/contains filters; multi-select values are "|a|b|".</summary>
    public string SearchText { get; private set; } = string.Empty;
    public string Display { get; private set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; private set; }

    public static CustomFieldValue Create(Guid tenantId, CustomFieldDefinition field, Guid entityId, CustomFieldValueData data, DateTime nowUtc)
    {
        var v = new CustomFieldValue { TenantId = tenantId, FieldId = field.Id, EntityType = field.EntityType, EntityId = entityId };
        v.Apply(data, nowUtc);
        return v;
    }

    public void Apply(CustomFieldValueData d, DateTime nowUtc) { TextValue = d.Text; NumberValue = d.Number; DateValue = d.Date; BoolValue = d.Bool; SearchText = d.Search; Display = d.Display; UpdatedAtUtc = nowUtc; }
}
