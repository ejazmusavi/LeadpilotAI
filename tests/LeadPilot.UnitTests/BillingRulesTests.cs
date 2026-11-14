using LeadPilot.Application.Billing;
using LeadPilot.Domain.Billing;
using LeadPilot.Domain.Common;
using Xunit;

namespace LeadPilot.UnitTests;

public class BillingRulesTests
{
    private static readonly DateTime T0 = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static Plan NewPlan(decimal price = 10m, PlanCycle cycle = PlanCycle.Monthly, string cur = "USD", bool trial = false, int? days = null, int? custom = null) =>
        Plan.Create("test-plan", "Test", null, cycle, custom, price, cur, trial, days, PlanVisibility.Public, 1, false, T0, "t");

    // ---- evaluation / override precedence
    [Fact]
    public void An_active_override_wins_over_the_plan_and_an_expired_one_does_not()
    {
        var plan = new Dictionary<UsageDimension, long?> { [UsageDimension.Leads] = 100 };
        var active = new OverrideView("Leads", OverrideKind.Limit, 500, false, null, T0.AddDays(1), "pilot");
        var expired = active with { ExpiresAtUtc = T0.AddDays(-1) };
        Assert.Equal(500, EntitlementEvaluator.Limit(UsageDimension.Leads, plan, [active], T0).Limit);
        Assert.Equal(LimitSource.Override, EntitlementEvaluator.Limit(UsageDimension.Leads, plan, [active], T0).Source);
        Assert.Equal(100, EntitlementEvaluator.Limit(UsageDimension.Leads, plan, [expired], T0).Limit);
        Assert.Null(EntitlementEvaluator.Limit(UsageDimension.Leads, plan, [active with { Unlimited = true, LimitValue = null }], T0).Limit);
        Assert.Equal(100, EntitlementEvaluator.Limit(UsageDimension.Leads, plan, [active with { Key = "Users" }], T0).Limit);   // other dimension's override is ignored
    }

    [Fact]
    public void A_dimension_a_plan_does_not_mention_is_unlimited_and_zero_means_not_included()
    {
        Assert.Null(EntitlementEvaluator.Limit(UsageDimension.Users, new Dictionary<UsageDimension, long?>(), [], T0).Limit);
        Assert.True(EntitlementEvaluator.Allows(null, 1_000_000, 1));
        Assert.False(EntitlementEvaluator.Allows(0, 0, 1));
        Assert.True(EntitlementEvaluator.Allows(5, 4, 1));    // at the limit after the call is allowed
        Assert.False(EntitlementEvaluator.Allows(5, 5, 1));   // already at the limit
        Assert.False(EntitlementEvaluator.Allows(5, 3, 3));   // over
    }

    [Fact]
    public void A_feature_override_can_switch_a_feature_on_or_off()
    {
        var included = new HashSet<string> { "ai" };
        Assert.True(EntitlementEvaluator.Feature("ai", included, [], T0));
        Assert.False(EntitlementEvaluator.Feature("discovery", included, [], T0));
        Assert.True(EntitlementEvaluator.Feature("discovery", included, [new OverrideView("discovery", OverrideKind.Feature, null, false, true, null, "x")], T0));
        Assert.False(EntitlementEvaluator.Feature("ai", included, [new OverrideView("ai", OverrideKind.Feature, null, false, false, null, "x")], T0));
    }

    // ---- period windows
    [Fact]
    public void The_usage_window_is_the_subscription_period_and_rolls_with_it()
    {
        var (s, e) = UsageWindow.For(T0, T0.AddMonths(1), T0.AddDays(3));
        Assert.Equal((T0, T0.AddMonths(1)), (s, e));
        var (s2, e2) = UsageWindow.For(T0, T0.AddMonths(1), T0.AddMonths(1).AddDays(2));   // overdue: still counted from the old start up to now
        Assert.Equal(T0, s2); Assert.True(e2 > T0.AddMonths(1));
    }

    // ---- proration
    [Theory]
    [InlineData(100, 15, 50.00)]   // 30-day period, 15 days left
    [InlineData(100, 30, 100.00)]
    [InlineData(100, 0, 0.00)]
    [InlineData(0, 10, 0.00)]
    public void Proration_credits_the_unused_share_rounded_down_to_cents(decimal price, int daysLeft, decimal expected)
    {
        var start = T0; var end = T0.AddDays(30); var now = end.AddDays(-daysLeft);
        Assert.Equal(expected, Proration.Credit(price, start, end, now));
    }

