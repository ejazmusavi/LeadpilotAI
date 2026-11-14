using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeadPilot.Application.Config;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Config;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Discovery;
using LeadPilot.Domain.Messaging;
using LeadPilot.Domain.Workflows;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Config;

public sealed class PackApplyException(string message) : Exception(message);

/// <summary>One thing a pack would create or update, reduced to comparable text fields. <see cref="Natural"/> finds the "same" thing in a workspace that did not get it from the pack.</summary>
internal sealed record PackItem(string Type, string Key, string Natural, string Name, string? ParentKey, string? ParentType, SortedDictionary<string, string> Fields, object Source)
{
    public string Id => Type + ":" + Key;
    public string Hash => PackHash.Of(Fields);
}

internal sealed record Existing(Guid Id, string Natural, SortedDictionary<string, string> Fields) { public string Hash => PackHash.Of(Fields); }

internal sealed record Planned(PackItem Item, PackOutcome Outcome, Existing? Entity, PackItemLink? Link, bool Conflict, string? Reason, IReadOnlyList<PackFieldChange> Changes);

internal static class PackHash
{
    public static string Of(IReadOnlyDictionary<string, string> fields)
    {
        var sb = new StringBuilder();
        foreach (var kv in fields.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append('=').Append(kv.Value.Replace("\\", "\\\\").Replace("\n", "\\n")).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    public static string Num(decimal? d) => d?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
    public static string Lines(IEnumerable<string>? l) => string.Join("\n", l ?? []);
    public static string Low(string s) => s.Trim().ToLowerInvariant();
}

internal static class PackTypes
{
    public const string Category = "category", Pipeline = "pipeline", Stage = "stage", Field = "customField", Rule = "scoringRule", Template = "messageTemplate", Agent = "agent", Question = "question",
        Product = "product", Plan = "plan", Knowledge = "knowledgeSource", DiscoveryCategory = "discoveryCategory", DiscoveryLocation = "discoveryLocation", Workflow = "workflow";

    public static string SectionOf(string type) => type switch
    {
        Category => PackSections.Categories, Pipeline or Stage => PackSections.Pipelines, Field => PackSections.CustomFields, Rule => PackSections.ScoringRules, Template => PackSections.MessageTemplates,
        Agent or Question => PackSections.Agents, Product or Plan => PackSections.Catalog, Knowledge => PackSections.Knowledge, DiscoveryCategory or DiscoveryLocation => PackSections.Discovery,
        Workflow => PackSections.Workflows, _ => type,
    };
}

/// <summary>
/// Turns a pack into tenant configuration. PLAN = pure decisions from (pack, what the workspace has now, what earlier imports left behind, the chosen policy).
/// APPLY = performs exactly the planned outcomes, inside the caller's transaction. Because both come from the same plan, a preview is exactly what happens.
/// A tenant edit is detected by comparing the entity's current content with the fingerprint stored when the pack last wrote it; it is never overwritten unless the person chose to.
/// </summary>
internal sealed class PackEngine(CrmContext crm, IWorkflowService workflows)
{
    private AppDbContext Db => crm.Db;
    private static readonly string DefaultOffTopic = SalesAgent.Create(Guid.Empty, "x", "x", false, false, DateTime.UnixEpoch, null).OffTopicReply;

    // ------------------------------------------------------------------------------------------------ items

    public static IReadOnlyList<PackItem> Build(PackDocument d)
    {
        var items = new List<PackItem>();
        var catName = d.Categories.ToDictionary(c => c.Key, c => c.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var c in ParentsFirst(d.Categories))
            items.Add(new(PackTypes.Category, c.Key, Normalizer.Name(c.Name), c.Name, c.Parent, c.Parent is null ? null : PackTypes.Category,
                new() { ["name"] = c.Name, ["parent"] = c.Parent is null ? "" : catName[c.Parent] }, c));
        foreach (var p in d.Pipelines)
        {
            items.Add(new(PackTypes.Pipeline, p.Key, PackHash.Low(p.Name), p.Name, null, null, new() { ["name"] = p.Name }, p));
            foreach (var s in p.Stages) items.Add(new(PackTypes.Stage, p.Key + "/" + PackHash.Low(s.Name), PackHash.Low(p.Name) + "/" + PackHash.Low(s.Name), s.Name, p.Key, PackTypes.Pipeline, new() { ["name"] = s.Name, ["kind"] = s.Kind }, s));
        }
        foreach (var f in d.CustomFields)
            items.Add(new(PackTypes.Field, f.Entity + "." + f.Key, f.Entity + "." + f.Key, f.Label, null, null, FieldFields(f.Label, f.Type, f.Options, f.Required, f.Min, f.Max, f.Pattern, f.Help, f.Filterable), f));
        foreach (var t in d.MessageTemplates)
            items.Add(new(PackTypes.Template, t.Key, PackHash.Low(t.Name), t.Name, null, null, new() { ["body"] = t.Body.Trim(), ["category"] = t.Category }, t));
        foreach (var r in d.ScoringRules)
            items.Add(new(PackTypes.Rule, r.Dimension + "|" + PackHash.Low(r.Target), r.Dimension + "|" + PackHash.Low(r.Target), r.Dimension + ": " + r.Target, null, null,
                new() { ["points"] = r.Points.ToString(CultureInfo.InvariantCulture), ["description"] = r.Description ?? "", ["enabled"] = "true" }, r));
        foreach (var a in d.Agents)
        {
            items.Add(new(PackTypes.Agent, a.Key, PackHash.Low(a.Name), a.Name, null, null, new() { ["description"] = a.Description ?? "", ["instructions"] = a.Instructions, ["offTopicReply"] = a.OffTopicReply ?? DefaultOffTopic }, a));
            var order = 0;
            foreach (var q in a.Questions)
                items.Add(new(PackTypes.Question, a.Key + "/" + q.Key, PackHash.Low(a.Name) + "/" + q.Key, q.Question, a.Key, PackTypes.Agent,
                    new() { ["question"] = q.Question, ["type"] = q.Type, ["choices"] = PackHash.Lines(q.Choices), ["required"] = Bool(q.Required) }, (q, ++order)));
        }
        foreach (var p in d.Catalog)
        {
            items.Add(new(PackTypes.Product, p.Key, PackHash.Low(p.Name), p.Name, null, null, new() { ["kind"] = p.Kind, ["summary"] = p.Summary ?? "", ["features"] = PackHash.Lines(p.Features) }, p));
            foreach (var pl in p.Plans)
                items.Add(new(PackTypes.Plan, p.Key + "/" + pl.Key, PackHash.Low(p.Name) + "/" + PackHash.Low(pl.Name), pl.Name, p.Key, PackTypes.Product,
                    new() { ["price"] = PackHash.Num(pl.Price), ["currency"] = pl.Currency, ["period"] = pl.Period ?? "", ["details"] = pl.Details ?? "" }, pl));
        }
        foreach (var k in d.Knowledge) items.Add(new(PackTypes.Knowledge, k.Key, PackHash.Low(k.Name), k.Name, null, null, new() { ["name"] = k.Name }, k));
        if (d.Discovery is { } dis)
        {
            foreach (var c in dis.Categories)
                items.Add(new(PackTypes.DiscoveryCategory, c.Key, Normalizer.Name(catName[c.Category]), catName[c.Category] + " (search)", c.Category, PackTypes.Category,
                    new() { ["terms"] = PackHash.Lines(c.SearchPhrases), ["placeType"] = c.PlaceType ?? "" }, c));
            foreach (var l in dis.Locations)
            {
                var label = Label(l.City, l.Province, l.Country);
                items.Add(new(PackTypes.DiscoveryLocation, l.Key, PackHash.Low(label), label, null, null, new() { ["label"] = label }, l));
            }
        }
        foreach (var w in d.Workflows)
            items.Add(new(PackTypes.Workflow, w.Key, PackHash.Low(w.Name), w.Name, null, null, new() { ["description"] = w.Description ?? "", ["definition"] = CanonicalDefinition(w) }, w));
        return items;
    }

    /// <summary>Stable order in which every category comes after its parent (the pack may list them in any order).</summary>
    private static IEnumerable<PackCategory> ParentsFirst(IReadOnlyList<PackCategory> all)
    {
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var rest = all.ToList();
        while (rest.Count > 0)
        {
            var ready = rest.Where(c => c.Parent is null || done.Contains(c.Parent)).ToList();
            if (ready.Count == 0) { foreach (var c in rest) yield return c; yield break; }
            foreach (var c in ready) { done.Add(c.Key); rest.Remove(c); yield return c; }
        }
    }

    private static string Bool(bool b) => b ? "true" : "false";
    private static string Label(string? city, string? province, string country) => string.Join(", ", new[] { city, province, country }.Where(x => !string.IsNullOrEmpty(x)));

    private static SortedDictionary<string, string> FieldFields(string label, string type, IEnumerable<string> options, bool required, decimal? min, decimal? max, string? pattern, string? help, bool filterable) => new()
    {
        ["label"] = label, ["type"] = type, ["options"] = PackHash.Lines(options), ["required"] = Bool(required), ["min"] = PackHash.Num(min), ["max"] = PackHash.Num(max),
        ["pattern"] = pattern ?? "", ["help"] = help ?? "", ["filterable"] = Bool(filterable),
    };

    // canonical (sorted-key, whitespace-free) JSON so a pack definition and a stored definition compare equal when they mean the same
    private static string Canon(JsonNode? n) => n switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canon(p.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canon)) + "]",
        _ => n.ToJsonString(),
    };

