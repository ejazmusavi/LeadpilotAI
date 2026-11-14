using System.Text.RegularExpressions;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Crm;

namespace LeadPilot.Domain.Billing;

public enum PlanCycle { Monthly = 1, Quarterly = 2, Annual = 3, Custom = 4 }
public enum PlanStatus { Active = 1, Archived = 2 }
public enum PlanVisibility { Public = 1, Private = 2 }

/// <summary>The measured quantities a plan can limit. Persisted by value: append only.</summary>
public enum UsageDimension
{
    Users = 1, MessagingAccounts = 2, MonthlyMessages = 3, MonthlyAiTokens = 4, Leads = 5, Campaigns = 6,
    KnowledgeStorageBytes = 7, MonthlyAutomationRuns = 8, MonthlyDiscoveryCalls = 9,
}

public enum SubscriptionState { Trialing = 1, Active = 2, PastDue = 3, Suspended = 4, Cancelled = 5 }
public enum InvoiceStatus { Draft = 1, Issued = 2, Paid = 3, Void = 4 }
public enum InvoiceKind { Initial = 1, Renewal = 2, PlanChange = 3 }
public enum PlanChangeKind { Upgrade = 1, Downgrade = 2 }
public enum PlanChangeStatus { AwaitingPayment = 1, Scheduled = 2, Applied = 3, Cancelled = 4 }
public enum OverrideKind { Limit = 1, Feature = 2 }
public enum PaymentReportStatus { Open = 1, Closed = 2 }

/// <summary>A subscription plan. Global (platform-owned): configured by platform administrators, never hard-coded.</summary>
public class Plan : AuditableEntity
{
    private static readonly Regex CodePattern = new("^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$", RegexOptions.Compiled);
    private Plan() { }

    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public PlanCycle Cycle { get; private set; } = PlanCycle.Monthly;
    /// <summary>Length of a period for <see cref="PlanCycle.Custom"/> (days).</summary>
    public int? CustomPeriodDays { get; private set; }
    public decimal Price { get; private set; }
    public string Currency { get; private set; } = "USD";
    public bool IsTrial { get; private set; }
    public int? TrialDays { get; private set; }
    public PlanStatus Status { get; private set; } = PlanStatus.Active;
    public PlanVisibility Visibility { get; private set; } = PlanVisibility.Public;
    public int SortOrder { get; private set; }
    /// <summary>True for the clearly-labelled placeholder plans the seeder creates. The platform owner edits or archives them.</summary>
    public bool IsSample { get; private set; }
    public List<PlanLimit> Limits { get; private set; } = [];
    public List<PlanFeature> Features { get; private set; } = [];

    public static string NormalizeCode(string? code) => (code ?? string.Empty).Trim().ToLowerInvariant();

    public static Plan Create(string code, string name, string? description, PlanCycle cycle, int? customDays, decimal price, string currency, bool isTrial, int? trialDays,
        PlanVisibility visibility, int sortOrder, bool isSample, DateTime nowUtc, string? actor)
    {
        var plan = new Plan { CreatedAtUtc = nowUtc, CreatedBy = actor, IsSample = isSample };
        var c = NormalizeCode(code);
        if (!CodePattern.IsMatch(c)) throw new DomainException("A plan code is 3-40 characters: lowercase letters, digits and hyphens.");
        plan.Code = c;
        plan.Configure(name, description, cycle, customDays, price, currency, isTrial, trialDays, visibility, sortOrder, nowUtc, actor, touch: false);
        return plan;
    }

    public void Configure(string name, string? description, PlanCycle cycle, int? customDays, decimal price, string currency, bool isTrial, int? trialDays, PlanVisibility visibility, int sortOrder,
        DateTime nowUtc, string? actor, bool touch = true)
    {
        if (!Enum.IsDefined(cycle)) throw new DomainException("Unknown billing cycle.");
        if (cycle == PlanCycle.Custom && customDays is null or < 1 or > 3660) throw new DomainException("A custom billing cycle needs a length of 1 to 3660 days.");
        if (isTrial && trialDays is null or < 1 or > 365) throw new DomainException("A trial plan needs a duration of 1 to 365 days.");
        Name = Guard.Required(name, 100, "Plan name");
        Description = Guard.Optional(description, 1000, "Description");
        Cycle = cycle; CustomPeriodDays = cycle == PlanCycle.Custom ? customDays : null;
        Price = Money.Amount(price, "Price");
        Currency = Money.Currency(currency);
        IsTrial = isTrial; TrialDays = isTrial ? trialDays : null;
        if (isTrial && Price != 0) throw new DomainException("A trial plan is free: its price must be zero.");
        Visibility = visibility; SortOrder = sortOrder;
        if (touch) { UpdatedAtUtc = nowUtc; UpdatedBy = actor; }
    }

