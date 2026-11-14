using System.Reflection;
using LeadPilot.Application.Agents;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Conversion;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace LeadPilot.ArchitectureTests;

public class ConversionArchitectureTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LeadPilotAI.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
    private static IEnumerable<string> Files(string rel) => Directory.EnumerateFiles(Path.Combine(Root(), "src", rel), "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/obj/") && !f.Contains("/bin/") && !f.Contains("Migrations"));

    [Fact]
    public void Every_conversion_entity_is_tenant_owned_and_the_ledgers_are_append_only()
    {
        var entities = typeof(Demo).Assembly.GetTypes().Where(t => t.Namespace == typeof(Demo).Namespace && typeof(Entity).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.True(entities.Count >= 14);
        Assert.All(entities, t => Assert.True(typeof(ITenantOwned).IsAssignableFrom(t), $"{t.Name} must be tenant owned"));
        foreach (var t in new[] { typeof(DemoHistory), typeof(TrialEvent), typeof(Payment), typeof(PaymentEvent), typeof(CommercialAgreement) }) Assert.True(typeof(IAppendOnly).IsAssignableFrom(t), t.Name);
    }

    [Fact]
    public void A_subscription_is_activated_only_by_the_payment_verification_core()
    {
        var offenders = Files(".").Where(f => !f.Replace('\\', '/').EndsWith("Domain/Conversion/Customers.cs") && File.ReadAllText(f).Contains("ActivateWithVerifiedPayment(")).Select(Path.GetFileName).ToList();
        Assert.Equal(["PaymentService.cs"], offenders);
        // and the subscription status can only be set through that method
        var setter = typeof(Subscription).GetProperty(nameof(Subscription.Status))!.SetMethod!;
        Assert.True(setter.IsPrivate);
        // payments are written only by the verification core
        var paymentWriters = Files(".").Where(f => File.ReadAllText(f).Contains("Payment.Create(") || File.ReadAllText(f).Contains("db.Payments.Add(")).Select(Path.GetFileName).Where(n => n != "Customers.cs").ToList();
        Assert.Equal(["PaymentService.cs"], paymentWriters);
    }

    [Fact]
    public void The_ai_tool_catalog_and_workflow_actions_cannot_verify_a_payment_or_activate_a_subscription()
    {
        foreach (var tool in AgentToolCatalog.All) Assert.DoesNotMatch("(?i)verify|activate|confirm.?payment|mark.?paid|subscription", tool.Name);
        foreach (var dir in new[] { "LeadPilot.Infrastructure/Workflows", "LeadPilot.Infrastructure/Agents" })
            foreach (var f in Files(dir)) { var t = File.ReadAllText(f); Assert.False(t.Contains("PaymentVerificationCore") || t.Contains("IPaymentService") || t.Contains("ActivateWithVerifiedPayment"), Path.GetFileName(f)); }
    }

    [Fact]
    public void The_product_ships_no_payment_provider_adapter_and_no_fake_trial_success()
    {
        var infra = typeof(LeadPilot.Infrastructure.Conversion.ConversionJobsMarker).Assembly;
        Assert.DoesNotContain(infra.GetTypes(), t => typeof(LeadPilot.Application.Conversion.IPaymentProviderAdapter).IsAssignableFrom(t) && !t.IsInterface);
        var manual = File.ReadAllText(Files("LeadPilot.Infrastructure/Conversion").Single(f => f.EndsWith("TrialService.cs")));
        Assert.Contains("new TrialProvisionResult(false", manual);
        Assert.DoesNotContain("new TrialProvisionResult(true", manual);
    }

    [Fact]
    public void Conversion_code_uses_the_one_scheduler_and_the_one_send_pipeline()
    {
        string[] banned = ["System.Threading.Timer", "new Timer(", "PeriodicTimer", "Quartz", "BackgroundService", "IHostedService", "Thread.Sleep", "new Thread(", "Task.Run("];
        foreach (var f in Files("LeadPilot.Infrastructure/Conversion")) foreach (var b in banned) Assert.False(File.ReadAllText(f).Contains(b), $"{Path.GetFileName(f)} uses {b}");
        foreach (var f in Files("LeadPilot.Infrastructure/Conversion")) { var t = File.ReadAllText(f); Assert.DoesNotContain("OutboundQueueService", t); Assert.DoesNotContain("IWhatsAppGateway", t); }
        foreach (var m in typeof(LeadPilot.Infrastructure.Conversion.ConversionSweepJob).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var attrs = m.GetCustomAttributes().Select(a => a.GetType().Name).ToList();
            Assert.Contains("QueueAttribute", attrs); Assert.Contains("AutomaticRetryAttribute", attrs);
        }
    }

    [Theory]
    [InlineData("LeadPilot.Web", "LeadPilot.Web.Controllers.DemosController")]
    [InlineData("LeadPilot.Web", "LeadPilot.Web.Controllers.DemoSettingsController")]
    [InlineData("LeadPilot.Web", "LeadPilot.Web.Controllers.TrialsController")]
    [InlineData("LeadPilot.Web", "LeadPilot.Web.Controllers.CustomersController")]
    [InlineData("LeadPilot.Web", "LeadPilot.Web.Controllers.PaymentsController")]
    [InlineData("LeadPilot.Api", "LeadPilot.Api.Controllers.DemosController")]
    [InlineData("LeadPilot.Api", "LeadPilot.Api.Controllers.DemoSettingsController")]
    [InlineData("LeadPilot.Api", "LeadPilot.Api.Controllers.TrialsController")]
    [InlineData("LeadPilot.Api", "LeadPilot.Api.Controllers.CustomersController")]
    [InlineData("LeadPilot.Api", "LeadPilot.Api.Controllers.PaymentsController")]
    public void Every_conversion_action_has_a_catalog_permission_and_mutations_are_never_get(string assembly, string controller)
    {
        var type = Assembly.Load(assembly).GetType(controller)!;
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any()))
        {
            var anonymous = m.GetCustomAttributes<AllowAnonymousAttribute>().Any();
            var policies = type.GetCustomAttributes<AuthorizeAttribute>().Concat(m.GetCustomAttributes<AuthorizeAttribute>()).Select(a => a.Policy).Where(p => p is not null).ToList();
            if (anonymous) { Assert.Equal("Webhook", m.Name); continue; }   // the ONE anonymous action: signature-verified webhook
            Assert.True(policies.Count > 0 && policies.All(p => PermissionCatalog.Exists(p!)), $"{controller}.{m.Name}");
            var verbs = m.GetCustomAttributes<HttpMethodAttribute>().SelectMany(a => a.HttpMethods).ToList();
            var reads = new[] { "Index", "Details", "Book", "Hours", "Create", "Convert", "Providers", "Slots", "Get", "List", "Reps", "Won", "Providers" };
            if (verbs.Contains("GET")) Assert.Contains(m.Name, reads);
        }
    }

    [Fact]
    public void Money_columns_are_decimal_18_2_and_no_price_is_hardcoded()
    {
        foreach (var f in Files("LeadPilot.Infrastructure/Conversion").Concat(Files("LeadPilot.Domain/Conversion")).Concat(Files("LeadPilot.Application/Conversion")))
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotMatch(@"\b(price|amount)\s*=\s*\d", text.Replace("MaxPerDay", ""));
            Assert.DoesNotMatch(@"\bdouble\s+[A-Za-z_]", System.Text.RegularExpressions.Regex.Replace(text, "//[^\n]*|\"[^\"\n]*\"", ""));
        }
        var model = File.ReadAllText(Files("LeadPilot.Infrastructure/Persistence").Single(f => f.EndsWith("ConversionModel.cs")));
        Assert.Contains("HasPrecision(18, 2)", model);
    }
}
