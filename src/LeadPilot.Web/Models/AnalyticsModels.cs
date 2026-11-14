using LeadPilot.Application.Analytics;

namespace LeadPilot.Web.Models;

public sealed record RangeFilterModel(string Controller, string Action, RangeRequest Request, RangeDto? Range, IDictionary<string, string?> Extra, bool ShowCompare);

public sealed class DashboardViewModel
{
    public DashboardDto? Dashboard { get; init; }
    public string? Error { get; init; }
    public RangeRequest Request { get; init; } = new();
    public bool DatabaseHealthy { get; init; }
    public string Environment { get; init; } = "";
    public string Version { get; init; } = "";
}

public sealed class ReportsIndexViewModel { public IReadOnlyList<ReportDefinitionDto> Reports { get; init; } = []; public bool Team { get; init; } public bool CanExport { get; init; } }

public sealed class ReportViewModel
{
    public required ReportDto Report { get; init; }
    public required RangeRequest Request { get; init; }
    public bool CanExport { get; init; }
    public IDictionary<string, string?> QueryValues { get; init; } = new Dictionary<string, string?>();
}

public static class AnalyticsFormat
{
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    public static string KindClass(MetricKind k) => k switch { MetricKind.Verified => "text-bg-success", MetricKind.Calculated => "text-bg-primary", _ => "text-bg-warning" };

    public static string Value(MetricValueDto m) => m.Unit switch
    {
        "percent" => m.Value is null ? "—" : m.Value.Value.ToString("0.0", Inv) + "%",
        "money" => "",
        _ => m.Value is null ? "—" : m.Value.Value.ToString("N0", Inv)
    };

    public static string Cell(object? v, string format) => v switch
    {
        null => "—",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", Inv),
        decimal d when format == "percent" => d.ToString("0.0", Inv) + "%",
        decimal d when format == "money" => d.ToString("N2", Inv),
        long l => l.ToString("N0", Inv),
        int i => i.ToString("N0", Inv),
        decimal d => d.ToString("0.0", Inv),
        _ => v.ToString() ?? ""
    };

    public static bool IsNumeric(string format) => format is "int" or "percent" or "money";
}
