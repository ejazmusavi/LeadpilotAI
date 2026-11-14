using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LeadPilot.Application.Agents;
using LeadPilot.Application.Ai;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TenancyResultFlow = LeadPilot.Infrastructure.Services.Tenancy.ResultFlow;

namespace LeadPilot.Infrastructure.Agents;

/// <summary>Everything a tool call is allowed to know. The tenant is NOT here: it comes from the tenant context inside <see cref="CrmContext"/>, never from the model.</summary>
public sealed record ToolCallContext(Conversation Conversation, Guid? TriggerMessageId, SalesAgent? Agent, IReadOnlySet<string> Allowed)
{
    /// <summary>Set only when a person approved the stored action.</summary>
    public ApprovalRequest? Approval { get; init; }
    public Guid? ExecutedByUserId { get; init; }
    public bool Approved => Approval is { Status: ApprovalStatus.Approved };
}

public sealed record ToolCall(string Name, string ArgumentsJson);

/// <summary>What the model is shown (and what is logged): a status and structured data. <see cref="Data"/> never contains ids of other tenants, secrets or raw records.</summary>
public sealed record ToolResult(string Name, ToolOutcome Outcome, string? Reason, string Json, Guid? ApprovalRequestId)
{
    public string Status => Outcome switch { ToolOutcome.Executed => "ok", ToolOutcome.Denied => "denied", ToolOutcome.Failed => "error", ToolOutcome.ApprovalRequired => "approval_required", ToolOutcome.NotAvailable => "not_available", _ => "invalid_arguments" };
}

/// <summary>What a handler returns. Handlers validate business rules and mutate tracked entities; the runner saves them together with the execution record.</summary>
internal sealed record ToolOutput(ToolOutcome Outcome, string? Reason, object Data, string? ApprovalReason = null, string? ApprovalSummary = null)
{
    public static ToolOutput Ok(object data) => new(ToolOutcome.Executed, null, data);
    public static ToolOutput Deny(string reason, string message) => new(ToolOutcome.Denied, reason, new { message });
    public static ToolOutput Invalid(string reason, string message) => new(ToolOutcome.Invalid, reason, new { message });
    public static ToolOutput NotAvailable(string message) => new(ToolOutcome.NotAvailable, "service_not_connected", new { message, nothingHappened = true });
    public static ToolOutput NeedsApproval(string why, string summary) => new(ToolOutcome.ApprovalRequired, "approval_required", new { message = "A colleague must approve this before anything happens. Do NOT tell the customer it is done; say a colleague will confirm.", why }, why, summary);
}

