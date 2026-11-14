using System.Net;
using System.Text;
using System.Text.Json;
using LeadPilot.Application.Agents;
using LeadPilot.Application.Common;
using LeadPilot.Application.Conversion;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

[Collection("app")]
public class PaymentTests(AppFixture app)
{
    private async Task<int> PaymentsAsync(ConvWorld w) => await w.Db(db => db.Payments.IgnoreQueryFilters().CountAsync(p => p.TenantId == w.TenantId));
    private static async Task<Guid> TriggerAsync(ConvWorld w, ChatThread chat, string? text = null)
    {
        if (text is not null) await chat.SendAsync(text);
        return await w.Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.TenantId == w.TenantId && m.ConversationId == chat.ConversationId && m.Direction == LeadPilot.Domain.Messaging.MessageDirection.Inbound).OrderByDescending(m => m.CreatedAtUtc).Select(m => m.Id).FirstAsync());
    }

    private static async Task<SubscriptionStatus> SubStatus(ConvWorld w, Guid customerId) => (await w.CustomerAsync(customerId)).Subscriptions.Single().Status;

    [RequiresMySqlFact]
    public async Task A_payment_request_takes_amount_and_currency_from_the_agreement_and_activates_nothing()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var (c, r) = await w.NewRequestAsync();
        Assert.Equal(500m, r.Amount); Assert.Equal("USD", r.Currency); Assert.Equal(PaymentRequestStatus.Open, r.Status); Assert.StartsWith("PR-", r.Reference); Assert.Contains("Quote the reference", r.Instructions);
        Assert.Equal(SubscriptionStatus.Pending, await SubStatus(w, c.Id)); Assert.Equal(CustomerStatus.Onboarding, (await w.CustomerAsync(c.Id)).Status); Assert.Equal(0, await PaymentsAsync(w));
        Assert.Equal(HttpStatusCode.Conflict, (await w.Admin.Post("/api/v1/payments/requests", new { subscriptionId = c.Subscriptions.Single().Id })).StatusCode);   // one open request
        Assert.Equal(HttpStatusCode.NotFound, (await w.Admin.Post("/api/v1/payments/requests", new { subscriptionId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Admin.Get($"/api/v1/payments/requests/{r.Id}")).StatusCode);
        // the request body cannot carry an amount: whatever extra a client sends is ignored
        var req2 = await w.Admin.Post("/api/v1/payments/requests/" + r.Id + "/cancel", new { reason = "x", amount = 1m, status = "Paid" });
        Assert.Equal(PaymentRequestStatus.Cancelled, (await Crm.Ok<PaymentRequestDto>(req2)).Status);
        Assert.Equal(SubscriptionStatus.Pending, await SubStatus(w, c.Id));
    }

    [RequiresMySqlFact]
    public async Task Only_a_matching_payment_with_evidence_verifies_and_it_activates_the_subscription_exactly_once()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var (c, r) = await w.NewRequestAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await w.VerifyRawAsync(r.Id, 250m, "RECEIPT-0001")).StatusCode);                 // partial
        Assert.Equal(HttpStatusCode.BadRequest, (await w.VerifyRawAsync(r.Id, 500m, "RECEIPT-0001", "EUR")).StatusCode);          // wrong currency
        Assert.Equal(HttpStatusCode.BadRequest, (await w.VerifyRawAsync(r.Id, 500m, "-")).StatusCode);                              // no evidence
        Assert.Equal(HttpStatusCode.BadRequest, (await w.VerifyRawAsync(r.Id, 500.005m, "RECEIPT-0001")).StatusCode);
        Assert.Equal(SubscriptionStatus.Pending, await SubStatus(w, c.Id)); Assert.Equal(0, await PaymentsAsync(w));
        var ok = await Crm.Ok<PaymentRequestDto>(await w.VerifyRawAsync(r.Id, 500m, "Receipt 0001"));
        Assert.Equal(PaymentRequestStatus.Paid, ok.Status); Assert.Equal(VerificationSource.Manual, ok.Payment!.Source); Assert.NotNull(ok.Payment.VerifiedByName);
        var after = await w.CustomerAsync(c.Id);
        Assert.Equal(SubscriptionStatus.Active, after.Subscriptions.Single().Status); Assert.Equal(ok.Payment.Id, after.Subscriptions.Single().ActivatedByPaymentId); Assert.Equal(CustomerStatus.Active, after.Status);
        Assert.Single(await w.OutboxAsync("payment.confirmed"));
        Assert.Equal(HttpStatusCode.Conflict, (await w.VerifyRawAsync(r.Id, 500m, "RECEIPT-0002")).StatusCode);                  // already paid
        Assert.Equal(1, await PaymentsAsync(w)); Assert.Single(await w.OutboxAsync("payment.confirmed"));

        // the same receipt cannot verify a second customer's payment
        var (c2, r2) = await w.NewRequestAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await w.VerifyRawAsync(r2.Id, 500m, "RECEIPT 0001")).StatusCode);
        Assert.Equal(SubscriptionStatus.Pending, await SubStatus(w, c2.Id)); Assert.Equal(PaymentRequestStatus.Open, (await Crm.Ok<PaymentRequestDto>(await w.Admin.Get($"/api/v1/payments/requests/{r2.Id}"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await w.VerifyRawAsync(r2.Id, 500m, "RECEIPT-0099")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Racing_verifications_activate_once_and_the_database_refuses_an_unverified_active_subscription()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var (c, r) = await w.NewRequestAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => w.VerifyRawAsync(r.Id, 500m, $"RACE-RECEIPT-{i}")));
        Assert.Equal(1, results.Count(x => x.StatusCode == HttpStatusCode.OK)); Assert.Equal(4, results.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await PaymentsAsync(w)); Assert.Single(await w.OutboxAsync("payment.confirmed"));
        // a database-level guard: no code path (or manual SQL) can leave a subscription Active without a payment
        var (c2, _) = await w.NewRequestAsync();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => w.Db(async db => { await db.Database.ExecuteSqlRawAsync("UPDATE Subscriptions SET Status='Active' WHERE Id={0}", c2.Subscriptions.Single().Id); return 0; }));
        Assert.Contains("CK_Subscriptions_ActiveNeedsPayment", ex.ToString());
        Assert.Equal(SubscriptionStatus.Pending, await SubStatus(w, c2.Id));
    }

    [RequiresMySqlFact]
    public async Task Verification_and_requests_are_separate_permissions_and_anonymous_callers_are_refused()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var (c, r) = await w.NewRequestAsync();
        var manager = await app.World.AddUserAsync(w.TenantId, $"mgr{Guid.NewGuid():N}@example.com", "Sales Manager"); var mgr = await ApiSession.LoginAsync(app, manager.Email, manager.Password);
        var agent = await app.World.AddUserAsync(w.TenantId, $"agt{Guid.NewGuid():N}@example.com", "Sales Agent"); var ag = await ApiSession.LoginAsync(app, agent.Email, agent.Password);
        var ro = await app.World.AddUserAsync(w.TenantId, $"ro{Guid.NewGuid():N}@example.com", "Read-only"); var rd = await ApiSession.LoginAsync(app, ro.Email, ro.Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.VerifyRawAsync(r.Id, 500m, "MGR-RECEIPT-1", s: mgr)).StatusCode);          // a sales manager can request but not verify
        Assert.Equal(HttpStatusCode.Forbidden, (await w.VerifyRawAsync(r.Id, 500m, "AGT-RECEIPT-1", s: ag)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.VerifyRawAsync(r.Id, 500m, "RO-RECEIPT-1", s: rd)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await w.VerifyRawAsync(r.Id, 500m, "ANON-RECEIPT-1", s: ApiSession.Anonymous(app))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ag.Post("/api/v1/payments/requests", new { subscriptionId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mgr.Get("/api/v1/payments/providers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mgr.Get($"/api/v1/payments/requests/{r.Id}")).StatusCode);                           // but can see them
        Assert.Equal(HttpStatusCode.Forbidden, (await rd.Post("/api/v1/customers/convert", new { })).StatusCode);
        Assert.Equal(SubscriptionStatus.Pending, await SubStatus(w, c.Id)); Assert.Equal(0, await PaymentsAsync(w));
        // the manager can create a request for a new customer
        var c2 = await w.NewCustomerAsync();
        Assert.Equal(HttpStatusCode.Created, (await mgr.Post("/api/v1/payments/requests", new { subscriptionId = c2.Subscriptions.Single().Id })).StatusCode);
    }

    // ------------------------------------------------------------------ AI tool gateway and workflow actions can never activate

    [RequiresMySqlFact]
    public async Task The_ai_can_request_a_payment_only_after_approval_and_can_never_activate_a_subscription()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var chat = await w.W.ChatAsync(linkLead: true);
        var leadId = await w.Db(db => (from ct in db.Contacts.IgnoreQueryFilters() where ct.TenantId == w.TenantId && ct.PhoneE164 == chat.Phone select ct.LeadId).FirstAsync());
        var trig = await w.Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.TenantId == w.TenantId && m.ConversationId == chat.ConversationId && m.Direction == LeadPilot.Domain.Messaging.MessageDirection.Inbound).OrderByDescending(m => m.CreatedAtUtc).Select(m => m.Id).FirstAsync());

        // not a customer yet: after approval the service says so and creates nothing
        var r0 = await w.W.RunToolAsync(chat.ConversationId, AgentTools.CreatePaymentRequest, new { plan = "Standard" }, trigger: trig);
        Assert.Equal(ToolOutcome.ApprovalRequired, r0.Outcome);
        var approvals = (await ApiSession.Json<PagedResult<LeadPilot.Application.Agents.ApprovalDto>>(await w.Admin.Get("/api/v1/approvals?status=Pending"))).Items;
        Assert.Equal(HttpStatusCode.OK, (await w.Admin.Post($"/api/v1/approvals/{approvals.Single().Id}/approve", new { })).StatusCode);
        Assert.Equal(0, await w.Db(db => db.PaymentRequests.IgnoreQueryFilters().CountAsync(p => p.TenantId == w.TenantId)));
        Assert.Equal(LeadPilot.Domain.Agents.ExecutionState.Failed, (await ApiSession.Json<LeadPilot.Application.Agents.ApprovalDto>(await w.Admin.Get($"/api/v1/approvals/{approvals.Single().Id}"))).Execution);

        // converted by a person: the approved tool call now creates the request from the agreement price
        var c = await w.NewCustomerAsync(leadId);
        trig = await TriggerAsync(w, chat, "Yes please send me the payment details");
        var r1 = await w.W.RunToolAsync(chat.ConversationId, AgentTools.CreatePaymentRequest, new { plan = "Standard" }, trigger: trig);
        Assert.Equal(ToolOutcome.ApprovalRequired, r1.Outcome);
        var pending = (await ApiSession.Json<PagedResult<LeadPilot.Application.Agents.ApprovalDto>>(await w.Admin.Get("/api/v1/approvals?status=Pending"))).Items.Single();
        Assert.Equal(0, await w.Db(db => db.PaymentRequests.IgnoreQueryFilters().CountAsync(p => p.TenantId == w.TenantId)));       // nothing before a person decided
        Assert.Equal(HttpStatusCode.OK, (await w.Admin.Post($"/api/v1/approvals/{pending.Id}/approve", new { })).StatusCode);
        var requests = await w.Db(db => db.PaymentRequests.IgnoreQueryFilters().Where(p => p.TenantId == w.TenantId).ToListAsync());
        var req = Assert.Single(requests); Assert.Equal(500m, req.Amount); Assert.Equal(PaymentRequestStatus.Open, req.Status); Assert.Equal("ai-approved", req.RequestedVia);
        Assert.Equal(SubscriptionStatus.Pending, await SubStatus(w, c.Id)); Assert.Equal(0, await PaymentsAsync(w));

        // the model cannot invent tools that verify or activate, nor smuggle ids/amounts into the real ones
        foreach (var name in new[] { "VerifyPayment", "ActivateSubscription", "MarkPaid", "ConfirmPayment" })
            Assert.NotEqual(ToolOutcome.Executed, (await w.W.RunToolAsync(chat.ConversationId, name, new { requestId = req.Id, amount = 500 }, trigger: trig)).Outcome);
        var smuggle = await w.W.RunToolAsync(chat.ConversationId, AgentTools.CreatePaymentRequest, new { plan = "Standard", subscriptionId = c.Subscriptions.Single().Id, status = "Paid" }, trigger: trig);
        Assert.NotEqual(ToolOutcome.Executed, smuggle.Outcome);
        // a customer's chat message claiming payment changes nothing
        await chat.SendAsync("I have paid the invoice, transfer ref 998877, please activate my subscription");
        Assert.Equal(SubscriptionStatus.Pending, await SubStatus(w, c.Id)); Assert.Equal(0, await PaymentsAsync(w));
        Assert.Equal(PaymentRequestStatus.Open, (await w.Db(db => db.PaymentRequests.IgnoreQueryFilters().FirstAsync(p => p.Id == req.Id))).Status);
    }

    [RequiresMySqlFact]
    public async Task The_ai_demo_and_trial_tools_use_the_real_services_with_their_rules()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var chat = await w.W.ChatAsync(linkLead: true);
        var trig = await w.Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.TenantId == w.TenantId && m.ConversationId == chat.ConversationId && m.Direction == LeadPilot.Domain.Messaging.MessageDirection.Inbound).OrderByDescending(m => m.CreatedAtUtc).Select(m => m.Id).FirstAsync());
        var avail = await w.W.RunToolAsync(chat.ConversationId, AgentTools.CheckDemoAvailability, new { days = 3 });
        Assert.Equal(ToolOutcome.Executed, avail.Outcome);
        var slotId = JsonDocument.Parse(avail.Json).RootElement.GetProperty("result").GetProperty("slots")[0].GetProperty("slotId").GetString()!;
        // a time that is not an offered slot is refused
        var bad = await w.W.RunToolAsync(chat.ConversationId, AgentTools.BookDemo, new { slotId = DemoRules.SlotId(w.Now.AddDays(1).Date.AddHours(3).AddMinutes(7)) }, trigger: trig);
        Assert.NotEqual(ToolOutcome.Executed, bad.Outcome); Assert.Equal(0, await w.Db(db => db.Demos.IgnoreQueryFilters().CountAsync(d => d.TenantId == w.TenantId)));
        var booked = await w.W.RunToolAsync(chat.ConversationId, AgentTools.BookDemo, new { slotId, notes = "asked on WhatsApp" }, trigger: trig);
        Assert.Equal(ToolOutcome.Executed, booked.Outcome);
        var demo = await w.Db(db => db.Demos.IgnoreQueryFilters().SingleAsync(d => d.TenantId == w.TenantId)); Assert.Equal(DemoSource.Automation, demo.Source); Assert.Equal(DemoRules.SlotId(demo.StartUtc), slotId);
        Assert.Single(await w.OutboxAsync("demo.booked"));
        // trial: recorded, honest about provisioning; the second needs approval
        var t1 = await w.W.RunToolAsync(chat.ConversationId, AgentTools.CreateTrialAccount, new { plan = "Standard" }, trigger: trig);
        Assert.Equal(ToolOutcome.Executed, t1.Outcome); Assert.Contains("NOT", t1.Json);
        var trial = await w.Db(db => db.Trials.IgnoreQueryFilters().SingleAsync(t => t.TenantId == w.TenantId)); Assert.Equal(TrialStatus.PendingProvisioning, trial.Status); Assert.Equal(TrialSource.Automation, trial.Source);
        trig = await TriggerAsync(w, chat, "Can I have another trial please?");
        Assert.Equal(ToolOutcome.ApprovalRequired, (await w.W.RunToolAsync(chat.ConversationId, AgentTools.CreateTrialAccount, new { plan = "Standard" }, trigger: trig)).Outcome);
        Assert.Equal(1, await w.Db(db => db.Trials.IgnoreQueryFilters().CountAsync(t => t.TenantId == w.TenantId)));
        Assert.NotEqual(ToolOutcome.Executed, (await w.W.RunToolAsync(chat.ConversationId, AgentTools.CreateTrialAccount, new { plan = "No such plan" }, trigger: trig)).Outcome);
    }

    [RequiresMySqlFact]
    public async Task Workflows_start_from_the_phase_10_events_book_demos_and_have_no_way_to_confirm_payments()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        // a workflow that books the next free slot, and two that react to the new events
        var book = await Crm.Ok<WorkflowDto>(await w.Admin.Post("/api/v1/workflows", new { name = "Book demo", description = "t", definition = Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("demo", "ScheduleDemo", new { withinDays = 5 })]) }));
        await Crm.Ok<WorkflowDto>(await w.Admin.Post($"/api/v1/workflows/{book.Id}/versions/1/activate"));
        var onBooked = await Crm.Ok<WorkflowDto>(await w.Admin.Post("/api/v1/workflows", new { name = "After booking", description = "t", definition = Wf.Definition([Wf.Trigger("demo.booked")], [Wf.Step("tag", "AddTag", new { tag = "demo-booked" })]) }));
        await Crm.Ok<WorkflowDto>(await w.Admin.Post($"/api/v1/workflows/{onBooked.Id}/versions/1/activate"));
        var onPaid = await Crm.Ok<WorkflowDto>(await w.Admin.Post("/api/v1/workflows", new { name = "After payment", description = "t", definition = Wf.Definition([Wf.Trigger("payment.confirmed")], [Wf.Step("tag", "AddTag", new { tag = "paid" })]) }));
        await Crm.Ok<WorkflowDto>(await w.Admin.Post($"/api/v1/workflows/{onPaid.Id}/versions/1/activate"));

        var lead = await w.LeadAsync();
        var run = await Crm.Ok<ExecutionDto>(await w.Admin.Post($"/api/v1/workflows/{book.Id}/run", new { leadId = lead }));
        await w.Worker.Scopes.RunAsync(w.TenantId, (sp, ct) => sp.GetRequiredService<IWorkflowEngine>().RunAsync(run.Summary.Id, ct));
        var done = await Crm.Ok<ExecutionDto>(await w.Admin.Get($"/api/v1/workflow-executions/{run.Summary.Id}"));
        Assert.Equal(ExecutionStatus.Completed, done.Summary.Status);
        var demo = await w.Db(db => db.Demos.IgnoreQueryFilters().SingleAsync(d => d.TenantId == w.TenantId && d.LeadId == lead)); Assert.Equal(DemoSource.Automation, demo.Source); Assert.True(demo.StartUtc > w.Now);

        // demo.booked is now a real event: it starts the second workflow
        await w.Worker.PumpAsync();
        var reacted = (await Crm.Ok<PagedResult<ExecutionListItemDto>>(await w.Admin.Get($"/api/v1/workflow-executions?workflowId={onBooked.Id}&leadId={lead}"))).Items;
        Assert.Single(reacted);

        // payment.confirmed likewise, and only after a verified payment
        var (c, r) = await w.NewRequestAsync();
        await w.Worker.PumpAsync();
        Assert.Empty((await Crm.Ok<PagedResult<ExecutionListItemDto>>(await w.Admin.Get($"/api/v1/workflow-executions?workflowId={onPaid.Id}"))).Items);
        await Crm.Ok<PaymentRequestDto>(await w.VerifyRawAsync(r.Id, 500m, "WF-RECEIPT-1"));
        await w.Worker.PumpAsync();
        Assert.Single((await Crm.Ok<PagedResult<ExecutionListItemDto>>(await w.Admin.Get($"/api/v1/workflow-executions?workflowId={onPaid.Id}"))).Items);
        // no workflow step type can touch payments or subscriptions
        Assert.DoesNotContain(StepCatalog.All, s => s.Type.ToString().Contains("Pay", StringComparison.OrdinalIgnoreCase) || s.Type.ToString().Contains("Subscription", StringComparison.OrdinalIgnoreCase));
    }

    // ------------------------------------------------------------------ provider webhooks (test-only fake provider)

    private static byte[] Body(string id, string reference, decimal amount, string kind = "succeeded", string currency = "USD") =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, reference, amount, currency, kind, at = DateTime.UtcNow.AddMinutes(-1), paymentId = "pay_" + id }));

    private static async Task<HttpResponseMessage> PostHookAsync(AppFixture app, Guid webhookId, byte[] body, string? secret, string? signatureOverride = null)
    {
        using var client = app.Api!.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/payments/webhooks/{webhookId}") { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new("application/json");
        if (secret is not null || signatureOverride is not null) req.Headers.Add("X-Fake-Signature", signatureOverride ?? FakeProviderAdapter.Sign(secret!, body));
        return await client.SendAsync(req);
    }

    private const string Secret = "whsec_test_0123456789abcdef";
    private static async Task<ProviderConfigDto> ConfigureAsync(ConvWorld w, bool active = true, string secret = Secret) =>
        await Crm.Ok<ProviderConfigDto>(await w.Admin.Put("/api/v1/payments/providers", new { providerKey = "fakepay", secret, isActive = active }));

    [RequiresMySqlFact]
    public async Task A_signed_provider_event_verifies_the_payment_once_and_forged_tampered_wrong_and_duplicate_events_do_nothing()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var (c, r) = await w.NewRequestAsync();
        var cfg = await ConfigureAsync(w);
        Assert.Equal("fakepay", cfg.ProviderKey); Assert.DoesNotContain(Secret, JsonSerializer.Serialize(cfg));
        Assert.DoesNotContain(Secret, await w.Db(db => db.PaymentProviderConfigs.IgnoreQueryFilters().Where(x => x.TenantId == w.TenantId).Select(x => x.SecretProtected).FirstAsync()));   // stored encrypted
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Put("/api/v1/payments/providers", new { providerKey = "unknown-provider", secret = Secret, isActive = true })).StatusCode);   // no adapter installed

        var forged = await PostHookAsync(app, cfg.WebhookId, Body("evt_1", r.Reference, 500m), null, "deadbeef");
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostHookAsync(app, cfg.WebhookId, Body("evt_1", r.Reference, 500m), null)).StatusCode);   // unsigned
        var signedOther = FakeProviderAdapter.Sign(Secret, Body("evt_1", r.Reference, 1m));
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostHookAsync(app, cfg.WebhookId, Body("evt_1", r.Reference, 500m), null, signedOther)).StatusCode);   // tampered body
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostHookAsync(app, cfg.WebhookId, Body("evt_1", r.Reference, 500m), "a-different-secret-0123456789")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostHookAsync(app, Guid.NewGuid(), Body("evt_1", r.Reference, 500m), Secret)).StatusCode);
        Assert.Equal(SubscriptionStatus.Pending, await SubStatus(w, c.Id)); Assert.Equal(0, await PaymentsAsync(w));
        Assert.Equal(0, await w.Db(db => db.PaymentEvents.IgnoreQueryFilters().CountAsync(e => e.TenantId == w.TenantId)));

        // valid signature but wrong amount / currency / not a success / unknown reference: recorded as rejected or ignored, nothing activates
        foreach (var (id, body) in new[] { ("evt_amt", Body("evt_amt", r.Reference, 499m)), ("evt_cur", Body("evt_cur", r.Reference, 500m, currency: "EUR")), ("evt_fail", Body("evt_fail", r.Reference, 500m, "failed")), ("evt_ref", Body("evt_ref", "PR-NOPE0000", 500m)) })
            Assert.Equal(HttpStatusCode.OK, (await PostHookAsync(app, cfg.WebhookId, body, Secret)).StatusCode);
        Assert.Equal(SubscriptionStatus.Pending, await SubStatus(w, c.Id)); Assert.Equal(0, await PaymentsAsync(w));
        Assert.Equal(4, await w.Db(db => db.PaymentEvents.IgnoreQueryFilters().CountAsync(e => e.TenantId == w.TenantId && e.Outcome != PaymentEventOutcome.Applied)));

        var good = Body("evt_ok", r.Reference, 500m);
        var res = await PostHookAsync(app, cfg.WebhookId, good, Secret);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode); Assert.Contains("Applied", await res.Content.ReadAsStringAsync());
        var after = await w.CustomerAsync(c.Id);
        Assert.Equal(SubscriptionStatus.Active, after.Subscriptions.Single().Status);
        var pay = await w.Db(db => db.Payments.IgnoreQueryFilters().SingleAsync(p => p.TenantId == w.TenantId)); Assert.Equal(VerificationSource.ProviderWebhook, pay.Source); Assert.Equal("evt_ok", pay.ProviderEventId);
        // redelivery (and a concurrent burst of redeliveries) is idempotent
        var again = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PostHookAsync(app, cfg.WebhookId, good, Secret)));
        Assert.All(again, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));
        Assert.Equal(1, await PaymentsAsync(w)); Assert.Single(await w.OutboxAsync("payment.confirmed"));
        // a different event id for an already-paid request is rejected
        await PostHookAsync(app, cfg.WebhookId, Body("evt_late", r.Reference, 500m), Secret);
        Assert.Equal(1, await PaymentsAsync(w));
        // inactive configuration: 404, nothing happens
        await ConfigureAsync(w, active: false);
        Assert.Equal(HttpStatusCode.NotFound, (await PostHookAsync(app, cfg.WebhookId, Body("evt_x", r.Reference, 500m), Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await PostHookAsync(app, cfg.WebhookId, new byte[300_000], Secret)).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task A_webhook_of_one_workspace_can_never_pay_another_workspaces_request()
    {
        await using var a = await ConvWorld.OpenAsync(app); await using var b = await ConvWorld.OpenAsync(app);
        var (ca, ra) = await a.NewRequestAsync();
        var cfgB = await ConfigureAsync(b);
        var res = await PostHookAsync(app, cfgB.WebhookId, Body("evt_x", ra.Reference, 500m), Secret);   // correct signature for B, but A's reference
        Assert.Equal(HttpStatusCode.OK, res.StatusCode); Assert.DoesNotContain("Applied", await res.Content.ReadAsStringAsync());
        Assert.Equal(SubscriptionStatus.Pending, await SubStatus(a, ca.Id)); Assert.Equal(0, await PaymentsAsync(a));
        Assert.Equal(PaymentRequestStatus.Open, (await Crm.Ok<PaymentRequestDto>(await a.Admin.Get($"/api/v1/payments/requests/{ra.Id}"))).Status);
        Assert.Equal(1, await b.Db(db => db.PaymentEvents.IgnoreQueryFilters().CountAsync(e => e.TenantId == b.TenantId && e.Outcome == PaymentEventOutcome.Rejected)));
    }
}
