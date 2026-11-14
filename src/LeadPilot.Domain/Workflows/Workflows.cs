using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;

namespace LeadPilot.Domain.Workflows;

/// <summary>Draft = never activated; Active = a version is live; Inactive = switched off by a person.</summary>
public enum WorkflowStatus { Draft = 1, Active = 2, Inactive = 3 }

/// <summary>Draft = editable. Active/Retired = immutable (edits create a new version; running executions stay on theirs).</summary>
public enum VersionState { Draft = 1, Active = 2, Retired = 3 }

public enum StepType
{
    AssignLead = 1, ChangeLeadStatus, ChangeLeadStage, SendMessage, Wait, CreateFollowUp, NotifyUser, ScheduleDemo, EscalateToHuman, UpdateLeadFields, AddTag, Branch, Stop
}

/// <summary>What happens when a step fails for good: retry (bounded, then the execution fails and a person can retry), skip it and continue, or stop the execution.</summary>
public enum FailurePolicy { Retry = 1, Skip = 2, Stop = 3 }

/// <summary>Once = a lead enters a workflow at most once ever; AfterCompletion = again only after the previous execution ended; Always = overlapping executions allowed.</summary>
public enum ReEntryPolicy { Once = 1, AfterCompletion = 2, Always = 3 }

public enum ExecutionStatus { Pending = 1, Running = 2, Waiting = 3, Completed = 4, Failed = 5, Cancelled = 6, Stopped = 7 }

public enum LogOutcome { Started = 1, Succeeded = 2, Skipped = 3, Failed = 4, Retrying = 5, Waiting = 6, Blocked = 7, Cancelled = 8, Info = 9 }

/// <summary>Rows that become immutable once their workflow version is activated. The persistence guard rejects every change or delete of a sealed row.</summary>
public interface ISealable { bool IsSealed { get; } }

public class Workflow : AuditableEntity, ITenantOwned
{
    private Workflow() { }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public WorkflowStatus Status { get; private set; } = WorkflowStatus.Draft;
    public Guid? ActiveVersionId { get; private set; }
    public int LatestVersionNumber { get; private set; }
    /// <summary>Per-workflow kill switch: nothing starts and running executions stop at their next step.</summary>
    public bool IsKilled { get; private set; }
    public DateTime? KilledAtUtc { get; private set; }
    public string? KillReason { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }
    public int Version { get; private set; } = 1;

    public static Workflow Create(Guid tenantId, string name, string? description, DateTime nowUtc, string? actor)
    {
        var w = new Workflow { TenantId = tenantId, CreatedAtUtc = nowUtc, CreatedBy = actor };
        w.Describe(name, description, nowUtc, actor, touch: false);
        return w;
    }

    public void Describe(string name, string? description, DateTime nowUtc, string? actor, bool touch = true)
    {
        Name = Guard.Required(name, 150, "Workflow name"); Description = Guard.Optional(description, 1000, "Description");
        if (touch) Touch(nowUtc, actor);
    }

    public int NextVersionNumber() => ++LatestVersionNumber;

    public void Activate(Guid versionId, DateTime nowUtc, string? actor)
    {
        if (IsDeleted) throw new DomainException("A deleted workflow cannot be activated.");
        ActiveVersionId = versionId; Status = WorkflowStatus.Active; Touch(nowUtc, actor);
    }

    public void Deactivate(DateTime nowUtc, string? actor)
    {
        if (Status != WorkflowStatus.Active) throw new DomainException("The workflow is not active.");
        Status = WorkflowStatus.Inactive; Touch(nowUtc, actor);
    }

    public void Kill(string? reason, DateTime nowUtc, string? actor) { IsKilled = true; KilledAtUtc = nowUtc; KillReason = Guard.Optional(reason, 300, "Reason"); Touch(nowUtc, actor); }
    public void Unkill(DateTime nowUtc, string? actor) { IsKilled = false; KilledAtUtc = null; KillReason = null; Touch(nowUtc, actor); }

    public void Delete(DateTime nowUtc, string? actor)
    {
        if (Status == WorkflowStatus.Active) throw new DomainException("Deactivate the workflow before deleting it.");
        IsDeleted = true; DeletedAtUtc = nowUtc; Touch(nowUtc, actor);
    }

    private void Touch(DateTime nowUtc, string? actor) { UpdatedAtUtc = nowUtc; UpdatedBy = actor; Version++; }
}

public class WorkflowVersion : Entity, ITenantOwned
{
    public const int MaxCooldownHours = 24 * 365;

