using System.Text.Json;
using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Messaging.Services;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenancyResultFlow = LeadPilot.Infrastructure.Services.Tenancy.ResultFlow;

namespace LeadPilot.Infrastructure.Campaigns;

/// <summary>Reads the workspace campaign policy; a workspace that never saved settings gets the defaults (nothing is written by a read).</summary>
public sealed class CampaignSettingsStore(CrmContext crm)
{
    public async Task<CampaignTenantSettings> GetAsync(bool tracked, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var q = tracked ? crm.Db.CampaignTenantSettings : crm.Db.CampaignTenantSettings.AsNoTracking();
        return await q.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct) ?? CampaignTenantSettings.Create(tenantId, crm.Now);
    }

    /// <summary>The row to modify: created (and saved) on first use. A concurrent creator wins; its row is returned.</summary>
    public async Task<CampaignTenantSettings> EnsureAsync(CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var row = await crm.Db.CampaignTenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (row is not null) return row;
        row = CampaignTenantSettings.Create(tenantId, crm.Now);
        crm.Db.CampaignTenantSettings.Add(row);
        try { await crm.Db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (TenancyResultFlow.IsDuplicate(ex))
        {
            crm.Db.Entry(row).State = EntityState.Detached;
            row = await crm.Db.CampaignTenantSettings.FirstAsync(s => s.TenantId == tenantId, ct);
        }
        return row;
    }

    public static IReadOnlyList<string> TestNumbers(CampaignTenantSettings s)
    {
        try { return JsonSerializer.Deserialize<List<string>>(s.TestNumbersJson) ?? []; } catch (JsonException) { return []; }
    }
}

/// <summary>
/// Shared behaviour of the campaign module: execution log, outbox events, the stats rollup (always recomputed from the raw recipient rows), pausing, and withdrawing queued messages.
/// Everything is tenant-scoped through <see cref="CrmContext"/>.
/// </summary>
public sealed class CampaignCore(CrmContext crm, ICampaignChannel channel, ICampaignJobScheduler jobs, CampaignSettingsStore settings, IOptions<CampaignOptions> options, ILogger<CampaignCore> logger)
{
    public CrmContext Crm => crm;

    public void Log(Guid campaignId, ExecutionLogLevel level, string code, string message, Guid? actor = null) =>
        crm.Db.CampaignExecutionLogs.Add(CampaignExecutionLog.Create(crm.TenantId, campaignId, level, code, message, actor, crm.Now));

    public void PublishStatus(Campaign c, string? reason = null, string? correlationId = null) => PublishStatus(c.Id, c.Status, reason, correlationId);

    public void PublishStatus(Guid campaignId, CampaignStatus status, string? reason = null, string? correlationId = null) =>
        crm.Db.OutboxMessages.Add(OutboxMessage.Create(crm.TenantId, CampaignEventTypes.StatusChanged, JsonSerializer.Serialize(new CampaignStatusChangedPayload(campaignId, status.ToString(), reason)), correlationId ?? Guid.NewGuid().ToString("N"), campaignId.ToString(), crm.Now));

    public void PublishRecipient(Guid campaignId, Guid recipientId, RecipientStatus status, string? correlationId = null) =>
        crm.Db.OutboxMessages.Add(OutboxMessage.Create(crm.TenantId, CampaignEventTypes.RecipientChanged, JsonSerializer.Serialize(new CampaignRecipientChangedPayload(campaignId, recipientId, status.ToString())), correlationId ?? Guid.NewGuid().ToString("N"), recipientId.ToString(), crm.Now));

    // ---------- rollup (read model) ----------

    public async Task<Dictionary<RecipientStatus, int>> LiveCountsAsync(Guid campaignId, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var rows = await crm.Db.CampaignRecipients.AsNoTracking().Where(r => r.TenantId == tenantId && r.CampaignId == campaignId).GroupBy(r => r.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => r.N);
    }

