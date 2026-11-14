using System.Reflection;
using LeadPilot.Application.Discovery;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Discovery;
using LeadPilot.Infrastructure.Discovery.Website;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.ArchitectureTests;

/// <summary>Rules that keep Phase 4 discovery inside the tenancy, SSRF and data-source rules.</summary>
public class DiscoveryArchitectureTests
{
    private static readonly Assembly Infrastructure = typeof(SafeHttpClientFactory).Assembly;
    private static readonly Assembly Web = typeof(LeadPilot.Web.Models.ErrorViewModel).Assembly;
    private static readonly Assembly Api = typeof(LeadPilot.Api.Controllers.AuthController).Assembly;

    [Fact]
    public void Every_discovery_entity_is_tenant_owned()
    {
        var entities = typeof(DiscoveryJob).Assembly.GetTypes().Where(t => t.Namespace == typeof(DiscoveryJob).Namespace && typeof(Entity).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.True(entities.Count >= 6);
        Assert.All(entities, t => Assert.True(typeof(ITenantOwned).IsAssignableFrom(t), $"{t.Name} must be tenant owned"));
        Assert.True(typeof(IAppendOnly).IsAssignableFrom(typeof(DiscoveryUsageEntry)));
    }

    [Fact]
    public void Only_the_ssrf_safe_factory_may_create_http_clients_for_website_fetching()
    {
        var offenders = new List<string>();
        foreach (var t in Infrastructure.GetTypes().Where(t => t.Namespace == "LeadPilot.Infrastructure.Discovery.Website" && t != typeof(SafeHttpClientFactory) && !t.IsNested))
        {
            var used = t.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).Concat(t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Select(f => f.FieldType))
                .Concat(t.GetProperties().Select(p => p.PropertyType));
            if (used.Any(x => x == typeof(HttpClient) || x == typeof(IHttpClientFactory) || x == typeof(HttpMessageHandler) || x == typeof(SocketsHttpHandler))) offenders.Add(t.Name);
        }
        Assert.True(offenders.Count == 0, "Website fetching types must use ISafeHttpClientFactory only: " + string.Join(", ", offenders));

        // and no source file in the Website folder news up an HttpClient/handler or asks for IHttpClientFactory, except the factory itself
        var dir = Path.Combine(RepoRoot(), "src", "LeadPilot.Infrastructure", "Discovery", "Website");
        foreach (var file in Directory.GetFiles(dir, "*.cs").Where(f => Path.GetFileName(f) != "SafeHttpClientFactory.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("new HttpClient", text); Assert.DoesNotContain("HttpClientHandler", text); Assert.DoesNotContain("IHttpClientFactory", text); Assert.DoesNotContain("SocketsHttpHandler", text);
        }
        Assert.NotNull(typeof(SafeHttpSender).GetConstructors().Single().GetParameters().Single(p => p.ParameterType == typeof(ISafeHttpClientFactory)));
    }

    [Fact]
    public void The_ssrf_safe_factory_never_follows_redirects_uses_no_proxy_and_validates_in_the_connect_callback()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LeadPilot.Infrastructure", "Discovery", "Website", "SafeHttpClientFactory.cs"));
        Assert.Contains("AllowAutoRedirect = false", text); Assert.Contains("UseProxy = false", text); Assert.Contains("UseCookies = false", text); Assert.Contains("ConnectCallback", text); Assert.Contains("policy.IsAllowed", text);
    }

    [Fact]
    public void Connector_adapters_live_in_infrastructure_and_the_application_layer_only_knows_the_port()
    {
        Assert.Contains(typeof(IDiscoveryConnector), Infrastructure.GetTypes().Where(t => t.Name == "GooglePlacesConnector").SelectMany(t => t.GetInterfaces()));
        Assert.DoesNotContain(typeof(IDiscoveryConnector).Assembly.GetTypes(), t => t.Name.Contains("GooglePlaces", StringComparison.OrdinalIgnoreCase) && t != typeof(GooglePlacesOptions));
        foreach (var asm in new[] { Web, Api })
            Assert.DoesNotContain(asm.GetTypes().SelectMany(t => t.GetConstructors()).SelectMany(c => c.GetParameters()), p => p.ParameterType.Namespace?.StartsWith("LeadPilot.Infrastructure.Discovery.Connectors") == true);
    }

