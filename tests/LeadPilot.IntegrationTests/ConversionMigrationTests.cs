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

/// <summary>The Phase 10 migration applies to an empty database and to a Phase 9 database that holds a tenant, roles and a lead.</summary>
[Collection("app")]
public class ConversionMigrationTests(AppFixture app)
{
    private static readonly string[] Codes = ["Demos.View", "Demos.Book", "Demos.Manage", "Trials.View", "Trials.Manage", "Customers.View", "Customers.Manage", "Payments.Request", "Payments.Verify"];
    private const string Tables = "('ConversionSettings','DemoReps','DemoAvailabilityRules','DemoAvailabilityExceptions','Demos','DemoHistory','DemoReminders','Trials','TrialEvents','Customers','Subscriptions','CommercialAgreements','PaymentRequests','Payments','PaymentProviderConfigs','PaymentEvents')";

    [RequiresMySqlFact]
    public async Task A_phase8_database_with_data_upgrades_and_existing_roles_receive_the_workflow_permissions()
    {
        _ = app;
        var adminConn = Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar)!;
        var dbName = "leadpilot_upgrade10_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        var oldEnv = (Environment.GetEnvironmentVariable("ConnectionStrings__Default"), Environment.GetEnvironmentVariable("Database__MigrateOnStartup"));
        WebApplicationFactory<Program>? factory = null;
        try
        {
            var tenantContext = new TenantContext(); Guid tenantId, leadId;
            await using (var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, tenantContext))
            {
                await ctx.GetService<IMigrator>().MigrateAsync(ctx.Database.GetMigrations().Single(m => m.EndsWith("_Phase9Workflows")));
                Assert.Equal(0L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {Tables}"));
                using var _ = tenantContext.BeginPlatformScope(); var now = DateTime.UtcNow;
                var old = PermissionCatalog.All.Where(p => !Codes.Contains(p.Code)).ToList();
                var permissions = old.Select((p, i) => Permission.Create(p.Code, p.Name, null, p.Group, p.IsPlatform, (i + 1) * 10)).ToList();
                ctx.Permissions.AddRange(permissions);
                var tenant = Tenant.Create("Phase9 Co", "phase9-co", "Education", "UTC", "en", now); tenantId = tenant.Id; ctx.Tenants.Add(tenant);
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

            Assert.Equal(16L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {Tables}"));
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM Leads WHERE Name = 'Legacy School'"));
            async Task<List<string>> RoleCodes(string role) => (await ScalarList(cs, $"SELECT p.Code FROM RolePermissions rp JOIN Permissions p ON p.Id = rp.PermissionId JOIN Roles r ON r.Id = rp.RoleId WHERE r.TenantId = '{tenantId}' AND r.Name = '{role}'")).ToList();
            Assert.Equal(PermissionCatalog.TenantCodes.Order().ToList(), (await RoleCodes("Tenant Administrator")).Order().ToList());
            var mgr = await RoleCodes("Sales Manager");
            foreach (var c in new[] { "Demos.View", "Demos.Book", "Demos.Manage", "Trials.View", "Trials.Manage", "Customers.View", "Customers.Manage", "Payments.Request" }) Assert.Contains(c, mgr);
            Assert.DoesNotContain("Payments.Verify", mgr); Assert.Contains("Leads.View", mgr);
            var ro = await RoleCodes("Read-only"); foreach (var c in new[] { "Demos.View", "Trials.View", "Customers.View" }) Assert.Contains(c, ro);
            foreach (var c in new[] { "Demos.Book", "Trials.Manage", "Customers.Manage", "Payments.Request", "Payments.Verify" }) Assert.DoesNotContain(c, ro);

            await factory.Services.GetRequiredService<ITenantScopeRunner>().RunAsync(tenantId, async (sp, ct) =>
            {
                var settings = await sp.GetRequiredService<LeadPilot.Application.Conversion.IConversionSettingsService>().GetAsync(ct); Assert.Equal("UTC", settings.TimeZoneId); Assert.Equal(30, settings.SlotMinutes); Assert.False(settings.RemindersEnabled);
                Assert.Empty((await sp.GetRequiredService<LeadPilot.Application.Conversion.IDemoService>().ListAsync(new LeadPilot.Application.Conversion.DemoQuery(), ct)).Items);
                Assert.Empty((await sp.GetRequiredService<LeadPilot.Application.Conversion.ICustomerService>().ListAsync(new LeadPilot.Application.Conversion.CustomerQuery(), ct)).Items);
                Assert.Equal(leadId, (await sp.GetRequiredService<LeadPilot.Application.Crm.ILeadService>().GetAsync(leadId, ct)).Value.Id);
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
        var dbName = "leadpilot_clean10_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        try
        {
            await using var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, new TenantContext());
            await ctx.Database.MigrateAsync();
            Assert.Empty(await ctx.Database.GetPendingMigrationsAsync()); Assert.False(ctx.Database.HasPendingModelChanges());
            Assert.Equal(16L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {Tables}"));
            async Task<long> Unique(string table, string columns) => await Scalar(cs, $"SELECT COUNT(*) FROM (SELECT index_name FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '{table}' AND non_unique = 0 GROUP BY index_name HAVING GROUP_CONCAT(column_name ORDER BY seq_in_index) = '{columns}') x");
            Assert.Equal(1L, await Unique("Demos", "TenantId,ActiveSlot"));                    // no double booking
            Assert.Equal(1L, await Unique("DemoReps", "TenantId,UserId")); Assert.Equal(1L, await Unique("DemoReminders", "DemoId,Generation,OffsetMinutes"));
            Assert.Equal(1L, await Unique("Payments", "PaymentRequestId")); Assert.Equal(1L, await Unique("Payments", "TenantId,EvidenceKey"));
            Assert.Equal(1L, await Unique("PaymentEvents", "TenantId,ProviderKey,ExternalEventId")); Assert.Equal(1L, await Unique("PaymentRequests", "TenantId,OpenForSubscription")); Assert.Equal(1L, await Unique("PaymentRequests", "TenantId,Reference"));
            Assert.Equal(1L, await Unique("CommercialAgreements", "TenantId,OpportunityId")); Assert.Equal(1L, await Unique("Customers", "TenantId,LeadId")); Assert.Equal(1L, await Unique("Customers", "TenantId,Number"));
            Assert.Equal(1L, await Unique("PaymentProviderConfigs", "WebhookId")); Assert.Equal(1L, await Unique("ConversionSettings", "TenantId"));
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM information_schema.check_constraints WHERE constraint_schema = DATABASE() AND constraint_name = 'CK_Subscriptions_ActiveNeedsPayment'"));
            Assert.Equal("decimal(18,2)", (await ScalarList(cs, "SELECT column_type FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'Payments' AND column_name = 'Amount'")).Single());
            foreach (var table in new[] { "ConversionSettings", "DemoReps", "DemoAvailabilityRules", "DemoAvailabilityExceptions", "Demos", "DemoHistory", "DemoReminders", "Trials", "TrialEvents", "Customers", "Subscriptions", "CommercialAgreements", "PaymentRequests", "Payments", "PaymentProviderConfigs", "PaymentEvents" })
                Assert.True(await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.referential_constraints WHERE constraint_schema = DATABASE() AND table_name = '{table}' AND referenced_table_name = 'Tenants'") >= 1, table + " has no tenant FK");
        }
        finally { MySqlConnection.ClearAllPools(); await Exec(adminConn, $"DROP DATABASE IF EXISTS `{dbName}`"); }
    }

    private static async Task Exec(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
    private static async Task<long> Scalar(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
    private static async Task<List<string>> ScalarList(string conn, string sql) { var l = new List<string>(); await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await using var r = await cmd.ExecuteReaderAsync(); while (await r.ReadAsync()) l.Add(r.GetString(0)); return l; }
}
