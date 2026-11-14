extern alias ApiApp;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Agents;
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

/// <summary>The Phase 8 migration applies to an empty database and to a database at the Phase 7 schema that already holds a tenant, roles, a lead and an old tool-log row.</summary>
[Collection("app")]
public class AgentMigrationTests(AppFixture app)
{
    private readonly AppFixture _app = app;
    private static readonly string[] Phase8Permissions = ["Agents.View", "Catalog.View", "Catalog.Manage", "Approvals.View", "Approvals.Decide"];
    private const string AgentTables = "('SalesAgents','AgentQualificationQuestions','CatalogProducts','PricingPlans','ApprovalRequests','ConversationNotes','HandoffEvents','AgentTenantSettings','LeadQualifications','FollowUps')";

    [RequiresMySqlFact]
    public async Task A_phase7_database_with_data_upgrades_keeps_everything_and_existing_roles_receive_the_new_permissions()
    {
        var adminConn = Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar)!;
        var dbName = "leadpilot_upgrade8_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        var oldEnv = (Environment.GetEnvironmentVariable("ConnectionStrings__Default"), Environment.GetEnvironmentVariable("Database__MigrateOnStartup"));
        WebApplicationFactory<Program>? factory = null;
        try
        {
            var tenantContext = new TenantContext(); Guid tenantId, leadId; var legacyTool = Guid.NewGuid();
            await using (var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, tenantContext))
            {
                var phase7 = ctx.Database.GetMigrations().Single(m => m.EndsWith("_Phase7AiKnowledge"));
                await ctx.GetService<IMigrator>().MigrateAsync(phase7);
                Assert.DoesNotContain("Phase8SalesAgent", await ctx.Database.GetAppliedMigrationsAsync());
                Assert.Equal(0L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {AgentTables}"));

                using var _ = tenantContext.BeginPlatformScope(); var now = DateTime.UtcNow;
                var phase7Codes = PermissionCatalog.All.Where(p => !Phase8Permissions.Contains(p.Code)).ToList();
                var permissions = phase7Codes.Select((p, i) => Permission.Create(p.Code, p.Name, null, p.Group, p.IsPlatform, (i + 1) * 10)).ToList();
                ctx.Permissions.AddRange(permissions);
                var tenant = Tenant.Create("Phase7 Co", "phase7-co", "Education", "UTC", "en", now); tenantId = tenant.Id; ctx.Tenants.Add(tenant);
                foreach (var (name, codes) in new[]
                         {
                             ("Sales Manager", new[] { "Leads.View", "Conversations.View", "Conversations.Reply", "Knowledge.View", "Ai.View" }),
                             ("Read-only", new[] { "Leads.View", "Conversations.View", "Knowledge.View" }),
                             ("Tenant Administrator", phase7Codes.Where(p => !p.IsPlatform).Select(p => p.Code).ToArray()),
                         })
                {
                    var role = Role.Create(tenantId, name, null, true, now); ctx.TenantRoles.Add(role);
                    foreach (var p in permissions.Where(p => codes.Contains(p.Code))) ctx.RolePermissions.Add(RolePermission.Create(tenantId, role.Id, p.Id));
                }
                LeadPilot.Infrastructure.Services.Crm.CrmDefaults.AddAll(ctx, tenantId, now);
                await ctx.SaveChangesAsync();
                var pipeline = await ctx.Pipelines.IgnoreQueryFilters().FirstAsync(p => p.TenantId == tenantId); var stage = await ctx.PipelineStages.IgnoreQueryFilters().Where(s => s.PipelineId == pipeline.Id).OrderBy(s => s.Position).FirstAsync();
                var status = await ctx.LeadStatuses.IgnoreQueryFilters().FirstAsync(s => s.TenantId == tenantId && s.IsDefault);
                var lead = Lead.Create(tenantId, new LeadProfile("Legacy School", null, "Pakistan", null, "Lahore", null, null, null, null, 60), pipeline.Id, stage.Id, status.Id, null, LeadSourceType.Manual, now, null); leadId = lead.Id; ctx.Leads.Add(lead);
                ctx.Contacts.Add(Contact.Create(tenantId, lead.Id, "Office", null, "+92 42 111 2233", "+924211112233", null, DataOrigin.Provided, true, now, null));
                await ctx.SaveChangesAsync();
            }
            // a Phase 7 tool-log row (the interaction it points at is not needed to prove the column change keeps the data)
            await Exec(cs, $"SET FOREIGN_KEY_CHECKS=0; INSERT INTO AiToolExecutions (Id, TenantId, InteractionId, ToolName, ArgumentsSummary, Outcome, Reason, OccurredAtUtc) VALUES ('{legacyTool}', '{tenantId}', '{Guid.NewGuid()}', 'escalate_to_human', 'reason=x', 'Executed', NULL, UTC_TIMESTAMP(6)); SET FOREIGN_KEY_CHECKS=1;");

            Environment.SetEnvironmentVariable("ConnectionStrings__Default", cs); Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "true");
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
            factory.CreateClient().Dispose();
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", oldEnv.Item1); Environment.SetEnvironmentVariable("Database__MigrateOnStartup", oldEnv.Item2);

