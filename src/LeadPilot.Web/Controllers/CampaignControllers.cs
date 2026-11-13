using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Messaging;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Web.Controllers;

/// <summary>Campaign pages. Every mutating action is POST-only (antiforgery) and sits behind its own permission; the same use cases back the REST API.</summary>
[Authorize(Policy = PermissionCodes.CampaignsView)]
public class CampaignsController(
    ICampaignService campaigns, ICampaignAudienceService audiences, ICampaignSettingsService settings, IMessageTemplateService templates, IChannelAccountService accounts, ILeadCatalogService catalog,
    IPipelineService pipelines, ITagService tags, IUserManagementService users, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CampaignQuery query, CancellationToken ct)
    {
        Crumbs(("Campaigns", null));
        var qv = new Dictionary<string, string?> { ["status"] = query.Status?.ToString(), ["type"] = query.Type?.ToString(), ["search"] = query.Search };
        return View(new CampaignListViewModel
        {
            Result = await campaigns.ListAsync(query, ct), Query = query, QueryValues = qv, CanCreate = await permissions.HasAsync(PermissionCodes.CampaignsCreate, ct), Settings = await settings.GetAsync(ct),
        });
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, string tab = "recipients", RecipientStatus? status = null, string? reason = null, int page = 1, CancellationToken ct = default)
    {
        var c = await campaigns.GetAsync(id, ct);
        if (Reject(c) is { } rej) return rej;
        var s = await settings.GetAsync(ct);
        var vm = new CampaignDetailsViewModel
        {
            Campaign = c.Value, Tab = tab, Settings = s, TimeZone = s.TenantTimeZone, LocalSchedule = ToLocal(c.Value.ScheduledAtUtc, s.TenantTimeZone),
            CanUpdate = await permissions.HasAsync(PermissionCodes.CampaignsUpdate, ct), CanExecute = await permissions.HasAsync(PermissionCodes.CampaignsExecute, ct), CanApprove = await permissions.HasAsync(PermissionCodes.CampaignsApprove, ct),
            CanCancel = await permissions.HasAsync(PermissionCodes.CampaignsCancel, ct), CanDelete = await permissions.HasAsync(PermissionCodes.CampaignsDelete, ct), CanCreate = await permissions.HasAsync(PermissionCodes.CampaignsCreate, ct),
            RecipientQuery = new RecipientQuery { Status = status, Reason = reason, Page = page }, QueryValues = new Dictionary<string, string?> { ["id"] = id.ToString(), ["tab"] = tab, ["status"] = status?.ToString(), ["reason"] = reason },
        };
        if (tab == "log") vm.Log = (await campaigns.LogAsync(id, page, 25, ct)).Value;
        else if (tab == "conversions") vm.Conversions = (await campaigns.ConversionsAsync(id, page, 25, ct)).Value;
        else vm.Recipients = (await campaigns.RecipientsAsync(id, vm.RecipientQuery, ct)).Value;
        Crumbs(("Campaigns", Url.Action(nameof(Index))), (c.Value.Name, null));
        return View(vm);
    }

    [HttpGet]
    public async Task<IActionResult> Stats(Guid id, CancellationToken ct)
    {
        var c = await campaigns.GetAsync(id, ct);
        if (Reject(c) is { } rej) return rej;
        var st = await campaigns.StatsAsync(id, ct);
        if (Reject(st) is { } rej2) return rej2;
        Crumbs(("Campaigns", Url.Action(nameof(Index))), (c.Value.Name, Url.Action(nameof(Details), new { id })), ("Statistics", null));
        return View(new CampaignStatsViewModel { Campaign = c.Value, Stats = st.Value });
    }

    // ---------- wizard ----------

    [HttpGet, Authorize(Policy = PermissionCodes.CampaignsCreate)]
    public async Task<IActionResult> New(Guid? cloneOf, CancellationToken ct)
    {
        Crumbs(("Campaigns", Url.Action(nameof(Index))), ("New campaign", null));
        return View("Form", await FormAsync(new CampaignFormModel(), ct));
    }

    [HttpGet, Authorize(Policy = PermissionCodes.CampaignsUpdate)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var c = await campaigns.GetAsync(id, ct);
        if (Reject(c) is { } rej) return rej;
        if (c.Value.Status != CampaignStatus.Draft) { Failure("Only a draft campaign can be edited. Unschedule it first, or clone it."); return RedirectToAction(nameof(Details), new { id }); }
        var s = await settings.GetAsync(ct);
        Crumbs(("Campaigns", Url.Action(nameof(Index))), (c.Value.Name, Url.Action(nameof(Details), new { id })), ("Edit", null));
        return View("Form", await FormAsync(CampaignFormModel.From(c.Value, ToLocal(c.Value.ScheduledAtUtc, s.TenantTimeZone)), ct));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.CampaignsCreate)]
    public async Task<IActionResult> Create([Bind(Prefix = "Form")] CampaignFormModel m, CancellationToken ct) => await SaveAsync(m, null, ct);

    [HttpPost, Authorize(Policy = PermissionCodes.CampaignsUpdate)]
    public async Task<IActionResult> Update(Guid id, [Bind(Prefix = "Form")] CampaignFormModel m, CancellationToken ct) => await SaveAsync(m, id, ct);

    private async Task<IActionResult> SaveAsync(CampaignFormModel m, Guid? id, CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        Crumbs(("Campaigns", Url.Action(nameof(Index))), (id is null ? "New campaign" : "Edit", null));
        if (!ModelState.IsValid) return View("Form", await FormAsync(m, ct));
        var scheduled = ToUtc(m.ScheduledLocal, s.TenantTimeZone, out var parseError);
        if (parseError) { ModelState.AddModelError(nameof(m.ScheduledLocal), "Enter a valid date and time."); return View("Form", await FormAsync(m, ct)); }
        var req = new SaveCampaignRequest(m.Name, m.Description, m.Type, m.TemplateId, m.ChannelAccountId, m.ToAudience(), m.FollowUpDelayDays, scheduled, s.TenantTimeZone, m.IncludeOptOutFooter,
            m.BatchSize, m.MinIntervalSeconds, m.MaxIntervalSeconds, m.MaxRecipients, m.DailyLimit, m.FailureThresholdPercent, m.CooldownDays);
        var r = id is null ? await campaigns.CreateAsync(req, ct) : await campaigns.UpdateAsync(id.Value, req, ct);
        if (r.IsSuccess) { Success("Campaign saved as a draft. Review it, then submit it."); return RedirectToAction(nameof(Details), new { id = r.Value.Id }); }
        if (Reject(r) is { } rej) return rej;
        AddError(r);
        return View("Form", await FormAsync(m, ct));
    }

    /// <summary>Live audience preview for the wizard (JSON). Nothing is stored or sent.</summary>
    [HttpPost, Authorize(Policy = PermissionCodes.AudiencesPreview)]
    public async Task<IActionResult> PreviewAudience([Bind(Prefix = "Form")] CampaignFormModel m, CancellationToken ct)
    {
        var r = await audiences.PreviewAsync(new AudiencePreviewRequest(m.ToAudience(), m.Type, m.TemplateId, m.MaxRecipients, m.CooldownDays, m.Id, m.IncludeOptOutFooter), ct);
        if (r.IsFailure) return BadRequest(new { error = r.Error.Message });
        return Json(r.Value);
    }

    // ---------- actions ----------

    [HttpPost, Authorize(Policy = PermissionCodes.CampaignsUpdate)]
    public Task<IActionResult> Submit(Guid id, CancellationToken ct) => Act(id, () => campaigns.SubmitAsync(id, ct), "Submitted.");

    [HttpPost, Authorize(Policy = PermissionCodes.CampaignsApprove)]
    public Task<IActionResult> Approve(Guid id, CancellationToken ct) => Act(id, () => campaigns.ApproveAsync(id, ct), "Approved and scheduled.");

    [HttpPost, Authorize(Policy = PermissionCodes.CampaignsApprove)]
    public Task<IActionResult> RejectCampaign(Guid id, string? reason, CancellationToken ct) => Act(id, () => campaigns.RejectAsync(id, new RejectCampaignRequest(reason ?? ""), ct), "Returned to draft.");

    [HttpPost, Authorize(Policy = PermissionCodes.CampaignsUpdate)]
    public Task<IActionResult> Unschedule(Guid id, CancellationToken ct) => Act(id, () => campaigns.UnscheduleAsync(id, ct), "Unscheduled: the campaign is a draft again.");

    [HttpPost, Authorize(Policy = PermissionCodes.CampaignsExecute)]
    public Task<IActionResult> Start(Guid id, CancellationToken ct) => Act(id, () => campaigns.StartNowAsync(id, ct), "The campaign is starting. The audience is selected and messages are queued in batches.");

    [HttpPost, Authorize(Policy = PermissionCodes.CampaignsExecute)]
    public Task<IActionResult> Pause(Guid id, CancellationToken ct) => Act(id, () => campaigns.PauseAsync(id, ct), "Paused. Queued messages were withdrawn.");

    [HttpPost, Authorize(Policy = PermissionCodes.CampaignsExecute)]
    public Task<IActionResult> Resume(Guid id, CancellationToken ct) => Act(id, () => campaigns.ResumeAsync(id, ct), "Resumed.");

    [HttpPost, Authorize(Policy = PermissionCodes.CampaignsCancel)]
    public Task<IActionResult> Cancel(Guid id, CancellationToken ct) => Act(id, () => campaigns.CancelAsync(id, ct), "Cancelled.");

    [HttpPost, Authorize(Policy = PermissionCodes.CampaignsCreate)]
    public async Task<IActionResult> Clone(Guid id, CancellationToken ct)
    {
        var r = await campaigns.CloneAsync(id, new CloneCampaignRequest(null), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) { Failure(r.Error.Message); return RedirectToAction(nameof(Details), new { id }); }
        Success("Cloned as a new draft.");
        return RedirectToAction(nameof(Details), new { id = r.Value.Id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.CampaignsDelete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var r = await campaigns.DeleteAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) { Failure(r.Error.Message); return RedirectToAction(nameof(Details), new { id }); }
        Success("The draft was deleted.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.CampaignsExecute)]
    public async Task<IActionResult> TestSend(Guid id, string? phone, CancellationToken ct)
    {
        var r = await campaigns.TestSendAsync(id, new TestSendRequest(phone ?? ""), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Test message queued. It is labelled as a test and goes through the normal WhatsApp pipeline."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.CampaignsExecute)]
    public async Task<IActionResult> DryRun(Guid id, CancellationToken ct)
    {
        var c = await campaigns.GetAsync(id, ct);
        if (Reject(c) is { } rej) return rej;
        var r = await campaigns.DryRunAsync(id, ct);
        if (r.IsFailure) { Failure(r.Error.Message); return RedirectToAction(nameof(Details), new { id }); }
        var s = await settings.GetAsync(ct);
        Crumbs(("Campaigns", Url.Action(nameof(Index))), (c.Value.Name, Url.Action(nameof(Details), new { id })), ("Dry run", null));
        return View("DryRun", new CampaignDetailsViewModel { Campaign = c.Value, DryRun = r.Value, Settings = s });
    }

    private async Task<IActionResult> Act(Guid id, Func<Task<LeadPilot.Shared.Results.Result<CampaignDto>>> action, string okMessage)
    {
        var r = await action();
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success(okMessage); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---------- helpers ----------

    private async Task<CampaignFormViewModel> FormAsync(CampaignFormModel m, CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        var pipes = await pipelines.ListAsync(ct);
        var parents = await campaigns.ListAsync(new CampaignQuery { PageSize = 100 }, ct);
        return new CampaignFormViewModel
        {
            Form = m, Settings = s, TimeZone = s.TenantTimeZone, CanPreview = await permissions.HasAsync(PermissionCodes.AudiencesPreview, ct),
            Lookups = new CampaignLookups
            {
                Templates = (await templates.ListAsync(true, ct)).Where(t => t.Category == TemplateCategory.Marketing).ToList(), Accounts = await accounts.ListAsync(ct),
                Statuses = (await catalog.ListStatusesAsync(ct)).Where(x => x.IsActive).ToList(), Stages = pipes.SelectMany(p => p.Stages).ToList(), Categories = (await catalog.ListCategoriesAsync(ct)).Where(x => x.IsActive).ToList(),
                Tags = await tags.ListAsync(ct), Users = (await users.ListAsync(null, 1, 100, ct)).Items, ParentCandidates = parents.Items.Where(p => p.Status != CampaignStatus.Draft && p.Id != m.Id).ToList(),
            },
        };
    }

    private static TimeZoneInfo Zone(string id) { try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (Exception) { return TimeZoneInfo.Utc; } }
    private static string ToLocal(DateTime? utc, string tz) => utc is { } u ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(u, DateTimeKind.Utc), Zone(tz)).ToString("yyyy-MM-dd'T'HH:mm") : "";

    private static DateTime? ToUtc(string? local, string tz, out bool error)
    {
        error = false;
        if (string.IsNullOrWhiteSpace(local)) return null;
        if (!DateTime.TryParse(local, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)) { error = true; return null; }
        var zone = Zone(tz);
        var unspecified = DateTime.SpecifyKind(d, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }
}

/// <summary>Workspace campaign policy and the kill switch.</summary>
[Authorize(Policy = PermissionCodes.CampaignsView)]
public class CampaignSettingsController(ICampaignSettingsService settings, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        Crumbs(("Campaigns", Url.Action("Index", "Campaigns")), ("Campaign settings", null));
        return View(await ModelAsync(s, null, ct));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.CampaignsSettings)]
    public async Task<IActionResult> Save([Bind(Prefix = "Form")] CampaignSettingsFormModel m, CancellationToken ct)
    {
        Crumbs(("Campaigns", Url.Action("Index", "Campaigns")), ("Campaign settings", null));
        if (!ModelState.IsValid) return View(nameof(Index), await ModelAsync(await settings.GetAsync(ct), m, ct));
        var r = await settings.UpdateAsync(new UpdateCampaignSettingsRequest(m.CooldownDays, m.ApprovalRequired, m.CreatorCannotApprove, m.AttributionWindowDays, m.FailureThresholdPercent, m.FailureMinSample, m.OptOutFooter, m.MonthlyMessageLimit, CampaignFormModel.Split(m.TestNumbers)), ct);
        if (r.IsSuccess) { Success("Campaign settings saved."); return RedirectToAction(nameof(Index)); }
        AddError(r);
        return View(nameof(Index), await ModelAsync(await settings.GetAsync(ct), m, ct));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.CampaignsSettings)]
    public async Task<IActionResult> KillSwitch(bool enabled, string? reason, CancellationToken ct)
    {
        var r = await settings.SetKillSwitchAsync(new KillSwitchRequest(enabled, reason), ct);
        if (r.IsSuccess) Success(enabled ? "Kill switch ON: every running campaign was paused and no campaign message will be sent." : "Kill switch off. Paused campaigns stay paused until someone resumes them."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    private async Task<CampaignSettingsViewModel> ModelAsync(CampaignSettingsDto s, CampaignSettingsFormModel? form, CancellationToken ct) => new()
    {
        Settings = s, CanManage = await permissions.HasAsync(PermissionCodes.CampaignsSettings, ct),
        Form = form ?? new CampaignSettingsFormModel { CooldownDays = s.CooldownDays, ApprovalRequired = s.ApprovalRequired, CreatorCannotApprove = s.CreatorCannotApprove, AttributionWindowDays = s.AttributionWindowDays,
            FailureThresholdPercent = s.FailureThresholdPercent, FailureMinSample = s.FailureMinSample, OptOutFooter = s.OptOutFooter, MonthlyMessageLimit = s.MonthlyMessageLimit, TestNumbers = string.Join(", ", s.TestNumbers) },
    };
}
