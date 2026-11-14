using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Services.Crm;

public sealed class NoteService(CrmContext crm, ILeadScorer scorer, ILogger<NoteService> logger) : INoteService
{
    public async Task<Result<PagedResult<NoteDto>>> ListAsync(Guid leadId, int page, int pageSize, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        if (!await crm.Db.Leads.AnyAsync(l => l.Id == leadId && l.TenantId == tenantId, ct)) return Result.Failure<PagedResult<NoteDto>>(Error.NotFound("Lead"));
        var (p, size) = CrmFlow.Page(page, pageSize);
        var q = crm.Db.LeadNotes.AsNoTracking().Where(n => n.TenantId == tenantId && n.LeadId == leadId && !n.IsDeleted);
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(n => n.CreatedAtUtc).ThenByDescending(n => n.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var users = await crm.UserNamesAsync(rows.Select(r => r.AuthorUserId), ct);
        return Result.Success(new PagedResult<NoteDto>(rows.Select(n => new NoteDto(n.Id, n.LeadId, n.Body, n.AuthorUserId is { } u ? users.GetValueOrDefault(u) : null, n.CreatedAtUtc)).ToList(), p, size, total));
    }

    public Task<Result<NoteDto>> AddAsync(Guid leadId, AddNoteRequest request, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var lead = await crm.Db.Leads.AsNoTracking().Where(l => l.Id == leadId && l.TenantId == tenantId).Select(l => new { l.Name }).FirstOrDefaultAsync(ct);
            if (lead is null) return Result.Failure<NoteDto>(Error.NotFound("Lead"));
            var note = LeadNote.Create(tenantId, leadId, request.Body, crm.UserId, crm.Now);
            crm.Db.LeadNotes.Add(note);
            crm.Activity(leadId, LeadActivityType.NoteAdded, "Note added.");
            crm.Record("Lead.NoteAdded", "LeadNote", note.Id, $"Note added to lead '{lead.Name}'.");
            await crm.Db.SaveChangesAsync(ct);
            await scorer.RescoreAsync([leadId], ct);
            var users = await crm.UserNamesAsync([note.AuthorUserId], ct);
            return Result.Success(new NoteDto(note.Id, leadId, note.Body, note.AuthorUserId is { } u ? users.GetValueOrDefault(u) : null, note.CreatedAtUtc));
        }, logger);

    public Task<Result> DeleteAsync(Guid noteId, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var note = await crm.Db.LeadNotes.FirstOrDefaultAsync(n => n.Id == noteId && n.TenantId == tenantId && !n.IsDeleted, ct);
            if (note is null) return Result.Failure(Error.NotFound("Note"));
            note.Delete(crm.Now);
            crm.Activity(note.LeadId, LeadActivityType.NoteDeleted, "Note deleted.");
            crm.Record("Lead.NoteDeleted", "LeadNote", noteId, "Note deleted.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success();
        }, logger);
}

public sealed class TagService(CrmContext crm, ILeadScorer scorer, ILogger<TagService> logger) : ITagService
{
    public async Task<IReadOnlyList<TagDto>> ListAsync(CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var tags = await crm.Db.LeadTags.AsNoTracking().Where(t => t.TenantId == tenantId).OrderBy(t => t.Name).ToListAsync(ct);
        var counts = await crm.Db.LeadTagMaps.AsNoTracking().Where(m => m.TenantId == tenantId).GroupBy(m => m.TagId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return tags.Select(t => new TagDto(t.Id, t.Name, counts.GetValueOrDefault(t.Id))).ToList();
    }

    public Task<Result<TagDto>> CreateAsync(SaveTagRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var tag = LeadTag.Create(tenantId, r.Name, crm.Now);
            if (await crm.Db.LeadTags.AnyAsync(t => t.TenantId == tenantId && t.NormalizedName == tag.NormalizedName, ct)) return Result.Failure<TagDto>(Error.Conflict($"A tag named '{tag.Name}' already exists."));
            crm.Db.LeadTags.Add(tag);
            crm.Record("Tag.Created", "LeadTag", tag.Id, $"Tag '{tag.Name}' created.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(new TagDto(tag.Id, tag.Name, 0));
        }, logger);

    public Task<Result<TagDto>> RenameAsync(Guid id, SaveTagRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var tag = await crm.Db.LeadTags.FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId, ct);
            if (tag is null) return Result.Failure<TagDto>(Error.NotFound("Tag"));
            var old = tag.Name;
            tag.Rename(r.Name, crm.Now);
            if (await crm.Db.LeadTags.AnyAsync(t => t.TenantId == tenantId && t.NormalizedName == tag.NormalizedName && t.Id != id, ct)) return Result.Failure<TagDto>(Error.Conflict($"A tag named '{tag.Name}' already exists."));
            crm.Record("Tag.Renamed", "LeadTag", id, $"Tag '{old}' renamed to '{tag.Name}'.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(new TagDto(tag.Id, tag.Name, await crm.Db.LeadTagMaps.CountAsync(m => m.TenantId == tenantId && m.TagId == id, ct)));
        }, logger);

