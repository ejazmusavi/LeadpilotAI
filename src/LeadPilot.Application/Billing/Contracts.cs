using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Billing;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Shared.Results;

namespace LeadPilot.Application.Billing;

// ---------- entitlement and usage ----------

/// <summary>THE entitlement check. Every plan limit and feature gate in the product goes through this one service (see docs/ARCHITECTURE.md).</summary>
public interface IEntitlementService
{
    /// <summary>
    /// Transactional guard for something that is inserted right after the check (a lead, a message, a campaign, a document, an account, an invitation, a workflow run). Under a per-tenant lock it measures the usage,
    /// refuses with a typed <c>plan_limit_exceeded</c> error when <paramref name="quantity"/> more would pass the effective limit, and otherwise runs <paramref name="action"/> in the same transaction: the action's insert is
    /// visible to the next caller before the lock is released, so two requests racing for the last unit cannot both succeed. A failed result or an exception rolls everything back.
    /// </summary>
    Task<Result<T>> RunWithinLimitAsync<T>(UsageDimension dimension, long quantity, Func<CancellationToken, Task<Result<T>>> action, CancellationToken ct = default);
    /// <summary>For consumption recorded later and elsewhere (AI tokens, discovery calls): reserves an estimate for the duration of the call so concurrent callers see it. Dispose releases the hold.</summary>
    Task<Result<IAsyncDisposable>> HoldAsync(UsageDimension dimension, long estimate, CancellationToken ct = default);
    Task<Result> RequireFeatureAsync(string feature, CancellationToken ct = default);
    Task<EntitlementOverview> OverviewAsync(CancellationToken ct = default);
    /// <summary>The usage per dimension right now (used by the downgrade check and the platform views).</summary>
    Task<IReadOnlyDictionary<UsageDimension, long>> MeasureAllAsync(CancellationToken ct = default);
}

public sealed record UsageLine(UsageDimension Dimension, string Label, string Unit, bool IsPeriodic, long Used, long? Limit, LimitSource Source, string? OverrideReason, DateTime? OverrideExpiresAtUtc, double? Percent, long UiFactor);
public sealed record FeatureLine(string Key, string Label, bool Enabled, bool Overridden);
public sealed record SubscriptionDto(Guid PlanId, string PlanName, SubscriptionState State, DateTime PeriodStartUtc, DateTime PeriodEndUtc, DateTime? TrialEndsAtUtc, DateTime? GraceEndsAtUtc,
    Guid? PendingPlanId, string? PendingPlanName, decimal Price, string Currency, PlanCycle Cycle, string? GrantReason);
public sealed record UsageNoticeDto(UsageDimension Dimension, string Label, int Threshold, long Used, long Limit, DateTime PeriodStartUtc, DateTime CreatedAtUtc);
public sealed record EntitlementOverview(SubscriptionDto? Subscription, IReadOnlyList<UsageLine> Usage, IReadOnlyList<FeatureLine> Features, IReadOnlyList<UsageNoticeDto> Notices, bool EmailVerificationPending, bool IsPlatformTenant);

// ---------- plans ----------

public sealed record PlanDto(Guid Id, string Code, string Name, string? Description, PlanCycle Cycle, int? CustomPeriodDays, decimal Price, string Currency, bool IsTrial, int? TrialDays,
    PlanStatus Status, PlanVisibility Visibility, int SortOrder, bool IsSample, IReadOnlyDictionary<UsageDimension, long?> Limits, IReadOnlyList<string> Features, int TenantCount);

/// <summary>Limits: a key with a null value is unlimited, a key with 0 means "not included", a missing key is unlimited. Features: the keys that are included.</summary>
public sealed record SavePlanRequest(string Code, string Name, string? Description, PlanCycle Cycle, int? CustomPeriodDays, decimal Price, string Currency, bool IsTrial, int? TrialDays,
    PlanVisibility Visibility, int SortOrder, Dictionary<UsageDimension, long?>? Limits, List<string>? Features);

public interface IPlanService
{
    Task<IReadOnlyList<PlanDto>> ListAsync(bool includeArchived, CancellationToken ct = default);
    Task<Result<PlanDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<PlanDto>> CreateAsync(SavePlanRequest request, CancellationToken ct = default);
    Task<Result<PlanDto>> UpdateAsync(Guid id, SavePlanRequest request, CancellationToken ct = default);
    Task<Result<PlanDto>> ArchiveAsync(Guid id, CancellationToken ct = default);
    Task<Result<PlanDto>> RestoreAsync(Guid id, CancellationToken ct = default);
}

// ---------- tenant billing ----------

public sealed record InvoiceLineDto(int Position, string Description, decimal Quantity, decimal UnitPrice, decimal Amount);
public sealed record InvoiceDto(Guid Id, string? Number, InvoiceKind Kind, InvoiceStatus Status, string Currency, decimal Subtotal, string? TaxName, decimal TaxRatePercent, decimal TaxAmount, decimal Total,
    DateTime? IssuedAtUtc, DateTime? DueAtUtc, DateTime? PaidAtUtc, DateTime? VoidedAtUtc, string? VoidReason, DateTime PeriodStartUtc, DateTime PeriodEndUtc, string PlanName,
    string BillToName, string? SellerText, string? PaymentInstructions, bool HasOpenReport, IReadOnlyList<InvoiceLineDto> Lines, Guid TenantId);