    private WorkflowVersion() { }
    public Guid TenantId { get; private set; }
    public Guid WorkflowId { get; private set; }
    public int Number { get; private set; }
    public VersionState State { get; private set; } = VersionState.Draft;
    public ReEntryPolicy ReEntry { get; private set; } = ReEntryPolicy.AfterCompletion;
    /// <summary>Minimum hours between two starts for the same lead (0 = none).</summary>
    public int CooldownHours { get; private set; }
    /// <summary>Frequency cap: at most this many starts per lead in 30 days (0 = no cap).</summary>
    public int MaxStartsPerLeadPer30Days { get; private set; } = 3;
    public string? Note { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public string? CreatedBy { get; private set; }
    public DateTime? ActivatedAtUtc { get; private set; }
    public DateTime? RetiredAtUtc { get; private set; }

    public static WorkflowVersion Create(Guid tenantId, Guid workflowId, int number, ReEntryPolicy reEntry, int cooldownHours, int maxStarts, string? note, DateTime nowUtc, string? actor)
    {
        var v = new WorkflowVersion { TenantId = tenantId, WorkflowId = workflowId, Number = number, CreatedAtUtc = nowUtc, CreatedBy = actor };
        v.Configure(reEntry, cooldownHours, maxStarts, note);
        return v;
    }

    public void Configure(ReEntryPolicy reEntry, int cooldownHours, int maxStarts, string? note)
    {
        EnsureEditable();
        if (!Enum.IsDefined(reEntry)) throw new DomainException("Unknown re-entry policy.");
        if (cooldownHours is < 0 or > MaxCooldownHours) throw new DomainException($"The cool-down must be between 0 and {MaxCooldownHours} hours.");
        if (maxStarts is < 0 or > 100) throw new DomainException("The frequency cap must be between 0 and 100 starts per 30 days.");
        ReEntry = reEntry; CooldownHours = cooldownHours; MaxStartsPerLeadPer30Days = maxStarts; Note = Guard.Optional(note, 300, "Note");
    }

    public void EnsureEditable()
    {
        if (State != VersionState.Draft) throw new DomainException("This version is active or retired and cannot be changed. Create a new version to edit it.");
    }

    public void Activate(DateTime nowUtc) { EnsureEditable(); State = VersionState.Active; ActivatedAtUtc = nowUtc; }
    public void Retire(DateTime nowUtc) { if (State != VersionState.Active) throw new DomainException("Only the active version can be retired."); State = VersionState.Retired; RetiredAtUtc = nowUtc; }
}

public class WorkflowTrigger : Entity, ITenantOwned, ISealable
{
    private WorkflowTrigger() { }
    public Guid TenantId { get; private set; }
    public Guid WorkflowVersionId { get; private set; }
    /// <summary>Event key from the trigger catalog, e.g. "lead.created".</summary>
    public string EventType { get; private set; } = string.Empty;
    /// <summary>Declarative condition (JSON of the condition language) evaluated against the lead when the event arrives; null = always.</summary>
    public string? ConditionJson { get; private set; }
    /// <summary>Trigger settings (e.g. {"inactiveDays":7}).</summary>
    public string? ConfigJson { get; private set; }
    public bool IsSealed { get; private set; }

    public static WorkflowTrigger Create(Guid tenantId, Guid versionId, string eventType, string? conditionJson, string? configJson) =>
        new() { TenantId = tenantId, WorkflowVersionId = versionId, EventType = Guard.Required(eventType, 60, "Trigger"), ConditionJson = conditionJson, ConfigJson = configJson };

