using System.Text.Json.Nodes;
using LeadPilot.Application.Common;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Workflows;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Infrastructure.Services.Tenancy;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Workflows;

internal sealed class WorkflowExecutionService(CrmContext crm, IWorkflowTriggerService triggers, IWorkflowJobScheduler jobs, WorkflowSupport support, ILogger<WorkflowExecutionService> logger) : IWorkflowExecutionService
{
    public async Task<PagedResult<ExecutionListItemDto>> ListAsync(ExecutionQuery query, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(query.Page, query.PageSize);
        var q = db.WorkflowExecutions.AsNoTracking().Where(e => e.TenantId == tenantId);
        if (query.WorkflowId is { } wid) q = q.Where(e => e.WorkflowId == wid);
        if (query.Status is { } st) q = q.Where(e => e.Status == st);
        if (query.LeadId is { } lid) q = q.Where(e => e.EntityId == lid);
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(e => e.CreatedAtUtc).ThenBy(e => e.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<ExecutionListItemDto>(await MapAsync(rows, ct), page, size, total);
    }

    public async Task<Result<ExecutionDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var e = await LoadAsync(id, false, ct);
        return e is null ? Result.Failure<ExecutionDto>(Error.NotFound("Execution")) : Result.Success(await DetailAsync(e, ct));
    }

