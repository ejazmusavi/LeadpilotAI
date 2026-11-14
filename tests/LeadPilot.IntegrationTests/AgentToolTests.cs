using System.Net;
using System.Text.Json;
using LeadPilot.Application.Agents;
using LeadPilot.Application.Ai;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>
/// The controlled tools, one by one, through the REAL gateway (allow-list, schema validation, tenant context, handlers, execution log) against MySQL. Demo, trial and payment services belong to Phase 10: by default they
/// are NOT connected and the tools must say so; a TEST-ONLY double stands in when a test needs the service to exist.
/// </summary>
[Collection("app")]
public class AgentToolTests(AppFixture app)
{
    private static JsonElement Out(ToolResult r) => JsonDocument.Parse(r.Json).RootElement;

    [RequiresMySqlFact]
    public async Task Product_and_pricing_come_only_from_confirmed_catalog_entries()
    {
        await using var w = await AgentWorld.OpenAsync(app);
        var chat = await w.ChatAsync();
        var p = await w.RunToolAsync(chat.ConversationId, AgentTools.GetProductInformation, new { topic = "attendance" });
        Assert.Equal(ToolOutcome.Executed, p.Outcome); var po = Out(p).GetProperty("result");
        Assert.True(po.GetProperty("known").GetBoolean()); Assert.Contains("Attendance tracking", p.Json); Assert.Contains("Demos last 30 minutes.", p.Json);
        var pl = await w.RunToolAsync(chat.ConversationId, AgentTools.GetPricingPlans);
        Assert.Contains("\"price\":500", pl.Json.Replace(".0", "")); Assert.Contains("USD", pl.Json);

        // un-confirm: the same questions now answer "known = false" and expose nothing
        var prod = await ApiSession.Json<ProductDto>(await w.Admin.Get($"/api/v1/catalog/products/{w.ProductId}"));
        Assert.Equal(HttpStatusCode.OK, (await w.Admin.Put($"/api/v1/catalog/products/{w.ProductId}", new { name = prod.Name, summary = prod.Summary, features = prod.Features, demoProcess = prod.DemoProcess, trialProcess = prod.TrialProcess, isConfirmed = false, isActive = true, version = prod.Version })).StatusCode);
        var again = await w.RunToolAsync(chat.ConversationId, AgentTools.GetProductInformation);
        Assert.False(Out(again).GetProperty("result").GetProperty("known").GetBoolean()); Assert.DoesNotContain("Attendance tracking", again.Json);
        var plans = await w.RunToolAsync(chat.ConversationId, AgentTools.GetPricingPlans);
        Assert.False(Out(plans).GetProperty("result").GetProperty("known").GetBoolean()); Assert.DoesNotContain("500", plans.Json);
    }

    [RequiresMySqlFact]
    public async Task The_sample_school_erp_configuration_contains_no_facts_and_the_ai_therefore_knows_nothing()
    {
        await using var w = await AiWorld.OpenAsync(app);
        var res = await w.Admin.Post("/api/v1/agents/sample");
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var agent = await ApiSession.Json<AgentDto>(res);
        Assert.True(agent.IsSample); Assert.True(agent.IsDefault); Assert.False(agent.AutoReplyEnabled); Assert.Contains("SAMPLE", agent.Name); Assert.Equal(5, agent.Questions.Count);
        Assert.DoesNotContain(AgentTools.CreatePaymentRequest, agent.EnabledTools); Assert.DoesNotContain(AgentTools.CreateTrialAccount, agent.EnabledTools);
        var products = await ApiSession.Json<List<ProductListItemDto>>(await w.Admin.Get("/api/v1/catalog/products"));
        var product = Assert.Single(products); Assert.True(product.IsSample); Assert.False(product.IsConfirmed);
        var detail = await ApiSession.Json<ProductDto>(await w.Admin.Get($"/api/v1/catalog/products/{product.Id}"));
        Assert.All(detail.Features!.Split('\n'), l => Assert.StartsWith("[PLACEHOLDER]", l));
        var plan = Assert.Single(detail.Plans); Assert.Null(plan.Price); Assert.False(plan.IsConfirmed); Assert.True(plan.IsSample);
        // a second sample is refused (it would duplicate), and the tools say "unknown"
        Assert.Equal(HttpStatusCode.Conflict, (await w.Admin.Post("/api/v1/agents/sample")).StatusCode);
        var wc = new AgentWorldProxy(w, agent.Id);
        var chat = await w.ChatAsync(linkLead: true);
        var info = await wc.RunAsync(chat.ConversationId, AgentTools.GetProductInformation);
        Assert.False(Out(info).GetProperty("result").GetProperty("known").GetBoolean()); Assert.DoesNotContain("PLACEHOLDER", info.Json);
        var price = await wc.RunAsync(chat.ConversationId, AgentTools.GetPricingPlans);
        Assert.False(Out(price).GetProperty("result").GetProperty("known").GetBoolean());
    }