    public void Archive(DateTime nowUtc, string? actor) { Status = PlanStatus.Archived; UpdatedAtUtc = nowUtc; UpdatedBy = actor; }
    public void Restore(DateTime nowUtc, string? actor) { Status = PlanStatus.Active; UpdatedAtUtc = nowUtc; UpdatedBy = actor; }
    public void ClearSampleMark() => IsSample = false;

    public bool IsFree => Price == 0;

    /// <summary>End of a billing period starting at <paramref name="start"/>. Trial plans use their trial duration.</summary>
    public DateTime PeriodEnd(DateTime start) => IsTrial ? start.AddDays(TrialDays ?? 1) : Cycle switch
    {
        PlanCycle.Monthly => start.AddMonths(1),
        PlanCycle.Quarterly => start.AddMonths(3),
        PlanCycle.Annual => start.AddYears(1),
        _ => start.AddDays(CustomPeriodDays ?? 30),
    };

    /// <summary>The price normalised to 30 days; used only to tell an upgrade from a downgrade between plans of the SAME currency.</summary>
    public decimal PricePer30Days(DateTime reference)
    {
        var days = Math.Max(1, (PeriodEnd(reference) - reference).TotalDays);
        return Price * 30m / (decimal)days;
    }

    public long? LimitFor(UsageDimension d) => Limits.FirstOrDefault(l => l.Dimension == d)?.Value;
    public bool HasLimit(UsageDimension d) => Limits.Any(l => l.Dimension == d);
    public bool FeatureEnabled(string feature) => Features.FirstOrDefault(f => f.Feature == feature)?.Enabled ?? false;

    public void SetLimit(UsageDimension d, long? value)
    {
        if (!Enum.IsDefined(d)) throw new DomainException("Unknown limit.");
        if (value is < 0) throw new DomainException("A limit cannot be negative (leave it empty for unlimited).");
        var existing = Limits.FirstOrDefault(l => l.Dimension == d);
        if (existing is null) Limits.Add(PlanLimit.Create(Id, d, value)); else existing.Set(value);
    }

    public void SetFeature(string feature, bool enabled)
    {
        var key = Guard.Required(feature, 40, "Feature");
        var existing = Features.FirstOrDefault(f => f.Feature == key);
        if (existing is null) Features.Add(PlanFeature.Create(Id, key, enabled)); else existing.Set(enabled);
    }
}

public class PlanLimit : Entity
{
    private PlanLimit() { }
    public Guid PlanId { get; private set; }
    public UsageDimension Dimension { get; private set; }
    /// <summary>Null means unlimited. Zero means the feature is not included.</summary>
    public long? Value { get; private set; }
    internal static PlanLimit Create(Guid planId, UsageDimension d, long? value) => new() { PlanId = planId, Dimension = d, Value = value };
    internal void Set(long? value) => Value = value;
}

public class PlanFeature : Entity
{
    private PlanFeature() { }
    public Guid PlanId { get; private set; }
    public string Feature { get; private set; } = string.Empty;
    public bool Enabled { get; private set; }
    internal static PlanFeature Create(Guid planId, string feature, bool enabled) => new() { PlanId = planId, Feature = feature, Enabled = enabled };
    internal void Set(bool enabled) => Enabled = enabled;
}

/// <summary>Platform-wide key/value setting (registration switch, grace period, renewal lead time, invoice issuer, tax). Global.</summary>
public class PlatformSetting : Entity
{
    private PlatformSetting() { }
    public string Key { get; private set; } = string.Empty;
    public string Value { get; private set; } = string.Empty;
    public DateTime UpdatedAtUtc { get; private set; }
    public string? UpdatedBy { get; private set; }
    public static PlatformSetting Create(string key, string value, DateTime nowUtc, string? actor) => new() { Key = Guard.Required(key, 80, "Key"), Value = value, UpdatedAtUtc = nowUtc, UpdatedBy = actor };
    public void Set(string value, DateTime nowUtc, string? actor) { Value = value; UpdatedAtUtc = nowUtc; UpdatedBy = actor; }
}

