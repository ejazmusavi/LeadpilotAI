using LeadPilot.Application.Common;
using LeadPilot.Application.Discovery;
using LeadPilot.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Api.Controllers;

/// <summary>Discovery searches of the signed-in tenant. Searches run in the background; poll the job for progress. Other tenants' ids answer 404.</summary>
[Route("api/v1/discovery/jobs")]
public class DiscoveryJobsController(IDiscoveryJobService jobs, IDiscoveryResultService results) : ApiControllerBase
{
    /// <summary>Lists searches, newest first.</summary>
    [HttpGet, Authorize(Policy = PermissionCodes.DiscoveryView)]
    public async Task<PagedResult<DiscoveryJobDto>> List([FromQuery] DiscoveryJobQuery query, CancellationToken ct) => await jobs.ListAsync(query, ct);

    /// <summary>One search with progress, counts, usage and (when failed) the error.</summary>
    [HttpGet("{id:guid}"), Authorize(Policy = PermissionCodes.DiscoveryView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await jobs.GetAsync(id, ct));

    /// <summary>Starts a search (location + category/keywords + connector). 202: it is queued and processed by the worker. 409 when the connector is not configured, 429 when the monthly limit is used up.</summary>
    [HttpPost, Authorize(Policy = PermissionCodes.DiscoveryRun)]
    public async Task<IActionResult> Create(CreateDiscoveryJobRequest request, CancellationToken ct)
    {
        var r = await jobs.CreateAsync(request, ct);
        return r.IsSuccess ? AcceptedAtAction(nameof(Get), new { id = r.Value.Id }, r.Value) : Fail(r);
    }

    /// <summary>Cancels a queued search immediately, or asks a running one to stop after the current page.</summary>
    [HttpPost("{id:guid}/cancel"), Authorize(Policy = PermissionCodes.DiscoveryRun)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) => Respond(await jobs.CancelAsync(id, ct));

    /// <summary>Retries a failed or cancelled search. Idempotent: it resumes from its checkpoint and never duplicates stored results.</summary>
    [HttpPost("{id:guid}/retry"), Authorize(Policy = PermissionCodes.DiscoveryRun)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct) => Respond(await jobs.RetryAsync(id, ct));

    /// <summary>Starts a new search with the same parameters.</summary>
    [HttpPost("{id:guid}/rerun"), Authorize(Policy = PermissionCodes.DiscoveryRun)]
    public async Task<IActionResult> Rerun(Guid id, CancellationToken ct) => Respond(await jobs.RerunAsync(id, ct));

    /// <summary>Results of one search.</summary>
    [HttpGet("{id:guid}/results"), Authorize(Policy = PermissionCodes.DiscoveryView)]
    public async Task<IActionResult> Results(Guid id, [FromQuery] DiscoveryResultQuery query, CancellationToken ct)
    {
        var job = await jobs.GetAsync(id, ct);
        if (job.IsFailure) return Fail(job);
        query.JobId = id;
        return Ok(await results.ListAsync(query, ct));
    }

    /// <summary>Distinct past searches with how often they ran ("run again" uses the rerun endpoint).</summary>
    [HttpGet("history"), Authorize(Policy = PermissionCodes.DiscoveryView)]
    public async Task<PagedResult<SearchHistoryItemDto>> History(int page = 1, int pageSize = 25, CancellationToken ct = default) => await jobs.HistoryAsync(page, pageSize, ct);
}

[Route("api/v1/discovery/results")]
public class DiscoveryResultsController(IDiscoveryResultService results) : ApiControllerBase
{
    /// <summary>Filter by status (default Pending), search, minimum confidence, website/phone present and possible duplicates.</summary>
    [HttpGet, Authorize(Policy = PermissionCodes.DiscoveryView)]
    public async Task<PagedResult<DiscoveryResultDto>> List([FromQuery] DiscoveryResultQuery query, CancellationToken ct) => await results.ListAsync(query, ct);

