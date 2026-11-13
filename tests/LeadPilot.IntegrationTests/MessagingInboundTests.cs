using System.Net;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>Webhook → exactly-once storage → outbox → consumer → conversation, contact link, opt-out. MySQL only: the broker is replaced by an in-memory publisher that feeds the REAL consumer processor.</summary>
[Collection("app")]
public class MessagingInboundTests(AppFixture app)
{
    private TestWorld W => app.World;

    private async Task<(TestAccount Account, ApiSession Admin)> AccountAAsync()
    {
        var a = await MessagingKit.SharedAccountAAsync(app);
        return (a, await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password));
    }

    private Task<int> MsgCount(TestAccount a, string chat) => app.Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.ChannelAccountId == a.Id && db.Conversations.IgnoreQueryFilters().Any(c => c.Id == m.ConversationId && c.ExternalChatId == chat)));
    private Task<int> EventCount(TestAccount a, string chat, WebhookEventStatus? status = null) => app.Db(db => db.WebhookEvents.IgnoreQueryFilters().CountAsync(w => w.ChannelAccountId == a.Id && w.PayloadJson.Contains(chat) && (status == null || w.Status == status)));
    private Task<int> OutboxFor(TestAccount a, string chat) => app.Db(db => db.OutboxMessages.IgnoreQueryFilters().CountAsync(o => o.TenantId == a.TenantId && o.Type == "message.received" && db.Messages.IgnoreQueryFilters().Any(m => m.ChannelAccountId == a.Id && o.PayloadJson.Contains(m.Id.ToString()) && db.Conversations.IgnoreQueryFilters().Any(c => c.Id == m.ConversationId && c.ExternalChatId == chat))));

    private static string Phone() => "+9230" + Random.Shared.Next(10_000_000, 99_999_999);

    [RequiresMySqlFact]
    public async Task An_account_is_added_with_encrypted_credentials_and_the_api_never_returns_them()
    {
        var (a, admin) = await AccountAAsync();
        var raw = await (await admin.Get($"/api/v1/channels/accounts/{a.Id}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(FakeWahaServer.ApiKey, raw); Assert.DoesNotContain(a.Secret, raw); Assert.DoesNotContain(a.WebhookKey, raw); Assert.DoesNotContain(app.Waha.BaseUrl, raw);
        Assert.Contains("••••" + FakeWahaServer.ApiKey[^4..], raw);
        var stored = await app.Db(db => db.ChannelCredentials.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.ChannelAccountId == a.Id));
        Assert.DoesNotContain(FakeWahaServer.ApiKey, stored.ProtectedApiKey); Assert.DoesNotContain(app.Waha.BaseUrl, stored.ProtectedBaseUrl); Assert.DoesNotContain(a.Secret, stored.ProtectedWebhookSecret);
        // the audit trail names the account but never a secret
        var audit = string.Join("\n", await app.Db(async db => await db.AuditLogs.IgnoreQueryFilters().Where(x => x.TenantId == W.A.Id && x.Action.StartsWith("Channel.")).Select(x => x.Summary).ToListAsync()));
        Assert.DoesNotContain(FakeWahaServer.ApiKey, audit); Assert.DoesNotContain(a.Secret, audit);
    }

    [RequiresMySqlFact]
    public async Task Connecting_registers_the_webhook_with_the_documented_session_config()
    {
        var (a, _) = await AccountAAsync();
        var s = app.Waha.Sessions[a.Session];
        Assert.Equal($"https://leadpilot.test/api/v1/webhooks/waha/{a.WebhookKey}", s.WebhookUrl);
        Assert.Equal(a.Secret, s.WebhookHmacKey);
        Assert.Equal(["message", "message.ack", "session.status"], s.WebhookEvents);
        // X-Api-Key on every call made for THIS session (other tests share the fake and send wrong keys on purpose)
        var own = app.Waha.CallsTo("/api/sessions").Where(c => c.PathAndQuery.Contains("/" + a.Session, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(own);
        Assert.All(own, c => Assert.Equal(FakeWahaServer.ApiKey, c.ApiKeyHeader));
    }

    [RequiresMySqlFact]
    public async Task An_incoming_message_is_stored_once_and_flows_through_the_outbox_to_the_conversation()
    {
        var (a, admin) = await AccountAAsync(); await using var worker = MessagingKit.BuildWorker(app);
        var phone = Phone(); var chat = a.ChatId(phone);
        var res = await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, chat, "Hello, do you teach maths?", pushName: "Parent"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode); Assert.Equal("accepted", await MessagingKit.StatusOf(res));
        Assert.True(app.Messaging.DispatchTriggers > 0);   // the dispatcher is poked right after the commit

        // before the consumer runs: stored, with an outbox row, and not yet visible in the inbox
        Assert.Equal(1, await MsgCount(a, chat));
        Assert.Equal(1, await OutboxFor(a, chat));
        Assert.DoesNotContain(await MessagingKit.ConversationsAsync(admin), c => c.PeerPhoneE164 == phone);

        await worker.PumpAsync();
        var conv = Assert.Single(await MessagingKit.ConversationsAsync(admin), c => c.PeerPhoneE164 == phone);
        Assert.Equal(1, conv.UnreadCount); Assert.Equal("Hello, do you teach maths?", conv.LastMessagePreview); Assert.Equal("Parent", conv.PeerName); Assert.Null(conv.ContactId);
        var thread = await MessagingKit.ThreadAsync(admin, conv.Id);
        var msg = Assert.Single(thread); Assert.Equal(MessageDirection.Inbound, msg.Direction); Assert.Equal(MessageStatus.Received, msg.Status);
        Assert.Equal(OutboxStatus.Published, await app.Db(async db => (await db.OutboxMessages.IgnoreQueryFilters().FirstAsync(o => o.TenantId == W.A.Id && o.CausationId != null && o.Type == "message.received" && o.PayloadJson.Contains(msg.Id.ToString()))).Status));
        Assert.Equal(WebhookEventStatus.Processed, await app.Db(async db => (await db.WebhookEvents.IgnoreQueryFilters().FirstAsync(w => w.ChannelAccountId == a.Id && w.EventType == "message" && w.PayloadJson.Contains(chat))).Status));
        Assert.Empty(worker.DeadLettered);
    }

    [RequiresMySqlFact]
    public async Task A_duplicate_delivery_of_the_same_event_is_stored_exactly_once()
    {
        var (a, admin) = await AccountAAsync(); await using var worker = MessagingKit.BuildWorker(app);
        var chat = a.ChatId(Phone()); var json = WahaEvents.Message(a.Session, chat, "only once");
        var first = await MessagingKit.PostWebhookAsync(app, a, json); var second = await MessagingKit.PostWebhookAsync(app, a, json); var third = await MessagingKit.PostWebhookAsync(app, a, json);
        Assert.Equal("accepted", await MessagingKit.StatusOf(first)); Assert.Equal("duplicate", await MessagingKit.StatusOf(second)); Assert.Equal("duplicate", await MessagingKit.StatusOf(third));
        Assert.Equal(1, await EventCount(a, chat)); Assert.Equal(1, await MsgCount(a, chat)); Assert.Equal(1, await OutboxFor(a, chat));
        await worker.PumpAsync();
        Assert.Equal(1, Assert.Single(await MessagingKit.ConversationsAsync(admin), c => c.PeerPhoneE164 != null && chat.StartsWith(c.PeerPhoneE164.TrimStart('+'))).UnreadCount);
    }

    [RequiresMySqlFact]
    public async Task Concurrent_identical_webhooks_are_stored_exactly_once()
    {
        var (a, _) = await AccountAAsync(); var chat = a.ChatId(Phone()); var json = WahaEvents.Message(a.Session, chat, "race");
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => MessagingKit.PostWebhookAsync(app, a, json))));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var statuses = await Task.WhenAll(results.Select(MessagingKit.StatusOf));
        Assert.Equal(1, statuses.Count(s => s == "accepted")); Assert.Equal(11, statuses.Count(s => s == "duplicate"));
        Assert.Equal(1, await MsgCount(a, chat)); Assert.Equal(1, await EventCount(a, chat)); Assert.Equal(1, await OutboxFor(a, chat));
        Assert.Equal(1, await app.Db(db => db.Conversations.IgnoreQueryFilters().CountAsync(c => c.ChannelAccountId == a.Id && c.ExternalChatId == chat)));
    }

    [RequiresMySqlFact]
    public async Task Concurrent_different_events_for_a_new_chat_create_one_conversation()
    {
        var (a, _) = await AccountAAsync(); var chat = a.ChatId(Phone());
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, chat, "m" + i)))));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, await app.Db(db => db.Conversations.IgnoreQueryFilters().CountAsync(c => c.ChannelAccountId == a.Id && c.ExternalChatId == chat)));
        Assert.Equal(8, await MsgCount(a, chat));
    }

    [RequiresMySqlFact]
    public async Task The_message_and_message_any_events_for_the_same_message_store_it_once()
    {
        var (a, _) = await AccountAAsync(); var chat = a.ChatId(Phone()); var id = "false_" + chat + "_SAMEMSG0001";
        var one = await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, chat, "hi", messageId: id));
        var any = await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, chat, "hi", messageId: id).Replace("\"event\":\"message\"", "\"event\":\"message.any\""));
        Assert.Equal(HttpStatusCode.OK, one.StatusCode); Assert.Equal(HttpStatusCode.OK, any.StatusCode);
        Assert.Equal(1, await MsgCount(a, chat));
        Assert.Equal(WebhookEventStatus.Ignored, await app.Db(async db => (await db.WebhookEvents.IgnoreQueryFilters().FirstAsync(w => w.ChannelAccountId == a.Id && w.EventType == "message.any" && w.PayloadJson.Contains(chat))).Status));
        Assert.Equal(1, await OutboxFor(a, chat));
    }

    [RequiresMySqlFact]
    public async Task A_message_we_sent_ourselves_or_from_a_group_is_never_stored_as_inbound()
    {
        var (a, _) = await AccountAAsync();
        var mine = a.ChatId(Phone()); var group = "12036" + Random.Shared.Next(100000, 999999) + "@g.us";
        Assert.Equal(HttpStatusCode.OK, (await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, mine, "mine", fromMe: true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, group, "group"))).StatusCode);
        Assert.Equal(0, await MsgCount(a, mine)); Assert.Equal(0, await MsgCount(a, group));
        Assert.Equal(1, await EventCount(a, mine, WebhookEventStatus.Ignored)); Assert.Equal(1, await EventCount(a, group, WebhookEventStatus.Ignored));
    }

    [RequiresMySqlFact]
    public async Task A_webhook_for_an_unknown_account_is_ignored_and_creates_no_data()
    {
        var (a, _) = await AccountAAsync();
        var before = await app.Db(async db => (await db.WebhookEvents.IgnoreQueryFilters().CountAsync(), await db.Messages.IgnoreQueryFilters().CountAsync(), await db.Conversations.IgnoreQueryFilters().CountAsync(), await db.OutboxMessages.IgnoreQueryFilters().CountAsync()));
        foreach (var key in new[] { new string('a', 48), "short", new string('Z', 48), Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")[..16] })
        {
            var r = await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(Phone()), "who?"), keyOverride: key);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode); Assert.Equal("ignored", await MessagingKit.StatusOf(r));
        }
        var after = await app.Db(async db => (await db.WebhookEvents.IgnoreQueryFilters().CountAsync(), await db.Messages.IgnoreQueryFilters().CountAsync(), await db.Conversations.IgnoreQueryFilters().CountAsync(), await db.OutboxMessages.IgnoreQueryFilters().CountAsync()));
        Assert.Equal(before, after);
    }

    [RequiresMySqlFact]
    public async Task Invalid_missing_or_replayed_signatures_are_rejected_and_store_nothing()
    {
        var (a, _) = await AccountAAsync(); var chat = a.ChatId(Phone());
        var json = WahaEvents.Message(a.Session, chat, "forged");
        Assert.Equal(HttpStatusCode.Unauthorized, (await MessagingKit.PostWebhookAsync(app, a, json, secret: "wrong-secret")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MessagingKit.PostWebhookAsync(app, a, json, omitSignature: true)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MessagingKit.PostWebhookAsync(app, a, json, algorithm: "md5")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await MessagingKit.PostWebhookAsync(app, a, json, sentAt: DateTime.UtcNow.AddHours(-3))).StatusCode);   // stale timestamp
        Assert.Equal(0, await EventCount(a, chat));
        // a validly signed body whose session is another account's is not acted upon
        var other = await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message("some-other-session", chat, "wrong session"));
        Assert.Equal("ignored", await MessagingKit.StatusOf(other));
        Assert.Equal(0, await MsgCount(a, chat)); Assert.Equal(0, await EventCount(a, chat));
    }

    [RequiresMySqlFact]
    public async Task Malformed_oversized_and_unsigned_garbage_gets_the_right_status_without_touching_data()
    {
        var (a, _) = await AccountAAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await MessagingKit.PostWebhookAsync(app, a, "{not json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MessagingKit.PostWebhookAsync(app, a, "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await MessagingKit.PostWebhookAsync(app, a, "")).StatusCode);
        var big = a.ChatId(Phone());
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, big, new string('x', 300_000)))).StatusCode);
        Assert.Equal(0, await EventCount(a, big));
    }

    [RequiresMySqlFact]
    public async Task A_webhook_of_a_suspended_tenant_is_refused_and_stores_nothing()
    {
        var t = await W.NewTenantAsync("susp"); var admin = await ApiSession.LoginAsync(app, t.Admin.Email, t.Admin.Password);
        var a = await MessagingKit.CreateAccountAsync(app, admin, t.Tenant.Id);
        var platform = await ApiSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        Assert.True((await platform.Post($"/api/v1/tenants/{t.Tenant.Id}/suspend", new { reason = "test" })).IsSuccessStatusCode);
        var r = await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(Phone()), "hello?"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(0, await app.Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.ChannelAccountId == a.Id)));
        Assert.Equal(0, await app.Db(db => db.WebhookEvents.IgnoreQueryFilters().CountAsync(w => w.ChannelAccountId == a.Id)));
        // and the API is blocked for that tenant's users as well
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Get("/api/v1/conversations")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task The_consumer_links_the_conversation_to_an_existing_contact_by_normalised_phone()
    {
        var (a, admin) = await AccountAAsync(); await using var worker = MessagingKit.BuildWorker(app);
        var (leadId, contactId, e164) = await MessagingKit.LeadWithContactAsync(admin, "+92 301 " + Random.Shared.Next(1_000_000, 9_999_999));   // entered with spaces, stored normalised
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(e164), "I am the parent of Sara"));
        await worker.PumpAsync();
        var conv = Assert.Single(await MessagingKit.ConversationsAsync(admin), c => c.PeerPhoneE164 == e164);
        Assert.Equal(contactId, conv.ContactId); Assert.Equal(leadId, conv.LeadId); Assert.StartsWith("+92301", e164);
        Assert.Single(await MessagingKit.ConversationsAsync(admin, $"&contactId={contactId}"));
    }

    [RequiresMySqlFact]
    public async Task Unknown_numbers_do_not_become_leads_unless_the_tenant_enables_it()
    {
        var (a, admin) = await AccountAAsync(); await using var worker = MessagingKit.BuildWorker(app);
        var phone = Phone(); var leadsBefore = (await Crm.Page<LeadPilot.Application.Crm.LeadListItemDto>(await admin.Get("/api/v1/leads?pageSize=1"))).TotalCount;
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(phone), "new here", pushName: "Walk In"));
        await worker.PumpAsync();
        Assert.Equal(leadsBefore, (await Crm.Page<LeadPilot.Application.Crm.LeadListItemDto>(await admin.Get("/api/v1/leads?pageSize=1"))).TotalCount);   // default: off
        Assert.Null(Assert.Single(await MessagingKit.ConversationsAsync(admin), c => c.PeerPhoneE164 == phone).ContactId);

        Assert.True((await admin.Put("/api/v1/settings/" + OutboundPolicy.AutoCreateLeadsSettingKey, new { value = "true", isSecret = false })).IsSuccessStatusCode);
        try
        {
            var phone2 = Phone();
            await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(phone2), "second visitor", pushName: "Second Visitor"));
            await worker.PumpAsync();
            var conv = Assert.Single(await MessagingKit.ConversationsAsync(admin), c => c.PeerPhoneE164 == phone2);
            Assert.NotNull(conv.ContactId); Assert.Equal("Second Visitor", conv.LeadName);
            Assert.Equal(leadsBefore + 1, (await Crm.Page<LeadPilot.Application.Crm.LeadListItemDto>(await admin.Get("/api/v1/leads?pageSize=1"))).TotalCount);
        }
        finally { await admin.Delete("/api/v1/settings/" + OutboundPolicy.AutoCreateLeadsSettingKey); }
    }

    [RequiresMySqlFact]
    public async Task A_hidden_lid_sender_stays_unlinked_and_is_never_guess_matched()
    {
        var (a, admin) = await AccountAAsync(); await using var worker = MessagingKit.BuildWorker(app);
        var (_, _, phone) = await MessagingKit.LeadWithContactAsync(admin);
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, "55501234567@lid", "who am I"));
        await worker.PumpAsync();
        var conv = Assert.Single(await MessagingKit.ConversationsAsync(admin), c => c.PeerPhoneE164 == null && c.LastMessagePreview == "who am I");
        Assert.Null(conv.ContactId); _ = phone;
    }

    [RequiresMySqlFact]
    public async Task Attachments_are_stored_as_metadata_only_with_the_type_and_size_policy()
    {
        var (a, admin) = await AccountAAsync(); await using var worker = MessagingKit.BuildWorker(app);
        var phone = Phone();
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(phone), "", media: true));
        await worker.PumpAsync();
        var conv = Assert.Single(await MessagingKit.ConversationsAsync(admin), c => c.PeerPhoneE164 == phone);
        var msg = Assert.Single(await MessagingKit.ThreadAsync(admin, conv.Id)); var att = Assert.Single(msg.Attachments);
        Assert.Equal("image/jpeg", att.MimeType); Assert.Equal("x.jpg", att.FileName); Assert.Equal(AttachmentStatus.NotDownloaded, att.Status);
        Assert.Equal("[attachment]", conv.LastMessagePreview);
        Assert.Null((await app.Db(db => db.MessageAttachments.IgnoreQueryFilters().FirstAsync(x => x.MessageId == msg.Id))).StorageKey);
    }
}
