using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Workflows;
using LeadPilot.Shared.Results;

namespace LeadPilot.Application.Workflows;

public sealed class WorkflowOptions
{
    public const string Section = "Workflows";
    /// <summary>How long one runner owns an execution before another may take it over (crash recovery).</summary>
    public int LeaseSeconds { get; set; } = 120;
    /// <summary>Steps one job run executes before it hands over (a run is also bounded by the definition's loop check).</summary>
    public int MaxStepsPerRun { get; set; } = 60;
    /// <summary>Executions the sweep pushes per tenant per minute.</summary>
    public int SweepBatch { get; set; } = 50;
    /// <summary>Leads the inactivity sweep may start per workflow per run.</summary>
    public int InactivityBatch { get; set; } = 100;
    public int ExecutionRetentionDays { get; set; } = 180;
}

// ---------- DTOs ----------

public sealed record WorkflowListItemDto(Guid Id, string Name, string? Description, WorkflowStatus Status, bool IsKilled, int? ActiveVersion, int LatestVersion, bool HasDraft, IReadOnlyList<string> Triggers, int RunningExecutions, DateTime UpdatedAtUtc);

public sealed record WorkflowVersionSummaryDto(Guid Id, int Number, VersionState State, string? Note, DateTime CreatedAtUtc, string? CreatedBy, DateTime? ActivatedAtUtc, DateTime? RetiredAtUtc, int Steps, int Executions);

public sealed record TriggerDto(string EventType, string Title, bool Available, object? Condition, int? InactiveDays);
public sealed record StepDto(string Key, int Position, StepType Type, string TypeTitle, string Name, object? Config, object? Condition, FailurePolicy OnFailure, int MaxRetries);

public sealed record WorkflowVersionDto(Guid Id, Guid WorkflowId, int Number, VersionState State, ReEntryPolicy ReEntry, int CooldownHours, int MaxStartsPerLeadPer30Days, string? Note,
    DateTime CreatedAtUtc, DateTime? ActivatedAtUtc, IReadOnlyList<TriggerDto> Triggers, IReadOnlyList<StepDto> Steps, ValidationReport Validation);

public sealed record WorkflowDto(Guid Id, string Name, string? Description, WorkflowStatus Status, bool IsKilled, string? KillReason, DateTime? KilledAtUtc, Guid? ActiveVersionId, int? ActiveVersion,
    int LatestVersion, int Version, DateTime CreatedAtUtc, DateTime? UpdatedAtUtc, IReadOnlyList<WorkflowVersionSummaryDto> Versions, WorkflowVersionDto? Draft, WorkflowVersionDto? Active);

public sealed record ExecutionListItemDto(Guid Id, Guid WorkflowId, string WorkflowName, int VersionNumber, string TriggerKey, Guid LeadId, string? LeadName, ExecutionStatus Status, string? StatusReason,
    string? CurrentStepKey, DateTime CreatedAtUtc, DateTime? EndedAtUtc, DateTime NextRunAtUtc);

public sealed record ExecutionLogDto(Guid Id, string? StepKey, string? StepName, StepType? StepType, int Attempt, LogOutcome Outcome, string Message, string? EffectRef, DateTime OccurredAtUtc);

public sealed record ExecutionStepStateDto(string Key, string Name, StepType Type, string Status, int Attempts, string? Effect, string? Error, DateTime? WaitUntilUtc);

public sealed record ExecutionDto(ExecutionListItemDto Summary, Guid? ContactId, Guid? ConversationId, string? StartedBy, IReadOnlyList<ExecutionStepStateDto> Steps, IReadOnlyList<ExecutionLogDto> Logs, bool CanRetry, bool CanCancel);

public sealed record ExecutionQuery
{
    public Guid? WorkflowId { get; init; }
    public ExecutionStatus? Status { get; init; }
    public Guid? LeadId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}

public sealed record WorkflowQuery { public string? Search { get; init; } public WorkflowStatus? Status { get; init; } public int Page { get; init; } = 1; public int PageSize { get; init; } = 25; }

public sealed record SimulationStepDto(string Key, string Name, StepType Type, string Outcome, string Detail);
public sealed record SimulationDto(bool Triggered, string TriggerNote, IReadOnlyList<SimulationStepDto> Steps, string Ending, bool SideEffects = false);

