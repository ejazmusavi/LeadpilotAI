using LeadPilot.Application.Crm;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Services.Crm;

/// <summary>
/// Turns an externally sourced record (discovery approval) into a lead. Duplicates are found with the Phase 3 <see cref="DuplicateDetector"/>; creation goes through
/// <see cref="ILeadService.CreateFromSourceAsync"/> (same pipeline defaults, scoring, audit). Contacts are ALWAYS <see cref="DataOrigin.Public"/> and NO consent record is written:
/// being discovered is not consent, and <see cref="IContactEligibilityService"/> keeps blocking them until consent is recorded.
/// </summary>
public sealed class LeadIntakeService(CrmContext crm, DuplicateDetector duplicates, ILeadService leads, ILeadScorer scorer, ILogger<LeadIntakeService> logger) : ILeadIntakeService
{
    public Task<Result<LeadIntakeResult>> IntakeAsync(LeadIntakeRequest r, CancellationToken ct = default) =>
        ResultFlowGuard(async () =>
        {
            await crm.EnsureDefaultsAsync(ct);
            if (string.IsNullOrWhiteSpace(r.Name)) return Result.Failure<LeadIntakeResult>(Error.Validation("Lead name is required."));
            if (r.SourceType != LeadSourceType.Discovery) return Result.Failure<LeadIntakeResult>(Error.Validation("Only discovery results use this path."));
            var country = await crm.DefaultCountryCodeAsync(ct);

            // contacts: only values that normalise; the rest stay on the discovery result for the reviewer
            var contacts = new List<(string? Phone, string? E164, string? Email)>();
            foreach (var c in r.Contacts)
            {
                var e164 = Normalizer.PhoneE164(c.Phone, country);
                var email = c.Email is null ? null : Normalizer.Email(c.Email);
                if (e164 is null && email is null) continue;
                if (contacts.Any(x => (e164 is not null && x.E164 == e164) || (email is not null && x.Email == email))) continue;
                contacts.Add((e164 is null ? null : c.Phone, e164, email));
            }
            var phones = contacts.Where(c => c.E164 is not null).Select(c => c.E164!).ToList();

            if (r.MergeIfDuplicate)
            {
                var found = await duplicates.FindAsync(new DuplicateProbe(r.Name, r.City, r.Website, phones), ct);
                if (found.Count > 0)
                {
                    var target = found.FirstOrDefault(m => !m.IsArchived);
                    if (target is null) return Result.Failure<LeadIntakeResult>(Error.Duplicate("A matching lead exists but is archived (" + LeadService.DescribeDuplicates(found) + ") Restore it, or choose 'create a new lead'."));
                    return Result.Success(await MergeAsync(target.LeadId, r, contacts, ct));
                }
            }

            var request = new CreateLeadRequest(r.Name, r.CategoryId, r.Country, r.Province, r.City, r.District, r.Area, r.Address, r.Website, r.Confidence, OwnerUserId: r.OwnerUserId,
                SourceName: r.SourceName, SourceUrl: r.SourceUrl,
                Contacts: contacts.Select((c, i) => new ContactInput(null, null, c.Phone, c.Email, DataOrigin.Public, i == 0)).ToList(),
                AllowDuplicate: !r.MergeIfDuplicate);
            var created = await leads.CreateFromSourceAsync(request, LeadSourceType.Discovery, r.ExternalId, r.Attribution, r.RetrievedAtUtc, ct);
            return created.IsFailure ? Result.Failure<LeadIntakeResult>(created.Error) : Result.Success(new LeadIntakeResult(created.Value.Id, IntakeAction.Created, []));
        });

    private async Task<LeadIntakeResult> MergeAsync(Guid leadId, LeadIntakeRequest r, List<(string? Phone, string? E164, string? Email)> contacts, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var lead = await db.Leads.FirstAsync(l => l.Id == leadId && l.TenantId == tenantId, ct);
        var profile = new LeadProfile(lead.Name, lead.CategoryId ?? r.CategoryId, lead.Country ?? r.Country, lead.Province ?? r.Province, lead.City ?? r.City, lead.District ?? r.District,
            lead.Area ?? r.Area, lead.Address ?? r.Address, lead.Website ?? r.Website, lead.Confidence);
        var changed = lead.UpdateProfile(profile, now, crm.Actor).ToList();

        var existing = await db.Contacts.Where(c => c.TenantId == tenantId && c.LeadId == leadId && !c.IsDeleted).ToListAsync(ct);
        var added = 0;
        foreach (var c in contacts)
        {
            if (existing.Any(x => (c.E164 is not null && x.PhoneE164 == c.E164) || (c.Email is not null && x.Email == c.Email))) continue;
            // a phone/e-mail unique key allows one per lead: skip values already on this lead under another contact
            var contact = Contact.Create(tenantId, leadId, null, null, c.Phone, c.E164, c.Email, DataOrigin.Public, existing.Count == 0 && added == 0, now, crm.Actor);
            db.Contacts.Add(contact); added++;
        }
        if (added > 0) changed.Add($"{added} contact(s)");

        var sourceKnown = r.ExternalId is not null && await db.LeadSources.AnyAsync(s => s.TenantId == tenantId && s.Type == LeadSourceType.Discovery && s.ExternalId == r.ExternalId, ct);
        if (!sourceKnown) db.LeadSources.Add(LeadSource.Create(tenantId, leadId, LeadSourceType.Discovery, r.ExternalId, r.SourceName, r.SourceUrl, r.Attribution, r.RetrievedAtUtc));
        crm.Activity(leadId, LeadActivityType.Merged, "Discovery result merged" + (changed.Count > 0 ? ": filled " + string.Join(", ", changed) : " (nothing new to add)") + ".");
        crm.Record("Lead.MergedFromDiscovery", "Lead", leadId, $"Discovery result '{r.Name}' merged into lead '{lead.Name}'.");
        await db.SaveChangesAsync(ct);
        await scorer.RescoreAsync([leadId], ct);
        return new LeadIntakeResult(leadId, IntakeAction.Merged, changed);
    }

    private async Task<Result<T>> ResultFlowGuard<T>(Func<Task<Result<T>>> work)
    {
        try { return await CrmFlow.GuardAsync(work, logger); }
        catch (DomainException ex) { return Result.Failure<T>(Error.Validation(ex.Message)); }
    }
}
