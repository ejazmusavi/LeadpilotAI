using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Messaging;
using LeadPilot.Shared.Results;

namespace LeadPilot.Application.Agents;

// ===================================================================================================================================================================
// Ports of services that belong to Phase 10 (demos, trials, payment requests). Phase 8 only DEFINES them; the registered implementation answers "not available" honestly (never a fake success).
// Tenant comes from the tenant context inside each implementation, never from a parameter.
// ===================================================================================================================================================================

/// <summary>Result of a call to a Phase 10 port. Available=false means the service is not connected: nothing happened.</summary>
public sealed record PortResult<T>(bool Available, T? Value, string? Error = null)
{
    public static PortResult<T> NotConnected(string what) => new(false, default, $"{what} is not connected yet.");
    public static PortResult<T> Ok(T value) => new(true, value);
    public static PortResult<T> Fail(string error) => new(true, default, error);
}

public sealed record DemoSlot(string SlotId, DateTime StartUtc, DateTime EndUtc);
public sealed record DemoBooking(string BookingId, DateTime StartUtc, DateTime EndUtc);
public sealed record TrialInfo(string TrialId, DateTime? ExpiresAtUtc, string Status = "Active", string? Message = null);
public sealed record PaymentRequestInfo(string RequestId, string? PayUrl);

public interface IDemoScheduler
{
    Task<PortResult<IReadOnlyList<DemoSlot>>> AvailabilityAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct);
    Task<PortResult<DemoBooking>> BookAsync(Guid leadId, DemoSlot slot, string? notes, CancellationToken ct);
}

public interface ITrialProvisioner
{
    Task<PortResult<TrialInfo>> CreateAsync(Guid leadId, string? plan, string? notes, CancellationToken ct);
}

/// <summary>Creates a payment REQUEST (invoice/link). It never confirms a payment and never activates anything: activation is only ever driven by verified payment confirmation (Phase 10).</summary>
public interface IPaymentRequestGateway
{
    Task<PortResult<PaymentRequestInfo>> CreateAsync(Guid leadId, string plan, decimal amount, string currency, decimal? discountPercent, string? customTerms, CancellationToken ct);
}

// ===================================================================================================================================================================
// AI reply sending. The AI module reaches the send pipeline ONLY through this port.
// ===================================================================================================================================================================

public sealed record AiSendRequest(Guid ConversationId, Guid TriggerMessageId, Guid InteractionId, string Text, int AiEpoch);

public sealed record AiSendOutcome(bool Sent, string? BlockedReason, Guid? MessageId);

public interface IAiReplySender
{
    /// <summary>
    /// Queues an AI reply through the normal outbound pipeline (suppression, opt-out, rate limits, idempotency key per interaction) as a SERVICE reply to the customer's message. Refuses when the AI may not speak:
    /// human control or paused (also if that changed after generation started), opt-out, outside the service window, a newer customer message, the daily cap, or the opt-in switches being off.
    /// There is no overload for starting a conversation: AI outreach is not possible.
    /// </summary>
    Task<AiSendOutcome> DeliverAsync(AiSendRequest request, CancellationToken ct);
}

// ===================================================================================================================================================================
// DTOs and services behind the pages and the API
// ===================================================================================================================================================================

public sealed record QuestionDto(Guid Id, string Key, string Question, AnswerType AnswerType, IReadOnlyList<string> Choices, bool Required, int SortOrder);

public sealed record AgentListItemDto(Guid Id, string Name, bool IsActive, bool IsDefault, bool IsSample, bool AutoReplyEnabled, int ToolCount, string? ChatModel, DateTime? UpdatedAtUtc);

public sealed record AgentDto(
    Guid Id, string Name, string? Description, string Instructions, bool IsActive, bool IsDefault, bool IsSample, string? ChatModel, decimal? MinConfidence, IReadOnlyList<string> EnabledTools, bool AutoReplyEnabled,
    string OffTopicReply, IReadOnlyList<QuestionDto> Questions, int Version, bool TenantAllowsAiReplies);