    public Task<int> LiveConversionsAsync(Guid campaignId, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        return crm.Db.CampaignConversions.AsNoTracking().CountAsync(c => c.TenantId == tenantId && c.CampaignId == campaignId, ct);
    }

    /// <summary>Recomputes the rollup row from the raw recipient and conversion rows (never incremented, so it can only equal the raw data).</summary>
    public async Task<CampaignStatsRollup> RecomputeRollupAsync(Guid campaignId, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var counts = await LiveCountsAsync(campaignId, ct); var conversions = await LiveConversionsAsync(campaignId, ct);
        for (var attempt = 0; ; attempt++)
        {
            var row = await db.CampaignStatsRollups.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.CampaignId == campaignId, ct);
            var isNew = row is null;
            row ??= CampaignStatsRollup.Create(tenantId, campaignId, crm.Now);
            row.Set(counts, conversions, crm.Now);
            if (isNew) db.CampaignStatsRollups.Add(row);
            try { await db.SaveChangesAsync(ct); return row; }
            catch (DbUpdateException ex) when (attempt < 2 && (TenancyResultFlow.IsDuplicate(ex) || ex is DbUpdateConcurrencyException)) { db.Entry(row).State = EntityState.Detached; }
        }
    }

    // ---------- pausing and withdrawing ----------

    /// <summary>
    /// Pauses a running campaign automatically (account trouble, failure rate, limits, kill switch), withdraws its queued messages and logs why. Returns false when it was not running
    /// (already paused/cancelled/completed, or a person changed it concurrently).
    /// </summary>
    public async Task<bool> AutoPauseAsync(Guid campaignId, string reason, string logCode, string withdrawCode = "campaign_paused", CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var c = await db.Campaigns.FirstOrDefaultAsync(x => x.Id == campaignId && x.TenantId == tenantId, ct);
        if (c is null || c.Status != CampaignStatus.Running) return false;
        c.Pause(reason, automatic: true, crm.Now);
        Log(c.Id, ExecutionLogLevel.Warning, logCode, "Paused automatically: " + reason);
        PublishStatus(c, reason);
        crm.Record("Campaign.AutoPaused", "Campaign", c.Id, $"Campaign '{c.Name}' paused automatically: {reason}");
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return false; }
        await WithdrawQueuedAsync(campaignId, withdrawCode, reason, ct);
        return true;
    }

    /// <summary>Cancels messages of the campaign that are still waiting in the Phase 5 queue and syncs their recipients (a pause sends them back to Pending, a cancel cancels them).</summary>
    public async Task<int> WithdrawQueuedAsync(Guid campaignId, string code, string reason, CancellationToken ct)
    {
        var n = await channel.CancelQueuedAsync(campaignId, code, reason, ct);
        await SyncQueuedRecipientsAsync(campaignId, ct);
        return n;
    }

    /// <summary>
    /// Repairs recipients that are still Queued although their message already reached a final state (a missed hook, a crash after the send): applies the message state to the recipient.
    /// Also the mechanism that turns withdrawn messages into Pending / Cancelled recipients. Returns the number of recipients changed.
    /// </summary>
    public async Task<int> SyncQueuedRecipientsAsync(Guid campaignId, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var changed = 0;
        for (var round = 0; round < 50; round++)
        {
            db.ChangeTracker.Clear();
            var batch = await (from r in db.CampaignRecipients
                               join m in db.Messages on r.MessageId equals m.Id
                               where r.TenantId == tenantId && r.CampaignId == campaignId && (r.Status == RecipientStatus.Queued || r.Status == RecipientStatus.Sent || r.Status == RecipientStatus.Delivered)
                                     && m.TenantId == tenantId && m.Status != MessageStatus.Queued && m.Status != MessageStatus.Sending && m.Status != MessageStatus.Unknown
                                     && (r.Status == RecipientStatus.Queued || (m.Status == MessageStatus.Read && r.Status != RecipientStatus.Read) || (m.Status == MessageStatus.Delivered && r.Status == RecipientStatus.Sent))
                               select m).Take(100).AsNoTracking().ToListAsync(ct);
            if (batch.Count == 0) break;
            foreach (var m in batch) if (await ApplyMessageAsync(m, save: false, ct) != SyncResult.None) changed++;
            await db.SaveChangesAsync(ct);
        }
        return changed;
    }

    // ---------- recipient ← message ----------

    /// <summary>Applies the state of a campaign message to its recipient: status, timestamps, usage ledger on the first Sent, rollup event. Stages changes; the caller saves unless <paramref name="save"/>.</summary>
    public async Task<SyncResult> ApplyMessageAsync(Message m, bool save, CancellationToken ct)
    {
        if (m.CampaignRecipientId is not { } recipientId) return SyncResult.None;
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var r = await db.CampaignRecipients.FirstOrDefaultAsync(x => x.Id == recipientId && x.TenantId == tenantId, ct);
        if (r is null || r.MessageId != m.Id) return SyncResult.None;   // a stale message of an earlier round
        var result = r.SyncFromMessage(m.Status, m.SentAtUtc, m.DeliveredAtUtc, m.ReadAtUtc, m.FailureCode, m.FailureMessage, now);
        if (result == SyncResult.None) return result;
        if (RecipientRules.IsMessaged(r.Status) && !await db.CampaignUsageEntries.AnyAsync(u => u.TenantId == tenantId && u.RecipientId == r.Id && u.Round == r.Round, ct))
            db.CampaignUsageEntries.Add(CampaignUsageEntry.Create(tenantId, r.CampaignId, r.Id, r.Round, m.SentAtUtc ?? now));
        if (result is SyncResult.Changed or SyncResult.ReturnedToPending) PublishRecipient(r.CampaignId, r.Id, r.Status, m.CorrelationId);
        if (r.Status == RecipientStatus.Failed) Log(r.CampaignId, ExecutionLogLevel.Warning, "recipient_failed", $"A message failed ({r.StatusReason}): {r.StatusDetail}");
        if (save)
        {
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (TenancyResultFlow.IsDuplicate(ex)) { db.ChangeTracker.Clear(); }   // the usage row of this round already exists
            if (r.Status == RecipientStatus.Failed) await EvaluateFailureRateAsync(r.CampaignId, ct);
        }
        return result;
    }

    /// <summary>Pauses the campaign when the failure rate (failed ÷ messaged-or-failed) reaches the threshold with enough samples.</summary>
    public async Task<bool> EvaluateFailureRateAsync(Guid campaignId, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var c = await db.Campaigns.AsNoTracking().Where(x => x.Id == campaignId && x.TenantId == tenantId).Select(x => new { x.Status, x.FailureThresholdPercent }).FirstOrDefaultAsync(ct);
        if (c is null || c.Status != CampaignStatus.Running) return false;
        var s = await settings.GetAsync(false, ct);
        var counts = await LiveCountsAsync(campaignId, ct);
        var failed = counts.GetValueOrDefault(RecipientStatus.Failed);
        var attempted = failed + counts.Where(kv => RecipientRules.IsMessaged(kv.Key)).Sum(kv => kv.Value);
        var threshold = c.FailureThresholdPercent ?? s.FailureThresholdPercent;
        if (!FailureRatePolicy.ShouldPause(failed, attempted, threshold, s.FailureMinSample)) return false;
        return await AutoPauseAsync(campaignId, $"{failed} of {attempted} messages failed ({FailureRatePolicy.Percent(failed, attempted)}%), which reaches the threshold of {threshold}%. Check the WhatsApp account, then resume.", "failure_threshold", ct: ct);
    }

    // ---------- usage ----------

    public async Task<int> UsedThisMonthAsync(CancellationToken ct)
    {
        var tenantId = crm.TenantId; var period = CampaignUsageEntry.PeriodOf(crm.Now);
        return await crm.Db.CampaignUsageEntries.AsNoTracking().CountAsync(u => u.TenantId == tenantId && u.PeriodKey == period, ct);
    }

    public int EffectiveMonthlyLimit(CampaignTenantSettings s) => s.MonthlyMessageLimit ?? options.Value.DefaultMonthlyMessageLimit;

    /// <summary>Messages of campaigns that were queued but have not been sent yet (they will consume allowance).</summary>
    public async Task<int> InFlightCampaignMessagesAsync(CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        return await crm.Db.Messages.AsNoTracking().CountAsync(m => m.TenantId == tenantId && m.CampaignRecipientId != null && (m.Status == MessageStatus.Queued || m.Status == MessageStatus.Sending || m.Status == MessageStatus.Unknown), ct);
    }

    public ILogger Logger => logger;
    public ICampaignJobScheduler Jobs => jobs;
}

