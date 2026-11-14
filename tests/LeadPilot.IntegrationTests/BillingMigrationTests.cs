using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Billing;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Billing;
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

/// <summary>The Phase 12 migration applies to an empty database and to a Phase 11 database that holds tenants, roles and data: existing tenants land on the default plan and keep full access.</summary>
[Collection("app")]
public class BillingMigrationTests(AppFixture app)
{
    private static readonly string[] NewTables = ["Plans", "PlanLimits", "PlanFeatures", "PlatformSettings", "InvoiceSequences", "RegistrationAttempts", "TenantBillingStates", "TenantSubscriptions", "SubscriptionEvents",
        "TenantEntitlementOverrides", "UsageNotices", "UsageReservations", "Invoices", "InvoiceLines", "BillingPayments", "PaymentReports", "PlanChangeRequests"];
    private static readonly string[] BillingCodes = [PermissionCodes.BillingView, PermissionCodes.BillingManage, PermissionCodes.PlatformPlansManage, PermissionCodes.PlatformTenantsManage, PermissionCodes.PlatformBillingVerify, PermissionCodes.PlatformSettingsManage, PermissionCodes.PlatformAuditView];
    private static string In(IEnumerable<string> l) => "(" + string.Join(",", l.Select(x => $"'{x}'")) + ")";

