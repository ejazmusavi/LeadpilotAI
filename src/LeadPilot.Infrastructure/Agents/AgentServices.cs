using System.Text.Json;
using LeadPilot.Application.Agents;
using LeadPilot.Application.Common;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Common;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Agents;

public sealed class AgentService(CrmContext crm, ILogger<AgentService> logger) : IAgentService
{
    public async Task<IReadOnlyList<AgentListItemDto>> ListAsync(CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var rows = await crm.Db.SalesAgents.AsNoTracking().Where(a => a.TenantId == tenantId).OrderByDescending(a => a.IsDefault).ThenBy(a => a.Name).ToListAsync(ct);
        return rows.Select(a => new AgentListItemDto(a.Id, a.Name, a.IsActive, a.IsDefault, a.IsSample, a.AutoReplyEnabled, a.EnabledTools.Count, a.ChatModel, a.UpdatedAtUtc ?? a.CreatedAtUtc)).ToList();
    }

    public async Task<Result<AgentDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var a = await Load(id, ct);
        return a is null ? Result.Failure<AgentDto>(Error.NotFound("Agent")) : Result.Success(await MapAsync(a, ct));
    }

    public Task<Result<AgentDto>> CreateAsync(SaveAgentRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            if (Check(r) is { } bad) return Result.Failure<AgentDto>(bad);
            var tenantId = crm.TenantId;
            var hasDefault = await crm.Db.SalesAgents.AnyAsync(a => a.TenantId == tenantId && a.IsDefault, ct);
            var a = SalesAgent.Create(tenantId, r.Name, r.Instructions, !hasDefault, false, crm.Now, crm.Actor);
            a.Update(r.Name, r.Description, r.Instructions, r.ChatModel, r.MinConfidence, Tools(r), r.OffTopicReply, crm.Now, crm.Actor);
            crm.Db.SalesAgents.Add(a);
            crm.Record("Agent.Created", "SalesAgent", a.Id, $"Sales agent '{a.Name}' created.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await MapAsync(a, ct));
        }, logger);

    public Task<Result<AgentDto>> UpdateAsync(Guid id, SaveAgentRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var a = await Load(id, ct, true); if (a is null) return Result.Failure<AgentDto>(Error.NotFound("Agent"));
            if (Check(r) is { } bad) return Result.Failure<AgentDto>(bad);
            if (r.Version is { } v && v != a.Version) return Result.Failure<AgentDto>(Error.Conflict("The agent was changed by someone else. Reload it and try again."));
            a.Update(r.Name, r.Description, r.Instructions, r.ChatModel, r.MinConfidence, Tools(r), r.OffTopicReply, crm.Now, crm.Actor);
            crm.Record("Agent.Updated", "SalesAgent", id, $"Sales agent '{a.Name}' updated (tools: {string.Join(", ", a.EnabledTools)}).");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await MapAsync(a, ct));
        }, logger);

    public Task<Result<AgentDto>> SaveQuestionsAsync(Guid id, SaveQuestionsRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var a = await Load(id, ct, true); if (a is null) return Result.Failure<AgentDto>(Error.NotFound("Agent"));
            if (r.Questions.Count > 20) return Result.Failure<AgentDto>(Error.Validation("An agent can have at most 20 qualification questions."));
            var tenantId = crm.TenantId; var order = 0; var fresh = new List<AgentQualificationQuestion>();
            foreach (var q in r.Questions) fresh.Add(AgentQualificationQuestion.Create(tenantId, id, q.Key, q.Question, q.AnswerType, q.Choices, q.Required, ++order));
            if (fresh.Select(q => q.Key).Distinct().Count() != fresh.Count) return Result.Failure<AgentDto>(Error.Validation("Each question needs its own key."));
            crm.Db.AgentQualificationQuestions.RemoveRange(await crm.Db.AgentQualificationQuestions.Where(q => q.TenantId == tenantId && q.AgentId == id).ToListAsync(ct));
            await crm.Db.SaveChangesAsync(ct);   // the unique (agent, key) index needs the old rows gone first
            crm.Db.AgentQualificationQuestions.AddRange(fresh);
            crm.Record("Agent.QuestionsUpdated", "SalesAgent", id, $"Qualification questions of '{a.Name}' updated ({fresh.Count}).");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await MapAsync(a, ct));
        }, logger);

    public Task<Result<AgentDto>> SetActiveAsync(Guid id, bool active, CancellationToken ct = default) =>
        Mutate(id, a => { a.SetActive(active, crm.Now, crm.Actor); crm.Record("Agent.Updated", "SalesAgent", id, $"Sales agent '{a.Name}' switched {(active ? "on" : "off")}{(active ? "" : " (default and automatic replies off)")}."); }, ct);

    public async Task<Result<AgentDto>> SetDefaultAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        foreach (var other in await crm.Db.SalesAgents.Where(x => x.TenantId == tenantId && x.IsDefault && x.Id != id).ToListAsync(ct)) other.SetDefault(false, crm.Now, crm.Actor);
        return await Mutate(id, a => { a.SetDefault(true, crm.Now, crm.Actor); crm.Record("Agent.Updated", "SalesAgent", id, $"Sales agent '{a.Name}' is now the default."); }, ct);
    }

    public async Task<Result<AgentDto>> SetAutoSendAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        if (enabled)
        {
            var tenantId = crm.TenantId;
            var s = await crm.Db.AgentTenantSettings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
            if (s is not { AiRepliesAllowed: true }) return Result.Failure<AgentDto>(Error.Validation("Automatic AI replies are not allowed for this workspace yet. Allow them in the agent settings first."));
        }
        return await Mutate(id, a => { a.SetAutoReply(enabled, crm.Now, crm.Actor); crm.Record("Agent.AutoReply", "SalesAgent", id, $"Automatic AI replies of '{a.Name}' switched {(enabled ? "ON" : "off")}."); }, ct);
    }

    public Task<Result<AgentDto>> CreateSampleAsync(CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId; var now = crm.Now;
            if (await crm.Db.SalesAgents.AnyAsync(a => a.TenantId == tenantId, ct)) return Result.Failure<AgentDto>(Error.Conflict("This workspace already has an agent."));
            var agent = SalesAgent.Create(tenantId, "School ERP sales assistant (SAMPLE)", SampleData.Instructions, true, true, now, crm.Actor);
            agent.Update(agent.Name, "SAMPLE configuration: placeholders only. Replace the instructions and fill in the catalog before relying on it.", SampleData.Instructions, null, null,
                [AgentTools.GetProductInformation, AgentTools.GetPricingPlans, AgentTools.GetLeadProfile, AgentTools.QualifyLead, AgentTools.UpdateLeadStatus, AgentTools.CheckDemoAvailability, AgentTools.BookDemo, AgentTools.ScheduleFollowUp, AgentTools.AddLeadNote],
                agent.OffTopicReply, now, crm.Actor, keepSampleFlag: true);
            crm.Db.SalesAgents.Add(agent);
            var order = 0;
            foreach (var q in SampleData.Questions) crm.Db.AgentQualificationQuestions.Add(AgentQualificationQuestion.Create(tenantId, agent.Id, q.Key, q.Question, q.Type, q.Choices, q.Required, ++order));
            var product = CatalogProduct.Create(tenantId, "School ERP (SAMPLE - not confirmed)", "[PLACEHOLDER] Describe the product here.", SampleData.FeatureLines, "[PLACEHOLDER] Describe how a demo is arranged.", "[PLACEHOLDER] Describe how a trial works.", false, true, now, crm.Actor);
            crm.Db.CatalogProducts.Add(product);
            crm.Db.PricingPlans.Add(PricingPlan.Create(tenantId, product.Id, "[PLACEHOLDER] Plan name", null, "USD", null, "[PLACEHOLDER] Price, billing period and what is included.", null, false, true, now, crm.Actor));
            crm.Record("Agent.SampleCreated", "SalesAgent", agent.Id, "Sample School ERP agent and placeholder catalog created.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await MapAsync(agent, ct));
        }, logger);

    public async Task<AgentSettingsDto> GetSettingsAsync(CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var s = await crm.Db.AgentTenantSettings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId, ct) ?? AgentTenantSettings.Create(tenantId, crm.Now);
        return new AgentSettingsDto(s.AiRepliesAllowed, s.MaxAutoRepliesPerConversationPerDay, s.MaxTrialsPerLead, s.ApprovalLifetimeHours, s.MaxOpenFollowUpsPerLead);
    }

    public Task<Result<AgentSettingsDto>> UpdateSettingsAsync(SaveAgentSettingsRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var s = await crm.Db.AgentTenantSettings.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
            if (s is null) { s = AgentTenantSettings.Create(tenantId, crm.Now); crm.Db.AgentTenantSettings.Add(s); }
            var was = s.AiRepliesAllowed;
            s.Configure(r.AiRepliesAllowed, r.MaxAutoRepliesPerConversationPerDay, r.MaxTrialsPerLead, r.ApprovalLifetimeHours, r.MaxOpenFollowUpsPerLead, crm.Now, crm.Actor);
            crm.Record("Agent.SettingsUpdated", "AgentTenantSettings", s.Id, $"Agent settings updated; automatic AI replies {(r.AiRepliesAllowed ? "ALLOWED" : "not allowed")}{(was != r.AiRepliesAllowed ? " (changed)" : "")}.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await GetSettingsAsync(ct));
        }, logger);

    public IReadOnlyList<ToolInfoDto> Tools() =>
        AgentToolCatalog.All.Select(t => new ToolInfoDto(t.Name, t.Description, t.ReadOnly, t.Safety, t.Approval.ToString(), t.Params.Select(p => p.Name + (p.Required ? "" : "?")).ToList())).ToList();

    // ---------------------------------------------------------------

    private static IEnumerable<string> Tools(SaveAgentRequest r) => r.EnabledTools.Where(t => AgentToolCatalog.Find(t) is { Safety: false });

    private static Error? Check(SaveAgentRequest r)
    {
        var unknown = r.EnabledTools.Where(t => AgentToolCatalog.Find(t) is null).ToList();
        return unknown.Count == 0 ? null : Error.Validation("Unknown tool(s): " + string.Join(", ", unknown.Take(5)));
    }

    private async Task<Result<AgentDto>> Mutate(Guid id, Action<SalesAgent> change, CancellationToken ct) =>
        await CrmFlow.GuardAsync(async () =>
        {
            var a = await Load(id, ct, true); if (a is null) return Result.Failure<AgentDto>(Error.NotFound("Agent"));
            change(a); await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await MapAsync(a, ct));
        }, logger);

    private Task<SalesAgent?> Load(Guid id, CancellationToken ct, bool tracked = false)
    {
        var tenantId = crm.TenantId; var q = crm.Db.SalesAgents.Where(a => a.Id == id && a.TenantId == tenantId);
        return (tracked ? q : q.AsNoTracking()).FirstOrDefaultAsync(ct);
    }

    private async Task<AgentDto> MapAsync(SalesAgent a, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var questions = await crm.Db.AgentQualificationQuestions.AsNoTracking().Where(q => q.TenantId == tenantId && q.AgentId == a.Id).OrderBy(q => q.SortOrder).ToListAsync(ct);
        var allowed = (await crm.Db.AgentTenantSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct))?.AiRepliesAllowed ?? false;
        return new AgentDto(a.Id, a.Name, a.Description, a.Instructions, a.IsActive, a.IsDefault, a.IsSample, a.ChatModel, a.MinConfidence, a.EnabledTools, a.AutoReplyEnabled, a.OffTopicReply,
            questions.Select(q => new QuestionDto(q.Id, q.Key, q.Question, q.AnswerType, q.Choices, q.Required, q.SortOrder)).ToList(), a.Version, allowed);
    }
}

