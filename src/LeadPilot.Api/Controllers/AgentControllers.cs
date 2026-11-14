using LeadPilot.Application.Agents;
using LeadPilot.Application.Common;
using LeadPilot.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Api.Controllers;

public sealed record ActiveRequest(bool Active);
public sealed record EnabledRequest(bool Enabled);

/// <summary>
/// The configurable sales agent: instructions, enabled tools, qualification questions, models, thresholds and the explicit opt-in for automatic replies. Everything is scoped to the signed-in workspace; ids of other
/// workspaces answer 404.
/// </summary>
[Route("api/v1/agents")]
public class AgentsController(IAgentService agents) : ApiControllerBase
{
    [HttpGet, Authorize(Policy = PermissionCodes.AgentsView)]
    public async Task<IReadOnlyList<AgentListItemDto>> List(CancellationToken ct) => await agents.ListAsync(ct);

    /// <summary>The controlled tools that exist. An agent can only use the ones enabled for it (plus the always-on safety tools).</summary>
    [HttpGet("tools"), Authorize(Policy = PermissionCodes.AgentsView)]
    public IReadOnlyList<ToolInfoDto> Tools() => agents.Tools();

    [HttpGet("settings"), Authorize(Policy = PermissionCodes.AgentsView)]
    public async Task<AgentSettingsDto> GetSettings(CancellationToken ct) => await agents.GetSettingsAsync(ct);

    /// <summary>Workspace switch for automatic AI replies (off by default) and the limits of the sales agents.</summary>
    [HttpPut("settings"), Authorize(Policy = PermissionCodes.AgentsManage)]
    public async Task<IActionResult> UpdateSettings(SaveAgentSettingsRequest request, CancellationToken ct) => Respond(await agents.UpdateSettingsAsync(request, ct));

    [HttpGet("{id:guid}"), Authorize(Policy = PermissionCodes.AgentsView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await agents.GetAsync(id, ct));

    [HttpPost, Authorize(Policy = PermissionCodes.AgentsManage)]
    public async Task<IActionResult> Create(SaveAgentRequest request, CancellationToken ct) { var r = await agents.CreateAsync(request, ct); return r.IsSuccess ? CreatedAtAction(nameof(Get), new { id = r.Value.Id }, r.Value) : Fail(r); }

    /// <summary>Creates the clearly-marked SAMPLE School ERP agent and placeholder catalog (nothing is confirmed, so the AI says it does not know until the catalog is filled in).</summary>
    [HttpPost("sample"), Authorize(Policy = PermissionCodes.AgentsManage)]
    public async Task<IActionResult> CreateSample(CancellationToken ct) { var r = await agents.CreateSampleAsync(ct); return r.IsSuccess ? CreatedAtAction(nameof(Get), new { id = r.Value.Id }, r.Value) : Fail(r); }

    [HttpPut("{id:guid}"), Authorize(Policy = PermissionCodes.AgentsManage)]
    public async Task<IActionResult> Update(Guid id, SaveAgentRequest request, CancellationToken ct) => Respond(await agents.UpdateAsync(id, request, ct));

    /// <summary>Replaces the qualification questions (the AI can only record answers for these keys).</summary>
    [HttpPut("{id:guid}/questions"), Authorize(Policy = PermissionCodes.AgentsManage)]
    public async Task<IActionResult> SaveQuestions(Guid id, SaveQuestionsRequest request, CancellationToken ct) => Respond(await agents.SaveQuestionsAsync(id, request, ct));

    [HttpPut("{id:guid}/active"), Authorize(Policy = PermissionCodes.AgentsManage)]
    public async Task<IActionResult> SetActive(Guid id, ActiveRequest request, CancellationToken ct) => Respond(await agents.SetActiveAsync(id, request.Active, ct));

    [HttpPost("{id:guid}/default"), Authorize(Policy = PermissionCodes.AgentsManage)]
    public async Task<IActionResult> SetDefault(Guid id, CancellationToken ct) => Respond(await agents.SetDefaultAsync(id, ct));

    /// <summary>The explicit opt-in for automatic replies of this agent (needs the workspace switch too). Replies still pass suppression, rate limits and every safety check; the AI only answers inside customer-initiated conversations.</summary>
    [HttpPut("{id:guid}/auto-send"), Authorize(Policy = PermissionCodes.AgentsManage)]
    public async Task<IActionResult> SetAutoSend(Guid id, EnabledRequest request, CancellationToken ct) => Respond(await agents.SetAutoSendAsync(id, request.Enabled, ct));
}

/// <summary>The product and pricing catalog the sales agent quotes from. Only CONFIRMED entries are given to the AI as facts.</summary>
[Route("api/v1/catalog")]
public class ProductCatalogController(ICatalogService catalog) : ApiControllerBase
{
    [HttpGet("products"), Authorize(Policy = PermissionCodes.CatalogView)]
    public async Task<IReadOnlyList<ProductListItemDto>> List(CancellationToken ct) => await catalog.ListAsync(ct);

