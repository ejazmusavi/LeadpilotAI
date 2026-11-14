using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Discovery;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Discovery;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LeadPilot.Web.Controllers;

[Authorize(Policy = PermissionCodes.DiscoveryView)]
public class DiscoveryController(IDiscoveryJobService jobs, IDiscoveryConnectorService connectors, IDiscoveryCatalogService catalog, ILeadService leads, IPermissionEvaluator permissions, IOptions<DiscoveryOptions> options) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(DiscoveryJobStatus? status, int page = 1, CancellationToken ct = default)
    {
        Crumbs(("Discovery", null));
        var vals = new Dictionary<string, string?>(); if (status is { } s) vals["status"] = s.ToString();
        var list = await jobs.ListAsync(new DiscoveryJobQuery { Status = status, Page = page }, ct);
        return View(new DiscoveryListViewModel
        {
            Result = list, Status = status, QueryValues = vals, CanRun = await permissions.HasAsync(PermissionCodes.DiscoveryRun, ct),
            HasReadyConnector = (await connectors.ListAsync(ct)).Any(c => c.IsReady),
        });
    }

    [HttpGet, Authorize(Policy = PermissionCodes.DiscoveryRun)]
    public async Task<IActionResult> New(Guid? from, CancellationToken ct)
    {
        Crumbs(("Discovery", Url.Action(nameof(Index))), ("New search", null));
        var model = await BuildNewAsync(new NewSearchViewModel(), ct);
        if (from is { } id && (await jobs.GetAsync(id, ct)) is { IsSuccess: true } j)
        { var v = j.Value; model.ConnectorType = v.ConnectorType; model.Country = v.Country; model.Province = v.Province; model.City = v.City; model.District = v.District; model.Area = v.Area; model.CategoryId = v.CategoryId; model.Keywords = v.Keywords; model.MaxResults = v.MaxResults; model.EnrichWebsites = v.EnrichWebsites; }
        return View(model);
    }

    private async Task<NewSearchViewModel> BuildNewAsync(NewSearchViewModel m, CancellationToken ct)
    {
        var lookups = await leads.LookupsAsync(ct);
        m.Connectors = (await connectors.ListAsync(ct)).Where(c => c.Kind == ConnectorKind.Search).ToList();
        m.Mappings = await catalog.ListMappingsAsync(ct);
        m.Categories = (await leads.LookupsAsync(ct)).Categories;
        _ = lookups;
        m.Presets = await catalog.ListLocationsAsync(ct);
        m.CanSetupConnectors = await permissions.HasAsync(PermissionCodes.IntegrationsManage, ct);
        m.MaxResultsCap = options.Value.MaxResultsPerJob;
        m.Categories = (await leads.LookupsAsync(ct)).Categories;
        return m;
    }

    [HttpPost, Authorize(Policy = PermissionCodes.DiscoveryRun)]
    public async Task<IActionResult> Create(NewSearchViewModel m, CancellationToken ct)
    {
        var result = await jobs.CreateAsync(new CreateDiscoveryJobRequest(m.ConnectorType ?? "", Pick(m.Country, 100), Pick(m.Province, 100), Pick(m.City, 100), Pick(m.District, 100), Pick(m.Area, 100),
            m.CategoryId, Pick(m.Keywords, 300), m.MaxResults, m.EnrichWebsites, m.SeedUrls), ct);
        if (result.IsSuccess) { Success("Search queued. It runs in the background; this page shows its progress."); return RedirectToAction(nameof(Details), new { id = result.Value.Id }); }
        if (Reject(result) is { } r) return r;
        AddError(result);
        Crumbs(("Discovery", Url.Action(nameof(Index))), ("New search", null));
        return View(nameof(New), await BuildNewAsync(m, ct));
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var job = await jobs.GetAsync(id, ct);
        if (Reject(job) is { } r) return r;
        Crumbs(("Discovery", Url.Action(nameof(Index))), (job.Value.ConnectorName, null));
        return View(new DiscoveryDetailsViewModel { Job = job.Value, CanRun = await permissions.HasAsync(PermissionCodes.DiscoveryRun, ct), CanReview = await permissions.HasAsync(PermissionCodes.DiscoveryReview, ct) });
    }

    /// <summary>Polled by the details page while a search is queued or running.</summary>
    [HttpGet]
    public async Task<IActionResult> Status(Guid id, CancellationToken ct)
    {
        var job = await jobs.GetAsync(id, ct);
        if (Reject(job) is { } r) return r;
        var j = job.Value;
        return Json(new { status = j.Status.ToString(), progress = j.Progress, found = j.ResultsFound, created = j.ResultsNew, known = j.ResultsAlreadyKnown, duplicates = j.DuplicatesFlagged, pages = j.PagesFetched, calls = j.ApiCalls, pending = j.PendingResults, terminal = j.Status is DiscoveryJobStatus.Completed or DiscoveryJobStatus.Failed or DiscoveryJobStatus.Cancelled });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.DiscoveryRun)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) => await Act(id, await jobs.CancelAsync(id, ct), "Cancellation requested.");

    [HttpPost, Authorize(Policy = PermissionCodes.DiscoveryRun)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct) => await Act(id, await jobs.RetryAsync(id, ct), "Search queued again. Results already stored are not duplicated.");

    [HttpPost, Authorize(Policy = PermissionCodes.DiscoveryRun)]
    public async Task<IActionResult> Rerun(Guid id, CancellationToken ct)
    {
        var r = await jobs.RerunAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) { Failure(r.Error.Message); return RedirectToAction(nameof(Details), new { id }); }
        Success("New search queued.");
        return RedirectToAction(nameof(Details), new { id = r.Value.Id });
    }

    private Task<IActionResult> Act(Guid id, Shared.Results.Result<DiscoveryJobDto> r, string ok)
    {
        IActionResult? rej = Reject(r);
        if (rej is null) { if (r.IsFailure) Failure(r.Error.Message); else Success(ok); rej = RedirectToAction(nameof(Details), new { id }); }
        return Task.FromResult(rej);
    }

    [HttpGet]
    public async Task<IActionResult> History(int page = 1, CancellationToken ct = default)
    {
        Crumbs(("Discovery", Url.Action(nameof(Index))), ("Search history", null));
        return View(new HistoryViewModel { Result = await jobs.HistoryAsync(page, 25, ct), CanRun = await permissions.HasAsync(PermissionCodes.DiscoveryRun, ct) });
    }
}