public sealed record SaveAgentRequest(string Name, string? Description, string Instructions, string? ChatModel, decimal? MinConfidence, IReadOnlyList<string> EnabledTools, string OffTopicReply, int? Version = null) : TenantScopedRequest;
public sealed record QuestionInput(string Key, string Question, AnswerType AnswerType, IReadOnlyList<string>? Choices, bool Required);
public sealed record SaveQuestionsRequest(IReadOnlyList<QuestionInput> Questions) : TenantScopedRequest;

public sealed record AgentSettingsDto(bool AiRepliesAllowed, int MaxAutoRepliesPerConversationPerDay, int MaxTrialsPerLead, int ApprovalLifetimeHours, int MaxOpenFollowUpsPerLead);
public sealed record SaveAgentSettingsRequest(bool AiRepliesAllowed, int MaxAutoRepliesPerConversationPerDay, int MaxTrialsPerLead, int ApprovalLifetimeHours, int MaxOpenFollowUpsPerLead) : TenantScopedRequest;

public sealed record ToolInfoDto(string Name, string Description, bool ReadOnly, bool Safety, string Approval, IReadOnlyList<string> Parameters);

public interface IAgentService
{
    Task<IReadOnlyList<AgentListItemDto>> ListAsync(CancellationToken ct = default);
    Task<Result<AgentDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<AgentDto>> CreateAsync(SaveAgentRequest request, CancellationToken ct = default);
    Task<Result<AgentDto>> UpdateAsync(Guid id, SaveAgentRequest request, CancellationToken ct = default);
    Task<Result<AgentDto>> SaveQuestionsAsync(Guid id, SaveQuestionsRequest request, CancellationToken ct = default);
    Task<Result<AgentDto>> SetActiveAsync(Guid id, bool active, CancellationToken ct = default);
    Task<Result<AgentDto>> SetDefaultAsync(Guid id, CancellationToken ct = default);
    /// <summary>The explicit opt-in. Enabling needs the tenant switch too and is audited.</summary>
    Task<Result<AgentDto>> SetAutoSendAsync(Guid id, bool enabled, CancellationToken ct = default);
    /// <summary>Creates the clearly-marked SAMPLE agent and catalog (placeholders, no invented facts, nothing confirmed). Refuses when one already exists.</summary>
    Task<Result<AgentDto>> CreateSampleAsync(CancellationToken ct = default);
    Task<AgentSettingsDto> GetSettingsAsync(CancellationToken ct = default);
    Task<Result<AgentSettingsDto>> UpdateSettingsAsync(SaveAgentSettingsRequest request, CancellationToken ct = default);
    IReadOnlyList<ToolInfoDto> Tools();
}

public sealed record PlanDto(Guid Id, Guid ProductId, string Name, decimal? Price, string Currency, string? BillingPeriod, string? Details, decimal? MaxDiscountPercent, bool IsActive, bool IsConfirmed, bool IsSample);
public sealed record ProductListItemDto(Guid Id, string Name, string? Summary, bool IsActive, bool IsConfirmed, bool IsSample, int PlanCount, DateTime? UpdatedAtUtc);
public sealed record ProductDto(Guid Id, string Name, string? Summary, string? Features, string? DemoProcess, string? TrialProcess, bool IsActive, bool IsConfirmed, bool IsSample, int Version, IReadOnlyList<PlanDto> Plans);
public sealed record SaveProductRequest(string Name, string? Summary, string? Features, string? DemoProcess, string? TrialProcess, bool IsConfirmed, bool IsActive, int? Version = null) : TenantScopedRequest;
public sealed record SavePlanRequest(string Name, decimal? Price, string Currency, string? BillingPeriod, string? Details, decimal? MaxDiscountPercent, bool IsConfirmed, bool IsActive) : TenantScopedRequest;