    public void Seal() => IsSealed = true;
}

public class WorkflowStep : Entity, ITenantOwned, ISealable
{
    private WorkflowStep() { }
    public Guid TenantId { get; private set; }
    public Guid WorkflowVersionId { get; private set; }
    /// <summary>Stable id of the step inside the version (used by branches and idempotency keys).</summary>
    public string Key { get; private set; } = string.Empty;
    public int Position { get; private set; }
    public StepType Type { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string ConfigJson { get; private set; } = "{}";
    /// <summary>Optional "run only if" condition; when false the step is skipped.</summary>
    public string? ConditionJson { get; private set; }
    public FailurePolicy OnFailure { get; private set; } = FailurePolicy.Retry;
    public int MaxRetries { get; private set; } = 2;
    public bool IsSealed { get; private set; }

    public static WorkflowStep Create(Guid tenantId, Guid versionId, string key, int position, StepType type, string name, string configJson, string? conditionJson, FailurePolicy onFailure, int maxRetries) =>
        new()
        {
            TenantId = tenantId, WorkflowVersionId = versionId, Key = Guard.Required(key, 20, "Step key"), Position = position, Type = type, Name = Guard.Required(name, 100, "Step name"),
            ConfigJson = configJson, ConditionJson = conditionJson, OnFailure = onFailure, MaxRetries = Math.Clamp(maxRetries, 0, 5)
        };

    public void Seal() => IsSealed = true;
}

/// <summary>
/// One run of one workflow version for one lead. Unique per (version, trigger event, lead): redelivered or concurrent events never start it twice.
/// All progress is persisted here (step states, next run time, lease), so a crash resumes from this row.
/// </summary>
public class WorkflowExecution : Entity, ITenantOwned
{
    private WorkflowExecution() { }
    public Guid TenantId { get; private set; }
    public Guid WorkflowId { get; private set; }
    public Guid WorkflowVersionId { get; private set; }
    /// <summary>Id of the domain event (outbox message) that started this run; a deterministic id for sweeps, a fresh one for manual runs.</summary>
    public Guid TriggerEventId { get; private set; }
    public string TriggerKey { get; private set; } = string.Empty;
    /// <summary>The lead this run works on.</summary>
    public Guid EntityId { get; private set; }
    public Guid? ContactId { get; private set; }
    public Guid? ConversationId { get; private set; }
    public ExecutionStatus Status { get; private set; } = ExecutionStatus.Pending;
    public string? CurrentStepKey { get; private set; }
    public string StateJson { get; private set; } = "{}";
    public DateTime NextRunAtUtc { get; private set; }
    public DateTime? LeaseUntilUtc { get; private set; }
    public string? StatusReason { get; private set; }
    public string? StartedBy { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? EndedAtUtc { get; private set; }
    /// <summary>"A" while the run is alive (unique per workflow+lead for the Once/AfterCompletion policies, so concurrent triggers cannot both start); null once ended.</summary>
    public string? ActiveSlot { get; private set; }
    public int Version { get; private set; } = 1;

    public bool IsTerminal => Status is ExecutionStatus.Completed or ExecutionStatus.Failed or ExecutionStatus.Cancelled or ExecutionStatus.Stopped;

    public static WorkflowExecution Create(Guid tenantId, Guid workflowId, Guid versionId, Guid triggerEventId, string triggerKey, Guid leadId, Guid? contactId, Guid? conversationId, string? firstStepKey,
        bool exclusive, string? startedBy, DateTime nowUtc)
    {
        var e = new WorkflowExecution
        {
            TenantId = tenantId, WorkflowId = workflowId, WorkflowVersionId = versionId, TriggerEventId = triggerEventId, TriggerKey = Guard.Required(triggerKey, 60, "Trigger"), EntityId = leadId,
            ContactId = contactId, ConversationId = conversationId, CurrentStepKey = firstStepKey, StartedBy = startedBy, CreatedAtUtc = nowUtc, NextRunAtUtc = nowUtc
        };
        e.ActiveSlot = exclusive ? "A" : e.Id.ToString("N");
        return e;
    }

    public void SetState(string stateJson, string? currentStepKey) { StateJson = stateJson; CurrentStepKey = currentStepKey; Version++; }

    public void MarkRunning(DateTime nowUtc, DateTime leaseUntil) { Status = ExecutionStatus.Running; StartedAtUtc ??= nowUtc; LeaseUntilUtc = leaseUntil; Version++; }

    public void MarkWaiting(DateTime nextRunUtc, string? reason) { Status = ExecutionStatus.Waiting; NextRunAtUtc = nextRunUtc; LeaseUntilUtc = null; StatusReason = reason; Version++; }

    public void End(ExecutionStatus terminal, string? reason, DateTime nowUtc)
    {
        if (terminal is not (ExecutionStatus.Completed or ExecutionStatus.Failed or ExecutionStatus.Cancelled or ExecutionStatus.Stopped)) throw new DomainException("Not a final status.");
        if (IsTerminal && terminal != Status) throw new DomainException("This execution has already ended.");
        Status = terminal; StatusReason = reason is { Length: > 500 } ? reason[..500] : reason; EndedAtUtc = nowUtc; LeaseUntilUtc = null; ActiveSlot = null; Version++;
    }

    /// <summary>Manual retry of a failed run: back to Waiting at <paramref name="nowUtc"/> from the failed step.</summary>
    public void Reopen(string? stepKey, bool exclusive, DateTime nowUtc)
    {
        if (Status != ExecutionStatus.Failed) throw new DomainException("Only a failed execution can be retried.");
        Status = ExecutionStatus.Waiting; EndedAtUtc = null; StatusReason = null; NextRunAtUtc = nowUtc; LeaseUntilUtc = null; CurrentStepKey = stepKey ?? CurrentStepKey; ActiveSlot = exclusive ? "A" : Id.ToString("N"); Version++;
    }
}

/// <summary>Per-step log of an execution. Append-only.</summary>
public class WorkflowExecutionLog : Entity, ITenantOwned, IAppendOnly
{
    private WorkflowExecutionLog() { }
    public Guid TenantId { get; private set; }
    public Guid WorkflowExecutionId { get; private set; }
    public string? StepKey { get; private set; }
    public string? StepName { get; private set; }
    public StepType? StepType { get; private set; }
    public int Attempt { get; private set; }
    public LogOutcome Outcome { get; private set; }
    public string Message { get; private set; } = string.Empty;
    /// <summary>Reference to what the step did (message id, follow-up id ...).</summary>
    public string? EffectRef { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    public static WorkflowExecutionLog Create(Guid tenantId, Guid executionId, string? stepKey, string? stepName, StepType? type, int attempt, LogOutcome outcome, string message, string? effectRef, DateTime nowUtc) =>
        new()
        {
            TenantId = tenantId, WorkflowExecutionId = executionId, StepKey = stepKey, StepName = stepName, StepType = type, Attempt = attempt, Outcome = outcome,
            Message = message.Length > 1000 ? message[..1000] : message, EffectRef = effectRef is { Length: > 100 } ? effectRef[..100] : effectRef, OccurredAtUtc = nowUtc
        };
}

/// <summary>Workspace-wide workflow controls: kill switch and limits. One row per tenant, created on first use.</summary>
public class WorkflowTenantSettings : AuditableEntity, ITenantOwned
{
    private WorkflowTenantSettings() { }
    public Guid TenantId { get; private set; }
    public bool KillSwitch { get; private set; }
    public DateTime? KillSwitchAtUtc { get; private set; }
    public string? KillSwitchReason { get; private set; }
    /// <summary>Executions that may run at the same time.</summary>
    public int MaxConcurrentExecutions { get; private set; } = 20;
    /// <summary>Executions that may start per calendar month (0 = unlimited).</summary>
    public int MonthlyExecutionLimit { get; private set; } = 5000;
    /// <summary>Messages workflows may queue per 24 hours (on top of the account and workspace sending limits).</summary>
    public int DailyMessageLimit { get; private set; } = 200;
    public int Version { get; private set; } = 1;

