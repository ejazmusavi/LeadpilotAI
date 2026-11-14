using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Services.Crm;

public sealed class LeadService(CrmContext crm, DuplicateDetector duplicates, ILeadScorer scorer, IPipelineService pipelines, IOptions<CrmOptions> options, ILogger<LeadService> logger) : ILeadService
{
    private static readonly LeadSourceType[] ClientSourceTypes = [LeadSourceType.Manual, LeadSourceType.Api, LeadSourceType.Referral, LeadSourceType.Website, LeadSourceType.Other];

    // ---------- reads ----------
    public async Task<PagedResult<LeadListItemDto>> ListAsync(LeadQuery query, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var (page, size) = CrmFlow.Page(query.Page, query.PageSize);
        var filtered = LeadQueries.Filter(db, tenantId, query);
        var total = await filtered.LongCountAsync(ct);
        var rows = await LeadQueries.Sort(db, filtered, query).Skip((page - 1) * size).Take(size)
            .Select(l => new
            {
                l.Id, l.Name, l.City, l.Country, l.Website, l.OwnerUserId, l.Score, l.Confidence, l.IsArchived, l.CreatedAtUtc, l.LastActivityAtUtc,
                Category = db.LeadCategories.Where(c => c.Id == l.CategoryId).Select(c => c.Name).FirstOrDefault(),
                Stage = db.PipelineStages.Where(s => s.Id == l.PipelineStageId).Select(s => s.Name).FirstOrDefault(),
                Kind = db.PipelineStages.Where(s => s.Id == l.PipelineStageId).Select(s => s.Kind).FirstOrDefault(),
                Status = db.LeadStatuses.Where(s => s.Id == l.StatusId).Select(s => s.Name).FirstOrDefault(),
            }).ToListAsync(ct);
        var items = await Enrich(rows.Select(r => (r.Id, r.OwnerUserId)).ToList(), ct);
        var list = rows.Select(r => new LeadListItemDto(r.Id, r.Name, r.Category, r.City, r.Country, r.Website, r.Stage ?? "", r.Kind, r.Status ?? "", r.OwnerUserId,
            r.OwnerUserId is { } o ? items.Owners.GetValueOrDefault(o) : null, r.Score, r.Confidence, r.IsArchived,
            items.Primary.GetValueOrDefault(r.Id)?.Name, items.Primary.GetValueOrDefault(r.Id)?.PhoneE164, items.Primary.GetValueOrDefault(r.Id)?.Email,
            items.Tags[r.Id].OrderBy(t => t).ToList(), r.CreatedAtUtc, r.LastActivityAtUtc)).ToList();
        return new PagedResult<LeadListItemDto>(list, page, size, total);
    }

    private sealed record Enriched(Dictionary<Guid, string> Owners, Dictionary<Guid, Contact> Primary, ILookup<Guid, string> Tags);

