using System.Text;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace LeadPilot.Infrastructure.Messaging.Broker;

/// <summary>
/// Runs the RabbitMQ consumers inside the Worker. Manual acks: a message is acknowledged only after it was processed, or after it was safely re-published to a retry queue /
/// dead-letter queue (republishing uses publisher confirms, so a crash can duplicate a delivery but never lose one; consumers are idempotent).
/// </summary>
public sealed class MessagingConsumerHost(RabbitMqConnectionFactory factory, MessageProcessor processor, IClock clock, ILogger<MessagingConsumerHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!factory.Options.IsConfigured) { logger.LogWarning("RabbitMQ is not configured (RabbitMq:Host): messaging consumers are not started; events stay in the outbox"); return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Messaging consumers stopped unexpectedly; reconnecting in 5 s");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var o = factory.Options;
        await using var connection = await factory.ConnectAsync("leadpilot-consumers", ct);
        await using var consumeChannel = await connection.CreateChannelAsync(cancellationToken: ct);
        await using var republishChannel = await connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        await MessagingTopology.DeclareAsync(consumeChannel, o, ct);
        await consumeChannel.BasicQosAsync(0, o.Prefetch, false, ct);

        foreach (var queue in MessagingTopology.Queues(o))
        {
            var consumer = new AsyncEventingBasicConsumer(consumeChannel);
            consumer.ReceivedAsync += (_, ea) => OnReceivedAsync(queue, ea, consumeChannel, republishChannel, ct);
            await consumeChannel.BasicConsumeAsync(queue.Name, autoAck: false, consumer, ct);
            logger.LogInformation("Consuming {Queue}", queue.Name);
        }
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task OnReceivedAsync(ConsumerQueue queue, BasicDeliverEventArgs ea, IChannel consumeChannel, IChannel republishChannel, CancellationToken ct)
    {
        try
        {
            var outcome = await processor.ProcessAsync(queue, ea.Body, ea.BasicProperties.Headers, ct);
            switch (outcome.Action)
            {
                case DeliveryAction.Retry:
                    await RepublishAsync(republishChannel, queue.Retry(Math.Min(outcome.NextAttempt - 1, factory.Options.RetryDelays.Count)), ea, outcome.NextAttempt, outcome.Reason, deadLetter: false, ct);
                    break;
                case DeliveryAction.DeadLetter:
                    await RepublishAsync(republishChannel, queue.Dlq, ea, MessageProcessor.HeaderInt(ea.BasicProperties.Headers, MessagingTopology.AttemptHeader) ?? 1, outcome.Reason, deadLetter: true, ct);
                    break;
            }
            await consumeChannel.BasicAckAsync(ea.DeliveryTag, false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* unacked messages return to the queue */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not settle a delivery on {Queue}; returning it to the queue", queue.Name);
            try { await consumeChannel.BasicNackAsync(ea.DeliveryTag, false, requeue: true, CancellationToken.None); } catch { /* channel gone: the broker requeues */ }
        }
    }

    private async Task RepublishAsync(IChannel channel, string targetQueue, BasicDeliverEventArgs ea, int attempt, string? reason, bool deadLetter, CancellationToken ct)
    {
        var headers = new Dictionary<string, object?>();
        if (ea.BasicProperties.Headers is { } existing) foreach (var kv in existing) headers[kv.Key] = kv.Value;
        headers[MessagingTopology.AttemptHeader] = attempt;
        if (reason is not null) headers[MessagingTopology.ReasonHeader] = Encoding.UTF8.GetBytes(reason.Length > 500 ? reason[..500] : reason);
        if (deadLetter) headers[MessagingTopology.DeadLetteredAtHeader] = clock.UtcNow.ToString("O");
        var props = new BasicProperties { Persistent = true, ContentType = ea.BasicProperties.ContentType, MessageId = ea.BasicProperties.MessageId, CorrelationId = ea.BasicProperties.CorrelationId, Type = ea.BasicProperties.Type, Headers = headers };
        await channel.BasicPublishAsync("", targetQueue, mandatory: true, props, ea.Body, ct);
    }
}
