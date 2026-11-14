using LeadPilot.Application.Crm;
using LeadPilot.Domain.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LeadPilot.Application.Contracts;

namespace LeadPilot.Infrastructure.Services.Crm;

public sealed class ScoringService(CrmContext crm, ILogger<ScoringService> logger, Lazy<ILeadService> leads) : IScoringService, ILeadScorer
{
    private static readonly LeadActivityType[] CountedActivity =
        [LeadActivityType.NoteAdded, LeadActivityType.StageChanged, LeadActivityType.ConsentRecorded, LeadActivityType.OpportunityCreated, LeadActivityType.OpportunityUpdated, LeadActivityType.ContactAdded, LeadActivityType.ContactUpdated];

    public async Task<ScoringRuleSetDto> GetActiveAsync(CancellationToken ct = default)
    {
        await crm.EnsureDefaultsAsync(ct);
        var tenantId = crm.TenantId;
        var set = await crm.Db.ScoringRuleSets.AsNoTracking().Where(s => s.TenantId == tenantId && s.IsActive).OrderByDescending(s => s.Version).FirstAsync(ct);
        return (await ToDtos([set], ct))[0];
    }

    public async Task<IReadOnlyList<ScoringRuleSetDto>> ListVersionsAsync(CancellationToken ct = default)
    {
        await crm.EnsureDefaultsAsync(ct);
        var tenantId = crm.TenantId;
        var sets = await crm.Db.ScoringRuleSets.AsNoTracking().Where(s => s.TenantId == tenantId).OrderByDescending(s => s.Version).Take(50).ToListAsync(ct);
        return await ToDtos(sets, ct);
    }

    private async Task<IReadOnlyList<ScoringRuleSetDto>> ToDtos(List<ScoringRuleSet> sets, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var ids = sets.Select(s => s.Id).ToList();
        var rules = (await crm.Db.ScoringRules.AsNoTracking().Where(r => r.TenantId == tenantId && ids.Contains(r.RuleSetId)).ToListAsync(ct)).ToLookup(r => r.RuleSetId);
        return sets.Select(s => new ScoringRuleSetDto(s.Id, s.Version, s.IsActive, s.Note, s.CreatedAtUtc, s.CreatedBy,
            rules[s.Id].OrderBy(r => r.Dimension).ThenBy(r => r.Target).Select(r => new ScoringRuleDto(r.Id, r.Dimension, r.Target, r.Points, r.Description, r.IsEnabled)).ToList())).ToList();
    }

    public Task<Result<ScoringRuleSetDto>> PublishAsync(PublishScoringRulesRequest request, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            await crm.EnsureDefaultsAsync(ct);
            var tenantId = crm.TenantId;
            if (request.Rules is null || request.Rules.Count > 100) return Result.Failure<ScoringRuleSetDto>(Error.Validation("Provide between 0 and 100 rules."));
            foreach (var r in request.Rules)
                if (ScoringRuleParser.Validate(r.Dimension, r.Target, r.Points) is { } problem) return Result.Failure<ScoringRuleSetDto>(Error.Validation($"{r.Dimension} '{r.Target}': {problem}"));

            var current = await crm.Db.ScoringRuleSets.Where(s => s.TenantId == tenantId && s.IsActive).ToListAsync(ct);
            var next = (await crm.Db.ScoringRuleSets.Where(s => s.TenantId == tenantId).MaxAsync(s => (int?)s.Version, ct) ?? 0) + 1;
            foreach (var c in current) c.Deactivate();
            var set = ScoringRuleSet.Create(tenantId, next, request.Note, crm.Now, crm.Actor);
            crm.Db.ScoringRuleSets.Add(set);
            foreach (var r in request.Rules) crm.Db.ScoringRules.Add(ScoringRule.Create(tenantId, set.Id, r.Dimension, r.Target, r.Points, r.Description, r.IsEnabled));
            crm.Record("Scoring.RulesPublished", "ScoringRuleSet", set.Id, $"Scoring rules version {next} published ({request.Rules.Count} rules). Existing scores are unchanged until recalculated.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success((await ToDtos([set], ct))[0]);
        }, logger);

    public async Task<Result<LeadDto>> RescoreLeadAsync(Guid leadId, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        if (!await crm.Db.Leads.AnyAsync(l => l.TenantId == tenantId && l.Id == leadId, ct)) return Result.Failure<LeadDto>(Error.NotFound("Lead"));
        await RescoreAsync([leadId], ct);
        return await leads.Value.GetAsync(leadId, ct);
    }