public interface ICatalogService
{
    Task<IReadOnlyList<ProductListItemDto>> ListAsync(CancellationToken ct = default);
    Task<Result<ProductDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<ProductDto>> CreateAsync(SaveProductRequest request, CancellationToken ct = default);
    Task<Result<ProductDto>> UpdateAsync(Guid id, SaveProductRequest request, CancellationToken ct = default);
    Task<Result<PlanDto>> AddPlanAsync(Guid productId, SavePlanRequest request, CancellationToken ct = default);
    Task<Result<PlanDto>> UpdatePlanAsync(Guid planId, SavePlanRequest request, CancellationToken ct = default);
    Task<Result> DeletePlanAsync(Guid planId, CancellationToken ct = default);
}

public sealed record ApprovalDto(
    Guid Id, string ToolName, string Reason, string Summary, ApprovalStatus Status, ExecutionState Execution, string? ExecutionNote, Guid ConversationId, string? PeerName, Guid? LeadId, string? LeadName,
    DateTime RequestedAtUtc, DateTime ExpiresAtUtc, string? DecidedByName, DateTime? DecidedAtUtc, string? DecisionNote, string InputJson);

public sealed record ApprovalQuery { public ApprovalStatus? Status { get; init; } public int Page { get; init; } = 1; public int PageSize { get; init; } = 25; }
public sealed record DecideApprovalRequest(string? Note) : TenantScopedRequest;

public interface IApprovalService
{
    Task<PagedResult<ApprovalDto>> ListAsync(ApprovalQuery query, CancellationToken ct = default);
    Task<Result<ApprovalDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<int> PendingCountAsync(CancellationToken ct = default);
    /// <summary>Approves and carries out the stored action (re-validated: tenant, lead, opt-out). The execution result is recorded honestly, including "not available".</summary>
    Task<Result<ApprovalDto>> ApproveAsync(Guid id, DecideApprovalRequest request, CancellationToken ct = default);
    Task<Result<ApprovalDto>> RejectAsync(Guid id, DecideApprovalRequest request, CancellationToken ct = default);
}

public sealed record ToolExecutionDto(
    Guid Id, string Tool, ToolOutcome Outcome, string? Reason, string ArgumentsSummary, string? InputJson, string? OutputJson, Guid? ConversationId, Guid? LeadId, Guid? ApprovalRequestId, string? ExecutedByName, int DurationMs, DateTime OccurredAtUtc);

public sealed record ToolExecutionQuery { public string? Tool { get; init; } public ToolOutcome? Outcome { get; init; } public Guid? ConversationId { get; init; } public int Page { get; init; } = 1; public int PageSize { get; init; } = 25; }

public interface IToolExecutionService
{
    Task<PagedResult<ToolExecutionDto>> ListAsync(ToolExecutionQuery query, CancellationToken ct = default);
}

public sealed record FollowUpDto(Guid Id, Guid LeadId, string LeadName, Guid? ConversationId, string? AssignedToName, DateTime DueAtUtc, string Note, FollowUpStatus Status, ActorKind CreatedBy, DateTime CreatedAtUtc, bool Overdue);
public sealed record FollowUpQuery { public FollowUpStatus? Status { get; init; } = FollowUpStatus.Open; public int Page { get; init; } = 1; public int PageSize { get; init; } = 25; }

public interface IFollowUpService
{
    Task<PagedResult<FollowUpDto>> ListAsync(FollowUpQuery query, CancellationToken ct = default);
    Task<Result<FollowUpDto>> CloseAsync(Guid id, bool done, CancellationToken ct = default);
}

public sealed record ConversationNoteDto(Guid Id, string Body, string? AuthorName, ActorKind Author, DateTime CreatedAtUtc);
public sealed record HandoffEventDto(Guid Id, HandoffKind Kind, ActorKind Actor, string? ActorName, string? TargetName, string? Reason, string? FromState, string? ToState, DateTime OccurredAtUtc);
public sealed record ConversationNoteRequest(string Body) : TenantScopedRequest;
public sealed record HandoffReasonRequest(string? Reason) : TenantScopedRequest;
