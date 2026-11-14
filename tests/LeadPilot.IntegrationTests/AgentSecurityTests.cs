using System.Net;
using System.Net.Http.Json;
using LeadPilot.Application.Agents;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>Tenant isolation (API, MVC, DB guard), permissions for every default role, anonymous access and the pages of the Phase 8 sales agent.</summary>
[Collection("app")]
public class AgentSecurityTests(AppFixture app)
{
    private TestWorld W => app.World;

    private sealed record Endpoint(string Method, string Url, string Permission, object? Body = null);

    private static IEnumerable<Endpoint> Endpoints(Guid agent, Guid product, Guid plan, Guid approval, Guid followUp, Guid conversation)
    {
        var settings = new { aiRepliesAllowed = false, maxAutoRepliesPerConversationPerDay = 10, maxTrialsPerLead = 1, approvalLifetimeHours = 72, maxOpenFollowUpsPerLead = 3 };
        var agentBody = new { name = "Perm agent", instructions = "Be helpful.", enabledTools = new[] { "GetPricingPlans" }, offTopicReply = "Only products." };
        return
        [
            new("GET", "/api/v1/agents", PermissionCodes.AgentsView), new("GET", "/api/v1/agents/tools", PermissionCodes.AgentsView), new("GET", "/api/v1/agents/settings", PermissionCodes.AgentsView), new("GET", $"/api/v1/agents/{agent}", PermissionCodes.AgentsView),
            new("PUT", "/api/v1/agents/settings", PermissionCodes.AgentsManage, settings), new("POST", "/api/v1/agents", PermissionCodes.AgentsManage, agentBody), new("POST", "/api/v1/agents/sample", PermissionCodes.AgentsManage),
            new("PUT", $"/api/v1/agents/{agent}", PermissionCodes.AgentsManage, agentBody), new("PUT", $"/api/v1/agents/{agent}/questions", PermissionCodes.AgentsManage, new { questions = Array.Empty<object>() }),
            new("PUT", $"/api/v1/agents/{agent}/active", PermissionCodes.AgentsManage, new { active = true }), new("POST", $"/api/v1/agents/{agent}/default", PermissionCodes.AgentsManage), new("PUT", $"/api/v1/agents/{agent}/auto-send", PermissionCodes.AgentsManage, new { enabled = false }),
            new("GET", "/api/v1/catalog/products", PermissionCodes.CatalogView), new("GET", $"/api/v1/catalog/products/{product}", PermissionCodes.CatalogView),
            new("POST", "/api/v1/catalog/products", PermissionCodes.CatalogManage, new { name = "P", isConfirmed = false, isActive = true }), new("PUT", $"/api/v1/catalog/products/{product}", PermissionCodes.CatalogManage, new { name = "P2", isConfirmed = false, isActive = true }),
            new("POST", $"/api/v1/catalog/products/{product}/plans", PermissionCodes.CatalogManage, new { name = "Plan", price = 1m, currency = "USD", isConfirmed = false, isActive = true }),
            new("PUT", $"/api/v1/catalog/plans/{plan}", PermissionCodes.CatalogManage, new { name = "Plan", price = 1m, currency = "USD", isConfirmed = false, isActive = true }), new("DELETE", $"/api/v1/catalog/plans/{Guid.NewGuid()}", PermissionCodes.CatalogManage),
            new("GET", "/api/v1/approvals", PermissionCodes.ApprovalsView), new("GET", $"/api/v1/approvals/{approval}", PermissionCodes.ApprovalsView),
            new("POST", $"/api/v1/approvals/{approval}/approve", PermissionCodes.ApprovalsDecide, new { }), new("POST", $"/api/v1/approvals/{approval}/reject", PermissionCodes.ApprovalsDecide, new { }),
            new("GET", "/api/v1/tool-executions", PermissionCodes.AgentsView),
            new("GET", "/api/v1/follow-ups", PermissionCodes.LeadsView), new("POST", $"/api/v1/follow-ups/{followUp}/done", PermissionCodes.LeadsUpdate), new("POST", $"/api/v1/follow-ups/{followUp}/cancel", PermissionCodes.LeadsUpdate),
            new("GET", $"/api/v1/conversations/{conversation}/notes", PermissionCodes.ConversationsView), new("GET", $"/api/v1/conversations/{conversation}/handoff-history", PermissionCodes.ConversationsView),
            new("POST", $"/api/v1/conversations/{conversation}/notes", PermissionCodes.ConversationsReply, new { body = "perm note" }), new("POST", $"/api/v1/conversations/{conversation}/takeover", PermissionCodes.ConversationsReply, new { reason = "x" }),
            new("POST", $"/api/v1/conversations/{conversation}/ai/pause", PermissionCodes.ConversationsReply, new { }), new("POST", $"/api/v1/conversations/{conversation}/ai/resume", PermissionCodes.ConversationsReply),
        ];
    }