/// <summary>WhatsApp implementation of the campaign channel: every message goes through the Phase 5 outbound queue (the only send pipeline).</summary>
public sealed class WhatsAppCampaignChannel(OutboundQueueService queue, CrmContext crm) : ICampaignChannel
{
    public CampaignChannel Channel => CampaignChannel.WhatsApp;

    public async Task<Result<QueuedCampaignMessage>> QueueAsync(CampaignChannelRequest r, CancellationToken ct = default)
    {
        var res = await queue.QueueAsync(new QueueRequest(null, r.ContactId, r.ChannelAccountId, r.Text, r.TemplateId, r.IdempotencyKey, false)
        { ForcedPurpose = MessagePurpose.Marketing, CampaignId = r.CampaignId, CampaignRecipientId = r.RecipientId, TextIsRendered = true }, ct);
        return Map(res);
    }

    public async Task<Result<QueuedCampaignMessage>> QueueTestAsync(CampaignChannelTestRequest r, CancellationToken ct = default)
    {
        var res = await queue.QueueAsync(new QueueRequest(null, null, r.ChannelAccountId, r.Text, null, r.IdempotencyKey, false)
        { ForcedPurpose = MessagePurpose.Service, CampaignId = r.CampaignId, TestPhoneE164 = r.PhoneE164, TextIsRendered = true }, ct);
        return Map(res);
    }

