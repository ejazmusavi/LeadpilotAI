using System.Text.Json.Nodes;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Workflows;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Workflows;

/// <summary>
/// Runs executions. Every run is resumable from the row alone: the current step, per-step state and next run time are persisted together with each step's database effects in ONE SaveChanges, so a crash
/// leaves either "step done and recorded" or "nothing happened". A lease (atomic UPDATE) keeps two workers from running the same execution; an expired lease is crash recovery. Cancellation and kill
/// switches are re-read before every step, and a concurrent cancel makes the runner's save fail (optimistic concurrency) so nothing happens after it.
/// </summary>
internal sealed class WorkflowEngine(CrmContext crm, WorkflowSupport support, IEnumerable<IWorkflowAction> actionList, IWorkflowJobScheduler jobs, IOptions<WorkflowOptions> options, ILogger<WorkflowEngine> logger) : IWorkflowEngine
{
    private readonly Dictionary<StepType, IWorkflowAction> _actions = actionList.ToDictionary(a => a.Type);

    public async Task RunAsync(Guid executionId, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var o = options.Value;
        var now = crm.Now;
        var peek = await db.WorkflowExecutions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == executionId && e.TenantId == tenantId, ct);
        if (peek is null || peek.IsTerminal) return;
        if (peek.Status == ExecutionStatus.Running && peek.LeaseUntilUtc > now) return;                      // another runner owns it
        if (peek.Status is ExecutionStatus.Waiting && peek.NextRunAtUtc > now.AddSeconds(1)) return;           // an early duplicate job: the job scheduled for the due time handles it

        // kill switches: stop at once, nothing is sent
        var killReason = await KillReasonAsync(peek.WorkflowId, ct);
        if (killReason is not null) { await EndAsync(executionId, ExecutionStatus.Cancelled, killReason, null, ct); return; }

        // per-tenant concurrency: too many live runners → come back shortly (the sweep also does)
        var settings = await support.SettingsAsync(ct);
        var running = await db.WorkflowExecutions.CountAsync(e => e.TenantId == tenantId && e.Status == ExecutionStatus.Running && e.LeaseUntilUtc > now && e.Id != executionId, ct);
        if (running >= settings.MaxConcurrentExecutions) { jobs.EnqueueRun(tenantId, executionId, TimeSpan.FromMinutes(1)); return; }