    [Fact]
    public void No_fake_or_test_only_types_exist_in_the_shipped_assemblies()
    {
        foreach (var asm in new[] { Infrastructure, typeof(DiscoveryJob).Assembly, typeof(IDiscoveryConnector).Assembly, Web, Api })
            Assert.DoesNotContain(asm.GetTypes(), t => t.Name.StartsWith("Fake", StringComparison.Ordinal) || t.Name.Contains("TestOnly", StringComparison.Ordinal) || t.Name.StartsWith("Mock", StringComparison.Ordinal));
        // the stubs are descriptor-only: they must not contain any code path that returns candidates
        var stub = Infrastructure.GetType("LeadPilot.Infrastructure.Discovery.Connectors.DescriptorOnlyConnector")!;
        var body = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LeadPilot.Infrastructure", "Discovery", "Connectors", "OtherConnectors.cs"));
        var stubSource = body[body.IndexOf("class DescriptorOnlyConnector", StringComparison.Ordinal)..body.IndexOf("class DiscoveryConnectorRegistry", StringComparison.Ordinal)];
        Assert.Contains("throw new ConnectorNotConfiguredException", stubSource); Assert.DoesNotContain("new DiscoveryCandidate", stubSource); Assert.NotNull(stub);
    }

    [Fact]
    public void Hangfire_is_in_the_worker_and_the_dashboard_is_platform_only()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LeadPilot.Web", "Program.cs"));
        Assert.Contains("PlatformDashboardAuthorizationFilter", program); Assert.Contains("Authorization = []", program); Assert.Contains("RequireAuthorization()", program);
        Assert.DoesNotContain("MapHangfireDashboard", File.ReadAllText(Path.Combine(RepoRoot(), "src", "LeadPilot.Api", "Program.cs")));
        Assert.Contains("AddWorkerServices", File.ReadAllText(Path.Combine(RepoRoot(), "src", "LeadPilot.Worker", "Program.cs")));
        // only the Worker host processes jobs: web hosts register the client side only
        var di = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LeadPilot.Infrastructure", "DependencyInjection.cs"));
        Assert.Contains("AddDiscoveryHangfire(config, server: false)", di);
    }

    [Fact]
    public void Discovery_jobs_run_through_the_tenant_scope_runner_and_services_never_bypass_the_tenant_filter()
    {
        var executor = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LeadPilot.Infrastructure", "Discovery", "Jobs", "HangfireIntegration.cs"));
        Assert.Contains("scopes.RunAsync(tenantId", executor);
        foreach (var f in Directory.GetFiles(Path.Combine(RepoRoot(), "src", "LeadPilot.Infrastructure", "Discovery"), "*.cs", SearchOption.AllDirectories)) Assert.DoesNotContain("IgnoreQueryFilters(", File.ReadAllText(f));
    }

    [Fact]
    public void Discovery_controllers_use_discovery_or_integration_permissions_and_never_expose_secrets()
    {
        string[] allowed = ["Discovery.View", "Discovery.Run", "Discovery.Review", "Discovery.Manage", "Integrations.Manage"];
        foreach (var asm in new[] { Web, Api })
            foreach (var c in asm.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && t.Name.StartsWith("Discovery") || t.Name is "ConnectorsController"))
                foreach (var a in c.GetCustomAttributes<AuthorizeAttribute>(true).Concat(c.GetMethods().SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>())))
                    Assert.Contains(a.Policy, allowed);
        // the connector DTO has a hint, never the key
        Assert.DoesNotContain(typeof(ConnectorStatusDto).GetProperties(), p => p.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Protected", StringComparison.OrdinalIgnoreCase));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LeadPilotAI.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
