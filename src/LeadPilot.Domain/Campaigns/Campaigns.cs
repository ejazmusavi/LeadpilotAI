using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;

namespace LeadPilot.Domain.Campaigns;

/// <summary>Everything a person edits on a campaign while it is a draft.</summary>
public sealed record CampaignDraft(
    string Name, string? Description, CampaignType Type, Guid? TemplateId, Guid? ChannelAccountId, string AudienceJson, int AudienceVersion,
    Guid? ParentCampaignId, int? FollowUpDelayDays, DateTime? ScheduledAtUtc, string? ScheduleTimeZone, bool IncludeOptOutFooter,
    int BatchSize, int MinIntervalSeconds, int MaxIntervalSeconds, int MaxRecipients, int? DailyLimit, int? FailureThresholdPercent, int? CooldownDays);

/// <summary>
/// A marketing campaign. Status changes go through <see cref="CampaignStateMachine"/>. The runner changes status with guarded SQL updates (also bumping <see cref="Version"/>),
/// so a pause/cancel racing a worker either wins or gets a concurrency conflict, never a silent overwrite.
/// </summary>
public class Campaign : AuditableEntity, ITenantOwned
{
    public const int HardMaxBatchSize = 100;
    public const int DefaultBatchSize = 10;
    public const int HardMinIntervalSeconds = 15;
    public const int DefaultMinIntervalSeconds = 60;
    public const int DefaultMaxIntervalSeconds = 180;
    public const int HardMaxRecipients = 5000;
    public const int DefaultMaxRecipients = 500;

    private Campaign() { }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public CampaignType Type { get; private set; }
    public CampaignChannel Channel { get; private set; } = CampaignChannel.WhatsApp;
    public CampaignStatus Status { get; private set; } = CampaignStatus.Draft;
    public Guid? TemplateId { get; private set; }
    public Guid? ChannelAccountId { get; private set; }
    /// <summary>The approved text (template body at submit time): later template edits never change an approved or running campaign.</summary>
    public string? MessageBodySnapshot { get; private set; }
    public bool IncludeOptOutFooter { get; private set; } = true;
    public string? OptOutFooterSnapshot { get; private set; }
    /// <summary>Versioned audience definition (JSON), see <c>AudienceDefinition</c>.</summary>
    public string AudienceJson { get; private set; } = "{}";
    public int AudienceVersion { get; private set; } = 1;
    public Guid? ParentCampaignId { get; private set; }
    /// <summary>Follow-ups: start this many days after the parent campaign completes (used when no fixed time is set).</summary>
    public int? FollowUpDelayDays { get; private set; }
    public DateTime? ScheduledAtUtc { get; private set; }
    /// <summary>IANA zone the person scheduled in (the tenant's zone at the time); <see cref="ScheduledAtUtc"/> is the instant that is executed.</summary>
    public string? ScheduleTimeZone { get; private set; }
    public string? ScheduleJobId { get; private set; }

    public int BatchSize { get; private set; } = DefaultBatchSize;
    public int MinIntervalSeconds { get; private set; } = DefaultMinIntervalSeconds;
    public int MaxIntervalSeconds { get; private set; } = DefaultMaxIntervalSeconds;
    public int MaxRecipients { get; private set; } = DefaultMaxRecipients;
    public int? DailyLimit { get; private set; }
    public int? FailureThresholdPercent { get; private set; }
    public int? CooldownDays { get; private set; }
    /// <summary>Copied from the workspace policy at submit time so results of a finished campaign stay reproducible.</summary>
    public int AttributionWindowDays { get; private set; } = 7;