    public Task<Result> DeleteAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var tag = await crm.Db.LeadTags.FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId, ct);
            if (tag is null) return Result.Failure(Error.NotFound("Tag"));
            var maps = await crm.Db.LeadTagMaps.Where(m => m.TenantId == tenantId && m.TagId == id).ToListAsync(ct);
            crm.Db.LeadTagMaps.RemoveRange(maps);
            crm.Db.LeadTags.Remove(tag);
            crm.Record("Tag.Deleted", "LeadTag", id, $"Tag '{tag.Name}' deleted (removed from {maps.Count} lead(s)).");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success();
        }, logger);

    public Task<Result<IReadOnlyList<LookupItem>>> SetLeadTagsAsync(Guid leadId, SetLeadTagsRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leadId && l.TenantId == tenantId, ct);
            if (lead is null) return Result.Failure<IReadOnlyList<LookupItem>>(Error.NotFound("Lead"));
            if (lead.IsArchived) return Result.Failure<IReadOnlyList<LookupItem>>(Error.Validation("Archived leads cannot be edited. Restore the lead first."));
            var wanted = (r.TagIds ?? []).Distinct().ToList();
            var tags = await db.LeadTags.AsNoTracking().Where(t => t.TenantId == tenantId && wanted.Contains(t.Id)).ToListAsync(ct);
            if (tags.Count != wanted.Count) return Result.Failure<IReadOnlyList<LookupItem>>(Error.Validation("One or more tags were not found."));
            var current = await db.LeadTagMaps.Where(m => m.TenantId == tenantId && m.LeadId == leadId).ToListAsync(ct);
            var names = await db.LeadTags.AsNoTracking().Where(t => t.TenantId == tenantId).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
            foreach (var m in current.Where(m => !wanted.Contains(m.TagId)))
            {
                db.LeadTagMaps.Remove(m);
                crm.Activity(leadId, LeadActivityType.TagRemoved, $"Tag '{names.GetValueOrDefault(m.TagId)}' removed.");
            }
            foreach (var t in tags.Where(t => current.All(m => m.TagId != t.Id)))
            {
                db.LeadTagMaps.Add(LeadTagMap.Create(tenantId, leadId, t.Id, crm.Now));
                crm.Activity(leadId, LeadActivityType.TagAdded, $"Tag '{t.Name}' added.");
            }
            if (db.ChangeTracker.HasChanges()) { crm.Record("Lead.TagsChanged", "Lead", leadId, $"Tags of lead '{lead.Name}' changed."); await db.SaveChangesAsync(ct); await scorer.RescoreAsync([leadId], ct); }
            return Result.Success<IReadOnlyList<LookupItem>>(tags.OrderBy(t => t.Name).Select(t => new LookupItem(t.Id, t.Name)).ToList());
        }, logger);
}

public sealed class PipelineService(CrmContext crm, ILogger<PipelineService> logger) : IPipelineService
{
    public async Task<IReadOnlyList<PipelineDto>> ListAsync(CancellationToken ct = default)
    {
        await crm.EnsureDefaultsAsync(ct);
        var db = crm.Db; var tenantId = crm.TenantId;
        var pipelines = await db.Pipelines.AsNoTracking().Where(p => p.TenantId == tenantId).OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync(ct);
        var stages = await db.PipelineStages.AsNoTracking().Where(s => s.TenantId == tenantId).ToListAsync(ct);
        var counts = await db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && !l.IsArchived).GroupBy(l => l.PipelineStageId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return pipelines.Select(p => ToDto(p, stages.Where(s => s.PipelineId == p.Id).OrderBy(s => s.Position).ThenBy(s => s.Name).ToList(), counts)).ToList();
    }

