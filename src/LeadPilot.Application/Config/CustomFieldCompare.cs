using System.Globalization;
using System.Text.RegularExpressions;

namespace LeadPilot.Application.Config;

/// <summary>
/// One comparison of a custom field value, shared by scoring rules (<c>student_count gte 500</c>) and workflow conditions (<c>lead.cf.student_count</c>).
/// The actual value is self-describing: decimal (number), bool, string (text, select, ISO date) or a list (multi-select). Pure, no I/O.
/// </summary>
public static class CustomFieldCompare
{
    public static readonly IReadOnlyList<string> Operators = ["eq", "neq", "gt", "gte", "lt", "lte", "contains", "not_contains", "is_empty", "is_not_empty"];
    public static bool NeedsValue(string op) => op is not ("is_empty" or "is_not_empty");

    /// <summary>"key op value" (value omitted for is_empty / is_not_empty). Null when the syntax is wrong.</summary>
    public static (string Key, string Op, string? Value)? ParseTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var m = Regex.Match(target.Trim(), @"^([a-z][a-z0-9_]{0,39})\s+([a-z_]+)(?:\s+(.{1,100}))?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        if (!m.Success) return null;
        var op = m.Groups[2].Value;
        if (!Operators.Contains(op)) return null;
        var value = m.Groups[3].Success ? m.Groups[3].Value.Trim() : null;
        if (NeedsValue(op) != (value is not null)) return null;
        return (m.Groups[1].Value, op, value);
    }

    public static bool Matches(object? actual, string op, string? value)
    {
        if (actual is null) return op is "neq" or "is_empty" or "not_contains";
        switch (op)
        {
            case "is_empty": return actual switch { string s => string.IsNullOrWhiteSpace(s), IReadOnlyCollection<string> l => l.Count == 0, _ => false };
            case "is_not_empty": return !Matches(actual, "is_empty", null);
            case "eq": return Equal(actual, value);
            case "neq": return !Equal(actual, value);
            case "gt": return Compare(actual, value) is { } g && g > 0;
            case "gte": return Compare(actual, value) is { } ge && ge >= 0;
            case "lt": return Compare(actual, value) is { } l1 && l1 < 0;
            case "lte": return Compare(actual, value) is { } le && le <= 0;
            case "contains": return Contains(actual, value);
            case "not_contains": return !Contains(actual, value);
            default: return false;
        }
    }

    private static bool Equal(object actual, string? value) => actual switch
    {
        decimal d => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && d == v,
        bool b => TryBool(value) is { } vb && b == vb,
        string s => string.Equals(s, value, StringComparison.OrdinalIgnoreCase),
        IReadOnlyCollection<string> l => l.Any(i => string.Equals(i, value, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    private static int? Compare(object actual, string? value) => actual switch
    {
        decimal d when decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) => d.CompareTo(v),
        string s when value is not null => Math.Sign(string.CompareOrdinal(s, value)),   // ISO dates sort correctly as text
        _ => null,
    };

    private static bool Contains(object actual, string? value) => actual switch
    {
        string s => value is not null && s.Contains(value, StringComparison.OrdinalIgnoreCase),
        IReadOnlyCollection<string> l => l.Any(i => string.Equals(i, value, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    private static bool? TryBool(string? v) => v?.Trim().ToLowerInvariant() switch { "true" or "yes" or "1" => true, "false" or "no" or "0" => false, _ => null };
}