    public Guid? CreatedByUserId { get; private set; }
    public Guid? SubmittedByUserId { get; private set; }
    public DateTime? SubmittedAtUtc { get; private set; }
    public Guid? ApprovedByUserId { get; private set; }
    public DateTime? ApprovedAtUtc { get; private set; }
    public string? RejectedReason { get; private set; }

    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public DateTime? PausedAtUtc { get; private set; }
    public string? PauseReason { get; private set; }
    public bool PausedAutomatically { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public Guid? CancelledByUserId { get; private set; }
    public string? FailureReason { get; private set; }

    /// <summary>Worker lease: one tick at a time per campaign; a crashed worker's lease simply expires.</summary>
    public DateTime? LeaseUntilUtc { get; private set; }
    public DateTime? NextRunAtUtc { get; private set; }
    public DateTime? LastTickAtUtc { get; private set; }
    public Guid? SnapshotId { get; private set; }
    public int Version { get; private set; } = 1;

    public static Campaign Create(Guid tenantId, string name, CampaignType type, Guid? userId, string? actor, DateTime nowUtc)
    {
        if (!Enum.IsDefined(type)) throw new DomainException("Choose a campaign type.");
        return new Campaign { TenantId = tenantId, Name = Guard.Required(name, 150, "Campaign name"), Type = type, CreatedByUserId = userId, CreatedAtUtc = nowUtc, CreatedBy = actor };
    }

    public bool IsEditable => Status == CampaignStatus.Draft;

    /// <summary>Replaces the editable fields. Only a draft can be edited: anything else would change what was approved or what is running.</summary>
    public void UpdateDraft(CampaignDraft d, DateTime nowUtc, string? actor)
    {
        if (!IsEditable) throw new DomainException("Only a draft campaign can be edited. Unschedule it first, or clone it.");
        Name = Guard.Required(d.Name, 150, "Campaign name");
        Description = Guard.Optional(d.Description, 500, "Description");
        if (!Enum.IsDefined(d.Type)) throw new DomainException("Choose a campaign type.");
        Type = d.Type;
        TemplateId = d.TemplateId; ChannelAccountId = d.ChannelAccountId;
        AudienceJson = string.IsNullOrWhiteSpace(d.AudienceJson) ? "{}" : d.AudienceJson; AudienceVersion = d.AudienceVersion;
        IncludeOptOutFooter = d.IncludeOptOutFooter;
        if (d.Type is CampaignType.FollowUp && d.ParentCampaignId is null) throw new DomainException("A follow-up campaign needs a parent campaign.");
        ParentCampaignId = d.ParentCampaignId;
        if (d.FollowUpDelayDays is { } fd && fd is < 0 or > 365) throw new DomainException("The follow-up delay must be between 0 and 365 days.");
        FollowUpDelayDays = d.FollowUpDelayDays;
        ScheduledAtUtc = d.ScheduledAtUtc is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null;
        ScheduleTimeZone = Guard.Optional(d.ScheduleTimeZone, 64, "Time zone");
        SetLimits(d.BatchSize, d.MinIntervalSeconds, d.MaxIntervalSeconds, d.MaxRecipients, d.DailyLimit, d.FailureThresholdPercent, d.CooldownDays);
        UpdatedAtUtc = nowUtc; UpdatedBy = actor; Version++;
    }

    public void SetLimits(int batchSize, int minInterval, int maxInterval, int maxRecipients, int? dailyLimit, int? failurePercent, int? cooldownDays)
    {
        if (batchSize is < 1 or > HardMaxBatchSize) throw new DomainException($"The batch size must be between 1 and {HardMaxBatchSize}.");
        if (minInterval < HardMinIntervalSeconds) throw new DomainException($"The minimum pause between batches is {HardMinIntervalSeconds} seconds.");
        if (maxInterval < minInterval || maxInterval > 86_400) throw new DomainException("The maximum pause between batches must be at least the minimum and at most one day.");
        if (maxRecipients is < 1 or > HardMaxRecipients) throw new DomainException($"A campaign can have between 1 and {HardMaxRecipients} recipients.");
        if (dailyLimit is < 1 or > ChannelAccount_HardMaxDaily) throw new DomainException("The daily limit must be between 1 and 1000.");
        if (failurePercent is < 1 or > 100) throw new DomainException("The failure threshold must be between 1 and 100 percent.");
        if (cooldownDays is < 0 or > 365) throw new DomainException("The cool-down must be between 0 and 365 days.");
        BatchSize = batchSize; MinIntervalSeconds = minInterval; MaxIntervalSeconds = maxInterval; MaxRecipients = maxRecipients;
        DailyLimit = dailyLimit; FailureThresholdPercent = failurePercent; CooldownDays = cooldownDays;
    }

    private const int ChannelAccount_HardMaxDaily = 1000;

    /// <summary>Freezes the approved content. Called when the draft is submitted.</summary>
    public void FreezeContent(string bodyText, string? footer, int attributionWindowDays)
    {
        MessageBodySnapshot = bodyText; OptOutFooterSnapshot = footer; AttributionWindowDays = Math.Clamp(attributionWindowDays, 1, 90);
    }

    public void Submit(bool requiresApproval, Guid? userId, DateTime nowUtc)
    {
        var target = requiresApproval ? CampaignStatus.PendingApproval : CampaignStatus.Scheduled;
        CampaignStateMachine.Ensure(Status, target);
        Status = target; SubmittedByUserId = userId; SubmittedAtUtc = nowUtc; RejectedReason = null; UpdatedAtUtc = nowUtc; Version++;
        if (!requiresApproval) { ApprovedByUserId = null; ApprovedAtUtc = null; }
    }

    /// <summary>Approval. <paramref name="creatorCannotApprove"/> implements the four-eyes policy: whoever submitted the campaign cannot approve it.</summary>
    public void Approve(Guid? approverId, bool creatorCannotApprove, DateTime nowUtc)
    {
        CampaignStateMachine.Ensure(Status, CampaignStatus.Scheduled);
        if (Status != CampaignStatus.PendingApproval) throw new DomainException("Only a campaign waiting for approval can be approved.");
        if (creatorCannotApprove && approverId is not null && (approverId == SubmittedByUserId || approverId == CreatedByUserId)) throw new DomainException("The person who created or submitted a campaign cannot approve it (workspace policy).");
        Status = CampaignStatus.Scheduled; ApprovedByUserId = approverId; ApprovedAtUtc = nowUtc; UpdatedAtUtc = nowUtc; Version++;
    }

    public void Reject(string reason, DateTime nowUtc)
    {
        if (Status != CampaignStatus.PendingApproval) throw new DomainException("Only a campaign waiting for approval can be returned.");
        Status = CampaignStatus.Draft; RejectedReason = Guard.Required(reason, 500, "Reason"); UpdatedAtUtc = nowUtc; Version++;
    }

    public void Unschedule(DateTime nowUtc)
    {
        if (Status != CampaignStatus.Scheduled) throw new DomainException("Only a scheduled campaign can be unscheduled.");
        Status = CampaignStatus.Draft; ApprovedByUserId = null; ApprovedAtUtc = null; ScheduleJobId = null; NextRunAtUtc = null; UpdatedAtUtc = nowUtc; Version++;
    }

    public void Pause(string reason, bool automatic, DateTime nowUtc)
    {
        CampaignStateMachine.Ensure(Status, CampaignStatus.Paused);
        Status = CampaignStatus.Paused; PausedAtUtc = nowUtc; PauseReason = Guard.Required(reason, 300, "Reason"); PausedAutomatically = automatic; LeaseUntilUtc = null; UpdatedAtUtc = nowUtc; Version++;
    }

    public void Resume(DateTime nowUtc)
    {
        CampaignStateMachine.Ensure(Status, CampaignStatus.Running);
        if (Status != CampaignStatus.Paused) throw new DomainException("Only a paused campaign can be resumed.");
        Status = CampaignStatus.Running; PausedAtUtc = null; PauseReason = null; PausedAutomatically = false; NextRunAtUtc = nowUtc; UpdatedAtUtc = nowUtc; Version++;
    }

    public void Cancel(Guid? userId, DateTime nowUtc)
    {
        CampaignStateMachine.Ensure(Status, CampaignStatus.Cancelled);
        Status = CampaignStatus.Cancelled; CancelledAtUtc = nowUtc; CancelledByUserId = userId; LeaseUntilUtc = null; NextRunAtUtc = null; ScheduleJobId = null; UpdatedAtUtc = nowUtc; Version++;
    }

    public void SetScheduleJob(string? jobId, DateTime nowUtc) { ScheduleJobId = jobId; UpdatedAtUtc = nowUtc; }
    public void SetSchedule(DateTime? atUtc, DateTime nowUtc) { ScheduledAtUtc = atUtc; UpdatedAtUtc = nowUtc; }

    /// <summary>A clone starts as a draft with the same definition; nothing of the execution history is copied.</summary>
    public static Campaign CloneOf(Campaign source, string name, Guid tenantId, Guid? userId, string? actor, DateTime nowUtc)
    {
        var c = Create(tenantId, name, source.Type, userId, actor, nowUtc);
        c.UpdateDraft(new CampaignDraft(name, source.Description, source.Type, source.TemplateId, source.ChannelAccountId, source.AudienceJson, source.AudienceVersion, source.ParentCampaignId, source.FollowUpDelayDays,
            null, source.ScheduleTimeZone, source.IncludeOptOutFooter, source.BatchSize, source.MinIntervalSeconds, source.MaxIntervalSeconds, source.MaxRecipients, source.DailyLimit, source.FailureThresholdPercent, source.CooldownDays), nowUtc, actor);
        return c;
    }
}

/// <summary>The frozen audience of one campaign run: when it was taken, from which definition, and how many contacts were excluded for which reason.</summary>
public class CampaignAudienceSnapshot : Entity, ITenantOwned
{
    private CampaignAudienceSnapshot() { }
    public Guid TenantId { get; private set; }
    public Guid CampaignId { get; private set; }
    public int AudienceVersion { get; private set; }
    public string DefinitionJson { get; private set; } = "{}";
    public SnapshotStatus Status { get; private set; } = SnapshotStatus.Building;
    public DateTime TakenAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public int Candidates { get; private set; }
    public int Recipients { get; private set; }
    public int Excluded { get; private set; }
    /// <summary>JSON object reason → count.</summary>
    public string ExclusionBreakdownJson { get; private set; } = "{}";
    public bool Truncated { get; private set; }
    public Guid? TakenByUserId { get; private set; }

