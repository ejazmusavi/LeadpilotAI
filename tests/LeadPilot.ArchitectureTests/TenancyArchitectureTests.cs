using System.Reflection;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Common;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LeadPilot.ArchitectureTests;

/// <summary>Guards the multi-tenancy rules from docs/MULTI_TENANCY.md so later phases cannot quietly weaken them.</summary>
public class TenancyArchitectureTests
{
    /// <summary>
    /// The ONLY files allowed to call IgnoreQueryFilters(). Each needs rows of other tenants by design and states an explicit tenant predicate.
    /// Adding a file here is a security decision: review it as such.
    /// </summary>
    private static readonly string[] IgnoreQueryFiltersAllowList =
    [
        "src/LeadPilot.Infrastructure/Persistence/TenantSaveChangesInterceptor.cs", // cross-tenant FK detection
        "src/LeadPilot.Infrastructure/Tenancy/MembershipResolver.cs",              // membership lookup before a tenant is active
        "src/LeadPilot.Infrastructure/Tenancy/PermissionEvaluator.cs",             // role/permission lookup keyed by (user, tenant)
        "src/LeadPilot.Infrastructure/Tenancy/RoleProvisioner.cs",                 // provisioning system roles of a target tenant
        "src/LeadPilot.Infrastructure/Seeding/PlatformSeeder.cs",                  // startup seeding (platform scope)
        "src/LeadPilot.Infrastructure/Services/Tenancy/TenantAdminService.cs",     // platform tenant lifecycle (member counts, provisioning)
        "src/LeadPilot.Infrastructure/Services/Tenancy/InvitationService.cs",      // token-keyed invitation preview/accept
        "src/LeadPilot.Infrastructure/Services/Tenancy/TenantSwitchService.cs",    // a user's memberships across tenants
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LeadPilotAI.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("LeadPilotAI.sln not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> SourceFiles(string project) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains("Migrations"));

    private static string Relative(string path) => Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');

    [Fact]
    public void IgnoreQueryFilters_is_only_used_in_allow_listed_files()
    {
        var offenders = new List<string>();
        foreach (var project in Directory.GetDirectories(Path.Combine(RepoRoot(), "src")).Select(Path.GetFileName))
            foreach (var file in SourceFiles(project!))
                if (File.ReadAllText(file).Contains("IgnoreQueryFilters(")) offenders.Add(Relative(file));

        var unexpected = offenders.Except(IgnoreQueryFiltersAllowList).ToList();
        Assert.True(unexpected.Count == 0, "IgnoreQueryFilters() used outside the allow-list: " + string.Join(", ", unexpected));
    }

    [Fact]
    public void Allow_list_has_no_stale_entries()
    {
        foreach (var path in IgnoreQueryFiltersAllowList)
        {
            var full = Path.Combine(RepoRoot(), path);
            Assert.True(File.Exists(full), $"allow-listed file no longer exists: {path}");
            Assert.Contains("IgnoreQueryFilters(", File.ReadAllText(full));
        }
    }

    [Theory]
    [InlineData("LeadPilot.Web")]
    [InlineData("LeadPilot.Api")]
    public void Web_and_Api_never_touch_the_DbContext_directly(string project)
    {
        // Program.cs is the composition root: it may name the context only to register ASP.NET Identity's stores.
        var offenders = SourceFiles(project).Where(f => Path.GetFileName(f) != "Program.cs" && File.ReadAllText(f).Contains("AppDbContext")).Select(Relative).ToList();
        Assert.True(offenders.Count == 0, "Web/Api must go through Application use cases: " + string.Join(", ", offenders));
    }

    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseMySQL("Server=localhost;Database=model_only;User=x;Password=x;").Options;
        using var db = new AppDbContext(options, new TenantContext());
        return db.GetService<IDesignTimeModel>().Model;
    }

    [Fact]
    public void Every_tenant_owned_entity_has_a_global_query_filter_and_a_tenant_foreign_key()
    {
        var model = Model();
        var owned = model.GetEntityTypes().Where(e => typeof(ITenantOwned).IsAssignableFrom(e.ClrType)).ToList();
        Assert.True(owned.Count >= 7);
        foreach (var e in owned)
        {
            Assert.True(e.GetDeclaredQueryFilters().Any(), $"{e.ShortName()} has no tenant query filter");
            Assert.Contains(e.GetForeignKeys(), fk => fk.Properties.Single().Name == nameof(ITenantOwned.TenantId));
            Assert.False(e.FindProperty(nameof(ITenantOwned.TenantId))!.IsNullable);
        }
    }

    [Fact]
    public void Entities_with_a_TenantId_column_must_implement_ITenantOwned_unless_explicitly_exempt()
    {
        // RefreshToken binds a session to a tenant but is a per-user credential record, deliberately not row-filtered.
        var exempt = new[] { "RefreshToken" };
        var offenders = Model().GetEntityTypes()
            .Where(e => e.FindProperty("TenantId") is not null && !typeof(ITenantOwned).IsAssignableFrom(e.ClrType) && !exempt.Contains(e.ShortName()))
            .Select(e => e.ShortName()).ToList();
        Assert.True(offenders.Count == 0, "Entities with TenantId that are not ITenantOwned: " + string.Join(", ", offenders));
    }

    private static IEnumerable<Type> Controllers(Assembly a) => a.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);

