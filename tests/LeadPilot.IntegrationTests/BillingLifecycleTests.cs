using System.Net;
using System.Text.RegularExpressions;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Billing;
using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Billing;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Billing;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

[Collection("app")]
public class BillingLifecycleTests(AppFixture app)
{
    /// <summary>A tenant on a priced plan whose period ends in <paramref name="endsInDays"/> days (granted by the platform: the first period is a comp, the renewal is billed).</summary>
    private async Task<(BillingWorld W, PlatformPlanDto Plan)> PricedAsync(decimal price = 50m, int endsInDays = 3, Dictionary<string, long?>? limits = null)
    {
        var w = await BillingWorld.OpenAsync(app);
        var plan = await w.PlanAsync(price: price, limits: limits);
        await w.GrantAsync(plan, until: w.Now.AddDays(endsInDays));
        return (w, plan);
    }

    private static async Task<InvoiceDto> OnlyOpenInvoiceAsync(BillingWorld w) => (await w.InvoicesAsync()).Single(i => i.Status == InvoiceStatus.Issued);

    [RequiresMySqlFact]
    public async Task The_renewal_job_issues_one_invoice_per_period_however_often_it_runs()
    {
        var (w, plan) = await PricedAsync(); await using var _w = w;
        Assert.Equal(LifecycleOutcome.RenewalInvoiceIssued, await w.LifecycleAsync());
        Assert.Equal(LifecycleOutcome.NothingToDo, await w.LifecycleAsync());
        await Task.WhenAll(Enumerable.Range(0, 5).Select(async _ => { try { await w.LifecycleAsync(); } catch (DbUpdateException) { } }));   // overlapping runs
        var invoices = await w.InvoicesAsync();
        var inv = Assert.Single(invoices);
        Assert.Equal((InvoiceKind.Renewal, InvoiceStatus.Issued, 50m, "USD"), (inv.Kind, inv.Status, inv.Total, inv.Currency));
        var sub = await w.SubscriptionAsync();
        Assert.Equal(sub.PeriodEndUtc, inv.PeriodStartUtc);                          // the invoice pays for the NEXT period
        Assert.Equal(plan.Name, inv.PlanName);
        Assert.StartsWith("INV-", inv.Number);
        Assert.Equal(inv.Subtotal, inv.Lines.Sum(l => l.Amount));
        // the hourly Hangfire job does the same for every tenant in need and is just as idempotent
        await w.RunJobAsync();
        Assert.Single(await w.InvoicesAsync());
    }

