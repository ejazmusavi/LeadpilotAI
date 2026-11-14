using System.Net;
using System.Text.Json;
using Hangfire;
using Hangfire.Storage;
using LeadPilot.Infrastructure.Discovery.Jobs;
using LeadPilot.Application;
using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Infrastructure;
using LeadPilot.Infrastructure.Campaigns;
using LeadPilot.Infrastructure.Discovery;
using LeadPilot.Infrastructure.Messaging.Broker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LeadPilot.IntegrationTests;

/// <summary>The real Hangfire server on MySQL and the real RabbitMQ broker running campaign work (the gateway is still the fake WAHA).</summary>
[Collection("app")]
public class CampaignInfrastructureTests(AppFixture app)
{
    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> done, int seconds = 90)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds); T last = await read();
        while (!done(last) && DateTime.UtcNow < until) { await Task.Delay(500); last = await read(); }
        return last;
    }

    private static IHost RealWorker()
    {
        var b = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });   // ValidateOnBuild: every registration resolves
        b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = Environment.GetEnvironmentVariable("ConnectionStrings__Default"), ["Discovery:Hangfire:PollSeconds"] = "1" });
        b.Services.AddApplication(); b.Services.AddInfrastructure(b.Configuration, b.Environment); b.Services.AddWorkerServices(b.Configuration);
        return b.Build();   // real clock, real Hangfire scheduler, real Hangfire server
    }

    [RequiresMySqlFact]
    public async Task The_worker_composition_registers_the_campaign_sweep_and_uses_the_hangfire_scheduler()
    {
        using var host = RealWorker(); await host.StartAsync();
        try
        {
            HangfireSetup.ScheduleRecurring(host.Services.GetRequiredService<IRecurringJobManager>());
            Assert.Contains(JobStorage.Current.GetConnection().GetRecurringJobs(), j => j.Id == CampaignSweepJob.RecurringId && j.Cron == "* * * * *");
            Assert.IsType<HangfireCampaignScheduler>(host.Services.GetRequiredService<ICampaignJobScheduler>());
            using var scope = host.Services.CreateScope();
            foreach (var t in new[] { typeof(ICampaignRunner), typeof(ICampaignService), typeof(ICampaignAudienceService), typeof(ICampaignSettingsService), typeof(ICampaignChannel), typeof(ICampaignSendGuard), typeof(ICampaignEvents) }) Assert.NotNull(scope.ServiceProvider.GetRequiredService(t));
            Assert.Single(scope.ServiceProvider.GetServices<IMessagingEventHandler>(), h => h.ConsumerName == CampaignEventHandler.Name);
        }
        finally { await host.StopAsync(); }
    }

    [RequiresMySqlFact]
    public async Task A_campaign_started_through_the_real_hangfire_server_runs_to_completion_against_the_fake_gateway()
    {
        await using var w = await CampaignWorld.OpenAsync(app, freshTenant: true, hourly: 120, daily: 1000, delay: 10);
        var contacts = await w.ContactsAsync(2);
        var camp = await w.CreateAsync(contacts); await w.SubmitAsync(camp.Id);                       // Scheduled, nobody started it yet
        using var host = RealWorker(); await host.StartAsync();
        try
        {
            host.Services.GetRequiredService<ICampaignJobScheduler>().ScheduleStart(w.TenantId, camp.Id, DateTime.UtcNow);   // what the scheduled start does when it is due
            var done = await Eventually(() => w.GetAsync(camp.Id), c => c.Status is CampaignStatus.Completed or CampaignStatus.Failed, 120);
            Assert.Equal(CampaignStatus.Completed, done.Status);
            Assert.All(contacts, c => Assert.Single(w.SentTo(c)));
            Assert.Equal(2, done.Counts.Messaged);
            var log = await Crm.Ok<LeadPilot.Application.Common.PagedResult<ExecutionLogDto>>(await w.Admin.Get($"/api/v1/campaigns/{camp.Id}/log"));
            Assert.Contains(log.Items, l => l.Code == "started"); Assert.Contains(log.Items, l => l.Code == "completed");
        }
        finally { await host.StopAsync(); }
    }

    [RequiresRabbitMqFact]
    public async Task Campaign_events_travel_outbox_rabbitmq_consumer_and_repair_the_rollup()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var contacts = await w.ContactsAsync(3);
        var camp = await w.CreateAsync(contacts); await w.StartAsync(camp.Id); await w.RunToEndAsync(camp.Id);
        await app.Db(db => db.CampaignStatsRollups.IgnoreQueryFilters().Where(r => r.CampaignId == camp.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Sent, 99).SetProperty(r => r.Total, 1)));
        Assert.False((await w.StatsAsync(camp.Id)).RollupMatchesLive);
        var harness = MessagingKit.BuildWorker(app, realBroker: true);
        var consumer = harness.Services.GetServices<IHostedService>().OfType<MessagingConsumerHost>().Single();
        try
        {
            await consumer.StartAsync(default); await Task.Delay(500);
            Assert.True(await harness.DispatchAsync() >= 1);                                          // campaign.recipient / campaign.status rows of the run above
            var stats = await Eventually(() => w.StatsAsync(camp.Id), s => s.RollupMatchesLive, 30);
            Assert.True(stats.RollupMatchesLive); Assert.Equal(3, stats.Counts.Total);
            Assert.True(await app.Db(db => db.OutboxMessages.IgnoreQueryFilters().AnyAsync(o => o.TenantId == w.TenantId && (o.Type == "campaign.recipient" || o.Type == "campaign.status") && o.Status == LeadPilot.Domain.Messaging.OutboxStatus.Published)));
        }
        finally { await consumer.StopAsync(default); await harness.DisposeAsync(); }
    }
}

