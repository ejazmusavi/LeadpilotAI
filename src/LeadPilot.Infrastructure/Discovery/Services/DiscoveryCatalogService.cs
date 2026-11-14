using LeadPilot.Application.Discovery;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Discovery;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Discovery.Services;

/// <summary>Category search phrases, saved locations and starter packs (all tenant data).</summary>
public sealed class DiscoveryCatalogService(CrmContext crm, ILogger<DiscoveryCatalogService> logger) : IDiscoveryCatalogService
{
    private bool _autoDone;

    /// <summary>Education tenants get the education starter pack the first time discovery is opened; nothing else is ever installed implicitly.</summary>
    public async Task EnsureAutoPackAsync(CancellationToken ct)
    {
        if (_autoDone) return; _autoDone = true;
        var tenantId = crm.TenantId;
        var industry = await crm.Db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Industry).FirstOrDefaultAsync(ct);
        if (DiscoveryStarterPacks.AutoPackForIndustry(industry) is not { } key) return;
        if (await crm.Db.DiscoveryCategoryMappings.AnyAsync(m => m.TenantId == tenantId, ct)) return;   // the tenant has already set up (or removed) its own mappings
        await InstallPackAsync(key, ct);
    }

    public async Task<IReadOnlyList<CategoryMappingDto>> ListMappingsAsync(CancellationToken ct = default)
    {
        await EnsureAutoPackAsync(ct);
        var tenantId = crm.TenantId;
        var rows = await (from m in crm.Db.DiscoveryCategoryMappings.AsNoTracking() join c in crm.Db.LeadCategories.AsNoTracking() on m.CategoryId equals c.Id
                          where m.TenantId == tenantId && c.TenantId == tenantId orderby c.Name select new { m, c.Name }).ToListAsync(ct);
        return rows.Select(x => new CategoryMappingDto(x.m.Id, x.m.CategoryId, x.Name, x.m.Terms, x.m.PlaceType, x.m.IsActive)).ToList();
    }

    public Task<Result<CategoryMappingDto>> SaveMappingAsync(SaveCategoryMappingRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            var cat = await db.LeadCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.CategoryId && c.TenantId == tenantId, ct);
            if (cat is null) return Result.Failure<CategoryMappingDto>(Error.Validation("Category not found."));
            var m = await db.DiscoveryCategoryMappings.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.CategoryId == r.CategoryId, ct);
            if (m is null) { m = DiscoveryCategoryMapping.Create(tenantId, r.CategoryId, r.SearchTerms, r.PlaceType, now); db.DiscoveryCategoryMappings.Add(m); }
            else m.Update(r.SearchTerms, r.PlaceType, now);
            crm.Record("Discovery.CategoryMappingSaved", "DiscoveryCategoryMapping", m.Id, $"Search phrases for '{cat.Name}' saved.");
            await db.SaveChangesAsync(ct);
            return Result.Success(new CategoryMappingDto(m.Id, m.CategoryId, cat.Name, m.Terms, m.PlaceType, m.IsActive));
        }, logger);

    public Task<Result<CategoryMappingDto>> SetMappingActiveAsync(Guid id, bool active, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var m = await crm.Db.DiscoveryCategoryMappings.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            if (m is null) return Result.Failure<CategoryMappingDto>(Error.NotFound("Category mapping"));
            m.SetActive(active, crm.Now);
            var name = await crm.Db.LeadCategories.AsNoTracking().Where(c => c.Id == m.CategoryId && c.TenantId == tenantId).Select(c => c.Name).FirstAsync(ct);
            crm.Record("Discovery.CategoryMappingChanged", "DiscoveryCategoryMapping", id, $"Search phrases for '{name}' {(active ? "activated" : "deactivated")}.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(new CategoryMappingDto(m.Id, m.CategoryId, name, m.Terms, m.PlaceType, m.IsActive));
        }, logger);

    public async Task<IReadOnlyList<LocationPresetDto>> ListLocationsAsync(CancellationToken ct = default)
    {
        await EnsureAutoPackAsync(ct);
        var tenantId = crm.TenantId;
        return await crm.Db.DiscoveryLocationPresets.AsNoTracking().Where(l => l.TenantId == tenantId).OrderBy(l => l.Label)
            .Select(l => new LocationPresetDto(l.Id, l.Label, l.Country, l.Province, l.City, l.District, l.Area)).ToListAsync(ct);
    }

    public Task<Result<LocationPresetDto>> AddLocationAsync(SaveLocationPresetRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var l = DiscoveryLocationPreset.Create(tenantId, r.Country, r.Province, r.City, r.District, r.Area, crm.Now);
            if (await crm.Db.DiscoveryLocationPresets.CountAsync(x => x.TenantId == tenantId, ct) >= 500) return Result.Failure<LocationPresetDto>(Error.Conflict("At most 500 saved locations."));
            crm.Db.DiscoveryLocationPresets.Add(l);
            crm.Record("Discovery.LocationAdded", "DiscoveryLocationPreset", l.Id, $"Saved location '{l.Label}' added.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(new LocationPresetDto(l.Id, l.Label, l.Country, l.Province, l.City, l.District, l.Area));
        }, logger);

    public async Task<Result> DeleteLocationAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var l = await crm.Db.DiscoveryLocationPresets.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (l is null) return Result.Failure(Error.NotFound("Saved location"));
        crm.Db.DiscoveryLocationPresets.Remove(l);
        crm.Record("Discovery.LocationRemoved", "DiscoveryLocationPreset", id, $"Saved location '{l.Label}' removed.");
        await crm.Db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<StarterPackDto>> ListPacksAsync(CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var names = await crm.Db.LeadCategories.AsNoTracking().Where(c => c.TenantId == tenantId).Select(c => c.NormalizedName).ToListAsync(ct);
        return DiscoveryStarterPacks.All.Select(p => new StarterPackDto(p.Key, p.Name, p.Description, p.Categories.Count, p.Locations.Count,
            p.Categories.All(c => names.Contains(Normalizer.Name(c.Name))))).ToList();
    }

    /// <summary>Idempotent: existing categories (by name), mappings and locations are left untouched, so a tenant's edits are never overwritten.</summary>
    public Task<Result<InstallPackResult>> InstallPackAsync(string key, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var pack = DiscoveryStarterPacks.Find(key);
            if (pack is null) return Result.Failure<InstallPackResult>(Error.NotFound("Starter pack"));
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            var cats = await db.LeadCategories.Where(c => c.TenantId == tenantId).ToListAsync(ct);
            var maps = await db.DiscoveryCategoryMappings.AsNoTracking().Where(m => m.TenantId == tenantId).Select(m => m.CategoryId).ToListAsync(ct);
            var labels = (await db.DiscoveryLocationPresets.AsNoTracking().Where(l => l.TenantId == tenantId).Select(l => l.Label).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int newCats = 0, newMaps = 0, newLocs = 0;
            foreach (var sc in pack.Categories)
            {
                var cat = cats.FirstOrDefault(c => c.NormalizedName == Normalizer.Name(sc.Name));
                if (cat is null) { cat = LeadCategory.Create(tenantId, sc.Name, now); db.LeadCategories.Add(cat); cats.Add(cat); newCats++; }
                if (!maps.Contains(cat.Id)) { db.DiscoveryCategoryMappings.Add(DiscoveryCategoryMapping.Create(tenantId, cat.Id, string.Join('\n', sc.Terms), sc.PlaceType, now)); newMaps++; }
            }
            foreach (var sl in pack.Locations)
            {
                var loc = DiscoveryLocationPreset.Create(tenantId, sl.Country, sl.Province, sl.City, null, null, now);
                if (labels.Add(loc.Label)) { db.DiscoveryLocationPresets.Add(loc); newLocs++; }
            }
            if (newCats + newMaps + newLocs > 0)
            {
                crm.Record("Discovery.StarterPackInstalled", "DiscoveryStarterPack", Guid.Empty, $"Starter pack '{pack.Name}': {newCats} category(ies), {newMaps} mapping(s), {newLocs} location(s) added.");
                await db.SaveChangesAsync(ct);
            }
            return Result.Success(new InstallPackResult(newCats, newMaps, newLocs));
        }, logger);
}