    private static PipelineDto ToDto(Pipeline p, List<PipelineStage> stages, Dictionary<Guid, int> counts) =>
        new(p.Id, p.Name, p.IsDefault, p.IsActive, stages.Select(s => new StageDto(s.Id, s.PipelineId, s.Name, s.Position, s.Kind, counts.GetValueOrDefault(s.Id))).ToList(), stages.Sum(s => counts.GetValueOrDefault(s.Id)));

    public async Task<Result<PipelineDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var p = (await ListAsync(ct)).FirstOrDefault(x => x.Id == id);
        return p is null ? Result.Failure<PipelineDto>(Error.NotFound("Pipeline")) : Result.Success(p);
    }

    private async Task<Result<PipelineDto>> Reload(Guid id, CancellationToken ct) => await GetAsync(id, ct);

    public Task<Result<PipelineDto>> CreateAsync(SavePipelineRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            await crm.EnsureDefaultsAsync(ct);
            var tenantId = crm.TenantId; var now = crm.Now;
            var p = Pipeline.Create(tenantId, r.Name, false, now);
            if (await crm.Db.Pipelines.AnyAsync(x => x.TenantId == tenantId && x.Name == p.Name, ct)) return Result.Failure<PipelineDto>(Error.Conflict($"A pipeline named '{p.Name}' already exists."));
            crm.Db.Pipelines.Add(p);
            var pos = 0;
            foreach (var (name, kind) in DefaultPipeline.Stages) crm.Db.PipelineStages.Add(PipelineStage.Create(tenantId, p.Id, name, ++pos * 10, kind, now));
            crm.Record("Pipeline.Created", "Pipeline", p.Id, $"Pipeline '{p.Name}' created with the default stages.");
            await crm.Db.SaveChangesAsync(ct);
            return await Reload(p.Id, ct);
        }, logger);

    public Task<Result<PipelineDto>> RenameAsync(Guid id, SavePipelineRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var p = await crm.Db.Pipelines.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            if (p is null) return Result.Failure<PipelineDto>(Error.NotFound("Pipeline"));
            var old = p.Name; p.Rename(r.Name, crm.Now);
            if (await crm.Db.Pipelines.AnyAsync(x => x.TenantId == tenantId && x.Name == p.Name && x.Id != id, ct)) return Result.Failure<PipelineDto>(Error.Conflict($"A pipeline named '{p.Name}' already exists."));
            crm.Record("Pipeline.Renamed", "Pipeline", id, $"Pipeline '{old}' renamed to '{p.Name}'.");
            await crm.Db.SaveChangesAsync(ct);
            return await Reload(id, ct);
        }, logger);

    public Task<Result<PipelineDto>> SetDefaultAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var all = await crm.Db.Pipelines.Where(x => x.TenantId == tenantId).ToListAsync(ct);
            var p = all.FirstOrDefault(x => x.Id == id);
            if (p is null) return Result.Failure<PipelineDto>(Error.NotFound("Pipeline"));
            if (!p.IsActive) return Result.Failure<PipelineDto>(Error.Validation("An inactive pipeline cannot be the default."));
            foreach (var other in all.Where(x => x.IsDefault && x.Id != id)) other.SetDefault(false, crm.Now);
            p.SetDefault(true, crm.Now);
            crm.Record("Pipeline.DefaultChanged", "Pipeline", id, $"Pipeline '{p.Name}' is now the default.");
            await crm.Db.SaveChangesAsync(ct);
            return await Reload(id, ct);
        }, logger);

    public Task<Result<PipelineDto>> SetActiveAsync(Guid id, bool active, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var p = await crm.Db.Pipelines.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            if (p is null) return Result.Failure<PipelineDto>(Error.NotFound("Pipeline"));
            if (!active && await crm.Db.Leads.AnyAsync(l => l.TenantId == tenantId && l.PipelineId == id, ct)) return Result.Failure<PipelineDto>(Error.Conflict("Leads still use this pipeline. Move them to another pipeline first."));
            p.SetActive(active, crm.Now);
            crm.Record(active ? "Pipeline.Activated" : "Pipeline.Deactivated", "Pipeline", id, $"Pipeline '{p.Name}' {(active ? "activated" : "deactivated")}.");
            await crm.Db.SaveChangesAsync(ct);
            return await Reload(id, ct);
        }, logger);

    public Task<Result<PipelineDto>> AddStageAsync(Guid pipelineId, SaveStageRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            if (!Enum.IsDefined(r.Kind)) return Result.Failure<PipelineDto>(Error.Validation("Stage kind is not valid."));
            var p = await crm.Db.Pipelines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == pipelineId && x.TenantId == tenantId, ct);
            if (p is null) return Result.Failure<PipelineDto>(Error.NotFound("Pipeline"));
            var max = await crm.Db.PipelineStages.Where(s => s.TenantId == tenantId && s.PipelineId == pipelineId).MaxAsync(s => (int?)s.Position, ct) ?? 0;
            var stage = PipelineStage.Create(tenantId, pipelineId, r.Name, max + 10, r.Kind, crm.Now);
            if (await crm.Db.PipelineStages.AnyAsync(s => s.PipelineId == pipelineId && s.TenantId == tenantId && s.Name == stage.Name, ct)) return Result.Failure<PipelineDto>(Error.Conflict($"A stage named '{stage.Name}' already exists in this pipeline."));
            crm.Db.PipelineStages.Add(stage);
            crm.Record("Pipeline.StageAdded", "PipelineStage", stage.Id, $"Stage '{stage.Name}' added to pipeline '{p.Name}'.");
            await crm.Db.SaveChangesAsync(ct);
            return await Reload(pipelineId, ct);
        }, logger);

    public Task<Result<PipelineDto>> UpdateStageAsync(Guid pipelineId, Guid stageId, SaveStageRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            if (!Enum.IsDefined(r.Kind)) return Result.Failure<PipelineDto>(Error.Validation("Stage kind is not valid."));
            var stage = await crm.Db.PipelineStages.FirstOrDefaultAsync(s => s.Id == stageId && s.PipelineId == pipelineId && s.TenantId == tenantId, ct);
            if (stage is null) return Result.Failure<PipelineDto>(Error.NotFound("Stage"));
            var old = stage.Name; stage.Update(r.Name, r.Kind, crm.Now);
            if (await crm.Db.PipelineStages.AnyAsync(s => s.PipelineId == pipelineId && s.TenantId == tenantId && s.Name == stage.Name && s.Id != stageId, ct)) return Result.Failure<PipelineDto>(Error.Conflict($"A stage named '{stage.Name}' already exists in this pipeline."));
            crm.Record("Pipeline.StageUpdated", "PipelineStage", stageId, $"Stage '{old}' updated to '{stage.Name}' ({stage.Kind}).");
            await crm.Db.SaveChangesAsync(ct);
            return await Reload(pipelineId, ct);
        }, logger);

    public Task<Result<PipelineDto>> MoveStageAsync(Guid pipelineId, Guid stageId, int delta, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var stages = await crm.Db.PipelineStages.Where(s => s.TenantId == tenantId && s.PipelineId == pipelineId).OrderBy(s => s.Position).ThenBy(s => s.Name).ToListAsync(ct);
            var index = stages.FindIndex(s => s.Id == stageId);
            if (index < 0) return Result.Failure<PipelineDto>(Error.NotFound("Stage"));
            var target = Math.Clamp(index + Math.Clamp(delta, -50, 50), 0, stages.Count - 1);
            if (target != index)
            {
                var item = stages[index]; stages.RemoveAt(index); stages.Insert(target, item);
                for (var i = 0; i < stages.Count; i++) if (stages[i].Position != (i + 1) * 10) stages[i].MoveTo((i + 1) * 10, crm.Now);
                crm.Record("Pipeline.StagesReordered", "PipelineStage", stageId, $"Stage '{item.Name}' moved to position {target + 1}.");
                await crm.Db.SaveChangesAsync(ct);
            }
            return await Reload(pipelineId, ct);
        }, logger);

    public Task<Result<PipelineDto>> DeleteStageAsync(Guid pipelineId, Guid stageId, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var stage = await crm.Db.PipelineStages.FirstOrDefaultAsync(s => s.Id == stageId && s.PipelineId == pipelineId && s.TenantId == tenantId, ct);
            if (stage is null) return Result.Failure<PipelineDto>(Error.NotFound("Stage"));
            if (await crm.Db.PipelineStages.CountAsync(s => s.PipelineId == pipelineId && s.TenantId == tenantId, ct) <= 1) return Result.Failure<PipelineDto>(Error.Conflict("A pipeline needs at least one stage."));
            var leads = await crm.Db.Leads.CountAsync(l => l.TenantId == tenantId && l.PipelineStageId == stageId, ct);
            if (leads > 0) return Result.Failure<PipelineDto>(Error.Conflict($"{leads} lead(s) are in this stage. Move them first."));
            if (await crm.Db.Opportunities.AnyAsync(o => o.TenantId == tenantId && o.PipelineStageId == stageId, ct)) return Result.Failure<PipelineDto>(Error.Conflict("Opportunities are in this stage. Move them first."));
            crm.Db.PipelineStages.Remove(stage);
            crm.Record("Pipeline.StageDeleted", "PipelineStage", stageId, $"Stage '{stage.Name}' deleted.");
            await crm.Db.SaveChangesAsync(ct);
            return await Reload(pipelineId, ct);
        }, logger);
}

