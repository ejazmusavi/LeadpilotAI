extern alias ApiApp;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
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

/// <summary>The Phase 6 migration applies to an empty database and to a database at the Phase 5 schema that already holds a tenant with CRM and messaging data.</summary>
[Collection("app")]
public class CampaignMigrationTests(AppFixture app)
{
    private readonly AppFixture _app = app;   // the collection fixture guarantees MySQL is configured; each test uses its own throw-away database
    private static readonly string[] Phase6Permissions = ["Campaigns.Update", "Campaigns.Approve", "Campaigns.Cancel", "Campaigns.Delete", "Campaigns.Settings", "Audiences.Preview"];
    private const string CampaignTables = "('Campaigns','CampaignAudienceSnapshots','CampaignRecipients','CampaignExecutionLogs','CampaignStatsRollups','CampaignConversions','CampaignUsageEntries','CampaignTenantSettings')";

    [RequiresMySqlFact]
    public async Task A_phase5_database_with_data_upgrades_keeps_everything_and_campaigns_work_for_the_existing_tenant()
    {
        var adminConn = Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar)!;
        var dbName = "leadpilot_upgrade6_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        var oldEnv = (Environment.GetEnvironmentVariable("ConnectionStrings__Default"), Environment.GetEnvironmentVariable("Database__MigrateOnStartup"));
        WebApplicationFactory<Program>? factory = null;
        try
        {
            // ---- 1. a database exactly as Phase 5 left it: tenant, Phase 5 role grants, a lead with a contact and a message template ----
            var tenantContext = new TenantContext(); Guid tenantId, leadId, contactId, templateId;
            await using (var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, tenantContext))
            {
                var phase5 = ctx.Database.GetMigrations().Single(m => m.EndsWith("_Phase5Messaging"));
                await ctx.GetService<IMigrator>().MigrateAsync(phase5);
                Assert.DoesNotContain("Phase6Campaigns", await ctx.Database.GetAppliedMigrationsAsync());
                Assert.Equal(0L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {CampaignTables}"));
                Assert.Equal(0L, await Scalar(cs, "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'Messages' AND column_name = 'CampaignId'"));

                using var _ = tenantContext.BeginPlatformScope(); var now = DateTime.UtcNow;
                var phase5Codes = PermissionCatalog.All.Where(p => !Phase6Permissions.Contains(p.Code)).ToList();
                var permissions = phase5Codes.Select((p, i) => Permission.Create(p.Code, p.Name, null, p.Group, p.IsPlatform, (i + 1) * 10)).ToList();
                ctx.Permissions.AddRange(permissions);
                var tenant = Tenant.Create("Phase5 Co", "phase5-co", "Education", "UTC", "en", now); tenantId = tenant.Id; ctx.Tenants.Add(tenant);
                foreach (var (name, codes) in new[]
                         {
                             ("Marketing Manager", new[] { "Leads.View", "Campaigns.View", "Campaigns.Create", "Campaigns.Execute", "Conversations.View", "Channels.View", "Messages.Send", "Consent.Manage", "Suppression.Manage" }),
                             ("Read-only", new[] { "Leads.View", "Campaigns.View" }),
                             ("Tenant Administrator", phase5Codes.Where(p => !p.IsPlatform).Select(p => p.Code).ToArray()),
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
                var contact = Contact.Create(tenantId, lead.Id, "Office", null, "+92 42 111 2233", "+924211112233", null, DataOrigin.Provided, true, now, null); contactId = contact.Id; ctx.Contacts.Add(contact);
                ctx.ConsentRecords.Add(ConsentRecord.Create(tenantId, contactId, ConsentChannel.WhatsApp, ConsentPurpose.Marketing, ConsentStatus.Granted, "Signed form", null, null, now, now, null));
                var template = MessageTemplate.Create(tenantId, "Legacy promo", "Hello {{first_name|there}}", TemplateCategory.Marketing, null, now); templateId = template.Id; ctx.MessageTemplates.Add(template);
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
            Assert.Contains(await WithDb(d => d.Database.GetAppliedMigrationsAsync().ContinueWith(t => t.Result.ToList())), m => m.EndsWith("_Phase6Campaigns"));
            Assert.Equal(8L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {CampaignTables}"));
            Assert.Equal(2L, await Scalar(cs, "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'Messages' AND column_name IN ('CampaignId','CampaignRecipientId')"));
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM Leads WHERE Name = 'Legacy School'"));                  // existing data untouched
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM Contacts WHERE PhoneE164 = '+924211112233'"));
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM MessageTemplates WHERE Name = 'Legacy promo'"));
            Assert.Equal(0L, await Scalar(cs, "SELECT COUNT(*) FROM Messages WHERE CampaignId IS NOT NULL"));

            var codesByRole = await WithDb(async d => (await (from rp in d.RolePermissions.IgnoreQueryFilters() join p in d.Permissions on rp.PermissionId equals p.Id join r in d.TenantRoles.IgnoreQueryFilters() on rp.RoleId equals r.Id
                                                              where r.TenantId == tenantId select new { r.Name, p.Code }).ToListAsync()).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.Select(x => x.Code).ToList()));
            Assert.Equal(PermissionCatalog.TenantCodes.Order().ToList(), codesByRole["Tenant Administrator"].Order().ToList());   // an existing tenant receives every new permission
            Assert.Superset(Phase6Permissions.ToHashSet(), codesByRole["Tenant Administrator"].ToHashSet());
            var marketing = codesByRole["Marketing Manager"];
            Assert.Contains("Campaigns.Update", marketing); Assert.Contains("Campaigns.Cancel", marketing); Assert.Contains("Audiences.Preview", marketing); Assert.DoesNotContain("Campaigns.Approve", marketing); Assert.DoesNotContain("Campaigns.Settings", marketing);
            Assert.Empty(codesByRole["Read-only"].Intersect(Phase6Permissions));
            Assert.Contains("Campaigns.Execute", marketing);                                                                    // old grants stay

            // ---- 4. the upgraded tenant can use campaigns (settings, audience over the legacy contact) ----
            await factory.Services.GetRequiredService<ITenantScopeRunner>().RunAsync(tenantId, async (sp, ct) =>
            {
                var settings = await sp.GetRequiredService<ICampaignSettingsService>().GetAsync(ct);
                Assert.Equal(7, settings.CooldownDays); Assert.False(settings.KillSwitch);
                var saved = await sp.GetRequiredService<ICampaignSettingsService>().UpdateAsync(new UpdateCampaignSettingsRequest(3, false, true, 7, 30, 10, "Reply STOP to opt out.", null, null), ct);
                Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Message : "");
                var preview = await sp.GetRequiredService<ICampaignAudienceService>().PreviewAsync(new AudiencePreviewRequest(new AudienceDefinition { ContactIds = [contactId] }, LeadPilot.Domain.Campaigns.CampaignType.Manual, templateId), ct);
                Assert.True(preview.IsSuccess, preview.IsFailure ? preview.Error.Message : ""); Assert.Equal(1, preview.Value.Eligible);
            });
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM CampaignTenantSettings"));
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
        var dbName = "leadpilot_clean6_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        try
        {
            await using var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, new TenantContext());
            await ctx.Database.MigrateAsync();
            Assert.Empty(await ctx.Database.GetPendingMigrationsAsync());
            Assert.False(ctx.Database.HasPendingModelChanges());
            Assert.Equal(8L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {CampaignTables}"));
            async Task<long> Unique(string table, string columns) => await Scalar(cs, $"SELECT COUNT(*) FROM (SELECT index_name FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '{table}' AND non_unique = 0 GROUP BY index_name HAVING GROUP_CONCAT(column_name ORDER BY seq_in_index) = '{columns}') x");
            Assert.Equal(1L, await Unique("CampaignRecipients", "CampaignId,ContactId"));                                      // a contact can never be queued twice
            Assert.Equal(1L, await Unique("CampaignStatsRollups", "CampaignId")); Assert.Equal(1L, await Unique("CampaignTenantSettings", "TenantId"));
            Assert.Equal(1L, await Unique("CampaignUsageEntries", "TenantId,RecipientId,Round")); Assert.Equal(1L, await Unique("CampaignConversions", "TenantId,RecipientId,Kind,ReferenceId"));
            Assert.True(await Scalar(cs, "SELECT COUNT(*) FROM information_schema.referential_constraints WHERE constraint_schema = DATABASE() AND table_name = 'CampaignRecipients'") >= 7);   // tenant + campaign + snapshot + contact + lead + message + conversation
            Assert.True(await Scalar(cs, "SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'CampaignRecipients' AND index_name <> 'PRIMARY' AND column_name = 'TenantId' AND seq_in_index = 1") >= 4);   // (TenantId, …) indexes
        }
        finally { MySqlConnection.ClearAllPools(); await Exec(adminConn, $"DROP DATABASE IF EXISTS `{dbName}`"); }
    }

    private static async Task Exec(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
    private static async Task<long> Scalar(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
}
