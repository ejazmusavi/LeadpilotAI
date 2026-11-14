using System.Globalization;
using System.Text.Json;

namespace LeadPilot.Application.Agents;

/// <summary>Names of the controlled tools. Everything the AI can DO is in <see cref="AgentToolCatalog"/>; there is no generic "run query" or "call function" tool.</summary>
public static class AgentTools
{
    public const string GetProductInformation = "GetProductInformation";
    public const string GetPricingPlans = "GetPricingPlans";
    public const string GetLeadProfile = "GetLeadProfile";
    public const string UpdateLeadStatus = "UpdateLeadStatus";
    public const string QualifyLead = "QualifyLead";
    public const string CheckDemoAvailability = "CheckDemoAvailability";
    public const string BookDemo = "BookDemo";
    public const string ScheduleFollowUp = "ScheduleFollowUp";
    public const string CreateTrialAccount = "CreateTrialAccount";
    public const string CreatePaymentRequest = "CreatePaymentRequest";
    public const string TransferToHuman = "TransferToHuman";
    public const string StopMarketingMessages = "StopMarketingMessages";
    /// <summary>Carried over from Phase 7 (named add_lead_note there).</summary>
    public const string AddLeadNote = "AddLeadNote";
}

public enum ParamKind { String, Integer, Number, Boolean, DateTimeUtc, StringMap }

public enum ApprovalPolicy { Never, Always, Conditional }

public sealed record ToolParam(string Name, ParamKind Kind, bool Required, string Description, int MaxLength = 300, IReadOnlyList<string>? Allowed = null, double? Min = null, double? Max = null);

/// <param name="Safety">Always available, whatever the agent's tool list says: they only ever REDUCE what the AI does (hand over, stop messaging).</param>
public sealed record ToolDefinition(string Name, string Description, string Capability, bool ReadOnly, bool Safety, ApprovalPolicy Approval, IReadOnlyList<ToolParam> Params, IReadOnlyList<string> Aliases);

public static class AgentToolCatalog
{
    private static ToolParam S(string n, string d, bool req = false, int max = 300, params string[] allowed) => new(n, ParamKind.String, req, d, max, allowed.Length == 0 ? null : allowed);

    public static readonly IReadOnlyList<ToolDefinition> All =
    [
        new(AgentTools.GetProductInformation, "Facts about the business's confirmed products and services (features, demo process, trial process). Returns known=false for anything the owner has not confirmed.", "catalog.read", true, false, ApprovalPolicy.Never,
            [S("topic", "Optional word(s) to narrow the result, e.g. a feature area.", false, 100)], []),
        new(AgentTools.GetPricingPlans, "The confirmed pricing plans. A plan without a price has price=null: say you do not know the price.", "catalog.read", true, false, ApprovalPolicy.Never,
            [S("product", "Optional product name to narrow the result.", false, 100)], []),
        new(AgentTools.GetLeadProfile, "What the CRM knows about the person you are talking to (name, organisation, city, stage, status, qualification so far). Takes no identifiers: it always refers to this conversation.", "lead.read", true, false, ApprovalPolicy.Never, [], []),
        new(AgentTools.UpdateLeadStatus, "Set the CRM status of this lead to one of the workspace's OPEN statuses (never a closed one).", "lead.write", false, false, ApprovalPolicy.Never,
            [S("status", "Name of an open lead status.", true, 100), S("reason", "Why, in one sentence.", true, 200)], []),
        new(AgentTools.QualifyLead, "Record the customer's answers to the qualification questions (only keys listed in the agent's questions) and your assessment of fit.", "lead.write", false, false, ApprovalPolicy.Never,
            [new("answers", ParamKind.StringMap, true, "Object: question key -> the customer's answer, exactly as stated.", 300), S("fit", "Your assessment from what was stated.", false, 20, "good", "possible", "poor", "unknown"), S("summary", "One sentence summary.", false, 300)], []),
        new(AgentTools.CheckDemoAvailability, "List the demo slots that are really free. Never propose a time that is not in this list.", "demo.read", true, false, ApprovalPolicy.Never,
            [new("days", ParamKind.Integer, false, "How many days ahead to look (1-14).", 3, null, 1, 14)], []),
        new(AgentTools.BookDemo, "Book one of the slots returned by CheckDemoAvailability. A time that is not an available slot is refused.", "demo.write", false, false, ApprovalPolicy.Never,
            [S("slotId", "The slotId exactly as returned by CheckDemoAvailability.", true, 100), S("notes", "Optional note for the host.", false, 300)], []),
        new(AgentTools.ScheduleFollowUp, "Create an internal follow-up task for the team to contact this lead later. Nothing is sent to the customer.", "followup.write", false, false, ApprovalPolicy.Never,
            [new("dueInHours", ParamKind.Integer, true, "Hours from now (1-720).", 4, null, 1, 720), S("note", "What to follow up on.", true, 500)], []),
        new(AgentTools.CreateTrialAccount, "Request a trial account for this lead. Limited per lead; beyond the limit a person must approve.", "trial.write", false, false, ApprovalPolicy.Conditional,
            [S("plan", "Optional plan name.", false, 100), S("notes", "Optional note.", false, 300)], []),
        new(AgentTools.CreatePaymentRequest, "Ask for a payment request for a confirmed plan. ALWAYS needs a person's approval first; nothing is charged and no subscription is activated.", "payment.write", false, false, ApprovalPolicy.Always,
            [S("plan", "Name of a confirmed plan.", true, 100), new("discountPercent", ParamKind.Number, false, "Only if the customer asked for a discount; needs approval.", 6, null, 0, 100), S("customTerms", "Any unusual commitment the customer asked for; needs approval.", false, 300)], []),
        new(AgentTools.TransferToHuman, "Hand this conversation to a member of staff. The AI stops answering it.", "conversation.escalate", false, true, ApprovalPolicy.Never,
            [S("reason", "Why a person is needed.", true, 200), S("summary", "Short summary for the person.", false, 500)], ["escalate_to_human"]),
        new(AgentTools.StopMarketingMessages, "The customer asked not to be contacted: suppress the number, revoke marketing consent and block further messages.", "compliance.optout", false, true, ApprovalPolicy.Never,
            [S("reason", "What the customer said, briefly.", false, 200)], []),
        new(AgentTools.AddLeadNote, "Add a short internal note to the lead.", "lead.note", false, false, ApprovalPolicy.Never, [S("note", "The note.", true, 600)], ["add_lead_note"]),
    ];

