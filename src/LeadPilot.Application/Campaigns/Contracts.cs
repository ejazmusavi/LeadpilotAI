using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Messaging;
using LeadPilot.Shared.Results;

namespace LeadPilot.Application.Campaigns;

// ---------- DTOs ----------

public sealed record CampaignCountsDto(int Total, int Pending, int Skipped, int Queued, int Sent, int Delivered, int Read, int Replied, int Failed, int Cancelled, int Conversions)
{
    /// <summary>Reached the gateway (Sent and every later state).</summary>
    public int Messaged => Sent + Delivered + Read + Replied;
    public static CampaignCountsDto Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
}

public sealed record CampaignListItemDto(
    Guid Id, string Name, CampaignType Type, CampaignStatus Status, DateTime? ScheduledAtUtc, DateTime CreatedAtUtc, DateTime? StartedAtUtc, DateTime? CompletedAtUtc,
    string? CreatedByName, bool PausedAutomatically, string? PauseReason, CampaignCountsDto Counts);

public sealed record CampaignDto(
    Guid Id, string Name, string? Description, CampaignType Type, CampaignChannel Channel, CampaignStatus Status, Guid? TemplateId, string? TemplateName, Guid? ChannelAccountId, string? ChannelAccountName,
    string? MessageBody, bool IncludeOptOutFooter, string? OptOutFooter, AudienceDefinition Audience, int AudienceVersion, Guid? ParentCampaignId, string? ParentCampaignName, int? FollowUpDelayDays,
    DateTime? ScheduledAtUtc, string? ScheduleTimeZone, int BatchSize, int MinIntervalSeconds, int MaxIntervalSeconds, int MaxRecipients, int? DailyLimit, int? FailureThresholdPercent, int? CooldownDays, int AttributionWindowDays,
    string? CreatedByName, DateTime CreatedAtUtc, string? SubmittedByName, DateTime? SubmittedAtUtc, string? ApprovedByName, DateTime? ApprovedAtUtc, string? RejectedReason,
    DateTime? StartedAtUtc, DateTime? CompletedAtUtc, DateTime? PausedAtUtc, string? PauseReason, bool PausedAutomatically, DateTime? CancelledAtUtc, string? FailureReason, DateTime? NextRunAtUtc,
    CampaignCountsDto Counts, IReadOnlyList<string> Actions, bool ApprovalRequired, int Version);