    private Result<QueuedCampaignMessage> Map(Result<Message> res) => res.IsSuccess
        ? Result.Success(new QueuedCampaignMessage(res.Value.Id, res.Value.ConversationId, res.Value.Status, res.Value.CreatedAtUtc < crm.Now.AddSeconds(-5)))
        : Result.Failure<QueuedCampaignMessage>(res.Error);

    public async Task<int> CancelQueuedAsync(Guid campaignId, string code, string reason, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now; var cancelled = 0;
        var ids = await db.Messages.AsNoTracking().Where(m => m.TenantId == tenantId && m.CampaignId == campaignId && m.CampaignRecipientId != null && m.Status == MessageStatus.Queued).Select(m => m.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            // atomic: a message the send runner already claimed (Sending) is NOT touched and finishes normally
            var n = await db.Messages.Where(m => m.Id == id && m.TenantId == tenantId && m.Status == MessageStatus.Queued)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MessageStatus.Failed).SetProperty(m => m.FailureCode, code).SetProperty(m => m.FailureMessage, reason.Length > 500 ? reason[..500] : reason)
                    .SetProperty(m => m.FailedAtUtc, now).SetProperty(m => m.NextAttemptAtUtc, (DateTime?)null).SetProperty(m => m.Version, m => m.Version + 1), ct);
            if (n == 0) continue;
            cancelled++;
            db.MessageStatusEvents.Add(MessageStatusEvent.Create(tenantId, id, MessageStatus.Failed, "campaign", null, true, now, now));
        }
        if (cancelled > 0) await db.SaveChangesAsync(ct);
        return cancelled;
    }
}