[Authorize(Policy = PermissionCodes.DiscoveryView)]
public class DiscoveryResultsController(IDiscoveryResultService results, IDiscoveryJobService jobs, ILeadService leads, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] DiscoveryResultQuery query, CancellationToken ct)
    {
        Crumbs(("Discovery", Url.Action("Index", "Discovery")), ("Review results", null));
        var vals = new Dictionary<string, string?>();
        if (query.JobId is { } j) vals["jobId"] = j.ToString();
        vals["status"] = query.Status?.ToString() ?? "";
        if (query.MinConfidence is { } mc) vals["minConfidence"] = mc.ToString();
        if (query.HasWebsite is { } hw) vals["hasWebsite"] = hw.ToString().ToLowerInvariant();
        if (query.HasPhone is { } hp) vals["hasPhone"] = hp.ToString().ToLowerInvariant();
        if (query.PossibleDuplicate is { } pd) vals["possibleDuplicate"] = pd.ToString().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(query.Search)) vals["search"] = query.Search;
        var can = await permissions.HasAsync(PermissionCodes.DiscoveryReview, ct);
        return View(new ResultsViewModel
        {
            Result = await results.ListAsync(query, ct), Query = query, QueryValues = vals, CanReview = can, CanAssign = await permissions.HasAsync(PermissionCodes.LeadsAssign, ct),
            RecentJobs = (await jobs.ListAsync(new DiscoveryJobQuery { PageSize = 50 }, ct)).Items, Owners = can ? await leads.ListOwnersAsync(ct) : [],
        });
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var r = await results.GetAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        Crumbs(("Discovery", Url.Action("Index", "Discovery")), ("Review results", Url.Action(nameof(Index))), (r.Value.Name ?? "Result", null));
        var can = await permissions.HasAsync(PermissionCodes.DiscoveryReview, ct);
        var lookups = await leads.LookupsAsync(ct);
        return View(new ResultDetailsViewModel { Result = r.Value, Categories = lookups.Categories, Owners = can ? lookups.Owners : [], CanReview = can, CanAssign = await permissions.HasAsync(PermissionCodes.LeadsAssign, ct) });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.DiscoveryReview)]
    public async Task<IActionResult> Approve(Guid id, Guid? categoryId, Guid? ownerUserId, DuplicateAction onDuplicate = DuplicateAction.Merge, CancellationToken ct = default)
    {
        var r = await results.ApproveAsync(id, new ApproveResultRequest(categoryId, ownerUserId, onDuplicate), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) { Failure(r.Error.Message); return RedirectToAction(nameof(Details), new { id }); }
        Success(r.Value.Action == ApprovalAction.Created ? "Approved: a new lead was created." : "Approved: merged into the existing lead.");
        return RedirectToAction("Details", "Leads", new { id = r.Value.LeadId });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.DiscoveryReview)]
    public async Task<IActionResult> Reject(Guid id, string? note, string? returnQuery, CancellationToken ct)
    {
        var r = await results.RejectAsync(id, new RejectResultRequest(Pick(note, 500)), ct);
        if (base.Reject(r) is { } rej) return rej;
        if (r.IsFailure) Failure(r.Error.Message); else Success("Result rejected.");
        return BackToList(returnQuery);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.DiscoveryReview)]
    public async Task<IActionResult> BulkApprove(List<Guid>? ids, Guid? ownerUserId, DuplicateAction onDuplicate, string? returnQuery, CancellationToken ct) =>
        BulkDone(await results.BulkApproveAsync(new BulkReviewRequest(ids ?? [], ownerUserId, null, onDuplicate), ct), "approved", returnQuery);

    [HttpPost, Authorize(Policy = PermissionCodes.DiscoveryReview)]
    public async Task<IActionResult> BulkReject(List<Guid>? ids, string? returnQuery, CancellationToken ct) =>
        BulkDone(await results.BulkRejectAsync(new BulkReviewRequest(ids ?? []), ct), "rejected", returnQuery);

    private IActionResult BulkDone(BulkReviewResult r, string verb, string? returnQuery)
    {
        if (r.Requested == 0) Failure("Select at least one result.");
        else if (r.Failed == 0) Success($"{r.Succeeded} result(s) {verb}.");
        else Failure($"{r.Succeeded} result(s) {verb}, {r.Failed} skipped: {string.Join("; ", r.Items.Where(i => !i.Success).Select(i => i.Error).Distinct().Take(3))}");
        return BackToList(returnQuery);
    }

    private IActionResult BackToList(string? returnQuery) =>
        Redirect(Url.Action(nameof(Index)) + (string.IsNullOrEmpty(returnQuery) || !returnQuery.StartsWith('?') || returnQuery.Contains('\n') || returnQuery.Contains('\r') ? "" : returnQuery));
}

