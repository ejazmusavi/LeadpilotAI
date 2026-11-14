using System.Net;
using System.Text;
using System.Text.Json;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Messaging;
using LeadPilot.Infrastructure.Messaging.Broker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace LeadPilot.IntegrationTests;

/// <summary>Outbox relay behaviour on MySQL (in-memory broker) — no RabbitMQ needed.</summary>
[Collection("app")]
public class MessagingOutboxTests(AppFixture app)
{
    private TestWorld W => app.World;
    private static string Phone() => "+9230" + Random.Shared.Next(10_000_000, 99_999_999);

    private async Task<(TestAccount A, ApiSession Admin, Guid TenantId)> OwnTenantAsync(string prefix)
    {
        var t = await W.NewTenantAsync(prefix); var admin = await ApiSession.LoginAsync(app, t.Admin.Email, t.Admin.Password);
        return (await MessagingKit.CreateAccountAsync(app, admin, t.Tenant.Id), admin, t.Tenant.Id);
    }

    [RequiresMySqlFact]
    public async Task A_broker_outage_keeps_events_in_the_outbox_with_backoff_and_they_are_published_once_after_recovery()
    {
        var (a, admin, tenantId) = await OwnTenantAsync("ob1"); await using var worker = MessagingKit.BuildWorker(app);
        var phone = Phone(); await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(phone), "survives the outage"));
        worker.Publisher.FailNext = 2;
        Assert.Equal(0, await worker.DispatchAsync());
        var row = await app.Db(db => db.OutboxMessages.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.TenantId == tenantId));
        Assert.Equal(OutboxStatus.Pending, row.Status); Assert.Equal(1, row.Attempts); Assert.Contains("broker unavailable", row.LastError); Assert.True(row.NextAttemptAtUtc > DateTime.UtcNow);
        Assert.Equal(0, await worker.DispatchAsync());                                   // backing off: not retried immediately
        Assert.Equal(1, (await app.Db(db => db.OutboxMessages.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.TenantId == tenantId))).Attempts);

        app.Clock.Offset = TimeSpan.FromMinutes(2);
        try
        {
            Assert.Equal(0, await worker.DispatchAsync());                               // second injected failure
            app.Clock.Offset = TimeSpan.FromMinutes(10);
            Assert.Equal(1, await worker.DispatchAsync());                               // recovered
            Assert.Equal(0, await worker.DispatchAsync());                               // and never again
        }
        finally { app.Clock.Offset = TimeSpan.Zero; }
        Assert.Single(worker.Publisher.Published);
        var published = await app.Db(db => db.OutboxMessages.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.TenantId == tenantId));
        Assert.Equal(OutboxStatus.Published, published.Status); Assert.NotNull(published.PublishedAtUtc);
        await worker.PumpAsync();
        Assert.Single(await MessagingKit.ConversationsAsync(admin), c => c.PeerPhoneE164 == phone);
    }

    [RequiresMySqlFact]
    public async Task An_event_that_can_never_be_published_is_parked_as_failed_and_shown_to_the_tenant()
    {
        var (a, admin, tenantId) = await OwnTenantAsync("ob2"); await using var worker = MessagingKit.BuildWorker(app);
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(Phone()), "poison pill"));
        worker.Publisher.FailNext = int.MaxValue;
        try
        {
            for (var i = 1; i <= 14; i++) { app.Clock.Offset = TimeSpan.FromHours(i * 2); await worker.DispatchAsync(); }
        }
        finally { app.Clock.Offset = TimeSpan.Zero; }
        var row = await app.Db(db => db.OutboxMessages.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.TenantId == tenantId));
        Assert.Equal(OutboxStatus.Failed, row.Status); Assert.Equal(12, row.Attempts);   // bounded: Messaging:OutboxMaxAttempts
        var stats = await ApiSession.Json<WebhookEventStatsDto>(await admin.Get("/api/v1/webhook-events/stats")); Assert.Equal(1, stats.FailedOutbox); Assert.Equal(0, stats.PendingOutbox);
        Assert.Empty(worker.Publisher.Published);
    }

    [RequiresMySqlFact]
    public async Task Concurrent_dispatchers_publish_every_row_exactly_once()
    {
        var (a, _, tenantId) = await OwnTenantAsync("ob3"); await using var worker = MessagingKit.BuildWorker(app);
        for (var i = 0; i < 12; i++) await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(Phone()), "m" + i));
        Assert.Equal(12, await app.Db(db => db.OutboxMessages.IgnoreQueryFilters().CountAsync(o => o.TenantId == tenantId && o.Status == OutboxStatus.Pending)));
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(worker.DispatchAsync)));   // (also drains other tests' leftover rows of other tenants)
        var mine = worker.Publisher.Published.Where(e => e.TenantId == tenantId).ToList();
        var ids = mine.Select(e => e.MessageId).ToList(); Assert.Equal(12, ids.Count); Assert.Equal(12, ids.Distinct().Count());   // every row exactly once, however many dispatchers raced
        Assert.Equal(worker.Publisher.Published.Count, worker.Publisher.Published.Select(e => e.MessageId).Distinct().Count());
        Assert.All(mine, e => { Assert.Equal(tenantId, e.TenantId); Assert.False(string.IsNullOrEmpty(e.CorrelationId)); Assert.NotNull(e.CausationId); });   // tenant, correlation and causation travel with every event
    }

    [RequiresMySqlFact]
    public async Task The_correlation_id_of_the_request_follows_the_event_into_the_outbox()
    {
        var (a, _, tenantId) = await OwnTenantAsync("ob4");
        var http = app.Api!.CreateClient(); http.DefaultRequestHeaders.Add("X-Correlation-Id", "corr-" + Guid.NewGuid().ToString("N")[..10]);
        var corr = http.DefaultRequestHeaders.GetValues("X-Correlation-Id").Single();
        var res = await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(Phone()), "traced"), client: http);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var row = await app.Db(db => db.OutboxMessages.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.TenantId == tenantId));
        Assert.Equal(corr, row.CorrelationId);
        var we = await app.Db(db => db.WebhookEvents.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.TenantId == tenantId));
        Assert.Equal(corr, we.CorrelationId); Assert.Equal(we.Id.ToString(), row.CausationId);
    }

    [RequiresMySqlFact]
    public async Task Retention_purges_old_webhook_payloads_but_a_late_duplicate_is_still_recognised()
    {
        var (a, _, tenantId) = await OwnTenantAsync("ob5"); await using var worker = MessagingKit.BuildWorker(app);
        var json = WahaEvents.Message(a.Session, a.ChatId(Phone()), "old news"); await MessagingKit.PostWebhookAsync(app, a, json); await worker.PumpAsync();
        await app.Db(db => db.WebhookEvents.IgnoreQueryFilters().Where(w => w.TenantId == tenantId).ExecuteUpdateAsync(s => s.SetProperty(w => w.ExpiresAtUtc, DateTime.UtcNow.AddDays(-1))));
        await using (var scope = worker.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<LeadPilot.Infrastructure.Messaging.Jobs.MessagingMaintenanceJob>().RetentionAsync(default);
        var we = await app.Db(db => db.WebhookEvents.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.TenantId == tenantId));
        Assert.Equal("{}", we.PayloadJson);
        Assert.Equal("duplicate", await MessagingKit.StatusOf(await MessagingKit.PostWebhookAsync(app, a, json)));
        // published outbox rows and consumer records older than their retention are removed too
        await app.Db(async db => { await db.OutboxMessages.IgnoreQueryFilters().Where(o => o.TenantId == tenantId).ExecuteUpdateAsync(s => s.SetProperty(o => o.PublishedAtUtc, DateTime.UtcNow.AddDays(-8))); await db.ProcessedMessages.IgnoreQueryFilters().Where(p => p.TenantId == tenantId).ExecuteUpdateAsync(s => s.SetProperty(p => p.ProcessedAtUtc, DateTime.UtcNow.AddDays(-31))); });
        await using (var scope = worker.Services.CreateAsyncScope()) await scope.ServiceProvider.GetRequiredService<LeadPilot.Infrastructure.Messaging.Jobs.MessagingMaintenanceJob>().RetentionAsync(default);
        Assert.Equal(0, await app.Db(db => db.OutboxMessages.IgnoreQueryFilters().CountAsync(o => o.TenantId == tenantId))); Assert.Equal(0, await app.Db(db => db.ProcessedMessages.IgnoreQueryFilters().CountAsync(p => p.TenantId == tenantId)));
    }

    [RequiresMySqlFact]
    public async Task The_ai_extension_event_is_published_only_for_active_conversations_and_never_for_opt_outs()
    {
        var (a, admin, tenantId) = await OwnTenantAsync("ob6"); await using var worker = MessagingKit.BuildWorker(app);
        var phone = Phone(); var conv = await MessagingKit.InboundAsync(app, worker, a, admin, phone, "first");
        Assert.Empty(worker.Unrouted);                                                       // default AI mode is Paused: no AI event
        Assert.True((await admin.Put($"/api/v1/conversations/{conv.Id}/ai-mode", new { mode = "Active" })).IsSuccessStatusCode);
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(phone), "second")); await worker.PumpAsync();
        var ai = Assert.Single(worker.Unrouted); Assert.Equal("ai.reply.requested", ai.Type); Assert.Equal(tenantId, ai.TenantId);   // no consumer is bound yet (Phase 7)
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(phone), "STOP")); await worker.PumpAsync();
        Assert.Single(worker.Unrouted);                                                      // an opt-out never triggers an AI reply
    }

    [RequiresMySqlFact]
    public async Task The_webhook_events_page_data_shows_status_counts_and_the_broker_state()
    {
        var (a, admin, _) = await OwnTenantAsync("ob7"); await using var worker = MessagingKit.BuildWorker(app);
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(Phone()), "stats")); 
        var before = await ApiSession.Json<WebhookEventStatsDto>(await admin.Get("/api/v1/webhook-events/stats")); Assert.Equal(1, before.Received); Assert.Equal(1, before.PendingOutbox);
        await worker.PumpAsync();
        var after = await ApiSession.Json<WebhookEventStatsDto>(await admin.Get("/api/v1/webhook-events/stats")); Assert.Equal(0, after.Received); Assert.Equal(1, after.Processed); Assert.Equal(0, after.PendingOutbox);
        var list = await Crm.Page<WebhookEventDto>(await admin.Get("/api/v1/webhook-events?status=Processed")); Assert.Single(list.Items); Assert.Equal("message", list.Items[0].EventType);
        var status = await ApiSession.Json<MessagingStatusDto>(await admin.Get("/api/v1/webhook-events/status")); Assert.True(status.BrokerConfigured == app.RabbitMqAvailable);
    }
}

