extern alias ApiApp;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Seeding;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace LeadPilot.IntegrationTests;

/// <summary>
/// The Phase 3 migration must apply to a database that is already at the Phase 2 schema and holds a tenant, and the startup seeder must then
/// give that existing tenant the new permissions (in its system roles) and its default pipeline, statuses and scoring rules.
/// </summary>
[Collection("app")]
public class CrmMigrationTests(AppFixture app)
{
    private static readonly string[] Phase2Permissions =
    [
        "Leads.View", "Leads.Create", "Leads.Update", "Leads.Delete", "Campaigns.View", "Campaigns.Create", "Campaigns.Execute", "Conversations.View", "Conversations.Reply",
        "Agents.Manage", "Reports.View", "Users.Manage", "Settings.Manage", "Audit.View", "Tenants.Manage", "Platform.SwitchTenant"
    ];

    private static readonly string[] NewPermissions = ["Leads.Import", "Leads.Export", "Leads.Assign", "Consent.Manage", "Suppression.Manage", "Pipelines.Manage", "Scoring.Manage"];

    [RequiresMySqlFact]
    public async Task A_phase2_database_upgrades_and_existing_tenants_receive_crm_defaults_and_permissions()
    {
        var adminConn = Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar)!;
        var dbName = "leadpilot_upgrade_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        var oldEnv = (Environment.GetEnvironmentVariable("ConnectionStrings__Default"), Environment.GetEnvironmentVariable("Database__MigrateOnStartup"));
        WebApplicationFactory<Program>? factory = null;
        try
        {
            // ---- 1. a database exactly as Phase 2 left it, with one customer tenant ----
            var tenantContext = new TenantContext();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options;
            Guid tenantId;
            await using (var ctx = new AppDbContext(options, tenantContext))
            {
                var phase2 = ctx.Database.GetMigrations().Single(m => m.EndsWith("_TenancyAndPermissions"));
                await ctx.GetService<IMigrator>().MigrateAsync(phase2);
                Assert.DoesNotContain("Phase3Crm", await ctx.Database.GetAppliedMigrationsAsync());
                Assert.Equal(0L, await Scalar(cs, "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN ('Leads','Contacts','ConsentRecords','Pipelines')"));

                using var _ = tenantContext.BeginPlatformScope();
                var now = DateTime.UtcNow;
                var permissions = Phase2Permissions.Select((code, i) => Permission.Create(code, code, null, code.Split('.')[0], code is "Tenants.Manage" or "Platform.SwitchTenant", (i + 1) * 10)).ToList();
                ctx.Permissions.AddRange(permissions);
                var tenant = Tenant.Create("Legacy Co", "legacy-co", "Education", "UTC", "en", now);
                tenantId = tenant.Id;
                ctx.Tenants.Add(tenant);
                Role Role(string name, bool system, params string[] codes)
                {
                    var r = Domain(name, system);
                    ctx.TenantRoles.Add(r);
                    foreach (var p in permissions.Where(p => codes.Contains(p.Code))) ctx.RolePermissions.Add(RolePermission.Create(tenantId, r.Id, p.Id));
                    return r;
                }
                Role Domain(string name, bool system) => LeadPilot.Domain.Tenancy.Role.Create(tenantId, name, null, system, now);
                Role("Sales Manager", true, "Leads.View", "Leads.Create", "Leads.Update", "Leads.Delete", "Campaigns.View", "Conversations.View", "Conversations.Reply", "Reports.View");
                Role("Sales Agent", true, "Leads.View", "Leads.Create", "Leads.Update", "Conversations.View", "Conversations.Reply");
                Role("Tenant Administrator", true, Phase2Permissions.Where(c => c is not ("Tenants.Manage" or "Platform.SwitchTenant")).ToArray());
                Role("Legacy custom role", false, "Leads.View");
                await ctx.SaveChangesAsync();
            }

            // ---- 2. start the application on that database (migrates, then seeds) ----
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", cs);
            Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "true");
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
            factory.CreateClient().Dispose();
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", oldEnv.Item1);       // the shared fixture's lazily built API host must still find ITS database
            Environment.SetEnvironmentVariable("Database__MigrateOnStartup", oldEnv.Item2);

            // ---- 3. everything is there ----
            async Task<T> WithDb<T>(Func<AppDbContext, Task<T>> work)
            {
                using var scope = factory!.Services.CreateScope();
                using var __ = scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().BeginPlatformScope();
                return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
            }
            Assert.Contains(await WithDb(d => d.Database.GetAppliedMigrationsAsync().ContinueWith(t => t.Result.ToList())), m => m.EndsWith("_Phase3Crm"));
            Assert.Equal(20L, await Scalar(cs, "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN " +
                "('LeadCategories','LeadStatuses','Pipelines','PipelineStages','Leads','LeadSources','Contacts','ConsentRecords','SuppressionEntries','LeadActivities','LeadNotes','LeadTags','LeadTagMaps','LeadScores','ScoringRuleSets','ScoringRules','Opportunities','OpportunityActivities','ImportJobs','ImportRowErrors')"));

