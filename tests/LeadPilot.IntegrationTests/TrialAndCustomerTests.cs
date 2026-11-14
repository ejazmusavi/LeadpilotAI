using System.Net;
using LeadPilot.Application.Common;
using LeadPilot.Application.Conversion;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Conversion;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.IntegrationTests;

[Collection("app")]
public class TrialAndCustomerTests(AppFixture app)
{
    private static async Task<TrialDto> NewTrialAsync(ConvWorld w, Guid? lead = null, int? days = null) =>
        await Crm.Ok<TrialDto>(await w.Admin.Post("/api/v1/trials", new { leadId = lead ?? await w.LeadAsync(), planId = w.W.PlanId, durationDays = days }));

    [RequiresMySqlFact]
    public async Task A_trial_is_recorded_pending_until_staff_provision_it_and_never_reported_as_ready_before()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var lead = await w.LeadAsync();
        var t = await NewTrialAsync(w, lead);
        Assert.Equal(TrialStatus.PendingProvisioning, t.Status); Assert.True(t.ProvisioningRequiresStaff); Assert.Null(t.ExpiresAtUtc);
        Assert.Contains("NOT been created", t.ProvisioningMessage); Assert.Equal("Standard", t.PlanName); Assert.Equal(14, t.DurationDays);
        Assert.Single(await w.OutboxAsync("trial.created"));
        Assert.Equal(HttpStatusCode.Conflict, (await w.Admin.Post("/api/v1/trials", new { leadId = lead })).StatusCode);                // one live trial per lead
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Put($"/api/v1/trials/{t.Id}/usage", new { loginCount = 1, activeUsers = 1 })).StatusCode);   // nothing to measure yet
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post($"/api/v1/trials/{t.Id}/extend", new { days = 3 })).StatusCode);

        var live = await Crm.Ok<TrialDto>(await w.Admin.Post($"/api/v1/trials/{t.Id}/provisioned", new { externalAccountRef = "acct-77", loginUrl = "https://app.example.com/login" }));
        Assert.Equal(TrialStatus.Active, live.Status); Assert.False(live.ProvisioningRequiresStaff);
        Assert.InRange((live.ExpiresAtUtc!.Value - w.Now).TotalDays, 13.9, 14.1);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post($"/api/v1/trials/{t.Id}/provisioned", new { loginUrl = "https://x" })).StatusCode);   // already provisioned
        var used = await Crm.Ok<TrialDto>(await w.Admin.Put($"/api/v1/trials/{t.Id}/usage", new { lastActivityUtc = w.Now.AddHours(-1), loginCount = 12, activeUsers = 3, notes = "Checked the admin console" }));
        Assert.Equal(12, used.LoginCount); Assert.Contains(used.Events, e => e.Type == "Usage");
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Put($"/api/v1/trials/{t.Id}/usage", new { lastActivityUtc = w.Now.AddDays(3), loginCount = 1, activeUsers = 1 })).StatusCode);
        var ext = await Crm.Ok<TrialDto>(await w.Admin.Post($"/api/v1/trials/{t.Id}/extend", new { days = 7 }));
        Assert.Equal(21, ext.DurationDays);
        var cancelled = await Crm.Ok<TrialDto>(await w.Admin.Post($"/api/v1/trials/{t.Id}/cancel", new { reason = "not interested" }));
        Assert.Equal(TrialStatus.Cancelled, cancelled.Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post($"/api/v1/trials/{t.Id}/cancel", new { })).StatusCode);
        await NewTrialAsync(w, lead);   // a cancelled trial no longer blocks a new one
    }

    [RequiresMySqlFact]
    public async Task Expiry_notices_and_expiry_are_idempotent_and_create_the_follow_up_and_the_event_once()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var lead = await w.LeadAsync();
        var t = await NewTrialAsync(w, lead, days: 3);
        await w.Admin.Post($"/api/v1/trials/{t.Id}/provisioned", new { externalAccountRef = "a-1" });
        await w.TrialSweepAsync();                                                     // 3 days away, notice window is 48 h: nothing yet
        Assert.Empty(await w.OutboxAsync("trial.expires"));
        app.Clock.Offset = TimeSpan.FromHours(30);                                      // 42 h left
        await Task.WhenAll(w.TrialSweepAsync(), w.TrialSweepAsync()); await w.TrialSweepAsync();
        Assert.Single(await w.OutboxAsync("trial.expires"));
        Assert.Equal(1, await w.Db(db => db.FollowUps.IgnoreQueryFilters().CountAsync(f => f.TenantId == w.TenantId && f.LeadId == lead && f.CreatedByKind == ActorKind.System)));
        Assert.Equal(TrialStatus.Active, (await Crm.Ok<TrialDto>(await w.Admin.Get($"/api/v1/trials/{t.Id}"))).Status);
        app.Clock.Offset = TimeSpan.FromDays(3) + TimeSpan.FromMinutes(5);
        await w.TrialSweepAsync(); await w.TrialSweepAsync();
        var ended = await Crm.Ok<TrialDto>(await w.Admin.Get($"/api/v1/trials/{t.Id}"));
        Assert.Equal(TrialStatus.Expired, ended.Status); Assert.Equal(1, ended.Events.Count(e => e.Type == "Expired")); Assert.Equal(1, ended.Events.Count(e => e.Type == "ExpiryNotice"));
        Assert.Single(await w.OutboxAsync("trial.expires"));
        // extending an expired trial reopens it and re-arms the notice
        var again = await Crm.Ok<TrialDto>(await w.Admin.Post($"/api/v1/trials/{t.Id}/extend", new { days = 2 }));
        Assert.Equal(TrialStatus.Active, again.Status); Assert.Null(again.ExpiryNoticeAtUtc);
    }

    [RequiresMySqlFact]
    public async Task The_customer_notice_uses_the_service_pipeline_once_and_only_for_eligible_contacts()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var account = await w.W.Ai.AccountAsync();
        await w.SettingsAsync(new() { ["trialCustomerNoticesEnabled"] = true, ["reminderChannelAccountId"] = account.Id, ["trialDefaultDays"] = 3 });
        var (leadOk, contactOk, _) = await MessagingKit.LeadWithContactAsync(w.Admin);
        Assert.True((await w.Admin.Post($"/api/v1/contacts/{contactOk}/consent", new { channel = "WhatsApp", purpose = "Service", status = "Granted", source = "Signup", evidenceRef = "s-1" })).IsSuccessStatusCode);
        var (leadSup, contactSup, phoneSup) = await MessagingKit.LeadWithContactAsync(w.Admin);
        Assert.True((await w.Admin.Post($"/api/v1/contacts/{contactSup}/consent", new { channel = "WhatsApp", purpose = "Service", status = "Granted", source = "Signup", evidenceRef = "s-2" })).IsSuccessStatusCode);
        Assert.True((await w.Admin.Post("/api/v1/suppression", new { channel = "WhatsApp", value = phoneSup, reason = "OptOut" })).IsSuccessStatusCode);
        var ok = await NewTrialAsync(w, leadOk); var sup = await NewTrialAsync(w, leadSup);
        foreach (var t in new[] { ok, sup }) await w.Admin.Post($"/api/v1/trials/{t.Id}/provisioned", new { externalAccountRef = "a" });
        app.Clock.Offset = TimeSpan.FromHours(30);
        await w.TrialSweepAsync(); await w.TrialSweepAsync();
        var msgs = await w.Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.TenantId == w.TenantId && m.IdempotencyKey!.StartsWith("trial-expiry:")).ToListAsync());
        Assert.Single(msgs); Assert.Contains(ok.Id.ToString("N"), msgs[0].IdempotencyKey);
        Assert.Contains((await Crm.Ok<TrialDto>(await w.Admin.Get($"/api/v1/trials/{sup.Id}"))).Events, e => e.Type == "NoticeNotSent");
    }

    [RequiresMySqlFact]
    public async Task Converting_a_won_opportunity_creates_a_pending_customer_that_links_the_history_and_cannot_be_repeated()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var lead = await w.LeadAsync();
        var trial = await NewTrialAsync(w, lead); await w.Admin.Post($"/api/v1/trials/{trial.Id}/provisioned", new { externalAccountRef = "a" });
        var opp = await Crm.Ok<LeadPilot.Application.Crm.OpportunityDto>(await w.Admin.Post($"/api/v1/leads/{lead}/opportunities", new { name = "Deal", value = 500m, currency = "USD" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await w.ConvertRawAsync(opp.Id)).StatusCode);                              // still open
        Assert.Empty(await Crm.Ok<List<WonOpportunityDto>>(await w.Admin.Get("/api/v1/customers/won-opportunities")));
        var won = await w.WonOpportunityAsync(lead);
        Assert.Single(await Crm.Ok<List<WonOpportunityDto>>(await w.Admin.Get("/api/v1/customers/won-opportunities")), x => x.OpportunityId == won.OpportunityId);
        var leadActivitiesBefore = await w.Db(db => db.LeadActivities.IgnoreQueryFilters().Where(a => a.TenantId == w.TenantId && a.LeadId == lead).Select(a => a.Id).ToListAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post("/api/v1/customers/convert", new { opportunityId = won.OpportunityId, planId = Guid.NewGuid(), terms = "x", billingCycle = "Monthly" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post("/api/v1/customers/convert", new { opportunityId = won.OpportunityId, planId = w.W.PlanId, terms = " ", billingCycle = "Monthly" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post("/api/v1/customers/convert", new { opportunityId = won.OpportunityId, planId = w.W.PlanId, terms = "t", billingCycle = "Monthly", price = 0m })).StatusCode);
        var res = await w.Admin.Post("/api/v1/customers/convert", new { opportunityId = won.OpportunityId, planId = w.W.PlanId, terms = "12 months, billed monthly.", billingCycle = "Monthly", price = 450m, trialId = trial.Id });
        var c = await Crm.Ok<CustomerDto>(res);
        Assert.Equal(lead, c.LeadId); Assert.Equal(CustomerStatus.Onboarding, c.Status); Assert.StartsWith("C-", c.Code);
        var sub = Assert.Single(c.Subscriptions); Assert.Equal(SubscriptionStatus.Pending, sub.Status); Assert.Null(sub.ActivatedByPaymentId);
        var ag = Assert.Single(c.Agreements); Assert.Equal(450m, ag.Price); Assert.Equal(500m, ag.ListPrice); Assert.Equal(10m, ag.DiscountPercent); Assert.Equal("USD", ag.Currency); Assert.False(string.IsNullOrEmpty(ag.ApprovedByName));
        Assert.Equal(TrialStatus.Converted, (await Crm.Ok<TrialDto>(await w.Admin.Get($"/api/v1/trials/{trial.Id}"))).Status);

        // history is LINKED, not copied: the lead's own activity rows are all still there, and the customer timeline reads them
        var after = await w.Db(db => db.LeadActivities.IgnoreQueryFilters().Where(a => a.TenantId == w.TenantId && a.LeadId == lead).Select(a => a.Id).ToListAsync());
        Assert.True(leadActivitiesBefore.All(after.Contains));
        Assert.Contains(c.Timeline, i => i.Kind == "Lead" && i.Summary.Contains("Opportunity")); Assert.Contains(c.Timeline, i => i.Kind == "Opportunity" && i.Summary.Contains("Converted"));
        Assert.Contains(c.Timeline, i => i.Kind == "Trial"); Assert.Contains(after, id => true);

        Assert.Equal(HttpStatusCode.Conflict, (await w.ConvertRawAsync(won.OpportunityId)).StatusCode);                      // converted exactly once
        Assert.Empty(await Crm.Ok<List<WonOpportunityDto>>(await w.Admin.Get("/api/v1/customers/won-opportunities")));
        // the agreement is a fact: it cannot be edited afterwards
        await Assert.ThrowsAnyAsync<Exception>(() => w.Db(async db => { var a = await db.CommercialAgreements.IgnoreQueryFilters().FirstAsync(x => x.TenantId == w.TenantId); db.Entry(a).Property(x => x.Price).CurrentValue = 1m; await db.SaveChangesAsync(); return 0; }));
        // a second won opportunity of the same lead attaches to the same customer record
        var second = await w.WonOpportunityAsync(lead, 900m);
        var c2 = await Crm.Ok<CustomerDto>(await w.ConvertRawAsync(second.OpportunityId));
        Assert.Equal(c.Id, c2.Id); Assert.Equal(2, c2.Subscriptions.Count);
        var list = await Crm.Ok<PagedResult<CustomerListItemDto>>(await w.Admin.Get("/api/v1/customers"));
        Assert.Single(list.Items, x => x.Id == c.Id);
    }

    [RequiresMySqlFact]
    public async Task A_customer_with_pending_subscription_can_cancel_it_and_cancelled_trials_cannot_convert()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var lead = await w.LeadAsync();
        var trial = await NewTrialAsync(w, lead); await w.Admin.Post($"/api/v1/trials/{trial.Id}/cancel", new { });
        var won = await w.WonOpportunityAsync(lead);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post("/api/v1/customers/convert", new { opportunityId = won.OpportunityId, planId = w.W.PlanId, terms = "t", billingCycle = "Yearly", trialId = trial.Id })).StatusCode);
        var c = await Crm.Ok<CustomerDto>(await w.ConvertRawAsync(won.OpportunityId));
        var req = await Crm.Ok<PaymentRequestDto>(await w.Admin.Post("/api/v1/payments/requests", new { subscriptionId = c.Subscriptions.Single().Id }));
        var cancelled = await Crm.Ok<CustomerDto>(await w.Admin.Post($"/api/v1/customers/{c.Id}/subscriptions/{c.Subscriptions.Single().Id}/cancel", new { reason = "changed plans" }));
        Assert.Equal(SubscriptionStatus.Cancelled, cancelled.Subscriptions.Single().Status); Assert.Equal(CustomerStatus.Cancelled, cancelled.Status);
        Assert.Equal(PaymentRequestStatus.Cancelled, cancelled.PaymentRequests.Single().Status);
        Assert.Equal(HttpStatusCode.Conflict, (await w.VerifyRawAsync(req.Id, 500m, "RECEIPT-CANCELLED-1")).StatusCode);   // money for a cancelled request never activates anything
    }
}