    [Fact]
    public void Proration_never_returns_a_negative_or_more_than_the_price()
    {
        Assert.Equal(0m, Proration.Credit(50m, T0, T0.AddDays(30), T0.AddDays(31)));
        Assert.Equal(50m, Proration.Credit(50m, T0, T0.AddDays(30), T0.AddDays(-5)));
    }

    // ---- downgrade check
    [Fact]
    public void A_downgrade_report_names_every_dimension_that_exceeds_the_target()
    {
        var usage = new Dictionary<UsageDimension, long> { [UsageDimension.Leads] = 150, [UsageDimension.Users] = 2, [UsageDimension.Campaigns] = 9 };
        var target = new Dictionary<UsageDimension, long?> { [UsageDimension.Leads] = 100, [UsageDimension.Users] = 5, [UsageDimension.Campaigns] = null };
        var r = DowngradeCheck.Exceeding(usage, target);
        var one = Assert.Single(r);
        Assert.Equal((UsageDimension.Leads, 150L, 100L), (one.Dimension, one.Used, one.Limit));
    }

    // ---- plan rules (domain)
    [Fact]
    public void A_plan_validates_money_currency_trial_and_custom_cycle()
    {
        Assert.Throws<DomainException>(() => NewPlan(-1));
        Assert.Throws<DomainException>(() => NewPlan(10.001m));
        Assert.Throws<DomainException>(() => NewPlan(10, cur: "usd1"));
        Assert.Throws<DomainException>(() => NewPlan(10, trial: true, days: 14));         // a trial is free
        Assert.Throws<DomainException>(() => NewPlan(0, trial: true));                    // a trial needs a duration
        Assert.Throws<DomainException>(() => NewPlan(10, PlanCycle.Custom));              // a custom cycle needs days
        Assert.Throws<DomainException>(() => Plan.Create("A", "x", null, PlanCycle.Monthly, null, 1, "USD", false, null, PlanVisibility.Public, 0, false, T0, null));   // code
        Assert.Equal("EUR", NewPlan(99.90m, cur: "eur").Currency);
    }

    [Fact]
    public void Period_ends_follow_the_cycle_and_the_trial_length()
    {
        Assert.Equal(T0.AddMonths(1), NewPlan(cycle: PlanCycle.Monthly).PeriodEnd(T0));
        Assert.Equal(T0.AddMonths(3), NewPlan(cycle: PlanCycle.Quarterly).PeriodEnd(T0));
        Assert.Equal(T0.AddYears(1), NewPlan(cycle: PlanCycle.Annual).PeriodEnd(T0));
        Assert.Equal(T0.AddDays(45), NewPlan(cycle: PlanCycle.Custom, custom: 45).PeriodEnd(T0));
        Assert.Equal(T0.AddDays(14), NewPlan(0, trial: true, days: 14).PeriodEnd(T0));
    }

    [Fact]
    public void A_priced_plan_cannot_start_a_subscription_without_a_payment_or_a_recorded_grant()
    {
        Assert.Throws<DomainException>(() => TenantSubscription.StartFreeOrTrial(Guid.NewGuid(), NewPlan(10), T0, null));
        var free = TenantSubscription.StartFreeOrTrial(Guid.NewGuid(), NewPlan(0), T0, null);
        Assert.Equal(SubscriptionState.Active, free.State);
        Assert.Throws<DomainException>(() => free.StartPaidPeriodWithVerifiedPayment(Guid.Empty, NewPlan(10), T0, T0.AddMonths(1), T0));
        Assert.Throws<DomainException>(() => free.RenewFree(NewPlan(10), T0, T0.AddMonths(1), T0));
        Assert.Throws<DomainException>(() => TenantSubscription.CreateGranted(Guid.NewGuid(), NewPlan(10), T0, T0.AddDays(5), " ", T0, null));   // a grant needs a reason
    }

