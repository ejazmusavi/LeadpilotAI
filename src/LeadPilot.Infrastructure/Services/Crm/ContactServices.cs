using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Services.Crm;

public sealed class ContactService(CrmContext crm, ILeadScorer scorer, ILogger<ContactService> logger) : IContactService
{
    public async Task<Result<IReadOnlyList<ContactDto>>> ListAsync(Guid leadId, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        if (!await crm.Db.Leads.AnyAsync(l => l.Id == leadId && l.TenantId == tenantId, ct)) return Result.Failure<IReadOnlyList<ContactDto>>(Error.NotFound("Lead"));
        var contacts = await crm.Db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && c.LeadId == leadId && !c.IsDeleted).OrderByDescending(c => c.IsPrimary).ThenBy(c => c.CreatedAtUtc).ThenBy(c => c.Id).ToListAsync(ct);
        return Result.Success(await ToDtos(contacts, ct));
    }

    public async Task<Result<ContactDto>> GetAsync(Guid contactId, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var c = await crm.Db.Contacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == contactId && x.TenantId == tenantId && !x.IsDeleted, ct);
        return c is null ? Result.Failure<ContactDto>(Error.NotFound("Contact")) : Result.Success((await ToDtos([c], ct))[0]);
    }

    internal async Task<IReadOnlyList<ContactDto>> ToDtos(List<Contact> contacts, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var ids = contacts.Select(c => c.Id).ToList();
        var records = ids.Count == 0 ? [] : await crm.Db.ConsentRecords.AsNoTracking().Where(r => r.TenantId == tenantId && ids.Contains(r.ContactId)).ToListAsync(ct);
        var latest = records.GroupBy(r => (r.ContactId, r.Channel, r.Purpose))
            .Select(g => g.OrderByDescending(r => r.OccurredAtUtc).ThenByDescending(r => r.IsRevocation).First()).ToLookup(r => r.ContactId);
        return contacts.Select(c => new ContactDto(c.Id, c.LeadId, c.Name, c.Role, c.PhoneE164, c.PhoneRaw, c.Email, c.DataOrigin, c.PhoneVerifiedAtUtc, c.IsPrimary,
            latest[c.Id].OrderBy(r => r.Channel).ThenBy(r => r.Purpose).Select(r => new ConsentStateDto(r.Channel, r.Purpose, r.Status, r.OccurredAtUtc, r.Source)).ToList())).ToList();
    }

    public Task<Result<ContactDto>> AddAsync(Guid leadId, SaveContactRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leadId && l.TenantId == tenantId, ct);
            if (lead is null) return Result.Failure<ContactDto>(Error.NotFound("Lead"));
            if (lead.IsArchived) return Result.Failure<ContactDto>(Error.Validation("Archived leads cannot be edited. Restore the lead first."));

            var e164 = Normalizer.PhoneE164(r.Phone, await crm.DefaultCountryCodeAsync(ct));
            var existing = await db.Contacts.Where(c => c.TenantId == tenantId && c.LeadId == leadId && !c.IsDeleted).ToListAsync(ct);
            var contact = Contact.Create(tenantId, leadId, r.Name, r.Role, r.Phone, e164, r.Email, r.DataOrigin, r.IsPrimary || existing.Count == 0, now, crm.Actor);
            if (contact.PhoneE164 is not null && existing.Any(c => c.PhoneE164 == contact.PhoneE164)) return Result.Failure<ContactDto>(Error.Conflict("This lead already has a contact with that phone number."));
            if (contact.Email is not null && existing.Any(c => c.Email == contact.Email)) return Result.Failure<ContactDto>(Error.Conflict("This lead already has a contact with that e-mail address."));
            if (contact.IsPrimary) foreach (var other in existing.Where(c => c.IsPrimary)) other.SetPrimary(false, now);

            db.Contacts.Add(contact);
            crm.Activity(leadId, LeadActivityType.ContactAdded, $"Contact added{(contact.Name is null ? "" : ": " + contact.Name)}.");
            crm.Record("Contact.Added", "Contact", contact.Id, $"Contact added to lead '{lead.Name}'.");
            await db.SaveChangesAsync(ct);
            await scorer.RescoreAsync([leadId], ct);
            return Result.Success((await ToDtos([contact], ct))[0]);
        }, logger);

    public Task<Result<ContactDto>> UpdateAsync(Guid contactId, SaveContactRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            var contact = await db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId && c.TenantId == tenantId && !c.IsDeleted, ct);
            if (contact is null) return Result.Failure<ContactDto>(Error.NotFound("Contact"));
            var lead = await db.Leads.AsNoTracking().FirstAsync(l => l.Id == contact.LeadId && l.TenantId == tenantId, ct);
            if (lead.IsArchived) return Result.Failure<ContactDto>(Error.Validation("Archived leads cannot be edited. Restore the lead first."));

            var oldPhone = contact.PhoneE164; var oldEmail = contact.Email;
            var e164 = Normalizer.PhoneE164(r.Phone, await crm.DefaultCountryCodeAsync(ct));
            contact.Update(r.Name, r.Role, r.Phone, e164, r.Email, r.DataOrigin, now, crm.Actor);
            var siblings = await db.Contacts.Where(c => c.TenantId == tenantId && c.LeadId == contact.LeadId && !c.IsDeleted && c.Id != contactId).ToListAsync(ct);
            if (contact.PhoneE164 is not null && siblings.Any(c => c.PhoneE164 == contact.PhoneE164)) return Result.Failure<ContactDto>(Error.Conflict("This lead already has a contact with that phone number."));
            if (contact.Email is not null && siblings.Any(c => c.Email == contact.Email)) return Result.Failure<ContactDto>(Error.Conflict("This lead already has a contact with that e-mail address."));
            if (r.IsPrimary && !contact.IsPrimary) { contact.SetPrimary(true, now); foreach (var o in siblings.Where(c => c.IsPrimary)) o.SetPrimary(false, now); }
            else if (!r.IsPrimary && contact.IsPrimary && siblings.Count > 0) { contact.SetPrimary(false, now); siblings.OrderBy(c => c.CreatedAtUtc).First().SetPrimary(true, now); }

            // Consent belongs to the person at a specific number/address. When it changes the old consent must not carry over to the new endpoint.
            var revoked = await RevokeConsentForChangedValues(contact, oldPhone != contact.PhoneE164, oldEmail != contact.Email, ct);
            crm.Activity(contact.LeadId, LeadActivityType.ContactUpdated, "Contact updated" + (revoked > 0 ? $"; {revoked} consent record(s) revoked because the phone/e-mail changed." : "."));
            crm.Record("Contact.Updated", "Contact", contact.Id, $"Contact of lead '{lead.Name}' updated.");
            await db.SaveChangesAsync(ct);
            await scorer.RescoreAsync([contact.LeadId], ct);
            return Result.Success((await ToDtos([contact], ct))[0]);
        }, logger);

    internal async Task<int> RevokeConsentForChangedValues(Contact contact, bool phoneChanged, bool emailChanged, CancellationToken ct)
    {
        if (!phoneChanged && !emailChanged) return 0;
        var tenantId = crm.TenantId;
        var records = await crm.Db.ConsentRecords.AsNoTracking().Where(r => r.TenantId == tenantId && r.ContactId == contact.Id).ToListAsync(ct);
        var n = 0;
        foreach (var g in records.GroupBy(r => (r.Channel, r.Purpose)))
        {
            var emailChannel = g.Key.Channel == ConsentChannel.Email;
            if (emailChannel ? !emailChanged : !phoneChanged) continue;
            var latest = g.OrderByDescending(r => r.OccurredAtUtc).ThenByDescending(r => r.IsRevocation).First();
            if (latest.Status != ConsentStatus.Granted) continue;
            crm.Db.ConsentRecords.Add(ConsentRecord.Create(tenantId, contact.Id, g.Key.Channel, g.Key.Purpose, ConsentStatus.Revoked, "system: contact detail changed",
                null, "Consent was given for the previous phone number or e-mail address.", crm.Now, crm.Now, crm.UserId));
            n++;
        }
        return n;
    }

    public Task<Result> RemoveAsync(Guid contactId, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            var contact = await db.Contacts.FirstOrDefaultAsync(c => c.Id == contactId && c.TenantId == tenantId && !c.IsDeleted, ct);
            if (contact is null) return Result.Failure(Error.NotFound("Contact"));
            var lead = await db.Leads.AsNoTracking().FirstAsync(l => l.Id == contact.LeadId && l.TenantId == tenantId, ct);
            if (lead.IsArchived) return Result.Failure(Error.Validation("Archived leads cannot be edited. Restore the lead first."));
            var wasPrimary = contact.IsPrimary;
            contact.Remove(now, crm.Actor); // soft delete: consent history keeps pointing at it
            if (wasPrimary)
            {
                var next = await db.Contacts.Where(c => c.TenantId == tenantId && c.LeadId == contact.LeadId && !c.IsDeleted && c.Id != contactId).OrderBy(c => c.CreatedAtUtc).FirstOrDefaultAsync(ct);
                next?.SetPrimary(true, now);
            }
            crm.Activity(contact.LeadId, LeadActivityType.ContactRemoved, "Contact removed.");
            crm.Record("Contact.Removed", "Contact", contactId, $"Contact removed from lead '{lead.Name}'.");
            await db.SaveChangesAsync(ct);
            await scorer.RescoreAsync([contact.LeadId], ct);
            return Result.Success();
        }, logger);
}

