using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;

namespace LeadPilot.Domain.Conversion;

public enum CustomerStatus { Onboarding = 1, Active = 2, Cancelled = 3 }
public enum SubscriptionStatus { Pending = 1, Active = 2, Cancelled = 3 }
public enum BillingCycle { OneTime = 1, Monthly = 2, Quarterly = 3, Yearly = 4 }
public enum PaymentRequestStatus { Open = 1, Paid = 2, Cancelled = 3, Expired = 4 }
public enum PaymentMethod { BankTransfer = 1, Cash = 2, Other = 3, Provider = 4 }
public enum VerificationSource { Manual = 1, ProviderWebhook = 2 }
public enum PaymentEventOutcome { Applied = 1, Rejected = 2, Ignored = 3 }

/// <summary>Money rules shared by every entity: two decimals, an explicit 3-letter currency, never negative.</summary>
public static class Money
{
    public const decimal Max = 999_999_999_999m;

    public static decimal Amount(decimal value, string label, bool allowZero = true)
    {
        if (value < 0 || value > Max || (!allowZero && value == 0)) throw new DomainException($"{label} must be {(allowZero ? "zero or positive" : "greater than zero")}.");
        if (decimal.Round(value, 2) != value) throw new DomainException($"{label} can have at most two decimals.");
        return value;
    }

    public static string Currency(string? currency)
    {
        var cur = (currency ?? string.Empty).Trim().ToUpperInvariant();
        if (cur.Length != 3 || !cur.All(char.IsAsciiLetterUpper)) throw new DomainException("Currency must be a 3-letter code such as USD.");
        return cur;
    }

    /// <summary>Normalised form of an evidence reference (receipt / transfer id) used to detect the same proof being used twice.</summary>
    public static string EvidenceKey(string reference) => new string(reference.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}

/// <summary>The customer record: separate from the lead and contact it came from. History is linked (LeadId, OpportunityId), never copied.</summary>
public class Customer : AuditableEntity, ITenantOwned
{
    private Customer() { }
    public Guid TenantId { get; private set; }
    public int Number { get; private set; }
    public Guid LeadId { get; private set; }
    public Guid? PrimaryContactId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public CustomerStatus Status { get; private set; } = CustomerStatus.Onboarding;
    /// <summary>Account id in the product, entered by staff after provisioning (the platform does not provision products).</summary>
    public string? AccountReference { get; private set; }
    public string? AccountNotes { get; private set; }
    public DateTime ConvertedAtUtc { get; private set; }
    public Guid? ConvertedByUserId { get; private set; }

    public static Customer Create(Guid tenantId, int number, Guid leadId, Guid? contactId, string name, Guid? by, DateTime nowUtc, string? actor) =>
        new() { TenantId = tenantId, Number = number, LeadId = leadId, PrimaryContactId = contactId, Name = Guard.Required(name, 200, "Customer name"), ConvertedAtUtc = nowUtc, ConvertedByUserId = by, CreatedAtUtc = nowUtc, CreatedBy = actor };

    public string Code => $"C-{Number:0000}";

    public void SetAccount(string? reference, string? notes, DateTime nowUtc) { AccountReference = Guard.Optional(reference, 150, "Account reference"); AccountNotes = Guard.Optional(notes, 500, "Account notes"); UpdatedAtUtc = nowUtc; }
    public void Activate(DateTime nowUtc) { if (Status == CustomerStatus.Onboarding) { Status = CustomerStatus.Active; UpdatedAtUtc = nowUtc; } }
    public void MarkCancelled(DateTime nowUtc) { Status = CustomerStatus.Cancelled; UpdatedAtUtc = nowUtc; }
}

/// <summary>The recorded commercial terms. Immutable once written: a change of terms is a NEW agreement (a new opportunity), never an edit.</summary>
public class CommercialAgreement : Entity, ITenantOwned, IAppendOnly
{
    private CommercialAgreement() { }
    public Guid TenantId { get; private set; }
    public Guid CustomerId { get; private set; }
    public Guid SubscriptionId { get; private set; }
    public Guid OpportunityId { get; private set; }
    public string Terms { get; private set; } = string.Empty;
    public decimal? ListPrice { get; private set; }
    public decimal Price { get; private set; }
    public string Currency { get; private set; } = "USD";
    public BillingCycle BillingCycle { get; private set; }
    public decimal? DiscountPercent { get; private set; }
    public Guid ApprovedByUserId { get; private set; }
    public DateTime ApprovedAtUtc { get; private set; }

