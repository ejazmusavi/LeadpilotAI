using LeadPilot.Application.Crm;
using LeadPilot.Domain.Crm;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Services.Crm;

/// <summary>Finds probable duplicate leads of the current tenant by normalised name+city, website domain and contact phone (E.164). Batch-capable and index-backed.</summary>
public sealed class DuplicateDetector(CrmContext crm)
{
    public async Task<IReadOnlyList<IReadOnlyList<DuplicateMatch>>> FindAsync(IReadOnlyList<DuplicateProbe> probes, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var keys = probes.Select(p => string.IsNullOrWhiteSpace(p.Name) || Normalizer.Name(p.Name).Length == 0 ? null : Normalizer.DedupKey(p.Name, p.City)).ToList();
        var domains = probes.Select(p => Normalizer.Domain(p.Website)).ToList();
        var keySet = keys.Where(k => k is not null).Select(k => k!).Distinct().ToList();
        var domainSet = domains.Where(d => d is not null).Select(d => d!).Distinct().ToList();
        var phoneSet = probes.SelectMany(p => p.PhonesE164).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();

        var byKey = keySet.Count == 0 ? [] : (await db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && keySet.Contains(l.DedupKey))
            .Select(l => new Row(l.Id, l.Name, l.City, l.IsArchived, l.DedupKey, l.WebsiteDomain)).ToListAsync(ct));
        var byDomain = domainSet.Count == 0 ? [] : (await db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && l.WebsiteDomain != null && domainSet.Contains(l.WebsiteDomain))
            .Select(l => new Row(l.Id, l.Name, l.City, l.IsArchived, l.DedupKey, l.WebsiteDomain)).ToListAsync(ct));
        var phoneHits = phoneSet.Count == 0 ? [] : await db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && !c.IsDeleted && c.PhoneE164 != null && phoneSet.Contains(c.PhoneE164))
            .Select(c => new { c.LeadId, Phone = c.PhoneE164! }).Distinct().ToListAsync(ct);
        var phoneLeadIds = phoneHits.Select(h => h.LeadId).Distinct().ToList();
        var byPhone = phoneLeadIds.Count == 0 ? [] : (await db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && phoneLeadIds.Contains(l.Id))
            .Select(l => new Row(l.Id, l.Name, l.City, l.IsArchived, l.DedupKey, l.WebsiteDomain)).ToListAsync(ct));
        var phoneRows = byPhone.ToDictionary(r => r.Id);

        var result = new List<IReadOnlyList<DuplicateMatch>>(probes.Count);
        for (var i = 0; i < probes.Count; i++)
        {
            var found = new Dictionary<Guid, (Row Row, List<string> Reasons)>();
            void Add(Row row, string reason)
            {
                if (row.Id == probes[i].ExcludeLeadId) return;
                if (!found.TryGetValue(row.Id, out var entry)) found[row.Id] = entry = (row, []);
                if (!entry.Reasons.Contains(reason)) entry.Reasons.Add(reason);
            }
            if (keys[i] is { } k) foreach (var r in byKey.Where(r => r.DedupKey == k)) Add(r, DuplicateMatch.ReasonNameCity);
            if (domains[i] is { } d) foreach (var r in byDomain.Where(r => r.Domain == d)) Add(r, DuplicateMatch.ReasonDomain);
            foreach (var phone in probes[i].PhonesE164.Distinct())
                foreach (var h in phoneHits.Where(h => h.Phone == phone))
                    if (phoneRows.TryGetValue(h.LeadId, out var row)) Add(row, DuplicateMatch.ReasonPhone);
            result.Add(found.Values.Select(v => new DuplicateMatch(v.Row.Id, v.Row.Name, v.Row.City, v.Row.IsArchived, v.Reasons)).OrderBy(m => m.LeadName).ToList());
        }
        return result;
    }

    public async Task<IReadOnlyList<DuplicateMatch>> FindAsync(DuplicateProbe probe, CancellationToken ct) => (await FindAsync([probe], ct))[0];

    private sealed record Row(Guid Id, string Name, string? City, bool IsArchived, string DedupKey, string? Domain);
}
