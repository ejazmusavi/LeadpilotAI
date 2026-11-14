using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Config;
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

/// <summary>The Phase 13 migration applies to an empty database and to a Phase 12 database with tenants and data: nothing changes for them except that workspaces already using the former School ERP sample are recorded as having applied the education pack.</summary>
[Collection("app")]
public class ConfigMigrationTests(AppFixture app)
{
    private static readonly string[] NewTables = ["CustomFieldDefinitions", "CustomFieldValues", "PackDefinitions", "TenantPackApplications", "PackItemLinks"];
    private static readonly string[] NewCodes = [PermissionCodes.ConfigPacksImport, PermissionCodes.ConfigPacksExport, PermissionCodes.ConfigPacksManage, PermissionCodes.CustomFieldsManage, PermissionCodes.PlatformPacksManage];
    private static string In(IEnumerable<string> l) => "(" + string.Join(",", l.Select(x => $"'{x}'")) + ")";

    [RequiresMySqlFact]
    public async Task A_phase12_database_with_data_upgrades_existing_education_workspaces_are_recorded_and_nothing_else_changes()
    {
        _ = app;
        var adminConn = Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar)!;
        var dbName = "leadpilot_upgrade13_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        var oldEnv = (Environment.GetEnvironmentVariable("ConnectionStrings__Default"), Environment.GetEnvironmentVariable("Database__MigrateOnStartup"));
        WebApplicationFactory<Program>? factory = null;
        try
        {
            var tenantContext = new TenantContext(); Guid sampleTenant, mappingTenant, plainTenant;
            await using (var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, tenantContext))
            {
                await ctx.GetService<IMigrator>().MigrateAsync(ctx.Database.GetMigrations().Single(m => m.EndsWith("_Phase12Billing")));
                Assert.Equal(0L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {In(NewTables)}"));
                using var _ = tenantContext.BeginPlatformScope(); var now = DateTime.UtcNow;
                var old = PermissionCatalog.All.Where(p => !NewCodes.Contains(p.Code)).ToList();
                var permissions = old.Select((p, i) => Permission.Create(p.Code, p.Name, null, p.Group, p.IsPlatform, (i + 1) * 10)).ToList();
                ctx.Permissions.AddRange(permissions);
                Guid Seed(string name, string slug)
                {
                    var t = Tenant.Create(name, slug, "Education", "UTC", "en", now); ctx.Tenants.Add(t);
                    foreach (var (role, codes) in new[] { ("Sales Manager", new[] { "Leads.View", "Pipelines.Manage", "Scoring.Manage" }), ("Tenant Administrator", old.Where(p => !p.IsPlatform).Select(p => p.Code).ToArray()), ("Read-only", new[] { "Leads.View" }) })
                    {
                        var r = Role.Create(t.Id, role, null, true, now); ctx.TenantRoles.Add(r);
                        foreach (var p in permissions.Where(p => codes.Contains(p.Code))) ctx.RolePermissions.Add(RolePermission.Create(t.Id, r.Id, p.Id));
                    }
                    LeadPilot.Infrastructure.Services.Crm.CrmDefaults.AddAll(ctx, t.Id, now);
                    return t.Id;
                }
                sampleTenant = Seed("Phase12 Sample", "phase12-sample"); mappingTenant = Seed("Phase12 Mapping", "phase12-mapping"); plainTenant = Seed("Phase12 Plain", "phase12-plain");
                await ctx.SaveChangesAsync();
            }
            // rows of tables whose model gained columns are written with SQL against the Phase 12 schema
            var categoryId = Guid.NewGuid(); var productId = Guid.NewGuid();
            await InsertAsync(cs, "SalesAgents", new() { ["Id"] = Guid.NewGuid(), ["TenantId"] = sampleTenant, ["Name"] = "School ERP sales assistant (SAMPLE)", ["Instructions"] = "legacy instructions", ["EnabledToolsJson"] = "[]", ["OffTopicReply"] = "Thanks.", ["Version"] = 1, ["IsActive"] = 1, ["IsDefault"] = 1, ["IsSample"] = 1 });
            await InsertAsync(cs, "CatalogProducts", new() { ["Id"] = productId, ["TenantId"] = sampleTenant, ["Name"] = "School ERP (SAMPLE - not confirmed)", ["IsActive"] = 1, ["IsConfirmed"] = 0, ["IsSample"] = 1 });
            await InsertAsync(cs, "LeadCategories", new() { ["Id"] = categoryId, ["TenantId"] = mappingTenant, ["Name"] = "Private schools", ["NormalizedName"] = "private schools", ["IsActive"] = 1 });
            await InsertAsync(cs, "DiscoveryCategoryMappings", new() { ["Id"] = Guid.NewGuid(), ["TenantId"] = mappingTenant, ["CategoryId"] = categoryId, ["SearchTerms"] = "private school", ["IsActive"] = 1 });
            await InsertAsync(cs, "LeadCategories", new() { ["Id"] = Guid.NewGuid(), ["TenantId"] = plainTenant, ["Name"] = "Clinics", ["NormalizedName"] = "clinics", ["IsActive"] = 1 });
            await InsertAsync(cs, "CatalogProducts", new() { ["Id"] = Guid.NewGuid(), ["TenantId"] = plainTenant, ["Name"] = "Widget", ["IsActive"] = 1, ["IsConfirmed"] = 1, ["IsSample"] = 0 });
            var customCountBefore = await Scalar(cs, "SELECT COUNT(*) FROM ScoringRules");

            Environment.SetEnvironmentVariable("ConnectionStrings__Default", cs); Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "true");
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
            factory.CreateClient().Dispose();
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", oldEnv.Item1); Environment.SetEnvironmentVariable("Database__MigrateOnStartup", oldEnv.Item2);