public sealed class ConsentService(CrmContext crm, ILogger<ConsentService> logger) : IConsentService
{
    public async Task<Result<PagedResult<ConsentRecordDto>>> HistoryForContactAsync(Guid contactId, int page, int pageSize, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        if (!await crm.Db.Contacts.AnyAsync(c => c.Id == contactId && c.TenantId == tenantId, ct)) return Result.Failure<PagedResult<ConsentRecordDto>>(Error.NotFound("Contact"));
        return Result.Success(await History(crm.Db.ConsentRecords.AsNoTracking().Where(r => r.TenantId == tenantId && r.ContactId == contactId), page, pageSize, ct));
    }

    public async Task<Result<PagedResult<ConsentRecordDto>>> HistoryForLeadAsync(Guid leadId, int page, int pageSize, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        if (!await crm.Db.Leads.AnyAsync(l => l.Id == leadId && l.TenantId == tenantId, ct)) return Result.Failure<PagedResult<ConsentRecordDto>>(Error.NotFound("Lead"));
        var q = from r in crm.Db.ConsentRecords.AsNoTracking() join c in crm.Db.Contacts.AsNoTracking() on r.ContactId equals c.Id
                where r.TenantId == tenantId && c.TenantId == tenantId && c.LeadId == leadId select r;
        return Result.Success(await History(q, page, pageSize, ct));
    }

