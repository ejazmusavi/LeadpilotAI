using System.Text.Json;
using Hangfire;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Domain.Workflows;
using LeadPilot.Infrastructure.Messaging.Services;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Infrastructure.Services.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Workflows;

/// <summary>
/// The one place an execution is created. Decides, in this order: kill switches → lead usable → already started by this event (idempotency) → trigger condition → monthly usage limit → re-entry policy,
/// cool-down and per-lead frequency cap → create (unique per version+event+lead; exclusive per workflow+lead for the Once/AfterCompletion policies, so two concurrent events cannot both start) → schedule.
/// </summary>
internal sealed class WorkflowTriggerService(CrmContext crm, WorkflowSupport support, IContactEligibilityService eligibility, IWorkflowJobScheduler jobs, ILogger<WorkflowTriggerService> logger) : IWorkflowTriggerService
{
    public async Task<IReadOnlyList<WorkflowStartResult>> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var results = new List<WorkflowStartResult>();
        Guid? leadId, contactId = null, conversationId = null, messageId = null;

        if (envelope.Type == MessagingEventTypes.MessageReceived)
        {
            var p = JsonSerializer.Deserialize<InboundReceivedPayload>(envelope.Payload) ?? throw new PoisonMessageException("payload_unreadable");
            var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == p.ConversationId && c.TenantId == tenantId, ct) ?? throw new PoisonMessageException("conversation_not_found_for_tenant");
            conversationId = conv.Id; messageId = p.MessageId; contactId = conv.ContactId;
            leadId = contactId is { } cid ? await db.Contacts.AsNoTracking().Where(c => c.Id == cid && c.TenantId == tenantId && !c.IsDeleted).Select(c => (Guid?)c.LeadId).FirstOrDefaultAsync(ct) : null;
            if (leadId is null) return results;   // a conversation that is not linked to a lead has nothing to run against
        }
        else
        {
            var p = JsonSerializer.Deserialize<WorkflowEventPayload>(envelope.Payload) ?? throw new PoisonMessageException("payload_unreadable");
            if (!await db.Leads.AnyAsync(l => l.Id == p.LeadId && l.TenantId == tenantId, ct)) throw new PoisonMessageException("lead_not_found_for_tenant");
            leadId = p.LeadId; contactId = p.ContactId; conversationId = p.ConversationId; messageId = p.MessageId;
        }

        // "lead becomes contact-eligible" only counts when the contact REALLY is eligible now
        if (envelope.Type == WorkflowEventTypes.LeadContactEligible && (contactId is null || !(await eligibility.CheckAsync(contactId.Value, ConsentChannel.WhatsApp, ConsentPurpose.Marketing, ct)).IsEligible))
            return results;

        var targets = await (from t in db.WorkflowTriggers.AsNoTracking()
                             join v in db.WorkflowVersions.AsNoTracking() on t.WorkflowVersionId equals v.Id
                             join w in db.Workflows.AsNoTracking() on v.WorkflowId equals w.Id
                             where t.TenantId == tenantId && v.TenantId == tenantId && w.TenantId == tenantId && t.EventType == envelope.Type && v.State == VersionState.Active
                                   && w.ActiveVersionId == v.Id && w.Status == WorkflowStatus.Active && !w.IsDeleted && !w.IsKilled
                             select new { WorkflowId = w.Id, VersionId = v.Id }).ToListAsync(ct);
        foreach (var t in targets)
            results.Add(await StartAsync(t.WorkflowId, t.VersionId, envelope.Type, envelope.MessageId, leadId!.Value, contactId, conversationId, true, null, ct, messageId));
        return results;
    }

    public Task<WorkflowStartResult> StartAsync(Guid workflowId, Guid versionId, string triggerKey, Guid triggerEventId, Guid leadId, Guid? contactId, Guid? conversationId, bool checkTriggerCondition, string? startedBy, CancellationToken ct = default) =>
        StartAsync(workflowId, versionId, triggerKey, triggerEventId, leadId, contactId, conversationId, checkTriggerCondition, startedBy, ct, null);

    private async Task<WorkflowStartResult> StartAsync(Guid workflowId, Guid versionId, string triggerKey, Guid triggerEventId, Guid leadId, Guid? contactId, Guid? conversationId, bool checkTriggerCondition, string? startedBy, CancellationToken ct, Guid? messageId)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        WorkflowStartResult Blocked(string why) { logger.LogInformation("Workflow {WorkflowId} not started for lead {LeadId}: {Why}", workflowId, leadId, why); return new(WorkflowStartOutcome.Blocked, null, why); }

        var settings = await support.SettingsAsync(ct);
        if (settings.KillSwitch) return Blocked("The workspace kill switch is on.");
        var wf = await db.Workflows.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workflowId && w.TenantId == tenantId && !w.IsDeleted, ct);
        if (wf is null) return Blocked("The workflow does not exist.");
        if (wf.IsKilled) return Blocked("The workflow's kill switch is on.");
        if (wf.Status != WorkflowStatus.Active || wf.ActiveVersionId != versionId) return Blocked("The workflow is not active.");
        var version = await db.WorkflowVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId && v.TenantId == tenantId && v.WorkflowId == workflowId && v.State == VersionState.Active, ct);
        if (version is null) return Blocked("That version is not active.");
        var lead = await db.Leads.AsNoTracking().Where(l => l.Id == leadId && l.TenantId == tenantId).Select(l => new { l.IsArchived }).FirstOrDefaultAsync(ct);
        if (lead is null || lead.IsArchived) return Blocked("The lead does not exist or is archived.");

        // idempotency: this event already started this version for this lead
        if (await db.WorkflowExecutions.AnyAsync(e => e.WorkflowVersionId == versionId && e.TenantId == tenantId && e.TriggerEventId == triggerEventId && e.EntityId == leadId, ct))
            return new(WorkflowStartOutcome.Duplicate, null, "Already started by this event.");

        if (checkTriggerCondition)
        {
            var trigger = await db.WorkflowTriggers.AsNoTracking().FirstOrDefaultAsync(t => t.WorkflowVersionId == versionId && t.TenantId == tenantId && t.EventType == triggerKey, ct);
            if (trigger is null) return new(WorkflowStartOutcome.NotMatched, null, "The version has no such trigger.");
            if (trigger.ConditionJson is not null)
            {
                var facts = await support.FactsAsync(leadId, contactId, conversationId, messageId, triggerKey, ct);
                if (!ConditionEvaluator.EvaluateJson(trigger.ConditionJson, facts, now, await support.TimeZoneAsync(ct))) return new(WorkflowStartOutcome.NotMatched, null, "The trigger condition was not met.");
            }
        }

        // usage limit
        if (settings.MonthlyExecutionLimit > 0)
        {
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            if (await db.WorkflowExecutions.CountAsync(e => e.TenantId == tenantId && e.CreatedAtUtc >= monthStart, ct) >= settings.MonthlyExecutionLimit) return Blocked($"The workspace reached its limit of {settings.MonthlyExecutionLimit} workflow runs this month.");
        }

        // re-entry policy, cool-down, frequency cap
        var history = db.WorkflowExecutions.AsNoTracking().Where(e => e.TenantId == tenantId && e.WorkflowId == workflowId && e.EntityId == leadId);
        if (version.ReEntry == ReEntryPolicy.Once && await history.AnyAsync(ct)) return Blocked("This lead already went through this workflow (re-entry policy: once).");
        if (version.ReEntry == ReEntryPolicy.AfterCompletion && await history.AnyAsync(e => e.ActiveSlot != null, ct)) return Blocked("A run for this lead is still in progress.");
        if (version.CooldownHours > 0 && await history.AnyAsync(e => e.CreatedAtUtc > now.AddHours(-version.CooldownHours), ct)) return Blocked($"Cool-down: this lead started this workflow less than {version.CooldownHours} h ago.");
        if (version.MaxStartsPerLeadPer30Days > 0 && await history.CountAsync(e => e.CreatedAtUtc > now.AddDays(-30), ct) >= version.MaxStartsPerLeadPer30Days) return Blocked($"Frequency cap: this lead already started this workflow {version.MaxStartsPerLeadPer30Days} times in 30 days.");

        var first = await db.WorkflowSteps.AsNoTracking().Where(s => s.WorkflowVersionId == versionId && s.TenantId == tenantId).OrderBy(s => s.Position).Select(s => s.Key).FirstOrDefaultAsync(ct);
        if (first is null) return Blocked("The version has no steps.");
        var exec = WorkflowExecution.Create(tenantId, workflowId, versionId, triggerEventId, triggerKey, leadId, contactId, conversationId, first, exclusive: version.ReEntry != ReEntryPolicy.Always, startedBy, now);
        db.WorkflowExecutions.Add(exec);
        db.WorkflowExecutionLogs.Add(WorkflowExecutionLog.Create(tenantId, exec.Id, null, null, null, 0, LogOutcome.Started, $"Started by '{triggerKey}'" + (startedBy is null ? "." : $" ({startedBy})."), null, now));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ResultFlow.IsDuplicate(ex))
        {
            // lost a race: either the very same event (idempotent) or another live run of this lead (exclusivity)
            db.Entry(exec).State = EntityState.Detached;
            foreach (var e in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList()) e.State = EntityState.Detached;
            var same = await db.WorkflowExecutions.AsNoTracking().AnyAsync(e => e.WorkflowVersionId == versionId && e.TenantId == tenantId && e.TriggerEventId == triggerEventId && e.EntityId == leadId, ct);
            return same ? new(WorkflowStartOutcome.Duplicate, null, "Already started by this event.") : Blocked("A run for this lead is still in progress.");
        }
        try { jobs.EnqueueRun(tenantId, exec.Id); }
        catch (Exception ex) { logger.LogWarning(ex, "The run job of execution {ExecutionId} could not be scheduled; the sweep will pick it up", exec.Id); }
        return new(WorkflowStartOutcome.Started, exec.Id, null);
    }

    // ---------------------------------------------------------------- inactivity (Hangfire-evaluated)

    public async Task<int> RunInactivitySweepAsync(CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now; var started = 0;
        var triggers = await (from t in db.WorkflowTriggers.AsNoTracking()
                              join v in db.WorkflowVersions.AsNoTracking() on t.WorkflowVersionId equals v.Id
                              join w in db.Workflows.AsNoTracking() on v.WorkflowId equals w.Id
                              where t.TenantId == tenantId && t.EventType == WorkflowEventTypes.LeadInactive && v.State == VersionState.Active && w.ActiveVersionId == v.Id && w.Status == WorkflowStatus.Active && !w.IsDeleted && !w.IsKilled
                              select new { WorkflowId = w.Id, VersionId = v.Id, t.ConfigJson }).ToListAsync(ct);
        foreach (var t in triggers)
        {
            var days = 7;
            try { if (t.ConfigJson is not null && JsonDocument.Parse(t.ConfigJson).RootElement.TryGetProperty("inactiveDays", out var d) && d.TryGetInt32(out var parsed)) days = Math.Clamp(parsed, 1, 365); } catch (JsonException) { }
            var cutoff = now.AddDays(-days);
            var batch = await (from l in db.Leads.AsNoTracking()
                               join s in db.PipelineStages.AsNoTracking() on l.PipelineStageId equals s.Id
                               where l.TenantId == tenantId && s.TenantId == tenantId && !l.IsArchived && s.Kind == StageKind.Open && l.LastActivityAtUtc != null && l.LastActivityAtUtc < cutoff
                               orderby l.LastActivityAtUtc select new { l.Id, Last = l.LastActivityAtUtc!.Value }).Take(100).ToListAsync(ct);
            foreach (var l in batch)
            {
                var r = await StartAsync(t.WorkflowId, t.VersionId, WorkflowEventTypes.LeadInactive, WorkflowIdempotency.SweepEventId(t.WorkflowId, l.Id, l.Last), l.Id, null, null, true, "inactivity check", ct, null);
                if (r.Outcome == WorkflowStartOutcome.Started) started++;
            }
        }
        return started;
    }
}

