using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using LeadPilot.Application.Agents;
using LeadPilot.Application.Ai;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Agents;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>
/// TEST-ONLY stand-in for the Phase 10 services (demo scheduler, trial provisioner, payment request gateway). By default every port is NOT connected, exactly like production in Phase 8; a test switches one on
/// to prove how the tools behave when the real service exists. It never touches a network.
/// </summary>
public sealed class Phase10Doubles : IDemoScheduler, ITrialProvisioner, IPaymentRequestGateway
{
    public bool DemoConnected, TrialConnected, PaymentConnected;
    public List<DemoSlot> Slots { get; } = [];
    public ConcurrentQueue<string> Calls { get; } = new();
    public bool FailBooking;
    /// <summary>When true the REAL Phase 10 services (registered by the product) answer instead of these doubles. Reset() turns it off again.</summary>
    public bool UseReal;

    public void Reset() { DemoConnected = TrialConnected = PaymentConnected = false; Slots.Clear(); Calls.Clear(); FailBooking = false; UseReal = false; }

    public Task<PortResult<IReadOnlyList<DemoSlot>>> AvailabilityAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        Calls.Enqueue("availability");
        return Task.FromResult(DemoConnected ? PortResult<IReadOnlyList<DemoSlot>>.Ok(Slots.Where(s => s.StartUtc >= fromUtc && s.StartUtc <= toUtc).ToList()) : PortResult<IReadOnlyList<DemoSlot>>.NotConnected("Demo scheduling"));
    }

    public Task<PortResult<DemoBooking>> BookAsync(Guid leadId, DemoSlot slot, string? notes, CancellationToken ct)
    {
        Calls.Enqueue("book:" + slot.SlotId);
        if (!DemoConnected) return Task.FromResult(PortResult<DemoBooking>.NotConnected("Demo scheduling"));
        if (FailBooking) return Task.FromResult(PortResult<DemoBooking>.Fail("taken"));
        lock (Slots) Slots.RemoveAll(s => s.SlotId == slot.SlotId);
        return Task.FromResult(PortResult<DemoBooking>.Ok(new DemoBooking("bk-" + slot.SlotId, slot.StartUtc, slot.EndUtc)));
    }

    public Task<PortResult<TrialInfo>> CreateAsync(Guid leadId, string? plan, string? notes, CancellationToken ct)
    {
        Calls.Enqueue("trial");
        return Task.FromResult(TrialConnected ? PortResult<TrialInfo>.Ok(new TrialInfo("tr-" + Guid.NewGuid().ToString("N")[..6], DateTime.UtcNow.AddDays(14))) : PortResult<TrialInfo>.NotConnected("Trial provisioning"));
    }

    public Task<PortResult<PaymentRequestInfo>> CreateAsync(Guid leadId, string plan, decimal amount, string currency, decimal? discountPercent, string? customTerms, CancellationToken ct)
    {
        Calls.Enqueue($"payment:{plan}:{amount}:{discountPercent}");
        return Task.FromResult(PaymentConnected ? PortResult<PaymentRequestInfo>.Ok(new PaymentRequestInfo("pr-1", null)) : PortResult<PaymentRequestInfo>.NotConnected("Payment requests"));
    }
}

public sealed record Step(object[]? Tools = null, string Answer = "", string[]? Cites = null, double Confidence = 0.9, bool NeedsHuman = false, string Reason = "");

public static class AgentScript
{
    public static object Call(string name, object? args = null) => new { name, arguments = args ?? new { } };

    /// <summary>A scripted agent: round n of the tool conversation (n = assistant messages so far) returns step n; the last step repeats.</summary>
    public static Func<AiCompletionRequest, string> Generate(params Step[] steps) => r =>
    {
        var round = r.Messages.Count(m => m.Role == "assistant");
        var s = steps[Math.Min(round, steps.Length - 1)];
        return ScriptedLlm.Json(new { answer = s.Answer, citations = s.Cites ?? [], confidence = s.Confidence, needs_human = s.NeedsHuman, reason = s.Reason, next_action = "Follow up.", tool_calls = s.Tools ?? [] });
    };

    public static Func<AiCompletionRequest, string> Classify(string intent, double confidence = 0.95, bool needsHuman = false, object? extracted = null) => r =>
        ScriptedLlm.Json(new { intent, needs_human = needsHuman, standalone_query = ScriptedLlm.Latest(r), language = "en", confidence, extracted = extracted ?? new { } });
}

