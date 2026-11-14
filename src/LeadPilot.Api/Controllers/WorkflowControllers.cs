using LeadPilot.Application.Common;
using LeadPilot.Application.Security;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Workflows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Api.Controllers;

/// <summary>
/// Automation workflows: a trigger (a real domain event), declarative conditions and a list of steps. Versions are immutable once active; editing creates a new draft version. Everything is scoped to the
/// signed-in workspace: ids of other workspaces answer 404. Workflows never run code: conditions and actions come from closed catalogs.
/// </summary>
[Route("api/v1/workflows")]
public class WorkflowsController(IWorkflowService workflows, IWorkflowSimulationService simulation, IWorkflowExecutionService executions) : ApiControllerBase
{
    /// <summary>Triggers, actions, condition fields/operators and the workspace's templates, accounts, people, statuses and stages that a definition may reference.</summary>
    [HttpGet("catalog"), Authorize(Policy = PermissionCodes.WorkflowsView)]
    public async Task<WorkflowCatalogDto> Catalog(CancellationToken ct) => await workflows.CatalogAsync(ct);

    [HttpGet, Authorize(Policy = PermissionCodes.WorkflowsView)]
    public async Task<PagedResult<WorkflowListItemDto>> List([FromQuery] WorkflowQuery query, CancellationToken ct) => await workflows.ListAsync(query, ct);

