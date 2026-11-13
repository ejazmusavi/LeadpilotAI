using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Messaging;
using RabbitMQ.Client;

namespace LeadPilot.Infrastructure.Messaging.Broker;

/// <summary>One consumer queue with its retry queues and dead-letter queue.</summary>
public sealed record ConsumerQueue(string Name, string ConsumerName, IReadOnlyList<string> RoutingKeys)
{
    public string Dlq => Name + ".dlq";
    public string Retry(int step) => $"{Name}.retry.{step}";
}

public static class MessagingTopology
{
    public static IReadOnlyList<ConsumerQueue> Queues(RabbitMqOptions o) =>
    [
        new($"{o.Prefix}.inbound", "messaging.inbound", [MessagingEventTypes.MessageReceived]),
        new($"{o.Prefix}.status", "messaging.status", [MessagingEventTypes.MessageStatusChanged, MessagingEventTypes.AccountStatusChanged]),
        new($"{o.Prefix}.campaigns", "campaigns.events", [CampaignEventTypes.RecipientChanged, CampaignEventTypes.StatusChanged]),
    ];

    /// <summary>
    /// Idempotent declaration. Per consumer queue Q: Q (rejected messages dead-letter to Q.dlq), Q.dlq, and one TTL retry queue per backoff step that dead-letters back into Q.
    /// <c>ai.reply.requested</c> is published to the exchange but no queue is bound yet (Phase 7 binds its own); RabbitMQ drops unroutable messages by design.
    /// </summary>
    public static async Task DeclareAsync(IChannel channel, RabbitMqOptions o, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(o.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        foreach (var q in Queues(o))
        {
            await channel.QueueDeclareAsync(q.Dlq, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
            await channel.QueueDeclareAsync(q.Name, durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = "", ["x-dead-letter-routing-key"] = q.Dlq }, cancellationToken: ct);
            foreach (var key in q.RoutingKeys) await channel.QueueBindAsync(q.Name, o.Exchange, key, cancellationToken: ct);
            for (var i = 0; i < o.RetryDelays.Count; i++)
                await channel.QueueDeclareAsync(q.Retry(i + 1), durable: true, exclusive: false, autoDelete: false,
                    arguments: new Dictionary<string, object?> { ["x-message-ttl"] = o.RetryDelays[i] * 1000, ["x-dead-letter-exchange"] = "", ["x-dead-letter-routing-key"] = q.Name }, cancellationToken: ct);
        }
    }

    public const string TenantHeader = "TenantId";
    public const string CorrelationHeader = "CorrelationId";
    public const string CausationHeader = "CausationId";
    public const string AttemptHeader = "x-attempt";
    public const string ReasonHeader = "x-dead-letter-reason";
    public const string DeadLetteredAtHeader = "x-dead-lettered-at";
    public const string TypeHeader = "x-event-type";
}
