using LeadPilot.Application.Analytics;
using LeadPilot.Infrastructure.Analytics;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>Every dashboard metric and every report total is compared with the raw rows (independent raw SQL and hand-counted constants of <see cref="Scenario"/>).</summary>
[Collection("app")]
public class AnalyticsReconciliationTests(AppFixture app)
{
    private static (string Key, string Sql)[] StreamSql =>
    [
        (StreamKeys.LeadsCreated, "SELECT COUNT(*) FROM Leads WHERE TenantId=@t AND CreatedAtUtc>=@f AND CreatedAtUtc<@e"),
        (StreamKeys.LeadsContacted, "SELECT COUNT(*) FROM (SELECT c.LeadId, MIN(m.SentAtUtc) first FROM Messages m JOIN Contacts c ON c.Id=m.ContactId WHERE m.TenantId=@t AND m.Direction='Outbound' AND m.SentAtUtc IS NOT NULL GROUP BY c.LeadId) x WHERE x.first>=@f AND x.first<@e"),
        (StreamKeys.Responses, "SELECT COUNT(*) FROM Messages WHERE TenantId=@t AND Direction='Inbound' AND ContactId IS NOT NULL AND OccurredAtUtc>=@f AND OccurredAtUtc<@e"),
        (StreamKeys.MessagesSent, "SELECT COUNT(*) FROM Messages WHERE TenantId=@t AND Direction='Outbound' AND SentAtUtc>=@f AND SentAtUtc<@e"),
        (StreamKeys.MessagesDelivered, "SELECT COUNT(*) FROM Messages WHERE TenantId=@t AND Direction='Outbound' AND SentAtUtc>=@f AND SentAtUtc<@e AND DeliveredAtUtc IS NOT NULL"),
        (StreamKeys.MessagesFailed, "SELECT COUNT(*) FROM Messages WHERE TenantId=@t AND Direction='Outbound' AND FailedAtUtc>=@f AND FailedAtUtc<@e"),
        (StreamKeys.DemosScheduled, "SELECT COUNT(*) FROM Demos WHERE TenantId=@t AND CreatedAtUtc>=@f AND CreatedAtUtc<@e"),
        (StreamKeys.DemosCompleted, "SELECT COUNT(*) FROM Demos WHERE TenantId=@t AND CompletedAtUtc>=@f AND CompletedAtUtc<@e"),
        (StreamKeys.TrialsStarted, "SELECT COUNT(*) FROM Trials WHERE TenantId=@t AND CreatedAtUtc>=@f AND CreatedAtUtc<@e"),
        (StreamKeys.CustomersWon, "SELECT COUNT(*) FROM Customers WHERE TenantId=@t AND ConvertedAtUtc>=@f AND ConvertedAtUtc<@e"),
        (StreamKeys.OpportunitiesLost, "SELECT COUNT(*) FROM Opportunities WHERE TenantId=@t AND Status='Lost' AND UpdatedAtUtc>=@f AND UpdatedAtUtc<@e"),
        (StreamKeys.AiConversations, "SELECT COUNT(*) FROM (SELECT ConversationId, MIN(CreatedAtUtc) first FROM AiInteractions WHERE TenantId=@t GROUP BY ConversationId) x WHERE x.first>=@f AND x.first<@e"),
        (StreamKeys.Handoffs, "SELECT COUNT(*) FROM HandoffEvents WHERE TenantId=@t AND Kind IN ('Takeover','Escalated','AutoTakeover') AND OccurredAtUtc>=@f AND OccurredAtUtc<@e"),
        (StreamKeys.CampaignSent, "SELECT COUNT(*) FROM CampaignRecipients WHERE TenantId=@t AND SentAtUtc>=@f AND SentAtUtc<@e"),
        (StreamKeys.CampaignReplied, "SELECT COUNT(*) FROM CampaignRecipients WHERE TenantId=@t AND SentAtUtc>=@f AND SentAtUtc<@e AND RepliedAtUtc IS NOT NULL"),
    ];