/// <summary>The sample School ERP configuration. It contains NO product facts: every fact field is a placeholder and everything is unconfirmed, so the AI answers "I don't know" until the owner fills it in.</summary>
internal static class SampleData
{
    public const string Instructions = """
        You are the sales assistant of a business that sells school management software (School ERP). You talk to school owners, principals and administrators on WhatsApp.
        Goals: welcome the person, understand their school and their problems, answer product questions, qualify them with the qualification questions (one or two per message, never an interrogation),
        explain relevant benefits, answer objections politely, and arrange a demo or a follow-up with the team.
        You know nothing about the product except what the tools return. Always call GetProductInformation or GetPricingPlans before you state a feature or a price. If a tool says the information is
        unknown or not confirmed, tell the person honestly that you do not have it and that a colleague will follow up. Never invent features, prices, discounts, dates or integrations.
        Areas the person may ask about: student admission, fee management, attendance, examinations and results, timetable, parent communication, school administration, pricing and plans, the demo process and the trial process.
        Hand the conversation to a colleague when the person asks for one, is upset, or raises money or legal questions.
        """;

    public const string FeatureLines = "[PLACEHOLDER] Student admission\n[PLACEHOLDER] Fee management\n[PLACEHOLDER] Attendance\n[PLACEHOLDER] Examination and results\n[PLACEHOLDER] Timetable\n[PLACEHOLDER] Parent communication\n[PLACEHOLDER] School administration";

