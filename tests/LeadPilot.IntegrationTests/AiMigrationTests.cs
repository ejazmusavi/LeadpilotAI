extern alias ApiApp;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Ai;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Ai;
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

/// <summary>The Phase 7 migration applies to an empty database and to a database at the Phase 6 schema that already holds a tenant, roles, a lead and a contact.</summary>
[Collection("app")]
public class AiMigrationTests(AppFixture app)
{
    private readonly AppFixture _app = app;
    private static readonly string[] Phase7Permissions = ["Knowledge.View", "Knowledge.Manage", "Ai.View", "Ai.Test", "Ai.Settings"];
    private const string AiTables = "('KnowledgeSources','KnowledgeDocuments','DocumentVersions','DocumentChunks','EmbeddingRecords','RetrievalLogs','AiTenantSettings','AiUsageEntries','ConversationSummaries','AiInteractions','AiToolExecutions')";

    [RequiresMySqlFact]
    public async Task A_phase6_database_with_data_upgrades_keeps_everything_and_the_ai_works_for_the_existing_tenant()
    {
        var adminConn = Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar)!;
        var dbName = "leadpilot_upgrade7_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        var oldEnv = (Environment.GetEnvironmentVariable("ConnectionStrings__Default"), Environment.GetEnvironmentVariable("Database__MigrateOnStartup"));
        WebApplicationFactory<Program>? factory = null;
        try
        {
            // ---- 1. a database exactly as Phase 6 left it: tenant, Phase 6 role grants, a lead with a contact ----
            var tenantContext = new TenantContext(); Guid tenantId, leadId;
            await using (var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, tenantContext))
            {
                var phase6 = ctx.Database.GetMigrations().Single(m => m.EndsWith("_Phase6Campaigns"));
                await ctx.GetService<IMigrator>().MigrateAsync(phase6);
                Assert.DoesNotContain("Phase7AiKnowledge", await ctx.Database.GetAppliedMigrationsAsync());
                Assert.Equal(0L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {AiTables}"));

                using var _ = tenantContext.BeginPlatformScope(); var now = DateTime.UtcNow;
                var phase6Codes = PermissionCatalog.All.Where(p => !Phase7Permissions.Contains(p.Code)).ToList();
                var permissions = phase6Codes.Select((p, i) => Permission.Create(p.Code, p.Name, null, p.Group, p.IsPlatform, (i + 1) * 10)).ToList();
                ctx.Permissions.AddRange(permissions);
                var tenant = Tenant.Create("Phase6 Co", "phase6-co", "Education", "UTC", "en", now); tenantId = tenant.Id; ctx.Tenants.Add(tenant);
                foreach (var (name, codes) in new[]
                         {
                             ("Sales Manager", new[] { "Leads.View", "Conversations.View", "Conversations.Reply", "Campaigns.View" }),
                             ("Read-only", new[] { "Leads.View", "Campaigns.View", "Conversations.View" }),
                             ("Tenant Administrator", phase6Codes.Where(p => !p.IsPlatform).Select(p => p.Code).ToArray()),
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

            // ---- 2. start the application on it (migrates, syncs roles) ----
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
            Assert.Contains(await WithDb(d => d.Database.GetAppliedMigrationsAsync().ContinueWith(t => t.Result.ToList())), m => m.EndsWith("_Phase7AiKnowledge"));
            Assert.Equal(11L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {AiTables}"));
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM Leads WHERE Name = 'Legacy School'"));                   // existing data untouched
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM Contacts WHERE PhoneE164 = '+924211112233'"));
            Assert.Equal(0L, await Scalar(cs, "SELECT COUNT(*) FROM AiTenantSettings") + await Scalar(cs, "SELECT COUNT(*) FROM KnowledgeDocuments"));

            var codesByRole = await WithDb(async d => (await (from rp in d.RolePermissions.IgnoreQueryFilters() join p in d.Permissions on rp.PermissionId equals p.Id join r in d.TenantRoles.IgnoreQueryFilters() on rp.RoleId equals r.Id
                                                              where r.TenantId == tenantId select new { r.Name, p.Code }).ToListAsync()).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.Select(x => x.Code).ToList()));
            Assert.Equal(PermissionCatalog.TenantCodes.Order().ToList(), codesByRole["Tenant Administrator"].Order().ToList());   // an existing administrator receives every new permission
            Assert.Superset(Phase7Permissions.ToHashSet(), codesByRole["Tenant Administrator"].ToHashSet());
            var sales = codesByRole["Sales Manager"];
            foreach (var c in new[] { "Knowledge.View", "Knowledge.Manage", "Ai.View", "Ai.Test" }) Assert.Contains(c, sales);
            Assert.DoesNotContain("Ai.Settings", sales);                                                                        // keys stay with administrators
            Assert.Contains("Knowledge.View", codesByRole["Read-only"]); Assert.DoesNotContain("Knowledge.Manage", codesByRole["Read-only"]); Assert.DoesNotContain("Ai.Test", codesByRole["Read-only"]);
            Assert.Contains("Conversations.Reply", sales);                                                                      // old grants stay

            // ---- 4. the upgraded tenant can use the AI module ----
            await factory.Services.GetRequiredService<ITenantScopeRunner>().RunAsync(tenantId, async (sp, ct) =>
            {
                var settings = await sp.GetRequiredService<IAiSettingsService>().GetAsync(ct);
                Assert.False(settings.IsEnabled); Assert.False(settings.HasKey); Assert.Equal(0.35m, settings.MinRetrievalScore);
                var saved = await sp.GetRequiredService<IAiSettingsService>().SetKeyAsync(AiKeyKind.Chat, new SetAiKeyRequest("sk-ant-upgrade-0123456789"), ct); Assert.True(saved.IsSuccess);
                var doc = await sp.GetRequiredService<IKnowledgeService>().CreateTextAsync(new CreateTextDocumentRequest("Legacy FAQ", DocumentKind.Faq, "We open at nine every weekday."), ct);
                Assert.True(doc.IsSuccess, doc.IsFailure ? doc.Error.Message : ""); Assert.Equal(IndexStatus.Pending, doc.Value.Status);
            });
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM KnowledgeDocuments")); Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM DocumentVersions"));
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
        var dbName = "leadpilot_clean7_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        try
        {
            await using var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, new TenantContext());
            await ctx.Database.MigrateAsync();
            Assert.Empty(await ctx.Database.GetPendingMigrationsAsync());
            Assert.False(ctx.Database.HasPendingModelChanges());
            Assert.Equal(11L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {AiTables}"));
            async Task<long> Unique(string table, string columns) => await Scalar(cs, $"SELECT COUNT(*) FROM (SELECT index_name FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '{table}' AND non_unique = 0 GROUP BY index_name HAVING GROUP_CONCAT(column_name ORDER BY seq_in_index) = '{columns}') x");
            Assert.Equal(1L, await Unique("AiInteractions", "TenantId,TriggerMessageId"));                                      // one AI run per inbound message, database-enforced
            Assert.Equal(1L, await Unique("DocumentVersions", "DocumentId,VersionNumber")); Assert.Equal(1L, await Unique("DocumentChunks", "VersionId,Ordinal"));
            Assert.Equal(1L, await Unique("EmbeddingRecords", "ChunkId,Model")); Assert.Equal(1L, await Unique("AiTenantSettings", "TenantId")); Assert.Equal(1L, await Unique("ConversationSummaries", "TenantId,ConversationId"));
            Assert.Equal("decimal(18,8)", (await ScalarText(cs, "SELECT column_type FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'AiUsageEntries' AND column_name = 'EstimatedCostUsd'")));
            Assert.Equal("longblob", await ScalarText(cs, "SELECT column_type FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'EmbeddingRecords' AND column_name = 'Vector'"));
            Assert.Equal("longtext", await ScalarText(cs, "SELECT column_type FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'DocumentVersions' AND column_name = 'Content'"));
            foreach (var table in new[] { "KnowledgeSources", "KnowledgeDocuments", "DocumentVersions", "DocumentChunks", "EmbeddingRecords", "RetrievalLogs", "AiTenantSettings", "AiUsageEntries", "ConversationSummaries", "AiInteractions", "AiToolExecutions" })
            {
                Assert.True(await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.referential_constraints WHERE constraint_schema = DATABASE() AND table_name = '{table}' AND referenced_table_name = 'Tenants'") == 1, $"{table} needs its tenant foreign key");
                Assert.True(await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '{table}' AND index_name <> 'PRIMARY' AND column_name = 'TenantId' AND seq_in_index = 1") >= 1, $"{table} needs a (TenantId, …) index");
            }
        }
        finally { MySqlConnection.ClearAllPools(); await Exec(adminConn, $"DROP DATABASE IF EXISTS `{dbName}`"); }
    }

    private static async Task Exec(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
    private static async Task<long> Scalar(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
    private static async Task<string> ScalarText(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToString(await cmd.ExecuteScalarAsync()) ?? ""; }
}
