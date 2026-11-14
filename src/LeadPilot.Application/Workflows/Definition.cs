using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Workflows;

namespace LeadPilot.Application.Workflows;

// ===================================================================================================================================================================
// Event contract (routing keys on the broker) and the catalog of triggers. Phase 10 events are declared and wired but have NO producer yet: they are listed as unavailable, never faked.
// ===================================================================================================================================================================

public static class WorkflowEventTypes
{
    public const string LeadCreated = "lead.created";
    public const string LeadEnriched = "lead.enriched";
    public const string LeadContactEligible = "lead.contact_eligible";
    public const string MessageReceived = MessagingEventTypes.MessageReceived;
    public const string CustomerReplied = "customer.replied";
    public const string LeadInterested = "lead.interested";
    public const string DemoBooked = "demo.booked";
    public const string DemoCompleted = "demo.completed";
    public const string TrialCreated = "trial.created";
    public const string TrialExpires = "trial.expires";
    public const string PaymentConfirmed = "payment.confirmed";
    public const string LeadInactive = "lead.inactive";
    /// <summary>Not a broker event: used as the trigger key of a run started by a person.</summary>
    public const string Manual = "manual";
}

/// <summary>Payload of the workflow domain events published through the outbox. Ids only; the consumer re-reads everything inside the tenant scope.</summary>
public sealed record WorkflowEventPayload(Guid LeadId, Guid? ContactId = null, Guid? ConversationId = null, Guid? MessageId = null, string? Detail = null);

public sealed record TriggerInfo(string Key, string Title, string Description, bool Available, string? UnavailableReason, bool HasProducer);

public static class TriggerCatalog
{
    private const string Phase10 = "Available when Phase 10 (demos, trials and payments) ships. Nothing produces this event yet, so a workflow with this trigger will not start until then.";

    public static readonly IReadOnlyList<TriggerInfo> All =
    [
        new(WorkflowEventTypes.LeadCreated, "New lead created", "A lead was added manually, imported or approved from discovery.", true, null, true),
        new(WorkflowEventTypes.LeadEnriched, "Lead enriched", "Discovery or website enrichment added new data to an existing lead.", true, null, true),
        new(WorkflowEventTypes.LeadContactEligible, "Lead becomes contact-eligible", "A contact received WhatsApp marketing consent and is not suppressed.", true, null, true),
        new(WorkflowEventTypes.MessageReceived, "Incoming WhatsApp message", "Any inbound message from a contact that belongs to a lead.", true, null, true),
        new(WorkflowEventTypes.CustomerReplied, "Customer replies", "A contact answered after we had messaged them.", true, null, true),
        new(WorkflowEventTypes.LeadInterested, "Lead becomes interested", "The lead moved to the pipeline stage named \"Interested\".", true, null, true),
        new(WorkflowEventTypes.LeadInactive, "Lead becomes inactive", "No activity on the lead for the configured number of days (checked by an hourly background job).", true, null, true),
        new(WorkflowEventTypes.DemoBooked, "Demo booked", "A demo was booked.", false, Phase10, false),
        new(WorkflowEventTypes.DemoCompleted, "Demo completed", "A demo took place.", false, Phase10, false),
        new(WorkflowEventTypes.TrialCreated, "Trial created", "A trial account was created.", false, Phase10, false),
        new(WorkflowEventTypes.TrialExpires, "Trial expires", "A trial is about to expire.", false, Phase10, false),
        new(WorkflowEventTypes.PaymentConfirmed, "Payment confirmed", "A verified payment confirmation arrived.", false, Phase10, false),
    ];

    private static readonly Dictionary<string, TriggerInfo> ByKey = All.ToDictionary(t => t.Key, StringComparer.Ordinal);
    public static TriggerInfo? Find(string? key) => key is not null && ByKey.TryGetValue(key, out var t) ? t : null;

    /// <summary>Every event type the workflow consumer listens to (the Phase 10 ones included, so the contract is in place when their producer appears).</summary>
    public static IReadOnlyList<string> ConsumedEventTypes => All.Where(t => t.Key != WorkflowEventTypes.LeadInactive).Select(t => t.Key).ToList();
}

public sealed record StepTypeInfo(StepType Type, string Title, string Description, bool Available, string? UnavailableReason);

