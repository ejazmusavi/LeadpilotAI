using System.Text.Json;
using LeadPilot.Application.Common;
using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Campaigns;

/// <summary>
/// Campaign use cases. Permissions are enforced at the edge (policies named after the permission codes); here every lookup carries the tenant predicate (foreign ids are "not found")
/// and the workspace policy (approval, four-eyes, kill switch) is enforced for both MVC and REST.
/// </summary>
internal sealed class CampaignService(
    CrmContext crm, CampaignCore core, CampaignSettingsStore settings, AudienceScanner scanner, ICampaignChannel channel, ICampaignJobScheduler jobs, IOutboundMessageService outbound,
    IOptions<CampaignOptions> options, ILogger<CampaignService> logger) : ICampaignService
{
    private static readonly string[] MessagedStatuses = [nameof(RecipientStatus.Sent), nameof(RecipientStatus.Delivered), nameof(RecipientStatus.Read), nameof(RecipientStatus.Replied)];

    // ---------- reads ----------

    public async Task<PagedResult<CampaignListItemDto>> ListAsync(CampaignQuery q, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(q.Page, q.PageSize);
        var query = db.Campaigns.AsNoTracking().Where(c => c.TenantId == tenantId);
        if (q.Status is { } st) query = query.Where(c => c.Status == st);
        if (q.Type is { } ty) query = query.Where(c => c.Type == ty);
        if (!string.IsNullOrWhiteSpace(q.Search)) { var s = q.Search.Trim(); if (s.Length > 100) s = s[..100]; query = query.Where(c => c.Name.Contains(s)); }
        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(c => c.CreatedAtUtc).ThenBy(c => c.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var rollups = await db.CampaignStatsRollups.AsNoTracking().Where(r => r.TenantId == tenantId && ids.Contains(r.CampaignId)).ToDictionaryAsync(r => r.CampaignId, ct);
        var users = await crm.UserNamesAsync(rows.Select(r => r.CreatedByUserId), ct);
        var items = rows.Select(c => new CampaignListItemDto(c.Id, c.Name, c.Type, c.Status, c.ScheduledAtUtc, c.CreatedAtUtc, c.StartedAtUtc, c.CompletedAtUtc, c.CreatedByUserId is { } u ? users.GetValueOrDefault(u) : null,
            c.PausedAutomatically && c.Status == CampaignStatus.Paused, c.Status == CampaignStatus.Paused ? c.PauseReason : null, rollups.TryGetValue(c.Id, out var r) ? Counts(r) : CampaignCountsDto.Empty)).ToList();
        return new PagedResult<CampaignListItemDto>(items, page, size, total);
    }

    public async Task<Result<CampaignDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var c = await FindAsync(id, ct, tracked: false);
        return c is null ? Result.Failure<CampaignDto>(Error.NotFound("Campaign")) : Result.Success(await ToDtoAsync(c, ct));
    }

    private static CampaignCountsDto Counts(CampaignStatsRollup r) => new(r.Total, r.Pending, r.Skipped, r.Queued, r.Sent, r.Delivered, r.Read, r.Replied, r.Failed, r.Cancelled, r.Conversions);

    internal static CampaignCountsDto Counts(IReadOnlyDictionary<RecipientStatus, int> c, int conversions) =>
        new(c.Values.Sum(), c.GetValueOrDefault(RecipientStatus.Pending), c.GetValueOrDefault(RecipientStatus.Skipped), c.GetValueOrDefault(RecipientStatus.Queued), c.GetValueOrDefault(RecipientStatus.Sent), c.GetValueOrDefault(RecipientStatus.Delivered),
            c.GetValueOrDefault(RecipientStatus.Read), c.GetValueOrDefault(RecipientStatus.Replied), c.GetValueOrDefault(RecipientStatus.Failed), c.GetValueOrDefault(RecipientStatus.Cancelled), conversions);

    private async Task<Campaign?> FindAsync(Guid id, CancellationToken ct, bool tracked = true)
    {
        var tenantId = crm.TenantId; var q = tracked ? crm.Db.Campaigns : crm.Db.Campaigns.AsNoTracking();
        return await q.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct);
    }

    private async Task<CampaignDto> ToDtoAsync(Campaign c, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var s = await settings.GetAsync(false, ct);
        var counts = Counts(await core.LiveCountsAsync(c.Id, ct), await core.LiveConversionsAsync(c.Id, ct));
        var template = c.TemplateId is { } t ? await db.MessageTemplates.AsNoTracking().Where(x => x.Id == t && x.TenantId == tenantId).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var account = c.ChannelAccountId is { } a ? await db.ChannelAccounts.AsNoTracking().Where(x => x.Id == a && x.TenantId == tenantId).Select(x => x.DisplayName).FirstOrDefaultAsync(ct) : null;
        var parent = c.ParentCampaignId is { } p ? await db.Campaigns.AsNoTracking().Where(x => x.Id == p && x.TenantId == tenantId).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var users = await crm.UserNamesAsync([c.CreatedByUserId, c.SubmittedByUserId, c.ApprovedByUserId], ct);
        string? U(Guid? id) => id is { } u ? users.GetValueOrDefault(u) : null;
        var (def, _) = AudienceDefinitions.Parse(c.AudienceJson);
        var body = c.MessageBodySnapshot ?? (c.TemplateId is { } tid ? await db.MessageTemplates.AsNoTracking().Where(x => x.Id == tid && x.TenantId == tenantId).Select(x => x.Body).FirstOrDefaultAsync(ct) : null);
        return new CampaignDto(c.Id, c.Name, c.Description, c.Type, c.Channel, c.Status, c.TemplateId, template, c.ChannelAccountId, account, body, c.IncludeOptOutFooter, c.OptOutFooterSnapshot ?? (c.IncludeOptOutFooter ? s.OptOutFooter : null),
            def ?? new AudienceDefinition(), c.AudienceVersion, c.ParentCampaignId, parent, c.FollowUpDelayDays, c.ScheduledAtUtc, c.ScheduleTimeZone, c.BatchSize, c.MinIntervalSeconds, c.MaxIntervalSeconds, c.MaxRecipients, c.DailyLimit,
            c.FailureThresholdPercent, c.CooldownDays, c.AttributionWindowDays, U(c.CreatedByUserId), c.CreatedAtUtc, U(c.SubmittedByUserId), c.SubmittedAtUtc, U(c.ApprovedByUserId), c.ApprovedAtUtc, c.RejectedReason,
            c.StartedAtUtc, c.CompletedAtUtc, c.PausedAtUtc, c.PauseReason, c.PausedAutomatically, c.CancelledAtUtc, c.FailureReason, c.NextRunAtUtc, counts, ActionsFor(c, s), s.ApprovalRequired, c.Version);
    }

    internal static IReadOnlyList<string> ActionsFor(Campaign c, CampaignTenantSettings s) => c.Status switch
    {
        CampaignStatus.Draft => ["edit", "delete", "submit", "test", "dryrun", "clone", "cancel"],
        CampaignStatus.PendingApproval => ["approve", "reject", "test", "dryrun", "clone", "cancel"],
        CampaignStatus.Scheduled => ["start", "unschedule", "test", "dryrun", "clone", "cancel"],
        CampaignStatus.Running => ["pause", "test", "clone", "cancel"],
        CampaignStatus.Paused => ["resume", "test", "clone", "cancel"],
        _ => ["clone"],
    };

    // ---------- create / edit ----------

    public Task<Result<CampaignDto>> CreateAsync(SaveCampaignRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var c = Campaign.Create(crm.TenantId, r.Name, r.Type, crm.UserId, crm.Actor, crm.Now);
            if (await Apply(c, r, ct) is { } error) return Result.Failure<CampaignDto>(error);
            crm.Db.Campaigns.Add(c);
            crm.Record("Campaign.Created", "Campaign", c.Id, $"Campaign '{c.Name}' ({c.Type}) created as a draft.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await ToDtoAsync(c, ct));
        }, logger);

    public Task<Result<CampaignDto>> UpdateAsync(Guid id, SaveCampaignRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var c = await FindAsync(id, ct);
            if (c is null) return Result.Failure<CampaignDto>(Error.NotFound("Campaign"));
            if (!c.IsEditable) return Result.Failure<CampaignDto>(Error.Conflict("Only a draft campaign can be edited. Unschedule it first, or clone it."));
            if (await Apply(c, r, ct) is { } error) return Result.Failure<CampaignDto>(error);
            crm.Record("Campaign.Updated", "Campaign", c.Id, $"Draft campaign '{c.Name}' edited.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await ToDtoAsync(c, ct));
        }, logger);

    /// <summary>Validates references (same tenant), normalises the audience and applies the draft. Returns a validation error or null.</summary>
    private async Task<Error?> Apply(Campaign c, SaveCampaignRequest r, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        if (!Enum.IsDefined(r.Type)) return Error.Validation("Choose a campaign type.");
        var audience = AudienceDefinitions.Normalize(r.Audience ?? new AudienceDefinition());
        if (r.TemplateId is { } tid && !await db.MessageTemplates.AnyAsync(t => t.Id == tid && t.TenantId == tenantId, ct)) return Error.Validation("The message template was not found.");
        if (r.ChannelAccountId is { } aid && !await db.ChannelAccounts.AnyAsync(a => a.Id == aid && a.TenantId == tenantId && !a.IsDeleted, ct)) return Error.Validation("The WhatsApp account was not found.");
        var parentId = audience.FollowUp?.ParentCampaignId;
        if (parentId is { } pid)
        {
            if (pid == c.Id) return Error.Validation("A campaign cannot be its own parent.");
            if (!await db.Campaigns.AnyAsync(p => p.Id == pid && p.TenantId == tenantId, ct)) return Error.Validation("The parent campaign was not found.");
        }
        var tz = r.ScheduleTimeZone ?? await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Timezone).FirstOrDefaultAsync(ct);
        var draft = new CampaignDraft(r.Name, r.Description, r.Type, r.TemplateId, r.ChannelAccountId, AudienceDefinitions.Serialize(audience), AudienceDefinition.CurrentVersion, parentId, r.FollowUpDelayDays,
            r.ScheduledAtUtc, tz, r.IncludeOptOutFooter, r.BatchSize ?? Campaign.DefaultBatchSize, r.MinIntervalSeconds ?? Campaign.DefaultMinIntervalSeconds, r.MaxIntervalSeconds ?? Campaign.DefaultMaxIntervalSeconds,
            r.MaxRecipients ?? Campaign.DefaultMaxRecipients, r.DailyLimit, r.FailureThresholdPercent, r.CooldownDays);
        c.UpdateDraft(draft, crm.Now, crm.Actor);
        return null;
    }

    public Task<Result> DeleteAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var c = await FindAsync(id, ct);
            if (c is null) return Result.Failure(Error.NotFound("Campaign"));
            if (c.Status != CampaignStatus.Draft) return Result.Failure(Error.Conflict("Only a draft campaign can be deleted. Cancel a campaign that was scheduled or has run: its history is kept."));
            crm.Db.Campaigns.Remove(c);
            crm.Record("Campaign.Deleted", "Campaign", c.Id, $"Draft campaign '{c.Name}' deleted.");
            try { await crm.Db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { crm.Db.ChangeTracker.Clear(); return Result.Failure(Error.Conflict("This campaign cannot be deleted because other records refer to it (a follow-up, or test messages). Cancel it instead.")); }
            return Result.Success();
        }, logger);

    public Task<Result<CampaignDto>> CloneAsync(Guid id, CloneCampaignRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var src = await FindAsync(id, ct, tracked: false);
            if (src is null) return Result.Failure<CampaignDto>(Error.NotFound("Campaign"));
            var name = string.IsNullOrWhiteSpace(r.Name) ? Trim("Copy of " + src.Name, 150) : r.Name.Trim();
            var copy = Campaign.CloneOf(src, name, crm.TenantId, crm.UserId, crm.Actor, crm.Now);
            crm.Db.Campaigns.Add(copy);
            crm.Record("Campaign.Cloned", "Campaign", copy.Id, $"Campaign '{src.Name}' cloned as draft '{copy.Name}'.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await ToDtoAsync(copy, ct));
        }, logger);

    // ---------- workflow ----------

    public Task<Result<CampaignDto>> SubmitAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            var c = await FindAsync(id, ct);
            if (c is null) return Result.Failure<CampaignDto>(Error.NotFound("Campaign"));
            if (c.Status != CampaignStatus.Draft) return Result.Failure<CampaignDto>(Error.Conflict("Only a draft campaign can be submitted."));
            var s = await settings.GetAsync(false, ct);
            var checkedContent = await ValidateForSubmitAsync(c, s, ct);
            if (checkedContent.Error is { } e) return Result.Failure<CampaignDto>(e);
            c.FreezeContent(checkedContent.Body!, c.IncludeOptOutFooter ? s.OptOutFooter : null, s.AttributionWindowDays);
            c.Submit(s.ApprovalRequired, crm.UserId, now);
            crm.Record(s.ApprovalRequired ? "Campaign.SubmittedForApproval" : "Campaign.Scheduled", "Campaign", c.Id, s.ApprovalRequired ? $"Campaign '{c.Name}' submitted for approval." : $"Campaign '{c.Name}' scheduled.");
            core.PublishStatus(c);
            await db.SaveChangesAsync(ct);
            if (!s.ApprovalRequired) await ScheduleJobAsync(c, ct);
            return Result.Success(await ToDtoAsync(c, ct));
        }, logger);

    private sealed record Checked(string? Body, Error? Error);

    private async Task<Checked> ValidateForSubmitAsync(Campaign c, CampaignTenantSettings s, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var problems = new List<string>();
        var template = c.TemplateId is { } tid ? await db.MessageTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tid && t.TenantId == tenantId, ct) : null;
        if (template is null) problems.Add("Choose a message template.");
        else
        {
            if (!template.IsActive) problems.Add("The message template is switched off.");
            if (template.Category != TemplateCategory.Marketing) problems.Add("Campaigns need a template of the Marketing category.");
        }
        var account = c.ChannelAccountId is { } aid ? await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == aid && a.TenantId == tenantId, ct) : null;
        if (account is null || account.IsDeleted) problems.Add("Choose a WhatsApp account to send from.");
        var (def, parseError) = AudienceDefinitions.Parse(c.AudienceJson);
        if (def is null) problems.Add(parseError ?? "The audience is not valid.");
        else problems.AddRange(AudienceDefinitions.Validate(def, c.Type));

        var keywords = OptOutDetector.ParseList(await db.TenantSettings.AsNoTracking().Where(x => x.TenantId == tenantId && x.Key == OptOutDetector.SettingKey && !x.IsSecret).Select(x => x.Value).FirstOrDefaultAsync(ct));
        if (template is not null)
        {
            var footer = c.IncludeOptOutFooter ? s.OptOutFooter : null;
            problems.AddRange(CampaignContent.Validate(template.Body, footer));
            if (!c.IncludeOptOutFooter && !CampaignContent.FooterContainsOptOutKeyword(template.Body, keywords))
                problems.Add("Every marketing message must tell people how to opt out. Keep the opt-out text on, or write it into the message (for example 'Reply STOP to opt out').");
            if (footer is not null && !CampaignContent.FooterContainsOptOutKeyword(footer, keywords)) problems.Add("The workspace opt-out text does not contain a word that the opt-out detector understands (for example STOP). Fix it in the campaign settings.");
        }
        switch (c.Type)
        {
            case CampaignType.Scheduled:
                if (c.ScheduledAtUtc is not { } at) problems.Add("Choose when the campaign starts.");
                else if (at < now.AddMinutes(1)) problems.Add("The start time must be in the future.");
                break;
            case CampaignType.FollowUp:
                if (c.ParentCampaignId is null) problems.Add("Choose the parent campaign.");
                // no start time and no delay = started by hand once the parent has finished
                if (c.ScheduledAtUtc is { } fa && fa < now.AddMinutes(1)) problems.Add("The start time must be in the future.");
                break;
            default:
                if (c.ScheduledAtUtc is { } da && da < now.AddMinutes(1)) problems.Add("The start time must be in the future (or leave it empty and start the campaign by hand).");
                break;
        }
        return problems.Count > 0 ? new Checked(null, Error.Validation(string.Join(" ", problems))) : new Checked(template!.Body, null);
    }

    /// <summary>Hangfire start job for a scheduled campaign (saved first, scheduled second: a job can never run before the status is committed).</summary>
    private async Task ScheduleJobAsync(Campaign c, CancellationToken ct)
    {
        if (c.Status != CampaignStatus.Scheduled || c.ScheduledAtUtc is not { } at) return;
        jobs.Delete(c.ScheduleJobId);
        string? job = null;
        try { job = jobs.ScheduleStart(crm.TenantId, c.Id, at); }
        catch (Exception ex) { logger.LogWarning(ex, "Start job of campaign {CampaignId} could not be scheduled; the sweep will start it when due", c.Id); }
        c.SetScheduleJob(job, crm.Now);
        await crm.Db.SaveChangesAsync(ct);
    }

    public Task<Result<CampaignDto>> ApproveAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var c = await FindAsync(id, ct);
            if (c is null) return Result.Failure<CampaignDto>(Error.NotFound("Campaign"));
            var s = await settings.GetAsync(false, ct);
            c.Approve(crm.UserId, s.CreatorCannotApprove, crm.Now);
            crm.Record("Campaign.Approved", "Campaign", c.Id, $"Campaign '{c.Name}' approved.");
            core.PublishStatus(c);
            await crm.Db.SaveChangesAsync(ct);
            await ScheduleJobAsync(c, ct);
            return Result.Success(await ToDtoAsync(c, ct));
        }, logger);

    public Task<Result<CampaignDto>> RejectAsync(Guid id, RejectCampaignRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var c = await FindAsync(id, ct);
            if (c is null) return Result.Failure<CampaignDto>(Error.NotFound("Campaign"));
            c.Reject(r.Reason ?? "", crm.Now);
            crm.Record("Campaign.Rejected", "Campaign", c.Id, $"Campaign '{c.Name}' returned to draft: {c.RejectedReason}");
            core.PublishStatus(c, c.RejectedReason);
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await ToDtoAsync(c, ct));
        }, logger);

    public Task<Result<CampaignDto>> UnscheduleAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var c = await FindAsync(id, ct);
            if (c is null) return Result.Failure<CampaignDto>(Error.NotFound("Campaign"));
            var job = c.ScheduleJobId;
            c.Unschedule(crm.Now);
            crm.Record("Campaign.Unscheduled", "Campaign", c.Id, $"Campaign '{c.Name}' unscheduled and returned to draft.");
            core.PublishStatus(c);
            await crm.Db.SaveChangesAsync(ct);
            jobs.Delete(job);
            return Result.Success(await ToDtoAsync(c, ct));
        }, logger);

    public Task<Result<CampaignDto>> StartNowAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var c = await FindAsync(id, ct);
            if (c is null) return Result.Failure<CampaignDto>(Error.NotFound("Campaign"));
            if (c.Status != CampaignStatus.Scheduled) return Result.Failure<CampaignDto>(Error.Conflict("Only a scheduled campaign can be started."));
            if ((await settings.GetAsync(false, ct)).KillSwitch) return Result.Failure<CampaignDto>(Error.Conflict("The workspace kill switch is on: no campaign can start."));
            jobs.Delete(c.ScheduleJobId);
            c.SetSchedule(crm.Now, crm.Now);
            crm.Record("Campaign.StartRequested", "Campaign", c.Id, $"Campaign '{c.Name}' started by hand.");
            await crm.Db.SaveChangesAsync(ct);
            var job = jobs.ScheduleStart(crm.TenantId, c.Id, crm.Now);
            c.SetScheduleJob(job, crm.Now);
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await ToDtoAsync(c, ct));
        }, logger);

    public Task<Result<CampaignDto>> PauseAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var c = await FindAsync(id, ct);
            if (c is null) return Result.Failure<CampaignDto>(Error.NotFound("Campaign"));
            if (c.Status != CampaignStatus.Running) return Result.Failure<CampaignDto>(Error.Conflict("Only a running campaign can be paused."));
            c.Pause("Paused by " + (crm.Actor ?? "a user"), automatic: false, crm.Now);
            core.Log(c.Id, ExecutionLogLevel.Info, "paused", "Paused by " + (crm.Actor ?? "a user") + ".", crm.UserId);
            crm.Record("Campaign.Paused", "Campaign", c.Id, $"Campaign '{c.Name}' paused.");
            core.PublishStatus(c);
            await crm.Db.SaveChangesAsync(ct);
            var withdrawn = await core.WithdrawQueuedAsync(c.Id, "campaign_paused", "The campaign was paused before this message was sent.", ct);
            if (withdrawn > 0) { core.Log(c.Id, ExecutionLogLevel.Info, "queue_withdrawn", $"{withdrawn} queued message(s) were withdrawn; their recipients wait for the campaign to resume."); await crm.Db.SaveChangesAsync(ct); }
            await core.RecomputeRollupAsync(c.Id, ct);
            return Result.Success(await ToDtoAsync(c, ct));
        }, logger);

    public Task<Result<CampaignDto>> ResumeAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var c = await FindAsync(id, ct);
            if (c is null) return Result.Failure<CampaignDto>(Error.NotFound("Campaign"));
            if (c.Status != CampaignStatus.Paused) return Result.Failure<CampaignDto>(Error.Conflict("Only a paused campaign can be resumed."));
            if ((await settings.GetAsync(false, ct)).KillSwitch) return Result.Failure<CampaignDto>(Error.Conflict("The workspace kill switch is on: no campaign can resume."));
            c.Resume(crm.Now);
            core.Log(c.Id, ExecutionLogLevel.Info, "resumed", "Resumed by " + (crm.Actor ?? "a user") + ".", crm.UserId);
            crm.Record("Campaign.Resumed", "Campaign", c.Id, $"Campaign '{c.Name}' resumed.");
            core.PublishStatus(c);
            await crm.Db.SaveChangesAsync(ct);
            jobs.EnqueueTick(crm.TenantId, c.Id);
            return Result.Success(await ToDtoAsync(c, ct));
        }, logger);

    public Task<Result<CampaignDto>> CancelAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            var c = await FindAsync(id, ct);
            if (c is null) return Result.Failure<CampaignDto>(Error.NotFound("Campaign"));
            var job = c.ScheduleJobId;
            c.Cancel(crm.UserId, now);
            if (c.StartedAtUtc is not null) core.Log(c.Id, ExecutionLogLevel.Info, "cancelled", "Cancelled by " + (crm.Actor ?? "a user") + ".", crm.UserId);
            crm.Record("Campaign.Cancelled", "Campaign", c.Id, $"Campaign '{c.Name}' cancelled.");
            core.PublishStatus(c);
            await db.SaveChangesAsync(ct);
            jobs.Delete(job);
            // recipients that have not left the system are cancelled; queued messages are withdrawn (one the gateway is already sending finishes normally)
            await db.CampaignRecipients.Where(r => r.TenantId == tenantId && r.CampaignId == c.Id && r.Status == RecipientStatus.Pending)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RecipientStatus.Cancelled).SetProperty(r => r.StatusReason, ExclusionCodes.CampaignStopped).SetProperty(r => r.UpdatedAtUtc, now).SetProperty(r => r.Version, r => r.Version + 1), ct);
            var withdrawn = await core.WithdrawQueuedAsync(c.Id, "campaign_cancelled", "The campaign was cancelled before this message was sent.", ct);
            if (c.StartedAtUtc is not null) { core.Log(c.Id, ExecutionLogLevel.Info, "queue_withdrawn", $"{withdrawn} queued message(s) were withdrawn."); await db.SaveChangesAsync(ct); await core.RecomputeRollupAsync(c.Id, ct); }
            return Result.Success(await ToDtoAsync(c, ct));
        }, logger);

    // ---------- test send / dry run ----------

    public Task<Result<MessageDto>> TestSendAsync(Guid id, TestSendRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var c = await FindAsync(id, ct, tracked: false);
            if (c is null) return Result.Failure<MessageDto>(Error.NotFound("Campaign"));
            if (CampaignStateMachine.IsTerminal(c.Status)) return Result.Failure<MessageDto>(Error.Conflict("A finished campaign cannot send tests."));
            if (c.ChannelAccountId is not { } accountId) return Result.Failure<MessageDto>(Error.Validation("Choose a WhatsApp account first."));
            var s = await settings.GetAsync(false, ct);
            var phone = Normalizer.PhoneE164(r.PhoneE164, await crm.DefaultCountryCodeAsync(ct));
            if (!TestSendRules.IsAllowed(phone, CampaignSettingsStore.TestNumbers(s)))
                return Result.Failure<MessageDto>(Error.Validation("Test messages can only go to the internal numbers listed in the campaign settings (Test numbers)."));
            var body = c.MessageBodySnapshot ?? (c.TemplateId is { } tid ? await db.MessageTemplates.AsNoTracking().Where(t => t.Id == tid && t.TenantId == tenantId).Select(t => t.Body).FirstOrDefaultAsync(ct) : null);
            if (string.IsNullOrWhiteSpace(body)) return Result.Failure<MessageDto>(Error.Validation("Choose a message template first."));
            var built = CampaignContent.Build(body, c.IncludeOptOutFooter ? (c.OptOutFooterSnapshot ?? s.OptOutFooter) : null, CampaignValues.For("Test Contact", "Test Business", phone, "Test City", "Test Country"));
            if (built.Missing.Count > 0) return Result.Failure<MessageDto>(Error.Validation("The message uses a placeholder that cannot be filled: " + string.Join(", ", built.Missing)));
            var res = await channel.QueueTestAsync(new CampaignChannelTestRequest(phone!, accountId, TestSendRules.Decorate(built.Text), CampaignKeys.ForTest(c.Id, phone!, crm.Now), c.Id), ct);
            if (res.IsFailure) return Result.Failure<MessageDto>(res.Error);
            crm.Record("Campaign.TestSent", "Campaign", c.Id, $"Test message of campaign '{c.Name}' queued to {CrmContext.Mask(phone!)}.");
            await db.SaveChangesAsync(ct);
            return await outbound.GetMessageAsync(res.Value.MessageId, ct);
        }, logger);

    public Task<Result<DryRunDto>> DryRunAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var c = await FindAsync(id, ct, tracked: false);
            if (c is null) return Result.Failure<DryRunDto>(Error.NotFound("Campaign"));
            var s = await settings.GetAsync(false, ct);
            var problems = new List<string>();
            var template = c.TemplateId is { } tid ? await db.MessageTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tid && t.TenantId == tenantId, ct) : null;
            var body = c.MessageBodySnapshot ?? template?.Body;
            if (body is null) problems.Add("Choose a message template.");
            else problems.AddRange(CampaignContent.Validate(body, c.IncludeOptOutFooter ? (c.OptOutFooterSnapshot ?? s.OptOutFooter) : null));
            var (def, err) = AudienceDefinitions.Parse(c.AudienceJson);
            if (def is null) return Result.Failure<DryRunDto>(Error.Validation(err ?? "The audience is not valid."));
            var preview = await PreviewCoreAsync(def, c.Type, body, c.IncludeOptOutFooter ? (c.OptOutFooterSnapshot ?? s.OptOutFooter) : null, c.MaxRecipients, c.CooldownDays ?? s.CooldownDays, c.Id, ct);
            var account = c.ChannelAccountId is { } aid ? await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == aid && a.TenantId == tenantId, ct) : null;
            string? note = null; var ready = false;
            if (account is null || account.IsDeleted) note = "No WhatsApp account is selected."; else if (account.IsPaused) note = "The account is paused."; else if (account.ConnectionStatus != ConnectionStatus.Working) note = "The account is not connected."; else ready = true;
            var perHour = account is null ? 1 : Math.Max(1, Math.Min(account.HourlyCap, 3600 / Math.Max(1, account.MinDelaySeconds)));
            var minutes = (int)Math.Ceiling(preview.Eligible * 60.0 / perHour);
            return Result.Success(new DryRunDto(preview, problems.Count == 0, problems, ready, note, minutes));
        }, logger);

    // ---------- audience preview (shared with ICampaignAudienceService) ----------

    internal async Task<AudiencePreviewDto> PreviewCoreAsync(AudienceDefinition def, CampaignType type, string? body, string? footer, int maxRecipients, int cooldownDays, Guid? campaignId, CancellationToken ct)
    {
        var o = options.Value;
        var parent = def.FollowUp?.ParentCampaignId;
        var session = new ScanSession();
        var opts = new ScanOptions(null, parent, cooldownDays, maxRecipients, 0, c => AudienceScanner.MissingVariableFor(body, footer, c));
        int eligible = 0, excluded = 0, candidates = 0; var breakdown = new Dictionary<string, int>(); var samples = new List<PreviewSampleDto>();
        await foreach (var item in scanner.ScanAsync(def, opts with { CampaignId = campaignId is null ? null : null }, session, ct))
        {
            candidates++;
            if (item.Eligible)
            {
                eligible++;
                if (samples.Count < o.SampleSize)
                {
                    var c = item.Candidate; string? rendered = null; string? problem = null;
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        var built = CampaignContent.Build(body, footer, CampaignValues.For(c.ContactName, c.LeadName, c.Phone, c.City, c.Country));
                        if (built.Missing.Count > 0) problem = "Missing: " + string.Join(", ", built.Missing); else rendered = built.Text;
                    }
                    samples.Add(new PreviewSampleDto(c.ContactId, c.LeadName, c.ContactName, c.Phone is null ? "" : CrmContext.Mask(c.Phone), rendered, problem));
                }
            }
            else { excluded++; breakdown[item.Reason!] = breakdown.GetValueOrDefault(item.Reason!) + 1; }
        }
        var warnings = new List<string>();
        if (session.Truncated) warnings.Add($"The audience matches {session.TotalMatching} contacts; only the first {o.ScanLimit} were examined.");
        if (def.FollowUp is null && !def.HasAnyFilter) warnings.Add("No filter is set: every contact of the workspace would be examined.");
        return new AudiencePreviewDto(candidates, eligible, excluded, breakdown.OrderByDescending(kv => kv.Value).Select(kv => new ExclusionCountDto(kv.Key, ExclusionReasons.Explain(kv.Key), kv.Value)).ToList(),
            session.Truncated, o.ScanLimit, samples, crm.Now, warnings);
    }

    // ---------- tracking reads ----------

    public async Task<Result<PagedResult<RecipientDto>>> RecipientsAsync(Guid id, RecipientQuery q, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(q.Page, q.PageSize);
        if (!await db.Campaigns.AnyAsync(c => c.Id == id && c.TenantId == tenantId, ct)) return Result.Failure<PagedResult<RecipientDto>>(Error.NotFound("Campaign"));
        var query = db.CampaignRecipients.AsNoTracking().Where(r => r.TenantId == tenantId && r.CampaignId == id);
        if (q.Status is { } st) query = query.Where(r => r.Status == st);
        if (!string.IsNullOrWhiteSpace(q.Reason)) { var reason = q.Reason.Trim(); query = query.Where(r => r.StatusReason == reason); }
        var total = await query.LongCountAsync(ct);
        var rows = await (from r in query.OrderBy(r => r.Sequence).Skip((page - 1) * size).Take(size)
                          join l in db.Leads.AsNoTracking() on r.LeadId equals l.Id into ls from l in ls.DefaultIfEmpty()
                          join c in db.Contacts.AsNoTracking() on r.ContactId equals c.Id into cs from c in cs.DefaultIfEmpty()
                          select new { r, LeadName = l.Name, ContactName = c.Name }).ToListAsync(ct);
        var items = rows.Select(x => new RecipientDto(x.r.Id, x.r.ContactId, x.r.LeadId, x.LeadName, x.ContactName, x.r.Destination is null ? null : CrmContext.Mask(x.r.Destination), x.r.Status, x.r.StatusReason,
            x.r.StatusReason is null ? null : ExclusionReasons.Explain(x.r.StatusReason), x.r.StatusDetail, x.r.MessageId, x.r.ConversationId, x.r.QueuedAtUtc, x.r.SentAtUtc, x.r.DeliveredAtUtc, x.r.ReadAtUtc, x.r.RepliedAtUtc, x.r.FailedAtUtc, x.r.EligibilityJson)).ToList();
        return Result.Success(new PagedResult<RecipientDto>(items, page, size, total));
    }

    public async Task<Result<PagedResult<ExecutionLogDto>>> LogAsync(Guid id, int page, int pageSize, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var (p, size) = CrmFlow.Page(page, pageSize);
        if (!await db.Campaigns.AnyAsync(c => c.Id == id && c.TenantId == tenantId, ct)) return Result.Failure<PagedResult<ExecutionLogDto>>(Error.NotFound("Campaign"));
        var q = db.CampaignExecutionLogs.AsNoTracking().Where(l => l.TenantId == tenantId && l.CampaignId == id);
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(l => l.OccurredAtUtc).ThenByDescending(l => l.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var users = await crm.UserNamesAsync(rows.Select(r => r.ActorUserId), ct);
        return Result.Success(new PagedResult<ExecutionLogDto>(rows.Select(r => new ExecutionLogDto(r.Id, r.Level, r.Code, r.Message, r.ActorUserId is { } u ? users.GetValueOrDefault(u) : null, r.OccurredAtUtc)).ToList(), p, size, total));
    }

    public async Task<Result<PagedResult<ConversionDto>>> ConversionsAsync(Guid id, int page, int pageSize, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var (p, size) = CrmFlow.Page(page, pageSize);
        if (!await db.Campaigns.AnyAsync(c => c.Id == id && c.TenantId == tenantId, ct)) return Result.Failure<PagedResult<ConversionDto>>(Error.NotFound("Campaign"));
        var q = db.CampaignConversions.AsNoTracking().Where(c => c.TenantId == tenantId && c.CampaignId == id);
        var total = await q.LongCountAsync(ct);
        var rows = await (from c in q.OrderByDescending(c => c.OccurredAtUtc).ThenBy(c => c.Id).Skip((p - 1) * size).Take(size)
                          join l in db.Leads.AsNoTracking() on c.LeadId equals l.Id into ls from l in ls.DefaultIfEmpty()
                          select new { c, LeadName = l.Name }).ToListAsync(ct);
        return Result.Success(new PagedResult<ConversionDto>(rows.Select(x => new ConversionDto(x.c.Id, x.c.RecipientId, x.c.LeadId, x.LeadName, x.c.Kind, x.c.Detail, x.c.SentAtUtc, x.c.OccurredAtUtc)).ToList(), p, size, total));
    }

    public async Task<Result<CampaignStatsDto>> StatsAsync(Guid id, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var c = await FindAsync(id, ct, tracked: false);
        if (c is null) return Result.Failure<CampaignStatsDto>(Error.NotFound("Campaign"));
        var live = await core.LiveCountsAsync(id, ct); var conversions = await core.LiveConversionsAsync(id, ct);
        var counts = Counts(live, conversions);
        var rollup = await db.CampaignStatsRollups.AsNoTracking().FirstOrDefaultAsync(r => r.TenantId == tenantId && r.CampaignId == id, ct);
        var matches = rollup is not null && Counts(rollup) == counts;
        var reasons = await db.CampaignRecipients.AsNoTracking().Where(r => r.TenantId == tenantId && r.CampaignId == id && (r.Status == RecipientStatus.Skipped || r.Status == RecipientStatus.Failed))
            .GroupBy(r => new { r.Status, r.StatusReason }).Select(g => new { g.Key.Status, g.Key.StatusReason, N = g.Count() }).ToListAsync(ct);
        ReasonCountDto R(string? code, int n) => new(code ?? "unknown", ExclusionReasons.Explain(code ?? "unknown"), n);
        var kinds = await db.CampaignConversions.AsNoTracking().Where(x => x.TenantId == tenantId && x.CampaignId == id).GroupBy(x => x.Kind).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var messaged = counts.Messaged; var delivered = counts.Delivered + counts.Read + counts.Replied; var read = counts.Read + counts.Replied;
        static double Pct(int a, int b) => b == 0 ? 0 : Math.Round(a * 100.0 / b, 1);
        return Result.Success(new CampaignStatsDto(counts, messaged, Pct(delivered, messaged), Pct(read, messaged), Pct(counts.Replied, messaged), FailureRatePolicy.Percent(counts.Failed, counts.Failed + messaged),
            reasons.Where(x => x.Status == RecipientStatus.Skipped).OrderByDescending(x => x.N).Select(x => R(x.StatusReason, x.N)).ToList(),
            reasons.Where(x => x.Status == RecipientStatus.Failed).OrderByDescending(x => x.N).Select(x => R(x.StatusReason, x.N)).ToList(),
            kinds.FirstOrDefault(k => k.Key == ConversionKind.Reply)?.N ?? 0, kinds.FirstOrDefault(k => k.Key == ConversionKind.StageChange)?.N ?? 0, rollup?.UpdatedAtUtc, matches, c.AttributionWindowDays));
    }

    private static string Trim(string s, int max) => s.Length > max ? s[..max] : s;
}

