using System.Reflection;
using LeadPilot.Application.Analytics;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Analytics;
using LeadPilot.Domain.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace LeadPilot.ArchitectureTests;

public class AnalyticsArchitectureTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LeadPilotAI.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
    private static IEnumerable<string> Files(string rel) => Directory.EnumerateFiles(Path.Combine(Root(), "src", rel), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/") && !f.Contains("/bin/") && !f.Contains("Migrations"));

    [Fact]
    public void Every_analytics_entity_is_tenant_owned()
    {
        var entities = typeof(DailyMetric).Assembly.GetTypes().Where(t => t.Namespace == typeof(DailyMetric).Namespace && typeof(Entity).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.Equal(2, entities.Count);
        Assert.All(entities, t => Assert.True(typeof(ITenantOwned).IsAssignableFrom(t), $"{t.Name} must be tenant owned"));
    }

    [Fact]
    public void Every_analytics_action_is_authorized_with_a_permission_policy_and_controllers_never_touch_the_database()
    {
        var controllers = new[] { typeof(LeadPilot.Api.Controllers.AnalyticsController), typeof(LeadPilot.Web.Controllers.ReportsController) };
        foreach (var c in controllers)
        {
            var actions = c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.GetCustomAttributes().OfType<HttpMethodAttribute>().Any()).ToList();
            Assert.NotEmpty(actions);
            foreach (var m in actions)
            {
                var policies = m.GetCustomAttributes<AuthorizeAttribute>().Concat(c.GetCustomAttributes<AuthorizeAttribute>()).Select(a => a.Policy).Where(p => p is not null).ToList();
                Assert.True(policies.Count > 0, $"{c.Name}.{m.Name} has no permission policy");
                Assert.All(policies, p => Assert.Contains(p, PermissionCatalog.All.Select(d => d.Code)));
            }
            Assert.DoesNotContain(c.GetConstructors().Single().GetParameters(), p => p.ParameterType.Name.Contains("DbContext"));
        }
        // the export endpoints need the export permission, not just the view permission
        Assert.Equal(PermissionCodes.ReportsExport, typeof(LeadPilot.Api.Controllers.AnalyticsController).GetMethod("Export")!.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal(PermissionCodes.ReportsExport, typeof(LeadPilot.Web.Controllers.ReportsController).GetMethod("Export")!.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [Fact]
    public void Only_the_analytics_cache_builds_cache_keys_and_it_uses_the_shared_tenant_helper()
    {
        foreach (var f in Files("LeadPilot.Infrastructure/Analytics"))
        {
            var text = File.ReadAllText(f); var name = Path.GetFileName(f);
            Assert.DoesNotContain("IMemoryCache", text); Assert.DoesNotContain("IDistributedCache", text); Assert.DoesNotContain("$\"t:", text);
            if (name != "Cache.cs") Assert.DoesNotContain("new MemoryCache", text);
        }
        Assert.Contains("TenantCacheKeys.For(", File.ReadAllText(Files("LeadPilot.Infrastructure/Analytics").Single(f => f.EndsWith("Cache.cs"))));
        // no competing keys anywhere else in the product
        var offenders = Files(".").Where(f => !f.Contains("Application/Tenancy/TenantKeys.cs") && File.ReadAllText(f).Contains("\"t:{")).Select(Path.GetFileName).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void Analytics_queries_aggregate_in_sql_and_never_load_whole_tables()
    {
        foreach (var f in Files("LeadPilot.Infrastructure/Analytics"))
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain(".AsEnumerable(", text); Assert.DoesNotContain(".Include(", text); Assert.DoesNotContain(".Load(", text);
        }
        // every root query names the tenant (index use + defence in depth on top of the query filter)
        var queries = File.ReadAllText(Files("LeadPilot.Infrastructure/Analytics").Single(f => f.EndsWith("Queries.cs")));
        Assert.True(System.Text.RegularExpressions.Regex.Matches(queries, @"db\.\w+\.(AsNoTracking\(\)\.)?Where\(\w+ => \w+\.TenantId == ").Count >= 15);
    }

    [Fact]
    public void Analytics_has_one_scheduler_one_audit_path_and_the_export_is_audited_before_any_data_is_sent()
    {
        foreach (var f in Files("LeadPilot.Infrastructure/Analytics")) { var t = File.ReadAllText(f); Assert.DoesNotContain("BackgroundService", t); Assert.DoesNotContain("IHostedService", t); Assert.DoesNotContain("new Timer(", t); Assert.DoesNotContain("PeriodicTimer", t); }
        Assert.Contains("recurring.AddOrUpdate<AnalyticsJob>", File.ReadAllText(Files("LeadPilot.Infrastructure/Analytics").Single(f => f.EndsWith("Jobs.cs"))));
        Assert.Contains("Analytics.AnalyticsJob.ScheduleRecurring(recurring)", File.ReadAllText(Files("LeadPilot.Infrastructure/Discovery").Single(f => f.EndsWith("HangfireIntegration.cs"))));
        var svc = File.ReadAllText(Files("LeadPilot.Infrastructure/Analytics").Single(f => f.EndsWith("Service.cs")));
        var export = svc[svc.IndexOf("ExportCsvAsync(string key", StringComparison.Ordinal)..];
        var audit = export.IndexOf("RecordAndSaveAsync", StringComparison.Ordinal); var open = export.IndexOf("openOutput(file)", StringComparison.Ordinal); var perm = export.IndexOf("PermissionCodes.ReportsExport", StringComparison.Ordinal);
        Assert.True(perm >= 0 && audit > perm && open > audit, "the export must check the permission, then be audited, before the output is opened");
        // the CSV writer is the shared formula-injection-safe one
        Assert.Contains("SpreadsheetSafe.CsvCell", svc);
        // the only code that writes CSV for analytics lives in the service
        Assert.DoesNotContain(Files("LeadPilot.Web/Controllers").Concat(Files("LeadPilot.Api/Controllers")).Where(f => f.Contains("Analytics")), f => File.ReadAllText(f).Contains("StreamWriter"));
    }

    [Fact]
    public void The_rollup_is_recomputed_from_raw_rows_and_never_incremented()
    {
        var text = File.ReadAllText(Files("LeadPilot.Infrastructure/Analytics").Single(f => f.EndsWith("Rollup.cs")));
        Assert.Contains("ExecuteDeleteAsync", text); Assert.Contains("DaysAsync(", text);
        Assert.DoesNotMatch(@"\.Count\s*\+=|\.Count\+\+|Count = .*\.Count \+", text);
        // the Phase 6 campaign statistics pattern is reused, not replaced
        Assert.Contains("RecomputeRollupAsync", File.ReadAllText(Files("LeadPilot.Infrastructure/Campaigns").Single(f => f.EndsWith("CampaignCore.cs"))));
    }
}
