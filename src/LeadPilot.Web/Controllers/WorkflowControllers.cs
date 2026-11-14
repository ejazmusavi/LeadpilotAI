using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Security;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Workflows;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Web.Controllers;

/// <summary>
/// Workflow list, builder, versions, simulation and workspace controls. The builder posts its state as JSON (a transport only); everything is validated by the same service the API uses. Every mutation is POST
/// (antiforgery) behind its own permission; ids of other workspaces answer 404.
/// </summary>
[Authorize(Policy = PermissionCodes.WorkflowsView)]
public class WorkflowsController(IWorkflowService workflows, IWorkflowExecutionService executions, IWorkflowSimulationService simulation, IWorkflowSettingsService settings, ILeadService leads, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(WorkflowQuery query, CancellationToken ct)
    {
        Crumbs(("Automation", null), ("Workflows", null));
        var qv = new Dictionary<string, string?> { ["search"] = query.Search, ["status"] = query.Status?.ToString() };
        return View(new WorkflowListViewModel { Result = await workflows.ListAsync(query, ct), Query = query, QueryValues = qv, Settings = await settings.GetAsync(ct), CanCreate = await permissions.HasAsync(PermissionCodes.WorkflowsCreate, ct) });
    }

    // ---------------------------------------------------------------- builder

    [HttpGet, Authorize(Policy = PermissionCodes.WorkflowsCreate)]
    public async Task<IActionResult> New(CancellationToken ct)
    {
        Crumbs(("Automation", null), ("Workflows", Url.Action(nameof(Index))), ("New workflow", null));
        return View("Builder", new WorkflowBuilderViewModel { Catalog = await workflows.CatalogAsync(ct) });
    }

    [HttpGet, Authorize(Policy = PermissionCodes.WorkflowsUpdate)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var w = await workflows.GetAsync(id, ct);
        if (Reject(w) is { } rej) return rej;
        if (w.Value.Draft is null) { Failure("There is no draft to edit. Start a new draft from the version you want to change."); return RedirectToAction(nameof(Details), new { id }); }
        Crumbs(("Automation", null), ("Workflows", Url.Action(nameof(Index))), (w.Value.Name, Url.Action(nameof(Details), new { id })), ($"Edit draft v{w.Value.Draft.Number}", null));
        return View("Builder", new WorkflowBuilderViewModel
        {
            Id = id, VersionNumber = w.Value.Draft.Number, Name = w.Value.Name, Description = w.Value.Description, Catalog = await workflows.CatalogAsync(ct),
            DefinitionJson = WorkflowJson.ForScript(ToDefinition(w.Value.Draft)), Report = w.Value.Draft.Validation, ActiveExists = w.Value.Active is not null,
        });
    }

    internal static WorkflowDefinition ToDefinition(WorkflowVersionDto v) => new(
        v.Triggers.Select(t => new TriggerDefinition(t.EventType, Elem(t.Condition), t.InactiveDays)).ToList(),
        v.Steps.Select(s => new StepDefinition(s.Key, s.Type, s.Name, Elem(s.Config), Elem(s.Condition), s.OnFailure, s.MaxRetries)).ToList(), v.ReEntry, v.CooldownHours, v.MaxStartsPerLeadPer30Days, v.Note);

    private static System.Text.Json.JsonElement? Elem(object? o) => o is System.Text.Json.JsonElement e ? e : null;

    [HttpPost, Authorize(Policy = PermissionCodes.WorkflowsUpdate)]
    public async Task<IActionResult> Save(WorkflowBuilderViewModel m, CancellationToken ct)
    {
        var (def, error) = WorkflowJson.Parse(m.DefinitionJson);
        if (error is not null) ModelState.AddModelError(string.Empty, error);
        if (!ModelState.IsValid || def is null) return await Redisplay(m, ct);

        if (m.Id is { } id)
        {
            var details = await workflows.UpdateAsync(id, new UpdateWorkflowRequest(m.Name, m.Description), ct);
            if (Reject(details) is { } rej) return rej;
            if (details.IsFailure) { AddError(details); return await Redisplay(m, ct); }
            var saved = await workflows.SaveDraftAsync(id, new SaveDraftRequest(def), ct);
            if (Reject(saved) is { } rej2) return rej2;
            if (saved.IsFailure) { AddError(saved); return await Redisplay(m, ct); }
            Success($"Draft v{saved.Value.Number} saved. It does not run until you activate it.");
            return RedirectToAction(nameof(Details), new { id });
        }
        if (!await permissions.HasAsync(PermissionCodes.WorkflowsCreate, ct)) return Forbid();
        var created = await workflows.CreateAsync(new CreateWorkflowRequest(m.Name, m.Description, def), ct);
        if (created.IsFailure) { AddError(created); return await Redisplay(m, ct); }
        Success("Workflow created as a draft. Check it with the simulation, then activate it.");
        return RedirectToAction(nameof(Details), new { id = created.Value.Id });
    }

    private async Task<IActionResult> Redisplay(WorkflowBuilderViewModel m, CancellationToken ct)
    {
        m.Catalog = await workflows.CatalogAsync(ct);
        Crumbs(("Automation", null), ("Workflows", Url.Action(nameof(Index))), (m.Id is null ? "New workflow" : "Edit draft", null));
        return View("Builder", m);
    }

    /// <summary>"Check workflow": the same validation as saving, shown next to the builder without saving.</summary>
    [HttpPost]
    public async Task<IActionResult> Validate([FromForm] string definitionJson, CancellationToken ct)
    {
        var (def, error) = WorkflowJson.Parse(definitionJson);
        if (def is null) return Json(new { errors = new[] { new { path = "", message = error } }, warnings = Array.Empty<object>() });
        var r = await workflows.ValidateAsync(def, ct);
        if (r.IsFailure) return Json(new { errors = new[] { new { path = "", message = r.Error.Message } }, warnings = Array.Empty<object>() });
        return Json(new { errors = r.Value.Errors, warnings = r.Value.Warnings });
    }

    // ---------------------------------------------------------------- details, versions

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var w = await workflows.GetAsync(id, ct);
        if (Reject(w) is { } rej) return rej;
        Crumbs(("Automation", null), ("Workflows", Url.Action(nameof(Index))), (w.Value.Name, null));
        return View(new WorkflowDetailsViewModel
        {
            Workflow = w.Value, CanUpdate = await permissions.HasAsync(PermissionCodes.WorkflowsUpdate, ct), CanActivate = await permissions.HasAsync(PermissionCodes.WorkflowsActivate, ct),
            CanExecute = await permissions.HasAsync(PermissionCodes.WorkflowsExecute, ct), CanDelete = await permissions.HasAsync(PermissionCodes.WorkflowsDelete, ct),
            Recent = await executions.ListAsync(new ExecutionQuery { WorkflowId = id, PageSize = 8 }, ct),
        });
    }

    [HttpGet]
    public async Task<IActionResult> Version(Guid id, int number, CancellationToken ct)
    {
        var w = await workflows.GetAsync(id, ct); if (Reject(w) is { } rej) return rej;
        var v = await workflows.GetVersionAsync(id, number, ct); if (Reject(v) is { } rej2) return rej2;
        Crumbs(("Automation", null), ("Workflows", Url.Action(nameof(Index))), (w.Value.Name, Url.Action(nameof(Details), new { id })), ($"Version {number}", null));
        return View(new WorkflowVersionViewModel { Workflow = w.Value, Version = v.Value, Catalog = await workflows.CatalogAsync(ct), CanUpdate = await permissions.HasAsync(PermissionCodes.WorkflowsUpdate, ct), CanActivate = await permissions.HasAsync(PermissionCodes.WorkflowsActivate, ct) });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.WorkflowsUpdate)]
    public async Task<IActionResult> NewDraft(Guid id, int number, CancellationToken ct)
    {
        var r = await workflows.NewDraftFromAsync(id, number, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) { Failure(r.Error.Message); return RedirectToAction(nameof(Details), new { id }); }
        Success($"Draft v{r.Value.Number} created from version {number}. Version {number} stays as it is.");
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.WorkflowsActivate)]
    public async Task<IActionResult> Activate(Guid id, int number, CancellationToken ct)
    {
        var r = await workflows.ActivateAsync(id, number, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success($"Version {number} is active. New events now start it; runs in progress finish on their own version."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.WorkflowsActivate)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var r = await workflows.DeactivateAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Workflow deactivated. Nothing new starts; runs in progress finish (cancel them under Runs, or use the kill switch)."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.WorkflowsActivate)]
    public async Task<IActionResult> KillSwitch(Guid id, bool on, string? reason, CancellationToken ct)
    {
        var r = await workflows.SetKillSwitchAsync(id, on, reason, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success(on ? "Kill switch ON: nothing starts and running runs stop before their next step." : "Kill switch off."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.WorkflowsDelete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var r = await workflows.DeleteAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) { Success("Workflow deleted. Its run history stays."); return RedirectToAction(nameof(Index)); }
        Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---------------------------------------------------------------- simulation and manual runs

    [HttpGet, Authorize(Policy = PermissionCodes.WorkflowsExecute)]
    public async Task<IActionResult> Simulate(Guid id, Guid? leadId, CancellationToken ct)
    {
        var w = await workflows.GetAsync(id, ct); if (Reject(w) is { } rej) return rej;
        Crumbs(("Automation", null), ("Workflows", Url.Action(nameof(Index))), (w.Value.Name, Url.Action(nameof(Details), new { id })), ("Simulate", null));
        var vm = new WorkflowSimulateViewModel { Workflow = w.Value, CanRun = w.Value.Status == WorkflowStatus.Active };
        if (leadId is { } l) { var lead = await leads.GetAsync(l, ct); if (lead.IsSuccess) { vm.LeadId = l; vm.LeadName = lead.Value.Name; } }
        return View(vm);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.WorkflowsExecute)]
    public async Task<IActionResult> Simulate(Guid id, Guid? leadId, string? messageText, string? triggerKey, CancellationToken ct)
    {
        var w = await workflows.GetAsync(id, ct); if (Reject(w) is { } rej) return rej;
        Crumbs(("Automation", null), ("Workflows", Url.Action(nameof(Index))), (w.Value.Name, Url.Action(nameof(Details), new { id })), ("Simulate", null));
        var vm = new WorkflowSimulateViewModel { Workflow = w.Value, LeadId = leadId, MessageText = messageText, TriggerKey = triggerKey, CanRun = w.Value.Status == WorkflowStatus.Active };
        if (leadId is null) { ModelState.AddModelError(string.Empty, "Choose a lead."); return View(vm); }
        var lead = await leads.GetAsync(leadId.Value, ct);
        if (lead.IsFailure) return NotFound();
        vm.LeadName = lead.Value.Name;
        var r = await simulation.SimulateAsync(id, new SimulateRequest(leadId.Value, null, string.IsNullOrWhiteSpace(triggerKey) ? null : triggerKey, messageText), ct);
        if (r.IsFailure) { if (Reject(r) is { } rej2) return rej2; AddError(r); } else vm.Result = r.Value;
        return View(vm);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.WorkflowsExecute)]
    public async Task<IActionResult> Run(Guid id, Guid leadId, CancellationToken ct)
    {
        var r = await executions.StartAsync(id, leadId, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) { Failure(r.Error.Message); return RedirectToAction(nameof(Simulate), new { id, leadId }); }
        Success("The workflow was started for this lead.");
        return RedirectToAction("Details", "WorkflowExecutions", new { id = r.Value.Summary.Id });
    }

    /// <summary>Lead picker of the simulation and run forms (also needs Leads.View: it reads lead data).</summary>
    [HttpGet, Authorize(Policy = PermissionCodes.WorkflowsExecute), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IActionResult> FindLeads(string? q, CancellationToken ct)
    {
        var page = await leads.ListAsync(new LeadQuery { Search = q, PageSize = 10 }, ct);
        return Json(page.Items.Select(l => new { id = l.Id, name = l.Name, city = l.City, stage = l.Stage }));
    }

    // ---------------------------------------------------------------- workspace controls

    [HttpGet]
    public async Task<IActionResult> Settings(CancellationToken ct)
    {
        Crumbs(("Automation", null), ("Workflows", Url.Action(nameof(Index))), ("Limits and kill switch", null));
        var s = await settings.GetAsync(ct);
        return View(new WorkflowSettingsViewModel { Settings = s, CanActivate = await permissions.HasAsync(PermissionCodes.WorkflowsActivate, ct),
            Form = new WorkflowSettingsFormModel { MaxConcurrentExecutions = s.MaxConcurrentExecutions, MonthlyExecutionLimit = s.MonthlyExecutionLimit, DailyMessageLimit = s.DailyMessageLimit, ExpectedVersion = s.Version } });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.WorkflowsActivate)]
    public async Task<IActionResult> SaveLimits(WorkflowSettingsFormModel m, CancellationToken ct)
    {
        if (!ModelState.IsValid) { Failure("Check the numbers: concurrent runs 1-500, monthly runs 0-1,000,000, daily messages 0-10,000."); return RedirectToAction(nameof(Settings)); }
        var r = await settings.SaveLimitsAsync(new SaveWorkflowSettingsRequest(m.MaxConcurrentExecutions, m.MonthlyExecutionLimit, m.DailyMessageLimit, m.ExpectedVersion), ct);
        if (r.IsSuccess) Success("Limits saved."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Settings));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.WorkflowsActivate)]
    public async Task<IActionResult> TenantKillSwitch(bool on, string? reason, CancellationToken ct)
    {
        var r = await settings.SetKillSwitchAsync(on, reason, ct);
        if (r.IsSuccess) Success(on ? "Workspace kill switch ON: no workflow starts and running ones stop before their next step." : "Workspace kill switch off."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Settings));
    }
}

