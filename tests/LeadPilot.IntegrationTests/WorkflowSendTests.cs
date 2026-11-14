using System.Net;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Messaging;
using LeadPilot.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>The compliance guarantee of workflows: nobody without consent, nobody suppressed, nobody opted out is ever messaged, and a retry never duplicates a message (asserted on the fake WAHA's received requests).</summary>
[Collection("app")]
public class WorkflowSendTests(AppFixture app)
{
    private static async Task<WorkflowDto> OnCreated(WorkflowWorld w, params object[] steps) =>
        await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created")], steps));

    private static async Task<ExecutionListItemDto> Started(WorkflowWorld w, WorkflowDto wf, TestContact c)
    {
        await w.PumpAsync();
        return (await w.ExecutionsAsync(wf.Id, c.LeadId)).Single();
    }

    [RequiresMySqlFact]
    public async Task An_eligible_contact_is_messaged_exactly_once_through_the_phase_5_pipeline_even_when_everything_is_redelivered()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wf = await OnCreated(w, w.SendStep());
        var c = await w.ContactAsync();
        var ex = await Started(w, wf, c);

        var done = await w.RunToEndAsync(ex.Id);
        Assert.Equal(ExecutionStatus.Completed, done.Summary.Status);
        var msgs = await w.MessagesOfAsync(c);
        var m = Assert.Single(msgs);
        Assert.Equal(MessagePurpose.Marketing, m.Purpose); Assert.StartsWith($"wf:{ex.Id:N}:send:0", m.IdempotencyKey);
        await w.SendQueuedAsync(c);
        Assert.Single(w.SentTo(c));                                             // the fake gateway got ONE request
        Assert.Contains("Lahore", w.SentTo(c).Single().Text);                   // the template was filled from the lead

        // a second job delivery, a sweep and a re-run all find nothing to do
        await w.RunAsync(ex.Id); await w.SweepAsync(); await w.RunAsync(ex.Id);
        await w.SendQueuedAsync(c);
        Assert.Single(w.SentTo(c)); Assert.Single(await w.MessagesOfAsync(c));
    }

    [RequiresMySqlFact]
    public async Task Contacts_without_consent_or_with_revoked_consent_or_suppressed_or_opted_out_are_never_messaged()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wf = await OnCreated(w, w.SendStep(onFailure: "Stop"));
        var noConsent = await w.ContactAsync(consent: false);
        var revoked = await w.ContactAsync();
        Assert.True((await w.Admin.Post($"/api/v1/contacts/{revoked.ContactId}/consent", new { channel = "WhatsApp", purpose = "Marketing", status = "Revoked", source = "reply STOP" })).IsSuccessStatusCode);
        var suppressed = await w.ContactAsync();
        Assert.True((await w.Admin.Post("/api/v1/suppression", new { channel = "WhatsApp", value = suppressed.Phone, reason = "OptOut" })).IsSuccessStatusCode);
        var optedOut = await w.ContactAsync();
        await MessagingKit.InboundAsync(app, w.Worker, w.Account, w.Admin, optedOut.Phone, "STOP");
        var archived = await w.ContactAsync();
        Assert.True((await w.Admin.Delete($"/api/v1/leads/{archived.LeadId}")).IsSuccessStatusCode);

        await w.PumpAsync();
        foreach (var c in new[] { noConsent, revoked, suppressed, optedOut })
        {
            var ex = (await w.ExecutionsAsync(wf.Id, c.LeadId)).Single();
            var e = await w.RunToEndAsync(ex.Id);
            Assert.Equal(ExecutionStatus.Stopped, e.Summary.Status);              // a compliance block ends the run, it is never retried
            Assert.Contains(e.Logs, l => l.Outcome == LogOutcome.Blocked);
            Assert.Empty(await w.MessagesOfAsync(c));                              // nothing was even queued
            await w.SendQueuedAsync(c);
            Assert.Empty(w.SentTo(c));
            Assert.Equal(HttpStatusCode.Conflict, (await w.Admin.Post($"/api/v1/workflow-executions/{ex.Id}/retry")).StatusCode);   // blocked runs cannot be retried into a send
        }
        Assert.Empty(await w.ExecutionsAsync(wf.Id, archived.LeadId));            // an archived lead never starts a run
    }

    [RequiresMySqlFact]
    public async Task Consent_revoked_while_a_run_waits_stops_the_message()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wf = await OnCreated(w, Wf.Wait("pause", 60), w.SendStep());
        var c = await w.ContactAsync();
        var ex = await Started(w, wf, c);
        await w.RunAsync(ex.Id);
        Assert.Equal(ExecutionStatus.Waiting, (await w.ExecutionAsync(ex.Id)).Summary.Status);

        Assert.True((await w.Admin.Post($"/api/v1/contacts/{c.ContactId}/consent", new { channel = "WhatsApp", purpose = "Marketing", status = "Revoked", source = "customer asked to stop" })).IsSuccessStatusCode);
        app.Clock.Offset += TimeSpan.FromMinutes(61);
        await w.RunAsync(ex.Id);
        var e = await w.ExecutionAsync(ex.Id);
        Assert.Equal(ExecutionStatus.Stopped, e.Summary.Status);
        Assert.Empty(await w.MessagesOfAsync(c)); Assert.Empty(w.SentTo(c));
    }

    [RequiresMySqlFact]
    public async Task Consent_revoked_after_queueing_still_stops_the_send_at_the_phase_5_runner()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wf = await OnCreated(w, w.SendStep());
        var c = await w.ContactAsync();
        var ex = await Started(w, wf, c);
        await w.RunToEndAsync(ex.Id);
        Assert.Single(await w.MessagesOfAsync(c));                                // queued, not yet sent
        Assert.True((await w.Admin.Post($"/api/v1/contacts/{c.ContactId}/consent", new { channel = "WhatsApp", purpose = "Marketing", status = "Revoked", source = "late STOP" })).IsSuccessStatusCode);
        await w.SendQueuedAsync(c);
        Assert.Empty(w.SentTo(c));
        Assert.Equal(MessageStatus.Failed, (await w.MessagesOfAsync(c)).Single().Status);
    }

    [RequiresMySqlFact]
    public async Task A_failed_send_step_is_retried_safely_a_paused_account_never_causes_a_duplicate()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wf = await OnCreated(w, Wf.Step("tag", "AddTag", new { tag = "step-one" }), w.SendStep(retries: 0), Wf.Step("after", "AddTag", new { tag = "step-three" }));
        var c = await w.ContactAsync();
        var ex = await Started(w, wf, c);

        Assert.True((await w.Admin.Post($"/api/v1/channels/accounts/{w.Account.Id}/pause")).IsSuccessStatusCode);
        var failed = await w.RunToEndAsync(ex.Id);
        Assert.Equal(ExecutionStatus.Failed, failed.Summary.Status);
        Assert.Equal("send", failed.Summary.CurrentStepKey);
        Assert.Contains(failed.Logs, l => l.StepKey == "send" && l.Outcome == LogOutcome.Failed);
        Assert.Empty(await w.MessagesOfAsync(c));

        Assert.True((await w.Admin.Post($"/api/v1/channels/accounts/{w.Account.Id}/resume")).IsSuccessStatusCode);
        var retried = await Crm.Ok<ExecutionDto>(await w.Admin.Post($"/api/v1/workflow-executions/{ex.Id}/retry"));
        Assert.Equal(ExecutionStatus.Waiting, retried.Summary.Status);
        var done = await w.RunToEndAsync(ex.Id);
        Assert.Equal(ExecutionStatus.Completed, done.Summary.Status);
        // step one ran once (the retry started at the failed step), the message exists once and leaves once
        Assert.Equal(1, done.Logs.Count(l => l.StepKey == "tag" && l.Outcome == LogOutcome.Succeeded));
        Assert.Single(await w.MessagesOfAsync(c));
        await w.SendQueuedAsync(c);
        Assert.Single(w.SentTo(c));

        // retrying something that is not failed is refused; retrying twice cannot send twice
        Assert.Equal(HttpStatusCode.Conflict, (await w.Admin.Post($"/api/v1/workflow-executions/{ex.Id}/retry")).StatusCode);
        await w.RunAsync(ex.Id);
        Assert.Single(w.SentTo(c));
    }

    [RequiresMySqlFact]
    public async Task A_message_that_failed_at_the_gateway_gets_a_new_generation_but_one_that_exists_is_never_repeated_after_a_crash()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wf = await OnCreated(w, w.SendStep());
        var c = await w.ContactAsync();
        var ex = await Started(w, wf, c);
        await w.RunToEndAsync(ex.Id);
        Assert.Single(await w.MessagesOfAsync(c));

        // crash simulation: the step's effect exists (message queued) but the execution row is back at "before the step"
        await w.Worker.Scopes.RunAsync(w.TenantId, async (sp, ct) =>
        {
            var db = sp.GetRequiredService<LeadPilot.Infrastructure.Persistence.AppDbContext>();
            await db.WorkflowExecutions.Where(e => e.Id == ex.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExecutionStatus.Running).SetProperty(e => e.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(-5))
                .SetProperty(e => e.StateJson, "{}").SetProperty(e => e.CurrentStepKey, "send").SetProperty(e => e.EndedAtUtc, (DateTime?)null).SetProperty(e => e.ActiveSlot, "A"), ct);
        });
        await w.SweepAsync();
        Assert.Contains(app.Workflows.Jobs, j => j.ExecutionId == ex.Id);
        var recovered = await w.RunToEndAsync(ex.Id);
        Assert.Equal(ExecutionStatus.Completed, recovered.Summary.Status);
        Assert.Single(await w.MessagesOfAsync(c));                                // found by its idempotency key, not sent again
        await w.SendQueuedAsync(c);
        Assert.Single(w.SentTo(c));

        // the queued message fails at the gateway for good (nothing was sent): a person retrying the step moves it to generation 1 and sends once more
        var c2 = await w.ContactAsync();
        var ex2 = await Started(w, wf, c2);
        await w.RunToEndAsync(ex2.Id);
        app.Waha.FaultNext(FakeWahaServer.SendFault.Http400);
        await w.SendQueuedAsync(c2);
        Assert.Equal(MessageStatus.Failed, (await w.MessagesOfAsync(c2)).Single().Status);
        Assert.Empty(w.SentTo(c2));
        await w.Worker.Scopes.RunAsync(w.TenantId, async (sp, ct) =>
        {
            var db = sp.GetRequiredService<LeadPilot.Infrastructure.Persistence.AppDbContext>();
            await db.WorkflowExecutions.Where(e => e.Id == ex2.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExecutionStatus.Running).SetProperty(e => e.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(-5))
                .SetProperty(e => e.StateJson, "{}").SetProperty(e => e.CurrentStepKey, "send").SetProperty(e => e.EndedAtUtc, (DateTime?)null).SetProperty(e => e.ActiveSlot, "A"), ct);
        });
        await w.RunToEndAsync(ex2.Id);
        var all = await w.MessagesOfAsync(c2);
        Assert.Equal(2, all.Count); Assert.Contains(all, m => m.IdempotencyKey!.EndsWith(":send:1"));
        await w.SendQueuedAsync(c2);
        Assert.Single(w.SentTo(c2));
    }

    [RequiresMySqlFact]
    public async Task Workflow_message_usage_is_limited_and_goes_through_the_account_limits()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        await w.SettingsAsync(kill: false, daily: 1);
        var wf = await OnCreated(w, w.SendStep(onFailure: "Stop", retries: 0));
        var c1 = await w.ContactAsync(); var c2 = await w.ContactAsync();
        await w.PumpAsync();
        var e1 = await w.RunToEndAsync((await w.ExecutionsAsync(wf.Id, c1.LeadId)).Single().Id);
        var e2 = await w.RunToEndAsync((await w.ExecutionsAsync(wf.Id, c2.LeadId)).Single().Id);
        Assert.Equal([ExecutionStatus.Completed, ExecutionStatus.Failed], new[] { e1.Summary.Status, e2.Summary.Status }.OrderBy(s => s));
        Assert.Contains((await w.ExecutionAsync(e2.Summary.Id)).Logs, l => l.Message.Contains("limit of 1"));
        await w.SettingsAsync(kill: false);
    }
}
