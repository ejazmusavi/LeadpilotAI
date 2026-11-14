using System.Reflection;
using Hangfire;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Messaging.Jobs;
using LeadPilot.Infrastructure.Messaging.Waha;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LeadPilot.ArchitectureTests;

/// <summary>Rules that keep the Phase 5 messaging module inside the tenancy, gateway-port and no-duplicate-send rules.</summary>
public class MessagingArchitectureTests
{
    public static readonly string[] AdministrativeMessagingControllers =
    [
        "ChannelAccountsController", "ChannelsController", "ConversationsController", "MessagesController", "MessageTemplatesController", "TemplatesController",
        "WebhookEventsController", "WebhooksController", "DeadLettersController",
    ];
    public static readonly string[] MessagingControllers = [.. AdministrativeMessagingControllers, "WahaWebhooksController"];

    private static readonly Assembly Infrastructure = typeof(WahaGateway).Assembly;
    private static readonly Assembly Web = typeof(LeadPilot.Web.Models.ErrorViewModel).Assembly;
    private static readonly Assembly Api = typeof(LeadPilot.Api.Controllers.AuthController).Assembly;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LeadPilotAI.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static IEnumerable<string> Sources(string project) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains("Migrations"));

    [Fact]
    public void Every_messaging_entity_is_tenant_owned_and_the_histories_are_append_only()
    {
        var entities = typeof(Message).Assembly.GetTypes().Where(t => t.Namespace == typeof(Message).Namespace && typeof(Entity).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.True(entities.Count >= 13, "expected the messaging entities to be discovered");
        Assert.All(entities, t => Assert.True(typeof(ITenantOwned).IsAssignableFrom(t), $"{t.Name} must be tenant owned"));
        Assert.True(typeof(IAppendOnly).IsAssignableFrom(typeof(MessageStatusEvent)));
        Assert.True(typeof(IAppendOnly).IsAssignableFrom(typeof(ConversationAssignment)));
    }

    [Fact]
    public void Gateway_specifics_exist_only_inside_the_waha_adapter_folder()
    {
        string[] waha = ["/api/sendText", "X-Webhook-Hmac", "X-Api-Key", "HMACSHA512", "WahaGateway", "WahaSignature", "@c.us\"", "SCAN_QR_CODE"];
        var adapterDir = Path.Combine("LeadPilot.Infrastructure", "Messaging", "Waha");
        var offenders = new List<string>();
        foreach (var project in new[] { "LeadPilot.Domain", "LeadPilot.Application", "LeadPilot.Web", "LeadPilot.Api", "LeadPilot.Worker", "LeadPilot.Shared", "LeadPilot.Infrastructure" })
            foreach (var file in Sources(project))
            {
                if (file.Contains(adapterDir)) continue;
                var text = File.ReadAllText(file);
                // the pure rules may mention the chat-id SUFFIX they convert; everything else is a leak
                foreach (var token in waha.Where(t => text.Contains(t)))
                    if (!(Path.GetFileName(file) == "Rules.cs" && token == "@c.us\"") && !(Path.GetFileName(file) == "MessagingServiceCollection.cs" && token == "WahaGateway"))
                        offenders.Add($"{Path.GetFileName(file)}: {token}");
            }
        Assert.True(offenders.Count == 0, "WAHA specifics outside Infrastructure/Messaging/Waha: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Only_infrastructure_implements_or_uses_the_gateway_port_and_web_and_api_never_see_the_adapter()
    {
        Assert.All(typeof(IWhatsAppGateway).Assembly.GetTypes().Where(t => typeof(IWhatsAppGateway).IsAssignableFrom(t) && t != typeof(IWhatsAppGateway)), t => Assert.Fail("Application must only declare the port: " + t.Name));
        Assert.Single(Infrastructure.GetTypes(), t => !t.IsInterface && typeof(IWhatsAppGateway).IsAssignableFrom(t));
        foreach (var asm in new[] { Web, Api })
        {
            var ctorParams = asm.GetTypes().SelectMany(t => t.GetConstructors()).SelectMany(c => c.GetParameters()).Select(p => p.ParameterType);
            Assert.DoesNotContain(ctorParams, p => p == typeof(IWhatsAppGateway) || p.Namespace?.StartsWith("LeadPilot.Infrastructure.Messaging") == true);
        }
    }

    [Fact]
    public void A_text_send_is_never_retried_by_the_adapter_and_never_automatically_by_hangfire()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LeadPilot.Infrastructure", "Messaging", "Waha", "WahaGateway.cs"));
        Assert.Contains("\"/api/sendText\", body, \"application/json\", idempotent: false", text);
        foreach (var m in new[] { nameof(MessageSendJob.SendAsync), nameof(MessageSendJob.ReconcileAsync) })
        {
            var retry = typeof(MessageSendJob).GetMethod(m)!.GetCustomAttribute<AutomaticRetryAttribute>();
            Assert.NotNull(retry);
            Assert.Equal(0, retry!.Attempts);
        }
        foreach (var t in new[] { typeof(OutboxDispatchJob), typeof(MessagingMaintenanceJob) })
            Assert.All(t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), m => Assert.Equal(0, m.GetCustomAttribute<AutomaticRetryAttribute>()?.Attempts));
    }

    [Fact]
    public void Credentials_and_secrets_are_never_part_of_any_messaging_dto()
    {
        var dtos = typeof(ChannelAccountDto).Assembly.GetTypes().Where(t => t.Namespace == typeof(ChannelAccountDto).Namespace && t.Name.EndsWith("Dto", StringComparison.Ordinal));
        foreach (var t in dtos)
            Assert.DoesNotContain(t.GetProperties(), p => p.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
                                                          p.Name.Contains("Protected", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase) || p.Name == "BaseUrl");
    }

    [Fact]
    public void Messaging_never_bypasses_the_tenant_filter_outside_the_directory()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(RepoRoot(), "src", "LeadPilot.Infrastructure", "Messaging"), "*.cs", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "MessagingDirectory.cs"))
            Assert.DoesNotContain("IgnoreQueryFilters(", File.ReadAllText(f));
    }

    [Fact]
    public void Messaging_controllers_use_messaging_or_conversation_permissions_and_only_the_webhook_is_anonymous()
    {
        var catalog = new[] { PermissionCodes.ChannelsView, PermissionCodes.ChannelsManage, PermissionCodes.MessagesSend, PermissionCodes.WebhooksView, PermissionCodes.ConversationsView, PermissionCodes.ConversationsReply, PermissionCodes.TenantsManage };
        foreach (var asm in new[] { Web, Api })
            foreach (var c in asm.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract && MessagingControllers.Contains(t.Name)))
            {
                var policies = c.GetCustomAttributes<AuthorizeAttribute>(true).Concat(c.GetMethods().SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>())).Select(a => a.Policy).ToList();
                Assert.All(policies.Where(p => p is not null), p => Assert.Contains(p!, catalog));
                var anonymous = c.GetMethods().Where(m => m.GetCustomAttributes<AllowAnonymousAttribute>().Any()).Select(m => c.Name + "." + m.Name).ToList();
                if (c.Name != "WahaWebhooksController") Assert.Empty(anonymous); else Assert.Equal(["WahaWebhooksController.Receive"], anonymous);
            }
        // the dead-letter queues hold every tenant's events: platform permission only
        foreach (var asm in new[] { Web, Api })
            foreach (var c in asm.GetTypes().Where(t => t.Name == "DeadLettersController"))
                Assert.All(c.GetCustomAttributes<AuthorizeAttribute>(true).Concat(c.GetMethods().SelectMany(m => m.GetCustomAttributes<AuthorizeAttribute>())), a => Assert.Equal(PermissionCodes.TenantsManage, a.Policy));
    }

    [Fact]
    public void New_permissions_are_in_the_catalog_and_the_default_roles_are_consistent()
    {
        foreach (var code in new[] { "Channels.View", "Channels.Manage", "Messages.Send", "Webhooks.View" }) Assert.True(PermissionCatalog.Exists(code), code);
        var admin = SystemRoles.All.Single(r => r.Name == SystemRoles.TenantAdministrator).Permissions;
        Assert.Contains("Channels.Manage", admin); Assert.Contains("Webhooks.View", admin);
        Assert.DoesNotContain("Channels.Manage", SystemRoles.All.Single(r => r.Name == SystemRoles.ReadOnly).Permissions);
        Assert.DoesNotContain("Messages.Send", SystemRoles.All.Single(r => r.Name == SystemRoles.ReadOnly).Permissions);
        Assert.DoesNotContain("Webhooks.View", SystemRoles.All.Single(r => r.Name == SystemRoles.SalesAgent).Permissions);
    }

    [Fact]
    public void Webhook_events_and_outbox_enforce_the_documented_uniqueness_in_the_model()
    {
        var options = new DbContextOptionsBuilder<LeadPilot.Infrastructure.Persistence.AppDbContext>().UseMySQL("Server=localhost;Database=model_only;User=x;Password=x;").Options;
        using var db = new LeadPilot.Infrastructure.Persistence.AppDbContext(options, new LeadPilot.Infrastructure.Persistence.TenantContext());
        var model = db.GetService<IDesignTimeModel>().Model;
        bool Unique(Type t, params string[] props) => model.FindEntityType(t)!.GetIndexes().Any(i => i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual(props));
        Assert.True(Unique(typeof(ChannelAccount), "GatewayAccountId"), "session id must be unique across all tenants");
        Assert.True(Unique(typeof(ChannelAccount), "WebhookKey"));
        Assert.True(Unique(typeof(WebhookEvent), "ChannelAccountId", "ExternalEventId"));
        Assert.True(Unique(typeof(Message), "TenantId", "ChannelAccountId", "ExternalMessageId"));
        Assert.True(Unique(typeof(Message), "TenantId", "IdempotencyKey"));
        Assert.True(Unique(typeof(ProcessedMessage), "TenantId", "ConsumerName", "MessageId"));
        Assert.True(Unique(typeof(Conversation), "TenantId", "ChannelAccountId", "ExternalChatId"));
    }
}