/// <summary>
/// Called by the Phase 5 send runner immediately before a campaign message is sent. Blocks when the workspace kill switch is on, the campaign is not running, or the recipient is no longer waiting
/// for exactly this message; re-checks marketing eligibility (the decision is stored on the recipient next to the selection-time decision) and skips the recipient when it changed.
/// </summary>
public sealed class CampaignSendGuard(CrmContext crm, IContactEligibilityService eligibility, CampaignSettingsStore settings, ILogger<CampaignSendGuard> logger) : ICampaignSendGuard
{
    public async Task<CampaignSendBlock?> CheckBeforeSendAsync(Message message, CancellationToken ct = default)
    {
        if (message.CampaignRecipientId is not { } recipientId || message.CampaignId is not { } campaignId) return null;
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var c = await db.Campaigns.AsNoTracking().Where(x => x.Id == campaignId && x.TenantId == tenantId).Select(x => new { x.Status }).FirstOrDefaultAsync(ct);
        if (c is null) return new CampaignSendBlock("campaign_cancelled", "The campaign no longer exists.", false);
        if ((await settings.GetAsync(false, ct)).KillSwitch) return new CampaignSendBlock("campaign_kill_switch", "The workspace kill switch is on: no campaign message is sent.", false);
        switch (c.Status)
        {
            case CampaignStatus.Running: break;
            case CampaignStatus.Paused: return new CampaignSendBlock("campaign_paused", "The campaign was paused before this message was sent.", true);
            case CampaignStatus.Cancelled: return new CampaignSendBlock("campaign_cancelled", "The campaign was cancelled before this message was sent.", false);
            default: return new CampaignSendBlock("campaign_stopped", $"The campaign is {c.Status}; nothing is sent.", false);
        }
        var r = await db.CampaignRecipients.AsNoTracking().FirstOrDefaultAsync(x => x.Id == recipientId && x.TenantId == tenantId, ct);
        if (r is null || r.MessageId != message.Id || r.Status != RecipientStatus.Queued) return new CampaignSendBlock("recipient_superseded", "The recipient is no longer waiting for this message.", false);

        // marketing permission, validated again immediately before the send; both decisions stay on the recipient
        var decision = await eligibility.CheckAsync(r.ContactId, ConsentChannel.WhatsApp, ConsentPurpose.Marketing, ct);
        var record = new EligibilityRecord(now, decision.IsEligible, decision.Reasons.Select(ExclusionReasons.Code).ToList());
        var trail = EligibilityTrail.Parse(r.EligibilityJson).WithSend(record);
        var contact = decision.IsEligible ? await db.Contacts.AsNoTracking().Where(x => x.Id == r.ContactId && x.TenantId == tenantId).Select(x => x.PhoneE164).FirstOrDefaultAsync(ct) : null;
        var changedPhone = decision.IsEligible && r.Destination is not null && contact != r.Destination;
        if (decision.IsEligible && !changedPhone)
        {
            await db.CampaignRecipients.Where(x => x.Id == r.Id && x.TenantId == tenantId && x.Status == RecipientStatus.Queued).ExecuteUpdateAsync(s => s.SetProperty(x => x.EligibilityJson, trail.ToJson()), ct);
            return null;
        }
        var reason = changedPhone ? ExclusionReasons.ContactChanged : ExclusionReasons.Primary(decision.Reasons);
        var detail = changedPhone ? "The contact's phone number changed after the audience was selected." : string.Join(" ", decision.Reasons.Select(EligibilityDecision.Explain));
        await db.CampaignRecipients.Where(x => x.Id == r.Id && x.TenantId == tenantId && x.Status == RecipientStatus.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, RecipientStatus.Skipped).SetProperty(x => x.StatusReason, reason).SetProperty(x => x.StatusDetail, detail).SetProperty(x => x.EligibilityJson, trail.ToJson())
                .SetProperty(x => x.UpdatedAtUtc, now).SetProperty(x => x.Version, x => x.Version + 1), ct);
        db.CampaignExecutionLogs.Add(CampaignExecutionLog.Create(tenantId, campaignId, ExecutionLogLevel.Warning, "skipped_at_send", $"A recipient was skipped immediately before sending ({reason}).", null, now));
        crm.Db.OutboxMessages.Add(OutboxMessage.Create(tenantId, CampaignEventTypes.RecipientChanged, JsonSerializer.Serialize(new CampaignRecipientChangedPayload(campaignId, r.Id, nameof(RecipientStatus.Skipped))), message.CorrelationId, r.Id.ToString(), now));
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Campaign recipient {RecipientId} skipped at send time: {Reason}", r.Id, reason);
        return new CampaignSendBlock("campaign_skipped", "Not sent: " + detail, false);
    }
}

