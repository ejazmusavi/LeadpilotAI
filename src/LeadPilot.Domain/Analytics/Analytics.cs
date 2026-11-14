using LeadPilot.Domain.Common;

namespace LeadPilot.Domain.Analytics;

/// <summary>
/// One cell of the daily rollup: how many recorded events of <see cref="MetricKey"/> happened on the workspace-local calendar <see cref="Day"/>
/// (and, for money, their sum per <see cref="Dimension"/> = currency). Rows are RECOMPUTED from the raw tables for a window of days (never incremented), so they can only equal the raw rows as of <see cref="ComputedAtUtc"/>.
/// Sparse: a day without events has no row.
/// </summary>
public class DailyMetric : Entity, ITenantOwned
{
    private DailyMetric() { }
    public Guid TenantId { get; private set; }
    /// <summary>Workspace-local calendar day (a date, time of day always midnight).</summary>
    public DateTime Day { get; private set; }
    public DateOnly DayOnly => DateOnly.FromDateTime(Day);
    public string MetricKey { get; private set; } = string.Empty;
    /// <summary>Empty, or the currency code for money metrics.</summary>
    public string Dimension { get; private set; } = string.Empty;
    public long Count { get; private set; }
    public decimal Amount { get; private set; }
    public DateTime ComputedAtUtc { get; private set; }

    public static DailyMetric Create(Guid tenantId, DateOnly day, string metricKey, string dimension, long count, decimal amount, DateTime nowUtc) =>
        new() { TenantId = tenantId, Day = day.ToDateTime(TimeOnly.MinValue), MetricKey = metricKey, Dimension = dimension ?? string.Empty, Count = count, Amount = amount, ComputedAtUtc = nowUtc };
}

/// <summary>Per workspace: what the rollup covers and when it was last (re)computed — the source of the "data as of" indicator.</summary>
public class AnalyticsRefreshState : Entity, ITenantOwned
{
    private AnalyticsRefreshState() { }
    public Guid TenantId { get; private set; }
    /// <summary>Time zone the day boundaries were computed in; a different current zone invalidates every row.</summary>
    public string TimeZoneId { get; private set; } = "UTC";
    public DateTime CoveredFromDay { get; private set; }
    public DateTime CoveredToDay { get; private set; }
    public DateOnly FromDay => DateOnly.FromDateTime(CoveredFromDay);
    public DateOnly ToDay => DateOnly.FromDateTime(CoveredToDay);
    public DateTime LastRefreshedAtUtc { get; private set; }
    public DateTime? LastFullRebuildAtUtc { get; private set; }
    public int LastDurationMs { get; private set; }
    public int LastRowCount { get; private set; }

    public static AnalyticsRefreshState Create(Guid tenantId) => new() { TenantId = tenantId };

    public void Record(string timeZoneId, DateOnly from, DateOnly to, DateTime nowUtc, bool full, int durationMs, int rows)
    {
        TimeZoneId = timeZoneId; CoveredFromDay = from.ToDateTime(TimeOnly.MinValue); CoveredToDay = to.ToDateTime(TimeOnly.MinValue); LastRefreshedAtUtc = nowUtc;
        if (full) LastFullRebuildAtUtc = nowUtc;
        LastDurationMs = durationMs; LastRowCount = rows;
    }
}
