using LeadPilot.Domain.Billing;
using LeadPilot.Shared.Results;

namespace LeadPilot.Application.Billing;

/// <summary>A feature a plan can include. The platform never switches on a plan NAME: it only asks whether the tenant's effective entitlements include one of these keys.</summary>
public sealed record FeatureInfo(string Key, string Label, string Description);

public static class Features
{
    public const string Discovery = "discovery", Campaigns = "campaigns", Workflows = "workflows", Ai = "ai", ReportsExport = "reports_export";

    public static readonly IReadOnlyList<FeatureInfo> All =
    [
        new(Discovery, "Prospect discovery", "Run discovery searches (Google Places, public websites)."),
        new(Campaigns, "Campaigns", "Create and run bulk messaging campaigns."),
        new(Workflows, "Workflow automation", "Activate and run automation workflows."),
        new(Ai, "AI assistant", "AI replies, qualification and knowledge-base retrieval."),
        new(ReportsExport, "Report export", "Download reports as CSV."),
    ];

    public static bool Exists(string key) => All.Any(f => f.Key == key);
}

/// <summary>
/// How a dimension is measured. Stock dimensions count what exists now (users, accounts, leads, campaigns, storage); period dimensions count what happened in the subscription period (messages, AI tokens, automation runs,
/// discovery calls). <see cref="UiFactor"/> converts the unit shown in the UI to the stored value (storage is stored in bytes, entered in MB).
/// </summary>
public sealed record DimensionInfo(UsageDimension Dimension, string Label, string Unit, bool IsPeriodic, string Source, long UiFactor = 1);

public static class DimensionCatalog
{
    public static readonly IReadOnlyList<DimensionInfo> All =
    [
        new(UsageDimension.Users, "Users", "users", false, "Active workspace members plus pending invitations."),
        new(UsageDimension.MessagingAccounts, "Connected messaging accounts", "accounts", false, "WhatsApp accounts that are not removed."),
        new(UsageDimension.MonthlyMessages, "Messages per period", "messages", true, "Outbound messages created in the period (failed ones do not count)."),
        new(UsageDimension.MonthlyAiTokens, "AI tokens per period", "tokens", true, "Input + output tokens recorded in the AI usage ledger in the period."),
        new(UsageDimension.Leads, "Leads", "leads", false, "Leads that are not archived."),
        new(UsageDimension.Campaigns, "Active campaigns", "campaigns", false, "Campaigns that are not completed, cancelled or failed."),
        new(UsageDimension.KnowledgeStorageBytes, "Knowledge-base storage", "MB", false, "Size of the stored knowledge documents (current versions).", 1_048_576),
        new(UsageDimension.MonthlyAutomationRuns, "Automation runs per period", "runs", true, "Workflow executions started in the period."),
        new(UsageDimension.MonthlyDiscoveryCalls, "Discovery calls per period", "calls", true, "Provider calls recorded in the discovery usage ledger in the period."),
    ];

    public static DimensionInfo Get(UsageDimension d) => All.First(x => x.Dimension == d);
}

/// <summary>Platform setting keys. Values live in the PlatformSettings table; defaults are in <see cref="PlatformDefaults"/>.</summary>
public static class PlatformSettingKeys
{
    public const string RegistrationEnabled = "registration.enabled";
    public const string RegistrationPlanId = "registration.plan_id";
    public const string DefaultPlanId = "billing.default_plan_id";
    public const string GraceDays = "billing.grace_days";
    public const string RenewalLeadDays = "billing.renewal_lead_days";
    public const string InvoiceDueDays = "billing.invoice_due_days";
    public const string TaxName = "billing.tax_name";
    public const string TaxRatePercent = "billing.tax_rate_percent";
    public const string IssuerText = "billing.issuer_text";
    public const string PaymentInstructions = "billing.payment_instructions";
    public const string InvoicePrefix = "billing.invoice_prefix";
    public const string MaxRegistrationsPerIpPerHour = "registration.max_per_ip_per_hour";
    public const string MaxRegistrationsPerDomainPerDay = "registration.max_per_domain_per_day";
    public const string ExtraBlockedDomains = "registration.blocked_domains";
    public const string CaptchaRequired = "registration.captcha_required";
}

public static class PlatformDefaults
{
    public static readonly IReadOnlyDictionary<string, string> Values = new Dictionary<string, string>
    {
        [PlatformSettingKeys.RegistrationEnabled] = "false",
        [PlatformSettingKeys.GraceDays] = "7",
        [PlatformSettingKeys.RenewalLeadDays] = "7",
        [PlatformSettingKeys.InvoiceDueDays] = "7",
        [PlatformSettingKeys.TaxRatePercent] = "0",
        [PlatformSettingKeys.InvoicePrefix] = "INV-",
        [PlatformSettingKeys.MaxRegistrationsPerIpPerHour] = "3",
        [PlatformSettingKeys.MaxRegistrationsPerDomainPerDay] = "20",
        [PlatformSettingKeys.CaptchaRequired] = "false",
    };
}

public enum LimitSource { Plan = 1, Override = 2, NoSubscription = 3 }

/// <summary>The limit that applies to one dimension right now, and where it comes from. <c>Limit == null</c> means unlimited.</summary>
public sealed record EffectiveLimit(UsageDimension Dimension, long? Limit, LimitSource Source, string? OverrideReason = null, DateTime? OverrideExpiresAtUtc = null);

