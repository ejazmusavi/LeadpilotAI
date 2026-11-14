using LeadPilot.Shared.Results;

namespace LeadPilot.Application.Analytics;

/// <summary>How a number was obtained. Shown as a badge on every metric and column and documented in docs/METRICS.md.</summary>
public enum MetricKind
{
    /// <summary>Counted or summed directly from recorded rows (an event that was stored, a payment that was verified).</summary>
    Verified = 1,
    /// <summary>Derived from verified numbers by a stated formula (rates, unions of rules).</summary>
    Calculated = 2,
    /// <summary>Rests on an assumption or a provider estimate (e.g. discovery and AI cost estimates).</summary>
    Estimated = 3
}

public sealed record MetricDefinition(string Key, string Name, MetricKind Kind, string Definition, string Sources, string? Formula, string Unit, bool Snapshot, bool OwnScope, string? DrillDown = null);

public enum RangePreset { Today = 1, Yesterday, Last7Days, Last30Days, Last90Days, ThisMonth, LastMonth, ThisYear, Custom }

/// <summary>What the caller asked for: a preset or a custom range of workspace-local calendar dates (both ends INCLUSIVE).</summary>
public sealed class RangeRequest
{
    public RangePreset Preset { get; set; } = RangePreset.Last30Days;
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public bool Compare { get; set; } = true;
}

/// <summary>
/// A resolved range. Dates are workspace-local calendar days; <see cref="FromLocal"/> and <see cref="ToLocal"/> are both inclusive. Instants are half-open: [<see cref="FromUtc"/>, <see cref="ToUtcExclusive"/>) =
/// local midnight of the first day up to (not including) local midnight of the day after the last one, so an event at exactly midnight belongs to the NEW day and DST days are 23/25 hours long.
/// The previous period has the same number of local days and ends the day before this one starts.
/// </summary>
public sealed record ResolvedRange(string TimeZoneId, DateOnly FromLocal, DateOnly ToLocal, DateTime FromUtc, DateTime ToUtcExclusive, DateOnly PrevFromLocal, DateOnly PrevToLocal, DateTime PrevFromUtc, DateTime PrevToUtcExclusive, string Label, bool Compare)
{
    public int Days => ToLocal.DayNumber - FromLocal.DayNumber + 1;
}

public sealed record RangeDto(string TimeZoneId, DateOnly From, DateOnly To, DateTime FromUtc, DateTime ToUtcExclusive, DateOnly PreviousFrom, DateOnly PreviousTo, string Label, bool Compare);

public sealed record MoneyDto(string Currency, decimal Amount, decimal? Previous);

public sealed record MetricValueDto(
    string Key, string Name, MetricKind Kind, string Definition, string Sources, string? Formula, string Unit, bool Snapshot,
    /// <summary>False when the metric cannot be computed for this viewer (team-only data in an own-data view) — shown as "team only", never as zero.</summary>
    bool Available, decimal? Value, decimal? Previous, decimal? ChangePercent, decimal? Numerator, decimal? Denominator, IReadOnlyList<MoneyDto>? Money, string? DrillDownUrl);

public sealed record TrendPointDto(DateOnly Day, long Value);
public sealed record TrendDto(string Key, string Name, string Grain, IReadOnlyList<TrendPointDto> Points);

public sealed record FreshnessDto(
    /// <summary>"Rollup" = period metrics read from the daily rollup refreshed by Hangfire; "Live" = computed from the raw rows at this moment.</summary>
    string Source, DateTime AsOfUtc, bool Stale, string Note);

public sealed record DashboardDto(RangeDto Range, string Scope, FreshnessDto Freshness, IReadOnlyList<MetricValueDto> Metrics, IReadOnlyList<TrendDto> Trends);

public sealed record ReportColumnDto(string Key, string Label, MetricKind Kind, string Definition, string Format);

public sealed record ReportDefinitionDto(string Key, string Title, string Description, bool TeamOnly, bool UsesDateRange, IReadOnlyList<ReportColumnDto> Columns);

public sealed record ReportDto(
    ReportDefinitionDto Definition, RangeDto? Range, string Scope, FreshnessDto Freshness, IReadOnlyList<IReadOnlyList<object?>> Rows, IReadOnlyList<object?>? Totals,
    int Page, int PageSize, long TotalRows, IReadOnlyList<string> Notes);

public sealed record ReportFile(string FileName, string ContentType);

public interface IAnalyticsService
{
    Task<Result<DashboardDto>> DashboardAsync(RangeRequest request, CancellationToken ct = default);
    IReadOnlyList<MetricDefinition> Metrics();
    IReadOnlyList<ReportDefinitionDto> Reports();
    Task<Result<ReportDto>> ReportAsync(string key, RangeRequest request, int page, int pageSize, CancellationToken ct = default);
    /// <summary>Validates permission, scope, range and the row cap, writes the audit entry, then streams the CSV (UTF-8 with BOM, formula-injection safe) to <paramref name="output"/> in chunks.</summary>
    Task<Result<ReportFile>> ExportCsvAsync(string key, RangeRequest request, Func<ReportFile, Stream> openOutput, CancellationToken ct = default);
}

/// <summary>Recomputes the daily rollup of the active workspace from the raw rows (idempotent; the Hangfire job and tests call it).</summary>
public interface IAnalyticsRollupService
{
    Task<RollupResult> RefreshAsync(bool full, CancellationToken ct = default);
}

public sealed record RollupResult(int Days, int Rows, int DurationMs, bool Full);

public sealed class AnalyticsOptions
{
    public const string Section = "Analytics";
    /// <summary>Days of history the rollup covers (full rebuild). Ranges starting earlier fall back to live queries.</summary>
    public int RollupRetentionDays { get; set; } = 400;
    /// <summary>Days recomputed by every incremental refresh (late delivery receipts, backfilled data).</summary>
    public int IncrementalDays { get; set; } = 7;
    public int CacheSeconds { get; set; } = 60;
    /// <summary>Rollups older than this are shown as stale.</summary>
    public int StaleAfterMinutes { get; set; } = 45;
    public int MaxRangeDays { get; set; } = 800;
    public int MaxExportRows { get; set; } = 50_000;
    public int MaxPageSize { get; set; } = 200;
}
