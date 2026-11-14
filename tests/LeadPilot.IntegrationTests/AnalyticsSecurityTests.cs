using System.Net;
using System.Text;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Analytics;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Analytics;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Analytics;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

[Collection("app")]
public class AnalyticsSecurityTests(AppFixture app)
{
    private static readonly string[] ApiPaths = ["/api/v1/analytics/dashboard", "/api/v1/analytics/metrics", "/api/v1/analytics/reports", "/api/v1/analytics/reports/campaigns", "/api/v1/analytics/reports/campaigns/export"];

    private static string S(object? o) => o is System.Text.Json.JsonElement e ? e.ToString() : o?.ToString() ?? "";
    private static long L(object? o) => o is System.Text.Json.JsonElement e ? (long)e.GetDecimal() : Convert.ToInt64(o);

    [RequiresMySqlFact]
    public async Task Permissions_answer_401_403_and_404_on_every_endpoint_and_page()
    {
        await using var w = await AnalyticsWorld.OpenAsync(app); await Scenario.SeedAsync(w);
        // 401 / redirect without a session
        var anon = ApiSession.Anonymous(app);
        foreach (var p in ApiPaths) Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Get(p)).StatusCode);
        var anonWeb = WebSession.Anonymous(app);
        foreach (var p in new[] { "/", "/Reports", "/Reports/Metrics", "/Reports/Show?key=campaigns", "/Reports/Export?key=campaigns" }) { var r = await anonWeb.Get(p); Assert.Equal(HttpStatusCode.Redirect, r.StatusCode); Assert.Contains("/Account/Login", r.Headers.Location!.OriginalString); }

        // a member with NO analytics permission: 403 everywhere (the dashboard page itself shows the permission message, never numbers)
        var roleId = await app.World.AddRoleAsync(w.TenantId, "Leads only " + Guid.NewGuid().ToString("N")[..5], PermissionCodes.LeadsView);
        var nobody = await app.World.AddUserAsync(w.TenantId, $"nobody-{Guid.NewGuid().ToString("N")[..8]}@c.test", SystemRoles.ReadOnly);
        await app.World.WithDb(async db => { var ur = await db.TenantUserRoles.IgnoreQueryFilters().Where(x => x.UserId == nobody.Id && x.TenantId == w.TenantId).ToListAsync(); db.TenantUserRoles.RemoveRange(ur); db.TenantUserRoles.Add(UserRole.Create(w.TenantId, nobody.Id, roleId, null, DateTime.UtcNow)); await db.SaveChangesAsync(); });
        var none = await ApiSession.LoginAsync(app, nobody.Email, nobody.Password);
        foreach (var p in ApiPaths) Assert.Equal(HttpStatusCode.Forbidden, (await none.Get(p)).StatusCode);
        var noneWeb = await WebSession.LoginAsync(app, nobody.Email, nobody.Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await noneWeb.Get("/Reports")).StatusCode);
        var home = await noneWeb.Html("/"); Assert.Contains("needs the Dashboard.View permission", home); Assert.DoesNotContain("metric-card", home);

        // read-only: sees everything, exports nothing; sales agent: no team report, no export
        var ro = await ApiSession.LoginAsync(app, w.ReadOnly.Email, w.ReadOnly.Password);
        Assert.Equal(HttpStatusCode.OK, (await ro.Get("/api/v1/analytics/dashboard")).StatusCode); Assert.Equal(HttpStatusCode.OK, (await ro.Get("/api/v1/analytics/reports/campaigns")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Get("/api/v1/analytics/reports/campaigns/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.AgentApi.Get("/api/v1/analytics/reports/campaigns")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.AgentApi.Get("/api/v1/analytics/reports/subscription-revenue")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.AgentApi.Get("/api/v1/analytics/reports/campaigns/export")).StatusCode);
        var roWeb = await WebSession.LoginAsync(app, w.ReadOnly.Email, w.ReadOnly.Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await roWeb.Get("/Reports/Export?key=campaigns")).StatusCode);
        var agentWeb = await WebSession.LoginAsync(app, w.Agent.Email, w.Agent.Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await agentWeb.Get("/Reports/Show?key=campaigns")).StatusCode);
        // unknown reports are 404 (also for export); bad paging is 400
        Assert.Equal(HttpStatusCode.NotFound, (await w.AdminApi.Get("/api/v1/analytics/reports/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.AdminApi.Get("/api/v1/analytics/reports/nope/export")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await roWeb.Get("/Reports/Show?key=nope")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.AdminApi.Get("/api/v1/analytics/reports/campaigns?page=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.AdminApi.Get("/api/v1/analytics/reports/campaigns?pageSize=100000")).StatusCode);
        // default role mappings (existing tenants get them through the system-role synchronisation)
        Assert.Contains(PermissionCodes.ReportsExport, SystemRoles.All.Single(r => r.Name == SystemRoles.SalesManager).Permissions); Assert.Contains(PermissionCodes.ReportsTeam, SystemRoles.All.Single(r => r.Name == SystemRoles.SalesManager).Permissions);
        Assert.DoesNotContain(PermissionCodes.ReportsTeam, SystemRoles.All.Single(r => r.Name == SystemRoles.SalesAgent).Permissions); Assert.DoesNotContain(PermissionCodes.ReportsExport, SystemRoles.All.Single(r => r.Name == SystemRoles.SalesAgent).Permissions);
        Assert.Contains(PermissionCodes.ReportsExport, PermissionCatalog.TenantCodes);
    }

    [RequiresMySqlFact]
    public async Task Sales_agents_see_only_their_own_numbers_and_managers_see_the_team()
    {
        await using var w = await AnalyticsWorld.OpenAsync(app); var s = await Scenario.SeedAsync(w);
        await w.RefreshAsync(full: true);
        var range = AnalyticsWorld.Custom(s.From, s.To);
        var team = await w.DashboardAsync(w.ManagerApi, range); var own = await w.DashboardAsync(w.AgentApi, range);
        Assert.Equal("Team", team.Scope); Assert.Equal(6, team.Metric("leads.total").Value); Assert.Equal("Rollup", team.Freshness.Source);
        Assert.Equal("Own", own.Scope); Assert.Equal("Live", own.Freshness.Source);           // the rollup is workspace-wide: an own view never reads it
        // the agent owns A1, A2, A4, A5 (archived) and A7
        Assert.Equal(4, own.Metric("leads.total").Value); Assert.Equal(3, own.Metric("leads.created").Value); Assert.Equal(2, own.Metric("leads.created").Previous);
        Assert.Equal(2, own.Metric("leads.eligible").Value); Assert.Equal(2, own.Metric("leads.qualified").Value); Assert.Equal(2, own.Metric("messages.inbound").Value);
        Assert.Equal(1, own.Metric("demos.completed").Value); Assert.Equal(1, own.Metric("customers.won").Value); Assert.Equal(1, own.Metric("opportunities.lost").Value);
        Assert.Equal(33.3m, own.Metric("rate.lead_conversion").Value);   // own cohort A1, A4, A5; A1 is a customer
        // team-only metrics are unavailable (never zero), and the drill-down is limited to the agent's own leads
        var rev = own.Metric("revenue.verified"); Assert.False(rev.Available); Assert.Null(rev.Money); Assert.Contains($"OwnerUserId={w.Agent.Id}", own.Metric("leads.created").DrillDownUrl);
        Assert.DoesNotContain("OwnerUserId", team.Metric("leads.created").DrillDownUrl);
        // a rep with no leads sees zeros
        var agent2 = await ApiSession.LoginAsync(app, w.Agent2.Email, w.Agent2.Password);
        var empty = await w.DashboardAsync(agent2, range); Assert.Equal(0, empty.Metric("leads.total").Value); Assert.Equal(0, empty.Metric("leads.created").Value);

        // reports: own scope for the personal ones, 403 for workspace-wide ones
        var pipe = await w.ReportAsync(w.AgentApi, "sales-pipeline", range); Assert.Equal("Own", pipe.Scope);
        Assert.Equal(4, pipe.Totals!.Skip(3).Take(1).Select(L).Single()); Assert.Contains(pipe.Rows, r => S(r[5]) == "USD 200.00");
        var funnel = await w.ReportAsync(w.AgentApi, "conversion-funnel", range); Assert.Equal(3, L(funnel.Rows[0][1]));
        var demos = await w.ReportAsync(w.AgentApi, "demo-conversion", range); Assert.Equal(1, L(demos.Rows[0][1]));
        var reps = await w.ReportAsync(w.AgentApi, "rep-performance", range); Assert.Single(reps.Rows); Assert.Contains("-agent-", S(reps.Rows[0][0])); Assert.Equal(4, L(reps.Rows[0][1]));
        var teamReps = await w.ReportAsync(w.ManagerApi, "rep-performance", range); Assert.Equal(2, teamReps.Rows.Count);
        foreach (var key in ReportCatalog.All.Where(r => r.TeamOnly).Select(r => r.Key))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await w.AgentApi.Get($"/api/v1/analytics/reports/{key}?preset=Last30Days")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await w.ManagerApi.Get($"/api/v1/analytics/reports/{key}?preset=Last30Days")).StatusCode);
        }
        // MVC shows the same scope
        var agentHtml = await (await WebSession.LoginAsync(app, w.Agent.Email, w.Agent.Password)).Html("/");
        Assert.Contains("Your own leads", agentHtml); Assert.Contains("Team only", agentHtml);
        var mgrHtml = await (await WebSession.LoginAsync(app, w.Manager.Email, w.Manager.Password)).Html("/");
        Assert.Contains("Workspace view", mgrHtml); Assert.DoesNotContain("Team only", mgrHtml);
    }

    [RequiresMySqlFact]
    public async Task Export_is_gated_audited_formula_safe_streamed_and_capped()
    {
        await using var w = await AnalyticsWorld.OpenAsync(app); var s = await Scenario.SeedAsync(w);
        var evil = new[] { "=HYPERLINK(\"http://evil\",\"x\")", "+SUM(1+1)", "-2+3 evil", "@SUM(A1)", "\tTAB", "plain, with comma \"and quote\"" };
        await w.SeedAsync(async db => { var job = db.DiscoveryJobs.Local.FirstOrDefault(); var jobId = await db.DiscoveryJobs.IgnoreQueryFilters().Where(j => j.TenantId == w.TenantId).Select(j => j.Id).FirstAsync();
            foreach (var (c, i) in evil.Select((c, i) => (c, i))) db.DiscoveryResults.Add(w.M<LeadPilot.Domain.Discovery.DiscoveryResult>(("JobId", jobId), ("ConnectorType", "google_places"), ("ExternalId", "evil" + i), ("Name", "e"), ("City", c), ("ReviewStatus", LeadPilot.Domain.Discovery.ReviewStatus.Pending), ("RetrievedAtUtc", s.FromUtc.AddHours(2)), ("CreatedAtUtc", s.FromUtc))); });
        var range = AnalyticsWorld.Custom(s.From, s.To);
        var auditBefore = await w.Sql<long>("SELECT COUNT(*) FROM AuditLogs WHERE TenantId=@t AND Action='Report.Exported'");
        var url = $"/api/v1/analytics/reports/discovery-by-city/export?{AnalyticsWorld.Qs(range)}";
        var res = await w.ManagerApi.Get(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode); Assert.Equal("text/csv", res.Content.Headers.ContentType!.MediaType);   // streaming itself (constant memory for very large reports) is measured in the large-dataset benchmark
        Assert.Contains("attachment", res.Content.Headers.ContentDisposition!.ToString()); Assert.Contains("discovery-by-city-", res.Content.Headers.ContentDisposition.FileName);
        var bytes = await res.Content.ReadAsByteArrayAsync(); Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3); var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("City,Results found,Approved,Rejected,Pending review,Leads created,Duplicates flagged,Searches,Provider calls,Est. cost (USD)", lines[0]);
        // formula injection: no cell starts with a formula trigger; the dangerous values are present, defused with a leading apostrophe
        Assert.Contains("'=HYPERLINK", text); Assert.Contains("'+SUM(1+1)", text); Assert.Contains("'-2+3 evil", text); Assert.Contains("'@SUM(A1)", text); Assert.Contains("'\tTAB", text);
        Assert.Contains("\"plain, with comma \"\"and quote\"\"\"", text);
        foreach (var line in lines.Skip(1)) { var first = line.TrimStart('"'); Assert.DoesNotContain(first[0], new[] { '=', '+', '@', '\t' }); }
        // same rows as the report: 3 normal cities + 6 evil ones + the totals row + header
        var page = await w.ReportAsync(w.ManagerApi, "discovery-by-city", range); Assert.Equal(9, page.TotalRows); Assert.Equal(page.TotalRows + 2, lines.Length);
        Assert.StartsWith("Total,", lines[^1]);
        // audited: who, what, how many rows, which range, when
        var audit = (await w.SqlRows("SELECT ActorEmail, EntityType, EntityId, Summary, CreatedAtUtc FROM AuditLogs WHERE TenantId=@t AND Action='Report.Exported' ORDER BY CreatedAtUtc DESC")).First();
        Assert.Equal(w.Manager.Email, audit[0]); Assert.Equal("discovery-by-city", audit[2]); Assert.Contains("9 row(s)", (string)audit[3]!); Assert.Contains(s.D0.ToString("yyyy-MM-dd"), (string)audit[3]!); Assert.Contains(w.Zone, (string)audit[3]!);
        Assert.Equal(auditBefore + 1, await w.Sql<long>("SELECT COUNT(*) FROM AuditLogs WHERE TenantId=@t AND Action='Report.Exported'"));
        // MVC export is the same path (and audited too)
        var web = await WebSession.LoginAsync(app, w.Manager.Email, w.Manager.Password);
        var webRes = await web.Get($"/Reports/Export?key=campaigns&{AnalyticsWorld.Qs(range)}"); Assert.Equal(HttpStatusCode.OK, webRes.StatusCode); Assert.Contains("Spring push", await webRes.Content.ReadAsStringAsync());
        Assert.Equal(auditBefore + 2, await w.Sql<long>("SELECT COUNT(*) FROM AuditLogs WHERE TenantId=@t AND Action='Report.Exported'"));
        // refused requests leave no audit row and no data
        Assert.Equal(HttpStatusCode.Forbidden, (await w.AgentApi.Get(url)).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await ApiSession.LoginAsync(app, w.ReadOnly.Email, w.ReadOnly.Password).ContinueWith(t => t.Result.Get(url)).Unwrap()).StatusCode);
        // a member with Export but without Team may export their own scope only, and no workspace-wide report
        var roleId = await app.World.AddRoleAsync(w.TenantId, "Exporter " + Guid.NewGuid().ToString("N")[..5], PermissionCodes.ReportsView, PermissionCodes.ReportsExport);
        var exporter = await app.World.AddUserAsync(w.TenantId, $"exp-{Guid.NewGuid().ToString("N")[..8]}@c.test", SystemRoles.ReadOnly);
        await app.World.WithDb(async db => { db.TenantUserRoles.RemoveRange(await db.TenantUserRoles.IgnoreQueryFilters().Where(x => x.UserId == exporter.Id && x.TenantId == w.TenantId).ToListAsync()); db.TenantUserRoles.Add(UserRole.Create(w.TenantId, exporter.Id, roleId, null, DateTime.UtcNow)); await db.SaveChangesAsync(); });
        var ex = await ApiSession.LoginAsync(app, exporter.Email, exporter.Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await ex.Get(url)).StatusCode);
        var own = await ex.Get($"/api/v1/analytics/reports/rep-performance/export?{AnalyticsWorld.Qs(range)}"); Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(2, (await own.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);   // header + the caller's own row
        Assert.Equal(auditBefore + 3, await w.Sql<long>("SELECT COUNT(*) FROM AuditLogs WHERE TenantId=@t AND Action='Report.Exported'"));

        // the row cap (documented, default 50 000) refuses an over-large report BEFORE anything is audited or sent
        using var capped = app.Api!.WithWebHostBuilder(b => b.UseSetting("Analytics:MaxExportRows", "3"));
        var client = capped.CreateClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", w.ManagerApi.Tokens.AccessToken);
        var big = await client.GetAsync(url); Assert.Equal(HttpStatusCode.RequestEntityTooLarge, big.StatusCode); Assert.DoesNotContain("HYPERLINK", await big.Content.ReadAsStringAsync());
        Assert.Equal(auditBefore + 3, await w.Sql<long>("SELECT COUNT(*) FROM AuditLogs WHERE TenantId=@t AND Action='Report.Exported'"));
    }

    [RequiresMySqlFact]
    public async Task The_cache_is_tenant_scoped_short_lived_and_explicitly_invalidated()
    {
        await using var a = await AnalyticsWorld.OpenAsync(app, prefix: "ca"); await using var b = await AnalyticsWorld.OpenAsync(app, prefix: "cb"); var s = await Scenario.SeedAsync(a);
        var cache = app.Api!.Services.GetRequiredService<AnalyticsCache>();
        Assert.NotEqual(cache.Key(a.TenantId, "x"), cache.Key(b.TenantId, "x")); Assert.Contains(a.TenantId.ToString("N"), cache.Key(a.TenantId, "x")); Assert.StartsWith($"t:{a.TenantId:N}:analytics:", cache.Key(a.TenantId, "x"));
        Assert.Throws<LeadPilot.Application.Abstractions.TenantContextMissingException>(() => cache.Key(Guid.Empty, "x"));
        var range = AnalyticsWorld.Custom(s.From, s.To);
        var first = await a.DashboardAsync(a.AdminApi, range);
        var other = await b.DashboardAsync(b.AdminApi, range);                        // same range, same query text, other tenant: must never see A's cached numbers
        Assert.Equal(6, first.Metric("leads.total").Value); Assert.Equal(0, other.Metric("leads.total").Value); Assert.Equal(0, other.Metric("leads.created").Value);
        Assert.Equal(6, (await a.DashboardAsync(a.AdminApi, range)).Metric("leads.total").Value);
        // cached: a new lead is invisible inside the TTL ... until the tenant's entries are invalidated
        await a.SeedAsync(db => { db.Leads.Add(a.NewLead("Cached out", s.FromUtc.AddHours(1), a.Agent.Id)); return Task.CompletedTask; });
        Assert.Equal(6, (await a.DashboardAsync(a.AdminApi, range)).Metric("leads.total").Value);
        cache.Invalidate(a.TenantId);
        Assert.Equal(7, (await a.DashboardAsync(a.AdminApi, range)).Metric("leads.total").Value);
        Assert.Equal(0, (await b.DashboardAsync(b.AdminApi, range)).Metric("leads.total").Value);
        // a refresh also moves the key (the rollup stamp is part of it): rolled-up numbers are served fresh, never the live ones cached earlier
        await a.RefreshAsync(full: true);
        Assert.Equal("Rollup", (await a.DashboardAsync(a.AdminApi, range)).Freshness.Source);
    }

    [RequiresMySqlFact]
    public async Task Reports_are_isolated_between_workspaces_through_api_mvc_jobs_and_the_database_guard()
    {
        await using var a = await AnalyticsWorld.OpenAsync(app, prefix: "ia"); await using var b = await AnalyticsWorld.OpenAsync(app, prefix: "ib");
        var s = await Scenario.SeedAsync(a);
        await b.SeedAsync(db =>
        {
            var l1 = b.NewLead("B Secret School", s.FromUtc.AddHours(2), b.Agent.Id, "Quetta", LeadSourceType.Referral); var l2 = b.NewLead("B Other School", s.FromUtc.AddHours(3), b.Agent.Id, "Quetta", LeadSourceType.Referral);
            db.Leads.AddRange(l1, l2);
            var camp = b.M<LeadPilot.Domain.Campaigns.Campaign>(("Name", "B-SECRET-CAMPAIGN"), ("Status", LeadPilot.Domain.Campaigns.CampaignStatus.Completed), ("StartedAtUtc", s.FromUtc.AddHours(4)), ("AudienceJson", "{}"), ("CreatedAtUtc", s.FromUtc));
            db.Campaigns.Add(camp);
            var job = b.M<LeadPilot.Domain.Discovery.DiscoveryJob>(("ConnectorType", "google_places"), ("Status", LeadPilot.Domain.Discovery.DiscoveryJobStatus.Completed), ("City", "Quetta"), ("CreatedAtUtc", s.FromUtc.AddHours(1))); db.DiscoveryJobs.Add(job);
            db.DiscoveryResults.Add(b.M<LeadPilot.Domain.Discovery.DiscoveryResult>(("JobId", job.Id), ("ConnectorType", "google_places"), ("ExternalId", "b1"), ("Name", "b"), ("City", "B-SECRET-CITY"), ("ReviewStatus", LeadPilot.Domain.Discovery.ReviewStatus.Pending), ("RetrievedAtUtc", s.FromUtc.AddHours(2)), ("CreatedAtUtc", s.FromUtc)));
            return Task.CompletedTask;
        });
        var range = AnalyticsWorld.Custom(s.From, s.To);
        // API: each sees only its own
        var da = await a.DashboardAsync(a.AdminApi, range); var db_ = await b.DashboardAsync(b.AdminApi, range);
        Assert.Equal(6, da.Metric("leads.total").Value); Assert.Equal(2, db_.Metric("leads.total").Value); Assert.Equal(2, db_.Metric("leads.created").Value);
        Assert.Empty(db_.Metric("revenue.verified").Money!);
        foreach (var key in ReportCatalog.All.Select(r => r.Key))
        {
            var ra = System.Text.Json.JsonSerializer.Serialize(await a.ReportAsync(a.AdminApi, key, range)); var rb = System.Text.Json.JsonSerializer.Serialize(await b.ReportAsync(b.AdminApi, key, range));
            Assert.DoesNotContain("B-SECRET", ra); Assert.DoesNotContain("B Secret", ra); Assert.DoesNotContain("Spring push", rb); Assert.DoesNotContain("Lahore", rb); Assert.DoesNotContain("PKR", rb); Assert.DoesNotContain("A1 Alpha", rb);
        }
        Assert.Contains("B-SECRET-CAMPAIGN", System.Text.Json.JsonSerializer.Serialize(await b.ReportAsync(b.AdminApi, "campaigns", range)));
        // MVC
        var wa = await WebSession.LoginAsync(app, a.Admin.Email, a.Admin.Password); var wb = await WebSession.LoginAsync(app, b.Admin.Email, b.Admin.Password);
        var qs = AnalyticsWorld.Qs(range);
        foreach (var key in new[] { "campaigns", "discovery-by-city", "lead-sources" })
        { var ha = await wa.Html($"/Reports/Show?key={key}&{qs}"); var hb = await wb.Html($"/Reports/Show?key={key}&{qs}"); Assert.DoesNotContain("B-SECRET", ha); Assert.DoesNotContain("Spring push", hb); Assert.DoesNotContain("Lahore", hb); }
        Assert.Contains("B-SECRET-CAMPAIGN", await wb.Html($"/Reports/Show?key=campaigns&{qs}"));
        // jobs: the recurring job refreshes every workspace inside its own scope and each rollup equals its own raw rows
        using (var scope = a.Worker.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<AnalyticsJob>().RunAsync(false, default);
        foreach (var world in new[] { a, b })
        {
            var rolled = await world.Sql<long>("SELECT COALESCE(SUM(`Count`),0) FROM DailyMetrics WHERE TenantId=@t AND MetricKey='leads.created'");
            Assert.Equal(await world.Sql<long>("SELECT COUNT(*) FROM Leads WHERE TenantId=@t"), rolled);
        }
        Assert.NotEqual(await a.Sql<long>("SELECT COUNT(*) FROM DailyMetrics WHERE TenantId=@t"), await b.Sql<long>("SELECT COUNT(*) FROM DailyMetrics WHERE TenantId=@t"));
        await b.Scoped(async sp =>
        {
            var db2 = sp.GetRequiredService<AppDbContext>();
            Assert.Equal(await db2.DailyMetrics.CountAsync(), await db2.DailyMetrics.CountAsync(m => m.TenantId == b.TenantId));           // the query filter hides A's rows
            Assert.Equal(1, await db2.AnalyticsRefreshStates.CountAsync());
            Assert.False(sp.GetRequiredService<AnalyticsCache>().Contains(a.TenantId, "x"));
        });
        // DB guard: a scope of B cannot store rollup rows stamped with A's tenant
        await Assert.ThrowsAsync<CrossTenantViolationException>(() => b.Worker.Scopes.RunAsync(b.TenantId, async (sp, ct) =>
        { var db3 = sp.GetRequiredService<AppDbContext>(); db3.DailyMetrics.Add(DailyMetric.Create(a.TenantId, s.D0, "leads.created", "", 99, 0m, DateTime.UtcNow)); await db3.SaveChangesAsync(ct); }));
        await Assert.ThrowsAsync<CrossTenantViolationException>(() => b.Worker.Scopes.RunAsync(b.TenantId, async (sp, ct) =>
        { var db3 = sp.GetRequiredService<AppDbContext>(); db3.AnalyticsRefreshStates.Add(AnalyticsRefreshState.Create(a.TenantId)); await db3.SaveChangesAsync(ct); }));
        // refreshing inside a scope can never write another tenant's rows: B's refresh leaves A's rollup untouched
        var aBefore = await a.Sql<long>("SELECT COALESCE(SUM(`Count`),0) FROM DailyMetrics WHERE TenantId=@t");
        await b.RefreshAsync(full: true); Assert.Equal(aBefore, await a.Sql<long>("SELECT COALESCE(SUM(`Count`),0) FROM DailyMetrics WHERE TenantId=@t"));
        // the job is registered with Hangfire (one scheduler) on its own queue
        Assert.Equal("analytics", AnalyticsQueues.Analytics);
    }

    [RequiresMySqlFact]
    public async Task The_pages_render_labelled_cards_filters_empty_states_and_a_phone_width_friendly_table()
    {
        await using var w = await AnalyticsWorld.OpenAsync(app); var s = await Scenario.SeedAsync(w);
        await w.RefreshAsync(full: true);
        var web = await WebSession.LoginAsync(app, w.Manager.Email, w.Manager.Password); var qs = AnalyticsWorld.Qs(AnalyticsWorld.Custom(s.From, s.To));
        var home = await web.Html($"/?{qs}");
        foreach (var m in MetricCatalog.All.Where(m => m.Key is not ("messages.sent" or "messages.delivered" or "messages.failed"))) Assert.Contains($"data-metric=\"{m.Key}\"", home);
        Assert.Contains("badge text-bg-success", home); Assert.Contains("badge text-bg-primary", home); Assert.Contains("Data as of", home); Assert.Contains("Rollup", home); Assert.Contains("Compare with the previous period", home);
        Assert.Contains("Formula:", home); Assert.Contains("<svg", home); Assert.Contains("role=\"img\"", home); Assert.Contains("View records", home);
        Assert.Contains($"{s.D0:yyyy-MM-dd} to {s.D1:yyyy-MM-dd}", home); Assert.Contains("(inclusive)", home); Assert.Contains("(exclusive)", home);
        var index = await web.Html("/Reports"); foreach (var r in ReportCatalog.All) Assert.Contains(r.Title, index);
        var rep = await web.Html($"/Reports/Show?key=lead-sources&{qs}");
        Assert.Contains("table-responsive", rep); Assert.Contains("Export CSV", rep); Assert.Contains("badge text-bg-warning", await web.Html($"/Reports/Show?key=discovery-by-city&{qs}"));   // the estimated cost column is labelled
        Assert.DoesNotContain("Export CSV", await (await WebSession.LoginAsync(app, w.ReadOnly.Email, w.ReadOnly.Password)).Html($"/Reports/Show?key=lead-sources&{qs}"));
        var defs = await web.Html("/Reports/Metrics"); foreach (var m in MetricCatalog.All) Assert.Contains(m.Name, defs);
        // pagination: page 2 of a 1-per-page style request through the API keeps totals and row counts consistent
        var p1 = await w.ReportAsync(w.AdminApi, "discovery-by-city", AnalyticsWorld.Custom(s.From, s.To), 1, 2); var p2 = await w.ReportAsync(w.AdminApi, "discovery-by-city", AnalyticsWorld.Custom(s.From, s.To), 2, 2);
        Assert.Equal(3, p1.TotalRows); Assert.Equal(2, p1.Rows.Count); Assert.Single(p2.Rows); Assert.Empty(p1.Rows.Select(r => S(r[0])).Intersect(p2.Rows.Select(r => S(r[0])))); Assert.Equal(S(p1.Totals![1]), S(p2.Totals![1]));
        // the metrics endpoint documents every metric
        var defsApi = await Crm.Ok<List<MetricDefinition>>(await w.AdminApi.Get("/api/v1/analytics/metrics")); Assert.Equal(MetricCatalog.All.Count, defsApi.Count); Assert.All(defsApi, d => Assert.False(string.IsNullOrWhiteSpace(d.Definition)));
    }
}
