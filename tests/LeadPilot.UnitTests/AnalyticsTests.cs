using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Analytics;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Security;
using LeadPilot.Application.Tenancy;
using LeadPilot.Infrastructure.Analytics;

namespace LeadPilot.UnitTests;

public class AnalyticsTests
{
    private static readonly DateTime Now = new(2026, 5, 20, 21, 30, 0, DateTimeKind.Utc);   // 2026-05-21 02:30 in Asia/Karachi, 17:30 on the 20th in New York

    private static ResolvedRange R(RangePreset p, string zone = "UTC", DateOnly? from = null, DateOnly? to = null, bool compare = true) =>
        RangeResolver.Resolve(new RangeRequest { Preset = p, From = from, To = to, Compare = compare }, zone, Now, 800).Value;

    [Fact]
    public void Presets_are_workspace_local_calendar_days_with_inclusive_ends()
    {
        var utc = R(RangePreset.Last7Days); Assert.Equal(new DateOnly(2026, 5, 14), utc.FromLocal); Assert.Equal(new DateOnly(2026, 5, 20), utc.ToLocal); Assert.Equal(7, utc.Days);
        Assert.Equal(new DateTime(2026, 5, 14, 0, 0, 0), utc.FromUtc); Assert.Equal(new DateTime(2026, 5, 21, 0, 0, 0), utc.ToUtcExclusive);
        // the same instant is "today" = the 21st in Karachi (UTC+5) but the 20th in New York (UTC-4)
        Assert.Equal(new DateOnly(2026, 5, 21), R(RangePreset.Today, "Asia/Karachi").FromLocal); Assert.Equal(new DateOnly(2026, 5, 20), R(RangePreset.Today, "America/New_York").FromLocal);
        var khi = R(RangePreset.Today, "Asia/Karachi"); Assert.Equal(new DateTime(2026, 5, 20, 19, 0, 0), khi.FromUtc); Assert.Equal(new DateTime(2026, 5, 21, 19, 0, 0), khi.ToUtcExclusive);
        Assert.Equal(new DateOnly(2026, 5, 19), R(RangePreset.Yesterday).FromLocal);
        Assert.Equal(new DateOnly(2026, 5, 1), R(RangePreset.ThisMonth).FromLocal); Assert.Equal(new DateOnly(2026, 5, 20), R(RangePreset.ThisMonth).ToLocal);
        var last = R(RangePreset.LastMonth); Assert.Equal(new DateOnly(2026, 4, 1), last.FromLocal); Assert.Equal(new DateOnly(2026, 4, 30), last.ToLocal);
        Assert.Equal(new DateOnly(2026, 1, 1), R(RangePreset.ThisYear).FromLocal);
        Assert.Equal(30, R(RangePreset.Last30Days).Days); Assert.Equal(90, R(RangePreset.Last90Days).Days);
    }

