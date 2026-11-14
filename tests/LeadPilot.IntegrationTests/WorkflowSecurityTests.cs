using System.Net;
using System.Net.Http.Json;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Security;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Workflows;
using LeadPilot.Infrastructure.Messaging.Broker;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LeadPilot.IntegrationTests;

/// <summary>Permissions (401/403/404) and tenant isolation of every workflow endpoint, consumer, job and the database guard.</summary>
[Collection("app")]
public class WorkflowSecurityTests(AppFixture app)
{
    private TestWorld W => app.World;
    private static readonly Guid Unknown = Guid.NewGuid();
    private sealed record Ep(string Method, string Url, string Permission, object? Body = null);

    private static IEnumerable<Ep> Endpoints() =>
    [
        new("GET", "/api/v1/workflows", PermissionCodes.WorkflowsView), new("GET", "/api/v1/workflows/catalog", PermissionCodes.WorkflowsView), new("GET", "/api/v1/workflows/{id}", PermissionCodes.WorkflowsView),
        new("POST", "/api/v1/workflows", PermissionCodes.WorkflowsCreate, new { name = "x", definition = new { triggers = new object[0], steps = new object[0] } }),
        new("PUT", "/api/v1/workflows/{id}", PermissionCodes.WorkflowsUpdate, new { name = "x" }), new("POST", "/api/v1/workflows/validate", PermissionCodes.WorkflowsView, new { definition = new { triggers = new object[0], steps = new object[0] } }),
        new("GET", "/api/v1/workflows/{id}/versions/1", PermissionCodes.WorkflowsView), new("PUT", "/api/v1/workflows/{id}/draft", PermissionCodes.WorkflowsUpdate, new { definition = new { triggers = new object[0], steps = new object[0] } }),
        new("POST", "/api/v1/workflows/{id}/versions/1/new-draft", PermissionCodes.WorkflowsUpdate), new("POST", "/api/v1/workflows/{id}/versions/1/activate", PermissionCodes.WorkflowsActivate),
        new("POST", "/api/v1/workflows/{id}/deactivate", PermissionCodes.WorkflowsActivate), new("PUT", "/api/v1/workflows/{id}/kill-switch", PermissionCodes.WorkflowsActivate, new { on = true }),
        new("DELETE", "/api/v1/workflows/{id}", PermissionCodes.WorkflowsDelete), new("POST", "/api/v1/workflows/{id}/simulate", PermissionCodes.WorkflowsExecute, new { leadId = Guid.NewGuid() }),
        new("POST", "/api/v1/workflows/{id}/run", PermissionCodes.WorkflowsExecute, new { leadId = Guid.NewGuid() }),
        new("GET", "/api/v1/workflow-executions", PermissionCodes.WorkflowsView), new("GET", "/api/v1/workflow-executions/{id}", PermissionCodes.WorkflowsView),
        new("POST", "/api/v1/workflow-executions/{id}/retry", PermissionCodes.WorkflowsExecute), new("POST", "/api/v1/workflow-executions/{id}/cancel", PermissionCodes.WorkflowsExecute),
        new("GET", "/api/v1/workflow-settings", PermissionCodes.WorkflowsView), new("PUT", "/api/v1/workflow-settings/limits", PermissionCodes.WorkflowsActivate, new { maxConcurrentExecutions = 5, monthlyExecutionLimit = 10, dailyMessageLimit = 10 }),
        new("PUT", "/api/v1/workflow-settings/kill-switch", PermissionCodes.WorkflowsActivate, new { on = false }),
        new("GET", "/api/v1/workflow-notifications", PermissionCodes.WorkflowsView), new("POST", "/api/v1/workflow-notifications/{id}/read", PermissionCodes.WorkflowsView),
    ];

    private static Task<HttpResponseMessage> Call(ApiSession s, Ep e, Guid id) =>
        s.Http.SendAsync(new HttpRequestMessage(new HttpMethod(e.Method), e.Url.Replace("{id}", id.ToString())) { Content = e.Body is null ? null : JsonContent.Create(e.Body, options: ApiSession.JsonOptions) });

    private static string Desc(Ep e) => $"{e.Method} {e.Url}";

    [RequiresMySqlFact]
    public async Task Every_workflow_endpoint_answers_401_to_anonymous_callers()
    {
        var anon = ApiSession.Anonymous(app);
        foreach (var e in Endpoints()) Assert.True((await Call(anon, e, Unknown)).StatusCode == HttpStatusCode.Unauthorized, Desc(e));
    }