/// <summary>The single platform-wide invoice counter (one row). Incremented with an atomic UPDATE, so two concurrent issues can never share a number.</summary>
public class InvoiceSequence
{
    public int Id { get; set; } = 1;
    public long LastNumber { get; set; }
}

/// <summary>One tenant's subscription to the platform. One row per tenant; history lives in <see cref="SubscriptionEvent"/>.</summary>
public class TenantSubscription : AuditableEntity, ITenantOwned
{
    private TenantSubscription() { }
    public Guid TenantId { get; private set; }
    public Guid PlanId { get; private set; }
    public SubscriptionState State { get; private set; }
    public DateTime PeriodStartUtc { get; private set; }
    public DateTime PeriodEndUtc { get; private set; }
    public DateTime? TrialEndsAtUtc { get; private set; }
    /// <summary>Set while PastDue: when the grace period ends and the tenant is suspended.</summary>
    public DateTime? GraceEndsAtUtc { get; private set; }
    /// <summary>A scheduled downgrade that takes effect with the next renewal.</summary>
    public Guid? PendingPlanId { get; private set; }
    public Guid? PendingChangeRequestId { get; private set; }
    /// <summary>The verified payment behind the current paid period. Null for trials, free plans and platform grants.</summary>
    public Guid? LastPaymentId { get; private set; }
    public string? GrantReason { get; private set; }

    public static TenantSubscription StartFreeOrTrial(Guid tenantId, Plan plan, DateTime nowUtc, string? actor)
    {
        if (plan.Price != 0) throw new DomainException("A priced plan can only start with a verified payment or a recorded platform grant.");
        var end = plan.PeriodEnd(nowUtc);
        return new TenantSubscription
        {
            TenantId = tenantId, PlanId = plan.Id, State = plan.IsTrial ? SubscriptionState.Trialing : SubscriptionState.Active, PeriodStartUtc = nowUtc, PeriodEndUtc = end,
            TrialEndsAtUtc = plan.IsTrial ? end : null, CreatedAtUtc = nowUtc, CreatedBy = actor,
        };
    }

    /// <summary>A platform administrator grants a plan period without payment (complimentary, pilot, migration). Audited by the caller with a mandatory reason.</summary>
    public static TenantSubscription CreateGranted(Guid tenantId, Plan plan, DateTime start, DateTime end, string reason, DateTime nowUtc, string? actor) =>
        new()
        {
            TenantId = tenantId, PlanId = plan.Id, State = plan.IsTrial ? SubscriptionState.Trialing : SubscriptionState.Active, PeriodStartUtc = start, PeriodEndUtc = end,
            TrialEndsAtUtc = plan.IsTrial ? end : null, GrantReason = Guard.Required(reason, 300, "Reason"), CreatedAtUtc = nowUtc, CreatedBy = actor,
        };

    public void Grant(Plan plan, DateTime start, DateTime end, string reason, DateTime nowUtc, string? actor)
    {
        if (end <= start) throw new DomainException("A grant needs an end after its start.");
        PlanId = plan.Id; PeriodStartUtc = start; PeriodEndUtc = end; State = plan.IsTrial ? SubscriptionState.Trialing : SubscriptionState.Active;
        TrialEndsAtUtc = plan.IsTrial ? end : null; GraceEndsAtUtc = null; PendingPlanId = null; PendingChangeRequestId = null;
        GrantReason = Guard.Required(reason, 300, "Reason"); UpdatedAtUtc = nowUtc; UpdatedBy = actor;
    }

    /// <summary>
    /// THE only way a PRICED period starts or is extended. It takes the id of a <see cref="BillingPayment"/>, and those rows are created only by the billing payment verifier
    /// (manual verification by an operator with Platform.Billing.Verify). An architecture test pins the callers.
    /// </summary>
    public void ActivateWithVerifiedPayment(Guid verifiedPaymentId, Plan plan, DateTime periodStart, DateTime periodEnd, DateTime nowUtc)
    {
        if (verifiedPaymentId == Guid.Empty) throw new DomainException("A paid period can only be activated by a verified payment.");
        if (periodEnd <= periodStart) throw new DomainException("A period must end after it starts.");
        PlanId = plan.Id; PeriodStartUtc = periodStart; PeriodEndUtc = periodEnd; State = SubscriptionState.Active; TrialEndsAtUtc = null; GraceEndsAtUtc = null;
        PendingPlanId = null; PendingChangeRequestId = null; LastPaymentId = verifiedPaymentId; GrantReason = null; UpdatedAtUtc = nowUtc;
    }

