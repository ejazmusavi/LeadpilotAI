using System.Globalization;
using System.Text;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Analytics;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Security;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Analytics;

/// <summary>
/// Dashboard, reports and CSV export of the ACTIVE workspace. Scope is decided here, server-side, from the caller's permissions: with Reports.Team the whole workspace, without it only the caller's own leads
/// (team-only reports and metrics are refused or marked unavailable, never silently shown as zero). Period metrics read the daily rollup when it covers the range, otherwise the raw rows ("Live").
/// </summary>
public sealed class AnalyticsService(AppDbContext db, ITenantContext tenant, ICurrentUser user, IPermissionEvaluator permissions, IClock clock, IOptions<AnalyticsOptions> options, AnalyticsCache cache, IAuditService audit) : IAnalyticsService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly string[] DashboardOrder =
    [
        MetricCatalog.TotalLeads, StreamKeys.LeadsCreated, MetricCatalog.VerifiedLeads, MetricCatalog.EligibleLeads, StreamKeys.LeadsContacted, StreamKeys.Responses, MetricCatalog.InterestedLeads, MetricCatalog.QualifiedLeads,
        StreamKeys.DemosScheduled, StreamKeys.DemosCompleted, StreamKeys.TrialsStarted, StreamKeys.CustomersWon, StreamKeys.OpportunitiesLost, StreamKeys.CampaignSent, MetricCatalog.CampaignReplyRate,
        StreamKeys.AiConversations, StreamKeys.Handoffs, MetricCatalog.DeliveryRate, MetricCatalog.ConversionRate, StreamKeys.Revenue
    ];
    private static readonly string[] TrendKeys = [StreamKeys.LeadsCreated, StreamKeys.MessagesSent, StreamKeys.Responses, StreamKeys.DemosCompleted, StreamKeys.CustomersWon];

    public IReadOnlyList<MetricDefinition> Metrics() => MetricCatalog.All;
    public IReadOnlyList<ReportDefinitionDto> Reports() => ReportCatalog.All;

    private sealed record Ctx(Guid TenantId, string ZoneId, TimeZoneInfo Tz, ResolvedRange Range, bool Team, Guid? Owner, AnalyticsQueries Q, LeadPilot.Domain.Analytics.AnalyticsRefreshState? State, bool UseRollup, DateTime Now)
    {
        public string ScopeName => Team ? "Team" : "Own";
        public string ScopeKey => Team ? "team" : "own-" + Owner;
    }

    private async Task<Result<Ctx>> PrepareAsync(RangeRequest request, CancellationToken ct)
    {
        var t = tenant.RequireTenantId(); var o = options.Value; var now = clock.UtcNow;
        var team = await permissions.HasAsync(PermissionCodes.ReportsTeam, ct);
        if (!team && user.UserId is null) return Result.Failure<Ctx>(Error.Forbidden());
        var zone = await AnalyticsSupport.ZoneIdAsync(db, t, ct);
        var range = RangeResolver.Resolve(request, zone, now, o.MaxRangeDays);
        if (range.IsFailure) return Result.Failure<Ctx>(range.Error);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(zone);
        var state = await db.AnalyticsRefreshStates.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == t, ct);
        var owner = team ? (Guid?)null : user.UserId;
        var today = RangeResolver.LocalDay(tz, now); var r = range.Value;
        var needFrom = r.Compare ? r.PrevFromLocal : r.FromLocal; var needTo = r.ToLocal < today ? r.ToLocal : today;
        var useRollup = owner is null && state is not null && state.TimeZoneId == zone && state.FromDay <= needFrom && state.ToDay >= needTo;
        return Result.Success(new Ctx(t, zone, tz, r, team, owner, new AnalyticsQueries(db, t, owner), state, useRollup, now));
    }

    private FreshnessDto Freshness(Ctx c) => c.UseRollup
        ? new FreshnessDto("Rollup", c.State!.LastRefreshedAtUtc, (c.Now - c.State.LastRefreshedAtUtc).TotalMinutes > options.Value.StaleAfterMinutes,
            "Period metrics come from the daily rollup, refreshed every few minutes by a background job. Current-state figures (leads, qualified, eligible), cohort rates and reports are read live.")
        : new FreshnessDto("Live", c.Now, false, c.Owner is not null ? "Own-data view: computed live from the raw records of your leads (the rollup is workspace-wide)." : "No usable rollup for this range yet: computed live from the raw records.");

    private TimeSpan Ttl => TimeSpan.FromSeconds(options.Value.CacheSeconds);

    // ---------------------------------------------------------------- dashboard

    public async Task<Result<DashboardDto>> DashboardAsync(RangeRequest request, CancellationToken ct = default)
    {
        var ctx = await PrepareAsync(request, ct);
        if (ctx.IsFailure) return Result.Failure<DashboardDto>(ctx.Error);
        var c = ctx.Value; var r = c.Range;
        var key = $"dash:{c.ScopeKey}:{r.FromLocal:yyyyMMdd}:{r.ToLocal:yyyyMMdd}:{r.Compare}:{c.ZoneId}:{c.State?.LastRefreshedAtUtc.Ticks}:{c.UseRollup}";
        var dto = await cache.GetOrAddAsync(c.TenantId, key, Ttl, () => BuildDashboardAsync(c, ct));
        return Result.Success(dto);
    }

    private async Task<DashboardDto> BuildDashboardAsync(Ctx c, CancellationToken ct)
    {
        var r = c.Range; var q = c.Q;
        var cur = await PeriodTotalsAsync(c, r.FromLocal, r.ToLocal, r.FromUtc, r.ToUtcExclusive, ct);
        var prev = r.Compare ? await PeriodTotalsAsync(c, r.PrevFromLocal, r.PrevToLocal, r.PrevFromUtc, r.PrevToUtcExclusive, ct) : null;
        long N(Dictionary<string, List<(string Dim, long Count, decimal Amount)>>? d, string k) => d is null ? 0 : d.GetValueOrDefault(k)?.Sum(x => x.Count) ?? 0;

        var snapshots = new Dictionary<string, long>
        {
            [MetricCatalog.TotalLeads] = await q.TotalLeadsAsync(ct), [MetricCatalog.VerifiedLeads] = await q.VerifiedLeadsAsync(ct), [MetricCatalog.EligibleLeads] = await q.EligibleLeadsAsync(ct),
            [MetricCatalog.InterestedLeads] = await q.InterestedLeadsAsync(ct), [MetricCatalog.QualifiedLeads] = await q.QualifiedLeadsAsync(ct),
        };
        var (cc, cv) = await q.ConversionCohortAsync(r.FromUtc, r.ToUtcExclusive, ct);
        (long Created, long Converted)? pc = r.Compare ? await q.ConversionCohortAsync(r.PrevFromUtc, r.PrevToUtcExclusive, ct) : null;

        var list = new List<MetricValueDto>();
        foreach (var k in DashboardOrder)
        {
            var def = MetricCatalog.Get(k);
            if (c.Owner is not null && !def.OwnScope) { list.Add(Dto(def, available: false)); continue; }
            switch (k)
            {
                case var s when snapshots.ContainsKey(s):
                    list.Add(Dto(def, value: snapshots[s], drill: Drill(c, k))); break;
                case MetricCatalog.DeliveryRate:
                    list.Add(Rate(def, N(cur, StreamKeys.MessagesDelivered), N(cur, StreamKeys.MessagesSent), prev is null ? null : (N(prev, StreamKeys.MessagesDelivered), N(prev, StreamKeys.MessagesSent)))); break;
                case MetricCatalog.CampaignReplyRate:
                    list.Add(Rate(def, N(cur, StreamKeys.CampaignReplied), N(cur, StreamKeys.CampaignSent), prev is null ? null : (N(prev, StreamKeys.CampaignReplied), N(prev, StreamKeys.CampaignSent)))); break;
                case MetricCatalog.ConversionRate:
                    list.Add(Rate(def, cv, cc, pc is null ? null : (pc.Value.Converted, pc.Value.Created))); break;
                case StreamKeys.Revenue:
                {
                    var money = cur.GetValueOrDefault(k)?.Where(x => x.Dim.Length > 0).Select(x => x.Dim).Concat(prev?.GetValueOrDefault(k)?.Select(x => x.Dim) ?? []).Distinct().OrderBy(x => x, StringComparer.Ordinal)
                        .Select(cur2 => new MoneyDto(cur2, cur[k].Where(x => x.Dim == cur2).Sum(x => x.Amount), prev is null ? null : prev.GetValueOrDefault(k)?.Where(x => x.Dim == cur2).Sum(x => x.Amount) ?? 0m)).ToList()
                        ?? (prev?.GetValueOrDefault(k)?.Select(x => x.Dim).Distinct().OrderBy(x => x, StringComparer.Ordinal).Select(d => new MoneyDto(d, 0m, prev[k].Where(x => x.Dim == d).Sum(x => x.Amount))).ToList() ?? []);
                    list.Add(Dto(def, money: money)); break;
                }
                default:
                {
                    var v = N(cur, k); long? p = prev is null ? null : N(prev, k);
                    list.Add(Dto(def, value: v, previous: p, change: RangeResolver.Change(v, p), drill: Drill(c, k))); break;
                }
            }
        }
        return new DashboardDto(RangeResolver.ToDto(r), c.ScopeName, Freshness(c), list, await TrendsAsync(c, ct));
    }

    private static MetricValueDto Dto(MetricDefinition d, bool available = true, decimal? value = null, decimal? previous = null, decimal? change = null, decimal? num = null, decimal? den = null, IReadOnlyList<MoneyDto>? money = null, string? drill = null) =>
        new(d.Key, d.Name, d.Kind, d.Definition, d.Sources, d.Formula, d.Unit, d.Snapshot, available, value, previous, change, num, den, money, drill);

    /// <summary>Rates: <c>ChangePercent</c> is the difference in PERCENTAGE POINTS (counts: relative change in %).</summary>
    private static MetricValueDto Rate(MetricDefinition d, long num, long den, (long Num, long Den)? prev)
    {
        var v = RangeResolver.Percent(num, den); var p = prev is null ? null : RangeResolver.Percent(prev.Value.Num, prev.Value.Den);
        return Dto(d, value: v, previous: p, change: v is not null && p is not null ? decimal.Round(v.Value - p.Value, 1) : null, num: num, den: den);
    }

    private static string? Drill(Ctx c, string key)
    {
        var owner = c.Owner is { } o ? $"&OwnerUserId={o}" : "";
        return key switch
        {
            MetricCatalog.TotalLeads => "/Leads" + (owner.Length > 0 ? "?" + owner[1..] : ""),
            StreamKeys.LeadsCreated => $"/Leads?CreatedFromUtc={Uri.EscapeDataString(c.Range.FromUtc.ToString("o", Inv))}&CreatedToUtc={Uri.EscapeDataString(c.Range.ToUtcExclusive.AddTicks(-1).ToString("o", Inv))}&Archived=All{owner}",
            _ => null
        };
    }

    private async Task<Dictionary<string, List<(string Dim, long Count, decimal Amount)>>> PeriodTotalsAsync(Ctx c, DateOnly fromDay, DateOnly toDay, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var d = new Dictionary<string, List<(string, long, decimal)>>();
        if (c.UseRollup)
        {
            DateTime a = fromDay.ToDateTime(TimeOnly.MinValue), b = toDay.ToDateTime(TimeOnly.MinValue);
            var rows = await db.DailyMetrics.AsNoTracking().Where(m => m.TenantId == c.TenantId && m.Day >= a && m.Day <= b)
                .GroupBy(m => new { m.MetricKey, m.Dimension }).Select(g => new { g.Key.MetricKey, g.Key.Dimension, N = g.Sum(m => m.Count), S = g.Sum(m => m.Amount) }).ToListAsync(ct);
            foreach (var x in rows) { if (!d.TryGetValue(x.MetricKey, out var l)) d[x.MetricKey] = l = []; l.Add((x.Dimension, x.N, x.S)); }
        }
        else
            foreach (var k in StreamKeys.All) d[k] = await c.Q.TotalsAsync(k, fromUtc, toUtc, ct);
        return d;
    }

    private async Task<List<TrendDto>> TrendsAsync(Ctx c, CancellationToken ct)
    {
        var r = c.Range;
        var cells = await new DailyReader(db, c.Q, c.UseRollup).ReadAsync(TrendKeys, r.FromLocal, r.ToLocal, r.FromUtc, r.ToUtcExclusive, c.Tz, ct);
        var weekly = r.Days > 92;
        var result = new List<TrendDto>();
        foreach (var k in TrendKeys)
        {
            var perDay = cells.Where(x => x.Key == k).GroupBy(x => x.Day).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
            var points = new List<TrendPointDto>();
            for (var day = r.FromLocal; day <= r.ToLocal; day = day.AddDays(1))
            {
                var bucket = weekly ? day.AddDays(-(((int)day.DayOfWeek + 6) % 7)) : day;
                if (bucket < r.FromLocal) bucket = r.FromLocal;
                var v = perDay.GetValueOrDefault(day);
                if (points.Count > 0 && points[^1].Day == bucket) points[^1] = points[^1] with { Value = points[^1].Value + v }; else points.Add(new TrendPointDto(bucket, v));
            }
            result.Add(new TrendDto(k, MetricCatalog.Get(k).Name, weekly ? "week" : "day", points));
        }
        return result;
    }

    // ---------------------------------------------------------------- reports

    private Result<ReportDefinitionDto> Authorize(string key, Ctx c)
    {
        var def = ReportCatalog.Find(key);
        if (def is null) return Result.Failure<ReportDefinitionDto>(Error.NotFound("Report"));
        if (def.TeamOnly && !c.Team) return Result.Failure<ReportDefinitionDto>(Error.Forbidden("This report covers the whole workspace. Ask an administrator for the Reports.Team permission."));
        return Result.Success(def);
    }

    public async Task<Result<ReportDto>> ReportAsync(string key, RangeRequest request, int page, int pageSize, CancellationToken ct = default)
    {
        var o = options.Value;
        if (page < 1 || pageSize < 1 || pageSize > o.MaxPageSize) return Result.Failure<ReportDto>(Error.Validation($"page must be at least 1 and pageSize between 1 and {o.MaxPageSize}."));
        var ctx = await PrepareAsync(request, ct);
        if (ctx.IsFailure) return Result.Failure<ReportDto>(ctx.Error);
        var c = ctx.Value; var def = Authorize(key, c);
        if (def.IsFailure) return Result.Failure<ReportDto>(def.Error);
        var ck = $"rep:{key}:{c.ScopeKey}:{c.Range.FromLocal:yyyyMMdd}:{c.Range.ToLocal:yyyyMMdd}:{c.ZoneId}:{page}:{pageSize}:{c.State?.LastRefreshedAtUtc.Ticks}:{c.UseRollup}";
        var dto = await cache.GetOrAddAsync(c.TenantId, ck, Ttl, async () =>
        {
            var p = await BuildPageAsync(key, c, (page - 1) * pageSize, pageSize, ct);
            return new ReportDto(def.Value, def.Value.UsesDateRange ? RangeResolver.ToDto(c.Range) : null, c.ScopeName, ReportFreshness(def.Value, c), p.Rows, p.Totals, page, pageSize, p.TotalRows, p.Notes);
        });
        return Result.Success(dto);
    }

    private FreshnessDto ReportFreshness(ReportDefinitionDto def, Ctx c) => def.Key == ReportKeys.Messaging ? Freshness(c) : new FreshnessDto("Live", c.Now, false, "Computed from the raw records when the page was requested (cached for a minute).");

    private Task<ReportPage> BuildPageAsync(string key, Ctx c, int skip, int take, CancellationToken ct) =>
        new ReportBuilder(db, c.Q).BuildAsync(key, c.Range, c.Tz, skip, take, new DailyReader(db, c.Q, c.UseRollup), ct);

    // ---------------------------------------------------------------- export

    public async Task<Result<ReportFile>> ExportCsvAsync(string key, RangeRequest request, Func<ReportFile, Stream> openOutput, CancellationToken ct = default)
    {
        if (!await permissions.HasAsync(PermissionCodes.ReportsExport, ct)) return Result.Failure<ReportFile>(Error.Forbidden("You do not have permission to export reports."));
        var ctx = await PrepareAsync(request, ct);
        if (ctx.IsFailure) return Result.Failure<ReportFile>(ctx.Error);
        var c = ctx.Value; var d = Authorize(key, c);
        if (d.IsFailure) return Result.Failure<ReportFile>(d.Error);
        var def = d.Value; var max = options.Value.MaxExportRows;

        var first = await BuildPageAsync(key, c, 0, 1, ct);
        if (first.TotalRows > max) return Result.Failure<ReportFile>(Error.TooLarge($"The report has {first.TotalRows:N0} rows; the export limit is {max:N0}. Narrow the date range."));

        // the export is audited BEFORE any data leaves: who, what, how many rows, which range, when (the audit row carries actor, workspace, time, IP and correlation id)
        await audit.RecordAndSaveAsync(new AuditEntry("Report.Exported", "Report", key,
            $"Report '{def.Title}' exported as CSV: {first.TotalRows} row(s){(def.UsesDateRange ? $", {c.Range.FromLocal:yyyy-MM-dd}..{c.Range.ToLocal:yyyy-MM-dd} ({c.ZoneId})" : "")}, scope {c.ScopeName.ToLowerInvariant()}."), ct);

        var file = new ReportFile($"{key}-{c.Now:yyyyMMdd-HHmmss}.csv", "text/csv; charset=utf-8");
        var output = openOutput(file);
        await using var writer = new StreamWriter(output, new UTF8Encoding(true), 16 * 1024, leaveOpen: true) { NewLine = "\r\n" };
        await writer.WriteLineAsync(string.Join(',', def.Columns.Select(col => SpreadsheetSafe.CsvCell(col.Format == "percent" ? col.Label + " (%)" : col.Label))));
        IReadOnlyList<object?>? totals = null;
        const int chunk = 1000;
        for (var skip = 0; skip < first.TotalRows; skip += chunk)
        {
            ct.ThrowIfCancellationRequested();
            var p = await BuildPageAsync(key, c, skip, chunk, ct);
            foreach (var row in p.Rows) await writer.WriteLineAsync(string.Join(',', row.Select((v, i) => SpreadsheetSafe.CsvCell(Cell(v, def.Columns[i].Format)))));
            totals = p.Totals;
            await writer.FlushAsync(ct);
        }
        if (totals is not null) await writer.WriteLineAsync(string.Join(',', totals.Select((v, i) => SpreadsheetSafe.CsvCell(Cell(v, def.Columns[i].Format)))));
        await writer.FlushAsync(ct);
        return Result.Success(file);
    }

    private static string? Cell(object? v, string format) => v switch
    {
        null => "",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", Inv),
        decimal m when format == "money" => m.ToString("0.00", Inv),
        decimal m => m.ToString("0.0", Inv),
        long or int => Convert.ToString(v, Inv),
        _ => v.ToString()
    };
}
