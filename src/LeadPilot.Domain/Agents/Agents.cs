using System.Text.Json;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;

namespace LeadPilot.Domain.Agents;

public enum ApprovalStatus { Pending = 1, Approved = 2, Rejected = 3, Expired = 4 }

/// <summary>What happened when an approved request was carried out. NotAvailable = the Phase 10 service behind the tool is not connected: nothing happened, and the record says so.</summary>
public enum ExecutionState { None = 0, Executed = 1, NotAvailable = 2, Failed = 3 }

public enum HandoffKind { Takeover = 1, AiPaused = 2, AiResumed = 3, Escalated = 4, Assigned = 5, Unassigned = 6, StatusChanged = 7, OptOut = 8, AutoTakeover = 9 }

public enum ActorKind { User = 1, Ai = 2, System = 3 }

public enum AutoSendState { NotAttempted = 0, Sent = 1, Blocked = 2 }

public enum FollowUpStatus { Open = 1, Done = 2, Cancelled = 3 }

public enum AnswerType { Text = 1, Number = 2, Choice = 3 }

public enum QualificationFit { Unknown = 0, Poor = 1, Possible = 2, Good = 3 }

/// <summary>
/// One configurable sales agent of a tenant. Instructions, enabled tools, qualification questions and the catalog are TENANT DATA: nothing about a product or industry is in the code.
/// Automatic sending is off until a person switches it on (here AND in <see cref="AgentTenantSettings"/>).
/// </summary>
public class SalesAgent : AuditableEntity, ITenantOwned
{
    private SalesAgent() { }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string Instructions { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;
    /// <summary>The agent that answers conversations. At most one per tenant.</summary>
    public bool IsDefault { get; private set; }
    /// <summary>Marks seed data created from the sample configuration. Cleared when a person saves the agent.</summary>
    public bool IsSample { get; private set; }
    public string? ChatModel { get; private set; }
    public decimal? MinConfidence { get; private set; }
    /// <summary>JSON array of enabled tool names. TransferToHuman and StopMarketingMessages are always available (safety tools).</summary>
    public string EnabledToolsJson { get; private set; } = "[]";
    public bool AutoReplyEnabled { get; private set; }
    public string OffTopicReply { get; private set; } = "Thank you for your message. I can only help with questions about our products and services. Is there something about them I can help you with?";
    public int Version { get; private set; } = 1;

    public static SalesAgent Create(Guid tenantId, string name, string instructions, bool isDefault, bool isSample, DateTime nowUtc, string? actor)
    {
        var a = new SalesAgent { TenantId = tenantId, CreatedAtUtc = nowUtc, CreatedBy = actor, IsDefault = isDefault, IsSample = isSample };
        a.Name = Guard.Required(name, 100, "Agent name");
        a.Instructions = Guard.Required(instructions, 8000, "Instructions");
        return a;
    }

    public void Update(string name, string? description, string instructions, string? chatModel, decimal? minConfidence, IEnumerable<string> tools, string offTopicReply, DateTime nowUtc, string? actor, bool keepSampleFlag = false)
    {
        Name = Guard.Required(name, 100, "Agent name");
        Description = Guard.Optional(description, 500, "Description");
        Instructions = Guard.Required(instructions, 8000, "Instructions");
        ChatModel = Guard.Optional(chatModel, 100, "Model");
        if (ChatModel is not null && !System.Text.RegularExpressions.Regex.IsMatch(ChatModel, @"^[A-Za-z0-9][A-Za-z0-9._:\-/]{0,99}$")) throw new DomainException("The model name contains invalid characters.");
        if (minConfidence is < 0 or > 1) throw new DomainException("The minimum confidence must be between 0 and 1.");
        MinConfidence = minConfidence;
        EnabledToolsJson = JsonSerializer.Serialize(tools.Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList());
        OffTopicReply = Guard.Required(offTopicReply, 500, "The off-topic reply");
        if (!keepSampleFlag) IsSample = false;
        Touch(nowUtc, actor);
    }

    public IReadOnlyList<string> EnabledTools => JsonSerializer.Deserialize<List<string>>(EnabledToolsJson) ?? [];
    public void SetActive(bool active, DateTime nowUtc, string? actor) { IsActive = active; if (!active) { IsDefault = false; AutoReplyEnabled = false; } Touch(nowUtc, actor); }
    public void SetDefault(bool value, DateTime nowUtc, string? actor) { if (value && !IsActive) throw new DomainException("An inactive agent cannot be the default."); IsDefault = value; Touch(nowUtc, actor); }
    public void SetAutoReply(bool enabled, DateTime nowUtc, string? actor) { if (enabled && !IsActive) throw new DomainException("Switch the agent on first."); AutoReplyEnabled = enabled; Touch(nowUtc, actor); }
    private void Touch(DateTime nowUtc, string? actor) { UpdatedAtUtc = nowUtc; UpdatedBy = actor; Version++; }
}

/// <summary>A question the agent asks to qualify a prospect. The answers are stored under <see cref="Key"/>; the LLM can only record answers for keys that exist here.</summary>
public class AgentQualificationQuestion : Entity, ITenantOwned
{
    private AgentQualificationQuestion() { }
    public Guid TenantId { get; private set; }
    public Guid AgentId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Question { get; private set; } = string.Empty;
    public AnswerType AnswerType { get; private set; }
    /// <summary>For Choice questions: JSON array of the allowed answers.</summary>
    public string? ChoicesJson { get; private set; }
    public bool Required { get; private set; }
    public int SortOrder { get; private set; }

