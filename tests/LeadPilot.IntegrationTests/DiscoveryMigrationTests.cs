extern alias ApiApp;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace LeadPilot.IntegrationTests;

/// <summary>The Phase 4 migration applies to a database that is already at the Phase 3 schema and holds a tenant with CRM data; nothing is lost and existing roles gain the discovery permissions.</summary>
[Collection("app")]
public class DiscoveryMigrationTests(AppFixture app)
{
    private static readonly string[] Phase3Permissions =
    [
        "Leads.View", "Leads.Create", "Leads.Update", "Leads.Delete", "Leads.Import", "Leads.Export", "Leads.Assign", "Consent.Manage", "Suppression.Manage", "Pipelines.Manage", "Scoring.Manage",
        "Campaigns.View", "Campaigns.Create", "Campaigns.Execute", "Conversations.View", "Conversations.Reply", "Agents.Manage", "Reports.View", "Users.Manage", "Settings.Manage", "Audit.View", "Tenants.Manage", "Platform.SwitchTenant",
    ];
    private static readonly string[] NewPermissions = ["Discovery.View", "Discovery.Run", "Discovery.Review", "Discovery.Manage", "Integrations.Manage"];
    private const string DiscoveryTables = "('DiscoveryJobs','DiscoveryResults','DiscoveryConnectorConfigs','DiscoveryUsageEntries','DiscoveryCategoryMappings','DiscoveryLocationPresets')";

