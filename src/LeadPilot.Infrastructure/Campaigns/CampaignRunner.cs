using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Messaging.Services;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Campaigns;

/// <summary>
/// Executes campaigns. One tick = one batch: it claims a lease (one tick per campaign at a time), repairs recipients from their messages, completes or pauses the campaign when it must,
/// otherwise hands up to a batch of Pending recipients to the Phase 5 outbound queue one by one (re-validating marketing permission for each) and schedules the next tick after a random pause.
/// The recipient table is the checkpoint: a restarted run continues from the recipients' statuses, and the (campaign, recipient, round) idempotency key makes a repeated hand-over return the
/// message that already exists instead of creating another. Rate limits, caps and warm-up are enforced twice: here (allowance) and by the Phase 5 send runner.
/// </summary>
internal sealed class CampaignRunner(
    CampaignCore core, CrmContext crm, ICampaignChannel channel, IContactEligibilityService eligibility, AudienceSnapshotBuilder snapshots, CampaignSettingsStore settings,
    ICampaignJobScheduler jobs, IMessagingJobScheduler messagingJobs, IOptions<CampaignOptions> options, ILogger<CampaignRunner> logger) : ICampaignRunner
{
    private enum Outcome { Queued, Skipped, Failed, Deferred, Stop }

    public async Task StartAsync(Guid campaignId, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var c = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == campaignId && x.TenantId == tenantId, ct);
        if (c is null) return;
        if (c.Status == CampaignStatus.Running) { jobs.EnqueueTick(tenantId, c.Id); return; }
        if (c.Status != CampaignStatus.Scheduled) return;                 // a stale job (cancelled, unscheduled, already finished)
        var s = await settings.GetAsync(false, ct);
        if (s.KillSwitch) { await DeferAsync(c, "The workspace kill switch is on; the campaign stays scheduled.", ct); return; }
        if (s.ApprovalRequired && c.ApprovedAtUtc is null) { await DeferAsync(c, "The workspace requires approval and this campaign was never approved; it will not start.", ct); return; }
        if (c.ScheduledAtUtc is { } at && at > now.AddSeconds(30)) return;   // an early job
        if (c.ParentCampaignId is { } pid && c.Type == CampaignType.FollowUp)
        {
            var parent = await db.Campaigns.AsNoTracking().Where(p => p.Id == pid && p.TenantId == tenantId).Select(p => p.Status).FirstOrDefaultAsync(ct);
            if (parent is not (CampaignStatus.Completed or CampaignStatus.Cancelled or CampaignStatus.Failed)) { await DeferAsync(c, "The parent campaign has not finished yet; the follow-up waits.", ct); return; }
        }

        var started = await db.Campaigns.Where(x => x.Id == campaignId && x.TenantId == tenantId && x.Status == CampaignStatus.Scheduled)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, CampaignStatus.Running).SetProperty(x => x.StartedAtUtc, now).SetProperty(x => x.NextRunAtUtc, now).SetProperty(x => x.ScheduleJobId, (string?)null)
                .SetProperty(x => x.UpdatedAtUtc, now).SetProperty(x => x.Version, x => x.Version + 1), ct);
        if (started == 0) return;
        db.ChangeTracker.Clear();
        core.Log(c.Id, ExecutionLogLevel.Info, "started", "The campaign started.");
        core.PublishStatus(c.Id, CampaignStatus.Running);
        crm.Record("Campaign.Started", "Campaign", c.Id, $"Campaign '{c.Name}' started.");
        await db.SaveChangesAsync(ct);
        jobs.EnqueueTick(tenantId, c.Id);
    }

    private async Task DeferAsync(Campaign c, string reason, CancellationToken ct)
    {
        var now = crm.Now;
        if (c.NextRunAtUtc is { } n && n > now) return;   // already reported recently
        await crm.Db.Campaigns.Where(x => x.Id == c.Id && x.TenantId == crm.TenantId).ExecuteUpdateAsync(u => u.SetProperty(x => x.NextRunAtUtc, now.AddMinutes(10)), ct);
        core.Log(c.Id, ExecutionLogLevel.Warning, "start_deferred", reason);
        await crm.Db.SaveChangesAsync(ct);
    }

    public async Task TickAsync(Guid campaignId, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now; var o = options.Value;
        var running = await db.Campaigns.AsNoTracking().AnyAsync(x => x.Id == campaignId && x.TenantId == tenantId && x.Status == CampaignStatus.Running, ct);
        if (!running) return;
        var lease = now.AddSeconds(Math.Max(30, o.LeaseSeconds));
        var claimed = await db.Campaigns.Where(x => x.Id == campaignId && x.TenantId == tenantId && x.Status == CampaignStatus.Running && (x.LeaseUntilUtc == null || x.LeaseUntilUtc < now))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.LeaseUntilUtc, lease).SetProperty(x => x.LastTickAtUtc, now), ct);
        if (claimed == 0) { logger.LogDebug("Campaign {CampaignId} is being processed by another tick", campaignId); return; }
        try { await TickCoreAsync(campaignId, ct); }
        finally
        {
            try { await crm.Db.Campaigns.Where(x => x.Id == campaignId && x.TenantId == tenantId).ExecuteUpdateAsync(u => u.SetProperty(x => x.LeaseUntilUtc, (DateTime?)null), CancellationToken.None); }
            catch (Exception ex) { logger.LogWarning(ex, "Could not release the lease of campaign {CampaignId}; it expires on its own", campaignId); }
        }
    }

    private async Task TickCoreAsync(Guid campaignId, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now; var o = options.Value;
        db.ChangeTracker.Clear();
        var c = await db.Campaigns.AsNoTracking().FirstAsync(x => x.Id == campaignId && x.TenantId == tenantId, ct);
        var s = await settings.GetAsync(false, ct);
        if (s.KillSwitch) { await core.AutoPauseAsync(c.Id, "The workspace kill switch is on.", "kill_switch", "campaign_kill_switch", ct); return; }

        var account = c.ChannelAccountId is { } aid ? await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == aid && a.TenantId == tenantId, ct) : null;
        if (account is null || account.IsDeleted) { await core.AutoPauseAsync(c.Id, "The WhatsApp account of this campaign no longer exists.", "account_unavailable", ct: ct); return; }
        if (account.IsPaused) { await core.AutoPauseAsync(c.Id, "The WhatsApp account is paused" + (account.PauseReason is null ? "." : ": " + account.PauseReason), "account_unavailable", ct: ct); return; }
        if (account.ConnectionStatus != ConnectionStatus.Working) { await core.AutoPauseAsync(c.Id, "The WhatsApp account is not connected.", "account_unavailable", ct: ct); return; }

        if (c.SnapshotId is null || !await db.CampaignAudienceSnapshots.AsNoTracking().AnyAsync(x => x.Id == c.SnapshotId && x.TenantId == tenantId && x.Status == SnapshotStatus.Ready, ct))
        {
            await snapshots.BuildAsync(c, ct);
            if (!await IsRunningAsync(c.Id, ct)) return;
        }

        await core.SyncQueuedRecipientsAsync(c.Id, ct);
        var counts = await core.LiveCountsAsync(c.Id, ct);
        var pending = counts.GetValueOrDefault(RecipientStatus.Pending); var queued = counts.GetValueOrDefault(RecipientStatus.Queued);
        if (pending == 0 && queued == 0) { await CompleteAsync(c, ct); return; }
        if (await core.EvaluateFailureRateAsync(c.Id, ct)) return;
        if (pending == 0) { await ScheduleNextAsync(c.Id, TimeSpan.FromSeconds(30), ct); return; }   // waiting for in-flight messages to settle

        var used = await core.UsedThisMonthAsync(ct); var inFlightAll = await core.InFlightCampaignMessagesAsync(ct);
        var limit = core.EffectiveMonthlyLimit(s); var remaining = limit - used - inFlightAll;
        if (remaining <= 0) { await core.AutoPauseAsync(c.Id, $"The workspace reached its monthly campaign message limit ({limit}). Raise the limit in the campaign settings, then resume.", "monthly_limit", ct: ct); return; }

        var facts = await SendRateFactsLoader.LoadAsync(db, account, now, ct);
        var accountQueued = await db.Messages.AsNoTracking().CountAsync(m => m.TenantId == tenantId && m.ChannelAccountId == account.Id && m.Direction == MessageDirection.Outbound && m.Status == MessageStatus.Queued, ct);
        var inFlight = await db.Messages.AsNoTracking().CountAsync(m => m.TenantId == tenantId && m.CampaignId == c.Id && m.CampaignRecipientId != null && (m.Status == MessageStatus.Queued || m.Status == MessageStatus.Sending || m.Status == MessageStatus.Unknown), ct);
        if (inFlight >= CampaignPacing.MaxInFlight(c.BatchSize)) { await ScheduleNextAsync(c.Id, TimeSpan.FromSeconds(30), ct); return; }
        var dayAgo = now.AddHours(-24);
        var queuedLast24h = await db.CampaignRecipients.AsNoTracking().CountAsync(r => r.TenantId == tenantId && r.CampaignId == c.Id && r.QueuedAtUtc != null && r.QueuedAtUtc >= dayAgo, ct);
        var allowance = CampaignPacing.BatchAllowance(c.BatchSize, pending, facts, accountQueued, now, c.DailyLimit, queuedLast24h, remaining);
        if (allowance <= 0)
        {
            var wait = (CampaignPacing.AccountRetryAt(facts, now) ?? now.AddMinutes(5)) - now;
            if (c.DailyLimit is { } dl && queuedLast24h >= dl) wait = TimeSpan.FromMinutes(30);
            if (wait < TimeSpan.FromSeconds(30)) wait = TimeSpan.FromSeconds(30);
            core.Log(c.Id, ExecutionLogLevel.Info, "waiting_for_capacity", $"Waiting for sending capacity (account caps, warm-up or the campaign's daily limit); next attempt in about {Math.Ceiling(wait.TotalMinutes)} minute(s).");
            await db.SaveChangesAsync(ct);
            await ScheduleNextAsync(c.Id, wait, ct);
            return;
        }

        var batch = await db.CampaignRecipients.AsNoTracking().Where(r => r.TenantId == tenantId && r.CampaignId == c.Id && r.Status == RecipientStatus.Pending).OrderBy(r => r.Sequence).Take(allowance).ToListAsync(ct);
        var tally = new Dictionary<Outcome, int>();
        foreach (var r in batch)
        {
            ct.ThrowIfCancellationRequested();
            if (!await IsRunningAsync(c.Id, ct)) break;                 // pause / cancel / kill switch take effect before the next recipient
            var outcome = await ProcessRecipientAsync(c, account, r, s, ct);
            tally[outcome] = tally.GetValueOrDefault(outcome) + 1;
            if (outcome is Outcome.Stop or Outcome.Deferred) break;
        }
        db.ChangeTracker.Clear();
        if (tally.Count > 0)
        {
            core.Log(c.Id, ExecutionLogLevel.Info, "batch", $"Batch handled: {tally.GetValueOrDefault(Outcome.Queued)} queued, {tally.GetValueOrDefault(Outcome.Skipped)} skipped, {tally.GetValueOrDefault(Outcome.Failed)} failed" + (tally.ContainsKey(Outcome.Deferred) ? ", stopped early (rate limit)" : "") + ".");
            await db.SaveChangesAsync(ct);
        }
        await core.RecomputeRollupAsync(c.Id, ct);
        if (!await IsRunningAsync(c.Id, ct)) return;
        await ScheduleNextAsync(c.Id, CampaignPacing.NextBatchDelay(c.MinIntervalSeconds, c.MaxIntervalSeconds, Random.Shared.NextDouble()), ct);
        messagingJobs.EnqueueOutboxDispatch();
    }

    private Task<bool> IsRunningAsync(Guid id, CancellationToken ct) =>
        crm.Db.Campaigns.AsNoTracking().AnyAsync(x => x.Id == id && x.TenantId == crm.TenantId && x.Status == CampaignStatus.Running, ct);

    private async Task ScheduleNextAsync(Guid id, TimeSpan delay, CancellationToken ct)
    {
        var at = crm.Now + delay;
        await crm.Db.Campaigns.Where(x => x.Id == id && x.TenantId == crm.TenantId && x.Status == CampaignStatus.Running).ExecuteUpdateAsync(u => u.SetProperty(x => x.NextRunAtUtc, at), ct);
        jobs.EnqueueTick(crm.TenantId, id, delay);
    }

    private async Task CompleteAsync(Campaign c, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var n = await db.Campaigns.Where(x => x.Id == c.Id && x.TenantId == tenantId && x.Status == CampaignStatus.Running)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, CampaignStatus.Completed).SetProperty(x => x.CompletedAtUtc, now).SetProperty(x => x.NextRunAtUtc, (DateTime?)null).SetProperty(x => x.UpdatedAtUtc, now).SetProperty(x => x.Version, x => x.Version + 1), ct);
        if (n == 0) return;
        db.ChangeTracker.Clear();
        var counts = await core.LiveCountsAsync(c.Id, ct);
        core.Log(c.Id, ExecutionLogLevel.Info, "completed", $"The campaign completed: {counts.Where(kv => RecipientRules.IsMessaged(kv.Key)).Sum(kv => kv.Value)} message(s) sent, {counts.GetValueOrDefault(RecipientStatus.Skipped)} skipped, {counts.GetValueOrDefault(RecipientStatus.Failed)} failed.");
        core.PublishStatus(c.Id, CampaignStatus.Completed);
        crm.Record("Campaign.Completed", "Campaign", c.Id, $"Campaign '{c.Name}' completed.");
        await db.SaveChangesAsync(ct);
        await core.RecomputeRollupAsync(c.Id, ct);
        messagingJobs.EnqueueOutboxDispatch();
    }

    // ------------------------------------------------------------------

    private async Task<Outcome> ProcessRecipientAsync(Campaign c, ChannelAccount account, CampaignRecipient r, CampaignTenantSettings s, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;

        // 1. marketing permission, validated immediately before this recipient is handed to the outbound queue (the send runner validates it again right before the gateway call)
        var decision = await eligibility.CheckAsync(r.ContactId, ConsentChannel.WhatsApp, ConsentPurpose.Marketing, ct);
        var trail = EligibilityTrail.Parse(r.EligibilityJson).WithSend(new EligibilityRecord(now, decision.IsEligible, decision.Reasons.Select(ExclusionReasons.Code).ToList()));
        if (!decision.IsEligible) return await SkipAsync(r, ExclusionReasons.Primary(decision.Reasons), string.Join(" ", decision.Reasons.Select(EligibilityDecision.Explain)), trail, ct);

        var contact = await db.Contacts.AsNoTracking().Where(x => x.Id == r.ContactId && x.TenantId == tenantId).Select(x => new { x.Name, x.PhoneE164, x.LeadId }).FirstOrDefaultAsync(ct);
        if (contact is null) return await SkipAsync(r, ExclusionReasons.ContactNotFound, ExclusionReasons.Explain(ExclusionReasons.ContactNotFound), trail, ct);
        if (r.Destination is not null && contact.PhoneE164 != r.Destination) return await SkipAsync(r, ExclusionReasons.ContactChanged, ExclusionReasons.Explain(ExclusionReasons.ContactChanged), trail, ct);
        var lead = await db.Leads.AsNoTracking().Where(x => x.Id == contact.LeadId && x.TenantId == tenantId).Select(x => new { x.Name, x.City, x.Country }).FirstOrDefaultAsync(ct);

        var messaged = new[] { RecipientStatus.Queued, RecipientStatus.Sent, RecipientStatus.Delivered, RecipientStatus.Read, RecipientStatus.Replied };
        if (r.Destination is not null && await db.CampaignRecipients.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.CampaignId == c.Id && x.Id != r.Id && x.Destination == r.Destination && messaged.Contains(x.Status), ct))
            return await SkipAsync(r, ExclusionReasons.DuplicateNumber, ExclusionReasons.Explain(ExclusionReasons.DuplicateNumber), trail, ct);
        var cooldown = c.CooldownDays ?? s.CooldownDays;
        if (cooldown > 0)
        {
            var cut = now.AddDays(-cooldown);
            var (def, _) = AudienceDefinitions.Parse(c.AudienceJson); var exempt = def?.FollowUp?.ParentCampaignId;
            if (await db.CampaignRecipients.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ContactId == r.ContactId && x.CampaignId != c.Id && x.CampaignId != exempt && x.QueuedAtUtc != null && x.QueuedAtUtc >= cut && messaged.Contains(x.Status), ct))
                return await SkipAsync(r, ExclusionReasons.Cooldown, ExclusionReasons.Explain(ExclusionReasons.Cooldown), trail, ct);
        }

        var built = CampaignContent.Build(c.MessageBodySnapshot ?? "", c.IncludeOptOutFooter ? c.OptOutFooterSnapshot : null, CampaignValues.For(contact.Name, lead?.Name, contact.PhoneE164, lead?.City, lead?.Country));
        if (built.Missing.Count > 0) return await SkipAsync(r, ExclusionReasons.MissingVariable, $"Missing value for {{{{{built.Missing[0]}}}}}.", trail, ct);

        // 2. hand over to the Phase 5 outbound queue (the single send pipeline). A repeated hand-over with the same key returns the existing message.
        var res = await channel.QueueAsync(new CampaignChannelRequest(r.ContactId, account.Id, built.Text, c.TemplateId, r.IdempotencyKey, c.Id, r.Id), ct);
        if (res.IsSuccess) return await LinkAsync(r, res.Value, trail, ct);
        switch (res.Error.Code)
        {
            case "conflict":
                // the key was already used by this recipient's earlier message (the text differs only because data changed since): that message is the one to follow
                var prior = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.TenantId == tenantId && m.IdempotencyKey == r.IdempotencyKey, ct);
                if (prior is not null && prior.CampaignRecipientId == r.Id) return await LinkAsync(r, new QueuedCampaignMessage(prior.Id, prior.ConversationId, prior.Status, true), trail, ct);
                return await FailAsync(r, "idempotency_conflict", res.Error.Message, ct);
            case "opted_out": return await SkipAsync(r, ExclusionReasons.OptedOutConversation, res.Error.Message, trail, ct);
            case "suppressed": return await SkipAsync(r, ExclusionReasons.Suppressed, res.Error.Message, trail, ct);
            case "not_eligible": return await SkipAsync(r, ExclusionReasons.NoConsent, res.Error.Message, trail, ct);
            case "validation": return await SkipAsync(r, ExclusionReasons.InvalidContact, res.Error.Message, trail, ct);
            case "account_paused" or "account_not_connected":
                await core.AutoPauseAsync(c.Id, res.Error.Message, "account_unavailable", ct: ct); return Outcome.Stop;
            case "new_contacts_blocked":
                await core.AutoPauseAsync(c.Id, res.Error.Message, "new_contacts_blocked", ct: ct); return Outcome.Stop;
            case "rate_limited":
                core.Log(c.Id, ExecutionLogLevel.Info, "rate_limited", res.Error.Message); await db.SaveChangesAsync(ct); return Outcome.Deferred;
            case "not_found":
                if (res.Error.Message.Contains("account", StringComparison.OrdinalIgnoreCase)) { await core.AutoPauseAsync(c.Id, res.Error.Message, "account_unavailable", ct: ct); return Outcome.Stop; }
                return await SkipAsync(r, ExclusionReasons.ContactNotFound, res.Error.Message, trail, ct);
            default: return await FailAsync(r, res.Error.Code, res.Error.Message, ct);
        }
    }

    private async Task<Outcome> SkipAsync(CampaignRecipient r, string reason, string detail, EligibilityTrail trail, CancellationToken ct)
    {
        var now = crm.Now; var d = detail.Length > 500 ? detail[..500] : detail;
        await crm.Db.CampaignRecipients.Where(x => x.Id == r.Id && x.TenantId == crm.TenantId && x.Status == RecipientStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, RecipientStatus.Skipped).SetProperty(x => x.StatusReason, reason).SetProperty(x => x.StatusDetail, d).SetProperty(x => x.EligibilityJson, trail.ToJson())
                .SetProperty(x => x.UpdatedAtUtc, now).SetProperty(x => x.Version, x => x.Version + 1), ct);
        return Outcome.Skipped;
    }

    private async Task<Outcome> FailAsync(CampaignRecipient r, string code, string detail, CancellationToken ct)
    {
        var now = crm.Now; var d = detail.Length > 500 ? detail[..500] : detail;
        await crm.Db.CampaignRecipients.Where(x => x.Id == r.Id && x.TenantId == crm.TenantId && x.Status == RecipientStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, RecipientStatus.Failed).SetProperty(x => x.StatusReason, code).SetProperty(x => x.StatusDetail, d).SetProperty(x => x.FailedAtUtc, now)
                .SetProperty(x => x.UpdatedAtUtc, now).SetProperty(x => x.Version, x => x.Version + 1), ct);
        return Outcome.Failed;
    }

    private async Task<Outcome> LinkAsync(CampaignRecipient r, QueuedCampaignMessage m, EligibilityTrail trail, CancellationToken ct)
    {
        var now = crm.Now;
        await crm.Db.CampaignRecipients.Where(x => x.Id == r.Id && x.TenantId == crm.TenantId && x.Status == RecipientStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, RecipientStatus.Queued).SetProperty(x => x.MessageId, m.MessageId).SetProperty(x => x.ConversationId, m.ConversationId).SetProperty(x => x.QueuedAtUtc, now)
                .SetProperty(x => x.EligibilityJson, trail.ToJson()).SetProperty(x => x.UpdatedAtUtc, now).SetProperty(x => x.Version, x => x.Version + 1), ct);
        return Outcome.Queued;
    }

    // ------------------------------------------------------------------

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now; var o = options.Value; var pushed = 0;
        var s = await settings.GetAsync(false, ct);

        // follow-ups that wait for their parent: start = parent completion + delay
        var waiting = await db.Campaigns.AsNoTracking().Where(c => c.TenantId == tenantId && c.Status == CampaignStatus.Scheduled && c.ScheduledAtUtc == null && c.ParentCampaignId != null && c.FollowUpDelayDays != null).ToListAsync(ct);
        foreach (var f in waiting)
        {
            var parentDone = await db.Campaigns.AsNoTracking().Where(p => p.Id == f.ParentCampaignId && p.TenantId == tenantId && p.Status == CampaignStatus.Completed).Select(p => p.CompletedAtUtc).FirstOrDefaultAsync(ct);
            if (FollowUpRules.StartAfterParent(parentDone, f.FollowUpDelayDays) is not { } start) continue;
            var n = await db.Campaigns.Where(c => c.Id == f.Id && c.TenantId == tenantId && c.Status == CampaignStatus.Scheduled && c.ScheduledAtUtc == null)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.ScheduledAtUtc, start).SetProperty(c => c.UpdatedAtUtc, now), ct);
            if (n == 0) continue;
            var job = jobs.ScheduleStart(tenantId, f.Id, start);
            await db.Campaigns.Where(c => c.Id == f.Id && c.TenantId == tenantId).ExecuteUpdateAsync(u => u.SetProperty(c => c.ScheduleJobId, job), ct);
            core.Log(f.Id, ExecutionLogLevel.Info, "follow_up_scheduled", $"The parent campaign completed; the follow-up starts at {start:u}.");
            await db.SaveChangesAsync(ct); pushed++;
        }

        if (!s.KillSwitch)
        {
            // due schedules (also the backup for a lost Hangfire job)
            var due = await db.Campaigns.AsNoTracking().Where(c => c.TenantId == tenantId && c.Status == CampaignStatus.Scheduled && c.ScheduledAtUtc != null && c.ScheduledAtUtc <= now && (c.NextRunAtUtc == null || c.NextRunAtUtc <= now)).Select(c => c.Id).ToListAsync(ct);
            foreach (var id in due) { jobs.ScheduleStart(tenantId, id, now); pushed++; }
        }

        // running campaigns nobody is ticking (crashed worker, lost job): lease expired and the planned next run is overdue
        var grace = now.AddMinutes(-Math.Max(0, o.StaleTickMinutes));
        var stuck = await db.Campaigns.AsNoTracking().Where(c => c.TenantId == tenantId && c.Status == CampaignStatus.Running && (c.LeaseUntilUtc == null || c.LeaseUntilUtc < now) && (c.NextRunAtUtc == null || c.NextRunAtUtc < grace)).Select(c => c.Id).ToListAsync(ct);
        foreach (var id in stuck) { jobs.EnqueueTick(tenantId, id); pushed++; }
        return pushed;
    }
}

