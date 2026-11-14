using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Common;
using LeadPilot.Application.Discovery;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Discovery;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Discovery.Services;

public sealed class DiscoveryJobService(
    CrmContext crm, IDiscoveryConnectorRegistry registry, DiscoveryUsage usage, DiscoveryCatalogService catalog, IDiscoveryJobScheduler scheduler, IOptions<DiscoveryOptions> options,
    ILogger<DiscoveryJobService> logger) : IDiscoveryJobService
{
    public const string UsageLimitCode = "usage_limit_reached";
    public const string NotConfiguredCode = "connector_not_configured";

    public async Task<PagedResult<DiscoveryJobDto>> ListAsync(DiscoveryJobQuery query, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(query.Page, query.PageSize);
        var q = crm.Db.DiscoveryJobs.AsNoTracking().Where(j => j.TenantId == tenantId);
        if (query.Status is { } st) q = q.Where(j => j.Status == st);
        if (!string.IsNullOrWhiteSpace(query.ConnectorType)) q = q.Where(j => j.ConnectorType == query.ConnectorType);
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(j => j.CreatedAtUtc).ThenByDescending(j => j.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<DiscoveryJobDto>(await ToDtos(rows, ct), page, size, total);
    }

    public async Task<Result<DiscoveryJobDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var job = await crm.Db.DiscoveryJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id && j.TenantId == tenantId, ct);
        return job is null ? Result.Failure<DiscoveryJobDto>(Error.NotFound("Search")) : Result.Success((await ToDtos([job], ct))[0]);
    }

    private async Task<IReadOnlyList<DiscoveryJobDto>> ToDtos(IReadOnlyList<DiscoveryJob> jobs, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var ids = jobs.Select(j => j.Id).ToList(); var catIds = jobs.Where(j => j.CategoryId != null).Select(j => j.CategoryId!.Value).Distinct().ToList();
        var cats = catIds.Count == 0 ? [] : await crm.Db.LeadCategories.AsNoTracking().Where(c => c.TenantId == tenantId && catIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var pending = ids.Count == 0 ? [] : (await crm.Db.DiscoveryResults.AsNoTracking().Where(r => r.TenantId == tenantId && ids.Contains(r.JobId) && r.ReviewStatus == ReviewStatus.Pending)
            .GroupBy(r => r.JobId).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.N);
        return jobs.Select(j => new DiscoveryJobDto(j.Id, j.ConnectorType, registry.All.FirstOrDefault(d => d.Key == j.ConnectorType)?.DisplayName ?? j.ConnectorType, j.Status, j.Country, j.Province, j.City,
            j.District, j.Area, j.CategoryId, j.CategoryId is { } c ? cats.GetValueOrDefault(c) : null, j.Keywords, j.MaxResults, j.EnrichWebsites, j.PagesFetched, j.ResultsFound, j.ResultsNew,
            j.ResultsAlreadyKnown, j.DuplicatesFlagged, j.ApiCalls, j.EstimatedCostUsd, j.Attempts, j.CancelRequested, j.Warnings, j.ErrorCode, j.ErrorMessage, j.ErrorRetryable, Progress(j),
            pending.GetValueOrDefault(j.Id), j.CreatedAtUtc, j.CreatedBy, j.StartedAtUtc, j.CompletedAtUtc)).ToList();
    }

    public static int Progress(DiscoveryJob j) => j.Status switch
    {
        DiscoveryJobStatus.Completed => 100, DiscoveryJobStatus.Queued => 0,
        _ => j.MaxResults <= 0 ? 0 : Math.Min(99, j.ResultsFound * 100 / j.MaxResults),
    };

    // ---------- create ----------
    public Task<Result<DiscoveryJobDto>> CreateAsync(CreateDiscoveryJobRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            await crm.EnsureDefaultsAsync(ct);
            await catalog.EnsureAutoPackAsync(ct);
            var d = options.Value; var tenantId = crm.TenantId;
            var connector = registry.Find(r.ConnectorType ?? "");
            if (connector is null) return Result.Failure<DiscoveryJobDto>(Error.Validation("Unknown connector."));
            var desc = connector.Descriptor;
            if (desc.Kind != ConnectorKind.Search) return Result.Failure<DiscoveryJobDto>(new Error(NotConfiguredCode, desc.Kind == ConnectorKind.InApp ? $"{desc.DisplayName} is not run as a discovery search. Use {desc.InAppPath}." : desc.NotConfiguredReason ?? $"{desc.DisplayName} is not configured."));
            var settings = await usage.SettingsAsync(desc.Key, ct);
            if (!settings.Enabled) return Result.Failure<DiscoveryJobDto>(new Error(NotConfiguredCode, $"{desc.DisplayName} is disabled for this workspace."));
            if (desc.RequiresCredentials && !settings.HasKey) return Result.Failure<DiscoveryJobDto>(new Error(NotConfiguredCode, $"{desc.DisplayName} needs an API key. Add it under Connectors."));

            string? seed = null;
            if (desc.AcceptsSeedUrls)
            {
                var urls = (r.SeedUrls ?? "").Split(['\n', '\r', ' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (urls.Count == 0) return Result.Failure<DiscoveryJobDto>(Error.Validation("Enter at least one website address."));
                if (urls.Count > d.Website.MaxWebsitesPerJob) return Result.Failure<DiscoveryJobDto>(Error.Validation($"At most {d.Website.MaxWebsitesPerJob} websites per search."));
                var bad = urls.FirstOrDefault(u => CandidateNormalizer.NormalizeWebsite(u) is null);
                if (bad is not null) return Result.Failure<DiscoveryJobDto>(Error.Validation($"'{(bad.Length > 60 ? bad[..60] + "…" : bad)}' is not a valid web address."));
                seed = string.Join('\n', urls.Select(u => CandidateNormalizer.NormalizeWebsite(u)!));
            }
            else
            {
                if (string.IsNullOrWhiteSpace(r.Country) && string.IsNullOrWhiteSpace(r.City)) return Result.Failure<DiscoveryJobDto>(Error.Validation("Choose where to search: at least a country or a city."));
                if (r.CategoryId is null && string.IsNullOrWhiteSpace(r.Keywords)) return Result.Failure<DiscoveryJobDto>(Error.Validation("Choose a business category or enter keywords."));
            }
            if (r.CategoryId is { } cid && !await crm.Db.LeadCategories.AnyAsync(c => c.Id == cid && c.TenantId == tenantId && c.IsActive, ct)) return Result.Failure<DiscoveryJobDto>(Error.Validation("Category not found."));
            if (r.MaxResults < 1 || r.MaxResults > d.MaxResultsPerJob) return Result.Failure<DiscoveryJobDto>(Error.Validation($"Maximum results must be between 1 and {d.MaxResultsPerJob}."));

            var decision = await usage.CheckAsync(desc.Key, ct);
            if (!decision.Allowed) return Result.Failure<DiscoveryJobDto>(new Error(UsageLimitCode, decision.Reason!));

            var job = DiscoveryJob.Create(tenantId, desc.Key, r.Country, r.Province, r.City, r.District, r.Area, r.CategoryId, r.Keywords, seed, r.MaxResults, r.EnrichWebsites && !desc.AcceptsSeedUrls, crm.UserId, crm.Actor, crm.Now);
            crm.Db.DiscoveryJobs.Add(job);
            crm.Record("Discovery.JobCreated", "DiscoveryJob", job.Id, $"Search started with {desc.DisplayName}: {DiscoveryQueryBuilder.LocationText(new SearchLocation(r.Country, r.Province, r.City, r.District, r.Area))}.");
            await crm.Db.SaveChangesAsync(ct);
            return await QueueAsync(job, ct);
        }, logger);

    private async Task<Result<DiscoveryJobDto>> QueueAsync(DiscoveryJob job, CancellationToken ct)
    {
        try
        {
            job.SetHangfireId(scheduler.Enqueue(job.TenantId, job.Id));
            await crm.Db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DbUpdateException)
        {
            logger.LogError(ex, "Discovery job {JobId} could not be queued", job.Id);
            job.Fail("queue_unavailable", "The background queue is not reachable. Try again in a moment.", retryable: true, crm.Now);
            await crm.Db.SaveChangesAsync(ct);
            return Result.Failure<DiscoveryJobDto>(Error.Conflict("The search was saved but could not be queued (background queue unavailable). Open it and use Retry once the queue is back."));
        }
        return Result.Success((await ToDtos([job], ct))[0]);
    }

    // ---------- cancel / retry / rerun ----------
    public Task<Result<DiscoveryJobDto>> CancelAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var job = await crm.Db.DiscoveryJobs.FirstOrDefaultAsync(j => j.Id == id && j.TenantId == tenantId, ct);
            if (job is null) return Result.Failure<DiscoveryJobDto>(Error.NotFound("Search"));
            var wasQueued = job.Status == DiscoveryJobStatus.Queued;
            job.RequestCancel(crm.Now);
            crm.Record("Discovery.JobCancelRequested", "DiscoveryJob", id, wasQueued ? "Queued search cancelled." : "Cancellation requested for a running search.");
            await crm.Db.SaveChangesAsync(ct);
            if (wasQueued) scheduler.TryDelete(job.HangfireJobId);
            return Result.Success((await ToDtos([job], ct))[0]);
        }, logger);

    public Task<Result<DiscoveryJobDto>> RetryAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var job = await crm.Db.DiscoveryJobs.FirstOrDefaultAsync(j => j.Id == id && j.TenantId == tenantId, ct);
            if (job is null) return Result.Failure<DiscoveryJobDto>(Error.NotFound("Search"));
            if (job.Status is not (DiscoveryJobStatus.Failed or DiscoveryJobStatus.Cancelled)) return Result.Failure<DiscoveryJobDto>(Error.Conflict("Only failed or cancelled searches can be retried."));
            var decision = await usage.CheckAsync(job.ConnectorType, ct);
            if (!decision.Allowed) return Result.Failure<DiscoveryJobDto>(new Error(UsageLimitCode, decision.Reason!));
            job.PrepareRetry();
            crm.Record("Discovery.JobRetried", "DiscoveryJob", id, "Search retried (resumes from its checkpoint; stored results are not duplicated).");
            await crm.Db.SaveChangesAsync(ct);
            return await QueueAsync(job, ct);
        }, logger);

    public async Task<Result<DiscoveryJobDto>> RerunAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var j = await crm.Db.DiscoveryJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (j is null) return Result.Failure<DiscoveryJobDto>(Error.NotFound("Search"));
        return await CreateAsync(new CreateDiscoveryJobRequest(j.ConnectorType, j.Country, j.Province, j.City, j.District, j.Area, j.CategoryId, j.Keywords, j.MaxResults, j.EnrichWebsites, j.SeedUrls), ct);
    }

    // ---------- history ----------
    public async Task<PagedResult<SearchHistoryItemDto>> HistoryAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var (p, size) = CrmFlow.Page(page, pageSize);
        var jobs = await crm.Db.DiscoveryJobs.AsNoTracking().Where(j => j.TenantId == tenantId).OrderByDescending(j => j.CreatedAtUtc).Take(1000).ToListAsync(ct);
        var groups = jobs.GroupBy(j => (j.ConnectorType, j.Country, j.Province, j.City, j.District, j.Area, j.CategoryId, Keywords: j.Keywords?.ToLowerInvariant(), Seed: j.SeedUrls is null ? null : j.SeedUrls.GetHashCode().ToString())).ToList();
        var catIds = jobs.Where(j => j.CategoryId != null).Select(j => j.CategoryId!.Value).Distinct().ToList();
        var cats = catIds.Count == 0 ? [] : await crm.Db.LeadCategories.AsNoTracking().Where(c => c.TenantId == tenantId && catIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var items = groups.Select(g =>
        {
            var last = g.First();
            var loc = last.SeedUrls is not null ? $"{last.SeedUrls.Split('\n').Length} website(s)" : DiscoveryQueryBuilder.LocationText(new SearchLocation(last.Country, last.Province, last.City, last.District, last.Area));
            return new SearchHistoryItemDto(last.Id, last.ConnectorType, registry.All.FirstOrDefault(d => d.Key == last.ConnectorType)?.DisplayName ?? last.ConnectorType, loc,
                last.CategoryId is { } c ? cats.GetValueOrDefault(c) : null, last.Keywords, g.Count(), last.CreatedAtUtc, last.Status, g.Sum(x => x.ResultsFound));
        }).ToList();
        return new PagedResult<SearchHistoryItemDto>(items.Skip((p - 1) * size).Take(size).ToList(), p, size, items.Count);
    }
}
