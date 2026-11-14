using LeadPilot.Application.Config;
using LeadPilot.Domain.Config;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Config;

/// <summary>Platform administrators publish packs that every workspace can use. Tenants can read published packs but never change them; builtin packs are read-only for everyone.</summary>
internal sealed class PlatformPackService(CrmContext crm, ILogger<PlatformPackService> logger) : IPlatformPackService
{
    public async Task<IReadOnlyList<PackSummaryDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await crm.Db.PackDefinitions.AsNoTracking().Where(p => p.Scope != PackScope.Tenant).OrderBy(p => p.Scope).ThenBy(p => p.Key).ThenByDescending(p => p.VersionNumber).ToListAsync(ct);
        return rows.Select(r => ToDto(r)).ToList();
    }

    public Task<Result<PackSummaryDto>> PublishAsync(string json, bool publish, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var parsed = PackParser.Parse(json);
            if (!parsed.IsValid) return Result.Failure<PackSummaryDto>(Error.Validation("The pack is not valid: " + string.Join(" ", parsed.Problems.Take(5).Select(p => $"{p.Path}: {p.Message}"))));
            var pack = parsed.Pack!; var db = crm.Db;
            if (await db.PackDefinitions.AnyAsync(p => p.Key == pack.Key && (p.Scope == PackScope.Builtin || p.Scope == PackScope.Tenant), ct))
                return Result.Failure<PackSummaryDto>(Error.Conflict($"The key '{pack.Key}' is already used by a builtin or private pack. Choose another key."));
            var same = await db.PackDefinitions.FirstOrDefaultAsync(p => p.Key == pack.Key && p.Scope == PackScope.Platform && p.VersionNumber == pack.Version, ct);
            if (same is not null)
            {
                if (same.ContentHash != parsed.Hash) return Result.Failure<PackSummaryDto>(Error.Conflict($"Version {pack.Version} of '{pack.Key}' is already published with different content. Raise the version number."));
                return await SetAsync(same, publish);
            }
            if (await db.PackDefinitions.AnyAsync(p => p.Key == pack.Key && p.Scope == PackScope.Platform && p.VersionNumber > pack.Version, ct)) return Result.Failure<PackSummaryDto>(Error.Conflict("A newer version of this pack already exists."));
            var row = PackDefinition.Create(PackScope.Platform, null, pack.Key, pack.Version, pack.Name, pack.Description, pack.Industry, pack.Sample, parsed.CanonicalJson!, parsed.Hash!, publish, crm.Now, crm.Actor);
            db.PackDefinitions.Add(row);
            crm.Record("Platform.Pack.Published", "PackDefinition", row.Id, $"Platform pack '{pack.Name}' v{pack.Version} ({pack.Key}) {(publish ? "published to all workspaces" : "saved as unpublished")}.");
            await db.SaveChangesAsync(ct);
            return Result.Success(ToDto(row));
        }, logger);

    public Task<Result<PackSummaryDto>> SetPublishedAsync(Guid id, bool published, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var row = await crm.Db.PackDefinitions.FirstOrDefaultAsync(p => p.Id == id && p.Scope == PackScope.Platform, ct);   // builtin packs cannot be withdrawn; private packs are not the platform's
            return row is null ? Result.Failure<PackSummaryDto>(Error.NotFound("Pack")) : await SetAsync(row, published);
        }, logger);

    private async Task<Result<PackSummaryDto>> SetAsync(PackDefinition row, bool published)
    {
        if (row.IsPublished != published)
        {
            row.SetPublished(published, crm.Now, crm.Actor);
            crm.Record(published ? "Platform.Pack.Published" : "Platform.Pack.Withdrawn", "PackDefinition", row.Id, $"Platform pack '{row.Name}' v{row.VersionNumber} {(published ? "published to all workspaces" : "withdrawn; workspaces that applied it keep what it created")}.");
            await crm.Db.SaveChangesAsync();
        }
        return Result.Success(ToDto(row));
    }

    private static PackSummaryDto ToDto(PackDefinition r)
    {
        var p = PackParser.Parse(r.ContentJson).Pack;
        return new PackSummaryDto(r.Id, r.Key, r.VersionNumber, r.Name, r.Description, r.Industry, r.Scope, r.IsPublished, r.IsSample, p?.Notice, p?.ItemCount ?? 0, new Dictionary<string, int>(), null, null, false);
    }
}
