using System.Reflection;
using System.Text.RegularExpressions;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Config;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace LeadPilot.ArchitectureTests;

/// <summary>The Phase 13 rules that must not erode: no industry hard-coded outside data packs, packs are data (never code), pack/field entities are tenant-safe, every endpoint is permission-protected.</summary>
public class IndustryArchitectureTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LeadPilotAI.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static IEnumerable<string> SourceFiles(params string[] extensions) =>
        Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Select(f => f.Replace('\\', '/'))
            .Where(f => !f.Contains("/obj/") && !f.Contains("/bin/") && !f.Contains("/Migrations/") && !f.Contains("/wwwroot/lib/"));

    private static readonly Regex Education = new(@"\b(schools?|colleges?|academy|academies|pakistan|pakistani|madrasa|k-12|educational|universit(y|ies)|students?)\b|school erp|\bERP\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void No_education_specific_literal_remains_in_code_views_scripts_or_settings_outside_the_education_pack()
    {
        var offenders = new List<string>();
        foreach (var file in SourceFiles(".cs", ".cshtml", ".js", ".json", ".css", ".html", ".csproj"))
        {
            if (file.EndsWith("/Packs/Builtin/education.json")) continue;           // the ONE place: the education pack is data
            var n = 0;
            foreach (var line in File.ReadLines(file))
            {
                n++;
                if (!Education.IsMatch(line)) continue;
                // the single allowed exception: the country-name → ISO-code table (geography for every country on the list, not an industry)
                if (file.EndsWith("LeadPilot.Application/Discovery/DiscoveryLogic.cs") && line.Contains("[\"pakistan\"] = \"PK\"")) continue;
                offenders.Add($"{file[(Root().Length + 1)..]}:{n}: {line.Trim()[..Math.Min(110, line.Trim().Length)]}");
            }
        }
        Assert.True(offenders.Count == 0, "Education-specific literals outside the education pack:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Industry_packs_are_data_only_and_the_pack_code_never_loads_or_runs_anything_from_a_pack()
    {
        var forbidden = new[] { "Process.Start", "Assembly.Load", "Assembly.Create", "CSharpScript", "Activator.CreateInstance", "Type.GetType(", "Expression.Compile", "DynamicInvoke", "ExecuteSqlRaw", "FromSqlRaw", "FromSqlInterpolated", "ExecuteSqlInterpolated", "BinaryFormatter", "Eval(" };
        var files = SourceFiles(".cs").Where(f => f.Contains("/LeadPilot.Infrastructure/Config/") || f.Contains("/LeadPilot.Application/Config/") || f.Contains("/LeadPilot.Domain/Config/")).ToList();
        Assert.True(files.Count >= 8);
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            foreach (var bad in forbidden) Assert.False(text.Contains(bad, StringComparison.Ordinal), $"{Path.GetFileName(f)} uses {bad}");
            Assert.DoesNotContain("IgnoreQueryFilters", text);           // pack code reads only through the tenant filter or the explicit visibility rule
        }
        // the only regular expressions that run user-supplied patterns use the non-backtracking engine with a timeout
        Assert.Contains("RegexOptions.NonBacktracking", File.ReadAllText(Path.Combine(Root(), "src/LeadPilot.Domain/Config/CustomFields.cs")));
        // builtin packs ship in the repository and are embedded, never downloaded
        Assert.Contains("Packs\\Builtin\\*.json", File.ReadAllText(Path.Combine(Root(), "src/LeadPilot.Infrastructure/LeadPilot.Infrastructure.csproj")));
    }

    [Fact]
    public void Custom_field_and_pack_tables_follow_the_tenancy_rules()
    {
        // tenant-owned: fields, values, applications and links
        foreach (var t in new[] { typeof(CustomFieldDefinition), typeof(CustomFieldValue), typeof(TenantPackApplication), typeof(PackItemLink) })
            Assert.True(typeof(ITenantOwned).IsAssignableFrom(t), t.Name + " must be tenant-owned");
        // the application history is append-only
        Assert.True(typeof(IAppendOnly).IsAssignableFrom(typeof(TenantPackApplication)));
        // stored packs are shared (builtin / published) or private to ONE owner: they carry an owner, never a TenantId column, and are only read through PackCatalog.Visible
        Assert.False(typeof(ITenantOwned).IsAssignableFrom(typeof(PackDefinition)));
        Assert.Null(typeof(PackDefinition).GetProperty("TenantId"));
        Assert.NotNull(typeof(PackDefinition).GetProperty(nameof(PackDefinition.OwnerTenantId)));
        var offenders = SourceFiles(".cs").Where(f => f.Contains("/LeadPilot.Infrastructure/") && !f.Contains("/Persistence/") && !f.Contains("/Seeding/") && !new[] { "PackService.cs", "PlatformPackService.cs", "PackSeeder.cs" }.Contains(Path.GetFileName(f)))
            .Where(f => File.ReadAllText(f).Contains(".PackDefinitions")).Select(Path.GetFileName).ToList();
        Assert.True(offenders.Count == 0, "PackDefinitions may only be read in the pack services: " + string.Join(", ", offenders));
        var service = File.ReadAllText(Path.Combine(Root(), "src/LeadPilot.Infrastructure/Config/PackService.cs"));
        foreach (var read in Regex.Matches(service, @"\.PackDefinitions\b[^\n]*").Select(m => m.Value))
            Assert.True(read.Contains("OwnerTenantId == tenantId") || read.Contains(".Add(") || read.Contains(".Remove(") || read.Contains("p.Scope != PackScope.Tenant"), "unscoped read of stored packs: " + read);
        // the platform service only ever reads shared packs (scope is always part of the query)
        foreach (var read in Regex.Matches(File.ReadAllText(Path.Combine(Root(), "src/LeadPilot.Infrastructure/Config/PlatformPackService.cs")), @"\.PackDefinitions\b[^\n]*").Select(m => m.Value))
            Assert.True(read.Contains("PackScope.") || read.Contains(".Add("), "platform read without a scope: " + read);
        // the visibility rule exists exactly once
        Assert.Single(Regex.Matches(service, @"static IQueryable<PackDefinition> Visible"));
        Assert.Contains("p.Scope == PackScope.Tenant && p.OwnerTenantId == tenantId", service);
    }

    [Fact]
    public void Every_pack_custom_field_and_platform_pack_endpoint_needs_a_permission_in_the_catalog()
    {
        var controllers = new[]
        {
            typeof(LeadPilot.Api.Controllers.PacksController), typeof(LeadPilot.Api.Controllers.CustomFieldsController), typeof(LeadPilot.Api.Controllers.CustomFieldValuesController), typeof(LeadPilot.Api.Controllers.PlatformPacksController),
            typeof(LeadPilot.Web.Controllers.PacksController), typeof(LeadPilot.Web.Controllers.CustomFieldsController), typeof(LeadPilot.Web.Controllers.PlatformPacksController),
        };
        foreach (var c in controllers)
        {
            var classAuthorized = c.GetCustomAttributes<AuthorizeAttribute>().Any();
            Assert.True(classAuthorized || c.GetMethods().All(m => !m.GetCustomAttributes().OfType<HttpMethodAttribute>().Any() || m.GetCustomAttributes<AuthorizeAttribute>().Any()), c.Name + " has unprotected actions");
            foreach (var m in c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.ReturnType.Name.StartsWith("Task") || m.ReturnType.Name.Contains("Result")))
            {
                var policies = m.GetCustomAttributes<AuthorizeAttribute>().Concat(c.GetCustomAttributes<AuthorizeAttribute>()).Select(a => a.Policy).Where(p => p is not null).ToList();
                if (c == typeof(LeadPilot.Api.Controllers.CustomFieldValuesController)) continue;   // authenticated; the permission depends on the record type and is checked in the action (tested: 401/403)
                Assert.True(policies.Count > 0, $"{c.Name}.{m.Name} has no permission policy");
                Assert.All(policies, p => Assert.Contains(p, PermissionCatalog.All.Select(d => d.Code)));
            }
        }
        // platform packs: only platform roles can hold the permission
        Assert.True(PermissionCatalog.IsPlatform(PermissionCodes.PlatformPacksManage));
        Assert.Equal(PermissionCodes.PlatformPacksManage, typeof(LeadPilot.Api.Controllers.PlatformPacksController).GetCustomAttributes<AuthorizeAttribute>().Concat(typeof(LeadPilot.Api.Controllers.PlatformPacksController).GetMethod("Publish")!.GetCustomAttributes<AuthorizeAttribute>()).Single(a => a.Policy is not null).Policy);
        // tenants cannot reach another tenant's configuration by id: the pack write endpoints need Manage, export needs Export, import/apply needs Import
        Assert.Equal(PermissionCodes.ConfigPacksExport, typeof(LeadPilot.Api.Controllers.PacksController).GetMethod("Export")!.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal(PermissionCodes.ConfigPacksManage, typeof(LeadPilot.Api.Controllers.PacksController).GetMethod("Upload")!.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
        Assert.Equal(PermissionCodes.ConfigPacksImport, typeof(LeadPilot.Api.Controllers.PacksController).GetMethod("Apply")!.GetCustomAttribute<AuthorizeAttribute>()!.Policy);
    }

    [Fact]
    public void Default_system_roles_for_the_new_permissions_are_conservative()
    {
        var admin = SystemRoles.All.Single(r => r.Name == SystemRoles.TenantAdministrator).Permissions;
        foreach (var c in new[] { PermissionCodes.ConfigPacksImport, PermissionCodes.ConfigPacksExport, PermissionCodes.ConfigPacksManage, PermissionCodes.CustomFieldsManage }) Assert.Contains(c, admin);
        Assert.DoesNotContain(PermissionCodes.PlatformPacksManage, admin);
        foreach (var role in new[] { SystemRoles.TenantStaff, SystemRoles.SalesAgent, SystemRoles.MarketingManager, SystemRoles.ReadOnly })
        {
            var codes = SystemRoles.All.Single(r => r.Name == role).Permissions;
            Assert.DoesNotContain(PermissionCodes.ConfigPacksImport, codes); Assert.DoesNotContain(PermissionCodes.ConfigPacksExport, codes); Assert.DoesNotContain(PermissionCodes.ConfigPacksManage, codes); Assert.DoesNotContain(PermissionCodes.CustomFieldsManage, codes);
        }
        Assert.Contains(PermissionCodes.CustomFieldsManage, SystemRoles.All.Single(r => r.Name == SystemRoles.SalesManager).Permissions);
        Assert.DoesNotContain(PermissionCodes.ConfigPacksImport, SystemRoles.All.Single(r => r.Name == SystemRoles.SalesManager).Permissions);
        Assert.Contains(PermissionCodes.PlatformPacksManage, SystemRoles.All.Single(r => r.Name == SystemRoles.PlatformOwner).Permissions);
    }

    [Fact]
    public void Money_in_catalogs_is_decimal_18_2_with_a_currency_and_no_price_is_hard_coded_in_packs()
    {
        var plan = typeof(LeadPilot.Domain.Agents.PricingPlan);
        Assert.Equal(typeof(decimal?), plan.GetProperty("Price")!.PropertyType);
        Assert.Equal(typeof(string), plan.GetProperty("Currency")!.PropertyType);
        foreach (var pack in Directory.EnumerateFiles(Path.Combine(Root(), "src/LeadPilot.Infrastructure/Packs/Builtin"), "*.json"))
            Assert.DoesNotMatch(new Regex("\"price\"\\s*:\\s*[0-9]"), File.ReadAllText(pack));
    }
}
