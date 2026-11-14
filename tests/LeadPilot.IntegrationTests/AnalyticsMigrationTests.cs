using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Analytics;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Analytics;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace LeadPilot.IntegrationTests;

/// <summary>The Phase 11 migration applies to an empty database and to a Phase 10 database that holds a tenant, roles and a lead.</summary>
[Collection("app")]
public class AnalyticsMigrationTests(AppFixture app)
{
    private static readonly string[] Codes = ["Reports.Export", "Reports.Team", "Dashboard.View"];
    private static readonly string[] Indexes = ["IX_Messages_Tenant_Dir_Sent", "IX_Messages_Tenant_Dir_Occurred", "IX_Messages_Tenant_Failed", "IX_Demos_Tenant_Created", "IX_Demos_Tenant_Completed",
        "IX_Trials_Tenant_Created", "IX_Customers_Tenant_Converted", "IX_Payments_Tenant_Paid", "IX_Opportunities_Tenant_Status_Updated", "IX_CampaignRecipients_Tenant_Sent"];

    [RequiresMySqlFact]
    public async Task A_phase10_database_with_data_upgrades_existing_roles_receive_the_analytics_permissions_and_the_rollup_works()
    {
        _ = app;
        var adminConn = Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar)!;
        var dbName = "leadpilot_upgrade11_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        var oldEnv = (Environment.GetEnvironmentVariable("ConnectionStrings__Default"), Environment.GetEnvironmentVariable("Database__MigrateOnStartup"));
        WebApplicationFactory<Program>? factory = null;
        try
        {
            var tenantContext = new TenantContext(); Guid tenantId;
            await using (var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, tenantContext))
            {
                await ctx.GetService<IMigrator>().MigrateAsync(ctx.Database.GetMigrations().Single(m => m.EndsWith("_Phase10Conversion")));
                Assert.Equal(0L, await Scalar(cs, "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN ('DailyMetrics','AnalyticsRefreshStates')"));
                using var _ = tenantContext.BeginPlatformScope(); var now = DateTime.UtcNow;
                var old = PermissionCatalog.All.Where(p => !Codes.Contains(p.Code)).ToList();
                var permissions = old.Select((p, i) => Permission.Create(p.Code, p.Name, null, p.Group, p.IsPlatform, (i + 1) * 10)).ToList();
                ctx.Permissions.AddRange(permissions);
                var tenant = Tenant.Create("Phase10 Co", "phase10-co", "Education", "UTC", "en", now); tenantId = tenant.Id; ctx.Tenants.Add(tenant);
                foreach (var (name, codes) in new[] { ("Sales Manager", new[] { "Leads.View", "Reports.View" }), ("Sales Agent", new[] { "Leads.View", "Reports.View" }), ("Read-only", new[] { "Leads.View", "Reports.View" }), ("Tenant Administrator", old.Where(p => !p.IsPlatform).Select(p => p.Code).ToArray()) })
                {
                    var role = Role.Create(tenantId, name, null, true, now); ctx.TenantRoles.Add(role);
                    foreach (var p in permissions.Where(p => codes.Contains(p.Code))) ctx.RolePermissions.Add(RolePermission.Create(tenantId, role.Id, p.Id));
                }
                LeadPilot.Infrastructure.Services.Crm.CrmDefaults.AddAll(ctx, tenantId, now);
                await ctx.SaveChangesAsync();
                var pipeline = await ctx.Pipelines.IgnoreQueryFilters().FirstAsync(p => p.TenantId == tenantId); var stage = await ctx.PipelineStages.IgnoreQueryFilters().Where(s => s.PipelineId == pipeline.Id).OrderBy(s => s.Position).FirstAsync();
                var status = await ctx.LeadStatuses.IgnoreQueryFilters().FirstAsync(s => s.TenantId == tenantId && s.IsDefault);
                ctx.Leads.Add(Lead.Create(tenantId, new LeadProfile("Legacy School", null, "Pakistan", null, "Lahore", null, null, null, null, 60), pipeline.Id, stage.Id, status.Id, null, LeadSourceType.Manual, now.AddDays(-3), null));
                await ctx.SaveChangesAsync();
            }
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", cs); Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "true");
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
            factory.CreateClient().Dispose();
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", oldEnv.Item1); Environment.SetEnvironmentVariable("Database__MigrateOnStartup", oldEnv.Item2);

