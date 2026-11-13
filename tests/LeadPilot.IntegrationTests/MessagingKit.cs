using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LeadPilot.Application;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure;
using LeadPilot.Infrastructure.Discovery;
using LeadPilot.Infrastructure.Messaging;
using LeadPilot.Infrastructure.Messaging.Broker;
using LeadPilot.Infrastructure.Messaging.Waha;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace LeadPilot.IntegrationTests;

/// <summary>TEST-ONLY clock: lets a test move time forward (grace periods, windows) and back. Tests of the "app" collection run one at a time.</summary>
public sealed class OffsetClock : IClock
{
    public TimeSpan Offset { get; set; }
    public DateTime UtcNow => DateTime.UtcNow + Offset;
}

/// <summary>TEST-ONLY scheduler: records the Hangfire jobs the app would enqueue; the tests run them deterministically.</summary>
public sealed class RecordingMessagingScheduler : IMessagingJobScheduler
{
    public sealed record Job(string Kind, Guid TenantId, Guid MessageId, TimeSpan? Delay);
    public ConcurrentQueue<Job> Jobs { get; } = new();
    public int DispatchTriggers;
    public bool ThrowOnEnqueue;
    public string EnqueueSend(Guid tenantId, Guid messageId, TimeSpan? delay = null) { if (ThrowOnEnqueue) throw new InvalidOperationException("scheduler down"); Jobs.Enqueue(new("send", tenantId, messageId, delay)); return "job-" + Jobs.Count; }
    public string EnqueueReconcile(Guid tenantId, Guid messageId, TimeSpan delay) { Jobs.Enqueue(new("reconcile", tenantId, messageId, delay)); return "job-" + Jobs.Count; }
    public void EnqueueOutboxDispatch() => Interlocked.Increment(ref DispatchTriggers);
    public IEnumerable<Job> SendJobsFor(Guid messageId) => Jobs.Where(j => j.Kind == "send" && j.MessageId == messageId);
}

/// <summary>TEST-ONLY publisher used by worker harnesses that do not need a real broker: collects what the outbox dispatcher publishes.</summary>
public sealed class InMemoryPublisher : IEventPublisher
{
    public ConcurrentQueue<EventEnvelope> Published { get; } = new();
    public int FailNext;
    public bool IsConfigured => true;
    public Task<bool> PingAsync(CancellationToken ct) => Task.FromResult(true);
    public Task PublishAsync(EventEnvelope e, CancellationToken ct)
    {
        if (Interlocked.Decrement(ref FailNext) >= 0) throw new InvalidOperationException("broker unavailable");
        Published.Enqueue(e); return Task.CompletedTask;
    }
}

/// <summary>A worker composition (the real AddWorkerServices) whose hosted services are NOT started: tests drive the outbox dispatcher, the consumer processor and the jobs themselves.</summary>
public sealed class WorkerHarness : IAsyncDisposable
{
    public IHost Host { get; init; } = default!;
    public InMemoryPublisher Publisher { get; init; } = default!;
    public List<(EventEnvelope Envelope, string Reason)> DeadLettered { get; } = [];
    public List<EventEnvelope> Unrouted { get; } = [];
    public int Retries;
    public IServiceProvider Services => Host.Services;
    public ITenantScopeRunner Scopes => Services.GetRequiredService<ITenantScopeRunner>();

    public async Task<int> DispatchAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().DispatchAsync();
    }

    /// <summary>Dispatches the outbox, then delivers every published event through the real consumer processor, honouring retry and dead-letter outcomes (no broker delays).</summary>
    public async Task PumpAsync(int rounds = 5)
    {
        var processor = Services.GetRequiredService<MessageProcessor>(); var rmq = Services.GetRequiredService<IOptions<RabbitMqOptions>>().Value;
        for (var round = 0; round < rounds; round++)
        {
            await DispatchAsync();
            if (Publisher.Published.IsEmpty) return;
            while (Publisher.Published.TryDequeue(out var env))
            {
                var queue = MessagingTopology.Queues(rmq).FirstOrDefault(q => q.RoutingKeys.Contains(env.Type));
                if (queue is null) { Unrouted.Add(env); continue; }
                var attempt = 1;
                while (true)
                {
                    var outcome = await processor.ProcessAsync(queue, JsonSerializer.SerializeToUtf8Bytes(env), Headers(env, attempt), default);
                    if (outcome.Action == DeliveryAction.Retry) { Retries++; attempt = outcome.NextAttempt; continue; }
                    if (outcome.Action == DeliveryAction.DeadLetter) DeadLettered.Add((env, outcome.Reason ?? ""));
                    break;
                }
            }
        }
    }

    public static Dictionary<string, object?> Headers(EventEnvelope e, int attempt = 1) => new()
    {
        [MessagingTopology.TenantHeader] = Encoding.UTF8.GetBytes(e.TenantId.ToString()), [MessagingTopology.CorrelationHeader] = e.CorrelationId, [MessagingTopology.AttemptHeader] = attempt,
    };

    public Task RunSendAsync(Guid tenantId, Guid messageId) => Scopes.RunAsync(tenantId, (sp, ct) => sp.GetRequiredService<IOutboundSendRunner>().SendAsync(messageId, ct));
    public Task RunReconcileAsync(Guid tenantId, Guid messageId) => Scopes.RunAsync(tenantId, (sp, ct) => sp.GetRequiredService<IOutboundSendRunner>().ReconcileAsync(messageId, ct));

    public async ValueTask DisposeAsync() { Host.Dispose(); await Task.CompletedTask; }
}

