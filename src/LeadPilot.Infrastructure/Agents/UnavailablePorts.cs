using LeadPilot.Application.Agents;

namespace LeadPilot.Infrastructure.Agents;

// Phase 10 owns demos, trials and payment requests. Until it registers real implementations these answer "not connected": the tools report it honestly and nothing happens. They are NOT test doubles
// and never return a success.

internal sealed class UnavailableDemoScheduler : IDemoScheduler
{
    public Task<PortResult<IReadOnlyList<DemoSlot>>> AvailabilityAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct) => Task.FromResult(PortResult<IReadOnlyList<DemoSlot>>.NotConnected("Demo scheduling"));
    public Task<PortResult<DemoBooking>> BookAsync(Guid leadId, DemoSlot slot, string? notes, CancellationToken ct) => Task.FromResult(PortResult<DemoBooking>.NotConnected("Demo scheduling"));
}

internal sealed class UnavailableTrialProvisioner : ITrialProvisioner
{
    public Task<PortResult<TrialInfo>> CreateAsync(Guid leadId, string? plan, string? notes, CancellationToken ct) => Task.FromResult(PortResult<TrialInfo>.NotConnected("Trial provisioning"));
}

internal sealed class UnavailablePaymentRequestGateway : IPaymentRequestGateway
{
    public Task<PortResult<PaymentRequestInfo>> CreateAsync(Guid leadId, string plan, decimal amount, string currency, decimal? discountPercent, string? customTerms, CancellationToken ct) => Task.FromResult(PortResult<PaymentRequestInfo>.NotConnected("Payment requests"));
}

public static class AgentServiceCollection
{
    /// <summary>The sales agent: controlled tool gateway, tool loop, configuration/catalog/approval services. The Phase 10 ports default to "not connected" (TryAdd: Phase 10 registers the real ones first or replaces them).</summary>
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddAgents(this Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddScoped<IDemoScheduler, UnavailableDemoScheduler>(services);
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddScoped<ITrialProvisioner, UnavailableTrialProvisioner>(services);
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddScoped<IPaymentRequestGateway, UnavailablePaymentRequestGateway>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<AgentToolHandlers>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<AgentToolRunner>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<AgentLoop>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IAgentService, AgentService>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<ICatalogService, CatalogService>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IApprovalService, ApprovalService>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IToolExecutionService, ToolExecutionService>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IFollowUpService, FollowUpService>(services);
        return services;
    }
}