    [RequiresMySqlFact]
    public async Task Only_a_verified_payment_with_the_exact_amount_and_a_fresh_receipt_can_renew()
    {
        var (w, _) = await PricedAsync(); var (b, _) = await PricedAsync();
        await using var _w = w; await using var _b = b;
        await w.LifecycleAsync(); await b.LifecycleAsync();
        var inv = await OnlyOpenInvoiceAsync(w); var invB = await OnlyOpenInvoiceAsync(b);
        var endBefore = (await w.SubscriptionAsync()).PeriodEndUtc;

        // "I paid" is not payment
        var report = await w.Admin.Post($"/api/v1/billing/invoices/{inv.Id}/payment-report", new { evidenceReference = "MY-TRANSFER-1", note = "paid today" });
        Assert.True((await ApiSession.Json<InvoiceDto>(report)).HasOpenReport);
        Assert.Equal(InvoiceStatus.Issued, (await OnlyOpenInvoiceAsync(w)).Status);
        Assert.Equal(endBefore, (await w.SubscriptionAsync()).PeriodEndUtc);
        var queue = await ApiSession.Json<PagedResult<QueueItem>>(await w.Owner.Get("/api/v1/platform/payments/queue?pageSize=100"));
        Assert.Equal("MY-TRANSFER-1", queue.Items.Single(q => q.InvoiceId == inv.Id).ReportedEvidence);

        // a workspace role cannot verify, not even its own invoice; neither can an unauthenticated caller
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Admin.Post($"/api/v1/platform/tenants/{w.TenantId}/invoices/{inv.Id}/verify", new { amount = inv.Total, currency = "USD", method = "BankTransfer", evidenceReference = "FORGED-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ApiSession.Anonymous(app).Post($"/api/v1/platform/tenants/{w.TenantId}/invoices/{inv.Id}/verify", new { amount = inv.Total })).StatusCode);

        // wrong amount, wrong currency, no real evidence, the provider method (no provider is installed): all refused, nothing changes
        Assert.Equal(HttpStatusCode.BadRequest, (await w.VerifyRawAsync(inv, amount: inv.Total - 0.01m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.VerifyRawAsync(inv, currency: "EUR")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.VerifyRawAsync(inv, evidence: "ab")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.Post($"/api/v1/platform/tenants/{w.TenantId}/invoices/{inv.Id}/verify", new { amount = inv.Total, currency = "USD", method = "Provider", evidenceReference = "PROV-123" })).StatusCode);
        // another tenant's invoice id under this tenant's route does not exist
        Assert.Equal(HttpStatusCode.NotFound, (await w.VerifyRawAsync(invB)).StatusCode);
        Assert.Equal(InvoiceStatus.Issued, (await OnlyOpenInvoiceAsync(w)).Status);
        Assert.Equal(0, (await w.WithDb(db => db.BillingPayments.IgnoreQueryFilters().CountAsync(p => p.TenantId == w.TenantId))));

        var paid = await w.VerifyAsync(inv, evidence: "BANK-REF-0001");
        Assert.Equal(InvoiceStatus.Paid, paid.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await w.VerifyRawAsync(inv)).StatusCode);                              // the same invoice cannot be paid twice
        Assert.Equal(HttpStatusCode.Conflict, (await b.VerifyRawAsync(invB, evidence: "bank-ref-0001")).StatusCode);   // one receipt proves one payment (normalised)
        Assert.Single(await w.InvoicesAsync(), i => i.Status == InvoiceStatus.Paid);
        Assert.Equal(1, (await w.WithDb(db => db.BillingPayments.IgnoreQueryFilters().CountAsync(p => p.TenantId == w.TenantId))));
        Assert.Equal(InvoiceStatus.Issued, (await OnlyOpenInvoiceAsync(b)).Status);
        // two operators racing the same invoice: exactly one wins
        var race = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => b.VerifyRawAsync(invB)));
        Assert.Equal(1, race.Count(r => r.IsSuccessStatusCode)); Assert.Equal(4, race.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        // the payment shows up in the tenant's history
        var payments = await ApiSession.Json<List<BillingPaymentDto>>(await w.Admin.Get("/api/v1/billing/payments"));
        Assert.Equal("BANK-REF-0001", payments.Single().EvidenceReference);
    }

    [RequiresMySqlFact]
    public async Task A_renewal_paid_in_advance_rolls_in_at_the_period_end_and_usage_starts_again()
    {
        var (w, plan) = await PricedAsync(limits: new() { ["MonthlyAiTokens"] = 1000 }); await using var _w = w;
        await w.LifecycleAsync();
        var inv = await OnlyOpenInvoiceAsync(w);
        await w.WithDb(async db => { db.AiUsageEntries.Add(AiUsageEntry.Create(w.TenantId, "chat", "claude", "m", 500, 400, 0, 0, 0m, true, null, 5, null, w.Now)); await db.SaveChangesAsync(); return 0; });
        Assert.Equal(900, (await w.UsageAsync(UsageDimension.MonthlyAiTokens)).Used);
        var before = await w.SubscriptionAsync();
        await w.VerifyAsync(inv);
        Assert.Equal(before.PeriodEndUtc, (await w.SubscriptionAsync()).PeriodEndUtc);         // paid early: nothing moves yet
        Assert.Equal(900, (await w.UsageAsync(UsageDimension.MonthlyAiTokens)).Used);

        app.Clock.Offset = TimeSpan.FromDays(4);                                                  // the period is over
        await w.LifecycleAsync();
        var after = await w.SubscriptionAsync();
        Assert.Equal((SubscriptionState.Active, inv.PeriodStartUtc, inv.PeriodEndUtc), (after.State, after.PeriodStartUtc, after.PeriodEndUtc));
        Assert.Equal(0, (await w.UsageAsync(UsageDimension.MonthlyAiTokens)).Used);              // period rollover: the window moved, the old usage no longer counts
        Assert.Equal(plan.Name, after.PlanName);
    }

    [RequiresMySqlFact]
    public async Task An_unpaid_subscription_gets_a_grace_period_then_is_suspended_everywhere_and_a_verified_payment_brings_it_back()
    {
        var (w, _) = await PricedAsync(price: 20m, endsInDays: 1); await using var _w = w;
        app.Clock.Offset = TimeSpan.FromDays(2);                                                  // past the period end
        Assert.Equal(LifecycleOutcome.MovedToGrace, await w.LifecycleAsync());
        var sub = await w.SubscriptionAsync();
        Assert.Equal(SubscriptionState.PastDue, sub.State);
        Assert.Equal(sub.PeriodEndUtc.AddDays(7), sub.GraceEndsAtUtc);                            // the configured grace period
        Assert.Equal(HttpStatusCode.OK, (await w.Admin.Get("/api/v1/billing")).StatusCode);       // still working during the grace period
        Assert.Equal(LifecycleOutcome.NothingToDo, await w.LifecycleAsync());

        app.Clock.Offset = TimeSpan.FromDays(9);                                                  // grace is over
        Assert.Equal(LifecycleOutcome.Suspended, await w.LifecycleAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Admin.Get("/api/v1/billing")).StatusCode);      // API
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Admin.Get("/api/v1/leads")).StatusCode);
        await Assert.ThrowsAsync<TenantUnavailableException>(() => app.World.Services.GetRequiredService<ITenantScopeRunner>().RunAsync(w.TenantId, (_, _) => Task.CompletedTask));   // background jobs
        await w.RunJobAsync();                                                                    // the platform job skips the suspended tenant instead of failing
        var login = await app.Api!.CreateClient().PostAsJsonAsyncCompat("/api/v1/auth/login", new { email = w.AdminUser.Email, password = w.AdminUser.Password });
        Assert.NotEqual(HttpStatusCode.OK, login.StatusCode);                                     // sign-in
        var detail = await ApiSession.Json<PlatformTenantDetail>(await w.Owner.Get($"/api/v1/platform/tenants/{w.TenantId}"));
        Assert.Equal((TenantStatus.Suspended, SuspensionKind.NonPayment), (detail.Row.Status, detail.Row.SuspensionKind));

        var paid = await w.VerifyAsync(detail.Invoices.Single(i => i.Status == InvoiceStatus.Issued));
        Assert.Equal(InvoiceStatus.Paid, paid.Status);
        Assert.Equal(HttpStatusCode.OK, (await w.Admin.Get("/api/v1/billing")).StatusCode);        // reactivated by the verified payment
        Assert.Equal(SubscriptionState.Active, (await w.SubscriptionAsync()).State);
    }

    [RequiresMySqlFact]
    public async Task A_payment_does_not_lift_a_manual_or_abuse_suspension_and_an_operator_can_reactivate()
    {
        var (w, _) = await PricedAsync(price: 20m, endsInDays: 1); await using var _w = w;
        app.Clock.Offset = TimeSpan.FromDays(2);
        await w.LifecycleAsync();
        var inv = await OnlyOpenInvoiceAsync(w);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.Post($"/api/v1/platform/tenants/{w.TenantId}/suspend", new { reason = " " })).StatusCode);
        Assert.True((await w.Owner.Post($"/api/v1/platform/tenants/{w.TenantId}/suspend", new { reason = "spam complaints", kind = "Abuse" })).IsSuccessStatusCode);
        await w.VerifyAsync(inv);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.Admin.Get("/api/v1/billing")).StatusCode);   // still suspended: abuse needs a person
        Assert.True((await w.Owner.Post($"/api/v1/platform/tenants/{w.TenantId}/reactivate")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await w.Admin.Get("/api/v1/billing")).StatusCode);
        var audit = await ApiSession.Json<PagedResult<PlatformAuditRow>>(await w.Owner.Get("/api/v1/platform/audit?pageSize=100"));
        Assert.Contains(audit.Items, a => a.Action == "Tenant.Suspended" && a.TenantId == w.TenantId);
        Assert.Contains(audit.Items, a => a.Action == "Platform.Payment.Verified" && a.TenantId == w.TenantId);
    }

    [RequiresMySqlFact]
    public async Task A_trial_is_never_renewed_and_a_free_plan_renews_itself()
    {
        await using var w = await BillingWorld.OpenAsync(app);
        var trial = await w.PlanAsync(trial: true, trialDays: 14);
        await w.GrantAsync(trial);
        Assert.Equal(SubscriptionState.Trialing, (await w.SubscriptionAsync()).State);
        app.Clock.Offset = TimeSpan.FromDays(15);
        Assert.Equal(LifecycleOutcome.MovedToGrace, await w.LifecycleAsync());
        Assert.Equal(SubscriptionState.PastDue, (await w.SubscriptionAsync()).State);
        Assert.Empty(await w.InvoicesAsync());
        app.Clock.Offset = TimeSpan.Zero;

        await using var f = await BillingWorld.OpenAsync(app);
        await f.GrantAsync(await f.PlanAsync(price: 0m), until: f.Now.AddHours(1));
        app.Clock.Offset = TimeSpan.FromHours(2);
        Assert.Equal(LifecycleOutcome.FreeRenewed, await f.LifecycleAsync());
        var sub = await f.SubscriptionAsync();
        Assert.Equal(SubscriptionState.Active, sub.State); Assert.True(sub.PeriodEndUtc > f.Now);
        Assert.Empty(await f.InvoicesAsync());
    }

    [RequiresMySqlFact]
    public async Task An_upgrade_is_invoiced_with_credit_and_only_changes_the_plan_when_the_payment_is_verified()
    {
        await using var w = await BillingWorld.OpenAsync(app);
        var s = await w.PlanAsync(price: 10m, name: "Small"); var l = await w.PlanAsync(price: 30m, name: "Large"); var xl = await w.PlanAsync(price: 60m, name: "Huge");
        var priv = await w.PlanAsync(price: 99m, visibility: "Private");
        await w.GrantAsync(s);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Admin.Post("/api/v1/billing/plan-changes", new { planId = priv.Id })).StatusCode);   // private plans are not offered
        Assert.Equal(HttpStatusCode.Forbidden, (await (await ApiSession.LoginAsync(app, w.ReadOnlyUser.Email, w.ReadOnlyUser.Password)).Post("/api/v1/billing/plan-changes", new { planId = l.Id })).StatusCode);

        var preview = await ApiSession.Json<PlanChangePreview>(await w.Admin.Get($"/api/v1/billing/plans/{l.Id}/preview"));
        Assert.Equal((PlanChangeKind.Upgrade, 30m, 0m, 30m), (preview.Kind, preview.NewPrice, preview.Credit, preview.Total));   // the granted period was never paid: no credit
        var req = await ApiSession.Json<PlanChangeDto>(await w.Admin.Post("/api/v1/billing/plan-changes", new { planId = l.Id }));
        Assert.Equal(PlanChangeStatus.AwaitingPayment, req.Status);
        Assert.Equal("Small", (await w.SubscriptionAsync()).PlanName);                               // requesting changes nothing
        Assert.Equal(HttpStatusCode.Conflict, (await w.Admin.Post("/api/v1/billing/plan-changes", new { planId = xl.Id })).StatusCode);   // one open change at a time
        var inv = await OnlyOpenInvoiceAsync(w);
        Assert.Equal((InvoiceKind.PlanChange, 30m), (inv.Kind, inv.Total));
        await w.VerifyAsync(inv);
        Assert.Equal("Large", (await w.SubscriptionAsync()).PlanName);

        app.Clock.Offset = TimeSpan.FromDays(10);                                                    // 10 days into a paid month
        var up = await ApiSession.Json<PlanChangeDto>(await w.Admin.Post("/api/v1/billing/plan-changes", new { planId = xl.Id }));
        var inv2 = await OnlyOpenInvoiceAsync(w);
        Assert.InRange(up.CreditAmount, 19m, 21m);                                                   // unused ~20 of 30 days of 30.00
        Assert.InRange(inv2.Total, 39m, 41m);
        Assert.Contains(inv2.Lines, x => x.Amount < 0 && x.Description.Contains("Credit"));
        // cancelling voids the invoice
        Assert.True((await w.Admin.Delete($"/api/v1/billing/plan-changes/{up.Id}")).IsSuccessStatusCode);
        Assert.Equal(InvoiceStatus.Void, (await w.InvoicesAsync()).Single(i => i.Id == inv2.Id).Status);
        Assert.Equal("Large", (await w.SubscriptionAsync()).PlanName);
    }

    [RequiresMySqlFact]
    public async Task A_downgrade_is_blocked_with_a_report_while_usage_exceeds_the_target_and_is_scheduled_when_it_fits()
    {
        await using var w = await BillingWorld.OpenAsync(app);
        var big = await w.PlanAsync(price: 30m, limits: new() { ["Leads"] = 100 });
        var small = await w.PlanAsync(price: 10m, limits: new() { ["Leads"] = 2 });
        await w.GrantAsync(big, until: w.Now.AddDays(20));
        var leads = new List<LeadDto>();
        for (var i = 0; i < 3; i++) leads.Add(await Crm.CreateLead(w.Admin));

        var preview = await ApiSession.Json<PlanChangePreview>(await w.Admin.Get($"/api/v1/billing/plans/{small.Id}/preview"));
        Assert.False(preview.Allowed);
        var ex = Assert.Single(preview.Exceeds);
        Assert.Equal((UsageDimension.Leads, 3L, 2L), (ex.Dimension, ex.Used, ex.Limit));
        var blocked = await w.Admin.Post("/api/v1/billing/plan-changes", new { planId = small.Id });
        var err = await BillingAssert.ErrorAsync(blocked, HttpStatusCode.Conflict, "conflict");
        Assert.Contains("Leads", err.Detail);

        foreach (var lead in leads.Take(2)) Assert.True((await w.Admin.Delete($"/api/v1/leads/{lead.Id}")).IsSuccessStatusCode);   // archived leads do not count
        var scheduled = await ApiSession.Json<PlanChangeDto>(await w.Admin.Post("/api/v1/billing/plan-changes", new { planId = small.Id }));
        Assert.Equal(PlanChangeStatus.Scheduled, scheduled.Status);
        var sub = await w.SubscriptionAsync();
        Assert.Equal(big.Name, sub.PlanName); Assert.NotNull(sub.PendingPlanId);                // still on the big plan until the period ends
        Assert.Equal(sub.PeriodEndUtc, scheduled.EffectiveAtUtc);

        // at renewal the invoice is for the NEW plan, and paying it moves the tenant down
        app.Clock.Offset = TimeSpan.FromDays(15);                                             // inside the 7-day renewal lead
        await w.LifecycleAsync();
        var inv = await OnlyOpenInvoiceAsync(w);
        Assert.Equal(10m, inv.Total);
        await w.VerifyAsync(inv);
        app.Clock.Offset = TimeSpan.FromDays(21);
        await w.LifecycleAsync();
        var after = await w.SubscriptionAsync();
        Assert.Equal(small.Id, after.PlanId); Assert.Null(after.PendingPlanId);
        Assert.Equal(2, (await w.UsageAsync(UsageDimension.Leads)).Limit);
    }

    [RequiresMySqlFact]
    public async Task A_scheduled_downgrade_is_cancelled_if_usage_grew_past_the_target_before_it_took_effect()
    {
        await using var w = await BillingWorld.OpenAsync(app);
        var big = await w.PlanAsync(price: 30m, limits: new() { ["Leads"] = 100 });
        var small = await w.PlanAsync(price: 10m, limits: new() { ["Leads"] = 2 });
        await w.GrantAsync(big, until: w.Now.AddDays(3));
        await Crm.CreateLead(w.Admin);
        var scheduled = await ApiSession.Json<PlanChangeDto>(await w.Admin.Post("/api/v1/billing/plan-changes", new { planId = small.Id }));
        await Crm.CreateLead(w.Admin); await Crm.CreateLead(w.Admin);                       // now 3 leads > 2
        Assert.Equal(LifecycleOutcome.PendingDowngradeCancelled, await w.LifecycleAsync());
        var inv = await OnlyOpenInvoiceAsync(w);
        Assert.Equal(30m, inv.Total);                                                        // renews on the CURRENT plan
        Assert.Null((await w.SubscriptionAsync()).PendingPlanId);
        var changes = await ApiSession.Json<List<PlanChangeDto>>(await w.Admin.Get("/api/v1/billing/plan-changes"));
        Assert.Equal(PlanChangeStatus.Cancelled, changes.Single(c => c.Id == scheduled.Id).Status);
    }

    [RequiresMySqlFact]
    public async Task Invoice_numbers_are_unique_and_gapless_under_concurrent_issuing()
    {
        await using var w = await BillingWorld.OpenAsync(app);
        var plan = await w.PlanAsync(price: 5m);
        await w.GrantAsync(plan);
        var start = w.Now;
        var numbers = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
        {
            using var scope = app.World.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(w.TenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = await db.Plans.AsNoTracking().FirstAsync(x => x.Id == plan.Id);
            var inv = await scope.ServiceProvider.GetRequiredService<InvoiceIssuer>().CreateAndIssueAsync(p, InvoiceKind.Renewal, start.AddDays(i), start.AddDays(i + 30), [new("Test", 1, 5m)], $"race:{Guid.NewGuid():N}", null, CancellationToken.None);
            return long.Parse(inv.Number!.Replace("INV-", ""));
        }));
        Assert.Equal(12, numbers.Distinct().Count());
        var sorted = numbers.Order().ToList();
        Assert.All(sorted.Zip(sorted.Skip(1)), p => Assert.Equal(p.First + 1, p.Second));    // no gaps: a number is only used by a saved invoice
    }

    [RequiresMySqlFact]
    public async Task An_issued_invoice_cannot_be_changed_or_deleted_even_through_the_database_guard()
    {
        var (w, _) = await PricedAsync(); await using var _w = w;
        await w.LifecycleAsync();
        var inv = await OnlyOpenInvoiceAsync(w);
        async Task Attempt(Action<AppDbContext, Invoice> change, bool lines = false)
        {
            using var scope = app.World.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(w.TenantId);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = lines ? await db.Invoices.Include(i => i.Lines).FirstAsync(i => i.Id == inv.Id) : await db.Invoices.FirstAsync(i => i.Id == inv.Id);
            change(db, row);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        await Attempt((_, i) => typeof(Invoice).GetProperty(nameof(Invoice.Total))!.SetValue(i, 1m));
        await Attempt((_, i) => typeof(Invoice).GetProperty(nameof(Invoice.Number))!.SetValue(i, "INV-999999"));
        await Attempt((_, i) => typeof(Invoice).GetProperty(nameof(Invoice.BillToName))!.SetValue(i, "Someone else"));
        await Attempt((_, i) => typeof(Invoice).GetProperty(nameof(Invoice.TenantId))!.SetValue(i, Guid.NewGuid()));
        await Attempt((_, i) => typeof(InvoiceLine).GetProperty(nameof(InvoiceLine.Amount))!.SetValue(i.Lines[0], 0m), lines: true);
        await Attempt((db, i) => db.Invoices.Remove(i));
        var again = (await w.InvoicesAsync()).Single(i => i.Id == inv.Id);
        Assert.Equal((inv.Total, inv.Number, inv.Lines.Count), (again.Total, again.Number, again.Lines.Count));
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.Post($"/api/v1/platform/tenants/{w.TenantId}/invoices/{inv.Id}/void", new { reason = " " })).StatusCode);
        var voided = await ApiSession.Json<InvoiceDto>(await w.Owner.Post($"/api/v1/platform/tenants/{w.TenantId}/invoices/{inv.Id}/void", new { reason = "issued in error" }));
        Assert.Equal(InvoiceStatus.Void, voided.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await w.VerifyRawAsync(inv)).StatusCode);       // a void invoice cannot be paid
    }

    [RequiresMySqlFact]
    public async Task Invoices_use_the_configured_issuer_and_tax_and_render_a_printable_page()
    {
        await using var w = await BillingWorld.OpenAsync(app);
        var settings = await ApiSession.Json<PlatformSettingsDto>(await w.Owner.Get("/api/v1/platform/settings"));
        try
        {
            var put = await w.Owner.Put("/api/v1/platform/settings", settings with { TaxName = "VAT", TaxRatePercent = 15m, IssuerText = "Test Issuer Ltd\n1 Test Street", PaymentInstructions = "IBAN TEST123", InvoicePrefix = "TST-" });
            Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
            var plan = await w.PlanAsync(price: 100m);
            await w.GrantAsync(plan, until: w.Now.AddDays(2));
            await w.LifecycleAsync();
            var inv = await OnlyOpenInvoiceAsync(w);
            Assert.Equal((100m, 15m, 115m, "VAT"), (inv.Subtotal, inv.TaxAmount, inv.Total, inv.TaxName));
            Assert.StartsWith("TST-", inv.Number);
            var web = await WebSession.LoginAsync(app, w.AdminUser.Email, w.AdminUser.Password);
            var page = await web.Html($"/Billing/Print/{inv.Id}");
            Assert.Contains(inv.Number!, page); Assert.Contains("Test Issuer Ltd", page); Assert.Contains("IBAN TEST123", page); Assert.Contains("115.00", page);
            Assert.Contains("window.print", page);
        }
        finally { await w.Owner.Put("/api/v1/platform/settings", settings); }
    }

    [RequiresMySqlFact]
    public async Task Platform_settings_are_validated_and_every_change_is_audited()
    {
        await using var w = await BillingWorld.OpenAsync(app);
        var s = await ApiSession.Json<PlatformSettingsDto>(await w.Owner.Get("/api/v1/platform/settings"));
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.Put("/api/v1/platform/settings", s with { GraceDays = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.Put("/api/v1/platform/settings", s with { TaxRatePercent = 120m })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.Put("/api/v1/platform/settings", s with { RegistrationEnabled = true, RegistrationPlanId = null })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Owner.Put("/api/v1/platform/settings", s with { DefaultPlanId = Guid.NewGuid() })).StatusCode);
        var ok = await w.Owner.Put("/api/v1/platform/settings", s with { GraceDays = 9 });
        Assert.Equal(9, (await ApiSession.Json<PlatformSettingsDto>(ok)).GraceDays);
        try
        {
            var audit = await ApiSession.Json<PagedResult<PlatformAuditRow>>(await w.Owner.Get("/api/v1/platform/audit?action=Platform.Settings"));
            Assert.Contains(audit.Items, a => a.Summary.Contains(PlatformSettingKeys.GraceDays));
        }
        finally { await w.Owner.Put("/api/v1/platform/settings", s); }
    }
}

internal static class HttpCompat
{
    public static Task<HttpResponseMessage> PostAsJsonAsyncCompat(this HttpClient c, string url, object body) => System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(c, url, body);
}