    public async Task<RescoreResult> RescoreAllAsync(CancellationToken ct = default)
    {
        await crm.EnsureDefaultsAsync(ct);
        var tenantId = crm.TenantId;
        var version = (await GetActiveAsync(ct)).Version;
        int processed = 0, changed = 0;
        while (true)
        {
            var ids = await crm.Db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId).OrderBy(l => l.Id).Skip(processed).Take(200).Select(l => l.Id).ToListAsync(ct);
            if (ids.Count == 0) break;
            changed += await ScoreBatchAsync(ids, ct);
            await crm.Db.SaveChangesAsync(ct);
            processed += ids.Count;
            crm.Db.ChangeTracker.Clear(); // keep memory flat on large tenants
        }
        crm.Record("Scoring.Recalculated", "Lead", Guid.Empty, $"Scores recalculated with rule version {version}: {processed} lead(s), {changed} changed.");
        await crm.Db.SaveChangesAsync(ct);
        return new RescoreResult(processed, changed, version);
    }

    public async Task<Result<IReadOnlyList<ScoreHistoryDto>>> HistoryAsync(Guid leadId, int take, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        if (!await crm.Db.Leads.AnyAsync(l => l.TenantId == tenantId && l.Id == leadId, ct)) return Result.Failure<IReadOnlyList<ScoreHistoryDto>>(Error.NotFound("Lead"));
        var rows = await crm.Db.LeadScores.AsNoTracking().Where(s => s.TenantId == tenantId && s.LeadId == leadId).OrderByDescending(s => s.CalculatedAtUtc).Take(Math.Clamp(take, 1, 100)).ToListAsync(ct);
        return Result.Success<IReadOnlyList<ScoreHistoryDto>>(rows.Select(r => new ScoreHistoryDto(r.Score, r.RuleVersion, r.CalculatedAtUtc, ScoreResult.ParseMatches(r.Breakdown))).ToList());
    }

    public async Task RescoreAsync(IReadOnlyCollection<Guid> leadIds, CancellationToken ct = default)
    {
        if (leadIds.Count == 0) return;
        foreach (var chunk in leadIds.Distinct().Chunk(200))
        {
            await ScoreBatchAsync(chunk, ct);
        }
        await crm.Db.SaveChangesAsync(ct);
    }

    /// <summary>Scores the given leads (tracked in the context, saved by the caller). Returns how many scores changed.</summary>
    private async Task<int> ScoreBatchAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        await crm.EnsureDefaultsAsync(ct);
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var set = await db.ScoringRuleSets.AsNoTracking().Where(s => s.TenantId == tenantId && s.IsActive).OrderByDescending(s => s.Version).FirstOrDefaultAsync(ct);
        if (set is null) return 0;
        var rules = (await db.ScoringRules.AsNoTracking().Where(r => r.TenantId == tenantId && r.RuleSetId == set.Id).ToListAsync(ct))
            .Select(r => new ScoringRuleSpec(r.Id, r.Dimension, r.Target, r.Points, r.Description, r.IsEnabled)).ToList();

        var batch = await db.Leads.Where(l => l.TenantId == tenantId && ids.Contains(l.Id)).ToListAsync(ct);
        if (batch.Count == 0) return 0;
        var leadIds = batch.Select(l => l.Id).ToList();
        var pipelineIds = batch.Select(l => l.PipelineId).Distinct().ToList();
        var stages = await db.PipelineStages.AsNoTracking().Where(s => s.TenantId == tenantId && pipelineIds.Contains(s.PipelineId)).ToListAsync(ct);
        var categoryIds = batch.Where(l => l.CategoryId != null).Select(l => l.CategoryId!.Value).Distinct().ToList();
        var categories = await db.LeadCategories.AsNoTracking().Where(c => c.TenantId == tenantId && categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var contacts = (await db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && !c.IsDeleted && leadIds.Contains(c.LeadId)).ToListAsync(ct)).ToLookup(c => c.LeadId);
        var since = now.AddDays(-30);
        var activity = await db.LeadActivities.AsNoTracking().Where(a => a.TenantId == tenantId && leadIds.Contains(a.LeadId) && a.OccurredAtUtc >= since && CountedActivity.Contains(a.Type))
            .GroupBy(a => a.LeadId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var tags = (await (from m in db.LeadTagMaps.AsNoTracking() join t in db.LeadTags.AsNoTracking() on m.TagId equals t.Id
                           where m.TenantId == tenantId && leadIds.Contains(m.LeadId) select new { m.LeadId, t.NormalizedName }).ToListAsync(ct)).ToLookup(x => x.LeadId, x => x.NormalizedName);

        var changed = 0;
        foreach (var lead in batch)
        {
            var pipelineStages = stages.Where(s => s.PipelineId == lead.PipelineId).ToList();
            var stage = pipelineStages.FirstOrDefault(s => s.Id == lead.PipelineStageId);
            var cs = contacts[lead.Id].ToList();
            var facts = new LeadFacts(
                lead.CategoryId is { } cid && categories.TryGetValue(cid, out var cn) ? cn : null, lead.Country, lead.Province, lead.City, lead.District, lead.Area,
                lead.WebsiteDomain is not null, cs.Count, cs.Any(c => c.PhoneE164 is not null), cs.Any(c => c.Email is not null), cs.Any(c => !string.IsNullOrWhiteSpace(c.Name)),
                cs.Any(c => c.PhoneVerifiedAtUtc is not null), cs.Any(c => c.DataOrigin == DataOrigin.Verified),
                activity.GetValueOrDefault(lead.Id), stage?.Kind ?? StageKind.Open, stage?.Position ?? 0,
                pipelineStages.GroupBy(s => s.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Min(s => s.Position)),
                tags[lead.Id].ToHashSet());
            var result = LeadScoringEngine.Score(facts, rules);
            if (result.Score == lead.Score && lead.ScoreRuleVersion == set.Version) continue;
            lead.ApplyScore(result.Score, set.Version, now);
            db.LeadScores.Add(LeadScore.Create(tenantId, lead.Id, result.Score, set.Version, result.ToJson(), now));
            changed++;
        }
        return changed;
    }
}
