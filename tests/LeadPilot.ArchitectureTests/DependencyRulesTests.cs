using System.Reflection;

namespace LeadPilot.ArchitectureTests;

/// <summary>Enforces Clean Architecture dependency direction at assembly-reference level.</summary>
public class DependencyRulesTests
{
    private const string Domain = "LeadPilot.Domain";
    private const string Application = "LeadPilot.Application";
    private const string Infrastructure = "LeadPilot.Infrastructure";
    private const string Shared = "LeadPilot.Shared";
    private const string Web = "LeadPilot.Web";
    private const string Api = "LeadPilot.Api";
    private const string Worker = "LeadPilot.Worker";

    private static readonly Dictionary<string, Assembly> Assemblies = new()
    {
        [Domain] = typeof(LeadPilot.Domain.Common.Entity).Assembly,
        [Application] = typeof(LeadPilot.Application.DependencyInjection).Assembly,
        [Infrastructure] = typeof(LeadPilot.Infrastructure.DependencyInjection).Assembly,
        [Shared] = typeof(LeadPilot.Shared.CorrelationIds).Assembly,
        [Web] = typeof(LeadPilot.Web.Models.ErrorViewModel).Assembly,
    };

    private static IEnumerable<string> References(Assembly a) =>
        a.GetReferencedAssemblies().Select(r => r.Name!).Where(n => n.StartsWith("LeadPilot."));

    public static TheoryData<string, string[]> Forbidden => new()
    {
        { Shared, [Domain, Application, Infrastructure, Web, Api, Worker] },
        { Domain, [Application, Infrastructure, Web, Api, Worker] },
        { Application, [Infrastructure, Web, Api, Worker] },
        { Infrastructure, [Web, Api, Worker] },
        { Web, [Api, Worker] },
    };

    [Theory, MemberData(nameof(Forbidden))]
    public void Layer_does_not_reference_forbidden_projects(string layer, string[] forbidden)
    {
        var refs = References(Assemblies[layer]).ToHashSet();
        Assert.Empty(refs.Intersect(forbidden));
    }

    [Fact]
    public void Api_and_Worker_do_not_reference_each_other_or_Web()
    {
        // Api/Worker assemblies are inspected by name from their project output to avoid referencing them from this project.
        foreach (var (name, forbidden) in new[] { (Api, new[] { Web, Worker }), (Worker, new[] { Web, Api }) })
        {
            var path = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            Assert.True(File.Exists(path), $"{path} not found next to the test assembly");
            var asm = Assembly.LoadFrom(path);
            Assert.Empty(References(asm).Intersect(forbidden));
        }
    }

    [Fact]
    public void Domain_has_no_framework_or_persistence_dependencies()
    {
        var all = Assemblies[Domain].GetReferencedAssemblies().Select(r => r.Name!).ToList();
        Assert.DoesNotContain(all, n => n.StartsWith("Microsoft.EntityFrameworkCore") || n.StartsWith("Microsoft.AspNetCore")
                                         || n.StartsWith("Microsoft.Extensions.Identity") || n == "MySql.EntityFrameworkCore");
    }

    [Fact]
    public void Application_does_not_depend_on_EF_or_AspNetCore()
    {
        var all = Assemblies[Application].GetReferencedAssemblies().Select(r => r.Name!).ToList();
        Assert.DoesNotContain(all, n => n.StartsWith("Microsoft.EntityFrameworkCore") || n.StartsWith("Microsoft.AspNetCore") || n == "MySql.EntityFrameworkCore");
    }
}
