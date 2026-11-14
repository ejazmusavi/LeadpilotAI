using System.Net;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Messaging.Services;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>Tenant isolation and permissions for every messaging endpoint, through the REST API, the webhook, the consumers and the database guard.</summary>
[Collection("app")]
public class MessagingSecurityTests(AppFixture app)
{
    private TestWorld W => app.World;
    private static string Phone() => "+9230" + Random.Shared.Next(10_000_000, 99_999_999);

    private sealed record Sides(TestAccount A, TestAccount B, ApiSession AAdmin, ApiSession BAdmin, ConversationListItemDto ConvA, ConversationListItemDto ConvB, MessageDto MsgA, TemplateDto TplA, TemplateDto TplB, Guid ContactA, Guid ContactB);

    private Task<Sides> SidesAsync() => app.SharedAsync("securitySides", async () =>
    {
        var aAdmin = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password); var bAdmin = await ApiSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password);
        var a = await MessagingKit.SharedAccountAAsync(app); await MessagingKit.PrepareAccountAsync(app, a.Id);
        var b = await MessagingKit.CreateAccountAsync(app, bAdmin, W.B.Id, hourly: 120, daily: 1000, delay: 10); await MessagingKit.PrepareAccountAsync(app, b.Id);
        await using var worker = MessagingKit.BuildWorker(app);
        var leadA = await MessagingKit.LeadWithContactAsync(aAdmin); var leadB = await MessagingKit.LeadWithContactAsync(bAdmin);
        var convA = await MessagingKit.InboundAsync(app, worker, a, aAdmin, leadA.Phone, "tenant A secret talk"); var convB = await MessagingKit.InboundAsync(app, worker, b, bAdmin, leadB.Phone, "tenant B secret talk");
        var msgA = await MessagingKit.ReplyAsync(aAdmin, convA.Id, "A outbound");
        var tplA = await ApiSession.Json<TemplateDto>(await aAdmin.Post("/api/v1/message-templates", new { name = "A tpl " + Guid.NewGuid().ToString("N")[..6], body = "A says hi {{name}}", category = "Service" }));
        var tplB = await ApiSession.Json<TemplateDto>(await bAdmin.Post("/api/v1/message-templates", new { name = "B tpl " + Guid.NewGuid().ToString("N")[..6], body = "B says hi {{name}}", category = "Service" }));
        return new Sides(a, b, aAdmin, bAdmin, convA, convB, msgA, tplA, tplB, leadA.ContactId, leadB.ContactId);
    });

    private static async Task Expect(HttpStatusCode code, string what, Task<HttpResponseMessage> call)
    {
        var res = await call;
        Assert.True(res.StatusCode == code, $"{what}: expected {(int)code} but got {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
    }

    [RequiresMySqlFact]
    public async Task Lists_never_contain_the_other_tenants_data_and_own_data_is_visible()
    {
        var s = await SidesAsync();
        var aConvs = await MessagingKit.ConversationsAsync(s.AAdmin); var bConvs = await MessagingKit.ConversationsAsync(s.BAdmin);
        Assert.Contains(aConvs, c => c.Id == s.ConvA.Id); Assert.DoesNotContain(aConvs, c => c.Id == s.ConvB.Id);
        Assert.Contains(bConvs, c => c.Id == s.ConvB.Id); Assert.DoesNotContain(bConvs, c => c.Id == s.ConvA.Id);
        Assert.Empty(await MessagingKit.ConversationsAsync(s.AAdmin, "&search=tenant%20B%20secret"));
        Assert.Empty(await MessagingKit.ConversationsAsync(s.BAdmin, "&search=tenant%20A%20secret"));
        Assert.Empty(await MessagingKit.ConversationsAsync(s.AAdmin, $"&accountId={s.B.Id}"));
        Assert.Empty(await MessagingKit.ConversationsAsync(s.AAdmin, $"&contactId={s.ContactB}"));
        Assert.DoesNotContain(await Crm.Ok<List<ChannelAccountDto>>(await s.AAdmin.Get("/api/v1/channels/accounts")), x => x.Id == s.B.Id);
        Assert.DoesNotContain(await Crm.Ok<List<ChannelAccountDto>>(await s.BAdmin.Get("/api/v1/channels/accounts")), x => x.Id == s.A.Id);
        Assert.DoesNotContain(await Crm.Ok<List<TemplateDto>>(await s.AAdmin.Get("/api/v1/message-templates")), x => x.Id == s.TplB.Id);
        Assert.DoesNotContain((await Crm.Page<WebhookEventDto>(await s.AAdmin.Get("/api/v1/webhook-events?pageSize=100"))).Items, e => e.ChannelAccountId == s.B.Id);
        Assert.DoesNotContain((await Crm.Page<WebhookEventDto>(await s.BAdmin.Get("/api/v1/webhook-events?pageSize=100"))).Items, e => e.ChannelAccountId == s.A.Id);
        Assert.DoesNotContain("tenant B secret", await (await s.AAdmin.Get("/api/v1/conversations?pageSize=100")).Content.ReadAsStringAsync());
    }

    [RequiresMySqlFact]
    public async Task Every_conversation_message_and_template_endpoint_answers_404_for_another_tenants_ids_and_changes_nothing()
    {
        var s = await SidesAsync(); var b = s.BAdmin;
        await Expect(HttpStatusCode.NotFound, "get conversation", b.Get($"/api/v1/conversations/{s.ConvA.Id}"));
        await Expect(HttpStatusCode.NotFound, "list messages", b.Get($"/api/v1/conversations/{s.ConvA.Id}/messages"));
        await Expect(HttpStatusCode.NotFound, "reply", b.Post($"/api/v1/conversations/{s.ConvA.Id}/messages", new { text = "intrusion" }));
        await Expect(HttpStatusCode.NotFound, "mark read", b.Post($"/api/v1/conversations/{s.ConvA.Id}/read"));
        await Expect(HttpStatusCode.NotFound, "status", b.Put($"/api/v1/conversations/{s.ConvA.Id}/status?status=Closed", new { }));
        await Expect(HttpStatusCode.NotFound, "ai mode", b.Put($"/api/v1/conversations/{s.ConvA.Id}/ai-mode", new { mode = "Active" }));
        await Expect(HttpStatusCode.NotFound, "assign", b.Put($"/api/v1/conversations/{s.ConvA.Id}/assignee", new { userId = W.BAdmin.Id }));
        await Expect(HttpStatusCode.NotFound, "get message", b.Get($"/api/v1/messages/{s.MsgA.Id}"));
        await Expect(HttpStatusCode.NotFound, "get template", b.Get($"/api/v1/message-templates/{s.TplA.Id}"));
        await Expect(HttpStatusCode.NotFound, "preview template", b.Get($"/api/v1/message-templates/{s.TplA.Id}/preview"));
        await Expect(HttpStatusCode.NotFound, "update template", b.Put($"/api/v1/message-templates/{s.TplA.Id}", new { name = "hijack", body = "x", category = "Service" }));
        await Expect(HttpStatusCode.NotFound, "toggle template", b.Put($"/api/v1/message-templates/{s.TplA.Id}/active?active=false", new { }));
        await Expect(HttpStatusCode.NotFound, "delete template", b.Delete($"/api/v1/message-templates/{s.TplA.Id}"));
        // send to the other tenant's contact / through the other tenant's account / with the other tenant's template
        await Expect(HttpStatusCode.NotFound, "send to foreign contact", b.Post("/api/v1/messages", new { contactId = s.ContactA, channelAccountId = s.B.Id, text = "x" }));
        await Expect(HttpStatusCode.NotFound, "send through foreign account", b.Post("/api/v1/messages", new { contactId = s.ContactB, channelAccountId = s.A.Id, text = "x" }));
        await Expect(HttpStatusCode.NotFound, "reply with foreign template", b.Post($"/api/v1/conversations/{s.ConvB.Id}/messages", new { templateId = s.TplA.Id }));
        // and A's data is exactly as it was
        var thread = await MessagingKit.ThreadAsync(s.AAdmin, s.ConvA.Id); Assert.DoesNotContain(thread, m => m.Body == "intrusion");
        var conv = await Crm.Ok<ConversationDto>(await s.AAdmin.Get($"/api/v1/conversations/{s.ConvA.Id}"));
        Assert.Equal(ConversationStatus.Open, conv.Summary.Status); Assert.Equal(AiMode.Paused, conv.Summary.AiMode); Assert.Null(conv.Summary.AssignedUserId);
        Assert.Equal("A says hi {{name}}", (await Crm.Ok<TemplateDto>(await s.AAdmin.Get($"/api/v1/message-templates/{s.TplA.Id}"))).Body);
        Assert.DoesNotContain(app.Waha.SentMessages, m => m.Text is "intrusion" or "x");
    }

    [RequiresMySqlFact]
    public async Task Every_account_endpoint_answers_404_for_another_tenants_account_and_the_gateway_is_never_called()
    {
        var s = await SidesAsync(); var b = s.BAdmin; var calls = app.Waha.Calls.Count;
        var id = s.A.Id;
        await Expect(HttpStatusCode.NotFound, "get", b.Get($"/api/v1/channels/accounts/{id}"));
        await Expect(HttpStatusCode.NotFound, "setup", b.Get($"/api/v1/channels/accounts/{id}/setup"));
        await Expect(HttpStatusCode.NotFound, "update", b.Put($"/api/v1/channels/accounts/{id}", new { displayName = "hijack", hourlyCap = 1, dailyCap = 2, minDelaySeconds = 10 }));
        await Expect(HttpStatusCode.NotFound, "credentials", b.Put($"/api/v1/channels/accounts/{id}/credentials", new { baseUrl = app.Waha.BaseUrl, apiKey = FakeWahaServer.ApiKey }));
        await Expect(HttpStatusCode.NotFound, "connect", b.Post($"/api/v1/channels/accounts/{id}/connect"));
        await Expect(HttpStatusCode.NotFound, "refresh", b.Post($"/api/v1/channels/accounts/{id}/refresh"));
        await Expect(HttpStatusCode.NotFound, "qr", b.Get($"/api/v1/channels/accounts/{id}/qr"));
        await Expect(HttpStatusCode.NotFound, "disconnect", b.Post($"/api/v1/channels/accounts/{id}/disconnect?logout=true"));
        await Expect(HttpStatusCode.NotFound, "pause", b.Post($"/api/v1/channels/accounts/{id}/pause"));
        await Expect(HttpStatusCode.NotFound, "resume", b.Post($"/api/v1/channels/accounts/{id}/resume"));
        await Expect(HttpStatusCode.NotFound, "notifications", b.Get($"/api/v1/channels/accounts/{id}/notifications"));
        await Expect(HttpStatusCode.NotFound, "acknowledge", b.Post($"/api/v1/channels/accounts/{id}/notifications/acknowledge"));
        await Expect(HttpStatusCode.NotFound, "remove", b.Delete($"/api/v1/channels/accounts/{id}"));
        Assert.Equal(calls, app.Waha.Calls.Count);                                        // not one request reached the gateway
        var dto = await ApiSession.Json<ChannelAccountDto>(await s.AAdmin.Get($"/api/v1/channels/accounts/{id}"));
        Assert.NotEqual("hijack", dto.DisplayName); Assert.False(dto.IsPaused);
    }

    [RequiresMySqlFact]
    public async Task A_session_name_already_used_by_another_tenant_cannot_be_registered_and_the_error_reveals_nothing()
    {
        var s = await SidesAsync();
        var res = await s.BAdmin.Post("/api/v1/channels/accounts", new { displayName = "Squatter", baseUrl = app.Waha.BaseUrl, apiKey = FakeWahaServer.ApiKey, sessionName = s.A.Session });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var detail = await Crm.ProblemDetail(res); Assert.DoesNotContain(W.A.Name, detail); Assert.DoesNotContain(s.A.Id.ToString(), detail);
        Assert.DoesNotContain(await Crm.Ok<List<ChannelAccountDto>>(await s.BAdmin.Get("/api/v1/channels/accounts")), x => x.GatewayAccountId == s.A.Session);
    }

    [RequiresMySqlFact]
    public async Task A_tenant_id_in_the_body_query_or_header_is_never_trusted()
    {
        var s = await SidesAsync();
        await Expect(HttpStatusCode.BadRequest, "body", s.AAdmin.Post($"/api/v1/conversations/{s.ConvA.Id}/messages", new { text = "x", tenantId = W.B.Id }));
        await Expect(HttpStatusCode.BadRequest, "query", s.AAdmin.Get($"/api/v1/conversations?tenantId={W.B.Id}"));
        await Expect(HttpStatusCode.BadRequest, "account body", s.AAdmin.Put($"/api/v1/channels/accounts/{s.A.Id}", new { displayName = "x", hourlyCap = 10, dailyCap = 20, minDelaySeconds = 10, tenantId = W.B.Id }));
        await Expect(HttpStatusCode.BadRequest, "template body", s.AAdmin.Post("/api/v1/message-templates", new { name = "spoof", body = "x", category = "Service", tenantId = W.B.Id }));
        s.AAdmin.Http.DefaultRequestHeaders.Add("X-Tenant-Id", W.B.Id.ToString());
        try { await Expect(HttpStatusCode.BadRequest, "header", s.AAdmin.Get("/api/v1/conversations")); }
        finally { s.AAdmin.Http.DefaultRequestHeaders.Remove("X-Tenant-Id"); }
    }

    [RequiresMySqlFact]
    public async Task A_webhook_for_one_tenants_account_can_never_write_into_another_tenant()
    {
        var s = await SidesAsync(); var chat = s.A.ChatId(Phone());
        var bBefore = await app.Db(async db => (await db.Messages.IgnoreQueryFilters().CountAsync(m => m.TenantId == W.B.Id), await db.WebhookEvents.IgnoreQueryFilters().CountAsync(w => w.TenantId == W.B.Id), await db.OutboxMessages.IgnoreQueryFilters().CountAsync(o => o.TenantId == W.B.Id)));
        // A's event signed with B's secret on A's address, and on B's address with A's secret: authentication fails both ways
        Assert.Equal(HttpStatusCode.Unauthorized, (await MessagingKit.PostWebhookAsync(app, s.A, WahaEvents.Message(s.A.Session, chat, "cross"), secret: s.B.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MessagingKit.PostWebhookAsync(app, s.B, WahaEvents.Message(s.B.Session, chat, "cross"), secret: s.A.Secret)).StatusCode);
        // B's address with B's secret but A's session name inside the signed body: ignored (the session must belong to the account that was addressed)
        var r = await MessagingKit.PostWebhookAsync(app, s.B, WahaEvents.Message(s.A.Session, chat, "cross"));
        Assert.Equal("ignored", await MessagingKit.StatusOf(r));
        // a proper A event lands in A, only
        Assert.Equal("accepted", await MessagingKit.StatusOf(await MessagingKit.PostWebhookAsync(app, s.A, WahaEvents.Message(s.A.Session, chat, "genuine"))));
        var bAfter = await app.Db(async db => (await db.Messages.IgnoreQueryFilters().CountAsync(m => m.TenantId == W.B.Id), await db.WebhookEvents.IgnoreQueryFilters().CountAsync(w => w.TenantId == W.B.Id), await db.OutboxMessages.IgnoreQueryFilters().CountAsync(o => o.TenantId == W.B.Id)));
        Assert.Equal(bBefore, bAfter);
        Assert.Equal(1, await app.Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.TenantId == W.A.Id && m.Body == "genuine")));
        Assert.Equal(0, await app.Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.Body == "cross")));
    }

    [RequiresMySqlFact]
    public async Task One_tenants_stored_credentials_are_never_opened_or_used_for_another_tenant()
    {
        var s = await SidesAsync();
        var (credA, credB) = await app.Db(async db => (await db.ChannelCredentials.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.ChannelAccountId == s.A.Id), await db.ChannelCredentials.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.ChannelAccountId == s.B.Id)));
        Assert.NotEqual(credA.ProtectedApiKey, credB.ProtectedApiKey);   // same plaintext key, different ciphertext: bound to tenant and account
        await using var worker = MessagingKit.BuildWorker(app); using var scope = worker.Services.CreateScope(); var vault = scope.ServiceProvider.GetRequiredService<CredentialVault>();
        Assert.Equal(FakeWahaServer.ApiKey, vault.Open(W.A.Id, s.A.Id, credA.ProtectedApiKey));
        Assert.Throws<CrossTenantViolationException>(() => vault.Open(W.B.Id, s.B.Id, credA.ProtectedApiKey));      // A's ciphertext under B's identity
        Assert.Throws<CrossTenantViolationException>(() => vault.Open(W.B.Id, s.A.Id, credA.ProtectedApiKey));
        Assert.Throws<CrossTenantViolationException>(() => vault.Open(W.A.Id, s.B.Id, credA.ProtectedApiKey));

        // even if B's credential row were overwritten with A's ciphertext (database tampering), B would not use it
        var original = credB.ProtectedApiKey;
        await app.Db(db => db.ChannelCredentials.IgnoreQueryFilters().Where(c => c.ChannelAccountId == s.B.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.ProtectedApiKey, credA.ProtectedApiKey)));
        try
        {
            var calls = app.Waha.Calls.Count;
            var refresh = await s.BAdmin.Post($"/api/v1/channels/accounts/{s.B.Id}/refresh");
            Assert.Equal(HttpStatusCode.NotFound, refresh.StatusCode); Assert.Equal(calls, app.Waha.Calls.Count);
        }
        finally { await app.Db(db => db.ChannelCredentials.IgnoreQueryFilters().Where(c => c.ChannelAccountId == s.B.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.ProtectedApiKey, original))); }
        Assert.True((await s.BAdmin.Post($"/api/v1/channels/accounts/{s.B.Id}/refresh")).IsSuccessStatusCode);
    }

    [RequiresMySqlFact]
    public async Task Consumers_dead_letter_forged_or_missing_tenant_ids_and_never_touch_the_real_tenants_data()
    {
        var s = await SidesAsync(); await using var worker = MessagingKit.BuildWorker(app);
        var processor = worker.Services.GetRequiredService<LeadPilot.Infrastructure.Messaging.Broker.MessageProcessor>(); var rmq = worker.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LeadPilot.Infrastructure.Messaging.RabbitMqOptions>>().Value;
        var inbound = LeadPilot.Infrastructure.Messaging.Broker.MessagingTopology.Queues(rmq).First(q => q.RoutingKeys.Contains("message.received"));
        var (msgA, webhookA) = await app.Db(async db => (await db.Messages.IgnoreQueryFilters().AsNoTracking().FirstAsync(m => m.ConversationId == s.ConvA.Id && m.Direction == MessageDirection.Inbound), await db.WebhookEvents.IgnoreQueryFilters().AsNoTracking().FirstAsync(w => w.ChannelAccountId == s.A.Id)));
        var unreadBefore = (await MessagingKit.ConversationsAsync(s.AAdmin)).Single(c => c.Id == s.ConvA.Id).UnreadCount;
        var payload = System.Text.Json.JsonSerializer.Serialize(new InboundReceivedPayload(webhookA.Id, msgA.Id, s.ConvA.Id, s.A.Id));
        EventEnvelope Env(Guid tenant) => new(Guid.NewGuid(), tenant, "forged-corr", null, "message.received", DateTime.UtcNow, 1, payload);
        async Task<LeadPilot.Infrastructure.Messaging.Broker.DeliveryOutcome> Run(EventEnvelope e, Dictionary<string, object?> headers) => await processor.ProcessAsync(inbound, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(e), headers, default);

        // tenant B's identity with tenant A's ids: not found in B's scope → poison → dead letter, A untouched
        var forged = Env(W.B.Id); var o1 = await Run(forged, WorkerHarness.Headers(forged));
        Assert.Equal(LeadPilot.Infrastructure.Messaging.Broker.DeliveryAction.DeadLetter, o1.Action); Assert.Contains("not_found_for_tenant", o1.Reason);
        // header says B, envelope says A
        var mismatch = Env(W.A.Id); var o2 = await Run(mismatch, WorkerHarness.Headers(Env(W.B.Id) with { MessageId = mismatch.MessageId }));
        Assert.Equal(LeadPilot.Infrastructure.Messaging.Broker.DeliveryAction.DeadLetter, o2.Action);
        // no header, empty header, garbage header, an unknown tenant
        var noHeader = Env(W.A.Id); Assert.Equal(LeadPilot.Infrastructure.Messaging.Broker.DeliveryAction.DeadLetter, (await Run(noHeader, new Dictionary<string, object?>())).Action);
        Assert.Equal(LeadPilot.Infrastructure.Messaging.Broker.DeliveryAction.DeadLetter, (await Run(noHeader, new() { ["TenantId"] = "" })).Action);
        var ghost = Env(Guid.NewGuid()); var o3 = await Run(ghost, WorkerHarness.Headers(ghost)); Assert.Equal(LeadPilot.Infrastructure.Messaging.Broker.DeliveryAction.DeadLetter, o3.Action); Assert.Contains("tenant_unavailable", o3.Reason);
        Assert.Equal(unreadBefore, (await MessagingKit.ConversationsAsync(s.AAdmin)).Single(c => c.Id == s.ConvA.Id).UnreadCount);
        Assert.Equal(0, await app.Db(db => db.ProcessedMessages.IgnoreQueryFilters().CountAsync(p => p.MessageId == forged.MessageId || p.MessageId == mismatch.MessageId || p.MessageId == ghost.MessageId)));
    }

    [RequiresMySqlFact]
    public async Task A_suspended_tenants_events_wait_in_the_outbox_and_are_never_processed_until_it_is_active_again()
    {
        var t = await W.NewTenantAsync("susp2"); var admin = await ApiSession.LoginAsync(app, t.Admin.Email, t.Admin.Password);
        var a = await MessagingKit.CreateAccountAsync(app, admin, t.Tenant.Id); await using var worker = MessagingKit.BuildWorker(app);
        var phone = Phone();
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(phone), "before suspension"));   // stored while the tenant is still active
        var platform = await ApiSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        Assert.True((await platform.Post($"/api/v1/tenants/{t.Tenant.Id}/suspend", new { reason = "test" })).IsSuccessStatusCode);

        await worker.PumpAsync();
        Assert.Equal(1, await app.Db(db => db.OutboxMessages.IgnoreQueryFilters().CountAsync(o => o.TenantId == t.Tenant.Id && o.Status == OutboxStatus.Pending)));   // held, not published
        Assert.Empty(worker.Publisher.Published); Assert.Empty(worker.DeadLettered);

        // an event of that tenant that is already on the broker is dead-lettered by the consumer (the tenant scope refuses a suspended tenant)
        var processor = worker.Services.GetRequiredService<LeadPilot.Infrastructure.Messaging.Broker.MessageProcessor>(); var rmq = worker.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LeadPilot.Infrastructure.Messaging.RabbitMqOptions>>().Value;
        var queue = LeadPilot.Infrastructure.Messaging.Broker.MessagingTopology.Queues(rmq).First(q => q.RoutingKeys.Contains("message.received"));
        var env = new EventEnvelope(Guid.NewGuid(), t.Tenant.Id, "c", null, "message.received", DateTime.UtcNow, 1, "{}");
        var outcome = await processor.ProcessAsync(queue, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(env), WorkerHarness.Headers(env), default);
        Assert.Equal(LeadPilot.Infrastructure.Messaging.Broker.DeliveryAction.DeadLetter, outcome.Action); Assert.Contains("tenant_unavailable", outcome.Reason);

        // a new webhook is refused while suspended, and after reactivation the held event flows through
        Assert.Equal(HttpStatusCode.Forbidden, (await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(phone), "during suspension"))).StatusCode);
        Assert.True((await platform.Post($"/api/v1/tenants/{t.Tenant.Id}/activate")).IsSuccessStatusCode);
        await worker.PumpAsync();
        var conv = Assert.Single(await MessagingKit.ConversationsAsync(admin), c => c.PeerPhoneE164 == phone);
        Assert.Equal("before suspension", conv.LastMessagePreview);
    }

    // ------------------------------------------------------------------ database guard

    private async Task InTenant(Guid tenantId, Func<AppDbContext, Task> work)
    {
        using var scope = app.Web!.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(tenantId);
        await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [RequiresMySqlFact]
    public async Task The_database_guard_rejects_a_cross_tenant_reference_for_every_messaging_entity()
    {
        var s = await SidesAsync();
        var (channelA, accountA, convA, msgA, tplA) = await app.Db(async db => (
            await db.Channels.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.TenantId == W.A.Id), await db.ChannelAccounts.IgnoreQueryFilters().AsNoTracking().FirstAsync(a => a.Id == s.A.Id),
            await db.Conversations.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.Id == s.ConvA.Id), await db.Messages.IgnoreQueryFilters().AsNoTracking().FirstAsync(m => m.ConversationId == s.ConvA.Id),
            await db.MessageTemplates.IgnoreQueryFilters().AsNoTracking().FirstAsync(t => t.Id == s.TplA.Id)));
        var now = DateTime.UtcNow; var b = W.B.Id;
        var (convB, accountB) = await app.Db(async db => (await db.Conversations.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.Id == s.ConvB.Id), await db.ChannelAccounts.IgnoreQueryFilters().AsNoTracking().FirstAsync(a => a.Id == s.B.Id)));

        var cases = new Dictionary<string, Func<AppDbContext, object>>
        {
            ["ChannelAccount→Channel"] = _ => ChannelAccount.Create(b, channelA.Id, "x", "guard-" + Guid.NewGuid().ToString("N")[..8], null, now),
            ["ChannelCredential→ChannelAccount"] = _ => ChannelCredential.Create(b, accountA.Id, "a", "b", "c", "••••", now),
            ["Conversation→ChannelAccount"] = _ => Conversation.Create(b, accountA.Id, "9230000000@c.us", null, null, now),
            ["Message→Conversation"] = _ => Message.Inbound(b, convA.Id, accountB.Id, "false_x_" + Guid.NewGuid().ToString("N"), "x", now, "c", now),
            ["Message→ChannelAccount"] = _ => Message.Inbound(b, convB.Id, accountA.Id, "false_x_" + Guid.NewGuid().ToString("N"), "x", now, "c", now),
            ["Message→Template"] = _ => Message.Outbound(b, convB.Id, accountB.Id, null, MessagePurpose.Service, "x", "k-" + Guid.NewGuid().ToString("N"), "c", tplA.Id, null, now),
            ["MessageAttachment→Message"] = _ => MessageAttachment.Describe(b, msgA.Id, "a.jpg", "image/jpeg", 1, now),
            ["MessageStatusEvent→Message"] = _ => MessageStatusEvent.Create(b, msgA.Id, MessageStatus.Sent, "ack", 1, true, now, now),
            ["WebhookEvent→ChannelAccount"] = _ => WebhookEvent.Create(b, accountA.Id, "evt-" + Guid.NewGuid().ToString("N"), "message", "{}", "c", null, now, TimeSpan.FromDays(1)),
            ["ConversationAssignment→Conversation"] = _ => ConversationAssignment.Create(b, convA.Id, null, null, now),
            ["ChannelAccountNotification→ChannelAccount"] = _ => ChannelAccountNotification.Create(b, accountA.Id, NotificationKind.AccountPaused, "x", now),
        };
        foreach (var (name, make) in cases)
            await InTenant(W.B.Id, async db =>
            {
                db.Add(make(db));
                var ex = await Assert.ThrowsAsync<CrossTenantViolationException>(() => db.SaveChangesAsync());
                Assert.True(ex.Message.Contains("another tenant", StringComparison.OrdinalIgnoreCase), name + ": " + ex.Message);
            });
        // writing an entity stamped for another tenant is rejected outright
        await InTenant(W.B.Id, async db => { db.Add(Channel.Create(W.A.Id, ChannelType.WhatsApp, ChannelProvider.Waha, "x", now)); await Assert.ThrowsAsync<CrossTenantViolationException>(() => db.SaveChangesAsync()); });
        // and without any tenant nothing can be written at all
        using var scope = app.Web!.Services.CreateScope();
        var bare = scope.ServiceProvider.GetRequiredService<AppDbContext>(); bare.Add(MessageTemplate.Create(Guid.Empty, "n", "b", TemplateCategory.Service, null, now));
        await Assert.ThrowsAsync<TenantContextMissingException>(() => bare.SaveChangesAsync());
    }

    [RequiresMySqlFact]
    public async Task Every_messaging_table_returns_nothing_without_a_tenant_and_only_own_rows_with_one()
    {
        var s = await SidesAsync();
        using (var scope = app.Web!.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();   // no tenant set: every filter matches nothing
            Assert.Empty(await db.Channels.ToListAsync()); Assert.Empty(await db.ChannelAccounts.ToListAsync()); Assert.Empty(await db.ChannelCredentials.ToListAsync()); Assert.Empty(await db.Conversations.ToListAsync());
            Assert.Empty(await db.ConversationAssignments.ToListAsync()); Assert.Empty(await db.Messages.ToListAsync()); Assert.Empty(await db.MessageAttachments.ToListAsync()); Assert.Empty(await db.MessageStatusEvents.ToListAsync());
            Assert.Empty(await db.WebhookEvents.ToListAsync()); Assert.Empty(await db.MessageTemplates.ToListAsync()); Assert.Empty(await db.OutboxMessages.ToListAsync()); Assert.Empty(await db.ProcessedMessages.ToListAsync());
            Assert.Empty(await db.ChannelAccountNotifications.ToListAsync());
        }
        await InTenant(W.A.Id, async db =>
        {
            Assert.DoesNotContain(await db.ChannelAccounts.ToListAsync(), x => x.TenantId != W.A.Id); Assert.DoesNotContain(await db.Conversations.ToListAsync(), x => x.TenantId != W.A.Id);
            Assert.DoesNotContain(await db.Messages.ToListAsync(), x => x.TenantId != W.A.Id); Assert.DoesNotContain(await db.WebhookEvents.ToListAsync(), x => x.TenantId != W.A.Id);
            Assert.DoesNotContain(await db.MessageTemplates.ToListAsync(), x => x.TenantId != W.A.Id); Assert.DoesNotContain(await db.OutboxMessages.ToListAsync(), x => x.TenantId != W.A.Id);
            Assert.DoesNotContain(await db.ChannelCredentials.ToListAsync(), x => x.ChannelAccountId == s.B.Id); Assert.DoesNotContain(await db.ProcessedMessages.ToListAsync(), x => x.TenantId != W.A.Id);
            Assert.NotEmpty(await db.Messages.ToListAsync());
        });
    }

    [RequiresMySqlFact]
    public async Task Uniqueness_is_per_tenant_except_for_the_global_session_and_webhook_keys_and_histories_are_append_only()
    {
        var s = await SidesAsync(); var now = DateTime.UtcNow;
        // the same template name, idempotency key and gateway message id may exist in two tenants
        var name = "shared-name-" + Guid.NewGuid().ToString("N")[..6];
        await InTenant(W.A.Id, async db => { db.MessageTemplates.Add(MessageTemplate.Create(W.A.Id, name, "x", TemplateCategory.Service, null, now)); await db.SaveChangesAsync(); });
        await InTenant(W.B.Id, async db => { db.MessageTemplates.Add(MessageTemplate.Create(W.B.Id, name, "x", TemplateCategory.Service, null, now)); await db.SaveChangesAsync(); });
        await InTenant(W.B.Id, async db => { db.MessageTemplates.Add(MessageTemplate.Create(W.B.Id, name, "x", TemplateCategory.Service, null, now)); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); });
        var key = "same-key-" + Guid.NewGuid().ToString("N")[..6]; var ext = "false_same_" + Guid.NewGuid().ToString("N")[..8];
        await InTenant(W.A.Id, async db => { db.Messages.Add(Message.Outbound(W.A.Id, s.ConvA.Id, s.A.Id, null, MessagePurpose.Service, "x", key, "c", null, null, now)); db.Messages.Add(Message.Inbound(W.A.Id, s.ConvA.Id, s.A.Id, ext, "x", now, "c", now)); await db.SaveChangesAsync(); });
        await InTenant(W.B.Id, async db => { db.Messages.Add(Message.Outbound(W.B.Id, s.ConvB.Id, s.B.Id, null, MessagePurpose.Service, "x", key, "c", null, null, now)); db.Messages.Add(Message.Inbound(W.B.Id, s.ConvB.Id, s.B.Id, ext, "x", now, "c", now)); await db.SaveChangesAsync(); });
        await InTenant(W.A.Id, async db => { db.Messages.Add(Message.Inbound(W.A.Id, s.ConvA.Id, s.A.Id, ext, "dup", now, "c", now)); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); });   // same account + gateway id twice: no
        await InTenant(W.A.Id, async db => { db.Messages.Add(Message.Outbound(W.A.Id, s.ConvA.Id, s.A.Id, null, MessagePurpose.Service, "dup", key, "c", null, null, now)); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); });   // same idempotency key twice: no
        // the WAHA session name is unique across ALL tenants
        await InTenant(W.B.Id, async db =>
        {
            var channel = await db.Channels.FirstAsync();
            db.ChannelAccounts.Add(ChannelAccount.Create(W.B.Id, channel.Id, "dup", s.A.Session, null, now));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });
        // histories cannot be edited or deleted
        await InTenant(W.A.Id, async db =>
        {
            var ev = await db.MessageStatusEvents.FirstAsync(); db.Entry(ev).Property(e => e.Applied).CurrentValue = !ev.Applied;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();
            db.MessageStatusEvents.Remove(await db.MessageStatusEvents.FirstAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        });
    }
}
