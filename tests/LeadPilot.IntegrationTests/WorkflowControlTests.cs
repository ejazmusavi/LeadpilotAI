using System.Net;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>Cancellation, kill switches, limits, simulation, re-entry rules, inactivity and every trigger that has a real producer.</summary>
[Collection("app")]
public class WorkflowControlTests(AppFixture app)
{
    private static string Phone() => "+9230" + Random.Shared.Next(10_000_000, 99_999_999);
    private static Task<WorkflowDto> OnCreated(WorkflowWorld w, params object[] steps) => w.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created")], steps));
    private static async Task<ExecutionListItemDto> Single(WorkflowWorld w, WorkflowDto wf, Guid leadId) { await w.PumpAsync(); return (await w.ExecutionsAsync(wf.Id, leadId)).Single(); }
    private static IEnumerable<string?> Done(ExecutionDto e) => e.Logs.Where(l => l.Outcome == LogOutcome.Succeeded && l.StepKey is not null).Select(l => l.StepKey);

    [RequiresMySqlFact]
    public async Task A_running_workflow_can_be_cancelled_and_nothing_runs_afterwards()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wf = await OnCreated(w, Wf.Step("a", "AddTag", new { tag = "pre" }), Wf.Wait("pause", 60), Wf.Step("b", "AddTag", new { tag = "post-cancel" }));
        var lead = await Crm.CreateLead(w.Admin, phone: Phone());
        var ex = await Single(w, wf, lead.Id);
        await w.RunAsync(ex.Id);
        Assert.Equal(ExecutionStatus.Waiting, (await w.ExecutionAsync(ex.Id)).Summary.Status);

        var cancelled = await Crm.Ok<ExecutionDto>(await w.Admin.Post($"/api/v1/workflow-executions/{ex.Id}/cancel"));
        Assert.Equal(ExecutionStatus.Cancelled, cancelled.Summary.Status); Assert.False(cancelled.CanCancel);
        app.Clock.Offset += TimeSpan.FromMinutes(90);
        await w.RunAsync(ex.Id); await w.SweepAsync();
        var e = await w.ExecutionAsync(ex.Id);
        Assert.Equal(ExecutionStatus.Cancelled, e.Summary.Status); Assert.Equal(["a"], Done(e));
        Assert.DoesNotContain(await app.Db(db => db.LeadTags.IgnoreQueryFilters().Select(t => t.Name).ToListAsync()), n => n == "post-cancel");
        Assert.Equal(HttpStatusCode.Conflict, (await w.Admin.Post($"/api/v1/workflow-executions/{ex.Id}/cancel")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Admin.Post($"/api/v1/workflow-executions/{ex.Id}/retry")).StatusCode);