    public static AgentQualificationQuestion Create(Guid tenantId, Guid agentId, string key, string question, AnswerType type, IEnumerable<string>? choices, bool required, int order)
    {
        var k = Guard.Required(key, 40, "Key").ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(k, "^[a-z][a-z0-9_]*$")) throw new DomainException("The key may contain lower-case letters, digits and underscores, and starts with a letter.");
        var list = choices?.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct().ToList() ?? [];
        if (type == AnswerType.Choice && (list.Count < 2 || list.Count > 12 || list.Any(c => c.Length > 80))) throw new DomainException("A choice question needs 2 to 12 answers of at most 80 characters.");
        return new AgentQualificationQuestion { TenantId = tenantId, AgentId = agentId, Key = k, Question = Guard.Required(question, 300, "Question"), AnswerType = type, ChoicesJson = type == AnswerType.Choice ? JsonSerializer.Serialize(list) : null, Required = required, SortOrder = order };
    }

    public IReadOnlyList<string> Choices => ChoicesJson is null ? [] : JsonSerializer.Deserialize<List<string>>(ChoicesJson) ?? [];
}

/// <summary>A product or service of the tenant. Only CONFIRMED, active products are ever given to the AI as facts.</summary>
public class CatalogProduct : AuditableEntity, ITenantOwned
{
    private CatalogProduct() { }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Summary { get; private set; }
    /// <summary>One feature per line.</summary>
    public string? Features { get; private set; }
    public string? DemoProcess { get; private set; }
    public string? TrialProcess { get; private set; }
    public bool IsActive { get; private set; } = true;
    /// <summary>A person who knows the product has confirmed the text is true. Sample/placeholder rows are NOT confirmed, so the AI says it does not know.</summary>
    public bool IsConfirmed { get; private set; }
    public bool IsSample { get; private set; }
    public int Version { get; private set; } = 1;

    public static CatalogProduct Create(Guid tenantId, string name, string? summary, string? features, string? demo, string? trial, bool confirmed, bool sample, DateTime nowUtc, string? actor)
    {
        var p = new CatalogProduct { TenantId = tenantId, CreatedAtUtc = nowUtc, CreatedBy = actor, IsSample = sample };
        p.Set(name, summary, features, demo, trial, confirmed);
        return p;
    }

    public void Update(string name, string? summary, string? features, string? demo, string? trial, bool confirmed, bool active, DateTime nowUtc, string? actor)
    {
        Set(name, summary, features, demo, trial, confirmed); IsActive = active; IsSample = false; UpdatedAtUtc = nowUtc; UpdatedBy = actor; Version++;
    }