    public static CampaignAudienceSnapshot Start(Guid tenantId, Guid campaignId, int version, string definitionJson, Guid? userId, DateTime nowUtc) =>
        new() { TenantId = tenantId, CampaignId = campaignId, AudienceVersion = version, DefinitionJson = definitionJson, TakenByUserId = userId, TakenAtUtc = nowUtc };

    public void Complete(int candidates, int recipients, int excluded, string breakdownJson, bool truncated, DateTime nowUtc)
    {
        Status = SnapshotStatus.Ready; Candidates = candidates; Recipients = recipients; Excluded = excluded; ExclusionBreakdownJson = breakdownJson; Truncated = truncated; CompletedAtUtc = nowUtc;
    }

    public void Fail(DateTime nowUtc) { Status = SnapshotStatus.Failed; CompletedAtUtc = nowUtc; }
}

/// <summary>
/// One contact of one campaign. Unique per (campaign, contact): a contact can never be queued twice. <see cref="EligibilityJson"/> holds the eligibility decision at selection time
/// and the decision taken immediately before the send (both kept). <see cref="Round"/> changes the idempotency key when a pause cancelled the queued message and the recipient is queued again.
/// </summary>
public class CampaignRecipient : Entity, ITenantOwned
{
    private CampaignRecipient() { }
    public Guid TenantId { get; private set; }
    public Guid CampaignId { get; private set; }
    public Guid SnapshotId { get; private set; }
    public Guid ContactId { get; private set; }
    public Guid LeadId { get; private set; }
    public int Sequence { get; private set; }
    /// <summary>E.164 number at selection time (display and duplicate-number protection; the send re-reads the contact).</summary>
    public string? Destination { get; private set; }
    public RecipientStatus Status { get; private set; } = RecipientStatus.Pending;
    public string? StatusReason { get; private set; }
    public string? StatusDetail { get; private set; }
    public string EligibilityJson { get; private set; } = "{}";
    public int Round { get; private set; }
    public Guid? MessageId { get; private set; }
    public Guid? ConversationId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? QueuedAtUtc { get; private set; }
    public DateTime? SentAtUtc { get; private set; }
    public DateTime? DeliveredAtUtc { get; private set; }
    public DateTime? ReadAtUtc { get; private set; }
    public DateTime? RepliedAtUtc { get; private set; }
    public DateTime? FailedAtUtc { get; private set; }
    public DateTime? UpdatedAtUtc { get; private set; }
    public int Version { get; private set; } = 1;

