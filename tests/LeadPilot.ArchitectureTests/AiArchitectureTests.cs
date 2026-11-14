using System.Reflection;
using Hangfire;
using LeadPilot.Application.Agents;
using LeadPilot.Application.Ai;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Common;
using LeadPilot.Infrastructure.Ai.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace LeadPilot.ArchitectureTests;

/// <summary>Rules that keep the Phase 7 AI module provider-independent, tenant-isolated, unable to send messages, and behind permissions.</summary>
public class AiArchitectureTests
{
    private static readonly Assembly Infrastructure = typeof(KnowledgeJob).Assembly;
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

    private static IEnumerable<string> AiFiles(string project) => Sources(project).Where(f => f.Replace('\\', '/').Contains("/Ai/") || f.Replace('\\', '/').Contains("/Agents/"));

    [Fact]
    public void Every_ai_entity_is_tenant_owned_and_ledgers_and_logs_are_append_only()
    {
        var entities = typeof(KnowledgeDocument).Assembly.GetTypes().Where(t => t.Namespace == typeof(KnowledgeDocument).Namespace && typeof(Entity).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.True(entities.Count >= 11, "expected the AI entities to be discovered");
        Assert.All(entities, t => Assert.True(typeof(ITenantOwned).IsAssignableFrom(t), $"{t.Name} must be tenant owned"));
        foreach (var t in new[] { typeof(RetrievalLog), typeof(AiUsageEntry), typeof(AiToolExecution) }) Assert.True(typeof(IAppendOnly).IsAssignableFrom(t), t.Name);
    }

    [Fact]
    public void The_vector_store_port_has_no_tenant_parameter_so_callers_cannot_omit_or_change_it()
    {
        foreach (var m in typeof(IVectorStore).GetMethods())
        {
            Assert.DoesNotContain(m.GetParameters(), p => p.Name!.Contains("tenant", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(m.GetParameters(), p => p.ParameterType == typeof(Guid) && p.Name!.Contains("tenant", StringComparison.OrdinalIgnoreCase));
        }
        foreach (var t in new[] { typeof(LeadPilot.Infrastructure.Ai.Stores.MySqlVectorStore), typeof(LeadPilot.Infrastructure.Ai.Stores.QdrantVectorStore) })
            Assert.Contains(t.GetConstructors().Single().GetParameters(), p => p.ParameterType == typeof(LeadPilot.Application.Abstractions.ITenantContext));
        // the Qdrant adapter builds every filter in ONE place that always carries the tenant condition
        var qdrant = File.ReadAllText(Sources("LeadPilot.Infrastructure").Single(f => f.EndsWith("QdrantVectorStore.cs")));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(qdrant, "key = \"tenant_id\""));
        Assert.DoesNotContain("filter = new", qdrant);
    }

    [Fact]
    public void Business_code_is_not_coupled_to_a_provider_only_the_adapters_know_the_vendor_endpoints_and_http()
    {
        string[] vendor = ["api.anthropic.com", "anthropic-version", "anthropic-beta", "voyageai.com", "/v1/embeddings"];
        var offenders = new List<string>();
        foreach (var project in new[] { "LeadPilot.Domain", "LeadPilot.Application", "LeadPilot.Infrastructure", "LeadPilot.Web", "LeadPilot.Api", "LeadPilot.Worker" })
            foreach (var file in Sources(project))
            {
                var rel = file.Replace('\\', '/');
                if (rel.Contains("/Ai/Providers/") || rel.EndsWith("Application/Ai/Options.cs")) continue;   // the adapters and their default endpoints
                var text = File.ReadAllText(file);
                offenders.AddRange(vendor.Where(text.Contains).Select(v => $"{rel}: {v}"));
            }
        Assert.Empty(offenders);

        string[] adapterTypes = ["ClaudeAIProvider", "VoyageEmbeddingProvider", "MySqlVectorStore", "QdrantVectorStore", "HttpClient"];
        var coupled = AiFiles("LeadPilot.Infrastructure").Where(f => { var r = f.Replace('\\', '/'); return r.Contains("/Ai/Services/") || r.Contains("/Ai/Jobs/") && !r.EndsWith("AiJobs.cs"); })
            .SelectMany(f => adapterTypes.Where(File.ReadAllText(f).Contains).Select(a => $"{Path.GetFileName(f)}: {a}")).ToList();
        Assert.Empty(coupled);
        // the use cases depend on the ports only
        foreach (var t in Infrastructure.GetTypes().Where(t => t.Namespace == "LeadPilot.Infrastructure.Ai.Services" && t.IsClass && !t.Name.StartsWith("Metered")))   // the metering decorators are the one place that wraps the raw adapters
            foreach (var c in t.GetConstructors())
                Assert.DoesNotContain(c.GetParameters(), p => p.ParameterType.Name is "ClaudeAIProvider" or "VoyageEmbeddingProvider" or "MySqlVectorStore" or "QdrantVectorStore" or "IRawAIProvider" or "IRawEmbeddingProvider");
    }

    [Fact]
    public void Only_the_credential_vault_decrypts_provider_keys_and_nothing_else_reads_the_protected_columns()
    {
        var offenders = new List<string>();
        foreach (var project in new[] { "LeadPilot.Application", "LeadPilot.Infrastructure", "LeadPilot.Web", "LeadPilot.Api", "LeadPilot.Worker" })
            foreach (var file in Sources(project))
            {
                var rel = file.Replace('\\', '/'); var text = File.ReadAllText(file);
                if (!rel.Contains("/Ai/") && !rel.Contains("Controllers/Ai")) continue;
                if (text.Contains(".Unprotect(") && !rel.EndsWith("Ai/Providers/AiCredentials.cs")) offenders.Add(rel + ": Unprotect");
                if ((project is "LeadPilot.Web" or "LeadPilot.Api" or "LeadPilot.Application") && (text.Contains("ProtectedApiKey") || text.Contains("ProtectedEmbeddingKey"))) offenders.Add(rel + ": protected column");
            }
        Assert.Empty(offenders);
        // no key or message text is ever logged: the AI namespace does not interpolate keys / bodies into log calls
        var logs = AiFiles("LeadPilot.Infrastructure").SelectMany(f => File.ReadAllLines(f).Where(l => l.Contains("logger.Log") && (l.Contains("Body") || l.Contains("ApiKey") || l.Contains("Content") || l.Contains("Prompt") || l.Contains("Text"))).Select(l => Path.GetFileName(f) + ": " + l.Trim())).ToList();
        Assert.Empty(logs);
    }

    [Fact]
    public void The_ai_module_cannot_send_messages_it_never_touches_the_send_pipeline_or_the_gateway()
    {
        string[] forbidden = ["IWhatsAppGateway", "IOutboundMessageService", "OutboundQueueService", "IOutboundSendRunner", ".SendToContactAsync(", ".SendTextAsync(", ".SendTemplateAsync(", "ReplyAsync(", "WahaGateway", "ICampaignService", "IConversationService"];
        var offenders = new List<string>();
        foreach (var project in new[] { "LeadPilot.Application", "LeadPilot.Infrastructure" })
            foreach (var file in AiFiles(project))
            {
                var text = File.ReadAllText(file);
                offenders.AddRange(forbidden.Where(text.Contains).Select(f => $"{Path.GetFileName(file)}: {f}"));
            }
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_ai_may_only_use_the_controlled_tools_and_none_of_them_can_activate_pay_delete_or_query()
    {
        string[] spec = ["GetProductInformation", "GetPricingPlans", "GetLeadProfile", "UpdateLeadStatus", "QualifyLead", "CheckDemoAvailability", "BookDemo", "ScheduleFollowUp", "CreateTrialAccount", "CreatePaymentRequest", "TransferToHuman", "StopMarketingMessages"];
        Assert.All(spec, n => Assert.NotNull(AgentToolCatalog.Find(n)));
        Assert.Equal(spec.Length + 1, AgentToolCatalog.All.Count);   // + AddLeadNote carried over from Phase 7
        string[] dangerous = ["activate", "subscription", "confirmpayment", "markpaid", "sql", "query", "execute", "delete", "refund", "export", "consent", "suppress", "sendmessage", "campaign", "tenant", "user", "role", "permission"];
        Assert.All(AgentToolCatalog.All, t => Assert.DoesNotContain(dangerous, d => t.Name.Contains(d, StringComparison.OrdinalIgnoreCase) || t.Capability.Contains(d, StringComparison.OrdinalIgnoreCase)));
        // the model can never choose the tenant, a user, a lead or a conversation
        Assert.All(AgentToolCatalog.All.SelectMany(t => t.Params), p => Assert.DoesNotContain(new[] { "tenant", "user", "lead", "conversation", "contact", "agent" }, d => p.Name.Contains(d, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(ApprovalPolicy.Always, AgentToolCatalog.Find("CreatePaymentRequest")!.Approval);
        Assert.Equal(ApprovalPolicy.Conditional, AgentToolCatalog.Find("CreateTrialAccount")!.Approval);
        Assert.All(AgentToolCatalog.All.Where(t => t.ReadOnly), t => Assert.Equal(ApprovalPolicy.Never, t.Approval));
        // the only tools that are always available only REDUCE what the AI does
        Assert.Equal(["StopMarketingMessages", "TransferToHuman"], AgentToolCatalog.All.Where(t => t.Safety).Select(t => t.Name).Order().ToArray());
        Assert.Null(AgentToolCatalog.Find("send_message")); Assert.Null(AgentToolCatalog.Find("apply_discount")); Assert.Null(AgentToolCatalog.Find("run_sql"));
    }

    [Fact]
    public void Only_the_tool_gateway_changes_things_for_the_ai_and_nothing_else_in_the_ai_module_touches_the_crm_write_services()
    {
        // the AI orchestration and the loop reach tools only through AgentToolRunner; handlers are reached only by the runner
        string[] writers = ["ILeadService", "INoteService", "ISuppressionService", "IConsentService", "AgentToolHandlers", "OptOutProcessor"];
        var offenders = new List<string>();
        foreach (var file in AiFiles("LeadPilot.Infrastructure").Where(f => { var r = f.Replace('\\', '/'); return r.Contains("/Ai/Services/") || r.EndsWith("AgentLoop.cs"); }))
        { var text = File.ReadAllText(file); offenders.AddRange(writers.Where(text.Contains).Select(w => $"{Path.GetFileName(file)}: {w}")); }
        Assert.Empty(offenders);
        Assert.Single(Infrastructure.GetTypes(), t => typeof(IAiReplySender).IsAssignableFrom(t) && !t.IsInterface);
    }

    [Fact]
    public void Ai_messages_leave_only_through_the_single_reply_sender_which_has_no_way_to_start_a_conversation()
    {
        var m = typeof(IAiReplySender).GetMethods().Single();
        Assert.DoesNotContain(m.GetParameters().SelectMany(p => p.ParameterType == typeof(AiSendRequest) ? typeof(AiSendRequest).GetProperties().Select(x => x.Name) : [p.Name!]), n => n.Contains("Contact", StringComparison.OrdinalIgnoreCase) || n.Contains("Phone", StringComparison.OrdinalIgnoreCase) || n.Contains("Template", StringComparison.OrdinalIgnoreCase));
        // outside messaging, only the sender may mark a message as AI-written
        var offenders = Sources("LeadPilot.Infrastructure").Where(f => !f.Replace('\\', '/').Contains("/Messaging/")).Where(f => File.ReadAllText(f).Contains("MarkAiGenerated") || File.ReadAllText(f).Contains("AiInteractionId =")).ToList();
        Assert.Empty(offenders);
        var senderSource = File.ReadAllText(Sources("LeadPilot.Infrastructure").Single(f => f.EndsWith("AiReplySender.cs")));
        Assert.Contains("ForcedPurpose = MessagePurpose.Service", senderSource);
        Assert.DoesNotContain("MessagePurpose.Marketing", senderSource);
        Assert.DoesNotContain("ContactId", senderSource.Replace("m.ContactId", ""));
    }

    [Fact]
    public void Hangfire_ai_jobs_declare_their_queue_and_a_bounded_retry_and_no_other_scheduler_exists()
    {
        foreach (var t in new[] { typeof(KnowledgeJob), typeof(KnowledgeSweepJob) })
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.Equal(AiQueues.Knowledge, m.GetCustomAttribute<QueueAttribute>()?.Queue);
                var retry = m.GetCustomAttribute<AutomaticRetryAttribute>();
                Assert.NotNull(retry); Assert.InRange(retry!.Attempts, 0, 3);
            }
        string[] forbidden = ["System.Threading.Timer", "new Timer(", "PeriodicTimer", "Quartz", "BackgroundService", "IHostedService", "Thread.Sleep", "new Thread("];
        var offenders = AiFiles("LeadPilot.Infrastructure").SelectMany(f => forbidden.Where(File.ReadAllText(f).Contains).Select(x => $"{Path.GetFileName(f)}: {x}")).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void No_test_doubles_ship_and_web_and_api_never_touch_the_database_context_for_ai()
    {
        Assert.DoesNotContain(Infrastructure.GetTypes(), t => t.Namespace?.Contains(".Ai") == true && (t.Name.StartsWith("Fake") || t.Name.StartsWith("Mock") || t.Name.Contains("TestOnly")));
        foreach (var project in new[] { "LeadPilot.Web", "LeadPilot.Api" })
            Assert.DoesNotContain(Sources(project).Where(f => Path.GetFileName(f).StartsWith("Ai") || Path.GetFileName(f).Contains("Knowledge")), f => File.ReadAllText(f).Contains("AppDbContext"));
    }

    public static IEnumerable<object[]> AiControllers() =>
        Web.GetTypes().Where(t => typeof(Controller).IsAssignableFrom(t) && t.Name is "KnowledgeController" or "AiTesterController" or "AiSettingsController" or "AiController").Select(t => new object[] { t })
            .Concat(Api.GetTypes().Where(t => t.Name is "KnowledgeController" or "AiController").Select(t => new object[] { t }));

    [Theory, MemberData(nameof(AiControllers))]
    public void Every_ai_action_has_a_catalog_permission_an_explicit_verb_and_mutations_are_post_in_the_web(Type controller)
    {
        var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() is null).ToList();
        Assert.NotEmpty(actions);
        var classPolicy = controller.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy).FirstOrDefault(p => p is not null);
        foreach (var a in actions)
        {
            var policy = a.GetCustomAttributes<AuthorizeAttribute>().Select(x => x.Policy).FirstOrDefault(p => p is not null) ?? classPolicy;
            Assert.True(policy is not null && PermissionCatalog.Exists(policy), $"{controller.Name}.{a.Name} needs a catalog permission");
            var verbs = a.GetCustomAttributes().OfType<HttpMethodAttribute>().SelectMany(v => v.HttpMethods).ToList();
            Assert.True(verbs.Count > 0, $"{controller.Name}.{a.Name} needs an explicit HTTP verb");
            if (controller.Namespace!.Contains("Web") && a.Name is not ("Index" or "Add" or "Document" or "Usage" or "Activity"))
                Assert.Equal(["POST"], verbs);
            if (controller.Namespace.Contains("Web") && a.Name is "Index" or "Add" or "Document" or "Usage" or "Activity") Assert.Equal(["GET"], verbs);
        }
    }

    public static IEnumerable<object[]> AgentControllers() =>
        Web.GetTypes().Where(t => typeof(Controller).IsAssignableFrom(t) && t.Name is "AgentsController" or "ApprovalsController" or "CatalogController" or "FollowUpsController").Select(t => new object[] { t })
            .Concat(Api.GetTypes().Where(t => t.Name is "AgentsController" or "ProductCatalogController" or "ApprovalsController" or "ToolExecutionsController" or "FollowUpsController").Select(t => new object[] { t }));

    [Theory, MemberData(nameof(AgentControllers))]
    public void Every_sales_agent_action_has_a_catalog_permission_an_explicit_verb_and_web_mutations_are_post(Type controller)
    {
        var actions = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName && m.GetCustomAttribute<NonActionAttribute>() is null && m.DeclaringType == controller).ToList();
        Assert.NotEmpty(actions);
        var classPolicy = controller.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy).FirstOrDefault(p => p is not null);
        string[] webReads = ["Index", "Edit", "Executions", "Product"];
        foreach (var a in actions)
        {
            var policy = a.GetCustomAttributes<AuthorizeAttribute>().Select(x => x.Policy).FirstOrDefault(p => p is not null) ?? classPolicy;
            Assert.True(policy is not null && PermissionCatalog.Exists(policy), $"{controller.Name}.{a.Name} needs a catalog permission");
            var verbs = a.GetCustomAttributes().OfType<HttpMethodAttribute>().SelectMany(v => v.HttpMethods).ToList();
            Assert.True(verbs.Count > 0, $"{controller.Name}.{a.Name} needs an explicit HTTP verb");
            if (controller.Namespace!.Contains("Web")) Assert.Equal(webReads.Contains(a.Name) ? ["GET"] : ["POST"], verbs);
        }
    }

    [Fact]
    public void The_conversation_handoff_actions_are_post_only_and_need_reply_permission()
    {
        var web = Web.GetTypes().Single(t => t.Name == "ConversationsController");
        foreach (var name in new[] { "TakeOver", "PauseAi", "ResumeAi", "AddNote", "Assign" })
        {
            var m = web.GetMethod(name)!;
            Assert.Equal(["POST"], m.GetCustomAttributes().OfType<HttpMethodAttribute>().SelectMany(v => v.HttpMethods).ToList());
            Assert.Equal(PermissionCodes.ConversationsReply, m.GetCustomAttributes<AuthorizeAttribute>().Single().Policy);
        }
        var api = Api.GetTypes().Single(t => t.Name == "ConversationsController");
        foreach (var name in new[] { "TakeOver", "PauseAi", "ResumeAi", "AddNote" })
            Assert.Equal(PermissionCodes.ConversationsReply, api.GetMethod(name)!.GetCustomAttributes<AuthorizeAttribute>().Single().Policy);
    }

    [Fact]
    public void Every_phase_8_entity_is_tenant_owned_ledgers_are_append_only_and_hangfire_is_still_the_only_scheduler()
    {
        var ns = typeof(LeadPilot.Domain.Agents.SalesAgent).Namespace;
        var entities = typeof(LeadPilot.Domain.Agents.SalesAgent).Assembly.GetTypes().Where(t => t.Namespace == ns && typeof(Entity).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.True(entities.Count >= 10);
        Assert.All(entities, t => Assert.True(typeof(ITenantOwned).IsAssignableFrom(t), $"{t.Name} must be tenant owned"));
        foreach (var t in new[] { typeof(LeadPilot.Domain.Agents.ConversationNote), typeof(LeadPilot.Domain.Agents.HandoffEvent), typeof(AiToolExecution) }) Assert.True(typeof(IAppendOnly).IsAssignableFrom(t), t.Name);
        string[] forbidden = ["System.Threading.Timer", "new Timer(", "PeriodicTimer", "Quartz", "BackgroundService", "IHostedService", "Thread.Sleep", "new Thread(", "Task.Run("];
        var offenders = AiFiles("LeadPilot.Infrastructure").Where(f => f.Replace('\\', '/').Contains("/Agents/")).SelectMany(f => forbidden.Where(File.ReadAllText(f).Contains).Select(x => $"{Path.GetFileName(f)}: {x}")).ToList();
        Assert.Empty(offenders);
    }

    [Fact]
    public void The_phase_10_ports_have_no_fake_implementation_in_the_product_and_report_not_connected()
    {
        foreach (var port in new[] { typeof(IDemoScheduler), typeof(ITrialProvisioner), typeof(IPaymentRequestGateway) })
        {
            var impl = Infrastructure.GetTypes().Single(t => port.IsAssignableFrom(t) && !t.IsInterface);
            Assert.StartsWith("Unavailable", impl.Name);
        }
        var source = File.ReadAllText(Sources("LeadPilot.Infrastructure").Single(f => f.EndsWith("UnavailablePorts.cs")));
        Assert.DoesNotContain("PortResult<TrialInfo>.Ok", source); Assert.DoesNotContain(".Ok(", source.Replace("NotConnected", ""));
    }

    [Fact]
    public void The_conversation_suggestion_actions_are_post_only_and_need_reply_permission()
    {
        var c = Web.GetTypes().Single(t => t.Name == "ConversationsController");
        foreach (var name in new[] { "UseSuggestion", "DismissSuggestion" })
        {
            var m = c.GetMethod(name)!;
            Assert.Equal(["POST"], m.GetCustomAttributes().OfType<HttpMethodAttribute>().SelectMany(v => v.HttpMethods).ToList());
            Assert.Equal(PermissionCodes.ConversationsReply, m.GetCustomAttributes<AuthorizeAttribute>().Single().Policy);
        }
    }

    [Fact]
    public void The_new_permissions_are_in_the_catalog_with_least_privilege_defaults()
    {
        string[] codes = [PermissionCodes.KnowledgeView, PermissionCodes.KnowledgeManage, PermissionCodes.AiView, PermissionCodes.AiTest, PermissionCodes.AiSettings];
        Assert.All(codes, c => Assert.True(PermissionCatalog.Exists(c) && !PermissionCatalog.IsPlatform(c)));
        var role = (string n) => SystemRoles.All.Single(r => r.Name == n).Permissions;
        Assert.All(codes, c => Assert.Contains(c, role(SystemRoles.TenantAdministrator)));
        Assert.All(codes, c => Assert.Contains(c, role(SystemRoles.PlatformOwner)));
        // provider keys: administrators only
        foreach (var n in new[] { SystemRoles.SalesManager, SystemRoles.SalesAgent, SystemRoles.MarketingManager, SystemRoles.TenantStaff, SystemRoles.ReadOnly }) Assert.DoesNotContain(PermissionCodes.AiSettings, role(n));
        foreach (var n in new[] { SystemRoles.SalesAgent, SystemRoles.MarketingManager, SystemRoles.TenantStaff, SystemRoles.ReadOnly }) Assert.DoesNotContain(PermissionCodes.KnowledgeManage, role(n));
        Assert.DoesNotContain(PermissionCodes.AiTest, role(SystemRoles.ReadOnly));
        Assert.Contains(PermissionCodes.KnowledgeView, role(SystemRoles.ReadOnly));
    }

    [Fact]
    public void The_ai_reply_event_has_its_own_consumer_queue_and_the_handler_is_registered()
    {
        var queues = LeadPilot.Infrastructure.Messaging.Broker.MessagingTopology.Queues(new LeadPilot.Infrastructure.Messaging.RabbitMqOptions { Prefix = "x" });
        Assert.Contains(queues, q => q.ConsumerName == "ai.reply" && q.RoutingKeys.Contains(LeadPilot.Application.Messaging.MessagingEventTypes.AiReplyRequested));
    }
}