public static class StepCatalog
{
    public static readonly IReadOnlyList<StepTypeInfo> All =
    [
        new(StepType.SendMessage, "Send approved message", "Queues an approved message template through the normal sending pipeline. Consent, suppression and opt-out are checked again right before it is queued and again before it leaves.", true, null),
        new(StepType.Wait, "Wait", "Pauses the run for a period (minutes to 30 days).", true, null),
        new(StepType.Branch, "If / else", "Continues at one step or another depending on a condition.", true, null),
        new(StepType.AssignLead, "Assign lead", "Sets (or clears) the owner of the lead.", true, null),
        new(StepType.ChangeLeadStatus, "Change lead status", "Moves the lead to another status (closing statuses are not allowed here).", true, null),
        new(StepType.ChangeLeadStage, "Change pipeline stage", "Moves the lead to another stage of its pipeline.", true, null),
        new(StepType.CreateFollowUp, "Create follow-up task", "Creates an internal task for a person. Nothing is sent to the customer.", true, null),
        new(StepType.NotifyUser, "Notify sales representative", "Shows an in-app notice to the lead's owner or to a chosen team member.", true, null),
        new(StepType.EscalateToHuman, "Escalate to human", "Hands the lead's conversation to a person (same handover as the AI agent's).", true, null),
        new(StepType.UpdateLeadFields, "Update CRM field", "Sets one text field of the lead (city, province, country, district, area).", true, null),
        new(StepType.AddTag, "Add tag", "Adds a tag to the lead.", true, null),
        new(StepType.ScheduleDemo, "Schedule demo", "Books the next free demo slot. Demo scheduling is delivered in Phase 10; until then this step reports that it is not available and a workflow containing it cannot be activated.", false, "Available when Phase 10 ships."),
        new(StepType.Stop, "Stop workflow", "Ends the run here.", true, null),
    ];

    public static StepTypeInfo? Find(StepType t) => All.FirstOrDefault(s => s.Type == t);
}

// ===================================================================================================================================================================
// Definition (what the builder and the API send) and validation
// ===================================================================================================================================================================

public sealed record TriggerDefinition(string EventType, JsonElement? Condition = null, int? InactiveDays = null);

public sealed record StepDefinition(string Key, StepType Type, string Name, JsonElement? Config = null, JsonElement? Condition = null, FailurePolicy OnFailure = FailurePolicy.Retry, int MaxRetries = 2);

public sealed record WorkflowDefinition(
    IReadOnlyList<TriggerDefinition> Triggers, IReadOnlyList<StepDefinition> Steps,
    ReEntryPolicy ReEntry = ReEntryPolicy.AfterCompletion, int CooldownHours = 0, int MaxStartsPerLeadPer30Days = 3, string? Note = null);

public sealed record ValidationProblem(string Path, string Message);

public sealed record ValidationReport(IReadOnlyList<ValidationProblem> Errors, IReadOnlyList<ValidationProblem> Warnings)
{
    public bool IsValid => Errors.Count == 0;
    public string Summary => string.Join(" ", Errors.Take(5).Select(e => $"{e.Path}: {e.Message}"));
}

public static class WorkflowLimits
{
    public const int MaxSteps = 25, MaxTriggers = 5, MaxBranchesOnPath = 5, MaxWaitMinutes = 30 * 24 * 60, MaxFollowUpHours = 720;
}

/// <summary>
/// Validates a workflow definition when it is saved or activated. Pure rules (shape, closed lists, limits, loops); references to templates, accounts, people, statuses and stages are checked against
/// the database by the service. Unknown actions, fields, operators or properties are rejected, never ignored.
/// </summary>
public static partial class WorkflowDefinitionValidator
{
    [GeneratedRegex("^[a-z][a-z0-9_]{0,19}$")] private static partial Regex KeyPattern();
    public static readonly IReadOnlyList<string> LeadTextFields = ["city", "province", "country", "district", "area"];

    private static readonly Dictionary<StepType, string[]> AllowedConfig = new()
    {
        [StepType.AssignLead] = ["userId", "unassign"],
        [StepType.ChangeLeadStatus] = ["status"],
        [StepType.ChangeLeadStage] = ["stage"],
        [StepType.SendMessage] = ["templateId", "channelAccountId"],
        [StepType.Wait] = ["minutes"],
        [StepType.CreateFollowUp] = ["dueInHours", "note", "assignToOwner"],
        [StepType.NotifyUser] = ["target", "title", "message"],
        [StepType.ScheduleDemo] = ["withinDays", "notes"],
        [StepType.EscalateToHuman] = ["reason"],
        [StepType.UpdateLeadFields] = ["field", "value"],
        [StepType.AddTag] = ["tag"],
        [StepType.Branch] = ["condition", "then", "else"],
        [StepType.Stop] = ["reason"],
    };

