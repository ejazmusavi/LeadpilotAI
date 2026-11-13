using System.Collections.Concurrent;
using System.Net;
using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>TEST-ONLY scheduler: records the Hangfire jobs the campaign module would enqueue; the tests run start/tick deterministically.</summary>
public sealed class RecordingCampaignScheduler : ICampaignJobScheduler
{
    public sealed record Job(string Kind, Guid TenantId, Guid CampaignId, DateTime? AtUtc, TimeSpan? Delay, string Id);
    public ConcurrentQueue<Job> Jobs { get; } = new();
    public ConcurrentQueue<string> Deleted { get; } = new();
    public string ScheduleStart(Guid tenantId, Guid campaignId, DateTime atUtc) { var id = "cs-" + Guid.NewGuid().ToString("N")[..8]; Jobs.Enqueue(new("start", tenantId, campaignId, atUtc, null, id)); return id; }
    public void EnqueueTick(Guid tenantId, Guid campaignId, TimeSpan? delay = null) => Jobs.Enqueue(new("tick", tenantId, campaignId, null, delay, "ct-" + Guid.NewGuid().ToString("N")[..8]));
    public void Delete(string? jobId) { if (jobId is not null) Deleted.Enqueue(jobId); }
    public IEnumerable<Job> For(Guid campaignId) => Jobs.Where(j => j.CampaignId == campaignId);
}

public sealed record TestContact(Guid LeadId, Guid ContactId, string Phone)
{
    public string Chat => Phone.TrimStart('+') + "@c.us";
}

/// <summary>One tenant with a connected fake-WAHA account, a marketing template and a worker harness: everything a campaign test needs, driven through the real API and the real worker code.</summary>
public sealed class CampaignWorld : IAsyncDisposable
{
    public const string DefaultBody = "Hello {{first_name|there}}, we help schools in {{city|your city}}.";
    private TimeSpan _clockAtOpen;
    public AppFixture App { get; private init; } = default!;
    public Guid TenantId { get; private init; }
    public ApiSession Admin { get; private init; } = default!;
    public TestUser AdminUser { get; private init; } = default!;
    public TestAccount Account { get; private init; } = default!;
    public Guid TemplateId { get; private set; }
    public WorkerHarness Worker { get; private init; } = default!;

    /// <summary>Tenant A with the run's shared account (the default), or a brand-new tenant (needed when a test changes workspace-wide policy or account limits).</summary>
    public static async Task<CampaignWorld> OpenAsync(AppFixture app, bool freshTenant = false, int? hourly = null, int? daily = null, int? delay = null, string body = DefaultBody, bool prepareAccount = true)
    {
        Guid tenantId; ApiSession admin; TestUser user; TestAccount account;
        if (freshTenant)
        {
            var (tenant, adm, _) = await app.World.NewTenantAsync("cm");
            tenantId = tenant.Id; user = adm; admin = await ApiSession.LoginAsync(app, adm.Email, adm.Password);
            account = await MessagingKit.CreateAccountAsync(app, admin, tenantId, hourly: hourly ?? 120, daily: daily ?? 1000, delay: delay ?? 10);
        }
        else
        {
            tenantId = app.World.A.Id; user = app.World.AAdmin; admin = await ApiSession.LoginAsync(app, user.Email, user.Password);
            account = await MessagingKit.SharedAccountAAsync(app);
        }
        app.Waha.ClearFaults(); app.Waha.Working(account.Session);
        if (prepareAccount) await MessagingKit.PrepareAccountAsync(app, account.Id);
        var w = new CampaignWorld { App = app, TenantId = tenantId, Admin = admin, AdminUser = user, Account = account, Worker = MessagingKit.BuildWorker(app), _clockAtOpen = app.Clock.Offset };
        await w.NewTemplateAsync(body);
        return w;
    }

    public async Task<Guid> NewTemplateAsync(string body = DefaultBody, string category = "Marketing")
    {
        var res = await Admin.Post("/api/v1/message-templates", new { name = "Promo " + Guid.NewGuid().ToString("N")[..8], body, category });
        var dto = await Crm.Ok<TemplateDto>(res);
        TemplateId = dto.Id;
        return dto.Id;
    }