    private async Task<PagedResult<ConsentRecordDto>> History(IQueryable<ConsentRecord> q, int page, int pageSize, CancellationToken ct)
    {
        var (p, size) = CrmFlow.Page(page, pageSize);
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(r => r.OccurredAtUtc).ThenByDescending(r => r.RecordedAtUtc).ThenByDescending(r => r.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var tenantId = crm.TenantId; var contactIds = rows.Select(r => r.ContactId).Distinct().ToList();
        var names = await crm.Db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && contactIds.Contains(c.Id)).Select(c => new { c.Id, c.Name, c.PhoneE164, c.Email }).ToDictionaryAsync(c => c.Id, ct);
        var users = await crm.UserNamesAsync(rows.Select(r => r.RecordedByUserId), ct);
        return new PagedResult<ConsentRecordDto>(rows.Select(r => new ConsentRecordDto(r.Id, r.ContactId, names.TryGetValue(r.ContactId, out var n) ? n.Name ?? n.PhoneE164 ?? n.Email : null, r.Channel, r.Purpose, r.Status,
            r.Source, r.EvidenceRef, r.Note, r.OccurredAtUtc, r.RecordedAtUtc, r.RecordedByUserId is { } u ? users.GetValueOrDefault(u) : null)).ToList(), p, size, total);
    }

    public Task<Result<ConsentRecordDto>> RecordAsync(Guid contactId, RecordConsentRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId && c.TenantId == tenantId && !c.IsDeleted, ct);
            if (contact is null) return Result.Failure<ConsentRecordDto>(Error.NotFound("Contact"));
            if (!Enum.IsDefined(r.Channel) || !Enum.IsDefined(r.Purpose) || !Enum.IsDefined(r.Status)) return Result.Failure<ConsentRecordDto>(Error.Validation("Channel, purpose and status are required."));
            var value = contact.ValueFor(r.Channel);
            if (value is null) return Result.Failure<ConsentRecordDto>(Error.Validation(r.Channel == ConsentChannel.Email ? "This contact has no e-mail address." : "This contact has no phone number."));

            if (r.Status == ConsentStatus.Granted)
            {
                var sup = ToSuppression(r.Channel);
                if (await db.SuppressionEntries.AnyAsync(s => s.TenantId == tenantId && s.ValueNormalized == value && (s.Channel == SuppressionChannel.Any || s.Channel == sup), ct))
                    return Result.Failure<ConsentRecordDto>(Error.Conflict("This phone number or e-mail address is on the suppression list. Remove the suppression entry first if that is intended."));
            }

