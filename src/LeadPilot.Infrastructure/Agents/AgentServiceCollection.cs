using LeadPilot.Application.Agents;

namespace LeadPilot.Infrastructure.Agents;

public static class AgentServiceCollection
{
    /// <summary>The sales agent: controlled tool gateway, tool loop, configuration/catalog/approval services. The Phase 10 ports (demo scheduler, trial provisioner, payment request gateway) are registered by AddConversion.</summary>
    public static Microsoft.Extensions.DependencyInjection.IServiceCollection AddAgents(this Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
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
