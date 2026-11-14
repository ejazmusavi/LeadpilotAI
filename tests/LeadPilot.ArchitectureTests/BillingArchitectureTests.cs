using LeadPilot.Domain.Billing;
using LeadPilot.Domain.Common;
using Xunit;

namespace LeadPilot.ArchitectureTests;

/// <summary>The Phase 12 rules that must not erode: one money path, one entitlement check, immutable invoices, tenant-owned billing rows, platform-only permissions.</summary>
public class BillingArchitectureTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LeadPilotAI.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static IEnumerable<string> Files(string rel) => Directory.EnumerateFiles(Path.Combine(Root(), "src", rel), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains("/obj/") && !f.Contains("/bin/") && !f.Contains("Migrations"));

    [Fact]
    public void A_paid_period_can_only_start_from_a_verified_payment_in_two_places()
    {
        var callers = Files(".").Where(f => !f.Replace('\\', '/').EndsWith("Domain/Billing/Billing.cs") && File.ReadAllText(f).Contains("StartPaidPeriodWithVerifiedPayment(") || File.ReadAllText(f).Contains("RollOverToPaidPeriod(") && !f.Replace('\\', '/').EndsWith("Domain/Billing/Billing.cs"))
            .Select(Path.GetFileName).OrderBy(x => x).ToList();
        Assert.Equal(["Invoicing.cs", "Subscriptions.cs"], callers);
        // the state of a subscription can only move through the domain methods
        Assert.True(typeof(TenantSubscription).GetProperty(nameof(TenantSubscription.State))!.SetMethod!.IsPrivate);
        // verified payments are written only by the verifier
        var writers = Files(".").Where(f => File.ReadAllText(f).Contains("BillingPayment.Create(") || File.ReadAllText(f).Contains("db.BillingPayments.Add(")).Select(Path.GetFileName).Where(n => n != "Billing.cs").ToList();
        Assert.Equal(["Invoicing.cs"], writers);
    }

    [Fact]
    public void Nothing_in_ai_workflows_or_agents_can_reach_billing_payments()
    {
        foreach (var dir in new[] { "LeadPilot.Infrastructure/Workflows", "LeadPilot.Infrastructure/Agents", "LeadPilot.Infrastructure/Ai", "LeadPilot.Application/Agents" })
            foreach (var f in Files(dir)) { var t = File.ReadAllText(f); Assert.False(t.Contains("BillingPaymentVerifier") || t.Contains("BillingPayment") || t.Contains("StartPaidPeriodWithVerifiedPayment") || t.Contains("IPlatformBillingService"), Path.GetFileName(f)); }
    }

    [Fact]
    public void Every_billing_row_that_names_a_tenant_is_tenant_owned_and_the_ledgers_are_append_only()
    {
        var types = typeof(TenantSubscription).Assembly.GetTypes().Where(t => t.Namespace == typeof(TenantSubscription).Namespace && typeof(Entity).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        var global = new[] { typeof(Plan), typeof(PlanLimit), typeof(PlanFeature), typeof(PlatformSetting), typeof(RegistrationAttempt) };
        foreach (var t in types.Where(x => !global.Contains(x))) Assert.True(typeof(ITenantOwned).IsAssignableFrom(t), $"{t.Name} must be tenant owned");
        foreach (var t in global) Assert.False(typeof(ITenantOwned).IsAssignableFrom(t), $"{t.Name} is a platform-level entity");
        foreach (var t in new[] { typeof(BillingPayment), typeof(InvoiceLine), typeof(SubscriptionEvent), typeof(UsageNotice) }) Assert.True(typeof(IAppendOnly).IsAssignableFrom(t), t.Name);
    }

    [Fact]
    public void Invoice_fields_cannot_be_changed_from_outside_the_domain()
    {
        foreach (var p in typeof(Invoice).GetProperties().Where(p => p.CanWrite && p.Name is not ("CreatedAtUtc" or "CreatedBy" or "UpdatedAtUtc" or "UpdatedBy")))
            Assert.True(p.SetMethod!.IsPrivate || p.SetMethod.IsFamily, $"Invoice.{p.Name} must have a private setter");
    }

    [Fact]
    public void Limits_are_enforced_only_through_the_entitlement_service()
    {
        // no feature code may re-implement a plan check: the plan tables are read only by the Billing module
        foreach (var f in Files("LeadPilot.Infrastructure").Where(f => !f.Replace('\\', '/').Contains("/Billing/") && !f.Replace('\\', '/').Contains("/Persistence/") && !f.Replace('\\', '/').Contains("/Seeding/")))
        {
            var t = File.ReadAllText(f);
            Assert.False(t.Contains("db.PlanLimits") || t.Contains(".TenantEntitlementOverrides") || t.Contains("UsageReservations"), Path.GetFileName(f));
        }
        foreach (var f in Files("LeadPilot.Web").Concat(Files("LeadPilot.Api"))) Assert.DoesNotContain("PlanLimit", File.ReadAllText(f).Replace("PlanLimitsFor", ""), StringComparison.Ordinal);
    }
}