    private static Task<HttpResponseMessage> Call(ApiSession s, Endpoint e) =>
        s.Http.SendAsync(new HttpRequestMessage(new HttpMethod(e.Method), e.Url) { Content = e.Body is null ? null : JsonContent.Create(e.Body, options: ApiSession.JsonOptions) });

    /// <summary>A workspace with an agent, a product, a plan, a pending approval, a follow-up and a conversation with a note: one of everything a foreign tenant could try to reach.</summary>
    private async Task<(AgentWorld W, Guid Approval, Guid FollowUp, ChatThread Chat, Guid Message)> RichWorldAsync()
    {
        var w = await AgentWorld.OpenAsync(app);
        var chat = await w.ChatAsync();
        var trig = await app.Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.TenantId == w.TenantId && m.ConversationId == chat.ConversationId).Select(m => m.Id).FirstAsync());
        await w.RunToolAsync(chat.ConversationId, AgentTools.CreatePaymentRequest, new { plan = "Standard" }, trigger: trig);
        await w.RunToolAsync(chat.ConversationId, AgentTools.ScheduleFollowUp, new { dueInHours = 24, note = "Call back" });
        Assert.Equal(HttpStatusCode.Created, (await w.Admin.Post($"/api/v1/conversations/{chat.ConversationId}/notes", new { body = "A private note" })).StatusCode);
        var approval = await app.Db(db => db.ApprovalRequests.IgnoreQueryFilters().Where(a => a.TenantId == w.TenantId).Select(a => a.Id).SingleAsync());
        var follow = await app.Db(db => db.FollowUps.IgnoreQueryFilters().Where(a => a.TenantId == w.TenantId).Select(a => a.Id).SingleAsync());
        return (w, approval, follow, chat, trig);
    }

    [RequiresMySqlFact]
    public async Task Every_agent_endpoint_answers_401_to_anonymous_callers()
    {
        var anon = ApiSession.Anonymous(app); var id = Guid.NewGuid();
        foreach (var e in Endpoints(id, id, id, id, id, id)) Assert.True(HttpStatusCode.Unauthorized == (await Call(anon, e)).StatusCode, $"{e.Method} {e.Url}");
    }

    [RequiresMySqlFact]
    public async Task Every_agent_endpoint_enforces_its_permission_for_every_default_role()
    {
        var (tenant, admin, ro) = await W.NewTenantAsync("agperm");
        var users = new List<(TestUser User, string Role)> { (ro, SystemRoles.ReadOnly), (admin, SystemRoles.TenantAdministrator) };
        foreach (var (name, role) in new[] { ("sm", SystemRoles.SalesManager), ("agent", SystemRoles.SalesAgent), ("mkt", SystemRoles.MarketingManager), ("staff", SystemRoles.TenantStaff) })
            users.Add((await W.AddUserAsync(tenant.Id, $"{name}-{Guid.NewGuid():N}"[..20] + "@agperm.test", role), role));
        var adminSession = await ApiSession.LoginAsync(app, admin.Email, admin.Password);
        var agent = await ApiSession.Json<AgentDto>(await adminSession.Post("/api/v1/agents/sample"));
        var product = (await ApiSession.Json<List<ProductListItemDto>>(await adminSession.Get("/api/v1/catalog/products"))).Single();
        var plan = (await ApiSession.Json<ProductDto>(await adminSession.Get($"/api/v1/catalog/products/{product.Id}"))).Plans.Single();
        var lead = await Crm.CreateLead(adminSession, phone: Crm.Phone());
        var follow = Guid.NewGuid(); var conv = Guid.NewGuid(); var approval = Guid.NewGuid();   // unknown ids: an authorised caller gets 404, an unauthorised one 403 BEFORE any lookup
        foreach (var (user, role) in users)
        {
            var session = await ApiSession.LoginAsync(app, user.Email, user.Password);
            var granted = SystemRoles.All.Single(r => r.Name == role).Permissions.ToHashSet();
            foreach (var e in Endpoints(agent.Id, product.Id, plan.Id, approval, follow, conv))
            {
                var status = (await Call(session, e)).StatusCode;
                if (granted.Contains(e.Permission)) Assert.True(status != HttpStatusCode.Forbidden && status != HttpStatusCode.Unauthorized, $"{role} should reach {e.Method} {e.Url} but got {(int)status}");
                else Assert.True(status == HttpStatusCode.Forbidden, $"{role} must be refused {e.Method} {e.Url} but got {(int)status}");
            }
        }
        _ = lead;
        // least privilege: only administrators may configure the agent or switch automatic replies on; only sales managers and administrators decide approvals or edit prices
        foreach (var r in new[] { SystemRoles.SalesAgent, SystemRoles.TenantStaff, SystemRoles.MarketingManager, SystemRoles.ReadOnly })
        { var p = SystemRoles.All.Single(x => x.Name == r).Permissions; Assert.DoesNotContain(PermissionCodes.AgentsManage, p); Assert.DoesNotContain(PermissionCodes.ApprovalsDecide, p); Assert.DoesNotContain(PermissionCodes.CatalogManage, p); }
    }

    [RequiresMySqlFact]
    public async Task One_workspace_cannot_see_or_change_the_agents_catalog_approvals_follow_ups_notes_or_handoffs_of_another()
    {
        var (b, bApproval, bFollow, bChat, _) = await RichWorldAsync(); await using var _b = b;
        await using var a = await AgentWorld.OpenAsync(app);
        var bPlan = b.PlanId;
        foreach (var e in new Endpoint[]
        {
            new("GET", $"/api/v1/agents/{b.AgentId}", ""), new("PUT", $"/api/v1/agents/{b.AgentId}", "", new { name = "Hijack", instructions = "x", enabledTools = Array.Empty<string>(), offTopicReply = "x" }),
            new("PUT", $"/api/v1/agents/{b.AgentId}/questions", "", new { questions = Array.Empty<object>() }), new("PUT", $"/api/v1/agents/{b.AgentId}/active", "", new { active = false }),
            new("POST", $"/api/v1/agents/{b.AgentId}/default", ""), new("PUT", $"/api/v1/agents/{b.AgentId}/auto-send", "", new { enabled = false }),
            new("GET", $"/api/v1/catalog/products/{b.ProductId}", ""), new("PUT", $"/api/v1/catalog/products/{b.ProductId}", "", new { name = "Hijack", isConfirmed = true, isActive = true }),
            new("POST", $"/api/v1/catalog/products/{b.ProductId}/plans", "", new { name = "Plan", price = 1m, currency = "USD", isConfirmed = false, isActive = true }),
            new("PUT", $"/api/v1/catalog/plans/{bPlan}", "", new { name = "Hijack", price = 1m, currency = "USD", isConfirmed = true, isActive = true }), new("DELETE", $"/api/v1/catalog/plans/{bPlan}", ""),
            new("GET", $"/api/v1/approvals/{bApproval}", ""), new("POST", $"/api/v1/approvals/{bApproval}/approve", "", new { }), new("POST", $"/api/v1/approvals/{bApproval}/reject", "", new { }),
            new("POST", $"/api/v1/follow-ups/{bFollow}/done", ""), new("POST", $"/api/v1/follow-ups/{bFollow}/cancel", ""),
            new("GET", $"/api/v1/conversations/{bChat.ConversationId}/notes", ""), new("POST", $"/api/v1/conversations/{bChat.ConversationId}/notes", "", new { body = "intrusion" }), new("GET", $"/api/v1/conversations/{bChat.ConversationId}/handoff-history", ""),
            new("POST", $"/api/v1/conversations/{bChat.ConversationId}/takeover", "", new { }), new("POST", $"/api/v1/conversations/{bChat.ConversationId}/ai/pause", "", new { }), new("POST", $"/api/v1/conversations/{bChat.ConversationId}/ai/resume", ""),
        })
            Assert.True(HttpStatusCode.NotFound == (await Call(a.Admin, e)).StatusCode, $"{e.Method} {e.Url}");
        // lists contain only the caller's rows; B's data is untouched
        Assert.DoesNotContain(await ApiSession.Json<List<AgentListItemDto>>(await a.Admin.Get("/api/v1/agents")), x => x.Id == b.AgentId);
        Assert.Empty((await ApiSession.Json<LeadPilot.Application.Common.PagedResult<ApprovalDto>>(await a.Admin.Get("/api/v1/approvals"))).Items);
        Assert.Empty((await ApiSession.Json<LeadPilot.Application.Common.PagedResult<FollowUpDto>>(await a.Admin.Get("/api/v1/follow-ups?status="))).Items);
        Assert.Empty((await ApiSession.Json<LeadPilot.Application.Common.PagedResult<ToolExecutionDto>>(await a.Admin.Get("/api/v1/tool-executions"))).Items);
        Assert.Equal(ApprovalStatus.Pending, (await ApiSession.Json<ApprovalDto>(await b.Admin.Get($"/api/v1/approvals/{bApproval}"))).Status);
        Assert.Equal("Standard", (await ApiSession.Json<ProductDto>(await b.Admin.Get($"/api/v1/catalog/products/{b.ProductId}"))).Plans.Single().Name);
        Assert.Equal(AiMode.Active, await bChat.ModeAsync());
        // ids smuggled into a tool call's arguments are refused, a foreign conversation is not found
        await Assert.ThrowsAnyAsync<Exception>(() => a.RunToolAsync(bChat.ConversationId, AgentTools.AddLeadNote, new { note = "x" }));
    }

    [RequiresMySqlFact]
    public async Task The_save_guard_refuses_cross_tenant_references_and_spoofed_tenant_ids_on_every_new_entity_and_keeps_notes_and_history_append_only()
    {
        var (b, bApproval, _, bChat, _) = await RichWorldAsync(); await using var _b = b;
        await using var a = await AgentWorld.OpenAsync(app);
        var now = DateTime.UtcNow;
        var bLead = await app.Db(db => db.Leads.IgnoreQueryFilters().Where(l => l.TenantId == b.TenantId).Select(l => l.Id).FirstAsync());

        async Task Refused(Func<DbContext, Task> add, string what)
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => a.Ai.InScope(async sp => { var db = sp.GetRequiredService<AppDbContext>(); await add(db); await db.SaveChangesAsync(); return 0; }));
            Assert.True(ex.ToString().Contains("another tenant", StringComparison.OrdinalIgnoreCase) || ex.GetType().Name.Contains("CrossTenant"), $"{what}: {ex.Message}");
        }
        // A's rows pointing at B's records
        await Refused(db => { db.Add(AgentQualificationQuestion.Create(a.TenantId, b.AgentId, "k", "q", AnswerType.Text, null, false, 1)); return Task.CompletedTask; }, "question → B's agent");
        await Refused(db => { db.Add(PricingPlan.Create(a.TenantId, b.ProductId, "x", 1, "USD", null, null, null, false, false, now, null)); return Task.CompletedTask; }, "plan → B's product");
        await Refused(db => { db.Add(ApprovalRequest.Create(a.TenantId, null, bChat.ConversationId, null, "CreatePaymentRequest", "r", "s", "{}", "k1", now, TimeSpan.FromHours(1))); return Task.CompletedTask; }, "approval → B's conversation");
        await Refused(db => { db.Add(ConversationNote.Create(a.TenantId, bChat.ConversationId, "x", null, ActorKind.User, now)); return Task.CompletedTask; }, "note → B's conversation");
        await Refused(db => { db.Add(HandoffEvent.Create(a.TenantId, bChat.ConversationId, HandoffKind.Takeover, ActorKind.User, null, null, null, null, null, now)); return Task.CompletedTask; }, "handoff → B's conversation");
        await Refused(db => { db.Add(LeadQualification.Create(a.TenantId, bLead, null, "{}", QualificationFit.Good, null, now)); return Task.CompletedTask; }, "qualification → B's lead");
        await Refused(db => { db.Add(FollowUp.Create(a.TenantId, bLead, null, null, now.AddDays(1), "x", ActorKind.User, now)); return Task.CompletedTask; }, "follow-up → B's lead");
        // spoofed tenant ids from an A scope
        await Refused(db => { db.Add(SalesAgent.Create(b.TenantId, "spoof", "x", false, false, now, null)); return Task.CompletedTask; }, "agent with B's tenant id");
        await Refused(db => { db.Add(CatalogProduct.Create(b.TenantId, "spoof", null, null, null, null, true, false, now, null)); return Task.CompletedTask; }, "product with B's tenant id");
        await Refused(db => { db.Add(AgentTenantSettings.Create(b.TenantId, now)); return Task.CompletedTask; }, "settings with B's tenant id");
        await Refused(db => { db.Add(AiToolExecution.Record(b.TenantId, null, null, null, null, "t", "a", null, null, ToolOutcome.Denied, null, null, null, null, 1, now)); return Task.CompletedTask; }, "tool log with B's tenant id");
        // append-only rows cannot be changed or deleted, not even by their own tenant
        foreach (var what in new[] { "note", "handoff", "tool" })
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => app.Db(async db =>
            {
                if (what == "note") db.Remove(await db.ConversationNotes.IgnoreQueryFilters().FirstAsync(e => e.TenantId == b.TenantId));
                else if (what == "handoff") { var h = await db.HandoffEvents.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.TenantId == b.TenantId); if (h is null) { db.HandoffEvents.Add(HandoffEvent.Create(b.TenantId, bChat.ConversationId, HandoffKind.Takeover, ActorKind.User, null, null, null, null, null, now)); await db.SaveChangesAsync(); h = await db.HandoffEvents.IgnoreQueryFilters().FirstAsync(e => e.TenantId == b.TenantId); } db.Remove(h); }
                else db.Remove(await db.AiToolExecutions.IgnoreQueryFilters().FirstAsync(e => e.TenantId == b.TenantId));
                await db.SaveChangesAsync();
            }));
            Assert.Contains("append-only", ex.ToString());
        }
        // the query filters hide B's rows from an A scope in every new table, and a tenant-less scope sees nothing
        await a.Ai.InScope(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            Assert.Empty(await db.SalesAgents.Where(r => r.TenantId == b.TenantId).ToListAsync()); Assert.Empty(await db.AgentQualificationQuestions.Where(r => r.TenantId == b.TenantId).ToListAsync());
            Assert.Empty(await db.CatalogProducts.Where(r => r.TenantId == b.TenantId).ToListAsync()); Assert.Empty(await db.PricingPlans.Where(r => r.TenantId == b.TenantId).ToListAsync());
            Assert.Empty(await db.ApprovalRequests.Where(r => r.TenantId == b.TenantId).ToListAsync()); Assert.Empty(await db.ConversationNotes.Where(r => r.TenantId == b.TenantId).ToListAsync());
            Assert.Empty(await db.HandoffEvents.Where(r => r.TenantId == b.TenantId).ToListAsync()); Assert.Empty(await db.AgentTenantSettings.Where(r => r.TenantId == b.TenantId).ToListAsync());
            Assert.Empty(await db.LeadQualifications.Where(r => r.TenantId == b.TenantId).ToListAsync()); Assert.Empty(await db.FollowUps.Where(r => r.TenantId == b.TenantId).ToListAsync());
            return 0;
        });
        using var scope = a.Ai.Worker.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().SalesAgents.ToListAsync());
        _ = bApproval;
    }

    [RequiresMySqlFact]
    public async Task The_ai_consumer_dead_letters_events_with_foreign_ids_and_the_runner_cannot_be_aimed_at_a_foreign_conversation()
    {
        var (b, _, _, bChat, bMsg) = await RichWorldAsync(); await using var _b = b;
        await using var a = await AgentWorld.OpenAsync(app);
        var processor = a.Ai.Worker.Services.GetRequiredService<LeadPilot.Infrastructure.Messaging.Broker.MessageProcessor>();
        var queue = LeadPilot.Infrastructure.Messaging.Broker.MessagingTopology.Queues(a.Ai.Worker.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LeadPilot.Infrastructure.Messaging.RabbitMqOptions>>().Value).Single(q => q.ConsumerName == "ai.reply");
        var forged = new LeadPilot.Application.Messaging.EventEnvelope(Guid.NewGuid(), a.TenantId, "corr", null, LeadPilot.Application.Messaging.MessagingEventTypes.AiReplyRequested, DateTime.UtcNow, 1,
            System.Text.Json.JsonSerializer.Serialize(new LeadPilot.Application.Messaging.AiReplyRequestedPayload(bChat.ConversationId, bMsg, bChat.Account.Id)));
        var outcome = await processor.ProcessAsync(queue, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(forged), WorkerHarness.Headers(forged), default);
        Assert.Equal(LeadPilot.Infrastructure.Messaging.Broker.DeliveryAction.DeadLetter, outcome.Action);
        Assert.Empty(await app.Db(db => db.AiInteractions.IgnoreQueryFilters().Where(i => i.TenantId == a.TenantId).ToListAsync()));
        Assert.Empty(await a.ToolsAsync());
    }

    [RequiresMySqlFact]
    public async Task Existing_roles_of_a_workspace_receive_the_new_permissions_through_the_role_sync_and_custom_roles_do_not()
    {
        string[] codes = [PermissionCodes.AgentsView, PermissionCodes.CatalogView, PermissionCodes.CatalogManage, PermissionCodes.ApprovalsView, PermissionCodes.ApprovalsDecide, PermissionCodes.AgentsManage];
        var granted = await app.Db(db => (from rp in db.RolePermissions.IgnoreQueryFilters() join r in db.TenantRoles.IgnoreQueryFilters() on rp.RoleId equals r.Id join p in db.Permissions on rp.PermissionId equals p.Id
                                          where r.TenantId == W.A.Id && r.IsSystem && codes.Contains(p.Code) select new { r.Name, p.Code }).ToListAsync());
        foreach (var role in new[] { SystemRoles.TenantAdministrator, SystemRoles.SalesManager, SystemRoles.SalesAgent, SystemRoles.MarketingManager, SystemRoles.TenantStaff, SystemRoles.ReadOnly })
            Assert.Equal(SystemRoles.All.Single(r => r.Name == role).Permissions.Where(codes.Contains).OrderBy(x => x), granted.Where(g => g.Name == role).Select(g => g.Code).OrderBy(x => x));
        Assert.DoesNotContain(granted, g => g.Name.StartsWith("A Custom"));
    }

    // ---------------------------------------------------------------------------------------------------------------- MVC pages

    [RequiresMySqlFact]
    public async Task The_agent_pages_render_encode_output_enforce_permissions_and_antiforgery_and_drive_the_use_cases()
    {
        var (w, approval, follow, chat, _) = await RichWorldAsync(); await using var _w = w;
        var evil = "<img src=x onerror=alert(1)>";
        var prod = await ApiSession.Json<ProductDto>(await w.Admin.Get($"/api/v1/catalog/products/{w.ProductId}"));
        await w.Admin.Put($"/api/v1/catalog/products/{w.ProductId}", new { name = "Evil " + evil, summary = prod.Summary, features = prod.Features, demoProcess = prod.DemoProcess, trialProcess = prod.TrialProcess, isConfirmed = true, isActive = true, version = prod.Version });
        await w.Admin.Post($"/api/v1/conversations/{chat.ConversationId}/notes", new { body = "Note " + evil });
        var s = await WebSession.LoginAsync(app, w.Ai.AdminUser.Email, w.Ai.AdminUser.Password);

        foreach (var url in new[] { "/Agents", $"/Agents/Edit/{w.AgentId}", "/Agents/Edit", "/Agents/Executions", "/Approvals", "/Catalog", $"/Catalog/Product/{w.ProductId}", "/Catalog/Product", "/FollowUps", $"/Conversations/Details/{chat.ConversationId}" })
        { var html = await s.Html(url); Assert.DoesNotContain("<img src=x onerror", html); Assert.Contains("breadcrumb", html.Replace("Breadcrumb", "breadcrumb"), StringComparison.OrdinalIgnoreCase); }
        var catalog = await s.Html("/Catalog"); Assert.Contains("Evil &lt;img src=x", catalog); Assert.Contains("Confirmed", catalog);
        var edit = await s.Html($"/Agents/Edit/{w.AgentId}"); Assert.Contains("Qualification questions", edit); Assert.Contains("student_count | How many students? | number | required", edit); Assert.Contains("Automatic replies", edit); Assert.Contains("TransferToHuman and StopMarketingMessages are always available", edit);
        var approvals = await s.Html("/Approvals"); Assert.Contains("CreatePaymentRequest", approvals); Assert.Contains("Approve", approvals); Assert.Contains("data-confirm", approvals);
        var thread = await s.Html($"/Conversations/Details/{chat.ConversationId}"); Assert.Contains("Take over", thread); Assert.Contains("Internal notes", thread); Assert.Contains("Handoff history", thread); Assert.Contains("Note &lt;img src=x", thread); Assert.Contains("Pause AI", thread);
        Assert.Contains("Sales agent", await s.Html("/Knowledge")); Assert.Contains("Approvals", await s.Html("/Knowledge"));

        // forms: antiforgery is required, and each form drives its use case
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Http.PostAsync($"/Conversations/TakeOver/{chat.ConversationId}", new FormUrlEncodedContent(new Dictionary<string, string> { ["reason"] = "x" }))).StatusCode);
        var took = await s.PostWithToken($"/Conversations/Details/{chat.ConversationId}", $"/Conversations/TakeOver/{chat.ConversationId}", ("reason", "Web takeover")); Assert.Equal(HttpStatusCode.Redirect, took.StatusCode);
        Assert.Equal(AiMode.HumanControl, await chat.ModeAsync());
        Assert.Contains("Web takeover", await s.Html($"/Conversations/Details/{chat.ConversationId}")); Assert.Contains("Resume AI", await s.Html($"/Conversations/Details/{chat.ConversationId}"));
        Assert.Equal(HttpStatusCode.Redirect, (await s.PostWithToken($"/Conversations/Details/{chat.ConversationId}", $"/Conversations/AddNote/{chat.ConversationId}", ("body", "From the web form"))).StatusCode);
        Assert.Contains(await w.Admin.Get($"/api/v1/conversations/{chat.ConversationId}/notes").ContinueWith(t => ApiSession.Json<List<ConversationNoteDto>>(t.Result)).Unwrap(), n => n.Body == "From the web form");
        Assert.Equal(HttpStatusCode.Redirect, (await s.PostWithToken($"/Conversations/Details/{chat.ConversationId}", $"/Conversations/ResumeAi/{chat.ConversationId}")).StatusCode);
        Assert.Equal(AiMode.Active, await chat.ModeAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await s.PostWithToken("/Agents", "/Agents/Questions/" + w.AgentId, ("questionsText", "city | Which city? | text | optional"))).StatusCode);
        Assert.Equal("city", (await ApiSession.Json<AgentDto>(await w.Admin.Get($"/api/v1/agents/{w.AgentId}"))).Questions.Single().Key);
        Assert.Equal(HttpStatusCode.Redirect, (await s.PostWithToken("/Agents", "/Agents/Questions/" + w.AgentId, ("questionsText", "broken line"))).StatusCode);       // bad format: message, nothing saved
        Assert.Equal("city", (await ApiSession.Json<AgentDto>(await w.Admin.Get($"/api/v1/agents/{w.AgentId}"))).Questions.Single().Key);
        Assert.Equal(HttpStatusCode.Redirect, (await s.PostWithToken("/Approvals", "/Approvals/RejectRequest/" + approval)).StatusCode);
        Assert.Equal(ApprovalStatus.Rejected, (await ApiSession.Json<ApprovalDto>(await w.Admin.Get($"/api/v1/approvals/{approval}"))).Status);
        Assert.Equal(HttpStatusCode.Redirect, (await s.PostWithToken("/FollowUps", $"/FollowUps/Close/{follow}", ("done", "true"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await s.PostWithToken("/Agents", "/Agents/SaveSettings", ("AiRepliesAllowed", "true"), ("MaxAutoRepliesPerConversationPerDay", "5"), ("MaxTrialsPerLead", "1"), ("ApprovalLifetimeHours", "24"), ("MaxOpenFollowUpsPerLead", "3"))).StatusCode);
        Assert.True((await ApiSession.Json<AgentSettingsDto>(await w.Admin.Get("/api/v1/agents/settings"))).AiRepliesAllowed);

        // a read-only user sees the pages but not the buttons, and cannot post
        var ro = await W.AddUserAsync(w.TenantId, $"ro-{Guid.NewGuid():N}@agweb.test", SystemRoles.ReadOnly);
        var rs = await WebSession.LoginAsync(app, ro.Email, ro.Password);
        Assert.Equal(HttpStatusCode.OK, (await rs.Get("/Catalog")).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await rs.Get("/Agents")).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await rs.Get("/Approvals")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rs.PostWithToken("/Catalog", "/Catalog/SaveProduct", ("Name", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rs.PostWithToken("/Catalog", $"/Conversations/TakeOver/{chat.ConversationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rs.Get("/Catalog/Product")).StatusCode);   // creating needs Catalog.Manage
    }

    [RequiresMySqlFact]
    public async Task The_web_pages_answer_404_for_another_workspaces_ids()
    {
        var (b, bApproval, bFollow, bChat, _) = await RichWorldAsync(); await using var _b = b;
        await using var a = await AgentWorld.OpenAsync(app);
        var s = await WebSession.LoginAsync(app, a.Ai.AdminUser.Email, a.Ai.AdminUser.Password);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Get($"/Agents/Edit/{b.AgentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Get($"/Catalog/Product/{b.ProductId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.PostWithToken("/Approvals", $"/Approvals/Approve/{bApproval}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.PostWithToken("/Approvals", $"/Approvals/RejectRequest/{bApproval}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.PostWithToken("/FollowUps", $"/FollowUps/Close/{bFollow}", ("done", "true"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.PostWithToken("/Agents", $"/Agents/SetActive/{b.AgentId}", ("active", "false"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.PostWithToken("/Agents", $"/Agents/SetAutoSend/{b.AgentId}", ("enabled", "false"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.PostWithToken("/Catalog", $"/Catalog/DeletePlan?productId={b.ProductId}&id={b.PlanId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.PostWithToken("/Conversations", $"/Conversations/TakeOver/{bChat.ConversationId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.PostWithToken("/Conversations", $"/Conversations/AddNote/{bChat.ConversationId}", ("body", "x"))).StatusCode);
        Assert.Equal(AiMode.Active, await bChat.ModeAsync());
    }
}