    public static ValidationReport Validate(WorkflowDefinition def)
    {
        var errors = new List<ValidationProblem>(); var warnings = new List<ValidationProblem>();
        void Err(string path, string msg) => errors.Add(new(path, msg));

        // settings
        if (!Enum.IsDefined(def.ReEntry)) Err("reEntry", "Unknown re-entry policy.");
        if (def.CooldownHours is < 0 or > WorkflowVersion.MaxCooldownHours) Err("cooldownHours", $"The cool-down must be between 0 and {WorkflowVersion.MaxCooldownHours} hours.");
        if (def.MaxStartsPerLeadPer30Days is < 0 or > 100) Err("maxStartsPerLeadPer30Days", "The frequency cap must be between 0 and 100.");

        // triggers
        if (def.Triggers is null || def.Triggers.Count == 0) Err("triggers", "Choose at least one trigger.");
        else
        {
            if (def.Triggers.Count > WorkflowLimits.MaxTriggers) Err("triggers", $"At most {WorkflowLimits.MaxTriggers} triggers.");
            var seenTriggers = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < def.Triggers.Count; i++)
            {
                var t = def.Triggers[i]; var path = $"triggers[{i}]";
                var info = TriggerCatalog.Find(t.EventType);
                if (info is null) { Err(path, $"Unknown trigger '{Cut(t.EventType)}'."); continue; }
                if (!info.Available) warnings.Add(new(path, $"'{info.Title}': {info.UnavailableReason}"));
                if (!seenTriggers.Add(t.EventType)) Err(path, "Each trigger can be used once.");
                CheckCondition(t.Condition, path + ".condition", errors);
                if (t.InactiveDays is { } d && (info.Key != WorkflowEventTypes.LeadInactive || d is < 1 or > 365)) Err(path, info.Key == WorkflowEventTypes.LeadInactive ? "Inactive days must be between 1 and 365." : "Inactive days only apply to the inactivity trigger.");
                if (info.Key == WorkflowEventTypes.LeadInactive && t.InactiveDays is null) Err(path, "Say after how many days without activity a lead counts as inactive.");
            }
        }