    private void Set(string name, string? summary, string? features, string? demo, string? trial, bool confirmed)
    {
        Name = Guard.Required(name, 150, "Product name"); Summary = Guard.Optional(summary, 1000, "Summary"); Features = Guard.Optional(features, 4000, "Features");
        DemoProcess = Guard.Optional(demo, 2000, "Demo process"); TrialProcess = Guard.Optional(trial, 2000, "Trial process"); IsConfirmed = confirmed;
    }

    public IReadOnlyList<string> FeatureList => (Features ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(60).ToList();
}

public class PricingPlan : AuditableEntity, ITenantOwned
{
    private PricingPlan() { }
    public Guid TenantId { get; private set; }
    public Guid ProductId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    /// <summary>Null = no price has been set; the AI must say so.</summary>
    public decimal? Price { get; private set; }
    public string Currency { get; private set; } = "USD";
    public string? BillingPeriod { get; private set; }
    public string? Details { get; private set; }
    /// <summary>Largest discount the AI may even ASK to arrange; any discount at all still needs a person's approval. Null = none.</summary>
    public decimal? MaxDiscountPercent { get; private set; }
    public bool IsActive { get; private set; } = true;
    public bool IsConfirmed { get; private set; }
    public bool IsSample { get; private set; }

    public static PricingPlan Create(Guid tenantId, Guid productId, string name, decimal? price, string currency, string? period, string? details, decimal? maxDiscount, bool confirmed, bool sample, DateTime nowUtc, string? actor)
    {
        var p = new PricingPlan { TenantId = tenantId, ProductId = productId, CreatedAtUtc = nowUtc, CreatedBy = actor, IsSample = sample };
        p.Set(name, price, currency, period, details, maxDiscount, confirmed);
        return p;
    }

    public void Update(string name, decimal? price, string currency, string? period, string? details, decimal? maxDiscount, bool confirmed, bool active, DateTime nowUtc, string? actor)
    {
        Set(name, price, currency, period, details, maxDiscount, confirmed); IsActive = active; IsSample = false; UpdatedAtUtc = nowUtc; UpdatedBy = actor;
    }

