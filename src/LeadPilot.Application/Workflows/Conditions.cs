using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeadPilot.Application.Workflows;

public enum FieldKind { Text = 1, Number, Bool, List }

public sealed record ConditionField(string Name, string Label, string Group, FieldKind Kind, string? Hint = null);

/// <summary>The ONLY things a condition may look at. A condition naming anything else is rejected when the workflow is saved.</summary>
public static class ConditionFields
{
    public static readonly IReadOnlyList<ConditionField> All =
    [
        new("lead.name", "Lead name", "Lead", FieldKind.Text), new("lead.city", "City", "Lead", FieldKind.Text), new("lead.province", "Province", "Lead", FieldKind.Text),
        new("lead.country", "Country", "Lead", FieldKind.Text), new("lead.score", "Score (0-100)", "Lead", FieldKind.Number), new("lead.confidence", "Data confidence (0-100)", "Lead", FieldKind.Number),
        new("lead.stage", "Pipeline stage", "Lead", FieldKind.Text), new("lead.status", "Status", "Lead", FieldKind.Text), new("lead.source", "Source (Manual, Import, Discovery ...)", "Lead", FieldKind.Text),
        new("lead.has_owner", "Has an owner", "Lead", FieldKind.Bool), new("lead.tags", "Tags", "Lead", FieldKind.List),
        new("lead.inactive_days", "Days without activity", "Lead", FieldKind.Number), new("lead.age_days", "Days since created", "Lead", FieldKind.Number),
        new("contact.has_phone", "Has a phone number", "Contact", FieldKind.Bool), new("contact.has_email", "Has an e-mail address", "Contact", FieldKind.Bool),
        new("contact.whatsapp_marketing_eligible", "May be sent WhatsApp marketing (consent, no suppression)", "Contact", FieldKind.Bool),
        new("conversation.exists", "Has a conversation", "Conversation", FieldKind.Bool), new("conversation.ai_mode", "AI mode (Active, Paused, HumanControl)", "Conversation", FieldKind.Text),
        new("conversation.opted_out", "Opted out", "Conversation", FieldKind.Bool), new("message.text", "Text of the incoming message", "Conversation", FieldKind.Text, "Only filled for message events."),
        new("campaign.recipient_of_any", "Was ever a campaign recipient", "Campaign", FieldKind.Bool), new("campaign.last_name", "Name of the latest campaign", "Campaign", FieldKind.Text),
        new("event.type", "Event that started the workflow", "Event", FieldKind.Text),
    ];

    private static readonly Dictionary<string, ConditionField> ByName = All.ToDictionary(f => f.Name, StringComparer.Ordinal);
    public static ConditionField? Find(string? name) => name is not null && ByName.TryGetValue(name, out var f) ? f : null;

    public static IReadOnlyList<string> OperatorsFor(FieldKind kind) => kind switch
    {
        FieldKind.Text => ["eq", "neq", "contains", "not_contains", "starts_with", "in", "is_empty", "is_not_empty"],
        FieldKind.Number => ["eq", "neq", "gt", "gte", "lt", "lte"],
        FieldKind.Bool => ["eq"],
        _ => ["contains", "not_contains", "is_empty", "is_not_empty"],
    };
}

public abstract record Condition;
public sealed record AllCondition(IReadOnlyList<Condition> Items) : Condition;
public sealed record AnyCondition(IReadOnlyList<Condition> Items) : Condition;
public sealed record NotCondition(Condition Inner) : Condition;
public sealed record CompareCondition(string Field, string Op, object? Value) : Condition;
public sealed record WindowCondition(IReadOnlyList<DayOfWeek> Days, TimeOnly From, TimeOnly To) : Condition;

public sealed record ConditionParseResult(Condition? Condition, IReadOnlyList<string> Errors) { public bool IsValid => Errors.Count == 0 && Condition is not null; }

/// <summary>
/// The declarative condition language: <c>{"all":[...]}</c>, <c>{"any":[...]}</c>, <c>{"not":{...}}</c>, <c>{"field":"lead.city","op":"eq","value":"Lahore"}</c> and
/// <c>{"window":{"days":["mon","tue"],"from":"09:00","to":"17:00"}}</c> (tenant time zone). It is data, never code: parsing only accepts these shapes, the field and operator lists are closed,
/// nothing is evaluated by a general interpreter, regular expressions and templates do not exist, and size/depth/length are bounded.
/// </summary>
public static class ConditionParser
{
    public const int MaxDepth = 5, MaxNodes = 40, MaxTextLength = 200, MaxListItems = 20, MaxJsonLength = 16_000;