    [RequiresMySqlFact]
    public async Task A_phase11_database_with_data_upgrades_and_existing_tenants_are_backfilled_onto_the_default_plan_without_losing_access()
    {
        _ = app;
        var adminConn = Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar)!;
        var dbName = "leadpilot_upgrade12_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        var oldEnv = (Environment.GetEnvironmentVariable("ConnectionStrings__Default"), Environment.GetEnvironmentVariable("Database__MigrateOnStartup"));
        WebApplicationFactory<Program>? factory = null;
        try
        {
            var tenantContext = new TenantContext(); Guid active, suspended;
            await using (var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, tenantContext))
            {
                await ctx.GetService<IMigrator>().MigrateAsync(ctx.Database.GetMigrations().Single(m => m.EndsWith("_Phase11Analytics")));
                Assert.Equal(0L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {In(NewTables)}"));
                using var _ = tenantContext.BeginPlatformScope(); var now = DateTime.UtcNow;
                var old = PermissionCatalog.All.Where(p => !BillingCodes.Contains(p.Code)).ToList();
                var permissions = old.Select((p, i) => Permission.Create(p.Code, p.Name, null, p.Group, p.IsPlatform, (i + 1) * 10)).ToList();
                ctx.Permissions.AddRange(permissions);
                Guid Seed(string name, string slug)
                {
                    var t = Tenant.Create(name, slug, "Education", "UTC", "en", now); ctx.Tenants.Add(t);
                    foreach (var (role, codes) in new[] { ("Sales Manager", new[] { "Leads.View", "Reports.View" }), ("Tenant Administrator", old.Where(p => !p.IsPlatform).Select(p => p.Code).ToArray()) })
                    {
                        var r = Role.Create(t.Id, role, null, true, now); ctx.TenantRoles.Add(r);
                        foreach (var p in permissions.Where(p => codes.Contains(p.Code))) ctx.RolePermissions.Add(RolePermission.Create(t.Id, r.Id, p.Id));
                    }
                    LeadPilot.Infrastructure.Services.Crm.CrmDefaults.AddAll(ctx, t.Id, now);
                    return t.Id;
                }
                active = Seed("Phase11 Active", "phase11-active"); suspended = Seed("Phase11 Suspended", "phase11-suspended");
                await ctx.SaveChangesAsync();
                var pipeline = await ctx.Pipelines.IgnoreQueryFilters().FirstAsync(p => p.TenantId == active); var stage = await ctx.PipelineStages.IgnoreQueryFilters().Where(s => s.PipelineId == pipeline.Id).OrderBy(s => s.Position).FirstAsync();
                var status = await ctx.LeadStatuses.IgnoreQueryFilters().FirstAsync(s => s.TenantId == active && s.IsDefault);
                ctx.Leads.Add(Lead.Create(active, new LeadProfile("Legacy School", null, "Pakistan", null, "Lahore", null, null, null, null, 60), pipeline.Id, stage.Id, status.Id, null, LeadSourceType.Manual, now.AddDays(-3), null));
                (await ctx.Tenants.FirstAsync(t => t.Id == suspended)).Suspend("old suspension", now);
                await ctx.SaveChangesAsync();
            }
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", cs); Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "true");
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
            factory.CreateClient().Dispose();
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", oldEnv.Item1); Environment.SetEnvironmentVariable("Database__MigrateOnStartup", oldEnv.Item2);

            Assert.Equal(NewTables.Length, (int)await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {In(NewTables)}"));
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM Leads WHERE Name = 'Legacy School'"));
            Assert.Equal((1L, 0L), (await Scalar(cs, "SELECT COUNT(*) FROM InvoiceSequences WHERE Id = 1"), await Scalar(cs, "SELECT LastNumber FROM InvoiceSequences WHERE Id = 1")));
            // sample plans exist, are labelled, and carry zero prices: nothing commercial was invented
            Assert.True(await Scalar(cs, "SELECT COUNT(*) FROM Plans WHERE IsSample = 1") >= 4);
            Assert.Equal(0L, await Scalar(cs, "SELECT COUNT(*) FROM Plans WHERE Price <> 0"));
            // both existing tenants (the suspended one too) were placed on the default plan, active, with no limits, and the platform tenant has none
            var defaultPlan = (await ScalarList(cs, "SELECT Value FROM PlatformSettings WHERE `Key` = 'billing.default_plan_id'")).Single();
            Assert.Equal(2L, await Scalar(cs, $"SELECT COUNT(*) FROM TenantSubscriptions WHERE PlanId = '{defaultPlan}' AND State = {(int)SubscriptionState.Active} AND TenantId IN ('{active}','{suspended}')"));
            Assert.Equal(0L, await Scalar(cs, "SELECT COUNT(*) FROM TenantSubscriptions s JOIN Tenants t ON t.Id = s.TenantId WHERE t.IsPlatform = 1"));
            Assert.Equal(0L, await Scalar(cs, $"SELECT COUNT(*) FROM PlanLimits WHERE PlanId = '{defaultPlan}' AND `Value` IS NOT NULL"));
            // roles: the Tenant Administrator gets the tenant billing permissions, the Sales Manager does not; the platform codes never go to a tenant role
            async Task<List<string>> RoleCodes(Guid tenantId, string role) => (await ScalarList(cs, $"SELECT p.Code FROM RolePermissions rp JOIN Permissions p ON p.Id = rp.PermissionId JOIN Roles r ON r.Id = rp.RoleId WHERE r.TenantId = '{tenantId}' AND r.Name = '{role}'")).ToList();
            var admin = await RoleCodes(active, "Tenant Administrator");
            Assert.Contains(PermissionCodes.BillingView, admin); Assert.Contains(PermissionCodes.BillingManage, admin); Assert.DoesNotContain(admin, c => PermissionCatalog.IsPlatform(c));
            Assert.DoesNotContain(PermissionCodes.BillingView, await RoleCodes(active, "Sales Manager"));
            Assert.Equal(PermissionCatalog.TenantCodes.Order().ToList(), admin.Order().ToList());
            Assert.True(await Scalar(cs, "SELECT COUNT(*) FROM Roles r JOIN RolePermissions rp ON rp.RoleId = r.Id JOIN Permissions p ON p.Id = rp.PermissionId WHERE r.Name = 'Platform Owner' AND p.Code = 'Platform.Billing.Verify'") >= 1);
            // no access lost: the active tenant works under the entitlement service with every limit unlimited, and its data is intact
            await factory.Services.GetRequiredService<ITenantScopeRunner>().RunAsync(active, async (sp, ct) =>
            {
                var ent = sp.GetRequiredService<IEntitlementService>();
                var ov = await ent.OverviewAsync(ct);
                Assert.Equal(SubscriptionState.Active, ov.Subscription!.State); Assert.All(ov.Usage, u => Assert.Null(u.Limit)); Assert.All(ov.Features, f => Assert.True(f.Enabled));
                Assert.Equal(1, ov.Usage.Single(u => u.Dimension == UsageDimension.Leads).Used);
                Assert.True((await ent.RunWithinLimitAsync(UsageDimension.Leads, 1, _ => Task.FromResult(LeadPilot.Shared.Results.Result.Success(true)), ct)).IsSuccess);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", oldEnv.Item1); Environment.SetEnvironmentVariable("Database__MigrateOnStartup", oldEnv.Item2);
            factory?.Dispose(); MySqlConnection.ClearAllPools(); await Exec(adminConn, $"DROP DATABASE IF EXISTS `{dbName}`");
        }
    }

    [RequiresMySqlFact]
    public async Task The_migration_chain_applies_to_an_empty_database_with_the_documented_money_columns_constraints_and_no_pending_model_changes()
    {
        _ = app;
        var adminConn = Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar)!;
        var dbName = "leadpilot_clean12_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        try
        {
            await using var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, new TenantContext());
            await ctx.Database.MigrateAsync();
            Assert.Empty(await ctx.Database.GetPendingMigrationsAsync()); Assert.False(ctx.Database.HasPendingModelChanges());
            foreach (var (table, column) in new[] { ("Plans", "Price"), ("Invoices", "Subtotal"), ("Invoices", "TaxAmount"), ("Invoices", "Total"), ("InvoiceLines", "UnitPrice"), ("InvoiceLines", "Amount"), ("BillingPayments", "Amount"), ("PlanChangeRequests", "CreditAmount") })
                Assert.Equal("decimal(18,2)", (await ScalarList(cs, $"SELECT column_type FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = '{table}' AND column_name = '{column}'")).Single());
            foreach (var (table, cols) in new[] { ("Invoices", "Number"), ("BillingPayments", "EvidenceKey"), ("BillingPayments", "InvoiceId"), ("TenantSubscriptions", "TenantId"), ("Plans", "Code"), ("Invoices", "TenantId,IdempotencyKey"), ("UsageNotices", "TenantId,Dimension,PeriodStartUtc,Threshold") })
                Assert.Equal(1L, await Scalar(cs, $"SELECT COUNT(*) FROM (SELECT index_name FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '{table}' AND non_unique = 0 GROUP BY index_name HAVING GROUP_CONCAT(column_name ORDER BY seq_in_index) = '{cols}') x"));
            foreach (var table in new[] { "TenantSubscriptions", "SubscriptionEvents", "TenantEntitlementOverrides", "UsageNotices", "UsageReservations", "Invoices", "InvoiceLines", "BillingPayments", "PaymentReports", "PlanChangeRequests" })
                Assert.True(await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.referential_constraints WHERE constraint_schema = DATABASE() AND table_name = '{table}' AND referenced_table_name = 'Tenants'") >= 1, table + " has no tenant FK");
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM InvoiceSequences"));
        }
        finally { MySqlConnection.ClearAllPools(); await Exec(adminConn, $"DROP DATABASE IF EXISTS `{dbName}`"); }
    }

    private static async Task Exec(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
    private static async Task<long> Scalar(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
    private static async Task<List<string>> ScalarList(string conn, string sql) { var l = new List<string>(); await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await using var r = await cmd.ExecuteReaderAsync(); while (await r.ReadAsync()) l.Add(r.GetValue(0)?.ToString() ?? ""); return l; }
}
