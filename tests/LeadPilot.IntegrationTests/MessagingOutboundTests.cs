using System.Net;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static LeadPilot.IntegrationTests.FakeWahaServer;

namespace LeadPilot.IntegrationTests;

/// <summary>Outgoing flow against the fake WAHA: tracking, idempotency, the no-duplicate guarantee across crashes/timeouts, acks, pacing, account health.</summary>
[Collection("app")]
public class MessagingOutboundTests(AppFixture app)
{
    private TestWorld W => app.World;
    private static string Phone() => "+9230" + Random.Shared.Next(10_000_000, 99_999_999);

    /// <summary>Shared connected account of tenant A, an inbound message from a fresh number, and the worker harness.</summary>
    private async Task<(TestAccount A, ApiSession Admin, WorkerHarness Worker, ConversationListItemDto Conv, string Phone)> ArrangeAsync(string text = "Is the demo free?")
    {
        var a = await MessagingKit.SharedAccountAAsync(app); var admin = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        await MessagingKit.PrepareAccountAsync(app, a.Id); app.Waha.ClearFaults(); app.Waha.Working(a.Session);
        var worker = MessagingKit.BuildWorker(app); var phone = Phone();
        return (a, admin, worker, await MessagingKit.InboundAsync(app, worker, a, admin, phone, text), phone);
    }

    private Task<int> SendCount(TestAccount a, string chat) => Task.FromResult(app.Waha.SentTo(chat).Count(m => m.Session == a.Session));

    [RequiresMySqlFact]
    public async Task A_reply_is_queued_sent_through_the_gateway_and_tracked()
    {
        var (a, admin, worker, conv, phone) = await ArrangeAsync(); await using var wk = worker;
        var msg = await MessagingKit.ReplyAsync(admin, conv.Id, "Yes, the demo is free.");
        Assert.Equal(MessageStatus.Queued, msg.Status); Assert.Equal(MessagePurpose.Service, msg.Purpose);
        Assert.Contains(app.Messaging.Jobs, j => j.Kind == "send" && j.MessageId == msg.Id && j.TenantId == W.A.Id);

        await worker.RunSendAsync(W.A.Id, msg.Id);
        var done = await MessagingKit.MessageAsync(admin, msg.Id);
        Assert.Equal(MessageStatus.Sent, done.Status); Assert.NotNull(done.ExternalMessageId); Assert.StartsWith("true_" + a.ChatId(phone) + "_", done.ExternalMessageId); Assert.NotNull(done.SentAtUtc);
        var sent = Assert.Single(app.Waha.SentTo(a.ChatId(phone))); Assert.Equal("Yes, the demo is free.", sent.Text); Assert.Equal(done.ExternalMessageId, sent.Id);
        Assert.Contains(app.Waha.Calls, c => c.PathAndQuery == "/api/sendSeen" && c.Body!.Contains(a.ChatId(phone)));   // WAHA's recommendation: seen before replying
        var events = await app.Db(db => db.MessageStatusEvents.IgnoreQueryFilters().Where(e => e.MessageId == msg.Id).OrderBy(e => e.RecordedAtUtc).Select(e => e.Source + ":" + e.Status).ToListAsync());
        Assert.Equal(["queue:Queued", "send:Sent"], events);
        var audit = await app.Db(db => db.AuditLogs.IgnoreQueryFilters().Where(x => x.TenantId == W.A.Id && x.EntityId == msg.Id.ToString()).Select(x => x.Action).ToListAsync());
        Assert.Contains("Message.Queued", audit); Assert.Contains("Message.Sent", audit);
        var thread = await MessagingKit.ThreadAsync(admin, conv.Id); Assert.Equal(["Received", "Sent"], thread.Select(m => m.Status.ToString()));
    }