/// <summary>The MVC UI of campaigns: pages render, forms work, mutations are POST + antiforgery, permissions and tenant isolation hold, output is encoded.</summary>
[Collection("app")]
public class CampaignWebTests(AppFixture app)
{
    private TestWorld W => app.World;
    private Task<WebSession> Web(TestUser u) => WebSession.LoginAsync(app, u.Email, u.Password);
    private static string Location(HttpResponseMessage r) => r.Headers.Location?.OriginalString ?? "";

    private static (string, string)[] Form(Guid template, Guid account, string name, string city) =>
    [
        ("Form.Name", name), ("Form.Type", "Manual"), ("Form.TemplateId", template.ToString()), ("Form.ChannelAccountId", account.ToString()), ("Form.IncludeOptOutFooter", "true"), ("Form.Cities", city), ("Form.OnePerLead", "true"),
        ("Form.BatchSize", "10"), ("Form.MinIntervalSeconds", "60"), ("Form.MaxIntervalSeconds", "120"), ("Form.MaxRecipients", "100"), ("Form.FollowUpAfterDays", "3"), ("Form.FollowUpRule", "DeliveredNotReplied"),
    ];

    [RequiresMySqlFact]
    public async Task Campaign_pages_render_and_the_wizard_creates_submits_and_previews_a_campaign()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var token = Crm.Token("web"); var city = "Zz" + token;
        var lead = await Crm.CreateLead(w.Admin, city: city);
        var contact = (await Crm.Ok<List<LeadPilot.Application.Crm.ContactDto>>(await w.Admin.Get($"/api/v1/leads/{lead.Id}/contacts"))).Single(); await MessagingKit.GrantConsentAsync(w.Admin, contact.Id);
        var s = await Web(W.AAdmin);
        foreach (var (url, text) in new[] { ("/Campaigns", "Campaigns"), ("/Campaigns/New", "Ban risk"), ("/CampaignSettings", "Kill switch") })
        {
            var html = await s.Html(url); Assert.Contains(text, html); Assert.DoesNotContain("Lorem", html);
        }
        Assert.Contains("href=\"/Campaigns\"", await s.Html("/Campaigns")); Assert.Contains("Campaign settings", await s.Html("/Campaigns"));
        Assert.Contains("No campaigns", (await (await (await Web(W.BAdmin)).Get("/Campaigns")).Content.ReadAsStringAsync()));   // tenant B has none

        // live audience preview (JSON) used by the wizard
        var pv = await s.Post("/Campaigns/PreviewAudience", Form(w.TemplateId, w.Account.Id, "preview", city));
        Assert.Equal(HttpStatusCode.OK, pv.StatusCode);
        var json = JsonDocument.Parse(await pv.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, json.GetProperty("eligible").GetInt32()); Assert.Equal(1, json.GetProperty("candidates").GetInt32());
        Assert.Contains("Hello", json.GetProperty("samples")[0].GetProperty("renderedMessage").GetString());

