using System.Net;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Common;
using LeadPilot.Application.Conversion;
using LeadPilot.Domain.Conversion;
using LeadPilot.Infrastructure.Conversion;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

[Collection("app")]
public class ConversionSecurityTests(AppFixture app)
{
    private sealed record Seed(Guid LeadId, DemoDto Demo, TrialDto Trial, CustomerDto Customer, PaymentRequestDto Request, Guid RepId);

    private static async Task<Seed> SeedAsync(ConvWorld w)
    {
        var lead = await w.LeadAsync(); var slot = await w.SlotAsync();
        var demo = await w.BookAsync(lead, slot.SlotId);
        var trial = await Crm.Ok<TrialDto>(await w.Admin.Post("/api/v1/trials", new { leadId = await w.LeadAsync(), planId = w.W.PlanId }));
        var (c, r) = await w.NewRequestAsync();
        var rep = (await Crm.Ok<List<DemoRepDto>>(await w.Admin.Get("/api/v1/demo-settings/reps"))).Single();
        return new(lead, demo, trial, c, r, rep.Id);
    }

    [RequiresMySqlFact]
    public async Task Another_workspace_cannot_see_or_change_any_demo_trial_customer_or_payment_through_the_api()
    {
        await using var a = await ConvWorld.OpenAsync(app); await using var b = await ConvWorld.OpenAsync(app);
        var s = await SeedAsync(a); var bs = await SeedAsync(b);   // B has data of its own, so "empty" results prove filtering, not absence
        var bAdmin = b.Admin;
        foreach (var url in new[] { $"/api/v1/demos/{s.Demo.Id}", $"/api/v1/trials/{s.Trial.Id}", $"/api/v1/customers/{s.Customer.Id}", $"/api/v1/payments/requests/{s.Request.Id}" })
            Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.Get(url)).StatusCode);
        Assert.DoesNotContain((await Crm.Ok<PagedResult<DemoDto>>(await bAdmin.Get("/api/v1/demos"))).Items, d => d.Id == s.Demo.Id);
        Assert.DoesNotContain((await Crm.Ok<PagedResult<TrialDto>>(await bAdmin.Get("/api/v1/trials"))).Items, d => d.Id == s.Trial.Id);
        Assert.DoesNotContain((await Crm.Ok<PagedResult<CustomerListItemDto>>(await bAdmin.Get("/api/v1/customers"))).Items, d => d.Id == s.Customer.Id);
        Assert.DoesNotContain((await Crm.Ok<PagedResult<PaymentRequestDto>>(await bAdmin.Get("/api/v1/payments/requests"))).Items, d => d.Id == s.Request.Id);
        Assert.DoesNotContain((await Crm.Ok<List<DemoRepDto>>(await bAdmin.Get("/api/v1/demo-settings/reps"))), d => d.Id == s.RepId);
        // mutations by id answer 404 and change nothing
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.Post($"/api/v1/demos/{s.Demo.Id}/cancel", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.Post($"/api/v1/demos/{s.Demo.Id}/reschedule", new { slotId = (await b.SlotAsync(3)).SlotId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.Put($"/api/v1/demos/{s.Demo.Id}", new { notes = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.Post($"/api/v1/trials/{s.Trial.Id}/cancel", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.Post($"/api/v1/trials/{s.Trial.Id}/provisioned", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.Put($"/api/v1/customers/{s.Customer.Id}/account", new { accountReference = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.Post($"/api/v1/customers/{s.Customer.Id}/subscriptions/{s.Customer.Subscriptions.Single().Id}/cancel", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.Post($"/api/v1/payments/requests/{s.Request.Id}/cancel", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.VerifyRawAsync(s.Request.Id, 500m, "CROSS-TENANT-RECEIPT")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.Post("/api/v1/payments/requests", new { subscriptionId = s.Customer.Subscriptions.Single().Id })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.Put($"/api/v1/demo-settings/reps/{s.RepId}/hours", new { rules = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.Post($"/api/v1/demo-settings/reps/{s.RepId}/exceptions", new { date = DateTime.UtcNow.Date.AddDays(3), kind = "Closed" })).StatusCode);
        // other workspace's leads/opportunities/plans/users cannot be referenced
        Assert.Equal(HttpStatusCode.NotFound, (await b.BookRawAsync(s.LeadId, (await b.SlotAsync(2)).SlotId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bAdmin.Post("/api/v1/trials", new { leadId = s.LeadId })).StatusCode);
        var won = await a.WonOpportunityAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await b.ConvertRawAsync(won.OpportunityId)).StatusCode);
        var wb = await b.WonOpportunityAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await bAdmin.Post("/api/v1/customers/convert", new { opportunityId = wb.OpportunityId, planId = a.W.PlanId, terms = "t", billingCycle = "Monthly" })).StatusCode);   // A's plan
        Assert.Equal(HttpStatusCode.BadRequest, (await bAdmin.Put("/api/v1/demo-settings/reps", new { userId = a.AdminUserId, isActive = true, bufferMinutes = 0, maxPerDay = 3 })).StatusCode);   // A's user
        // nothing of A changed
        Assert.Equal(DemoStatus.Booked, (await Crm.Ok<DemoDto>(await a.Admin.Get($"/api/v1/demos/{s.Demo.Id}"))).Status);
        Assert.Equal(PaymentRequestStatus.Open, (await Crm.Ok<PaymentRequestDto>(await a.Admin.Get($"/api/v1/payments/requests/{s.Request.Id}"))).Status);
        Assert.Equal(0, await a.Db(db => db.Payments.IgnoreQueryFilters().CountAsync(p => p.TenantId == a.TenantId)));
        _ = bs;
    }

    [RequiresMySqlFact]
    public async Task The_database_guard_refuses_cross_tenant_rows_and_jobs_only_touch_their_own_tenant()
    {
        await using var a = await ConvWorld.OpenAsync(app); await using var b = await ConvWorld.OpenAsync(app);
        var s = await SeedAsync(a);
        // DB guard: a scope of B cannot store a demo / trial / customer that points at A's lead, or a row stamped with A's tenant id
        await Assert.ThrowsAsync<CrossTenantViolationException>(() => b.Worker.Scopes.RunAsync(b.TenantId, async (sp, ct) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Demos.Add(Demo.Create(b.TenantId, s.LeadId, null, b.AdminUserId, DateTime.UtcNow.AddDays(9), DateTime.UtcNow.AddDays(9).AddHours(1), DemoStatus.Booked, DemoSource.Staff, null, null, DateTime.UtcNow, "t"));
            await db.SaveChangesAsync(ct);
        }));
        await Assert.ThrowsAsync<CrossTenantViolationException>(() => b.Worker.Scopes.RunAsync(b.TenantId, async (sp, ct) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Trials.Add(Trial.Create(b.TenantId, s.LeadId, null, null, null, null, 14, TrialSource.Staff, null, DateTime.UtcNow, "t"));
            await db.SaveChangesAsync(ct);
        }));
        await Assert.ThrowsAsync<CrossTenantViolationException>(() => b.Worker.Scopes.RunAsync(b.TenantId, async (sp, ct) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Customers.Add(Customer.Create(a.TenantId, 99, s.LeadId, null, "Forged", null, DateTime.UtcNow, "t"));   // stamped with A's tenant while the scope is B
            await db.SaveChangesAsync(ct);
        }));
        await Assert.ThrowsAsync<CrossTenantViolationException>(() => b.Worker.Scopes.RunAsync(b.TenantId, async (sp, ct) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.PaymentRequests.Add(PaymentRequest.Create(b.TenantId, s.Customer.Id, s.Customer.Subscriptions.Single().Id, s.Customer.Agreements.Single().Id, "PR-FORGED01", 1m, "USD", null, null, "staff", null, DateTime.UtcNow, "t"));   // A's customer
            await db.SaveChangesAsync(ct);
        }));
        // services inside B's scope cannot read A's rows even by id
        await b.Worker.Scopes.RunAsync(b.TenantId, async (sp, ct) =>
        {
            Assert.True((await sp.GetRequiredService<IDemoService>().GetAsync(s.Demo.Id, ct)).IsFailure);
            Assert.True((await sp.GetRequiredService<ICustomerService>().GetAsync(s.Customer.Id, ct)).IsFailure);
            Assert.True((await sp.GetRequiredService<IPaymentService>().VerifyManuallyAsync(s.Request.Id, new VerifyPaymentRequest(500m, "USD", PaymentMethod.Cash, "SCOPE-B-RECEIPT"), ct)).Error.Code is "not_found" or "forbidden");
        });

        // jobs: A has a due reminder; a sweep in B's scope leaves it alone, the real sweep job handles exactly A
        var account = await a.W.Ai.AccountAsync();
        await a.SettingsAsync(new() { ["remindersEnabled"] = true, ["reminderChannelAccountId"] = account.Id });
        var (lead, contact, _) = await MessagingKit.LeadWithContactAsync(a.Admin);
        await a.Admin.Post($"/api/v1/contacts/{contact}/consent", new { channel = "WhatsApp", purpose = "Service", status = "Granted", source = "x", evidenceRef = "y" });
        var demo = await a.BookAsync(lead, (await a.SlotAsync(3)).SlotId);
        app.Clock.Offset = demo.StartUtc.AddMinutes(-1440 + 1) - DateTime.UtcNow;
        await b.ReminderSweepAsync(); await b.TrialSweepAsync();
        Assert.All((await Crm.Ok<DemoDto>(await a.Admin.Get($"/api/v1/demos/{demo.Id}"))).Reminders, r => Assert.NotEqual(ReminderStatus.Sent, r.Status));
        using (var scope = a.Worker.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<ConversionSweepJob>().RunRemindersAsync(default);
        Assert.Equal(1, (await Crm.Ok<DemoDto>(await a.Admin.Get($"/api/v1/demos/{demo.Id}"))).Reminders.Count(r => r.Status == ReminderStatus.Sent));
        Assert.Equal(0, await b.Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.TenantId == b.TenantId && m.IdempotencyKey!.StartsWith("demo-reminder"))));
    }

    [RequiresMySqlFact]
    public async Task Default_roles_reach_existing_tenants_and_the_conversion_screens_enforce_them()
    {
        var w = app.World;
        async Task<ApiSession> Login(TestUser u) => await ApiSession.LoginAsync(app, u.Email, u.Password);
        var mgr = await Login(w.ASalesManager); var agent = await Login(w.ASalesAgent); var ro = await Login(w.AReadOnly); var admin = await Login(w.AAdmin); var marketing = await Login(w.AMarketing);
        foreach (var url in new[] { "/api/v1/demos", "/api/v1/trials", "/api/v1/customers", "/api/v1/payments/requests", "/api/v1/demo-settings" })
        {
            Assert.Equal(HttpStatusCode.OK, (await mgr.Get(url)).StatusCode); Assert.Equal(HttpStatusCode.OK, (await ro.Get(url)).StatusCode); Assert.Equal(HttpStatusCode.OK, (await admin.Get(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await ApiSession.Anonymous(app).Get(url)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await marketing.Get(url)).StatusCode);   // marketing has no sales-conversion access
        }
        Assert.Equal(HttpStatusCode.OK, (await agent.Get("/api/v1/demos")).StatusCode); Assert.Equal(HttpStatusCode.OK, (await agent.Get("/api/v1/trials")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.Post("/api/v1/trials", new { leadId = Guid.NewGuid() })).StatusCode);             // agents may book demos but not create trials
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.Put("/api/v1/demo-settings", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.Post("/api/v1/customers/convert", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post("/api/v1/demos", new { leadId = Guid.NewGuid(), slotId = "s202701010000" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post("/api/v1/trials", new { leadId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mgr.Post($"/api/v1/payments/requests/{Guid.NewGuid()}/verify", new { amount = 1, currency = "USD", method = "Cash", evidenceReference = "ABC123" })).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await mgr.Post("/api/v1/trials", new { leadId = Guid.NewGuid() })).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await admin.Post($"/api/v1/payments/requests/{Guid.NewGuid()}/verify", new { amount = 1, currency = "USD", method = "Cash", evidenceReference = "ABC123" })).StatusCode);
        // a client cannot choose the tenant
        var spoof = new HttpRequestMessage(HttpMethod.Get, "/api/v1/customers"); spoof.Headers.Add("X-Tenant-Id", w.B.Id.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await mgr.Http.SendAsync(spoof)).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task The_mvc_screens_render_hold_their_permissions_and_keep_other_workspaces_out()
    {
        await using var a = await ConvWorld.OpenAsync(app); await using var b = await ConvWorld.OpenAsync(app);
        var s = await SeedAsync(a);
        var web = await WebSession.LoginAsync(app, a.W.Ai.AdminUser.Email, a.W.Ai.AdminUser.Password);
        Assert.Contains("Demo calendar", await web.Html("/Demos")); Assert.Contains("Demo calendar", await web.Html("/Demos?view=day"));
        Assert.Contains("Book a demo", await web.Html("/Demos/Book")); Assert.Contains("Sales representatives", await web.Html("/DemoSettings"));
        Assert.Contains("Weekly hours", await web.Html($"/DemoSettings/Hours/{s.RepId}"));
        var details = await web.Html($"/Demos/Details/{s.Demo.Id}"); Assert.Contains("Cancel demo", details); Assert.Contains("Reschedule", details);
        Assert.Contains("Reschedule (", await web.Html($"/Demos/Details/{s.Demo.Id}?reschedule=true"));
        Assert.Contains("Trials", await web.Html("/Trials")); Assert.Contains("Record trial", await web.Html($"/Trials/Create?leadId={s.LeadId}"));
        Assert.Contains("staff must provision", await web.Html("/Trials")); Assert.Contains("NOT been created", await web.Html($"/Trials/Details/{s.Trial.Id}"));
        var cust = await web.Html($"/Customers/Details/{s.Customer.Id}");
        Assert.Contains(s.Customer.Name, cust); Assert.Contains("Verify payment", cust); Assert.Contains(s.Request.Reference, cust); Assert.Contains("activates only after a verified payment", cust);
        Assert.Contains("Customers", await web.Html("/Customers")); Assert.Contains(s.Request.Reference, await web.Html("/Payments"));
        var providers = await web.Html("/Payments/Providers"); Assert.Contains("Payment providers", providers); Assert.Contains("Fake provider (tests only)", providers);   // only installed adapters can be configured
        var won = await a.WonOpportunityAsync();
        Assert.Contains("Convert to customer", await web.Html($"/Customers?x=1")); Assert.Contains("Commercial terms", await web.Html($"/Customers/Convert?opportunityId={won.OpportunityId}"));

        // a manual verification through the page activates the subscription (antiforgery protected)
        var verify = await web.PostWithToken($"/Customers/Details/{s.Customer.Id}", "/Payments/Verify", ("id", s.Request.Id.ToString()), ("customerId", s.Customer.Id.ToString()), ("amount", "500.00"), ("currency", "USD"), ("method", "BankTransfer"), ("evidenceReference", "PAGE-RECEIPT-1"));
        Assert.Equal(HttpStatusCode.Redirect, verify.StatusCode);
        Assert.Equal(SubscriptionStatus.Active, (await a.CustomerAsync(s.Customer.Id)).Subscriptions.Single().Status);
        // POST without a token is refused
        var noToken = await web.Http.PostAsync("/Payments/Verify", new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = s.Request.Id.ToString() }));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        // another workspace gets 404 for A's ids and a read-only member cannot reach the mutating screens
        var wb = await WebSession.LoginAsync(app, b.W.Ai.AdminUser.Email, b.W.Ai.AdminUser.Password);
        foreach (var url in new[] { $"/Demos/Details/{s.Demo.Id}", $"/Trials/Details/{s.Trial.Id}", $"/Customers/Details/{s.Customer.Id}", $"/DemoSettings/Hours/{s.RepId}" })
            Assert.Equal(HttpStatusCode.NotFound, (await wb.Get(url)).StatusCode);
        var rdUser = await app.World.AddUserAsync(a.TenantId, $"ro{Guid.NewGuid():N}@example.com", "Read-only");
        var rd = await WebSession.LoginAsync(app, rdUser.Email, rdUser.Password);
        Assert.Equal(HttpStatusCode.OK, (await rd.Get("/Demos")).StatusCode); Assert.Equal(HttpStatusCode.OK, (await rd.Get("/Customers")).StatusCode);
        foreach (var url in new[] { "/Demos/Book", "/Trials/Create", "/Payments/Providers", $"/Customers/Convert?opportunityId={won.OpportunityId}" })
        { var r = await rd.Get(url); Assert.True(r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect, url + " -> " + r.StatusCode); if (r.StatusCode == HttpStatusCode.Redirect) Assert.Contains("AccessDenied", r.Headers.Location!.ToString()); }
        var denied = await rd.PostWithToken("/Demos", "/Payments/Verify", ("id", s.Request.Id.ToString()), ("customerId", s.Customer.Id.ToString()), ("amount", "500"), ("currency", "USD"), ("method", "Cash"), ("evidenceReference", "RO-RECEIPT-1"));
        Assert.True(denied.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Redirect);
    }
}