    public static WorkflowTenantSettings Create(Guid tenantId, DateTime nowUtc) => new() { TenantId = tenantId, CreatedAtUtc = nowUtc };

    public void SetKillSwitch(bool on, string? reason, DateTime nowUtc, string? actor)
    {
        KillSwitch = on; KillSwitchAtUtc = on ? nowUtc : null; KillSwitchReason = on ? Guard.Optional(reason, 300, "Reason") : null; Touch(nowUtc, actor);
    }

    public void SetLimits(int maxConcurrent, int monthly, int dailyMessages, DateTime nowUtc, string? actor)
    {
        if (maxConcurrent is < 1 or > 500) throw new DomainException("Concurrent executions must be between 1 and 500.");
        if (monthly is < 0 or > 1_000_000) throw new DomainException("The monthly execution limit must be between 0 (unlimited) and 1,000,000.");
        if (dailyMessages is < 0 or > 10_000) throw new DomainException("The daily message limit must be between 0 (no messages) and 10,000.");
        MaxConcurrentExecutions = maxConcurrent; MonthlyExecutionLimit = monthly; DailyMessageLimit = dailyMessages; Touch(nowUtc, actor);
    }

    private void Touch(DateTime nowUtc, string? actor) { UpdatedAtUtc = nowUtc; UpdatedBy = actor; Version++; }
}

/// <summary>In-app notice for a team member created by a "notify sales representative" step. Unique per (execution, step, user): a replayed step never notifies twice.</summary>
public class WorkflowNotification : Entity, ITenantOwned
{
    private WorkflowNotification() { }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid? LeadId { get; private set; }
    public Guid WorkflowExecutionId { get; private set; }
    public string StepKey { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ReadAtUtc { get; private set; }

    public static WorkflowNotification Create(Guid tenantId, Guid userId, Guid? leadId, Guid executionId, string stepKey, string title, string body, DateTime nowUtc) =>
        new() { TenantId = tenantId, UserId = userId, LeadId = leadId, WorkflowExecutionId = executionId, StepKey = stepKey, Title = Guard.Required(title, 150, "Title"), Body = Guard.Required(body, 1000, "Message"), CreatedAtUtc = nowUtc };

    public void MarkRead(DateTime nowUtc) { ReadAtUtc ??= nowUtc; }
}
