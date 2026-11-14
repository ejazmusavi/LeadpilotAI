using System.ComponentModel.DataAnnotations;
using LeadPilot.Application.Billing;
using LeadPilot.Application.Common;
using LeadPilot.Domain.Billing;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Tenancy;

namespace LeadPilot.Web.Models;

public static class BillingFormat
{
    public static string Amount(long v, long factor, string unit) => factor > 1 ? $"{v / (double)factor:0.##} {unit}" : $"{v:N0} {unit}";
    public static string Used(UsageLine l) => Amount(l.Used, l.UiFactor, l.Unit);
    public static string Limit(UsageLine l) => l.Limit is null ? "Unlimited" : l.Limit == 0 ? "Not included" : Amount(l.Limit.Value, l.UiFactor, l.Unit);
    public static string Bar(UsageLine l) => l.Percent is null ? "bg-secondary" : l.Percent >= 100 ? "bg-danger" : l.Percent >= 80 ? "bg-warning" : "bg-success";
    public static string Money(decimal amount, string currency) => $"{amount.ToString("N2", System.Globalization.CultureInfo.InvariantCulture)} {currency}";
    public static string StateBadge(SubscriptionState s) => s switch { SubscriptionState.Active => "bg-success", SubscriptionState.Trialing => "bg-info", SubscriptionState.PastDue => "bg-warning text-dark", _ => "bg-danger" };
}

public sealed class BillingIndexViewModel { public BillingOverview Overview { get; set; } = default!; public bool CanManage { get; set; } }
public sealed class InvoiceListViewModel { public PagedResult<InvoiceDto> Result { get; set; } = default!; }
public sealed class InvoiceViewModel
{
    public InvoiceDto Invoice { get; set; } = default!; public bool CanManage { get; set; }
    public PaymentReportForm Form { get; set; } = new();
}
public sealed class PaymentReportForm
{
    [Required, StringLength(150)] public string EvidenceReference { get; set; } = string.Empty;
    public DateTime? PaidOn { get; set; }
    [StringLength(500)] public string? Note { get; set; }
}
public sealed class PlansViewModel { public IReadOnlyList<PlanDto> Plans { get; set; } = []; public SubscriptionDto? Current { get; set; } public bool CanManage { get; set; } public PlanChangeDto? OpenChange { get; set; } }
public sealed class PlanPreviewViewModel { public PlanChangePreview Preview { get; set; } = default!; }
public sealed class BillingPaymentsViewModel { public IReadOnlyList<BillingPaymentDto> Payments { get; set; } = []; public IReadOnlyList<PlanChangeDto> Changes { get; set; } = []; }

public sealed class SignUpViewModel
{
    [Required, StringLength(200)] public string WorkspaceName { get; set; } = string.Empty;
    [Required, StringLength(200)] public string DisplayName { get; set; } = string.Empty;
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = string.Empty;
    [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
    public string? Timezone { get; set; }
    /// <summary>Honeypot: hidden from people by CSS; bots fill it.</summary>
    public string? Website { get; set; }
    public string? CaptchaToken { get; set; }
}
public sealed class ResendViewModel { [Required, EmailAddress] public string Email { get; set; } = string.Empty; }

public sealed class OnboardingViewModel { public OnboardingOverview Overview { get; set; } = default!; }

// ---- platform ----
public sealed class PlatformTenantsViewModel { public string? Query { get; set; } public string? Status { get; set; } public PagedResult<PlatformTenantRow> Result { get; set; } = default!; }
public sealed class PlatformTenantViewModel
{
    public PlatformTenantDetail Detail { get; set; } = default!; public IReadOnlyList<PlanDto> Plans { get; set; } = [];
}
public sealed class GrantForm { [Required] public Guid PlanId { get; set; } public DateTime? PeriodEnd { get; set; } [Required, StringLength(300)] public string Reason { get; set; } = string.Empty; }
public sealed class LimitOverrideForm
{
    [Required] public UsageDimension Dimension { get; set; } public long? Value { get; set; } public bool Unlimited { get; set; } public DateTime? ExpiresAt { get; set; }
    [Required, StringLength(300)] public string Reason { get; set; } = string.Empty;
}
public sealed class FeatureOverrideForm { [Required] public string Feature { get; set; } = string.Empty; public bool Enabled { get; set; } public DateTime? ExpiresAt { get; set; } [Required, StringLength(300)] public string Reason { get; set; } = string.Empty; }
public sealed class SuspendForm { [Required, StringLength(300)] public string Reason { get; set; } = string.Empty; public SuspensionKind Kind { get; set; } = SuspensionKind.Manual; }

public sealed class PlatformPlanForm
{
    public Guid? Id { get; set; }
    [Required, StringLength(40)] public string Code { get; set; } = string.Empty;
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [StringLength(1000)] public string? Description { get; set; }
    public PlanCycle Cycle { get; set; } = PlanCycle.Monthly;
    public int? CustomPeriodDays { get; set; }
    public decimal Price { get; set; }
    [Required, StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = "USD";
    public bool IsTrial { get; set; }
    public int? TrialDays { get; set; }
    public PlanVisibility Visibility { get; set; } = PlanVisibility.Public;
    public int SortOrder { get; set; }
    /// <summary>One entry per dimension, in the unit shown on screen (storage in MB). Empty = unlimited.</summary>
    public Dictionary<UsageDimension, long?> Limits { get; set; } = [];
    public List<string> Features { get; set; } = [];
}
public sealed class PlatformPlansViewModel { public IReadOnlyList<PlanDto> Plans { get; set; } = []; }

public sealed class QueueViewModel { public PagedResult<QueueItem> Result { get; set; } = default!; public string? Status { get; set; } public bool Queue { get; set; } }
public sealed class VerifyForm
{
    [Required] public Guid ForTenant { get; set; } [Required] public decimal Amount { get; set; } [Required, StringLength(3, MinimumLength = 3)] public string Currency { get; set; } = "USD";
    [Required] public PaymentMethod Method { get; set; } = PaymentMethod.BankTransfer; [Required, StringLength(150)] public string EvidenceReference { get; set; } = string.Empty;
    public DateTime? PaidAt { get; set; } [StringLength(500)] public string? Notes { get; set; }
}
public sealed class PlatformInvoiceViewModel { public InvoiceDto Invoice { get; set; } = default!; public VerifyForm Form { get; set; } = new(); }
public sealed class PlatformSettingsViewModel { public PlatformSettingsDto Settings { get; set; } = default!; public IReadOnlyList<PlanDto> Plans { get; set; } = []; }
public sealed class PlatformAuditViewModel { public string? Action { get; set; } public PagedResult<PlatformAuditRow> Result { get; set; } = default!; }