            var record = ConsentRecord.Create(tenantId, contactId, r.Channel, r.Purpose, r.Status, r.Source, r.EvidenceRef, r.Note, r.OccurredAtUtc?.ToUniversalTime() ?? now, now, crm.UserId);
            db.ConsentRecords.Add(record);
            var lead = await db.Leads.AsNoTracking().Where(l => l.Id == contact.LeadId && l.TenantId == tenantId).Select(l => l.Name).FirstAsync(ct);
            crm.Activity(contact.LeadId, LeadActivityType.ConsentRecorded, $"Consent {r.Status.ToString().ToLowerInvariant()} for {r.Channel} / {r.Purpose}.");
            crm.Record(r.Status == ConsentStatus.Granted ? "Consent.Granted" : "Consent.Revoked", "ConsentRecord", record.Id, $"Consent {r.Status.ToString().ToLowerInvariant()} for {r.Channel}/{r.Purpose} (lead '{lead}', source: {record.Source}).");
            // a WhatsApp marketing grant may make the contact eligible: the workflow consumer re-checks real eligibility before anything starts
            if (r.Status == ConsentStatus.Granted && r.Channel == ConsentChannel.WhatsApp && r.Purpose == ConsentPurpose.Marketing)
                Workflows.WorkflowEvents.Emit(crm, LeadPilot.Application.Workflows.WorkflowEventTypes.LeadContactEligible, contact.LeadId, contactId);
            await db.SaveChangesAsync(ct);
            var users = await crm.UserNamesAsync([record.RecordedByUserId], ct);
            return Result.Success(new ConsentRecordDto(record.Id, contactId, contact.Name ?? contact.PhoneE164 ?? contact.Email, record.Channel, record.Purpose, record.Status, record.Source,
                record.EvidenceRef, record.Note, record.OccurredAtUtc, record.RecordedAtUtc, record.RecordedByUserId is { } u ? users.GetValueOrDefault(u) : null));
        }, logger);

    internal static SuppressionChannel ToSuppression(ConsentChannel c) => c switch
    {
        ConsentChannel.WhatsApp => SuppressionChannel.WhatsApp, ConsentChannel.Sms => SuppressionChannel.Sms, ConsentChannel.Email => SuppressionChannel.Email, _ => SuppressionChannel.Phone
    };
}

public sealed class SuppressionService(CrmContext crm, ILogger<SuppressionService> logger) : ISuppressionService
{
    public async Task<PagedResult<SuppressionDto>> ListAsync(SuppressionQuery query, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(query.Page, query.PageSize);
        var q = crm.Db.SuppressionEntries.AsNoTracking().Where(s => s.TenantId == tenantId);
        if (query.Channel is { } ch) q = q.Where(s => s.Channel == ch);
        if (query.Reason is { } re) q = q.Where(s => s.Reason == re);
        if (!string.IsNullOrWhiteSpace(query.Search)) { var s = query.Search.Trim().ToLowerInvariant(); q = q.Where(x => x.ValueNormalized.Contains(s) || (x.Note != null && x.Note.Contains(s))); }
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(s => s.CreatedAtUtc).ThenBy(s => s.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var users = await crm.UserNamesAsync(rows.Select(r => r.CreatedByUserId), ct);
        return new PagedResult<SuppressionDto>(rows.Select(r => new SuppressionDto(r.Id, r.Channel, r.ValueNormalized, r.Reason, r.Note, r.CreatedAtUtc, r.CreatedByUserId is { } u ? users.GetValueOrDefault(u) : null)).ToList(), page, size, total);
    }

    public Task<Result<SuppressionDto>> AddAsync(AddSuppressionRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            if (!Enum.IsDefined(r.Channel) || !Enum.IsDefined(r.Reason)) return Result.Failure<SuppressionDto>(Error.Validation("Channel and reason are required."));
            var value = SuppressionEntry.NormalizeValue(r.Channel, r.Value, await crm.DefaultCountryCodeAsync(ct));
            if (value is null) return Result.Failure<SuppressionDto>(Error.Validation(r.Channel == SuppressionChannel.Email ? "Enter a valid e-mail address." : "Enter a valid phone number in international format, e.g. +966501234567."));
            if (await crm.Db.SuppressionEntries.AnyAsync(s => s.TenantId == tenantId && s.Channel == r.Channel && s.ValueNormalized == value, ct))
                return Result.Failure<SuppressionDto>(Error.Conflict("That value is already on the suppression list for this channel."));
            var entry = SuppressionEntry.Create(tenantId, r.Channel, value, r.Reason, r.Note, crm.UserId, crm.Now);
            crm.Db.SuppressionEntries.Add(entry);
            crm.Record("Suppression.Added", "SuppressionEntry", entry.Id, $"{CrmContext.Mask(value)} suppressed on {r.Channel} ({r.Reason}).");
            await crm.Db.SaveChangesAsync(ct);
            var users = await crm.UserNamesAsync([entry.CreatedByUserId], ct);
            return Result.Success(new SuppressionDto(entry.Id, entry.Channel, entry.ValueNormalized, entry.Reason, entry.Note, entry.CreatedAtUtc, entry.CreatedByUserId is { } u ? users.GetValueOrDefault(u) : null));
        }, logger);