    public static CampaignRecipient Create(Guid tenantId, Guid campaignId, Guid snapshotId, Guid contactId, Guid leadId, int sequence, string? destination, string eligibilityJson, DateTime nowUtc) =>
        new() { TenantId = tenantId, CampaignId = campaignId, SnapshotId = snapshotId, ContactId = contactId, LeadId = leadId, Sequence = sequence, Destination = destination, EligibilityJson = eligibilityJson, CreatedAtUtc = nowUtc };

    public static CampaignRecipient CreateSkipped(Guid tenantId, Guid campaignId, Guid snapshotId, Guid contactId, Guid leadId, int sequence, string? destination, string reason, string detail, string eligibilityJson, DateTime nowUtc)
    {
        var r = Create(tenantId, campaignId, snapshotId, contactId, leadId, sequence, destination, eligibilityJson, nowUtc);
        r.Status = RecipientStatus.Skipped; r.StatusReason = Cut(reason, 60); r.StatusDetail = Cut(detail, 500); r.UpdatedAtUtc = nowUtc;
        return r;
    }

    /// <summary>Idempotency key handed to the Phase 5 outbound queue. See <c>CampaignKeys</c>.</summary>
    public string IdempotencyKey => CampaignKeys.ForRecipient(CampaignId, Id, Round);