    public sealed record Q(string Key, string Question, AnswerType Type, string[]? Choices, bool Required);

    public static readonly Q[] Questions =
    [
        new("role", "What is your role at the school?", AnswerType.Text, null, false),
        new("student_count", "Roughly how many students does your school have?", AnswerType.Number, null, true),
        new("current_system", "How do you manage admissions, fees and attendance today?", AnswerType.Text, null, true),
        new("main_problem", "Which area causes you the most trouble at the moment?", AnswerType.Text, null, false),
        new("timeline", "When would you like to start using a new system?", AnswerType.Choice, ["this month", "in 1-3 months", "later", "just exploring"], false),
    ];
}

public sealed class CatalogService(CrmContext crm, ILogger<CatalogService> logger) : ICatalogService
{
    public async Task<IReadOnlyList<ProductListItemDto>> ListAsync(CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var rows = await crm.Db.CatalogProducts.AsNoTracking().Where(p => p.TenantId == tenantId).OrderBy(p => p.Name).ToListAsync(ct);
        var counts = await crm.Db.PricingPlans.AsNoTracking().Where(p => p.TenantId == tenantId).GroupBy(p => p.ProductId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.N, ct);
        return rows.Select(p => new ProductListItemDto(p.Id, p.Name, p.Summary, p.IsActive, p.IsConfirmed, p.IsSample, counts.GetValueOrDefault(p.Id), p.UpdatedAtUtc ?? p.CreatedAtUtc)).ToList();
    }