    // ---------- data ----------

    public async Task<TestContact> ContactAsync(bool consent = true, string? city = "Lahore")
    {
        var lead = await Crm.CreateLead(Admin, city: city, phone: "+9230" + Random.Shared.Next(10_000_000, 99_999_999));
        var contact = (await Crm.Ok<List<LeadPilot.Application.Crm.ContactDto>>(await Admin.Get($"/api/v1/leads/{lead.Id}/contacts"))).Single();
        if (consent) await MessagingKit.GrantConsentAsync(Admin, contact.Id);
        return new TestContact(lead.Id, contact.Id, contact.Phone!);
    }

    public async Task<List<TestContact>> ContactsAsync(int n, bool consent = true) { var l = new List<TestContact>(); for (var i = 0; i < n; i++) l.Add(await ContactAsync(consent)); return l; }

    // ---------- campaigns ----------

    public async Task<CampaignDto> CreateAsync(IEnumerable<TestContact> contacts, string? name = null, string type = "Manual", int batchSize = 10, int? daily = null, int? failurePercent = null, int cooldownDays = 0,
        DateTime? scheduledAtUtc = null, int maxRecipients = 500, bool onePerLead = true, object? audienceOverride = null, Guid? templateId = null, Guid? accountId = null)
    {
        var res = await CreateRawAsync(Admin, new
        {
            name = name ?? "Campaign " + Guid.NewGuid().ToString("N")[..8], type, templateId = templateId ?? TemplateId, channelAccountId = accountId ?? Account.Id,
            audience = audienceOverride ?? new { contactIds = contacts.Select(c => c.ContactId).ToArray(), onePerLead }, batchSize, minIntervalSeconds = 15, maxIntervalSeconds = 20, maxRecipients, dailyLimit = daily,
            failureThresholdPercent = failurePercent, cooldownDays, scheduledAtUtc,
        });
        return await Crm.Ok<CampaignDto>(res);
    }

    public static Task<HttpResponseMessage> CreateRawAsync(ApiSession s, object body) => s.Post("/api/v1/campaigns", body);

    public async Task<CampaignDto> GetAsync(Guid id, ApiSession? s = null) => await Crm.Ok<CampaignDto>(await (s ?? Admin).Get($"/api/v1/campaigns/{id}"));

    public async Task<CampaignDto> SubmitAsync(Guid id) => await Crm.Ok<CampaignDto>(await Admin.Post($"/api/v1/campaigns/{id}/submit"));

    /// <summary>Submit, start by hand (API) and run the start job: the campaign is Running with its first tick pending.</summary>
    public async Task<CampaignDto> StartAsync(Guid id)
    {
        var c = await GetAsync(id);
        if (c.Status == CampaignStatus.Draft) await SubmitAsync(id);
        await Crm.Ok<CampaignDto>(await Admin.Post($"/api/v1/campaigns/{id}/start"));
        await RunAsync(sp => sp.GetRequiredService<ICampaignRunner>().StartAsync(id, default));
        return await GetAsync(id);
    }

    public Task RunAsync(Func<IServiceProvider, Task> work, Guid? tenant = null) => Worker.Scopes.RunAsync(tenant ?? TenantId, (sp, _) => work(sp));
    public Task TickAsync(Guid id) => RunAsync(sp => sp.GetRequiredService<ICampaignRunner>().TickAsync(id, default));
    public Task SweepAsync() => RunAsync(sp => sp.GetRequiredService<ICampaignRunner>().SweepAsync(default));