/// <summary>End-to-end with a REAL RabbitMQ broker: publisher confirms, topology, consumers, retry queues, dead-letter queues, health check. Skipped (NOT passed) when no broker is configured.</summary>
[Collection("app")]
public class MessagingBrokerTests(AppFixture app)
{
    private TestWorld W => app.World;
    private static string Phone() => "+9230" + Random.Shared.Next(10_000_000, 99_999_999);

    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> done, int seconds = 30)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds); T v;
        do { v = await read(); if (done(v)) return v; await Task.Delay(250); } while (DateTime.UtcNow < until);
        return v;
    }

    /// <summary>The real worker composition with a REAL publisher and the RabbitMQ consumer host started (Hangfire server and everything else stays off).</summary>
    private sealed class BrokerRuntime(WorkerHarness harness, MessagingConsumerHost consumer) : IAsyncDisposable
    {
        public WorkerHarness Harness => harness;
        public IServiceProvider Services => harness.Services;
        public async Task<int> DispatchAsync() { await using var scope = Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().DispatchAsync(); }
        public async ValueTask DisposeAsync() { await consumer.StopAsync(default); await harness.DisposeAsync(); }
    }

    private async Task<BrokerRuntime> StartConsumersAsync()
    {
        var harness = MessagingKit.BuildWorker(app, realBroker: true);
        var consumer = harness.Services.GetServices<IHostedService>().OfType<MessagingConsumerHost>().Single();
        await consumer.StartAsync(default);
        await Task.Delay(500);   // let the consumers attach
        return new BrokerRuntime(harness, consumer);
    }

    private async Task<(IConnection Conn, IChannel Ch)> RawAsync()
    {
        var f = new ConnectionFactory { HostName = Environment.GetEnvironmentVariable(RequiresRabbitMqFactAttribute.EnvVar)!, UserName = "guest", Password = "guest" };
        var conn = await f.CreateConnectionAsync(); return (conn, await conn.CreateChannelAsync());
    }

    [RequiresRabbitMqFact]
    public async Task An_incoming_message_travels_webhook_outbox_rabbitmq_consumer_conversation()
    {
        var a = await MessagingKit.SharedAccountAAsync(app); var admin = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        await using var host = await StartConsumersAsync(); var phone = Phone();
        var (leadId, contactId, e164) = await MessagingKit.LeadWithContactAsync(admin);
        Assert.Equal(HttpStatusCode.OK, (await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(e164), "through the real broker", pushName: "Broker Parent"))).StatusCode);
        Assert.True(await host.DispatchAsync() >= 1);
        var conv = await Eventually(async () => (await MessagingKit.ConversationsAsync(admin)).FirstOrDefault(c => c.PeerPhoneE164 == e164), c => c is not null);
        Assert.NotNull(conv); Assert.Equal(contactId, conv!.ContactId); Assert.Equal("through the real broker", conv.LastMessagePreview); Assert.Equal(leadId, conv.LeadId);
        _ = phone;
    }

    [RequiresRabbitMqFact]
    public async Task Acks_and_session_events_flow_through_the_real_broker_and_the_status_updates_monotonically()
    {
        var a = await MessagingKit.SharedAccountAAsync(app); var admin = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        await MessagingKit.PrepareAccountAsync(app, a.Id); app.Waha.ClearFaults(); app.Waha.Working(a.Session);
        await using var host = await StartConsumersAsync(); var phone = Phone(); var chat = a.ChatId(phone);
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Message(a.Session, chat, "hi over rabbit")); await host.DispatchAsync();
        var conv = (await Eventually(async () => (await MessagingKit.ConversationsAsync(admin)).FirstOrDefault(c => c.PeerPhoneE164 == phone), c => c is not null))!;
        var msg = await MessagingKit.ReplyAsync(admin, conv.Id, "tracked over rabbit"); await host.Harness.RunSendAsync(W.A.Id, msg.Id);
        var id = (await MessagingKit.MessageAsync(admin, msg.Id)).ExternalMessageId!;

        async Task<MessageStatus> After(int ack, MessageStatus waitFor)
        {
            await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Ack(a.Session, id, chat, ack)); await host.DispatchAsync();
            return (await Eventually(async () => (await MessagingKit.MessageAsync(admin, msg.Id)).Status, st => st == waitFor, 15));
        }
        Assert.Equal(MessageStatus.Delivered, await After(2, MessageStatus.Delivered));
        Assert.Equal(MessageStatus.Read, await After(3, MessageStatus.Read));
        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Ack(a.Session, id, chat, 2)); await host.DispatchAsync(); await Task.Delay(1500);
        Assert.Equal(MessageStatus.Read, (await MessagingKit.MessageAsync(admin, msg.Id)).Status);   // the late DEVICE ack never regresses it

        await MessagingKit.PostWebhookAsync(app, a, WahaEvents.SessionStatus(a.Session, "FAILED")); await host.DispatchAsync();
        var acc = await Eventually(async () => await ApiSession.Json<ChannelAccountDto>(await admin.Get($"/api/v1/channels/accounts/{a.Id}")), x => x.IsPaused, 15);
        Assert.True(acc.IsPaused); Assert.Equal(ConnectionStatus.Failed, acc.Status);
        await MessagingKit.PrepareAccountAsync(app, a.Id); app.Waha.Working(a.Session);
        Assert.True((await admin.Post($"/api/v1/channels/accounts/{a.Id}/refresh")).IsSuccessStatusCode);   // leave the shared account usable for other tests
    }

    [RequiresRabbitMqFact]
    public async Task A_message_without_a_tenant_header_or_with_a_forged_one_lands_in_the_dead_letter_queue_and_is_visible_to_platform_operators_only()
    {
        await using var host = await StartConsumersAsync(); var rmq = host.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
        var inbound = MessagingTopology.Queues(rmq).First(q => q.Name.EndsWith(".inbound"));
        var (conn, ch) = await RawAsync(); await using var _c = conn; await using var _ch = ch;
        var env = new EventEnvelope(Guid.NewGuid(), Guid.NewGuid(), "attack", null, "message.received", DateTime.UtcNow, 1, "{}");
        var body = JsonSerializer.SerializeToUtf8Bytes(env);
        await ch.BasicPublishAsync(rmq.Exchange, "message.received", false, new BasicProperties { Persistent = true, MessageId = env.MessageId.ToString(), Headers = new Dictionary<string, object?>() }, body);   // no TenantId at all
        var forged = env with { MessageId = Guid.NewGuid(), TenantId = W.B.Id };
        await ch.BasicPublishAsync(rmq.Exchange, "message.received", false, new BasicProperties { Persistent = true, MessageId = forged.MessageId.ToString(), Headers = new Dictionary<string, object?> { ["TenantId"] = W.A.Id.ToString() } }, JsonSerializer.SerializeToUtf8Bytes(forged));   // header ≠ envelope
        var ghost = env with { MessageId = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        await ch.BasicPublishAsync(rmq.Exchange, "message.received", false, new BasicProperties { Persistent = true, MessageId = ghost.MessageId.ToString(), Headers = new Dictionary<string, object?> { ["TenantId"] = ghost.TenantId.ToString() } }, JsonSerializer.SerializeToUtf8Bytes(ghost));   // unknown tenant

        var dlq = host.Services.GetRequiredService<IDeadLetterService>();
        var queues = await Eventually(() => dlq.QueuesAsync(), q => q.Single(x => x.Queue == inbound.Dlq).Messages >= 3);
        Assert.True(queues.Single(x => x.Queue == inbound.Dlq).Messages >= 3);
        var peek = await dlq.PeekAsync(inbound.Dlq, 20);
        Assert.Contains(peek, m => m.Reason!.Contains("missing_or_invalid_tenant_header")); Assert.Contains(peek, m => m.Reason!.Contains("does_not_match")); Assert.Contains(peek, m => m.Reason!.Contains("tenant_unavailable"));
        Assert.All(peek, m => { Assert.False(string.IsNullOrEmpty(m.Reason)); Assert.NotNull(m.DeadLetteredAtUtc); });
        Assert.True((await dlq.QueuesAsync()).Single(x => x.Queue == inbound.Dlq).Messages >= 3);   // peeking put everything back

        var platform = await ApiSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        var viaApi = await Crm.Ok<List<DeadLetterQueueDto>>(await platform.Get("/api/v1/admin/dead-letters")); Assert.True(viaApi.Single(x => x.Queue == inbound.Dlq).Messages >= 3);
        var tenantAdmin = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password); Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.Get("/api/v1/admin/dead-letters")).StatusCode);
        var page = await (await WebSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword)).Html("/DeadLetters?queue=" + Uri.EscapeDataString(inbound.Dlq));
        Assert.Contains("missing_or_invalid_tenant_header", page); Assert.Contains(inbound.Dlq, page);
        // clean the queue so the run leaves nothing behind
        while (await ch.BasicGetAsync(inbound.Dlq, autoAck: true) is not null) { }
    }

    [RequiresRabbitMqFact]
    public async Task A_failing_message_goes_through_the_retry_queues_with_backoff_and_is_then_resolved()
    {
        var a = await MessagingKit.SharedAccountAAsync(app); await using var host = await StartConsumersAsync(); var rmq = host.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
        var status = MessagingTopology.Queues(rmq).First(q => q.Name.EndsWith(".status"));
        // an ack for a message that does not exist: the handler throws "not stored yet" for the first attempts (retry queues), then gives up quietly
        var chat = a.ChatId(Phone());
        Assert.Equal(HttpStatusCode.OK, (await MessagingKit.PostWebhookAsync(app, a, WahaEvents.Ack(a.Session, "true_" + chat + "_NEVERSENT0001", chat, 3))).StatusCode);
        var started = DateTime.UtcNow;
        Assert.True(await host.DispatchAsync() >= 1);
        var we = await Eventually(() => app.Db(db => db.WebhookEvents.IgnoreQueryFilters().AsNoTracking().FirstAsync(w => w.ChannelAccountId == a.Id && w.EventType == "message.ack" && w.PayloadJson.Contains("NEVERSENT0001"))), w => w.Status == WebhookEventStatus.Ignored, 40);
        Assert.Equal(WebhookEventStatus.Ignored, we.Status);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromSeconds(2.5), "three 1-second retry hops must have happened");   // RabbitMq:RetryDelaysSeconds = 1,1,1 in the test host
        var counts = await host.Services.GetRequiredService<IDeadLetterService>().QueuesAsync();
        Assert.Equal(0, counts.Single(x => x.Queue == status.Dlq).Messages);                 // resolved, not dead-lettered
    }

    [RequiresRabbitMqFact]
    public async Task The_broker_health_check_reports_the_real_state_of_the_broker()
    {
        var ok = app.Api!.Services.GetRequiredService<RabbitMqHealthCheck>();
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy, (await ok.CheckHealthAsync(new(), default)).Status);
        var ready = await app.Api!.CreateClient().GetAsync("/health/ready"); Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var down = new RabbitMqEventPublisher(new RabbitMqConnectionFactory(Options.Create(new RabbitMqOptions { Host = "127.0.0.1", Port = 1 })), Microsoft.Extensions.Logging.Abstractions.NullLogger<RabbitMqEventPublisher>.Instance);
        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, (await new RabbitMqHealthCheck(down).CheckHealthAsync(new(), default)).Status);
        Assert.False(await new RabbitMqEventPublisher(new RabbitMqConnectionFactory(Options.Create(new RabbitMqOptions())), Microsoft.Extensions.Logging.Abstractions.NullLogger<RabbitMqEventPublisher>.Instance).PingAsync(default));   // not configured
    }

    [RequiresRabbitMqFact]
    public async Task The_publisher_uses_confirms_declares_the_topology_and_the_message_carries_tenant_correlation_and_causation_headers()
    {
        var harness = MessagingKit.BuildWorker(app, realBroker: true); await using var _h = harness;
        var rmq = harness.Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value; var publisher = harness.Services.GetRequiredService<IEventPublisher>();
        var env = new EventEnvelope(Guid.NewGuid(), W.A.Id, "corr-xyz", "cause-xyz", "message.received", DateTime.UtcNow, 1, "{}");
        await publisher.PublishAsync(env, default);
        var (conn, ch) = await RawAsync(); await using var _c = conn; await using var _ch = ch;
        var q = MessagingTopology.Queues(rmq).First(x => x.Name.EndsWith(".inbound"));
        var got = await Eventually(async () => await ch.BasicGetAsync(q.Name, autoAck: false), g => g is not null);
        Assert.NotNull(got);
        Assert.Equal(W.A.Id.ToString(), MessageProcessor.HeaderString(got!.BasicProperties.Headers, "TenantId")); Assert.Equal("corr-xyz", MessageProcessor.HeaderString(got.BasicProperties.Headers, "CorrelationId")); Assert.Equal("cause-xyz", MessageProcessor.HeaderString(got.BasicProperties.Headers, "CausationId"));
        Assert.Equal(DeliveryModes.Persistent, got.BasicProperties.DeliveryMode); Assert.Equal(env.MessageId.ToString(), got.BasicProperties.MessageId);
        await ch.BasicAckAsync(got.DeliveryTag, false);
        foreach (var name in new[] { q.Dlq, q.Retry(1), q.Retry(2), q.Retry(3) }) Assert.Equal(0u, (await ch.QueueDeclarePassiveAsync(name)).MessageCount);   // the whole retry/DLQ topology exists
    }
}
