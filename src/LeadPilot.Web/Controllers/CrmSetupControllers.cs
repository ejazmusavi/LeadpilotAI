using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Crm;
using LeadPilot.Shared.Results;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LeadPilot.Web.Controllers;

[Authorize(Policy = PermissionCodes.LeadsImport)]
public class ImportsController(ILeadImportService imports, ILeadService leads, IPermissionEvaluator permissions, IOptions<CrmOptions> options) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, CancellationToken ct = default)
    {
        Crumbs(("Leads", Url.Action("Index", "Leads")), ("Imports", null));
        return View(new ImportListViewModel { Result = await imports.ListAsync(page, 25, ct), MaxRows = options.Value.MaxImportRows, MaxBytes = options.Value.MaxImportFileBytes });
    }

    [HttpGet]
    public IActionResult New()
    {
        Crumbs(("Leads", Url.Action("Index", "Leads")), ("Imports", Url.Action(nameof(Index))), ("New import", null));
        return View(new ImportListViewModel { Result = new PagedResult<ImportJobDto>([], 1, 25, 0), MaxRows = options.Value.MaxImportRows, MaxBytes = options.Value.MaxImportFileBytes });
    }

    [HttpPost, RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) { Failure("Choose a .csv or .xlsx file."); return RedirectToAction(nameof(New)); }
        await using var stream = file.OpenReadStream();
        var result = await imports.UploadAsync(new UploadedFile(file.FileName, file.Length, stream), ct);
        if (result.IsFailure)
        {
            if (Reject(result) is { } r) return r;
            Failure(result.Error.Message);
            return RedirectToAction(nameof(New));
        }
        return RedirectToAction(nameof(Map), new { id = result.Value.Job.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Map(Guid id, CancellationToken ct)
    {
        var preview = await imports.PreviewAsync(id, ct);
        if (Reject(preview) is { } r) return r;
        if (preview.IsFailure) { Failure(preview.Error.Message); return RedirectToAction(nameof(Result), new { id }); }
        Crumbs(("Leads", Url.Action("Index", "Leads")), ("Imports", Url.Action(nameof(Index))), ("Map columns", null));
        return View(new ImportMapViewModel { Preview = preview.Value, Lookups = await leads.LookupsAsync(ct), CanAssign = await permissions.HasAsync(PermissionCodes.LeadsAssign, ct), MaxRows = options.Value.MaxImportRows });
    }

    [HttpPost]
    public async Task<IActionResult> Run(Guid id, List<string?> fields, DuplicatePolicy duplicatePolicy, bool createMissingCategories, Guid? defaultCategoryId, Guid? defaultOwnerUserId, string? sourceName, CancellationToken ct)
    {
        var mapping = fields.Select((f, i) => new ColumnMapping(i, Enum.TryParse<ImportField>(f, out var parsed) && Enum.IsDefined(parsed) ? parsed : null)).ToList();
        var result = await imports.RunAsync(id, new RunImportRequest(mapping, duplicatePolicy, createMissingCategories, defaultCategoryId, defaultOwnerUserId, sourceName), ct);
        if (result.IsFailure)
        {
            if (Reject(result) is { } r) return r;
            Failure(result.Error.Message);
            return result.Error.Code == "conflict" ? RedirectToAction(nameof(Result), new { id }) : RedirectToAction(nameof(Map), new { id });
        }
        Success("Import finished.");
        return RedirectToAction(nameof(Result), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Result(Guid id, ImportErrorKind? kind, int page = 1, CancellationToken ct = default)
    {
        var job = await imports.GetAsync(id, ct);
        if (Reject(job) is { } r) return r;
        var errors = await imports.ErrorsAsync(id, kind, page, 50, ct);
        Crumbs(("Leads", Url.Action("Index", "Leads")), ("Imports", Url.Action(nameof(Index))), (job.Value.FileName, null));
        return View(new ImportResultViewModel { Job = job.Value, Errors = errors.Value, Kind = kind });
    }

    [HttpGet]
    public async Task<IActionResult> ErrorReport(Guid id, CancellationToken ct)
    {
        var result = await imports.ErrorReportAsync(id, ct);
        if (Reject(result) is { } r) return r;
        return File(result.Value.Content, result.Value.ContentType, result.Value.FileName);
    }

    [HttpPost]
    public async Task<IActionResult> ResolveDuplicate(Guid id, Guid errorId, bool merge, CancellationToken ct)
    {
        var result = await imports.ResolveDuplicateAsync(id, errorId, new ResolveDuplicateRequest(merge), ct);
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success(merge ? "Merged into the existing lead." : "Row skipped."); else Failure(result.Error.Message);
        return RedirectToAction(nameof(Result), new { id, kind = ImportErrorKind.Duplicate });
    }
}

[Authorize(Policy = PermissionCodes.SuppressionManage)]
public class SuppressionController(ISuppressionService suppression) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] SuppressionQuery query, CancellationToken ct)
    {
        Crumbs(("Suppression list", null));
        return View(new SuppressionViewModel { Query = query, Result = await suppression.ListAsync(query, ct) });
    }

    [HttpPost]
    public async Task<IActionResult> Add([Bind(Prefix = "Form")] SuppressionFormModel form, CancellationToken ct)
    {
        if (!ModelState.IsValid) { Failure("Enter a phone number or e-mail address."); return RedirectToAction(nameof(Index)); }
        var result = await suppression.AddAsync(new AddSuppressionRequest(form.Channel, form.Value, form.Reason, form.Note), ct);
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success("Added to the suppression list. This value can no longer be contacted."); else Failure(result.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var result = await suppression.RemoveAsync(id, ct);
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success("Removed from the suppression list."); else Failure(result.Error.Message);
        return RedirectToAction(nameof(Index));
    }
}

[Authorize(Policy = PermissionCodes.ScoringManage)]
public class ScoringController(IScoringService scoring) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("Scoring rules", null));
        return View(new ScoringViewModel { Active = await scoring.GetActiveAsync(ct), Versions = await scoring.ListVersionsAsync(ct) });
    }

    /// <summary>Every edit publishes a new immutable version (copy of the active rules plus the change). Scores are recalculated only on request.</summary>
    private async Task<IActionResult> Republish(Func<List<ScoringRuleInput>, bool> change, string note, string success, CancellationToken ct)
    {
        var active = await scoring.GetActiveAsync(ct);
        var rules = active.Rules.Select(r => new ScoringRuleInput(r.Dimension, r.Target, r.Points, r.Description, r.IsEnabled)).ToList();
        if (!change(rules)) return NotFound();
        var result = await scoring.PublishAsync(new PublishScoringRulesRequest(rules, note), ct);
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success($"{success} Published as version {result.Value.Version}. Recalculate scores to apply it to existing leads."); else Failure(result.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> AddRule([Bind(Prefix = "Form")] ScoringRuleFormModel form, CancellationToken ct)
    {
        if (!ModelState.IsValid) { Failure("Fill in the target and points (-100 to 100)."); return RedirectToAction(nameof(Index)); }
        return await Republish(rules => { rules.Add(new ScoringRuleInput(form.Dimension, form.Target, form.Points, form.Description, true)); return true; }, $"Added {form.Dimension} rule", "Rule added.", ct);
    }

    [HttpPost]
    public async Task<IActionResult> ToggleRule(Guid ruleId, CancellationToken ct)
    {
        var active = await scoring.GetActiveAsync(ct);
        var index = active.Rules.ToList().FindIndex(r => r.Id == ruleId);
        if (index < 0) return NotFound();
        return await Republish(rules => { var r = rules[index]; rules[index] = r with { IsEnabled = !r.IsEnabled }; return true; }, "Toggled a rule", "Rule updated.", ct);
    }

    [HttpPost]
    public async Task<IActionResult> DeleteRule(Guid ruleId, CancellationToken ct)
    {
        var active = await scoring.GetActiveAsync(ct);
        var index = active.Rules.ToList().FindIndex(r => r.Id == ruleId);
        if (index < 0) return NotFound();
        return await Republish(rules => { rules.RemoveAt(index); return true; }, "Removed a rule", "Rule removed.", ct);
    }

    [HttpPost]
    public async Task<IActionResult> Rescore(CancellationToken ct)
    {
        var r = await scoring.RescoreAllAsync(ct);
        Success($"Scores recalculated with rule version {r.RuleVersion}: {r.Processed} lead(s) checked, {r.Changed} changed.");
        return RedirectToAction(nameof(Index));
    }
}

[Authorize(Policy = PermissionCodes.PipelinesManage)]
public class PipelinesController(IPipelineService pipelines) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("Pipelines", null));
        return View(new PipelinesViewModel { Pipelines = await pipelines.ListAsync(ct) });
    }

    private IActionResult Outcome<T>(Result<T> result, string success)
    {
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success(success); else Failure(result.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost] public async Task<IActionResult> Create([Bind(Prefix = "NewPipeline")] PipelineFormModel form, CancellationToken ct) => ModelState.IsValid ? Outcome(await pipelines.CreateAsync(new SavePipelineRequest(form.Name), ct), "Pipeline created with the default stages.") : Invalid();
    [HttpPost] public async Task<IActionResult> Rename(Guid id, PipelineFormModel form, CancellationToken ct) => ModelState.IsValid ? Outcome(await pipelines.RenameAsync(id, new SavePipelineRequest(form.Name), ct), "Pipeline renamed.") : Invalid();
    [HttpPost] public async Task<IActionResult> SetDefault(Guid id, CancellationToken ct) => Outcome(await pipelines.SetDefaultAsync(id, ct), "Default pipeline changed. New leads start in it.");
    [HttpPost] public async Task<IActionResult> SetActive(Guid id, bool active, CancellationToken ct) => Outcome(await pipelines.SetActiveAsync(id, active, ct), active ? "Pipeline activated." : "Pipeline deactivated.");
    [HttpPost] public async Task<IActionResult> AddStage(Guid id, StageFormModel form, CancellationToken ct) => ModelState.IsValid ? Outcome(await pipelines.AddStageAsync(id, new SaveStageRequest(form.Name, form.Kind), ct), "Stage added.") : Invalid();
    [HttpPost] public async Task<IActionResult> UpdateStage(Guid id, Guid stageId, StageFormModel form, CancellationToken ct) => ModelState.IsValid ? Outcome(await pipelines.UpdateStageAsync(id, stageId, new SaveStageRequest(form.Name, form.Kind), ct), "Stage updated.") : Invalid();
    [HttpPost] public async Task<IActionResult> MoveStage(Guid id, Guid stageId, int delta, CancellationToken ct) => Outcome(await pipelines.MoveStageAsync(id, stageId, delta, ct), "Stage moved.");
    [HttpPost] public async Task<IActionResult> DeleteStage(Guid id, Guid stageId, CancellationToken ct) => Outcome(await pipelines.DeleteStageAsync(id, stageId, ct), "Stage deleted.");

    private IActionResult Invalid() { Failure("A name is required (max 100 characters)."); return RedirectToAction(nameof(Index)); }
}

