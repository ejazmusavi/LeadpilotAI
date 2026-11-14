using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Common;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Campaigns;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Api.Controllers;

/// <summary>
/// Marketing campaigns of the signed-in workspace. Campaign messages go through the same outbound pipeline as every other WhatsApp message: marketing consent and the suppression list are
/// checked when the audience is selected, again before each recipient is queued and once more immediately before the gateway call. WhatsApp automation via WAHA is unofficial and a number can be
/// restricted or banned: sending limits and warm-up are enforced and cannot be switched off. Other workspaces' ids answer 404.
/// </summary>
[Route("api/v1/campaigns")]
public class CampaignsController(ICampaignService campaigns, ICampaignAudienceService audiences, ICampaignSettingsService settings) : ApiControllerBase
{
    /// <summary>Campaigns with their (rollup) counts, newest first.</summary>
    [HttpGet, Authorize(Policy = PermissionCodes.CampaignsView)]
    public async Task<PagedResult<CampaignListItemDto>> List([FromQuery] CampaignQuery query, CancellationToken ct) => await campaigns.ListAsync(query, ct);

    [HttpGet("{id:guid}"), Authorize(Policy = PermissionCodes.CampaignsView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await campaigns.GetAsync(id, ct));

    /// <summary>Creates a draft. Nothing is selected or sent until it is submitted and started.</summary>
    [HttpPost, Authorize(Policy = PermissionCodes.CampaignsCreate)]
    public async Task<IActionResult> Create(SaveCampaignRequest request, CancellationToken ct)
    {
        var r = await campaigns.CreateAsync(request, ct);
        return r.IsSuccess ? CreatedAtAction(nameof(Get), new { id = r.Value.Id }, r.Value) : Fail(r);
    }

    /// <summary>Edits a draft (409 for any other status).</summary>
    [HttpPut("{id:guid}"), Authorize(Policy = PermissionCodes.CampaignsUpdate)]
    public async Task<IActionResult> Update(Guid id, SaveCampaignRequest request, CancellationToken ct) => Respond(await campaigns.UpdateAsync(id, request, ct));

    /// <summary>Deletes a draft (409 for anything that was scheduled or has run: cancel it instead).</summary>
    [HttpDelete("{id:guid}"), Authorize(Policy = PermissionCodes.CampaignsDelete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => Respond(await campaigns.DeleteAsync(id, ct));

    /// <summary>A new draft with the same definition.</summary>
    [HttpPost("{id:guid}/clone"), Authorize(Policy = PermissionCodes.CampaignsCreate)]
    public async Task<IActionResult> Clone(Guid id, CloneCampaignRequest request, CancellationToken ct)
    {
        var r = await campaigns.CloneAsync(id, request, ct);
        return r.IsSuccess ? CreatedAtAction(nameof(Get), new { id = r.Value.Id }, r.Value) : Fail(r);
    }

    /// <summary>Validates and freezes the content, then asks for approval (when the workspace requires it) or schedules the campaign.</summary>
    [HttpPost("{id:guid}/submit"), Authorize(Policy = PermissionCodes.CampaignsUpdate)]
    public async Task<IActionResult> Submit(Guid id, CancellationToken ct) => Respond(await campaigns.SubmitAsync(id, ct));

    /// <summary>Approves a campaign waiting for approval. The creator/submitter cannot approve when the workspace policy says so.</summary>
    [HttpPost("{id:guid}/approve"), Authorize(Policy = PermissionCodes.CampaignsApprove)]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct) => Respond(await campaigns.ApproveAsync(id, ct));

    [HttpPost("{id:guid}/reject"), Authorize(Policy = PermissionCodes.CampaignsApprove)]
    public async Task<IActionResult> Reject(Guid id, RejectCampaignRequest request, CancellationToken ct) => Respond(await campaigns.RejectAsync(id, request, ct));

    /// <summary>Scheduled → draft again (the scheduled start is removed).</summary>
    [HttpPost("{id:guid}/unschedule"), Authorize(Policy = PermissionCodes.CampaignsUpdate)]
    public async Task<IActionResult> Unschedule(Guid id, CancellationToken ct) => Respond(await campaigns.UnscheduleAsync(id, ct));

    /// <summary>Starts a scheduled campaign now. 409 while the workspace kill switch is on.</summary>
    [HttpPost("{id:guid}/start"), Authorize(Policy = PermissionCodes.CampaignsExecute)]
    public async Task<IActionResult> Start(Guid id, CancellationToken ct) => Respond(await campaigns.StartNowAsync(id, ct));

    /// <summary>Pauses a running campaign. The running batch stops before the next recipient and queued messages are withdrawn.</summary>
    [HttpPost("{id:guid}/pause"), Authorize(Policy = PermissionCodes.CampaignsExecute)]
    public async Task<IActionResult> Pause(Guid id, CancellationToken ct) => Respond(await campaigns.PauseAsync(id, ct));

    [HttpPost("{id:guid}/resume"), Authorize(Policy = PermissionCodes.CampaignsExecute)]
    public async Task<IActionResult> Resume(Guid id, CancellationToken ct) => Respond(await campaigns.ResumeAsync(id, ct));

    /// <summary>Cancels the campaign: waiting recipients are cancelled and queued messages withdrawn. A message already being sent finishes.</summary>
    [HttpPost("{id:guid}/cancel"), Authorize(Policy = PermissionCodes.CampaignsCancel)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) => Respond(await campaigns.CancelAsync(id, ct));

    /// <summary>Sends the labelled test message to ONE number of the workspace's test-number allow-list (campaign settings). 202: sent by the worker.</summary>
    [HttpPost("{id:guid}/test-send"), Authorize(Policy = PermissionCodes.CampaignsExecute)]
    public async Task<IActionResult> TestSend(Guid id, TestSendRequest request, CancellationToken ct)
    {
        var r = await campaigns.TestSendAsync(id, request, ct);
        return r.IsSuccess ? Accepted(r.Value) : Fail(r);
    }

    /// <summary>Evaluates audience, rendering and account readiness without sending anything.</summary>
    [HttpPost("{id:guid}/dry-run"), Authorize(Policy = PermissionCodes.CampaignsExecute)]
    public async Task<IActionResult> DryRun(Guid id, CancellationToken ct) => Respond(await campaigns.DryRunAsync(id, ct));

    [HttpGet("{id:guid}/recipients"), Authorize(Policy = PermissionCodes.CampaignsView)]
    public async Task<IActionResult> Recipients(Guid id, [FromQuery] RecipientQuery query, CancellationToken ct) => Respond(await campaigns.RecipientsAsync(id, query, ct));

    [HttpGet("{id:guid}/log"), Authorize(Policy = PermissionCodes.CampaignsView)]
    public async Task<IActionResult> Log(Guid id, int page = 1, int pageSize = 50, CancellationToken ct = default) => Respond(await campaigns.LogAsync(id, page, pageSize, ct));

    /// <summary>Replies and CRM events (lead stage changes) attributed to this campaign inside its attribution window.</summary>
    [HttpGet("{id:guid}/conversions"), Authorize(Policy = PermissionCodes.CampaignsView)]
    public async Task<IActionResult> Conversions(Guid id, int page = 1, int pageSize = 50, CancellationToken ct = default) => Respond(await campaigns.ConversionsAsync(id, page, pageSize, ct));

    /// <summary>Counts, rates, skip/failure reasons and whether the rollup equals the raw rows.</summary>
    [HttpGet("{id:guid}/stats"), Authorize(Policy = PermissionCodes.CampaignsView)]
    public async Task<IActionResult> Stats(Guid id, CancellationToken ct) => Respond(await campaigns.StatsAsync(id, ct));

    /// <summary>Audience preview: how many contacts match, how many would be messaged and why the others are excluded. Nothing is stored or sent.</summary>
    [HttpPost("audience/preview"), Authorize(Policy = PermissionCodes.AudiencesPreview)]
    public async Task<IActionResult> Preview(AudiencePreviewRequest request, CancellationToken ct) => Respond(await audiences.PreviewAsync(request, ct));

    /// <summary>Workspace campaign policy: cool-down, approval, attribution window, failure threshold, opt-out text, monthly limit, test numbers, kill switch.</summary>
    [HttpGet("settings"), Authorize(Policy = PermissionCodes.CampaignsView)]
    public async Task<CampaignSettingsDto> GetSettings(CancellationToken ct) => await settings.GetAsync(ct);

    [HttpPut("settings"), Authorize(Policy = PermissionCodes.CampaignsSettings)]
    public async Task<IActionResult> UpdateSettings(UpdateCampaignSettingsRequest request, CancellationToken ct) => Respond(await settings.UpdateAsync(request, ct));

    /// <summary>Turns the kill switch on (pauses every running campaign, blocks every campaign send) or off (nothing resumes by itself).</summary>
    [HttpPut("settings/kill-switch"), Authorize(Policy = PermissionCodes.CampaignsSettings)]
    public async Task<IActionResult> KillSwitch(KillSwitchRequest request, CancellationToken ct) => Respond(await settings.SetKillSwitchAsync(request, ct));
}
