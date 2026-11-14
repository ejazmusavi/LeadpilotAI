using System.Net;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Messaging;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.IntegrationTests;

/// <summary>401/403 for every messaging endpoint (API and MVC) against the real default roles, plus the account management behaviour.</summary>
[Collection("app")]
public class MessagingPermissionTests(AppFixture app)
{
    private TestWorld W => app.World;

    private sealed record Endpoint(string Method, string Url, string Needs, object? Body = null);

    private static IReadOnlyList<Endpoint> ApiEndpoints()
    {
        var id = Guid.NewGuid(); var body = new { text = "x" };
        return
        [
            new("GET", "/api/v1/channels/accounts", PermissionCodes.ChannelsView), new("GET", $"/api/v1/channels/accounts/{id}", PermissionCodes.ChannelsView), new("POST", $"/api/v1/channels/accounts/{id}/refresh", PermissionCodes.ChannelsView),
            new("GET", $"/api/v1/channels/accounts/{id}/notifications", PermissionCodes.ChannelsView),
            new("GET", $"/api/v1/channels/accounts/{id}/setup", PermissionCodes.ChannelsManage), new("POST", "/api/v1/channels/accounts/test-connection", PermissionCodes.ChannelsManage, new { baseUrl = "x", apiKey = "y" }),
            new("POST", "/api/v1/channels/accounts", PermissionCodes.ChannelsManage, new { displayName = "x", baseUrl = "x", apiKey = "y", sessionName = "zzz" }),
            new("PUT", $"/api/v1/channels/accounts/{id}", PermissionCodes.ChannelsManage, new { displayName = "x", hourlyCap = 1, dailyCap = 2, minDelaySeconds = 10 }),
            new("PUT", $"/api/v1/channels/accounts/{id}/credentials", PermissionCodes.ChannelsManage, new { baseUrl = "x", apiKey = "y" }), new("POST", $"/api/v1/channels/accounts/{id}/connect", PermissionCodes.ChannelsManage),
            new("GET", $"/api/v1/channels/accounts/{id}/qr", PermissionCodes.ChannelsManage), new("POST", $"/api/v1/channels/accounts/{id}/disconnect", PermissionCodes.ChannelsManage),
            new("POST", $"/api/v1/channels/accounts/{id}/pause", PermissionCodes.ChannelsManage), new("POST", $"/api/v1/channels/accounts/{id}/resume", PermissionCodes.ChannelsManage),
            new("DELETE", $"/api/v1/channels/accounts/{id}", PermissionCodes.ChannelsManage), new("POST", $"/api/v1/channels/accounts/{id}/notifications/acknowledge", PermissionCodes.ChannelsManage),
            new("GET", "/api/v1/conversations", PermissionCodes.ConversationsView), new("GET", $"/api/v1/conversations/{id}", PermissionCodes.ConversationsView), new("GET", $"/api/v1/conversations/{id}/messages", PermissionCodes.ConversationsView),
            new("POST", $"/api/v1/conversations/{id}/messages", PermissionCodes.ConversationsReply, body), new("POST", $"/api/v1/conversations/{id}/read", PermissionCodes.ConversationsReply),
            new("PUT", $"/api/v1/conversations/{id}/status?status=Closed", PermissionCodes.ConversationsReply, new { }), new("PUT", $"/api/v1/conversations/{id}/ai-mode", PermissionCodes.ConversationsReply, new { mode = "Paused" }),
            new("PUT", $"/api/v1/conversations/{id}/assignee", PermissionCodes.ConversationsReply, new { userId = (Guid?)null }),
            new("POST", "/api/v1/messages", PermissionCodes.MessagesSend, new { contactId = id, channelAccountId = id, text = "x" }), new("GET", $"/api/v1/messages/{id}", PermissionCodes.ConversationsView),
            new("GET", "/api/v1/message-templates", PermissionCodes.ConversationsView), new("GET", $"/api/v1/message-templates/{id}", PermissionCodes.ConversationsView), new("GET", $"/api/v1/message-templates/{id}/preview", PermissionCodes.ConversationsView),
            new("POST", "/api/v1/message-templates", PermissionCodes.ChannelsManage, new { name = "perm", body = "x", category = "Service" }), new("PUT", $"/api/v1/message-templates/{id}", PermissionCodes.ChannelsManage, new { name = "perm", body = "x", category = "Service" }),
            new("PUT", $"/api/v1/message-templates/{id}/active?active=true", PermissionCodes.ChannelsManage, new { }), new("DELETE", $"/api/v1/message-templates/{id}", PermissionCodes.ChannelsManage),
            new("GET", "/api/v1/webhook-events", PermissionCodes.WebhooksView), new("GET", "/api/v1/webhook-events/stats", PermissionCodes.WebhooksView), new("GET", "/api/v1/webhook-events/status", PermissionCodes.WebhooksView),
            new("GET", "/api/v1/admin/dead-letters", PermissionCodes.TenantsManage), new("GET", "/api/v1/admin/dead-letters/x", PermissionCodes.TenantsManage),
        ];
    }