    [RequiresMySqlFact]
    public async Task Roles_only_reach_what_their_permissions_allow_and_unknown_ids_are_404()
    {
        var users = new (TestUser User, string[] Has)[]
        {
            (W.AReadOnly, [PermissionCodes.WorkflowsView]), (W.ASalesAgent, [PermissionCodes.WorkflowsView]),
            (W.AMarketing, [PermissionCodes.WorkflowsView, PermissionCodes.WorkflowsCreate, PermissionCodes.WorkflowsUpdate, PermissionCodes.WorkflowsExecute]),
            (W.ASalesManager, [PermissionCodes.WorkflowsView, PermissionCodes.WorkflowsCreate, PermissionCodes.WorkflowsUpdate, PermissionCodes.WorkflowsActivate, PermissionCodes.WorkflowsExecute]),
            (W.AUsersOnly, []),
        };
        foreach (var (user, has) in users)
        {
            var s = await ApiSession.LoginAsync(app, user.Email, user.Password);
            foreach (var e in Endpoints())
            {
                var code = (await Call(s, e, Unknown)).StatusCode;
                if (!has.Contains(e.Permission)) Assert.True(code == HttpStatusCode.Forbidden, $"{user.Email} {Desc(e)} → {code}");
                else Assert.True(code != HttpStatusCode.Forbidden && code != HttpStatusCode.Unauthorized, $"{user.Email} {Desc(e)} → {code}");
            }
        }
        var admin = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        foreach (var e in Endpoints().Where(e => e.Url.Contains("{id}") && e.Method != "POST" || e.Url.Contains("{id}")))
            if (e.Url.Contains("/workflow-notifications") || e.Url.Contains("/workflows/{id}") || e.Url.Contains("/workflow-executions/{id}"))
                Assert.True((await Call(admin, e, Unknown)).StatusCode == HttpStatusCode.NotFound, "admin " + Desc(e));
    }

