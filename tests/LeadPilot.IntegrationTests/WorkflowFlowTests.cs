using System.Net;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Workflows;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>Creating, activating, triggering and running workflows through the real API, outbox, consumers and engine (fake WAHA only at the far end).</summary>
[Collection("app")]
public class WorkflowFlowTests(AppFixture app)
{
    [RequiresMySqlFact]
    public async Task A_workflow_can_be_created_and_activated_and_an_active_version_never_changes()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wf = await w.CreateAsync(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("tag", "AddTag", new { tag = "vip" })]));
        Assert.Equal(WorkflowStatus.Draft, wf.Status);
        Assert.Equal(VersionState.Draft, wf.Draft!.State);
        Assert.True(wf.Draft.Validation.IsValid);

        var active = await Crm.Ok<WorkflowDto>(await w.Admin.Post($"/api/v1/workflows/{wf.Id}/versions/1/activate"));
        Assert.Equal(WorkflowStatus.Active, active.Status); Assert.Equal(1, active.ActiveVersion); Assert.Null(active.Draft);
        Assert.Equal(VersionState.Active, active.Active!.State);

        // "editing" an active workflow creates version 2 as a draft; version 1 is untouched
        var v2 = await Crm.Ok<WorkflowVersionDto>(await w.Admin.Put($"/api/v1/workflows/{wf.Id}/draft", new { definition = Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("tag", "AddTag", new { tag = "gold" }), Wf.Step("tag2", "AddTag", new { tag = "x" })]) }));
        Assert.Equal(2, v2.Number); Assert.Equal(VersionState.Draft, v2.State);
        var v1 = await Crm.Ok<WorkflowVersionDto>(await w.Admin.Get($"/api/v1/workflows/{wf.Id}/versions/1"));
        Assert.Single(v1.Steps); Assert.Equal(VersionState.Active, v1.State);

        // the database itself refuses to change a sealed row
        await w.Worker.Scopes.RunAsync(w.TenantId, async (sp, ct) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var step = await db.WorkflowSteps.FirstAsync(s => s.WorkflowVersionId == v1.Id, ct);
            db.Entry(step).Property(s => s.Name).CurrentValue = "hacked";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(ct));
            db.ChangeTracker.Clear();
            db.WorkflowSteps.Remove(await db.WorkflowSteps.FirstAsync(s => s.WorkflowVersionId == v1.Id, ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(ct));
        });

        // activating v2 retires v1 and a second draft cannot be started while one exists
        var again = await Crm.Ok<WorkflowDto>(await w.Admin.Post($"/api/v1/workflows/{wf.Id}/versions/2/activate"));
        Assert.Equal(2, again.ActiveVersion); Assert.Contains(again.Versions, v => v.Number == 1 && v.State == VersionState.Retired);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Admin.Post($"/api/v1/workflows/{wf.Id}/versions/1/activate")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Trigger_conditions_decide_which_leads_start_a_run_and_steps_run_in_the_intended_order()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wf = await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created", Wf.Cmp("lead.city", "eq", "Lahore"))],
        [
            Wf.Step("tag", "AddTag", new { tag = "vip" }), Wf.Step("status", "ChangeLeadStatus", new { status = "Working" }),
            Wf.Step("task", "CreateFollowUp", new { dueInHours = 24, note = "Call {{first_name|them}}", assignToOwner = false }),
            Wf.Step("note", "NotifyUser", new { target = w.AdminUser.Id.ToString(), title = "New lead {{name}}", message = "Please look at {{business_name}} in {{city|town}}." }),
            Wf.Step("city", "UpdateLeadFields", new { field = "district", value = "Gulberg" }),
        ]));

        var lahore = await Crm.CreateLead(w.Admin, city: "Lahore", phone: "+9230" + Random.Shared.Next(10_000_000, 99_999_999));
        var karachi = await Crm.CreateLead(w.Admin, city: "Karachi", phone: "+9230" + Random.Shared.Next(10_000_000, 99_999_999));
        await w.PumpAsync();

        Assert.Empty(await w.ExecutionsAsync(wf.Id, karachi.Id));                   // the condition kept the other lead out
        var exec = (await w.ExecutionsAsync(wf.Id, lahore.Id)).Single();
        Assert.Equal(ExecutionStatus.Pending, exec.Status);
        Assert.Contains(app.Workflows.Jobs, j => j.ExecutionId == exec.Id);

        var done = await w.RunToEndAsync(exec.Id);
        Assert.Equal(ExecutionStatus.Completed, done.Summary.Status);
        var ran = done.Logs.Where(l => l.StepKey is not null && l.Outcome == LogOutcome.Succeeded).Select(l => l.StepKey).ToList();
        Assert.Equal(["tag", "status", "task", "note", "city"], ran);                // exactly once each, in order
        Assert.All(done.Steps, s => Assert.Equal("done", s.Status));

        await app.Db(async db =>
        {
            Assert.True(await db.LeadTagMaps.IgnoreQueryFilters().AnyAsync(m => m.LeadId == lahore.Id));
            var lead = await db.Leads.IgnoreQueryFilters().FirstAsync(l => l.Id == lahore.Id);
            Assert.Equal("Gulberg", lead.District);
            var status = await db.LeadStatuses.IgnoreQueryFilters().FirstAsync(s => s.Id == lead.StatusId);
            Assert.Equal("Working", status.Name);
            var task = await db.FollowUps.IgnoreQueryFilters().SingleAsync(f => f.LeadId == lahore.Id);
            Assert.StartsWith("Call ", task.Note);
            var note = await db.WorkflowNotifications.IgnoreQueryFilters().SingleAsync(n => n.LeadId == lahore.Id);
            Assert.Equal(w.AdminUser.Id, note.UserId); Assert.Contains("in Lahore", note.Body);
        });

        // the second delivery of the whole run changes nothing
        await w.RunAsync(exec.Id);
        Assert.Equal(1, await app.Db(db => db.FollowUps.IgnoreQueryFilters().CountAsync(f => f.LeadId == lahore.Id)));
    }

    [RequiresMySqlFact]
    public async Task Waits_delay_the_run_through_the_scheduler_and_resume_when_due()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wf = await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("a", "AddTag", new { tag = "before" }), Wf.Wait("pause", 120), Wf.Step("b", "AddTag", new { tag = "after" })]));
        var lead = await Crm.CreateLead(w.Admin, phone: "+9230" + Random.Shared.Next(10_000_000, 99_999_999));
        await w.PumpAsync();
        var exec = (await w.ExecutionsAsync(wf.Id, lead.Id)).Single();

        await w.RunAsync(exec.Id);
        var waiting = await w.ExecutionAsync(exec.Id);
        Assert.Equal(ExecutionStatus.Waiting, waiting.Summary.Status);
        Assert.Equal("pause", waiting.Summary.CurrentStepKey);
        Assert.Contains(app.Workflows.Jobs, j => j.ExecutionId == exec.Id && j.Delay is { } d && d > TimeSpan.FromMinutes(100));   // Hangfire is asked to come back in ~2 h
        Assert.Equal(1, waiting.Logs.Count(l => l.Outcome == LogOutcome.Succeeded && l.StepKey == "a"));

        await w.RunAsync(exec.Id);                                                    // an early / duplicate job does nothing
        Assert.Equal(ExecutionStatus.Waiting, (await w.ExecutionAsync(exec.Id)).Summary.Status);
        Assert.DoesNotContain(await app.Db(db => db.LeadTags.IgnoreQueryFilters().Select(t => t.Name).ToListAsync()), n => n == "after");

        app.Clock.Offset += TimeSpan.FromMinutes(125);
        await w.RunAsync(exec.Id);
        var done = await w.ExecutionAsync(exec.Id);
        Assert.Equal(ExecutionStatus.Completed, done.Summary.Status);
        Assert.Equal(["a", "pause", "b"], done.Logs.Where(l => l.Outcome == LogOutcome.Succeeded && l.StepKey is not null).Select(l => l.StepKey));
    }

    [RequiresMySqlFact]
    public async Task Branches_follow_conditions_and_loops_or_unknown_things_are_rejected_when_saving()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var branch = Wf.Step("check", "Branch", new { condition = Wf.Cmp("lead.score", "gte", 1000), then = "hot", @else = "cold" });
        var wf = await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created")],
            [branch, Wf.Step("hot", "AddTag", new { tag = "hot" }), Wf.Step("stop1", "Stop"), Wf.Step("cold", "AddTag", new { tag = "cold" })]));
        var lead = await Crm.CreateLead(w.Admin, phone: "+9230" + Random.Shared.Next(10_000_000, 99_999_999));
        await w.PumpAsync();
        var e = await w.RunToEndAsync((await w.ExecutionsAsync(wf.Id, lead.Id)).Single().Id);
        Assert.Equal(ExecutionStatus.Completed, e.Summary.Status);
        Assert.Contains(e.Steps, s => s.Key == "cold" && s.Status == "done"); Assert.Contains(e.Steps, s => s.Key == "hot" && s.Status == "pending");

        async Task<string> Rejected(object def) { var r = await w.Admin.Post("/api/v1/workflows", new { name = "bad", definition = def }); Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode); return await Crm.ProblemDetail(r); }
        Assert.Contains("loop", await Rejected(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("a", "Branch", new { condition = Wf.Cmp("lead.score", "gt", 1), then = "b", @else = "b" }), Wf.Step("b", "Branch", new { condition = Wf.Cmp("lead.score", "gt", 1), then = "a", @else = "end" })])));
        Assert.Contains("does not exist", await Rejected(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("a", "Branch", new { condition = Wf.Cmp("lead.score", "gt", 1), then = "zzz" })])));
        Assert.Contains("Unknown field", await Rejected(Wf.Definition([Wf.Trigger("lead.created", Wf.Cmp("lead.password", "eq", "x"))], [Wf.Step("a", "AddTag", new { tag = "t" })])));
        await Rejected(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("a", "RunScript", new { code = "1" })]));   // not an action: the enum does not parse → rejected before any step exists
        Assert.Contains("Unknown setting", await Rejected(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("a", "AddTag", new { tag = "t", code = "1" })])));   // unknown properties are rejected, never ignored
    }

    [RequiresMySqlFact]
    public async Task Event_redelivery_and_concurrent_triggers_start_exactly_one_run()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wf = await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("a", "AddTag", new { tag = "once" })]));
        var lead = await Crm.CreateLead(w.Admin, phone: "+9230" + Random.Shared.Next(10_000_000, 99_999_999));
        await w.Worker.DispatchAsync();
        var envelope = w.Worker.Publisher.Published.Single(e => e.Type == "lead.created" && e.Payload.Contains(lead.Id.ToString()));

        // the same message delivered three times (broker redelivery), including at the same time
        var processor = w.Worker.Services.GetRequiredService<LeadPilot.Infrastructure.Messaging.Broker.MessageProcessor>();
        var rmq = w.Worker.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LeadPilot.Infrastructure.Messaging.RabbitMqOptions>>().Value;
        var queue = LeadPilot.Infrastructure.Messaging.Broker.MessagingTopology.Queues(rmq).Single(q => q.ConsumerName == "workflows.triggers");
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(envelope);
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => processor.ProcessAsync(queue, body, WorkerHarness.Headers(envelope), default)));
        Assert.Single(await w.ExecutionsAsync(wf.Id, lead.Id));

        // two DIFFERENT events for the same (fresh) lead at the same moment: the exclusive slot lets exactly one of them in
        var lead2 = await Crm.CreateLead(w.Admin, phone: "+9230" + Random.Shared.Next(10_000_000, 99_999_999));
        var versionId = (await w.GetAsync(wf.Id)).ActiveVersionId!.Value;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => w.Worker.Scopes.RunAsync(w.TenantId, async (sp, ct) =>
        {
            var r = await sp.GetRequiredService<IWorkflowTriggerService>().StartAsync(wf.Id, versionId, "lead.created", Guid.NewGuid(), lead2.Id, null, null, false, "test", ct);
            lock (outcomes) outcomes.Add(r.Outcome);
        })));
        Assert.Equal(1, outcomes.Count(o => o == WorkflowStartOutcome.Started));
        Assert.Equal(3, outcomes.Count(o => o == WorkflowStartOutcome.Blocked));
        Assert.Single(await w.ExecutionsAsync(wf.Id, lead2.Id));
    }

    private readonly List<WorkflowStartOutcome> outcomes = [];
}