    private static JsonNode? Node(string? raw) { if (string.IsNullOrWhiteSpace(raw)) return null; try { return JsonNode.Parse(raw); } catch (JsonException) { return null; } }

    private static string CanonicalDefinition(PackWorkflow w) => Canonical(w.ReEntry, w.CooldownHours, w.MaxStartsPer30Days,
        [(w.TriggerEvent, w.TriggerCondition?.GetRawText(), w.InactiveDays)],
        w.Steps.Select(s => (s.Key, s.Type, s.Name, s.Config?.GetRawText(), s.Condition?.GetRawText(), s.OnFailure, s.MaxRetries)).ToList());

    private static string Canonical(string reEntry, int cooldown, int maxStarts, IEnumerable<(string Event, string? Cond, int? Inactive)> triggers, IEnumerable<(string Key, string Type, string Name, string? Config, string? Cond, string OnFailure, int Retries)> steps)
    {
        var o = new JsonObject
        {
            ["reEntry"] = reEntry, ["cooldownHours"] = cooldown, ["maxStarts"] = maxStarts,
            ["triggers"] = new JsonArray(triggers.Select(t => (JsonNode?)new JsonObject { ["event"] = t.Event, ["condition"] = Node(t.Cond), ["inactiveDays"] = t.Inactive }).ToArray()),
            ["steps"] = new JsonArray(steps.Select(s => (JsonNode?)new JsonObject { ["key"] = s.Key, ["type"] = s.Type, ["name"] = s.Name, ["config"] = Node(s.Config) ?? new JsonObject(), ["condition"] = Node(s.Cond), ["onFailure"] = s.OnFailure, ["retries"] = s.Retries }).ToArray()),
        };
        return Canon(o);
    }

