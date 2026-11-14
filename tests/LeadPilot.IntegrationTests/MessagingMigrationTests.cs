extern alias ApiApp;
using System.Text;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Messaging.Waha;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace LeadPilot.IntegrationTests;

/// <summary>The Phase 5 migration applies to a database at the Phase 4 schema that already holds a tenant with CRM data; nothing is lost, roles gain the messaging permissions and the tenant can use WhatsApp afterwards.</summary>
[Collection("app")]
public class MessagingMigrationTests(AppFixture app)
{
    private static readonly string[] Phase4Permissions =
    [
        "Leads.View", "Leads.Create", "Leads.Update", "Leads.Delete", "Leads.Import", "Leads.Export", "Leads.Assign", "Consent.Manage", "Suppression.Manage", "Pipelines.Manage", "Scoring.Manage",
        "Campaigns.View", "Campaigns.Create", "Campaigns.Execute", "Conversations.View", "Conversations.Reply", "Agents.Manage", "Reports.View", "Users.Manage", "Settings.Manage", "Audit.View",
        "Discovery.View", "Discovery.Run", "Discovery.Review", "Discovery.Manage", "Integrations.Manage", "Tenants.Manage", "Platform.SwitchTenant",
    ];
    private static readonly string[] NewPermissions = ["Channels.View", "Channels.Manage", "Messages.Send", "Webhooks.View"];
    private const string MessagingTables = "('Channels','ChannelAccounts','ChannelCredentials','Conversations','ConversationAssignments','Messages','MessageAttachments','MessageStatusEvents','WebhookEvents','MessageTemplates','OutboxMessages','ProcessedMessages','ChannelAccountNotifications')";