    // ---- invoice rules (domain)
    [Fact]
    public void An_invoice_freezes_when_issued_and_totals_are_computed_once_with_tax()
    {
        var plan = NewPlan(100m);
        var inv = Invoice.CreateDraft(Guid.NewGuid(), InvoiceKind.Renewal, plan, T0, T0.AddMonths(1), "Acme", "VAT", 15m, null, null, null, null, T0, null);
        inv.AddLine("Plan", 1, 100m);
        inv.AddLine("Credit", 1, -20m);
        inv.Issue("INV-000001", T0, T0.AddDays(7));
        Assert.Equal(80m, inv.Subtotal); Assert.Equal(12m, inv.TaxAmount); Assert.Equal(92m, inv.Total);
        Assert.Throws<DomainException>(() => inv.AddLine("x", 1, 1m));
        Assert.Throws<DomainException>(() => inv.Issue("INV-000002", T0, T0));
        inv.MarkPaid(T0);
        Assert.Equal(InvoiceStatus.Paid, inv.Status);
        Assert.Throws<DomainException>(() => inv.Void("oops", T0));
        Assert.Throws<DomainException>(() => inv.MarkPaid(T0));
    }

    [Fact]
    public void A_credit_larger_than_the_charge_never_produces_a_negative_invoice()
    {
        var inv = Invoice.CreateDraft(Guid.NewGuid(), InvoiceKind.PlanChange, NewPlan(10m), T0, T0.AddMonths(1), "Acme", null, 0, null, null, null, null, T0, null);
        inv.AddLine("Plan", 1, 10m); inv.AddLine("Credit", 1, -50m);
        inv.Issue("INV-000009", T0, T0);
        Assert.Equal(0m, inv.Total);
    }

    [Fact]
    public void A_billing_payment_needs_evidence_a_verifier_and_a_sane_date()
    {
        var id = Guid.NewGuid();
        Assert.Throws<DomainException>(() => BillingPayment.Create(id, id, 10, "USD", LeadPilot.Domain.Conversion.PaymentMethod.BankTransfer, LeadPilot.Domain.Conversion.VerificationSource.Manual, "ab", Guid.NewGuid(), T0, null, T0));
        Assert.Throws<DomainException>(() => BillingPayment.Create(id, id, 10, "USD", LeadPilot.Domain.Conversion.PaymentMethod.BankTransfer, LeadPilot.Domain.Conversion.VerificationSource.Manual, "TRX-123", null, T0, null, T0));
        Assert.Throws<DomainException>(() => BillingPayment.Create(id, id, 10, "USD", LeadPilot.Domain.Conversion.PaymentMethod.BankTransfer, LeadPilot.Domain.Conversion.VerificationSource.Manual, "TRX-123", Guid.NewGuid(), T0.AddDays(1), null, T0));
        Assert.Throws<DomainException>(() => BillingPayment.Create(id, id, 0, "USD", LeadPilot.Domain.Conversion.PaymentMethod.BankTransfer, LeadPilot.Domain.Conversion.VerificationSource.Manual, "TRX-123", Guid.NewGuid(), T0, null, T0));
        Assert.Equal("TRX123", BillingPayment.Create(id, id, 10, "USD", LeadPilot.Domain.Conversion.PaymentMethod.BankTransfer, LeadPilot.Domain.Conversion.VerificationSource.Manual, "trx-123", Guid.NewGuid(), T0, null, T0).EvidenceKey);
    }

    [Fact]
    public void Error_messages_are_clear_and_typed()
    {
        var e = EntitlementErrors.Limit(UsageDimension.Leads, 100, 100, 1);
        Assert.Equal("plan_limit_exceeded", e.Code); Assert.Contains("100", e.Message); Assert.Contains("Upgrade", e.Message);
        Assert.Contains("not included", EntitlementErrors.Limit(UsageDimension.Campaigns, 0, 0, 1).Message);
        Assert.Contains("MB", EntitlementErrors.Limit(UsageDimension.KnowledgeStorageBytes, 5 * 1_048_576, 5 * 1_048_576, 100).Message);
        Assert.Equal("feature_not_in_plan", EntitlementErrors.Feature(Features.Ai).Code);
        Assert.Equal("email_not_verified", EntitlementErrors.Unverified().Code);
    }

    [Fact]
    public void Every_dimension_and_feature_has_catalog_metadata_and_every_default_has_a_value()
    {
        foreach (var d in Enum.GetValues<UsageDimension>()) Assert.Equal(d, DimensionCatalog.Get(d).Dimension);
        Assert.Equal(Enum.GetValues<UsageDimension>().Length, DimensionCatalog.All.Count);
        Assert.All(Features.All, f => Assert.True(Features.Exists(f.Key)));
        Assert.Equal("false", PlatformDefaults.Values[PlatformSettingKeys.RegistrationEnabled]);   // sign-up is closed until the owner opens it
    }
}
