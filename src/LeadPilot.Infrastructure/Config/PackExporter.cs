using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LeadPilot.Application.Config;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Crm;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Config;

/// <summary>
/// Writes the active workspace's configuration as a pack. It only reads configuration tables: never people (leads, contacts, conversations, users), credentials, API keys, channel accounts, webhooks or settings.
/// Free text is scanned for e-mail addresses and phone-like numbers, which are replaced and reported. The result is checked by the same parser that imports it.
/// </summary>
public sealed partial class PackExporter(CrmContext crm)
{
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(\.[A-Za-z0-9\-]+)+")] private static partial Regex Email();
    [GeneratedRegex(@"(?<![\w])\+?\d[\d\s().\-]{6,}\d(?![\w])")] private static partial Regex Phone();

    private int _redactions;
    private string? S(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var t = Email().Replace(v, _ => { _redactions++; return "[email removed]"; });
        t = Phone().Replace(t, _ => { _redactions++; return "[number removed]"; });
        return t;
    }
    private JsonNode? SN(string? v) => S(v) is { } s ? JsonValue.Create(s) : null;
    private static JsonArray Arr(IEnumerable<JsonNode?> items) => new(items.ToArray());

    public async Task<(string? Json, IReadOnlyDictionary<string, int> Counts, List<string> Warnings, IReadOnlyList<PackProblem> Problems)> ExportAsync(PackExportRequest req, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var want = (req.Sections ?? PackSections.All).ToHashSet(StringComparer.Ordinal);
        var warnings = new List<string>(); var counts = new Dictionary<string, int>();
        var root = new JsonObject
        {
            ["format"] = PackParser.Format, ["formatVersion"] = PackParser.FormatVersion, ["key"] = req.Key, ["version"] = req.Version, ["name"] = req.Name,
            ["description"] = SN(req.Description), ["industry"] = SN(req.Industry),
        };

        var cats = await db.LeadCategories.AsNoTracking().Where(c => c.TenantId == tenantId && c.IsActive).OrderBy(c => c.Name).ToListAsync(ct);
        var catKeys = new Dictionary<Guid, string>(); var used = new HashSet<string>();
        foreach (var c in cats) catKeys[c.Id] = Unique(Slug(c.Name), used);
        if (want.Contains(PackSections.Categories) || want.Contains(PackSections.Discovery))
        {
            // a category whose parent is inactive is exported at the top level
            var arr = cats.Select(c => (JsonNode?)new JsonObject { ["key"] = catKeys[c.Id], ["name"] = SN(c.Name), ["parent"] = c.ParentId is { } p && catKeys.TryGetValue(p, out var pk) ? pk : null });
            if (want.Contains(PackSections.Categories)) { root["categories"] = Arr(arr); counts["categories"] = cats.Count; }
            else { /* discovery needs the categories it points at */ root["categories"] = Arr(arr); counts["categories"] = cats.Count; warnings.Add("Categories were added because the discovery section refers to them."); }
        }

        if (want.Contains(PackSections.Pipelines))
        {
            var pipes = await db.Pipelines.AsNoTracking().Where(p => p.TenantId == tenantId && p.IsActive).OrderBy(p => p.Name).ToListAsync(ct);
            var stages = (await db.PipelineStages.AsNoTracking().Where(s => s.TenantId == tenantId).ToListAsync(ct)).ToLookup(s => s.PipelineId);
            var k = new HashSet<string>();
            root["pipelines"] = Arr(pipes.Select(p => (JsonNode?)new JsonObject { ["key"] = Unique(Slug(p.Name), k), ["name"] = SN(p.Name), ["stages"] = Arr(stages[p.Id].OrderBy(s => s.Position).Select(s => (JsonNode?)new JsonObject { ["name"] = SN(s.Name), ["kind"] = s.Kind.ToString() })) }));
            counts["pipelines"] = pipes.Count;
        }

        var leadFieldKeys = new HashSet<string>();
        if (want.Contains(PackSections.CustomFields))
        {
            var fields = await db.CustomFieldDefinitions.AsNoTracking().Where(f => f.TenantId == tenantId && f.IsActive).OrderBy(f => f.EntityType).ThenBy(f => f.SortOrder).ToListAsync(ct);
            root["customFields"] = Arr(fields.Select(f => (JsonNode?)new JsonObject
            {
                ["key"] = f.Key, ["entity"] = f.EntityType.ToString(), ["label"] = SN(f.Label), ["type"] = f.Type.ToString(), ["options"] = f.Options.Count == 0 ? null : Arr(f.Options.Select(o => SN(o))),
                ["required"] = f.IsRequired, ["min"] = f.MinValue, ["max"] = f.MaxValue, ["pattern"] = f.Pattern, ["help"] = SN(f.HelpText), ["filterable"] = f.IsFilterable,
            }));
            counts["customFields"] = fields.Count;
        }

        if (want.Contains(PackSections.ScoringRules))
        {
            var set = await db.ScoringRuleSets.AsNoTracking().Where(s => s.TenantId == tenantId && s.IsActive).OrderByDescending(s => s.Version).FirstOrDefaultAsync(ct);
            var rules = set is null ? [] : await db.ScoringRules.AsNoTracking().Where(r => r.TenantId == tenantId && r.RuleSetId == set.Id && r.IsEnabled && r.Points != 0).OrderBy(r => r.Dimension).ThenBy(r => r.Target).ToListAsync(ct);
            root["scoringRules"] = Arr(rules.Select(r => (JsonNode?)new JsonObject { ["dimension"] = r.Dimension.ToString(), ["target"] = SN(r.Target), ["points"] = r.Points, ["description"] = SN(r.Description) }));
            counts["scoringRules"] = rules.Count;
        }

        if (want.Contains(PackSections.MessageTemplates))
        {
            var t = await db.MessageTemplates.AsNoTracking().Where(x => x.TenantId == tenantId && x.IsActive).OrderBy(x => x.Name).ToListAsync(ct);
            var k = new HashSet<string>();
            root["messageTemplates"] = Arr(t.Select(x => (JsonNode?)new JsonObject { ["key"] = Unique(Slug(x.Name), k), ["name"] = SN(x.Name), ["body"] = SN(x.Body), ["category"] = x.Category.ToString() }));
            counts["messageTemplates"] = t.Count;
        }

        if (want.Contains(PackSections.Agents))
        {
            var agents = await db.SalesAgents.AsNoTracking().Where(a => a.TenantId == tenantId && a.IsActive).OrderBy(a => a.Name).ToListAsync(ct);
            var qs = (await db.AgentQualificationQuestions.AsNoTracking().Where(q => q.TenantId == tenantId).ToListAsync(ct)).ToLookup(q => q.AgentId);
            var k = new HashSet<string>();
            root["agents"] = Arr(agents.Select(a => (JsonNode?)new JsonObject
            {
                ["key"] = Unique(Slug(a.Name), k), ["name"] = SN(a.Name), ["description"] = SN(a.Description), ["instructions"] = SN(a.Instructions), ["offTopicReply"] = SN(a.OffTopicReply),
                ["tools"] = Arr(a.EnabledTools.Where(t => t is not (Application.Agents.AgentTools.CreateTrialAccount or Application.Agents.AgentTools.CreatePaymentRequest) && Application.Agents.AgentToolCatalog.All.Any(d => d.Name == t)).OrderBy(t => t, StringComparer.Ordinal).Select(t => (JsonNode?)JsonValue.Create(t))),
                ["qualificationQuestions"] = Arr(qs[a.Id].OrderBy(q => q.SortOrder).Select(q => (JsonNode?)new JsonObject { ["key"] = q.Key, ["question"] = SN(q.Question), ["type"] = q.AnswerType.ToString(), ["choices"] = q.Choices.Count == 0 ? null : Arr(q.Choices.Select(c => SN(c))), ["required"] = q.Required })),
            }));
            counts["agents"] = agents.Count;
        }

        if (want.Contains(PackSections.Catalog))
        {
            var products = await db.CatalogProducts.AsNoTracking().Where(p => p.TenantId == tenantId && p.IsActive).OrderBy(p => p.Name).ToListAsync(ct);
            var plans = (await db.PricingPlans.AsNoTracking().Where(p => p.TenantId == tenantId && p.IsActive).ToListAsync(ct)).ToLookup(p => p.ProductId);
            var k = new HashSet<string>();
            root["catalog"] = Arr(products.Select(p =>
            {
                var pk = new HashSet<string>();
                return (JsonNode?)new JsonObject
                {
                    ["key"] = Unique(Slug(p.Name), k), ["kind"] = p.Kind.ToString(), ["name"] = SN(p.Name), ["summary"] = SN(p.Summary), ["features"] = p.FeatureList.Count == 0 ? null : Arr(p.FeatureList.Select(f => SN(f))), ["demoProcess"] = SN(p.DemoProcess), ["trialProcess"] = SN(p.TrialProcess),
                    ["plans"] = Arr(plans[p.Id].OrderBy(x => x.Name).Select(x => (JsonNode?)new JsonObject { ["key"] = Unique(Slug(x.Name), pk), ["name"] = SN(x.Name), ["price"] = x.Price, ["currency"] = x.Currency, ["period"] = SN(x.BillingPeriod), ["details"] = SN(x.Details) })),
                };
            }));
            counts["catalog"] = products.Count;
            warnings.Add("The catalog was exported with its prices as you entered them. Imported products always start unconfirmed.");
        }

        if (want.Contains(PackSections.Knowledge))
        {
            var src = await db.KnowledgeSources.AsNoTracking().Where(s => s.TenantId == tenantId && s.Type == KnowledgeSourceType.Text).OrderBy(s => s.Name).ToListAsync(ct);
            var k = new HashSet<string>();
            root["knowledgeBase"] = Arr(src.Select(s => (JsonNode?)new JsonObject { ["key"] = Unique(Slug(s.Name), k), ["name"] = SN(s.Name) }));
            counts["knowledgeBase"] = src.Count;
            warnings.Add("Knowledge: only the names of text sources are exported, never documents, uploads or website addresses.");
        }

        if (want.Contains(PackSections.Workflows))
        {
            var ws = await db.Workflows.AsNoTracking().Where(w => w.TenantId == tenantId && !w.IsDeleted).OrderBy(w => w.Name).ToListAsync(ct);
            var list = new List<JsonNode?>(); var k = new HashSet<string>();
            foreach (var w in ws)
            {
                var v = await db.WorkflowVersions.AsNoTracking().Where(x => x.TenantId == tenantId && x.WorkflowId == w.Id).OrderByDescending(x => x.Number).FirstOrDefaultAsync(ct);
                if (v is null) continue;
                var trg = await db.WorkflowTriggers.AsNoTracking().Where(t => t.TenantId == tenantId && t.WorkflowVersionId == v.Id).ToListAsync(ct);
                var stp = await db.WorkflowSteps.AsNoTracking().Where(s => s.TenantId == tenantId && s.WorkflowVersionId == v.Id).OrderBy(s => s.Position).ToListAsync(ct);
                var blocked = stp.FirstOrDefault(s => s.Type is Domain.Workflows.StepType.SendMessage or Domain.Workflows.StepType.AssignLead
                    || (s.Type == Domain.Workflows.StepType.NotifyUser && !(JsonSerializer.Deserialize<JsonObject>(s.ConfigJson)?["target"]?.GetValue<string>() == "owner")));
                if (trg.Count != 1 || blocked is not null) { warnings.Add($"Workflow '{w.Name}' was not exported: " + (trg.Count != 1 ? "packs support one trigger per workflow." : "it sends messages, assigns people or notifies a specific person, which packs cannot carry.")); continue; }
                list.Add(new JsonObject
                {
                    ["key"] = Unique(Slug(w.Name), k), ["name"] = SN(w.Name), ["description"] = SN(w.Description),
                    ["trigger"] = new JsonObject { ["event"] = trg[0].EventType, ["condition"] = Parse(trg[0].ConditionJson), ["inactiveDays"] = Parse(trg[0].ConfigJson)?["inactiveDays"]?.DeepClone() },
                    ["steps"] = Arr(stp.Select(s => (JsonNode?)new JsonObject { ["key"] = s.Key, ["type"] = s.Type.ToString(), ["name"] = SN(s.Name), ["config"] = Parse(s.ConfigJson), ["condition"] = Parse(s.ConditionJson), ["onFailure"] = s.OnFailure.ToString(), ["maxRetries"] = s.MaxRetries })),
                    ["reEntry"] = v.ReEntry.ToString(), ["cooldownHours"] = v.CooldownHours, ["maxStartsPer30Days"] = v.MaxStartsPerLeadPer30Days,
                });
            }
            root["workflows"] = Arr(list); counts["workflows"] = list.Count;
        }

        if (want.Contains(PackSections.Discovery))
        {
            var maps = await db.DiscoveryCategoryMappings.AsNoTracking().Where(m => m.TenantId == tenantId && m.IsActive).ToListAsync(ct);
            var locs = await db.DiscoveryLocationPresets.AsNoTracking().Where(l => l.TenantId == tenantId).OrderBy(l => l.Label).ToListAsync(ct);
            var mk = new HashSet<string>(); var lk = new HashSet<string>();
            var mapped = maps.Where(m => catKeys.ContainsKey(m.CategoryId)).ToList();
            var simple = locs.Where(l => l.District is null && l.Area is null).ToList();
            if (simple.Count < locs.Count) warnings.Add($"{locs.Count - simple.Count} saved search location(s) with a district or area were not exported (packs hold country, province and city).");
            root["discovery"] = new JsonObject
            {
                ["categories"] = Arr(mapped.Select(m => (JsonNode?)new JsonObject { ["key"] = Unique("map-" + catKeys[m.CategoryId], mk), ["category"] = catKeys[m.CategoryId], ["searchPhrases"] = Arr(m.Terms.Select(t => SN(t))), ["placeType"] = m.PlaceType })),
                ["locations"] = Arr(simple.Select(l => (JsonNode?)new JsonObject { ["key"] = Unique(Slug(l.Label), lk), ["country"] = SN(l.Country), ["province"] = SN(l.Province), ["city"] = SN(l.City) })),
            };
            counts["discovery"] = mapped.Count + simple.Count;
        }

        if (_redactions > 0) warnings.Add($"{_redactions} value(s) that looked like e-mail addresses or phone numbers were replaced with a placeholder.");
        warnings.Add("Never exported: leads, contacts, conversations, messages, users, API keys, channel accounts, webhooks and settings. Review the file before sharing it: free text such as instructions is exported as you wrote it.");
        _ = leadFieldKeys;
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var parsed = PackParser.Parse(json);
        return parsed.IsValid ? (json, counts, warnings, []) : (null, counts, warnings, parsed.Problems);
    }

    private static JsonNode? Parse(string? raw) { if (string.IsNullOrWhiteSpace(raw)) return null; try { var n = JsonNode.Parse(raw); return n is JsonObject { Count: 0 } ? null : n; } catch (JsonException) { return null; } }

    public static string Slug(string name)
    {
        var s = Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (s.Length == 0 || !char.IsAsciiLetterOrDigit(s[0])) s = "item-" + s;
        return s.Length > 50 ? s[..50].TrimEnd('-') : s;
    }

    private static string Unique(string key, HashSet<string> used)
    {
        var k = key; var n = 2;
        while (!used.Add(k)) k = key + "-" + n++.ToString(CultureInfo.InvariantCulture);
        return k;
    }
}