    [HttpGet("{id:guid}"), Authorize(Policy = PermissionCodes.DiscoveryView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await results.GetAsync(id, ct));

    /// <summary>Approves a result: creates a lead (or merges into the duplicate it matches) with its source and public contacts. Contacts get NO consent.</summary>
    [HttpPost("{id:guid}/approve"), Authorize(Policy = PermissionCodes.DiscoveryReview)]
    public async Task<IActionResult> Approve(Guid id, ApproveResultRequest request, CancellationToken ct) => Respond(await results.ApproveAsync(id, request, ct));

    [HttpPost("{id:guid}/reject"), Authorize(Policy = PermissionCodes.DiscoveryReview)]
    public async Task<IActionResult> Reject(Guid id, RejectResultRequest request, CancellationToken ct) => Respond(await results.RejectAsync(id, request, ct));

    /// <summary>Approves up to 100 results; each item succeeds or fails on its own.</summary>
    [HttpPost("bulk-approve"), Authorize(Policy = PermissionCodes.DiscoveryReview)]
    public async Task<BulkReviewResult> BulkApprove(BulkReviewRequest request, CancellationToken ct) => await results.BulkApproveAsync(request, ct);

    [HttpPost("bulk-reject"), Authorize(Policy = PermissionCodes.DiscoveryReview)]
    public async Task<BulkReviewResult> BulkReject(BulkReviewRequest request, CancellationToken ct) => await results.BulkRejectAsync(request, ct);
}

[Route("api/v1/discovery/connectors")]
public class DiscoveryConnectorsController(IDiscoveryConnectorService connectors) : ApiControllerBase
{
    /// <summary>Available connectors with their readiness and this month's usage. Keys are never returned, only a hint (last 4 characters).</summary>
    [HttpGet, Authorize(Policy = PermissionCodes.DiscoveryView)]
    public async Task<IReadOnlyList<ConnectorStatusDto>> List(CancellationToken ct) => await connectors.ListAsync(ct);

    /// <summary>Stores/removes the API key, enables/disables the connector and sets the monthly call/budget limits.</summary>
    [HttpPut("{type}"), Authorize(Policy = PermissionCodes.IntegrationsManage)]
    public async Task<IActionResult> Save(string type, SaveConnectorSettingsRequest request, CancellationToken ct) => Respond(await connectors.SaveSettingsAsync(type, request, ct));

    /// <summary>Usage ledger: calls and estimated cost per call (estimates, not billing data).</summary>
    [HttpGet("usage"), Authorize(Policy = PermissionCodes.IntegrationsManage)]
    public async Task<UsageSummaryDto> Usage(int page = 1, int pageSize = 25, CancellationToken ct = default) => await connectors.UsageAsync(page, pageSize, ct);
}

[Route("api/v1/discovery")]
public class DiscoveryCatalogController(IDiscoveryCatalogService catalog) : ApiControllerBase
{
    [HttpGet("categories"), Authorize(Policy = PermissionCodes.DiscoveryView)]
    public async Task<IReadOnlyList<CategoryMappingDto>> Categories(CancellationToken ct) => await catalog.ListMappingsAsync(ct);

    /// <summary>Sets the search phrases (and optional provider place type) of one of your lead categories.</summary>
    [HttpPut("categories"), Authorize(Policy = PermissionCodes.DiscoveryManage)]
    public async Task<IActionResult> SaveCategory(SaveCategoryMappingRequest request, CancellationToken ct) => Respond(await catalog.SaveMappingAsync(request, ct));

    [HttpPut("categories/{id:guid}/active"), Authorize(Policy = PermissionCodes.DiscoveryManage)]
    public async Task<IActionResult> SetCategoryActive(Guid id, [FromQuery] bool active, CancellationToken ct) => Respond(await catalog.SetMappingActiveAsync(id, active, ct));

    [HttpGet("locations"), Authorize(Policy = PermissionCodes.DiscoveryView)]
    public async Task<IReadOnlyList<LocationPresetDto>> Locations(CancellationToken ct) => await catalog.ListLocationsAsync(ct);

    [HttpPost("locations"), Authorize(Policy = PermissionCodes.DiscoveryManage)]
    public async Task<IActionResult> AddLocation(SaveLocationPresetRequest request, CancellationToken ct) => Respond(await catalog.AddLocationAsync(request, ct));

    [HttpDelete("locations/{id:guid}"), Authorize(Policy = PermissionCodes.DiscoveryManage)]
    public async Task<IActionResult> DeleteLocation(Guid id, CancellationToken ct) => Respond(await catalog.DeleteLocationAsync(id, ct));

    [HttpGet("starter-packs"), Authorize(Policy = PermissionCodes.DiscoveryView)]
    public async Task<IReadOnlyList<StarterPackDto>> Packs(CancellationToken ct) => await catalog.ListPacksAsync(ct);

    /// <summary>Adds the pack's categories, search phrases and locations. Idempotent; never overwrites your edits.</summary>
    [HttpPost("starter-packs/{key}/install"), Authorize(Policy = PermissionCodes.DiscoveryManage)]
    public async Task<IActionResult> InstallPack(string key, CancellationToken ct) => Respond(await catalog.InstallPackAsync(key, ct));
}