[Authorize(Policy = PermissionCodes.WorkflowsView)]
public class WorkflowExecutionsController(IWorkflowExecutionService executions, IWorkflowService workflows, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(ExecutionQuery query, CancellationToken ct)
    {
        Crumbs(("Automation", null), ("Runs", null));
        var qv = new Dictionary<string, string?> { ["workflowId"] = query.WorkflowId?.ToString(), ["status"] = query.Status?.ToString(), ["leadId"] = query.LeadId?.ToString() };
        return View(new ExecutionListViewModel { Result = await executions.ListAsync(query, ct), Query = query, QueryValues = qv, Workflows = (await workflows.ListAsync(new WorkflowQuery { PageSize = 100 }, ct)).Items });
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var e = await executions.GetAsync(id, ct);
        if (Reject(e) is { } rej) return rej;
        Crumbs(("Automation", null), ("Runs", Url.Action(nameof(Index))), ($"{e.Value.Summary.WorkflowName} · {e.Value.Summary.LeadName}", null));
        return View(new ExecutionDetailsViewModel { Execution = e.Value, CanExecute = await permissions.HasAsync(PermissionCodes.WorkflowsExecute, ct) });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.WorkflowsExecute)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        var r = await executions.RetryAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("The run continues from the failed step. Steps that already ran are not repeated."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.WorkflowsExecute)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var r = await executions.CancelAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("The run was cancelled. Steps that already ran are not undone."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }
}

[Authorize(Policy = PermissionCodes.WorkflowsView)]
public class WorkflowNotificationsController(IWorkflowNotificationService notifications) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("Automation", null), ("My notices", null));
        return View(await notifications.MineAsync(false, ct));
    }

    [HttpPost]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        var r = await notifications.MarkReadAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        return RedirectToAction(nameof(Index));
    }
}