    // ------------------------------------------------------------------------------------------------ what the workspace has

    private async Task<List<Existing>> LoadAsync(string type, Guid tenantId, CancellationToken ct)
    {
        switch (type)
        {
            case PackTypes.Category:
            {
                var all = await Db.LeadCategories.AsNoTracking().Where(c => c.TenantId == tenantId).ToListAsync(ct);
                var names = all.ToDictionary(c => c.Id, c => c.Name);
                return all.Select(c => new Existing(c.Id, c.NormalizedName, new() { ["name"] = c.Name, ["parent"] = c.ParentId is { } p && names.TryGetValue(p, out var pn) ? pn : "" })).ToList();
            }
            case PackTypes.Pipeline:
                return (await Db.Pipelines.AsNoTracking().Where(p => p.TenantId == tenantId).ToListAsync(ct)).Select(p => new Existing(p.Id, PackHash.Low(p.Name), new() { ["name"] = p.Name })).ToList();
            case PackTypes.Stage:
            {
                var pipes = await Db.Pipelines.AsNoTracking().Where(p => p.TenantId == tenantId).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
                return (await Db.PipelineStages.AsNoTracking().Where(s => s.TenantId == tenantId).ToListAsync(ct))
                    .Select(s => new Existing(s.Id, PackHash.Low(pipes.GetValueOrDefault(s.PipelineId, "")) + "/" + PackHash.Low(s.Name), new() { ["name"] = s.Name, ["kind"] = s.Kind.ToString() })).ToList();
            }
            case PackTypes.Field:
                return (await Db.CustomFieldDefinitions.AsNoTracking().Where(f => f.TenantId == tenantId).ToListAsync(ct))
                    .Select(f => new Existing(f.Id, f.EntityType + "." + f.Key, FieldFields(f.Label, f.Type.ToString(), f.Options, f.IsRequired, f.MinValue, f.MaxValue, f.Pattern, f.HelpText, f.IsFilterable))).ToList();
            case PackTypes.Template:
                return (await Db.MessageTemplates.AsNoTracking().Where(t => t.TenantId == tenantId).ToListAsync(ct)).Select(t => new Existing(t.Id, PackHash.Low(t.Name), new() { ["body"] = t.Body, ["category"] = t.Category.ToString() })).ToList();
            case PackTypes.Rule:
            {
                var set = await Db.ScoringRuleSets.AsNoTracking().Where(s => s.TenantId == tenantId && s.IsActive).OrderByDescending(s => s.Version).FirstOrDefaultAsync(ct);
                if (set is null) return [];
                return (await Db.ScoringRules.AsNoTracking().Where(r => r.TenantId == tenantId && r.RuleSetId == set.Id).ToListAsync(ct))
                    .Select(r => new Existing(r.Id, r.Dimension + "|" + PackHash.Low(r.Target), new() { ["points"] = r.Points.ToString(CultureInfo.InvariantCulture), ["description"] = r.Description ?? "", ["enabled"] = Bool(r.IsEnabled) })).ToList();
            }
            case PackTypes.Agent:
                return (await Db.SalesAgents.AsNoTracking().Where(a => a.TenantId == tenantId).ToListAsync(ct)).Select(a => new Existing(a.Id, PackHash.Low(a.Name), new() { ["description"] = a.Description ?? "", ["instructions"] = a.Instructions, ["offTopicReply"] = a.OffTopicReply })).ToList();
            case PackTypes.Question:
            {
                var agents = await Db.SalesAgents.AsNoTracking().Where(a => a.TenantId == tenantId).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
                return (await Db.AgentQualificationQuestions.AsNoTracking().Where(q => q.TenantId == tenantId).ToListAsync(ct))
                    .Select(q => new Existing(q.Id, PackHash.Low(agents.GetValueOrDefault(q.AgentId, "")) + "/" + q.Key, new() { ["question"] = q.Question, ["type"] = q.AnswerType.ToString(), ["choices"] = PackHash.Lines(q.Choices), ["required"] = Bool(q.Required) })).ToList();
            }
            case PackTypes.Product:
                return (await Db.CatalogProducts.AsNoTracking().Where(p => p.TenantId == tenantId).ToListAsync(ct)).Select(p => new Existing(p.Id, PackHash.Low(p.Name), new() { ["kind"] = p.Kind.ToString(), ["summary"] = p.Summary ?? "", ["features"] = PackHash.Lines(p.FeatureList) })).ToList();
            case PackTypes.Plan:
            {
                var products = await Db.CatalogProducts.AsNoTracking().Where(p => p.TenantId == tenantId).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
                return (await Db.PricingPlans.AsNoTracking().Where(p => p.TenantId == tenantId).ToListAsync(ct))
                    .Select(p => new Existing(p.Id, PackHash.Low(products.GetValueOrDefault(p.ProductId, "")) + "/" + PackHash.Low(p.Name), new() { ["price"] = PackHash.Num(p.Price), ["currency"] = p.Currency, ["period"] = p.BillingPeriod ?? "", ["details"] = p.Details ?? "" })).ToList();
            }
            case PackTypes.Knowledge:
                return (await Db.KnowledgeSources.AsNoTracking().Where(k => k.TenantId == tenantId).ToListAsync(ct)).Select(k => new Existing(k.Id, PackHash.Low(k.Name), new() { ["name"] = k.Name })).ToList();
            case PackTypes.DiscoveryCategory:
            {
                var cats = await Db.LeadCategories.AsNoTracking().Where(c => c.TenantId == tenantId).ToDictionaryAsync(c => c.Id, c => c.NormalizedName, ct);
                return (await Db.DiscoveryCategoryMappings.AsNoTracking().Where(m => m.TenantId == tenantId).ToListAsync(ct))
                    .Select(m => new Existing(m.Id, cats.GetValueOrDefault(m.CategoryId, ""), new() { ["terms"] = PackHash.Lines(m.Terms), ["placeType"] = m.PlaceType ?? "" })).ToList();
            }
            case PackTypes.DiscoveryLocation:
                return (await Db.DiscoveryLocationPresets.AsNoTracking().Where(l => l.TenantId == tenantId).ToListAsync(ct)).Select(l => new Existing(l.Id, PackHash.Low(l.Label), new() { ["label"] = l.Label })).ToList();
            case PackTypes.Workflow:
            {
                var ws = await Db.Workflows.AsNoTracking().Where(w => w.TenantId == tenantId && !w.IsDeleted).ToListAsync(ct);
                var result = new List<Existing>();
                foreach (var w in ws)
                {
                    var v = await Db.WorkflowVersions.AsNoTracking().Where(x => x.TenantId == tenantId && x.WorkflowId == w.Id).OrderByDescending(x => x.Number).FirstOrDefaultAsync(ct);
                    string def = "";
                    if (v is not null)
                    {
                        var trg = await Db.WorkflowTriggers.AsNoTracking().Where(t => t.TenantId == tenantId && t.WorkflowVersionId == v.Id).ToListAsync(ct);
                        var stp = await Db.WorkflowSteps.AsNoTracking().Where(s => s.TenantId == tenantId && s.WorkflowVersionId == v.Id).OrderBy(s => s.Position).ToListAsync(ct);
                        def = Canonical(v.ReEntry.ToString(), v.CooldownHours, v.MaxStartsPerLeadPer30Days,
                            trg.Select(t => (t.EventType, t.ConditionJson, Node(t.ConfigJson)?["inactiveDays"]?.GetValue<int?>())),
                            stp.Select(s => (s.Key, s.Type.ToString(), s.Name, (string?)s.ConfigJson, s.ConditionJson, s.OnFailure.ToString(), s.MaxRetries)));
                    }
                    result.Add(new Existing(w.Id, PackHash.Low(w.Name), new() { ["description"] = w.Description ?? "", ["definition"] = def }));
                }
                return result;
            }
            default: return [];
        }
    }

