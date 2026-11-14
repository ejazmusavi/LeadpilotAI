using System.Text.Json;
using LeadPilot.Application.Config;
using LeadPilot.Application.Contracts;
using LeadPilot.Domain.Config;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Config;

/// <summary>Which stored packs a workspace may see. The ONE place that rule lives: builtin packs, published platform packs, and the workspace's own private packs. Another tenant's private pack does not exist for it.</summary>
internal static class PackCatalog
{
    public static IQueryable<PackDefinition> Visible(AppDbContext db, Guid tenantId) =>
        db.PackDefinitions.Where(p => p.Scope == PackScope.Builtin || (p.Scope == PackScope.Platform && p.IsPublished) || (p.Scope == PackScope.Tenant && p.OwnerTenantId == tenantId));
}

internal sealed class PackService(CrmContext crm, ILogger<PackService> logger, Lazy<IPackEngineFactory> engineFactory) : IPackService
{
    public const int MaxPrivatePacks = 20;

    public async Task<IReadOnlyList<PackSummaryDto>> ListAsync(CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var rows = await PackCatalog.Visible(crm.Db, tenantId).AsNoTracking().ToListAsync(ct);
        var latest = rows.GroupBy(r => r.Key).Select(g => g.OrderByDescending(r => r.VersionNumber).First()).OrderBy(r => r.Scope).ThenBy(r => r.Name).ToList();
        var applied = await AppliedAsync(tenantId, ct);
        return latest.Select(r => Summary(r, applied)).ToList();
    }