/// <summary>Consumer of the workflow domain events (RabbitMQ, durable): matches active workflows and starts executions. Idempotent by (consumer, message id) AND by the execution's unique key.</summary>
internal sealed class WorkflowEventHandler(CrmContext crm, IWorkflowTriggerService triggers) : IMessagingEventHandler
{
    public const string Name = "workflows.triggers";
    public string ConsumerName => Name;

    public async Task HandleAsync(EventEnvelope envelope, int attempt, CancellationToken ct)
    {
        if (await ConsumerIdempotency.AlreadyProcessedAsync(crm, Name, envelope.MessageId, ct)) return;
        await triggers.HandleEventAsync(envelope, ct);
        await ConsumerIdempotency.CommitAsync(crm, Name, envelope.MessageId, ct);
    }
}

// ---------------------------------------------------------------- Hangfire

public static class WorkflowQueues { public const string Workflows = "workflows"; }

/// <summary>Hangfire entry point of one execution. Arguments are ids only; the tenant scope is established here and fails closed. The engine is idempotent (lease, step state), so automatic retry is safe.</summary>
public sealed class WorkflowJob(ITenantScopeRunner scopes)
{
    [Queue(WorkflowQueues.Workflows)]
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [20, 60], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task RunAsync(Guid tenantId, Guid executionId, CancellationToken ct) =>
        scopes.RunAsync(tenantId, (sp, c) => sp.GetRequiredService<IWorkflowEngine>().RunAsync(executionId, c), ct);
}