            Assert.Equal(NewTables.Length, (int)await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {In(NewTables)}"));
            // the recorded pack applications: only the workspaces that used the former School ERP sample / education discovery starter
            Assert.Equal(1L, await Scalar(cs, $"SELECT COUNT(*) FROM TenantPackApplications WHERE TenantId = '{sampleTenant}' AND PackKey = 'education' AND Mode = 'Migrated'"));
            Assert.Equal(1L, await Scalar(cs, $"SELECT COUNT(*) FROM TenantPackApplications WHERE TenantId = '{mappingTenant}' AND PackKey = 'education' AND Mode = 'Migrated'"));
            Assert.Equal(0L, await Scalar(cs, $"SELECT COUNT(*) FROM TenantPackApplications WHERE TenantId = '{plainTenant}'"));
            Assert.Equal(2L, await Scalar(cs, "SELECT COUNT(*) FROM TenantPackApplications"));
            Assert.Equal(0L, await Scalar(cs, "SELECT COUNT(*) FROM PackItemLinks"));                                        // no links: a later import adopts what exists by name
            // existing configuration is untouched: categories stay top-level, products stay products, the sample agent is as it was
            Assert.Equal(0L, await Scalar(cs, "SELECT COUNT(*) FROM LeadCategories WHERE ParentId IS NOT NULL"));
            Assert.Equal(2L, await Scalar(cs, "SELECT COUNT(*) FROM CatalogProducts WHERE Kind = 'Product'"));
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM SalesAgents WHERE Name = 'School ERP sales assistant (SAMPLE)' AND Instructions = 'legacy instructions' AND AutoReplyEnabled = 0 AND IsSample = 1"));
            Assert.Equal(customCountBefore, await Scalar(cs, "SELECT COUNT(*) FROM ScoringRules"));
            Assert.Equal(0L, await Scalar(cs, "SELECT COUNT(*) FROM CustomFieldDefinitions"));
            // the builtin packs were loaded at start-up
            Assert.True(await Scalar(cs, "SELECT COUNT(*) FROM PackDefinitions WHERE Scope = 'Builtin' AND IsPublished = 1") >= 3);
            // roles: the Tenant Administrator receives the new tenant permissions, the Sales Manager only custom-field management (like pipelines and scoring), read-only none; platform codes never go to tenant roles
            async Task<List<string>> RoleCodes(Guid tenantId, string role) => (await ScalarList(cs, $"SELECT p.Code FROM RolePermissions rp JOIN Permissions p ON p.Id = rp.PermissionId JOIN Roles r ON r.Id = rp.RoleId WHERE r.TenantId = '{tenantId}' AND r.Name = '{role}'")).ToList();
            var admin = await RoleCodes(sampleTenant, "Tenant Administrator");
            foreach (var c in new[] { PermissionCodes.ConfigPacksImport, PermissionCodes.ConfigPacksExport, PermissionCodes.ConfigPacksManage, PermissionCodes.CustomFieldsManage }) Assert.Contains(c, admin);
            Assert.DoesNotContain(PermissionCodes.PlatformPacksManage, admin);
            var sm = await RoleCodes(sampleTenant, "Sales Manager");
            Assert.Contains(PermissionCodes.CustomFieldsManage, sm); Assert.DoesNotContain(PermissionCodes.ConfigPacksImport, sm); Assert.Contains("Leads.View", sm);
            Assert.DoesNotContain(await RoleCodes(sampleTenant, "Read-only"), c => NewCodes.Contains(c));
            Assert.Equal(PermissionCatalog.TenantCodes.Order().ToList(), admin.Order().ToList());

            // the workspace that used the sample can import the pack: its sample agent is adopted, never duplicated, and its own wording is never overwritten
            await factory.Services.GetRequiresScopeRunner().RunAsync(sampleTenant, async (sp, ct) =>
            {
                var packs = sp.GetRequiredService<IPackService>();
                var education = (await packs.ListAsync(ct)).Single(p => p.Key == "education");
                Assert.Equal(1, education.AppliedVersion);
                var plan = (await packs.PreviewAsync(education.Id, new ApplyPackOptions(), ct)).Value;
                var agent = plan.Items.Single(i => i.Type == "agent");
                Assert.Equal(PackOutcome.KeepYours, agent.Outcome);                                                            // "legacy instructions" differ from the pack: kept as the workspace's own
                Assert.Equal(PackOutcome.KeepYours, plan.Items.Single(i => i.Type == "product" && i.Key == "school-erp").Outcome);   // the legacy product has none of the pack's text: kept as the workspace's own
                Assert.Equal(1L, await sp.GetRequiredService<AppDbContext>().SalesAgents.CountAsync(a => a.Name == "School ERP sales assistant (SAMPLE)"));
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", oldEnv.Item1); Environment.SetEnvironmentVariable("Database__MigrateOnStartup", oldEnv.Item2);
            factory?.Dispose(); MySqlConnection.ClearAllPools(); await Exec(adminConn, $"DROP DATABASE IF EXISTS `{dbName}`");
        }
    }

    [RequiresMySqlFact]
    public async Task The_migration_chain_applies_to_an_empty_database_with_decimal_money_unique_keys_and_no_pending_model_changes()
    {
        _ = app;
        var adminConn = Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar)!;
        var dbName = "leadpilot_clean13_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        try
        {
            await using var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, new TenantContext());
            await ctx.Database.MigrateAsync();
            Assert.Empty(await ctx.Database.GetPendingMigrationsAsync()); Assert.False(ctx.Database.HasPendingModelChanges());
            Assert.Equal("decimal(18,2)", (await ScalarList(cs, "SELECT column_type FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'PricingPlans' AND column_name = 'Price'")).Single());
            Assert.Equal("decimal(18,4)", (await ScalarList(cs, "SELECT column_type FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'CustomFieldValues' AND column_name = 'NumberValue'")).Single());
            foreach (var index in new[] { "UX_CustomFieldDefinitions_Key", "UX_CustomFieldValues_Field_Entity", "UX_PackDefinitions_Key_Version_Scope", "UX_PackItemLinks_Item" })
                Assert.Equal(1L, await Scalar(cs, $"SELECT COUNT(DISTINCT index_name) FROM information_schema.statistics WHERE table_schema = DATABASE() AND index_name = '{index}' AND non_unique = 0"));
            foreach (var table in new[] { "CustomFieldDefinitions", "CustomFieldValues", "TenantPackApplications", "PackItemLinks" })
                Assert.True(await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.referential_constraints WHERE constraint_schema = DATABASE() AND table_name = '{table}' AND referenced_table_name = 'Tenants'") >= 1, table + " must reference Tenants");
            Assert.Equal(0L, await Scalar(cs, "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'PackDefinitions' AND column_name = 'TenantId'"));   // not tenant-owned: published packs are shared
        }
        finally { MySqlConnection.ClearAllPools(); await Exec(adminConn, $"DROP DATABASE IF EXISTS `{dbName}`"); }
    }