public sealed record OverrideView(string Key, OverrideKind Kind, long? LimitValue, bool Unlimited, bool? FeatureEnabled, DateTime? ExpiresAtUtc, string Reason);

/// <summary>Pure entitlement evaluation: override (while active) beats plan; a plan row without a value means unlimited; a dimension the plan does not mention is unlimited.</summary>
public static class EntitlementEvaluator
{
    public static EffectiveLimit Limit(UsageDimension d, IReadOnlyDictionary<UsageDimension, long?> planLimits, IEnumerable<OverrideView> overrides, DateTime nowUtc)
    {
        var o = overrides.FirstOrDefault(x => x.Kind == OverrideKind.Limit && x.Key == d.ToString() && (x.ExpiresAtUtc is null || x.ExpiresAtUtc > nowUtc));
        if (o is not null) return new EffectiveLimit(d, o.Unlimited ? null : o.LimitValue, LimitSource.Override, o.Reason, o.ExpiresAtUtc);
        return new EffectiveLimit(d, planLimits.TryGetValue(d, out var v) ? v : null, LimitSource.Plan);
    }

    public static bool Feature(string key, IReadOnlySet<string> planFeatures, IEnumerable<OverrideView> overrides, DateTime nowUtc)
    {
        var o = overrides.FirstOrDefault(x => x.Kind == OverrideKind.Feature && x.Key == key && (x.ExpiresAtUtc is null || x.ExpiresAtUtc > nowUtc));
        return o is not null ? o.FeatureEnabled == true : planFeatures.Contains(key);
    }

    /// <summary>True when <paramref name="used"/> plus <paramref name="requested"/> would pass the limit. Unlimited never blocks.</summary>
    public static bool Allows(long? limit, long used, long requested) => limit is null || used + requested <= limit.Value;
}

/// <summary>Where usage is counted for the dimensions that are measured per period: the subscription period (UTC instants). Tenant time zones only matter for DISPLAY: the period boundaries are stored and compared in UTC.</summary>
public static class UsageWindow
{
    public static (DateTime Start, DateTime End) For(DateTime periodStartUtc, DateTime periodEndUtc, DateTime nowUtc) =>
        nowUtc >= periodEndUtc ? (periodStartUtc, nowUtc.AddTicks(1)) : (periodStartUtc, periodEndUtc);
}

/// <summary>Unused-time credit when a paid plan is replaced mid-period: price × remaining / total, rounded to two decimals, never negative, only within the same currency.</summary>
public static class Proration
{
    public static decimal Credit(decimal oldPrice, DateTime periodStartUtc, DateTime periodEndUtc, DateTime nowUtc)
    {
        var total = (periodEndUtc - periodStartUtc).TotalSeconds;
        if (total <= 0 || nowUtc >= periodEndUtc || oldPrice <= 0) return 0m;
        var remaining = (periodEndUtc - (nowUtc < periodStartUtc ? periodStartUtc : nowUtc)).TotalSeconds;
        return decimal.Round(oldPrice * (decimal)(remaining / total), 2, MidpointRounding.ToZero);
    }
}

public sealed record Exceedance(UsageDimension Dimension, string Label, long Used, long Limit, string Unit, long UiFactor);

public static class DowngradeCheck
{
    /// <summary>Dimensions whose CURRENT usage is above the target plan's limit: the report shown when a downgrade is blocked.</summary>
    public static IReadOnlyList<Exceedance> Exceeding(IReadOnlyDictionary<UsageDimension, long> usage, IReadOnlyDictionary<UsageDimension, long?> targetLimits) =>
        DimensionCatalog.All
            .Where(i => targetLimits.TryGetValue(i.Dimension, out var l) && l is not null && usage.TryGetValue(i.Dimension, out var u) && u > l)
            .Select(i => new Exceedance(i.Dimension, i.Label, usage[i.Dimension], targetLimits[i.Dimension]!.Value, i.Unit, i.UiFactor)).ToList();
}

public static class EntitlementErrors
{
    public const string LimitExceeded = "plan_limit_exceeded", FeatureNotIncluded = "feature_not_in_plan", EmailNotVerified = "email_not_verified", SubscriptionInactive = "subscription_inactive";

    public static Error Limit(UsageDimension d, long? limit, long used, long requested)
    {
        var info = DimensionCatalog.Get(d);
        var fmt = (long v) => info.UiFactor > 1 ? $"{v / (double)info.UiFactor:0.##} {info.Unit}" : $"{v:N0} {info.Unit}";
        var lim = limit is null ? "unlimited" : fmt(limit.Value);
        return new Error(LimitExceeded, limit == 0
            ? $"{info.Label} is not included in your plan. Upgrade your plan to use it."
            : $"Your plan allows {lim} of {info.Label.ToLowerInvariant()} and {fmt(used)} {(info.IsPeriodic ? "were used in this period" : "are in use")}; {requested:N0} more would exceed it. Upgrade your plan, or ask the platform administrator for an override.");
    }

    public static Error Feature(string key) =>
        new(FeatureNotIncluded, $"{Features.All.FirstOrDefault(f => f.Key == key)?.Label ?? key} is not included in your plan. Upgrade your plan to use it.");

    public static Error Unverified() =>
        new(EmailNotVerified, "Confirm your e-mail address to unlock this feature. Check your inbox for the verification link (you can request a new one on the sign-in page).");

    public static Error Inactive() => new(SubscriptionInactive, "Your subscription is not active. Pay the open invoice or contact the platform administrator.");
}