    [RequiresMySqlFact]
    public async Task A_phase4_database_with_data_upgrades_keeps_everything_and_whatsapp_works_for_the_existing_tenant()
    {
        var adminConn = Environment.GetEnvironmentVariable(RequiresMySqlFactAttribute.EnvVar)!;
        var dbName = "leadpilot_upgrade5_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        var oldEnv = (Environment.GetEnvironmentVariable("ConnectionStrings__Default"), Environment.GetEnvironmentVariable("Database__MigrateOnStartup"));
        WebApplicationFactory<Program>? factory = null;
        try
        {
            // ---- 1. a database exactly as Phase 4 left it: tenant, roles, CRM defaults, one lead with a contact ----
            var tenantContext = new TenantContext(); Guid tenantId, leadId;
            var options = new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options;
            await using (var ctx = new AppDbContext(options, tenantContext))
            {
                var phase4 = ctx.Database.GetMigrations().Single(m => m.EndsWith("_Phase4Discovery"));
                await ctx.GetService<IMigrator>().MigrateAsync(phase4);
                Assert.DoesNotContain("Phase5Messaging", await ctx.Database.GetAppliedMigrationsAsync());
                Assert.Equal(0L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {MessagingTables}"));

                using var _ = tenantContext.BeginPlatformScope(); var now = DateTime.UtcNow;
                var permissions = Phase4Permissions.Select((c, i) => Permission.Create(c, c, null, c.Split('.')[0], c is "Tenants.Manage" or "Platform.SwitchTenant", (i + 1) * 10)).ToList();
                ctx.Permissions.AddRange(permissions);
                var tenant = Tenant.Create("Phase4 Co", "phase4-co", "Education", "UTC", "en", now); tenantId = tenant.Id; ctx.Tenants.Add(tenant);
                foreach (var (name, codes) in new[]
                         {
                             ("Sales Manager", Phase4Permissions.Where(c => c.StartsWith("Leads") || c.StartsWith("Discovery") || c is "Consent.Manage" or "Suppression.Manage" or "Conversations.View" or "Conversations.Reply" or "Reports.View").ToArray()),
                             ("Sales Agent", new[] { "Leads.View", "Conversations.View", "Conversations.Reply" }), ("Read-only", new[] { "Leads.View", "Conversations.View" }),
                             ("Tenant Administrator", Phase4Permissions.Where(c => c is not ("Tenants.Manage" or "Platform.SwitchTenant")).ToArray()),
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
            Assert.Contains(await WithDb(d => d.Database.GetAppliedMigrationsAsync().ContinueWith(t => t.Result.ToList())), m => m.EndsWith("_Phase5Messaging"));
            Assert.Equal(13L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {MessagingTables}"));
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(DISTINCT index_name) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'ChannelAccounts' AND non_unique = 0 AND column_name = 'GatewayAccountId'"));
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(DISTINCT index_name) FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = 'WebhookEvents' AND non_unique = 0 AND index_name <> 'PRIMARY'"));
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM Leads WHERE Name = 'Legacy School'"));       // existing data untouched
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM Contacts WHERE PhoneE164 = '+924211112222'"));
            Assert.Equal(0L, await Scalar(cs, "SELECT COUNT(*) FROM Messages"));

            var codesByRole = await WithDb(async d => (await (from rp in d.RolePermissions.IgnoreQueryFilters() join p in d.Permissions on rp.PermissionId equals p.Id join r in d.TenantRoles.IgnoreQueryFilters() on rp.RoleId equals r.Id
                                                              where r.TenantId == tenantId select new { r.Name, p.Code }).ToListAsync()).GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.Select(x => x.Code).ToHashSet()));
            Assert.Equal(PermissionCatalog.TenantCodes.Order().ToList(), codesByRole["Tenant Administrator"].Order().ToList());
            Assert.Superset(NewPermissions.ToHashSet(), codesByRole["Tenant Administrator"]);
            Assert.Contains("Channels.View", codesByRole["Sales Manager"]); Assert.Contains("Messages.Send", codesByRole["Sales Manager"]); Assert.DoesNotContain("Channels.Manage", codesByRole["Sales Manager"]);
            Assert.Contains("Channels.View", codesByRole["Sales Agent"]); Assert.DoesNotContain("Messages.Send", codesByRole["Sales Agent"]); Assert.DoesNotContain("Webhooks.View", codesByRole["Sales Agent"]);
            Assert.Empty(codesByRole["Read-only"].Intersect(NewPermissions));
            Assert.Contains("Discovery.Run", codesByRole["Sales Manager"]);   // old grants stay

            // ---- 4. the upgraded tenant can add a WhatsApp account and receive a webhook ----
            using var scope2 = factory.Services.CreateScope();
            string webhookKey = "", secret = "upgrade-secret-" + Guid.NewGuid().ToString("N"); var session = MessagingKit.NewSession();
            await factory.Services.GetRequiredService<ITenantScopeRunner>().RunAsync(tenantId, async (sp, ct) =>
            {
                var accounts = sp.GetRequiredService<IChannelAccountService>();
                var created = await accounts.CreateAsync(new CreateChannelAccountRequest("Upgrade line", app.Waha.BaseUrl, FakeWahaServer.ApiKey, session, secret), ct);
                Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : "");
                webhookKey = (await accounts.GetSetupAsync(created.Value.Id, ct)).Value.WebhookPath.Split('/').Last();
            });
            var chat = "923001119999@c.us"; var body = Encoding.UTF8.GetBytes(WahaEvents.Message(session, chat, "hello after the upgrade"));
            var ingestion = factory.Services.CreateScope().ServiceProvider.GetRequiredService<IWebhookIngestionService>();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Webhook-Hmac"] = WahaSignature.Compute(secret, body), ["X-Webhook-Hmac-Algorithm"] = "sha512" };
            Assert.Equal(WebhookReceiveStatus.Accepted, (await ingestion.ReceiveAsync(webhookKey, body, headers, "127.0.0.1")).Status);
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM Messages WHERE Body = 'hello after the upgrade'"));
            Assert.Equal(1L, await Scalar(cs, "SELECT COUNT(*) FROM OutboxMessages WHERE Status = 'Pending'"));
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
        var dbName = "leadpilot_clean5_" + Guid.NewGuid().ToString("N")[..10];
        var cs = new MySqlConnectionStringBuilder(adminConn) { Database = dbName }.ConnectionString;
        await Exec(adminConn, $"CREATE DATABASE `{dbName}`");
        try
        {
            await using var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options, new TenantContext());
            await ctx.Database.MigrateAsync();
            Assert.Empty(await ctx.Database.GetPendingMigrationsAsync());
            Assert.Equal(13L, await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN {MessagingTables}"));
            Assert.False(ctx.Database.HasPendingModelChanges());
            async Task<long> Unique(string table, string columns) => await Scalar(cs, $"SELECT COUNT(*) FROM (SELECT index_name FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name = '{table}' AND non_unique = 0 AND index_name <> 'PRIMARY' GROUP BY index_name HAVING GROUP_CONCAT(column_name ORDER BY seq_in_index) = '{columns}') x");
            Assert.Equal(1L, await Unique("ChannelAccounts", "GatewayAccountId")); Assert.Equal(1L, await Unique("ChannelAccounts", "WebhookKey"));
            Assert.Equal(1L, await Unique("WebhookEvents", "ChannelAccountId,ExternalEventId")); Assert.Equal(1L, await Unique("Messages", "TenantId,ChannelAccountId,ExternalMessageId")); Assert.Equal(1L, await Unique("Messages", "TenantId,IdempotencyKey"));
            Assert.Equal(1L, await Unique("ProcessedMessages", "TenantId,ConsumerName,MessageId")); Assert.Equal(1L, await Unique("Conversations", "TenantId,ChannelAccountId,ExternalChatId")); Assert.Equal(1L, await Unique("MessageTemplates", "TenantId,Name"));
            Assert.True(await Scalar(cs, "SELECT COUNT(*) FROM information_schema.referential_constraints WHERE constraint_schema = DATABASE() AND table_name = 'Messages'") >= 5);
            Assert.Equal(0L, await Scalar(cs, "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name IN ('ChannelCredentials') AND column_name IN ('ApiKey','Password','Secret','BaseUrl','WebhookSecret')"));   // only Protected* columns exist
        }
        finally { MySqlConnection.ClearAllPools(); await Exec(adminConn, $"DROP DATABASE IF EXISTS `{dbName}`"); }
    }

    private static async Task Exec(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; await cmd.ExecuteNonQueryAsync(); }
    private static async Task<long> Scalar(string conn, string sql) { await using var c = new MySqlConnection(conn); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(await cmd.ExecuteScalarAsync()); }
}