    [RequiresMySqlFact]
    public async Task Every_dashboard_metric_matches_the_raw_rows_and_the_hand_counts()
    {
        await using var w = await AnalyticsWorld.OpenAsync(app); var s = await Scenario.SeedAsync(w);
        var d = await w.DashboardAsync(w.AdminApi, AnalyticsWorld.Custom(s.From, s.To));
        Assert.Equal("Team", d.Scope); Assert.Equal("Live", d.Freshness.Source);

        // independent raw SQL for every recorded-event metric, current and previous period
        foreach (var (key, sql) in StreamSql)
        {
            var cur = await w.Sql<long>(sql, ("@f", s.FromUtc), ("@e", s.ToUtc)); var prev = await w.Sql<long>(sql, ("@f", s.PrevFromUtc), ("@e", s.PrevToUtc));
            if (key is StreamKeys.MessagesSent or StreamKeys.MessagesDelivered or StreamKeys.MessagesFailed or StreamKeys.CampaignReplied) continue;   // not dashboard tiles: covered through the rates below
            var m = d.Metric(key); Assert.True(m.Available);
            Assert.True(cur == m.Value, $"{key}: raw {cur} vs dashboard {m.Value}"); Assert.True(prev == m.Previous, $"{key}: raw previous {prev} vs dashboard {m.Previous}");
        }
        // hand-counted constants (see the Scenario summary)
        void Is(string key, long cur, long? prev) { var m = d.Metric(key); Assert.True(cur == m.Value, $"{key}: expected {cur}, got {m.Value}"); if (prev is not null) Assert.True(prev == m.Previous, $"{key} previous: expected {prev}, got {m.Previous}"); }
        Is("leads.total", 6, null); Is("leads.verified", 2, null); Is("leads.eligible", 3, null); Is("leads.interested", 4, null); Is("leads.qualified", 2, null);
        Is("leads.created", 4, 2); Is("leads.contacted", 1, 2); Is("messages.inbound", 2, 1); Is("demos.scheduled", 2, 1); Is("demos.completed", 1, 0); Is("trials.started", 2, 1); Is("customers.won", 1, 1);
        Is("opportunities.lost", 1, 1); Is("campaigns.sent", 2, 1); Is("ai.conversations", 2, 1); Is("handoffs.human", 3, 0);
        // rates: formula, numerator, denominator, rounding, previous period
        var dr = d.Metric("rate.delivery"); Assert.Equal(66.7m, dr.Value); Assert.Equal(2, dr.Numerator); Assert.Equal(3, dr.Denominator); Assert.Equal(100.0m, dr.Previous); Assert.Equal(-33.3m, dr.ChangePercent);
        var cr = d.Metric("rate.campaign_reply"); Assert.Equal(50.0m, cr.Value); Assert.Equal(0.0m, cr.Previous);
        var lc = d.Metric("rate.lead_conversion"); Assert.Equal(25.0m, lc.Value); Assert.Equal(50.0m, lc.Previous);
        // revenue: verified payments only, per currency, never summed across currencies; the open payment request (5000 USD) is not revenue
        var rev = d.Metric("revenue.verified"); Assert.Equal(2, rev.Money!.Count);
        Assert.Equal(150.50m, rev.Money.Single(x => x.Currency == "USD").Amount); Assert.Equal(70m, rev.Money.Single(x => x.Currency == "USD").Previous);
        Assert.Equal(20000m, rev.Money.Single(x => x.Currency == "PKR").Amount); Assert.Equal(0m, rev.Money.Single(x => x.Currency == "PKR").Previous);
        Assert.Equal(await w.Sql<decimal>("SELECT SUM(Amount) FROM Payments WHERE TenantId=@t AND Currency='USD' AND PaidAtUtc>=@f AND PaidAtUtc<@e", ("@f", s.FromUtc), ("@e", s.ToUtc)), rev.Money.Single(x => x.Currency == "USD").Amount);
        // every metric carries a label and a definition, and every tile is one of the documented metrics
        Assert.All(d.Metrics, m => { Assert.False(string.IsNullOrWhiteSpace(m.Definition)); Assert.True(Enum.IsDefined(m.Kind)); Assert.NotEmpty(m.Sources); });
        Assert.Equal(MetricKind.Calculated, d.Metric("rate.delivery").Kind); Assert.Equal(MetricKind.Verified, d.Metric("leads.created").Kind);
        Assert.Equal(20, d.Metrics.Count);
    }

