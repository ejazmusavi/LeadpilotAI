using System.Net;
using System.Text.RegularExpressions;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Billing;
using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Billing;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

[Collection("app")]
public class BillingRegistrationTests(AppFixture app)
{
    private const string Pw = "Reg1ster!Passw0rd#";

    private async Task<PlatformSettingsDto> SettingsAsync(ApiSession owner) => await ApiSession.Json<PlatformSettingsDto>(await owner.Get("/api/v1/platform/settings"));

    /// <summary>Opens sign-up with the seeded trial plan and clears the attempt log (all test clients share one IP bucket), restoring the original settings afterwards.</summary>
    private async Task<(ApiSession Owner, PlatformSettingsDto Original, Func<PlatformSettingsDto, PlatformSettingsDto> Open)> OpenAsync(Func<PlatformSettingsDto, PlatformSettingsDto>? tweak = null)
    {
        var owner = await ApiSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        var original = await SettingsAsync(owner);
        await app.World.WithDb(db => db.RegistrationAttempts.ExecuteDeleteAsync());
        var opened = original with { RegistrationEnabled = true, MaxRegistrationsPerIpPerHour = 1000, MaxRegistrationsPerDomainPerDay = 1000 };
        if (tweak is not null) opened = tweak(opened);
        var r = await owner.Put("/api/v1/platform/settings", opened);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (owner, original, tweak ?? (s => s));
    }

    private static object Reg(string email, string? name = null, string? website = null, string password = Pw) =>
        new { workspaceName = name ?? "Reg Co " + Guid.NewGuid().ToString("N")[..8], displayName = "Reg Person", email, password, timezone = "UTC", website };

    private static string Mail(string domain = "signup.test") => $"reg-{Guid.NewGuid():N}@{domain}";

    private Task<int> TenantCountAsync(string name) => app.World.WithDb(db => db.Tenants.CountAsync(t => t.Name == name));

    [RequiresMySqlFact]
    public async Task Sign_up_is_closed_until_the_platform_owner_opens_it_and_creates_nothing_while_closed()
    {
        var owner = await ApiSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        var original = await SettingsAsync(owner);
        await owner.Put("/api/v1/platform/settings", original with { RegistrationEnabled = false });
        try
        {
            var anon = ApiSession.Anonymous(app);
            Assert.False((await ApiSession.Json<System.Text.Json.JsonElement>(await anon.Get("/api/v1/registration/status"))).GetProperty("open").GetBoolean());
            var name = "Closed Co " + Guid.NewGuid().ToString("N")[..6];
            await BillingAssert.ErrorAsync(await anon.Post("/api/v1/registration", Reg(Mail(), name)), HttpStatusCode.Forbidden, "registration_closed");
            Assert.Equal(0, await TenantCountAsync(name));
            var web = WebSession.Anonymous(app);
            Assert.Contains("currently closed", await web.Html("/Register"));
        }
        finally { await owner.Put("/api/v1/platform/settings", original); }
    }