public sealed class LeadCatalogService(CrmContext crm, ILogger<LeadCatalogService> logger) : ILeadCatalogService
{
    public async Task<IReadOnlyList<CategoryDto>> ListCategoriesAsync(CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var cats = await crm.Db.LeadCategories.AsNoTracking().Where(c => c.TenantId == tenantId).OrderByDescending(c => c.IsActive).ThenBy(c => c.Name).ToListAsync(ct);
        var counts = await crm.Db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && l.CategoryId != null).GroupBy(l => l.CategoryId!.Value).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return cats.Select(c => new CategoryDto(c.Id, c.Name, c.IsActive, counts.GetValueOrDefault(c.Id))).ToList();
    }

    public Task<Result<CategoryDto>> CreateCategoryAsync(SaveCategoryRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var c = LeadCategory.Create(tenantId, r.Name, crm.Now);
            if (await crm.Db.LeadCategories.AnyAsync(x => x.TenantId == tenantId && x.NormalizedName == c.NormalizedName, ct)) return Result.Failure<CategoryDto>(Error.Conflict($"A category named '{c.Name}' already exists."));
            crm.Db.LeadCategories.Add(c);
            crm.Record("Category.Created", "LeadCategory", c.Id, $"Category '{c.Name}' created.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(new CategoryDto(c.Id, c.Name, true, 0));
        }, logger);

    public Task<Result<CategoryDto>> RenameCategoryAsync(Guid id, SaveCategoryRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var c = await crm.Db.LeadCategories.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            if (c is null) return Result.Failure<CategoryDto>(Error.NotFound("Category"));
            var old = c.Name; c.Rename(r.Name, crm.Now);
            if (await crm.Db.LeadCategories.AnyAsync(x => x.TenantId == tenantId && x.NormalizedName == c.NormalizedName && x.Id != id, ct)) return Result.Failure<CategoryDto>(Error.Conflict($"A category named '{c.Name}' already exists."));
            crm.Record("Category.Renamed", "LeadCategory", id, $"Category '{old}' renamed to '{c.Name}'.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(new CategoryDto(c.Id, c.Name, c.IsActive, await crm.Db.Leads.CountAsync(l => l.TenantId == tenantId && l.CategoryId == id, ct)));
        }, logger);

    public Task<Result<CategoryDto>> SetCategoryActiveAsync(Guid id, bool active, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var c = await crm.Db.LeadCategories.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            if (c is null) return Result.Failure<CategoryDto>(Error.NotFound("Category"));
            c.SetActive(active, crm.Now);
            crm.Record(active ? "Category.Activated" : "Category.Deactivated", "LeadCategory", id, $"Category '{c.Name}' {(active ? "activated" : "deactivated")}.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(new CategoryDto(c.Id, c.Name, c.IsActive, await crm.Db.Leads.CountAsync(l => l.TenantId == tenantId && l.CategoryId == id, ct)));
        }, logger);

    public async Task<IReadOnlyList<StatusDto>> ListStatusesAsync(CancellationToken ct = default)
    {
        await crm.EnsureDefaultsAsync(ct);
        var tenantId = crm.TenantId;
        var rows = await crm.Db.LeadStatuses.AsNoTracking().Where(s => s.TenantId == tenantId).OrderBy(s => s.SortOrder).ToListAsync(ct);
        var counts = await crm.Db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId).GroupBy(l => l.StatusId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return rows.Select(s => new StatusDto(s.Id, s.Name, s.IsClosed, s.IsDefault, s.IsActive, s.SortOrder, counts.GetValueOrDefault(s.Id))).ToList();
    }

    public Task<Result<StatusDto>> CreateStatusAsync(SaveStatusRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            await crm.EnsureDefaultsAsync(ct);
            var tenantId = crm.TenantId;
            var max = await crm.Db.LeadStatuses.Where(s => s.TenantId == tenantId).MaxAsync(s => (int?)s.SortOrder, ct) ?? 0;
            var s = LeadStatus.Create(tenantId, r.Name, max + 10, r.IsClosed, false, crm.Now);
            if (await crm.Db.LeadStatuses.AnyAsync(x => x.TenantId == tenantId && x.Name == s.Name, ct)) return Result.Failure<StatusDto>(Error.Conflict($"A status named '{s.Name}' already exists."));
            crm.Db.LeadStatuses.Add(s);
            crm.Record("Status.Created", "LeadStatus", s.Id, $"Lead status '{s.Name}' created.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(new StatusDto(s.Id, s.Name, s.IsClosed, s.IsDefault, s.IsActive, s.SortOrder, 0));
        }, logger);

    public Task<Result<StatusDto>> UpdateStatusAsync(Guid id, SaveStatusRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var s = await crm.Db.LeadStatuses.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            if (s is null) return Result.Failure<StatusDto>(Error.NotFound("Status"));
            var old = s.Name; s.Update(r.Name, r.IsClosed, crm.Now);
            if (await crm.Db.LeadStatuses.AnyAsync(x => x.TenantId == tenantId && x.Name == s.Name && x.Id != id, ct)) return Result.Failure<StatusDto>(Error.Conflict($"A status named '{s.Name}' already exists."));
            crm.Record("Status.Updated", "LeadStatus", id, $"Lead status '{old}' updated to '{s.Name}'.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(new StatusDto(s.Id, s.Name, s.IsClosed, s.IsDefault, s.IsActive, s.SortOrder, await crm.Db.Leads.CountAsync(l => l.TenantId == tenantId && l.StatusId == id, ct)));
        }, logger);

    public Task<Result<StatusDto>> SetStatusActiveAsync(Guid id, bool active, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var s = await crm.Db.LeadStatuses.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            if (s is null) return Result.Failure<StatusDto>(Error.NotFound("Status"));
            s.SetActive(active, crm.Now);
            crm.Record(active ? "Status.Activated" : "Status.Deactivated", "LeadStatus", id, $"Lead status '{s.Name}' {(active ? "activated" : "deactivated")}.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(new StatusDto(s.Id, s.Name, s.IsClosed, s.IsDefault, s.IsActive, s.SortOrder, await crm.Db.Leads.CountAsync(l => l.TenantId == tenantId && l.StatusId == id, ct)));
        }, logger);
}

