using System.Net;
using System.Text.RegularExpressions;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Billing;
using LeadPilot.Application.Common;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Billing;
using LeadPilot.Domain.Discovery;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Billing;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

[Collection("app")]
public class BillingEntitlementTests(AppFixture app)
{
    private static readonly string LeadsOnly = "Leads";

    private static Task<HttpResponseMessage> NewLead(ApiSession s) => s.Post("/api/v1/leads", new { name = Crm.Name(Crm.Token()), city = "Riyadh" });

    private async Task<(BillingWorld W, PlatformPlanDto Plan)> WithPlanAsync(Dictionary<string, long?> limits, string[]? features = null)
    {
        var w = await BillingWorld.OpenAsync(app);
        var plan = await w.PlanAsync(limits: limits, features: features);
        await w.GrantAsync(plan);
        return (w, plan);
    }

    [RequiresMySqlFact]
    public async Task Sample_plans_are_seeded_labelled_with_zero_prices_and_new_tenants_start_on_the_default_plan()
    {
        await using var w = await BillingWorld.OpenAsync(app);
        var plans = await ApiSession.Json<List<PlatformPlanDto>>(await w.Owner.Get("/api/v1/platform/plans"));
        var samples = plans.Where(p => p.IsSample).ToList();
        Assert.True(samples.Count >= 4);
        Assert.All(samples, p => { Assert.Equal(0m, p.Price); Assert.Contains("SAMPLE", p.Name); });
        var settings = await ApiSession.Json<PlatformSettingsDto>(await w.Owner.Get("/api/v1/platform/settings"));
        Assert.False(settings.RegistrationEnabled);          // sign-up is closed until the owner opens it
        Assert.NotNull(settings.DefaultPlanId);
        var sub = await w.SubscriptionAsync();
        Assert.Equal(settings.DefaultPlanId, sub.PlanId);     // an existing / operator-created tenant has a plan and keeps full access
        Assert.All((await w.OverviewAsync()).Entitlements.Usage, u => Assert.Null(u.Limit));
    }

