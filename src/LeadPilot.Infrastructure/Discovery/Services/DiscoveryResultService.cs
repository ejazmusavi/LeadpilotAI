using System.Text.Json;
using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Discovery;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Discovery;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Discovery.Services;

/// <summary>Review of discovery results. Approval goes through <see cref="ILeadIntakeService"/>: duplicate detection, lead + contacts, source and retrieval time. Nothing reaches the CRM without a human decision.</summary>
public sealed class DiscoveryResultService(CrmContext crm, ILeadIntakeService intake, IOptions<DiscoveryOptions> options, ILogger<DiscoveryResultService> logger) : IDiscoveryResultService
{
    public async Task<PagedResult<DiscoveryResultDto>> ListAsync(DiscoveryResultQuery q, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(q.Page, q.PageSize);
        var query = crm.Db.DiscoveryResults.AsNoTracking().Where(r => r.TenantId == tenantId);
        if (q.JobId is { } j) query = query.Where(r => r.JobId == j);
        if (q.Status is { } s) query = query.Where(r => r.ReviewStatus == s);
        if (q.MinConfidence is { } m) query = query.Where(r => r.Confidence >= m);
        if (q.HasWebsite is { } hw) query = hw ? query.Where(r => r.Website != null) : query.Where(r => r.Website == null);
        if (q.HasPhone is { } hp) query = hp ? query.Where(r => r.PublicPhone != null) : query.Where(r => r.PublicPhone == null);
        if (q.PossibleDuplicate is { } pd) query = pd ? query.Where(r => r.DuplicateLeadId != null) : query.Where(r => r.DuplicateLeadId == null);
        if (!string.IsNullOrWhiteSpace(q.Search)) { var t = q.Search.Trim(); query = query.Where(r => (r.Name != null && r.Name.Contains(t)) || (r.City != null && r.City.Contains(t)) || (r.Address != null && r.Address.Contains(t))); }
        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(r => r.Confidence).ThenBy(r => r.Name).ThenBy(r => r.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<DiscoveryResultDto>(await ToDtos(rows, ct), page, size, total);
    }

    public async Task<Result<DiscoveryResultDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var r = await crm.Db.DiscoveryResults.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        return r is null ? Result.Failure<DiscoveryResultDto>(Error.NotFound("Result")) : Result.Success((await ToDtos([r], ct))[0]);
    }

    private async Task<IReadOnlyList<DiscoveryResultDto>> ToDtos(IReadOnlyList<DiscoveryResult> rows, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var leadIds = rows.Where(r => r.DuplicateLeadId != null).Select(r => r.DuplicateLeadId!.Value).Distinct().ToList();
        var names = leadIds.Count == 0 ? [] : await crm.Db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && leadIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        return rows.Select(r => new DiscoveryResultDto(r.Id, r.JobId, r.ConnectorType, r.ExternalId, r.Name, r.CategoryId, r.CategoryLabel, r.Country, r.Province, r.City, r.District, r.Area, r.Address, r.Website,
            r.PublicPhone, r.PublicEmail, ParseList(r.SocialLinks), r.SourceName, r.SourceUrl, r.Attribution, r.RetrievedAtUtc, r.ExpiresAtUtc, r.ContentPurged, r.LastVerifiedAtUtc, r.Confidence, r.ConfidenceLevel,
            r.ResearchNotes, ParseMap(r.Provenance), r.Enrichment, r.ReviewStatus, r.ReviewedAtUtc, r.ReviewNote, r.DuplicateLeadId, r.DuplicateLeadId is { } d ? names.GetValueOrDefault(d) : null, r.DuplicateReasons, r.LeadId, r.Approval)).ToList();
    }

    private static IReadOnlyList<string> ParseList(string? json) { try { return json is null ? [] : JsonSerializer.Deserialize<List<string>>(json) ?? []; } catch (JsonException) { return []; } }
    private static IReadOnlyDictionary<string, string> ParseMap(string? json) { try { return json is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; } catch (JsonException) { return new Dictionary<string, string>(); } }

