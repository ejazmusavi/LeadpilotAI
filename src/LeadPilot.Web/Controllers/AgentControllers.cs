using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Agents;
using LeadPilot.Application.Ai;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Agents;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Web.Controllers;

/// <summary>The sales agent: configuration, qualification questions, opt-in for automatic replies and the tool log. Mutations are POST-only (antiforgery) behind Agents.Manage.</summary>
[Authorize(Policy = PermissionCodes.AgentsView)]
public class AgentsController(IAgentService agents, IToolExecutionService log, IAiSettingsService aiSettings, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("AI", null), ("Sales agent", null));
        return View(new AgentsIndexViewModel { Agents = await agents.ListAsync(ct), Settings = await agents.GetSettingsAsync(ct), CanManage = await permissions.HasAsync(PermissionCodes.AgentsManage, ct) });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid? id, CancellationToken ct)
    {
        var vm = new AgentEditViewModel { Tools = agents.Tools().Where(t => !t.Safety).ToList(), CanManage = await permissions.HasAsync(PermissionCodes.AgentsManage, ct) };
        if (id is { } agentId)
        {
            var a = await agents.GetAsync(agentId, ct);
            if (Reject(a) is { } rej) return rej;
            vm.Agent = a.Value; vm.QuestionsText = QuestionLines.Format(a.Value.Questions);
            vm.Form = new AgentFormModel { Id = a.Value.Id, Version = a.Value.Version, Name = a.Value.Name, Description = a.Value.Description, Instructions = a.Value.Instructions, ChatModel = a.Value.ChatModel, MinConfidence = a.Value.MinConfidence, OffTopicReply = a.Value.OffTopicReply, EnabledTools = a.Value.EnabledTools.ToList() };
        }
        else if (!vm.CanManage) return Forbid();
        else vm.Form.EnabledTools = [AgentTools.GetProductInformation, AgentTools.GetPricingPlans, AgentTools.GetLeadProfile, AgentTools.QualifyLead];
        vm.SuggestedModels = await ModelsAsync(ct);
        Crumbs(("AI", null), ("Sales agent", Url.Action(nameof(Index))), (vm.Agent?.Name ?? "New agent", null));
        return View(vm);
    }

    private async Task<IReadOnlyList<string>> ModelsAsync(CancellationToken ct) => await permissions.HasAsync(PermissionCodes.AiSettings, ct) ? (await aiSettings.GetAsync(ct)).SuggestedChatModels : [];

    [HttpPost, Authorize(Policy = PermissionCodes.AgentsManage)]
    public async Task<IActionResult> Save(AgentFormModel m, CancellationToken ct)
    {
        if (!ModelState.IsValid) return await Redisplay(m, ct);
        var req = new SaveAgentRequest(m.Name, m.Description, m.Instructions, m.ChatModel, m.MinConfidence, m.EnabledTools, m.OffTopicReply, m.Version);
        var r = m.Id is { } id ? await agents.UpdateAsync(id, req, ct) : await agents.CreateAsync(req, ct);
        if (r.IsSuccess) { Success("Agent saved."); return RedirectToAction(nameof(Edit), new { id = r.Value.Id }); }
        if (Reject(r) is { } rej) return rej;
        AddError(r);
        return await Redisplay(m, ct);
    }

    private async Task<IActionResult> Redisplay(AgentFormModel m, CancellationToken ct)
    {
        var vm = new AgentEditViewModel { Form = m, Tools = agents.Tools().Where(t => !t.Safety).ToList(), CanManage = true, SuggestedModels = await ModelsAsync(ct) };
        if (m.Id is { } id) { var a = await agents.GetAsync(id, ct); if (a.IsSuccess) { vm.Agent = a.Value; vm.QuestionsText = QuestionLines.Format(a.Value.Questions); } }
        Crumbs(("AI", null), ("Sales agent", Url.Action(nameof(Index))), (vm.Agent?.Name ?? "New agent", null));
        return View("Edit", vm);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.AgentsManage)]
    public async Task<IActionResult> Questions(Guid id, string? questionsText, CancellationToken ct)
    {
        var (list, error) = QuestionLines.Parse(questionsText);
        if (error is not null) { Failure(error); return RedirectToAction(nameof(Edit), new { id }); }
        var r = await agents.SaveQuestionsAsync(id, new SaveQuestionsRequest(list), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Qualification questions saved."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.AgentsManage)]
    public async Task<IActionResult> SetActive(Guid id, bool active, CancellationToken ct)
    {
        var r = await agents.SetActiveAsync(id, active, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success(active ? "Agent switched on." : "Agent switched off (and removed as default; automatic replies off)."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.AgentsManage)]
    public async Task<IActionResult> SetDefault(Guid id, CancellationToken ct)
    {
        var r = await agents.SetDefaultAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("This agent now answers conversations in AI mode."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.AgentsManage)]
    public async Task<IActionResult> SetAutoSend(Guid id, bool enabled, CancellationToken ct)
    {
        var r = await agents.SetAutoSendAsync(id, enabled, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success(enabled ? "Automatic replies are ON for this agent. It answers only inside conversations the customer started." : "Automatic replies are off: the agent only suggests replies."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.AgentsManage)]
    public async Task<IActionResult> CreateSample(CancellationToken ct)
    {
        var r = await agents.CreateSampleAsync(ct);
        if (r.IsSuccess) { Success("Sample School ERP agent created. It contains placeholders only: fill in and confirm the product catalog before relying on it."); return RedirectToAction(nameof(Edit), new { id = r.Value.Id }); }
        Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.AgentsManage)]
    public async Task<IActionResult> SaveSettings(AgentSettingsFormModel m, CancellationToken ct)
    {
        if (!ModelState.IsValid) { Failure("Check the values: " + string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage))); return RedirectToAction(nameof(Index)); }
        var r = await agents.UpdateSettingsAsync(new SaveAgentSettingsRequest(m.AiRepliesAllowed, m.MaxAutoRepliesPerConversationPerDay, m.MaxTrialsPerLead, m.ApprovalLifetimeHours, m.MaxOpenFollowUpsPerLead), ct);
        if (r.IsSuccess) Success("Agent settings saved."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Executions(ToolExecutionQuery query, CancellationToken ct)
    {
        Crumbs(("AI", null), ("Sales agent", Url.Action(nameof(Index))), ("Tool log", null));
        var qv = new Dictionary<string, string?> { ["tool"] = query.Tool, ["outcome"] = query.Outcome?.ToString() };
        return View(new ToolLogViewModel { Result = await log.ListAsync(query, ct), Query = query, QueryValues = qv, Tools = agents.Tools() });
    }
}

[Authorize(Policy = PermissionCodes.ApprovalsView)]
public class ApprovalsController(IApprovalService approvals, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(ApprovalQuery query, CancellationToken ct)
    {
        Crumbs(("AI", null), ("Approvals", null));
        var qv = new Dictionary<string, string?> { ["status"] = query.Status?.ToString() };
        return View(new ApprovalsViewModel { Result = await approvals.ListAsync(query, ct), Query = query, QueryValues = qv, CanDecide = await permissions.HasAsync(PermissionCodes.ApprovalsDecide, ct) });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ApprovalsDecide)]
    public async Task<IActionResult> Approve(Guid id, string? note, CancellationToken ct)
    {
        var r = await approvals.ApproveAsync(id, new DecideApprovalRequest(note), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) Failure(r.Error.Message);
        else Success(r.Value.Execution switch
        {
            ExecutionState.Executed => "Approved and carried out.",
            ExecutionState.NotAvailable => "Approved and recorded, but NOTHING was done: the service behind this action (payments, trials or demos) is not connected yet.",
            _ => "Approved, but the action could not be carried out: " + r.Value.ExecutionNote,
        });
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ApprovalsDecide)]
    public async Task<IActionResult> RejectRequest(Guid id, string? note, CancellationToken ct)
    {
        var r = await approvals.RejectAsync(id, new DecideApprovalRequest(note), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Request rejected. Nothing was done."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }
}

[Authorize(Policy = PermissionCodes.CatalogView)]
public class CatalogController(ICatalogService catalog, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("AI", null), ("Product catalog", null));
        return View(new CatalogIndexViewModel { Products = await catalog.ListAsync(ct), CanManage = await permissions.HasAsync(PermissionCodes.CatalogManage, ct) });
    }

    [HttpGet]
    public async Task<IActionResult> Product(Guid? id, CancellationToken ct)
    {
        var vm = new ProductEditViewModel { CanManage = await permissions.HasAsync(PermissionCodes.CatalogManage, ct) };
        if (id is { } pid)
        {
            var p = await catalog.GetAsync(pid, ct);
            if (Reject(p) is { } rej) return rej;
            vm.Product = p.Value;
            vm.Form = new ProductFormModel { Id = p.Value.Id, Version = p.Value.Version, Name = p.Value.Name, Summary = p.Value.Summary, Features = p.Value.Features, DemoProcess = p.Value.DemoProcess, TrialProcess = p.Value.TrialProcess, IsConfirmed = p.Value.IsConfirmed, IsActive = p.Value.IsActive };
        }
        else if (!vm.CanManage) return Forbid();
        Crumbs(("AI", null), ("Product catalog", Url.Action(nameof(Index))), (vm.Product?.Name ?? "New product", null));
        return View(vm);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.CatalogManage)]
    public async Task<IActionResult> SaveProduct(ProductFormModel m, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var req = new SaveProductRequest(m.Name, m.Summary, m.Features, m.DemoProcess, m.TrialProcess, m.IsConfirmed, m.IsActive, m.Version);
            var r = m.Id is { } id ? await catalog.UpdateAsync(id, req, ct) : await catalog.CreateAsync(req, ct);
            if (r.IsSuccess) { Success("Product saved."); return RedirectToAction(nameof(Product), new { id = r.Value.Id }); }
            if (Reject(r) is { } rej) return rej;
            AddError(r);
        }
        var vm = new ProductEditViewModel { Form = m, CanManage = true };
        if (m.Id is { } pid) { var p = await catalog.GetAsync(pid, ct); if (p.IsSuccess) vm.Product = p.Value; }
        Crumbs(("AI", null), ("Product catalog", Url.Action(nameof(Index))), (m.Name, null));
        return View("Product", vm);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.CatalogManage)]
    public async Task<IActionResult> SavePlan(Guid productId, PlanFormModel m, CancellationToken ct)
    {
        if (!ModelState.IsValid) { Failure("Check the plan: " + string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage))); return RedirectToAction(nameof(Product), new { id = productId }); }
        var req = new SavePlanRequest(m.Name, m.Price, m.Currency, m.BillingPeriod, m.Details, m.MaxDiscountPercent, m.IsConfirmed, m.IsActive);
        var r = m.Id is { } id ? await catalog.UpdatePlanAsync(id, req, ct) : await catalog.AddPlanAsync(productId, req, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Plan saved."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Product), new { id = productId });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.CatalogManage)]
    public async Task<IActionResult> DeletePlan(Guid productId, Guid id, CancellationToken ct)
    {
        var r = await catalog.DeletePlanAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Plan deleted."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Product), new { id = productId });
    }
}

[Authorize(Policy = PermissionCodes.LeadsView)]
public class FollowUpsController(IFollowUpService followUps, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(FollowUpQuery query, CancellationToken ct)
    {
        Crumbs(("Follow-ups", null));
        var qv = new Dictionary<string, string?> { ["status"] = query.Status?.ToString() };
        return View(new FollowUpsViewModel { Result = await followUps.ListAsync(query, ct), Query = query, QueryValues = qv, CanUpdate = await permissions.HasAsync(PermissionCodes.LeadsUpdate, ct) });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> Close(Guid id, bool done, CancellationToken ct)
    {
        var r = await followUps.CloseAsync(id, done, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success(done ? "Follow-up marked done." : "Follow-up cancelled."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }
}