    private void Set(string name, decimal? price, string currency, string? period, string? details, decimal? maxDiscount, bool confirmed)
    {
        Name = Guard.Required(name, 100, "Plan name");
        if (price is < 0 or > 1_000_000_000m) throw new DomainException("The price must be between 0 and 1,000,000,000.");
        Price = price;
        var c = Guard.Required(currency, 3, "Currency").ToUpperInvariant();
        if (c.Length != 3 || !c.All(char.IsLetter)) throw new DomainException("The currency must be a 3-letter code such as USD.");
        Currency = c; BillingPeriod = Guard.Optional(period, 40, "Billing period"); Details = Guard.Optional(details, 2000, "Details");
        if (maxDiscount is < 0 or > 100) throw new DomainException("The maximum discount must be between 0 and 100.");
        MaxDiscountPercent = maxDiscount;
        if (confirmed && price is null) throw new DomainException("A plan cannot be confirmed without a price. Enter the price, or leave the plan unconfirmed.");
        IsConfirmed = confirmed;
    }
}

/// <summary>A sensitive AI action waiting for a person (payment request, discount, custom terms, trial over the limit). The exact validated arguments are stored; approving runs THEM, nothing the AI says afterwards.</summary>
public class ApprovalRequest : Entity, ITenantOwned
{
    private ApprovalRequest() { }
    public Guid TenantId { get; private set; }
    public Guid? AgentId { get; private set; }
    public Guid ConversationId { get; private set; }
    public Guid? LeadId { get; private set; }
    public string ToolName { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public string Summary { get; private set; } = string.Empty;
    public string InputJson { get; private set; } = "{}";
    public string DedupeKey { get; private set; } = string.Empty;
    public ApprovalStatus Status { get; private set; } = ApprovalStatus.Pending;
    public ExecutionState Execution { get; private set; }
    public string? ExecutionNote { get; private set; }
    public DateTime RequestedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public Guid? DecidedByUserId { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    public string? DecisionNote { get; private set; }

    public static ApprovalRequest Create(Guid tenantId, Guid? agentId, Guid conversationId, Guid? leadId, string tool, string reason, string summary, string inputJson, string dedupeKey, DateTime nowUtc, TimeSpan lifetime) =>
        new()
        {
            TenantId = tenantId, AgentId = agentId, ConversationId = conversationId, LeadId = leadId, ToolName = tool, Reason = Cut(reason, 200)!, Summary = Cut(summary, 500)!, InputJson = inputJson,
            DedupeKey = Cut(dedupeKey, 100)!, RequestedAtUtc = nowUtc, ExpiresAtUtc = nowUtc + lifetime
        };

    public bool IsExpired(DateTime nowUtc) => Status == ApprovalStatus.Pending && nowUtc >= ExpiresAtUtc;

    public void Approve(Guid? userId, string? note, DateTime nowUtc)
    {
        if (Status != ApprovalStatus.Pending) throw new DomainException("This request was already decided.");
        if (IsExpired(nowUtc)) { Status = ApprovalStatus.Expired; throw new DomainException("This request expired. Ask the customer again."); }
        Status = ApprovalStatus.Approved; DecidedByUserId = userId; DecidedAtUtc = nowUtc; DecisionNote = Cut(note, 300);
    }

    public void Reject(Guid? userId, string? note, DateTime nowUtc)
    {
        if (Status != ApprovalStatus.Pending) throw new DomainException("This request was already decided.");
        Status = ApprovalStatus.Rejected; DecidedByUserId = userId; DecidedAtUtc = nowUtc; DecisionNote = Cut(note, 300);
    }

    public void Expire(DateTime nowUtc) { if (IsExpired(nowUtc)) Status = ApprovalStatus.Expired; }
    public void RecordExecution(ExecutionState state, string? note) { Execution = state; ExecutionNote = Cut(note, 500); }
    private static string? Cut(string? s, int n) => s is null ? null : s.Length > n ? s[..n] : s;
}

/// <summary>Internal note on a conversation (never sent to the customer). Append-only.</summary>
public class ConversationNote : Entity, ITenantOwned, IAppendOnly
{
    private ConversationNote() { }
    public Guid TenantId { get; private set; }
    public Guid ConversationId { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public Guid? AuthorUserId { get; private set; }
    public ActorKind Author { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static ConversationNote Create(Guid tenantId, Guid conversationId, string body, Guid? userId, ActorKind author, DateTime nowUtc) =>
        new() { TenantId = tenantId, ConversationId = conversationId, Body = Guard.Required(body, 2000, "Note"), AuthorUserId = userId, Author = author, CreatedAtUtc = nowUtc };
}

/// <summary>Who changed who answers a conversation, why, and when: takeover, pause/resume, escalation, assignment, status. Append-only.</summary>
public class HandoffEvent : Entity, ITenantOwned, IAppendOnly
{
    private HandoffEvent() { }
    public Guid TenantId { get; private set; }
    public Guid ConversationId { get; private set; }
    public HandoffKind Kind { get; private set; }
    public ActorKind Actor { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public Guid? TargetUserId { get; private set; }
    public string? Reason { get; private set; }
    public string? FromState { get; private set; }
    public string? ToState { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    public static HandoffEvent Create(Guid tenantId, Guid conversationId, HandoffKind kind, ActorKind actor, Guid? actorUserId, Guid? targetUserId, string? reason, string? from, string? to, DateTime nowUtc) =>
        new() { TenantId = tenantId, ConversationId = conversationId, Kind = kind, Actor = actor, ActorUserId = actorUserId, TargetUserId = targetUserId, Reason = reason is { Length: > 300 } ? reason[..300] : reason, FromState = from, ToState = to, OccurredAtUtc = nowUtc };
}

/// <summary>Tenant-wide switches and limits of the sales agents. Safe defaults: AI sending forbidden, conservative caps.</summary>
public class AgentTenantSettings : AuditableEntity, ITenantOwned
{
    private AgentTenantSettings() { }
    public Guid TenantId { get; private set; }
    /// <summary>Master switch for AI replies leaving the system. Off by default; an agent's own switch must be on too.</summary>
    public bool AiRepliesAllowed { get; private set; }
    public int MaxAutoRepliesPerConversationPerDay { get; private set; } = 10;
    public int MaxTrialsPerLead { get; private set; } = 1;
    public int ApprovalLifetimeHours { get; private set; } = 72;
    public int MaxOpenFollowUpsPerLead { get; private set; } = 3;
    public int Version { get; private set; } = 1;

    public static AgentTenantSettings Create(Guid tenantId, DateTime nowUtc) => new() { TenantId = tenantId, CreatedAtUtc = nowUtc };

    public void Configure(bool aiRepliesAllowed, int maxRepliesPerDay, int maxTrials, int approvalHours, int maxFollowUps, DateTime nowUtc, string? actor)
    {
        if (maxRepliesPerDay is < 1 or > 100) throw new DomainException("Automatic replies per conversation per day must be between 1 and 100.");
        if (maxTrials is < 0 or > 10) throw new DomainException("Trials per lead must be between 0 and 10.");
        if (approvalHours is < 1 or > 720) throw new DomainException("Approval lifetime must be between 1 and 720 hours.");
        if (maxFollowUps is < 1 or > 20) throw new DomainException("Open follow-ups per lead must be between 1 and 20.");
        AiRepliesAllowed = aiRepliesAllowed; MaxAutoRepliesPerConversationPerDay = maxRepliesPerDay; MaxTrialsPerLead = maxTrials; ApprovalLifetimeHours = approvalHours; MaxOpenFollowUpsPerLead = maxFollowUps;
        UpdatedAtUtc = nowUtc; UpdatedBy = actor; Version++;
    }
}

/// <summary>What the agent learned about a lead through the tenant's qualification questions. One row per lead and agent; answers are validated against the questions.</summary>
public class LeadQualification : Entity, ITenantOwned
{
    private LeadQualification() { }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }
    public Guid? AgentId { get; private set; }
    public string AnswersJson { get; private set; } = "{}";
    public QualificationFit Fit { get; private set; }
    public string? Summary { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public int Version { get; private set; } = 1;

    public static LeadQualification Create(Guid tenantId, Guid leadId, Guid? agentId, string answersJson, QualificationFit fit, string? summary, DateTime nowUtc) =>
        new() { TenantId = tenantId, LeadId = leadId, AgentId = agentId, AnswersJson = answersJson, Fit = fit, Summary = Cut(summary), UpdatedAtUtc = nowUtc };

    public void Update(string answersJson, QualificationFit fit, string? summary, DateTime nowUtc) { AnswersJson = answersJson; if (fit != QualificationFit.Unknown) Fit = fit; if (summary is not null) Summary = Cut(summary); UpdatedAtUtc = nowUtc; Version++; }
    private static string? Cut(string? s) => s is null ? null : s.Length > 300 ? s[..300] : s;
}

/// <summary>An internal task: someone should contact this lead again. Nothing is sent automatically (Phase 9 may automate).</summary>
public class FollowUp : AuditableEntity, ITenantOwned
{
    private FollowUp() { }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }
    public Guid? ConversationId { get; private set; }
    public Guid? AssignedUserId { get; private set; }
    public DateTime DueAtUtc { get; private set; }
    public string Note { get; private set; } = string.Empty;
    public FollowUpStatus Status { get; private set; } = FollowUpStatus.Open;
    public ActorKind CreatedByKind { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }

    public static FollowUp Create(Guid tenantId, Guid leadId, Guid? conversationId, Guid? assignedUserId, DateTime dueAtUtc, string note, ActorKind by, DateTime nowUtc) =>
        new() { TenantId = tenantId, LeadId = leadId, ConversationId = conversationId, AssignedUserId = assignedUserId, DueAtUtc = dueAtUtc, Note = Guard.Required(note, 500, "Note"), CreatedByKind = by, CreatedAtUtc = nowUtc };

    public void Close(FollowUpStatus status, DateTime nowUtc)
    {
        if (Status != FollowUpStatus.Open) throw new DomainException("This follow-up is already closed.");
        Status = status; ClosedAtUtc = nowUtc; UpdatedAtUtc = nowUtc;
    }
}
