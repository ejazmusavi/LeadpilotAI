using System.ComponentModel.DataAnnotations;
using LeadPilot.Application.Agents;
using LeadPilot.Application.Common;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Ai;

namespace LeadPilot.Web.Models;

public class AgentsIndexViewModel
{
    public IReadOnlyList<AgentListItemDto> Agents { get; set; } = [];
    public AgentSettingsDto Settings { get; set; } = default!;
    public bool CanManage { get; set; }
}

public class AgentFormModel
{
    public Guid? Id { get; set; }
    public int? Version { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [StringLength(500)] public string? Description { get; set; }
    [Required, StringLength(8000)] public string Instructions { get; set; } = string.Empty;
    [StringLength(100)] public string? ChatModel { get; set; }
    [Range(0, 1)] public decimal? MinConfidence { get; set; }
    [Required, StringLength(500)] public string OffTopicReply { get; set; } = "Thank you for your message. I can only help with questions about our products and services. Is there something about them I can help you with?";
    public List<string> EnabledTools { get; set; } = [];
}

public class AgentEditViewModel
{
    public AgentFormModel Form { get; set; } = new();
    public AgentDto? Agent { get; set; }
    public IReadOnlyList<ToolInfoDto> Tools { get; set; } = [];
    public string QuestionsText { get; set; } = string.Empty;
    public bool CanManage { get; set; }
    public IReadOnlyList<string> SuggestedModels { get; set; } = [];
}

/// <summary>One question per line: <c>key | question | text, number or choice | required or optional | answer1; answer2 (choice only)</c>.</summary>
public static class QuestionLines
{
    public static string Format(IEnumerable<QuestionDto> questions) =>
        string.Join("\n", questions.Select(q => $"{q.Key} | {q.Question} | {q.AnswerType.ToString().ToLowerInvariant()} | {(q.Required ? "required" : "optional")}{(q.Choices.Count > 0 ? " | " + string.Join("; ", q.Choices) : "")}"));

    public static (List<QuestionInput> Questions, string? Error) Parse(string? text)
    {
        var result = new List<QuestionInput>(); var n = 0;
        foreach (var raw in (text ?? string.Empty).Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            n++;
            var p = raw.Split('|', StringSplitOptions.TrimEntries);
            if (p.Length < 4) return ([], $"Line {n}: use  key | question | text, number or choice | required or optional  (and for choice: | answer1; answer2).");
            if (!Enum.TryParse<AnswerType>(p[2], true, out var type)) return ([], $"Line {n}: the type must be text, number or choice.");
            if (!p[3].Equals("required", StringComparison.OrdinalIgnoreCase) && !p[3].Equals("optional", StringComparison.OrdinalIgnoreCase)) return ([], $"Line {n}: write required or optional.");
            var choices = p.Length > 4 ? p[4].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() : null;
            result.Add(new QuestionInput(p[0], p[1], type, choices, p[3].Equals("required", StringComparison.OrdinalIgnoreCase)));
        }
        return (result, null);
    }
}

public class AgentSettingsFormModel
{
    public bool AiRepliesAllowed { get; set; }
    [Range(1, 100)] public int MaxAutoRepliesPerConversationPerDay { get; set; } = 10;
    [Range(0, 10)] public int MaxTrialsPerLead { get; set; } = 1;
    [Range(1, 720)] public int ApprovalLifetimeHours { get; set; } = 72;
    [Range(1, 20)] public int MaxOpenFollowUpsPerLead { get; set; } = 3;
}

public class ToolLogViewModel
{
    public PagedResult<ToolExecutionDto> Result { get; set; } = default!;
    public ToolExecutionQuery Query { get; set; } = new();
    public IDictionary<string, string?> QueryValues { get; set; } = new Dictionary<string, string?>();
    public IReadOnlyList<ToolInfoDto> Tools { get; set; } = [];
}

public class ApprovalsViewModel
{
    public PagedResult<ApprovalDto> Result { get; set; } = default!;
    public ApprovalQuery Query { get; set; } = new();
    public IDictionary<string, string?> QueryValues { get; set; } = new Dictionary<string, string?>();
    public bool CanDecide { get; set; }
}

public class CatalogIndexViewModel { public IReadOnlyList<ProductListItemDto> Products { get; set; } = []; public bool CanManage { get; set; } }

public class ProductFormModel
{
    public Guid? Id { get; set; }
    public int? Version { get; set; }
    [Required, StringLength(150)] public string Name { get; set; } = string.Empty;
    [StringLength(1000)] public string? Summary { get; set; }
    [StringLength(4000)] public string? Features { get; set; }
    [StringLength(2000)] public string? DemoProcess { get; set; }
    [StringLength(2000)] public string? TrialProcess { get; set; }
    public bool IsConfirmed { get; set; }
    public bool IsActive { get; set; } = true;
}

public class PlanFormModel
{
    public Guid? Id { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [Range(0, 1_000_000_000)] public decimal? Price { get; set; }
    [Required, StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = "USD";
    [StringLength(40)] public string? BillingPeriod { get; set; }
    [StringLength(2000)] public string? Details { get; set; }
    [Range(0, 100)] public decimal? MaxDiscountPercent { get; set; }
    public bool IsConfirmed { get; set; }
    public bool IsActive { get; set; } = true;
}

public class ProductEditViewModel { public ProductFormModel Form { get; set; } = new(); public ProductDto? Product { get; set; } public PlanFormModel NewPlan { get; set; } = new(); public bool CanManage { get; set; } }

public class FollowUpsViewModel { public PagedResult<FollowUpDto> Result { get; set; } = default!; public FollowUpQuery Query { get; set; } = new(); public IDictionary<string, string?> QueryValues { get; set; } = new Dictionary<string, string?>(); public bool CanUpdate { get; set; } }
