using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using LeadPilot.Application.Common;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Workflows;

namespace LeadPilot.Web.Models;

public static class WorkflowJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() }, MaxDepth = 24 };
    public const int MaxDefinitionChars = 200_000;

    /// <summary>The builder posts its state as JSON (a transport only: people never type it). Anything that is not a valid definition is rejected here before the service sees it.</summary>
    public static (WorkflowDefinition? Definition, string? Error) Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, "The workflow is empty.");
        if (json.Length > MaxDefinitionChars) return (null, "The workflow is too large.");
        try { return (JsonSerializer.Deserialize<WorkflowDefinition>(json, Options), null); }
        catch (JsonException) { return (null, "The workflow could not be read. Reload the page and try again."); }
    }

    /// <summary>JSON that is safe inside a script block: the default encoder escapes &lt;, &gt; and &amp;.</summary>
    public static string ForScript(object value) => JsonSerializer.Serialize(value, Options);
}

public class WorkflowListViewModel
{
    public PagedResult<WorkflowListItemDto> Result { get; set; } = default!;
    public WorkflowQuery Query { get; set; } = new();
    public IDictionary<string, string?> QueryValues { get; set; } = new Dictionary<string, string?>();
    public WorkflowSettingsDto Settings { get; set; } = default!;
    public bool CanCreate { get; set; }
}

public class WorkflowBuilderViewModel
{
    public Guid? Id { get; set; }
    public int? VersionNumber { get; set; }
    [Required, StringLength(150)] public string Name { get; set; } = string.Empty;
    [StringLength(1000)] public string? Description { get; set; }
    public string DefinitionJson { get; set; } = string.Empty;
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever, Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever] public WorkflowCatalogDto Catalog { get; set; } = default!;
    [Microsoft.AspNetCore.Mvc.ModelBinding.BindNever, Microsoft.AspNetCore.Mvc.ModelBinding.Validation.ValidateNever] public ValidationReport? Report { get; set; }
    public bool ActiveExists { get; set; }
}

public class WorkflowDetailsViewModel
{
    public WorkflowDto Workflow { get; set; } = default!;
    public bool CanUpdate { get; set; }
    public bool CanActivate { get; set; }
    public bool CanExecute { get; set; }
    public bool CanDelete { get; set; }
    public PagedResult<ExecutionListItemDto> Recent { get; set; } = default!;
}

public class WorkflowVersionViewModel
{
    public WorkflowDto Workflow { get; set; } = default!;
    public WorkflowVersionDto Version { get; set; } = default!;
    public bool CanUpdate { get; set; }
    public bool CanActivate { get; set; }
    public WorkflowCatalogDto Catalog { get; set; } = default!;
}

public class WorkflowSimulateViewModel
{
    public WorkflowDto Workflow { get; set; } = default!;
    public Guid? LeadId { get; set; }
    public string? LeadName { get; set; }
    public string? MessageText { get; set; }
    public string? TriggerKey { get; set; }
    public SimulationDto? Result { get; set; }
    public bool CanRun { get; set; }
}

public class ExecutionListViewModel
{
    public PagedResult<ExecutionListItemDto> Result { get; set; } = default!;
    public ExecutionQuery Query { get; set; } = new();
    public IDictionary<string, string?> QueryValues { get; set; } = new Dictionary<string, string?>();
    public IReadOnlyList<WorkflowListItemDto> Workflows { get; set; } = [];
}

public class ExecutionDetailsViewModel
{
    public ExecutionDto Execution { get; set; } = default!;
    public bool CanExecute { get; set; }
}

public class WorkflowSettingsFormModel
{
    [Range(1, 500)] public int MaxConcurrentExecutions { get; set; }
    [Range(0, 1_000_000)] public int MonthlyExecutionLimit { get; set; }
    [Range(0, 10_000)] public int DailyMessageLimit { get; set; }
    public int? ExpectedVersion { get; set; }
}

public class WorkflowSettingsViewModel
{
    public WorkflowSettingsDto Settings { get; set; } = default!;
    public WorkflowSettingsFormModel Form { get; set; } = new();
    public bool CanActivate { get; set; }
}

/// <summary>Human-readable text for conditions and steps (read-only pages, execution detail).</summary>
public static class WorkflowDescribe
{
    public static string Condition(object? c, WorkflowCatalogDto catalog)
    {
        if (c is not JsonElement e || e.ValueKind != JsonValueKind.Object) return "always";
        return Node(e, catalog);
    }