/// <summary>Every minute: crash recovery and due waits (executions whose runner died or whose enqueue was lost). Every hour: the inactivity trigger.</summary>
public sealed class WorkflowSweepJob(ITenantScopeRunner scopes, IClock clock, ILogger<WorkflowSweepJob> logger)
{
    public const string RecurringId = "workflow-sweep", InactivityId = "workflow-inactivity";

    [Queue(WorkflowQueues.Workflows)]
    [DisableConcurrentExecution(120)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(CancellationToken ct)
    {
        List<Guid> tenants = [];
        await scopes.RunPlatformJobAsync(async (sp, c) => tenants = await sp.GetRequiredService<WorkflowDirectory>().TenantsWithDueExecutionsAsync(clock.UtcNow, c), ct);
        foreach (var id in tenants)
        {
            try { await scopes.RunAsync(id, (sp, c) => sp.GetRequiredService<IWorkflowEngine>().SweepAsync(c), ct); }
            catch (TenantUnavailableException) { }
            catch (Exception ex) { logger.LogError(ex, "Workflow sweep failed for tenant {TenantId}", id); }
        }
    }

    [Queue(WorkflowQueues.Workflows)]
    [DisableConcurrentExecution(600)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunInactivityAsync(CancellationToken ct)
    {
        List<Guid> tenants = [];
        await scopes.RunPlatformJobAsync(async (sp, c) => tenants = await sp.GetRequiredService<WorkflowDirectory>().TenantsWithInactivityTriggersAsync(c), ct);
        foreach (var id in tenants)
        {
            try { await scopes.RunAsync(id, (sp, c) => sp.GetRequiredService<IWorkflowTriggerService>().RunInactivitySweepAsync(c), ct); }
            catch (TenantUnavailableException) { }
            catch (Exception ex) { logger.LogError(ex, "Workflow inactivity sweep failed for tenant {TenantId}", id); }
        }
    }

    public static void ScheduleRecurring(IRecurringJobManager recurring)
    {
        recurring.AddOrUpdate<WorkflowSweepJob>(RecurringId, x => x.RunAsync(CancellationToken.None), Cron.Minutely());
        recurring.AddOrUpdate<WorkflowSweepJob>(InactivityId, x => x.RunInactivityAsync(CancellationToken.None), Cron.Hourly());
    }
}

public sealed class HangfireWorkflowScheduler(IBackgroundJobClient client) : IWorkflowJobScheduler
{
    public string EnqueueRun(Guid tenantId, Guid executionId, TimeSpan? delay = null) =>
        delay is { } d && d > TimeSpan.Zero
            ? client.Schedule<WorkflowJob>(x => x.RunAsync(tenantId, executionId, CancellationToken.None), d)
            : client.Enqueue<WorkflowJob>(x => x.RunAsync(tenantId, executionId, CancellationToken.None));
}

/// <summary>Cross-tenant ids needed BEFORE a tenant scope exists (the sweeps). Returns ids only; every piece of work then runs inside its own tenant scope.</summary>
internal sealed class WorkflowDirectory(AppDbContext db)
{
    public async Task<List<Guid>> TenantsWithDueExecutionsAsync(DateTime now, CancellationToken ct) =>
        await db.WorkflowExecutions.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Status == ExecutionStatus.Pending || (e.Status == ExecutionStatus.Waiting && e.NextRunAtUtc <= now) || (e.Status == ExecutionStatus.Running && e.LeaseUntilUtc <= now))
            .Select(e => e.TenantId).Distinct().Take(500).ToListAsync(ct);

    public async Task<List<Guid>> TenantsWithInactivityTriggersAsync(CancellationToken ct) =>
        await db.WorkflowTriggers.IgnoreQueryFilters().AsNoTracking().Where(t => t.EventType == WorkflowEventTypes.LeadInactive)
            .Join(db.WorkflowVersions.IgnoreQueryFilters().Where(v => v.State == VersionState.Active), t => t.WorkflowVersionId, v => v.Id, (t, v) => t.TenantId).Distinct().Take(500).ToListAsync(ct);
}