internal sealed class CampaignAudienceService(CrmContext crm, CampaignService campaigns, CampaignSettingsStore settings, ILogger<CampaignAudienceService> logger) : ICampaignAudienceService
{
    public Task<Result<AudiencePreviewDto>> PreviewAsync(AudiencePreviewRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var def = AudienceDefinitions.Normalize(r.Audience ?? new AudienceDefinition());
            var problems = AudienceDefinitions.Validate(def, r.Type);
            if (problems.Count > 0) return Result.Failure<AudiencePreviewDto>(Error.Validation(string.Join(" ", problems)));
            if (def.FollowUp is { } f && !await db.Campaigns.AnyAsync(c => c.Id == f.ParentCampaignId && c.TenantId == tenantId, ct)) return Result.Failure<AudiencePreviewDto>(Error.NotFound("Parent campaign"));
            var s = await settings.GetAsync(false, ct);
            string? body = null;
            if (r.TemplateId is { } tid)
            {
                body = await db.MessageTemplates.AsNoTracking().Where(t => t.Id == tid && t.TenantId == tenantId).Select(t => t.Body).FirstOrDefaultAsync(ct);
                if (body is null) return Result.Failure<AudiencePreviewDto>(Error.NotFound("Message template"));
            }
            var preview = await campaigns.PreviewCoreAsync(def, r.Type, body, r.IncludeOptOutFooter ? s.OptOutFooter : null, Math.Clamp(r.MaxRecipients ?? Campaign.DefaultMaxRecipients, 1, Campaign.HardMaxRecipients), r.CooldownDays ?? s.CooldownDays, r.CampaignId, ct);
            return Result.Success(preview);
        }, logger);
}