    /// <summary>Tools of the Phase 7 behaviour, used when no agent is configured.</summary>
    public static readonly IReadOnlyList<string> LegacyTools = [AgentTools.TransferToHuman, AgentTools.AddLeadNote];

    public static ToolDefinition? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : All.FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.Ordinal) || t.Aliases.Contains(name.Trim(), StringComparer.Ordinal));

    /// <summary>The tools an agent may use: its enabled list plus the always-available safety tools.</summary>
    public static IReadOnlyList<ToolDefinition> AllowedFor(IEnumerable<string> enabled)
    {
        var set = enabled.ToHashSet(StringComparer.Ordinal);
        return All.Where(t => t.Safety || set.Contains(t.Name)).ToList();
    }

    public static string DescribeForPrompt(IEnumerable<ToolDefinition> tools) =>
        string.Join("\n", tools.Select(t => $"- {t.Name}({string.Join(", ", t.Params.Select(p => p.Name + (p.Required ? "" : "?") + ":" + p.Kind.ToString().ToLowerInvariant()))}): {t.Description}"));
}

public sealed record ToolArgsResult(bool Ok, IReadOnlyDictionary<string, object?> Values, IReadOnlyList<string> Errors, bool TenantArgument)
{
    public string? Str(string name) => Values.TryGetValue(name, out var v) ? v as string : null;
    public int? Int(string name) => Values.TryGetValue(name, out var v) && v is long l ? (int)l : null;
    public decimal? Dec(string name) => Values.TryGetValue(name, out var v) ? v switch { decimal d => d, long l => l, _ => null } : null;
    public IReadOnlyDictionary<string, string>? Map(string name) => Values.TryGetValue(name, out var v) ? v as IReadOnlyDictionary<string, string> : null;
}

/// <summary>
/// Schema validation of what the LLM sent. Strict: unknown arguments, wrong types, over-long strings, values outside the allowed list or range are rejected. Arguments that look like an attempt to choose the tenant,
/// a user, a lead or a conversation are rejected outright: those always come from the server-side context, never from the model.
/// </summary>
public static class ToolArguments
{
    public const int MaxJsonChars = 4000;
    private static readonly string[] Forbidden = ["tenant", "workspace", "userid", "user_id", "leadid", "lead_id", "conversationid", "conversation_id", "agentid", "agent_id", "contactid", "contact_id", "sql", "query", "table"];