    private static Task<HttpResponseMessage> Call(ApiSession s, Endpoint e) => e.Method switch
    {
        "GET" => s.Get(e.Url), "POST" => s.Post(e.Url, e.Body), "PUT" => s.Put(e.Url, e.Body ?? new { }), "DELETE" => s.Delete(e.Url), _ => throw new NotSupportedException(),
    };

    [RequiresMySqlFact]
    public async Task Anonymous_callers_get_401_on_every_endpoint_except_the_signed_webhook()
    {
        var anon = ApiSession.Anonymous(app);
        foreach (var e in ApiEndpoints()) Assert.True((await Call(anon, e)).StatusCode == HttpStatusCode.Unauthorized, $"{e.Method} {e.Url} should be 401 for anonymous callers");
        var webhook = await anon.Post("/api/v1/webhooks/waha/" + new string('a', 48), new { });
        Assert.NotEqual(HttpStatusCode.Unauthorized, webhook.StatusCode);   // anonymous by design: authenticated by key + signature instead (see the webhook tests)
    }

    [RequiresMySqlFact]
    public async Task Every_endpoint_enforces_its_permission_for_every_default_role()
    {
        var roles = new (TestUser User, string[] Codes)[]
        {
            (W.AReadOnly, SystemRoles.All.Single(r => r.Name == SystemRoles.ReadOnly).Permissions.ToArray()), (W.ASalesAgent, SystemRoles.All.Single(r => r.Name == SystemRoles.SalesAgent).Permissions.ToArray()),
            (W.AMarketing, SystemRoles.All.Single(r => r.Name == SystemRoles.MarketingManager).Permissions.ToArray()), (W.ASalesManager, SystemRoles.All.Single(r => r.Name == SystemRoles.SalesManager).Permissions.ToArray()),
            (W.AUsersOnly, [PermissionCodes.UsersManage]), (W.AAdmin, PermissionCatalog.TenantCodes.ToArray()),
        };
        foreach (var (user, codes) in roles)
        {
            var s = await ApiSession.LoginAsync(app, user.Email, user.Password);
            foreach (var e in ApiEndpoints().Where(e => e.Needs != PermissionCodes.TenantsManage))
            {
                var res = await Call(s, e); var allowed = codes.Contains(e.Needs);
                if (allowed) Assert.True(res.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized), $"{user.Email} should be allowed {e.Method} {e.Url} but got {(int)res.StatusCode}");
                else Assert.True(res.StatusCode == HttpStatusCode.Forbidden, $"{user.Email} must be denied {e.Method} {e.Url} (needs {e.Needs}) but got {(int)res.StatusCode}");
            }
            // dead-letter queues are platform-only: no tenant role (not even Tenant Administrator) may see them
            Assert.Equal(HttpStatusCode.Forbidden, (await s.Get("/api/v1/admin/dead-letters")).StatusCode);
        }
        var platform = await ApiSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        Assert.Equal(HttpStatusCode.OK, (await platform.Get("/api/v1/admin/dead-letters")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.Get("/api/v1/admin/dead-letters/not-a-queue")).StatusCode);   // only the real DLQ names are readable
    }

    [RequiresMySqlFact]
    public async Task Existing_tenants_received_the_new_permissions_through_the_role_sync()
    {
        var perms = await app.Db(async db => await db.RolePermissions.IgnoreQueryFilters().Where(rp => rp.TenantId == W.A.Id).Join(db.Permissions, rp => rp.PermissionId, p => p.Id, (rp, p) => new { rp.RoleId, p.Code }).ToListAsync());
        var adminRole = W.TenantAdminRoleA;
        foreach (var code in new[] { "Channels.View", "Channels.Manage", "Messages.Send", "Webhooks.View" }) Assert.Contains(perms, p => p.RoleId == adminRole && p.Code == code);
        Assert.Equal(4, await app.Db(db => db.Permissions.CountAsync(p => p.Code == "Channels.View" || p.Code == "Channels.Manage" || p.Code == "Messages.Send" || p.Code == "Webhooks.View")));
    }

    // ------------------------------------------------------------------ MVC

    [RequiresMySqlFact]
    public async Task The_mvc_pages_enforce_the_same_permissions()
    {
        var anon = WebSession.Anonymous(app);
        foreach (var url in new[] { "/Channels", "/Channels/New", "/Conversations", "/Templates", "/Webhooks", "/DeadLetters" })
        {
            var r = await anon.Get(url); Assert.True(r.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found, $"{url} anonymous → {(int)r.StatusCode}");
            Assert.Contains("/Account/Login", r.Headers.Location!.ToString());
        }
        var pages = new (string Url, string Needs)[] { ("/Channels", PermissionCodes.ChannelsView), ("/Channels/New", PermissionCodes.ChannelsManage), ("/Conversations", PermissionCodes.ConversationsView), ("/Templates", PermissionCodes.ConversationsView), ("/Templates/New", PermissionCodes.ChannelsManage), ("/Webhooks", PermissionCodes.WebhooksView) };
        foreach (var (user, codes) in new[] { (W.AReadOnly, SystemRoles.All.Single(r => r.Name == SystemRoles.ReadOnly).Permissions.ToArray()), (W.ASalesAgent, SystemRoles.All.Single(r => r.Name == SystemRoles.SalesAgent).Permissions.ToArray()), (W.AAdmin, PermissionCatalog.TenantCodes.ToArray()) })
        {
            var web = await WebSession.LoginAsync(app, user.Email, user.Password);
            foreach (var (url, needs) in pages)
            {
                var r = await web.Get(url);
                if (codes.Contains(needs)) Assert.Equal(HttpStatusCode.OK, r.StatusCode);
                else Assert.True(r.StatusCode == HttpStatusCode.Forbidden, $"{user.Email} {url} → {(int)r.StatusCode}");
            }
            Assert.Equal(HttpStatusCode.Forbidden, (await web.Get("/DeadLetters")).StatusCode);   // platform only
        }
        var platform = await WebSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        var dl = await platform.Get("/DeadLetters"); Assert.Equal(HttpStatusCode.OK, dl.StatusCode); Assert.Contains("Dead-letter queues", await dl.Content.ReadAsStringAsync());
    }

    [RequiresMySqlFact]
    public async Task Mvc_actions_are_denied_without_the_permission_even_with_a_valid_antiforgery_token()
    {
        var agent = await WebSession.LoginAsync(app, W.ASalesAgent.Email, W.ASalesAgent.Password); var id = Guid.NewGuid();
        foreach (var url in new[] { $"/Channels/Connect/{id}", $"/Channels/Pause/{id}", $"/Channels/Resume/{id}", $"/Channels/Disconnect/{id}", $"/Channels/Remove/{id}", $"/Channels/Limits/{id}", $"/Channels/Create", $"/Channels/Test", $"/Templates/Save", $"/Templates/Delete/{id}", $"/Templates/Toggle/{id}" })
            Assert.Equal(HttpStatusCode.Forbidden, (await agent.Post(url, ("DisplayName", "x"))).StatusCode);
        var ro = await WebSession.LoginAsync(app, W.AReadOnly.Email, W.AReadOnly.Password);
        foreach (var url in new[] { $"/Conversations/Reply/{id}", $"/Conversations/MarkRead/{id}", $"/Conversations/SetStatus/{id}" }) Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post(url, ("Text", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Get($"/Channels/Qr/{id}")).StatusCode);
    }
}