    public async Task<Result<PackSummaryDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var row = await PackCatalog.Visible(crm.Db, tenantId).AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return row is null ? Result.Failure<PackSummaryDto>(Error.NotFound("Pack")) : Result.Success(Summary(row, await AppliedAsync(tenantId, ct)));
    }

    public async Task<Result<string>> GetContentAsync(Guid id, CancellationToken ct = default)
    {
        var row = await PackCatalog.Visible(crm.Db, crm.TenantId).AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        return row is null ? Result.Failure<string>(Error.NotFound("Pack")) : Result.Success(row.ContentJson);
    }

    public Task<Result<PackPlanDto>> PreviewAsync(Guid id, ApplyPackOptions options, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var (row, doc, err) = await LoadAsync(id, ct);
            if (err is not null) return Result.Failure<PackPlanDto>(err);
            var plan = await engineFactory.Value.Create().PlanAsync(doc!, options, ct);
            return Result.Success(await ToDtoAsync(row!, doc!, plan, null, ct));
        }, logger);

    public Task<Result<PackApplyResultDto>> ApplyAsync(Guid id, ApplyPackOptions options, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var (row, doc, err) = await LoadAsync(id, ct);
            if (err is not null) return Result.Failure<PackApplyResultDto>(err);
            var db = crm.Db; var tenantId = crm.TenantId;
            var engine = engineFactory.Value.Create();
            try
            {
                await using var tx = await TxScope.BeginAsync(db, ct);
                var plan = await engine.PlanAsync(doc!, options, ct);
                var ids = await engine.ExecuteAsync(doc!, plan, options, ct);
                var dto = await ToDtoAsync(row!, doc!, plan, ids, ct);
                var summary = JsonSerializer.Serialize(new { dto.Counts, items = dto.Items.Count, options.Policy, sections = (options.Sections ?? PackSections.All).ToArray() });
                var app = TenantPackApplication.Create(tenantId, doc!.Key, doc.Version, doc.Name, row!.ScopeKey, row.ContentHash, dto.Mode, options.Policy.ToString(), summary, crm.UserId, crm.Actor, crm.Now);
                db.TenantPackApplications.Add(app);
                crm.Record("Pack.Applied", "PackDefinition", row.Id, $"Industry pack '{doc.Name}' v{doc.Version} applied ({dto.Mode}): {dto.Counts.Create} created, {dto.Counts.Update} updated, {dto.Counts.Overwrite} overwritten, {dto.Counts.KeepYours} kept as yours, {dto.Counts.Blocked} blocked.");
                foreach (var o in dto.Items.Where(i => i.Outcome == PackOutcome.Overwrite))
                    crm.Record("Pack.ItemOverwritten", "PackItem", o.EntityId ?? Guid.Empty, $"Pack '{doc.Key}' overwrote your version of {o.Type} '{o.Name}' (you chose the pack's version). Changed: {string.Join("; ", o.Changes.Take(5).Select(c => $"{c.Field}: '{c.Before}' -> '{c.After}'"))}");
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return Result.Success(new PackApplyResultDto(dto, app.Id, app.AppliedAtUtc));
            }
            catch (Exception ex) when (ex is PackApplyException or DbUpdateException or Domain.Common.DomainException)
            {
                db.ChangeTracker.Clear();   // the transaction was never committed: nothing of the pack remains
                logger.LogWarning(ex, "Pack {Pack} could not be applied and was rolled back", doc!.Key);
                try { await crm.Audit.RecordAndSaveAsync(new AuditEntry("Pack.ApplyFailed", "PackDefinition", row!.Id.ToString(), $"Industry pack '{doc.Name}' v{doc.Version} could not be applied and was rolled back: {Trim(ex.Message)}"), ct); }
                catch (Exception audit) { logger.LogError(audit, "Could not record the failed pack import"); }
                return Result.Failure<PackApplyResultDto>(ex is DbUpdateException ? Error.Conflict("The pack could not be applied (another import may be running). Nothing was changed. Try again.") : Error.Validation($"The pack was not applied and nothing was changed: {Trim(ex.Message)}"));
            }
        }, logger);

    public async Task<IReadOnlyList<PackApplicationDto>> HistoryAsync(CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var rows = await crm.Db.TenantPackApplications.AsNoTracking().Where(a => a.TenantId == tenantId).OrderByDescending(a => a.AppliedAtUtc).Take(200).ToListAsync(ct);
        return rows.Select(a => new PackApplicationDto(a.Id, a.PackKey, a.PackVersion, a.PackName, a.Mode, a.ConflictPolicy, a.SummaryJson, a.AppliedBy, a.AppliedAtUtc)).ToList();
    }

    public Task<PackValidationDto> ValidateAsync(string json, CancellationToken ct = default)
    {
        var r = PackParser.Parse(json);
        return Task.FromResult(new PackValidationDto(r.IsValid, r.Problems, r.Pack?.Key, r.Pack?.Version, r.Pack?.Name, r.Pack?.ItemCount));
    }

    public Task<Result<PackSummaryDto>> UploadPrivateAsync(string json, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var parsed = PackParser.Parse(json);
            if (!parsed.IsValid) return Result.Failure<PackSummaryDto>(Error.Validation("The pack is not valid: " + string.Join(" ", parsed.Problems.Take(5).Select(p => $"{p.Path}: {p.Message}"))));
            var pack = parsed.Pack!; var db = crm.Db; var tenantId = crm.TenantId;
            if (await db.PackDefinitions.AnyAsync(p => p.Key == pack.Key && p.Scope != PackScope.Tenant && (p.Scope == PackScope.Builtin || p.IsPublished), ct))
                return Result.Failure<PackSummaryDto>(Error.Conflict($"The key '{pack.Key}' belongs to a pack offered by the platform. Choose another key."));
            var mine = await db.PackDefinitions.Where(p => p.OwnerTenantId == tenantId).ToListAsync(ct);
            var same = mine.FirstOrDefault(p => p.Key == pack.Key && p.VersionNumber == pack.Version);
            if (same is not null)
                return same.ContentHash == parsed.Hash ? Result.Success(Summary(same, await AppliedAsync(tenantId, ct))) : Result.Failure<PackSummaryDto>(Error.Conflict($"Version {pack.Version} of '{pack.Key}' already exists with different content. Raise the version number."));
            if (mine.Any(p => p.Key == pack.Key && p.VersionNumber > pack.Version)) return Result.Failure<PackSummaryDto>(Error.Conflict("A newer version of this pack already exists."));
            if (mine.Select(p => p.Key).Distinct().Count() >= MaxPrivatePacks && mine.All(p => p.Key != pack.Key)) return Result.Failure<PackSummaryDto>(Error.Validation($"A workspace can keep at most {MaxPrivatePacks} private packs."));
            var row = PackDefinition.Create(PackScope.Tenant, tenantId, pack.Key, pack.Version, pack.Name, pack.Description, pack.Industry, pack.Sample, parsed.CanonicalJson!, parsed.Hash!, false, crm.Now, crm.Actor);
            db.PackDefinitions.Add(row);
            crm.Record("Pack.Uploaded", "PackDefinition", row.Id, $"Private pack '{pack.Name}' v{pack.Version} ({pack.Key}) uploaded: {pack.ItemCount} item(s).");
            await db.SaveChangesAsync(ct);
            return Result.Success(Summary(row, await AppliedAsync(tenantId, ct)));
        }, logger);

    public Task<Result> DeletePrivateAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var row = await crm.Db.PackDefinitions.FirstOrDefaultAsync(p => p.Id == id && p.Scope == PackScope.Tenant && p.OwnerTenantId == tenantId, ct);
            if (row is null) return Result.Failure(Error.NotFound("Pack"));
            crm.Db.PackDefinitions.Remove(row);
            crm.Record("Pack.Deleted", "PackDefinition", id, $"Private pack '{row.Name}' v{row.VersionNumber} deleted. What it created stays; its history stays.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success();
        }, logger);

    public Task<Result<PackExportDto>> ExportAsync(PackExportRequest request, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(request.Key ?? "", "^[a-z][a-z0-9-]{1,59}$")) return Result.Failure<PackExportDto>(Error.Validation("The pack key uses lower-case letters, digits and hyphens, starts with a letter (2 to 60 characters)."));
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 150) return Result.Failure<PackExportDto>(Error.Validation("Give the pack a name of up to 150 characters."));
            if (request.Version is < 1 or > 100000) return Result.Failure<PackExportDto>(Error.Validation("The version is a whole number from 1."));
            var (json, counts, warnings, problems) = await new PackExporter(crm).ExportAsync(request with { Name = request.Name.Trim() }, ct);
            if (json is null) return Result.Failure<PackExportDto>(Error.Validation("The configuration cannot be written as a valid pack yet: " + string.Join(" ", problems.Take(5).Select(p => $"{p.Path}: {p.Message}")) + " Fix these or leave the section out."));
            crm.Record("Pack.Exported", "PackDefinition", Guid.Empty, $"Workspace configuration exported as pack '{request.Key}' v{request.Version}: {string.Join(", ", counts.Select(c => $"{c.Value} {c.Key}"))}.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(new PackExportDto(json, request.Key + "-v" + request.Version + ".leadpilot-pack.json", counts, warnings));
        }, logger);

    // ------------------------------------------------------------------------------------------------

    private async Task<(PackDefinition? Row, PackDocument? Doc, Error? Error)> LoadAsync(Guid id, CancellationToken ct)
    {
        var row = await PackCatalog.Visible(crm.Db, crm.TenantId).AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is null) return (null, null, Error.NotFound("Pack"));
        var parsed = PackParser.Parse(row.ContentJson);
        return parsed.IsValid ? (row, parsed.Pack, null) : (row, null, Error.Validation("The stored pack is no longer valid: " + parsed.Problems[0].Message));
    }

    private async Task<Dictionary<string, TenantPackApplication>> AppliedAsync(Guid tenantId, CancellationToken ct)
    {
        var all = await crm.Db.TenantPackApplications.AsNoTracking().Where(a => a.TenantId == tenantId).ToListAsync(ct);
        return all.GroupBy(a => a.PackKey).ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.AppliedAtUtc).First());
    }

    private async Task<PackPlanDto> ToDtoAsync(PackDefinition row, PackDocument doc, IReadOnlyList<Planned> plan, Dictionary<string, Guid>? ids, CancellationToken ct)
    {
        var prior = await crm.Db.TenantPackApplications.AsNoTracking().Where(a => a.TenantId == crm.TenantId && a.PackKey == doc.Key).OrderByDescending(a => a.AppliedAtUtc).FirstOrDefaultAsync(ct);
        var mode = prior is null ? PackApplyMode.Initial : prior.PackVersion < doc.Version ? PackApplyMode.Upgrade : PackApplyMode.Reapply;
        var items = plan.Select(p => new PackItemDto(p.Item.Type, p.Item.Key, p.Item.Name, p.Outcome, p.Conflict, p.Reason, p.Changes, ids is not null && ids.TryGetValue(p.Item.Id, out var g) ? g : p.Entity?.Id)).ToList();
        int N(PackOutcome o) => items.Count(i => i.Outcome == o);
        var counts = new PackCounts(N(PackOutcome.Create), N(PackOutcome.Update), N(PackOutcome.Unchanged), N(PackOutcome.Link), N(PackOutcome.KeepYours), N(PackOutcome.Overwrite), N(PackOutcome.LeaveDeleted), N(PackOutcome.Recreate), N(PackOutcome.Blocked), items.Count(i => i.IsConflict));
        var notes = new List<string>();
        if (doc.Sample) notes.Add(doc.Notice ?? "SAMPLE – review before use");
        notes.Add("Nothing is switched on: agents do not reply on their own, workflows stay drafts, products and prices start unconfirmed.");
        return new PackPlanDto(doc.Key, doc.Version, doc.Name, doc.Sample, doc.Notice, mode, items, counts, notes);
    }

    private static PackSummaryDto Summary(PackDefinition r, Dictionary<string, TenantPackApplication> applied)
    {
        var parsed = PackParser.Parse(r.ContentJson);
        var p = parsed.Pack;
        var sections = p is null ? new Dictionary<string, int>() : new Dictionary<string, int>
        {
            [PackSections.Categories] = p.Categories.Count, [PackSections.Pipelines] = p.Pipelines.Count, [PackSections.CustomFields] = p.CustomFields.Count, [PackSections.ScoringRules] = p.ScoringRules.Count,
            [PackSections.MessageTemplates] = p.MessageTemplates.Count, [PackSections.Agents] = p.Agents.Count, [PackSections.Catalog] = p.Catalog.Count, [PackSections.Knowledge] = p.Knowledge.Count,
            [PackSections.Workflows] = p.Workflows.Count, [PackSections.Discovery] = (p.Discovery?.Categories.Count ?? 0) + (p.Discovery?.Locations.Count ?? 0),
        };
        applied.TryGetValue(r.Key, out var a);
        return new PackSummaryDto(r.Id, r.Key, r.VersionNumber, r.Name, r.Description, r.Industry, r.Scope, r.IsPublished, r.IsSample, p?.Notice, p?.ItemCount ?? 0, sections, a?.PackVersion, a?.AppliedAtUtc, a is not null && a.PackVersion < r.VersionNumber);
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}

/// <summary>Creates the engine on demand (it needs the workflow service, which needs the same unit of work).</summary>
internal interface IPackEngineFactory { PackEngine Create(); }

internal sealed class PackEngineFactory(CrmContext crm, Application.Workflows.IWorkflowService workflows) : IPackEngineFactory
{
    public PackEngine Create() => new(crm, workflows);
}
