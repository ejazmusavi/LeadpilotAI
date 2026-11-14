using System.Collections.Concurrent;
using System.Net;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Messaging;
using LeadPilot.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>TEST-ONLY scheduler: records the Hangfire jobs the workflow module would enqueue; the tests run executions deterministically.</summary>
public sealed class RecordingWorkflowScheduler : IWorkflowJobScheduler
{
    public sealed record Job(Guid TenantId, Guid ExecutionId, TimeSpan? Delay);
    public ConcurrentQueue<Job> Jobs { get; } = new();
    public string EnqueueRun(Guid tenantId, Guid executionId, TimeSpan? delay = null) { Jobs.Enqueue(new(tenantId, executionId, delay)); return "wf-" + Jobs.Count; }
    public IEnumerable<Job> For(Guid executionId) => Jobs.Where(j => j.ExecutionId == executionId);
}

/// <summary>Step builders for workflow definitions sent through the real API.</summary>
public static class Wf
{
    public static object Trigger(string eventType, object? condition = null, int? inactiveDays = null) => new { eventType, condition, inactiveDays };
    public static object Step(string key, string type, object? config = null, object? runIf = null, string onFailure = "Retry", int maxRetries = 2, string? name = null) =>
        new { key, type, name = name ?? key, config = config ?? new { }, condition = runIf, onFailure, maxRetries };
    public static object Definition(object[] triggers, object[] steps, string reEntry = "AfterCompletion", int cooldownHours = 0, int maxStarts = 0) =>
        new { triggers, steps, reEntry, cooldownHours, maxStartsPerLeadPer30Days = maxStarts };
    public static object Cmp(string field, string op, object? value = null) => new { field, op, value };
    public static object Wait(string key, int minutes) => Step(key, "Wait", new { minutes });
}

/// <summary>One tenant with a connected fake-WAHA account, an approved template and a worker harness: everything a workflow test needs, driven through the real API, the real consumers and the real engine.</summary>
public sealed class WorkflowWorld : IAsyncDisposable
{
    public const string Body = "Hello {{first_name|there}}, we help schools in {{city|your city}}.";
    private TimeSpan _clockAtOpen;
    public AppFixture App { get; private init; } = default!;
    public Guid TenantId { get; private init; }
    public ApiSession Admin { get; private init; } = default!;
    public TestUser AdminUser { get; private init; } = default!;
    public TestAccount Account { get; private init; } = default!;
    public Guid TemplateId { get; private set; }
    public WorkerHarness Worker { get; private init; } = default!;

    public static async Task<WorkflowWorld> OpenAsync(AppFixture app, bool freshTenant = false)
    {
        Guid tenantId; ApiSession admin; TestUser user; TestAccount account;
        if (freshTenant)
        {
            var (tenant, adm, _) = await app.World.NewTenantAsync("wf");
            tenantId = tenant.Id; user = adm; admin = await ApiSession.LoginAsync(app, adm.Email, adm.Password);
            account = await MessagingKit.CreateAccountAsync(app, admin, tenantId, hourly: 120, daily: 1000, delay: 10);
        }
        else
        {
            tenantId = app.World.A.Id; user = app.World.AAdmin; admin = await ApiSession.LoginAsync(app, user.Email, user.Password);
            account = await MessagingKit.SharedAccountAAsync(app);
        }
        app.Waha.ClearFaults(); app.Waha.Working(account.Session); app.Ai.Reset();
        await MessagingKit.PrepareAccountAsync(app, account.Id);
        var w = new WorkflowWorld { App = app, TenantId = tenantId, Admin = admin, AdminUser = user, Account = account, Worker = MessagingKit.BuildWorker(app), _clockAtOpen = app.Clock.Offset };
        await w.NewTemplateAsync();
        await w.SettingsAsync(kill: false);   // every test starts with the workspace kill switch off and generous limits
        return w;
    }

    public async Task<Guid> NewTemplateAsync(string body = Body)
    {
        var dto = await Crm.Ok<TemplateDto>(await Admin.Post("/api/v1/message-templates", new { name = "Follow-up " + Guid.NewGuid().ToString("N")[..8], body, category = "Marketing" }));
        return TemplateId = dto.Id;
    }

    public async Task<WorkflowSettingsDto> SettingsAsync(bool kill, int concurrent = 50, int monthly = 5000, int daily = 500)
    {
        await Crm.Ok<WorkflowSettingsDto>(await Admin.Put("/api/v1/workflow-settings/limits", new { maxConcurrentExecutions = concurrent, monthlyExecutionLimit = monthly, dailyMessageLimit = daily }));
        return await Crm.Ok<WorkflowSettingsDto>(await Admin.Put("/api/v1/workflow-settings/kill-switch", new { on = kill, reason = kill ? "test" : null }));
    }

    // ---------- data ----------

    public async Task<TestContact> ContactAsync(bool consent = true, string? city = "Lahore")
    {
        var lead = await Crm.CreateLead(Admin, city: city, phone: "+9230" + Random.Shared.Next(10_000_000, 99_999_999));
        var contact = (await Crm.Ok<List<LeadPilot.Application.Crm.ContactDto>>(await Admin.Get($"/api/v1/leads/{lead.Id}/contacts"))).Single();
        if (consent) await MessagingKit.GrantConsentAsync(Admin, contact.Id);
        return new TestContact(lead.Id, contact.Id, contact.Phone!);
    }

