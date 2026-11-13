using System.Text.RegularExpressions;

namespace LeadPilot.Application.Crm;

/// <summary>
/// Formula-injection protection for CSV/XLSX exports. A cell starting with = + - @ (or a tab/CR/LF) is prefixed with a single quote so spreadsheet
/// applications show it as text. Plain numbers and phone numbers (digits, spaces and ( ) . - only after an optional sign) cannot call a function and are left intact.
/// </summary>
public static partial class SpreadsheetSafe
{
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var first = value[0];
        if (first is '\t' or '\r' or '\n' or '=' or '@') return "'" + value;
        if (first is '+' or '-') return NumericLike().IsMatch(value) ? value : "'" + value;
        // Leading whitespace/control characters before a formula trigger are stripped by some spreadsheet apps.
        var trimmed = value.TrimStart(' ', ' ');
        if (trimmed.Length > 0 && trimmed != value && trimmed[0] is '=' or '+' or '-' or '@') return "'" + value;
        return value;
    }

    public static string CsvCell(string? value)
    {
        var s = Escape(value);
        return s.IndexOfAny([',', '"', '\r', '\n', ';']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    [GeneratedRegex(@"^[+-]?[\d][\d\s().\-]*$")] private static partial Regex NumericLike();
}