    public static CommercialAgreement Create(Guid tenantId, Guid customerId, Guid subscriptionId, Guid opportunityId, string terms, decimal? listPrice, decimal price, string currency, BillingCycle cycle,
        decimal? discountPercent, Guid approvedBy, DateTime nowUtc)
    {
        if (!Enum.IsDefined(cycle)) throw new DomainException("Unknown billing cycle.");
        if (discountPercent is < 0 or > 100) throw new DomainException("A discount is between 0 and 100 percent.");
        return new()
        {
            TenantId = tenantId, CustomerId = customerId, SubscriptionId = subscriptionId, OpportunityId = opportunityId, Terms = Guard.Required(terms, 4000, "Commercial terms"),
            ListPrice = listPrice is null ? null : Money.Amount(listPrice.Value, "List price"), Price = Money.Amount(price, "Price"), Currency = Money.Currency(currency), BillingCycle = cycle,
            DiscountPercent = discountPercent, ApprovedByUserId = approvedBy, ApprovedAtUtc = nowUtc,
        };
    }
}

public class Subscription : AuditableEntity, ITenantOwned
{
    private Subscription() { }
    public Guid TenantId { get; private set; }
    public Guid CustomerId { get; private set; }
    public Guid? PlanId { get; private set; }
    public string PlanName { get; private set; } = string.Empty;
    public string? ProductName { get; private set; }
    public SubscriptionStatus Status { get; private set; } = SubscriptionStatus.Pending;
    public BillingCycle BillingCycle { get; private set; }
    public DateTime? ActivatedAtUtc { get; private set; }
    /// <summary>The verified payment that activated the subscription. A subscription cannot be Active without it (also a database check constraint).</summary>
    public Guid? ActivatedByPaymentId { get; private set; }
    public DateTime? CancelledAtUtc { get; private set; }
    public string? CancelReason { get; private set; }

    public static Subscription CreatePending(Guid tenantId, Guid customerId, Guid? planId, string planName, string? productName, BillingCycle cycle, DateTime nowUtc, string? actor) =>
        new() { TenantId = tenantId, CustomerId = customerId, PlanId = planId, PlanName = Guard.Required(planName, 100, "Plan"), ProductName = Guard.Optional(productName, 100, "Product"), BillingCycle = cycle, CreatedAtUtc = nowUtc, CreatedBy = actor };

    /// <summary>
    /// THE only way a subscription becomes Active. It takes the id of a Payment row, and Payment rows are only ever created by the payment verification service (manual verification by staff, or a signature-verified
    /// provider event). Nothing in the AI tool gateway, the workflow engine or a chat message can reach it. An architecture test pins the callers.
    /// </summary>
    public void ActivateWithVerifiedPayment(Guid verifiedPaymentId, DateTime nowUtc)
    {
        if (verifiedPaymentId == Guid.Empty) throw new DomainException("A subscription can only be activated by a verified payment.");
        if (Status != SubscriptionStatus.Pending) throw new DomainException($"A subscription that is {Status} cannot be activated.");
        Status = SubscriptionStatus.Active; ActivatedAtUtc = nowUtc; ActivatedByPaymentId = verifiedPaymentId; UpdatedAtUtc = nowUtc;
    }

    public void Cancel(string? reason, DateTime nowUtc)
    {
        if (Status == SubscriptionStatus.Cancelled) throw new DomainException("This subscription is already cancelled.");
        Status = SubscriptionStatus.Cancelled; CancelledAtUtc = nowUtc; CancelReason = Guard.Optional(reason, 300, "Reason"); UpdatedAtUtc = nowUtc;
    }
}

public class PaymentRequest : AuditableEntity, ITenantOwned
{
    private PaymentRequest() { }
    public Guid TenantId { get; private set; }
    public Guid CustomerId { get; private set; }
    public Guid SubscriptionId { get; private set; }
    public Guid AgreementId { get; private set; }
    /// <summary>Human-readable reference the customer quotes (bank transfer narrative). Unique per tenant.</summary>
    public string Reference { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public PaymentRequestStatus Status { get; private set; } = PaymentRequestStatus.Open;
    public DateTime? DueAtUtc { get; private set; }
    public string? Instructions { get; private set; }
    public string? PayUrl { get; private set; }
    public string RequestedVia { get; private set; } = "staff";
    public Guid? ApprovalRequestId { get; private set; }
    /// <summary>Set while the request is Open: at most one open request per subscription.</summary>
    public Guid? OpenForSubscription { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }
    public string? CloseReason { get; private set; }

    public static PaymentRequest Create(Guid tenantId, Guid customerId, Guid subscriptionId, Guid agreementId, string reference, decimal amount, string currency, DateTime? dueAtUtc, string? instructions,
        string requestedVia, Guid? approvalId, DateTime nowUtc, string? actor) =>
        new()
        {
            TenantId = tenantId, CustomerId = customerId, SubscriptionId = subscriptionId, AgreementId = agreementId, Reference = Guard.Required(reference, 40, "Reference"),
            Amount = Money.Amount(amount, "Amount", allowZero: false), Currency = Money.Currency(currency), DueAtUtc = dueAtUtc, Instructions = Guard.Optional(instructions, 1500, "Instructions"),
            RequestedVia = requestedVia, ApprovalRequestId = approvalId, OpenForSubscription = subscriptionId, CreatedAtUtc = nowUtc, CreatedBy = actor,
        };

    public void Cancel(string? reason, DateTime nowUtc)
    {
        if (Status != PaymentRequestStatus.Open) throw new DomainException($"A payment request that is {Status} cannot be cancelled.");
        Status = PaymentRequestStatus.Cancelled; OpenForSubscription = null; ClosedAtUtc = nowUtc; CloseReason = Guard.Optional(reason, 300, "Reason"); UpdatedAtUtc = nowUtc;
    }
}

/// <summary>
/// A VERIFIED payment. Rows exist only after a person with the verify permission recorded evidence, or after a provider event passed signature verification and matched the request. There is no "claimed" or
/// "unverified" payment row: the absence of this row is what "not paid" means.
/// </summary>
public class Payment : Entity, ITenantOwned, IAppendOnly
{
    private Payment() { }
    public Guid TenantId { get; private set; }
    public Guid PaymentRequestId { get; private set; }
    public Guid CustomerId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public PaymentMethod Method { get; private set; }
    public VerificationSource Source { get; private set; }
    /// <summary>Bank transfer receipt id, deposit slip number, provider payment id...</summary>
    public string EvidenceReference { get; private set; } = string.Empty;
    public string EvidenceKey { get; private set; } = string.Empty;
    public Guid? VerifiedByUserId { get; private set; }
    public string? ProviderKey { get; private set; }
    public string? ProviderEventId { get; private set; }
    public DateTime PaidAtUtc { get; private set; }
    public DateTime VerifiedAtUtc { get; private set; }
    public string? Notes { get; private set; }