[Authorize(Policy = PermissionCodes.DiscoveryView)]
public class DiscoverySetupController(IDiscoveryCatalogService catalog, ILeadService leads, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("Discovery", Url.Action("Index", "Discovery")), ("Categories & locations", null));
        var mappings = await catalog.ListMappingsAsync(ct);
        var mapped = mappings.Select(m => m.CategoryId).ToHashSet();
        return View(new DiscoverySetupViewModel
        {
            Mappings = mappings, Locations = await catalog.ListLocationsAsync(ct), Packs = await catalog.ListPacksAsync(ct), CanManage = await permissions.HasAsync(PermissionCodes.DiscoveryManage, ct),
            UnmappedCategories = (await leads.LookupsAsync(ct)).Categories.Where(c => !mapped.Contains(c.Id)).ToList(),
        });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.DiscoveryManage)]
    public async Task<IActionResult> SaveMapping(Guid categoryId, string? searchTerms, string? placeType, CancellationToken ct)
    {
        var r = await catalog.SaveMappingAsync(new SaveCategoryMappingRequest(categoryId, searchTerms ?? "", Pick(placeType, 60)), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) Failure(r.Error.Message); else Success("Search phrases saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.DiscoveryManage)]
    public async Task<IActionResult> SetMappingActive(Guid id, bool active, CancellationToken ct)
    {
        var r = await catalog.SetMappingActiveAsync(id, active, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) Failure(r.Error.Message); else Success(active ? "Activated." : "Deactivated.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.DiscoveryManage)]
    public async Task<IActionResult> AddLocation(string? country, string? province, string? city, string? district, string? area, CancellationToken ct)
    {
        var r = await catalog.AddLocationAsync(new SaveLocationPresetRequest(country ?? "", Pick(province, 100), Pick(city, 100), Pick(district, 100), Pick(area, 100)), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) Failure(r.Error.Message); else Success("Location saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.DiscoveryManage)]
    public async Task<IActionResult> DeleteLocation(Guid id, CancellationToken ct)
    {
        var r = await catalog.DeleteLocationAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        Success("Location removed.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.DiscoveryManage)]
    public async Task<IActionResult> InstallPack(string key, CancellationToken ct)
    {
        var r = await catalog.InstallPackAsync(key, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) Failure(r.Error.Message);
        else Success($"Starter pack installed: {r.Value.CategoriesCreated} categor{(r.Value.CategoriesCreated == 1 ? "y" : "ies")}, {r.Value.MappingsCreated} search setting(s), {r.Value.LocationsCreated} location(s) added. Existing items were left unchanged.");
        return RedirectToAction(nameof(Index));
    }
}

[Authorize(Policy = PermissionCodes.IntegrationsManage)]
public class ConnectorsController(IDiscoveryConnectorService connectors) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(int page = 1, CancellationToken ct = default)
    {
        Crumbs(("Discovery", Url.Action("Index", "Discovery")), ("Connectors & usage", null));
        var usage = await connectors.UsageAsync(page, 15, ct);
        return View(new ConnectorsViewModel { Usage = usage, All = await connectors.ListAsync(ct), Page = page });
    }

    [HttpPost]
    public async Task<IActionResult> Save(string type, string? apiKey, bool clearApiKey, bool enabled, int? monthlyCallLimit, decimal? monthlyBudgetUsd, decimal? costPerCallUsd, CancellationToken ct)
    {
        var r = await connectors.SaveSettingsAsync(type, new SaveConnectorSettingsRequest(string.IsNullOrWhiteSpace(apiKey) ? null : apiKey, clearApiKey, enabled, monthlyCallLimit, monthlyBudgetUsd, costPerCallUsd, UpdateLimits: true), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) Failure(r.Error.Message); else Success("Connector settings saved.");
        return RedirectToAction(nameof(Index));
    }
}