    [RequiresMySqlFact]
    public async Task Plans_are_configured_by_the_platform_owner_with_decimal_prices_and_are_validated()
    {
        await using var w = await BillingWorld.OpenAsync(app);
        var plan = await w.PlanAsync(price: 99.90m, currency: "eur", cycle: "Quarterly", limits: new() { [LeadsOnly] = 10, ["KnowledgeStorageBytes"] = null, ["Campaigns"] = 0 }, features: [Features.Ai]);
        Assert.Equal(99.90m, plan.Price); Assert.Equal("EUR", plan.Currency); Assert.Equal(PlanCycle.Quarterly, plan.Cycle);
        Assert.Equal(10, plan.Limits[UsageDimension.Leads]); Assert.Equal(0, plan.Limits[UsageDimension.Campaigns]); Assert.Equal([Features.Ai], plan.Features);
        var again = await ApiSession.Json<PlatformPlanDto>(await w.Owner.Get($"/api/v1/platform/plans/{plan.Id}"));
        Assert.Equal(99.90m, again.Price);

        object Body(string code, decimal price = 5, string currency = "USD", bool trial = false, int? days = null, string[]? feats = null, Dictionary<string, long?>? lim = null) =>
            new { code, name = "x", cycle = "Monthly", price, currency, isTrial = trial, trialDays = days, visibility = "Public", sortOrder = 1, limits = lim ?? new Dictionary<string, long?>(), features = feats ?? [] };
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.Post("/api/v1/platform/plans", Body(plan.Code))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.Post("/api/v1/platform/plans", Body(BillingWorld.Code(), price: -1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.Post("/api/v1/platform/plans", Body(BillingWorld.Code(), price: 1.234m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.Post("/api/v1/platform/plans", Body(BillingWorld.Code(), currency: "US"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.Post("/api/v1/platform/plans", Body(BillingWorld.Code(), trial: true, days: 14, price: 5))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.Post("/api/v1/platform/plans", Body(BillingWorld.Code(), feats: ["warp-drive"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.Post("/api/v1/platform/plans", Body(BillingWorld.Code(), lim: new() { [LeadsOnly] = -5 }))).StatusCode);

        var upd = await w.Owner.Put($"/api/v1/platform/plans/{plan.Id}", new { code = plan.Code, name = "Renamed", cycle = "Annual", price = 250.50m, currency = "EUR", isTrial = false, visibility = "Private", sortOrder = 2, limits = new Dictionary<string, long?> { [LeadsOnly] = 20 }, features = new[] { Features.Ai, Features.Campaigns } });
        var u = await ApiSession.Json<PlatformPlanDto>(upd);
        Assert.Equal((250.50m, PlanCycle.Annual, PlanVisibility.Private), (u.Price, u.Cycle, u.Visibility)); Assert.Equal(20, u.Limits[UsageDimension.Leads]);

        var settings = await ApiSession.Json<PlatformSettingsDto>(await w.Owner.Get("/api/v1/platform/settings"));
        Assert.Equal(HttpStatusCode.Conflict, (await w.Owner.Post($"/api/v1/platform/plans/{settings.DefaultPlanId}/archive")).StatusCode);   // the default plan cannot be archived from under the platform
        var arch = await ApiSession.Json<PlatformPlanDto>(await w.Owner.Post($"/api/v1/platform/plans/{plan.Id}/archive"));
        Assert.Equal(PlanStatus.Archived, arch.Status);
    }

    [RequiresMySqlFact]
    public async Task Workspace_roles_and_anonymous_callers_can_never_reach_platform_endpoints()
    {
        await using var w = await BillingWorld.OpenAsync(app);
        var ro = await ApiSession.LoginAsync(app, w.ReadOnlyUser.Email, w.ReadOnlyUser.Password);
        var anon = ApiSession.Anonymous(app);
        var gets = new[] { "plans", "tenants", $"tenants/{w.TenantId}", "payments/queue", "invoices", "settings", "audit" }.Select(x => "/api/v1/platform/" + x).ToList();
        foreach (var url in gets)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Get(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await w.Admin.Get(url)).StatusCode);      // even a Tenant Administrator
            Assert.Equal(HttpStatusCode.Forbidden, (await ro.Get(url)).StatusCode);
            Assert.True((await w.Owner.Get(url)).IsSuccessStatusCode, url);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Admin.Post($"/api/v1/platform/tenants/{w.TenantId}/suspend", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Admin.Post($"/api/v1/platform/tenants/{w.TenantId}/grant", new { planId = Guid.NewGuid(), reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Admin.Put("/api/v1/platform/settings", new { })).StatusCode);
        // billing: read-only has neither Billing.View nor Manage; the Tenant Administrator has both; anonymous is 401
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Get("/api/v1/billing")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Get("/api/v1/billing")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Admin.Get("/api/v1/billing")).StatusCode);
        // the platform permissions can only live in platform-tenant roles: the catalog marks them
        foreach (var code in new[] { PermissionCodes.PlatformPlansManage, PermissionCodes.PlatformTenantsManage, PermissionCodes.PlatformBillingVerify, PermissionCodes.PlatformSettingsManage, PermissionCodes.PlatformAuditView })
            Assert.True(PermissionCatalog.IsPlatform(code), code);
        Assert.DoesNotContain(SystemRoles.All.First(r => r.Name == SystemRoles.TenantAdministrator).Permissions, c => PermissionCatalog.IsPlatform(c));
    }

    [RequiresMySqlFact]
    public async Task A_tenant_cannot_exceed_its_lead_limit_without_an_override_and_overrides_expire()
    {
        var (w, _) = await WithPlanAsync(new() { [LeadsOnly] = 3 });
        await using var _w = w;
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Created, (await NewLead(w.Admin)).StatusCode);
        var blocked = await BillingAssert.ErrorAsync(await NewLead(w.Admin), HttpStatusCode.PaymentRequired, "plan_limit_exceeded");
        Assert.Contains("3", blocked.Detail); Assert.Contains("Upgrade", blocked.Detail);
        var usage = await w.UsageAsync(UsageDimension.Leads);
        Assert.Equal((3L, 3L), (usage.Used, usage.Limit));
        var notices = (await w.OverviewAsync()).Entitlements.Notices.Where(n => n.Dimension == UsageDimension.Leads).Select(n => n.Threshold).Order().ToList();
        Assert.Equal([80, 100], notices);                                    // soft thresholds were recorded once each

        // an authorised override lifts the limit, with a reason, and is audited
        var set = await w.Owner.Put($"/api/v1/platform/tenants/{w.TenantId}/overrides/limits", new { dimension = "Leads", value = 5, unlimited = false, expiresAtUtc = w.Now.AddDays(1), reason = "pilot extension" });
        Assert.True(set.IsSuccessStatusCode, await set.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await NewLead(w.Admin)).StatusCode);
        var line = await w.UsageAsync(UsageDimension.Leads);
        Assert.Equal((5L, LimitSource.Override, "pilot extension"), (line.Limit, line.Source, line.OverrideReason));
        // an override needs a reason and a future expiry
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.Put($"/api/v1/platform/tenants/{w.TenantId}/overrides/limits", new { dimension = "Leads", value = 9, reason = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.Put($"/api/v1/platform/tenants/{w.TenantId}/overrides/limits", new { dimension = "Leads", value = 9, expiresAtUtc = w.Now.AddDays(-1), reason = "past" })).StatusCode);
        var audit = await ApiSession.Json<PagedResult<PlatformAuditRow>>(await w.Owner.Get("/api/v1/platform/audit?action=Platform.Override"));
        Assert.Contains(audit.Items, a => a.TenantId == w.TenantId && a.Summary.Contains("pilot extension"));

        // after it expires the plan applies again: the tenant is over (5 > 3) and creates nothing more
        app.Clock.Offset = TimeSpan.FromDays(2);
        await BillingAssert.ErrorAsync(await NewLead(w.Admin), HttpStatusCode.PaymentRequired, "plan_limit_exceeded");
        Assert.Equal(LimitSource.Plan, (await w.UsageAsync(UsageDimension.Leads)).Source);
        app.Clock.Offset = TimeSpan.Zero;
        // removing / unlimited override
        Assert.True((await w.Owner.Put($"/api/v1/platform/tenants/{w.TenantId}/overrides/limits", new { dimension = "Leads", unlimited = true, reason = "enterprise deal" })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Created, (await NewLead(w.Admin)).StatusCode);
        Assert.True((await w.Owner.Delete($"/api/v1/platform/tenants/{w.TenantId}/overrides/Leads")).IsSuccessStatusCode);
        await BillingAssert.ErrorAsync(await NewLead(w.Admin), HttpStatusCode.PaymentRequired, "plan_limit_exceeded");
    }

    [RequiresMySqlFact]
    public async Task Concurrent_requests_racing_for_the_last_lead_let_exactly_one_through()
    {
        var (w, _) = await WithPlanAsync(new() { [LeadsOnly] = 4 });
        await using var _w = w;
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Created, (await NewLead(w.Admin)).StatusCode);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => NewLead(w.Admin)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(9, results.Count(r => r.StatusCode == HttpStatusCode.PaymentRequired));
        Assert.Equal(4, (await w.UsageAsync(UsageDimension.Leads)).Used);
    }

    [RequiresMySqlFact]
    public async Task The_user_limit_counts_members_and_pending_invitations_and_the_last_seat_cannot_be_raced()
    {
        var (w, _) = await WithPlanAsync(new() { ["Users"] = 4 });   // admin + read-only = 2 members
        await using var _w = w;
        var roleId = await app.World.RoleIdAsync(w.TenantId, SystemRoles.ReadOnly);
        Task<HttpResponseMessage> Invite() => w.Admin.Post("/api/v1/invitations", new { email = $"inv-{Guid.NewGuid():N}@seat.test", roleId });
        Assert.Equal(HttpStatusCode.Created, (await Invite()).StatusCode);                  // 3
        var race = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Invite()));       // one seat left
        Assert.Equal(1, race.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(race.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.PaymentRequired, r.StatusCode));
        Assert.Equal(4, (await w.UsageAsync(UsageDimension.Users)).Used);
    }

    [RequiresMySqlFact]
    public async Task Knowledge_storage_is_limited_in_bytes_and_a_refused_document_leaves_nothing_behind()
    {
        var (w, _) = await WithPlanAsync(new() { ["KnowledgeStorageBytes"] = 1000 });
        await using var _w = w;
        object Doc(int n) => new { title = "Doc " + Guid.NewGuid().ToString("N")[..6], kind = "Faq", content = new string('a', n) };
        Assert.Equal(HttpStatusCode.Created, (await w.Admin.Post("/api/v1/knowledge/documents/text", Doc(600))).StatusCode);
        await BillingAssert.ErrorAsync(await w.Admin.Post("/api/v1/knowledge/documents/text", Doc(600)), HttpStatusCode.PaymentRequired, "plan_limit_exceeded");
        Assert.Equal(600, (await w.UsageAsync(UsageDimension.KnowledgeStorageBytes)).Used);
        Assert.Equal(HttpStatusCode.Created, (await w.Admin.Post("/api/v1/knowledge/documents/text", Doc(400))).StatusCode);   // exactly at the limit is allowed
        await BillingAssert.ErrorAsync(await w.Admin.Post("/api/v1/knowledge/documents/text", Doc(1)), HttpStatusCode.PaymentRequired, "plan_limit_exceeded");
    }

    [RequiresMySqlFact]
    public async Task Connected_messaging_accounts_and_monthly_messages_are_limited()
    {
        await using var w = await BillingWorld.OpenAsync(app);
        var plan = await w.PlanAsync(limits: new() { ["MessagingAccounts"] = 1, ["MonthlyMessages"] = 2 });
        await w.GrantAsync(plan);
        var acc = await MessagingKit.CreateAccountAsync(app, w.Admin, w.TenantId);
        var second = await w.Admin.Post("/api/v1/channels/accounts", new { displayName = "Second", baseUrl = app.Waha.BaseUrl, apiKey = FakeWahaServer.ApiKey, sessionName = MessagingKit.NewSession(), webhookSecret = "secret-" + Guid.NewGuid().ToString("N") });
        await BillingAssert.ErrorAsync(second, HttpStatusCode.PaymentRequired, "plan_limit_exceeded");
        Assert.Equal(1, (await w.UsageAsync(UsageDimension.MessagingAccounts)).Used);

        await MessagingKit.PrepareAccountAsync(app, acc.Id);
        var worker = MessagingKit.BuildWorker(app);
        var conv = await MessagingKit.InboundAsync(app, worker, acc, w.Admin, Crm.Phone());
        Task<HttpResponseMessage> Reply() => w.Admin.Post($"/api/v1/conversations/{conv.Id}/messages", new { text = "hello " + Guid.NewGuid().ToString("N")[..6] });
        Assert.Equal(HttpStatusCode.Accepted, (await Reply()).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Reply()).StatusCode);
        await BillingAssert.ErrorAsync(await Reply(), HttpStatusCode.PaymentRequired, "plan_limit_exceeded");
        Assert.Equal(2, (await w.UsageAsync(UsageDimension.MonthlyMessages)).Used);
        // the next period starts with a clean count: usage is windowed by the subscription period
        await w.GrantAsync(plan, until: w.Now.AddDays(40));
        Assert.Equal(HttpStatusCode.Accepted, (await Reply()).StatusCode);   // a new grant starts a new period window
    }