        // steps
        var steps = def.Steps ?? [];
        if (steps.Count == 0) Err("steps", "Add at least one step.");
        if (steps.Count > WorkflowLimits.MaxSteps) { Err("steps", $"A workflow can have at most {WorkflowLimits.MaxSteps} steps."); return new(errors, warnings); }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i]; var path = $"steps[{i}]";
            if (!KeyPattern().IsMatch(s.Key ?? "")) Err(path + ".key", "A step key is lowercase letters, digits and underscores, starting with a letter (max 20).");
            else if (!keys.Add(s.Key!)) Err(path + ".key", $"The key '{s.Key}' is used twice.");
            if (string.IsNullOrWhiteSpace(s.Name) || s.Name.Trim().Length > 100) Err(path + ".name", "Give the step a name of up to 100 characters.");
            if (!Enum.IsDefined(s.Type)) { Err(path + ".type", "Unknown action."); continue; }
            if (s.MaxRetries is < 0 or > 5) Err(path + ".maxRetries", "Retries: 0 to 5.");
            if (!Enum.IsDefined(s.OnFailure)) Err(path + ".onFailure", "Unknown failure policy.");
            CheckCondition(s.Condition, path + ".runIf", errors);
            CheckConfig(s, path, errors);
            if (s.Type == StepType.ScheduleDemo) warnings.Add(new(path, "Demo scheduling is delivered in Phase 10. This workflow cannot be activated while it contains this step."));
        }
        if (errors.Count == 0) CheckGraph(steps, errors);
        return new(errors, warnings);
    }

    private static void CheckCondition(JsonElement? condition, string path, List<ValidationProblem> errors)
    {
        if (condition is null || condition.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return;
        var parsed = ConditionParser.Parse(condition.Value.GetRawText());
        foreach (var e in parsed.Errors) errors.Add(new(path, e));
    }

    private static void CheckConfig(StepDefinition s, string path, List<ValidationProblem> errors)
    {
        void Err(string msg) => errors.Add(new(path + ".config", msg));
        JsonObject cfg;
        try { cfg = s.Config is { ValueKind: JsonValueKind.Object } c ? (JsonObject)JsonNode.Parse(c.GetRawText())! : []; }
        catch (JsonException) { Err("The settings are not valid."); return; }
        if (cfg.ToJsonString().Length > 8000) { Err("The settings are too large."); return; }
        var allowed = AllowedConfig[s.Type];
        foreach (var k in cfg.Select(p => p.Key)) if (!allowed.Contains(k)) Err($"Unknown setting '{Cut(k)}' for this action.");
        string? Str(string k) => cfg[k] is JsonValue v && v.TryGetValue<string>(out var x) ? x?.Trim() : null;
        int? Int(string k) => cfg[k] is JsonValue v && v.TryGetValue<int>(out var x) ? x : null;
        Guid? Id(string k) => Guid.TryParse(Str(k), out var g) && g != Guid.Empty ? g : null;
        bool Placeholders(string? text, string label) { if (text is null) return true; var p = TemplateRenderer.Problems(text); if (p.Count == 0) return true; Err($"{label}: {p[0]}"); return false; }

        switch (s.Type)
        {
            case StepType.AssignLead:
                if (cfg["unassign"] is JsonValue u && u.TryGetValue<bool>(out var un) && un) { if (cfg.ContainsKey("userId")) Err("Choose a person or 'unassign', not both."); }
                else if (Id("userId") is null) Err("Choose the person the lead is assigned to.");
                break;
            case StepType.ChangeLeadStatus: if (string.IsNullOrWhiteSpace(Str("status")) || Str("status")!.Length > 100) Err("Choose a status."); break;
            case StepType.ChangeLeadStage: if (string.IsNullOrWhiteSpace(Str("stage")) || Str("stage")!.Length > 100) Err("Choose a stage."); break;
            case StepType.SendMessage:
                if (Id("templateId") is null) Err("Choose an approved message template.");
                if (Id("channelAccountId") is null) Err("Choose the WhatsApp account that sends the message.");
                break;
            case StepType.Wait: if (Int("minutes") is not { } m || m < 1 || m > WorkflowLimits.MaxWaitMinutes) Err($"Wait between 1 minute and {WorkflowLimits.MaxWaitMinutes / 1440} days."); break;
            case StepType.CreateFollowUp:
                if (Int("dueInHours") is not { } h || h < 1 || h > WorkflowLimits.MaxFollowUpHours) Err($"The task is due in 1 to {WorkflowLimits.MaxFollowUpHours} hours.");
                if (string.IsNullOrWhiteSpace(Str("note")) || Str("note")!.Length > 500) Err("Write the task (up to 500 characters).");
                else Placeholders(Str("note"), "Task");
                break;
            case StepType.NotifyUser:
                if (Str("target") is not { } tg || (tg != "owner" && !Guid.TryParse(tg, out _))) Err("Notify the lead's owner or choose a person.");
                if (string.IsNullOrWhiteSpace(Str("title")) || Str("title")!.Length > 150) Err("Write a title (up to 150 characters).");
                else Placeholders(Str("title"), "Title");
                if (string.IsNullOrWhiteSpace(Str("message")) || Str("message")!.Length > 1000) Err("Write the notice (up to 1000 characters).");
                else Placeholders(Str("message"), "Notice");
                break;
            case StepType.ScheduleDemo: if (Int("withinDays") is { } d && d is < 1 or > 14) Err("Look for a slot within 1 to 14 days."); break;
            case StepType.EscalateToHuman: if (Str("reason") is { } r && r.Length > 200) Err("The reason can have up to 200 characters."); break;
            case StepType.UpdateLeadFields:
                if (Str("field") is not { } f || !LeadTextFields.Contains(f)) Err("Choose one of: " + string.Join(", ", LeadTextFields) + ".");
                if (Str("value") is not { } v || v.Length > 100 || v.Contains('{') || v.Contains('}')) Err("Write the new value (up to 100 characters, no braces).");
                break;
            case StepType.AddTag: if (string.IsNullOrWhiteSpace(Str("tag")) || Str("tag")!.Length > 50) Err("Name the tag (up to 50 characters)."); break;
            case StepType.Branch:
                if (cfg["condition"] is null) Err("An if/else step needs a condition.");
                else { var cond = ConditionParser.Parse(cfg["condition"]!.ToJsonString()); foreach (var e in cond.Errors) Err("Condition: " + e); }
                if (string.IsNullOrWhiteSpace(Str("then")) && string.IsNullOrWhiteSpace(Str("else"))) Err("Say where the run continues for 'yes' or 'no'.");
                break;
            case StepType.Stop: if (Str("reason") is { } sr && sr.Length > 200) Err("The reason can have up to 200 characters."); break;
        }
    }

    /// <summary>Where the run can go after step <paramref name="index"/>: its sequential successor and/or its branch targets. "end" (or no successor) finishes the run.</summary>
    public static IReadOnlyList<string> Successors(IReadOnlyList<StepDefinition> steps, int index)
    {
        var s = steps[index]; var next = index + 1 < steps.Count ? steps[index + 1].Key : "end";
        if (s.Type == StepType.Stop) return ["end"];
        if (s.Type != StepType.Branch) return [next];
        var cfg = ConfigOf(s);
        string Target(string name) => cfg[name] is JsonValue v && v.TryGetValue<string>(out var t) && !string.IsNullOrWhiteSpace(t) ? t.Trim() : next;
        return [Target("then"), Target("else")];
    }

    public static JsonObject ConfigOf(StepDefinition s)
    {
        try { return s.Config is { ValueKind: JsonValueKind.Object } c ? (JsonObject)JsonNode.Parse(c.GetRawText())! : []; } catch (JsonException) { return []; }
    }

    private static void CheckGraph(IReadOnlyList<StepDefinition> steps, List<ValidationProblem> errors)
    {
        var index = steps.Select((s, i) => (s.Key, i)).ToDictionary(x => x.Key, x => x.i, StringComparer.Ordinal);
        for (var i = 0; i < steps.Count; i++)
            foreach (var target in Successors(steps, i))
                if (target != "end" && !index.ContainsKey(target)) errors.Add(new($"steps[{i}].config", $"The step '{Cut(target)}' does not exist."));
        if (errors.Count > 0) return;

        // loop detection (depth-first, white/grey/black) + number of branches on any path
        var colour = new int[steps.Count]; var stack = new List<string>();
        var maxBranches = 0;
        bool Visit(int i, int branches)
        {
            colour[i] = 1; stack.Add(steps[i].Key);
            if (steps[i].Type == StepType.Branch) branches++;
            maxBranches = Math.Max(maxBranches, branches);
            foreach (var t in Successors(steps, i))
            {
                if (t == "end") continue;
                var j = index[t];
                if (colour[j] == 1) { errors.Add(new("steps", "The steps form a loop: " + string.Join(" → ", stack.SkipWhile(k => k != t).Append(t)) + ". A run must always be able to finish.")); return false; }
                if (colour[j] == 0 && !Visit(j, branches)) return false;
            }
            stack.RemoveAt(stack.Count - 1); colour[i] = 2;
            return true;
        }
        if (!Visit(0, 0)) return;
        if (maxBranches > WorkflowLimits.MaxBranchesOnPath) errors.Add(new("steps", $"A path can pass at most {WorkflowLimits.MaxBranchesOnPath} if/else steps."));
    }

    private static string Cut(string? s) => s is null ? "" : s.Length > 40 ? s[..40] : s;
}