    // ------------------------------------------------------------------------------------------------ plan

    public async Task<IReadOnlyList<Planned>> PlanAsync(PackDocument doc, ApplyPackOptions o, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var included = (o.Sections ?? PackSections.All).ToHashSet(StringComparer.Ordinal);
        var items = Build(doc).Where(i => included.Contains(PackTypes.SectionOf(i.Type))).ToList();
        var links = (await Db.PackItemLinks.AsNoTracking().Where(l => l.TenantId == tenantId && l.PackKey == doc.Key).ToListAsync(ct)).ToDictionary(l => l.ItemType + "|" + l.ItemKey);
        var cache = new Dictionary<string, List<Existing>>();
        var planned = new List<Planned>(); var byId = new Dictionary<string, Planned>();
        var fieldCount = (await Db.CustomFieldDefinitions.AsNoTracking().Where(f => f.TenantId == tenantId).GroupBy(f => f.EntityType).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.N);
        var leadFields = (await Db.CustomFieldDefinitions.AsNoTracking().Where(f => f.TenantId == tenantId && f.EntityType == CustomEntityType.Lead && f.IsActive).Select(f => f.Key).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var stageNames = (await Db.PipelineStages.AsNoTracking().Where(s => s.TenantId == tenantId).Select(s => s.Name).ToListAsync(ct)).Select(PackHash.Low).ToHashSet();
        var openStatuses = (await Db.LeadStatuses.AsNoTracking().Where(s => s.TenantId == tenantId && s.IsActive && !s.IsClosed).Select(s => s.Name).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var i in items.Where(i => i.Type == PackTypes.Stage)) stageNames.Add(PackHash.Low(i.Name));   // stages this pack adds count for workflows of the same pack
        var packLeadFields = doc.CustomFields.Where(f => f.Entity == nameof(CustomEntityType.Lead)).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (!cache.TryGetValue(item.Type, out var existing)) cache[item.Type] = existing = await LoadAsync(item.Type, tenantId, ct);
            links.TryGetValue(item.Type + "|" + item.Key, out var link);
            var byNatural = existing.FirstOrDefault(e => string.Equals(e.Natural, item.Natural, StringComparison.Ordinal));
            // the linked entity if it is still there; otherwise (deleted, or deleted and rebuilt by hand) whatever now has the same natural key
            var entity = link is not null ? existing.FirstOrDefault(e => e.Id == link.EntityId) ?? byNatural : byNatural;

            var resolution = o.Resolutions is not null && o.Resolutions.TryGetValue(item.Id, out var res) ? res : (PackResolution?)null;
            var replace = resolution is { } r ? r == PackResolution.Replace : o.Policy == ConflictPolicy.UseThePack;
            PackOutcome outcome; var conflict = false; string? reason = null;

            if (entity is null)
            {
                if (link is null) outcome = PackOutcome.Create;
                else if (replace) { outcome = PackOutcome.Recreate; reason = "You deleted it; you chose to restore it."; }
                else { outcome = PackOutcome.LeaveDeleted; reason = "It came from this pack but you deleted it."; }
            }
            else if (entity.Hash == item.Hash) outcome = link is null ? PackOutcome.Link : PackOutcome.Unchanged;
            else if (link is null) { conflict = true; outcome = replace ? PackOutcome.Overwrite : PackOutcome.KeepYours; reason = "The same thing already exists with different content."; }
            else if (entity.Hash == link.AppliedHash) outcome = PackOutcome.Update;
            else if (item.Hash == link.AppliedHash) { outcome = PackOutcome.KeepYours; reason = "You changed this; the pack did not."; }
            else { conflict = true; outcome = replace ? PackOutcome.Overwrite : PackOutcome.KeepYours; reason = "You changed this and the pack changed it too."; }

            // things that cannot be applied here
            var creates = outcome is PackOutcome.Create or PackOutcome.Recreate;
            var writes = creates || outcome is PackOutcome.Update or PackOutcome.Overwrite;
            if (item.Type == PackTypes.Field && entity is not null && writes && entity.Fields["type"] != item.Fields["type"]) { outcome = PackOutcome.Blocked; conflict = false; reason = "The field exists with a different type; a field's type cannot change (create a new field instead)."; }
            else if (item.Type == PackTypes.Field && creates && fieldCount.GetValueOrDefault(Enum.Parse<CustomEntityType>(((PackCustomField)item.Source).Entity)) >= CustomFieldDefinition.MaxFieldsPerEntityType)
            { outcome = PackOutcome.Blocked; reason = $"The workspace already has {CustomFieldDefinition.MaxFieldsPerEntityType} custom fields for this record type."; }
            else if (item.Type == PackTypes.Field && creates) fieldCount[Enum.Parse<CustomEntityType>(((PackCustomField)item.Source).Entity)] = fieldCount.GetValueOrDefault(Enum.Parse<CustomEntityType>(((PackCustomField)item.Source).Entity)) + 1;
            else if (item.Type == PackTypes.Rule && writes && ((PackScoringRule)item.Source).Dimension == nameof(ScoreDimension.CustomField)
                && CustomFieldCompare.ParseTarget(((PackScoringRule)item.Source).Target) is { } rt && !leadFields.Contains(rt.Key) && !packLeadFields.Contains(rt.Key))
            { outcome = PackOutcome.Blocked; reason = $"The rule uses the lead field '{rt.Key}', which does not exist in this workspace."; }
            else if (item.Type == PackTypes.Workflow && writes && WorkflowProblem((PackWorkflow)item.Source, leadFields, packLeadFields, stageNames, openStatuses) is { } wp) { outcome = PackOutcome.Blocked; reason = wp; }
            else if (creates && item.ParentKey is not null && byId.TryGetValue(item.ParentType + ":" + item.ParentKey, out var parent) && parent.Entity is null && parent.Outcome is PackOutcome.LeaveDeleted or PackOutcome.Blocked)
            { outcome = PackOutcome.Blocked; reason = "Its parent item is not available."; }

            var changes = outcome is PackOutcome.Update or PackOutcome.Overwrite or PackOutcome.KeepYours && entity is not null
                ? item.Fields.Where(f => !entity.Fields.TryGetValue(f.Key, out var cur) || cur != f.Value).Select(f => new PackFieldChange(f.Key, Cut(entity.Fields.GetValueOrDefault(f.Key)), Cut(f.Value))).ToList() : [];
            var p = new Planned(item, outcome, entity, link, conflict, reason, changes);
            planned.Add(p); byId[item.Id] = p;
        }
        BlockCategoryTreeViolations(planned, cache.GetValueOrDefault(PackTypes.Category) ?? await LoadAsync(PackTypes.Category, tenantId, ct));
        return planned;
    }

