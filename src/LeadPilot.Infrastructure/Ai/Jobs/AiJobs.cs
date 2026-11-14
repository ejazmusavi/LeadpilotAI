using LeadPilot.Infrastructure.Agents;
using Hangfire;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Ai;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Ai.Providers;
using LeadPilot.Infrastructure.Ai.Services;
using LeadPilot.Infrastructure.Ai.Stores;
using LeadPilot.Infrastructure.Messaging;
using LeadPilot.Infrastructure.Messaging.Broker;
using LeadPilot.Infrastructure.Messaging.Services;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace LeadPilot.Infrastructure.Ai.Jobs;

public static class AiQueues { public const string Knowledge = "knowledge"; }

/// <summary>Hangfire entry points of the knowledge base (the only scheduler). Arguments are ids; the tenant scope is established here and fails closed. Indexing is idempotent (atomic claim), so bounded retries are safe.</summary>
public sealed class KnowledgeJob(ITenantScopeRunner scopes)
{
    [Queue(AiQueues.Knowledge)]
    [AutomaticRetry(Attempts = 2, DelaysInSeconds = [30, 120], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task IndexAsync(Guid tenantId, Guid versionId, CancellationToken ct) =>
        scopes.RunAsync(tenantId, (sp, c) => sp.GetRequiredService<IKnowledgeIndexer>().IndexVersionAsync(versionId, c), ct);

    [Queue(AiQueues.Knowledge)]
    [AutomaticRetry(Attempts = 1, DelaysInSeconds = [60], OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task SyncWebsiteAsync(Guid tenantId, Guid sourceId, CancellationToken ct) =>
        scopes.RunAsync(tenantId, (sp, c) => sp.GetRequiredService<IKnowledgeIndexer>().SyncWebsiteSourceAsync(sourceId, c), ct);
}

/// <summary>Cross-tenant lookup for the sweep: tenant ids that have versions waiting too long. The ONLY AI code allowed to ignore the tenant filter (allow-listed by an architecture test); it returns ids only.</summary>
public sealed class KnowledgeDirectory(AppDbContext db)
{
    public async Task<List<Guid>> TenantsWithStuckVersionsAsync(DateTime nowUtc, int staleMinutes, CancellationToken ct)
    {
        var waiting = nowUtc.AddMinutes(-2); var stale = nowUtc.AddMinutes(-Math.Max(1, staleMinutes));
        return await db.DocumentVersions.IgnoreQueryFilters().AsNoTracking()
            .Where(v => (v.Status == IndexStatus.Pending && v.CreatedAtUtc < waiting) || (v.Status == IndexStatus.Indexing && v.IndexingStartedAtUtc < stale))
            .Select(v => v.TenantId).Distinct().Take(200).ToListAsync(ct);
    }
}

/// <summary>Every 5 minutes: re-queues versions whose job was lost (scheduler down at upload time, worker crash). Crash recovery for indexing.</summary>
public sealed class KnowledgeSweepJob(ITenantScopeRunner scopes, IClock clock, IOptions<AiOptions> options, ILogger<KnowledgeSweepJob> logger)
{
    public const string RecurringId = "knowledge-sweep";

    [Queue(AiQueues.Knowledge)]
    [DisableConcurrentExecution(300)]
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task RunAsync(CancellationToken ct)
    {
        List<Guid> tenants = [];
        await scopes.RunPlatformJobAsync(async (sp, c) => tenants = await sp.GetRequiredService<KnowledgeDirectory>().TenantsWithStuckVersionsAsync(clock.UtcNow, options.Value.Ingestion.StaleIndexingMinutes, c), ct);
        var queued = 0;
        foreach (var id in tenants)
        {
            try { await scopes.RunAsync(id, async (sp, c) => queued += await sp.GetRequiredService<KnowledgeSweeper>().SweepAsync(c), ct); }
            catch (TenantUnavailableException) { /* suspended tenants are skipped */ }
            catch (Exception ex) { logger.LogError(ex, "Knowledge sweep failed for tenant {TenantId}", id); }
        }
        if (queued > 0) logger.LogInformation("Knowledge sweep re-queued {Count} version(s)", queued);
    }

    public static void ScheduleRecurring(IRecurringJobManager recurring) =>
        recurring.AddOrUpdate<KnowledgeSweepJob>(RecurringId, x => x.RunAsync(CancellationToken.None), "*/5 * * * *");
}

internal sealed class KnowledgeSweeper(CrmContext crm, IAiJobScheduler jobs, IOptions<AiOptions> options)
{
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        var tenantId = crm.TenantId; var now = crm.Now;
        var waiting = now.AddMinutes(-2); var stale = now.AddMinutes(-Math.Max(1, options.Value.Ingestion.StaleIndexingMinutes));
        var ids = await crm.Db.DocumentVersions.AsNoTracking().Where(v => v.TenantId == tenantId && ((v.Status == IndexStatus.Pending && v.CreatedAtUtc < waiting) || (v.Status == IndexStatus.Indexing && v.IndexingStartedAtUtc < stale))).Select(v => v.Id).Take(50).ToListAsync(ct);
        foreach (var id in ids) jobs.EnqueueIndex(tenantId, id);
        return ids.Count;
    }
}

public sealed class HangfireAiScheduler(IBackgroundJobClient client) : IAiJobScheduler
{
    public string EnqueueIndex(Guid tenantId, Guid versionId) => client.Enqueue<KnowledgeJob>(x => x.IndexAsync(tenantId, versionId, CancellationToken.None));
    public string EnqueueWebsiteSync(Guid tenantId, Guid sourceId) => client.Enqueue<KnowledgeJob>(x => x.SyncWebsiteAsync(tenantId, sourceId, CancellationToken.None));
}

/// <summary>
/// Consumer of <c>ai.reply.requested</c> (RabbitMQ, durable): runs the AI workflow for the inbound message and stores the suggestion for staff. Idempotent through the (consumer, message id) record AND the unique
/// suggestion per trigger message. Retryable provider failures use the broker's bounded retry; when the attempts are used up the failure is recorded as an escalation so staff see that nothing answered.
/// </summary>
internal sealed class AiReplyHandler(CrmContext crm, AiOrchestrator orchestrator, IConversationMemory memory, AiCredentials credentials, IOptions<RabbitMqOptions> rabbit, ILogger<AiReplyHandler> logger) : IMessagingEventHandler
{
    public const string Name = "ai.reply";
    public string ConsumerName => Name;

    public async Task HandleAsync(EventEnvelope envelope, int attempt, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        if (envelope.Type != MessagingEventTypes.AiReplyRequested) throw new PoisonMessageException("unexpected_event_type");
        var p = JsonSerializer.Deserialize<AiReplyRequestedPayload>(envelope.Payload) ?? throw new PoisonMessageException("payload_unreadable");
        if (await ConsumerIdempotency.AlreadyProcessedAsync(crm, Name, envelope.MessageId, ct)) return;
        // ids must belong to the tenant of the verified header: a forged tenant finds nothing and the message is dead-lettered
        var message = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == p.MessageId && m.TenantId == tenantId, ct) ?? throw new PoisonMessageException("message_not_found_for_tenant");
        if (message.ConversationId != p.ConversationId || !await db.Conversations.AnyAsync(c => c.Id == p.ConversationId && c.TenantId == tenantId, ct)) throw new PoisonMessageException("ids_do_not_belong_together");

        try { await orchestrator.RunForConversationAsync(p.ConversationId, p.MessageId, ct); }
        catch (AiProviderException ex) when (ex.Retryable && attempt < rabbit.Value.MaxAttempts) { throw; }   // broker retry with backoff
        catch (AiProviderException ex)
        {
            logger.LogWarning("AI could not answer message {MessageId}: {Kind}", p.MessageId, ex.Kind);
            await orchestrator.RecordUnavailableAsync(p.ConversationId, p.MessageId, ex.Kind.ToString(), ct);
        }
        catch (VectorStoreException) when (attempt < rabbit.Value.MaxAttempts) { throw; }
        catch (VectorStoreException)
        {
            await orchestrator.RecordUnavailableAsync(p.ConversationId, p.MessageId, "VectorStore", ct);
        }

        // keep the rolling summary of older messages current (best effort: the reply itself is already stored)
        try { var settings = await credentials.SettingsAsync(ct); await memory.RefreshSummaryAsync(p.ConversationId, settings?.HistoryMessages ?? 12, ct); }
        catch (Exception ex) when (ex is AiProviderException or DbUpdateException) { logger.LogInformation("Conversation summary not refreshed: {Reason}", ex.GetType().Name); }
        await ConsumerIdempotency.CommitAsync(crm, Name, envelope.MessageId, ct);
    }
}

public static class AiServiceCollection
{
    /// <summary>AI engine: provider adapters behind the metering decorators, the configured vector store, ingestion, retrieval, memory, orchestration and the services behind the pages and the API.</summary>
    public static IServiceCollection AddAi(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AiOptions>(config.GetSection(AiOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient("ai-http", c => c.Timeout = Timeout.InfiniteTimeSpan);        // per-attempt timeouts are applied by AiHttpPipeline
        services.AddHttpClient(QdrantVectorStore.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(Math.Max(2, config.GetValue("Ai:VectorStore:Qdrant:TimeoutSeconds", 20))));
        services.AddSingleton<AiCircuitBreaker>();
        services.AddScoped(sp => new AiHttpPipeline(sp.GetRequiredService<IHttpClientFactory>().CreateClient("ai-http"), sp.GetRequiredService<AiCircuitBreaker>(), sp.GetRequiredService<TimeProvider>()));
        services.AddScoped<AiCredentials>();
        services.AddScoped<AiUsageLedger>();

        // providers: raw adapters (replaceable) behind the metering decorators that everything else uses
        services.TryAddScoped<IRawAIProvider, ClaudeAIProvider>();
        services.TryAddScoped<IRawEmbeddingProvider, VoyageEmbeddingProvider>();
        services.AddScoped<IAIProvider, MeteredAIProvider>();
        services.AddScoped<IEmbeddingProvider, MeteredEmbeddingProvider>();
        if (string.Equals(config["Ai:VectorStore:Provider"], "qdrant", StringComparison.OrdinalIgnoreCase)) services.AddScoped<IVectorStore, QdrantVectorStore>();
        else services.AddScoped<IVectorStore, MySqlVectorStore>();

        services.AddSingleton<Extraction.DocumentTextExtractor>();
        services.AddScoped<KnowledgeWriter>();
        services.AddScoped<IKnowledgeIndexer, KnowledgeIndexer>();
        services.AddScoped<IKnowledgeService, KnowledgeService>();
        services.AddScoped<IKnowledgeRetriever, KnowledgeRetriever>();
        services.AddScoped<IConversationMemory, ConversationMemory>();
        services.AddAgents();
        services.AddScoped<AiOrchestrator>();
        services.AddScoped<IAIOrchestrator>(sp => sp.GetRequiredService<AiOrchestrator>());
        services.AddScoped<IAiSettingsService, AiSettingsService>();
        services.AddScoped<IAiUsageService, AiUsageService>();
        services.AddScoped<IAiSuggestionService, AiSuggestionService>();
        services.AddScoped<IAiTesterService, AiTesterService>();
        services.AddScoped<IMessagingEventHandler, AiReplyHandler>();

        services.AddSingleton<IAiJobScheduler, HangfireAiScheduler>();
        services.AddScoped<KnowledgeJob>();
        services.AddScoped<KnowledgeSweepJob>();
        services.AddScoped<KnowledgeDirectory>();
        services.AddScoped<KnowledgeSweeper>();
        return services;
    }
}