    public Task<Result<ExecutionDto>> StartAsync(Guid workflowId, Guid leadId, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var wf = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflowId && w.TenantId == tenantId && !w.IsDeleted, ct);
            if (wf is null) return Result.Failure<ExecutionDto>(Error.NotFound("Workflow"));
            if (!await db.Leads.AnyAsync(l => l.Id == leadId && l.TenantId == tenantId, ct)) return Result.Failure<ExecutionDto>(Error.NotFound("Lead"));
            if (wf.Status != WorkflowStatus.Active || wf.ActiveVersionId is null) return Result.Failure<ExecutionDto>(Error.Conflict("Only an active workflow can be run. Activate it first."));
            var r = await triggers.StartAsync(workflowId, wf.ActiveVersionId.Value, WorkflowEventTypes.Manual, Guid.NewGuid(), leadId, null, null, false, crm.Actor ?? "a team member", ct);
            if (r.Outcome != WorkflowStartOutcome.Started || r.ExecutionId is null) return Result.Failure<ExecutionDto>(Error.Conflict(r.Reason ?? "The workflow could not be started."));
            crm.Record("WorkflowExecution.Started", "WorkflowExecution", r.ExecutionId.Value, $"Workflow '{wf.Name}' run for a lead by a person.");
            await db.SaveChangesAsync(ct);
            return Result.Success(await DetailAsync((await LoadAsync(r.ExecutionId.Value, false, ct))!, ct));
        }, logger);

    public Task<Result<ExecutionDto>> RetryAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var e = await LoadAsync(id, true, ct); if (e is null) return Result.Failure<ExecutionDto>(Error.NotFound("Execution"));
            if (e.Status != ExecutionStatus.Failed) return Result.Failure<ExecutionDto>(Error.Conflict("Only a failed run can be retried. Runs that stopped because a contact may not be messaged are never retried."));
            if ((await support.SettingsAsync(ct)).KillSwitch) return Result.Failure<ExecutionDto>(Error.Conflict("The workspace kill switch is on."));
            var wf = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == e.WorkflowId && w.TenantId == tenantId && !w.IsDeleted, ct);
            if (wf is null || wf.IsKilled) return Result.Failure<ExecutionDto>(Error.Conflict("The workflow is switched off by its kill switch or was deleted."));
            var version = await db.WorkflowVersions.AsNoTracking().FirstAsync(v => v.Id == e.WorkflowVersionId && v.TenantId == tenantId, ct);

            // the failed step gets a fresh set of attempts; its idempotency key (and generation) are kept, so a retry can never repeat an effect that already happened
            var state = ExecutionState.Parse(e.StateJson);
            if (e.CurrentStepKey is { } key) { var ss = state.Of(key); ss.Attempts = 0; ss.Status = "pending"; ss.Error = null; }
            state.Executed = 0;
            e.SetState(state.Serialize(), e.CurrentStepKey);
            e.Reopen(e.CurrentStepKey, version.ReEntry != ReEntryPolicy.Always, crm.Now);
            db.WorkflowExecutionLogs.Add(WorkflowExecutionLog.Create(tenantId, e.Id, e.CurrentStepKey, null, null, 0, LogOutcome.Info, $"Retried by {crm.Actor ?? "a team member"}.", null, crm.Now));
            crm.Record("WorkflowExecution.Retried", "WorkflowExecution", e.Id, "A failed workflow run was retried.");
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ResultFlow.IsDuplicate(ex)) { db.ChangeTracker.Clear(); return Result.Failure<ExecutionDto>(Error.Conflict("Another run of this workflow for the same lead is in progress.")); }
            try { jobs.EnqueueRun(tenantId, e.Id); } catch (Exception ex) { logger.LogWarning(ex, "Retry job could not be scheduled; the sweep will pick it up"); }
            return Result.Success(await DetailAsync(e, ct));
        }, logger);

    public Task<Result<ExecutionDto>> CancelAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var e = await LoadAsync(id, true, ct); if (e is null) return Result.Failure<ExecutionDto>(Error.NotFound("Execution"));
                if (e.IsTerminal) return Result.Failure<ExecutionDto>(Error.Conflict("This run has already ended."));
                e.End(ExecutionStatus.Cancelled, $"Cancelled by {crm.Actor ?? "a team member"}.", crm.Now);
                db.WorkflowExecutionLogs.Add(WorkflowExecutionLog.Create(tenantId, e.Id, e.CurrentStepKey, null, null, 0, LogOutcome.Cancelled, $"Cancelled by {crm.Actor ?? "a team member"}. Steps that already ran are not undone.", null, crm.Now));
                crm.Record("WorkflowExecution.Cancelled", "WorkflowExecution", e.Id, "A workflow run was cancelled.");
                try { await db.SaveChangesAsync(ct); return Result.Success(await DetailAsync(e, ct)); }
                catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }   // a runner saved a step at the same moment: look again
            }
            return Result.Failure<ExecutionDto>(Error.Conflict("The run is busy right now. Try again in a moment."));
        }, logger);

    private async Task<WorkflowExecution?> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        return await (track ? crm.Db.WorkflowExecutions : crm.Db.WorkflowExecutions.AsNoTracking()).FirstOrDefaultAsync(e => e.Id == id && e.TenantId == tenantId, ct);
    }

    private async Task<IReadOnlyList<ExecutionListItemDto>> MapAsync(List<WorkflowExecution> rows, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var wf = rows.Select(r => r.WorkflowId).Distinct().ToList(); var vs = rows.Select(r => r.WorkflowVersionId).Distinct().ToList(); var ls = rows.Select(r => r.EntityId).Distinct().ToList();
        var names = await db.Workflows.AsNoTracking().Where(w => w.TenantId == tenantId && wf.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var numbers = await db.WorkflowVersions.AsNoTracking().Where(v => v.TenantId == tenantId && vs.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Number, ct);
        var leads = await db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && ls.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        return rows.Select(r => new ExecutionListItemDto(r.Id, r.WorkflowId, names.GetValueOrDefault(r.WorkflowId) ?? "?", numbers.GetValueOrDefault(r.WorkflowVersionId), r.TriggerKey, r.EntityId, leads.GetValueOrDefault(r.EntityId), r.Status, r.StatusReason, r.CurrentStepKey, r.CreatedAtUtc, r.EndedAtUtc, r.NextRunAtUtc)).ToList();
    }

    private async Task<ExecutionDto> DetailAsync(WorkflowExecution e, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var summary = (await MapAsync([e], ct))[0];
        var steps = await db.WorkflowSteps.AsNoTracking().Where(s => s.TenantId == tenantId && s.WorkflowVersionId == e.WorkflowVersionId).OrderBy(s => s.Position).ToListAsync(ct);
        var state = ExecutionState.Parse(e.StateJson);
        var stepStates = steps.Select(s => { var ss = state.Steps.GetValueOrDefault(s.Key) ?? new StepState(); return new ExecutionStepStateDto(s.Key, s.Name, s.Type, ss.Status, ss.Attempts, ss.Effect, ss.Error, ss.WaitUntilUtc); }).ToList();
        var logs = await db.WorkflowExecutionLogs.AsNoTracking().Where(l => l.TenantId == tenantId && l.WorkflowExecutionId == e.Id).OrderBy(l => l.OccurredAtUtc).ThenBy(l => l.Id).Take(500).ToListAsync(ct);
        return new ExecutionDto(summary, e.ContactId, e.ConversationId, e.StartedBy, stepStates,
            logs.Select(l => new ExecutionLogDto(l.Id, l.StepKey, l.StepName, l.StepType, l.Attempt, l.Outcome, l.Message, l.EffectRef, l.OccurredAtUtc)).ToList(), e.Status == ExecutionStatus.Failed, !e.IsTerminal);
    }
}

internal sealed class WorkflowSimulationService(CrmContext crm, WorkflowSupport support, IEnumerable<IWorkflowAction> actionList, IWorkflowService workflows) : IWorkflowSimulationService
{
    private readonly WorkflowSupport _support = support;