    [HttpGet("{id:guid}"), Authorize(Policy = PermissionCodes.WorkflowsView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await workflows.GetAsync(id, ct));

    [HttpPost, Authorize(Policy = PermissionCodes.WorkflowsCreate)]
    public async Task<IActionResult> Create(CreateWorkflowRequest request, CancellationToken ct)
    { var r = await workflows.CreateAsync(request, ct); return r.IsSuccess ? CreatedAtAction(nameof(Get), new { id = r.Value.Id }, r.Value) : Fail(r); }

    [HttpPut("{id:guid}"), Authorize(Policy = PermissionCodes.WorkflowsUpdate)]
    public async Task<IActionResult> Update(Guid id, UpdateWorkflowRequest request, CancellationToken ct) => Respond(await workflows.UpdateAsync(id, request, ct));

    /// <summary>Checks a definition without saving it: errors (unknown fields/actions, loops, limits, missing references) and warnings.</summary>
    [HttpPost("validate"), Authorize(Policy = PermissionCodes.WorkflowsView)]
    public async Task<IActionResult> Validate(SaveDraftRequest request, CancellationToken ct) => Respond(await workflows.ValidateAsync(request.Definition, ct));

    [HttpGet("{id:guid}/versions/{number:int}"), Authorize(Policy = PermissionCodes.WorkflowsView)]
    public async Task<IActionResult> GetVersion(Guid id, int number, CancellationToken ct) => Respond(await workflows.GetVersionAsync(id, number, ct));

    /// <summary>Saves the editable draft (creates a new version when there is none). An active version is never changed.</summary>
    [HttpPut("{id:guid}/draft"), Authorize(Policy = PermissionCodes.WorkflowsUpdate)]
    public async Task<IActionResult> SaveDraft(Guid id, SaveDraftRequest request, CancellationToken ct) => Respond(await workflows.SaveDraftAsync(id, request, ct));

    /// <summary>Creates a new draft from an existing version (how an active workflow is edited).</summary>
    [HttpPost("{id:guid}/versions/{number:int}/new-draft"), Authorize(Policy = PermissionCodes.WorkflowsUpdate)]
    public async Task<IActionResult> NewDraft(Guid id, int number, CancellationToken ct) => Respond(await workflows.NewDraftFromAsync(id, number, ct));

    [HttpPost("{id:guid}/versions/{number:int}/activate"), Authorize(Policy = PermissionCodes.WorkflowsActivate)]
    public async Task<IActionResult> Activate(Guid id, int number, CancellationToken ct) => Respond(await workflows.ActivateAsync(id, number, ct));

    [HttpPost("{id:guid}/deactivate"), Authorize(Policy = PermissionCodes.WorkflowsActivate)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct) => Respond(await workflows.DeactivateAsync(id, ct));

    /// <summary>Per-workflow kill switch: nothing starts and running executions stop before their next step.</summary>
    [HttpPut("{id:guid}/kill-switch"), Authorize(Policy = PermissionCodes.WorkflowsActivate)]
    public async Task<IActionResult> KillSwitch(Guid id, WorkflowKillSwitchRequest request, CancellationToken ct) => Respond(await workflows.SetKillSwitchAsync(id, request.On, request.Reason, ct));

    [HttpDelete("{id:guid}"), Authorize(Policy = PermissionCodes.WorkflowsDelete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => Respond(await workflows.DeleteAsync(id, ct));

    /// <summary>Dry run: what would happen for this lead. Writes nothing, sends nothing.</summary>
    [HttpPost("{id:guid}/simulate"), Authorize(Policy = PermissionCodes.WorkflowsExecute)]
    public async Task<IActionResult> Simulate(Guid id, SimulateRequest request, CancellationToken ct) => Respond(await simulation.SimulateAsync(id, request, ct));

    /// <summary>Runs the active version for one lead now (same limits, eligibility checks and idempotency as an event-driven run).</summary>
    [HttpPost("{id:guid}/run"), Authorize(Policy = PermissionCodes.WorkflowsExecute)]
    public async Task<IActionResult> Run(Guid id, StartExecutionRequest request, CancellationToken ct) => Respond(await executions.StartAsync(id, request.LeadId, ct));
}

[Route("api/v1/workflow-executions")]
public class WorkflowExecutionsController(IWorkflowExecutionService executions) : ApiControllerBase
{
    [HttpGet, Authorize(Policy = PermissionCodes.WorkflowsView)]
    public async Task<PagedResult<ExecutionListItemDto>> List([FromQuery] ExecutionQuery query, CancellationToken ct) => await executions.ListAsync(query, ct);

    /// <summary>One run with its per-step state and log.</summary>
    [HttpGet("{id:guid}"), Authorize(Policy = PermissionCodes.WorkflowsView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await executions.GetAsync(id, ct));

    /// <summary>Retries a failed run from its failed step. Effects that already happened (a message that was queued) are never repeated.</summary>
    [HttpPost("{id:guid}/retry"), Authorize(Policy = PermissionCodes.WorkflowsExecute)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct) => Respond(await executions.RetryAsync(id, ct));

    [HttpPost("{id:guid}/cancel"), Authorize(Policy = PermissionCodes.WorkflowsExecute)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) => Respond(await executions.CancelAsync(id, ct));
}

[Route("api/v1/workflow-settings")]
public class WorkflowSettingsController(IWorkflowSettingsService settings) : ApiControllerBase
{
    [HttpGet, Authorize(Policy = PermissionCodes.WorkflowsView)]
    public async Task<WorkflowSettingsDto> Get(CancellationToken ct) => await settings.GetAsync(ct);

    [HttpPut("limits"), Authorize(Policy = PermissionCodes.WorkflowsActivate)]
    public async Task<IActionResult> SaveLimits(SaveWorkflowSettingsRequest request, CancellationToken ct) => Respond(await settings.SaveLimitsAsync(request, ct));

    /// <summary>Workspace kill switch: no workflow starts and running workflows stop before their next step.</summary>
    [HttpPut("kill-switch"), Authorize(Policy = PermissionCodes.WorkflowsActivate)]
    public async Task<IActionResult> KillSwitch(WorkflowKillSwitchRequest request, CancellationToken ct) => Respond(await settings.SetKillSwitchAsync(request.On, request.Reason, ct));
}

[Route("api/v1/workflow-notifications")]
public class WorkflowNotificationsController(IWorkflowNotificationService notifications) : ApiControllerBase
{
    /// <summary>The signed-in person's own notices created by "notify" steps.</summary>
    [HttpGet, Authorize(Policy = PermissionCodes.WorkflowsView)]
    public async Task<IReadOnlyList<WorkflowNotificationDto>> Mine([FromQuery] bool unreadOnly, CancellationToken ct) => await notifications.MineAsync(unreadOnly, ct);

    [HttpPost("{id:guid}/read"), Authorize(Policy = PermissionCodes.WorkflowsView)]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct) => Respond(await notifications.MarkReadAsync(id, ct));
}