[Authorize(Policy = PermissionCodes.LeadsView)]
public class TagsController(ITagService tags, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("Leads", Url.Action("Index", "Leads")), ("Tags", null));
        var p = await permissions.GetCurrentAsync(ct);
        return View(new TagsViewModel { Tags = await tags.ListAsync(ct), CanUpdate = p.Has(PermissionCodes.LeadsUpdate), CanDelete = p.Has(PermissionCodes.LeadsDelete) });
    }

    private IActionResult Outcome<T>(Result<T> result, string success)
    {
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success(success); else Failure(result.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> Create(string? name, CancellationToken ct) => Outcome(await tags.CreateAsync(new SaveTagRequest(name ?? ""), ct), "Tag created.");

    [HttpPost, Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> Rename(Guid id, string? name, CancellationToken ct) => Outcome(await tags.RenameAsync(id, new SaveTagRequest(name ?? ""), ct), "Tag renamed.");

    [HttpPost, Authorize(Policy = PermissionCodes.LeadsDelete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var result = await tags.DeleteAsync(id, ct);
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success("Tag deleted and removed from all leads."); else Failure(result.Error.Message);
        return RedirectToAction(nameof(Index));
    }
}

[Authorize(Policy = PermissionCodes.PipelinesManage)]
public class CategoriesController(ILeadCatalogService catalog) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("Categories & statuses", null));
        return View(new CategoriesViewModel { Categories = await catalog.ListCategoriesAsync(ct), Statuses = await catalog.ListStatusesAsync(ct) });
    }

    private IActionResult Outcome<T>(Result<T> result, string success)
    {
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success(success); else Failure(result.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost] public async Task<IActionResult> CreateCategory(string? name, CancellationToken ct) => Outcome(await catalog.CreateCategoryAsync(new SaveCategoryRequest(name ?? ""), ct), "Category created.");
    [HttpPost] public async Task<IActionResult> RenameCategory(Guid id, string? name, CancellationToken ct) => Outcome(await catalog.RenameCategoryAsync(id, new SaveCategoryRequest(name ?? ""), ct), "Category renamed.");
    [HttpPost] public async Task<IActionResult> SetCategoryActive(Guid id, bool active, CancellationToken ct) => Outcome(await catalog.SetCategoryActiveAsync(id, active, ct), active ? "Category activated." : "Category deactivated. Existing leads keep it; new leads cannot choose it.");
    [HttpPost] public async Task<IActionResult> CreateStatus(string? name, bool isClosed, CancellationToken ct) => Outcome(await catalog.CreateStatusAsync(new SaveStatusRequest(name ?? "", isClosed), ct), "Status created.");
    [HttpPost] public async Task<IActionResult> UpdateStatus(Guid id, string? name, bool isClosed, CancellationToken ct) => Outcome(await catalog.UpdateStatusAsync(id, new SaveStatusRequest(name ?? "", isClosed), ct), "Status updated.");
    [HttpPost] public async Task<IActionResult> SetStatusActive(Guid id, bool active, CancellationToken ct) => Outcome(await catalog.SetStatusActiveAsync(id, active, ct), active ? "Status activated." : "Status deactivated.");
}
