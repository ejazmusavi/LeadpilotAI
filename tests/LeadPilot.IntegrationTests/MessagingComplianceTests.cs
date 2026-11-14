using System.Net;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static LeadPilot.IntegrationTests.FakeWahaServer;

namespace LeadPilot.IntegrationTests;

public sealed record EligibilityDto(bool IsEligible);

/// <summary>Opt-out, consent, suppression and the service/marketing policy, plus account health (auto-pause, restrictions).</summary>
[Collection("app")]
public class MessagingComplianceTests(AppFixture app)
{
    private TestWorld W => app.World;
    private static string Phone() => "+9230" + Random.Shared.Next(10_000_000, 99_999_999);

    private async Task<(TestAccount A, ApiSession Admin, WorkerHarness Worker)> ArrangeAsync()
    {
        var a = await MessagingKit.SharedAccountAAsync(app); var admin = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        await MessagingKit.PrepareAccountAsync(app, a.Id); app.Waha.ClearFaults(); app.Waha.Working(a.Session);
        return (a, admin, MessagingKit.BuildWorker(app));
    }

    private async Task<(Guid ContactId, string Phone)> ContactAsync(ApiSession admin, bool consent)
    {
        var (_, contactId, phone) = await MessagingKit.LeadWithContactAsync(admin);
        if (consent) await MessagingKit.GrantConsentAsync(admin, contactId);
        return (contactId, phone);
    }

    private Task<List<SuppressionEntry>> SuppressionFor(string phone) => app.Db(db => db.SuppressionEntries.IgnoreQueryFilters().AsNoTracking().Where(s => s.TenantId == W.A.Id && s.ValueNormalized == phone).ToListAsync());

    private async Task<HttpResponseMessage> SendToContact(ApiSession s, TestAccount a, Guid contactId, string text = "Hello from the school", string? key = null) =>
        await s.Post("/api/v1/messages", new { contactId, channelAccountId = a.Id, text, idempotencyKey = key });