            Assert.Equal(2L, await Scalar(cs, "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN ('DailyMetrics','AnalyticsRefreshStates')"));
            foreach (var ix in Indexes) Assert.True(await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND index_name = '{ix}'") >= 1, "missing index " + ix);
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM Leads WHERE Name = 'Legacy School'"));
            async Task<List<string>> RoleCodes(string role) => (await ScalarList(cs, $"SELECT p.Code FROM RolePermissions rp JOIN Permissions p ON p.Id = rp.PermissionId JOIN Roles r ON r.Id = rp.RoleId WHERE r.TenantId = '{tenantId}' AND r.Name = '{role}'")).ToList();
            Assert.Equal(PermissionCatalog.TenantCodes.Order().ToList(), (await RoleCodes("Tenant Administrator")).Order().ToList());
            var mgr = await RoleCodes("Sales Manager"); foreach (var c in Codes) Assert.Contains(c, mgr);
            var agent = await RoleCodes("Sales Agent"); Assert.Contains("Dashboard.View", agent); Assert.DoesNotContain("Reports.Team", agent); Assert.DoesNotContain("Reports.Export", agent);
            var ro = await RoleCodes("Read-only"); Assert.Contains("Dashboard.View", ro); Assert.Contains("Reports.Team", ro); Assert.DoesNotContain("Reports.Export", ro);

            await factory.Services.GetRequiredService<ITenantScopeRunner>().RunAsync(tenantId, async (sp, ct) =>
            {
                var r = await sp.GetRequiredService<IAnalyticsRollupService>().RefreshAsync(true, ct); Assert.True(r.Full);
                var db = sp.GetRequiredService<AppDbContext>(); var q = new AnalyticsQueries(db, tenantId, null);
                Assert.Equal(1, await q.TotalLeadsAsync(ct));
                Assert.Equal(1L, await db.DailyMetrics.Where(m => m.MetricKey == StreamKeys.LeadsCreated).SumAsync(m => m.Count, ct));
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
        var dbName = "leadpilot_clean11_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        try
        {
            await using var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, new TenantContext());
            await ctx.Database.MigrateAsync();
            Assert.Empty(await ctx.Database.GetPendingMigrationsAsync()); Assert.False(ctx.Database.HasPendingModelChanges());
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM (SELECT index_name FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'DailyMetrics' AND non_unique = 0 GROUP BY index_name HAVING GROUP_CONCAT(column_name ORDER BY seq_in_index) = 'TenantId,MetricKey,Day,Dimension') x"));
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM (SELECT index_name FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'AnalyticsRefreshStates' AND non_unique = 0 GROUP BY index_name HAVING GROUP_CONCAT(column_name ORDER BY seq_in_index) = 'TenantId') x"));
            Assert.Equal("date", (await ScalarList(cs, "SELECT column_type FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'DailyMetrics' AND column_name = 'Day'")).Single());
            Assert.Equal("decimal(18,2)", (await ScalarList(cs, "SELECT column_type FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'DailyMetrics' AND column_name = 'Amount'")).Single());
            foreach (var table in new[] { "DailyMetrics", "AnalyticsRefreshStates" })
                Assert.True(await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.referential_constraints WHERE constraint_schema = DATABASE() AND table_name = '{table}' AND referenced_table_name = 'Tenants'") >= 1, table + " has no tenant FK");
        }
        finally { MySqlConnection.ClearAllPools(); await Exec(adminConn, $"DROP DATABASE IF EXISTS `{dbName}`"); }
    }

    private static async Task Exec(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
    private static async Task<long> Scalar(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
    private static async Task<List<string>> ScalarList(string conn, string sql) { var l = new List<string>(); await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await using var r = await cmd.ExecuteReaderAsync(); while (await r.ReadAsync()) l.Add(r.GetValue(0)?.ToString() ?? ""); return l; }
}