/// <summary>A workspace with the AI configured and a sales agent with a confirmed product and plan (500 USD per month), ready for conversations.</summary>
public sealed class AgentWorld : IAsyncDisposable
{
    public AiWorld Ai { get; private init; } = default!;
    public AppFixture App => Ai.App;
    public Guid TenantId => Ai.TenantId;
    public ApiSession Admin => Ai.Admin;
    public Guid AgentId { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid PlanId { get; private set; }
    public AiTestControls Controls => Ai.Controls;

    public static readonly string[] AllTools = [AgentTools.GetProductInformation, AgentTools.GetPricingPlans, AgentTools.GetLeadProfile, AgentTools.UpdateLeadStatus, AgentTools.QualifyLead, AgentTools.CheckDemoAvailability,
        AgentTools.BookDemo, AgentTools.ScheduleFollowUp, AgentTools.CreateTrialAccount, AgentTools.CreatePaymentRequest, AgentTools.AddLeadNote];

    public static async Task<AgentWorld> OpenAsync(AppFixture app, string[]? tools = null, bool autoSend = false, bool confirmCatalog = true, Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configureWorker = null)
    {
        var ai = await AiWorld.OpenAsync(app, configureWorker: configureWorker);
        await ai.UpdateSettingsAsync(r => r with { MinConfidence = 0.6m });
        var w = new AgentWorld { Ai = ai };
        await w.SetupAsync(tools ?? AllTools, autoSend, confirmCatalog);
        return w;
    }

    private async Task SetupAsync(string[] tools, bool autoSend, bool confirmCatalog)
    {
        var res = await Admin.Post("/api/v1/agents", new { name = "Sales agent", instructions = "Help the customer, qualify them and arrange a demo.", enabledTools = tools, offTopicReply = "I can only help with our products.", description = (string?)null });
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        AgentId = (await ApiSession.Json<AgentDto>(res)).Id;
        Assert.Equal(HttpStatusCode.OK, (await Admin.Put($"/api/v1/agents/{AgentId}/questions", new { questions = new object[]
        {
            new { key = "student_count", question = "How many students?", answerType = "Number", required = true },
            new { key = "role", question = "What is your role?", answerType = "Text", required = false },
            new { key = "timeline", question = "When do you want to start?", answerType = "Choice", choices = new[] { "this month", "later" }, required = false },
        } })).StatusCode);
        var p = await Admin.Post("/api/v1/catalog/products", new { name = "School ERP", summary = "School management software.", features = "Attendance tracking\nFee collection\nReport cards", demoProcess = "Demos last 30 minutes.", trialProcess = "Trials last 14 days.", isConfirmed = confirmCatalog, isActive = true });
        Assert.Equal(HttpStatusCode.Created, p.StatusCode);
        ProductId = (await ApiSession.Json<ProductDto>(p)).Id;
        var pl = await Admin.Post($"/api/v1/catalog/products/{ProductId}/plans", new { name = "Standard", price = 500m, currency = "USD", billingPeriod = "per month", details = "Up to 1000 students.", maxDiscountPercent = (decimal?)null, isConfirmed = confirmCatalog, isActive = true });
        Assert.Equal(HttpStatusCode.Created, pl.StatusCode);
        PlanId = (await ApiSession.Json<PlanDto>(pl)).Id;
        if (autoSend) await EnableAutoSendAsync();
    }

    public async Task EnableAutoSendAsync(bool workspace = true, bool agent = true, int maxPerDay = 10)
    {
        if (workspace) Assert.Equal(HttpStatusCode.OK, (await Admin.Put("/api/v1/agents/settings", new { aiRepliesAllowed = true, maxAutoRepliesPerConversationPerDay = maxPerDay, maxTrialsPerLead = 1, approvalLifetimeHours = 72, maxOpenFollowUpsPerLead = 3 })).StatusCode);
        if (agent) Assert.Equal(HttpStatusCode.OK, (await Admin.Put($"/api/v1/agents/{AgentId}/auto-send", new { enabled = true })).StatusCode);
    }

    public Task<ChatThread> ChatAsync(bool linkLead = true, string first = "hello there") => Ai.ChatAsync(linkLead: linkLead, first: first);

    public Task<List<AiToolExecution>> ToolsAsync(Guid? conversationId = null) =>
        App.Db(db => db.AiToolExecutions.IgnoreQueryFilters().Where(t => t.TenantId == TenantId && (conversationId == null || t.ConversationId == conversationId)).OrderBy(t => t.OccurredAtUtc).ToListAsync());

    public Task<List<Message>> OutboundAsync(Guid conversationId) =>
        App.Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.TenantId == TenantId && m.ConversationId == conversationId && m.Direction == MessageDirection.Outbound).OrderBy(m => m.CreatedAtUtc).ToListAsync());

    /// <summary>Runs one tool call through the real gateway in a worker tenant scope, as the orchestrator would.</summary>
    public Task<ToolResult> RunToolAsync(Guid conversationId, string tool, object? args = null, string[]? allowed = null, Guid? trigger = null, bool agent = true) =>
        Ai.InScope(async sp =>
        {
            var crm = sp.GetRequiredService<CrmContext>();
            var conv = await crm.Db.Conversations.FirstAsync(c => c.Id == conversationId && c.TenantId == crm.TenantId);
            var a = agent ? await crm.Db.SalesAgents.AsNoTracking().FirstAsync(x => x.Id == AgentId) : null;
            var names = AgentToolCatalog.AllowedFor(allowed ?? a?.EnabledTools ?? []).Select(t => t.Name).ToHashSet();
            return await sp.GetRequiredService<AgentToolRunner>().RunAsync(new ToolCall(tool, args is string s ? s : JsonSerializer.Serialize(args ?? new { })), new ToolCallContext(conv, trigger, a, names), default);
        });

    public async ValueTask DisposeAsync() => await Ai.DisposeAsync();
}
