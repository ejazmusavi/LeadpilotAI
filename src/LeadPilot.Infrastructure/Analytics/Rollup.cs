using System.Diagnostics;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Analytics;
using LeadPilot.Domain.Analytics;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Analytics;

/// <summary>
/// Recomputes the daily rollup of the ACTIVE workspace from the raw rows, in SQL, for a window of workspace-local days: the aggregates are computed first, then rows of the window are replaced in one short
/// transaction. Never incremented, so the rollup can only equal the raw rows as of the refresh (the same recompute-not-increment pattern as the Phase 6 campaign statistics).
/// Incremental refreshes cover the last <see cref="AnalyticsOptions.IncrementalDays"/> days (late delivery receipts); a full rebuild covers <see cref="AnalyticsOptions.RollupRetentionDays"/> and runs when
/// nothing exists yet, the workspace time zone changed, the rollup is older than the incremental window, or when asked.
/// </summary>
public sealed class AnalyticsRollupService(AppDbContext db, ITenantContext tenant, IClock clock, IOptions<AnalyticsOptions> options, AnalyticsCache cache) : IAnalyticsRollupService
{
    public async Task<RollupResult> RefreshAsync(bool full, CancellationToken ct = default)
    {
        var t = tenant.RequireTenantId(); var o = options.Value; var sw = Stopwatch.StartNew(); var now = clock.UtcNow;
        var zoneId = await AnalyticsSupport.ZoneIdAsync(db, t, ct); var tz = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var state = await db.AnalyticsRefreshStates.FirstOrDefaultAsync(s => s.TenantId == t, ct);
        var today = RangeResolver.LocalDay(tz, now);
        var needFull = full || state is null || state.TimeZoneId != zoneId || state.ToDay < today.AddDays(-o.IncrementalDays);
        var fromDay = needFull ? today.AddDays(-(o.RollupRetentionDays - 1)) : today.AddDays(-(o.IncrementalDays - 1));
        var fromUtc = RangeResolver.DayStart(tz, fromDay); var toUtc = RangeResolver.DayStart(tz, today.AddDays(1));

        var q = new AnalyticsQueries(db, t, null);
        var fresh = new List<DailyMetric>();
        foreach (var key in StreamKeys.All)
            foreach (var c in await q.DaysAsync(key, fromUtc, toUtc, tz, ct))
                if (c.Day >= fromDay && c.Day <= today) fresh.Add(DailyMetric.Create(t, c.Day, key, c.Dim, c.Count, c.Amount, now));

        for (var attempt = 0; ; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            try
            {
                // a full rebuild also removes rows older than the retention window and rows of an earlier time zone
                if (needFull) await db.DailyMetrics.Where(m => m.TenantId == t).ExecuteDeleteAsync(ct);
                else { var fromDt = fromDay.ToDateTime(TimeOnly.MinValue); await db.DailyMetrics.Where(m => m.TenantId == t && m.Day >= fromDt).ExecuteDeleteAsync(ct); }
                db.DailyMetrics.AddRange(fresh);
                state = await db.AnalyticsRefreshStates.FirstOrDefaultAsync(s => s.TenantId == t, ct);
                if (state is null) { state = AnalyticsRefreshState.Create(t); db.AnalyticsRefreshStates.Add(state); }
                state.Record(zoneId, needFull ? fromDay : (state.CoveredFromDay == default ? fromDay : state.FromDay), today, now, needFull, (int)sw.ElapsedMilliseconds, fresh.Count);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                break;
            }
            catch (DbUpdateException) when (attempt < 2) { db.ChangeTracker.Clear(); }   // a concurrent refresh of the same workspace won the unique key: replace again
        }
        cache.Invalidate(t);
        return new RollupResult(today.DayNumber - fromDay.DayNumber + 1, fresh.Count, (int)sw.ElapsedMilliseconds, needFull);
    }
}

internal static class AnalyticsSupport
{
    public static async Task<string> ZoneIdAsync(AppDbContext db, Guid tenantId, CancellationToken ct)
    {
        var id = await db.ConversionSettings.AsNoTracking().Where(s => s.TenantId == tenantId).Select(s => s.TimeZoneId).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(id) ? "UTC" : id;
    }
}