    public static ConditionParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(null, ["The condition is empty."]);
        if (json.Length > MaxJsonLength) return new(null, ["The condition is too large."]);
        JsonNode? node;
        try { node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 24, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false }); }
        catch (JsonException) { return new(null, ["The condition is not valid JSON."]); }
        var errors = new List<string>(); var nodes = 0;
        var c = Build(node, 1, errors, ref nodes);
        return new(errors.Count == 0 ? c : null, errors);
    }

    private static Condition? Build(JsonNode? node, int depth, List<string> errors, ref int nodes)
    {
        if (++nodes > MaxNodes) { if (errors.Count == 0 || errors[^1] != "The condition has too many parts.") errors.Add("The condition has too many parts."); return null; }
        if (depth > MaxDepth) { errors.Add($"Conditions can be nested at most {MaxDepth} levels deep."); return null; }
        if (node is not JsonObject obj) { errors.Add("A condition must be an object."); return null; }
        var keys = obj.Select(p => p.Key).ToList();

        if (keys.Count == 1 && (keys[0] is "all" or "any"))
        {
            if (obj[keys[0]] is not JsonArray arr || arr.Count == 0) { errors.Add($"'{keys[0]}' needs at least one condition."); return null; }
            if (arr.Count > 20) { errors.Add($"'{keys[0]}' can hold at most 20 conditions."); return null; }
            var items = new List<Condition>();
            foreach (var child in arr) { var c = Build(child, depth + 1, errors, ref nodes); if (c is null) return null; items.Add(c); }
            return keys[0] == "all" ? new AllCondition(items) : new AnyCondition(items);
        }
        if (keys.Count == 1 && keys[0] == "not") { var inner = Build(obj["not"], depth + 1, errors, ref nodes); return inner is null ? null : new NotCondition(inner); }
        if (keys.Count == 1 && keys[0] == "window") return BuildWindow(obj["window"], errors);
        if (keys.All(k => k is "field" or "op" or "value") && keys.Contains("field") && keys.Contains("op")) return BuildCompare(obj, errors);
        errors.Add("Unknown condition shape. Use all / any / not / window or {field, op, value}.");
        return null;
    }

    private static Condition? BuildCompare(JsonObject obj, List<string> errors)
    {
        var fieldName = Str(obj["field"]); var op = Str(obj["op"]);
        var field = ConditionFields.Find(fieldName);
        if (field is null) { errors.Add($"Unknown field '{Trim(fieldName)}'."); return null; }
        if (op is null || !ConditionFields.OperatorsFor(field.Kind).Contains(op)) { errors.Add($"'{Trim(op)}' cannot be used with '{field.Label}'."); return null; }
        var needsValue = op is not ("is_empty" or "is_not_empty");
        if (!needsValue) return new CompareCondition(field.Name, op, null);
        var raw = obj["value"];
        if (raw is null) { errors.Add($"'{field.Label}' needs a value."); return null; }
        try
        {
            object? value = field.Kind switch
            {
                FieldKind.Number => Number(raw),
                FieldKind.Bool => raw is JsonValue bv && bv.TryGetValue<bool>(out var b) ? b : throw new FormatException("true or false"),
                _ when op == "in" => TextList(raw),
                _ => Text(raw),
            };
            return new CompareCondition(field.Name, op, value);
        }
        catch (FormatException ex) { errors.Add($"The value for '{field.Label}' must be {ex.Message}."); return null; }
    }

    private static Condition? BuildWindow(JsonNode? node, List<string> errors)
    {
        if (node is not JsonObject w || w.Any(p => p.Key is not ("days" or "from" or "to"))) { errors.Add("A time window has 'days', 'from' and 'to'."); return null; }
        var days = new List<DayOfWeek>();
        if (w["days"] is not JsonArray arr || arr.Count == 0) { errors.Add("A time window needs at least one day."); return null; }
        foreach (var d in arr)
        {
            var parsed = Str(d)?.ToLowerInvariant() switch
            {
                "mon" => (DayOfWeek?)DayOfWeek.Monday, "tue" => DayOfWeek.Tuesday, "wed" => DayOfWeek.Wednesday, "thu" => DayOfWeek.Thursday, "fri" => DayOfWeek.Friday, "sat" => DayOfWeek.Saturday, "sun" => DayOfWeek.Sunday, _ => null
            };
            if (parsed is null) { errors.Add("Days are mon, tue, wed, thu, fri, sat, sun."); return null; }
            if (!days.Contains(parsed.Value)) days.Add(parsed.Value);
        }
        if (!TimeOnly.TryParseExact(Str(w["from"]), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) || !TimeOnly.TryParseExact(Str(w["to"]), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
        { errors.Add("Window times are written HH:mm, e.g. 09:00."); return null; }
        if (from == to) { errors.Add("A time window cannot start and end at the same time."); return null; }
        return new WindowCondition(days, from, to);
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static string Trim(string? s) => s is null ? "" : s.Length > 40 ? s[..40] : s;

    private static decimal Number(JsonNode raw)
    {
        if (raw is JsonValue v)
        {
            if (v.TryGetValue<decimal>(out var d) && Math.Abs(d) <= 1_000_000_000m) return d;
            if (v.TryGetValue<double>(out var dd) && Math.Abs(dd) <= 1e9) return (decimal)dd;
            if (v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && Math.Abs(p) <= 1_000_000_000m) return p;
        }
        throw new FormatException("a number");
    }

    private static string Text(JsonNode raw)
    {
        var s = Str(raw) ?? throw new FormatException("text");
        if (s.Length > MaxTextLength) throw new FormatException($"at most {MaxTextLength} characters");
        return s;
    }

    private static List<string> TextList(JsonNode raw)
    {
        if (raw is JsonArray arr)
        {
            if (arr.Count == 0 || arr.Count > MaxListItems) throw new FormatException($"a list of 1 to {MaxListItems} texts");
            return arr.Select(n => Text(n ?? throw new FormatException("text"))).ToList();
        }
        // "a, b, c" typed into one box
        return Text(raw).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxListItems).ToList();
    }
}

/// <summary>Evaluates a parsed condition against facts. Pure: no I/O, no clock of its own, no reflection.</summary>
public static class ConditionEvaluator
{
    public static bool Evaluate(Condition c, IReadOnlyDictionary<string, object?> facts, DateTime nowUtc, TimeZoneInfo tz) => c switch
    {
        AllCondition a => a.Items.All(i => Evaluate(i, facts, nowUtc, tz)),
        AnyCondition a => a.Items.Any(i => Evaluate(i, facts, nowUtc, tz)),
        NotCondition n => !Evaluate(n.Inner, facts, nowUtc, tz),
        WindowCondition w => InWindow(w, nowUtc, tz),
        CompareCondition cmp => Compare(cmp, facts),
        _ => false,
    };

    /// <summary>Overnight windows (from &gt; to) belong to the day they start on.</summary>
    public static bool InWindow(WindowCondition w, DateTime nowUtc, TimeZoneInfo tz)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);
        var t = TimeOnly.FromDateTime(local);
        if (w.From < w.To) return w.Days.Contains(local.DayOfWeek) && t >= w.From && t < w.To;
        if (t >= w.From) return w.Days.Contains(local.DayOfWeek);
        return t < w.To && w.Days.Contains(local.AddDays(-1).DayOfWeek);
    }

    private static bool Compare(CompareCondition c, IReadOnlyDictionary<string, object?> facts)
    {
        facts.TryGetValue(c.Field, out var actual);
        var kind = ConditionFields.Find(c.Field)?.Kind;
        switch (kind)
        {
            case FieldKind.Number:
                if (actual is null) return c.Op == "neq";
                var a = Convert.ToDecimal(actual, CultureInfo.InvariantCulture); var b = Convert.ToDecimal(c.Value, CultureInfo.InvariantCulture);
                return c.Op switch { "eq" => a == b, "neq" => a != b, "gt" => a > b, "gte" => a >= b, "lt" => a < b, "lte" => a <= b, _ => false };
            case FieldKind.Bool:
                var ab = actual is bool x && x; return ab == (c.Value is true);
            case FieldKind.List:
                var list = actual as IReadOnlyCollection<string> ?? [];
                return c.Op switch
                {
                    "is_empty" => list.Count == 0, "is_not_empty" => list.Count > 0,
                    "contains" => list.Any(i => string.Equals(i, c.Value as string, StringComparison.OrdinalIgnoreCase)),
                    "not_contains" => !list.Any(i => string.Equals(i, c.Value as string, StringComparison.OrdinalIgnoreCase)),
                    _ => false,
                };
            default:
                var s = actual as string;
                return c.Op switch
                {
                    "is_empty" => string.IsNullOrWhiteSpace(s), "is_not_empty" => !string.IsNullOrWhiteSpace(s),
                    "eq" => s is not null && string.Equals(s, (string?)c.Value, StringComparison.OrdinalIgnoreCase),
                    "neq" => s is null || !string.Equals(s, (string?)c.Value, StringComparison.OrdinalIgnoreCase),
                    "contains" => s is not null && s.Contains((string)c.Value!, StringComparison.OrdinalIgnoreCase),
                    "not_contains" => s is null || !s.Contains((string)c.Value!, StringComparison.OrdinalIgnoreCase),
                    "starts_with" => s is not null && s.StartsWith((string)c.Value!, StringComparison.OrdinalIgnoreCase),
                    "in" => s is not null && ((IEnumerable<string>)c.Value!).Any(v => string.Equals(v, s, StringComparison.OrdinalIgnoreCase)),
                    _ => false,
                };
        }
    }

    /// <summary>Parse + evaluate a stored condition. Anything unparseable evaluates to FALSE (a damaged condition never lets a step run).</summary>
    public static bool EvaluateJson(string? json, IReadOnlyDictionary<string, object?> facts, DateTime nowUtc, TimeZoneInfo tz)
    {
        if (string.IsNullOrWhiteSpace(json)) return true;
        var parsed = ConditionParser.Parse(json);
        return parsed.IsValid && Evaluate(parsed.Condition!, facts, nowUtc, tz);
    }
}