    public Task<Result> RemoveAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var entry = await crm.Db.SuppressionEntries.FirstOrDefaultAsync(s => s.Id == id && s.TenantId == tenantId, ct);
            if (entry is null) return Result.Failure(Error.NotFound("Suppression entry"));
            crm.Db.SuppressionEntries.Remove(entry);
            crm.Record("Suppression.Removed", "SuppressionEntry", id, $"{CrmContext.Mask(entry.ValueNormalized)} removed from the suppression list ({entry.Channel}, {entry.Reason}).");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success();
        }, logger);
}

public sealed class ContactEligibilityService(CrmContext crm) : IContactEligibilityService
{
    public async Task<EligibilityDecision> CheckAsync(Guid contactId, ConsentChannel channel, ConsentPurpose purpose, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var contact = await db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId && c.TenantId == tenantId, ct);
        if (contact is null) return EligibilityDecision.NotFound;
        var lead = await (from l in db.Leads.AsNoTracking()
                          where l.Id == contact.LeadId && l.TenantId == tenantId
                          select new { l.IsArchived, Excluded = db.PipelineStages.Any(s => s.Id == l.PipelineStageId && s.Kind == StageKind.Suppressed) }).FirstOrDefaultAsync(ct);
        var raw = contact.ValueFor(channel);
        var normalized = EligibilityRules.NormalizeFor(channel, raw, await crm.DefaultCountryCodeAsync(ct));
        var sup = ConsentService.ToSuppression(channel);
        var suppressed = normalized is not null && await db.SuppressionEntries.AnyAsync(s => s.TenantId == tenantId && s.ValueNormalized == normalized && (s.Channel == SuppressionChannel.Any || s.Channel == sup), ct);
        var latest = await db.ConsentRecords.AsNoTracking().Where(r => r.TenantId == tenantId && r.ContactId == contactId && r.Channel == channel && r.Purpose == purpose)
            .OrderByDescending(r => r.OccurredAtUtc).ThenByDescending(r => r.IsRevocation).Select(r => (ConsentStatus?)r.Status).FirstOrDefaultAsync(ct);
        return EligibilityRules.Evaluate(new EligibilityFacts(true, contact.IsDeleted, lead?.IsArchived ?? true, lead?.Excluded ?? false, raw, normalized, suppressed, latest));
    }

    public async Task<IReadOnlyDictionary<Guid, EligibilityDecision>> CheckManyAsync(IReadOnlyCollection<Guid> contactIds, ConsentChannel channel, ConsentPurpose purpose, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var ids = contactIds.Distinct().ToList();
        var result = new Dictionary<Guid, EligibilityDecision>(ids.Count);
        if (ids.Count == 0) return result;
        var contacts = await db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && ids.Contains(c.Id)).ToListAsync(ct);
        var leadIds = contacts.Select(c => c.LeadId).Distinct().ToList();
        var leads = await (from l in db.Leads.AsNoTracking()
                           where l.TenantId == tenantId && leadIds.Contains(l.Id)
                           select new { l.Id, l.IsArchived, Excluded = db.PipelineStages.Any(s => s.Id == l.PipelineStageId && s.Kind == StageKind.Suppressed) }).ToDictionaryAsync(l => l.Id, ct);
        var country = await crm.DefaultCountryCodeAsync(ct);
        var normalized = contacts.ToDictionary(c => c.Id, c => EligibilityRules.NormalizeFor(channel, c.ValueFor(channel), country));
        var values = normalized.Values.Where(v => v is not null).Select(v => v!).Distinct().ToList();
        var sup = ConsentService.ToSuppression(channel);
        var suppressedValues = values.Count == 0 ? [] : (await db.SuppressionEntries.AsNoTracking()
            .Where(s => s.TenantId == tenantId && values.Contains(s.ValueNormalized) && (s.Channel == SuppressionChannel.Any || s.Channel == sup)).Select(s => s.ValueNormalized).ToListAsync(ct)).ToHashSet();
        var consent = (await db.ConsentRecords.AsNoTracking().Where(r => r.TenantId == tenantId && ids.Contains(r.ContactId) && r.Channel == channel && r.Purpose == purpose)
                .Select(r => new { r.ContactId, r.Status, r.OccurredAtUtc, r.IsRevocation }).ToListAsync(ct))
            .GroupBy(r => r.ContactId).ToDictionary(g => g.Key, g => (ConsentStatus?)g.OrderByDescending(r => r.OccurredAtUtc).ThenByDescending(r => r.IsRevocation).First().Status);
        foreach (var id in ids)
        {
            var contact = contacts.FirstOrDefault(c => c.Id == id);
            if (contact is null) { result[id] = EligibilityDecision.NotFound; continue; }
            leads.TryGetValue(contact.LeadId, out var lead);
            var norm = normalized[id];
            result[id] = EligibilityRules.Evaluate(new EligibilityFacts(true, contact.IsDeleted, lead?.IsArchived ?? true, lead?.Excluded ?? false, contact.ValueFor(channel), norm,
                norm is not null && suppressedValues.Contains(norm), consent.GetValueOrDefault(id)));
        }
        return result;
    }

    public async Task<PagedResult<EligibleContactDto>> QueryAudienceAsync(AudienceQuery q, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(q.Page, q.PageSize);
        var channel = q.Channel; var purpose = q.Purpose; var sup = ConsentService.ToSuppression(channel);
        if (!Enum.IsDefined(channel) || !Enum.IsDefined(purpose)) return new PagedResult<EligibleContactDto>([], page, size, 0);

        var contacts = db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && !c.IsDeleted);
        // the value a channel sends to, present and not suppressed (on this channel or on every channel)
        contacts = channel == ConsentChannel.Email
            ? contacts.Where(c => c.Email != null && !db.SuppressionEntries.Any(s => s.TenantId == tenantId && s.ValueNormalized == c.Email && (s.Channel == SuppressionChannel.Any || s.Channel == sup)))
            : contacts.Where(c => c.PhoneE164 != null && !db.SuppressionEntries.Any(s => s.TenantId == tenantId && s.ValueNormalized == c.PhoneE164 && (s.Channel == SuppressionChannel.Any || s.Channel == sup)));
        // latest consent decision is "granted" (a revocation wins a tie)
        contacts = contacts.Where(c => db.ConsentRecords.Where(r => r.TenantId == tenantId && r.ContactId == c.Id && r.Channel == channel && r.Purpose == purpose)
            .OrderByDescending(r => r.OccurredAtUtc).ThenByDescending(r => r.IsRevocation).Select(r => r.Status).FirstOrDefault() == ConsentStatus.Granted);

        var leads = db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && !l.IsArchived
            && !db.PipelineStages.Any(s => s.Id == l.PipelineStageId && s.Kind == StageKind.Suppressed));
        if (q.CategoryId is { } cat) leads = leads.Where(l => l.CategoryId == cat);
        if (q.StageId is { } stage) leads = leads.Where(l => l.PipelineStageId == stage);
        if (q.OwnerUserId is { } owner) leads = leads.Where(l => l.OwnerUserId == owner);
        if (q.MinScore is { } min) leads = leads.Where(l => l.Score >= min);
        if (q.TagId is { } tag) leads = leads.Where(l => db.LeadTagMaps.Any(m => m.TenantId == tenantId && m.LeadId == l.Id && m.TagId == tag));

        var joined = from c in contacts join l in leads on c.LeadId equals l.Id select new { c, l };
        var total = await joined.LongCountAsync(ct);
        var rows = await joined.OrderByDescending(x => x.l.Score).ThenBy(x => x.l.NormalizedName).ThenBy(x => x.c.Id).Skip((page - 1) * size).Take(size)
            .Select(x => new { ContactId = x.c.Id, LeadId = x.l.Id, LeadName = x.l.Name, ContactName = x.c.Name, Phone = x.c.PhoneE164, x.c.Email, x.l.Score }).ToListAsync(ct);
        return new PagedResult<EligibleContactDto>(rows.Select(r => new EligibleContactDto(r.ContactId, r.LeadId, r.LeadName, r.ContactName, (channel == ConsentChannel.Email ? r.Email : r.Phone) ?? "", r.Score)).ToList(), page, size, total);
    }
}