        // atomic claim (Pending / due Waiting / Running with an expired lease)
        var lease = now.AddSeconds(Math.Max(30, o.LeaseSeconds));
        var claimed = await db.WorkflowExecutions.Where(e => e.Id == executionId && e.TenantId == tenantId && e.Version == peek.Version &&
                (e.Status == ExecutionStatus.Pending || (e.Status == ExecutionStatus.Waiting && e.NextRunAtUtc <= now.AddSeconds(1)) || (e.Status == ExecutionStatus.Running && e.LeaseUntilUtc <= now)))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ExecutionStatus.Running).SetProperty(e => e.LeaseUntilUtc, lease).SetProperty(e => e.StartedAtUtc, e => e.StartedAtUtc ?? now).SetProperty(e => e.Version, e => e.Version + 1), ct);
        if (claimed == 0) return;

        var exec = await db.WorkflowExecutions.FirstAsync(e => e.Id == executionId && e.TenantId == tenantId, ct);
        var version = await db.WorkflowVersions.AsNoTracking().FirstAsync(v => v.Id == exec.WorkflowVersionId && v.TenantId == tenantId, ct);
        var steps = await db.WorkflowSteps.AsNoTracking().Where(s => s.TenantId == tenantId && s.WorkflowVersionId == exec.WorkflowVersionId).OrderBy(s => s.Position).ToListAsync(ct);
        var defs = steps.Select(s => new StepDefinition(s.Key, s.Type, s.Name, null, null, s.OnFailure, s.MaxRetries)).ToList();
        var tz = await support.TimeZoneAsync(ct);
        try { await LoopAsync(exec, steps, defs, tz, ct); }
        catch (DbUpdateConcurrencyException) { logger.LogInformation("Execution {ExecutionId} changed while running (cancelled or taken over); stopping this runner", executionId); }
    }

    private async Task LoopAsync(WorkflowExecution exec, List<WorkflowStep> steps, List<StepDefinition> defs, TimeZoneInfo tz, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var o = options.Value;
        var state = ExecutionState.Parse(exec.StateJson);

        for (var n = 0; n < Math.Max(1, o.MaxStepsPerRun); n++)
        {
            await db.Entry(exec).ReloadAsync(ct);
            if (exec.IsTerminal) return;                                                                     // cancelled while we worked
            var now = crm.Now;
            if (await KillReasonAsync(exec.WorkflowId, ct) is { } kill) { await FinishAsync(exec, ExecutionStatus.Cancelled, kill, null, state, ct); return; }

            var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == exec.EntityId && l.TenantId == tenantId, ct);
            if (lead is null || lead.IsArchived) { await FinishAsync(exec, ExecutionStatus.Stopped, "The lead was archived or removed.", null, state, ct); return; }

            if (exec.CurrentStepKey is null) { await FinishAsync(exec, ExecutionStatus.Completed, null, null, state, ct); return; }
            var step = steps.FirstOrDefault(s => s.Key == exec.CurrentStepKey);
            if (step is null) { await FinishAsync(exec, ExecutionStatus.Failed, $"Step '{exec.CurrentStepKey}' does not exist in this version.", null, state, ct); return; }
            var index = steps.IndexOf(step);
            var ss = state.Of(step.Key);
            if (++state.Executed > Math.Max(2 * steps.Count, 10) * 3) { await FinishAsync(exec, ExecutionStatus.Failed, "The run exceeded its step budget (loop guard).", step, state, ct); return; }
            exec.MarkRunning(now, now.AddSeconds(Math.Max(30, o.LeaseSeconds)));

            // "run only if"
            if (step.ConditionJson is not null)
            {
                var facts = await support.FactsAsync(lead.Id, exec.ContactId, exec.ConversationId, null, exec.TriggerKey, ct);
                if (!ConditionEvaluator.EvaluateJson(step.ConditionJson, facts, now, tz))
                {
                    ss.Status = "skipped"; Log(exec, step, ss, LogOutcome.Skipped, "The step's condition was not met.", null);
                    await AdvanceAsync(exec, state, NextOf(defs, steps, index, null), ct); continue;
                }
            }

            switch (step.Type)
            {
                case StepType.Wait:
                {
                    var minutes = (int)(Cfg(step)["minutes"]?.GetValue<int>() ?? 1);
                    if (ss.WaitUntilUtc is null) { ss.WaitUntilUtc = now.AddMinutes(minutes); ss.Status = "waiting"; }
                    if (now < ss.WaitUntilUtc)
                    {
                        Log(exec, step, ss, LogOutcome.Waiting, $"Waiting until {ss.WaitUntilUtc:u}.", null);
                        exec.SetState(state.Serialize(), exec.CurrentStepKey); exec.MarkWaiting(ss.WaitUntilUtc.Value, $"Waiting until {ss.WaitUntilUtc:u}");
                        await db.SaveChangesAsync(ct);
                        Schedule(exec, ss.WaitUntilUtc.Value - now);
                        return;
                    }
                    ss.Status = "done"; Log(exec, step, ss, LogOutcome.Succeeded, "The wait is over.", null);
                    await AdvanceAsync(exec, state, NextOf(defs, steps, index, null), ct);
                    continue;
                }
                case StepType.Branch:
                {
                    var cfg = Cfg(step);
                    var facts = await support.FactsAsync(lead.Id, exec.ContactId, exec.ConversationId, null, exec.TriggerKey, ct);
                    var yes = ConditionEvaluator.EvaluateJson(cfg["condition"]?.ToJsonString(), facts, now, tz);
                    var target = (yes ? cfg["then"] : cfg["else"])?.GetValue<string>();
                    ss.Status = "done"; ss.Effect = yes ? "yes" : "no";
                    Log(exec, step, ss, LogOutcome.Succeeded, yes ? "Condition met: took the 'yes' path." : "Condition not met: took the 'no' path.", null);
                    await AdvanceAsync(exec, state, NextOf(defs, steps, index, string.IsNullOrWhiteSpace(target) ? null : target.Trim()), ct);
                    continue;
                }
            }

            // an action
            if (!_actions.TryGetValue(step.Type, out var action)) { await FinishAsync(exec, ExecutionStatus.Failed, $"No handler for '{step.Type}'.", step, state, ct); return; }
            ss.Attempts++;
            var ctx = new ActionContext { Execution = exec, Step = defs[index], StepName = step.Name, Config = Cfg(step), State = ss, Lead = lead, Now = now, ContactId = exec.ContactId, ConversationId = exec.ConversationId };
            ActionResult result;
            try { result = await action.ExecuteAsync(ctx, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (DbUpdateConcurrencyException) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Workflow step {Step} of execution {ExecutionId} threw", step.Key, exec.Id);
                // whatever the action half-did is discarded; the persisted state is the truth
                db.ChangeTracker.Clear();
                exec = await db.WorkflowExecutions.FirstAsync(e => e.Id == exec.Id && e.TenantId == tenantId, ct);
                state = ExecutionState.Parse(exec.StateJson); ss = state.Of(step.Key); ss.Attempts++;
                result = ActionResult.Retry("Unexpected error (" + ex.GetType().Name + ").");
            }

            await ApplyResultAsync(exec, steps, defs, index, step, ss, state, result, ct);
            if (exec.IsTerminal || exec.Status == ExecutionStatus.Waiting) return;
        }
        // budget for this job run used: hand over to a fresh job
        exec.MarkWaiting(crm.Now, "Continuing");
        await db.SaveChangesAsync(ct);
        Schedule(exec, TimeSpan.Zero);
    }

    private async Task ApplyResultAsync(WorkflowExecution exec, List<WorkflowStep> steps, List<StepDefinition> defs, int index, WorkflowStep step, StepState ss, ExecutionState state, ActionResult r, CancellationToken ct)
    {
        var db = crm.Db; var now = crm.Now;
        switch (r.Outcome)
        {
            case ActionOutcome.Done:
                ss.Status = "done"; ss.Effect = r.Effect; ss.Error = null;
                Log(exec, step, ss, LogOutcome.Succeeded, r.Message, r.Effect);
                await AdvanceAsync(exec, state, NextOf(defs, steps, index, null), ct);
                return;
            case ActionOutcome.Stop:
                ss.Status = "done"; Log(exec, step, ss, LogOutcome.Succeeded, r.Message, null);
                await FinishAsync(exec, ExecutionStatus.Stopped, string.IsNullOrWhiteSpace(r.Message) ? "Stopped by the workflow." : r.Message, null, state, ct);
                return;
            case ActionOutcome.Retry when step.OnFailure == FailurePolicy.Retry && ss.Attempts <= step.MaxRetries:
            {
                ss.Status = "pending"; ss.Error = r.Message;
                var delay = WorkflowIdempotency.RetryBackoff(ss.Attempts);
                Log(exec, step, ss, LogOutcome.Retrying, $"{r.Message} Trying again in {(int)delay.TotalMinutes} min (attempt {ss.Attempts} of {step.MaxRetries + 1}).", null);
                exec.SetState(state.Serialize(), exec.CurrentStepKey); exec.MarkWaiting(now + delay, "Retrying: " + r.Message);
                await db.SaveChangesAsync(ct);
                Schedule(exec, delay);
                return;
            }
        }

        // a final failure (or a compliance block): the step's failure policy decides
        var blocked = r.Outcome == ActionOutcome.Blocked;
        ss.Status = blocked ? "blocked" : "failed"; ss.Error = r.Message;
        Log(exec, step, ss, blocked ? LogOutcome.Blocked : LogOutcome.Failed, r.Message, null);
        if (step.OnFailure == FailurePolicy.Skip)
        {
            Log(exec, step, ss, LogOutcome.Skipped, "The step failed; its policy is to skip it and continue.", null);
            await AdvanceAsync(exec, state, NextOf(defs, steps, index, null), ct);
            return;
        }
        await FinishAsync(exec, blocked ? ExecutionStatus.Stopped : ExecutionStatus.Failed, blocked ? "Not sent: " + r.Message : $"Step '{step.Name}' failed: {r.Message}", null, state, ct, keepStep: true);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Saves the execution at its next step together with whatever the step changed (one transaction).</summary>
    private async Task AdvanceAsync(WorkflowExecution exec, ExecutionState state, string? nextKey, CancellationToken ct)
    {
        exec.SetState(state.Serialize(), nextKey);
        await crm.Db.SaveChangesAsync(ct);
    }

    private async Task FinishAsync(WorkflowExecution exec, ExecutionStatus status, string? reason, WorkflowStep? failedStep, ExecutionState state, CancellationToken ct, bool keepStep = false)
    {
        if (!keepStep && failedStep is null && status == ExecutionStatus.Completed) exec.SetState(state.Serialize(), null);
        else exec.SetState(state.Serialize(), exec.CurrentStepKey);
        exec.End(status, reason, crm.Now);
        crm.Db.WorkflowExecutionLogs.Add(WorkflowExecutionLog.Create(crm.TenantId, exec.Id, null, null, null, 0, status == ExecutionStatus.Failed ? LogOutcome.Failed : status == ExecutionStatus.Cancelled ? LogOutcome.Cancelled : LogOutcome.Info,
            status switch { ExecutionStatus.Completed => "The run completed.", ExecutionStatus.Cancelled => "The run was cancelled: " + reason, ExecutionStatus.Stopped => "The run stopped: " + reason, _ => "The run failed: " + reason }, null, crm.Now));
        await crm.Db.SaveChangesAsync(ct);
    }

    private async Task EndAsync(Guid executionId, ExecutionStatus status, string reason, string? _, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var exec = await db.WorkflowExecutions.FirstOrDefaultAsync(e => e.Id == executionId && e.TenantId == tenantId, ct);
        if (exec is null || exec.IsTerminal) return;
        exec.End(status, reason, crm.Now);
        db.WorkflowExecutionLogs.Add(WorkflowExecutionLog.Create(tenantId, exec.Id, null, null, null, 0, LogOutcome.Cancelled, "The run was cancelled: " + reason, null, crm.Now));
        try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }
    }

    private void Log(WorkflowExecution exec, WorkflowStep step, StepState ss, LogOutcome outcome, string message, string? effect) =>
        crm.Db.WorkflowExecutionLogs.Add(WorkflowExecutionLog.Create(crm.TenantId, exec.Id, step.Key, step.Name, step.Type, ss.Attempts, outcome, message, effect, crm.Now));

    private void Schedule(WorkflowExecution exec, TimeSpan delay)
    {
        try { jobs.EnqueueRun(crm.TenantId, exec.Id, delay > TimeSpan.Zero ? delay : null); }
        catch (Exception ex) { logger.LogWarning(ex, "The run job for execution {ExecutionId} could not be scheduled; the sweep will pick it up", exec.Id); }
    }

    private static JsonObject Cfg(WorkflowStep s) { try { return JsonNode.Parse(s.ConfigJson) as JsonObject ?? []; } catch (System.Text.Json.JsonException) { return []; } }

    /// <summary>Next step key: a branch target, the following step, or null (= the run is complete).</summary>
    private static string? NextOf(List<StepDefinition> defs, List<WorkflowStep> steps, int index, string? branchTarget)
    {
        if (branchTarget is not null) return branchTarget == "end" ? null : branchTarget;
        if (steps[index].Type == StepType.Stop) return null;
        return index + 1 < steps.Count ? steps[index + 1].Key : null;
    }

    private async Task<string?> KillReasonAsync(Guid workflowId, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var settings = await support.SettingsAsync(ct);
        if (settings.KillSwitch) return "The workspace kill switch is on.";
        var wf = await crm.Db.Workflows.AsNoTracking().Where(w => w.Id == workflowId && w.TenantId == tenantId).Select(w => new { w.IsKilled, w.IsDeleted }).FirstOrDefaultAsync(ct);
        if (wf is null || wf.IsDeleted) return "The workflow was removed.";
        return wf.IsKilled ? "The workflow's kill switch is on." : null;
    }

    // ---------------------------------------------------------------- crash recovery

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now; var o = options.Value;
        var due = await db.WorkflowExecutions.AsNoTracking().Where(e => e.TenantId == tenantId &&
                (e.Status == ExecutionStatus.Pending || (e.Status == ExecutionStatus.Waiting && e.NextRunAtUtc <= now) || (e.Status == ExecutionStatus.Running && e.LeaseUntilUtc <= now)))
            .OrderBy(e => e.NextRunAtUtc).Select(e => e.Id).Take(Math.Max(1, o.SweepBatch)).ToListAsync(ct);
        foreach (var id in due) jobs.EnqueueRun(tenantId, id);
        return due.Count;
    }
}