    private static string? Cut(string? s) => s is null ? null : s.Length <= 200 ? s : s[..200] + "…";

    private string? WorkflowProblem(PackWorkflow w, HashSet<string> leadFields, HashSet<string> packLeadFields, HashSet<string> stageNames, HashSet<string> openStatuses)
    {
        var raw = (w.TriggerCondition?.GetRawText() ?? "") + string.Concat(w.Steps.Select(s => (s.Condition?.GetRawText() ?? "") + (s.Config?.GetRawText() ?? "")));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(raw, @"lead\.cf\.([a-z][a-z0-9_]{0,39})"))
            if (!leadFields.Contains(m.Groups[1].Value) && !packLeadFields.Contains(m.Groups[1].Value)) return $"The workflow uses the lead field '{m.Groups[1].Value}', which does not exist in this workspace.";
        foreach (var s in w.Steps)
        {
            var cfg = s.Config is { ValueKind: JsonValueKind.Object } c ? c : (JsonElement?)null;
            string? Get(string k) => cfg is { } e && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;
            if (s.Type == nameof(StepType.ChangeLeadStage) && Get("stage") is { } st && !stageNames.Contains(PackHash.Low(st))) return $"The workflow moves leads to the stage '{st}', which does not exist in this workspace.";
            if (s.Type == nameof(StepType.ChangeLeadStatus) && Get("status") is { } ss && !openStatuses.Contains(ss)) return $"The workflow sets the status '{ss}', which does not exist (or is a closing status) in this workspace.";
        }
        return null;
    }

    /// <summary>Pack categories must not push the workspace's category tree beyond its depth limit or into a loop.</summary>
    private static void BlockCategoryTreeViolations(List<Planned> planned, List<Existing> existing)
    {
        var parent = existing.Where(e => e.Fields["parent"].Length > 0).ToDictionary(e => e.Natural, e => Normalizer.Name(e.Fields["parent"]));
        var applies = planned.Where(p => p.Item.Type == PackTypes.Category && p.Outcome is PackOutcome.Create or PackOutcome.Recreate or PackOutcome.Update or PackOutcome.Overwrite).ToList();
        foreach (var p in applies) { var pn = p.Item.Fields["parent"]; if (pn.Length > 0) parent[p.Item.Natural] = Normalizer.Name(pn); else parent.Remove(p.Item.Natural); }
        for (var i = 0; i < planned.Count; i++)
        {
            var p = planned[i];
            if (!applies.Contains(p)) continue;
            var depth = 1; var cur = p.Item.Natural; var seen = new HashSet<string> { cur }; string? bad = null;
            while (parent.TryGetValue(cur, out var next)) { if (!seen.Add(next)) { bad = "The category tree would contain a loop."; break; } depth++; cur = next; }
            if (bad is null && depth > LeadCategory.MaxDepth) bad = $"Categories can be nested at most {LeadCategory.MaxDepth} levels deep.";
            if (bad is not null) planned[i] = p with { Outcome = PackOutcome.Blocked, Reason = bad, Conflict = false, Changes = [] };
        }
    }

    // ------------------------------------------------------------------------------------------------ apply

    /// <summary>Performs the planned outcomes. The caller owns the transaction and the final SaveChanges.</summary>
    public async Task<Dictionary<string, Guid>> ExecuteAsync(PackDocument doc, IReadOnlyList<Planned> plan, ApplyPackOptions o, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var now = crm.Now;
        var ids = new Dictionary<string, Guid>();
        foreach (var p in plan.Where(p => p.Entity is not null)) ids[p.Item.Id] = p.Entity!.Id;
        var pendingRules = new List<(PackItem Item, Existing? Existing)>();
        string? lastType = null;
        var packNaturalToId = new Dictionary<string, Guid>();

        foreach (var p in plan)
        {
            if (lastType is not null && lastType != p.Item.Type) await Db.SaveChangesAsync(ct);
            lastType = p.Item.Type;
            var write = p.Outcome is PackOutcome.Create or PackOutcome.Recreate or PackOutcome.Update or PackOutcome.Overwrite;
            Guid id = p.Entity?.Id ?? Guid.Empty;
            if (write)
            {
                if (p.Item.Type == PackTypes.Rule) { pendingRules.Add((p.Item, p.Outcome is PackOutcome.Create or PackOutcome.Recreate ? null : p.Entity)); }
                else if (p.Outcome is PackOutcome.Create or PackOutcome.Recreate) id = await CreateAsync(doc, p.Item, ids, tenantId, now, ct);
                else await UpdateAsync(doc, p.Item, p.Entity!.Id, ids, tenantId, now, ct);
                if (id != Guid.Empty) ids[p.Item.Id] = id;
            }
            if (write || p.Outcome == PackOutcome.Link || (p.Outcome == PackOutcome.Unchanged && p.Link is not null && p.Link.AppliedHash != p.Item.Hash))
            {
                var linked = p.Item.Type == PackTypes.Rule ? Guid.Empty : id;
                var link = await Db.PackItemLinks.FirstOrDefaultAsync(l => l.TenantId == tenantId && l.PackKey == doc.Key && l.ItemType == p.Item.Type && l.ItemKey == p.Item.Key, ct);
                if (link is null) Db.PackItemLinks.Add(PackItemLink.Create(tenantId, doc.Key, p.Item.Type, p.Item.Key, linked, p.Item.Hash, doc.Version, now));
                else link.Refresh(linked, p.Item.Hash, doc.Version, now);
            }
        }
        await Db.SaveChangesAsync(ct);
        if (pendingRules.Count > 0) { await PublishRulesAsync(pendingRules, tenantId, now, ct); await Db.SaveChangesAsync(ct); }

        if (o.DefaultPipelineKey is { } pk && ids.TryGetValue(PackTypes.Pipeline + ":" + pk, out var pid))
        {
            foreach (var pipe in await Db.Pipelines.Where(x => x.TenantId == tenantId).ToListAsync(ct)) { var want = pipe.Id == pid; if (pipe.IsDefault != want) { if (want && !pipe.IsActive) pipe.SetActive(true, now); pipe.SetDefault(want, now); } }
            await Db.SaveChangesAsync(ct);
        }
        return ids;
    }

    private async Task<Guid> CreateAsync(PackDocument doc, PackItem i, Dictionary<string, Guid> ids, Guid tenantId, DateTime now, CancellationToken ct)
    {
        Guid Parent() => ids.TryGetValue(i.ParentType + ":" + i.ParentKey, out var g) ? g : throw new PackApplyException($"'{i.Name}' needs '{i.ParentKey}', which is not available.");
        switch (i.Type)
        {
            case PackTypes.Category:
            {
                var c = (PackCategory)i.Source;
                var cat = LeadCategory.Create(tenantId, c.Name, now, c.Parent is null ? null : Parent());
                Db.LeadCategories.Add(cat); return cat.Id;
            }
            case PackTypes.Pipeline:
            {
                var p = (PackPipeline)i.Source;
                var pipe = Pipeline.Create(tenantId, p.Name, false, now); Db.Pipelines.Add(pipe); return pipe.Id;
            }
            case PackTypes.Stage:
            {
                var s = (PackStage)i.Source; var pid = Parent();
                var max = await Db.PipelineStages.Where(x => x.TenantId == tenantId && x.PipelineId == pid).MaxAsync(x => (int?)x.Position, ct) ?? 0;
                var pending = Db.ChangeTracker.Entries<PipelineStage>().Where(e => e.State == EntityState.Added && e.Entity.PipelineId == pid).Select(e => e.Entity.Position).DefaultIfEmpty(0).Max();
                var st = PipelineStage.Create(tenantId, pid, s.Name, Math.Max(max, pending) + 10, Enum.Parse<StageKind>(s.Kind), now); Db.PipelineStages.Add(st); return st.Id;
            }
            case PackTypes.Field:
            {
                var f = (PackCustomField)i.Source;
                var def = CustomFieldDefinition.Create(tenantId, Enum.Parse<CustomEntityType>(f.Entity), f.Key, f.Label, Enum.Parse<CustomFieldType>(f.Type), f.Options, f.Required, f.Min, f.Max, f.Pattern, f.Help, f.Filterable,
                    (await Db.CustomFieldDefinitions.CountAsync(x => x.TenantId == tenantId, ct)) * 10, now, crm.Actor);
                Db.CustomFieldDefinitions.Add(def); return def.Id;
            }
            case PackTypes.Template:
            {
                var t = (PackTemplate)i.Source;
                var tpl = MessageTemplate.Create(tenantId, t.Name, t.Body, Enum.Parse<TemplateCategory>(t.Category), crm.Actor, now); Db.MessageTemplates.Add(tpl); return tpl.Id;
            }
            case PackTypes.Agent:
            {
                var a = (PackAgent)i.Source;
                var hasDefault = await Db.SalesAgents.AnyAsync(x => x.TenantId == tenantId && x.IsDefault && x.IsActive, ct);
                var agent = SalesAgent.Create(tenantId, a.Name, a.Instructions, !hasDefault, doc.Sample, now, crm.Actor);
                agent.Update(a.Name, a.Description, a.Instructions, null, null, [], a.OffTopicReply ?? DefaultOffTopic, now, crm.Actor, keepSampleFlag: true);
                Db.SalesAgents.Add(agent); return agent.Id;
            }
            case PackTypes.Question:
            {
                var (q, order) = ((PackQuestion, int))i.Source;
                var row = AgentQualificationQuestion.Create(tenantId, Parent(), q.Key, q.Question, Enum.Parse<AnswerType>(q.Type), q.Choices, q.Required, order * 10); Db.AgentQualificationQuestions.Add(row); return row.Id;
            }
            case PackTypes.Product:
            {
                var p = (PackProduct)i.Source;
                var prod = CatalogProduct.Create(tenantId, p.Name, p.Summary, PackHash.Lines(p.Features), null, null, false, doc.Sample, now, crm.Actor, Enum.Parse<CatalogItemKind>(p.Kind)); Db.CatalogProducts.Add(prod); return prod.Id;
            }
            case PackTypes.Plan:
            {
                var p = (PackPlan)i.Source;
                var plan = PricingPlan.Create(tenantId, Parent(), p.Name, p.Price, p.Currency, p.Period, p.Details, null, false, doc.Sample, now, crm.Actor); Db.PricingPlans.Add(plan); return plan.Id;
            }
            case PackTypes.Knowledge:
            {
                var k = (PackKnowledge)i.Source; var src = KnowledgeSource.Create(tenantId, KnowledgeSourceType.Text, k.Name, null, now); Db.KnowledgeSources.Add(src); return src.Id;
            }
            case PackTypes.DiscoveryCategory:
            {
                var c = (PackDiscoveryCategory)i.Source;
                var m = DiscoveryCategoryMapping.Create(tenantId, Parent(), PackHash.Lines(c.SearchPhrases), c.PlaceType, now); Db.DiscoveryCategoryMappings.Add(m); return m.Id;
            }
            case PackTypes.DiscoveryLocation:
            {
                var l = (PackDiscoveryLocation)i.Source;
                var loc = DiscoveryLocationPreset.Create(tenantId, l.Country, l.Province, l.City, null, null, now); Db.DiscoveryLocationPresets.Add(loc); return loc.Id;
            }
            case PackTypes.Workflow:
            {
                var w = (PackWorkflow)i.Source;
                var r = await workflows.CreateAsync(new CreateWorkflowRequest(w.Name, w.Description, PackParser.ToDefinition(w)), ct);
                if (!r.IsSuccess) throw new PackApplyException($"Workflow '{w.Name}' could not be created: {r.Error.Message}");
                return r.Value.Id;
            }
            default: throw new PackApplyException("Unknown pack item type.");
        }
    }

    private async Task UpdateAsync(PackDocument doc, PackItem i, Guid id, Dictionary<string, Guid> ids, Guid tenantId, DateTime now, CancellationToken ct)
    {
        switch (i.Type)
        {
            case PackTypes.Category:
            {
                var c = (PackCategory)i.Source;
                var cat = await Db.LeadCategories.FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct);
                cat.Rename(c.Name, now);
                cat.SetParent(c.Parent is null ? null : ids.TryGetValue(PackTypes.Category + ":" + c.Parent, out var pp) ? pp : throw new PackApplyException($"'{c.Name}' needs '{c.Parent}', which is not available."), now);
                break;
            }
            case PackTypes.Pipeline:
            {
                var pipe = await Db.Pipelines.FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct); pipe.Rename(((PackPipeline)i.Source).Name, now); break;
            }
            case PackTypes.Stage:
            {
                var s = (PackStage)i.Source;
                var st = await Db.PipelineStages.FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct); st.Update(s.Name, Enum.Parse<StageKind>(s.Kind), now); break;
            }
            case PackTypes.Field:
            {
                var f = (PackCustomField)i.Source;
                var def = await Db.CustomFieldDefinitions.FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct);
                def.Update(f.Label, f.Options, f.Required, f.Min, f.Max, f.Pattern, f.Help, f.Filterable, def.SortOrder, now, crm.Actor); break;
            }
            case PackTypes.Template:
            {
                var t = (PackTemplate)i.Source;
                var tpl = await Db.MessageTemplates.FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct); tpl.Update(t.Name, t.Body, Enum.Parse<TemplateCategory>(t.Category), crm.Actor, now); break;
            }
            case PackTypes.Agent:
            {
                var a = (PackAgent)i.Source;
                var agent = await Db.SalesAgents.FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct);
                agent.Update(a.Name, a.Description, a.Instructions, agent.ChatModel, agent.MinConfidence, agent.EnabledTools, a.OffTopicReply ?? DefaultOffTopic, now, crm.Actor, keepSampleFlag: agent.IsSample); break;
            }
            case PackTypes.Question:
            {
                var (q, order) = ((PackQuestion, int))i.Source;
                var old = await Db.AgentQualificationQuestions.FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct);
                var agentId = old.AgentId;
                Db.AgentQualificationQuestions.Remove(old); await Db.SaveChangesAsync(ct);   // unique (agent, key): the old row must be gone first
                Db.AgentQualificationQuestions.Add(AgentQualificationQuestion.Create(tenantId, agentId, q.Key, q.Question, Enum.Parse<AnswerType>(q.Type), q.Choices, q.Required, order * 10)); break;
            }
            case PackTypes.Product:
            {
                var p = (PackProduct)i.Source;
                var prod = await Db.CatalogProducts.FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct);
                // changed facts are never silently trusted: the product is unconfirmed again until a person confirms it
                prod.Update(p.Name, p.Summary, PackHash.Lines(p.Features), prod.DemoProcess, prod.TrialProcess, false, prod.IsActive, now, crm.Actor, Enum.Parse<CatalogItemKind>(p.Kind)); break;
            }
            case PackTypes.Plan:
            {
                var p = (PackPlan)i.Source;
                var plan = await Db.PricingPlans.FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct);
                plan.Update(p.Name, p.Price, p.Currency, p.Period, p.Details, plan.MaxDiscountPercent, false, plan.IsActive, now, crm.Actor); break;
            }
            case PackTypes.Knowledge:
            {
                var k = (PackKnowledge)i.Source; var src = await Db.KnowledgeSources.FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct); src.Rename(k.Name, now); break;
            }
            case PackTypes.DiscoveryCategory:
            {
                var c = (PackDiscoveryCategory)i.Source; var m = await Db.DiscoveryCategoryMappings.FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct); m.Update(PackHash.Lines(c.SearchPhrases), c.PlaceType, now); break;
            }
            case PackTypes.DiscoveryLocation: break;   // a location is only its label: nothing else can differ
            case PackTypes.Workflow:
            {
                var w = (PackWorkflow)i.Source; var def = PackParser.ToDefinition(w);
                var u = await workflows.UpdateAsync(id, new UpdateWorkflowRequest(w.Name, w.Description), ct);
                if (!u.IsSuccess) throw new PackApplyException($"Workflow '{w.Name}' could not be updated: {u.Error.Message}");
                var latest = await Db.WorkflowVersions.AsNoTracking().Where(x => x.TenantId == tenantId && x.WorkflowId == id).OrderByDescending(x => x.Number).FirstAsync(ct);
                if (latest.State != VersionState.Draft)
                {
                    var nd = await workflows.NewDraftFromAsync(id, latest.Number, ct);
                    if (!nd.IsSuccess) throw new PackApplyException($"Workflow '{w.Name}' could not get a new draft: {nd.Error.Message}");
                }
                var sd = await workflows.SaveDraftAsync(id, new SaveDraftRequest(def), ct);
                if (!sd.IsSuccess) throw new PackApplyException($"Workflow '{w.Name}' could not be updated: {sd.Error.Message}");
                break;
            }
        }
    }

    /// <summary>All scoring rule changes of one import become ONE new rule set version (existing scores are untouched until a recalculation).</summary>
    private async Task PublishRulesAsync(List<(PackItem Item, Existing? Existing)> pending, Guid tenantId, DateTime now, CancellationToken ct)
    {
        await crm.EnsureDefaultsAsync(ct);
        var current = await Db.ScoringRuleSets.Where(s => s.TenantId == tenantId && s.IsActive).OrderByDescending(s => s.Version).ToListAsync(ct);
        var cur = current.FirstOrDefault();
        var rules = cur is null ? [] : await Db.ScoringRules.AsNoTracking().Where(r => r.TenantId == tenantId && r.RuleSetId == cur.Id).ToListAsync(ct);
        var next = (await Db.ScoringRuleSets.Where(s => s.TenantId == tenantId).MaxAsync(s => (int?)s.Version, ct) ?? 0) + 1;
        foreach (var c in current) c.Deactivate();
        var set = ScoringRuleSet.Create(tenantId, next, "Industry pack import", now, crm.Actor); Db.ScoringRuleSets.Add(set);
        var byNatural = pending.ToDictionary(p => p.Item.Natural, p => (PackScoringRule)p.Item.Source);
        var written = new HashSet<string>();
        foreach (var r in rules)
        {
            var nat = r.Dimension + "|" + PackHash.Low(r.Target);
            if (byNatural.TryGetValue(nat, out var pr)) { Db.ScoringRules.Add(ScoringRule.Create(tenantId, set.Id, r.Dimension, r.Target, pr.Points, pr.Description, true)); written.Add(nat); }
            else Db.ScoringRules.Add(ScoringRule.Create(tenantId, set.Id, r.Dimension, r.Target, r.Points, r.Description, r.IsEnabled));
        }
        foreach (var (nat, pr) in byNatural.Where(x => !written.Contains(x.Key)))
            Db.ScoringRules.Add(ScoringRule.Create(tenantId, set.Id, Enum.Parse<ScoreDimension>(pr.Dimension), pr.Target, pr.Points, pr.Description, true));
    }
}
