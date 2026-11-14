using System.Text;
using System.Text.Json;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace LeadPilot.Infrastructure.Messaging.Broker;

/// <summary>Opens broker connections from <see cref="RabbitMqOptions"/>. One place so publisher, consumers, DLQ inspector and health check configure TLS/credentials identically.</summary>
public sealed class RabbitMqConnectionFactory(IOptions<RabbitMqOptions> options)
{
    public RabbitMqOptions Options => options.Value;

    public Task<IConnection> ConnectAsync(string clientName, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.IsConfigured) throw new InvalidOperationException("RabbitMQ is not configured (RabbitMq:Host).");
        var f = new ConnectionFactory
        {
            HostName = o.Host!, Port = o.Port, UserName = o.UserName, Password = o.Password, VirtualHost = o.VirtualHost, ClientProvidedName = clientName,
            AutomaticRecoveryEnabled = true, NetworkRecoveryInterval = TimeSpan.FromSeconds(5), RequestedConnectionTimeout = TimeSpan.FromSeconds(10),
        };
        if (o.UseTls) f.Ssl = new SslOption { Enabled = true, ServerName = o.Host! };
        return f.CreateConnectionAsync(ct);
    }
}

/// <summary>Publishes events with broker confirms: the call returns only once RabbitMQ has taken responsibility for the message (or throws).</summary>
public sealed class RabbitMqEventPublisher(RabbitMqConnectionFactory factory, ILogger<RabbitMqEventPublisher> logger) : IEventPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public bool IsConfigured => factory.Options.IsConfigured;

    private async Task<IChannel> EnsureChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true } ch && _connection is { IsOpen: true }) return ch;
        if (_channel is not null) await SafeDispose();
        _connection = await factory.ConnectAsync("leadpilot-publisher", ct);
        _channel = await _connection.CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
        await MessagingTopology.DeclareAsync(_channel, factory.Options, ct);
        return _channel;
    }

    public async Task PublishAsync(EventEnvelope envelope, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(2, factory.Options.PublishTimeoutSeconds)));
        await _gate.WaitAsync(timeout.Token);
        try
        {
            var channel = await EnsureChannelAsync(timeout.Token);
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope));
            var props = new BasicProperties
            {
                Persistent = true, ContentType = "application/json", MessageId = envelope.MessageId.ToString(), CorrelationId = envelope.CorrelationId, Type = envelope.Type,
                Timestamp = new AmqpTimestamp(new DateTimeOffset(envelope.OccurredAtUtc, TimeSpan.Zero).ToUnixTimeSeconds()),
                Headers = new Dictionary<string, object?>
                {
                    [MessagingTopology.TenantHeader] = envelope.TenantId.ToString(), [MessagingTopology.CorrelationHeader] = envelope.CorrelationId,
                    [MessagingTopology.CausationHeader] = envelope.CausationId ?? string.Empty, [MessagingTopology.TypeHeader] = envelope.Type, [MessagingTopology.AttemptHeader] = 1,
                },
            };
            await channel.BasicPublishAsync(factory.Options.Exchange, envelope.Type, mandatory: false, props, body, timeout.Token);
        }
        catch
        {
            await SafeDispose();   // a broken channel/connection is rebuilt on the next call
            throw;
        }
        finally { _gate.Release(); }
    }

    /// <summary>For the health check: can we hold a healthy connection?</summary>
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        if (!IsConfigured) return false;
        await _gate.WaitAsync(ct);
        try { await EnsureChannelAsync(ct); return true; }
        catch (Exception ex) { logger.LogWarning(ex, "RabbitMQ health probe failed"); await SafeDispose(); return false; }
        finally { _gate.Release(); }
    }

    private async Task SafeDispose()
    {
        try { if (_channel is not null) await _channel.DisposeAsync(); } catch { /* already broken */ }
        try { if (_connection is not null) await _connection.DisposeAsync(); } catch { /* already broken */ }
        _channel = null; _connection = null;
    }

    public async ValueTask DisposeAsync() { await SafeDispose(); _gate.Dispose(); }
}

public sealed class RabbitMqHealthCheck(RabbitMqEventPublisher publisher) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await publisher.PingAsync(ct) ? HealthCheckResult.Healthy("RabbitMQ reachable") : HealthCheckResult.Unhealthy("RabbitMQ is not reachable");
}