    // ---------- approve ----------
    public Task<Result<ApprovalOutcomeDto>> ApproveAsync(Guid id, ApproveResultRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var res = await db.DiscoveryResults.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            if (res is null) return Result.Failure<ApprovalOutcomeDto>(Error.NotFound("Result"));
            if (res.ReviewStatus != ReviewStatus.Pending) return Result.Failure<ApprovalOutcomeDto>(Error.Conflict($"This result was already {res.ReviewStatus.ToString().ToLowerInvariant()}."));
            if (res.ContentPurged || string.IsNullOrWhiteSpace(res.Name)) return Result.Failure<ApprovalOutcomeDto>(Error.Conflict("The provider's retention period for this result has ended and its content was removed. Run the search again."));

            var categoryId = r.CategoryId ?? res.CategoryId;
            if (categoryId is { } cid && !await db.LeadCategories.AnyAsync(c => c.Id == cid && c.TenantId == tenantId && c.IsActive, ct)) return Result.Failure<ApprovalOutcomeDto>(Error.Validation("Category not found."));
            if (r.OwnerUserId is { } owner)
            {
                if (!await crm.Permissions.HasAsync(PermissionCodes.LeadsAssign, ct)) return Result.Failure<ApprovalOutcomeDto>(Error.Forbidden("Assigning leads requires the Assign leads permission."));
                if (!await crm.IsActiveMemberAsync(owner, ct)) return Result.Failure<ApprovalOutcomeDto>(Error.Validation("The owner must be an active member of this workspace."));
            }

            var contacts = new List<IntakeContact>();
            if (res.PublicPhone is not null || res.PublicEmail is not null) contacts.Add(new IntakeContact(res.PublicPhone, res.PublicEmail));
            var result = await intake.IntakeAsync(new LeadIntakeRequest(res.Name!, categoryId, res.Country, res.Province, res.City, res.District, res.Area, res.Address, res.Website, res.Confidence,
                LeadSourceType.Discovery, $"{res.ConnectorType}:{res.ExternalId}", res.SourceName, res.SourceUrl, res.Attribution, contacts, r.OwnerUserId, r.OnDuplicate == DuplicateAction.Merge, res.RetrievedAtUtc), ct);
            if (result.IsFailure) return Result.Failure<ApprovalOutcomeDto>(result.Error);

            var action = result.Value.Action == IntakeAction.Created ? ApprovalAction.Created : ApprovalAction.Merged;
            // the intake saved its own work; now record the decision on the result
            var fresh = await db.DiscoveryResults.FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            fresh.Approve(result.Value.LeadId, action, crm.UserId, crm.Now);
            crm.Record("Discovery.ResultApproved", "DiscoveryResult", id, $"Result '{fresh.Name}' approved ({action.ToString().ToLowerInvariant()} lead).");
            await db.SaveChangesAsync(ct);
            return Result.Success(new ApprovalOutcomeDto(id, result.Value.LeadId, action));
        }, logger);

    // ---------- reject ----------
    public Task<Result<DiscoveryResultDto>> RejectAsync(Guid id, RejectResultRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var res = await crm.Db.DiscoveryResults.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            if (res is null) return Result.Failure<DiscoveryResultDto>(Error.NotFound("Result"));
            if (res.ReviewStatus != ReviewStatus.Pending) return Result.Failure<DiscoveryResultDto>(Error.Conflict($"This result was already {res.ReviewStatus.ToString().ToLowerInvariant()}."));
            res.Reject(r.Note, crm.UserId, crm.Now);
            crm.Record("Discovery.ResultRejected", "DiscoveryResult", id, $"Result '{res.Name}' rejected.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success((await ToDtos([res], ct))[0]);
        }, logger);

    // ---------- bulk ----------
    public async Task<BulkReviewResult> BulkApproveAsync(BulkReviewRequest r, CancellationToken ct = default) =>
        await BulkAsync(r, async id =>
        {
            var x = await ApproveAsync(id, new ApproveResultRequest(null, r.OwnerUserId, r.OnDuplicate), ct);
            return x.IsSuccess ? new BulkReviewItem(id, true, null, x.Value.LeadId, x.Value.Action) : new BulkReviewItem(id, false, x.Error.Message, null, null);
        });

    public async Task<BulkReviewResult> BulkRejectAsync(BulkReviewRequest r, CancellationToken ct = default) =>
        await BulkAsync(r, async id =>
        {
            var x = await RejectAsync(id, new RejectResultRequest(r.Note), ct);
            return x.IsSuccess ? new BulkReviewItem(id, true, null, null, null) : new BulkReviewItem(id, false, x.Error.Message, null, null);
        });

    private async Task<BulkReviewResult> BulkAsync(BulkReviewRequest r, Func<Guid, Task<BulkReviewItem>> each)
    {
        var ids = (r.ResultIds ?? []).Distinct().ToList();
        var max = options.Value.MaxBulkItems;
        var items = new List<BulkReviewItem>();
        if (ids.Count == 0) return new BulkReviewResult(0, 0, 0, items);
        foreach (var id in ids.Take(max))
        {
            try { items.Add(await each(id)); }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException) { logger.LogWarning(ex, "Bulk review item {Id} failed", id); crm.Db.ChangeTracker.Clear(); items.Add(new BulkReviewItem(id, false, "The item could not be saved.", null, null)); }
        }
        foreach (var id in ids.Skip(max)) items.Add(new BulkReviewItem(id, false, $"Only {max} items can be processed at once.", null, null));
        return new BulkReviewResult(ids.Count, items.Count(i => i.Success), items.Count(i => !i.Success), items);
    }
}