    /// <summary>Pending → Skipped with a reason (the decision JSON is kept). Only a recipient that has not been messaged can be skipped.</summary>
    public bool Skip(string reason, string detail, string? eligibilityJson, DateTime nowUtc)
    {
        if (Status is not (RecipientStatus.Pending or RecipientStatus.Queued)) return false;
        Status = RecipientStatus.Skipped; StatusReason = Cut(reason, 60); StatusDetail = Cut(detail, 500); if (eligibilityJson is not null) EligibilityJson = eligibilityJson; UpdatedAtUtc = nowUtc; Version++;
        return true;
    }

    public bool Cancel(DateTime nowUtc)
    {
        if (Status is not (RecipientStatus.Pending or RecipientStatus.Queued)) return false;
        Status = RecipientStatus.Cancelled; StatusReason = ExclusionCodes.CampaignStopped; UpdatedAtUtc = nowUtc; Version++;
        return true;
    }

    /// <summary>A pause withdrew the queued message: the recipient waits again and its next message gets a new idempotency key (round + 1).</summary>
    public void ReturnToPending(DateTime nowUtc)
    {
        Status = RecipientStatus.Pending; MessageId = null; QueuedAtUtc = null; Round++; StatusReason = null; StatusDetail = null; UpdatedAtUtc = nowUtc; Version++;
    }