/// <summary>
/// The controlled tool gateway, the only way the AI changes anything. For every call: the tool must exist in <see cref="AgentToolCatalog"/> and be enabled for the agent (safety tools always are), the arguments are
/// schema-validated (arguments that try to pick a tenant/user/lead/conversation are refused), sensitive actions become an <see cref="ApprovalRequest"/> instead of running, the handler works on the CONVERSATION'S lead
/// only, and one execution row (inputs, outputs, outcome, approval link) is written together with the change. A redelivered event replays the stored result instead of acting twice. There is no raw query/SQL tool.
/// </summary>
public sealed class AgentToolRunner(CrmContext crm, AgentToolHandlers handlers, ILogger<AgentToolRunner> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public async Task<ToolResult> RunAsync(ToolCall call, ToolCallContext ctx, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew(); var db = crm.Db; var tenantId = crm.TenantId;
        var def = AgentToolCatalog.Find(call.Name);
        var leadId = await handlers.LeadIdOfAsync(ctx.Conversation, ct);
        if (def is null) return await RecordAsync(ctx, call.Name, Summarise(call.ArgumentsJson), null, ToolOutcome.Denied, "not_a_permitted_tool", new { message = "That tool does not exist." }, leadId, null, null, sw, ct);

        if (!ctx.Approved && !ctx.Allowed.Contains(def.Name))
            return await RecordAsync(ctx, def.Name, Summarise(call.ArgumentsJson), null, ToolOutcome.Denied, "tool_not_enabled_for_agent", new { message = "This tool is not enabled for this agent." }, leadId, null, null, sw, ct);

        var args = ToolArguments.Validate(def, call.ArgumentsJson);
        if (!args.Ok)
        {
            var outcome = args.TenantArgument ? ToolOutcome.Denied : ToolOutcome.Invalid;
            return await RecordAsync(ctx, def.Name, Summarise(call.ArgumentsJson), null, outcome, args.TenantArgument ? "tenant_argument_forbidden" : "invalid_arguments", new { message = "The arguments were rejected.", errors = args.Errors }, leadId, null, null, sw, ct);
        }
        var canonical = JsonSerializer.Serialize(args.Values, Json);
        var summary = Summarise(canonical);

        // replay: the same logical call for the same customer message returns what it returned before
        string? dedupe = null;
        if (ctx.TriggerMessageId is { } trig && !ctx.Approved)
        {
            dedupe = $"{trig:N}:{def.Name}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16]}";
            var prior = await db.AiToolExecutions.AsNoTracking().Where(e => e.TenantId == tenantId && e.DedupeKey == dedupe).FirstOrDefaultAsync(ct);
            if (prior is not null) return new ToolResult(def.Name, prior.Outcome, prior.Reason, prior.OutputJson ?? "{}", prior.ApprovalRequestId);
        }

        var before = db.ChangeTracker.Entries().Select(e => e.Entity).ToHashSet();
        try
        {
            var output = await handlers.ExecuteAsync(def, args, ctx, ct);
            ApprovalRequest? approval = null;
            if (output.Outcome == ToolOutcome.ApprovalRequired)
            {
                var settings = await handlers.SettingsAsync(ct);
                approval = ApprovalRequest.Create(tenantId, ctx.Agent?.Id, ctx.Conversation.Id, leadId, def.Name, output.ApprovalReason ?? "approval_required", output.ApprovalSummary ?? def.Name, canonical,
                    dedupe ?? Guid.NewGuid().ToString("N"), crm.Now, TimeSpan.FromHours(settings.ApprovalLifetimeHours));
                db.ApprovalRequests.Add(approval);
                crm.Record("Approval.Requested", "ApprovalRequest", approval.Id, $"AI requested approval for {def.Name}: {output.ApprovalReason}.");
            }
            return await RecordAsync(ctx, def.Name, summary, canonical, output.Outcome, output.Reason, output.Data, leadId, approval?.Id, dedupe, sw, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Rollback(before);
            if (ex is DbUpdateException du && TenancyResultFlow.IsDuplicate(du) && dedupe is not null)
            {
                var prior = await db.AiToolExecutions.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.DedupeKey == dedupe, ct);
                if (prior is not null) return new ToolResult(def.Name, prior.Outcome, prior.Reason, prior.OutputJson ?? "{}", prior.ApprovalRequestId);
            }
            logger.LogWarning(ex, "AI tool {Tool} failed", def.Name);
            var failed = ex is DomainException ? ToolOutcome.Invalid : ToolOutcome.Failed;
            try { return await RecordAsync(ctx, def.Name, summary, canonical, failed, ex is DomainException ? "rejected" : "execution_error", new { message = ex is DomainException ? ex.Message : "The action could not be completed." }, leadId, null, dedupe is not null && failed == ToolOutcome.Invalid ? dedupe : null, sw, ct); }
            catch (Exception ex2) when (ex2 is not OperationCanceledException)
            {
                Rollback(before);
                return new ToolResult(def.Name, ToolOutcome.Failed, "execution_error", JsonSerializer.Serialize(new { status = "error", message = "The action could not be completed." }, Json), null);
            }
        }
    }

    /// <summary>Carries out an approved request: same gateway, same validation, but the approval is the permission (the agent's tool list is not consulted: a person decided).</summary>
    public async Task<ToolResult> RunApprovedAsync(ApprovalRequest approval, Conversation conversation, Guid? userId, CancellationToken ct)
    {
        var agent = approval.AgentId is { } aid ? await crm.Db.SalesAgents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == aid && a.TenantId == crm.TenantId, ct) : null;
        var ctx = new ToolCallContext(conversation, null, agent, new HashSet<string>()) { Approval = approval, ExecutedByUserId = userId };
        return await RunAsync(new ToolCall(approval.ToolName, approval.InputJson), ctx, ct);
    }

    private async Task<ToolResult> RecordAsync(ToolCallContext ctx, string tool, string summary, string? input, ToolOutcome outcome, string? reason, object data, Guid? leadId, Guid? approvalId, string? dedupe, Stopwatch sw, CancellationToken ct)
    {
        var db = crm.Db;
        var output = JsonSerializer.Serialize(new { status = new ToolResult(tool, outcome, reason, "", null).Status, tool, result = data }, Json);
        db.AiToolExecutions.Add(AiToolExecution.Record(crm.TenantId, ctx.TriggerMessageId, ctx.Conversation.Id, leadId, ctx.Agent?.Id, tool, summary, input, output, outcome, reason,
            ctx.Approval?.Id ?? approvalId, ctx.ExecutedByUserId, dedupe, (int)sw.ElapsedMilliseconds, crm.Now));
        await db.SaveChangesAsync(ct);
        return new ToolResult(tool, outcome, reason, output, ctx.Approval?.Id ?? approvalId);
    }

    /// <summary>Undoes what a failed handler left in the change tracker without touching entities that were tracked before the call.</summary>
    private void Rollback(HashSet<object> before)
    {
        foreach (var e in crm.Db.ChangeTracker.Entries().ToList())
        {
            if (before.Contains(e.Entity)) { if (e.State == EntityState.Modified) { e.CurrentValues.SetValues(e.OriginalValues); e.State = EntityState.Unchanged; } continue; }
            if (e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted) e.State = EntityState.Detached;
        }
    }

    private static string Summarise(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;
        var s = PromptGuard.Neutralize(json, 500, escapeAngles: false);
        return s.Length > 500 ? s[..500] : s;
    }
}
