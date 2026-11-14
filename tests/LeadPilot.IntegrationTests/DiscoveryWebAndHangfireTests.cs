using System.Net;
using Hangfire;
using Hangfire.Storage;
using LeadPilot.Application;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Discovery;
using LeadPilot.Domain.Discovery;
using LeadPilot.Infrastructure;
using LeadPilot.Infrastructure.Discovery;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LeadPilot.IntegrationTests;

[Collection("app")]
public class DiscoveryWebTests(AppFixture app)
{
    private TestWorld W => app.World;
    private Task<WebSession> Web(TestUser u) => WebSession.LoginAsync(app, u.Email, u.Password);
    private Task<ApiSession> Api(TestUser u) => ApiSession.LoginAsync(app, u.Email, u.Password);
    private static async Task<string> Body(HttpResponseMessage r) => await r.Content.ReadAsStringAsync();

    [RequiresMySqlFact]
    public async Task Discovery_pages_render_with_real_data_empty_states_and_a_sidebar_that_shows_only_permitted_pages()
    {
        var manager = await Web(W.ASalesManager);
        foreach (var (url, text) in new[] { ("/Discovery", "Discovery"), ("/Discovery/New", "New discovery search"), ("/Discovery/History", "Search history"), ("/DiscoveryResults", "Review results"), ("/DiscoverySetup", "Categories &amp; locations") })
        {
            var html = await manager.Html(url);
            Assert.Contains(text, html); Assert.DoesNotContain("Lorem", html); Assert.Contains("Review results", html);   // sidebar entry
        }
        Assert.DoesNotContain("Connectors &amp; usage", await manager.Html("/Discovery"));   // no Integrations.Manage → no sidebar entry
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Get("/Connectors")).StatusCode);

        var admin = await Web(W.AAdmin);
        var connectors = await admin.Html("/Connectors");
        Assert.Contains("Connectors &amp; usage", connectors); Assert.Contains("Fake connector", connectors); Assert.Contains("TEST ONLY", connectors);
        Assert.Contains("Not configured", connectors);            // the extension points say so
        Assert.Contains("Needs an API key", connectors);          // Google Places without a key
        Assert.Contains("Meta Lead Ads", connectors);

        // read-only user: no discovery at all; agent: view only (no "New search" page)
        var ro = await Web(W.AReadOnly);
        foreach (var url in new[] { "/Discovery", "/Discovery/New", "/DiscoveryResults", "/DiscoverySetup", "/Connectors" }) Assert.Equal(HttpStatusCode.Forbidden, (await ro.Get(url)).StatusCode);
        Assert.DoesNotContain("Review results", await (await Web(W.AReadOnly)).Html("/Leads").ContinueWith(t => t.Result));
        var agent = await Web(W.ASalesAgent);
        Assert.Equal(HttpStatusCode.OK, (await agent.Get("/Discovery")).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await agent.Get("/Discovery/New")).StatusCode);
        Assert.DoesNotContain("New search", await agent.Html("/Discovery"));
        // anonymous: sign-in redirect
        var anon = WebSession.Anonymous(app); var r = await anon.Get("/Discovery");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode); Assert.Contains("/Account/Login", r.Headers.Location!.OriginalString);
    }

    [RequiresMySqlFact]
    public async Task A_search_started_from_the_wizard_shows_progress_results_and_can_be_reviewed_in_the_browser_flow()
    {
        var s = await Web(W.ASalesManager); var token = DiscoveryKit.NewToken(); app.Script.Set(token, new FakeScript.Behavior { Total = 4 });
        var wizard = await s.Html("/Discovery/New");
        Assert.Contains("Fake connector (TEST ONLY)", wizard); Assert.Contains("Pakistan", wizard);   // saved Pakistani locations from the education starter pack (tenant A is an Education workspace)

        var create = await s.PostWithToken("/Discovery/New", "/Discovery/Create", ("ConnectorType", DiscoveryKit.Fake), ("Country", "Pakistan"), ("City", "Lahore"), ("Keywords", token), ("MaxResults", "10"));
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        var jobId = Guid.Parse(create.Headers.Location!.OriginalString.Split('/').Last());
        var details = await s.Html($"/Discovery/Details/{jobId}"); Assert.Contains("Queued", details); Assert.Contains("progress", details, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"terminal\":false", await Body(await s.Get($"/Discovery/Status/{jobId}")));

        await DiscoveryKit.RunAsync(app, W.A.Id, jobId);
        Assert.Contains("\"terminal\":true", await Body(await s.Get($"/Discovery/Status/{jobId}")));
        Assert.Contains("Completed", await s.Html($"/Discovery/Details/{jobId}"));
        var list = await s.Html($"/DiscoveryResults?jobId={jobId}");
        Assert.Contains(FakeScript.Name(token, 0), list); Assert.Contains("no consent", list); Assert.Contains("Fake data for automated tests", list);   // attribution shown

        var api = await Api(W.ASalesManager); var results = await DiscoveryKit.Results(api, jobId);
        var detail = await s.Html($"/DiscoveryResults/Details/{results[0].Id}");
        Assert.Contains("ProviderReported", detail.Replace("reported by the provider, not independently verified", "ProviderReported")); Assert.Contains("Research notes", detail);

        var approve = await s.PostWithToken($"/DiscoveryResults/Details/{results[0].Id}", $"/DiscoveryResults/Approve/{results[0].Id}", ("categoryId", ""));
        Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode); Assert.Contains("/Leads/Details/", approve.Headers.Location!.OriginalString);
        Assert.Contains(results[0].Name!, await s.Html(approve.Headers.Location.OriginalString));
        var reject = await s.PostWithToken("/DiscoveryResults", $"/DiscoveryResults/Reject/{results[1].Id}", ("note", "no"), ("returnQuery", $"?jobId={jobId}"));
        Assert.Equal(HttpStatusCode.Redirect, reject.StatusCode);
        var bulk = await s.PostWithToken("/DiscoveryResults", "/DiscoveryResults/BulkApprove", ("ids", results[2].Id.ToString()), ("ids", results[3].Id.ToString()), ("onDuplicate", "Merge"), ("returnQuery", ""));
        Assert.Equal(HttpStatusCode.Redirect, bulk.StatusCode);
        Assert.Empty(await DiscoveryKit.Results(api, jobId));   // everything decided

        // mutating actions are POST only
        Assert.True((await s.Get($"/DiscoveryResults/Approve/{results[0].Id}")).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.True((await s.Get($"/Discovery/Cancel/{jobId}")).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        // a POST without the antiforgery token is refused
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.PostAsync($"/Discovery/Rerun/{jobId}", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
        // history offers "run again"
        Assert.Contains("Run again", await s.Html("/Discovery/History"));
    }

    [RequiresMySqlFact]
    public async Task Other_tenants_searches_and_results_are_404_in_the_browser_and_connector_settings_are_saved_without_echoing_the_key()
    {
        var a = await Api(W.AAdmin); var token = DiscoveryKit.NewToken(); app.Script.Set(token, new FakeScript.Behavior { Total = 2 });
        var job = await DiscoveryKit.CreateAndRun(app, a, W.A.Id, token, 5); var result = (await DiscoveryKit.Results(a, job.Id))[0];
        var b = await Web(W.BAdmin);
        foreach (var url in new[] { $"/Discovery/Details/{job.Id}", $"/Discovery/Status/{job.Id}", $"/DiscoveryResults/Details/{result.Id}" }) Assert.Equal(HttpStatusCode.NotFound, (await b.Get(url)).StatusCode);
        foreach (var url in new[] { $"/Discovery/Cancel/{job.Id}", $"/Discovery/Retry/{job.Id}", $"/Discovery/Rerun/{job.Id}", $"/DiscoveryResults/Approve/{result.Id}", $"/DiscoveryResults/Reject/{result.Id}" })
            Assert.Equal(HttpStatusCode.NotFound, (await b.PostWithToken("/Profile", url)).StatusCode);
        Assert.DoesNotContain(token, await b.Html("/Discovery")); Assert.DoesNotContain(token, await b.Html("/DiscoveryResults?status="));
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostWithToken("/Profile", "/Discovery/Create", ("ConnectorType", DiscoveryKit.Fake), ("Country", "X"), ("Keywords", "k"), ("MaxResults", "3"), ("tenantId", W.A.Id.ToString()))).StatusCode);

        var admin = await Web(W.AAdmin); var key = "KEY-WEB-" + Guid.NewGuid().ToString("N");
        var save = await admin.PostWithToken("/Connectors", "/Connectors/Save", ("type", DiscoveryKit.FakeKeyed), ("apiKey", key), ("enabled", "true"), ("monthlyCallLimit", "50"), ("costPerCallUsd", "0.02"));
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        var page = await admin.Html("/Connectors"); Assert.DoesNotContain(key, page); Assert.Contains(key[^4..], page);   // only the hint (last 4 characters) is shown Assert.Contains("50", page);
        var manager = await Web(W.ASalesManager);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostWithToken("/Profile", "/Connectors/Save", ("type", DiscoveryKit.Fake), ("enabled", "true"))).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task The_hangfire_dashboard_is_only_for_authenticated_platform_operators()
    {
        var anon = WebSession.Anonymous(app); var r = await anon.Get("/hangfire");
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode); Assert.True(r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await Web(W.AAdmin)).Get("/hangfire")).StatusCode);        // a tenant administrator is not a platform operator
        Assert.Equal(HttpStatusCode.Forbidden, (await (await Web(W.ASalesManager)).Get("/hangfire")).StatusCode);
        var platform = await WebSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        var ok = await platform.Get("/hangfire"); Assert.Equal(HttpStatusCode.OK, ok.StatusCode); Assert.Contains("Hangfire", await Body(ok));
    }
}

