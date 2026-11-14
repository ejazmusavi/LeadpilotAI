using System.Net;
using System.Text.RegularExpressions;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Messaging;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.IntegrationTests;

/// <summary>The MVC pages: content, the ban-risk warning, forms, delivery status in the thread and tenant isolation (404 for another tenant's ids).</summary>
[Collection("app")]
public class MessagingWebTests(AppFixture app)
{
    private TestWorld W => app.World;
    private static string Phone() => "+9230" + Random.Shared.Next(10_000_000, 99_999_999);

    [RequiresMySqlFact]
    public async Task The_accounts_pages_show_the_ban_risk_warning_the_limits_and_never_the_credentials()
    {
        var a = await MessagingKit.SharedAccountAAsync(app); var web = await WebSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var index = await web.Html("/Channels");
        Assert.Contains("restrict or permanently ban", index); Assert.Contains("not</strong> the official WhatsApp Business Platform", index); Assert.Contains(a.Session, index); Assert.Contains("Add account", index);
        Assert.DoesNotContain(FakeWahaServer.ApiKey, index);
        var details = await web.Html($"/Channels/Details/{a.Id}");
        Assert.Contains("restrict or permanently ban", details); Assert.Contains($"/api/v1/webhooks/waha/{a.WebhookKey}", details); Assert.Contains("https://leadpilot.test", details);
        Assert.DoesNotContain(FakeWahaServer.ApiKey, details); Assert.DoesNotContain(a.Secret, details); Assert.Contains("••••" + FakeWahaServer.ApiKey[^4..], System.Net.WebUtility.HtmlDecode(details));
        Assert.Contains("Pause sending", details); Assert.Contains("Stop session", details); Assert.Contains("Unlink phone", details);
        var form = await web.Html("/Channels/New"); Assert.Contains("restrict or permanently ban", form); Assert.Contains("Test connection", form); Assert.Contains("Sending limits", form);
        // a sales agent (Channels.View only) sees status and usage but neither the webhook address nor management buttons
        var agent = await WebSession.LoginAsync(app, W.ASalesAgent.Email, W.ASalesAgent.Password); var adetails = await agent.Html($"/Channels/Details/{a.Id}");
        Assert.DoesNotContain(a.WebhookKey, adetails); Assert.DoesNotContain("Pause sending", adetails); Assert.DoesNotContain("Danger zone", adetails); Assert.DoesNotContain("Replace credentials", adetails);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.Get($"/Channels/Credentials/{a.Id}")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task An_account_is_added_connected_paused_and_limited_through_the_forms()
    {
        var t = await W.NewTenantAsync("webacc"); var web = await WebSession.LoginAsync(app, t.Admin.Email, t.Admin.Password);
        var session = MessagingKit.NewSession(); var secret = "web-secret-" + Guid.NewGuid().ToString("N");
        // "Test connection" reports the result and does not save anything
        var test = await web.PostWithToken("/Channels/New", "/Channels/Test", ("DisplayName", "Web line"), ("BaseUrl", app.Waha.BaseUrl), ("ApiKey", FakeWahaServer.ApiKey), ("SessionName", session), ("HourlyCap", "30"), ("DailyCap", "150"), ("MinDelaySeconds", "30"));
        Assert.Equal(HttpStatusCode.OK, test.StatusCode); Assert.Contains("Connected to WAHA", await test.Content.ReadAsStringAsync());
        Assert.DoesNotContain("Web line", await web.Html("/Channels"));
        var bad = await web.PostWithToken("/Channels/New", "/Channels/Create", ("DisplayName", "Bad"), ("BaseUrl", app.Waha.BaseUrl), ("ApiKey", "wrong"), ("SessionName", session), ("HourlyCap", "30"), ("DailyCap", "150"), ("MinDelaySeconds", "30"));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode); Assert.Contains("connection test failed", await bad.Content.ReadAsStringAsync());   // stays on the form with the reason
        var create = await web.PostWithToken("/Channels/New", "/Channels/Create", ("DisplayName", "Web line"), ("BaseUrl", app.Waha.BaseUrl), ("ApiKey", FakeWahaServer.ApiKey), ("SessionName", session), ("WebhookSecret", secret), ("HourlyCap", "30"), ("DailyCap", "150"), ("MinDelaySeconds", "30"));
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode); var details = create.Headers.Location!.ToString();
        var id = Guid.Parse(details.Split('/').Last());
        var connect = await web.PostWithToken(details, $"/Channels/Connect/{id}"); Assert.Equal(HttpStatusCode.Redirect, connect.StatusCode);
        var page = await web.Html(details); Assert.Contains("ScanQrCode", page); Assert.Contains("Scan with WhatsApp", page); Assert.Contains("qr-img", page);
        var qr = await web.Get($"/Channels/Qr/{id}"); Assert.Equal(HttpStatusCode.OK, qr.StatusCode); var json = await qr.Content.ReadAsStringAsync(); Assert.Contains("\"status\":\"ScanQrCode\"", json); Assert.Matches("\"png\":\"[A-Za-z0-9+/=]{20,}\"", json); Assert.DoesNotContain(FakeWahaServer.ApiKey, json);
        app.Waha.Working(session);
        Assert.Equal(HttpStatusCode.Redirect, (await web.PostWithToken(details, $"/Channels/Refresh/{id}")).StatusCode); Assert.Contains("Working", await web.Html(details));
        Assert.Equal(HttpStatusCode.Redirect, (await web.PostWithToken(details, $"/Channels/Pause/{id}")).StatusCode); var paused = await web.Html(details); Assert.Contains("Sending is paused", paused); Assert.Contains("Resume sending", paused);
        Assert.Equal(HttpStatusCode.Redirect, (await web.PostWithToken(details, $"/Channels/Resume/{id}")).StatusCode); Assert.DoesNotContain("Sending is paused", await web.Html(details));
        Assert.Equal(HttpStatusCode.Redirect, (await web.PostWithToken(details, $"/Channels/Limits/{id}", ("DisplayName", "Renamed"), ("HourlyCap", "10"), ("DailyCap", "40"), ("MinDelaySeconds", "60"))).StatusCode);
        var limited = await web.Html(details); Assert.Contains("Renamed", limited); Assert.Contains("40", limited);
        var tooMuch = await web.PostWithToken(details, $"/Channels/Limits/{id}", ("DisplayName", "x"), ("HourlyCap", "500"), ("DailyCap", "40"), ("MinDelaySeconds", "60")); Assert.Equal(HttpStatusCode.Redirect, tooMuch.StatusCode);
        Assert.Contains("10", await web.Html(details));   // unchanged: 500 is above the hard ceiling
        var cred = await web.Get($"/Channels/Credentials/{id}"); Assert.Equal(HttpStatusCode.OK, cred.StatusCode); Assert.DoesNotContain(FakeWahaServer.ApiKey, await cred.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await web.PostWithToken(details, $"/Channels/Disconnect/{id}", ("logout", "true"))).StatusCode); Assert.Contains("Stopped", await web.Html(details));
        Assert.Equal(HttpStatusCode.Redirect, (await web.PostWithToken(details, $"/Channels/Remove/{id}")).StatusCode); Assert.DoesNotContain("Renamed", await web.Html("/Channels"));
        Assert.Equal(HttpStatusCode.NotFound, (await web.Get(details)).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task The_inbox_lists_filters_shows_the_thread_with_delivery_status_and_sends_replies()
    {
        var a = await MessagingKit.SharedAccountAAsync(app); var admin = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password); var web = await WebSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        await MessagingKit.PrepareAccountAsync(app, a.Id); app.Waha.ClearFaults(); app.Waha.Working(a.Session);
        await using var worker = MessagingKit.BuildWorker(app);
        var (leadId, _, phone) = await MessagingKit.LeadWithContactAsync(admin);
        var unique = "Fees" + Guid.NewGuid().ToString("N")[..6];
        var conv = await MessagingKit.InboundAsync(app, worker, a, admin, phone, "What are the " + unique + "?");

        var list = await web.Html("/Conversations?search=" + unique); Assert.Contains(unique, list); Assert.Contains("1 new", list); Assert.Contains($"/Conversations/Details/{conv.Id}", list); Assert.Contains("lead:", list);
        Assert.DoesNotContain("No conversations", list);
        Assert.Contains("No conversations", await web.Html("/Conversations?search=zzz-nothing-" + Guid.NewGuid().ToString("N")));
        Assert.Contains($"/Conversations/Details/{conv.Id}", await web.Html("/Conversations?unreadOnly=true&search=" + unique)); Assert.DoesNotContain($"/Conversations/Details/{conv.Id}", await web.Html("/Conversations?unlinkedOnly=true&search=" + unique));   // it is linked to a contact

        var thread = await web.Html($"/Conversations/Details/{conv.Id}");
        Assert.Contains(unique, thread); Assert.Contains($"/Leads/Details/{leadId}", thread); Assert.Contains("service reply", thread); Assert.Contains("Send reply", thread); Assert.Contains("Mark as read", thread);
        var key = Regex.Match(thread, "name=\"IdempotencyKey\" value=\"([^\"]+)\"").Groups[1].Value; Assert.False(string.IsNullOrEmpty(key));
        var reply = await web.PostWithToken($"/Conversations/Details/{conv.Id}", $"/Conversations/Reply/{conv.Id}", ("Text", "Fees are listed on our site."), ("IdempotencyKey", key));
        Assert.Equal(HttpStatusCode.Redirect, reply.StatusCode);
        var again = await web.PostWithToken($"/Conversations/Details/{conv.Id}", $"/Conversations/Reply/{conv.Id}", ("Text", "Fees are listed on our site."), ("IdempotencyKey", key));   // browser resubmit
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        var messages = await MessagingKit.ThreadAsync(admin, conv.Id); var outbound = messages.Where(m => m.Direction == MessageDirection.Outbound).ToList(); Assert.Single(outbound);
        Assert.Contains("Queued", await web.Html($"/Conversations/Details/{conv.Id}"));
        await worker.RunSendAsync(W.A.Id, outbound[0].Id);
        var sentId = (await MessagingKit.MessageAsync(admin, outbound[0].Id)).ExternalMessageId!;
        Assert.Contains("Sent ✓", System.Net.WebUtility.HtmlDecode(await web.Html($"/Conversations/Details/{conv.Id}")));
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Ack(a.Session, sentId, a.ChatId(phone), 3)); await worker.PumpAsync();
        Assert.Contains("Read ✓✓", System.Net.WebUtility.HtmlDecode(await web.Html($"/Conversations/Details/{conv.Id}")));
        Assert.Equal(HttpStatusCode.Redirect, (await web.PostWithToken($"/Conversations/Details/{conv.Id}", $"/Conversations/MarkRead/{conv.Id}")).StatusCode);
        Assert.DoesNotContain("Mark as read", await web.Html($"/Conversations/Details/{conv.Id}"));
        Assert.Equal(HttpStatusCode.Redirect, (await web.PostWithToken($"/Conversations/Details/{conv.Id}", $"/Conversations/SetStatus/{conv.Id}", ("status", "Closed"))).StatusCode);
        Assert.Contains("Closed", await web.Html($"/Conversations?status=Closed&search={unique}"));
        // an empty reply is refused with a readable message, not queued
        var empty = await web.PostWithToken($"/Conversations/Details/{conv.Id}", $"/Conversations/Reply/{conv.Id}", ("Text", "  "), ("IdempotencyKey", Guid.NewGuid().ToString("N"))); Assert.Equal(HttpStatusCode.Redirect, empty.StatusCode);
        Assert.Single(await MessagingKit.ThreadAsync(admin, conv.Id), m => m.Direction == MessageDirection.Outbound);
    }

    [RequiresMySqlFact]
    public async Task A_blocked_conversation_explains_why_instead_of_offering_a_reply_box()
    {
        var a = await MessagingKit.SharedAccountAAsync(app); var admin = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password); var web = await WebSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        await MessagingKit.PrepareAccountAsync(app, a.Id); app.Waha.Working(a.Session); await using var worker = MessagingKit.BuildWorker(app);
        var conv = await MessagingKit.InboundAsync(app, worker, a, admin, Phone(), "STOP");
        var page = await web.Html($"/Conversations/Details/{conv.Id}");
        Assert.Contains("You cannot reply right now", page); Assert.DoesNotContain("Send reply", page); Assert.Contains("asked to stop", page);
        var ro = await WebSession.LoginAsync(app, W.AReadOnly.Email, W.AReadOnly.Password); var rpage = await ro.Html($"/Conversations/Details/{conv.Id}");
        Assert.DoesNotContain("Send reply", rpage); Assert.DoesNotContain("Mark as read", rpage); Assert.DoesNotContain("Close conversation", rpage);   // read-only users see the thread, nothing to click
    }

    [RequiresMySqlFact]
    public async Task Templates_are_created_edited_switched_off_and_deleted_through_the_forms()
    {
        var t = await W.NewTenantAsync("webtpl"); var web = await WebSession.LoginAsync(app, t.Admin.Email, t.Admin.Password);
        Assert.Contains("No templates yet", await web.Html("/Templates"));
        Assert.Equal(HttpStatusCode.Redirect, (await web.PostWithToken("/Templates/New", "/Templates/Save", ("Name", "Greeting"), ("Body", "Hi {{first_name}}!"), ("Category", "Service"))).StatusCode);
        var list = await web.Html("/Templates"); Assert.Contains("Greeting", list); Assert.Contains("Hi {{first_name}}!", list); Assert.Contains("Switch off", list);
        var bad = await web.PostWithToken("/Templates/New", "/Templates/Save", ("Name", "Typo"), ("Body", "Hi {{nmae}}"), ("Category", "Service")); Assert.Equal(HttpStatusCode.OK, bad.StatusCode); Assert.Contains("Unknown placeholder", await bad.Content.ReadAsStringAsync());
        var id = Guid.Parse(Regex.Match(list, @"/Templates/Edit/([0-9a-f-]{36})").Groups[1].Value);
        Assert.Contains("Hi {{first_name}}!", await web.Html($"/Templates/Edit/{id}"));
        Assert.Equal(HttpStatusCode.Redirect, (await web.PostWithToken("/Templates/New", "/Templates/Save", ("Id", id.ToString()), ("Name", "Greeting v2"), ("Body", "Hello {{name}}"), ("Category", "Marketing"))).StatusCode);
        Assert.Contains("Greeting v2", await web.Html("/Templates"));
        Assert.Equal(HttpStatusCode.Redirect, (await web.PostWithToken("/Templates", $"/Templates/Toggle/{id}", ("active", "false"))).StatusCode); Assert.Contains("Switch on", await web.Html("/Templates"));
        Assert.Equal(HttpStatusCode.Redirect, (await web.PostWithToken("/Templates", $"/Templates/Delete/{id}")).StatusCode); Assert.Contains("No templates yet", await web.Html("/Templates"));
    }

    [RequiresMySqlFact]
    public async Task The_mvc_pages_answer_404_for_another_tenants_ids()
    {
        var a = await MessagingKit.SharedAccountAAsync(app); var bAdmin = await ApiSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password); var aAdmin = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        await using var worker = MessagingKit.BuildWorker(app); var convA = await MessagingKit.InboundAsync(app, worker, a, aAdmin, Phone(), "A-only message " + Guid.NewGuid().ToString("N")[..6]);
        var tplA = await ApiSession.Json<TemplateDto>(await aAdmin.Post("/api/v1/message-templates", new { name = "A-only " + Guid.NewGuid().ToString("N")[..6], body = "hi", category = "Service" }));
        var b = await WebSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password);
        foreach (var url in new[] { $"/Conversations/Details/{convA.Id}", $"/Channels/Details/{a.Id}", $"/Channels/Credentials/{a.Id}", $"/Channels/Qr/{a.Id}", $"/Templates/Edit/{tplA.Id}" })
            Assert.Equal(HttpStatusCode.NotFound, (await b.Get(url)).StatusCode);
        foreach (var url in new[] { $"/Conversations/Reply/{convA.Id}", $"/Conversations/MarkRead/{convA.Id}", $"/Conversations/SetStatus/{convA.Id}", $"/Channels/Connect/{a.Id}", $"/Channels/Pause/{a.Id}", $"/Channels/Resume/{a.Id}", $"/Channels/Disconnect/{a.Id}", $"/Channels/Remove/{a.Id}", $"/Channels/Refresh/{a.Id}",
                                    $"/Channels/Acknowledge/{a.Id}", $"/Templates/Toggle/{tplA.Id}", $"/Templates/Delete/{tplA.Id}" })
            Assert.Equal(HttpStatusCode.NotFound, (await b.Post(url, ("Text", "x"), ("status", "Closed"), ("active", "false"))).StatusCode);
        var inbox = await b.Html("/Conversations"); Assert.DoesNotContain("A-only message", inbox); Assert.DoesNotContain(a.Session, await b.Html("/Channels")); Assert.DoesNotContain("A-only", await b.Html("/Templates"));
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Post("/Templates/Save", ("Name", "x"), ("Body", "y"), ("Category", "Service"), ("tenantId", W.A.Id.ToString()))).StatusCode);   // spoofed tenant id in a form
        _ = bAdmin;
    }

    [RequiresMySqlFact]
    public async Task The_webhook_events_page_lists_events_and_the_sidebar_shows_messaging_only_to_those_who_may_use_it()
    {
        var a = await MessagingKit.SharedAccountAAsync(app); var web = await WebSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.SessionStatus(a.Session, "WORKING"));
        var page = await web.Html("/Webhooks"); Assert.Contains("session.status", page); Assert.Contains("Waiting to be processed", page); Assert.Contains("Outbox", page);
        Assert.Contains("session.status", await web.Html("/Webhooks?eventType=session.status")); Assert.Contains("No events", await web.Html("/Webhooks?status=Failed&eventType=message.reaction"));
        var admin = await web.Html("/Home"); Assert.Contains("WhatsApp accounts", admin); Assert.Contains("Webhook events", admin); Assert.Contains("Conversations", admin); Assert.Contains("Message templates", admin); Assert.DoesNotContain("Dead-letter queues", admin);
        var ro = await (await WebSession.LoginAsync(app, W.AReadOnly.Email, W.AReadOnly.Password)).Html("/Home"); Assert.Contains("Conversations", ro); Assert.DoesNotContain("WhatsApp accounts", ro); Assert.DoesNotContain("Webhook events", ro);
        var platform = await (await WebSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword)).Html("/Home"); Assert.Contains("Dead-letter queues", platform);
    }
}