    [Fact]
    public void The_previous_period_has_the_same_length_and_ends_the_day_before()
    {
        var r = R(RangePreset.Custom, "UTC", new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 14));
        Assert.Equal(5, r.Days); Assert.Equal(new DateOnly(2026, 3, 5), r.PrevFromLocal); Assert.Equal(new DateOnly(2026, 3, 9), r.PrevToLocal);
        Assert.Equal(r.PrevToUtcExclusive, r.FromUtc);                       // adjacent, no gap and no overlap
        var jan = R(RangePreset.LastMonth, "UTC"); Assert.Equal(30, jan.Days); Assert.Equal(new DateOnly(2026, 3, 2), jan.PrevFromLocal);   // 30 days before April 1st: not "the month before"
    }

    [Fact]
    public void Daylight_saving_days_are_23_and_25_hours_long_and_the_range_follows_the_wall_clock()
    {
        var spring = R(RangePreset.Custom, "America/New_York", new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 8)); Assert.Equal(23, (spring.ToUtcExclusive - spring.FromUtc).TotalHours);
        var fall = R(RangePreset.Custom, "America/New_York", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1)); Assert.Equal(25, (fall.ToUtcExclusive - fall.FromUtc).TotalHours);
        var span = R(RangePreset.Custom, "America/New_York", new DateOnly(2026, 3, 7), new DateOnly(2026, 3, 9)); Assert.Equal(71, (span.ToUtcExclusive - span.FromUtc).TotalHours);
        // an instant exactly at local midnight belongs to the NEW day
        Assert.Equal(new DateOnly(2026, 3, 9), RangeResolver.LocalDay(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), spring.ToUtcExclusive));
        Assert.Equal(new DateOnly(2026, 3, 8), RangeResolver.LocalDay(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"), spring.ToUtcExclusive.AddTicks(-1)));
    }

    [Fact]
    public void Invalid_ranges_are_refused_with_a_message()
    {
        RangeResolver.Resolve(new RangeRequest { Preset = RangePreset.Custom }, "UTC", Now, 800).Error.Message.Contains("start and an end");
        Assert.True(RangeResolver.Resolve(new RangeRequest { Preset = RangePreset.Custom }, "UTC", Now, 800).IsFailure);
        Assert.True(RangeResolver.Resolve(new RangeRequest { Preset = RangePreset.Custom, From = new DateOnly(2026, 2, 2), To = new DateOnly(2026, 2, 1) }, "UTC", Now, 800).IsFailure);
        Assert.True(RangeResolver.Resolve(new RangeRequest { Preset = RangePreset.Custom, From = new DateOnly(2020, 1, 1), To = new DateOnly(2026, 1, 1) }, "UTC", Now, 800).IsFailure);
        Assert.True(RangeResolver.Resolve(new RangeRequest { Preset = RangePreset.Custom, From = new DateOnly(2026, 1, 1), To = new DateOnly(2026, 1, 1) }, "No/Such_Zone", Now, 800).IsFailure);
        Assert.True(RangeResolver.Resolve(new RangeRequest { Preset = (RangePreset)99 }, "UTC", Now, 800).IsFailure);
    }

    [Theory]
    [InlineData(2, 3, 66.7)] [InlineData(1, 3, 33.3)] [InlineData(1, 8, 12.5)] [InlineData(0, 5, 0.0)] [InlineData(5, 5, 100.0)] [InlineData(1, 6, 16.7)] [InlineData(5, 8, 62.5)]
    public void Rates_are_rounded_to_one_decimal_away_from_zero(long n, long d, double expected) => Assert.Equal((decimal)expected, RangeResolver.Percent(n, d));

    [Fact]
    public void A_rate_without_a_denominator_is_not_a_rate_and_a_change_from_zero_is_undefined()
    {
        Assert.Null(RangeResolver.Percent(0, 0)); Assert.Null(RangeResolver.Percent(3, 0));
        Assert.Null(RangeResolver.Change(5, 0)); Assert.Null(RangeResolver.Change(5, null));
        Assert.Equal(50.0m, RangeResolver.Change(15, 10)); Assert.Equal(-25.0m, RangeResolver.Change(30, 40)); Assert.Equal(0.0m, RangeResolver.Change(7, 7));
    }

    [Fact]
    public void Every_recorded_stream_has_exactly_one_documented_metric_and_every_metric_has_a_label_and_definition()
    {
        Assert.Equal(StreamKeys.All.Count, StreamKeys.All.Distinct().Count());
        foreach (var k in StreamKeys.All) Assert.Single(MetricCatalog.All, m => m.Key == k);
        Assert.Equal(MetricCatalog.All.Count, MetricCatalog.All.Select(m => m.Key).Distinct().Count());
        Assert.All(MetricCatalog.All, m => { Assert.False(string.IsNullOrWhiteSpace(m.Definition)); Assert.False(string.IsNullOrWhiteSpace(m.Sources)); Assert.True(Enum.IsDefined(m.Kind)); });
        // calculated metrics state their formula; nothing is "Estimated" on the dashboard (estimates live in reports and are labelled there)
        Assert.All(MetricCatalog.All.Where(m => m.Kind == MetricKind.Calculated && m.Unit == "percent"), m => Assert.False(string.IsNullOrWhiteSpace(m.Formula)));
        Assert.All(MetricCatalog.All.Where(m => m.Snapshot), m => Assert.Contains("not affected by the date filter", m.Definition));
        // the eighteen metrics of the README exist
        string[] required = ["leads.total", "leads.created", "leads.verified", "leads.eligible", "leads.contacted", "messages.inbound", "leads.interested", "leads.qualified", "demos.scheduled", "demos.completed", "trials.started", "customers.won", "opportunities.lost", "campaigns.sent", "rate.campaign_reply", "ai.conversations", "handoffs.human", "rate.delivery", "rate.lead_conversion", "revenue.verified"];
        foreach (var k in required) Assert.Contains(MetricCatalog.All, m => m.Key == k);
    }

    [Fact]
    public void Every_report_has_labelled_defined_columns_and_estimates_are_marked()
    {
        Assert.Equal(11, ReportCatalog.All.Count); Assert.Equal(11, ReportCatalog.All.Select(r => r.Key).Distinct().Count());
        Assert.All(ReportCatalog.All, r => Assert.All(r.Columns, c => { Assert.False(string.IsNullOrWhiteSpace(c.Definition)); Assert.True(Enum.IsDefined(c.Kind)); Assert.Contains(c.Format, new[] { "text", "int", "percent", "money", "date" }); }));
        Assert.Contains(ReportCatalog.Find(ReportKeys.DiscoveryByCity)!.Columns, c => c.Key == "cost" && c.Kind == MetricKind.Estimated);
        Assert.All(ReportCatalog.All.SelectMany(r => r.Columns).Where(c => c.Format == "percent"), c => Assert.Equal(MetricKind.Calculated, c.Kind));
        Assert.Null(ReportCatalog.Find("nope"));
    }

    [Fact]
    public void The_documentation_lists_every_metric_and_report_with_its_sources()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LeadPilotAI.sln"))) dir = dir.Parent;
        var doc = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "METRICS.md"));
        foreach (var m in MetricCatalog.All) Assert.True(doc.Contains($"`{m.Key}`"), $"docs/METRICS.md does not document metric {m.Key}");
        foreach (var r in ReportCatalog.All) Assert.True(doc.Contains($"`{r.Key}`"), $"docs/METRICS.md does not document report {r.Key}");
        foreach (var t in new[] { "Verified", "Calculated", "Estimated", "inclusive", "exclusive", "Reports.Team", "Reports.Export", "Dashboard.View" }) Assert.Contains(t, doc);
    }

    [Fact]
    public void Default_roles_get_the_analytics_permissions_and_agents_stay_scoped()
    {
        IReadOnlyList<string> P(string role) => SystemRoles.All.Single(r => r.Name == role).Permissions;
        foreach (var c in new[] { PermissionCodes.DashboardView, PermissionCodes.ReportsTeam, PermissionCodes.ReportsExport, PermissionCodes.ReportsView }) Assert.Contains(c, PermissionCatalog.TenantCodes.Concat([PermissionCodes.ReportsView]));
        Assert.Contains(PermissionCodes.ReportsExport, P(SystemRoles.SalesManager)); Assert.Contains(PermissionCodes.ReportsTeam, P(SystemRoles.SalesManager)); Assert.Contains(PermissionCodes.DashboardView, P(SystemRoles.SalesManager));
        Assert.Contains(PermissionCodes.ReportsExport, P(SystemRoles.MarketingManager)); Assert.Contains(PermissionCodes.ReportsTeam, P(SystemRoles.MarketingManager));
        Assert.Contains(PermissionCodes.DashboardView, P(SystemRoles.SalesAgent)); Assert.Contains(PermissionCodes.ReportsView, P(SystemRoles.SalesAgent));
        Assert.DoesNotContain(PermissionCodes.ReportsTeam, P(SystemRoles.SalesAgent)); Assert.DoesNotContain(PermissionCodes.ReportsExport, P(SystemRoles.SalesAgent));
        foreach (var role in new[] { SystemRoles.ReadOnly, SystemRoles.TenantStaff }) { Assert.Contains(PermissionCodes.DashboardView, P(role)); Assert.Contains(PermissionCodes.ReportsTeam, P(role)); Assert.DoesNotContain(PermissionCodes.ReportsExport, P(role)); }
        Assert.Equal(PermissionCatalog.TenantCodes.Order(), P(SystemRoles.TenantAdministrator).Order());
    }

    [Fact]
    public void Cache_keys_are_built_with_the_tenant_helper_and_fail_closed()
    {
        var cache = new AnalyticsCache(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
        Assert.NotEqual(cache.Key(a, "dash:1"), cache.Key(b, "dash:1")); Assert.StartsWith($"t:{a:N}:analytics:v0:", cache.Key(a, "dash:1"));
        Assert.Throws<TenantContextMissingException>(() => cache.Key(Guid.Empty, "x"));
        var v0 = cache.Key(a, "k"); cache.Invalidate(a); Assert.NotEqual(v0, cache.Key(a, "k")); Assert.Equal(cache.Key(b, "k"), $"t:{b:N}:analytics:v0:k");   // invalidation is per tenant
    }

    [Fact]
    public async Task The_cache_serves_within_the_ttl_and_never_across_tenants()
    {
        var cache = new AnalyticsCache(); var a = Guid.NewGuid(); var b = Guid.NewGuid(); var calls = 0;
        Task<int> Make(int v) { calls++; return Task.FromResult(v); }
        Assert.Equal(1, await cache.GetOrAddAsync(a, "k", TimeSpan.FromMinutes(1), () => Make(1)));
        Assert.Equal(1, await cache.GetOrAddAsync(a, "k", TimeSpan.FromMinutes(1), () => Make(2)));         // served from the cache
        Assert.Equal(3, await cache.GetOrAddAsync(b, "k", TimeSpan.FromMinutes(1), () => Make(3)));         // same key text, other tenant: computed, not shared
        Assert.Equal(2, calls);
        cache.Invalidate(a); Assert.Equal(4, await cache.GetOrAddAsync(a, "k", TimeSpan.FromMinutes(1), () => Make(4)));
        Assert.Equal(5, await cache.GetOrAddAsync(a, "z", TimeSpan.Zero, () => Make(5))); Assert.Equal(6, await cache.GetOrAddAsync(a, "z", TimeSpan.Zero, () => Make(6)));   // TTL 0 = no caching
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")] [InlineData("+SUM(A1)", "'+SUM(A1)")] [InlineData("-cmd", "'-cmd")] [InlineData("@x", "'@x")] [InlineData("\tx", "'\tx")] [InlineData("-12.5", "-12.5")] [InlineData("Lahore", "Lahore")] [InlineData("", "")]
    public void Csv_cells_are_defused_for_spreadsheets(string input, string expected) => Assert.Equal(expected, SpreadsheetSafe.CsvCell(input));

    [Fact]
    public void Export_defaults_are_documented_limits() { var o = new AnalyticsOptions(); Assert.Equal(50_000, o.MaxExportRows); Assert.Equal(400, o.RollupRetentionDays); Assert.Equal(7, o.IncrementalDays); Assert.Equal(60, o.CacheSeconds); Assert.Equal(800, o.MaxRangeDays); Assert.Equal(200, o.MaxPageSize); }
}