    /// <summary>
    /// Applies the state of the recipient's message (the source of truth for delivery): Sent, Delivered, Read move forward only, a reply is never undone, a failed message maps through
    /// <see cref="RecipientRules.FromFailure"/>. Returns what happened so the caller can queue events.
    /// </summary>
    public SyncResult SyncFromMessage(MessageStatus status, DateTime? sentAt, DateTime? deliveredAt, DateTime? readAt, string? failureCode, string? failureMessage, DateTime nowUtc)
    {
        if (Status is RecipientStatus.Pending or RecipientStatus.Skipped or RecipientStatus.Cancelled) return SyncResult.None;
        var before = Status;
        if (sentAt is not null) SentAtUtc ??= sentAt;
        if (deliveredAt is not null) { DeliveredAtUtc ??= deliveredAt; SentAtUtc ??= deliveredAt; }
        if (readAt is not null) { ReadAtUtc ??= readAt; DeliveredAtUtc ??= readAt; SentAtUtc ??= readAt; }

        switch (status)
        {
            case MessageStatus.Sent or MessageStatus.Delivered or MessageStatus.Read:
                var target = status switch { MessageStatus.Sent => RecipientStatus.Sent, MessageStatus.Delivered => RecipientStatus.Delivered, _ => RecipientStatus.Read };
                if (Status == RecipientStatus.Queued || RecipientRules.Rank(target) > RecipientRules.Rank(Status)) Status = target;
                break;
            case MessageStatus.Failed:
                var f = RecipientRules.FromFailure(failureCode);
                if (f.NoChange || Status is not (RecipientStatus.Queued or RecipientStatus.Sent)) break;
                if (f.ReturnToPending) { ReturnToPending(nowUtc); return SyncResult.ReturnedToPending; }
                Status = f.Status; StatusReason = Cut(f.Reason, 60); StatusDetail = Cut(failureMessage, 500); if (f.Status == RecipientStatus.Failed) FailedAtUtc = nowUtc;
                break;
        }
        if (Status == before && !(sentAt is not null || deliveredAt is not null || readAt is not null)) return SyncResult.None;
        UpdatedAtUtc = nowUtc; Version++;
        return Status == before ? SyncResult.TimestampsOnly : SyncResult.Changed;
    }

    /// <summary>A real inbound message answered this campaign's message. Only a message that reached the gateway can be replied to.</summary>
    public bool MarkReplied(DateTime repliedAtUtc, DateTime nowUtc)
    {
        if (!RecipientRules.IsMessaged(Status)) return false;
        RepliedAtUtc ??= repliedAtUtc;
        if (Status == RecipientStatus.Replied) return false;
        Status = RecipientStatus.Replied; UpdatedAtUtc = nowUtc; Version++;
        return true;
    }