    [RequiresMySqlFact]
    public async Task Active_campaigns_are_limited_and_a_plan_without_the_feature_refuses_with_a_typed_error()
    {
        await using var cw = await CampaignWorld.OpenAsync(app, freshTenant: true);
        await using var w = new BillingWorldAdapter(app, cw.TenantId, cw.Admin);
        var plan = await w.PlanAsync(limits: new() { ["Campaigns"] = 1 });
        await w.GrantAsync(plan);
        var contacts = await cw.ContactsAsync(2);
        await cw.CreateAsync(contacts);
        object Body() => new { name = "C " + Guid.NewGuid().ToString("N")[..6], type = "Manual", templateId = cw.TemplateId, channelAccountId = cw.Account.Id, audience = new { contactIds = contacts.Select(c => c.ContactId).ToArray(), onePerLead = true }, batchSize = 10, minIntervalSeconds = 15, maxIntervalSeconds = 20, maxRecipients = 500, cooldownDays = 0 };
        await BillingAssert.ErrorAsync(await CampaignWorld.CreateRawAsync(cw.Admin, Body()), HttpStatusCode.PaymentRequired, "plan_limit_exceeded");

        var noFeature = await w.PlanAsync(features: [Features.Ai]);
        await w.GrantAsync(noFeature);
        await BillingAssert.ErrorAsync(await CampaignWorld.CreateRawAsync(cw.Admin, Body()), HttpStatusCode.PaymentRequired, "feature_not_in_plan");
        Assert.True((await w.Owner.Put($"/api/v1/platform/tenants/{cw.TenantId}/overrides/features", new { feature = Features.Campaigns, enabled = true, reason = "trial of campaigns" })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Created, (await CampaignWorld.CreateRawAsync(cw.Admin, Body())).StatusCode);   // the override beats the plan
    }

    /// <summary>The same helpers as <see cref="BillingWorld"/> for a tenant that another kit created.</summary>
    private sealed class BillingWorldAdapter : IAsyncDisposable
    {
        private readonly AppFixture _app; private readonly Guid _tenant; private readonly ApiSession _admin; private ApiSession? _owner;
        public ApiSession Owner => _owner ??= ApiSession.LoginAsync(_app, AppFixture.AdminEmail, AppFixture.AdminPassword).GetAwaiter().GetResult();
        public BillingWorldAdapter(AppFixture app, Guid tenant, ApiSession admin) { _app = app; _tenant = tenant; _admin = admin; }
        public async Task<PlatformPlanDto> PlanAsync(Dictionary<string, long?>? limits = null, string[]? features = null)
        {
            var res = await Owner.Post("/api/v1/platform/plans", new { code = BillingWorld.Code(), name = "Plan", cycle = "Monthly", price = 0m, currency = "USD", isTrial = false, visibility = "Public", sortOrder = 1, limits = limits ?? new(), features = features ?? Features.All.Select(f => f.Key).ToArray() });
            Assert.Equal(HttpStatusCode.Created, res.StatusCode); return await ApiSession.Json<PlatformPlanDto>(res);
        }
        public async Task GrantAsync(PlatformPlanDto p) => Assert.True((await Owner.Post($"/api/v1/platform/tenants/{_tenant}/grant", new { planId = p.Id, reason = "test" })).IsSuccessStatusCode);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [RequiresMySqlFact]
    public async Task A_dimension_the_plan_does_not_include_blocks_and_an_unlimited_one_never_does_for_every_dimension()
    {
        foreach (var d in Enum.GetValues<UsageDimension>())
        {
            await using var w = await BillingWorld.OpenAsync(app);
            await w.GrantAsync(await w.PlanAsync(limits: new() { [d.ToString()] = 0 }));
            var ran = false;
            var r = await RunAsync(w, d, () => ran = true);
            Assert.True(r.IsFailure, d.ToString()); Assert.Equal("plan_limit_exceeded", r.Error.Code); Assert.Contains("not included", r.Error.Message); Assert.False(ran, d.ToString());
            await w.GrantAsync(await w.PlanAsync(limits: new() { [d.ToString()] = null }));
            Assert.True((await RunAsync(w, d, () => ran = true)).IsSuccess, d.ToString()); Assert.True(ran);
        }
    }

    private async Task<LeadPilot.Shared.Results.Result<bool>> RunAsync(BillingWorld w, UsageDimension d, Action onRun)
    {
        LeadPilot.Shared.Results.Result<bool>? r = null;
        await app.World.Services.GetRequiredService<ITenantScopeRunner>().RunAsync(w.TenantId, async (sp, ct) =>
            r = await sp.GetRequiredService<IEntitlementService>().RunWithinLimitAsync(d, 1, _ => { onRun(); return Task.FromResult(LeadPilot.Shared.Results.Result.Success(true)); }, ct));
        return r!;
    }

    [RequiresMySqlFact]
    public async Task Ai_tokens_and_discovery_calls_are_metered_from_their_ledgers_and_holds_stop_two_callers_spending_the_last_unit()
    {
        await using var w = await BillingWorld.OpenAsync(app);
        await w.GrantAsync(await w.PlanAsync(limits: new() { ["MonthlyAiTokens"] = 2000, ["MonthlyDiscoveryCalls"] = 5 }));
        await w.WithDb(async db =>
        {
            db.AiUsageEntries.Add(AiUsageEntry.Create(w.TenantId, "chat", "claude", "m", 1000, 999, 0, 0, 0m, true, null, 5, null, w.Now));   // 1 999 of 2 000 tokens used
            db.DiscoveryUsageEntries.Add(DiscoveryUsageEntry.Create(w.TenantId, null, "google_places", "search", 4, 0m, 200, w.Now));          // 4 of 5 calls used
            await db.SaveChangesAsync(); return 0;
        });
        var ov = await w.OverviewAsync();
        Assert.Equal(1999, ov.Entitlements.Usage.Single(u => u.Dimension == UsageDimension.MonthlyAiTokens).Used);
        Assert.Equal(4, ov.Entitlements.Usage.Single(u => u.Dimension == UsageDimension.MonthlyDiscoveryCalls).Used);

        foreach (var (dim, est) in new[] { (UsageDimension.MonthlyAiTokens, 500L), (UsageDimension.MonthlyDiscoveryCalls, 1L) })
        {
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                string? code = null; IAsyncDisposable? hold = null;
                await app.World.Services.GetRequiredService<ITenantScopeRunner>().RunAsync(w.TenantId, async (sp, ct) =>
                {
                    var r = await sp.GetRequiredService<IEntitlementService>().HoldAsync(dim, est, ct);
                    if (r.IsFailure) code = r.Error.Code; else hold = r.Value;
                    await Task.Delay(300, ct);      // keep the hold while the others try: this is the in-flight call
                    if (hold is not null) await hold.DisposeAsync();
                });
                return code;
            }));
            Assert.Equal(1, outcomes.Count(c => c is null));                                       // exactly one caller got the last unit
            Assert.All(outcomes.Where(c => c is not null), c => Assert.Equal("plan_limit_exceeded", c));
        }
        // holds are released: after the ledger reaches the limit the next call is refused outright
        await w.WithDb(async db => { db.DiscoveryUsageEntries.Add(DiscoveryUsageEntry.Create(w.TenantId, null, "google_places", "search", 1, 0m, 200, w.Now)); await db.SaveChangesAsync(); return 0; });
        await app.World.Services.GetRequiredService<ITenantScopeRunner>().RunAsync(w.TenantId, async (sp, ct) =>
            Assert.True((await sp.GetRequiredService<IEntitlementService>().HoldAsync(UsageDimension.MonthlyDiscoveryCalls, 1, ct)).IsFailure));
    }
}
