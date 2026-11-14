using LeadPilot.Application.Config;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.Infrastructure.Config;

public static class ConfigServiceCollection
{
    /// <summary>Custom fields and industry packs (Phase 13). Web and Api.</summary>
    public static IServiceCollection AddIndustryConfig(this IServiceCollection services)
    {
        services.AddScoped<ICustomFieldService, CustomFieldService>();
        services.AddScoped<IPackEngineFactory, PackEngineFactory>();
        services.AddScoped(sp => new Lazy<IPackEngineFactory>(() => sp.GetRequiredService<IPackEngineFactory>()));
        services.AddScoped<IPackService, PackService>();
        services.AddScoped<IPlatformPackService, PlatformPackService>();
        return services;
    }
}