    public Task<List<Guid>> QueuedMessagesAsync(Guid campaignId) => App.Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.CampaignId == campaignId && m.CampaignRecipientId != null && m.Status == MessageStatus.Queued).OrderBy(m => m.CreatedAtUtc).Select(m => m.Id).ToListAsync());

    /// <summary>Runs the Phase 5 send job for every message of the campaign that is waiting in the queue; moves the test clock past the minimum gap after each send.</summary>
    public async Task<int> SendQueuedAsync(Guid campaignId, bool advanceClock = true)
    {
        var ids = await QueuedMessagesAsync(campaignId);
        foreach (var id in ids) { await Worker.RunSendAsync(TenantId, id); if (advanceClock) App.Clock.Offset += TimeSpan.FromSeconds(31); }
        return ids.Count;
    }

    /// <summary>Ticks and sends until the campaign is finished (or paused/cancelled), like the real worker would over time.</summary>
    public async Task<CampaignDto> RunToEndAsync(Guid id, int maxRounds = 40)
    {
        CampaignDto c = await GetAsync(id);
        for (var i = 0; i < maxRounds; i++)
        {
            await TickAsync(id);
            await SendQueuedAsync(id);
            c = await GetAsync(id);
            if (c.Status != CampaignStatus.Running) break;
            App.Clock.Offset += TimeSpan.FromSeconds(30);
        }
        return c;
    }

    public async Task<List<RecipientDto>> RecipientsAsync(Guid id, string query = "", ApiSession? s = null)
    {
        var res = await (s ?? Admin).Get($"/api/v1/campaigns/{id}/recipients?pageSize=100" + query);
        return (await Crm.Ok<PagedResult<RecipientDto>>(res)).Items.ToList();
    }

    public async Task<RecipientDto> RecipientOfAsync(Guid campaignId, TestContact c) => (await RecipientsAsync(campaignId)).Single(r => r.ContactId == c.ContactId);

    public IEnumerable<FakeWahaServer.Sent> SentTo(TestContact c) => App.Waha.SentTo(c.Chat).Where(m => m.Session == Account.Session);
    public int TotalSent(IEnumerable<TestContact> cs) => cs.Sum(c => SentTo(c).Count());

    /// <summary>Delivery / read ack from the (fake) gateway through the real webhook → outbox → consumer path.</summary>
    public async Task AckAsync(TestContact c, int ack)
    {
        var sent = SentTo(c).Last();
        var res = await MessagingKit.PostWebhookAsync(App, Account, WahaEvents.Ack(Account.Session, sent.Id, c.Chat, ack), sentAt: DateTime.UtcNow + App.Clock.Offset);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await Worker.PumpAsync();
    }

    public async Task InboundAsync(TestContact c, string text)
    {
        var now = DateTime.UtcNow + App.Clock.Offset;   // the test clock may be ahead: the gateway's timestamps follow it
        var res = await MessagingKit.PostWebhookAsync(App, Account, WahaEvents.Message(Account.Session, c.Chat, text, ts: new DateTimeOffset(now).ToUnixTimeSeconds() + 1), sentAt: now);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await Worker.PumpAsync();
    }

    public async Task SetSettingsAsync(Action<Dictionary<string, object?>>? change = null, ApiSession? s = null)
    {
        var cur = await Crm.Ok<CampaignSettingsDto>(await (s ?? Admin).Get("/api/v1/campaigns/settings"));
        var body = new Dictionary<string, object?>
        {
            ["cooldownDays"] = cur.CooldownDays, ["approvalRequired"] = cur.ApprovalRequired, ["creatorCannotApprove"] = cur.CreatorCannotApprove, ["attributionWindowDays"] = cur.AttributionWindowDays,
            ["failureThresholdPercent"] = cur.FailureThresholdPercent, ["failureMinSample"] = cur.FailureMinSample, ["optOutFooter"] = cur.OptOutFooter, ["monthlyMessageLimit"] = cur.MonthlyMessageLimit, ["testNumbers"] = cur.TestNumbers,
        };
        change?.Invoke(body);
        await Crm.Ok<CampaignSettingsDto>(await (s ?? Admin).Put("/api/v1/campaigns/settings", body));
    }

    public async Task<CampaignStatsDto> StatsAsync(Guid id) => await Crm.Ok<CampaignStatsDto>(await Admin.Get($"/api/v1/campaigns/{id}/stats"));

    public async ValueTask DisposeAsync()
    {
        App.Clock.Offset = _clockAtOpen;
        await Worker.DisposeAsync();
    }
}