[Collection("app")]
public class MessagingAccountTests(AppFixture app)
{
    private TestWorld W => app.World;

    private async Task<(ApiSession Admin, Guid TenantId)> FreshTenantAsync(string prefix) { var t = await W.NewTenantAsync(prefix); return (await ApiSession.LoginAsync(app, t.Admin.Email, t.Admin.Password), t.Tenant.Id); }

    [RequiresMySqlFact]
    public async Task The_connection_test_explains_wrong_keys_unreachable_servers_and_unsafe_addresses_and_nothing_is_saved()
    {
        var (admin, _) = await FreshTenantAsync("conn");
        var ok = await ApiSession.Json<ConnectionTestResult>(await admin.Post("/api/v1/channels/accounts/test-connection", new { baseUrl = app.Waha.BaseUrl, apiKey = FakeWahaServer.ApiKey, sessionName = "nope-session" }));
        Assert.True(ok.Ok); Assert.False(ok.SessionExists); Assert.Contains("created when you connect", ok.Message);
        var badKey = await ApiSession.Json<ConnectionTestResult>(await admin.Post("/api/v1/channels/accounts/test-connection", new { baseUrl = app.Waha.BaseUrl, apiKey = "wrong", sessionName = "x-session" }));
        Assert.False(badKey.Ok); Assert.Contains("API key", badKey.Message);
        var dead = await ApiSession.Json<ConnectionTestResult>(await admin.Post("/api/v1/channels/accounts/test-connection", new { baseUrl = "http://127.0.0.1:1", apiKey = "k", sessionName = "x-session" }));
        Assert.False(dead.Ok); Assert.Contains("could not be reached", dead.Message);
        var meta = await ApiSession.Json<ConnectionTestResult>(await admin.Post("/api/v1/channels/accounts/test-connection", new { baseUrl = "http://169.254.169.254", apiKey = "k", sessionName = "x-session" }));
        Assert.False(meta.Ok); Assert.Contains("not allowed", meta.Message);   // cloud metadata addresses are refused even when private hosts are allowed
        Assert.False((await ApiSession.Json<ConnectionTestResult>(await admin.Post("/api/v1/channels/accounts/test-connection", new { baseUrl = "ftp://x", apiKey = "k" }))).Ok);

        foreach (var bad in new object[]
        {
            new { displayName = "Wrong key", baseUrl = app.Waha.BaseUrl, apiKey = "wrong", sessionName = MessagingKit.NewSession() },
            new { displayName = "Dead", baseUrl = "http://127.0.0.1:1", apiKey = "k", sessionName = MessagingKit.NewSession() },
            new { displayName = "Bad session", baseUrl = app.Waha.BaseUrl, apiKey = FakeWahaServer.ApiKey, sessionName = "../etc" },
            new { displayName = "", baseUrl = app.Waha.BaseUrl, apiKey = FakeWahaServer.ApiKey, sessionName = MessagingKit.NewSession() },
        })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post("/api/v1/channels/accounts", bad)).StatusCode);
        Assert.Empty(await Crm.Ok<List<ChannelAccountDto>>(await admin.Get("/api/v1/channels/accounts")));
    }

    [RequiresMySqlFact]
    public async Task A_workspace_can_have_at_most_five_accounts_and_limits_are_validated()
    {
        var (admin, tenantId) = await FreshTenantAsync("cap");
        TestAccount? first = null;
        for (var i = 0; i < ChannelAccountService5; i++) first ??= await MessagingKit.CreateAccountAsync(app, admin, tenantId, working: false);
        for (var i = 1; i < ChannelAccountService5; i++) await MessagingKit.CreateAccountAsync(app, admin, tenantId, working: false);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Post("/api/v1/channels/accounts", new { displayName = "Sixth", baseUrl = app.Waha.BaseUrl, apiKey = FakeWahaServer.ApiKey, sessionName = MessagingKit.NewSession() })).StatusCode);
        foreach (var bad in new object[] { new { displayName = "x", hourlyCap = 0, dailyCap = 10, minDelaySeconds = 30 }, new { displayName = "x", hourlyCap = 500, dailyCap = 600, minDelaySeconds = 30 }, new { displayName = "x", hourlyCap = 50, dailyCap = 20, minDelaySeconds = 30 },
                                          new { displayName = "x", hourlyCap = 10, dailyCap = 5000, minDelaySeconds = 30 }, new { displayName = "x", hourlyCap = 10, dailyCap = 20, minDelaySeconds = 1 }, new { displayName = " ", hourlyCap = 10, dailyCap = 20, minDelaySeconds = 30 } })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.Put($"/api/v1/channels/accounts/{first!.Id}", bad)).StatusCode);
        var upd = await ApiSession.Json<ChannelAccountDto>(await admin.Put($"/api/v1/channels/accounts/{first!.Id}", new { displayName = "Renamed line", hourlyCap = 10, dailyCap = 40, minDelaySeconds = 45 }));
        Assert.Equal("Renamed line", upd.DisplayName); Assert.Equal(10, upd.HourlyCap); Assert.Equal(40, upd.DailyCap); Assert.Equal(45, upd.MinDelaySeconds);
    }
    private const int ChannelAccountService5 = 5;

    [RequiresMySqlFact]
    public async Task Connect_shows_the_qr_while_waiting_and_disconnect_stops_the_session_and_blocks_sending()
    {
        var (admin, tenantId) = await FreshTenantAsync("qr");
        var a = await MessagingKit.CreateAccountAsync(app, admin, tenantId, working: false);
        var connected = await ApiSession.Json<ChannelAccountDto>(await admin.Post($"/api/v1/channels/accounts/{a.Id}/connect"));
        Assert.Equal(ConnectionStatus.ScanQrCode, connected.Status);
        var qr = await ApiSession.Json<QrCodeDto>(await admin.Get($"/api/v1/channels/accounts/{a.Id}/qr"));
        Assert.Equal(GatewaySessionStatus.ScanQrCode, qr.Status); Assert.False(string.IsNullOrEmpty(qr.PngBase64)); Assert.True(Convert.FromBase64String(qr.PngBase64!).Length > 20);
        app.Waha.Working(a.Session);
        Assert.Equal(ConnectionStatus.Working, (await ApiSession.Json<ChannelAccountDto>(await admin.Post($"/api/v1/channels/accounts/{a.Id}/refresh"))).Status);
        Assert.Null((await ApiSession.Json<QrCodeDto>(await admin.Get($"/api/v1/channels/accounts/{a.Id}/qr"))).PngBase64);   // nothing to scan any more
        var dc = await ApiSession.Json<ChannelAccountDto>(await admin.Post($"/api/v1/channels/accounts/{a.Id}/disconnect?logout=true"));
        Assert.Equal(ConnectionStatus.Stopped, dc.Status); Assert.True(dc.IsPaused); Assert.False(dc.PausedAutomatically);
        Assert.Equal("STOPPED", app.Waha.Sessions[a.Session].Status);
        Assert.Contains(app.Waha.Calls, c => c.PathAndQuery == $"/api/sessions/{a.Session}/logout");
    }

    [RequiresMySqlFact]
    public async Task Connect_is_idempotent_and_reconfigures_an_existing_session_without_creating_a_second_one()
    {
        var (admin, tenantId) = await FreshTenantAsync("idem");
        var a = await MessagingKit.CreateAccountAsync(app, admin, tenantId, working: true);
        for (var i = 0; i < 3; i++) Assert.True((await admin.Post($"/api/v1/channels/accounts/{a.Id}/connect")).IsSuccessStatusCode);
        Assert.Single(app.Waha.Sessions.Values, s => s.Name == a.Session);
        Assert.Contains(app.Waha.Calls, c => c.Method == "PUT" && c.PathAndQuery == $"/api/sessions/{a.Session}");
        var body = app.Waha.Calls.Last(c => c.Method == "PUT" && c.PathAndQuery == $"/api/sessions/{a.Session}").Body!;
        Assert.Contains("\"hmac\":{\"key\":\"" + a.Secret + "\"}", body); Assert.Contains("\"groups\":true", body); Assert.Contains("\"tagsEventsOn\":true", body);
    }

    [RequiresMySqlFact]
    public async Task Rotating_credentials_tests_them_first_updates_the_hint_and_never_echoes_them()
    {
        var (admin, tenantId) = await FreshTenantAsync("rot");
        var a = await MessagingKit.CreateAccountAsync(app, admin, tenantId, working: false);
        var before = await app.Db(db => db.ChannelCredentials.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.ChannelAccountId == a.Id));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Put($"/api/v1/channels/accounts/{a.Id}/credentials", new { baseUrl = app.Waha.BaseUrl, apiKey = "not-the-key" })).StatusCode);
        Assert.Equal(before.ProtectedApiKey, (await app.Db(db => db.ChannelCredentials.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.ChannelAccountId == a.Id))).ProtectedApiKey);   // unchanged after a failed test
        var newSecret = "rotated-secret-" + Guid.NewGuid().ToString("N");
        var res = await admin.Put($"/api/v1/channels/accounts/{a.Id}/credentials", new { baseUrl = app.Waha.BaseUrl + "/", apiKey = FakeWahaServer.ApiKey, webhookSecret = newSecret });
        Assert.True(res.IsSuccessStatusCode); var raw = await res.Content.ReadAsStringAsync(); Assert.DoesNotContain(newSecret, raw); Assert.DoesNotContain(FakeWahaServer.ApiKey, raw);
        var after = await app.Db(db => db.ChannelCredentials.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.ChannelAccountId == a.Id));
        Assert.NotEqual(before.ProtectedWebhookSecret, after.ProtectedWebhookSecret);
        Assert.DoesNotContain(newSecret, string.Join("\n", await app.Db(async db => await db.AuditLogs.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).Select(x => x.Summary).ToListAsync())));
        // after "Connect" the new secret is what WAHA signs with: an event signed with it is accepted, the old one is not
        await admin.Post($"/api/v1/channels/accounts/{a.Id}/connect");
        Assert.Equal(newSecret, app.Waha.Sessions[a.Session].WebhookHmacKey);
        var chat = "92300" + Random.Shared.Next(1000000, 9999999) + "@c.us";
        Assert.Equal(HttpStatusCode.Unauthorized, (await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, chat, "old secret"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, chat, "new secret"), secret: newSecret)).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task A_removed_account_stops_accepting_webhooks_and_disappears_from_the_list_but_history_stays_readable()
    {
        var (admin, tenantId) = await FreshTenantAsync("rm");
        var a = await MessagingKit.CreateAccountAsync(app, admin, tenantId); await using var worker = MessagingKit.BuildWorker(app);
        var phone = "+9230" + Random.Shared.Next(10_000_000, 99_999_999);
        var conv = await MessagingKit.InboundAsync(app, worker, a, admin, phone, "before removal");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Delete($"/api/v1/channels/accounts/{a.Id}")).StatusCode);
        Assert.Empty(await Crm.Ok<List<ChannelAccountDto>>(await admin.Get("/api/v1/channels/accounts")));
        Assert.Equal("ignored", await MessagingKit.StatusOf(await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(phone), "after removal"))));
        Assert.Single(await MessagingKit.ThreadAsync(admin, conv.Id));
        var detail = await Crm.Ok<ConversationDto>(await admin.Get($"/api/v1/conversations/{conv.Id}")); Assert.False(detail.CanReply);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Post($"/api/v1/channels/accounts/{a.Id}/refresh")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task A_gateway_that_is_down_gives_a_readable_error_and_recovers()
    {
        var (admin, tenantId) = await FreshTenantAsync("down");
        var a = await MessagingKit.CreateAccountAsync(app, admin, tenantId);
        app.Waha.Offline = true;
        try
        {
            var r = await admin.Post($"/api/v1/channels/accounts/{a.Id}/refresh");
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.False(string.IsNullOrWhiteSpace(await Crm.ProblemDetail(r)));
            Assert.Equal(HttpStatusCode.OK, (await admin.Get($"/api/v1/channels/accounts/{a.Id}")).StatusCode);   // the page still works from what we know
        }
        finally { app.Waha.Offline = false; }
        Assert.True((await admin.Post($"/api/v1/channels/accounts/{a.Id}/refresh")).IsSuccessStatusCode);
    }

    [RequiresMySqlFact]
    public async Task Templates_validate_names_placeholders_and_deletion_rules()
    {
        var (admin, tenantId) = await FreshTenantAsync("tpl");
        var t = await ApiSession.Json<TemplateDto>(await admin.Post("/api/v1/message-templates", new { name = "Welcome", body = "Hi {{first_name}} at {{business_name}}", category = "Marketing" }));
        Assert.Equal(["first_name", "business_name"], t.Variables); Assert.True(t.IsActive);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Post("/api/v1/message-templates", new { name = "Welcome", body = "again", category = "Service" })).StatusCode);   // names are unique per tenant
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post("/api/v1/message-templates", new { name = "Bad", body = "{{unknown_var}}", category = "Service" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post("/api/v1/message-templates", new { name = "Empty", body = "  ", category = "Service" })).StatusCode);
        var preview = await ApiSession.Json<TemplatePreviewDto>(await admin.Get($"/api/v1/message-templates/{t.Id}/preview")); Assert.Contains("Sample", preview.Rendered);
        var off = await ApiSession.Json<TemplateDto>(await admin.Put($"/api/v1/message-templates/{t.Id}/active?active=false", new { })); Assert.False(off.IsActive);
        Assert.DoesNotContain(await Crm.Ok<List<TemplateDto>>(await admin.Get("/api/v1/message-templates?activeOnly=true")), x => x.Id == t.Id);
        var upd = await ApiSession.Json<TemplateDto>(await admin.Put($"/api/v1/message-templates/{t.Id}", new { name = "Welcome v2", body = "Hello {{name}}", category = "Service" })); Assert.Equal("Welcome v2", upd.Name);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Delete($"/api/v1/message-templates/{t.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Get($"/api/v1/message-templates/{t.Id}")).StatusCode);
        _ = tenantId;
    }
}
