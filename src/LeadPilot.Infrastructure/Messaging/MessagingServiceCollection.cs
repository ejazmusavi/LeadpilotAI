using LeadPilot.Application.Agents;
using LeadPilot.Application.Messaging;
using LeadPilot.Infrastructure.Discovery.Website;
using LeadPilot.Infrastructure.Messaging.Broker;
using LeadPilot.Infrastructure.Messaging.Jobs;
using LeadPilot.Infrastructure.Messaging.Services;
using LeadPilot.Infrastructure.Messaging.Waha;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LeadPilot.Infrastructure.Messaging;

public static class MessagingServiceCollection
{
    /// <summary>
    /// Messaging module for every host that serves or runs it (Web, Api, Worker): WAHA adapter, use cases, outbox publisher and the consumer HANDLERS.
    /// Needs the CRM services and Hangfire. The consumer HOST (RabbitMQ listener) is Worker-only: <see cref="AddMessagingConsumers"/>.
    /// </summary>
    public static IServiceCollection AddMessaging(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<MessagingOptions>(config.GetSection(MessagingOptions.Section));
        services.Configure<WahaOptions>(config.GetSection(WahaOptions.Section));
        services.Configure<RabbitMqOptions>(config.GetSection(RabbitMqOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IDnsResolver, SystemDnsResolver>();
        services.TryAddSingleton<IAddressPolicy, PublicOnlyAddressPolicy>();

        // WAHA adapter (the only code that knows WAHA)
        services.AddSingleton<WahaAddressPolicy>();
        services.AddSingleton<WahaHttpClientFactory>();
        services.AddSingleton<WahaCircuitBreaker>();
        services.AddSingleton<IWhatsAppGateway, WahaGateway>();

        // broker
        services.AddSingleton<RabbitMqConnectionFactory>();
        services.AddSingleton<RabbitMqEventPublisher>();
        services.AddSingleton<IEventPublisher>(sp => sp.GetRequiredService<RabbitMqEventPublisher>());
        services.AddSingleton<IDeadLetterService, DeadLetterService>();
        if (!string.IsNullOrWhiteSpace(config[$"{RabbitMqOptions.Section}:Host"]))
            services.AddHealthChecks().AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);
        services.AddSingleton<RabbitMqHealthCheck>();

        // use cases
        services.AddScoped<MessagingDirectory>();
        services.AddScoped<CredentialVault>();
        services.AddScoped<GatewayConnections>();
        services.AddScoped<IAccountHealthService, AccountHealthService>();
        services.AddScoped<IChannelAccountService, ChannelAccountService>();
        services.AddScoped<OutboundQueueService>();
        services.AddScoped<IOutboundMessageService, OutboundMessageService>();
        services.AddScoped<IConversationService, ConversationService>();
        services.AddScoped<IMessageTemplateService, MessageTemplateService>();
        services.AddScoped<IWebhookEventService, WebhookEventService>();
        services.AddScoped<IWebhookIngestionService, WebhookIngestionService>();
        services.AddScoped<IOutboundSendRunner, OutboundSendRunner>();
        services.AddScoped<IOutboxDispatcher, OutboxDispatcher>();
        services.AddScoped<OptOutProcessor>();
        services.AddScoped<IAiReplySender, AiReplySender>();
        services.AddScoped<LeadPilot.Application.Workflows.IWorkflowMessageSender, WorkflowMessageSender>();
        services.AddScoped<IMessagingEventHandler, InboundMessageHandler>();
        services.AddScoped<IMessagingEventHandler, StatusEventHandler>();

        // jobs
        services.AddSingleton<IMessagingJobScheduler, HangfireMessagingScheduler>();
        services.AddScoped<MessageSendJob>();
        services.AddScoped<OutboxDispatchJob>();
        services.AddScoped<MessagingMaintenanceJob>();
        return services;
    }

    /// <summary>The RabbitMQ listener. Registered by the Worker only; Web and Api just publish (through the outbox).</summary>
    public static IServiceCollection AddMessagingConsumers(this IServiceCollection services)
    {
        services.AddSingleton<MessageProcessor>();
        services.AddHostedService<MessagingConsumerHost>();
        return services;
    }
}