    [RequiresMySqlFact]
    public async Task Contact_eligible_leads_equal_the_audience_builders_distinct_leads()
    {
        await using var w = await AnalyticsWorld.OpenAsync(app); await Scenario.SeedAsync(w);
        var viaAudience = await w.Scoped(async sp =>
        {
            var consent = sp.GetRequiredService<LeadPilot.Application.Crm.IContactEligibilityService>();
            var leads = new HashSet<Guid>();
            for (var page = 1; ; page++)
            {
                var r = await consent.QueryAudienceAsync(new LeadPilot.Application.Crm.AudienceQuery { Page = page, PageSize = 100 });
                foreach (var c in r.Items) leads.Add(c.LeadId);
                if (page >= r.TotalPages) break;
            }
            return leads.Count;
        });
        var d = await w.DashboardAsync(w.AdminApi, AnalyticsWorld.Custom(w.Today.AddDays(-50), w.Today));
        Assert.Equal(3, viaAudience); Assert.Equal(viaAudience, d.Metric("leads.eligible").Value);
    }

    [RequiresMySqlFact]
    public async Task The_rollup_equals_a_live_query_and_the_dashboard_reads_it()
    {
        await using var w = await AnalyticsWorld.OpenAsync(app); var s = await Scenario.SeedAsync(w);
        var range = AnalyticsWorld.Custom(s.From, s.To);
        var live = await w.DashboardAsync(w.AdminApi, range);
        Assert.Equal("Live", live.Freshness.Source);
        var r1 = await w.RefreshAsync(); Assert.True(r1.Full); Assert.True(r1.Rows > 0);

        // daily rows == live per-day cells, for every stream, over the whole retained window
        await w.Scoped(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>(); var q = new AnalyticsQueries(db, w.TenantId, null);
            var today = w.Today; var from = today.AddDays(-399);
            foreach (var key in StreamKeys.All)
            {
                var rolled = (await db.DailyMetrics.Where(m => m.TenantId == w.TenantId && m.MetricKey == key).OrderBy(m => m.Day).ThenBy(m => m.Dimension).Select(m => new { m.Day, m.Dimension, m.Count, m.Amount }).ToListAsync()).Select(m => new { Day = DateOnly.FromDateTime(m.Day), m.Dimension, m.Count, m.Amount }).ToList();
                var raw = (await q.DaysAsync(key, RangeResolver.DayStart(w.Tz, from), RangeResolver.DayStart(w.Tz, today.AddDays(1)), w.Tz, default)).Select(c => new { c.Day, Dimension = c.Dim, c.Count, c.Amount }).ToList();
                Assert.Equal(raw, rolled);
            }
        });
        var fromRollup = await w.DashboardAsync(w.AdminApi, range);
        Assert.Equal("Rollup", fromRollup.Freshness.Source);
        foreach (var m in live.Metrics.Where(m => m.Unit != "money")) { var r = fromRollup.Metric(m.Key); Assert.Equal(m.Value, r.Value); Assert.Equal(m.Previous, r.Previous); }
        Assert.Equal(live.Metric("revenue.verified").Money!.Select(x => (x.Currency, x.Amount, x.Previous)), fromRollup.Metric("revenue.verified").Money!.Select(x => (x.Currency, x.Amount, x.Previous)));
        Assert.Equal(live.Trends.SelectMany(t => t.Points.Select(p => (t.Key, p.Day, p.Value))), fromRollup.Trends.SelectMany(t => t.Points.Select(p => (t.Key, p.Day, p.Value))));