    private async Task<Enriched> Enrich(IReadOnlyList<(Guid Id, Guid? Owner)> leads, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var ids = leads.Select(l => l.Id).ToList();
        var owners = await crm.UserNamesAsync(leads.Select(l => l.Owner), ct);
        var contacts = ids.Count == 0 ? [] : await db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && !c.IsDeleted && ids.Contains(c.LeadId)).ToListAsync(ct);
        var primary = contacts.GroupBy(c => c.LeadId).ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.IsPrimary).ThenBy(c => c.CreatedAtUtc).First());
        var tags = ids.Count == 0 ? Enumerable.Empty<(Guid, string)>().ToLookup(x => x.Item1, x => x.Item2)
            : (await (from m in db.LeadTagMaps.AsNoTracking() join t in db.LeadTags.AsNoTracking() on m.TagId equals t.Id where m.TenantId == tenantId && ids.Contains(m.LeadId) select new { m.LeadId, t.Name }).ToListAsync(ct)).ToLookup(x => x.LeadId, x => x.Name);
        return new Enriched(owners, primary, tags);
    }

    public async Task<Result<LeadDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var lead = await crm.Db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id && l.TenantId == tenantId, ct); // explicit ownership check
        return lead is null ? Result.Failure<LeadDto>(Error.NotFound("Lead")) : Result.Success(await ToDto(lead, ct));
    }

    private async Task<LeadDto> ToDto(Lead l, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var category = l.CategoryId is { } cid ? await db.LeadCategories.AsNoTracking().Where(c => c.Id == cid && c.TenantId == tenantId).Select(c => c.Name).FirstOrDefaultAsync(ct) : null;
        var pipeline = await db.Pipelines.AsNoTracking().Where(p => p.Id == l.PipelineId && p.TenantId == tenantId).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? "";
        var stage = await db.PipelineStages.AsNoTracking().Where(s => s.Id == l.PipelineStageId && s.TenantId == tenantId).Select(s => new { s.Name, s.Kind }).FirstAsync(ct);
        var status = await db.LeadStatuses.AsNoTracking().Where(s => s.Id == l.StatusId && s.TenantId == tenantId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? "";
        var owner = l.OwnerUserId is { } oid ? (await crm.UserNamesAsync([oid], ct)).GetValueOrDefault(oid) : null;
        var sources = await db.LeadSources.AsNoTracking().Where(s => s.TenantId == tenantId && s.LeadId == l.Id).OrderBy(s => s.RetrievedAtUtc).Select(s => new LeadSourceDto(s.Type, s.Name, s.Url, s.ExternalId, s.RetrievedAtUtc)).ToListAsync(ct);
        var tags = await (from m in db.LeadTagMaps.AsNoTracking() join t in db.LeadTags.AsNoTracking() on m.TagId equals t.Id where m.TenantId == tenantId && m.LeadId == l.Id orderby t.Name select new LookupItem(t.Id, t.Name)).ToListAsync(ct);
        var contactCount = await db.Contacts.CountAsync(c => c.TenantId == tenantId && c.LeadId == l.Id && !c.IsDeleted, ct);
        var breakdown = await db.LeadScores.AsNoTracking().Where(s => s.TenantId == tenantId && s.LeadId == l.Id).OrderByDescending(s => s.CalculatedAtUtc).Select(s => s.Breakdown).FirstOrDefaultAsync(ct);
        return new LeadDto(l.Id, l.Name, l.CategoryId, category, l.Country, l.Province, l.City, l.District, l.Area, l.Address, l.Website, l.WebsiteDomain, l.PipelineId, pipeline,
            l.PipelineStageId, stage.Name, stage.Kind, l.StatusId, status, l.OwnerUserId, owner, l.Score, l.ScoreRuleVersion, l.ScoredAtUtc,
            breakdown is null ? [] : ScoreResult.ParseMatches(breakdown), l.Confidence, l.PrimarySourceType, sources, tags, contactCount, l.IsArchived, l.ArchivedAtUtc,
            l.CreatedAtUtc, l.CreatedBy, l.UpdatedAtUtc, l.LastActivityAtUtc, l.Version);
    }

    // ---------- create / update ----------
    public Task<Result<LeadDto>> CreateAsync(CreateLeadRequest r, CancellationToken ct = default)
    {
        if (r.SourceType is { } st && !ClientSourceTypes.Contains(st)) return Task.FromResult(Result.Failure<LeadDto>(Error.Validation($"Source type '{st}' is reserved for imports and discovery.")));
        return CreateCore(r, r.SourceType ?? LeadSourceType.Manual, null, null, null, ct);
    }

    /// <summary>Server-side only (discovery approval): creates a lead whose source is a system source type with its external id, attribution and retrieval time.</summary>
    public Task<Result<LeadDto>> CreateFromSourceAsync(CreateLeadRequest r, LeadSourceType source, string? externalId, string? attribution, DateTime? retrievedAtUtc, CancellationToken ct = default) =>
        CreateCore(r, source, externalId, attribution, retrievedAtUtc, ct);

    private Task<Result<LeadDto>> CreateCore(CreateLeadRequest r, LeadSourceType source, string? externalId, string? attribution, DateTime? retrievedAtUtc, CancellationToken ct) =>
        CrmFlow.GuardAsync(async () =>
        {
            await crm.EnsureDefaultsAsync(ct);
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            if (string.IsNullOrWhiteSpace(r.Name)) return Result.Failure<LeadDto>(Error.Validation("Lead name is required."));

            if (await CheckCategory(r.CategoryId, null, ct) is { } catErr) return Result.Failure<LeadDto>(catErr);

            // pipeline, stage, status
            PipelineStage? stage;
            if (r.StageId is { } sid)
            {
                stage = await db.PipelineStages.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid && s.TenantId == tenantId, ct);
                if (stage is null) return Result.Failure<LeadDto>(Error.Validation("Stage not found."));
                if (!await db.Pipelines.AnyAsync(p => p.Id == stage.PipelineId && p.TenantId == tenantId && p.IsActive, ct)) return Result.Failure<LeadDto>(Error.Validation("That pipeline is not active."));
            }
            else
            {
                stage = await (from s in db.PipelineStages.AsNoTracking() join p in db.Pipelines.AsNoTracking() on s.PipelineId equals p.Id
                               where s.TenantId == tenantId && p.TenantId == tenantId && p.IsDefault && p.IsActive orderby s.Position select s).FirstOrDefaultAsync(ct);
                if (stage is null) return Result.Failure<LeadDto>(Error.Validation("This workspace has no default pipeline with stages. Configure one under Pipelines."));
            }
            LeadStatus? status = r.StatusId is { } stid
                ? await db.LeadStatuses.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stid && s.TenantId == tenantId && s.IsActive, ct)
                : await db.LeadStatuses.AsNoTracking().Where(s => s.TenantId == tenantId && s.IsActive).OrderByDescending(s => s.IsDefault).ThenBy(s => s.SortOrder).FirstOrDefaultAsync(ct);
            if (status is null) return Result.Failure<LeadDto>(Error.Validation(r.StatusId is null ? "No lead status is configured." : "Status not found."));

            // owner
            if (r.OwnerUserId is { } owner)
            {
                if (owner != crm.UserId && !await crm.Permissions.HasAsync(PermissionCodes.LeadsAssign, ct)) return Result.Failure<LeadDto>(Error.Forbidden("Assigning leads to other people requires the Assign leads permission."));
                if (!await crm.IsActiveMemberAsync(owner, ct)) return Result.Failure<LeadDto>(Error.Validation("The owner must be an active member of this workspace."));
            }

            // contacts
            var contacts = new List<Contact>();
            var phones = new List<string>();
            var country = await crm.DefaultCountryCodeAsync(ct);
            var inputs = r.Contacts ?? [];
            if (inputs.Count > 20) return Result.Failure<LeadDto>(Error.Validation("At most 20 contacts can be created with a lead."));
            var lead = Lead.Create(tenantId, new LeadProfile(r.Name, r.CategoryId, r.Country, r.Province, r.City, r.District, r.Area, r.Address, r.Website, r.Confidence),
                stage.PipelineId, stage.Id, status.Id, r.OwnerUserId, source, now, crm.Actor);
            for (var i = 0; i < inputs.Count; i++)
            {
                var c = inputs[i];
                try
                {
                    var e164 = Normalizer.PhoneE164(c.Phone, country);
                    var contact = Contact.Create(tenantId, lead.Id, c.Name, c.Role, c.Phone, e164, c.Email, c.DataOrigin, c.IsPrimary, now, crm.Actor);
                    if (contact.PhoneE164 is not null && contacts.Any(x => x.PhoneE164 == contact.PhoneE164)) return Result.Failure<LeadDto>(Error.Validation($"Contact {i + 1}: the phone number is used twice."));
                    if (contact.Email is not null && contacts.Any(x => x.Email == contact.Email)) return Result.Failure<LeadDto>(Error.Validation($"Contact {i + 1}: the e-mail address is used twice."));
                    contacts.Add(contact);
                    if (contact.PhoneE164 is not null) phones.Add(contact.PhoneE164);
                }
                catch (DomainException ex) { return Result.Failure<LeadDto>(Error.Validation($"Contact {i + 1}: {ex.Message}")); }
            }
            if (contacts.Count > 0 && !contacts.Any(c => c.IsPrimary)) contacts[0].SetPrimary(true, now);
            foreach (var extra in contacts.Where(c => c.IsPrimary).Skip(1)) extra.SetPrimary(false, now);

            // tags
            var tagIds = (r.TagIds ?? []).Distinct().ToList();
            if (tagIds.Count > 0 && await db.LeadTags.CountAsync(t => t.TenantId == tenantId && tagIds.Contains(t.Id), ct) != tagIds.Count) return Result.Failure<LeadDto>(Error.Validation("One or more tags were not found."));

            // duplicates
            if (!r.AllowDuplicate)
            {
                var found = await duplicates.FindAsync(new DuplicateProbe(r.Name, r.City, r.Website, phones), ct);
                if (found.Count > 0) return Result.Failure<LeadDto>(Error.Duplicate(DescribeDuplicates(found)));
            }

            db.Leads.Add(lead);
            db.LeadSources.Add(LeadSource.Create(tenantId, lead.Id, lead.PrimarySourceType, externalId, r.SourceName, r.SourceUrl, attribution, retrievedAtUtc ?? now));
            foreach (var c in contacts) db.Contacts.Add(c);
            foreach (var t in tagIds) db.LeadTagMaps.Add(LeadTagMap.Create(tenantId, lead.Id, t, now));
            crm.Activity(lead.Id, LeadActivityType.Created, $"Lead created ({lead.PrimarySourceType}).");
            crm.Record("Lead.Created", "Lead", lead.Id, $"Lead '{lead.Name}' created.");
            await db.SaveChangesAsync(ct);
            await scorer.RescoreAsync([lead.Id], ct);
            return await GetAsync(lead.Id, ct);
        }, logger);

    public static string DescribeDuplicates(IReadOnlyList<DuplicateMatch> found) =>
        "Possible duplicate of " + string.Join("; ", found.Take(3).Select(m => $"'{m.LeadName}' ({string.Join(", ", m.Reasons)}{(m.IsArchived ? ", archived" : "")})")) + (found.Count > 3 ? $" and {found.Count - 3} more" : "") + ".";

    public Task<Result<LeadDto>> UpdateAsync(Guid id, UpdateLeadRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.TenantId == tenantId, ct);
            if (lead is null) return Result.Failure<LeadDto>(Error.NotFound("Lead"));
            if (r.ExpectedVersion is { } v && v != lead.Version) return Result.Failure<LeadDto>(Error.Conflict("The lead was changed by someone else. Reload it and try again."));
            if (await CheckCategory(r.CategoryId, lead.CategoryId, ct) is { } catErr) return Result.Failure<LeadDto>(catErr);

            var changed = lead.UpdateProfile(new LeadProfile(r.Name, r.CategoryId, r.Country, r.Province, r.City, r.District, r.Area, r.Address, r.Website, r.Confidence), now, crm.Actor);
            if (r.StatusId is { } stid && stid != lead.StatusId)
            {
                if (!await db.LeadStatuses.AnyAsync(s => s.Id == stid && s.TenantId == tenantId && s.IsActive, ct)) return Result.Failure<LeadDto>(Error.Validation("Status not found."));
                lead.SetStatus(stid, now, crm.Actor); changed = [.. changed, "Status"];
            }
            if (changed.Count == 0) return await GetAsync(id, ct);
            crm.Activity(id, LeadActivityType.Updated, "Updated: " + string.Join(", ", changed));
            crm.Record("Lead.Updated", "Lead", id, $"Lead '{lead.Name}' updated ({string.Join(", ", changed)}).");
            await db.SaveChangesAsync(ct);
            await scorer.RescoreAsync([id], ct);
            return await GetAsync(id, ct);
        }, logger);

    private async Task<Error?> CheckCategory(Guid? categoryId, Guid? current, CancellationToken ct)
    {
        if (categoryId is null || categoryId == current) return null;
        var tenantId = crm.TenantId;
        return await crm.Db.LeadCategories.AnyAsync(c => c.Id == categoryId && c.TenantId == tenantId && c.IsActive, ct) ? null : Error.Validation("Category not found.");
    }

    // ---------- lifecycle ----------
    public Task<Result> ArchiveAsync(Guid id, CancellationToken ct = default) => SetArchived(id, archive: true, ct);
    public Task<Result> RestoreAsync(Guid id, CancellationToken ct = default) => SetArchived(id, archive: false, ct);

    private Task<Result> SetArchived(Guid id, bool archive, CancellationToken ct) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var lead = await crm.Db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.TenantId == tenantId, ct);
            if (lead is null) return Result.Failure(Error.NotFound("Lead"));
            if (archive) lead.Archive(crm.Now, crm.Actor); else lead.Restore(crm.Now, crm.Actor);
            crm.Activity(id, archive ? LeadActivityType.Archived : LeadActivityType.Restored, archive ? "Lead archived." : "Lead restored.");
            crm.Record(archive ? "Lead.Archived" : "Lead.Restored", "Lead", id, $"Lead '{lead.Name}' {(archive ? "archived" : "restored")}.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success();
        }, logger);

    public Task<Result<LeadDto>> ChangeStageAsync(Guid id, Guid stageId, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var lead = await crm.Db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.TenantId == tenantId, ct);
            if (lead is null) return Result.Failure<LeadDto>(Error.NotFound("Lead"));
            var error = await ApplyStage(lead, stageId, ct);
            if (error is not null) return Result.Failure<LeadDto>(error);
            await crm.Db.SaveChangesAsync(ct);
            await scorer.RescoreAsync([id], ct);
            return await GetAsync(id, ct);
        }, logger);

    /// <summary>Moves a tracked lead to a stage of its own pipeline, recording activity + audit. Caller saves.</summary>
    private async Task<Error?> ApplyStage(Lead lead, Guid stageId, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var target = await crm.Db.PipelineStages.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stageId && s.TenantId == tenantId, ct);
        if (target is null) return Error.Validation("Stage not found.");
        if (target.PipelineId != lead.PipelineId) return Error.Validation("That stage belongs to a different pipeline.");
        if (target.Id == lead.PipelineStageId) return null;
        var from = await crm.Db.PipelineStages.AsNoTracking().Where(s => s.Id == lead.PipelineStageId && s.TenantId == tenantId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? "?";
        lead.MoveToStage(lead.PipelineId, target.Id, crm.Now, crm.Actor);
        crm.Activity(lead.Id, LeadActivityType.StageChanged, $"Stage changed from {from} to {target.Name}.", new { from = lead.PipelineStageId, to = target.Id, fromName = from, toName = target.Name });
        crm.Record("Lead.StageChanged", "Lead", lead.Id, $"Lead '{lead.Name}' moved from '{from}' to '{target.Name}'.");
        return null;
    }

    public Task<Result<LeadDto>> AssignAsync(Guid id, Guid? ownerUserId, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var lead = await crm.Db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.TenantId == tenantId, ct);
            if (lead is null) return Result.Failure<LeadDto>(Error.NotFound("Lead"));
            if (await ApplyOwner(lead, ownerUserId, ct) is { } error) return Result.Failure<LeadDto>(error);
            await crm.Db.SaveChangesAsync(ct);
            return await GetAsync(id, ct);
        }, logger);

    private async Task<Error?> ApplyOwner(Lead lead, Guid? ownerUserId, CancellationToken ct)
    {
        if (ownerUserId is { } o && !await crm.IsActiveMemberAsync(o, ct)) return Error.Validation("The owner must be an active member of this workspace.");
        if (lead.OwnerUserId == ownerUserId) return null;
        lead.AssignTo(ownerUserId, crm.Now, crm.Actor);
        var name = ownerUserId is { } oid ? (await crm.UserNamesAsync([oid], ct)).GetValueOrDefault(oid) ?? "a team member" : null;
        crm.Activity(lead.Id, ownerUserId is null ? LeadActivityType.Unassigned : LeadActivityType.Assigned, ownerUserId is null ? "Lead unassigned." : $"Lead assigned to {name}.");
        crm.Record(ownerUserId is null ? "Lead.Unassigned" : "Lead.Assigned", "Lead", lead.Id, $"Lead '{lead.Name}' {(ownerUserId is null ? "unassigned" : "assigned to " + name)}.");
        return null;
    }

    public async Task<Result<PagedResult<ActivityDto>>> ActivitiesAsync(Guid id, int page, int pageSize, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        if (!await db.Leads.AnyAsync(l => l.Id == id && l.TenantId == tenantId, ct)) return Result.Failure<PagedResult<ActivityDto>>(Error.NotFound("Lead"));
        var (p, size) = CrmFlow.Page(page, pageSize);
        var q = db.LeadActivities.AsNoTracking().Where(a => a.TenantId == tenantId && a.LeadId == id);
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(a => a.OccurredAtUtc).ThenByDescending(a => a.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var names = await crm.UserNamesAsync(rows.Select(r => r.ActorUserId), ct);
        return Result.Success(new PagedResult<ActivityDto>(rows.Select(a => new ActivityDto(a.Id, a.Type, a.Summary, a.ActorUserId is { } u ? names.GetValueOrDefault(u) : null, a.OccurredAtUtc)).ToList(), p, size, total));
    }

    public Task<IReadOnlyList<DuplicateMatch>> FindDuplicatesAsync(DuplicateProbe probe, CancellationToken ct = default) => duplicates.FindAsync(probe, ct);

    public async Task<IReadOnlyList<OwnerDto>> ListOwnersAsync(CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        return await (from m in crm.Db.UserTenants.AsNoTracking() join u in crm.Db.Users.AsNoTracking() on m.UserId equals u.Id
                      where m.TenantId == tenantId && m.Status == MembershipStatus.Active && u.IsActive orderby u.DisplayName
                      select new OwnerDto(u.Id, u.DisplayName, u.Email ?? "")).ToListAsync(ct);
    }

    public async Task<CrmLookupsDto> LookupsAsync(CancellationToken ct = default)
    {
        await crm.EnsureDefaultsAsync(ct);
        var db = crm.Db; var tenantId = crm.TenantId;
        var categories = await db.LeadCategories.AsNoTracking().Where(c => c.TenantId == tenantId && c.IsActive).OrderBy(c => c.Name).Select(c => new LookupItem(c.Id, c.Name)).ToListAsync(ct);
        var statuses = await db.LeadStatuses.AsNoTracking().Where(s => s.TenantId == tenantId && s.IsActive).OrderBy(s => s.SortOrder).Select(s => new LookupItem(s.Id, s.Name)).ToListAsync(ct);
        var tags = await db.LeadTags.AsNoTracking().Where(t => t.TenantId == tenantId).OrderBy(t => t.Name).Select(t => new LookupItem(t.Id, t.Name)).ToListAsync(ct);
        var pipelineList = await pipelines.ListAsync(ct);
        return new CrmLookupsDto(categories, statuses, pipelineList.FirstOrDefault(p => p.IsDefault), tags, await ListOwnersAsync(ct));
    }

    public async Task<Result<BoardDto>> BoardAsync(Guid? pipelineId, int cardsPerColumn, CancellationToken ct = default)
    {
        await crm.EnsureDefaultsAsync(ct);
        var db = crm.Db; var tenantId = crm.TenantId;
        var all = await pipelines.ListAsync(ct);
        var pipeline = pipelineId is { } pid ? all.FirstOrDefault(p => p.Id == pid) : all.FirstOrDefault(p => p.IsDefault) ?? all.FirstOrDefault();
        if (pipeline is null) return Result.Failure<BoardDto>(Error.NotFound("Pipeline"));
        var n = Math.Clamp(cardsPerColumn, 1, 100);
        var columns = new List<BoardColumnDto>();
        foreach (var stage in pipeline.Stages.OrderBy(s => s.Position))
        {
            var cards = await db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && l.PipelineStageId == stage.Id && !l.IsArchived)
                .OrderByDescending(l => l.Score).ThenBy(l => l.NormalizedName).ThenBy(l => l.Id).Take(n)
                .Select(l => new { l.Id, l.Name, l.City, l.Score, l.OwnerUserId, Category = db.LeadCategories.Where(c => c.Id == l.CategoryId).Select(c => c.Name).FirstOrDefault() }).ToListAsync(ct);
            var ids = cards.Select(c => c.Id).ToList();
            var owners = await crm.UserNamesAsync(cards.Select(c => c.OwnerUserId), ct);
            var phones = await db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && !c.IsDeleted && c.PhoneE164 != null && ids.Contains(c.LeadId)).OrderByDescending(c => c.IsPrimary).Select(c => new { c.LeadId, c.PhoneE164 }).ToListAsync(ct);
            columns.Add(new BoardColumnDto(stage, cards.Select(c => new BoardCardDto(c.Id, c.Name, c.City, c.Category, c.Score, c.OwnerUserId is { } o ? owners.GetValueOrDefault(o) : null,
                phones.FirstOrDefault(p => p.LeadId == c.Id)?.PhoneE164)).ToList()));
        }
        return Result.Success(new BoardDto(pipeline, columns, n));
    }

    // ---------- bulk ----------
    public Task<BulkResult> BulkAssignAsync(BulkAssignRequest r, CancellationToken ct = default) =>
        Bulk(r.LeadIds, PermissionCodes.LeadsAssign, "Lead.BulkAssign", ct, prepare: async () =>
        {
            if (r.OwnerUserId is { } o && !await crm.IsActiveMemberAsync(o, ct)) return "The owner must be an active member of this workspace.";
            return null;
        }, apply: (lead, _) => ApplyOwner(lead, r.OwnerUserId, ct));

    public Task<BulkResult> BulkTagAsync(BulkTagRequest r, CancellationToken ct = default)
    {
        LeadTag? tag = null; HashSet<Guid> existing = [];
        return Bulk(r.LeadIds, PermissionCodes.LeadsUpdate, "Lead.BulkTag", ct, prepare: async () =>
        {
            var tenantId = crm.TenantId;
            var wanted = (r.LeadIds ?? []).ToList();
            tag = await crm.Db.LeadTags.AsNoTracking().FirstOrDefaultAsync(t => t.Id == r.TagId && t.TenantId == tenantId, ct);
            if (tag is null) return "Tag not found.";
            existing = (await crm.Db.LeadTagMaps.Where(m => m.TenantId == tenantId && m.TagId == r.TagId && wanted.Contains(m.LeadId)).Select(m => m.LeadId).ToListAsync(ct)).ToHashSet();
            return null;
        }, apply: async (lead, _) =>
        {
            if (lead.IsArchived) return Error.Validation("Archived leads cannot be edited.");
            var tenantId = crm.TenantId;
            if (r.Remove)
            {
                if (!existing.Contains(lead.Id)) return null;
                var map = await crm.Db.LeadTagMaps.FirstAsync(m => m.TenantId == tenantId && m.LeadId == lead.Id && m.TagId == r.TagId, ct);
                crm.Db.LeadTagMaps.Remove(map);
                crm.Activity(lead.Id, LeadActivityType.TagRemoved, $"Tag '{tag!.Name}' removed.");
            }
            else
            {
                if (existing.Contains(lead.Id)) return null;
                crm.Db.LeadTagMaps.Add(LeadTagMap.Create(tenantId, lead.Id, r.TagId, crm.Now));
                crm.Activity(lead.Id, LeadActivityType.TagAdded, $"Tag '{tag!.Name}' added.");
            }
            crm.Record(r.Remove ? "Lead.TagRemoved" : "Lead.TagAdded", "Lead", lead.Id, $"Tag '{tag.Name}' {(r.Remove ? "removed from" : "added to")} lead '{lead.Name}'.");
            return null;
        });
    }

    public Task<BulkResult> BulkStageAsync(BulkStageRequest r, CancellationToken ct = default) =>
        Bulk(r.LeadIds, PermissionCodes.LeadsUpdate, "Lead.BulkStage", ct, prepare: async () =>
            await crm.Db.PipelineStages.AnyAsync(s => s.Id == r.StageId && s.TenantId == crm.TenantId, ct) ? null : "Stage not found.",
            apply: (lead, _) => ApplyStage(lead, r.StageId, ct));

    public Task<BulkResult> BulkArchiveAsync(BulkArchiveRequest r, CancellationToken ct = default) =>
        Bulk(r.LeadIds, PermissionCodes.LeadsDelete, r.Restore ? "Lead.BulkRestore" : "Lead.BulkArchive", ct, prepare: () => Task.FromResult<string?>(null), apply: (lead, _) =>
        {
            if (r.Restore) lead.Restore(crm.Now, crm.Actor); else lead.Archive(crm.Now, crm.Actor);
            crm.Activity(lead.Id, r.Restore ? LeadActivityType.Restored : LeadActivityType.Archived, r.Restore ? "Lead restored." : "Lead archived.");
            crm.Record(r.Restore ? "Lead.Restored" : "Lead.Archived", "Lead", lead.Id, $"Lead '{lead.Name}' {(r.Restore ? "restored" : "archived")}.");
            return Task.FromResult<Error?>(null);
        });

    /// <summary>
    /// Runs one operation per lead. Each id is authorized individually: unknown or other-tenant ids are reported as "not found" (indistinguishable), the
    /// actor's permission is re-checked, and domain rules (e.g. archived leads) are applied per item. Valid items are saved together.
    /// </summary>
    private async Task<BulkResult> Bulk(IReadOnlyList<Guid>? requested, string permission, string auditAction, CancellationToken ct, Func<Task<string?>> prepare, Func<Lead, CancellationToken, Task<Error?>> apply)
    {
        var ids = (requested ?? []).Distinct().ToList();
        var results = new List<BulkItemResult>();
        BulkResult Done() => new(ids.Count, results.Count(x => x.Success), results.Count(x => !x.Success), results);
        BulkResult AllFail(string message) { results.AddRange(ids.Select(i => new BulkItemResult(i, false, message))); return Done(); }

        if (ids.Count == 0) return new BulkResult(0, 0, 0, []);
        if (ids.Count > options.Value.MaxBulkItems) return AllFail($"At most {options.Value.MaxBulkItems} leads can be changed at once.");
        if (!await crm.Permissions.HasAsync(permission, ct)) return AllFail("You do not have permission to perform this action.");
        if (await prepare() is { } problem) return AllFail(problem);

        var tenantId = crm.TenantId;
        var leads = await crm.Db.Leads.Where(l => l.TenantId == tenantId && ids.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
        var touched = new List<Guid>();
        foreach (var id in ids)
        {
            if (!leads.TryGetValue(id, out var lead)) { results.Add(new BulkItemResult(id, false, "Lead not found.")); continue; }
            try
            {
                var error = await apply(lead, ct);
                results.Add(error is null ? new BulkItemResult(id, true, null) : new BulkItemResult(id, false, error.Message));
                if (error is null) touched.Add(id);
            }
            catch (DomainException ex) { results.Add(new BulkItemResult(id, false, ex.Message)); }
        }
        if (touched.Count > 0)
        {
            crm.Record(auditAction, "Lead", Guid.Empty, $"{auditAction}: {touched.Count} of {ids.Count} lead(s) changed.");
            await crm.Db.SaveChangesAsync(ct);
            await scorer.RescoreAsync(touched, ct);
        }
        return Done();
    }
}
