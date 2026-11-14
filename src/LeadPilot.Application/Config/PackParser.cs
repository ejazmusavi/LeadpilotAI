using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Config;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Domain.Workflows;

namespace LeadPilot.Application.Config;

/// <summary>
/// Strict reader/validator of the pack format. Pack content is data from outside (an uploaded file or another tenant's export), so it is treated as hostile:
/// size and depth limits, unknown or duplicate keys rejected, every string length-limited and stripped of markup, control and bidi characters, every reference checked,
/// every value run through the same domain rules the normal screens use. Nothing in a pack is ever executed or evaluated as code.
/// </summary>
public static class PackParser
{
    public const string Format = "leadpilot.pack";
    public const int FormatVersion = 1;
    public const int MaxBytes = PackDefinition.MaxContentBytes;

    private static readonly Regex Slug = new("^[a-z][a-z0-9-]{1,59}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ItemKey = new("^[a-z0-9][a-z0-9_.-]{0,59}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Markup = new(@"<\s*[a-zA-Z/!?]|&#?\w+;", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static PackParseResult Parse(string? json)
    {
        var problems = new List<PackProblem>();
        if (string.IsNullOrWhiteSpace(json)) return Fail("", "The pack is empty.");
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) return Fail("", $"The pack is larger than {MaxBytes / 1024} KB.");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false }); }
        catch (JsonException ex) { return Fail("", "The pack is not valid JSON: " + Cut(ex.Message, 120)); }
        using (doc)
        {
            var r = new Reader(problems);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail("", "The pack must be a JSON object.");
            r.Known(root, "", "format", "formatVersion", "key", "version", "name", "description", "industry", "sample", "notice", "categories", "pipelines", "customFields", "scoringRules", "messageTemplates", "agents", "catalog", "knowledgeBase", "workflows", "discovery");
            if (r.Str(root, "", "format", true, 40) != Format) problems.Add(new("format", $"The format must be '{Format}'."));
            var fv = r.Int(root, "", "formatVersion", true, 1, 1000);
            if (fv is not null && fv != FormatVersion) problems.Add(new("formatVersion", $"This version of LeadPilot reads format version {FormatVersion}; the pack uses {fv}."));
            var key = r.Str(root, "", "key", true, 60);
            if (key is not null && !Slug.IsMatch(key)) problems.Add(new("key", "The pack key uses lower-case letters, digits and hyphens, starts with a letter (2 to 60 characters)."));
            var version = r.Int(root, "", "version", true, 1, 100000) ?? 0;
            var name = r.Str(root, "", "name", true, 150);
            var desc = r.Str(root, "", "description", false, 1000, multiline: true);
            var industry = r.Str(root, "", "industry", false, 60);
            var sample = r.Bool(root, "", "sample") ?? false;
            var notice = r.Str(root, "", "notice", false, 200);
            if (sample && string.IsNullOrWhiteSpace(notice)) problems.Add(new("notice", "A sample pack needs a notice such as 'SAMPLE – review before use'."));

            var categories = r.List(root, "categories", 300, (e, p) => ReadCategory(r, e, p));
            var pipelines = r.List(root, "pipelines", 10, (e, p) => ReadPipeline(r, e, p));
            var fields = r.List(root, "customFields", 200, (e, p) => ReadField(r, e, p));
            var rules = r.List(root, "scoringRules", 100, (e, p) => ReadRule(r, e, p));
            var templates = r.List(root, "messageTemplates", 50, (e, p) => ReadTemplate(r, e, p));
            var agents = r.List(root, "agents", 5, (e, p) => ReadAgent(r, e, p));
            var catalog = r.List(root, "catalog", 50, (e, p) => ReadProduct(r, e, p));
            var knowledge = r.List(root, "knowledgeBase", 20, (e, p) => ReadKnowledge(r, e, p));
            var workflows = r.List(root, "workflows", 10, (e, p) => ReadWorkflow(r, e, p));
            var discovery = root.TryGetProperty("discovery", out var dEl) ? ReadDiscovery(r, dEl) : null;

            CrossCheck(problems, categories, fields, rules, discovery);
            if (problems.Count > 0 || key is null || name is null) return new(null, null, null, problems.Take(60).ToList());
            var pack = new PackDocument(key, version, name, desc, industry, sample, notice, categories, pipelines, fields, rules, templates, agents, catalog, knowledge, workflows, discovery);
            var canonical = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = false });
            return new(pack, canonical, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant(), []);
        }

        static PackParseResult Fail(string path, string msg) => new(null, null, null, [new(path, msg)]);
    }

    // ------------------------------------------------------------------------------------------------ sections

    private static PackCategory? ReadCategory(Reader r, JsonElement e, string p)
    {
        if (!r.Obj(e, p, "key", "name", "parent")) return null;
        var key = r.Key(e, p); var name = r.Str(e, p, "name", true, 100); var parent = r.Str(e, p, "parent", false, 60);
        return key is null || name is null ? null : new(key, name, parent);
    }

    private static PackPipeline? ReadPipeline(Reader r, JsonElement e, string p)
    {
        if (!r.Obj(e, p, "key", "name", "stages")) return null;
        var key = r.Key(e, p); var name = r.Str(e, p, "name", true, 100);
        var stages = r.List(e, "stages", 30, (s, sp) =>
        {
            if (!r.Obj(s, sp, "name", "kind")) return null;
            var n = r.Str(s, sp, "name", true, 100); var k = r.Str(s, sp, "kind", true, 20);
            if (k is not null && (!Enum.TryParse<StageKind>(k, false, out var sk) || !Enum.IsDefined(sk))) { r.Problems.Add(new(sp + ".kind", "Stage kind is Open, Won, Lost or Suppressed.")); return null; }
            return n is null || k is null ? null : new PackStage(n, k);
        }, p);
        if (stages.Count < 2) r.Problems.Add(new(p + ".stages", "A pipeline needs at least two stages."));
        if (stages.Select(s => s.Name.ToLowerInvariant()).Distinct().Count() != stages.Count) r.Problems.Add(new(p + ".stages", "Stage names must be different from each other."));
        if (!stages.Any(s => s.Kind == "Won") || !stages.Any(s => s.Kind == "Lost")) r.Problems.Add(new(p + ".stages", "A pipeline needs a Won and a Lost stage."));
        return key is null || name is null ? null : new(key, name, stages);
    }

    private static PackCustomField? ReadField(Reader r, JsonElement e, string p)
    {
        if (!r.Obj(e, p, "key", "entity", "label", "type", "options", "required", "min", "max", "pattern", "help", "filterable")) return null;
        var key = r.Str(e, p, "key", true, 40); var entity = r.Str(e, p, "entity", true, 20); var label = r.Str(e, p, "label", true, 100); var type = r.Str(e, p, "type", true, 20);
        var options = r.StrList(e, p, "options", 50, 80);
        var req = r.Bool(e, p, "required") ?? false; var filt = r.Bool(e, p, "filterable") ?? false;
        var min = r.Dec(e, p, "min"); var max = r.Dec(e, p, "max"); var pattern = r.Str(e, p, "pattern", false, 200, allowMarkup: true); var help = r.Str(e, p, "help", false, 200);
        if (key is null || entity is null || label is null || type is null) return null;
        if (!Enum.TryParse<CustomEntityType>(entity, false, out var et) || !Enum.IsDefined(et)) { r.Problems.Add(new(p + ".entity", "Entity is Lead, Contact, Opportunity or Customer.")); return null; }
        if (!Enum.TryParse<CustomFieldType>(type, false, out var ft) || !Enum.IsDefined(ft)) { r.Problems.Add(new(p + ".type", "Type is Text, Number, Date, Select, MultiSelect or Bool.")); return null; }
        try { _ = CustomFieldDefinition.Create(Guid.Empty, et, key, label, ft, options, req, min, max, pattern, help, filt, 0, DateTime.UnixEpoch, null); }
        catch (Domain.Common.DomainException ex) { r.Problems.Add(new(p, ex.Message)); return null; }
        return new(key.ToLowerInvariant(), entity, label, type, options, req, min, max, pattern, help, filt);
    }

    private static PackScoringRule? ReadRule(Reader r, JsonElement e, string p)
    {
        if (!r.Obj(e, p, "dimension", "target", "points", "description")) return null;
        var dim = r.Str(e, p, "dimension", true, 30); var target = r.Str(e, p, "target", true, 200); var points = r.Int(e, p, "points", true, -100, 100); var desc = r.Str(e, p, "description", false, 200);
        if (dim is null || target is null || points is null) return null;
        if (!Enum.TryParse<ScoreDimension>(dim, false, out var d) || !Enum.IsDefined(d)) { r.Problems.Add(new(p + ".dimension", "Unknown scoring dimension.")); return null; }
        if (ScoringRuleParser.Validate(d, target, points.Value) is { } problem) { r.Problems.Add(new(p + ".target", problem)); return null; }
        return new(dim, target, points.Value, desc);
    }

    private static PackTemplate? ReadTemplate(Reader r, JsonElement e, string p)
    {
        if (!r.Obj(e, p, "key", "name", "body", "category")) return null;
        var key = r.Key(e, p); var name = r.Str(e, p, "name", true, 100); var body = r.Str(e, p, "body", true, Message.MaxBodyLength, multiline: true, allowPlaceholders: true); var cat = r.Str(e, p, "category", true, 20);
        if (key is null || name is null || body is null || cat is null) return null;
        if (!Enum.TryParse<TemplateCategory>(cat, false, out var tc) || !Enum.IsDefined(tc)) { r.Problems.Add(new(p + ".category", "Category is Service or Marketing.")); return null; }
        var issues = TemplateRenderer.Problems(body);
        if (issues.Count > 0) { r.Problems.Add(new(p + ".body", issues[0])); return null; }
        var unknown = TemplateRenderer.UnknownVariables(body);
        if (unknown.Count > 0) { r.Problems.Add(new(p + ".body", $"Unknown placeholder {{{{{unknown[0]}}}}}. Available: {string.Join(", ", TemplateRenderer.KnownVariables)}.")); return null; }
        return new(key, name, body, cat);
    }

    private static PackAgent? ReadAgent(Reader r, JsonElement e, string p)
    {
        if (!r.Obj(e, p, "key", "name", "description", "instructions", "offTopicReply", "qualificationQuestions")) return null;
        var key = r.Key(e, p); var name = r.Str(e, p, "name", true, 100); var desc = r.Str(e, p, "description", false, 500);
        var instr = r.Str(e, p, "instructions", true, 8000, multiline: true); var off = r.Str(e, p, "offTopicReply", false, 500, multiline: true);
        var qs = r.List(e, "qualificationQuestions", 20, (q, qp) =>
        {
            if (!r.Obj(q, qp, "key", "question", "type", "choices", "required")) return null;
            var k = r.Str(q, qp, "key", true, 40); var text = r.Str(q, qp, "question", true, 300); var t = r.Str(q, qp, "type", true, 20);
            var choices = r.StrList(q, qp, "choices", 12, 80); var req = r.Bool(q, qp, "required") ?? false;
            if (k is null || text is null || t is null) return null;
            if (!Enum.TryParse<AnswerType>(t, false, out var at) || !Enum.IsDefined(at)) { r.Problems.Add(new(qp + ".type", "Type is Text, Number or Choice.")); return null; }
            try { _ = AgentQualificationQuestion.Create(Guid.Empty, Guid.Empty, k, text, at, choices, req, 0); }
            catch (Domain.Common.DomainException ex) { r.Problems.Add(new(qp, ex.Message)); return null; }
            return new PackQuestion(k.ToLowerInvariant(), text, t, choices, req);
        }, p);
        if (qs.Select(q => q.Key).Distinct().Count() != qs.Count) r.Problems.Add(new(p + ".qualificationQuestions", "Question keys must be different from each other."));
        return key is null || name is null || instr is null ? null : new(key, name, desc, instr, off, qs);
    }

    private static PackProduct? ReadProduct(Reader r, JsonElement e, string p)
    {
        if (!r.Obj(e, p, "key", "kind", "name", "summary", "features", "plans")) return null;
        var key = r.Key(e, p); var kind = r.Str(e, p, "kind", true, 20); var name = r.Str(e, p, "name", true, 150); var summary = r.Str(e, p, "summary", false, 1000, multiline: true);
        var features = r.StrList(e, p, "features", 60, 200);
        if (kind is not null && (!Enum.TryParse<CatalogItemKind>(kind, false, out var ck) || !Enum.IsDefined(ck))) { r.Problems.Add(new(p + ".kind", "Kind is Product or Service.")); return null; }
        var plans = r.List(e, "plans", 10, (pl, pp) =>
        {
            if (!r.Obj(pl, pp, "key", "name", "price", "currency", "period", "details")) return null;
            var k = r.Key(pl, pp); var n = r.Str(pl, pp, "name", true, 100); var price = r.Dec(pl, pp, "price"); var cur = r.Str(pl, pp, "currency", false, 3) ?? "USD";
            var period = r.Str(pl, pp, "period", false, 40); var details = r.Str(pl, pp, "details", false, 2000, multiline: true);
            if (price is < 0 or > 1_000_000_000m || (price is { } pr && decimal.Round(pr, 2) != pr)) { r.Problems.Add(new(pp + ".price", "The price is between 0 and 1,000,000,000 with at most two decimals, or left out.")); return null; }
            if (cur.Length != 3 || !cur.All(char.IsAsciiLetter)) { r.Problems.Add(new(pp + ".currency", "The currency is a 3-letter code such as USD.")); return null; }
            return k is null || n is null ? null : new PackPlan(k, n, price, cur.ToUpperInvariant(), period, details);
        }, p);
        if (plans.Select(x => x.Key).Distinct().Count() != plans.Count) r.Problems.Add(new(p + ".plans", "Plan keys must be different from each other."));
        return key is null || kind is null || name is null ? null : new(key, kind, name, summary, features, plans);
    }

    private static PackKnowledge? ReadKnowledge(Reader r, JsonElement e, string p)
    {
        if (!r.Obj(e, p, "key", "name")) return null;
        var key = r.Key(e, p); var name = r.Str(e, p, "name", true, 150);
        return key is null || name is null ? null : new(key, name);
    }

    private static PackWorkflow? ReadWorkflow(Reader r, JsonElement e, string p)
    {
        if (!r.Obj(e, p, "key", "name", "description", "trigger", "steps", "reEntry", "cooldownHours", "maxStartsPer30Days")) return null;
        var key = r.Key(e, p); var name = r.Str(e, p, "name", true, 150); var desc = r.Str(e, p, "description", false, 1000, multiline: true);
        var reEntry = r.Str(e, p, "reEntry", false, 20) ?? "AfterCompletion"; var cooldown = r.Int(e, p, "cooldownHours", false, 0, WorkflowVersion.MaxCooldownHours) ?? 0; var maxStarts = r.Int(e, p, "maxStartsPer30Days", false, 0, 100) ?? 3;
        string? ev = null; JsonElement? tcond = null; int? inactive = null;
        if (e.TryGetProperty("trigger", out var t) && r.Obj(t, p + ".trigger", "event", "condition", "inactiveDays"))
        {
            ev = r.Str(t, p + ".trigger", "event", true, 60); inactive = r.Int(t, p + ".trigger", "inactiveDays", false, 1, 365);
            if (t.TryGetProperty("condition", out var c)) { r.CheckJsonValue(c, p + ".trigger.condition"); tcond = c.Clone(); }
        }
        else if (!e.TryGetProperty("trigger", out _)) r.Problems.Add(new(p + ".trigger", "A workflow needs a trigger."));
        var steps = r.List(e, "steps", WorkflowLimits.MaxSteps, (s, sp) =>
        {
            if (!r.Obj(s, sp, "key", "type", "name", "config", "condition", "onFailure", "maxRetries")) return null;
            var k = r.Str(s, sp, "key", true, 20); var ty = r.Str(s, sp, "type", true, 30); var n = r.Str(s, sp, "name", true, 100);
            var onFail = r.Str(s, sp, "onFailure", false, 20) ?? "Retry"; var retries = r.Int(s, sp, "maxRetries", false, 0, 5) ?? 2;
            JsonElement? cfg = null, cond = null;
            if (s.TryGetProperty("config", out var c)) { r.CheckJsonValue(c, sp + ".config"); cfg = c.Clone(); }
            if (s.TryGetProperty("condition", out var cd)) { r.CheckJsonValue(cd, sp + ".condition"); cond = cd.Clone(); }
            if (k is null || ty is null || n is null) return null;
            if (!Enum.TryParse<StepType>(ty, false, out var st) || !Enum.IsDefined(st)) { r.Problems.Add(new(sp + ".type", "Unknown step type.")); return null; }
            if (st == StepType.NotifyUser && !(cfg is { ValueKind: JsonValueKind.Object } nc && nc.TryGetProperty("target", out var tg) && tg.ValueKind == JsonValueKind.String && tg.GetString() == "owner")) { r.Problems.Add(new(sp + ".config", "A pack can only notify the lead's owner (target \"owner\"), never a specific person.")); return null; }
            if (st is StepType.SendMessage or StepType.AssignLead) { r.Problems.Add(new(sp + ".type", "A pack cannot add steps that send messages or assign people: they need your WhatsApp account and team, so add them after the import.")); return null; }
            return new PackStep(k, ty, n, cfg, cond, onFail, retries);
        }, p);
        if (key is null || name is null || ev is null) return null;
        if (!Enum.TryParse<ReEntryPolicy>(reEntry, false, out var rp) || !Enum.IsDefined(rp)) { r.Problems.Add(new(p + ".reEntry", "Re-entry is Once, AfterCompletion or Always.")); return null; }
        foreach (var s in steps) if (!Enum.TryParse<FailurePolicy>(s.OnFailure, false, out var fp) || !Enum.IsDefined(fp)) { r.Problems.Add(new(p + ".steps", "onFailure is Retry, Skip or Stop.")); return null; }
        var wf = new PackWorkflow(key, name, desc, ev, tcond, inactive, steps, reEntry, cooldown, maxStarts);
        var report = WorkflowDefinitionValidator.Validate(ToDefinition(wf));
        foreach (var err in report.Errors.Take(5)) r.Problems.Add(new(p + "." + err.Path, err.Message));
        return wf;
    }

    /// <summary>Pack workflow → the normal workflow definition (the same validator and storage the editor uses).</summary>
    public static WorkflowDefinition ToDefinition(PackWorkflow w) => new(
        [new TriggerDefinition(w.TriggerEvent, w.TriggerCondition, w.InactiveDays)],
        w.Steps.Select(s => new StepDefinition(s.Key, Enum.Parse<StepType>(s.Type), s.Name, s.Config, s.Condition, Enum.Parse<FailurePolicy>(s.OnFailure), s.MaxRetries)).ToList(),
        Enum.Parse<ReEntryPolicy>(w.ReEntry), w.CooldownHours, w.MaxStartsPer30Days, null);

    private static PackDiscovery? ReadDiscovery(Reader r, JsonElement e)
    {
        if (!r.Obj(e, "discovery", "categories", "locations")) return null;
        var cats = r.List(e, "categories", 100, (c, p) =>
        {
            if (!r.Obj(c, p, "key", "category", "searchPhrases", "placeType")) return null;
            var key = r.Key(c, p); var cat = r.Str(c, p, "category", true, 60); var phrases = r.StrList(c, p, "searchPhrases", 5, 100); var place = r.Str(c, p, "placeType", false, 60);
            if (phrases.Count == 0) r.Problems.Add(new(p + ".searchPhrases", "Give at least one search phrase."));
            return key is null || cat is null ? null : new PackDiscoveryCategory(key, cat, phrases, place);
        }, "discovery");
        var locs = r.List(e, "locations", 200, (l, p) =>
        {
            if (!r.Obj(l, p, "key", "country", "province", "city")) return null;
            var key = r.Key(l, p); var country = r.Str(l, p, "country", true, 100); var prov = r.Str(l, p, "province", false, 100); var city = r.Str(l, p, "city", false, 100);
            return key is null || country is null ? null : new PackDiscoveryLocation(key, country, prov, city);
        }, "discovery");
        return new(cats, locs);
    }

    // ------------------------------------------------------------------------------------------------ references

    private static void CrossCheck(List<PackProblem> problems, IReadOnlyList<PackCategory> cats, IReadOnlyList<PackCustomField> fields, IReadOnlyList<PackScoringRule> rules, PackDiscovery? discovery)
    {
        void Unique(string section, IEnumerable<string> keys)
        {
            var dup = keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dup is not null) problems.Add(new(section, $"The key '{Cut(dup.Key, 40)}' is used twice."));
        }
        Unique("categories", cats.Select(c => c.Key));
        Unique("categories", cats.Select(c => c.Name));
        Unique("customFields", fields.Select(f => f.Entity + "." + f.Key));
        Unique("scoringRules", rules.Select(r => r.Dimension + "|" + r.Target.ToLowerInvariant()));
        foreach (var f in Enum.GetValues<CustomEntityType>())
            if (fields.Count(x => x.Entity == f.ToString()) > CustomFieldDefinition.MaxFieldsPerEntityType) problems.Add(new("customFields", $"At most {CustomFieldDefinition.MaxFieldsPerEntityType} fields per record type."));

        var byKey = cats.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var c in cats)
        {
            if (c.Parent is null) continue;
            if (!byKey.ContainsKey(c.Parent)) { problems.Add(new("categories." + c.Key, $"The parent '{Cut(c.Parent, 40)}' is not in the pack.")); continue; }
            var depth = 1; var cur = c; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { c.Key };
            while (cur.Parent is { } pk && byKey.TryGetValue(pk, out var next))
            {
                if (!seen.Add(next.Key)) { problems.Add(new("categories." + c.Key, "The category tree contains a loop.")); break; }
                depth++; cur = next;
            }
            if (depth > LeadCategory.MaxDepth) problems.Add(new("categories." + c.Key, $"Categories can be nested at most {LeadCategory.MaxDepth} levels deep."));
        }
        if (discovery is not null)
        {
            Unique("discovery.categories", discovery.Categories.Select(c => c.Key));
            Unique("discovery.locations", discovery.Locations.Select(c => c.Key));
            foreach (var d in discovery.Categories)
                if (!byKey.ContainsKey(d.Category)) problems.Add(new("discovery.categories." + d.Key, $"The category '{Cut(d.Category, 40)}' is not in the pack."));
        }
        // a rule or workflow condition may use a field the workspace already has: that is checked against the workspace when the pack is planned (LeadFieldReferences)
    }

    /// <summary>The custom-field keys the pack's rules and workflow conditions refer to.</summary>
    public static IReadOnlySet<string> LeadFieldReferences(PackDocument p)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in p.ScoringRules.Where(r => r.Dimension == nameof(ScoreDimension.CustomField)))
            if (CustomFieldCompare.ParseTarget(r.Target) is { } t) set.Add(t.Key);
        foreach (var w in p.Workflows)
        {
            var raw = (w.TriggerCondition?.GetRawText() ?? "") + string.Concat(w.Steps.Select(s => (s.Condition?.GetRawText() ?? "") + (s.Config?.GetRawText() ?? "")));
            foreach (Match m in Regex.Matches(raw, @"lead\.cf\.([a-z][a-z0-9_]{0,39})")) set.Add(m.Groups[1].Value);
        }
        return set;
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n];

    // ------------------------------------------------------------------------------------------------ low-level reader

    private sealed class Reader(List<PackProblem> problems)
    {
        public List<PackProblem> Problems { get; } = problems;
        private int _nodes;

        public bool Obj(JsonElement e, string path, params string[] allowed)
        {
            if (e.ValueKind != JsonValueKind.Object) { Problems.Add(new(path, "An object is expected here.")); return false; }
            Known(e, path, allowed);
            return true;
        }

        public void Known(JsonElement e, string path, params string[] allowed)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject())
            {
                if (!seen.Add(p.Name)) Problems.Add(new(Join(path, p.Name), "The key appears twice."));
                else if (!allowed.Contains(p.Name)) Problems.Add(new(Join(path, p.Name), $"Unknown key '{Cut(p.Name, 40)}'."));
            }
        }

        public string? Key(JsonElement e, string path)
        {
            var k = Str(e, path, "key", true, 60);
            if (k is not null && !ItemKey.IsMatch(k)) { Problems.Add(new(Join(path, "key"), "A key uses lower-case letters, digits, '-', '_' and '.' (at most 60 characters).")); return null; }
            return k;
        }

        public string? Str(JsonElement e, string path, string name, bool required, int max, bool multiline = false, bool allowMarkup = false, bool allowPlaceholders = false)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) { if (required) Problems.Add(new(Join(path, name), "This is required.")); return null; }
            if (v.ValueKind != JsonValueKind.String) { Problems.Add(new(Join(path, name), "Text is expected.")); return null; }
            var s = v.GetString()!.Trim();
            if (s.Length == 0) { if (required) Problems.Add(new(Join(path, name), "This is required.")); return null; }
            if (s.Length > max) { Problems.Add(new(Join(path, name), $"At most {max} characters.")); return null; }
            foreach (var ch in s)
            {
                if (char.IsControl(ch) && !(multiline && ch is '\n' or '\r' or '\t')) { Problems.Add(new(Join(path, name), "Control characters are not allowed.")); return null; }
                if (ch is >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '​' or '﻿') { Problems.Add(new(Join(path, name), "Invisible or direction-changing characters are not allowed.")); return null; }
            }
            if (!allowMarkup && Markup.IsMatch(s)) { Problems.Add(new(Join(path, name), "Markup (HTML tags or entities) is not allowed: packs hold plain text.")); return null; }
            if (!allowPlaceholders && (s.Contains("{{") || s.Contains("}}"))) { Problems.Add(new(Join(path, name), "Placeholders are only allowed in message template bodies.")); return null; }
            if (s[0] is '=' or '@' && name != "pattern") { Problems.Add(new(Join(path, name), "Text must not start with '=' or '@' (spreadsheet formula characters).")); return null; }
            return s;
        }

        public int? Int(JsonElement e, string path, string name, bool required, int min, int max)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) { if (required) Problems.Add(new(Join(path, name), "This is required.")); return null; }
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var i) || i < min || i > max) { Problems.Add(new(Join(path, name), $"A whole number from {min} to {max} is expected.")); return null; }
            return i;
        }

        public decimal? Dec(JsonElement e, string path, string name)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var d)) { Problems.Add(new(Join(path, name), "A number is expected.")); return null; }
            return d;
        }

        public bool? Bool(JsonElement e, string path, string name)
        {
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { Problems.Add(new(Join(path, name), "true or false is expected.")); return null; }
            return v.GetBoolean();
        }

        public List<string> StrList(JsonElement e, string path, string name, int maxItems, int maxLen)
        {
            var list = new List<string>();
            if (!e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return list;
            if (v.ValueKind != JsonValueKind.Array) { Problems.Add(new(Join(path, name), "A list is expected.")); return list; }
            if (v.GetArrayLength() > maxItems) { Problems.Add(new(Join(path, name), $"At most {maxItems} entries.")); return list; }
            var i = 0;
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) { Problems.Add(new($"{Join(path, name)}[{i}]", "Text is expected.")); i++; continue; }
                var wrapper = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["v"] = item.GetString()! }));
                var s = Str(wrapper.RootElement, $"{Join(path, name)}[{i}]", "v", true, maxLen);
                wrapper.Dispose();
                if (s is not null) list.Add(s);
                i++;
            }
            return list;
        }

        public List<T> List<T>(JsonElement parent, string name, int max, Func<JsonElement, string, T?> read, string? parentPath = null) where T : class
        {
            var list = new List<T>();
            if (!parent.TryGetProperty(name, out var arr) || arr.ValueKind == JsonValueKind.Null) return list;
            var basePath = parentPath is null ? name : parentPath + "." + name;
            if (arr.ValueKind != JsonValueKind.Array) { Problems.Add(new(basePath, "A list is expected.")); return list; }
            if (arr.GetArrayLength() > max) { Problems.Add(new(basePath, $"At most {max} entries.")); return list; }
            var i = 0;
            foreach (var item in arr.EnumerateArray()) { var x = read(item, $"{basePath}[{i++}]"); if (x is not null) list.Add(x); }
            return list;
        }

        /// <summary>Free-form JSON (workflow config / conditions): bounded, strings checked like every other string. Its shape is validated by the workflow validators.</summary>
        public void CheckJsonValue(JsonElement e, string path)
        {
            if (++_nodes > 4000) return;
            switch (e.ValueKind)
            {
                case JsonValueKind.String:
                    var s = e.GetString()!;
                    if (s.Length > 1000 || s.Any(c => char.IsControl(c) && c is not ('\n' or '\t')) || Markup.IsMatch(s)) Problems.Add(new(path, "A text value is too long or contains markup or control characters."));
                    break;
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) CheckJsonValue(p.Value, path + "." + Cut(p.Name, 30)); break;
                case JsonValueKind.Array: foreach (var i in e.EnumerateArray()) CheckJsonValue(i, path + "[]"); break;
            }
        }

        private static string Join(string path, string name) => path.Length == 0 ? name : path + "." + name;
    }
}