    private static string? Cut(string? v, int max) => v is null ? null : v.Length > max ? v[..max] : v;
}

/// <summary>Append-only execution log of a campaign (what the worker did and why).</summary>
public class CampaignExecutionLog : Entity, ITenantOwned, IAppendOnly
{
    private CampaignExecutionLog() { }
    public Guid TenantId { get; private set; }
    public Guid CampaignId { get; private set; }
    public ExecutionLogLevel Level { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Message { get; private set; } = string.Empty;
    public Guid? ActorUserId { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    public static CampaignExecutionLog Create(Guid tenantId, Guid campaignId, ExecutionLogLevel level, string code, string message, Guid? actor, DateTime nowUtc) =>
        new() { TenantId = tenantId, CampaignId = campaignId, Level = level, Code = Guard.Required(code, 60, "Code"), Message = message.Length > 1000 ? message[..1000] : message, ActorUserId = actor, OccurredAtUtc = nowUtc };
}

/// <summary>
/// Read model of the counts of one campaign. It is NEVER incremented: it is recomputed from the recipient rows (which are updated from real message/status/inbound events),
/// so it can only ever equal the raw data (reconciliation-tested).
/// </summary>
public class CampaignStatsRollup : Entity, ITenantOwned
{
    private CampaignStatsRollup() { }
    public Guid TenantId { get; private set; }
    public Guid CampaignId { get; private set; }
    public int Total { get; private set; }
    public int Pending { get; private set; }
    public int Skipped { get; private set; }
    public int Queued { get; private set; }
    public int Sent { get; private set; }
    public int Delivered { get; private set; }
    public int Read { get; private set; }
    public int Replied { get; private set; }
    public int Failed { get; private set; }
    public int Cancelled { get; private set; }
    public int Conversions { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static CampaignStatsRollup Create(Guid tenantId, Guid campaignId, DateTime nowUtc) => new() { TenantId = tenantId, CampaignId = campaignId, UpdatedAtUtc = nowUtc };

    public void Set(IReadOnlyDictionary<RecipientStatus, int> counts, int conversions, DateTime nowUtc)
    {
        int C(RecipientStatus s) => counts.GetValueOrDefault(s);
        Pending = C(RecipientStatus.Pending); Skipped = C(RecipientStatus.Skipped); Queued = C(RecipientStatus.Queued); Sent = C(RecipientStatus.Sent); Delivered = C(RecipientStatus.Delivered);
        Read = C(RecipientStatus.Read); Replied = C(RecipientStatus.Replied); Failed = C(RecipientStatus.Failed); Cancelled = C(RecipientStatus.Cancelled);
        Total = counts.Values.Sum(); Conversions = conversions; UpdatedAtUtc = nowUtc;
    }

    /// <summary>Reached the gateway: Sent and every later state (a delivered message was also sent).</summary>
    public int Messaged => Sent + Delivered + Read + Replied;
    /// <summary>Delivered or better.</summary>
    public int DeliveredOrBetter => Delivered + Read + Replied;
}

/// <summary>A reply or a downstream CRM event attributed to a campaign. Only ever written from a real event, inside the attribution window.</summary>
public class CampaignConversion : Entity, ITenantOwned
{
    private CampaignConversion() { }
    public Guid TenantId { get; private set; }
    public Guid CampaignId { get; private set; }
    public Guid RecipientId { get; private set; }
    public Guid LeadId { get; private set; }
    public ConversionKind Kind { get; private set; }
    /// <summary>The inbound message id (Reply) or the lead activity id (StageChange). Unique with (recipient, kind): one conversion per real event.</summary>
    public Guid ReferenceId { get; private set; }
    public string? Detail { get; private set; }
    public DateTime SentAtUtc { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
    public DateTime RecordedAtUtc { get; private set; }

    public static CampaignConversion Create(Guid tenantId, Guid campaignId, Guid recipientId, Guid leadId, ConversionKind kind, Guid referenceId, string? detail, DateTime sentAtUtc, DateTime occurredAtUtc, DateTime nowUtc) =>
        new() { TenantId = tenantId, CampaignId = campaignId, RecipientId = recipientId, LeadId = leadId, Kind = kind, ReferenceId = referenceId, Detail = detail is { Length: > 300 } ? detail[..300] : detail, SentAtUtc = sentAtUtc, OccurredAtUtc = occurredAtUtc, RecordedAtUtc = nowUtc };
}

/// <summary>Usage ledger (same pattern as the discovery ledger): one row per recipient/round that was queued, per UTC month. Append-only; the monthly limit counts these rows.</summary>
public class CampaignUsageEntry : Entity, ITenantOwned, IAppendOnly
{
    private CampaignUsageEntry() { }
    public Guid TenantId { get; private set; }
    public Guid CampaignId { get; private set; }
    public Guid RecipientId { get; private set; }
    public int Round { get; private set; }
    /// <summary>yyyymm (UTC).</summary>
    public int PeriodKey { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    public static int PeriodOf(DateTime utc) => utc.Year * 100 + utc.Month;
    public static CampaignUsageEntry Create(Guid tenantId, Guid campaignId, Guid recipientId, int round, DateTime nowUtc) =>
        new() { TenantId = tenantId, CampaignId = campaignId, RecipientId = recipientId, Round = round, PeriodKey = PeriodOf(nowUtc), OccurredAtUtc = nowUtc };
}

/// <summary>Per-workspace campaign policy (one row per tenant): cool-down, approval, attribution, failure threshold, opt-out footer, monthly limit, test numbers and the kill switch.</summary>
public class CampaignTenantSettings : AuditableEntity, ITenantOwned
{
    public const int DefaultCooldownDays = 7;
    public const int DefaultAttributionDays = 7;
    public const int DefaultFailurePercent = 30;
    public const int DefaultFailureMinSample = 10;
    public const string DefaultFooter = "Reply STOP to opt out.";
    public const int MaxTestNumbers = 10;

    private CampaignTenantSettings() { }
    public Guid TenantId { get; private set; }
    public int CooldownDays { get; private set; } = DefaultCooldownDays;
    public bool ApprovalRequired { get; private set; }
    public bool CreatorCannotApprove { get; private set; } = true;
    public int AttributionWindowDays { get; private set; } = DefaultAttributionDays;
    public int FailureThresholdPercent { get; private set; } = DefaultFailurePercent;
    public int FailureMinSample { get; private set; } = DefaultFailureMinSample;
    public string OptOutFooter { get; private set; } = DefaultFooter;
    /// <summary>Null = the platform default (<c>Campaigns:DefaultMonthlyMessageLimit</c>).</summary>
    public int? MonthlyMessageLimit { get; private set; }
    /// <summary>JSON array of E.164 numbers a test message may be sent to (the team's own phones).</summary>
    public string TestNumbersJson { get; private set; } = "[]";
    public bool KillSwitch { get; private set; }
    public DateTime? KillSwitchAtUtc { get; private set; }
    public Guid? KillSwitchByUserId { get; private set; }

    public static CampaignTenantSettings Create(Guid tenantId, DateTime nowUtc) => new() { TenantId = tenantId, CreatedAtUtc = nowUtc };

    public void Update(int cooldownDays, bool approvalRequired, bool creatorCannotApprove, int attributionDays, int failurePercent, int failureMinSample, string footer, int? monthlyLimit, string testNumbersJson, string? actor, DateTime nowUtc)
    {
        if (cooldownDays is < 0 or > 365) throw new DomainException("The cool-down must be between 0 and 365 days.");
        if (attributionDays is < 1 or > 90) throw new DomainException("The attribution window must be between 1 and 90 days.");
        if (failurePercent is < 1 or > 100) throw new DomainException("The failure threshold must be between 1 and 100 percent.");
        if (failureMinSample is < 1 or > 1000) throw new DomainException("The minimum sample for the failure threshold must be between 1 and 1000 messages.");
        if (monthlyLimit is < 1 or > 1_000_000) throw new DomainException("The monthly message limit must be between 1 and 1,000,000.");
        CooldownDays = cooldownDays; ApprovalRequired = approvalRequired; CreatorCannotApprove = creatorCannotApprove; AttributionWindowDays = attributionDays;
        FailureThresholdPercent = failurePercent; FailureMinSample = failureMinSample; OptOutFooter = Guard.Required(footer, 200, "Opt-out text"); MonthlyMessageLimit = monthlyLimit; TestNumbersJson = testNumbersJson;
        UpdatedAtUtc = nowUtc; UpdatedBy = actor;
    }

    public void SetKillSwitch(bool on, Guid? userId, string? actor, DateTime nowUtc)
    {
        KillSwitch = on; KillSwitchAtUtc = on ? nowUtc : null; KillSwitchByUserId = on ? userId : null; UpdatedAtUtc = nowUtc; UpdatedBy = actor;
    }
}