    private static string Node(JsonElement e, WorkflowCatalogDto cat)
    {
        if (e.TryGetProperty("all", out var all)) return "(" + string.Join(" AND ", all.EnumerateArray().Select(x => Node(x, cat))) + ")";
        if (e.TryGetProperty("any", out var any)) return "(" + string.Join(" OR ", any.EnumerateArray().Select(x => Node(x, cat))) + ")";
        if (e.TryGetProperty("not", out var not)) return "NOT " + Node(not, cat);
        if (e.TryGetProperty("window", out var w))
            return $"during {string.Join(", ", w.GetProperty("days").EnumerateArray().Select(d => d.GetString()))} {w.GetProperty("from").GetString()}–{w.GetProperty("to").GetString()} (workspace time zone)";
        var field = e.GetProperty("field").GetString() ?? "";
        var label = cat.Fields.FirstOrDefault(f => f.Name == field)?.Label ?? field;
        var op = e.GetProperty("op").GetString() ?? "";
        var text = op switch { "eq" => "is", "neq" => "is not", "gt" => ">", "gte" => "≥", "lt" => "<", "lte" => "≤", "contains" => "contains", "not_contains" => "does not contain", "starts_with" => "starts with", "in" => "is one of", "is_empty" => "is empty", "is_not_empty" => "is not empty", _ => op };
        return e.TryGetProperty("value", out var v) && v.ValueKind != JsonValueKind.Null ? $"{label} {text} {(v.ValueKind == JsonValueKind.String ? "“" + v.GetString() + "”" : v.ToString())}" : $"{label} {text}";
    }

    public static string Step(StepDto s, WorkflowCatalogDto cat)
    {
        if (s.Config is not JsonElement c || c.ValueKind != JsonValueKind.Object) return "";
        string? S(string k) => c.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string Name(IReadOnlyList<CatalogOption> list, string? id) => list.FirstOrDefault(o => o.Value == id)?.Label ?? "(removed)";
        return s.Type switch
        {
            StepType.SendMessage => $"Template “{Name(cat.Templates, S("templateId"))}” from “{Name(cat.Accounts, S("channelAccountId"))}”",
            StepType.Wait => c.TryGetProperty("minutes", out var m) ? Minutes(m.GetInt32()) : "",
            StepType.AssignLead => c.TryGetProperty("unassign", out var u) && u.ValueKind == JsonValueKind.True ? "Remove the owner" : $"Assign to {Name(cat.Users, S("userId"))}",
            StepType.ChangeLeadStatus => "Status → " + S("status"),
            StepType.ChangeLeadStage => "Stage → " + S("stage"),
            StepType.CreateFollowUp => $"Task due in {(c.TryGetProperty("dueInHours", out var h) ? h.GetInt32() : 0)} h: {S("note")}",
            StepType.NotifyUser => $"Notify {(S("target") == "owner" ? "the lead's owner" : Name(cat.Users, S("target")))}: {S("title")}",
            StepType.EscalateToHuman => "Hand over to a person" + (S("reason") is { } r ? ": " + r : ""),
            StepType.UpdateLeadFields => $"{S("field")} = {S("value")}",
            StepType.AddTag => "Tag “" + S("tag") + "”",
            StepType.Branch => $"If {Condition(c.TryGetProperty("condition", out var cond) ? cond : null, cat)} → {S("then") ?? "next"}, otherwise → {S("else") ?? "next"}",
            StepType.Stop => "End the run" + (S("reason") is { } sr ? ": " + sr : ""),
            StepType.ScheduleDemo => "Book the next free demo slot (Phase 10)",
            _ => "",
        };
    }

    public static string Minutes(int m) => m % 1440 == 0 ? $"Wait {m / 1440} day(s)" : m % 60 == 0 ? $"Wait {m / 60} hour(s)" : $"Wait {m} minute(s)";

    public static string StatusClass(ExecutionStatus s) => s switch
    {
        ExecutionStatus.Completed => "text-bg-success", ExecutionStatus.Failed => "text-bg-danger", ExecutionStatus.Cancelled => "text-bg-secondary", ExecutionStatus.Stopped => "text-bg-warning",
        ExecutionStatus.Running => "text-bg-primary", ExecutionStatus.Waiting => "text-bg-info", _ => "text-bg-light border",
    };
}