public sealed class OpportunityService(CrmContext crm, ILogger<OpportunityService> logger) : IOpportunityService
{
    public async Task<Result<IReadOnlyList<OpportunityDto>>> ListForLeadAsync(Guid leadId, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        if (!await crm.Db.Leads.AnyAsync(l => l.Id == leadId && l.TenantId == tenantId, ct)) return Result.Failure<IReadOnlyList<OpportunityDto>>(Error.NotFound("Lead"));
        var rows = await crm.Db.Opportunities.AsNoTracking().Where(o => o.TenantId == tenantId && o.LeadId == leadId).OrderByDescending(o => o.CreatedAtUtc).ToListAsync(ct);
        return Result.Success(await ToDtos(rows, ct));
    }

    private async Task<IReadOnlyList<OpportunityDto>> ToDtos(List<Opportunity> rows, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var stages = await crm.Db.PipelineStages.AsNoTracking().Where(s => s.TenantId == tenantId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var owners = await crm.UserNamesAsync(rows.Select(r => r.OwnerUserId), ct);
        return rows.Select(o => new OpportunityDto(o.Id, o.LeadId, o.Name, o.Value, o.Currency, o.ExpectedCloseDateUtc, o.OwnerUserId, o.OwnerUserId is { } u ? owners.GetValueOrDefault(u) : null,
            o.PipelineStageId, stages.GetValueOrDefault(o.PipelineStageId) ?? "", o.Status, o.CreatedAtUtc)).ToList();
    }

    public Task<Result<OpportunityDto>> CreateAsync(Guid leadId, SaveOpportunityRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leadId && l.TenantId == tenantId, ct);
            if (lead is null) return Result.Failure<OpportunityDto>(Error.NotFound("Lead"));
            if (lead.IsArchived) return Result.Failure<OpportunityDto>(Error.Validation("Archived leads cannot be edited. Restore the lead first."));
            if (r.OwnerUserId is { } o && !await crm.IsActiveMemberAsync(o, ct)) return Result.Failure<OpportunityDto>(Error.Validation("The owner must be an active member of this workspace."));
            var firstStage = await db.PipelineStages.AsNoTracking().Where(s => s.TenantId == tenantId && s.PipelineId == lead.PipelineId).OrderBy(s => s.Position).FirstAsync(ct);
            var opp = Opportunity.Create(tenantId, leadId, firstStage.Id, r.Name, r.Value, r.Currency, r.ExpectedCloseDateUtc?.ToUniversalTime(), r.OwnerUserId, crm.Now);
            db.Opportunities.Add(opp);
            db.OpportunityActivities.Add(OpportunityActivity.Create(tenantId, opp.Id, "Created", $"Opportunity '{opp.Name}' created.", crm.UserId, crm.Now));
            crm.Activity(leadId, LeadActivityType.OpportunityCreated, $"Opportunity '{opp.Name}' created.");
            crm.Record("Opportunity.Created", "Opportunity", opp.Id, $"Opportunity '{opp.Name}' created for lead '{lead.Name}'.");
            await db.SaveChangesAsync(ct);
            return Result.Success((await ToDtos([opp], ct))[0]);
        }, logger);

    public Task<Result<OpportunityDto>> UpdateAsync(Guid id, SaveOpportunityRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var opp = await db.Opportunities.FirstOrDefaultAsync(o => o.Id == id && o.TenantId == tenantId, ct);
            if (opp is null) return Result.Failure<OpportunityDto>(Error.NotFound("Opportunity"));
            if (r.OwnerUserId is { } o && !await crm.IsActiveMemberAsync(o, ct)) return Result.Failure<OpportunityDto>(Error.Validation("The owner must be an active member of this workspace."));
            opp.Update(r.Name, r.Value, r.Currency, r.ExpectedCloseDateUtc?.ToUniversalTime(), r.OwnerUserId, crm.Now);
            db.OpportunityActivities.Add(OpportunityActivity.Create(tenantId, id, "Updated", "Opportunity updated.", crm.UserId, crm.Now));
            crm.Activity(opp.LeadId, LeadActivityType.OpportunityUpdated, $"Opportunity '{opp.Name}' updated.");
            crm.Record("Opportunity.Updated", "Opportunity", id, $"Opportunity '{opp.Name}' updated.");
            await db.SaveChangesAsync(ct);
            return Result.Success((await ToDtos([opp], ct))[0]);
        }, logger);

    public Task<Result<OpportunityDto>> ChangeStageAsync(Guid id, Guid stageId, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var opp = await db.Opportunities.FirstOrDefaultAsync(o => o.Id == id && o.TenantId == tenantId, ct);
            if (opp is null) return Result.Failure<OpportunityDto>(Error.NotFound("Opportunity"));
            var lead = await db.Leads.AsNoTracking().FirstAsync(l => l.Id == opp.LeadId && l.TenantId == tenantId, ct);
            var stage = await db.PipelineStages.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stageId && s.TenantId == tenantId, ct);
            if (stage is null || stage.PipelineId != lead.PipelineId) return Result.Failure<OpportunityDto>(Error.Validation("Stage not found in this lead's pipeline."));
            opp.MoveToStage(stage.Id, stage.Kind, crm.Now);
            db.OpportunityActivities.Add(OpportunityActivity.Create(tenantId, id, "StageChanged", $"Moved to {stage.Name}.", crm.UserId, crm.Now));
            crm.Activity(opp.LeadId, LeadActivityType.OpportunityUpdated, $"Opportunity '{opp.Name}' moved to {stage.Name}.");
            crm.Record("Opportunity.StageChanged", "Opportunity", id, $"Opportunity '{opp.Name}' moved to '{stage.Name}'.");
            await db.SaveChangesAsync(ct);
            return Result.Success((await ToDtos([opp], ct))[0]);
        }, logger);
}