    public async Task<Result<SimulationDto>> SimulateAsync(Guid workflowId, SimulateRequest r, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        if ((await workflows.GetAsync(workflowId, ct)) is { IsFailure: true } missing) return Result.Failure<SimulationDto>(missing.Error);
        var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == r.LeadId && l.TenantId == tenantId, ct);
        if (lead is null) return Result.Failure<SimulationDto>(Error.NotFound("Lead"));

        WorkflowDefinition def;
        if (r.Definition is not null) def = r.Definition;
        else
        {
            var wf = (await workflows.GetAsync(workflowId, ct)).Value;
            Guid? versionId = r.WorkflowVersionId ?? wf.ActiveVersionId ?? wf.Draft?.Id;
            var number = wf.Versions.FirstOrDefault(v => v.Id == versionId)?.Number;
            if (number is null) return Result.Failure<SimulationDto>(Error.Validation("This workflow has no version to simulate yet."));
            var v = await workflows.GetVersionAsync(workflowId, number.Value, ct);
            if (v.IsFailure) return Result.Failure<SimulationDto>(v.Error);
            def = new WorkflowDefinition(v.Value.Triggers.Select(t => new TriggerDefinition(t.EventType, ToElement(t.Condition), t.InactiveDays)).ToList(),
                v.Value.Steps.Select(s => new StepDefinition(s.Key, s.Type, s.Name, ToElement(s.Config), ToElement(s.Condition), s.OnFailure, s.MaxRetries)).ToList(), v.Value.ReEntry, v.Value.CooldownHours, v.Value.MaxStartsPerLeadPer30Days, v.Value.Note);
        }
        var report = WorkflowDefinitionValidator.Validate(def);
        if (!report.IsValid) return Result.Failure<SimulationDto>(Error.Validation("The workflow is not valid: " + report.Summary));

        var now = crm.Now; var tz = await _support.TimeZoneAsync(ct);
        var key = r.TriggerKey ?? def.Triggers[0].EventType;
        var trigger = def.Triggers.FirstOrDefault(t => t.EventType == key);
        if (trigger is null) return Result.Failure<SimulationDto>(Error.Validation($"This version has no trigger '{key}'."));
        var facts = await _support.FactsAsync(lead.Id, null, null, null, key, ct);
        if (r.MessageText is { } text) facts["message.text"] = text.Length > 500 ? text[..500] : text;
        var steps = new List<SimulationStepDto>();
        if (trigger.Condition is { } tc && !ConditionEvaluator.EvaluateJson(DefinitionMapper.Raw(tc), facts, now, tz))
            return Result.Success(new SimulationDto(false, "The trigger condition is NOT met for this lead, so the workflow would not start.", steps, "Would not start."));

        var actions = actionList.ToDictionary(a => a.Type);
        var clock = now; var index = 0; var guard = 0; string ending = "Would complete.";
        while (index < def.Steps.Count && guard++ < 200)
        {
            var s = def.Steps[index]; var cfg = WorkflowDefinitionValidator.ConfigOf(s);
            if (s.Condition is { } sc && !ConditionEvaluator.EvaluateJson(DefinitionMapper.Raw(sc), facts, clock, tz))
            { steps.Add(new(s.Key, s.Name, s.Type, "skipped", "Its 'run only if' condition is not met.")); index++; continue; }
            switch (s.Type)
            {
                case StepType.Wait:
                    var minutes = cfg["minutes"]?.GetValue<int>() ?? 1; clock = clock.AddMinutes(minutes);
                    steps.Add(new(s.Key, s.Name, s.Type, "waits", $"Would wait {minutes} min (the simulation moves its clock to {clock:u}; nothing is scheduled).")); index++; continue;
                case StepType.Branch:
                    var yes = ConditionEvaluator.EvaluateJson(cfg["condition"]?.ToJsonString(), facts, clock, tz);
                    var target = ((yes ? cfg["then"] : cfg["else"])?.GetValue<string>())?.Trim();
                    steps.Add(new(s.Key, s.Name, s.Type, yes ? "yes" : "no", yes ? "Condition met: continues on the 'yes' path." : "Condition not met: continues on the 'no' path."));
                    if (string.IsNullOrWhiteSpace(target)) index++;
                    else if (target == "end") index = def.Steps.Count;
                    else index = def.Steps.ToList().FindIndex(x => x.Key == target);
                    continue;
                case StepType.Stop:
                    steps.Add(new(s.Key, s.Name, s.Type, "stops", "The run would end here.")); ending = "Would stop at this step."; index = def.Steps.Count; continue;
            }
            var ctx = new ActionContext { Execution = SimExecution(workflowId, lead.Id), Step = s, StepName = s.Name, Config = cfg, State = new StepState(), Lead = lead, Now = clock, Simulation = true };
            var detail = await actions[s.Type].DescribeAsync(ctx, ct);
            steps.Add(new(s.Key, s.Name, s.Type, detail.StartsWith("would NOT", StringComparison.Ordinal) || detail.StartsWith("NOT AVAILABLE", StringComparison.Ordinal) ? "blocked" : "would run", detail));
            index++;
        }
        return Result.Success(new SimulationDto(true, "The workflow would start for this lead.", steps, ending));
    }

    private static WorkflowExecution SimExecution(Guid workflowId, Guid leadId) => WorkflowExecution.Create(Guid.Empty, workflowId, Guid.Empty, Guid.Empty, "simulation", leadId, null, null, null, true, null, DateTime.UtcNow);

    private static System.Text.Json.JsonElement? ToElement(object? o) => o is System.Text.Json.JsonElement e ? e : null;
}