    public async Task<Result<ProductDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var p = await crm.Db.CatalogProducts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        return p is null ? Result.Failure<ProductDto>(Error.NotFound("Product")) : Result.Success(await MapAsync(p, ct));
    }

    public Task<Result<ProductDto>> CreateAsync(SaveProductRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var p = CatalogProduct.Create(crm.TenantId, r.Name, r.Summary, r.Features, r.DemoProcess, r.TrialProcess, r.IsConfirmed, false, crm.Now, crm.Actor);
            if (!r.IsActive) p.Update(r.Name, r.Summary, r.Features, r.DemoProcess, r.TrialProcess, r.IsConfirmed, false, crm.Now, crm.Actor);
            crm.Db.CatalogProducts.Add(p);
            crm.Record("Catalog.ProductCreated", "CatalogProduct", p.Id, $"Product '{p.Name}' created ({(p.IsConfirmed ? "confirmed" : "not confirmed")}).");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await MapAsync(p, ct));
        }, logger);

    public Task<Result<ProductDto>> UpdateAsync(Guid id, SaveProductRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var p = await crm.Db.CatalogProducts.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            if (p is null) return Result.Failure<ProductDto>(Error.NotFound("Product"));
            if (r.Version is { } v && v != p.Version) return Result.Failure<ProductDto>(Error.Conflict("The product was changed by someone else. Reload it and try again."));
            var was = p.IsConfirmed;
            p.Update(r.Name, r.Summary, r.Features, r.DemoProcess, r.TrialProcess, r.IsConfirmed, r.IsActive, crm.Now, crm.Actor);
            crm.Record("Catalog.ProductUpdated", "CatalogProduct", id, $"Product '{p.Name}' updated{(was != p.IsConfirmed ? (p.IsConfirmed ? " and CONFIRMED (the AI may now state it as fact)" : " and un-confirmed") : "")}.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await MapAsync(p, ct));
        }, logger);

    public Task<Result<PlanDto>> AddPlanAsync(Guid productId, SavePlanRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            if (!await crm.Db.CatalogProducts.AnyAsync(x => x.Id == productId && x.TenantId == tenantId, ct)) return Result.Failure<PlanDto>(Error.NotFound("Product"));
            var p = PricingPlan.Create(tenantId, productId, r.Name, r.Price, r.Currency, r.BillingPeriod, r.Details, r.MaxDiscountPercent, r.IsConfirmed, false, crm.Now, crm.Actor);
            if (!r.IsActive) p.Update(r.Name, r.Price, r.Currency, r.BillingPeriod, r.Details, r.MaxDiscountPercent, r.IsConfirmed, false, crm.Now, crm.Actor);
            crm.Db.PricingPlans.Add(p);
            crm.Record("Catalog.PlanCreated", "PricingPlan", p.Id, $"Pricing plan '{p.Name}' created ({(p.IsConfirmed ? "confirmed" : "not confirmed")}).");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(Map(p));
        }, logger);

    public Task<Result<PlanDto>> UpdatePlanAsync(Guid planId, SavePlanRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var p = await crm.Db.PricingPlans.FirstOrDefaultAsync(x => x.Id == planId && x.TenantId == tenantId, ct);
            if (p is null) return Result.Failure<PlanDto>(Error.NotFound("Pricing plan"));
            p.Update(r.Name, r.Price, r.Currency, r.BillingPeriod, r.Details, r.MaxDiscountPercent, r.IsConfirmed, r.IsActive, crm.Now, crm.Actor);
            crm.Record("Catalog.PlanUpdated", "PricingPlan", planId, $"Pricing plan '{p.Name}' updated.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(Map(p));
        }, logger);

    public Task<Result> DeletePlanAsync(Guid planId, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var p = await crm.Db.PricingPlans.FirstOrDefaultAsync(x => x.Id == planId && x.TenantId == tenantId, ct);
            if (p is null) return Result.Failure(Error.NotFound("Pricing plan"));
            crm.Db.PricingPlans.Remove(p);
            crm.Record("Catalog.PlanDeleted", "PricingPlan", planId, $"Pricing plan '{p.Name}' deleted.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success();
        }, logger);

    private async Task<ProductDto> MapAsync(CatalogProduct p, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var plans = await crm.Db.PricingPlans.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProductId == p.Id).OrderBy(x => x.Price).ThenBy(x => x.Name).ToListAsync(ct);
        return new ProductDto(p.Id, p.Name, p.Summary, p.Features, p.DemoProcess, p.TrialProcess, p.IsActive, p.IsConfirmed, p.IsSample, p.Version, plans.Select(Map).ToList());
    }

    private static PlanDto Map(PricingPlan p) => new(p.Id, p.ProductId, p.Name, p.Price, p.Currency, p.BillingPeriod, p.Details, p.MaxDiscountPercent, p.IsActive, p.IsConfirmed, p.IsSample);
}