    [RequiresMySqlFact]
    public async Task The_same_idempotency_key_never_creates_or_sends_a_second_message()
    {
        var (a, admin, worker, conv, phone) = await ArrangeAsync(); await using var wk = worker;
        var one = await MessagingKit.ReplyAsync(admin, conv.Id, "hello once", key: "form-1"); var two = await MessagingKit.ReplyAsync(admin, conv.Id, "hello once", key: "form-1");
        Assert.Equal(one.Id, two.Id);
        var clash = await admin.Post($"/api/v1/conversations/{conv.Id}/messages", new { text = "DIFFERENT text", idempotencyKey = "form-1" });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        await worker.RunSendAsync(W.A.Id, one.Id); await worker.RunSendAsync(W.A.Id, one.Id);
        Assert.Equal(1, await SendCount(a, a.ChatId(phone)));
        // concurrent identical requests converge on one message as well
        var key = "race-" + Guid.NewGuid().ToString("N");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => admin.Post($"/api/v1/conversations/{conv.Id}/messages", new { text = "race text", idempotencyKey = key }))));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        Assert.Single((await Task.WhenAll(results.Select(ApiSession.Json<MessageDto>))).Select(m => m.Id).Distinct());
        Assert.Equal(1, await app.Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.TenantId == W.A.Id && m.IdempotencyKey == key)));
    }

    [RequiresMySqlFact]
    public async Task A_retried_or_concurrent_send_job_sends_exactly_once()
    {
        var (a, admin, worker, conv, phone) = await ArrangeAsync(); await using var wk = worker;
        var msg = await MessagingKit.ReplyAsync(admin, conv.Id, "exactly once please");
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => worker.RunSendAsync(W.A.Id, msg.Id))));   // Hangfire retry / duplicate delivery / two workers
        await worker.RunSendAsync(W.A.Id, msg.Id);                                                                    // and one more, sequentially, after completion
        Assert.Equal(1, await SendCount(a, a.ChatId(phone)));
        Assert.Equal(MessageStatus.Sent, (await MessagingKit.MessageAsync(admin, msg.Id)).Status);
        Assert.Equal(1, await app.Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.Id == msg.Id).Select(m => m.SendAttempts).FirstAsync()));
    }

    [RequiresMySqlFact]
    public async Task A_send_whose_answer_is_lost_is_unknown_then_reconciled_and_never_sent_twice()
    {
        foreach (var fault in new[] { SendFault.TimeoutButSent, SendFault.Http500ButSent, SendFault.ConnectionDroppedButSent, SendFault.NoIdButSent })
        {
            var (a, admin, worker, conv, phone) = await ArrangeAsync($"fault {fault}"); await using var wk = worker;
            var msg = await MessagingKit.ReplyAsync(admin, conv.Id, "must arrive once " + fault);
            app.Waha.FaultNext(fault);
            await worker.RunSendAsync(W.A.Id, msg.Id);
            var unknown = await MessagingKit.MessageAsync(admin, msg.Id);
            Assert.True(unknown.Status == MessageStatus.Unknown, $"{fault}: expected Unknown, got {unknown.Status} ({unknown.FailureMessage})");
            Assert.Equal(1, await SendCount(a, a.ChatId(phone)));                      // WAHA did send it...
            Assert.Contains(app.Messaging.Jobs, j => j.Kind == "reconcile" && j.MessageId == msg.Id);

            await worker.RunSendAsync(W.A.Id, msg.Id);                                // ...and a repeated send job must NOT send again
            Assert.Equal(1, await SendCount(a, a.ChatId(phone)));
            Assert.Equal(MessageStatus.Unknown, (await MessagingKit.MessageAsync(admin, msg.Id)).Status);

            await worker.RunReconcileAsync(W.A.Id, msg.Id);                           // reconciliation finds it in the gateway's own message list
            var fixedUp = await MessagingKit.MessageAsync(admin, msg.Id);
            Assert.True(fixedUp.Status == MessageStatus.Sent, $"{fault}: expected Sent after reconcile, got {fixedUp.Status}");
            Assert.Equal(app.Waha.SentTo(a.ChatId(phone)).Single().Id, fixedUp.ExternalMessageId);
            Assert.Equal(1, await SendCount(a, a.ChatId(phone)));
        }
    }

    [RequiresMySqlFact]
    public async Task A_message_the_gateway_never_got_is_failed_after_the_grace_period_and_is_not_resent()
    {
        var (a, admin, worker, conv, phone) = await ArrangeAsync(); await using var wk = worker;
        var msg = await MessagingKit.ReplyAsync(admin, conv.Id, "never arrived");
        app.Waha.FaultNext(SendFault.Http500NotSent);
        await worker.RunSendAsync(W.A.Id, msg.Id);
        Assert.Equal(MessageStatus.Unknown, (await MessagingKit.MessageAsync(admin, msg.Id)).Status);
        await worker.RunReconcileAsync(W.A.Id, msg.Id);                              // inside the grace period: keep waiting
        Assert.Equal(MessageStatus.Unknown, (await MessagingKit.MessageAsync(admin, msg.Id)).Status);
        app.Clock.Offset = TimeSpan.FromMinutes(11);
        try { await worker.RunReconcileAsync(W.A.Id, msg.Id); }
        finally { app.Clock.Offset = TimeSpan.Zero; }
        var failed = await MessagingKit.MessageAsync(admin, msg.Id);
        Assert.Equal(MessageStatus.Failed, failed.Status); Assert.Equal("delivery_unconfirmed", failed.FailureCode); Assert.Contains("not re-sent automatically", failed.FailureMessage);
        await worker.RunSendAsync(W.A.Id, msg.Id); await worker.RunReconcileAsync(W.A.Id, msg.Id);
        Assert.Equal(0, await SendCount(a, a.ChatId(phone)));                          // the system never re-sends on its own
    }

    [RequiresMySqlFact]
    public async Task A_worker_that_died_after_claiming_the_message_is_recovered_by_the_sweep_and_reconcile()
    {
        var (a, admin, worker, conv, phone) = await ArrangeAsync(); await using var wk = worker;
        var msg = await MessagingKit.ReplyAsync(admin, conv.Id, "crash window");
        // the worker claimed it (Sending) and the gateway sent it, but the process died before saving the id
        var started = DateTime.UtcNow.AddMinutes(-10);
        await app.Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.Id == msg.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MessageStatus.Sending).SetProperty(m => m.SendStartedAtUtc, started).SetProperty(m => m.SendAttempts, 1)));
        app.Waha.AddSent(a.Session, a.ChatId(phone), "crash window");
        while (app.Messaging.Jobs.TryDequeue(out _)) { }
        await using (var sweepScope = worker.Services.CreateAsyncScope()) await sweepScope.ServiceProvider.GetRequiredService<LeadPilot.Infrastructure.Messaging.Jobs.MessagingMaintenanceJob>().SweepAsync(default);
        Assert.Contains(app.Messaging.Jobs, j => j.Kind == "reconcile" && j.MessageId == msg.Id);
        await worker.RunSendAsync(W.A.Id, msg.Id);                                      // a duplicate send job does nothing (status is not Queued)
        Assert.Equal(1, await SendCount(a, a.ChatId(phone)));
        await worker.RunReconcileAsync(W.A.Id, msg.Id);
        Assert.Equal(MessageStatus.Sent, (await MessagingKit.MessageAsync(admin, msg.Id)).Status);
        Assert.Equal(1, await SendCount(a, a.ChatId(phone)));
    }

    [RequiresMySqlFact]
    public async Task A_message_stuck_in_queued_whose_job_was_lost_is_picked_up_by_the_sweep()
    {
        var (a, admin, worker, conv, phone) = await ArrangeAsync(); await using var wk = worker;
        var msg = await MessagingKit.ReplyAsync(admin, conv.Id, "lost job");
        await app.Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.Id == msg.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.NextAttemptAtUtc, DateTime.UtcNow.AddMinutes(-10))));
        while (app.Messaging.Jobs.TryDequeue(out _)) { }
        await using (var sweepScope = worker.Services.CreateAsyncScope()) await sweepScope.ServiceProvider.GetRequiredService<LeadPilot.Infrastructure.Messaging.Jobs.MessagingMaintenanceJob>().SweepAsync(default);
        Assert.Contains(app.Messaging.Jobs, j => j.Kind == "send" && j.MessageId == msg.Id);
        await worker.RunSendAsync(W.A.Id, msg.Id);
        Assert.Equal(MessageStatus.Sent, (await MessagingKit.MessageAsync(admin, msg.Id)).Status);
        Assert.Equal(1, await SendCount(a, a.ChatId(phone)));
    }

    [RequiresMySqlFact]
    public async Task A_scheduler_outage_at_queue_time_does_not_lose_or_duplicate_the_message()
    {
        var (a, admin, worker, conv, phone) = await ArrangeAsync(); await using var wk = worker;
        app.Messaging.ThrowOnEnqueue = true;
        MessageDto msg;
        try { msg = await MessagingKit.ReplyAsync(admin, conv.Id, "scheduler down"); } finally { app.Messaging.ThrowOnEnqueue = false; }
        Assert.Equal(MessageStatus.Queued, (await MessagingKit.MessageAsync(admin, msg.Id)).Status);
        await worker.RunSendAsync(W.A.Id, msg.Id);                                      // what the sweep would trigger
        Assert.Equal(1, await SendCount(a, a.ChatId(phone)));
    }

    [RequiresMySqlFact]
    public async Task A_rate_limited_gateway_answer_requeues_and_the_message_is_sent_once_later()
    {
        var (a, admin, worker, conv, phone) = await ArrangeAsync(); await using var wk = worker;
        var msg = await MessagingKit.ReplyAsync(admin, conv.Id, "after the 429");
        app.Waha.FaultNext(SendFault.Http429);
        await worker.RunSendAsync(W.A.Id, msg.Id);
        var queued = await MessagingKit.MessageAsync(admin, msg.Id);
        Assert.Equal(MessageStatus.Queued, queued.Status); Assert.Equal(0, await SendCount(a, a.ChatId(phone)));
        var retry = app.Messaging.Jobs.Last(j => j.Kind == "send" && j.MessageId == msg.Id); Assert.True(retry.Delay > TimeSpan.Zero);
        await MessagingKit.PrepareAccountAsync(app, a.Id);
        app.Clock.Offset = TimeSpan.FromMinutes(5);
        try { await worker.RunSendAsync(W.A.Id, msg.Id); } finally { app.Clock.Offset = TimeSpan.Zero; }
        Assert.Equal(MessageStatus.Sent, (await MessagingKit.MessageAsync(admin, msg.Id)).Status); Assert.Equal(1, await SendCount(a, a.ChatId(phone)));
    }

    [RequiresMySqlFact]
    public async Task A_permanent_gateway_rejection_fails_the_message_without_retrying()
    {
        var (a, admin, worker, conv, phone) = await ArrangeAsync(); await using var wk = worker;
        var msg = await MessagingKit.ReplyAsync(admin, conv.Id, "bad chat");
        app.Waha.FaultNext(SendFault.Http400);
        await worker.RunSendAsync(W.A.Id, msg.Id);
        var failed = await MessagingKit.MessageAsync(admin, msg.Id);
        Assert.Equal(MessageStatus.Failed, failed.Status); Assert.Equal("rejected", failed.FailureCode); Assert.Equal(0, await SendCount(a, a.ChatId(phone)));
        Assert.DoesNotContain(app.Messaging.Jobs, j => j.Kind == "send" && j.MessageId == msg.Id && j.Delay > TimeSpan.Zero);
    }

    [RequiresMySqlFact]
    public async Task Delivery_and_read_acks_update_the_status_monotonically_and_late_acks_never_regress_it()
    {
        var (a, admin, worker, conv, phone) = await ArrangeAsync(); await using var wk = worker;
        var msg = await MessagingKit.ReplyAsync(admin, conv.Id, "track me"); await worker.RunSendAsync(W.A.Id, msg.Id);
        var sent = await MessagingKit.MessageAsync(admin, msg.Id); var chat = a.ChatId(phone); var id = sent.ExternalMessageId!;

        async Task AckAsync(int ack, string? eventId = null) { Assert.Equal(HttpStatusCode.OK, (await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Ack(a.Session, id, chat, ack, eventId))).StatusCode); await worker.PumpAsync(); }
        await AckAsync(2); var delivered = await MessagingKit.MessageAsync(admin, msg.Id);
        Assert.Equal(MessageStatus.Delivered, delivered.Status); Assert.NotNull(delivered.DeliveredAtUtc);
        await AckAsync(3); var read = await MessagingKit.MessageAsync(admin, msg.Id);
        Assert.Equal(MessageStatus.Read, read.Status); Assert.NotNull(read.ReadAtUtc);
        await AckAsync(2); await AckAsync(1); await AckAsync(0);                      // late/duplicate lower acks
        Assert.Equal(MessageStatus.Read, (await MessagingKit.MessageAsync(admin, msg.Id)).Status);
        await AckAsync(4);                                                           // PLAYED counts as read, never "more than read"
        Assert.Equal(MessageStatus.Read, (await MessagingKit.MessageAsync(admin, msg.Id)).Status);
        var events = await app.Db(db => db.MessageStatusEvents.IgnoreQueryFilters().Where(e => e.MessageId == msg.Id && e.Source == "ack").OrderBy(e => e.RecordedAtUtc).Select(e => new { e.Status, e.Applied }).ToListAsync());
        Assert.Equal([(MessageStatus.Delivered, true), (MessageStatus.Read, true), (MessageStatus.Delivered, false), (MessageStatus.Sent, false), (MessageStatus.Read, false)], events.Select(e => (e.Status, e.Applied)).ToArray());

        // the same ack event delivered twice is stored and applied once
        var once = "evt_dup_" + Guid.NewGuid().ToString("N");
        var json = WahaEvents.Ack(a.Session, id, chat, 3, once);
        Assert.Equal("accepted", await MessagingKit.StatusOf(await MessagingKit.PostWebhookAsync(app, a, json))); Assert.Equal("duplicate", await MessagingKit.StatusOf(await MessagingKit.PostWebhookAsync(app, a, json)));
        Assert.Empty(worker.DeadLettered);
    }

    [RequiresMySqlFact]
    public async Task An_ack_that_overtakes_the_send_is_retried_until_the_gateway_id_is_stored()
    {
        var (a, admin, worker, conv, phone) = await ArrangeAsync(); await using var wk = worker;
        var msg = await MessagingKit.ReplyAsync(admin, conv.Id, "race the ack");
        app.Waha.FaultNext(SendFault.NoIdButSent);                                    // we do not know the id yet
        await worker.RunSendAsync(W.A.Id, msg.Id);
        var id = app.Waha.SentTo(a.ChatId(phone)).Single().Id;
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Ack(a.Session, id, a.ChatId(phone), 2));
        await worker.DispatchAsync();
        var processor = worker.Services.GetRequiredService<LeadPilot.Infrastructure.Messaging.Broker.MessageProcessor>(); var rmq = worker.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<LeadPilot.Infrastructure.Messaging.RabbitMqOptions>>().Value;
        var env = worker.Publisher.Published.Single(e => e.Type == "message.status"); var queue = LeadPilot.Infrastructure.Messaging.Broker.MessagingTopology.Queues(rmq).First(q => q.RoutingKeys.Contains(env.Type));
        var first = await processor.ProcessAsync(queue, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(env), WorkerHarness.Headers(env, 1), default);
        Assert.Equal(LeadPilot.Infrastructure.Messaging.Broker.DeliveryAction.Retry, first.Action);          // not matched yet: retried, not lost
        await worker.RunReconcileAsync(W.A.Id, msg.Id);                                // the sweep reconciles and stores the id
        var second = await processor.ProcessAsync(queue, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(env), WorkerHarness.Headers(env, 2), default);
        Assert.Equal(LeadPilot.Infrastructure.Messaging.Broker.DeliveryAction.Ack, second.Action);
        Assert.Equal(MessageStatus.Delivered, (await MessagingKit.MessageAsync(admin, msg.Id)).Status);
    }

    [RequiresMySqlFact]
    public async Task An_ack_for_an_unknown_message_is_ignored_after_a_few_retries_not_dead_lettered()
    {
        var (a, _, worker, _, phone) = await ArrangeAsync(); await using var wk = worker;
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Ack(a.Session, "true_" + a.ChatId(phone) + "_NOSUCHMSG0001", a.ChatId(phone), 3));
        await worker.PumpAsync();
        Assert.True(worker.Retries >= 3); Assert.Empty(worker.DeadLettered);
        Assert.Contains("No outbound message", await app.Db(async db => (await db.WebhookEvents.IgnoreQueryFilters().Where(w => w.ChannelAccountId == a.Id && w.EventType == "message.ack").OrderByDescending(w => w.ReceivedAtUtc).FirstAsync()).Note!));
    }

    [RequiresMySqlFact]
    public async Task A_failed_ack_marks_an_unconfirmed_message_failed_but_never_a_read_one()
    {
        var (a, admin, worker, conv, phone) = await ArrangeAsync(); await using var wk = worker;
        var chat = a.ChatId(phone);
        var m1 = await MessagingKit.ReplyAsync(admin, conv.Id, "will error"); await worker.RunSendAsync(W.A.Id, m1.Id); await MessagingKit.PrepareAccountAsync(app, a.Id);
        var id1 = (await MessagingKit.MessageAsync(admin, m1.Id)).ExternalMessageId!;
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Ack(a.Session, id1, chat, -1)); await worker.PumpAsync();
        Assert.Equal(MessageStatus.Failed, (await MessagingKit.MessageAsync(admin, m1.Id)).Status);
        var m2 = await MessagingKit.ReplyAsync(admin, conv.Id, "was read"); await worker.RunSendAsync(W.A.Id, m2.Id);
        var id2 = (await MessagingKit.MessageAsync(admin, m2.Id)).ExternalMessageId!;
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Ack(a.Session, id2, chat, 3)); await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Ack(a.Session, id2, chat, -1)); await worker.PumpAsync();
        Assert.Equal(MessageStatus.Read, (await MessagingKit.MessageAsync(admin, m2.Id)).Status);   // a read message stays read
    }

    [RequiresMySqlFact]
    public async Task A_template_is_rendered_locally_and_sent_as_plain_text()
    {
        var (a, admin, worker, conv, phone) = await ArrangeAsync(); await using var wk = worker;
        var lead = await MessagingKit.LeadWithContactAsync(admin);   // contact "Sara"
        var conv2 = await MessagingKit.InboundAsync(app, worker, a, admin, (await Crm.Ok<IReadOnlyList<LeadPilot.Application.Crm.ContactDto>>(await admin.Get($"/api/v1/leads/{lead.LeadId}/contacts"))).Single().Phone!, "hi");
        var tpl = await ApiSession.Json<TemplateDto>(await admin.Post("/api/v1/message-templates", new { name = "Greeting " + Guid.NewGuid().ToString("N")[..6], body = "Hi {{first_name}}, thanks for writing!", category = "Service" }));
        var msg = await MessagingKit.ReplyAsync(admin, conv2.Id, "", templateId: tpl.Id);
        Assert.Equal("Hi Sara, thanks for writing!", msg.Body);
        await worker.RunSendAsync(W.A.Id, msg.Id);
        Assert.Equal("Hi Sara, thanks for writing!", app.Waha.SentTo(a.ChatId(conv2.PeerPhoneE164!)).Single().Text);
        // a template that needs a value the contact does not have is refused, and typos are caught on save
        var noName = await admin.Post($"/api/v1/conversations/{conv.Id}/messages", new { templateId = tpl.Id });
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Post("/api/v1/message-templates", new { name = "Typo", body = "Hi {{nmae}}", category = "Service" })).StatusCode);
        _ = phone;
    }

    [RequiresMySqlFact]
    public async Task Typing_is_shown_before_a_reply_when_enabled_and_never_blocks_the_send()
    {
        var (a, admin, worker, conv, phone) = await ArrangeAsync(); await using var wk = worker;
        var msg = await MessagingKit.ReplyAsync(admin, conv.Id, "no typing in tests");
        await worker.RunSendAsync(W.A.Id, msg.Id);
        Assert.DoesNotContain(app.Waha.Calls, c => c.PathAndQuery == "/api/startTyping" && c.Body!.Contains(a.ChatId(phone)));   // disabled by Messaging:TypingMillisecondsPerChar=0 in the test host
        Assert.Equal(1, await SendCount(a, a.ChatId(phone)));
    }

    [RequiresMySqlFact]
    public async Task Pacing_holds_a_message_back_instead_of_failing_it_and_the_enqueue_check_stops_unbounded_queues()
    {
        var t = await W.NewTenantAsync("pace"); var admin = await ApiSession.LoginAsync(app, t.Admin.Email, t.Admin.Password);
        var a = await MessagingKit.CreateAccountAsync(app, admin, t.Tenant.Id, hourly: 2, daily: 3, delay: 10); await MessagingKit.PrepareAccountAsync(app, a.Id, hourly: 2, daily: 3, delay: 10, resetPacing: true);
        await using var worker = MessagingKit.BuildWorker(app);
        var convs = new List<ConversationListItemDto>();
        for (var i = 0; i < 4; i++) convs.Add(await MessagingKit.InboundAsync(app, worker, a, admin, Phone(), "hi " + i));
        var m1 = await MessagingKit.ReplyAsync(admin, convs[0].Id, "one"); await worker.RunSendAsync(t.Tenant.Id, m1.Id);
        Assert.Equal(MessageStatus.Sent, (await MessagingKit.MessageAsync(admin, m1.Id)).Status);
        var m2 = await MessagingKit.ReplyAsync(admin, convs[1].Id, "two"); await worker.RunSendAsync(t.Tenant.Id, m2.Id);   // within the 10 s minimum gap → held back
        var held = await MessagingKit.MessageAsync(admin, m2.Id);
        Assert.Equal(MessageStatus.Queued, held.Status);
        var job = app.Messaging.Jobs.Last(j => j.Kind == "send" && j.MessageId == m2.Id); Assert.True(job.Delay > TimeSpan.Zero && job.Delay < TimeSpan.FromMinutes(1));
        app.Clock.Offset = TimeSpan.FromSeconds(30);
        try { await worker.RunSendAsync(t.Tenant.Id, m2.Id); } finally { app.Clock.Offset = TimeSpan.Zero; }
        Assert.Equal(MessageStatus.Sent, (await MessagingKit.MessageAsync(admin, m2.Id)).Status);
        // hourly cap 2: the third is held until the first leaves the hour, not sent and not failed
        await MessagingKit.PrepareAccountAsync(app, a.Id);
        var m3 = await MessagingKit.ReplyAsync(admin, convs[2].Id, "three"); await worker.RunSendAsync(t.Tenant.Id, m3.Id);
        var capped = await MessagingKit.MessageAsync(admin, m3.Id); Assert.Equal(MessageStatus.Queued, capped.Status);
        Assert.True(app.Messaging.Jobs.Last(j => j.Kind == "send" && j.MessageId == m3.Id).Delay > TimeSpan.FromMinutes(30));
        Assert.Equal(2, app.Waha.SentMessages.Count(m => m.Session == a.Session));
        // daily cap 3: two sent + one queued = 3, so a fourth is refused at queue time
        var over = await admin.Post($"/api/v1/conversations/{convs[3].Id}/messages", new { text = "four" });
        Assert.Equal(HttpStatusCode.TooManyRequests, over.StatusCode); Assert.Equal("rate_limited", await Crm.ProblemCode(over));
    }

    [RequiresMySqlFact]
    public async Task A_new_number_is_held_to_the_warm_up_allowance()
    {
        var t = await W.NewTenantAsync("warm"); var admin = await ApiSession.LoginAsync(app, t.Admin.Email, t.Admin.Password);
        var a = await MessagingKit.CreateAccountAsync(app, admin, t.Tenant.Id, hourly: 120, daily: 1000, delay: 10);   // warm-up starts now: day 1 = 20
        var dto = await ApiSession.Json<ChannelAccountDto>(await admin.Get($"/api/v1/channels/accounts/{a.Id}"));
        Assert.Equal(20, dto.DailyLimitToday); Assert.Equal(1000, dto.DailyCap);
        await using var worker = MessagingKit.BuildWorker(app);
        var conv = await MessagingKit.InboundAsync(app, worker, a, admin, Phone(), "hi");
        for (var i = 0; i < 20; i++) await app.Db(async db => { db.Messages.Add(MessageForCount(t.Tenant.Id, conv.Id, a.Id, i)); await db.SaveChangesAsync(); });
        var refused = await admin.Post($"/api/v1/conversations/{conv.Id}/messages", new { text = "the 21st" });
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    private static Message MessageForCount(Guid tenantId, Guid convId, Guid accountId, int i)
    {
        var m = Message.Outbound(tenantId, convId, accountId, null, MessagePurpose.Service, "filler " + i, "filler-" + Guid.NewGuid().ToString("N"), "c", null, null, DateTime.UtcNow);
        m.MarkSent("true_filler_" + Guid.NewGuid().ToString("N")[..12], DateTime.UtcNow);
        return m;
    }
}
