using LeadPilot.Application.Billing;
using LeadPilot.Domain.Billing;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Billing;

public static class BillingServiceCollection
{
    public static IServiceCollection AddBilling(this IServiceCollection services)
    {
        services.AddScoped<IPlatformSettings, PlatformSettings>();
        services.AddScoped<UsageMeter>();
        services.AddScoped<IEntitlementService, EntitlementService>();
        services.AddScoped<InvoiceIssuer>();
        services.AddScoped<BillingPaymentVerifier>();
        services.AddScoped<IPlanService, PlanService>();
        services.AddScoped<IBillingService, BillingService>();
        services.AddScoped<IBillingLifecycleService, BillingLifecycleService>();
        services.AddScoped<PlatformDirectory>();
        services.AddScoped<IPlatformBillingService, PlatformBillingService>();
        services.AddScoped<BillingJob>();
        services.AddScoped<ICaptchaVerifier, NotConfiguredCaptchaVerifier>();
        services.AddScoped<IDisposableDomainPolicy, DefaultDisposableDomainPolicy>();
        services.AddScoped<IRegistrationService, RegistrationService>();
        services.AddScoped<IOnboardingService, OnboardingService>();
        return services;
    }
}

/// <summary>
/// Start-up seeding of the billing module (idempotent). It creates the invoice counter row and, ONLY when the platform has no plan at all, four clearly-labelled SAMPLE plans with zero prices that the platform
/// owner edits or archives; then it puts every existing tenant that has no subscription onto the default plan so nobody loses access. Registration stays switched off until the owner opens it.
/// </summary>
public static class BillingSeed
{
    public static async Task EnsureAsync(AppDbContext db, DateTime now, ILogger logger, CancellationToken ct)
    {

        if (!await db.Plans.AnyAsync(ct))
        {
            Plan P(string code, string name, string desc, PlanCycle cycle, bool trial, int? days, PlanVisibility vis, int order, long? users, long? accounts, long? msgs, long? tokens, long? leads, long? camps, long? mb, long? runs, long? calls, params string[] features)
            {
                var p = Plan.Create(code, name, desc, cycle, null, 0m, "USD", trial, days, vis, order, true, now, "seed");
                foreach (var (d, v) in new (UsageDimension, long?)[] { (UsageDimension.Users, users), (UsageDimension.MessagingAccounts, accounts), (UsageDimension.MonthlyMessages, msgs), (UsageDimension.MonthlyAiTokens, tokens),
                    (UsageDimension.Leads, leads), (UsageDimension.Campaigns, camps), (UsageDimension.KnowledgeStorageBytes, mb is null ? null : mb * 1_048_576), (UsageDimension.MonthlyAutomationRuns, runs), (UsageDimension.MonthlyDiscoveryCalls, calls) })
                    p.SetLimit(d, v);
                foreach (var f in Features.All) p.SetFeature(f.Key, features.Contains(f.Key));
                return p;
            }
            var all = Features.All.Select(f => f.Key).ToArray();
            var trial = P("sample-trial", "SAMPLE — Trial (placeholder, edit me)", "Sample trial plan created at install time. Edit its limits, or archive it.", PlanCycle.Monthly, true, 14, PlanVisibility.Public, 10, 3, 1, 200, 100_000, 200, 2, 25, 200, 100, Features.Ai, Features.Campaigns, Features.Workflows);
            var starter = P("sample-starter", "SAMPLE — Starter (placeholder price 0)", "Sample plan with a placeholder price of zero. Set the real price and limits.", PlanCycle.Monthly, false, null, PlanVisibility.Public, 20, 5, 1, 2_000, 1_000_000, 2_000, 5, 100, 1_000, 500, Features.Ai, Features.Campaigns, Features.Workflows, Features.Discovery);
            var growth = P("sample-growth", "SAMPLE — Growth (placeholder price 0)", "Sample plan with a placeholder price of zero. Set the real price and limits.", PlanCycle.Monthly, false, null, PlanVisibility.Public, 30, 20, 5, 20_000, 10_000_000, 20_000, 50, 1_000, 10_000, 5_000, all);
            var legacy = P("sample-existing-workspaces", "SAMPLE — Existing workspaces (unlimited, private)", "Where workspaces that existed before billing were placed so they keep full access. Replace with real plans, then move them.", PlanCycle.Monthly, false, null, PlanVisibility.Private, 90, null, null, null, null, null, null, null, null, null, all);
            db.Plans.AddRange(trial, starter, growth, legacy);
            db.PlatformSettings.Add(PlatformSetting.Create(PlatformSettingKeys.DefaultPlanId, legacy.Id.ToString(), now, "seed"));
            db.PlatformSettings.Add(PlatformSetting.Create(PlatformSettingKeys.RegistrationPlanId, trial.Id.ToString(), now, "seed"));
            logger.LogInformation("Sample plans created (zero prices): the platform owner should edit them");
        }
        await db.SaveChangesAsync(ct);

        var plan = await SubscriptionProvisioner.DefaultPlanAsync(db, ct);
        if (plan is null) return;
        var have = (await db.TenantSubscriptions.IgnoreQueryFilters().Select(s => s.TenantId).ToListAsync(ct)).ToHashSet();
        var n = 0;
        foreach (var t in await db.Tenants.Where(t => !t.IsPlatform).ToListAsync(ct))
        {
            if (have.Contains(t.Id)) continue;
            db.TenantSubscriptions.Add(SubscriptionProvisioner.Create(t.Id, plan, now, "Existing workspace placed on the default plan when billing was introduced", "seed"));
            db.SubscriptionEvents.Add(SubscriptionEvent.Create(t.Id, "Started", $"Existing workspace placed on plan '{plan.Name}'.", null, plan.Id, "seed", now));
            n++;
        }
        if (n > 0) { await db.SaveChangesAsync(ct); logger.LogInformation("{Count} existing tenants were placed on the default plan", n); }
    }
}