public sealed class ApprovalService(CrmContext crm, AgentToolRunner runner, ILogger<ApprovalService> logger) : IApprovalService
{
    public async Task<PagedResult<ApprovalDto>> ListAsync(ApprovalQuery q, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(q.Page, q.PageSize);
        await ExpireDueAsync(ct);
        var query = crm.Db.ApprovalRequests.AsNoTracking().Where(a => a.TenantId == tenantId);
        if (q.Status is { } s) query = query.Where(a => a.Status == s);
        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(a => a.RequestedAtUtc).ThenBy(a => a.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<ApprovalDto>(await MapAsync(rows, ct), page, size, total);
    }

    public async Task<Result<ApprovalDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var a = await crm.Db.ApprovalRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        return a is null ? Result.Failure<ApprovalDto>(Error.NotFound("Approval request")) : Result.Success((await MapAsync([a], ct))[0]);
    }

    public async Task<int> PendingCountAsync(CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var now = crm.Now;
        return await crm.Db.ApprovalRequests.CountAsync(a => a.TenantId == tenantId && a.Status == ApprovalStatus.Pending && a.ExpiresAtUtc > now, ct);
    }

    public async Task<Result<ApprovalDto>> ApproveAsync(Guid id, DecideApprovalRequest r, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var now = crm.Now; var note = r.Note is { Length: > 300 } ? r.Note[..300] : r.Note;
        var existing = await crm.Db.ApprovalRequests.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.TenantId == tenantId, ct);
        if (existing is null) return Result.Failure<ApprovalDto>(Error.NotFound("Approval request"));
        // atomic claim: of two people clicking at once, exactly one approves (and runs the action once)
        var claimed = await crm.Db.ApprovalRequests.Where(a => a.Id == id && a.TenantId == tenantId && a.Status == ApprovalStatus.Pending && a.ExpiresAtUtc > now)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, ApprovalStatus.Approved).SetProperty(a => a.DecidedByUserId, crm.UserId).SetProperty(a => a.DecidedAtUtc, now).SetProperty(a => a.DecisionNote, note), ct);
        if (claimed == 0)
        {
            await ExpireDueAsync(ct);
            return Result.Failure<ApprovalDto>(Error.Conflict(existing.Status == ApprovalStatus.Pending ? "This request expired. Ask the customer again." : "This request was already decided."));
        }
        var approval = await crm.Db.ApprovalRequests.FirstAsync(a => a.Id == id && a.TenantId == tenantId, ct);
        crm.Record("Approval.Approved", "ApprovalRequest", id, $"Approved {approval.ToolName}: {approval.Summary}");
        var conv = await crm.Db.Conversations.FirstOrDefaultAsync(c => c.Id == approval.ConversationId && c.TenantId == tenantId, ct);
        if (conv is null) approval.RecordExecution(ExecutionState.Failed, "The conversation no longer exists.");
        else
        {
            var result = await runner.RunApprovedAsync(approval, conv, crm.UserId, ct);
            approval.RecordExecution(result.Outcome switch { ToolOutcome.Executed => ExecutionState.Executed, ToolOutcome.NotAvailable => ExecutionState.NotAvailable, _ => ExecutionState.Failed },
                result.Outcome == ToolOutcome.Executed ? null : $"{result.Outcome}: {result.Reason}");
        }
        await crm.Db.SaveChangesAsync(ct);
        return Result.Success((await MapAsync([approval], ct))[0]);
    }

    public async Task<Result<ApprovalDto>> RejectAsync(Guid id, DecideApprovalRequest r, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var now = crm.Now; var note = r.Note is { Length: > 300 } ? r.Note[..300] : r.Note;
        if (!await crm.Db.ApprovalRequests.AnyAsync(a => a.Id == id && a.TenantId == tenantId, ct)) return Result.Failure<ApprovalDto>(Error.NotFound("Approval request"));
        var claimed = await crm.Db.ApprovalRequests.Where(a => a.Id == id && a.TenantId == tenantId && a.Status == ApprovalStatus.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, ApprovalStatus.Rejected).SetProperty(a => a.DecidedByUserId, crm.UserId).SetProperty(a => a.DecidedAtUtc, now).SetProperty(a => a.DecisionNote, note), ct);
        if (claimed == 0) return Result.Failure<ApprovalDto>(Error.Conflict("This request was already decided."));
        var approval = await crm.Db.ApprovalRequests.AsNoTracking().FirstAsync(a => a.Id == id && a.TenantId == tenantId, ct);
        crm.Record("Approval.Rejected", "ApprovalRequest", id, $"Rejected {approval.ToolName}: {approval.Summary}");
        await crm.Db.SaveChangesAsync(ct);
        logger.LogInformation("Approval {Id} rejected", id);
        return Result.Success((await MapAsync([approval], ct))[0]);
    }

    private async Task ExpireDueAsync(CancellationToken ct)
    {
        var tenantId = crm.TenantId; var now = crm.Now;
        await crm.Db.ApprovalRequests.Where(a => a.TenantId == tenantId && a.Status == ApprovalStatus.Pending && a.ExpiresAtUtc <= now).ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, ApprovalStatus.Expired), ct);
    }

    private async Task<List<ApprovalDto>> MapAsync(IReadOnlyList<ApprovalRequest> rows, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var convIds = rows.Select(r => r.ConversationId).Distinct().ToList(); var leadIds = rows.Where(r => r.LeadId != null).Select(r => r.LeadId!.Value).Distinct().ToList();
        var convs = await crm.Db.Conversations.AsNoTracking().Where(c => c.TenantId == tenantId && convIds.Contains(c.Id)).Select(c => new { c.Id, c.PeerName, c.PeerPhoneE164 }).ToDictionaryAsync(c => c.Id, ct);
        var leads = await crm.Db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && leadIds.Contains(l.Id)).Select(l => new { l.Id, l.Name }).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var users = await crm.UserNamesAsync(rows.Select(r => r.DecidedByUserId), ct);
        return rows.Select(r => new ApprovalDto(r.Id, r.ToolName, r.Reason, r.Summary, r.IsExpired(crm.Now) ? ApprovalStatus.Expired : r.Status, r.Execution, r.ExecutionNote, r.ConversationId,
            convs.TryGetValue(r.ConversationId, out var c) ? c.PeerName ?? c.PeerPhoneE164 : null, r.LeadId, r.LeadId is { } l ? leads.GetValueOrDefault(l) : null, r.RequestedAtUtc, r.ExpiresAtUtc,
            r.DecidedByUserId is { } u ? users.GetValueOrDefault(u) : null, r.DecidedAtUtc, r.DecisionNote, r.InputJson)).ToList();
    }
}

