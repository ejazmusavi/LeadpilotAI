using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Security;
using LeadPilot.Application.Workflows;
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

/// <summary>The Phase 9 migration applies to an empty database and to a Phase 8 database that holds a tenant, roles and a lead.</summary>
[Collection("app")]
public class WorkflowMigrationTests(AppFixture app)
{
    private static readonly string[] Codes = ["Workflows.View", "Workflows.Create", "Workflows.Update", "Workflows.Activate", "Workflows.Execute", "Workflows.Delete"];
    private const string Tables = "('Workflows','WorkflowVersions','WorkflowTriggers','WorkflowSteps','WorkflowExecutions','WorkflowExecutionLogs','WorkflowTenantSettings','WorkflowNotifications')";

    [RequiresMySqlFact]
    public async Task A_phase8_database_with_data_upgrades_and_existing_roles_receive_the_workflow_permissions()
    {
        _ = app;
        var adminConn = Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar)!;
        var dbName = "leadpilot_upgrade9_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        var oldEnv = (Environment.GetEnvironmentVariable("ConnectionStrings__Default"), Environment.GetEnvironmentVariable("Database__MigrateOnStartup"));
        WebApplicationFactory<Program>? factory = null;
        try
        {
            var tenantContext = new TenantContext(); Guid tenantId, leadId;
            await using (var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, tenantContext))
            {
                await ctx.GetService<IMigrator>().MigrateAsync(ctx.Database.GetMigrations().Single(m => m.EndsWith("_Phase8SalesAgent")));
                Assert.Equal(0L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {Tables}"));
                using var _ = tenantContext.BeginPlatformScope(); var now = DateTime.UtcNow;
                var old = PermissionCatalog.All.Where(p => !Codes.Contains(p.Code)).ToList();
                var permissions = old.Select((p, i) => Permission.Create(p.Code, p.Name, null, p.Group, p.IsPlatform, (i + 1) * 10)).ToList();
                ctx.Permissions.AddRange(permissions);
                var tenant = Tenant.Create("Phase8 Co", "phase8-co", "Education", "UTC", "en", now); tenantId = tenant.Id; ctx.Tenants.Add(tenant);
                foreach (var (name, codes) in new[] { ("Sales Manager", new[] { "Leads.View", "Conversations.View" }), ("Read-only", new[] { "Leads.View" }), ("Tenant Administrator", old.Where(p => !p.IsPlatform).Select(p => p.Code).ToArray()) })
                {
                    var role = Role.Create(tenantId, name, null, true, now); ctx.TenantRoles.Add(role);
                    foreach (var p in permissions.Where(p => codes.Contains(p.Code))) ctx.RolePermissions.Add(RolePermission.Create(tenantId, role.Id, p.Id));
                }
                LeadPilot.Infrastructure.Services.Crm.CrmDefaults.AddAll(ctx, tenantId, now);
                await ctx.SaveChangesAsync();
                var pipeline = await ctx.Pipelines.IgnoreQueryFilters().FirstAsync(p => p.TenantId == tenantId); var stage = await ctx.PipelineStages.IgnoreQueryFilters().Where(s => s.PipelineId == pipeline.Id).OrderBy(s => s.Position).FirstAsync();
                var status = await ctx.LeadStatuses.IgnoreQueryFilters().FirstAsync(s => s.TenantId == tenantId && s.IsDefault);
                var lead = Lead.Create(tenantId, new LeadProfile("Legacy School", null, "Pakistan", null, "Lahore", null, null, null, null, 60), pipeline.Id, stage.Id, status.Id, null, LeadSourceType.Manual, now, null);
                leadId = lead.Id; ctx.Leads.Add(lead); await ctx.SaveChangesAsync();
            }
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", cs); Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "true");
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
            factory.CreateClient().Dispose();
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", oldEnv.Item1); Environment.SetEnvironmentVariable("Database__MigrateOnStartup", oldEnv.Item2);

            Assert.Equal(8L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {Tables}"));
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM Leads WHERE Name = 'Legacy School'"));
            async Task<List<string>> RoleCodes(string role) => (await ScalarList(cs, $"SELECT p.Code FROM RolePermissions rp JOIN Permissions p ON p.Id = rp.PermissionId JOIN Roles r ON r.Id = rp.RoleId WHERE r.TenantId = '{tenantId}' AND r.Name = '{role}'")).ToList();
            Assert.Equal(PermissionCatalog.TenantCodes.Order().ToList(), (await RoleCodes("Tenant Administrator")).Order().ToList());
            var mgr = await RoleCodes("Sales Manager");
            foreach (var c in new[] { "Workflows.View", "Workflows.Create", "Workflows.Update", "Workflows.Activate", "Workflows.Execute" }) Assert.Contains(c, mgr);
            Assert.DoesNotContain("Workflows.Delete", mgr); Assert.Contains("Leads.View", mgr);
            var ro = await RoleCodes("Read-only"); Assert.Contains("Workflows.View", ro); Assert.DoesNotContain("Workflows.Create", ro); Assert.DoesNotContain("Workflows.Execute", ro);

            await factory.Services.GetRequiredService<ITenantScopeRunner>().RunAsync(tenantId, async (sp, ct) =>
            {
                var s = await sp.GetRequiredService<IWorkflowSettingsService>().GetAsync(ct); Assert.False(s.KillSwitch); Assert.Equal(20, s.MaxConcurrentExecutions);
                var list = await sp.GetRequiredService<IWorkflowService>().ListAsync(new WorkflowQuery(), ct); Assert.Empty(list.Items);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", oldEnv.Item1); Environment.SetEnvironmentVariable("Database__MigrateOnStartup", oldEnv.Item2);
            factory?.Dispose(); MySqlConnection.ClearAllPools(); await Exec(adminConn, $"DROP DATABASE IF EXISTS `{dbName}`");
        }
    }

    [RequiresMySqlFact]
    public async Task The_migration_chain_applies_to_an_empty_database_with_the_documented_constraints_and_no_pending_model_changes()
    {
        var adminConn = Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar)!;
        var dbName = "leadpilot_clean9_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        try
        {
            await using var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, new TenantContext());
            await ctx.Database.MigrateAsync();
            Assert.Empty(await ctx.Database.GetPendingMigrationsAsync()); Assert.False(ctx.Database.HasPendingModelChanges());
            Assert.Equal(8L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {Tables}"));
            async Task<long> Unique(string table, string columns) => await Scalar(cs, $"SELECT COUNT(*) FROM (SELECT index_name FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '{table}' AND non_unique = 0 GROUP BY index_name HAVING GROUP_CONCAT(column_name ORDER BY seq_in_index) = '{columns}') x");
            Assert.Equal(1L, await Unique("WorkflowExecutions", "WorkflowVersionId,TriggerEventId,EntityId"));     // idempotency
            Assert.Equal(1L, await Unique("WorkflowExecutions", "WorkflowId,EntityId,ActiveSlot"));                 // exclusivity
            Assert.Equal(1L, await Unique("WorkflowVersions", "TenantId,WorkflowId,Number")); Assert.Equal(1L, await Unique("WorkflowSteps", "WorkflowVersionId,Key"));
            Assert.Equal(1L, await Unique("WorkflowTenantSettings", "TenantId")); Assert.Equal(1L, await Unique("WorkflowNotifications", "WorkflowExecutionId,StepKey,UserId"));
            foreach (var table in new[] { "Workflows", "WorkflowVersions", "WorkflowTriggers", "WorkflowSteps", "WorkflowExecutions", "WorkflowExecutionLogs", "WorkflowTenantSettings", "WorkflowNotifications" })
                Assert.True(await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.referential_constraints WHERE constraint_schema = DATABASE() AND table_name = '{table}' AND referenced_table_name = 'Tenants'") >= 1, table + " has no tenant FK");
        }
        finally { MySqlConnection.ClearAllPools(); await Exec(adminConn, $"DROP DATABASE IF EXISTS `{dbName}`"); }
    }

    private static async Task Exec(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
    private static async Task<long> Scalar(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
    private static async Task<List<string>> ScalarList(string conn, string sql) { var l = new List<string>(); await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await using var r = await cmd.ExecuteReaderAsync(); while (await r.ReadAsync()) l.Add(r.GetString(0)); return l; }
}