        // after the cancel the same lead may enter again (the slot was released)
        Assert.True((await w.Admin.Post($"/api/v1/workflows/{wf.Id}/run", new { leadId = lead.Id })).IsSuccessStatusCode);
    }

    [RequiresMySqlFact]
    public async Task The_workflow_kill_switch_and_the_workspace_kill_switch_stop_new_runs_and_running_ones()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wf = await OnCreated(w, Wf.Wait("pause", 30), Wf.Step("b", "AddTag", new { tag = "after-kill" }));
        var lead = await Crm.CreateLead(w.Admin, phone: Phone());
        var ex = await Single(w, wf, lead.Id);
        await w.RunAsync(ex.Id);

        await Crm.Ok<WorkflowDto>(await w.Admin.Put($"/api/v1/workflows/{wf.Id}/kill-switch", new { on = true, reason = "emergency" }));
        var lead2 = await Crm.CreateLead(w.Admin, phone: Phone()); await w.PumpAsync();
        Assert.Empty(await w.ExecutionsAsync(wf.Id, lead2.Id));                                  // nothing new starts
        Assert.Equal(HttpStatusCode.Conflict, (await w.Admin.Post($"/api/v1/workflows/{wf.Id}/run", new { leadId = lead2.Id })).StatusCode);
        app.Clock.Offset += TimeSpan.FromMinutes(40);
        await w.RunAsync(ex.Id);
        var e = await w.ExecutionAsync(ex.Id);
        Assert.Equal(ExecutionStatus.Cancelled, e.Summary.Status); Assert.Contains("kill switch", e.Summary.StatusReason);
        Assert.DoesNotContain(await app.Db(db => db.LeadTags.IgnoreQueryFilters().Select(t => t.Name).ToListAsync()), n => n == "after-kill");

        await Crm.Ok<WorkflowDto>(await w.Admin.Put($"/api/v1/workflows/{wf.Id}/kill-switch", new { on = false }));
        var lead3 = await Crm.CreateLead(w.Admin, phone: Phone()); await w.PumpAsync();
        Assert.Single(await w.ExecutionsAsync(wf.Id, lead3.Id));                                 // switched off again: runs start

        // workspace switch
        await w.SettingsAsync(kill: true);
        var lead4 = await Crm.CreateLead(w.Admin, phone: Phone()); await w.PumpAsync();
        Assert.Empty(await w.ExecutionsAsync(wf.Id, lead4.Id));
        var running = (await w.ExecutionsAsync(wf.Id, lead3.Id)).Single();
        await w.RunAsync(running.Id);
        Assert.Equal(ExecutionStatus.Cancelled, (await w.ExecutionAsync(running.Id)).Summary.Status);
        await w.SettingsAsync(kill: false);
    }

    [RequiresMySqlFact]
    public async Task Usage_limits_cap_monthly_runs_and_concurrent_runs()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        await w.SettingsAsync(kill: false, monthly: 1);
        var wf = await OnCreated(w, Wf.Step("a", "AddTag", new { tag = "m" }));
        var l1 = await Crm.CreateLead(w.Admin, phone: Phone()); var l2 = await Crm.CreateLead(w.Admin, phone: Phone());
        await w.PumpAsync();
        Assert.Single(await w.ExecutionsAsync(wf.Id));                                  // the second lead hit the monthly limit
        var settings = await Crm.Ok<WorkflowSettingsDto>(await w.Admin.Get("/api/v1/workflow-settings"));
        Assert.Equal(1, settings.ExecutionsThisMonth);

        await w.SettingsAsync(kill: false, concurrent: 1);
        var wf2 = await OnCreated(w, Wf.Wait("p", 30));
        var l3 = await Crm.CreateLead(w.Admin, phone: Phone()); var l4 = await Crm.CreateLead(w.Admin, phone: Phone()); await w.PumpAsync();
        var a = (await w.ExecutionsAsync(wf2.Id, l3.Id)).Single(); var b = (await w.ExecutionsAsync(wf2.Id, l4.Id)).Single();
        await app.Db(db => db.WorkflowExecutions.IgnoreQueryFilters().Where(e => e.Id == a.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExecutionStatus.Running).SetProperty(e => e.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(10) + app.Clock.Offset)));
        await w.RunAsync(b.Id);
        Assert.Equal(ExecutionStatus.Pending, (await w.ExecutionAsync(b.Id)).Summary.Status);   // deferred, not run
        Assert.Contains(app.Workflows.Jobs, j => j.ExecutionId == b.Id && j.Delay == TimeSpan.FromMinutes(1));
        await w.SettingsAsync(kill: false);
    }

    [RequiresMySqlFact]
    public async Task Re_entry_policy_cool_down_and_frequency_cap_are_enforced()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var once = await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("a", "AddTag", new { tag = "o" })], reEntry: "Once"));
        var lead = await Crm.CreateLead(w.Admin, phone: Phone());
        var ex = await Single(w, once, lead.Id);
        await w.RunToEndAsync(ex.Id);
        var again = await w.Admin.Post($"/api/v1/workflows/{once.Id}/run", new { leadId = lead.Id });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode); Assert.Contains("once", await Crm.ProblemDetail(again));

        var cool = await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("a", "AddTag", new { tag = "c" })], cooldownHours: 24));
        var ex2 = (await Crm.Ok<ExecutionDto>(await w.Admin.Post($"/api/v1/workflows/{cool.Id}/run", new { leadId = lead.Id }))).Summary;
        await w.RunToEndAsync(ex2.Id);
        Assert.Contains("Cool-down", await Crm.ProblemDetail(await w.Admin.Post($"/api/v1/workflows/{cool.Id}/run", new { leadId = lead.Id })));
        app.Clock.Offset += TimeSpan.FromHours(25);
        var ok = await Crm.Ok<ExecutionDto>(await w.Admin.Post($"/api/v1/workflows/{cool.Id}/run", new { leadId = lead.Id }));
        await w.RunToEndAsync(ok.Summary.Id);

        var cap = await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("a", "AddTag", new { tag = "f" })], maxStarts: 2));
        var ex3 = (await Crm.Ok<ExecutionDto>(await w.Admin.Post($"/api/v1/workflows/{cap.Id}/run", new { leadId = lead.Id }))).Summary; await w.RunToEndAsync(ex3.Id);
        var second = await Crm.Ok<ExecutionDto>(await w.Admin.Post($"/api/v1/workflows/{cap.Id}/run", new { leadId = lead.Id })); await w.RunToEndAsync(second.Summary.Id);
        Assert.Contains("Frequency cap", await Crm.ProblemDetail(await w.Admin.Post($"/api/v1/workflows/{cap.Id}/run", new { leadId = lead.Id })));

        // a run still in progress blocks the next one for the default AfterCompletion policy
        var live = await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Wait("p", 30)]));
        var first = await Crm.Ok<ExecutionDto>(await w.Admin.Post($"/api/v1/workflows/{live.Id}/run", new { leadId = lead.Id }));
        Assert.Contains("in progress", await Crm.ProblemDetail(await w.Admin.Post($"/api/v1/workflows/{live.Id}/run", new { leadId = lead.Id })));
    }

    [RequiresMySqlFact]
    public async Task Simulation_reports_what_would_happen_and_has_no_side_effects()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wf = await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created", Wf.Cmp("lead.city", "eq", "Lahore"))],
        [
            Wf.Step("tag", "AddTag", new { tag = "sim" }), Wf.Wait("pause", 60), Wf.Step("task", "CreateFollowUp", new { dueInHours = 5, note = "call", assignToOwner = false }),
            Wf.Step("note", "NotifyUser", new { target = w.AdminUser.Id.ToString(), title = "t", message = "m" }), w.SendStep(),
        ]));
        var ok = await w.ContactAsync(consent: true); var noConsent = await w.ContactAsync(consent: false);
        async Task<int[]> Counts() => await app.Db(async db => new[]
        {
            await db.Messages.IgnoreQueryFilters().CountAsync(), await db.WorkflowExecutions.IgnoreQueryFilters().CountAsync(), await db.WorkflowExecutionLogs.IgnoreQueryFilters().CountAsync(),
            await db.LeadTagMaps.IgnoreQueryFilters().CountAsync(), await db.FollowUps.IgnoreQueryFilters().CountAsync(), await db.WorkflowNotifications.IgnoreQueryFilters().CountAsync(),
            await db.LeadActivities.IgnoreQueryFilters().CountAsync(), await db.OutboxMessages.IgnoreQueryFilters().CountAsync(), await db.AuditLogs.IgnoreQueryFilters().CountAsync(),
        });
        await w.PumpAsync();                                                                                 // consume the events of the setup so only the simulation could change anything below
        foreach (var e in (await w.ExecutionsAsync())) await w.RunToEndAsync(e.Id);
        await w.SendQueuedAsync(ok); await w.SendQueuedAsync(noConsent);

        var before = await Counts(); var wahaCalls = app.Waha.Calls.Count; var jobs = app.Workflows.Jobs.Count; var sent = app.Waha.SentMessages.Count;
        var yes = await Crm.Ok<SimulationDto>(await w.Admin.Post($"/api/v1/workflows/{wf.Id}/simulate", new { leadId = ok.LeadId }));
        var no = await Crm.Ok<SimulationDto>(await w.Admin.Post($"/api/v1/workflows/{wf.Id}/simulate", new { leadId = noConsent.LeadId }));
        Assert.True(yes.Triggered); Assert.Equal(5, yes.Steps.Count); Assert.False(yes.SideEffects);
        Assert.Equal("would run", yes.Steps.Single(s => s.Key == "send").Outcome); Assert.Contains("eligible", yes.Steps.Single(s => s.Key == "send").Detail);
        Assert.Equal("blocked", no.Steps.Single(s => s.Key == "send").Outcome); Assert.Contains("consent", no.Steps.Single(s => s.Key == "send").Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("waits", yes.Steps.Single(s => s.Key == "pause").Outcome);
        Assert.Equal(before, await Counts());
        Assert.Equal(wahaCalls, app.Waha.Calls.Count); Assert.Equal(sent, app.Waha.SentMessages.Count); Assert.Equal(jobs, app.Workflows.Jobs.Count);

        // a lead the trigger condition rejects, and an unsaved definition
        var karachi = await Crm.CreateLead(w.Admin, city: "Karachi", phone: Phone());
        var skip = await Crm.Ok<SimulationDto>(await w.Admin.Post($"/api/v1/workflows/{wf.Id}/simulate", new { leadId = karachi.Id }));
        Assert.False(skip.Triggered); Assert.Empty(skip.Steps);
        var draft = await Crm.Ok<SimulationDto>(await w.Admin.Post($"/api/v1/workflows/{wf.Id}/simulate", new { leadId = ok.LeadId, definition = Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("x", "AddTag", new { tag = "draft-only" })]) }));
        Assert.Single(draft.Steps);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Admin.Post($"/api/v1/workflows/{wf.Id}/simulate", new { leadId = Guid.NewGuid() })).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Inactivity_is_evaluated_by_the_sweep_and_starts_each_idle_stretch_once()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wf = await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.inactive", inactiveDays: 7)], [Wf.Step("n", "AddTag", new { tag = "idle" })]));
        var idle = await Crm.CreateLead(w.Admin, phone: Phone()); var fresh = await Crm.CreateLead(w.Admin, phone: Phone());
        await app.Db(db => db.Leads.IgnoreQueryFilters().Where(l => l.Id == idle.Id).ExecuteUpdateAsync(s => s.SetProperty(l => l.LastActivityAtUtc, DateTime.UtcNow.AddDays(-10))));
        Assert.Equal(1, await w.InactivitySweepAsync());
        Assert.Equal(0, await w.InactivitySweepAsync());                                         // the same idle stretch never starts twice
        Assert.Single(await w.ExecutionsAsync(wf.Id, idle.Id)); Assert.Empty(await w.ExecutionsAsync(wf.Id, fresh.Id));
        Assert.Equal("lead.inactive", (await w.ExecutionsAsync(wf.Id, idle.Id)).Single().TriggerKey);
    }

    [RequiresMySqlFact]
    public async Task Incoming_messages_replies_stage_changes_and_consent_start_the_matching_workflows()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var incoming = await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("message.received", Wf.Cmp("message.text", "contains", "price"))], [Wf.Step("a", "AddTag", new { tag = "asked-price" })]));
        var replied = await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("customer.replied")], [Wf.Step("a", "AddTag", new { tag = "replied" })]));
        var interested = await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.interested")], [Wf.Step("a", "AddTag", new { tag = "interested" })]));
        var eligible = await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.contact_eligible")], [Wf.Step("a", "AddTag", new { tag = "eligible" })]));

        // a contact without consent: no "eligible" run; granting consent starts it
        var c = await w.ContactAsync(consent: false);
        await w.PumpAsync();
        Assert.Empty(await w.ExecutionsAsync(eligible.Id, c.LeadId));
        await MessagingKit.GrantConsentAsync(w.Admin, c.ContactId);
        await w.PumpAsync();
        Assert.Single(await w.ExecutionsAsync(eligible.Id, c.LeadId));

        // inbound "hello" matches neither the price condition nor "replied" (we never messaged them); "price?" matches the first
        await MessagingKit.InboundAsync(app, w.Worker, w.Account, w.Admin, c.Phone, "hello there");
        Assert.Empty(await w.ExecutionsAsync(incoming.Id, c.LeadId)); Assert.Empty(await w.ExecutionsAsync(replied.Id, c.LeadId));
        await MessagingKit.InboundAsync(app, w.Worker, w.Account, w.Admin, c.Phone, "what is the PRICE?");
        Assert.Single(await w.ExecutionsAsync(incoming.Id, c.LeadId));

        // once we have written to the contact, an answer is a "reply"
        var outbound = await Crm.Ok<MessageDto>(await w.Admin.Post("/api/v1/messages", new { contactId = c.ContactId, channelAccountId = w.Account.Id, text = "Thanks for your interest" }));
        await w.SendQueuedAsync(c);
        await MessagingKit.InboundAsync(app, w.Worker, w.Account, w.Admin, c.Phone, "ok please call");
        Assert.Single(await w.ExecutionsAsync(replied.Id, c.LeadId));

        // moving the lead to the stage "Interested"
        var pipeline = (await Crm.Ok<List<PipelineDto>>(await w.Admin.Get("/api/v1/pipelines"))).Single(p => p.IsDefault);
        Assert.True((await w.Admin.Put($"/api/v1/leads/{c.LeadId}/stage", new { stageId = pipeline.Stages.Single(s => s.Name == "Interested").Id })).IsSuccessStatusCode);
        await w.PumpAsync();
        Assert.Single(await w.ExecutionsAsync(interested.Id, c.LeadId));

        // import and discovery enrichment: "lead created" for imported rows, "lead enriched" for a merge that added data
        var token = Crm.Token();
        var created = await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("a", "AddTag", new { tag = "new" })]));
        var preview = await Crm.Upload(w.Admin, "wf.csv", Crm.CsvBytes("name,city,phone", $"{token} Academy,Lahore,{Phone()}"));
        await Crm.RunImport(w.Admin, preview);
        await w.PumpAsync();
        Assert.Single(await w.ExecutionsAsync(created.Id));
        var enriched = await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.enriched")], [Wf.Step("a", "AddTag", new { tag = "enriched" })]));
        await w.Worker.Scopes.RunAsync(w.TenantId, async (sp, ct) =>
        {
            var r = await sp.GetRequiredService<ILeadIntakeService>().IntakeAsync(new LeadIntakeRequest($"{token} Academy", null, null, null, "Lahore", null, null, null, "https://academy.example.org", 60, LeadSourceType.Discovery, "ext-" + token, "Places", null, null,
                [], null, true, DateTime.UtcNow), ct);
            Assert.True(r.IsSuccess); Assert.Equal(IntakeAction.Merged, r.Value.Action);
        });
        await w.PumpAsync();
        Assert.Single(await w.ExecutionsAsync(enriched.Id));
    }

    [RequiresMySqlFact]
    public async Task Phase_10_triggers_and_the_schedule_demo_step_are_available_and_the_step_refuses_activation_only_while_the_scheduler_cannot_answer()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var catalog = await Crm.Ok<WorkflowCatalogDto>(await w.Admin.Get("/api/v1/workflows/catalog"));
        foreach (var key in new[] { "demo.booked", "demo.completed", "trial.created", "trial.expires", "payment.confirmed" })
        { var t = catalog.Triggers.Single(x => x.Key == key); Assert.True(t.Available); Assert.True(t.HasProducer); Assert.Null(t.UnavailableReason); }
        Assert.All(catalog.Triggers, t => Assert.True(t.HasProducer));
        Assert.True(catalog.Actions.Single(a => a.Type == StepType.ScheduleDemo).Available);

        var wf = await w.CreateAsync(Wf.Definition([Wf.Trigger("demo.booked")], [Wf.Step("a", "AddTag", new { tag = "x" })]));
        Assert.DoesNotContain(wf.Draft!.Validation.Warnings, x => x.Message.Contains("Phase 10"));
        // this suite's default Phase 10 doubles answer "not connected": activation is refused honestly (the real service is covered by the conversion tests)
        var withDemo = await w.CreateAsync(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("demo", "ScheduleDemo")]));
        var refused = await w.Admin.Post($"/api/v1/workflows/{withDemo.Id}/versions/1/activate");
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode); Assert.Contains("demo slot", await Crm.ProblemDetail(refused));

        // ... and once the (test-only) Phase 10 scheduler exists, the step books the next free slot through the port
        app.Ai.Phase10.DemoConnected = true;
        try
        {
            app.Ai.Phase10.Slots.Add(new LeadPilot.Application.Agents.DemoSlot("slot-1", DateTime.UtcNow.AddDays(1) + app.Clock.Offset, DateTime.UtcNow.AddDays(1).AddMinutes(30) + app.Clock.Offset));
            await Crm.Ok<WorkflowDto>(await w.Admin.Post($"/api/v1/workflows/{withDemo.Id}/versions/1/activate"));
            var lead = await Crm.CreateLead(w.Admin, phone: Phone());
            var ex = await Single(w, withDemo, lead.Id);
            var e = await w.RunToEndAsync(ex.Id);
            Assert.Equal(ExecutionStatus.Completed, e.Summary.Status); Assert.Equal("bk-slot-1", e.Steps.Single().Effect);
        }
        finally { app.Ai.Phase10.Reset(); }
    }
}