public sealed record CampaignQuery
{
    public CampaignStatus? Status { get; init; }
    public CampaignType? Type { get; init; }
    public string? Search { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}

public sealed record SaveCampaignRequest(
    string Name, string? Description, CampaignType Type, Guid? TemplateId, Guid? ChannelAccountId, AudienceDefinition? Audience, int? FollowUpDelayDays = null, DateTime? ScheduledAtUtc = null, string? ScheduleTimeZone = null,
    bool IncludeOptOutFooter = true, int? BatchSize = null, int? MinIntervalSeconds = null, int? MaxIntervalSeconds = null, int? MaxRecipients = null, int? DailyLimit = null, int? FailureThresholdPercent = null, int? CooldownDays = null)
    : TenantScopedRequest;

public sealed record RejectCampaignRequest(string Reason) : TenantScopedRequest;
public sealed record CloneCampaignRequest(string? Name) : TenantScopedRequest;
public sealed record TestSendRequest(string PhoneE164) : TenantScopedRequest;

public sealed record AudiencePreviewRequest(AudienceDefinition Audience, CampaignType Type, Guid? TemplateId = null, int? MaxRecipients = null, int? CooldownDays = null, Guid? CampaignId = null, bool IncludeOptOutFooter = true) : TenantScopedRequest;
public sealed record ExclusionCountDto(string Reason, string Explanation, int Count);
public sealed record PreviewSampleDto(Guid ContactId, string LeadName, string? ContactName, string MaskedDestination, string? RenderedMessage, string? Problem);
public sealed record AudiencePreviewDto(int Candidates, int Eligible, int Excluded, IReadOnlyList<ExclusionCountDto> Breakdown, bool Truncated, int ScanLimit, IReadOnlyList<PreviewSampleDto> Samples, DateTime GeneratedAtUtc, IReadOnlyList<string> Warnings);

public sealed record DryRunDto(AudiencePreviewDto Audience, bool TemplateValid, IReadOnlyList<string> Problems, bool AccountReady, string? AccountNote, int EstimatedMinutes);

public sealed record RecipientQuery { public RecipientStatus? Status { get; init; } public string? Reason { get; init; } public int Page { get; init; } = 1; public int PageSize { get; init; } = 25; }

public sealed record RecipientDto(
    Guid Id, Guid ContactId, Guid LeadId, string? LeadName, string? ContactName, string? MaskedDestination, RecipientStatus Status, string? StatusReason, string? ReasonText, string? StatusDetail,
    Guid? MessageId, Guid? ConversationId, DateTime? QueuedAtUtc, DateTime? SentAtUtc, DateTime? DeliveredAtUtc, DateTime? ReadAtUtc, DateTime? RepliedAtUtc, DateTime? FailedAtUtc, string EligibilityJson);

public sealed record ExecutionLogDto(Guid Id, ExecutionLogLevel Level, string Code, string Message, string? ActorName, DateTime OccurredAtUtc);
public sealed record ConversionDto(Guid Id, Guid RecipientId, Guid LeadId, string? LeadName, ConversionKind Kind, string? Detail, DateTime SentAtUtc, DateTime OccurredAtUtc);
public sealed record ReasonCountDto(string Reason, string Explanation, int Count);

public sealed record CampaignStatsDto(
    CampaignCountsDto Counts, int Messaged, double DeliveryRatePercent, double ReadRatePercent, double ReplyRatePercent, double FailureRatePercent, IReadOnlyList<ReasonCountDto> SkipReasons, IReadOnlyList<ReasonCountDto> FailureReasons,
    int ReplyConversions, int StageChangeConversions, DateTime? RollupUpdatedAtUtc, bool RollupMatchesLive, int AttributionWindowDays);

public sealed record CampaignSettingsDto(
    int CooldownDays, bool ApprovalRequired, bool CreatorCannotApprove, int AttributionWindowDays, int FailureThresholdPercent, int FailureMinSample, string OptOutFooter, int? MonthlyMessageLimit, int EffectiveMonthlyLimit,
    IReadOnlyList<string> TestNumbers, bool KillSwitch, DateTime? KillSwitchAtUtc, string TenantTimeZone, int UsedThisMonth);

public sealed record UpdateCampaignSettingsRequest(
    int CooldownDays, bool ApprovalRequired, bool CreatorCannotApprove, int AttributionWindowDays, int FailureThresholdPercent, int FailureMinSample, string OptOutFooter, int? MonthlyMessageLimit, IReadOnlyList<string>? TestNumbers) : TenantScopedRequest;

public sealed record KillSwitchRequest(bool Enabled, string? Reason) : TenantScopedRequest;

// ---------- Permission-gated use cases (Web and Api call the same implementations) ----------

public interface ICampaignService
{
    Task<PagedResult<CampaignListItemDto>> ListAsync(CampaignQuery query, CancellationToken ct = default);
    Task<Result<CampaignDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<CampaignDto>> CreateAsync(SaveCampaignRequest request, CancellationToken ct = default);
    Task<Result<CampaignDto>> UpdateAsync(Guid id, SaveCampaignRequest request, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid id, CancellationToken ct = default);
    Task<Result<CampaignDto>> CloneAsync(Guid id, CloneCampaignRequest request, CancellationToken ct = default);
    /// <summary>Validates and freezes the content, then asks for approval (when the workspace requires it) or schedules directly.</summary>
    Task<Result<CampaignDto>> SubmitAsync(Guid id, CancellationToken ct = default);
    Task<Result<CampaignDto>> ApproveAsync(Guid id, CancellationToken ct = default);
    Task<Result<CampaignDto>> RejectAsync(Guid id, RejectCampaignRequest request, CancellationToken ct = default);
    Task<Result<CampaignDto>> UnscheduleAsync(Guid id, CancellationToken ct = default);
    Task<Result<CampaignDto>> StartNowAsync(Guid id, CancellationToken ct = default);
    Task<Result<CampaignDto>> PauseAsync(Guid id, CancellationToken ct = default);
    Task<Result<CampaignDto>> ResumeAsync(Guid id, CancellationToken ct = default);
    Task<Result<CampaignDto>> CancelAsync(Guid id, CancellationToken ct = default);
    /// <summary>Sends the rendered message (labelled as a test) to ONE number from the workspace's explicit allow-list, through the normal outbound pipeline.</summary>
    Task<Result<MessageDto>> TestSendAsync(Guid id, TestSendRequest request, CancellationToken ct = default);
    /// <summary>Nothing is sent: audience, rendering and account readiness are evaluated and reported.</summary>
    Task<Result<DryRunDto>> DryRunAsync(Guid id, CancellationToken ct = default);
    Task<Result<PagedResult<RecipientDto>>> RecipientsAsync(Guid id, RecipientQuery query, CancellationToken ct = default);
    Task<Result<PagedResult<ExecutionLogDto>>> LogAsync(Guid id, int page, int pageSize, CancellationToken ct = default);
    Task<Result<PagedResult<ConversionDto>>> ConversionsAsync(Guid id, int page, int pageSize, CancellationToken ct = default);
    Task<Result<CampaignStatsDto>> StatsAsync(Guid id, CancellationToken ct = default);
}

public interface ICampaignAudienceService
{
    Task<Result<AudiencePreviewDto>> PreviewAsync(AudiencePreviewRequest request, CancellationToken ct = default);
}

public interface ICampaignSettingsService
{
    Task<CampaignSettingsDto> GetAsync(CancellationToken ct = default);
    Task<Result<CampaignSettingsDto>> UpdateAsync(UpdateCampaignSettingsRequest request, CancellationToken ct = default);
    /// <summary>Turning it on pauses every running campaign of the workspace and blocks every campaign send until it is turned off. Turning it off does not resume anything by itself.</summary>
    Task<Result<CampaignSettingsDto>> SetKillSwitchAsync(KillSwitchRequest request, CancellationToken ct = default);
}

// ---------- Ports used by the worker and by the Phase 5 pipeline ----------

/// <summary>Schedules the Hangfire jobs of the campaign module (the only scheduler; replaced by a recorder in tests).</summary>
public interface ICampaignJobScheduler
{
    /// <summary>Runs <see cref="ICampaignRunner.StartAsync"/> at <paramref name="atUtc"/> (immediately when due). Returns the job id (kept so unscheduling deletes it).</summary>
    string ScheduleStart(Guid tenantId, Guid campaignId, DateTime atUtc);
    void EnqueueTick(Guid tenantId, Guid campaignId, TimeSpan? delay = null);
    void Delete(string? jobId);
}

/// <summary>Executes campaigns inside a tenant scope (Hangfire entry points call these).</summary>
public interface ICampaignRunner
{
    /// <summary>Scheduled → Running (once), builds the audience snapshot (resumable), then starts ticking.</summary>
    Task StartAsync(Guid campaignId, CancellationToken ct = default);
    /// <summary>Processes one batch of recipients and reschedules itself. Safe to call twice: a lease allows one tick at a time.</summary>
    Task TickAsync(Guid campaignId, CancellationToken ct = default);
    /// <summary>Finds campaigns of this tenant that need a push (due schedules, expired leases, parents that completed). Returns how many jobs were scheduled.</summary>
    Task<int> SweepAsync(CancellationToken ct = default);
}

public sealed record CampaignSendBlock(string Code, string Reason, bool ReturnToPending);

/// <summary>
/// Called by the Phase 5 send runner immediately before a campaign message is sent (in addition to its own consent/suppression re-validation): is the campaign still running,
/// is the kill switch off, is the recipient still waiting for this message? A block fails the message and updates the recipient.
/// </summary>
public interface ICampaignSendGuard
{
    Task<CampaignSendBlock?> CheckBeforeSendAsync(Message message, CancellationToken ct = default);
}

/// <summary>Real events flowing into campaign tracking: send outcomes, acks, inbound messages, CRM changes, account trouble.</summary>
public interface ICampaignEvents
{
    /// <summary>A campaign message changed state (sent, failed, delivered, read…). Updates the recipient from the message and queues the rollup event. No-op for non-campaign messages.</summary>
    /// <param name="save">false: stage the changes in the caller's unit of work (the status consumer commits them together with its idempotency record).</param>
    Task OnOutboundChangedAsync(Guid messageId, bool save = true, CancellationToken ct = default);
    /// <summary>An inbound message arrived: attribute it as a reply when it answers a campaign message inside the attribution window. An opt-out (STOP) is logged but is not counted as a reply.</summary>
    Task OnInboundMessageAsync(Guid conversationId, Guid inboundMessageId, bool isOptOut, CancellationToken ct = default);
    /// <summary>Same as <see cref="OnInboundMessageAsync"/> but only stages the changes (the inbound consumer commits them with its idempotency record).</summary>
    Task StageInboundAsync(Guid conversationId, Guid inboundMessageId, bool isOptOut, CancellationToken ct = default);
    Task OnLeadStageChangedAsync(Guid leadId, Guid fromStageId, Guid toStageId, Guid activityId, CancellationToken ct = default);
    /// <summary>The account paused, disconnected or was removed: pause running campaigns that use it.</summary>
    Task OnAccountUnavailableAsync(Guid accountId, string reason, CancellationToken ct = default);
}

public sealed record CampaignChannelRequest(Guid ContactId, Guid ChannelAccountId, string Text, Guid? TemplateId, string IdempotencyKey, Guid CampaignId, Guid RecipientId);
public sealed record CampaignChannelTestRequest(string PhoneE164, Guid ChannelAccountId, string Text, string IdempotencyKey, Guid CampaignId);
public sealed record QueuedCampaignMessage(Guid MessageId, Guid ConversationId, MessageStatus Status, bool AlreadyExisted);

/// <summary>
/// The channel abstraction of the campaign module. WhatsApp is the only implementation and it hands every message to the Phase 5 outbound queue (the single send pipeline).
/// A future e-mail channel would implement this with its own pipeline; no placeholder implementations exist.
/// </summary>
public interface ICampaignChannel
{
    CampaignChannel Channel { get; }
    Task<Result<QueuedCampaignMessage>> QueueAsync(CampaignChannelRequest request, CancellationToken ct = default);
    Task<Result<QueuedCampaignMessage>> QueueTestAsync(CampaignChannelTestRequest request, CancellationToken ct = default);
    /// <summary>Cancels messages that are still waiting in the queue (never one that is already being sent). Returns how many were cancelled.</summary>
    Task<int> CancelQueuedAsync(Guid campaignId, string code, string reason, CancellationToken ct = default);
}

public static class CampaignEventTypes
{
    public const string RecipientChanged = "campaign.recipient";
    public const string StatusChanged = "campaign.status";
    public static readonly IReadOnlyList<string> All = [RecipientChanged, StatusChanged];
}

public sealed record CampaignRecipientChangedPayload(Guid CampaignId, Guid RecipientId, string Status);
public sealed record CampaignStatusChangedPayload(Guid CampaignId, string Status, string? Reason);

public sealed class CampaignOptions
{
    public const string Section = "Campaigns";
    /// <summary>Hard ceiling of candidates one preview/snapshot scans (never loaded at once: streamed in pages).</summary>
    public int ScanLimit { get; set; } = 20_000;
    public int PageSize { get; set; } = 200;
    public int DefaultMonthlyMessageLimit { get; set; } = 1000;
    public int LeaseSeconds { get; set; } = 120;
    public int SampleSize { get; set; } = 5;
    /// <summary>A Running campaign nobody ticked for this long is pushed again by the sweep (crash recovery).</summary>
    public int StaleTickMinutes { get; set; } = 3;
}