    [RequiresMySqlFact]
    public async Task A_stop_message_suppresses_the_number_revokes_consent_with_audit_and_blocks_later_sends()
    {
        var (a, admin, worker) = await ArrangeAsync(); await using var wk = worker;
        var (contactId, phone) = await ContactAsync(admin, consent: true);
        Assert.True((await Crm.Ok<EligibilityDto>(await admin.Get($"/api/v1/contacts/{contactId}/eligibility?channel=WhatsApp&purpose=Marketing"))).IsEligible);
        var conv = await MessagingKit.InboundAsync(app, worker, a, admin, phone, "STOP");

        var entry = Assert.Single(await SuppressionFor(phone)); Assert.Equal(SuppressionReason.OptOut, entry.Reason); Assert.Equal(SuppressionChannel.WhatsApp, entry.Channel);
        Assert.False((await Crm.Ok<EligibilityDto>(await admin.Get($"/api/v1/contacts/{contactId}/eligibility?channel=WhatsApp&purpose=Marketing"))).IsEligible);
        var consent = await Crm.Page<ConsentRecordDto>(await admin.Get($"/api/v1/contacts/{contactId}/consent"));
        Assert.Equal(ConsentStatus.Revoked, consent.Items.First().Status); Assert.Equal("WhatsApp opt-out message", consent.Items.First().Source);
        var audit = await app.Db(db => db.AuditLogs.IgnoreQueryFilters().Where(x => x.TenantId == W.A.Id).OrderByDescending(x => x.CreatedAtUtc).Take(60).Select(x => x.Action).ToListAsync());
        Assert.Contains("Messaging.OptOut", audit); Assert.Contains("Suppression.Added", audit); Assert.Contains("Consent.Revoked", audit);
        Assert.True(await app.Db(async db => (await db.Conversations.IgnoreQueryFilters().FirstAsync(c => c.Id == conv.Id)).OptedOutAtUtc != null));

        // nothing can be sent any more: marketing, and even a "reply" in the conversation the person just wrote in
        var marketing = await SendToContact(admin, a, contactId); Assert.Equal(HttpStatusCode.Conflict, marketing.StatusCode); Assert.Equal("opted_out", await Crm.ProblemCode(marketing));
        var reply = await admin.Post($"/api/v1/conversations/{conv.Id}/messages", new { text = "Are you sure?" }); Assert.Equal(HttpStatusCode.Conflict, reply.StatusCode);
        var detail = await Crm.Ok<ConversationDto>(await admin.Get($"/api/v1/conversations/{conv.Id}")); Assert.False(detail.CanReply); Assert.Contains("stop", detail.CannotReplyReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(app.Waha.SentTo(a.ChatId(phone)));
    }

    [RequiresMySqlFact]
    public async Task Redelivering_the_opt_out_event_does_not_duplicate_the_revocation()
    {
        var (a, admin, worker) = await ArrangeAsync(); await using var wk = worker;
        var (contactId, phone) = await ContactAsync(admin, consent: true);
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(phone), "unsubscribe"));
        await worker.DispatchAsync();
        var env = worker.Publisher.Published.Single(e => e.Type == "message.received");
        worker.Publisher.Published.Enqueue(env); worker.Publisher.Published.Enqueue(env);   // the broker delivers the same message three times
        await worker.PumpAsync();
        var revocations = await app.Db(db => db.ConsentRecords.IgnoreQueryFilters().CountAsync(r => r.ContactId == contactId && r.Status == ConsentStatus.Revoked));
        Assert.Equal(1, revocations); Assert.Single(await SuppressionFor(phone));
        Assert.Equal(1, await app.Db(db => db.ProcessedMessages.IgnoreQueryFilters().CountAsync(p => p.TenantId == W.A.Id && p.MessageId == env.MessageId && p.ConsumerName == "messaging.inbound")));
        Assert.Equal(1, (await MessagingKit.ConversationsAsync(admin)).Single(c => c.PeerPhoneE164 == phone).UnreadCount);
    }

    [RequiresMySqlFact]
    public async Task Opt_outs_in_english_roman_urdu_and_urdu_are_all_honoured()
    {
        var (a, admin, worker) = await ArrangeAsync(); await using var wk = worker;
        foreach (var phrase in new[] { "Unsubscribe", "cancel", "stop sending me messages", "Band karo", "mat bhejo", "bas karo", "nahi chahiye", "بند کریں", "بس کرو", "مت بھیجو" })
        {
            var (contactId, phone) = await ContactAsync(admin, consent: true);
            await MessagingKit.InboundAsync(app, worker, a, admin, phone, phrase);
            Assert.True((await SuppressionFor(phone)).Count == 1, $"'{phrase}' was not treated as an opt-out");
            Assert.False((await Crm.Ok<EligibilityDto>(await admin.Get($"/api/v1/contacts/{contactId}/eligibility?channel=WhatsApp&purpose=Marketing"))).IsEligible);
        }
    }

    [RequiresMySqlFact]
    public async Task Sentences_that_merely_contain_a_keyword_are_normal_messages_and_suppress_nobody()
    {
        var (a, admin, worker) = await ArrangeAsync(); await using var wk = worker;
        foreach (var text in new[] { "don't stop now", "please stop by tomorrow", "cancel my order #5 and send the invoice", "I want to unsubscribe from the newsletter", "band karo darwaza" })
        {
            var (contactId, phone) = await ContactAsync(admin, consent: true);
            var conv = await MessagingKit.InboundAsync(app, worker, a, admin, phone, text);
            Assert.Empty(await SuppressionFor(phone));
            Assert.True((await Crm.Ok<EligibilityDto>(await admin.Get($"/api/v1/contacts/{contactId}/eligibility?channel=WhatsApp&purpose=Marketing"))).IsEligible);
            Assert.True((await Crm.Ok<ConversationDto>(await admin.Get($"/api/v1/conversations/{conv.Id}"))).CanReply);
        }
    }

    [RequiresMySqlFact]
    public async Task A_tenant_can_replace_the_opt_out_keyword_list()
    {
        var (a, admin, worker) = await ArrangeAsync(); await using var wk = worker;
        Assert.True((await admin.Put("/api/v1/settings/" + OptOutDetector.SettingKey, new { value = "khatam\nbasta", isSecret = false })).IsSuccessStatusCode);
        try
        {
            var p1 = Phone(); await MessagingKit.InboundAsync(app, worker, a, admin, p1, "KHATAM");
            Assert.Single(await SuppressionFor(p1));
            var p2 = Phone(); await MessagingKit.InboundAsync(app, worker, a, admin, p2, "stop");   // not in the tenant's list any more
            Assert.Empty(await SuppressionFor(p2));
        }
        finally { await admin.Delete("/api/v1/settings/" + OptOutDetector.SettingKey); }
    }

    [RequiresMySqlFact]
    public async Task An_unknown_number_and_a_hidden_id_that_opt_out_are_both_respected()
    {
        var (a, admin, worker) = await ArrangeAsync(); await using var wk = worker;
        var unknown = Phone(); var conv = await MessagingKit.InboundAsync(app, worker, a, admin, unknown, "STOP");   // not a contact: the number itself is suppressed
        Assert.Single(await SuppressionFor(unknown));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Post($"/api/v1/conversations/{conv.Id}/messages", new { text = "hello?" })).StatusCode);

        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, "77700011122@lid", "STOP")); await worker.PumpAsync();
        var hidden = (await MessagingKit.ConversationsAsync(admin)).Single(c => c.PeerPhoneE164 == null && c.LastMessagePreview == "STOP");
        var r = await admin.Post($"/api/v1/conversations/{hidden.Id}/messages", new { text = "hello?" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("opted_out", await Crm.ProblemCode(r));   // no number to suppress, so the chat itself is blocked
    }

    [RequiresMySqlFact]
    public async Task A_message_queued_before_the_opt_out_is_stopped_at_send_time()
    {
        var (a, admin, worker) = await ArrangeAsync(); await using var wk = worker;
        var (_, phone) = await ContactAsync(admin, consent: true);
        var conv = await MessagingKit.InboundAsync(app, worker, a, admin, phone, "hi, tell me about fees");
        var queued = await MessagingKit.ReplyAsync(admin, conv.Id, "Our fees are ...");
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(phone), "STOP")); await worker.PumpAsync();   // the opt-out arrives while the message waits
        await worker.RunSendAsync(W.A.Id, queued.Id);
        var after = await MessagingKit.MessageAsync(admin, queued.Id);
        Assert.Equal(MessageStatus.Failed, after.Status); Assert.True(after.FailureCode is "suppressed" or "opted_out", after.FailureCode);
        Assert.Empty(app.Waha.SentTo(a.ChatId(phone)));
    }

    [RequiresMySqlFact]
    public async Task Marketing_needs_recorded_consent_and_is_checked_again_just_before_sending()
    {
        var (a, admin, worker) = await ArrangeAsync(); await using var wk = worker;
        var (noConsent, p1) = await ContactAsync(admin, consent: false);
        var refused = await SendToContact(admin, a, noConsent); Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode); Assert.Equal("not_eligible", await Crm.ProblemCode(refused));
        Assert.Contains("consent", await Crm.ProblemDetail(refused), StringComparison.OrdinalIgnoreCase);

        var (withConsent, p2) = await ContactAsync(admin, consent: true);
        var ok = await SendToContact(admin, a, withConsent, "Welcome to our open day"); Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        var msg = await ApiSession.Json<MessageDto>(ok); Assert.Equal(MessagePurpose.Marketing, msg.Purpose);
        await worker.RunSendAsync(W.A.Id, msg.Id);
        Assert.Equal(MessageStatus.Sent, (await MessagingKit.MessageAsync(admin, msg.Id)).Status); Assert.Equal("Welcome to our open day", app.Waha.SentTo(a.ChatId(p2)).Single().Text);

        // consent withdrawn between queueing and sending
        await MessagingKit.PrepareAccountAsync(app, a.Id);
        var (revoked, p3) = await ContactAsync(admin, consent: true);
        var queued = await ApiSession.Json<MessageDto>(await SendToContact(admin, a, revoked, "Promotion!"));
        Assert.True((await admin.Post($"/api/v1/contacts/{revoked}/consent", new { channel = "WhatsApp", purpose = "Marketing", status = "Revoked", source = "phone call" })).IsSuccessStatusCode);
        await worker.RunSendAsync(W.A.Id, queued.Id);
        var failed = await MessagingKit.MessageAsync(admin, queued.Id); Assert.Equal(MessageStatus.Failed, failed.Status); Assert.Equal("not_eligible", failed.FailureCode);
        Assert.Empty(app.Waha.SentTo(a.ChatId(p3))); _ = p1;
    }

    [RequiresMySqlFact]
    public async Task A_suppressed_contact_is_never_messaged_even_with_consent()
    {
        var (a, admin, worker) = await ArrangeAsync(); await using var wk = worker;
        var (contactId, phone) = await ContactAsync(admin, consent: true);
        Assert.True((await admin.Post("/api/v1/suppression", new { channel = "WhatsApp", value = phone, reason = "Blocked" })).IsSuccessStatusCode);
        var r = await SendToContact(admin, a, contactId); Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("suppressed", await Crm.ProblemCode(r));
        Assert.Empty(app.Waha.SentTo(a.ChatId(phone)));
    }

    [RequiresMySqlFact]
    public async Task Replies_inside_the_customer_window_are_service_messages_and_outside_it_become_marketing()
    {
        var (a, admin, worker) = await ArrangeAsync(); await using var wk = worker;
        var (_, phone) = await ContactAsync(admin, consent: false);          // no marketing consent at all
        var conv = await MessagingKit.InboundAsync(app, worker, a, admin, phone, "hello");
        var detail = await Crm.Ok<ConversationDto>(await admin.Get($"/api/v1/conversations/{conv.Id}"));
        Assert.True(detail.CanReply); Assert.Equal(MessagePurpose.Service, detail.ReplyPurpose); Assert.True(detail.InsideServiceWindow);
        var msg = await MessagingKit.ReplyAsync(admin, conv.Id, "service answer"); Assert.Equal(MessagePurpose.Service, msg.Purpose);

        app.Clock.Offset = TimeSpan.FromHours(25);                              // the customer has been silent for 25 hours
        try
        {
            var late = await Crm.Ok<ConversationDto>(await admin.Get($"/api/v1/conversations/{conv.Id}"));
            Assert.False(late.CanReply); Assert.Equal(MessagePurpose.Marketing, late.ReplyPurpose); Assert.False(late.InsideServiceWindow);
            var r = await admin.Post($"/api/v1/conversations/{conv.Id}/messages", new { text = "a promotion" });
            Assert.Equal(HttpStatusCode.Conflict, r.StatusCode); Assert.Equal("not_eligible", await Crm.ProblemCode(r));
        }
        finally { app.Clock.Offset = TimeSpan.Zero; }
    }

    [RequiresMySqlFact]
    public async Task A_conversation_with_a_number_that_is_not_a_contact_cannot_be_marketed_to_outside_the_window()
    {
        var (a, admin, worker) = await ArrangeAsync(); await using var wk = worker;
        var conv = await MessagingKit.InboundAsync(app, worker, a, admin, Phone(), "who are you");
        app.Clock.Offset = TimeSpan.FromHours(30);
        try { Assert.Equal(HttpStatusCode.Conflict, (await admin.Post($"/api/v1/conversations/{conv.Id}/messages", new { text = "special offer" })).StatusCode); }
        finally { app.Clock.Offset = TimeSpan.Zero; }
    }

    // ------------------------------------------------------------------ account health

    private async Task<(TestAccount A, ApiSession Admin, WorkerHarness Worker, Guid TenantId)> OwnAccountAsync()
    {
        var t = await W.NewTenantAsync("health"); var admin = await ApiSession.LoginAsync(app, t.Admin.Email, t.Admin.Password);
        var a = await MessagingKit.CreateAccountAsync(app, admin, t.Tenant.Id, hourly: 120, daily: 1000, delay: 10);
        await MessagingKit.PrepareAccountAsync(app, a.Id);
        return (a, admin, MessagingKit.BuildWorker(app), t.Tenant.Id);
    }

    [RequiresMySqlFact]
    public async Task Repeated_send_failures_pause_the_account_with_a_notice_and_resume_is_a_deliberate_act()
    {
        var (a, admin, worker, tenantId) = await OwnAccountAsync(); await using var wk = worker;
        var convs = new List<ConversationListItemDto>(); for (var i = 0; i < 4; i++) convs.Add(await MessagingKit.InboundAsync(app, worker, a, admin, Phone(), "hi " + i));
        for (var i = 0; i < 3; i++)
        {
            app.Waha.FaultNext(SendFault.Http401);
            var m = await MessagingKit.ReplyAsync(admin, convs[i].Id, "failing " + i); await worker.RunSendAsync(tenantId, m.Id);
            Assert.Equal("gateway_unauthorized", (await MessagingKit.MessageAsync(admin, m.Id)).FailureCode);
        }
        var acc = await ApiSession.Json<ChannelAccountDto>(await admin.Get($"/api/v1/channels/accounts/{a.Id}"));
        Assert.True(acc.IsPaused); Assert.True(acc.PausedAutomatically); Assert.Contains("3 failed sends", acc.PauseReason); Assert.True(acc.UnacknowledgedNotifications >= 1);
        var notes = await Crm.Ok<IReadOnlyList<AccountNotificationDto>>(await admin.Get($"/api/v1/channels/accounts/{a.Id}/notifications"));
        Assert.Contains(notes, n => n.Kind == NotificationKind.AccountPaused);
        var blocked = await admin.Post($"/api/v1/conversations/{convs[3].Id}/messages", new { text = "now blocked" }); Assert.Equal("account_paused", await Crm.ProblemCode(blocked));
        Assert.True(await app.Db(db => db.AuditLogs.IgnoreQueryFilters().AnyAsync(x => x.TenantId == tenantId && x.Action == "Channel.AutoPaused")));

        Assert.True((await admin.Post($"/api/v1/channels/accounts/{a.Id}/resume")).IsSuccessStatusCode);
        var m4 = await MessagingKit.ReplyAsync(admin, convs[3].Id, "works again"); await MessagingKit.PrepareAccountAsync(app, a.Id); await worker.RunSendAsync(tenantId, m4.Id);
        Assert.Equal(MessageStatus.Sent, (await MessagingKit.MessageAsync(admin, m4.Id)).Status);
    }

    [RequiresMySqlFact]
    public async Task Pausing_after_queueing_fails_the_message_instead_of_sending_it_later()
    {
        var (a, admin, worker, tenantId) = await OwnAccountAsync(); await using var wk = worker;
        var conv = await MessagingKit.InboundAsync(app, worker, a, admin, Phone(), "hi");
        var m = await MessagingKit.ReplyAsync(admin, conv.Id, "queued then paused");
        Assert.True((await admin.Post($"/api/v1/channels/accounts/{a.Id}/pause")).IsSuccessStatusCode);
        await worker.RunSendAsync(tenantId, m.Id);
        Assert.Equal("account_paused", (await MessagingKit.MessageAsync(admin, m.Id)).FailureCode); Assert.Empty(app.Waha.SentTo(conv.PeerPhoneE164 is null ? "" : a.ChatId(conv.PeerPhoneE164)));
    }

    [RequiresMySqlFact]
    public async Task A_failed_or_stopped_session_pauses_the_account_and_a_reconnect_does_not_silently_resume_it()
    {
        var (a, admin, worker, _) = await OwnAccountAsync(); await using var wk = worker;
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.SessionStatus(a.Session, "FAILED")); await worker.PumpAsync();
        var failed = await ApiSession.Json<ChannelAccountDto>(await admin.Get($"/api/v1/channels/accounts/{a.Id}"));
        Assert.Equal(ConnectionStatus.Failed, failed.Status); Assert.True(failed.IsPaused); Assert.True(failed.PausedAutomatically);
        Assert.Contains(await Crm.Ok<IReadOnlyList<AccountNotificationDto>>(await admin.Get($"/api/v1/channels/accounts/{a.Id}/notifications")), n => n.Kind == NotificationKind.AccountPaused || n.Kind == NotificationKind.SessionDisconnected);
        var conv = await MessagingKit.InboundAsync(app, worker, a, admin, Phone(), "hello?");
        Assert.Equal("account_paused", await Crm.ProblemCode(await admin.Post($"/api/v1/conversations/{conv.Id}/messages", new { text = "x" })));
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.SessionStatus(a.Session, "WORKING")); await worker.PumpAsync();
        var back = await ApiSession.Json<ChannelAccountDto>(await admin.Get($"/api/v1/channels/accounts/{a.Id}"));
        Assert.Equal(ConnectionStatus.Working, back.Status); Assert.True(back.IsPaused);   // a human resumes
    }

    [RequiresMySqlFact]
    public async Task A_whatsapp_reachout_restriction_blocks_new_contacts_only_and_is_shown_to_the_owner()
    {
        var (a, admin, worker, tenantId) = await OwnAccountAsync(); await using var wk = worker;
        var existing = await MessagingKit.InboundAsync(app, worker, a, admin, Phone(), "I wrote first");
        var until = DateTimeOffset.UtcNow.AddHours(5).ToUnixTimeSeconds();
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.SessionStatus(a.Session, "WORKING", data: new { reachoutTimelock = new { enforcementType = "RESTRICT_ALL_COMPANIONS", isActive = true, timeEnforcementEnds = until } }));
        await worker.PumpAsync();
        var acc = await ApiSession.Json<ChannelAccountDto>(await admin.Get($"/api/v1/channels/accounts/{a.Id}"));
        Assert.NotNull(acc.NewContactBlockedUntilUtc); Assert.False(acc.IsPaused);   // the session is healthy: do not restart or pause it
        Assert.Contains(await Crm.Ok<IReadOnlyList<AccountNotificationDto>>(await admin.Get($"/api/v1/channels/accounts/{a.Id}/notifications")), n => n.Kind == NotificationKind.ReachoutTimelock);

        var (contactId, _) = await ContactAsync(admin, consent: true);
        var newContact = await SendToContact(admin, a, contactId); Assert.Equal("new_contacts_blocked", await Crm.ProblemCode(newContact));
        var reply = await MessagingKit.ReplyAsync(admin, existing.Id, "existing chats still work");
        await MessagingKit.PrepareAccountAsync(app, a.Id, resetPacing: true); await worker.RunSendAsync(tenantId, reply.Id);
        Assert.Equal(MessageStatus.Sent, (await MessagingKit.MessageAsync(admin, reply.Id)).Status);

        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.SessionStatus(a.Session, "WORKING", data: new { reachoutTimelock = new { isActive = false } })); await worker.PumpAsync();
        Assert.Null((await ApiSession.Json<ChannelAccountDto>(await admin.Get($"/api/v1/channels/accounts/{a.Id}"))).NewContactBlockedUntilUtc);
    }

    [RequiresMySqlFact]
    public async Task A_463_refusal_from_whatsapp_fails_that_message_blocks_new_contacts_and_is_not_a_pause_or_a_retry()
    {
        var (a, admin, worker, tenantId) = await OwnAccountAsync(); await using var wk = worker;
        var (contactId, phone) = await ContactAsync(admin, consent: true);
        var queued = await ApiSession.Json<MessageDto>(await SendToContact(admin, a, contactId, "first contact"));
        app.Waha.FaultNext(SendFault.Http422Error463);
        await worker.RunSendAsync(tenantId, queued.Id);
        var failed = await MessagingKit.MessageAsync(admin, queued.Id); Assert.Equal("new_contacts_blocked", failed.FailureCode);
        var acc = await ApiSession.Json<ChannelAccountDto>(await admin.Get($"/api/v1/channels/accounts/{a.Id}"));
        Assert.NotNull(acc.NewContactBlockedUntilUtc); Assert.False(acc.IsPaused); Assert.Empty(app.Waha.SentTo(a.ChatId(phone)));
        Assert.DoesNotContain(app.Messaging.Jobs, j => j.Kind == "send" && j.MessageId == queued.Id && j.Delay > TimeSpan.Zero);
    }
}