internal sealed class CampaignSettingsService(CrmContext crm, CampaignSettingsStore store, CampaignCore core, ILogger<CampaignSettingsService> logger) : ICampaignSettingsService
{
    public async Task<CampaignSettingsDto> GetAsync(CancellationToken ct = default) => await ToDtoAsync(await store.GetAsync(false, ct), ct);

    private async Task<CampaignSettingsDto> ToDtoAsync(CampaignTenantSettings s, CancellationToken ct)
    {
        var tz = await crm.Db.Tenants.AsNoTracking().Where(t => t.Id == crm.TenantId).Select(t => t.Timezone).FirstOrDefaultAsync(ct) ?? "UTC";
        return new CampaignSettingsDto(s.CooldownDays, s.ApprovalRequired, s.CreatorCannotApprove, s.AttributionWindowDays, s.FailureThresholdPercent, s.FailureMinSample, s.OptOutFooter, s.MonthlyMessageLimit, core.EffectiveMonthlyLimit(s),
            CampaignSettingsStore.TestNumbers(s), s.KillSwitch, s.KillSwitchAtUtc, tz, await core.UsedThisMonthAsync(ct));
    }

    public Task<Result<CampaignSettingsDto>> UpdateAsync(UpdateCampaignSettingsRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var keywords = OptOutDetector.ParseList(await db.TenantSettings.AsNoTracking().Where(x => x.TenantId == tenantId && x.Key == OptOutDetector.SettingKey && !x.IsSecret).Select(x => x.Value).FirstOrDefaultAsync(ct));
            var footer = TemplateRenderer.Sanitize(r.OptOutFooter, keepNewlines: false, maxLength: 200);
            if (footer.Length < 5) return Result.Failure<CampaignSettingsDto>(Error.Validation("The opt-out text is required (at least 5 characters)."));
            if (!CampaignContent.FooterContainsOptOutKeyword(footer, keywords))
                return Result.Failure<CampaignSettingsDto>(Error.Validation("The opt-out text must contain a word that the opt-out detector understands, for example STOP (the workspace's opt-out keywords: " + string.Join(", ", keywords.Take(8)) + ")."));
            var country = await crm.DefaultCountryCodeAsync(ct);
            var numbers = new List<string>();
            foreach (var raw in r.TestNumbers ?? [])
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var n = Normalizer.PhoneE164(raw, country);
                if (n is null) return Result.Failure<CampaignSettingsDto>(Error.Validation($"'{raw}' is not a valid phone number in international format (+...)."));
                if (!numbers.Contains(n)) numbers.Add(n);
            }
            if (numbers.Count > CampaignTenantSettings.MaxTestNumbers) return Result.Failure<CampaignSettingsDto>(Error.Validation($"At most {CampaignTenantSettings.MaxTestNumbers} test numbers can be listed."));
            var row = await store.EnsureAsync(ct);
            row.Update(r.CooldownDays, r.ApprovalRequired, r.CreatorCannotApprove, r.AttributionWindowDays, r.FailureThresholdPercent, r.FailureMinSample, footer, r.MonthlyMessageLimit, JsonSerializer.Serialize(numbers), crm.Actor, crm.Now);
            crm.Record("Campaign.SettingsChanged", "CampaignTenantSettings", row.Id, $"Campaign settings changed (cool-down {row.CooldownDays} d, approval {(row.ApprovalRequired ? "required" : "not required")}, attribution {row.AttributionWindowDays} d, failure threshold {row.FailureThresholdPercent}%, {numbers.Count} test number(s)).");
            await db.SaveChangesAsync(ct);
            return Result.Success(await ToDtoAsync(row, ct));
        }, logger);

    public Task<Result<CampaignSettingsDto>> SetKillSwitchAsync(KillSwitchRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var row = await store.EnsureAsync(ct);
            row.SetKillSwitch(r.Enabled, crm.UserId, crm.Actor, crm.Now);
            var reason = string.IsNullOrWhiteSpace(r.Reason) ? "" : " Reason: " + r.Reason.Trim();
            crm.Record(r.Enabled ? "Campaign.KillSwitchOn" : "Campaign.KillSwitchOff", "CampaignTenantSettings", row.Id, (r.Enabled ? "Campaign kill switch turned ON: all running campaigns are paused and nothing is sent." : "Campaign kill switch turned off. Paused campaigns stay paused until someone resumes them.") + reason);
            await db.SaveChangesAsync(ct);
            if (r.Enabled)
            {
                var running = await db.Campaigns.AsNoTracking().Where(c => c.TenantId == tenantId && c.Status == CampaignStatus.Running).Select(c => c.Id).ToListAsync(ct);
                foreach (var id in running) await core.AutoPauseAsync(id, "The workspace kill switch was turned on by " + (crm.Actor ?? "a user") + "." + reason, "kill_switch", "campaign_kill_switch", ct);
                // anything still waiting in the Phase 5 queue for a campaign is withdrawn too
                var withMessages = await db.Messages.AsNoTracking().Where(m => m.TenantId == tenantId && m.CampaignRecipientId != null && m.Status == MessageStatus.Queued).Select(m => m.CampaignId!.Value).Distinct().ToListAsync(ct);
                foreach (var id in withMessages) await core.WithdrawQueuedAsync(id, "campaign_kill_switch", "The workspace kill switch is on.", ct);
            }
            return Result.Success(await ToDtoAsync(row, ct));
        }, logger);
}