public sealed record WorkflowSettingsDto(bool KillSwitch, DateTime? KillSwitchAtUtc, string? KillSwitchReason, int MaxConcurrentExecutions, int MonthlyExecutionLimit, int DailyMessageLimit,
    int ExecutionsThisMonth, int MessagesLast24Hours, int RunningNow, int Version);

public sealed record WorkflowNotificationDto(Guid Id, Guid? LeadId, string? LeadName, string Title, string Body, DateTime CreatedAtUtc, bool IsRead);

public sealed record CatalogOption(string Value, string Label, string? Detail = null);
public sealed record WorkflowCatalogDto(IReadOnlyList<TriggerInfo> Triggers, IReadOnlyList<StepTypeInfo> Actions, IReadOnlyList<ConditionField> Fields, IReadOnlyDictionary<string, IReadOnlyList<string>> Operators,
    IReadOnlyList<CatalogOption> Templates, IReadOnlyList<CatalogOption> Accounts, IReadOnlyList<CatalogOption> Users, IReadOnlyList<CatalogOption> Statuses, IReadOnlyList<CatalogOption> Stages,
    IReadOnlyList<string> TemplateVariables, IReadOnlyList<string> LeadTextFields, bool DemoSchedulingConnected);

// ---------- requests ----------

public sealed record CreateWorkflowRequest(string Name, string? Description, WorkflowDefinition Definition) : TenantScopedRequest;
public sealed record UpdateWorkflowRequest(string Name, string? Description, int? ExpectedVersion = null) : TenantScopedRequest;
/// <summary>Saves the editable draft. When there is no draft, a new version is created from this definition (the active version is never touched).</summary>
public sealed record SaveDraftRequest(WorkflowDefinition Definition) : TenantScopedRequest;
public sealed record WorkflowKillSwitchRequest(bool On, string? Reason) : TenantScopedRequest;
public sealed record StartExecutionRequest(Guid LeadId) : TenantScopedRequest;
public sealed record SimulateRequest(Guid LeadId, Guid? WorkflowVersionId = null, string? TriggerKey = null, string? MessageText = null, WorkflowDefinition? Definition = null) : TenantScopedRequest;
public sealed record SaveWorkflowSettingsRequest(int MaxConcurrentExecutions, int MonthlyExecutionLimit, int DailyMessageLimit, int? ExpectedVersion = null) : TenantScopedRequest;

// ---------- services (Web and Api call the same implementations) ----------

public interface IWorkflowService
{
    Task<PagedResult<WorkflowListItemDto>> ListAsync(WorkflowQuery query, CancellationToken ct = default);
    Task<Result<WorkflowDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<WorkflowDto>> CreateAsync(CreateWorkflowRequest request, CancellationToken ct = default);
    Task<Result<WorkflowDto>> UpdateAsync(Guid id, UpdateWorkflowRequest request, CancellationToken ct = default);
    Task<Result<WorkflowVersionDto>> GetVersionAsync(Guid workflowId, int number, CancellationToken ct = default);
    Task<Result<WorkflowVersionDto>> SaveDraftAsync(Guid workflowId, SaveDraftRequest request, CancellationToken ct = default);
    /// <summary>Creates a new editable draft from an existing version (the way to edit an active workflow).</summary>
    Task<Result<WorkflowVersionDto>> NewDraftFromAsync(Guid workflowId, int number, CancellationToken ct = default);
    Task<Result<ValidationReport>> ValidateAsync(WorkflowDefinition definition, CancellationToken ct = default);
    Task<Result<WorkflowDto>> ActivateAsync(Guid workflowId, int versionNumber, CancellationToken ct = default);
    Task<Result<WorkflowDto>> DeactivateAsync(Guid workflowId, CancellationToken ct = default);
    Task<Result<WorkflowDto>> SetKillSwitchAsync(Guid workflowId, bool on, string? reason, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid workflowId, CancellationToken ct = default);
    Task<WorkflowCatalogDto> CatalogAsync(CancellationToken ct = default);
}

public interface IWorkflowExecutionService
{
    Task<PagedResult<ExecutionListItemDto>> ListAsync(ExecutionQuery query, CancellationToken ct = default);
    Task<Result<ExecutionDto>> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Starts the active version for one lead right now (a person pressed "run"). Same limits, eligibility checks and idempotency as an event-driven run.</summary>
    Task<Result<ExecutionDto>> StartAsync(Guid workflowId, Guid leadId, CancellationToken ct = default);
    Task<Result<ExecutionDto>> RetryAsync(Guid id, CancellationToken ct = default);
    Task<Result<ExecutionDto>> CancelAsync(Guid id, CancellationToken ct = default);
}