// ===================================================================================================================================================================
// Persisted progress of an execution
// ===================================================================================================================================================================

public sealed class StepState
{
    /// <summary>pending | done | skipped | failed | waiting | blocked</summary>
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    /// <summary>Bumped only when a previous side effect provably did not happen (a message that Failed), so a retry never repeats an effect that did.</summary>
    public int Generation { get; set; }
    public DateTime? WaitUntilUtc { get; set; }
    public string? Effect { get; set; }
    public string? Error { get; set; }
}

public sealed class ExecutionState
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };
    public Dictionary<string, StepState> Steps { get; set; } = [];
    /// <summary>Steps executed in this run (loop guard in addition to the definition check).</summary>
    public int Executed { get; set; }

    public StepState Of(string key) { if (!Steps.TryGetValue(key, out var s)) Steps[key] = s = new StepState(); return s; }
    public string Serialize() => JsonSerializer.Serialize(this, Json);
    public static ExecutionState Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<ExecutionState>(json, Json) ?? new(); } catch (JsonException) { return new(); }
    }
}

public static class WorkflowIdempotency
{
    /// <summary>The idempotency key of a step's side effect: stable across redelivery, retry and crash recovery.</summary>
    public static string StepKey(Guid executionId, string stepKey, int generation) => $"wf:{executionId:N}:{stepKey}:{generation}";

    /// <summary>Deterministic trigger-event id for runs started by the inactivity sweep: the same lead in the same inactive stretch maps to the same id.</summary>
    public static Guid SweepEventId(Guid workflowId, Guid leadId, DateTime lastActivityUtc)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"inactive:{workflowId:N}:{leadId:N}:{lastActivityUtc.Ticks}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    public static TimeSpan RetryBackoff(int attempt) => TimeSpan.FromMinutes(Math.Min(60, 1 << Math.Clamp(attempt - 1, 0, 6)));   // 1, 2, 4 ... max 60 minutes
}