internal sealed class WorkflowSettingsService(CrmContext crm, WorkflowSupport support, ILogger<WorkflowSettingsService> logger) : IWorkflowSettingsService
{
    public async Task<WorkflowSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var s = await support.SettingsAsync(ct);
        return await MapAsync(s, ct);
    }

    public Task<Result<WorkflowSettingsDto>> SaveLimitsAsync(SaveWorkflowSettingsRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var s = await support.SettingsAsync(ct, track: true);
            if (r.ExpectedVersion is { } v && v != s.Version) return Result.Failure<WorkflowSettingsDto>(Error.Conflict("The settings were changed by someone else. Reload and try again."));
            s.SetLimits(r.MaxConcurrentExecutions, r.MonthlyExecutionLimit, r.DailyMessageLimit, crm.Now, crm.Actor);
            crm.Record("Workflow.SettingsChanged", "WorkflowTenantSettings", s.Id, $"Workflow limits: {r.MaxConcurrentExecutions} concurrent, {r.MonthlyExecutionLimit} per month, {r.DailyMessageLimit} messages per day.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await MapAsync(s, ct));
        }, logger);

    public Task<Result<WorkflowSettingsDto>> SetKillSwitchAsync(bool on, string? reason, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var s = await support.SettingsAsync(ct, track: true);
            s.SetKillSwitch(on, reason, crm.Now, crm.Actor);
            crm.Record(on ? "Workflow.TenantKillSwitchOn" : "Workflow.TenantKillSwitchOff", "WorkflowTenantSettings", s.Id, on ? "Workspace kill switch for workflows turned ON." : "Workspace kill switch for workflows turned OFF.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await MapAsync(s, ct));
        }, logger);

    private async Task<WorkflowSettingsDto> MapAsync(WorkflowTenantSettings s, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc); var since = now.AddHours(-24);
        var month = await db.WorkflowExecutions.CountAsync(e => e.TenantId == tenantId && e.CreatedAtUtc >= monthStart, ct);
        var msgs = await db.Messages.CountAsync(m => m.TenantId == tenantId && m.IdempotencyKey != null && m.IdempotencyKey.StartsWith("wf:") && m.CreatedAtUtc >= since && m.Status != LeadPilot.Domain.Messaging.MessageStatus.Failed, ct);
        var running = await db.WorkflowExecutions.CountAsync(e => e.TenantId == tenantId && e.Status == ExecutionStatus.Running && e.LeaseUntilUtc > now, ct);
        return new WorkflowSettingsDto(s.KillSwitch, s.KillSwitchAtUtc, s.KillSwitchReason, s.MaxConcurrentExecutions, s.MonthlyExecutionLimit, s.DailyMessageLimit, month, msgs, running, s.Version);
    }
}

internal sealed class WorkflowNotificationService(CrmContext crm) : IWorkflowNotificationService
{
    public async Task<IReadOnlyList<WorkflowNotificationDto>> MineAsync(bool unreadOnly, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        if (crm.UserId is not { } me) return [];
        var q = db.WorkflowNotifications.AsNoTracking().Where(n => n.TenantId == tenantId && n.UserId == me);
        if (unreadOnly) q = q.Where(n => n.ReadAtUtc == null);
        var rows = await q.OrderByDescending(n => n.CreatedAtUtc).Take(100).ToListAsync(ct);
        var leadIds = rows.Select(r => r.LeadId).Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
        var names = await db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && leadIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        return rows.Select(n => new WorkflowNotificationDto(n.Id, n.LeadId, n.LeadId is { } l ? names.GetValueOrDefault(l) : null, n.Title, n.Body, n.CreatedAtUtc, n.ReadAtUtc is not null)).ToList();
    }

    public async Task<Result> MarkReadAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        if (crm.UserId is not { } me) return Result.Failure(Error.NotFound("Notification"));
        var n = await crm.Db.WorkflowNotifications.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId && x.UserId == me, ct);   // somebody else's notice is "not found"
        if (n is null) return Result.Failure(Error.NotFound("Notification"));
        n.MarkRead(crm.Now);
        await crm.Db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