    public static ToolArgsResult Validate(ToolDefinition tool, string? json)
    {
        var errors = new List<string>(); var values = new Dictionary<string, object?>(StringComparer.Ordinal); var tenantArg = false;
        JsonElement root;
        if (string.IsNullOrWhiteSpace(json)) { using var d = JsonDocument.Parse("{}"); root = d.RootElement.Clone(); }
        else
        {
            if (json.Length > MaxJsonChars) return new(false, values, ["arguments_too_large"], false);
            try { root = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 }).RootElement.Clone(); }
            catch (JsonException) { return new(false, values, ["arguments_not_json"], false); }
        }
        if (root.ValueKind != JsonValueKind.Object) return new(false, values, ["arguments_not_an_object"], false);

        foreach (var prop in root.EnumerateObject())
        {
            var lower = prop.Name.ToLowerInvariant().Replace("-", "_");
            if (Forbidden.Any(f => lower.Contains(f)) && tool.Params.All(p => p.Name != prop.Name)) { tenantArg = true; errors.Add($"forbidden_argument:{Clip(prop.Name)}"); continue; }
            if (tool.Params.All(p => p.Name != prop.Name)) errors.Add($"unknown_argument:{Clip(prop.Name)}");
        }
        foreach (var p in tool.Params)
        {
            if (!root.TryGetProperty(p.Name, out var v) || v.ValueKind == JsonValueKind.Null) { if (p.Required) errors.Add($"missing_argument:{p.Name}"); continue; }
            switch (p.Kind)
            {
                case ParamKind.String:
                    if (v.ValueKind != JsonValueKind.String) { errors.Add($"wrong_type:{p.Name}"); break; }
                    var s = Neutral(v.GetString()); if (s.Length == 0) { if (p.Required) errors.Add($"missing_argument:{p.Name}"); break; }
                    if (s.Length > p.MaxLength) { errors.Add($"too_long:{p.Name}"); break; }
                    if (p.Allowed is not null && !p.Allowed.Contains(s, StringComparer.OrdinalIgnoreCase)) { errors.Add($"not_allowed:{p.Name}"); break; }
                    values[p.Name] = p.Allowed is null ? s : p.Allowed.First(a => string.Equals(a, s, StringComparison.OrdinalIgnoreCase)); break;
                case ParamKind.Integer:
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) || v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
                    { if (p.Min is { } mn && l < mn || p.Max is { } mx && l > mx) errors.Add($"out_of_range:{p.Name}"); else values[p.Name] = l; }
                    else errors.Add($"wrong_type:{p.Name}"); break;
                case ParamKind.Number:
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) || v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    { if (p.Min is { } mn2 && d < (decimal)mn2 || p.Max is { } mx2 && d > (decimal)mx2) errors.Add($"out_of_range:{p.Name}"); else values[p.Name] = d; }
                    else errors.Add($"wrong_type:{p.Name}"); break;
                case ParamKind.Boolean:
                    if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) values[p.Name] = v.GetBoolean(); else errors.Add($"wrong_type:{p.Name}"); break;
                case ParamKind.DateTimeUtc:
                    if (v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)) values[p.Name] = dt; else errors.Add($"wrong_type:{p.Name}"); break;
                case ParamKind.StringMap:
                    if (v.ValueKind != JsonValueKind.Object) { errors.Add($"wrong_type:{p.Name}"); break; }
                    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var bad = false;
                    foreach (var e in v.EnumerateObject())
                    {
                        if (map.Count >= 20 || e.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) { bad = true; break; }
                        var text = e.Value.ValueKind switch { JsonValueKind.String => e.Value.GetString(), JsonValueKind.Number => e.Value.GetRawText(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => null };
                        text = Neutral(text); if (text.Length == 0) continue;
                        if (text.Length > p.MaxLength || e.Name.Length > 40) { bad = true; break; }
                        map[e.Name] = text;
                    }
                    if (bad) errors.Add($"invalid_map:{p.Name}"); else if (map.Count == 0 && p.Required) errors.Add($"missing_argument:{p.Name}"); else values[p.Name] = map; break;
            }
        }
        return new(errors.Count == 0, values, errors, tenantArg);
    }

    private static string Clip(string s) => s.Length > 30 ? s[..30] : s;
    private static string Neutral(string? s) => LeadPilot.Application.Ai.PromptGuard.Neutralize(s, 2000, escapeAngles: false);
}