    // ---------- workflows ----------

    public object SendStep(string key = "send", string onFailure = "Retry", int retries = 2, object? runIf = null) =>
        Wf.Step(key, "SendMessage", new { templateId = TemplateId, channelAccountId = Account.Id }, runIf, onFailure, retries);

    public async Task<WorkflowDto> CreateAsync(object definition, string? name = null, ApiSession? s = null) =>
        await Crm.Ok<WorkflowDto>(await (s ?? Admin).Post("/api/v1/workflows", new { name = name ?? "Workflow " + Guid.NewGuid().ToString("N")[..8], description = "test", definition }));

    public async Task<WorkflowDto> CreateActiveAsync(object definition, string? name = null)
    {
        var wf = await CreateAsync(definition, name);
        return await Crm.Ok<WorkflowDto>(await Admin.Post($"/api/v1/workflows/{wf.Id}/versions/1/activate"));
    }

    public async Task<WorkflowDto> GetAsync(Guid id, ApiSession? s = null) => await Crm.Ok<WorkflowDto>(await (s ?? Admin).Get($"/api/v1/workflows/{id}"));

    // ---------- events and runs ----------

    /// <summary>Delivers every pending outbox event through the real consumers (workflow triggers start executions).</summary>
    public Task PumpAsync() => Worker.PumpAsync();

    public async Task<List<ExecutionListItemDto>> ExecutionsAsync(Guid? workflowId = null, Guid? leadId = null, ApiSession? s = null)
    {
        var q = "?pageSize=100" + (workflowId is { } w ? $"&workflowId={w}" : "") + (leadId is { } l ? $"&leadId={l}" : "");
        return (await Crm.Ok<LeadPilot.Application.Common.PagedResult<ExecutionListItemDto>>(await (s ?? Admin).Get("/api/v1/workflow-executions" + q))).Items.ToList();
    }

    public async Task<ExecutionDto> ExecutionAsync(Guid id, ApiSession? s = null) => await Crm.Ok<ExecutionDto>(await (s ?? Admin).Get($"/api/v1/workflow-executions/{id}"));

    public Task RunAsync(Guid executionId, Guid? tenant = null) => Worker.Scopes.RunAsync(tenant ?? TenantId, (sp, ct) => sp.GetRequiredService<IWorkflowEngine>().RunAsync(executionId, ct));
    public Task SweepAsync(Guid? tenant = null) => Worker.Scopes.RunAsync(tenant ?? TenantId, (sp, ct) => sp.GetRequiredService<IWorkflowEngine>().SweepAsync(ct));
    public async Task<int> InactivitySweepAsync() { var n = 0; await Worker.Scopes.RunAsync(TenantId, async (sp, ct) => n = await sp.GetRequiredService<IWorkflowTriggerService>().RunInactivitySweepAsync(ct)); return n; }

    /// <summary>Runs the execution, then keeps running while it is waiting and the clock can be advanced past its next run (delays, retries). Returns the final state.</summary>
    public async Task<ExecutionDto> RunToEndAsync(Guid id, int maxRounds = 20)
    {
        ExecutionDto e = await ExecutionAsync(id);
        for (var i = 0; i < maxRounds; i++)
        {
            await RunAsync(id);
            e = await ExecutionAsync(id);
            if (e.Summary.Status is ExecutionStatus.Completed or ExecutionStatus.Failed or ExecutionStatus.Cancelled or ExecutionStatus.Stopped) return e;
            if (e.Summary.Status == ExecutionStatus.Waiting) App.Clock.Offset = App.Clock.Offset + (e.Summary.NextRunAtUtc - (DateTime.UtcNow + App.Clock.Offset)) + TimeSpan.FromSeconds(2);
        }
        return e;
    }

    public async Task<ExecutionListItemDto> StartedFor(Guid workflowId, TestContact c)
    {
        var all = await ExecutionsAsync(workflowId, c.LeadId);
        return all.Single();
    }

    public IEnumerable<FakeWahaServer.Sent> SentTo(TestContact c) => App.Waha.SentTo(c.Chat).Where(m => m.Session == Account.Session);

    /// <summary>Runs the Phase 5 send job for the queued messages of the contact, like the worker would.</summary>
    public async Task SendQueuedAsync(TestContact c)
    {
        var ids = await App.Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.TenantId == TenantId && m.ContactId == c.ContactId && m.Direction == MessageDirection.Outbound && m.Status == MessageStatus.Queued).Select(m => m.Id).ToListAsync());
        foreach (var id in ids) { await Worker.RunSendAsync(TenantId, id); App.Clock.Offset += TimeSpan.FromSeconds(31); }
    }

    public Task<List<LeadPilot.Domain.Messaging.Message>> MessagesOfAsync(TestContact c) =>
        App.Db(db => db.Messages.IgnoreQueryFilters().AsNoTracking().Where(m => m.TenantId == TenantId && m.ContactId == c.ContactId && m.Direction == MessageDirection.Outbound).ToListAsync());

    public async ValueTask DisposeAsync()
    {
        App.Clock.Offset = _clockAtOpen;
        await Worker.DisposeAsync();
    }
}
