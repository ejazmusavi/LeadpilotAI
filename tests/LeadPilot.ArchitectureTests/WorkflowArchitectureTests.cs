using System.Reflection;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Workflows;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace LeadPilot.ArchitectureTests;

public class WorkflowArchitectureTests
{
    private static string Root() { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null && !File.Exists(Path.Combine(d.FullName, "LeadPilotAI.sln"))) d = d.Parent; return d!.FullName; }
    private static IEnumerable<string> Files(string rel) => Directory.EnumerateFiles(Path.Combine(Root(), "src", rel), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/") && !f.Contains("/bin/") && !f.Contains("Migrations"));
    private static readonly Type[] Entities = [typeof(Workflow), typeof(WorkflowVersion), typeof(WorkflowTrigger), typeof(WorkflowStep), typeof(WorkflowExecution), typeof(WorkflowExecutionLog), typeof(WorkflowTenantSettings), typeof(WorkflowNotification)];

    [Fact]
    public void Every_workflow_entity_is_tenant_owned_the_log_is_append_only_and_steps_and_triggers_are_sealable()
    {
        Assert.All(Entities, t => Assert.True(typeof(ITenantOwned).IsAssignableFrom(t), t.Name));
        Assert.True(typeof(IAppendOnly).IsAssignableFrom(typeof(WorkflowExecutionLog)));
        Assert.True(typeof(ISealable).IsAssignableFrom(typeof(WorkflowStep))); Assert.True(typeof(ISealable).IsAssignableFrom(typeof(WorkflowTrigger)));
        var guard = File.ReadAllText(Path.Combine(Root(), "src/LeadPilot.Infrastructure/Persistence/TenantSaveChangesInterceptor.cs"));
        Assert.Contains("ISealable", guard);
    }

    [Fact]
    public void The_workflow_module_never_touches_the_gateway_or_the_send_pipeline_and_sends_only_through_the_single_sender()
    {
        foreach (var f in Files("LeadPilot.Infrastructure/Workflows").Concat(Files("LeadPilot.Application/Workflows")))
        {
            var text = File.ReadAllText(f);
            foreach (var banned in new[] { "IWhatsAppGateway", "OutboundQueueService", "QueueRequest", "SendTextAsync", "Waha", "IOutboundSendRunner" })
                Assert.False(text.Contains(banned), $"{Path.GetFileName(f)} references {banned}");
        }
        Assert.Contains("OutboundQueueService", File.ReadAllText(Path.Combine(Root(), "src/LeadPilot.Infrastructure/Messaging/Services/WorkflowMessageSender.cs")));
        Assert.Single(typeof(LeadPilot.Application.Workflows.IWorkflowMessageSender).GetMethods(), m => m.Name == "SendAsync");
        var sender = File.ReadAllText(Path.Combine(Root(), "src/LeadPilot.Infrastructure/Messaging/Services/WorkflowMessageSender.cs"));
        Assert.Contains("eligibility.CheckAsync", sender); Assert.Contains("MessagePurpose.Marketing", sender);   // consent is always required
    }

    [Fact]
    public void Workflows_contain_no_dynamic_code_execution_no_regex_conditions_and_no_other_scheduler()
    {
        foreach (var f in Files("LeadPilot.Infrastructure/Workflows").Concat(Files("LeadPilot.Application/Workflows")))
        {
            var text = File.ReadAllText(f);
            foreach (var banned in new[] { "CSharpScript", "Assembly.Load", "Activator.CreateInstance", "DataTable", "Expression.Lambda", "Process.Start", "System.Reflection.Emit", "Task.Delay", "new Timer", "System.Threading.Timer", "new Thread(", "Regex.Match(", "Regex.IsMatch(", "new Regex(", "ScriptEngine", "Jint" })
                Assert.False(text.Contains(banned), $"{Path.GetFileName(f)} uses {banned}");
        }
        foreach (var t in new[] { typeof(LeadPilot.Infrastructure.Workflows.WorkflowJob), typeof(LeadPilot.Infrastructure.Workflows.WorkflowSweepJob) })
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var attrs = m.GetCustomAttributes().Select(a => a.GetType().Name).ToList();
                Assert.Contains("QueueAttribute", attrs); Assert.Contains("AutomaticRetryAttribute", attrs);
            }
    }

    [Fact]
    public void Phase_10_events_have_no_producer_in_the_product()
    {
        foreach (var f in Files("."))
        {
            if (f.EndsWith("Definition.cs") && f.Contains("Workflows")) continue;
            var text = File.ReadAllText(f);
            foreach (var n in new[] { "WorkflowEventTypes.DemoBooked", "WorkflowEventTypes.DemoCompleted", "WorkflowEventTypes.TrialCreated", "WorkflowEventTypes.TrialExpires", "WorkflowEventTypes.PaymentConfirmed" })
                Assert.False(text.Contains(n), $"{Path.GetFileName(f)} produces/uses {n}");
        }
    }

    [Theory]
    [InlineData("LeadPilot.Web", "LeadPilot.Web.Controllers.WorkflowsController")]
    [InlineData("LeadPilot.Web", "LeadPilot.Web.Controllers.WorkflowExecutionsController")]
    [InlineData("LeadPilot.Web", "LeadPilot.Web.Controllers.WorkflowNotificationsController")]
    [InlineData("LeadPilot.Api", "LeadPilot.Api.Controllers.WorkflowsController")]
    [InlineData("LeadPilot.Api", "LeadPilot.Api.Controllers.WorkflowExecutionsController")]
    [InlineData("LeadPilot.Api", "LeadPilot.Api.Controllers.WorkflowSettingsController")]
    [InlineData("LeadPilot.Api", "LeadPilot.Api.Controllers.WorkflowNotificationsController")]
    public void Every_workflow_action_has_a_catalog_permission_and_mutations_are_never_get(string assembly, string controller)
    {
        var type = Assembly.Load(assembly).GetType(controller)!;
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any()))
        {
            var policies = type.GetCustomAttributes<AuthorizeAttribute>().Concat(m.GetCustomAttributes<AuthorizeAttribute>()).Select(a => a.Policy).Where(p => p is not null).ToList();
            Assert.True(policies.Count > 0 && policies.All(p => PermissionCatalog.Exists(p!)), $"{controller}.{m.Name}");
            var verbs = m.GetCustomAttributes<HttpMethodAttribute>().SelectMany(a => a.HttpMethods).ToList();
            var reads = new[] { "Index", "Details", "Version", "Edit", "New", "Simulate", "Settings", "FindLeads", "List", "Get", "GetVersion", "Catalog", "Mine" };
            if (!reads.Contains(m.Name)) Assert.DoesNotContain("GET", verbs);
        }
    }

    [Fact]
    public void The_workflow_permissions_are_in_the_catalog_with_least_privilege_defaults()
    {
        var codes = new[] { "Workflows.View", "Workflows.Create", "Workflows.Update", "Workflows.Activate", "Workflows.Execute", "Workflows.Delete" };
        foreach (var c in codes) Assert.True(PermissionCatalog.Exists(c) && !PermissionCatalog.IsPlatform(c));
        var role = (string n) => SystemRoles.All.Single(r => r.Name == n).Permissions;
        Assert.All(codes, c => Assert.Contains(c, role(SystemRoles.TenantAdministrator)));
        Assert.Equal(["Workflows.View"], role(SystemRoles.ReadOnly).Where(c => c.StartsWith("Workflows.")).ToArray());
        Assert.Equal(["Workflows.View"], role(SystemRoles.SalesAgent).Where(c => c.StartsWith("Workflows.")).ToArray());
        Assert.DoesNotContain("Workflows.Delete", role(SystemRoles.SalesManager)); Assert.DoesNotContain("Workflows.Activate", role(SystemRoles.MarketingManager));
    }

    [Fact]
    public void The_workflow_consumer_has_its_own_queue_and_hangfire_processes_the_workflow_queue()
    {
        var q = LeadPilot.Infrastructure.Messaging.Broker.MessagingTopology.Queues(new LeadPilot.Infrastructure.Messaging.RabbitMqOptions());
        var wq = q.Single(x => x.ConsumerName == "workflows.triggers");
        Assert.Contains("lead.created", wq.RoutingKeys); Assert.Contains("message.received", wq.RoutingKeys); Assert.Contains("customer.replied", wq.RoutingKeys);
        Assert.Contains("WorkflowQueues.Workflows", File.ReadAllText(Path.Combine(Root(), "src/LeadPilot.Infrastructure/Discovery/Jobs/HangfireIntegration.cs")));
    }
}