public sealed record TestAccount(Guid Id, Guid TenantId, string Session, string WebhookKey, string Secret, ApiSession Admin)
{
    public string WebhookPath => $"/api/v1/webhooks/waha/{WebhookKey}";
    public string ChatId(string e164) => e164.TrimStart('+') + "@c.us";
}

public static class WahaEvents
{
    public static string Message(string session, string fromChat, string text, string? messageId = null, string? eventId = null, bool fromMe = false, long? ts = null, string? pushName = null, bool media = false)
    {
        var id = messageId ?? $"false_{fromChat}_{Guid.NewGuid():N}".ToUpperInvariant().Replace("FALSE", "false");
        return JsonSerializer.Serialize(new
        {
            id = eventId ?? "evt_" + Guid.NewGuid().ToString("N")[..24], timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), @event = "message", session, metadata = new { }, me = new { id = "923009990000@c.us", pushName = "Fake" },
            payload = new Dictionary<string, object?>
            {
                ["id"] = id, ["timestamp"] = ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["from"] = fromChat, ["fromMe"] = fromMe, ["to"] = "923009990000@c.us", ["body"] = text, ["hasMedia"] = media, ["ack"] = 1,
                ["media"] = media ? new { url = "http://waha/files/x.jpg", mimetype = "image/jpeg", filename = "x.jpg" } : null, ["_data"] = new { notifyName = pushName },
            },
            engine = "GOWS", environment = new { tier = "CORE", version = "2026.10.1" },
        });
    }

    public static string Ack(string session, string messageId, string chatId, int ack, string? eventId = null) => JsonSerializer.Serialize(new
    {
        id = eventId ?? "evt_" + Guid.NewGuid().ToString("N")[..24], timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), @event = "message.ack", session,
        payload = new { id = messageId, from = chatId, participant = (string?)null, fromMe = true, ack, ackName = ack switch { 1 => "SERVER", 2 => "DEVICE", 3 => "READ", 4 => "PLAYED", -1 => "ERROR", _ => "PENDING" } }, engine = "GOWS",
    });

    public static string SessionStatus(string session, string status, string? phone = "923009990000", object? data = null, string? eventId = null) => JsonSerializer.Serialize(new
    {
        id = eventId ?? "evt_" + Guid.NewGuid().ToString("N")[..24], timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), @event = "session.status", session,
        me = phone is null ? null : new { id = phone + "@c.us", pushName = "Fake" }, payload = new { status, statuses = new object[0], data }, engine = "GOWS",
    });
}

public static class MessagingKit
{
    public static void RegisterTestServices(IServiceCollection s, OffsetClock clock, RecordingMessagingScheduler scheduler, RecordingCampaignScheduler? campaigns = null, AiTestControls? ai = null)
    {
        if (ai is not null) AiKit.Register(s, ai);
        s.RemoveAll<IClock>(); s.AddSingleton<IClock>(clock);
        s.RemoveAll<IMessagingJobScheduler>(); s.AddSingleton<IMessagingJobScheduler>(scheduler);
        if (campaigns is not null) { s.RemoveAll<LeadPilot.Application.Campaigns.ICampaignJobScheduler>(); s.AddSingleton<LeadPilot.Application.Campaigns.ICampaignJobScheduler>(campaigns); }
    }