            var codesByRole = await WithDb(async d => (await (from rp in d.RolePermissions.IgnoreQueryFilters() join p in d.Permissions on rp.PermissionId equals p.Id
                                                              join r in d.TenantRoles.IgnoreQueryFilters() on rp.RoleId equals r.Id where r.TenantId == tenantId select new { r.Name, p.Code }).ToListAsync())
                .GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.Select(x => x.Code).ToHashSet()));
            Assert.True(NewPermissions.All(p => PermissionCatalog.Exists(p)));
            Assert.Superset(NewPermissions.ToHashSet(), codesByRole["Tenant Administrator"].Intersect(NewPermissions).ToHashSet());
            Assert.Equal(PermissionCatalog.TenantCodes.Order().ToList(), codesByRole["Tenant Administrator"].Order().ToList());
            foreach (var code in new[] { "Leads.Import", "Leads.Export", "Leads.Assign", "Consent.Manage", "Suppression.Manage", "Pipelines.Manage", "Scoring.Manage" }) Assert.Contains(code, codesByRole["Sales Manager"]);
            Assert.Contains("Leads.Delete", codesByRole["Sales Manager"]);                      // old grants stay
            Assert.Empty(codesByRole["Sales Agent"].Intersect(NewPermissions));                // agents gain none of the new powers
            Assert.Contains("Leads.Update", codesByRole["Sales Agent"]);
            Assert.Equal(["Leads.View"], codesByRole["Legacy custom role"]);                    // custom roles are never touched

            var crm = await WithDb(async d => new
            {
                Pipelines = await d.Pipelines.IgnoreQueryFilters().Where(p => p.TenantId == tenantId).ToListAsync(),
                Stages = await d.PipelineStages.IgnoreQueryFilters().Where(s => s.TenantId == tenantId).OrderBy(s => s.Position).Select(s => s.Name).ToListAsync(),
                Statuses = await d.LeadStatuses.IgnoreQueryFilters().Where(s => s.TenantId == tenantId).CountAsync(),
                RuleSets = await d.ScoringRuleSets.IgnoreQueryFilters().Where(s => s.TenantId == tenantId).ToListAsync(),
                Rules = await d.ScoringRules.IgnoreQueryFilters().Where(s => s.TenantId == tenantId).CountAsync(),
                PlatformPipelines = await d.Pipelines.IgnoreQueryFilters().CountAsync(p => p.TenantId != tenantId && d.Tenants.Any(t => t.Id == p.TenantId && t.IsPlatform)),
                TenantStillThere = await d.Tenants.AnyAsync(t => t.Id == tenantId && t.Slug == "legacy-co"),
            });
            Assert.True(crm.TenantStillThere);
            Assert.True(Assert.Single(crm.Pipelines).IsDefault);
            Assert.Equal(DefaultPipeline.Stages.Select(s => s.Name), crm.Stages);
            Assert.Equal(4, crm.Statuses);
            var ruleSet = Assert.Single(crm.RuleSets);
            Assert.True(ruleSet.IsActive && ruleSet.Version == 1);
            Assert.Equal(9, crm.Rules);
            Assert.Equal(0, crm.PlatformPipelines);                                              // the platform tenant does not get CRM data

            // ---- 4. the seeder is idempotent: a second run changes nothing ----
            await PlatformSeeder.SeedAsync(factory.Services, factory.Services.GetRequiredService<IConfiguration>());
            var again = await WithDb(async d => (await d.Pipelines.IgnoreQueryFilters().CountAsync(), await d.PipelineStages.IgnoreQueryFilters().CountAsync(), await d.LeadStatuses.IgnoreQueryFilters().CountAsync(),
                await d.ScoringRuleSets.IgnoreQueryFilters().CountAsync(), await d.RolePermissions.IgnoreQueryFilters().CountAsync()));
            await PlatformSeeder.SeedAsync(factory.Services, factory.Services.GetRequiredService<IConfiguration>());
            Assert.Equal(again, await WithDb(async d => (await d.Pipelines.IgnoreQueryFilters().CountAsync(), await d.PipelineStages.IgnoreQueryFilters().CountAsync(), await d.LeadStatuses.IgnoreQueryFilters().CountAsync(),
                await d.ScoringRuleSets.IgnoreQueryFilters().CountAsync(), await d.RolePermissions.IgnoreQueryFilters().CountAsync())));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", oldEnv.Item1);
            Environment.SetEnvironmentVariable("Database__MigrateOnStartup", oldEnv.Item2);
            factory?.Dispose();
            MySqlConnection.ClearAllPools();
            await Exec(adminConn, $"DROP DATABASE IF EXISTS `{dbName}`");
        }
    }

    [RequiresMySqlFact]
    public async Task The_model_has_no_pending_changes_relative_to_the_latest_migration()
    {
        using var scope = app.World.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = db.Database.HasPendingModelChanges();
        Assert.False(pending, "The EF model differs from the migration snapshot: add a migration.");
    }

    private static async Task Exec(string conn, string sql)
    {
        await using var c = new MySqlConnection(conn); await c.OpenAsync();
        await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(string conn, string sql)
    {
        await using var c = new MySqlConnection(conn); await c.OpenAsync();
        await using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