    [RequiresMySqlFact]
    public async Task A_new_workspace_is_locked_for_sensitive_features_until_the_owner_verifies_the_email()
    {
        var (owner, original, _) = await OpenAsync();
        try
        {
            var anon = ApiSession.Anonymous(app); var email = Mail(); var name = "Fresh Co " + Guid.NewGuid().ToString("N")[..6];
            var res = await anon.Post("/api/v1/registration", Reg(email, name));
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
            var created = await ApiSession.Json<RegisterResult>(res);
            Assert.True(created.VerificationEmailSent); Assert.Equal(1, await TenantCountAsync(name));

            var admin = await ApiSession.LoginAsync(app, email, Pw);                                     // can sign in before verifying
            var ov = await ApiSession.Json<BillingOverview>(await admin.Get("/api/v1/billing"));
            Assert.True(ov.Entitlements.EmailVerificationPending);
            Assert.Equal(SubscriptionState.Trialing, ov.Entitlements.Subscription!.State);               // on the sign-up plan: a trial
            Assert.Contains("verify", string.Join(" ", (await ApiSession.Json<OnboardingOverview>(await admin.Get("/api/v1/onboarding"))).Steps.Where(s => !s.Done).Select(s => s.Key)));
            // sensitive features answer 403 email_not_verified; ordinary CRM work is open
            await BillingAssert.ErrorAsync(await admin.Post("/api/v1/campaigns", new { name = "x", type = "Manual" }), HttpStatusCode.Forbidden, "email_not_verified");
            Assert.Equal(HttpStatusCode.Created, (await admin.Post("/api/v1/leads", new { name = Crm.Name(Crm.Token()) })).StatusCode);

            var body = app.Outbox.Sent.Reverse().First(m => m.To.Equals(email, StringComparison.OrdinalIgnoreCase)).HtmlBody;
            var userId = Guid.Parse(Regex.Match(body, "userId=([0-9a-fA-F-]{36})").Groups[1].Value); var token = app.Outbox.TokenFor(email);
            Assert.Equal(HttpStatusCode.BadRequest, (await anon.Post("/api/v1/registration/verify-email", new { userId, token = "not-the-token" })).StatusCode);
            Assert.True((await anon.Post("/api/v1/registration/verify-email", new { userId, token })).IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await anon.Post("/api/v1/registration/verify-email", new { userId, token })).StatusCode);   // single use
            Assert.False((await ApiSession.Json<BillingOverview>(await admin.Get("/api/v1/billing"))).Entitlements.EmailVerificationPending);
            Assert.NotEqual(HttpStatusCode.Forbidden, (await admin.Post("/api/v1/campaigns", new { name = "x", type = "Manual" })).StatusCode);   // unlocked (now just a validation error)
            // the admin of the new workspace is a Tenant Administrator of that workspace only
            Assert.Equal(HttpStatusCode.Forbidden, (await admin.Get("/api/v1/platform/tenants")).StatusCode);
        }
        finally { await owner.Put("/api/v1/platform/settings", original); }
    }

    [RequiresMySqlFact]
    public async Task Sign_up_resists_disposable_domains_bots_duplicates_rate_abuse_and_a_missing_captcha()
    {
        var (owner, original, _) = await OpenAsync();
        try
        {
            var anon = ApiSession.Anonymous(app);
            await BillingAssert.ErrorAsync(await anon.Post("/api/v1/registration", Reg(Mail("mailinator.com"))), HttpStatusCode.BadRequest, "email_domain_blocked");
            await BillingAssert.ErrorAsync(await anon.Post("/api/v1/registration", Reg(Mail("sub.yopmail.com"))), HttpStatusCode.BadRequest, "email_domain_blocked");
            Assert.Equal(HttpStatusCode.BadRequest, (await anon.Post("/api/v1/registration", Reg("not-an-email"))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await anon.Post("/api/v1/registration", Reg(Mail(), password: "weak"))).StatusCode);
            // the owner's own block list
            await owner.Put("/api/v1/platform/settings", (await SettingsAsync(owner)) with { ExtraBlockedDomains = "badco.example, *.worse.example" });
            await BillingAssert.ErrorAsync(await anon.Post("/api/v1/registration", Reg(Mail("badco.example"))), HttpStatusCode.BadRequest, "email_domain_blocked");
            await BillingAssert.ErrorAsync(await anon.Post("/api/v1/registration", Reg(Mail("x.worse.example"))), HttpStatusCode.BadRequest, "email_domain_blocked");

            // honeypot: looks like success, creates nothing
            var botName = "Bot Co " + Guid.NewGuid().ToString("N")[..6];
            Assert.True((await anon.Post("/api/v1/registration", Reg(Mail(), botName, website: "http://spam.example"))).IsSuccessStatusCode);
            Assert.Equal(0, await TenantCountAsync(botName));

            // an address that already exists gets the same answer and no second workspace (no account enumeration)
            var email = Mail(); var n1 = "Dup One " + Guid.NewGuid().ToString("N")[..6]; var n2 = "Dup Two " + Guid.NewGuid().ToString("N")[..6];
            Assert.True((await anon.Post("/api/v1/registration", Reg(email, n1))).IsSuccessStatusCode);
            var second = await anon.Post("/api/v1/registration", Reg(email, n2));
            Assert.True(second.IsSuccessStatusCode); Assert.Equal(1, await TenantCountAsync(n1)); Assert.Equal(0, await TenantCountAsync(n2));

            // resend: the same neutral answer for known, unknown and verified addresses; mail only for the pending one
            var before = app.Outbox.Sent.Count;
            Assert.True((await anon.Post("/api/v1/registration/resend", new { email = Mail() })).IsSuccessStatusCode);
            Assert.Equal(before, app.Outbox.Sent.Count);
            Assert.True((await anon.Post("/api/v1/registration/resend", new { email })).IsSuccessStatusCode);
            Assert.Equal(before + 1, app.Outbox.Sent.Count);

            // a CAPTCHA the platform requires but nobody installed fails closed instead of being skipped
            await owner.Put("/api/v1/platform/settings", (await SettingsAsync(owner)) with { CaptchaRequired = true });
            await BillingAssert.ErrorAsync(await anon.Post("/api/v1/registration", Reg(Mail())), HttpStatusCode.Conflict, "registration_unavailable");
            await owner.Put("/api/v1/platform/settings", (await SettingsAsync(owner)) with { CaptchaRequired = false });
        }
        finally { await owner.Put("/api/v1/platform/settings", original); await app.World.WithDb(db => db.RegistrationAttempts.ExecuteDeleteAsync()); }
    }

    [RequiresMySqlFact]
    public async Task Sign_up_is_rate_limited_per_address_and_per_email_domain()
    {
        var (owner, original, _) = await OpenAsync(s => s with { MaxRegistrationsPerIpPerHour = 3 });
        try
        {
            var anon = ApiSession.Anonymous(app);
            for (var i = 0; i < 3; i++) Assert.True((await anon.Post("/api/v1/registration", Reg(Mail()))).IsSuccessStatusCode);
            await BillingAssert.ErrorAsync(await anon.Post("/api/v1/registration", Reg(Mail())), HttpStatusCode.TooManyRequests, "rate_limited");
            // once the window has passed the address may try again
            app.Clock.Offset = TimeSpan.FromHours(2);
            Assert.True((await anon.Post("/api/v1/registration", Reg(Mail()))).IsSuccessStatusCode);
            app.Clock.Offset = TimeSpan.Zero;

            await app.World.WithDb(db => db.RegistrationAttempts.ExecuteDeleteAsync());
            await owner.Put("/api/v1/platform/settings", (await SettingsAsync(owner)) with { MaxRegistrationsPerIpPerHour = 1000, MaxRegistrationsPerDomainPerDay = 2 });
            Assert.True((await anon.Post("/api/v1/registration", Reg(Mail("bulk.test")))).IsSuccessStatusCode);
            Assert.True((await anon.Post("/api/v1/registration", Reg(Mail("bulk.test")))).IsSuccessStatusCode);
            await BillingAssert.ErrorAsync(await anon.Post("/api/v1/registration", Reg(Mail("bulk.test"))), HttpStatusCode.TooManyRequests, "rate_limited");
            Assert.True((await anon.Post("/api/v1/registration", Reg(Mail("other.test")))).IsSuccessStatusCode);
        }
        finally { app.Clock.Offset = TimeSpan.Zero; await owner.Put("/api/v1/platform/settings", original); await app.World.WithDb(db => db.RegistrationAttempts.ExecuteDeleteAsync()); }
    }

    [RequiresMySqlFact]
    public async Task The_sign_up_pages_work_in_the_browser_flow()
    {
        var (owner, original, _) = await OpenAsync();
        try
        {
            var web = WebSession.Anonymous(app);
            var html = await web.Html("/Register");
            Assert.Contains("Create workspace", html); Assert.Contains("Website", html);   // the honeypot field is present (hidden by CSS)
            var email = Mail(); var name = "Web Co " + Guid.NewGuid().ToString("N")[..6];
            var token = await AppFixture.GetAntiForgeryTokenAsync(web.Http, "/Register");
            var post = await web.Http.PostAsync("/Register", new FormUrlEncodedContent(new Dictionary<string, string> { ["WorkspaceName"] = name, ["DisplayName"] = "Web Person", ["Email"] = email, ["Password"] = Pw, ["Timezone"] = "UTC", ["__RequestVerificationToken"] = token }));
            Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
            Assert.Equal(1, await TenantCountAsync(name));
            var userId = Regex.Match(app.Outbox.Sent.Reverse().First(m => m.To == email).HtmlBody, "userId=([0-9a-fA-F-]{36})").Groups[1].Value;
            var verify = await web.Html($"/Register/Verify?userId={userId}&token={Uri.EscapeDataString(app.Outbox.TokenFor(email))}");
            Assert.Contains("verified", verify);
            Assert.Contains("invalid or has expired", await web.Html($"/Register/Verify?userId={userId}&token=nope"));
        }
        finally { await owner.Put("/api/v1/platform/settings", original); await app.World.WithDb(db => db.RegistrationAttempts.ExecuteDeleteAsync()); }
    }
}