public sealed record BillingPaymentDto(Guid Id, Guid InvoiceId, string? InvoiceNumber, decimal Amount, string Currency, PaymentMethod Method, VerificationSource Source, string EvidenceReference, DateTime PaidAtUtc, DateTime VerifiedAtUtc);
public sealed record PaymentReportRequest(string EvidenceReference, DateTime? PaidOn, string? Note) : TenantScopedRequest;
public sealed record PlanChangeRequestBody(Guid PlanId) : TenantScopedRequest;

public sealed record PlanChangePreview(Guid FromPlanId, string FromPlanName, Guid ToPlanId, string ToPlanName, PlanChangeKind Kind, decimal NewPrice, decimal Credit, decimal Total, string Currency,
    bool Allowed, string? BlockedReason, IReadOnlyList<Exceedance> Exceeds, string Policy);
public sealed record PlanChangeDto(Guid Id, Guid FromPlanId, string FromPlanName, Guid ToPlanId, string ToPlanName, PlanChangeKind Kind, PlanChangeStatus Status, Guid? InvoiceId, DateTime? EffectiveAtUtc,
    decimal CreditAmount, string? Currency, DateTime CreatedAtUtc, string? CloseReason);

public sealed record BillingOverview(EntitlementOverview Entitlements, IReadOnlyList<InvoiceDto> OpenInvoices, PlanChangeDto? OpenChange, long InvoiceCount);

public interface IBillingService
{
    Task<BillingOverview> OverviewAsync(CancellationToken ct = default);
    Task<PagedResult<InvoiceDto>> ListInvoicesAsync(int page, int pageSize, CancellationToken ct = default);
    Task<Result<InvoiceDto>> GetInvoiceAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<BillingPaymentDto>> ListPaymentsAsync(CancellationToken ct = default);
    Task<Result<InvoiceDto>> ReportPaymentAsync(Guid invoiceId, PaymentReportRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<PlanDto>> AvailablePlansAsync(CancellationToken ct = default);
    Task<Result<PlanChangePreview>> PreviewPlanChangeAsync(Guid planId, CancellationToken ct = default);
    Task<Result<PlanChangeDto>> RequestPlanChangeAsync(Guid planId, CancellationToken ct = default);
    Task<Result> CancelPlanChangeAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<PlanChangeDto>> ListPlanChangesAsync(CancellationToken ct = default);
}

// ---------- platform administration ----------

public sealed record VerifyBillingPaymentRequest(decimal Amount, string Currency, PaymentMethod Method, string EvidenceReference, DateTime? PaidAtUtc, string? Notes);
public sealed record PlatformTenantRow(Guid Id, string Name, string Slug, TenantStatus Status, SuspensionKind? SuspensionKind, string? PlanName, SubscriptionState? State, DateTime? PeriodEndUtc,
    int Users, int Leads, long? LeadLimit, long? UserLimit, bool SelfRegistered, bool EmailVerified, DateTime CreatedAtUtc);
public sealed record OverrideDto(string Key, OverrideKind Kind, long? LimitValue, bool Unlimited, bool? FeatureEnabled, DateTime? ExpiresAtUtc, string Reason, DateTime CreatedAtUtc);
public sealed record SubscriptionEventDto(string Type, string Summary, DateTime OccurredAtUtc, string? Actor);
public sealed record PlatformTenantDetail(PlatformTenantRow Row, EntitlementOverview Entitlements, IReadOnlyList<OverrideDto> Overrides, IReadOnlyList<InvoiceDto> Invoices, IReadOnlyList<SubscriptionEventDto> Events, string? SuspensionReason);
public sealed record QueueItem(Guid TenantId, string TenantName, Guid InvoiceId, string? Number, decimal Total, string Currency, DateTime? DueAtUtc, DateTime? IssuedAtUtc, string? ReportedEvidence, DateTime? ReportedAtUtc, InvoiceKind Kind);
public sealed record GrantPlanRequest(Guid PlanId, DateTime? PeriodEndUtc, string Reason);
public sealed record SetLimitOverrideRequest(UsageDimension Dimension, long? Value, bool Unlimited, DateTime? ExpiresAtUtc, string Reason);
public sealed record SetFeatureOverrideRequest(string Feature, bool Enabled, DateTime? ExpiresAtUtc, string Reason);
public sealed record SuspendTenantBillingRequest(string? Reason, SuspensionKind Kind = SuspensionKind.Manual);

public sealed record PlatformSettingsDto(bool RegistrationEnabled, Guid? RegistrationPlanId, Guid? DefaultPlanId, int GraceDays, int RenewalLeadDays, int InvoiceDueDays, string? TaxName, decimal TaxRatePercent,
    string? IssuerText, string? PaymentInstructions, string InvoicePrefix, int MaxRegistrationsPerIpPerHour, int MaxRegistrationsPerDomainPerDay, string? ExtraBlockedDomains, bool CaptchaRequired);
public sealed record PlatformAuditRow(DateTime CreatedAtUtc, string Action, string? EntityType, string Summary, string? ActorEmail, Guid TenantId, string? TenantName, string? IpAddress);

public interface IPlatformSettings
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default);
    Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default);
    Task<PlatformSettingsDto> GetAllAsync(CancellationToken ct = default);
    Task<Result<PlatformSettingsDto>> SaveAsync(PlatformSettingsDto settings, CancellationToken ct = default);
}