    [RequiresMySqlFact]
    public async Task Tenant_B_can_neither_see_nor_change_run_retry_or_cancel_tenant_A_workflows_and_runs()
    {
        await using var a = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        await using var b = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wf = await a.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Wait("p", 30)]));
        var lead = await Crm.CreateLead(a.Admin, phone: "+9230" + Random.Shared.Next(10_000_000, 99_999_999));
        await a.PumpAsync();
        var ex = (await a.ExecutionsAsync(wf.Id, lead.Id)).Single();
        await a.RunAsync(ex.Id);
        var note = await a.Worker.Scopes.RunAsync(a.TenantId, async (sp, ct) => { var db = sp.GetRequiredService<AppDbContext>(); db.WorkflowNotifications.Add(WorkflowNotification.Create(a.TenantId, a.AdminUser.Id, lead.Id, ex.Id, "n1", "t", "b", DateTime.UtcNow)); await db.SaveChangesAsync(ct); }).ContinueWith(_ => 0);

        foreach (var e in Endpoints().Where(e => e.Url.Contains("{id}")))
        {
            var url = e.Url.Contains("/workflow-executions/") ? ex.Id : e.Url.Contains("/workflow-notifications/") ? Unknown : wf.Id;
            var code = (await Call(b.Admin, e, url)).StatusCode;
            Assert.True(code == HttpStatusCode.NotFound, $"B {Desc(e)} → {code}");
        }
        var res = await b.Admin.Post($"/api/v1/workflows/{wf.Id}/simulate", new { leadId = lead.Id });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.DoesNotContain((await Crm.Ok<LeadPilot.Application.Common.PagedResult<WorkflowListItemDto>>(await b.Admin.Get("/api/v1/workflows"))).Items, w => w.Id == wf.Id);
        Assert.Empty(await b.ExecutionsAsync(wf.Id)); Assert.Empty(await b.ExecutionsAsync(leadId: lead.Id));
        // B cannot start ITS OWN workflow for A's lead either
        var own = await b.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("a", "AddTag", new { tag = "x" })]));
        Assert.Equal(HttpStatusCode.NotFound, (await b.Admin.Post($"/api/v1/workflows/{own.Id}/run", new { leadId = lead.Id })).StatusCode);
        // nothing of A changed
        Assert.Equal(ExecutionStatus.Waiting, (await a.ExecutionAsync(ex.Id)).Summary.Status);
        Assert.Equal(WorkflowStatus.Active, (await a.GetAsync(wf.Id)).Status);
    }

    [RequiresMySqlFact]
    public async Task Events_of_one_tenant_never_start_workflows_of_another_and_forged_tenants_are_dead_lettered()
    {
        await using var a = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        await using var b = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wfB = await b.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("a", "AddTag", new { tag = "b-only" })]));
        var leadA = await Crm.CreateLead(a.Admin, phone: "+9230" + Random.Shared.Next(10_000_000, 99_999_999));
        await a.Worker.DispatchAsync();
        var envelope = a.Worker.Publisher.Published.Single(e => e.Type == "lead.created" && e.Payload.Contains(leadA.Id.ToString()));
        var processor = a.Worker.Services.GetRequiredService<MessageProcessor>();
        var queue = MessagingTopology.Queues(a.Worker.Services.GetRequiredService<IOptions<LeadPilot.Infrastructure.Messaging.RabbitMqOptions>>().Value).Single(q => q.ConsumerName == "workflows.triggers");
        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(envelope);

        // A's event delivered normally: B's workflow is untouched
        Assert.Equal(DeliveryAction.Ack, (await processor.ProcessAsync(queue, body, WorkerHarness.Headers(envelope), default)).Action);
        Assert.Empty(await b.ExecutionsAsync(wfB.Id));
        // header says B, envelope says A: dead letter
        var forgedHeader = WorkerHarness.Headers(envelope with { TenantId = b.TenantId });
        var o1 = await processor.ProcessAsync(queue, body, forgedHeader, default);
        Assert.Equal(DeliveryAction.DeadLetter, o1.Action);
        // header AND envelope say B, but the lead belongs to A: the handler finds nothing for B and dead-letters, nothing starts
        var forged = envelope with { TenantId = b.TenantId, MessageId = Guid.NewGuid() };
        var o2 = await processor.ProcessAsync(queue, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(forged), WorkerHarness.Headers(forged), default);
        Assert.Equal(DeliveryAction.DeadLetter, o2.Action); Assert.Contains("lead_not_found_for_tenant", o2.Reason);
        Assert.Empty(await b.ExecutionsAsync(wfB.Id));
    }

    [RequiresMySqlFact]
    public async Task Spoofed_tenant_ids_jobs_and_the_database_guard_refuse_cross_tenant_access()
    {
        await using var a = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        await using var b = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wfA = await a.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Wait("p", 30)]));
        var leadA = await Crm.CreateLead(a.Admin, phone: "+9230" + Random.Shared.Next(10_000_000, 99_999_999));
        await a.PumpAsync();
        var ex = (await a.ExecutionsAsync(wfA.Id, leadA.Id)).Single();

        // a TenantId in the body that is not the caller's is rejected
        var spoof = await b.Admin.Post("/api/v1/workflows", new { tenantId = a.TenantId, name = "x", definition = Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("a", "AddTag", new { tag = "t" })]) });
        Assert.Equal(HttpStatusCode.BadRequest, spoof.StatusCode);

        // a job running in B's scope with A's execution id does nothing
        await b.RunAsync(ex.Id, b.TenantId);
        Assert.Equal(ExecutionStatus.Pending, (await a.ExecutionAsync(ex.Id)).Summary.Status);

        // the database guard: B cannot write a run that points at A's lead or A's workflow, nor change A's rows
        await b.Worker.Scopes.RunAsync(b.TenantId, async (sp, ct) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            var bWorkflow = (await b.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("a", "AddTag", new { tag = "t" })]))).Id;
            var version = await db.WorkflowVersions.FirstAsync(v => v.WorkflowId == bWorkflow, ct);
            db.WorkflowExecutions.Add(WorkflowExecution.Create(b.TenantId, bWorkflow, version.Id, Guid.NewGuid(), "manual", leadA.Id, null, null, "a", true, null, DateTime.UtcNow));
            await Assert.ThrowsAsync<CrossTenantViolationException>(() => db.SaveChangesAsync(ct));
            db.ChangeTracker.Clear();
            db.WorkflowExecutions.Add(WorkflowExecution.Create(a.TenantId, wfA.Id, version.Id, Guid.NewGuid(), "manual", leadA.Id, null, null, "a", true, null, DateTime.UtcNow));
            await Assert.ThrowsAsync<CrossTenantViolationException>(() => db.SaveChangesAsync(ct));
        });
        Assert.Equal(0, await app.Db(db => db.WorkflowExecutions.IgnoreQueryFilters().CountAsync(e => e.EntityId == leadA.Id && e.TenantId == b.TenantId)));
    }

    [RequiresMySqlFact]
    public async Task Notices_are_private_to_the_person_they_were_created_for()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var wf = await w.CreateActiveAsync(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("n", "NotifyUser", new { target = w.AdminUser.Id.ToString(), title = "Hi", message = "lead {{name}}" })]));
        var lead = await Crm.CreateLead(w.Admin, phone: "+9230" + Random.Shared.Next(10_000_000, 99_999_999));
        await w.PumpAsync();
        await w.RunToEndAsync((await w.ExecutionsAsync(wf.Id, lead.Id)).Single().Id);
        var mine = await Crm.Ok<List<WorkflowNotificationDto>>(await w.Admin.Get("/api/v1/workflow-notifications"));
        var n = Assert.Single(mine);
        // a colleague of the same workspace sees none and cannot mark it
        var other = await app.World.AddUserAsync(w.TenantId, $"other{Guid.NewGuid():N}@example.com", "Read-only");
        var s = await ApiSession.LoginAsync(app, other.Email, other.Password);
        Assert.Empty(await Crm.Ok<List<WorkflowNotificationDto>>(await s.Get("/api/v1/workflow-notifications")));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Post($"/api/v1/workflow-notifications/{n.Id}/read")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await w.Admin.Post($"/api/v1/workflow-notifications/{n.Id}/read")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Swagger_documents_the_workflow_endpoints()
    {
        var json = await ApiSession.Anonymous(app).Http.GetStringAsync("/swagger/v1/swagger.json");
        foreach (var path in new[] { "/api/v1/workflows", "/api/v1/workflows/{id}/simulate", "/api/v1/workflow-executions/{id}/retry", "/api/v1/workflow-settings/kill-switch" }) Assert.Contains($"\"{path}\"", json);
        Assert.Contains("Dry run", json); Assert.Contains("never repeated", json);
    }
}