    [Fact]
    public void Tenant_controllers_authorize_every_action_explicitly()
    {
        string[] tenantControllers = ["TenantsController", "UsersController", "RolesController", "InvitationsController", "SettingsController", "AuditController",
            "AuditLogsController", "PermissionsController", "WorkspaceController", "AuthController", .. CrmArchitectureTests.CrmControllers];
        var unprotected = new List<string>();
        foreach (var asm in new[] { typeof(LeadPilot.Web.Models.ErrorViewModel).Assembly, typeof(LeadPilot.Api.Controllers.AuthController).Assembly })
            foreach (var c in Controllers(asm).Where(c => tenantControllers.Contains(c.Name)))
            {
                var classHasAuth = c.GetCustomAttributes<AuthorizeAttribute>(true).Any() || c.GetCustomAttributes<AllowAnonymousAttribute>(true).Any();
                foreach (var m in c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.GetCustomAttributes<NonActionAttribute>().Count() == 0))
                {
                    var hasAuth = classHasAuth || m.GetCustomAttributes<AuthorizeAttribute>().Any() || m.GetCustomAttributes<AllowAnonymousAttribute>().Any();
                    if (!hasAuth) unprotected.Add($"{c.FullName}.{m.Name}");
                }
            }
        Assert.True(unprotected.Count == 0, "Actions without explicit authorization: " + string.Join(", ", unprotected));
    }

    [Fact]
    public void Administrative_controllers_require_a_permission_policy_that_exists_in_the_catalog()
    {
        string[] administrative = ["TenantsController", "UsersController", "RolesController", "SettingsController", "AuditController", "AuditLogsController", "PermissionsController", .. CrmArchitectureTests.CrmControllers];
        foreach (var asm in new[] { typeof(LeadPilot.Web.Models.ErrorViewModel).Assembly, typeof(LeadPilot.Api.Controllers.AuthController).Assembly })
            foreach (var c in Controllers(asm).Where(c => administrative.Contains(c.Name)))
            {
                var policies = c.GetCustomAttributes<AuthorizeAttribute>(true).Select(a => a.Policy)
                    .Concat(c.GetMethods().SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy))).ToList();
                if (c.Name == "TenantsController" && asm == typeof(LeadPilot.Web.Models.ErrorViewModel).Assembly) policies.RemoveAll(p => p is null); // Switch is for every signed-in user
                Assert.NotEmpty(policies);
                Assert.All(policies.Where(p => p is not null), p => Assert.True(PermissionCatalog.Exists(p!), $"{c.Name}: unknown policy '{p}'"));
                Assert.DoesNotContain(c.GetMethods().SelectMany(m => m.GetCustomAttributes<AllowAnonymousAttribute>()), _ => true);
            }
    }
}