public interface IPlatformBillingService
{
    Task<PagedResult<PlatformTenantRow>> ListTenantsAsync(string? search, TenantStatus? status, int page, int pageSize, CancellationToken ct = default);
    Task<Result<PlatformTenantDetail>> GetTenantAsync(Guid tenantId, CancellationToken ct = default);
    Task<Result<PlatformTenantDetail>> GrantPlanAsync(Guid tenantId, GrantPlanRequest request, CancellationToken ct = default);
    Task<Result<PlatformTenantDetail>> SetLimitOverrideAsync(Guid tenantId, SetLimitOverrideRequest request, CancellationToken ct = default);
    Task<Result<PlatformTenantDetail>> SetFeatureOverrideAsync(Guid tenantId, SetFeatureOverrideRequest request, CancellationToken ct = default);
    Task<Result<PlatformTenantDetail>> RemoveOverrideAsync(Guid tenantId, string key, CancellationToken ct = default);
    Task<Result<PlatformTenantDetail>> SuspendAsync(Guid tenantId, SuspendTenantBillingRequest request, CancellationToken ct = default);
    Task<Result<PlatformTenantDetail>> ReactivateAsync(Guid tenantId, CancellationToken ct = default);
    Task<PagedResult<QueueItem>> VerificationQueueAsync(int page, int pageSize, CancellationToken ct = default);
    Task<PagedResult<QueueItem>> InvoicesAsync(InvoiceStatus? status, int page, int pageSize, CancellationToken ct = default);
    Task<Result<InvoiceDto>> VerifyPaymentAsync(Guid tenantId, Guid invoiceId, VerifyBillingPaymentRequest request, CancellationToken ct = default);
    Task<Result<InvoiceDto>> VoidInvoiceAsync(Guid tenantId, Guid invoiceId, string reason, CancellationToken ct = default);
    Task<Result<InvoiceDto>> GetInvoiceAsync(Guid tenantId, Guid invoiceId, CancellationToken ct = default);
    Task<PagedResult<PlatformAuditRow>> AuditAsync(string? action, int page, int pageSize, CancellationToken ct = default);
}

/// <summary>Runs inside ONE tenant's scope: the renewal / grace / suspension step for that tenant's subscription. Idempotent.</summary>
public interface IBillingLifecycleService
{
    Task<LifecycleOutcome> ProcessAsync(CancellationToken ct = default);
}

public enum LifecycleOutcome { NothingToDo = 0, RenewalInvoiceIssued = 1, FreeRenewed = 2, MovedToGrace = 3, Suspended = 4, PendingDowngradeCancelled = 5 }

// ---------- registration and onboarding ----------

public sealed record RegisterRequest(string WorkspaceName, string DisplayName, string Email, string Password, string? Timezone, string? Website, string? CaptchaToken);
public sealed record RegisterResult(Guid TenantId, string Slug, bool VerificationEmailSent);
public sealed record RegistrationContext(string? IpAddress);

/// <summary>Port: a CAPTCHA provider (hCaptcha, Turnstile, reCAPTCHA...). The product ships none; the default says so and, when the platform requires a CAPTCHA, registration is refused instead of silently skipping it.</summary>
public interface ICaptchaVerifier
{
    bool IsConfigured { get; }
    Task<bool> VerifyAsync(string? token, string? ipAddress, CancellationToken ct = default);
}

/// <summary>Port: decides whether an e-mail domain is a throw-away mailbox provider. The default is a built-in list plus the platform setting <c>registration.blocked_domains</c>.</summary>
public interface IDisposableDomainPolicy
{
    Task<bool> IsBlockedAsync(string domain, CancellationToken ct = default);
}

public interface IRegistrationService
{
    Task<bool> IsOpenAsync(CancellationToken ct = default);
    Task<Result<RegisterResult>> RegisterAsync(RegisterRequest request, RegistrationContext context, CancellationToken ct = default);
    Task<Result> VerifyEmailAsync(Guid userId, string token, CancellationToken ct = default);
    /// <summary>Always succeeds from the caller's point of view (no account enumeration); sends nothing for unknown or already-verified addresses.</summary>
    Task<Result> ResendVerificationAsync(string email, RegistrationContext context, CancellationToken ct = default);
}

public sealed record OnboardingStep(string Key, string Title, string Description, bool Done, string Controller, string Action, string? Detail);
public sealed record OnboardingOverview(IReadOnlyList<OnboardingStep> Steps, int DoneCount, bool Complete);

public interface IOnboardingService
{
    Task<OnboardingOverview> GetAsync(CancellationToken ct = default);
}
