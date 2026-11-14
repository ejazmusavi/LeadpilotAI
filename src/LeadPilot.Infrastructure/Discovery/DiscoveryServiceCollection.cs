using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Discovery;
using LeadPilot.Infrastructure.Discovery.Connectors;
using LeadPilot.Infrastructure.Discovery.Jobs;
using LeadPilot.Infrastructure.Discovery.Services;
using LeadPilot.Infrastructure.Discovery.Website;
using LeadPilot.Infrastructure.Messaging;
using LeadPilot.Infrastructure.Services.Tenancy;
using LeadPilot.Infrastructure.Tenancy;
using LeadPilot.Infrastructure.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LeadPilot.Infrastructure.Discovery;

public static class DiscoveryServiceCollection
{
    /// <summary>
    /// Discovery engine and use cases: connectors, SSRF-safe website fetching, usage tracking, catalog, job runner and the job/result/connector/catalog services.
    /// Needs the CRM services (CrmContext, duplicate detector, lead intake). Does NOT include Hangfire: see <see cref="HangfireSetup.AddDiscoveryHangfire"/>.
    /// </summary>
    public static IServiceCollection AddDiscovery(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<DiscoveryOptions>(config.GetSection(DiscoveryOptions.Section));

        // website fetching: the only HTTP path to arbitrary sites
        services.TryAddSingleton<IDnsResolver, SystemDnsResolver>();
        services.TryAddSingleton<IAddressPolicy, PublicOnlyAddressPolicy>();
        services.TryAddSingleton<ISafeHttpClientFactory, SafeHttpClientFactory>();
        services.TryAddSingleton<IHttpSender, SafeHttpSender>();
        services.TryAddSingleton<UrlGuard>();
        services.TryAddSingleton<SafeWebFetcher>();
        services.AddScoped<IWebsiteEnricher, WebsiteEnricher>();

        // connectors
        var googleBase = config["Discovery:GooglePlaces:BaseUrl"] ?? new GooglePlacesOptions().BaseUrl;
        var googleTimeout = config.GetValue("Discovery:GooglePlaces:TimeoutSeconds", new GooglePlacesOptions().TimeoutSeconds);
        services.AddHttpClient(GooglePlacesConnector.HttpClientName, c => { c.BaseAddress = new Uri(googleBase); c.Timeout = TimeSpan.FromSeconds(googleTimeout); });
        services.AddScoped<IDiscoveryConnector, GooglePlacesConnector>();
        services.AddScoped<IDiscoveryConnector, WebsiteConnector>();
        foreach (var connector in DiscoveryConnectorRegistry.BuiltInDescriptorConnectors()) services.AddSingleton(connector);
        services.AddScoped<IDiscoveryConnectorRegistry, DiscoveryConnectorRegistry>();

        // engine + use cases
        services.AddScoped<DiscoveryUsage>();
        services.AddScoped<DiscoveryCatalogService>();
        services.AddScoped<IDiscoveryCatalogService>(sp => sp.GetRequiredService<DiscoveryCatalogService>());
        services.AddScoped<IDiscoveryConnectorService, DiscoveryConnectorService>();
        services.AddScoped<IDiscoveryJobService, DiscoveryJobService>();
        services.AddScoped<IDiscoveryResultService, DiscoveryResultService>();
        services.AddScoped<IDiscoveryJobRunner, DiscoveryJobRunner>();
        services.AddScoped<DiscoveryRetentionStore>();
        services.AddScoped<IDiscoveryRetention, DiscoveryRetention>();
        return services;
    }

    /// <summary>Everything the Worker host registers on top of AddInfrastructure: no HTTP edge, the CRM and discovery engine, and the Hangfire SERVER. Shared with the integration tests so they exercise the real composition.</summary>
    public static IServiceCollection AddWorkerServices(this IServiceCollection services, IConfiguration config)
    {
        services.AddBackgroundTenancyServices();
        services.AddCrmServices(config);
        services.AddDiscovery(config);
        services.AddDiscoveryHangfire(config, server: true);
        services.AddMessaging(config);
        services.AddMessagingConsumers();
        return services;
    }

    /// <summary>What the Worker needs instead of the HTTP/Identity edge: no signed-in user (jobs audit with the requesting user's id explicitly), permission evaluation, audit and settings.</summary>
    public static IServiceCollection AddBackgroundTenancyServices(this IServiceCollection services)
    {
        services.AddSingleton<NoRequestInfo>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<NoRequestInfo>());
        services.AddScoped<IRequestInfo>(sp => sp.GetRequiredService<NoRequestInfo>());
        services.AddScoped<IMembershipResolver, MembershipResolver>();
        services.AddScoped<IPermissionEvaluator, PermissionEvaluator>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<ITenantSettingsService, TenantSettingsService>();
        return services;
    }
}