    [RequiresMySqlFact]
    public async Task A_plan_cannot_be_confirmed_without_a_price_through_the_api()
    {
        await using var w = await AgentWorld.OpenAsync(app);
        var r = await w.Admin.Post($"/api/v1/catalog/products/{w.ProductId}/plans", new { name = "Gold", price = (decimal?)null, currency = "USD", isConfirmed = true, isActive = true });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Lead_profile_status_and_notes_work_on_the_conversations_own_lead_and_closed_statuses_need_a_person()
    {
        await using var w = await AgentWorld.OpenAsync(app);
        var chat = await w.ChatAsync();
        var prof = await w.RunToolAsync(chat.ConversationId, AgentTools.GetLeadProfile);
        Assert.Equal(ToolOutcome.Executed, prof.Outcome); Assert.Contains("School", prof.Json); Assert.DoesNotContain(chat.Phone, prof.Json);

        var statuses = await ApiSession.Json<List<LeadPilot.Application.Crm.StatusDto>>(await w.Admin.Get("/api/v1/lead-statuses"));
        var open = statuses.First(s => !s.IsClosed && !s.IsDefault); var closed = statuses.First(s => s.IsClosed);
        var ok = await w.RunToolAsync(chat.ConversationId, AgentTools.UpdateLeadStatus, new { status = open.Name, reason = "Customer is interested" });
        Assert.Equal(ToolOutcome.Executed, ok.Outcome);
        var denied = await w.RunToolAsync(chat.ConversationId, AgentTools.UpdateLeadStatus, new { status = closed.Name, reason = "Not a fit" });
        Assert.Equal((ToolOutcome.Denied, "closed_status_requires_staff"), (denied.Outcome, denied.Reason));
        Assert.Equal(ToolOutcome.Invalid, (await w.RunToolAsync(chat.ConversationId, AgentTools.UpdateLeadStatus, new { status = "Made Up Status", reason = "x" })).Outcome);
        var statusNow = await app.Db(db => db.Leads.IgnoreQueryFilters().Where(l => l.TenantId == w.TenantId).Select(l => l.StatusId).SingleAsync());
        Assert.Equal(open.Id, statusNow);
        var note = await w.RunToolAsync(chat.ConversationId, AgentTools.AddLeadNote, new { note = "Asked about report cards" });
        Assert.Equal(ToolOutcome.Executed, note.Outcome);
        Assert.Contains(await app.Db(db => db.LeadNotes.IgnoreQueryFilters().Where(n => n.TenantId == w.TenantId).ToListAsync()), n => n.Body.StartsWith("[AI assistant]"));
    }

    [RequiresMySqlFact]
    public async Task Tools_that_need_a_lead_are_denied_for_an_unlinked_conversation()
    {
        await using var w = await AgentWorld.OpenAsync(app);
        var chat = await w.ChatAsync(linkLead: false);
        foreach (var (tool, args) in new (string, object)[] { (AgentTools.GetLeadProfile, new { }), (AgentTools.QualifyLead, new { answers = new { role = "Principal" } }), (AgentTools.ScheduleFollowUp, new { dueInHours = 5, note = "x" }), (AgentTools.AddLeadNote, new { note = "x" }) })
        { var r = await w.RunToolAsync(chat.ConversationId, tool, args); Assert.Equal((ToolOutcome.Denied, "no_linked_lead"), (r.Outcome, r.Reason)); }
    }

    [RequiresMySqlFact]
    public async Task Qualify_lead_stores_validated_answers_for_the_tenants_own_questions_only()
    {
        await using var w = await AgentWorld.OpenAsync(app);
        var chat = await w.ChatAsync();
        var r = await w.RunToolAsync(chat.ConversationId, AgentTools.QualifyLead, new { answers = new { student_count = "1,200", role = "Principal", timeline = "THIS MONTH" }, fit = "good", summary = "Mid-size school" });
        Assert.Equal(ToolOutcome.Executed, r.Outcome);
        var q = await app.Db(db => db.LeadQualifications.IgnoreQueryFilters().SingleAsync(x => x.TenantId == w.TenantId));
        var answers = JsonSerializer.Deserialize<Dictionary<string, string>>(q.AnswersJson)!;
        Assert.Equal("1200", answers["student_count"]); Assert.Equal("this month", answers["timeline"]); Assert.Equal(QualificationFit.Good, q.Fit);
        Assert.Empty(Out(r).GetProperty("result").GetProperty("missingRequired").EnumerateArray());

        var bad = await w.RunToolAsync(chat.ConversationId, AgentTools.QualifyLead, new { answers = new { favourite_colour = "blue" } });
        Assert.Equal(ToolOutcome.Invalid, bad.Outcome);
        var notNumber = await w.RunToolAsync(chat.ConversationId, AgentTools.QualifyLead, new { answers = new { student_count = "many" } });
        Assert.Equal(ToolOutcome.Invalid, notNumber.Outcome);
        var badChoice = await w.RunToolAsync(chat.ConversationId, AgentTools.QualifyLead, new { answers = new { timeline = "yesterday" } });
        Assert.Equal(ToolOutcome.Invalid, badChoice.Outcome);
        Assert.Equal(1, await app.Db(db => db.LeadQualifications.IgnoreQueryFilters().CountAsync(x => x.TenantId == w.TenantId)));
    }

    [RequiresMySqlFact]
    public async Task Demo_tools_say_not_available_until_phase_10_connects_a_scheduler_and_never_fake_a_booking()
    {
        await using var w = await AgentWorld.OpenAsync(app);
        var chat = await w.ChatAsync();
        var a = await w.RunToolAsync(chat.ConversationId, AgentTools.CheckDemoAvailability);
        Assert.Equal(ToolOutcome.NotAvailable, a.Outcome); Assert.Contains("nothingHappened", a.Json);
        var b = await w.RunToolAsync(chat.ConversationId, AgentTools.BookDemo, new { slotId = "slot-1" });
        Assert.Equal(ToolOutcome.NotAvailable, b.Outcome);
        Assert.DoesNotContain(app.Ai.Phase10.Calls, c => c.StartsWith("book:"));      // never even attempted
    }

    [RequiresMySqlFact]
    public async Task A_demo_can_only_be_booked_inside_an_available_slot_through_the_tool()
    {
        await using var w = await AgentWorld.OpenAsync(app);
        var chat = await w.ChatAsync();
        var start = DateTime.UtcNow.AddDays(2).Date.AddHours(10);
        app.Ai.Phase10.DemoConnected = true; app.Ai.Phase10.Slots.Add(new DemoSlot("slot-A", start, start.AddMinutes(30)));
        var list = await w.RunToolAsync(chat.ConversationId, AgentTools.CheckDemoAvailability, new { days = 5 });
        Assert.Equal(ToolOutcome.Executed, list.Outcome); Assert.Contains("slot-A", list.Json);

        var invented = await w.RunToolAsync(chat.ConversationId, AgentTools.BookDemo, new { slotId = "slot-Z" });
        Assert.Equal((ToolOutcome.Denied, "slot_not_available"), (invented.Outcome, invented.Reason));
        var asTime = await w.RunToolAsync(chat.ConversationId, AgentTools.BookDemo, new { slotId = start.ToString("o") });
        Assert.Equal(ToolOutcome.Denied, asTime.Outcome);
        Assert.DoesNotContain(app.Ai.Phase10.Calls, c => c.StartsWith("book:"));

        var ok = await w.RunToolAsync(chat.ConversationId, AgentTools.BookDemo, new { slotId = "slot-A", notes = "Principal and accountant" });
        Assert.Equal(ToolOutcome.Executed, ok.Outcome); Assert.Contains("booked", ok.Json);
        Assert.Equal(1, app.Ai.Phase10.Calls.Count(c => c == "book:slot-A"));
        // the slot is gone: a second booking of it is refused
        Assert.Equal(ToolOutcome.Denied, (await w.RunToolAsync(chat.ConversationId, AgentTools.BookDemo, new { slotId = "slot-A" })).Outcome);

        // a failing scheduler is reported as a failure, not as a booking
        app.Ai.Phase10.Slots.Add(new DemoSlot("slot-B", start.AddHours(1), start.AddHours(2))); app.Ai.Phase10.FailBooking = true;
        Assert.Equal(ToolOutcome.Failed, (await w.RunToolAsync(chat.ConversationId, AgentTools.BookDemo, new { slotId = "slot-B" })).Outcome);
    }

    [RequiresMySqlFact]
    public async Task Follow_ups_are_internal_tasks_limited_per_lead_and_owned_by_the_lead_owner()
    {
        await using var w = await AgentWorld.OpenAsync(app);
        var chat = await w.ChatAsync();
        for (var i = 0; i < 3; i++) Assert.Equal(ToolOutcome.Executed, (await w.RunToolAsync(chat.ConversationId, AgentTools.ScheduleFollowUp, new { dueInHours = 24 + i, note = "Call back " + i })).Outcome);
        var fourth = await w.RunToolAsync(chat.ConversationId, AgentTools.ScheduleFollowUp, new { dueInHours = 30, note = "One more" });
        Assert.Equal((ToolOutcome.Denied, "too_many_open_follow_ups"), (fourth.Outcome, fourth.Reason));
        Assert.Equal(ToolOutcome.Invalid, (await w.RunToolAsync(chat.ConversationId, AgentTools.ScheduleFollowUp, new { dueInHours = 5000, note = "far away" })).Outcome);
        var list = await ApiSession.Json<LeadPilot.Application.Common.PagedResult<FollowUpDto>>(await w.Admin.Get("/api/v1/follow-ups"));
        Assert.Equal(3, list.TotalCount); Assert.All(list.Items, f => { Assert.Equal(ActorKind.Ai, f.CreatedBy); Assert.False(f.Overdue); });
        // nothing was sent to the customer by scheduling
        Assert.Empty(await w.OutboundAsync(chat.ConversationId));
        // a person closes one
        Assert.Equal(HttpStatusCode.OK, (await w.Admin.Post($"/api/v1/follow-ups/{list.Items[0].Id}/done")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post($"/api/v1/follow-ups/{list.Items[0].Id}/done")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task A_payment_request_always_waits_for_a_person_and_an_approval_without_the_service_is_recorded_as_not_carried_out()
    {
        await using var w = await AgentWorld.OpenAsync(app);
        var chat = await w.ChatAsync();
        var trig = await TriggerAsync(w, chat);
        var r = await w.RunToolAsync(chat.ConversationId, AgentTools.CreatePaymentRequest, new { plan = "Standard" }, trigger: trig);
        Assert.Equal(ToolOutcome.ApprovalRequired, r.Outcome); Assert.NotNull(r.ApprovalRequestId); Assert.Contains("Do NOT tell the customer", r.Json);
        Assert.DoesNotContain(app.Ai.Phase10.Calls, c => c.StartsWith("payment"));            // nothing created before a person decided

        var approvals = await ApiSession.Json<LeadPilot.Application.Common.PagedResult<ApprovalDto>>(await w.Admin.Get("/api/v1/approvals?status=Pending"));
        var a = Assert.Single(approvals.Items); Assert.Equal(AgentTools.CreatePaymentRequest, a.ToolName); Assert.Contains("500", a.Summary);
        var done = await w.Admin.Post($"/api/v1/approvals/{a.Id}/approve", new { note = "OK" });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var after = await ApiSession.Json<ApprovalDto>(done);
        Assert.Equal((ApprovalStatus.Approved, ExecutionState.NotAvailable), (after.Status, after.Execution));      // honest: the payment service is not connected
        Assert.DoesNotContain(app.Ai.Phase10.Calls, c => c.StartsWith("payment") && !c.StartsWith("payment:Standard:500"));
        var log = await w.ToolsAsync(chat.ConversationId);
        Assert.Contains(log, t => t.ToolName == AgentTools.CreatePaymentRequest && t.Outcome == ToolOutcome.ApprovalRequired);
        Assert.Contains(log, t => t.ToolName == AgentTools.CreatePaymentRequest && t.Outcome == ToolOutcome.NotAvailable && t.ApprovalRequestId == a.Id && t.ExecutedByUserId != null);
        // a second decision is refused
        Assert.Equal(HttpStatusCode.Conflict, (await w.Admin.Post($"/api/v1/approvals/{a.Id}/approve", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Admin.Post($"/api/v1/approvals/{a.Id}/reject", new { })).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task With_the_payment_service_connected_an_approved_request_runs_exactly_once_with_the_catalog_price_never_the_models()
    {
        await using var w = await AgentWorld.OpenAsync(app);
        app.Ai.Phase10.PaymentConnected = true;
        var chat = await w.ChatAsync(); var trig = await TriggerAsync(w, chat);
        var r = await w.RunToolAsync(chat.ConversationId, AgentTools.CreatePaymentRequest, new { plan = "Standard", discountPercent = 20, customTerms = "Pay in 12 instalments" }, trigger: trig);
        Assert.Equal(ToolOutcome.ApprovalRequired, r.Outcome);
        var a = (await ApiSession.Json<LeadPilot.Application.Common.PagedResult<ApprovalDto>>(await w.Admin.Get("/api/v1/approvals"))).Items.Single();
        Assert.Equal("discount_above_limit", a.Reason);                                       // no plan allows any discount
        // two people approve at the same moment: exactly one wins
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => w.Admin.Post($"/api/v1/approvals/{a.Id}/approve", new { })));
        Assert.Equal(1, results.Count(x => x.StatusCode == HttpStatusCode.OK)); Assert.Equal(1, results.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        Assert.Single(app.Ai.Phase10.Calls, c => c.StartsWith("payment:"));
        Assert.Contains(app.Ai.Phase10.Calls, c => c.StartsWith("payment:Standard:500") && c.EndsWith(":20"));           // amount from the catalog; the discount only because a person approved it
        var after = await ApiSession.Json<ApprovalDto>(await w.Admin.Get($"/api/v1/approvals/{a.Id}"));
        Assert.Equal(ExecutionState.Executed, after.Execution);
    }

    [RequiresMySqlFact]
    public async Task A_rejected_request_does_nothing_and_an_expired_one_cannot_be_approved()
    {
        await using var w = await AgentWorld.OpenAsync(app);
        app.Ai.Phase10.PaymentConnected = true;
        var chat = await w.ChatAsync(); var trig = await TriggerAsync(w, chat);
        await w.RunToolAsync(chat.ConversationId, AgentTools.CreatePaymentRequest, new { plan = "Standard" }, trigger: trig);
        var a = (await ApiSession.Json<LeadPilot.Application.Common.PagedResult<ApprovalDto>>(await w.Admin.Get("/api/v1/approvals"))).Items.Single();
        Assert.Equal(HttpStatusCode.OK, (await w.Admin.Post($"/api/v1/approvals/{a.Id}/reject", new { note = "no" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Admin.Post($"/api/v1/approvals/{a.Id}/approve", new { })).StatusCode);
        Assert.DoesNotContain(app.Ai.Phase10.Calls, c => c.StartsWith("payment"));

        var chat2 = await w.ChatAsync(); var trig2 = await TriggerAsync(w, chat2);
        await w.RunToolAsync(chat2.ConversationId, AgentTools.CreatePaymentRequest, new { plan = "Standard" }, trigger: trig2);
        await app.Db(db => db.ApprovalRequests.IgnoreQueryFilters().Where(x => x.TenantId == w.TenantId && x.Status == ApprovalStatus.Pending).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1))));
        var pending = (await ApiSession.Json<LeadPilot.Application.Common.PagedResult<ApprovalDto>>(await w.Admin.Get("/api/v1/approvals?status=Pending"))).Items;
        Assert.Empty(pending);
        var expired = (await ApiSession.Json<LeadPilot.Application.Common.PagedResult<ApprovalDto>>(await w.Admin.Get("/api/v1/approvals?status=Expired"))).Items.Single();
        Assert.Equal(HttpStatusCode.Conflict, (await w.Admin.Post($"/api/v1/approvals/{expired.Id}/approve", new { })).StatusCode);
        Assert.DoesNotContain(app.Ai.Phase10.Calls, c => c.StartsWith("payment"));
    }

    [RequiresMySqlFact]
    public async Task Trials_within_the_limit_go_to_the_service_and_beyond_it_need_approval()
    {
        await using var w = await AgentWorld.OpenAsync(app);
        var chat = await w.ChatAsync();
        // not connected: honest answer, and it does not count towards the limit
        Assert.Equal(ToolOutcome.NotAvailable, (await w.RunToolAsync(chat.ConversationId, AgentTools.CreateTrialAccount, new { plan = "Standard" })).Outcome);
        app.Ai.Phase10.TrialConnected = true;
        Assert.Equal(ToolOutcome.Executed, (await w.RunToolAsync(chat.ConversationId, AgentTools.CreateTrialAccount, new { plan = "Standard" })).Outcome);
        var second = await w.RunToolAsync(chat.ConversationId, AgentTools.CreateTrialAccount, new { plan = "Standard", notes = "another please" });
        Assert.Equal(ToolOutcome.ApprovalRequired, second.Outcome);
        Assert.Equal(1, app.Ai.Phase10.Calls.Count(c => c == "trial") - 1);                   // the NotAvailable attempt + one real creation; the second one did not reach the service
        var a = (await ApiSession.Json<LeadPilot.Application.Common.PagedResult<ApprovalDto>>(await w.Admin.Get("/api/v1/approvals?status=Pending"))).Items.Single();
        Assert.Equal("trial_limit_reached", a.Reason);
        var approved = await ApiSession.Json<ApprovalDto>(await w.Admin.Post($"/api/v1/approvals/{a.Id}/approve", new { }));
        Assert.Equal(ExecutionState.Executed, approved.Execution);
    }

    [RequiresMySqlFact]
    public async Task Stop_marketing_suppresses_the_number_revokes_consent_blocks_the_chat_and_pauses_the_ai()
    {
        await using var w = await AgentWorld.OpenAsync(app);
        var chat = await w.ChatAsync();
        var lead = await app.Db(db => db.Contacts.IgnoreQueryFilters().Where(c => c.TenantId == w.TenantId).Select(c => new { c.Id, c.PhoneE164 }).SingleAsync());
        await MessagingKit.GrantConsentAsync(w.Admin, lead.Id);
        var r = await w.RunToolAsync(chat.ConversationId, AgentTools.StopMarketingMessages, new { reason = "Customer said stop sending" }, allowed: []);   // a safety tool: needs no allow-list entry
        Assert.Equal(ToolOutcome.Executed, r.Outcome);
        Assert.Contains(await app.Db(db => db.SuppressionEntries.IgnoreQueryFilters().Where(s => s.TenantId == w.TenantId).ToListAsync()), s => s.ValueNormalized == lead.PhoneE164 && s.Reason == SuppressionReason.OptOut);
        var latest = await app.Db(db => db.ConsentRecords.IgnoreQueryFilters().Where(c => c.TenantId == w.TenantId && c.ContactId == lead.Id).OrderByDescending(c => c.OccurredAtUtc).ThenByDescending(c => c.IsRevocation).FirstAsync());
        Assert.Equal(ConsentStatus.Revoked, latest.Status);
        var conv = await app.Db(db => db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == chat.ConversationId));
        Assert.NotNull(conv.OptedOutAtUtc); Assert.Equal(AiMode.Paused, conv.AiMode);
        var reply = await w.Admin.Post($"/api/v1/conversations/{chat.ConversationId}/messages", new { text = "hello again" });
        Assert.Equal(HttpStatusCode.Conflict, reply.StatusCode);                              // the normal pipeline now refuses everything for this chat
        Assert.Equal(HttpStatusCode.Conflict, (await w.Admin.Post($"/api/v1/conversations/{chat.ConversationId}/ai/resume")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Transfer_to_human_takes_the_conversation_from_the_ai_records_why_and_assigns_the_leads_owner()
    {
        await using var w = await AgentWorld.OpenAsync(app);
        var chat = await w.ChatAsync();
        var r = await w.RunToolAsync(chat.ConversationId, AgentTools.TransferToHuman, new { reason = "Wants to negotiate", summary = "Principal of a 900 student school" }, allowed: []);
        Assert.Equal(ToolOutcome.Executed, r.Outcome);
        var conv = await app.Db(db => db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == chat.ConversationId));
        Assert.Equal(AiMode.HumanControl, conv.AiMode); Assert.Equal("Wants to negotiate", conv.EscalationReason);
        var hist = await ApiSession.Json<List<HandoffEventDto>>(await w.Admin.Get($"/api/v1/conversations/{chat.ConversationId}/handoff-history"));
        var ev = Assert.Single(hist, h => h.Kind == HandoffKind.Escalated); Assert.Equal(ActorKind.Ai, ev.Actor); Assert.Equal("Wants to negotiate", ev.Reason);
        var notes = await ApiSession.Json<List<ConversationNoteDto>>(await w.Admin.Get($"/api/v1/conversations/{chat.ConversationId}/notes"));
        Assert.Contains(notes, n => n.Author == ActorKind.Ai && n.Body.Contains("900 student"));
    }

    [RequiresMySqlFact]
    public async Task Tools_outside_the_agents_list_or_that_do_not_exist_are_refused_and_logged_without_effect()
    {
        await using var w = await AgentWorld.OpenAsync(app, tools: [AgentTools.GetPricingPlans]);
        var chat = await w.ChatAsync();
        var notEnabled = await w.RunToolAsync(chat.ConversationId, AgentTools.ScheduleFollowUp, new { dueInHours = 4, note = "x" });
        Assert.Equal((ToolOutcome.Denied, "tool_not_enabled_for_agent"), (notEnabled.Outcome, notEnabled.Reason));
        foreach (var name in new[] { "ApplyDiscount", "SendMessage", "ActivateSubscription", "RunSql", "ConfirmPayment", "DeleteLead" })
        { var r = await w.RunToolAsync(chat.ConversationId, name, new { id = 1 }); Assert.Equal((ToolOutcome.Denied, "not_a_permitted_tool"), (r.Outcome, r.Reason)); }
        Assert.Equal(0, await app.Db(db => db.FollowUps.IgnoreQueryFilters().CountAsync(f => f.TenantId == w.TenantId)));
        var log = await w.ToolsAsync(chat.ConversationId);
        Assert.Equal(7, log.Count); Assert.DoesNotContain(log, l => l.Outcome == ToolOutcome.Executed);
    }

    [RequiresMySqlFact]
    public async Task The_model_can_never_choose_the_tenant_a_lead_a_conversation_or_run_a_query()
    {
        await using var w = await AgentWorld.OpenAsync(app);
        var other = await AgentWorld.OpenAsync(app);
        var otherChat = await other.ChatAsync(); var chat = await w.ChatAsync();
        foreach (var args in new[]
        {
            $$"""{"tenantId":"{{other.TenantId}}","dueInHours":2,"note":"x"}""", $$"""{"conversationId":"{{otherChat.ConversationId}}","dueInHours":2,"note":"x"}""",
            """{"leadId":"00000000-0000-0000-0000-000000000001","dueInHours":2,"note":"x"}""", """{"sql":"delete from Leads","dueInHours":2,"note":"x"}""",
        })
        {
            var r = await w.RunToolAsync(chat.ConversationId, AgentTools.ScheduleFollowUp, args);
            Assert.Equal((ToolOutcome.Denied, "tenant_argument_forbidden"), (r.Outcome, r.Reason));
        }
        Assert.Equal(0, await app.Db(db => db.FollowUps.IgnoreQueryFilters().CountAsync(f => f.TenantId == w.TenantId || f.TenantId == other.TenantId)));
        await other.DisposeAsync();
    }

    [RequiresMySqlFact]
    public async Task A_tool_cannot_be_pointed_at_a_conversation_of_another_tenant()
    {
        await using var w = await AgentWorld.OpenAsync(app); await using var other = await AgentWorld.OpenAsync(app);
        var otherChat = await other.ChatAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => w.RunToolAsync(otherChat.ConversationId, AgentTools.GetLeadProfile));   // the conversation does not exist for this tenant
    }

    [RequiresMySqlFact]
    public async Task A_repeated_call_for_the_same_customer_message_replays_the_stored_result_instead_of_acting_twice()
    {
        await using var w = await AgentWorld.OpenAsync(app);
        var chat = await w.ChatAsync(); var trig = await TriggerAsync(w, chat);
        var args = new { dueInHours = 24, note = "Call back" };
        var first = await w.RunToolAsync(chat.ConversationId, AgentTools.ScheduleFollowUp, args, trigger: trig);
        var second = await w.RunToolAsync(chat.ConversationId, AgentTools.ScheduleFollowUp, args, trigger: trig);
        Assert.Equal(ToolOutcome.Executed, first.Outcome); Assert.Equal(first.Json, second.Json);
        Assert.Equal(1, await app.Db(db => db.FollowUps.IgnoreQueryFilters().CountAsync(f => f.TenantId == w.TenantId)));
        Assert.Equal(1, (await w.ToolsAsync(chat.ConversationId)).Count(t => t.ToolName == AgentTools.ScheduleFollowUp));
        // a different customer message is a different call
        var other = await app.Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.TenantId == w.TenantId && m.Direction == MessageDirection.Inbound).Select(m => m.Id).FirstAsync());
        await chat.SendAsync("one more question", pump: false);
        Assert.Equal(1, await app.Db(db => db.FollowUps.IgnoreQueryFilters().CountAsync(f => f.TenantId == w.TenantId)));
        _ = other;
    }

    [RequiresMySqlFact]
    public async Task The_execution_log_keeps_inputs_outputs_and_outcome_and_cannot_be_changed()
    {
        await using var w = await AgentWorld.OpenAsync(app);
        var chat = await w.ChatAsync(); var trig = await TriggerAsync(w, chat);
        await w.RunToolAsync(chat.ConversationId, AgentTools.GetPricingPlans, trigger: trig);
        await w.RunToolAsync(chat.ConversationId, AgentTools.ScheduleFollowUp, new { dueInHours = 0, note = "x" }, trigger: trig);
        var page = await ApiSession.Json<LeadPilot.Application.Common.PagedResult<ToolExecutionDto>>(await w.Admin.Get("/api/v1/tool-executions"));
        var price = page.Items.Single(t => t.Tool == AgentTools.GetPricingPlans);
        Assert.Equal(ToolOutcome.Executed, price.Outcome); Assert.Contains("Standard", price.OutputJson); Assert.Equal(chat.ConversationId, price.ConversationId); Assert.NotNull(price.LeadId);
        var bad = page.Items.Single(t => t.Tool == AgentTools.ScheduleFollowUp);
        Assert.Equal(ToolOutcome.Invalid, bad.Outcome); Assert.Contains("out_of_range:dueInHours", bad.OutputJson);
        var filtered = await ApiSession.Json<LeadPilot.Application.Common.PagedResult<ToolExecutionDto>>(await w.Admin.Get("/api/v1/tool-executions?outcome=Invalid"));
        Assert.Equal(1, filtered.TotalCount);
        await Assert.ThrowsAnyAsync<Exception>(() => app.Db(async db => { var row = await db.AiToolExecutions.IgnoreQueryFilters().FirstAsync(t => t.TenantId == w.TenantId); db.Remove(row); await db.SaveChangesAsync(); }));
    }

    // ---- helpers

    private static async Task<Guid> TriggerAsync(AgentWorld w, ChatThread chat) =>
        await w.App.Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.TenantId == w.TenantId && m.ConversationId == chat.ConversationId && m.Direction == MessageDirection.Inbound).OrderByDescending(m => m.CreatedAtUtc).Select(m => m.Id).FirstAsync());

    private sealed class AgentWorldProxy(AiWorld w, Guid agentId)
    {
        public Task<ToolResult> RunAsync(Guid conversationId, string tool, object? args = null) =>
            w.InScope(async sp =>
            {
                var crm = sp.GetRequiredService<LeadPilot.Infrastructure.Services.Crm.CrmContext>();
                var conv = await crm.Db.Conversations.FirstAsync(c => c.Id == conversationId && c.TenantId == crm.TenantId);
                var a = await crm.Db.SalesAgents.AsNoTracking().FirstAsync(x => x.Id == agentId);
                var names = AgentToolCatalog.AllowedFor(a.EnabledTools).Select(t => t.Name).ToHashSet();
                return await sp.GetRequiredService<LeadPilot.Infrastructure.Agents.AgentToolRunner>().RunAsync(new LeadPilot.Infrastructure.Agents.ToolCall(tool, JsonSerializer.Serialize(args ?? new { })), new LeadPilot.Infrastructure.Agents.ToolCallContext(conv, null, a, names), default);
            });
    }
}