/// <summary>The real Worker composition (AddWorkerServices) with a REAL Hangfire server on MySQL: jobs are enqueued, serialised, executed inside the tenant scope and fail closed.</summary>
[Collection("app")]
public class DiscoveryHangfireTests(AppFixture app)
{
    private TestWorld W => app.World;

    private IHost BuildWorker()
    {
        var b = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });   // Development enables ValidateOnBuild: every registration must be resolvable without the HTTP edge
        b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = Environment.GetEnvironmentVariable("ConnectionStrings__Default"), ["Discovery:Hangfire:PollSeconds"] = "1", ["Discovery:PageDelayMs"] = "0",
        });
        b.Services.AddApplication(); b.Services.AddInfrastructure(b.Configuration, b.Environment); b.Services.AddWorkerServices(b.Configuration);
        DiscoveryKit.RegisterTestServices(b.Services, app.Script, scheduler: null);   // fake connector only; the REAL Hangfire scheduler stays
        return b.Build();
    }

    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> done, int seconds = 60)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds); T v;
        do { v = await read(); if (done(v)) return v; await Task.Delay(500); } while (DateTime.UtcNow < until);
        return v;
    }

    [RequiresMySqlFact]
    public async Task A_discovery_job_enqueued_in_hangfire_is_executed_by_the_worker_inside_its_tenant_scope()
    {
        using var host = BuildWorker(); await host.StartAsync();
        try
        {
            var api = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
            var token = DiscoveryKit.NewToken(); app.Script.Set(token, new FakeScript.Behavior { Total = 5 });
            var job = await DiscoveryKit.CreateJob(api, token, 10);               // created by the API (recording scheduler: nothing runs yet)
            var scheduler = host.Services.GetRequiredService<IDiscoveryJobScheduler>();
            Assert.IsType<LeadPilot.Infrastructure.Discovery.Jobs.HangfireDiscoveryScheduler>(scheduler);
            var hangfireId = scheduler.Enqueue(W.A.Id, job.Id);                    // the real thing: serialised into the Hangfire tables

            var done = await Eventually(() => DiscoveryKit.GetJob(api, job.Id), j => j.Status is DiscoveryJobStatus.Completed or DiscoveryJobStatus.Failed);
            Assert.Equal(DiscoveryJobStatus.Completed, done.Status); Assert.Equal(5, done.ResultsNew); Assert.Equal(5, (await DiscoveryKit.Results(api, job.Id)).Count);
            var state = await Eventually(() => Task.FromResult(JobStorage.Current.GetMonitoringApi().JobDetails(hangfireId)?.History.First().StateName), s => s == "Succeeded", 20);
            Assert.Equal("Succeeded", state);
        }
        finally { await host.StopAsync(); }
    }

    [RequiresMySqlFact]
    public async Task Hangfire_jobs_fail_closed_without_a_tenant_and_do_nothing_for_a_foreign_job_payload()
    {
        using var host = BuildWorker(); await host.StartAsync();
        try
        {
            var b = await ApiSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password);
            var token = DiscoveryKit.NewToken(); app.Script.Set(token, new FakeScript.Behavior { Total = 3 });
            var bJob = await DiscoveryKit.CreateJob(b, token, 5);
            var scheduler = host.Services.GetRequiredService<IDiscoveryJobScheduler>();

            // no tenant id: the job throws TenantContextMissingException (Hangfire records the failure and schedules its retry)
            var noTenant = scheduler.Enqueue(Guid.Empty, bJob.Id);
            var failure = await Eventually(() => Task.FromResult(JobStorage.Current.GetMonitoringApi().JobDetails(noTenant)?.History.FirstOrDefault(h => h.Data.ContainsKey("ExceptionType"))), h => h is not null, 40);
            Assert.NotNull(failure); Assert.Contains("TenantContextMissingException", failure!.Data["ExceptionType"]);
            BackgroundJob.Delete(noTenant);

            // tenant A + tenant B's job id: the job runs, finds nothing in A's scope, and B's job is untouched
            var foreign = scheduler.Enqueue(W.A.Id, bJob.Id);
            var succeeded = await Eventually(() => Task.FromResult(JobStorage.Current.GetMonitoringApi().JobDetails(foreign)?.History.First().StateName), s => s is "Succeeded" or "Failed", 30);
            Assert.Equal("Succeeded", succeeded);
            var after = await DiscoveryKit.GetJob(b, bJob.Id);
            Assert.Equal((DiscoveryJobStatus.Queued, 0), (after.Status, after.ApiCalls)); Assert.DoesNotContain(app.Script.Calls, c => c.Token == token);
        }
        finally { await host.StopAsync(); }
    }

    [RequiresMySqlFact]
    public async Task The_worker_composition_builds_and_registers_the_recurring_retention_job()
    {
        using var host = BuildWorker(); await host.StartAsync();
        try
        {
            LeadPilot.Infrastructure.Discovery.Jobs.HangfireSetup.ScheduleRecurring(host.Services.GetRequiredService<IRecurringJobManager>());
            var recurring = JobStorage.Current.GetConnection().GetRecurringJobs();
            Assert.Contains(recurring, j => j.Id == "discovery-retention" && j.Cron == "0 3 * * *");
            var q = host.Services.GetRequiredService<JobStorage>(); Assert.NotNull(q);
            foreach (var t in new[] { typeof(IDiscoveryJobRunner), typeof(IDiscoveryRetention), typeof(IDiscoveryConnectorRegistry) }) { using var scope = host.Services.CreateScope(); Assert.NotNull(scope.ServiceProvider.GetRequiredService(t)); }
            using var s2 = host.Services.CreateScope();
            var registry = s2.ServiceProvider.GetRequiredService<IDiscoveryConnectorRegistry>();
            Assert.Contains(registry.All, d => d.Key == ConnectorKeys.GooglePlaces); Assert.Contains(registry.All, d => d.Key == ConnectorKeys.Website);
        }
        finally { await host.StopAsync(); }
    }
}