    /// <summary>Inserts one row, filling every other NOT NULL column without a default with a neutral value (so tests do not depend on every column of old tables).</summary>
    private static async Task InsertAsync(string cs, string table, Dictionary<string, object> values)
    {
        await using var c = new MySqlConnection(cs); await c.OpenAsync();
        var cols = new List<(string Name, string Type, bool Nullable, bool HasDefault)>();
        await using (var q = c.CreateCommand())
        {
            q.CommandText = $"SELECT column_name, data_type, is_nullable, column_default FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = '{table}'";
            await using var r = await q.ExecuteReaderAsync();
            while (await r.ReadAsync()) cols.Add((r.GetString(0), r.GetString(1), r.GetString(2) == "YES", !r.IsDBNull(3)));
        }
        var all = new Dictionary<string, object>(values);
        foreach (var (name, type, nullable, hasDefault) in cols)
        {
            if (all.ContainsKey(name) || nullable || hasDefault) continue;
            all[name] = type switch { "char" or "varchar" => name.EndsWith("Id") && type == "char" ? Guid.NewGuid() : "", "text" or "longtext" or "mediumtext" => "", "datetime" or "timestamp" => DateTime.UtcNow, "date" => DateTime.UtcNow.Date, _ => 0 };
        }
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT INTO `{table}` ({string.Join(",", all.Keys.Select(k => "`" + k + "`"))}) VALUES ({string.Join(",", all.Keys.Select((_, i) => "@p" + i))})";
        var n = 0; foreach (var v in all.Values) cmd.Parameters.AddWithValue("@p" + n++, v is Guid g ? g.ToString() : v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Exec(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
    private static async Task<long> Scalar(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
    private static async Task<List<string>> ScalarList(string conn, string sql) { var l = new List<string>(); await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await using var r = await cmd.ExecuteReaderAsync(); while (await r.ReadAsync()) l.Add(r.GetValue(0)?.ToString() ?? ""); return l; }
}

internal static class ScopeRunnerExtensions
{
    public static ITenantScopeRunner GetRequiresScopeRunner(this IServiceProvider sp) => sp.GetRequiredService<ITenantScopeRunner>();
}