        // recomputed, not incremented: a second refresh (incremental, then full) leaves the same totals
        var before = await w.Sql<long>("SELECT COALESCE(SUM(`Count`),0) FROM DailyMetrics WHERE TenantId=@t");
        await w.RefreshAsync(); await w.RefreshAsync(full: true);
        Assert.Equal(before, await w.Sql<long>("SELECT COALESCE(SUM(`Count`),0) FROM DailyMetrics WHERE TenantId=@t"));
        Assert.Equal(1, await w.Sql<long>("SELECT COUNT(*) FROM AnalyticsRefreshStates WHERE TenantId=@t"));
    }

    [RequiresMySqlFact]
    public async Task A_late_event_inside_the_incremental_window_is_picked_up_by_the_next_refresh()
    {
        await using var w = await AnalyticsWorld.OpenAsync(app); var s = await Scenario.SeedAsync(w, daysAgo: 2);
        await w.RefreshAsync(full: true);
        var range = AnalyticsWorld.Custom(s.From, s.To, compare: false);
        var before = (await w.DashboardAsync(w.AdminApi, range)).Metric("leads.created").Value;
        await w.SeedAsync(db => { db.Leads.Add(w.NewLead("Late arrival", s.FromUtc.AddHours(3), w.Agent.Id)); return Task.CompletedTask; });
        app.Api!.Services.GetRequiredService<AnalyticsCache>().Invalidate(w.TenantId);
        Assert.Equal(before, (await w.DashboardAsync(w.AdminApi, range)).Metric("leads.created").Value);          // the rollup has not been refreshed yet: still "as of" the earlier refresh
        await w.RefreshAsync();
        Assert.Equal(before + 1, (await w.DashboardAsync(w.AdminApi, range)).Metric("leads.created").Value);
    }

    [RequiresMySqlFact]
    public async Task Freshness_reports_the_refresh_time_and_marks_old_data_stale()
    {
        await using var w = await AnalyticsWorld.OpenAsync(app); var s = await Scenario.SeedAsync(w);
        await w.RefreshAsync(full: true);
        var asOf = await w.Sql<DateTime>("SELECT LastRefreshedAtUtc FROM AnalyticsRefreshStates WHERE TenantId=@t");
        var d = await w.DashboardAsync(w.AdminApi, AnalyticsWorld.Custom(s.From, s.To));
        Assert.Equal("Rollup", d.Freshness.Source); Assert.Equal(asOf, d.Freshness.AsOfUtc); Assert.False(d.Freshness.Stale);
        // three hours without a refresh: the background job is late, so the indicator says so
        await w.Sql<long>("UPDATE AnalyticsRefreshStates SET LastRefreshedAtUtc = @x WHERE TenantId=@t", ("@x", asOf.AddHours(-3)));
        var stale = await w.DashboardAsync(w.AdminApi, AnalyticsWorld.Custom(s.From, s.To));
        Assert.True(stale.Freshness.Stale); Assert.Equal(asOf.AddHours(-3), stale.Freshness.AsOfUtc);
    }

    [RequiresMySqlFact]
    public async Task Date_filter_boundaries_are_inclusive_at_the_start_and_exclusive_at_the_next_midnight()
    {
        await using var w = await AnalyticsWorld.OpenAsync(app); var s = await Scenario.SeedAsync(w);
        async Task<long> Created(DateOnly a, DateOnly b) => (await w.DashboardAsync(w.AdminApi, AnalyticsWorld.Custom(a, b, false))).Metric("leads.created").Value!.Value is var v ? (long)v : 0;
        Assert.Equal(2, await Created(s.D0, s.D0));                          // A1 (00:00:00 exactly) and A3; A2 (23:59:59 the day before) and A4 (next midnight) are NOT in
        Assert.Equal(2, await Created(s.D1, s.D1));                          // A4 (00:00:00 exactly) and archived A5
        Assert.Equal(4, await Created(s.D0, s.D1));                          // to-date is inclusive
        Assert.Equal(1, await Created(s.D0.AddDays(-1), s.D0.AddDays(-1)));  // A2
        Assert.Equal(1, await Created(s.D0.AddDays(2), s.D0.AddDays(2)));    // A6 (local midnight of D0+2)
        // raw check of the same boundaries in UTC (workspace zone is UTC+5)
        Assert.Equal(2, await w.Sql<long>("SELECT COUNT(*) FROM Leads WHERE TenantId=@t AND CreatedAtUtc>=@f AND CreatedAtUtc<@e", ("@f", s.FromUtc), ("@e", w.L(s.D1))));
        // invalid ranges are refused, not guessed
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await w.AdminApi.Get($"/api/v1/analytics/dashboard?preset=Custom&from={s.D1:yyyy-MM-dd}&to={s.D0:yyyy-MM-dd}")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await w.AdminApi.Get("/api/v1/analytics/dashboard?preset=Custom&from=2020-01-01&to=2024-12-31")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await w.AdminApi.Get("/api/v1/analytics/dashboard?preset=Custom")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Daylight_saving_days_are_whole_local_days()
    {
        // America/New_York: 2026-03-08 has 23 hours (spring forward), 2026-11-01 has 25 hours. An event at local 00:00 and at 23:30 both belong to that day; the next local midnight belongs to the next day.
        await using var w = await AnalyticsWorld.OpenAsync(app, "America/New_York", "dst");
        var spring = new DateOnly(2026, 3, 8); var fall = new DateOnly(2026, 11, 1);
        await w.SeedAsync(db =>
        {
            foreach (var (day, i) in new[] { spring, spring.AddDays(1), fall, fall.AddDays(1) }.Select((d, i) => (d, i)))
            {
                db.Leads.Add(w.NewLead($"dst-start-{i}", w.L(day, 0, 0, 0), null)); db.Leads.Add(w.NewLead($"dst-late-{i}", w.L(day, 23, 30, 0), null));
            }
            return Task.CompletedTask;
        });
        Assert.Equal(23, (w.L(spring.AddDays(1)) - w.L(spring)).TotalHours); Assert.Equal(25, (w.L(fall.AddDays(1)) - w.L(fall)).TotalHours);
        async Task<long> Created(DateOnly a, DateOnly b, ApiSession s) => (await w.DashboardAsync(s, AnalyticsWorld.Custom(a, b, false))).Metric("leads.created").Value!.Value is var v ? (long)v : 0;
        Assert.Equal(2, await Created(spring, spring, w.AdminApi)); Assert.Equal(2, await Created(fall, fall, w.AdminApi)); Assert.Equal(4, await Created(spring, spring.AddDays(1), w.AdminApi));
        await w.RefreshAsync(full: true);   // the rollup folds the same instants into the same local days
        Assert.Equal(2, await Created(spring, spring, w.AdminApi)); Assert.Equal(2, await Created(fall, fall, w.AdminApi)); Assert.Equal(8, await Created(spring, fall.AddDays(1), w.AdminApi));
        var d = await w.DashboardAsync(w.AdminApi, AnalyticsWorld.Custom(spring, spring, false)); Assert.Equal("Rollup", d.Freshness.Source);
    }

    [RequiresMySqlFact]
    public async Task Every_report_total_matches_the_raw_rows()
    {
        await using var w = await AnalyticsWorld.OpenAsync(app); var s = await Scenario.SeedAsync(w);
        var range = AnalyticsWorld.Custom(s.From, s.To);
        static long L(object? o) => o is System.Text.Json.JsonElement e ? (long)e.GetDecimal() : Convert.ToInt64(o); static decimal Dm(object? o) => o is System.Text.Json.JsonElement e ? e.GetDecimal() : Convert.ToDecimal(o);
        static string? S(object? o) => o is System.Text.Json.JsonElement e ? (e.ValueKind == System.Text.Json.JsonValueKind.Null ? null : e.ToString()) : o?.ToString();

        // 1 discovery by city (paged in SQL): rows by city, totals row over ALL jobs
        var city = await w.ReportAsync(w.AdminApi, "discovery-by-city", range);
        Assert.Equal(3, city.TotalRows); var lahore = city.Rows.Single(r => S(r[0]) == "Lahore");
        Assert.Equal([2L, 1L, 0L, 1L, 1L, 0L, 1L, 3L], lahore.Skip(1).Take(8).Select(L).ToArray()); Assert.Equal(0.051m, Dm(lahore[9]));
        Assert.Equal(1L, L(city.Rows.Single(r => S(r[0]) == "(no city)")[6])); Assert.Equal(1, L(city.Rows.Single(r => S(r[0]) == "Karachi")[3]));
        Assert.Equal([4L, 1L, 1L, 2L, 1L, 1L, 2L, 5L], city.Totals!.Skip(1).Take(8).Select(L).ToArray()); Assert.Equal(0.085m, Dm(city.Totals![9]));
        Assert.Equal(await w.Sql<long>("SELECT COUNT(*) FROM DiscoveryResults WHERE TenantId=@t AND RetrievedAtUtc>=@f AND RetrievedAtUtc<@e", ("@f", s.FromUtc), ("@e", s.ToUtc)), L(city.Totals[1]));
        Assert.Contains(city.Definition.Columns, c => c.Key == "cost" && c.Kind == MetricKind.Estimated);

        // 2 lead sources
        var src = await w.ReportAsync(w.AdminApi, "lead-sources", range);
        var manual = src.Rows.Single(r => S(r[0]) == "Manual");
        Assert.Equal([2L, 1L, 1L], [L(manual[1]), L(manual[2]), L(manual[3])]); Assert.Equal(50.0m, Dm(manual[4]));
        Assert.Equal([4L, 1L, 1L], [L(src.Totals![1]), L(src.Totals[2]), L(src.Totals[3])]); Assert.Equal(25.0m, Dm(src.Totals[4]));
        Assert.Equal(await w.Sql<long>("SELECT COUNT(*) FROM Leads WHERE TenantId=@t AND CreatedAtUtc>=@f AND CreatedAtUtc<@e", ("@f", s.FromUtc), ("@e", s.ToUtc)), L(src.Totals[1]));

        // 3 campaigns (statistics from the Phase 6 rollup)
        var camp = await w.ReportAsync(w.AdminApi, "campaigns", range);
        Assert.Equal(1, camp.TotalRows); var cr = camp.Rows.Single();
        Assert.Equal("Spring push", S(cr[0])); Assert.Equal("Completed", S(cr[1])); Assert.Equal([4L, 3L, 2L, 1L, 1L, 1L], cr.Skip(3).Take(6).Select(L).ToArray()); Assert.Equal([66.7m, 33.3m], [Dm(cr[9]), Dm(cr[10])]);
        Assert.Equal(L(cr[4]), L(camp.Totals![4]));

        // 4 pipeline (today's snapshot)
        var pipe = await w.ReportAsync(w.AdminApi, "sales-pipeline", range);
        var open = pipe.Rows.First(r => S(r[2]) == "Open" && L(r[3]) > 0); Assert.Equal(5, L(open[3])); Assert.Equal("USD 200.00", S(open[5]));
        Assert.Equal(1, L(pipe.Rows.Single(r => S(r[2]) == "Won")[3])); Assert.Equal(6, L(pipe.Totals![3]));
        Assert.Equal(await w.Sql<long>("SELECT COUNT(*) FROM Leads WHERE TenantId=@t AND IsArchived=0"), L(pipe.Totals[3]));
        Assert.Equal(1, L(pipe.Totals[4]));

        // 5 funnel
        var funnel = await w.ReportAsync(w.AdminApi, "conversion-funnel", range);
        Assert.Equal([4L, 2L, 2L, 3L, 1L, 3L, 1L, 2L, 1L], funnel.Rows.Select(r => L(r[1])).ToArray()); Assert.Equal(50.0m, Dm(funnel.Rows[1][2])); Assert.Equal(25.0m, Dm(funnel.Rows[8][3]));

        // 6 representatives (team view)
        var reps = await w.ReportAsync(w.AdminApi, "rep-performance", range);
        var agentRow = reps.Rows.Single(r => S(r[0])!.Contains("-agent-")); var mgrRow = reps.Rows.Single(r => S(r[0])!.Contains("-mgr-"));
        Assert.Equal([4L, 1L, 1L, 1L, 0L, 1L], agentRow.Skip(1).Take(6).Select(L).ToArray()); Assert.Equal("", S(agentRow[7]));
        Assert.Equal([2L, 1L, 0L, 0L, 1L, 0L], mgrRow.Skip(1).Take(6).Select(L).ToArray()); Assert.Equal("PKR 500.00", S(mgrRow[7]));
        Assert.DoesNotContain(reps.Rows, r => S(r[0])!.Contains("-agent2-"));

        // 7 AI agents
        var ai = await w.ReportAsync(w.AdminApi, "ai-agent", range);
        var withAgent = ai.Rows.Single(r => S(r[0]) == "Sales agent"); var without = ai.Rows.Single(r => S(r[0])!.StartsWith("(knowledge"));
        Assert.Equal([2L, 2L, 0L, 0L, 1L, 1L, 0L, 0L, 300L, 1L, 1L], withAgent.Skip(1).Take(11).Select(L).ToArray()); Assert.Equal(50.0m, Dm(withAgent[12]));
        Assert.Equal([1L, 0L, 1L, 0L, 0L, 0L, 0L, 1L, 300L], without.Skip(1).Take(9).Select(L).ToArray()); Assert.Equal(100.0m, Dm(without[13]));
        Assert.Contains(ai.Notes, n => n.StartsWith("ESTIMATED") && n.Contains("0.0123") && n.Contains("1 time"));

        // 8 demo conversion, 9 trial conversion
        var demos = await w.ReportAsync(w.AdminApi, "demo-conversion", range);
        Assert.Equal([2L, 1L, 0L, 0L, 1L, 1L, 1L, 1L], demos.Rows.Select(r => L(r[1])).ToArray()); Assert.Equal(50.0m, Dm(demos.Rows[1][2])); Assert.Equal(100.0m, Dm(demos.Rows[6][2]));
        var trials = await w.ReportAsync(w.AdminApi, "trial-conversion", range);
        Assert.Equal([2L, 0L, 0L, 1L, 1L, 0L, 1L], trials.Rows.Select(r => L(r[1])).ToArray()); Assert.Equal(50.0m, Dm(trials.Rows[3][2])); Assert.Equal(50.0m, Dm(trials.Rows[6][2]));

        // 10 revenue: only verified payments, per currency, no grand total
        var revenue = await w.ReportAsync(w.AdminApi, "subscription-revenue", range);
        Assert.Equal(2, revenue.Rows.Count); Assert.Null(revenue.Totals);
        var usd = revenue.Rows.Single(r => S(r[0]) == "USD"); Assert.Equal([2L, 1L], [L(usd[1]), L(usd[2])]); Assert.Equal(150.50m, Dm(usd[3])); Assert.Equal(75.25m, Dm(usd[4]));
        Assert.Equal(20000m, Dm(revenue.Rows.Single(r => S(r[0]) == "PKR")[3]));
        Assert.Equal(await w.Sql<decimal>("SELECT SUM(Amount) FROM Payments WHERE TenantId=@t AND Currency='USD' AND PaidAtUtc>=@f AND PaidAtUtc<@e", ("@f", s.FromUtc), ("@e", s.ToUtc)), Dm(usd[3]));
        Assert.Equal(0, await w.Sql<long>("SELECT COUNT(*) FROM Payments WHERE TenantId=@t AND Amount=5000"));

        // 11 messaging usage (per local day) — also from the rollup
        var msg = await w.ReportAsync(w.AdminApi, "messaging-usage", range);
        Assert.Equal(2, msg.Rows.Count); Assert.Equal([2L, 1L, 0L, 1L], msg.Rows[0].Skip(1).Take(4).Select(L).ToArray()); Assert.Equal([1L, 1L, 1L, 1L], msg.Rows[1].Skip(1).Take(4).Select(L).ToArray());
        Assert.Equal([3L, 2L, 1L, 2L], msg.Totals!.Skip(1).Take(4).Select(L).ToArray()); Assert.Equal(66.7m, Dm(msg.Totals![5]));
        await w.RefreshAsync(full: true);
        var msgRollup = await w.ReportAsync(w.AdminApi, "messaging-usage", range);
        Assert.Equal("Rollup", msgRollup.Freshness.Source); Assert.Equal(msg.Rows.Select(r => string.Join('|', r.Select(S))), msgRollup.Rows.Select(r => string.Join('|', r.Select(S)))); Assert.Equal(string.Join('|', msg.Totals.Select(S)), string.Join('|', msgRollup.Totals!.Select(S)));

        // every report has labelled, defined columns that match its cells
        foreach (var def in ReportCatalog.All) Assert.All(def.Columns, c => { Assert.False(string.IsNullOrWhiteSpace(c.Definition)); Assert.True(Enum.IsDefined(c.Kind)); });
        Assert.Equal(11, ReportCatalog.All.Count);
    }

    [RequiresMySqlFact]
    public async Task Empty_workspace_shows_zeros_and_empty_reports_never_sample_data()
    {
        await using var w = await AnalyticsWorld.OpenAsync(app);
        var d = await w.DashboardAsync(w.AdminApi, AnalyticsWorld.Custom(w.Today.AddDays(-5), w.Today));
        Assert.All(d.Metrics.Where(m => m.Unit == "count"), m => Assert.Equal(0, m.Value));
        Assert.All(d.Metrics.Where(m => m.Unit == "percent"), m => Assert.Null(m.Value));      // no denominator: no rate, never 0 %
        Assert.Empty(d.Metric("revenue.verified").Money!);
        Assert.All(d.Trends, t => Assert.All(t.Points, p => Assert.Equal(0, p.Value)));
        foreach (var key in new[] { "discovery-by-city", "lead-sources", "campaigns", "subscription-revenue", "ai-agent" }) Assert.Empty((await w.ReportAsync(w.AdminApi, key, AnalyticsWorld.Custom(w.Today.AddDays(-5), w.Today))).Rows);
        var html = await (await WebSession.LoginAsync(app, w.Admin.Email, w.Admin.Password)).Html("/");
        Assert.Contains("No recorded activity yet", html); Assert.Contains("Data as of", html);
    }
}