    /// <summary>A renewal invoice for a FUTURE period was verified early: only the payment is remembered; the period rolls over when the current one ends (see <see cref="RollOverToPaidPeriod"/>).</summary>
    public void RecordEarlyPayment(Guid verifiedPaymentId, DateTime nowUtc)
    {
        if (verifiedPaymentId == Guid.Empty) throw new DomainException("A paid period can only be recorded from a verified payment.");
        LastPaymentId = verifiedPaymentId; UpdatedAtUtc = nowUtc;
    }

    /// <summary>The current period ended and the next one was paid in advance: move to it. Takes the verified payment of that invoice, like <see cref="ActivateWithVerifiedPayment"/>.</summary>
    public void RollOverToPaidPeriod(Guid verifiedPaymentId, Plan plan, DateTime periodStart, DateTime periodEnd, DateTime nowUtc) =>
        ActivateWithVerifiedPayment(verifiedPaymentId, plan, periodStart, periodEnd, nowUtc);

    /// <summary>A free (zero-price) plan renews itself: no money is involved, so no payment can be required.</summary>
    public void RenewFree(Plan plan, DateTime periodStart, DateTime periodEnd, DateTime nowUtc)
    {
        if (plan.Price != 0) throw new DomainException("Only a free plan renews without a payment.");
        PlanId = plan.Id; PeriodStartUtc = periodStart; PeriodEndUtc = periodEnd; State = SubscriptionState.Active; TrialEndsAtUtc = null; GraceEndsAtUtc = null;
        PendingPlanId = null; PendingChangeRequestId = null; UpdatedAtUtc = nowUtc;
    }

    /// <summary>An operator lifted a suspension: back to Active/Trialing while the period is still running, otherwise PastDue with a fresh grace period so the tenant can pay.</summary>
    public void Reinstate(DateTime graceEndsAtUtc, DateTime nowUtc)
    {
        if (PeriodEndUtc > nowUtc) { State = TrialEndsAtUtc is not null ? SubscriptionState.Trialing : SubscriptionState.Active; GraceEndsAtUtc = null; }
        else { State = SubscriptionState.PastDue; GraceEndsAtUtc = graceEndsAtUtc; }
        UpdatedAtUtc = nowUtc;
    }

    public void MarkPastDue(DateTime graceEndsAtUtc, DateTime nowUtc) { State = SubscriptionState.PastDue; GraceEndsAtUtc = graceEndsAtUtc; UpdatedAtUtc = nowUtc; }
    public void MarkSuspended(DateTime nowUtc) { State = SubscriptionState.Suspended; UpdatedAtUtc = nowUtc; }
    public void MarkCancelled(DateTime nowUtc) { State = SubscriptionState.Cancelled; UpdatedAtUtc = nowUtc; }
    public void SchedulePlanChange(Guid planId, Guid requestId, DateTime nowUtc) { PendingPlanId = planId; PendingChangeRequestId = requestId; UpdatedAtUtc = nowUtc; }
    public void ClearPendingChange(DateTime nowUtc) { PendingPlanId = null; PendingChangeRequestId = null; UpdatedAtUtc = nowUtc; }
    /// <summary>Switch plan immediately without touching the period (a free target plan, or an operator action). Limits of the new plan apply at once.</summary>
    public void SwitchPlanKeepingPeriod(Guid planId, DateTime nowUtc) { PlanId = planId; PendingPlanId = null; PendingChangeRequestId = null; UpdatedAtUtc = nowUtc; }
    public bool IsExpiredAt(DateTime nowUtc) => nowUtc >= PeriodEndUtc;
    public bool GrantsAccess => State is SubscriptionState.Trialing or SubscriptionState.Active or SubscriptionState.PastDue;
}