public sealed class ToolExecutionService(CrmContext crm) : IToolExecutionService
{
    public async Task<PagedResult<ToolExecutionDto>> ListAsync(ToolExecutionQuery q, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(q.Page, q.PageSize);
        var query = crm.Db.AiToolExecutions.AsNoTracking().Where(e => e.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(q.Tool)) { var t = q.Tool.Trim(); query = query.Where(e => e.ToolName == t); }
        if (q.Outcome is { } o) query = query.Where(e => e.Outcome == o);
        if (q.ConversationId is { } c) query = query.Where(e => e.ConversationId == c);
        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(e => e.OccurredAtUtc).ThenBy(e => e.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var users = await crm.UserNamesAsync(rows.Select(r => r.ExecutedByUserId), ct);
        return new PagedResult<ToolExecutionDto>(rows.Select(e => new ToolExecutionDto(e.Id, e.ToolName, e.Outcome, e.Reason, e.ArgumentsSummary, e.InputJson, e.OutputJson, e.ConversationId, e.LeadId, e.ApprovalRequestId,
            e.ExecutedByUserId is { } u ? users.GetValueOrDefault(u) : null, e.DurationMs, e.OccurredAtUtc)).ToList(), page, size, total);
    }
}

public sealed class FollowUpService(CrmContext crm, ILogger<FollowUpService> logger) : IFollowUpService
{
    public async Task<PagedResult<FollowUpDto>> ListAsync(FollowUpQuery q, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(q.Page, q.PageSize);
        var query = crm.Db.FollowUps.AsNoTracking().Where(f => f.TenantId == tenantId);
        if (q.Status is { } s) query = query.Where(f => f.Status == s);
        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderBy(f => f.DueAtUtc).ThenBy(f => f.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<FollowUpDto>(await MapAsync(rows, ct), page, size, total);
    }

    public Task<Result<FollowUpDto>> CloseAsync(Guid id, bool done, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var f = await crm.Db.FollowUps.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            if (f is null) return Result.Failure<FollowUpDto>(Error.NotFound("Follow-up"));
            f.Close(done ? FollowUpStatus.Done : FollowUpStatus.Cancelled, crm.Now);
            crm.Record("FollowUp.Closed", "FollowUp", id, $"Follow-up marked {(done ? "done" : "cancelled")}.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success((await MapAsync([f], ct))[0]);
        }, logger);

    private async Task<List<FollowUpDto>> MapAsync(IReadOnlyList<FollowUp> rows, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var leadIds = rows.Select(r => r.LeadId).Distinct().ToList();
        var leads = await crm.Db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && leadIds.Contains(l.Id)).Select(l => new { l.Id, l.Name }).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var users = await crm.UserNamesAsync(rows.Select(r => r.AssignedUserId), ct);
        return rows.Select(f => new FollowUpDto(f.Id, f.LeadId, leads.GetValueOrDefault(f.LeadId) ?? "(removed)", f.ConversationId, f.AssignedUserId is { } u ? users.GetValueOrDefault(u) : null, f.DueAtUtc, f.Note, f.Status, f.CreatedByKind, f.CreatedAtUtc,
            f.Status == FollowUpStatus.Open && f.DueAtUtc < crm.Now)).ToList();
    }
}
