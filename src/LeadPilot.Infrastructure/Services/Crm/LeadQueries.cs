using LeadPilot.Application.Crm;
using LeadPilot.Domain.Crm;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Services.Crm;

/// <summary>Shared lead filtering/sorting used by the list, the export and the board. Always scoped to <paramref name="tenantId"/> on top of the global filter.</summary>
internal static class LeadQueries
{
    public static IQueryable<Lead> Filter(AppDbContext db, Guid tenantId, LeadQuery q)
    {
        var query = db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId);
        query = q.Archived switch
        {
            ArchivedFilter.Active => query.Where(l => !l.IsArchived),
            ArchivedFilter.Archived => query.Where(l => l.IsArchived),
            _ => query,
        };
        if (q.CategoryId is { } cat) query = query.Where(l => l.CategoryId == cat);
        if (q.StatusId is { } st) query = query.Where(l => l.StatusId == st);
        if (q.StageId is { } stage) query = query.Where(l => l.PipelineStageId == stage);
        if (q.StageKind is { } kind) query = query.Where(l => db.PipelineStages.Any(s => s.Id == l.PipelineStageId && s.TenantId == tenantId && s.Kind == kind));
        if (q.Unassigned) query = query.Where(l => l.OwnerUserId == null);
        else if (q.OwnerUserId is { } owner) query = query.Where(l => l.OwnerUserId == owner);
        if (q.TagId is { } tag) query = query.Where(l => db.LeadTagMaps.Any(m => m.TenantId == tenantId && m.LeadId == l.Id && m.TagId == tag));
        if (!string.IsNullOrWhiteSpace(q.Country)) { var v = q.Country.Trim(); query = query.Where(l => l.Country == v); }
        if (!string.IsNullOrWhiteSpace(q.Province)) { var v = q.Province.Trim(); query = query.Where(l => l.Province == v); }
        if (!string.IsNullOrWhiteSpace(q.City)) { var v = q.City.Trim(); query = query.Where(l => l.City == v); }
        if (q.MinScore is { } min) query = query.Where(l => l.Score >= min);
        if (q.MaxScore is { } max) query = query.Where(l => l.Score <= max);
        if (q.SourceType is { } src) query = query.Where(l => l.PrimarySourceType == src);
        if (q.CreatedFromUtc is { } from) query = query.Where(l => l.CreatedAtUtc >= from);
        if (q.CreatedToUtc is { } to) query = query.Where(l => l.CreatedAtUtc < to);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            if (s.Length > 100) s = s[..100];
            var digits = new string(s.Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
            var phoneTerm = digits.Length >= 5 ? digits : null;
            query = query.Where(l => l.Name.Contains(s) || (l.City != null && l.City.Contains(s)) || (l.WebsiteDomain != null && l.WebsiteDomain.Contains(s))
                || db.Contacts.Any(c => c.TenantId == tenantId && c.LeadId == l.Id && !c.IsDeleted
                    && ((c.Name != null && c.Name.Contains(s)) || (c.Email != null && c.Email.Contains(s)) || (phoneTerm != null && c.PhoneE164 != null && c.PhoneE164.Contains(phoneTerm)))));
        }
        return query;
    }

    public static IQueryable<Lead> Sort(AppDbContext db, IQueryable<Lead> query, LeadQuery q)
    {
        var desc = q.Descending;
        IOrderedQueryable<Lead> ordered = q.Sort switch
        {
            LeadSort.Name => desc ? query.OrderByDescending(l => l.NormalizedName) : query.OrderBy(l => l.NormalizedName),
            LeadSort.Score => desc ? query.OrderByDescending(l => l.Score) : query.OrderBy(l => l.Score),
            LeadSort.City => desc ? query.OrderByDescending(l => l.City) : query.OrderBy(l => l.City),
            LeadSort.Updated => desc ? query.OrderByDescending(l => l.LastActivityAtUtc) : query.OrderBy(l => l.LastActivityAtUtc),
            LeadSort.Stage => desc
                ? query.OrderByDescending(l => db.PipelineStages.Where(s => s.Id == l.PipelineStageId).Select(s => s.Position).FirstOrDefault())
                : query.OrderBy(l => db.PipelineStages.Where(s => s.Id == l.PipelineStageId).Select(s => s.Position).FirstOrDefault()),
            _ => desc ? query.OrderByDescending(l => l.CreatedAtUtc) : query.OrderBy(l => l.CreatedAtUtc),
        };
        return ordered.ThenBy(l => l.Id);
    }
}