public interface IWorkflowSimulationService
{
    /// <summary>Walks a workflow against a lead and reports what WOULD happen. Reads data, writes nothing, sends nothing, schedules nothing.</summary>
    Task<Result<SimulationDto>> SimulateAsync(Guid workflowId, SimulateRequest request, CancellationToken ct = default);
}

public interface IWorkflowSettingsService
{
    Task<WorkflowSettingsDto> GetAsync(CancellationToken ct = default);
    Task<Result<WorkflowSettingsDto>> SaveLimitsAsync(SaveWorkflowSettingsRequest request, CancellationToken ct = default);
    Task<Result<WorkflowSettingsDto>> SetKillSwitchAsync(bool on, string? reason, CancellationToken ct = default);
}

public interface IWorkflowNotificationService
{
    /// <summary>The signed-in person's own notices only.</summary>
    Task<IReadOnlyList<WorkflowNotificationDto>> MineAsync(bool unreadOnly, CancellationToken ct = default);
    Task<Result> MarkReadAsync(Guid id, CancellationToken ct = default);
}

// ---------- ports ----------

/// <summary>Schedules the Hangfire jobs of the workflow module (replaced by a recorder in tests). Hangfire is the only scheduler.</summary>
public interface IWorkflowJobScheduler
{
    string EnqueueRun(Guid tenantId, Guid executionId, TimeSpan? delay = null);
}

/// <summary>Runs one execution (called by the Hangfire job inside the tenant scope).</summary>
public interface IWorkflowEngine
{
    Task RunAsync(Guid executionId, CancellationToken ct = default);
    /// <summary>Crash recovery and due waits: pushes every execution that is due or whose runner died. Returns how many were pushed.</summary>
    Task<int> SweepAsync(CancellationToken ct = default);
}

public enum WorkflowStartOutcome { Started = 1, Duplicate, NotMatched, Blocked }

public sealed record WorkflowStartResult(WorkflowStartOutcome Outcome, Guid? ExecutionId, string? Reason);

/// <summary>The one place an execution is created: kill switches, limits, trigger condition, re-entry policy, cool-down, frequency cap and idempotency are all decided here.</summary>
public interface IWorkflowTriggerService
{
    Task<IReadOnlyList<WorkflowStartResult>> HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default);
    Task<WorkflowStartResult> StartAsync(Guid workflowId, Guid versionId, string triggerKey, Guid triggerEventId, Guid leadId, Guid? contactId, Guid? conversationId, bool checkTriggerCondition, string? startedBy, CancellationToken ct = default);
    /// <summary>Hangfire-evaluated inactivity: starts the workflows with an inactivity trigger for leads that have been idle long enough.</summary>
    Task<int> RunInactivitySweepAsync(CancellationToken ct = default);
}

public sealed record WorkflowSendRequest(Guid LeadId, Guid? ContactId, Guid? ConversationId, Guid TemplateId, Guid ChannelAccountId, string IdempotencyKey);

public enum WorkflowSendStatus { Queued = 1, Blocked, Failed }

/// <summary>"Blocked" = compliance said no (never retried); "Failed" = a transient problem (account paused, rate limit...) that a retry may overcome.</summary>
public sealed record WorkflowSendOutcome(WorkflowSendStatus Status, Guid? MessageId, string? Code, string? Reason);

/// <summary>
/// The ONLY way a workflow sends a message. It lives with the messaging module: it re-checks eligibility at execution time (consent, suppression, opt-out, archived/excluded lead) and then goes through the
/// Phase 5 outbound pipeline (which checks again and applies account limits, warm-up and the per-key idempotency). It always sends as MARKETING, so consent is mandatory.
/// </summary>
public interface IWorkflowMessageSender
{
    Task<WorkflowSendOutcome> SendAsync(WorkflowSendRequest request, CancellationToken ct = default);
    /// <summary>What happened to the message of an earlier attempt: null = no such message.</summary>
    Task<WorkflowSentMessage?> FindAsync(string idempotencyKey, CancellationToken ct = default);
    /// <summary>Read-only eligibility for simulation: would a message to this lead be allowed right now?</summary>
    Task<(bool Allowed, string Reason)> CheckAsync(Guid leadId, Guid? contactId, CancellationToken ct = default);
}

public sealed record WorkflowSentMessage(Guid MessageId, bool Failed);
