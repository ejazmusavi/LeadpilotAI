using System.Text;
using LeadPilot.Application.Messaging;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace LeadPilot.Infrastructure.Messaging.Broker;

/// <summary>Visibility of the dead-letter queues. Platform operators only (enforced at the edge): the queues hold every tenant's failed events, so only headers are ever shown.</summary>
public sealed class DeadLetterService(RabbitMqConnectionFactory factory, ILogger<DeadLetterService> logger) : IDeadLetterService
{
    public async Task<IReadOnlyList<DeadLetterQueueDto>> QueuesAsync(CancellationToken ct = default)
    {
        var o = factory.Options;
        var queues = MessagingTopology.Queues(o);
        if (!o.IsConfigured) return queues.Select(q => new DeadLetterQueueDto(q.Dlq, q.Name, 0, false, "RabbitMQ is not configured.")).ToList();
        var result = new List<DeadLetterQueueDto>();
        try
        {
            await using var connection = await factory.ConnectAsync("leadpilot-dlq-view", ct);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
            foreach (var q in queues)
            {
                try { var ok = await channel.QueueDeclarePassiveAsync(q.Dlq, ct); result.Add(new DeadLetterQueueDto(q.Dlq, q.Name, ok.MessageCount, true, null)); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Add(new DeadLetterQueueDto(q.Dlq, q.Name, 0, false, "Queue does not exist yet (the worker creates it on start)."));
                    // a failed passive declare closes the channel: open a fresh one for the next queue
                    await channel.DisposeAsync();
                    return await FinishAsync(connection, queues.Skip(result.Count).ToList(), result, ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read dead-letter queues");
            return queues.Select(q => new DeadLetterQueueDto(q.Dlq, q.Name, 0, false, "RabbitMQ is not reachable.")).ToList();
        }
        return result;
    }

    private static async Task<IReadOnlyList<DeadLetterQueueDto>> FinishAsync(IConnection connection, List<ConsumerQueue> rest, List<DeadLetterQueueDto> result, CancellationToken ct)
    {
        foreach (var q in rest)
        {
            await using var ch = await connection.CreateChannelAsync(cancellationToken: ct);
            try { var ok = await ch.QueueDeclarePassiveAsync(q.Dlq, ct); result.Add(new DeadLetterQueueDto(q.Dlq, q.Name, ok.MessageCount, true, null)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { result.Add(new DeadLetterQueueDto(q.Dlq, q.Name, 0, false, "Queue does not exist yet (the worker creates it on start).")); }
        }
        return result;
    }

    public async Task<IReadOnlyList<DeadLetterMessageDto>> PeekAsync(string queue, int max, CancellationToken ct = default)
    {
        var o = factory.Options;
        var allowed = MessagingTopology.Queues(o).Select(q => q.Dlq).ToHashSet(StringComparer.Ordinal);
        if (!allowed.Contains(queue)) throw new ArgumentException("Unknown dead-letter queue.", nameof(queue));
        if (!o.IsConfigured) return [];
        max = Math.Clamp(max, 1, 50);
        var list = new List<DeadLetterMessageDto>();
        await using var connection = await factory.ConnectAsync("leadpilot-dlq-peek", ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        var tags = new List<ulong>();
        for (var i = 0; i < max; i++)
        {
            var got = await channel.BasicGetAsync(queue, autoAck: false, ct);
            if (got is null) break;
            tags.Add(got.DeliveryTag);
            var h = got.BasicProperties.Headers;
            DateTime? at = DateTime.TryParse(MessageProcessor.HeaderString(h, MessagingTopology.DeadLetteredAtHeader), null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : null;
            list.Add(new DeadLetterMessageDto(queue, MessageProcessor.HeaderString(h, MessagingTopology.TenantHeader), got.BasicProperties.MessageId, got.BasicProperties.Type ?? MessageProcessor.HeaderString(h, MessagingTopology.TypeHeader),
                MessageProcessor.HeaderString(h, MessagingTopology.ReasonHeader), MessageProcessor.HeaderString(h, MessagingTopology.CorrelationHeader), MessageProcessor.HeaderInt(h, MessagingTopology.AttemptHeader) ?? 1, at));
        }
        foreach (var t in tags) await channel.BasicNackAsync(t, false, requeue: true, ct);   // put everything back, in order
        return list;
    }
}