            async Task<T> WithDb<T>(Func<AppDbContext, Task<T>> work)
            {
                using var scope = factory!.Services.CreateScope(); using var __ = scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().BeginPlatformScope();
                return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
            }
            Assert.Contains(await WithDb(d => d.Database.GetAppliedMigrationsAsync().ContinueWith(t => t.Result.ToList())), m => m.EndsWith("_Phase8SalesAgent"));
            Assert.Equal(10L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {AgentTables}"));
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM Leads WHERE Name = 'Legacy School'"));
            Assert.Equal(1L, await Scalar(cs, $"SELECT COUNT(*) FROM AiToolExecutions WHERE Id = '{legacyTool}' AND ToolName = 'escalate_to_human' AND InteractionId IS NOT NULL AND InputJson IS NULL AND TriggerMessageId IS NULL"));

            var codesByRole = await WithDb(async d => (await (from rp in d.RolePermissions.IgnoreQueryFilters() join p in d.Permissions on rp.PermissionId equals p.Id join r in d.TenantRoles.IgnoreQueryFilters() on rp.RoleId equals r.Id
                                                              where r.TenantId == tenantId select new { r.Name, p.Code }).ToListAsync()).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.Select(x => x.Code).ToList()));
            Assert.Equal(PermissionCatalog.TenantCodes.Order().ToList(), codesByRole["Tenant Administrator"].Order().ToList());
            foreach (var c in new[] { "Agents.View", "Catalog.View", "Catalog.Manage", "Approvals.View", "Approvals.Decide" }) Assert.Contains(c, codesByRole["Sales Manager"]);
            Assert.DoesNotContain("Agents.Manage", codesByRole["Sales Manager"]);
            Assert.Contains("Catalog.View", codesByRole["Read-only"]); Assert.DoesNotContain("Catalog.Manage", codesByRole["Read-only"]); Assert.DoesNotContain("Approvals.Decide", codesByRole["Read-only"]);
            Assert.Contains("Conversations.Reply", codesByRole["Sales Manager"]);                              // old grants stay

            await factory.Services.GetRequiredService<ITenantScopeRunner>().RunAsync(tenantId, async (sp, ct) =>
            {
                var agents = sp.GetRequiredService<IAgentService>();
                var s = await agents.GetSettingsAsync(ct); Assert.False(s.AiRepliesAllowed);                    // opt-in: off for upgraded tenants
                var sample = await agents.CreateSampleAsync(ct); Assert.True(sample.IsSuccess, sample.IsFailure ? sample.Error.Message : "");
                Assert.False(sample.Value.AutoReplyEnabled); Assert.True(sample.Value.IsSample);
            });
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM SalesAgents")); Assert.Equal(5L, await Scalar(cs, "SELECT COUNT(*) FROM AgentQualificationQuestions"));
            Assert.True(await WithDb(d => d.Leads.IgnoreQueryFilters().AnyAsync(l => l.Id == leadId)));
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
        var dbName = "leadpilot_clean8_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        try
        {
            await using var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, new TenantContext());
            await ctx.Database.MigrateAsync();
            Assert.Empty(await ctx.Database.GetPendingMigrationsAsync()); Assert.False(ctx.Database.HasPendingModelChanges());
            Assert.Equal(10L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {AgentTables}"));
            async Task<long> Unique(string table, string columns) => await Scalar(cs, $"SELECT COUNT(*) FROM (SELECT index_name FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '{table}' AND non_unique = 0 GROUP BY index_name HAVING GROUP_CONCAT(column_name ORDER BY seq_in_index) = '{columns}') x");
            Assert.Equal(1L, await Unique("AgentTenantSettings", "TenantId")); Assert.Equal(1L, await Unique("ApprovalRequests", "TenantId,DedupeKey")); Assert.Equal(1L, await Unique("AiToolExecutions", "TenantId,DedupeKey"));
            Assert.Equal(1L, await Unique("LeadQualifications", "TenantId,LeadId")); Assert.Equal(1L, await Unique("AgentQualificationQuestions", "TenantId,AgentId,Key"));
            Assert.Equal("decimal(18,2)", await ScalarText(cs, "SELECT column_type FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'PricingPlans' AND column_name = 'Price'"));
            Assert.Equal("YES", await ScalarText(cs, "SELECT is_nullable FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'PricingPlans' AND column_name = 'Price'"));   // "no price set" is representable
            Assert.Equal("YES", await ScalarText(cs, "SELECT is_nullable FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'AiToolExecutions' AND column_name = 'InteractionId'"));
            foreach (var table in new[] { "SalesAgents", "AgentQualificationQuestions", "CatalogProducts", "PricingPlans", "ApprovalRequests", "ConversationNotes", "HandoffEvents", "AgentTenantSettings", "LeadQualifications", "FollowUps" })
            {
                Assert.True(await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.referential_constraints WHERE constraint_schema = DATABASE() AND table_name = '{table}' AND referenced_table_name = 'Tenants'") == 1, $"{table} needs its tenant foreign key");
                Assert.True(await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '{table}' AND index_name <> 'PRIMARY' AND column_name = 'TenantId' AND seq_in_index = 1") >= 1, $"{table} needs a (TenantId, …) index");
            }
            foreach (var (table, column) in new[] { ("Conversations", "AiEpoch"), ("Conversations", "EscalationReason"), ("Messages", "IsAiGenerated"), ("Messages", "AiEpoch"), ("AiInteractions", "AutoSend") })
                Assert.Equal(1L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = '{table}' AND column_name = '{column}'"));
        }
        finally { MySqlConnection.ClearAllPools(); await Exec(adminConn, $"DROP DATABASE IF EXISTS `{dbName}`"); }
    }

    private static async Task Exec(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
    private static async Task<long> Scalar(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
    private static async Task<string> ScalarText(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToString(await cmd.ExecuteScalarAsync()) ?? ""; }
}