public class SubscriptionEvent : Entity, ITenantOwned, IAppendOnly
{
    private SubscriptionEvent() { }
    public Guid TenantId { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string Summary { get; private set; } = string.Empty;
    public Guid? FromPlanId { get; private set; }
    public Guid? ToPlanId { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
    public string? Actor { get; private set; }

    public static SubscriptionEvent Create(Guid tenantId, string type, string summary, Guid? from, Guid? to, string? actor, DateTime nowUtc) =>
        new() { TenantId = tenantId, Type = Guard.Required(type, 40, "Type"), Summary = summary.Length > 500 ? summary[..500] : summary, FromPlanId = from, ToPlanId = to, Actor = actor, OccurredAtUtc = nowUtc };
}

/// <summary>A per-tenant exception to the plan, set by a platform administrator (always with a reason, always audited). Wins over the plan until it expires.</summary>
public class TenantEntitlementOverride : AuditableEntity, ITenantOwned
{
    private TenantEntitlementOverride() { }
    public Guid TenantId { get; private set; }
    /// <summary>A <see cref="UsageDimension"/> name or a feature key.</summary>
    public string Key { get; private set; } = string.Empty;
    public OverrideKind Kind { get; private set; }
    public long? LimitValue { get; private set; }
    public bool Unlimited { get; private set; }
    public bool? FeatureEnabled { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public Guid? GrantedByUserId { get; private set; }

    public static TenantEntitlementOverride ForLimit(Guid tenantId, UsageDimension d, long? value, bool unlimited, DateTime? expires, string reason, Guid? by, DateTime nowUtc, string? actor)
    {
        var o = new TenantEntitlementOverride { TenantId = tenantId, Kind = OverrideKind.Limit, Key = d.ToString(), CreatedAtUtc = nowUtc, CreatedBy = actor };
        o.SetLimit(value, unlimited, expires, reason, by, nowUtc, actor, touch: false);
        return o;
    }

    public static TenantEntitlementOverride ForFeature(Guid tenantId, string feature, bool enabled, DateTime? expires, string reason, Guid? by, DateTime nowUtc, string? actor) =>
        new() { TenantId = tenantId, Kind = OverrideKind.Feature, Key = Guard.Required(feature, 40, "Feature"), FeatureEnabled = enabled, ExpiresAtUtc = expires, Reason = Guard.Required(reason, 300, "Reason"), GrantedByUserId = by, CreatedAtUtc = nowUtc, CreatedBy = actor };

    public void SetLimit(long? value, bool unlimited, DateTime? expires, string reason, Guid? by, DateTime nowUtc, string? actor, bool touch = true)
    {
        if (!unlimited && value is null or < 0) throw new DomainException("An override needs a limit of zero or more, or 'unlimited'.");
        LimitValue = unlimited ? null : value; Unlimited = unlimited; ExpiresAtUtc = expires; Reason = Guard.Required(reason, 300, "Reason"); GrantedByUserId = by;
        if (touch) { UpdatedAtUtc = nowUtc; UpdatedBy = actor; }
    }

    public void SetFeature(bool enabled, DateTime? expires, string reason, Guid? by, DateTime nowUtc, string? actor) { FeatureEnabled = enabled; ExpiresAtUtc = expires; Reason = Guard.Required(reason, 300, "Reason"); GrantedByUserId = by; UpdatedAtUtc = nowUtc; UpdatedBy = actor; }
    public bool IsActiveAt(DateTime nowUtc) => ExpiresAtUtc is null || ExpiresAtUtc > nowUtc;
}

/// <summary>A threshold notice (80 % / 100 %) for one dimension in one period. (tenant, dimension, period, threshold) is unique, so a notice is recorded once however often usage is re-evaluated.</summary>
public class UsageNotice : Entity, ITenantOwned, IAppendOnly
{
    private UsageNotice() { }
    public Guid TenantId { get; private set; }
    public UsageDimension Dimension { get; private set; }
    public DateTime PeriodStartUtc { get; private set; }
    public int Threshold { get; private set; }
    public long Used { get; private set; }
    public long Limit { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public static UsageNotice Create(Guid tenantId, UsageDimension d, DateTime periodStart, int threshold, long used, long limit, DateTime nowUtc) =>
        new() { TenantId = tenantId, Dimension = d, PeriodStartUtc = periodStart, Threshold = threshold, Used = used, Limit = limit, CreatedAtUtc = nowUtc };
}

/// <summary>A short-lived hold on a not-yet-recorded consumption (AI tokens, discovery calls): concurrent callers see each other's holds, so the last unit cannot be spent twice. Expires on its own if a process dies.</summary>
public class UsageReservation : Entity, ITenantOwned
{
    private UsageReservation() { }
    public Guid TenantId { get; private set; }
    public UsageDimension Dimension { get; private set; }
    public long Quantity { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public static UsageReservation Create(Guid tenantId, UsageDimension d, long quantity, DateTime nowUtc, TimeSpan ttl) =>
        new() { TenantId = tenantId, Dimension = d, Quantity = quantity, CreatedAtUtc = nowUtc, ExpiresAtUtc = nowUtc.Add(ttl) };
}

/// <summary>
/// An invoice. Draft invoices can still receive lines; <see cref="Issue"/> assigns the platform-wide number and freezes it: afterwards only the status moves (Issued → Paid / Void). Lines are append-only and the
/// persistence guard refuses any other change to an issued invoice.
/// </summary>
public class Invoice : AuditableEntity, ITenantOwned
{
    private Invoice() { }
    public Guid TenantId { get; private set; }
    public string? Number { get; private set; }
    public InvoiceKind Kind { get; private set; }
    public InvoiceStatus Status { get; private set; } = InvoiceStatus.Draft;
    public Guid PlanId { get; private set; }
    public string Currency { get; private set; } = "USD";
    public decimal Subtotal { get; private set; }
    public string? TaxName { get; private set; }
    public decimal TaxRatePercent { get; private set; }
    public decimal TaxAmount { get; private set; }
    public decimal Total { get; private set; }
    public DateTime? IssuedAtUtc { get; private set; }
    public DateTime? DueAtUtc { get; private set; }
    public DateTime? PaidAtUtc { get; private set; }
    public DateTime? VoidedAtUtc { get; private set; }
    public string? VoidReason { get; private set; }
    /// <summary>The service period this invoice pays for.</summary>
    public DateTime PeriodStartUtc { get; private set; }
    public DateTime PeriodEndUtc { get; private set; }
    public Guid? PlanChangeRequestId { get; private set; }
    /// <summary>Idempotency key: at most one invoice per (tenant, key). Renewals use the period start, so the renewal job can run any number of times.</summary>
    public string? IdempotencyKey { get; private set; }
    public string BillToName { get; private set; } = string.Empty;
    public string? SellerText { get; private set; }
    public string? PaymentInstructions { get; private set; }
    public string? Notes { get; private set; }
    public List<InvoiceLine> Lines { get; private set; } = [];

    public static Invoice CreateDraft(Guid tenantId, InvoiceKind kind, Plan plan, DateTime periodStart, DateTime periodEnd, string billToName, string? taxName, decimal taxRatePercent,
        string? sellerText, string? paymentInstructions, string? idempotencyKey, Guid? planChangeId, DateTime nowUtc, string? actor)
    {
        if (taxRatePercent is < 0 or > 100 || decimal.Round(taxRatePercent, 2) != taxRatePercent) throw new DomainException("A tax rate is between 0 and 100 percent with at most two decimals.");
        return new Invoice
        {
            TenantId = tenantId, Kind = kind, PlanId = plan.Id, Currency = Money.Currency(plan.Currency), PeriodStartUtc = periodStart, PeriodEndUtc = periodEnd,
            BillToName = Guard.Required(billToName, 200, "Bill-to name"), TaxName = Guard.Optional(taxName, 40, "Tax name"), TaxRatePercent = taxRatePercent,
            SellerText = Guard.Optional(sellerText, 1000, "Issuer"), PaymentInstructions = Guard.Optional(paymentInstructions, 1500, "Payment instructions"),
            IdempotencyKey = idempotencyKey, PlanChangeRequestId = planChangeId, CreatedAtUtc = nowUtc, CreatedBy = actor,
        };
    }

    public InvoiceLine AddLine(string description, decimal quantity, decimal unitPrice)
    {
        if (Status != InvoiceStatus.Draft) throw new DomainException("An issued invoice cannot be changed.");
        if (quantity <= 0 || quantity > 100_000) throw new DomainException("Quantity must be greater than zero.");
        var up = Money.Amount(Math.Abs(unitPrice), "Unit price"); // sign handled below: credits are negative lines
        var signedUnit = unitPrice < 0 ? -up : up;
        var amount = decimal.Round(quantity * signedUnit, 2, MidpointRounding.AwayFromZero);
        var line = InvoiceLine.Create(TenantId, Id, Lines.Count + 1, description, quantity, signedUnit, amount);
        Lines.Add(line);
        Subtotal += amount;
        return line;
    }

    /// <summary>Freezes the invoice: number assigned by the platform sequence, totals computed once (tax = round(subtotal × rate)), never below zero.</summary>
    public void Issue(string number, DateTime nowUtc, DateTime dueAtUtc)
    {
        if (Status != InvoiceStatus.Draft) throw new DomainException($"A {Status} invoice cannot be issued.");
        if (Lines.Count == 0) throw new DomainException("An invoice needs at least one line.");
        if (string.IsNullOrWhiteSpace(number)) throw new DomainException("An issued invoice needs a number.");
        if (Subtotal < 0) Subtotal = 0;   // a credit larger than the charge is not paid out
        TaxAmount = decimal.Round(Subtotal * TaxRatePercent / 100m, 2, MidpointRounding.AwayFromZero);
        Total = Subtotal + TaxAmount;
        Number = number; Status = InvoiceStatus.Issued; IssuedAtUtc = nowUtc; DueAtUtc = dueAtUtc; UpdatedAtUtc = nowUtc;
    }

    public void MarkPaid(DateTime nowUtc)
    {
        if (Status != InvoiceStatus.Issued) throw new DomainException($"A {Status} invoice cannot be paid.");
        Status = InvoiceStatus.Paid; PaidAtUtc = nowUtc; UpdatedAtUtc = nowUtc;
    }

    public void Void(string reason, DateTime nowUtc)
    {
        if (Status is not (InvoiceStatus.Draft or InvoiceStatus.Issued)) throw new DomainException($"A {Status} invoice cannot be voided.");
        Status = InvoiceStatus.Void; VoidedAtUtc = nowUtc; VoidReason = Guard.Required(reason, 300, "Reason"); UpdatedAtUtc = nowUtc;
    }
}

public class InvoiceLine : Entity, ITenantOwned, IAppendOnly
{
    private InvoiceLine() { }
    public Guid TenantId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public int Position { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public decimal Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal Amount { get; private set; }
    internal static InvoiceLine Create(Guid tenantId, Guid invoiceId, int position, string description, decimal quantity, decimal unitPrice, decimal amount) =>
        new() { TenantId = tenantId, InvoiceId = invoiceId, Position = position, Description = Guard.Required(description, 300, "Description"), Quantity = quantity, UnitPrice = unitPrice, Amount = amount };
}

/// <summary>A VERIFIED platform payment. Like Phase 10's <c>Payment</c>: rows exist only after an operator recorded evidence; the absence of a row is what "not paid" means. One per invoice, evidence unique platform-wide.</summary>
public class BillingPayment : Entity, ITenantOwned, IAppendOnly
{
    private BillingPayment() { }
    public Guid TenantId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public PaymentMethod Method { get; private set; }
    public VerificationSource Source { get; private set; }
    public string EvidenceReference { get; private set; } = string.Empty;
    public string EvidenceKey { get; private set; } = string.Empty;
    public Guid? VerifiedByUserId { get; private set; }
    public DateTime PaidAtUtc { get; private set; }
    public DateTime VerifiedAtUtc { get; private set; }
    public string? Notes { get; private set; }

    public static BillingPayment Create(Guid tenantId, Guid invoiceId, decimal amount, string currency, PaymentMethod method, VerificationSource source, string evidence, Guid? verifiedBy,
        DateTime paidAtUtc, string? notes, DateTime nowUtc)
    {
        var ev = Guard.Required(evidence, 150, "Evidence reference");
        var key = Money.EvidenceKey(ev);
        if (key.Length < 3) throw new DomainException("The evidence reference (for example the receipt or transfer id) must contain at least 3 letters or digits.");
        if (source == VerificationSource.Manual && verifiedBy is null) throw new DomainException("A manual verification needs the verifying person.");
        if (paidAtUtc > nowUtc.AddMinutes(5)) throw new DomainException("The payment date cannot be in the future.");
        return new() { TenantId = tenantId, InvoiceId = invoiceId, Amount = Money.Amount(amount, "Amount", allowZero: false), Currency = Money.Currency(currency), Method = method, Source = source, EvidenceReference = ev, EvidenceKey = key,
            VerifiedByUserId = verifiedBy, PaidAtUtc = paidAtUtc, VerifiedAtUtc = nowUtc, Notes = Guard.Optional(notes, 500, "Notes") };
    }
}

/// <summary>A tenant telling the platform "I paid this invoice" (with the transfer reference). It is NOT a payment: it only puts the invoice at the top of the operator's verification queue.</summary>
public class PaymentReport : AuditableEntity, ITenantOwned
{
    private PaymentReport() { }
    public Guid TenantId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public string EvidenceReference { get; private set; } = string.Empty;
    public DateTime? PaidOnUtc { get; private set; }
    public string? Note { get; private set; }
    public Guid? ReportedByUserId { get; private set; }
    public PaymentReportStatus Status { get; private set; } = PaymentReportStatus.Open;

    public static PaymentReport Create(Guid tenantId, Guid invoiceId, string evidence, DateTime? paidOn, string? note, Guid? by, DateTime nowUtc, string? actor) =>
        new() { TenantId = tenantId, InvoiceId = invoiceId, EvidenceReference = Guard.Required(evidence, 150, "Transfer reference"), PaidOnUtc = paidOn, Note = Guard.Optional(note, 500, "Note"), ReportedByUserId = by, CreatedAtUtc = nowUtc, CreatedBy = actor };
    public void Close(DateTime nowUtc) { Status = PaymentReportStatus.Closed; UpdatedAtUtc = nowUtc; }
}

/// <summary>An upgrade or downgrade request of a tenant. Upgrades wait for their invoice's verified payment; downgrades are scheduled for the period end (or refused with the report of what exceeds the target plan).</summary>
public class PlanChangeRequest : AuditableEntity, ITenantOwned
{
    private PlanChangeRequest() { }
    public Guid TenantId { get; private set; }
    public Guid FromPlanId { get; private set; }
    public Guid ToPlanId { get; private set; }
    public PlanChangeKind Kind { get; private set; }
    public PlanChangeStatus Status { get; private set; }
    public Guid? InvoiceId { get; private set; }
    public DateTime? EffectiveAtUtc { get; private set; }
    public decimal CreditAmount { get; private set; }
    public string? Currency { get; private set; }
    public Guid? RequestedByUserId { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }
    public string? CloseReason { get; private set; }
    /// <summary>Set while AwaitingPayment or Scheduled: at most one open request per tenant.</summary>
    public bool IsOpen { get; private set; } = true;

    public static PlanChangeRequest Create(Guid tenantId, Guid from, Guid to, PlanChangeKind kind, PlanChangeStatus status, DateTime? effectiveAt, decimal credit, string? currency, Guid? by, DateTime nowUtc, string? actor) =>
        new() { TenantId = tenantId, FromPlanId = from, ToPlanId = to, Kind = kind, Status = status, EffectiveAtUtc = effectiveAt, CreditAmount = credit, Currency = currency, RequestedByUserId = by, CreatedAtUtc = nowUtc, CreatedBy = actor };

    public void AttachInvoice(Guid invoiceId, DateTime nowUtc) { InvoiceId = invoiceId; UpdatedAtUtc = nowUtc; }
    public void Close(PlanChangeStatus status, string? reason, DateTime nowUtc) { Status = status; IsOpen = false; ClosedAtUtc = nowUtc; CloseReason = Guard.Optional(reason, 300, "Reason"); UpdatedAtUtc = nowUtc; }
}

public enum RegistrationOutcome { Accepted = 1, RateLimited = 2, Blocked = 3, Invalid = 4, Duplicate = 5 }

/// <summary>One self-registration attempt, kept for rate limiting and abuse review. Global; stores a hash of the address, never the address.</summary>
public class RegistrationAttempt : Entity
{
    private RegistrationAttempt() { }
    public string IpHash { get; private set; } = string.Empty;
    public string EmailHash { get; private set; } = string.Empty;
    public string EmailDomain { get; private set; } = string.Empty;
    public RegistrationOutcome Outcome { get; private set; }
    public string? Reason { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public static RegistrationAttempt Create(string ipHash, string emailHash, string domain, RegistrationOutcome outcome, string? reason, DateTime nowUtc) =>
        new() { IpHash = ipHash, EmailHash = emailHash, EmailDomain = domain.Length > 100 ? domain[..100] : domain, Outcome = outcome, Reason = reason is { Length: > 200 } ? reason[..200] : reason, CreatedAtUtc = nowUtc };
}