    public static Payment Create(Guid tenantId, Guid requestId, Guid customerId, decimal amount, string currency, PaymentMethod method, VerificationSource source, string evidence, Guid? verifiedBy,
        string? providerKey, string? providerEventId, DateTime paidAtUtc, string? notes, DateTime nowUtc)
    {
        var ev = Guard.Required(evidence, 150, "Evidence reference");
        var key = Money.EvidenceKey(ev);
        if (key.Length < 3) throw new DomainException("The evidence reference (for example the receipt or transfer id) must contain at least 3 letters or digits.");
        if (source == VerificationSource.Manual && verifiedBy is null) throw new DomainException("A manual verification needs the verifying person.");
        if (paidAtUtc > nowUtc.AddMinutes(5)) throw new DomainException("The payment date cannot be in the future.");
        return new()
        {
            TenantId = tenantId, PaymentRequestId = requestId, CustomerId = customerId, Amount = Money.Amount(amount, "Amount", allowZero: false), Currency = Money.Currency(currency), Method = method, Source = source,
            EvidenceReference = ev, EvidenceKey = key, VerifiedByUserId = verifiedBy, ProviderKey = providerKey, ProviderEventId = providerEventId, PaidAtUtc = paidAtUtc, VerifiedAtUtc = nowUtc, Notes = Guard.Optional(notes, 500, "Notes"),
        };
    }
}

/// <summary>Tenant configuration of ONE payment provider webhook. The platform ships no provider; an adapter must be installed for a key to be usable.</summary>
public class PaymentProviderConfig : AuditableEntity, ITenantOwned
{
    private PaymentProviderConfig() { }
    public Guid TenantId { get; private set; }
    public string ProviderKey { get; private set; } = string.Empty;
    /// <summary>Random id in the webhook URL (resolves the tenant). Not a secret: the signature is.</summary>
    public Guid WebhookId { get; private set; } = Guid.NewGuid();
    public string SecretProtected { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;

    public static PaymentProviderConfig Create(Guid tenantId, string providerKey, string secretProtected, DateTime nowUtc, string? actor) =>
        new() { TenantId = tenantId, ProviderKey = Guard.Required(providerKey, 50, "Provider"), SecretProtected = secretProtected, CreatedAtUtc = nowUtc, CreatedBy = actor };

    public void SetSecret(string secretProtected, DateTime nowUtc) { SecretProtected = secretProtected; UpdatedAtUtc = nowUtc; }
    public void SetActive(bool active, DateTime nowUtc) { IsActive = active; UpdatedAtUtc = nowUtc; }
}

/// <summary>One delivery of a provider event. (tenant, provider, external id) is unique: a redelivery can never be applied twice.</summary>
public class PaymentEvent : Entity, ITenantOwned, IAppendOnly
{
    private PaymentEvent() { }
    public Guid TenantId { get; private set; }
    public string ProviderKey { get; private set; } = string.Empty;
    public string ExternalEventId { get; private set; } = string.Empty;
    public Guid? PaymentRequestId { get; private set; }
    public PaymentEventOutcome Outcome { get; private set; }
    public string? Reason { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public DateTime ReceivedAtUtc { get; private set; }

    public static PaymentEvent Create(Guid tenantId, string providerKey, string externalEventId, Guid? requestId, PaymentEventOutcome outcome, string? reason, string kind, DateTime nowUtc) =>
        new() { TenantId = tenantId, ProviderKey = providerKey, ExternalEventId = externalEventId.Length <= 150 ? externalEventId : externalEventId[..150], PaymentRequestId = requestId, Outcome = outcome,
                Reason = reason is { Length: > 300 } ? reason[..300] : reason, Kind = kind.Length <= 40 ? kind : kind[..40], ReceivedAtUtc = nowUtc };
}