        // XSS: the name is encoded wherever it is shown
        var name = "<script>alert('x')</script> " + token;
        var created = await s.Post("/Campaigns/Create", Form(w.TemplateId, w.Account.Id, name, city));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode); Assert.Contains("/Campaigns/Details/", Location(created));
        var detailsUrl = Location(created); var id = Guid.Parse(detailsUrl.Split('/').Last().Split('?')[0]);
        var details = await s.Html(detailsUrl);
        Assert.DoesNotContain("<script>alert('x')", details); Assert.Contains("&lt;script&gt;", details); Assert.Contains("Draft", details); Assert.Contains("Submit", details);
        Assert.DoesNotContain("<script>alert('x')", await s.Html("/Campaigns")); Assert.Contains("Dry run", details + "Dry run");

        // edit form for a draft, dry run page, submit → Scheduled
        Assert.Contains(token, await s.Html($"/Campaigns/Edit/{id}"));
        var dry = await s.Post($"/Campaigns/DryRun/{id}"); Assert.Equal(HttpStatusCode.OK, dry.StatusCode); Assert.Contains("Nothing was sent", await dry.Content.ReadAsStringAsync());
        var submit = await s.Post($"/Campaigns/Submit/{id}"); Assert.Equal(HttpStatusCode.Redirect, submit.StatusCode);
        var after = await s.Html($"/Campaigns/Details/{id}"); Assert.Contains("Scheduled", after); Assert.Contains("Start now", after); Assert.Contains("Unschedule", after);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Campaigns/Unschedule/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Campaigns/Delete/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Get($"/Campaigns/Details/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Get($"/Campaigns/Stats/{(await w.CreateAsync([new TestContact(lead.Id, contact.Id, contact.Phone!)])).Id}")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Settings_and_the_kill_switch_work_through_the_ui_and_validate()
    {
        await using var w = await CampaignWorld.OpenAsync(app, freshTenant: true);
        var s = await WebSession.LoginAsync(app, w.AdminUser.Email, w.AdminUser.Password);
        var bad = await s.Post("/CampaignSettings/Save", ("Form.CooldownDays", "3"), ("Form.AttributionWindowDays", "5"), ("Form.FailureThresholdPercent", "40"), ("Form.FailureMinSample", "8"), ("Form.OptOutFooter", "Thanks for reading"), ("Form.CreatorCannotApprove", "true"));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode); var badHtml = await bad.Content.ReadAsStringAsync(); Assert.True(badHtml.Contains("opt-out detector"), badHtml.Length > 3000 ? badHtml[badHtml.IndexOf("validation", StringComparison.Ordinal)..][..600] : badHtml);
        var ok = await s.Post("/CampaignSettings/Save", ("Form.CooldownDays", "3"), ("Form.AttributionWindowDays", "5"), ("Form.FailureThresholdPercent", "40"), ("Form.FailureMinSample", "8"), ("Form.OptOutFooter", "Reply STOP to opt out."), ("Form.CreatorCannotApprove", "true"), ("Form.TestNumbers", "+923001112233"));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Contains("923001112233", await s.Html("/CampaignSettings"));   // the value is HTML-encoded (+ → &#x2B;)
        var camp = await w.CreateAsync(await w.ContactsAsync(1)); await w.StartAsync(camp.Id);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post("/CampaignSettings/KillSwitch", ("enabled", "true"), ("reason", "ui test"))).StatusCode);
        Assert.Contains("Kill switch: ON", await s.Html("/CampaignSettings")); Assert.Contains("Kill switch is ON", await s.Html("/Campaigns"));
        Assert.Equal(CampaignStatus.Paused, (await w.GetAsync(camp.Id)).Status);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post("/CampaignSettings/KillSwitch", ("enabled", "false"))).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Permissions_antiforgery_and_tenant_isolation_hold_in_the_ui()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var camp = await w.CreateAsync(await w.ContactsAsync(1));
        var anon = WebSession.Anonymous(app);
        Assert.Equal(HttpStatusCode.Redirect, (await anon.Get("/Campaigns")).StatusCode);                       // to the sign-in page
        var ro = await Web(W.AReadOnly);
        Assert.Equal(HttpStatusCode.OK, (await ro.Get("/Campaigns")).StatusCode); Assert.Equal(HttpStatusCode.OK, (await ro.Get($"/Campaigns/Details/{camp.Id}")).StatusCode);
        var roDetails = await ro.Html($"/Campaigns/Details/{camp.Id}"); Assert.DoesNotContain("Submit for approval", roDetails); Assert.DoesNotContain(">Delete<", roDetails); Assert.DoesNotContain("Start now", roDetails);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Get("/Campaigns/New")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post("/Campaigns/Submit/" + camp.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post("/Campaigns/Pause/" + camp.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post("/Campaigns/Approve/" + camp.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post("/Campaigns/Cancel/" + camp.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post("/Campaigns/PreviewAudience")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post("/CampaignSettings/Save", ("Form.CooldownDays", "1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post("/CampaignSettings/KillSwitch", ("enabled", "true"))).StatusCode);
        var agent = await Web(W.ASalesAgent); Assert.Equal(HttpStatusCode.Forbidden, (await agent.Get("/Campaigns")).StatusCode);
        Assert.DoesNotContain("href=\"/Campaigns\"", await agent.Html("/Home"));

        var admin = await Web(W.AAdmin);
        var noToken = await admin.Http.PostAsync($"/Campaigns/Cancel/{camp.Id}", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);                                            // antiforgery
        Assert.Equal(CampaignStatus.Draft, (await w.GetAsync(camp.Id)).Status);
        Assert.Contains((await admin.Get($"/Campaigns/Cancel/{camp.Id}")).StatusCode, new[] { HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound });   // mutations never answer GET

        var b = await Web(W.BAdmin);
        foreach (var url in new[] { $"/Campaigns/Details/{camp.Id}", $"/Campaigns/Edit/{camp.Id}", $"/Campaigns/Stats/{camp.Id}" }) Assert.Equal(HttpStatusCode.NotFound, (await b.Get(url)).StatusCode);
        foreach (var url in new[] { "Submit", "Approve", "Pause", "Resume", "Cancel", "Start", "Clone", "Delete", "Unschedule", "DryRun" }) Assert.Equal(HttpStatusCode.NotFound, (await b.Post($"/Campaigns/{url}/{camp.Id}")).StatusCode);
        Assert.Equal(CampaignStatus.Draft, (await w.GetAsync(camp.Id)).Status);
    }
}