/// <summary>Real events into campaign tracking: send outcomes, replies, CRM stage changes, account trouble.</summary>
public sealed class CampaignEvents(CampaignCore core, CrmContext crm, ILogger<CampaignEvents> logger) : ICampaignEvents
{
    public async Task OnOutboundChangedAsync(Guid messageId, bool save = true, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var m = await db.Messages.FirstOrDefaultAsync(x => x.Id == messageId && x.TenantId == tenantId, ct);
        if (m is null || m.CampaignRecipientId is null) return;
        for (var attempt = 0; ; attempt++)
        {
            try { await core.ApplyMessageAsync(m, save, ct); return; }
            catch (DbUpdateConcurrencyException) when (save && attempt < 3)
            {
                db.ChangeTracker.Clear();
                m = await db.Messages.FirstOrDefaultAsync(x => x.Id == messageId && x.TenantId == tenantId, ct);
                if (m is null) return;
            }
        }
    }

    public async Task OnInboundMessageAsync(Guid conversationId, Guid inboundMessageId, bool isOptOut, CancellationToken ct = default)
    {
        await StageInboundAsync(conversationId, inboundMessageId, isOptOut, ct);
        await crm.Db.SaveChangesAsync(ct);
    }

    public async Task StageInboundAsync(Guid conversationId, Guid inboundMessageId, bool isOptOut, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var inbound = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == inboundMessageId && m.TenantId == tenantId && m.Direction == MessageDirection.Inbound && m.ConversationId == conversationId, ct);
        if (inbound is null) return;
        var rows = await (from m in db.Messages.AsNoTracking()
                          join r in db.CampaignRecipients on m.CampaignRecipientId equals r.Id
                          join c in db.Campaigns on r.CampaignId equals c.Id
                          where m.TenantId == tenantId && m.ConversationId == conversationId && m.Direction == MessageDirection.Outbound && m.SentAtUtc != null && m.SentAtUtc <= inbound.OccurredAtUtc && r.MessageId == m.Id
                                && (r.Status == RecipientStatus.Sent || r.Status == RecipientStatus.Delivered || r.Status == RecipientStatus.Read || r.Status == RecipientStatus.Replied)
                          orderby m.SentAtUtc descending
                          select new { r.Id, r.CampaignId, r.LeadId, SentAt = m.SentAtUtc!.Value, c.AttributionWindowDays, c.Name }).Take(10).ToListAsync(ct);
        var inWindow = rows.Where(x => AttributionRules.IsInside(x.SentAt, inbound.OccurredAtUtc, TimeSpan.FromDays(x.AttributionWindowDays))).ToList();
        var pick = AttributionRules.Pick(inWindow.Select(x => new AttributionCandidate(x.Id, x.CampaignId, x.SentAt)), inbound.OccurredAtUtc, TimeSpan.FromDays(inWindow.Count == 0 ? 1 : inWindow.Max(x => x.AttributionWindowDays)));
        if (pick is null) return;
        var chosen = inWindow.First(x => x.Id == pick.RecipientId);
        if (isOptOut)
        {
            core.Log(chosen.CampaignId, ExecutionLogLevel.Info, "opt_out_received", "A recipient answered with an opt-out. It is not counted as a reply; the number is suppressed.");
            return;
        }
        var recipient = await db.CampaignRecipients.FirstAsync(r => r.Id == chosen.Id && r.TenantId == tenantId, ct);
        var changed = recipient.MarkReplied(inbound.OccurredAtUtc, now);
        if (!await db.CampaignConversions.AnyAsync(c => c.TenantId == tenantId && c.RecipientId == recipient.Id && c.Kind == ConversionKind.Reply && c.ReferenceId == inbound.Id, ct))
            db.CampaignConversions.Add(CampaignConversion.Create(tenantId, chosen.CampaignId, recipient.Id, recipient.LeadId, ConversionKind.Reply, inbound.Id, "Replied to the campaign message", chosen.SentAt, inbound.OccurredAtUtc, now));
        if (changed) { core.PublishRecipient(chosen.CampaignId, recipient.Id, RecipientStatus.Replied, inbound.CorrelationId); core.Log(chosen.CampaignId, ExecutionLogLevel.Info, "reply", "A recipient replied inside the attribution window."); }
    }

    public async Task OnLeadStageChangedAsync(Guid leadId, Guid fromStageId, Guid toStageId, Guid activityId, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var to = await db.PipelineStages.AsNoTracking().Where(s => s.Id == toStageId && s.TenantId == tenantId).Select(s => new { s.Name, s.Kind }).FirstOrDefaultAsync(ct);
        if (to is null || to.Kind is StageKind.Lost or StageKind.Suppressed) return;   // only progress is a conversion
        var rows = await (from r in db.CampaignRecipients.AsNoTracking()
                          join c in db.Campaigns on r.CampaignId equals c.Id
                          where r.TenantId == tenantId && r.LeadId == leadId && r.SentAtUtc != null && r.SentAtUtc <= now
                                && (r.Status == RecipientStatus.Sent || r.Status == RecipientStatus.Delivered || r.Status == RecipientStatus.Read || r.Status == RecipientStatus.Replied)
                          orderby r.SentAtUtc descending
                          select new { r.Id, r.CampaignId, r.Status, SentAt = r.SentAtUtc!.Value, c.AttributionWindowDays }).Take(10).ToListAsync(ct);
        var inWindow = rows.Where(x => AttributionRules.IsInside(x.SentAt, now, TimeSpan.FromDays(x.AttributionWindowDays))).ToList();
        var pick = inWindow.OrderByDescending(x => x.SentAt).FirstOrDefault();
        if (pick is null) return;
        if (await db.CampaignConversions.AnyAsync(c => c.TenantId == tenantId && c.RecipientId == pick.Id && c.Kind == ConversionKind.StageChange && c.ReferenceId == activityId, ct)) return;
        db.CampaignConversions.Add(CampaignConversion.Create(tenantId, pick.CampaignId, pick.Id, leadId, ConversionKind.StageChange, activityId, $"Lead moved to '{to.Name}' ({to.Kind})", pick.SentAt, now, now));
        core.PublishRecipient(pick.CampaignId, pick.Id, pick.Status);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (TenancyResultFlow.IsDuplicate(ex)) { db.ChangeTracker.Clear(); }
    }

    public async Task OnAccountUnavailableAsync(Guid accountId, string reason, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var ids = await db.Campaigns.AsNoTracking().Where(c => c.TenantId == tenantId && c.ChannelAccountId == accountId && c.Status == CampaignStatus.Running).Select(c => c.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            try { await core.AutoPauseAsync(id, "The WhatsApp account is unavailable: " + reason, "account_unavailable", ct: ct); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not auto-pause campaign {CampaignId} after an account problem", id); }
        }
    }
}