    [HttpGet("products/{id:guid}"), Authorize(Policy = PermissionCodes.CatalogView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await catalog.GetAsync(id, ct));

    [HttpPost("products"), Authorize(Policy = PermissionCodes.CatalogManage)]
    public async Task<IActionResult> Create(SaveProductRequest request, CancellationToken ct) { var r = await catalog.CreateAsync(request, ct); return r.IsSuccess ? CreatedAtAction(nameof(Get), new { id = r.Value.Id }, r.Value) : Fail(r); }

    [HttpPut("products/{id:guid}"), Authorize(Policy = PermissionCodes.CatalogManage)]
    public async Task<IActionResult> Update(Guid id, SaveProductRequest request, CancellationToken ct) => Respond(await catalog.UpdateAsync(id, request, ct));

    [HttpPost("products/{id:guid}/plans"), Authorize(Policy = PermissionCodes.CatalogManage)]
    public async Task<IActionResult> AddPlan(Guid id, SavePlanRequest request, CancellationToken ct) { var r = await catalog.AddPlanAsync(id, request, ct); return r.IsSuccess ? Created($"api/v1/catalog/products/{id}", r.Value) : Fail(r); }

    [HttpPut("plans/{id:guid}"), Authorize(Policy = PermissionCodes.CatalogManage)]
    public async Task<IActionResult> UpdatePlan(Guid id, SavePlanRequest request, CancellationToken ct) => Respond(await catalog.UpdatePlanAsync(id, request, ct));

    [HttpDelete("plans/{id:guid}"), Authorize(Policy = PermissionCodes.CatalogManage)]
    public async Task<IActionResult> DeletePlan(Guid id, CancellationToken ct) => Respond(await catalog.DeletePlanAsync(id, ct));
}

/// <summary>Sensitive AI actions (payment requests, discounts, unusual terms, trials over the limit) waiting for a person. Approving carries out the stored action; nothing is ever activated on an AI claim.</summary>
[Route("api/v1/approvals")]
public class ApprovalsController(IApprovalService approvals) : ApiControllerBase
{
    [HttpGet, Authorize(Policy = PermissionCodes.ApprovalsView)]
    public async Task<PagedResult<ApprovalDto>> List([FromQuery] ApprovalQuery query, CancellationToken ct) => await approvals.ListAsync(query, ct);

    [HttpGet("{id:guid}"), Authorize(Policy = PermissionCodes.ApprovalsView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await approvals.GetAsync(id, ct));

    /// <summary>409 when it was already decided or expired. The response says whether the action ran, was not available (the Phase 10 service is not connected) or failed.</summary>
    [HttpPost("{id:guid}/approve"), Authorize(Policy = PermissionCodes.ApprovalsDecide)]
    public async Task<IActionResult> Approve(Guid id, DecideApprovalRequest request, CancellationToken ct) => Respond(await approvals.ApproveAsync(id, request, ct));

    [HttpPost("{id:guid}/reject"), Authorize(Policy = PermissionCodes.ApprovalsDecide)]
    public async Task<IActionResult> Reject(Guid id, DecideApprovalRequest request, CancellationToken ct) => Respond(await approvals.RejectAsync(id, request, ct));
}

/// <summary>Every tool the AI asked for: inputs, outputs, outcome and approval link.</summary>
[Route("api/v1/tool-executions")]
public class ToolExecutionsController(IToolExecutionService log) : ApiControllerBase
{
    [HttpGet, Authorize(Policy = PermissionCodes.AgentsView)]
    public async Task<PagedResult<ToolExecutionDto>> List([FromQuery] ToolExecutionQuery query, CancellationToken ct) => await log.ListAsync(query, ct);
}

/// <summary>Internal follow-up tasks (created by the AI agent or by people). Nothing is sent to the customer automatically.</summary>
[Route("api/v1/follow-ups")]
public class FollowUpsController(IFollowUpService followUps) : ApiControllerBase
{
    [HttpGet, Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<PagedResult<FollowUpDto>> List([FromQuery] FollowUpQuery query, CancellationToken ct) => await followUps.ListAsync(query, ct);

    [HttpPost("{id:guid}/done"), Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> Done(Guid id, CancellationToken ct) => Respond(await followUps.CloseAsync(id, true, ct));

    [HttpPost("{id:guid}/cancel"), Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) => Respond(await followUps.CloseAsync(id, false, ct));
}
