using System.Text;
using System.Text.Json;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Messaging.Broker;

public enum DeliveryAction { Ack = 1, Retry, DeadLetter }

public sealed record DeliveryOutcome(DeliveryAction Action, string? Reason = null, int NextAttempt = 0)
{
    public static readonly DeliveryOutcome Ack = new(DeliveryAction.Ack);
}

/// <summary>
/// Broker-independent core of a consumer: checks the delivery, establishes the tenant scope from the VERIFIED TenantId header BEFORE any data access, runs the handler and
/// decides ack / retry (bounded exponential backoff queues) / dead-letter. Missing, malformed or mismatching tenant → dead-letter, never processed.
/// </summary>
public sealed class MessageProcessor(ITenantScopeRunner scopes, IOptions<RabbitMqOptions> options, ILogger<MessageProcessor> logger)
{
    public async Task<DeliveryOutcome> ProcessAsync(ConsumerQueue queue, ReadOnlyMemory<byte> body, IDictionary<string, object?>? headers, CancellationToken ct)
    {
        var attempt = Math.Max(1, HeaderInt(headers, MessagingTopology.AttemptHeader) ?? 1);

        // 1. the tenant header: missing/forged → dead letter, nothing else is touched
        var tenantRaw = HeaderString(headers, MessagingTopology.TenantHeader);
        if (!Guid.TryParse(tenantRaw, out var tenantId) || tenantId == Guid.Empty) return Dead("missing_or_invalid_tenant_header");

        EventEnvelope? envelope;
        try { envelope = JsonSerializer.Deserialize<EventEnvelope>(body.Span); }
        catch (JsonException) { return Dead("invalid_envelope"); }
        if (envelope is null || envelope.MessageId == Guid.Empty || string.IsNullOrWhiteSpace(envelope.Type)) return Dead("invalid_envelope");
        if (envelope.TenantId != tenantId) return Dead("tenant_header_does_not_match_envelope");
        if (!queue.RoutingKeys.Contains(envelope.Type)) return Dead("event_type_not_handled_by_this_queue");

        using var _ = logger.BeginScope(new Dictionary<string, object?> { ["TenantId"] = tenantId, ["CorrelationId"] = envelope.CorrelationId, ["CausationId"] = envelope.CausationId, ["EventType"] = envelope.Type });
        try
        {
            await scopes.RunAsync(tenantId, async (sp, c) =>
            {
                var handler = sp.GetServices<IMessagingEventHandler>().Single(h => h.ConsumerName == queue.ConsumerName);
                await handler.HandleAsync(envelope, attempt, c);
            }, ct);
            return DeliveryOutcome.Ack;
        }
        catch (TenantUnavailableException ex) { return Dead("tenant_unavailable: " + ex.Message); }
        catch (TenantContextMissingException ex) { return Dead("tenant_context_missing: " + ex.Message); }
        catch (PoisonMessageException ex) { return Dead(ex.Message); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (attempt >= options.Value.MaxAttempts)
            {
                logger.LogError(ex, "Message {MessageId} ({Type}) failed after {Attempts} attempts; dead-lettering", envelope.MessageId, envelope.Type, attempt);
                return Dead($"max_attempts_exceeded: {ex.GetType().Name}: {Trim(ex.Message)}");
            }
            logger.LogWarning(ex, "Message {MessageId} ({Type}) failed on attempt {Attempt}; will retry", envelope.MessageId, envelope.Type, attempt);
            return new DeliveryOutcome(DeliveryAction.Retry, $"{ex.GetType().Name}: {Trim(ex.Message)}", attempt + 1);
        }

        DeliveryOutcome Dead(string reason)
        {
            logger.LogError("Dead-lettering a message on {Queue}: {Reason}", queue.Name, reason);
            return new DeliveryOutcome(DeliveryAction.DeadLetter, reason);
        }
    }

    public static string? HeaderString(IDictionary<string, object?>? headers, string key) =>
        headers is not null && headers.TryGetValue(key, out var v) ? v switch { byte[] b => Encoding.UTF8.GetString(b), string s => s, null => null, _ => v.ToString() } : null;

    public static int? HeaderInt(IDictionary<string, object?>? headers, string key) =>
        headers is not null && headers.TryGetValue(key, out var v) ? v switch { int i => i, long l => (int)l, byte[] b when int.TryParse(Encoding.UTF8.GetString(b), out var p) => p, string s when int.TryParse(s, out var q) => q, _ => null } : null;

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}