    [RequiresMySqlFact]
    public async Task A_phase3_database_with_data_upgrades_keeps_everything_and_existing_roles_receive_the_discovery_permissions()
    {
        var adminConn = Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar)!;
        var dbName = "leadpilot_upgrade4_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        var oldEnv = (Environment.GetEnvironmentVariable("ConnectionStrings__Default"), Environment.GetEnvironmentVariable("Database__MigrateOnStartup"));
        WebApplicationFactory<Program>? factory = null;
        try
        {
            // ---- 1. a database exactly as Phase 3 left it: tenant, roles, CRM defaults, one lead with a contact ----
            var tenantContext = new TenantContext(); Guid tenantId, leadId;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options;
            await using (var ctx = new AppDbContext(options, tenantContext))
            {
                var phase3 = ctx.Database.GetMigrations().Single(m => m.EndsWith("_Phase3Crm"));
                await ctx.GetService<IMigrator>().MigrateAsync(phase3);
                Assert.DoesNotContain("Phase4Discovery", await ctx.Database.GetAppliedMigrationsAsync());
                Assert.Equal(0L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {DiscoveryTables}"));

                using var _ = tenantContext.BeginPlatformScope(); var now = DateTime.UtcNow;
                var permissions = Phase3Permissions.Select((c, i) => Permission.Create(c, c, null, c.Split('.')[0], c is "Tenants.Manage" or "Platform.SwitchTenant", (i + 1) * 10)).ToList();
                ctx.Permissions.AddRange(permissions);
                var tenant = Tenant.Create("Phase3 Co", "phase3-co", "Education", "UTC", "en", now); tenantId = tenant.Id; ctx.Tenants.Add(tenant);
                foreach (var (name, codes) in new[]
                         {
                             ("Sales Manager", Phase3Permissions.Where(c => c.StartsWith("Leads") || c is "Consent.Manage" or "Suppression.Manage" or "Pipelines.Manage" or "Scoring.Manage" or "Campaigns.View" or "Reports.View").ToArray()),
                             ("Sales Agent", new[] { "Leads.View", "Leads.Create", "Leads.Update" }), ("Tenant Administrator", Phase3Permissions.Where(c => c is not ("Tenants.Manage" or "Platform.SwitchTenant")).ToArray()),
                         })
                {
                    var role = LeadPilot.Domain.Tenancy.Role.Create(tenantId, name, null, true, now); ctx.TenantRoles.Add(role);
                    foreach (var p in permissions.Where(p => codes.Contains(p.Code))) ctx.RolePermissions.Add(RolePermission.Create(tenantId, role.Id, p.Id));
                }
                LeadPilot.Infrastructure.Services.Crm.CrmDefaults.AddAll(ctx, tenantId, now);
                await ctx.SaveChangesAsync();
                var pipeline = await ctx.Pipelines.IgnoreQueryFilters().FirstAsync(p => p.TenantId == tenantId); var stage = await ctx.PipelineStages.IgnoreQueryFilters().Where(s => s.PipelineId == pipeline.Id).OrderBy(s => s.Position).FirstAsync(); var status = await ctx.LeadStatuses.IgnoreQueryFilters().FirstAsync(s => s.TenantId == tenantId);
                var lead = Lead.Create(tenantId, new LeadProfile("Legacy School", null, "Pakistan", null, "Lahore", null, null, null, "https://legacy.example", 60), pipeline.Id, stage.Id, status.Id, null, LeadSourceType.Manual, now, null);
                leadId = lead.Id; ctx.Leads.Add(lead);
                ctx.Contacts.Add(Contact.Create(tenantId, lead.Id, "Office", null, "+92 42 111 2222", "+924211112222", null, DataOrigin.Provided, true, now, null));
                await ctx.SaveChangesAsync();
            }

            // ---- 2. start the application on it (migrates, seeds) ----
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", cs); Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "true");
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
            factory.CreateClient().Dispose();
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", oldEnv.Item1); Environment.SetEnvironmentVariable("Database__MigrateOnStartup", oldEnv.Item2);

            // ---- 3. checks ----
            async Task<T> WithDb<T>(Func<AppDbContext, Task<T>> work)
            {
                using var scope = factory!.Services.CreateScope(); using var __ = scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().BeginPlatformScope();
                return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
            }
            Assert.Contains(await WithDb(d => d.Database.GetAppliedMigrationsAsync().ContinueWith(t => t.Result.ToList())), m => m.EndsWith("_Phase4Discovery"));
            Assert.Equal(6L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {DiscoveryTables}"));
            Assert.Equal(1L, await Scalar(cs, $"SELECT COUNT(DISTINCT index_name) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'DiscoveryResults' AND non_unique = 0 AND index_name <> 'PRIMARY' AND index_name LIKE '%ConnectorType%ExternalId%'"));
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM Leads WHERE Name = 'Legacy School'"));       // existing data untouched
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM Contacts WHERE PhoneE164 = '+924211112222'"));
            Assert.Equal(0L, await Scalar(cs, "SELECT COUNT(*) FROM DiscoveryJobs"));

            var codesByRole = await WithDb(async d => (await (from rp in d.RolePermissions.IgnoreQueryFilters() join p in d.Permissions on rp.PermissionId equals p.Id join r in d.TenantRoles.IgnoreQueryFilters() on rp.RoleId equals r.Id
                                                              where r.TenantId == tenantId select new { r.Name, p.Code }).ToListAsync()).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.Select(x => x.Code).ToHashSet()));
            Assert.Equal(PermissionCatalog.TenantCodes.Order().ToList(), codesByRole["Tenant Administrator"].Order().ToList());
            Assert.Superset(NewPermissions.ToHashSet(), codesByRole["Tenant Administrator"].Intersect(NewPermissions).ToHashSet());
            Assert.Equal(["Discovery.Manage", "Discovery.Review", "Discovery.Run", "Discovery.View"], codesByRole["Sales Manager"].Where(c => c.StartsWith("Discovery")).Order().ToArray());
            Assert.Equal(["Discovery.View"], codesByRole["Sales Agent"].Where(c => c.StartsWith("Discovery") || c.StartsWith("Integrations")).ToArray());
            Assert.Contains("Leads.Import", codesByRole["Sales Manager"]);   // old grants stay

            // ---- 4. the upgraded tenant can use discovery end to end (education workspace: starter pack on first use) ----
            using var scope2 = factory.Services.CreateScope();
            await factory.Services.GetRequiredService<ITenantScopeRunner>().RunAsync(tenantId, async (sp, ct) =>
            {
                var catalog = sp.GetRequiredService<LeadPilot.Application.Discovery.IDiscoveryCatalogService>();
                Assert.Contains(await catalog.ListMappingsAsync(ct), m => m.CategoryName == "Private schools");
                var jobs = sp.GetRequiredService<LeadPilot.Application.Discovery.IDiscoveryJobService>();
                Assert.True((await jobs.ListAsync(new LeadPilot.Application.Discovery.DiscoveryJobQuery(), ct)).TotalCount == 0);
            });
            Assert.True(await WithDb(d => d.Leads.IgnoreQueryFilters().AnyAsync(l => l.Id == leadId)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", oldEnv.Item1); Environment.SetEnvironmentVariable("Database__MigrateOnStartup", oldEnv.Item2);
            factory?.Dispose(); MySqlConnection.ClearAllPools(); await Exec(adminConn, $"DROP DATABASE IF EXISTS `{dbName}`");
        }
    }

    [RequiresMySqlFact]
    public async Task The_migration_chain_applies_to_an_empty_database_and_the_model_has_no_pending_changes()
    {
        var adminConn = Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar)!;
        var dbName = "leadpilot_clean4_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        try
        {
            await using var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, new TenantContext());
            await ctx.Database.MigrateAsync();
            Assert.Empty(await ctx.Database.GetPendingMigrationsAsync());
            Assert.Equal(6L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {DiscoveryTables}"));
            Assert.False(ctx.Database.HasPendingModelChanges());
            // foreign keys exist with RESTRICT semantics on the review path
            Assert.True(await Scalar(cs, "SELECT COUNT(*) FROM information_schema.referential_constraints WHERE constraint_schema = DATABASE() AND table_name = 'DiscoveryResults'") >= 4);
        }
        finally { MySqlConnection.ClearAllPools(); await Exec(adminConn, $"DROP DATABASE IF EXISTS `{dbName}`"); }
        _ = app;
    }

    private static async Task Exec(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
    private static async Task<long> Scalar(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
}