    public static WorkerHarness BuildWorker(AppFixture app, bool realBroker = false, Action<IServiceCollection>? configure = null, Dictionary<string, string?>? extraConfig = null)
    {
        var b = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });   // Development: ValidateOnBuild proves every registration resolves without the HTTP edge
        b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = Environment.GetEnvironmentVariable("ConnectionStrings__Default"), ["Discovery:Hangfire:PollSeconds"] = "1" });
        if (extraConfig is not null) b.Configuration.AddInMemoryCollection(extraConfig);
        b.Services.AddApplication(); b.Services.AddInfrastructure(b.Configuration, b.Environment); b.Services.AddWorkerServices(b.Configuration);
        var publisher = new InMemoryPublisher();
        RegisterTestServices(b.Services, app.Clock, app.Messaging, app.Campaigns, app.Ai);
        configure?.Invoke(b.Services);
        if (!realBroker) { b.Services.RemoveAll<IEventPublisher>(); b.Services.AddSingleton<IEventPublisher>(publisher); }
        return new WorkerHarness { Host = b.Build(), Publisher = publisher };
    }

    public static Task<T> Db<T>(this AppFixture app, Func<AppDbContext, Task<T>> work) => app.World.WithDb(work);
    public static Task Db(this AppFixture app, Func<AppDbContext, Task> work) => app.World.WithDb(work);

    public static async Task<T> SharedAsync<T>(this AppFixture app, string key, Func<Task<T>> create) where T : notnull =>
        (T)await app.Cache.GetOrAdd(key, _ => new Lazy<Task<object>>(async () => await create())).Value;

    /// <summary>One connected, working account of tenant A (limits generous), shared by the tests of the run; tests use their own chats.</summary>
    public static Task<TestAccount> SharedAccountAAsync(AppFixture app) => app.SharedAsync("accountA", async () =>
    {
        var admin = await ApiSession.LoginAsync(app, app.World.AAdmin.Email, app.World.AAdmin.Password);
        return await CreateAccountAsync(app, admin, app.World.A.Id, hourly: 120, daily: 1000, delay: 10);
    });

    /// <summary>Test setup: the number has been working for a month (no warm-up cap) and nothing was sent a moment ago (no pacing delay).</summary>
    public static Task PrepareAccountAsync(AppFixture app, Guid accountId, int? hourly = null, int? daily = null, int? delay = null, bool resetPacing = true) =>
        app.Db(async db =>
        {
            var month = DateTime.UtcNow.AddDays(-30);
            await db.ChannelAccounts.IgnoreQueryFilters().Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.WarmupStartedAtUtc, month));
            await db.ChannelAccounts.IgnoreQueryFilters().Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsPaused, false).SetProperty(a => a.PausedAutomatically, false).SetProperty(a => a.PauseReason, (string?)null).SetProperty(a => a.ConsecutiveFailures, 0).SetProperty(a => a.NewContactBlockedUntilUtc, (DateTime?)null));
            if (resetPacing) await db.ChannelAccounts.IgnoreQueryFilters().Where(a => a.Id == accountId).ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSendAtUtc, (DateTime?)null));
            if (hourly is { } h && daily is { } d && delay is { } m) await db.ChannelAccounts.IgnoreQueryFilters().Where(a => a.Id == accountId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.HourlyCap, h).SetProperty(a => a.DailyCap, d).SetProperty(a => a.MinDelaySeconds, m));
        });

    public static async Task<MessageDto> ReplyAsync(ApiSession s, Guid conversationId, string text, string? key = null, Guid? templateId = null)
    {
        var res = await s.Post($"/api/v1/conversations/{conversationId}/messages", new { text, idempotencyKey = key, templateId });
        Assert.True(res.StatusCode == HttpStatusCode.Accepted, $"reply failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return await ApiSession.Json<MessageDto>(res);
    }

    public static async Task<MessageDto> MessageAsync(ApiSession s, Guid id) => await ApiSession.Json<MessageDto>(await s.Get($"/api/v1/messages/{id}"));

    /// <summary>An inbound message from <paramref name="phone"/>, processed by the consumers; returns the conversation.</summary>
    public static async Task<ConversationListItemDto> InboundAsync(AppFixture app, WorkerHarness worker, TestAccount a, ApiSession s, string phone, string text = "hello", string? pushName = null)
    {
        var res = await PostWebhookAsync(app, a, WahaEvents.Message(a.Session, a.ChatId(phone), text, pushName: pushName));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await worker.PumpAsync();
        return (await ConversationsAsync(s)).Single(c => c.PeerPhoneE164 == phone && c.ChannelAccountId == a.Id);
    }

    public static string NewSession() => "t" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>Adds a WhatsApp account through the REST API (credentials against the fake WAHA), connects it and brings the fake session to WORKING.</summary>
    public static async Task<TestAccount> CreateAccountAsync(AppFixture app, ApiSession admin, Guid tenantId, string? session = null, bool working = true, int? hourly = null, int? daily = null, int? delay = null)
    {
        session ??= NewSession(); var secret = "secret-" + Guid.NewGuid().ToString("N");
        var res = await admin.Post("/api/v1/channels/accounts", new { displayName = "Line " + session, baseUrl = app.Waha.BaseUrl, apiKey = FakeWahaServer.ApiKey, sessionName = session, webhookSecret = secret, hourlyCap = hourly, dailyCap = daily, minDelaySeconds = delay });
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"create account failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        var dto = await ApiSession.Json<ChannelAccountDto>(res);
        var setup = await ApiSession.Json<ChannelAccountSetupDto>(await admin.Get($"/api/v1/channels/accounts/{dto.Id}/setup"));
        var account = new TestAccount(dto.Id, tenantId, session, setup.WebhookPath.Split('/').Last(), secret, admin);
        if (working) await MakeWorkingAsync(app, account);
        return account;
    }

    public static async Task MakeWorkingAsync(AppFixture app, TestAccount a)
    {
        var connect = await a.Admin.Post($"/api/v1/channels/accounts/{a.Id}/connect");
        Assert.True(connect.IsSuccessStatusCode, "connect failed: " + await connect.Content.ReadAsStringAsync());
        app.Waha.Working(a.Session);
        var refresh = await a.Admin.Post($"/api/v1/channels/accounts/{a.Id}/refresh");
        Assert.True(refresh.IsSuccessStatusCode, await refresh.Content.ReadAsStringAsync());
        Assert.Equal(ConnectionStatus.Working, (await ApiSession.Json<ChannelAccountDto>(refresh)).Status);
    }

    /// <summary>Posts a signed webhook to the API exactly as WAHA would.</summary>
    public static async Task<HttpResponseMessage> PostWebhookAsync(AppFixture app, TestAccount a, string json, string? secret = null, DateTime? sentAt = null, bool omitSignature = false, string? keyOverride = null, string? algorithm = "sha512", HttpClient? client = null)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var http = client ?? app.Api!.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/webhooks/waha/{keyOverride ?? a.WebhookKey}") { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new("application/json");
        if (!omitSignature) req.Headers.TryAddWithoutValidation("X-Webhook-Hmac", WahaSignature.Compute(secret ?? a.Secret, body));
        if (algorithm is not null) req.Headers.TryAddWithoutValidation("X-Webhook-Hmac-Algorithm", algorithm);
        req.Headers.TryAddWithoutValidation("X-Webhook-Request-Id", Guid.NewGuid().ToString());
        req.Headers.TryAddWithoutValidation("X-Webhook-Timestamp", new DateTimeOffset(sentAt ?? DateTime.UtcNow).ToUnixTimeMilliseconds().ToString());
        return await http.SendAsync(req);
    }

    public static async Task<string> StatusOf(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString()!;

    public static async Task<List<MessageDto>> ThreadAsync(ApiSession s, Guid conversationId)
    {
        var res = await s.Get($"/api/v1/conversations/{conversationId}/messages?pageSize=100");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await ApiSession.Json<LeadPilot.Application.Common.PagedResult<MessageDto>>(res)).Items.ToList();
    }

    public static async Task<List<ConversationListItemDto>> ConversationsAsync(ApiSession s, string query = "")
    {
        var res = await s.Get("/api/v1/conversations?pageSize=100" + query);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await ApiSession.Json<LeadPilot.Application.Common.PagedResult<ConversationListItemDto>>(res)).Items.ToList();
    }

    /// <summary>Records marketing consent for a contact through the real API.</summary>
    public static async Task GrantConsentAsync(ApiSession s, Guid contactId)
    {
        var res = await s.Post($"/api/v1/contacts/{contactId}/consent", new { channel = "WhatsApp", purpose = "Marketing", status = "Granted", source = "Signed web form", evidenceRef = "form-1" });
        Assert.True(res.IsSuccessStatusCode, "consent failed: " + await res.Content.ReadAsStringAsync());
    }

    public static async Task<(Guid LeadId, Guid ContactId, string Phone)> LeadWithContactAsync(ApiSession s, string? phone = null)
    {
        phone ??= "+9230" + Random.Shared.Next(10_000_000, 99_999_999);
        var lead = await Crm.CreateLead(s, phone: phone);
        var contacts = await Crm.Ok<IReadOnlyList<LeadPilot.Application.Crm.ContactDto>>(await s.Get($"/api/v1/leads/{lead.Id}/contacts"));
        var contact = contacts.Single();   // the lead was created with exactly one contact
        return (lead.Id, contact.Id, contact.Phone!);
    }

    public static async Task DeleteQueuesAsync(string prefix)
    {
        try
        {
            var f = new ConnectionFactory { HostName = Environment.GetEnvironmentVariable(RequiresRabbitMqFactAttribute.EnvVar)!, UserName = "guest", Password = "guest" };
            await using var conn = await f.CreateConnectionAsync(); await using var ch = await conn.CreateChannelAsync();
            foreach (var q in new[] { "inbound", "status", "campaigns" })
            {
                foreach (var name in new[] { $"{prefix}.{q}", $"{prefix}.{q}.dlq" }.Concat(Enumerable.Range(1, 8).Select(i => $"{prefix}.{q}.retry.{i}")))
                    try { await ch.QueueDeleteAsync(name); } catch { /* not declared */ }
            }
            try { await ch.ExchangeDeleteAsync(prefix); } catch { /* not declared */ }
        }
        catch { /* cleanup is best effort */ }
    }
}