[Collection("app")]
public class BillingIsolationTests(AppFixture app)
{
    [RequiresMySqlFact]
    public async Task One_tenant_can_never_see_or_change_another_tenants_subscription_invoices_payments_or_usage()
    {
        await using var a = await BillingWorld.OpenAsync(app); await using var b = await BillingWorld.OpenAsync(app);
        foreach (var w in new[] { a, b }) { await w.GrantAsync(await w.PlanAsync(price: 40m, limits: new() { ["Leads"] = 3 }), until: w.Now.AddDays(2)); await w.LifecycleAsync(); }
        var invA = (await a.InvoicesAsync()).Single(); var invB = (await b.InvoicesAsync()).Single();
        await b.VerifyAsync(invB);
        var changeB = await ApiSession.Json<PlanChangeDto>(await b.Admin.Post("/api/v1/billing/plan-changes", new { planId = (await b.PlanAsync(price: 90m)).Id }));
        for (var i = 0; i < 3; i++) await Crm.CreateLead(b.Admin);

        // reads
        Assert.Equal(HttpStatusCode.NotFound, (await a.Admin.Get($"/api/v1/billing/invoices/{invB.Id}")).StatusCode);
        Assert.DoesNotContain(await a.InvoicesAsync(), i => i.Id == invB.Id);
        Assert.NotEqual(invA.Number, invB.Number);
        Assert.Empty(await ApiSession.Json<List<BillingPaymentDto>>(await a.Admin.Get("/api/v1/billing/payments")));
        Assert.DoesNotContain(await ApiSession.Json<List<PlanChangeDto>>(await a.Admin.Get("/api/v1/billing/plan-changes")), c => c.Id == changeB.Id);
        // usage and overrides are per tenant: B's three leads and B's override do not touch A
        Assert.Equal(0, (await a.UsageAsync(UsageDimension.Leads)).Used);
        Assert.True((await a.Owner.Put($"/api/v1/platform/tenants/{b.TenantId}/overrides/limits", new { dimension = "Leads", value = 50, reason = "B only" })).IsSuccessStatusCode);
        Assert.Equal(3, (await a.UsageAsync(UsageDimension.Leads)).Limit); Assert.Equal(50, (await b.UsageAsync(UsageDimension.Leads)).Limit);
        // writes by id answer 404 and change nothing
        Assert.Equal(HttpStatusCode.NotFound, (await a.Admin.Post($"/api/v1/billing/invoices/{invB.Id}/payment-report", new { evidenceReference = "STEAL-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Admin.Delete($"/api/v1/billing/plan-changes/{changeB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Owner.Post($"/api/v1/platform/tenants/{a.TenantId}/invoices/{invB.Id}/void", new { reason = "wrong route" })).StatusCode);
        Assert.Equal(PlanChangeStatus.AwaitingPayment, (await ApiSession.Json<List<PlanChangeDto>>(await b.Admin.Get("/api/v1/billing/plan-changes"))).Single(c => c.Id == changeB.Id).Status);
        // a spoofed tenant id in a body is rejected, never obeyed
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.Post($"/api/v1/billing/invoices/{invA.Id}/payment-report", new { evidenceReference = "SPOOF-1", tenantId = b.TenantId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Admin.Post("/api/v1/billing/plan-changes", new { planId = Guid.NewGuid(), tenantId = b.TenantId })).StatusCode);
        // the platform views of a tenant only show that tenant's rows
        var detailA = await ApiSession.Json<PlatformTenantDetail>(await a.Owner.Get($"/api/v1/platform/tenants/{a.TenantId}"));
        Assert.DoesNotContain(detailA.Invoices, i => i.Id == invB.Id); Assert.Empty(detailA.Overrides);
        // the pages: B's invoice is a 404 for A, a read-only user has no billing page, and no workspace role reaches a platform page
        var webA = await WebSession.LoginAsync(app, a.AdminUser.Email, a.AdminUser.Password);
        Assert.Equal(HttpStatusCode.NotFound, (await webA.Get($"/Billing/Invoice/{invB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await webA.Get($"/Billing/Print/{invB.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await webA.Get($"/Billing/Invoice/{invA.Id}")).StatusCode);
        var webRo = await WebSession.LoginAsync(app, a.ReadOnlyUser.Email, a.ReadOnlyUser.Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await webRo.Get("/Billing")).StatusCode);
        foreach (var page in new[] { "/PlatformTenants", $"/PlatformTenants/Details/{b.TenantId}", "/PlatformPlans", "/PlatformPayments", "/PlatformPayments/Invoices", "/PlatformSettings", "/PlatformAudit" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await webA.Get(page)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await webRo.Get(page)).StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await WebSession.Anonymous(app).Get(page)).StatusCode);
        }
    }

    [RequiresMySqlFact]
    public async Task The_billing_and_platform_pages_render_for_the_right_roles()
    {
        await using var w = await BillingWorld.OpenAsync(app);
        await w.GrantAsync(await w.PlanAsync(price: 25m, limits: new() { ["Leads"] = 10 }), until: w.Now.AddDays(2));
        await w.LifecycleAsync();
        var inv = (await w.InvoicesAsync()).Single();
        var web = await WebSession.LoginAsync(app, w.AdminUser.Email, w.AdminUser.Password);
        var index = await web.Html("/Billing");
        Assert.Contains("data-usage=\"Leads\"", index); Assert.Contains("Open invoices", index); Assert.Contains(inv.Number!, index);
        foreach (var url in new[] { "/Billing/Invoices", $"/Billing/Invoice/{inv.Id}", "/Billing/Payments", "/Billing/Plans", "/Onboarding" }) Assert.Equal(HttpStatusCode.OK, (await web.Get(url)).StatusCode);
        var owner = await WebSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        foreach (var url in new[] { "/PlatformTenants", $"/PlatformTenants/Details/{w.TenantId}", "/PlatformPlans", "/PlatformPlans/Create", "/PlatformPayments", "/PlatformPayments/Invoices", $"/PlatformPayments/Invoice?forTenant={w.TenantId}&id={inv.Id}", "/PlatformSettings", "/PlatformAudit" })
            Assert.Equal(HttpStatusCode.OK, (await owner.Get(url)).StatusCode);
        Assert.Contains(w.TenantId.ToString(), await owner.Html("/PlatformTenants?q=Tenant+BL"));
        // verifying from the page works and the tenant sees the paid invoice
        var res = await owner.PostWithToken($"/PlatformPayments/Invoice?forTenant={w.TenantId}&id={inv.Id}", $"/PlatformPayments/Verify/{inv.Id}",
            ("ForTenant", w.TenantId.ToString()), ("Amount", inv.Total.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("Currency", inv.Currency), ("Method", "BankTransfer"), ("EvidenceReference", "WEB-PAY-" + Guid.NewGuid().ToString("N")[..6]));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(InvoiceStatus.Paid, (await w.InvoicesAsync()).Single().Status);
    }

    [RequiresMySqlFact]
    public async Task The_onboarding_checklist_completes_from_real_data_only()
    {
        await using var w = await BillingWorld.OpenAsync(app);
        async Task<Dictionary<string, bool>> Steps() => (await ApiSession.Json<OnboardingOverview>(await w.Admin.Get("/api/v1/onboarding"))).Steps.ToDictionary(s => s.Key, s => s.Done);
        var s0 = await Steps();
        Assert.False(s0["leads"]); Assert.False(s0["knowledge"]); Assert.False(s0["whatsapp"]); Assert.False(s0["campaign"]);
        Assert.True(s0["team"]);                                                         // the fixture workspace already has two members
        await Crm.CreateLead(w.Admin);
        Assert.True((await w.Admin.Post("/api/v1/knowledge/documents/text", new { title = "Onboarding doc", kind = "Faq", content = "We sell things." })).IsSuccessStatusCode);
        var s1 = await Steps();
        Assert.True(s1["leads"]); Assert.True(s1["knowledge"]); Assert.False(s1["catalog"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await ApiSession.LoginAsync(app, w.ReadOnlyUser.Email, w.ReadOnlyUser.Password)).Get("/api/v1/onboarding")).StatusCode);
    }
}
