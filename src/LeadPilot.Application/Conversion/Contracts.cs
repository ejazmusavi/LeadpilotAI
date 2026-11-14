using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Conversion;
using LeadPilot.Shared.Results;

namespace LeadPilot.Application.Conversion;

// ===================================================================================================================================================================
// Phase 10: demos, trials, customers, payments. Web and Api call the same services. The tenant always comes from the tenant context, never from a request.
// ===================================================================================================================================================================

public sealed class ConversionOptions
{
    public const string Section = "Conversion";
    /// <summary>Reminders / expiry notices one sweep handles per tenant per run.</summary>
    public int SweepBatch { get; set; } = 50;
    /// <summary>A reminder that is still "Processing" after this many minutes is picked up again (worker crash).</summary>
    public int StaleProcessingMinutes { get; set; } = 10;
    /// <summary>A reminder more than this many minutes overdue is skipped instead of sent late (a reminder after the demo started is noise).</summary>
    public int ReminderMaxLatenessMinutes { get; set; } = 30;
    public int MaxWebhookBytes { get; set; } = 256 * 1024;
}

public static class ConversionEventDetail
{
    public const string DemoBooked = "demo";
    public const string Trial = "trial";
    public const string Payment = "payment";
}

// ---------------------------------------------------------------- settings and reps

public sealed record ConversionSettingsDto(string TimeZoneId, int SlotMinutes, int MinNoticeMinutes, int MaxDaysAhead, AssignmentStrategy Assignment, string? MeetingLinkTemplate, bool ConfirmAutomatedBookings,
    bool RemindersEnabled, Guid? ReminderChannelAccountId, string ReminderOffsets, string ReminderBody, int TrialDefaultDays, int TrialExpiryNoticeHours, bool TrialCustomerNoticesEnabled, string TrialNoticeBody,
    int PaymentDueDays, string? PaymentInstructions, int Version);

public sealed record SaveConversionSettingsRequest(string TimeZoneId, int SlotMinutes, int MinNoticeMinutes, int MaxDaysAhead, AssignmentStrategy Assignment, string? MeetingLinkTemplate, bool ConfirmAutomatedBookings,
    bool RemindersEnabled, Guid? ReminderChannelAccountId, string? ReminderOffsets, string? ReminderBody, int TrialDefaultDays, int TrialExpiryNoticeHours, bool TrialCustomerNoticesEnabled, string? TrialNoticeBody,
    int PaymentDueDays, string? PaymentInstructions, int? ExpectedVersion = null) : TenantScopedRequest;

public sealed record RuleDto(DayOfWeek Day, int StartMinute, int EndMinute);
public sealed record ExceptionDto(Guid Id, DateTime Date, ExceptionKind Kind, int? StartMinute, int? EndMinute, string? Note);
public sealed record DemoRepDto(Guid Id, Guid UserId, string UserName, bool IsActive, string? TimeZoneId, string EffectiveTimeZoneId, int BufferMinutes, int MaxPerDay, IReadOnlyList<RuleDto> Rules, IReadOnlyList<ExceptionDto> Exceptions);

public sealed record SaveRepRequest(Guid UserId, bool IsActive, string? TimeZoneId, int BufferMinutes, int MaxPerDay) : TenantScopedRequest;
public sealed record SaveRulesRequest(IReadOnlyList<RuleDto> Rules) : TenantScopedRequest;
public sealed record AddExceptionRequest(DateTime Date, ExceptionKind Kind, int? StartMinute, int? EndMinute, string? Note) : TenantScopedRequest;

public interface IConversionSettingsService
{
    Task<ConversionSettingsDto> GetAsync(CancellationToken ct = default);
    Task<Result<ConversionSettingsDto>> SaveAsync(SaveConversionSettingsRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<DemoRepDto>> RepsAsync(CancellationToken ct = default);
    Task<Result<DemoRepDto>> GetRepAsync(Guid repId, CancellationToken ct = default);
    Task<Result<DemoRepDto>> SaveRepAsync(SaveRepRequest request, CancellationToken ct = default);
    Task<Result<DemoRepDto>> SaveRulesAsync(Guid repId, SaveRulesRequest request, CancellationToken ct = default);
    Task<Result<DemoRepDto>> AddExceptionAsync(Guid repId, AddExceptionRequest request, CancellationToken ct = default);
    Task<Result<DemoRepDto>> RemoveExceptionAsync(Guid repId, Guid exceptionId, CancellationToken ct = default);
    /// <summary>Active members of the workspace that are not yet reps (for the "add rep" list).</summary>
    Task<IReadOnlyList<LookupItem>> CandidateRepsAsync(CancellationToken ct = default);
}

// ---------------------------------------------------------------- demos

public sealed record SlotDto(string SlotId, DateTime StartUtc, DateTime EndUtc, string LocalStart, string LocalDate, int FreeReps);
public sealed record DemoReminderDto(Guid Id, int OffsetMinutes, DateTime DueAtUtc, ReminderStatus Status, string? Reason, Guid? MessageId, DateTime? ProcessedAtUtc);
public sealed record DemoHistoryDto(string Type, string Summary, string? ActorName, string ActorKind, DateTime OccurredAtUtc);
public sealed record DemoDto(Guid Id, Guid LeadId, string LeadName, Guid? ContactId, string? ContactName, Guid RepUserId, string RepName, DateTime StartUtc, DateTime EndUtc, string LocalStart, string LocalEnd, string LocalDate,
    string TimeZoneId, DemoStatus Status, DemoSource Source, string? MeetingLink, string? Notes, string? CancelReason, int Generation, DateTime CreatedAtUtc,
    IReadOnlyList<DemoReminderDto> Reminders, IReadOnlyList<DemoHistoryDto> History);

public sealed record DemoQuery
{
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public DemoStatus? Status { get; init; }
    public Guid? LeadId { get; init; }
    public Guid? RepUserId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>SlotId comes from the slot list. A time that is not in the list is refused. The tenant, the source and the rep assignment are never client choices (the rep may be a preference, honoured only if that rep is free).</summary>
public sealed record BookDemoRequest(Guid LeadId, string SlotId, Guid? ContactId = null, string? Notes = null, string? MeetingLink = null, Guid? PreferredRepUserId = null) : TenantScopedRequest;
public sealed record RescheduleDemoRequest(string SlotId, Guid? PreferredRepUserId = null) : TenantScopedRequest;
public sealed record CancelDemoRequest(string? Reason) : TenantScopedRequest;
public sealed record UpdateDemoRequest(string? MeetingLink, string? Notes) : TenantScopedRequest;

public interface IDemoService
{
    Task<IReadOnlyList<SlotDto>> SlotsAsync(DateTime fromUtc, DateTime toUtc, Guid? repUserId = null, CancellationToken ct = default);
    Task<PagedResult<DemoDto>> ListAsync(DemoQuery query, CancellationToken ct = default);
    Task<Result<DemoDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<DemoDto>> BookAsync(BookDemoRequest request, DemoSource source = DemoSource.Staff, CancellationToken ct = default);
    Task<Result<DemoDto>> RescheduleAsync(Guid id, RescheduleDemoRequest request, CancellationToken ct = default);
    Task<Result<DemoDto>> CancelAsync(Guid id, CancelDemoRequest request, CancellationToken ct = default);
    /// <summary>Requested → Booked.</summary>
    Task<Result<DemoDto>> AcceptAsync(Guid id, CancellationToken ct = default);
    Task<Result<DemoDto>> ConfirmAsync(Guid id, CancellationToken ct = default);
    Task<Result<DemoDto>> CompleteAsync(Guid id, CancellationToken ct = default);
    Task<Result<DemoDto>> NoShowAsync(Guid id, CancellationToken ct = default);
    Task<Result<DemoDto>> UpdateAsync(Guid id, UpdateDemoRequest request, CancellationToken ct = default);
}

/// <summary>Sends and records the reminders that are due. Called by the Hangfire sweep inside the tenant scope.</summary>
public interface IDemoReminderService
{
    /// <summary>Returns how many reminders were handled (sent, skipped or failed).</summary>
    Task<int> ProcessDueAsync(CancellationToken ct = default);
}

// ---------------------------------------------------------------- ports

public sealed record MeetingLinkRequest(Guid DemoId, DateTime StartUtc, string LeadName);
public sealed record MeetingLink(string Url, string Source);

/// <summary>
/// Gives a demo a meeting address. The shipped provider only expands the workspace's own template (https://host/{code}) or returns nothing; staff can always type a link. NOTHING is created at Google Meet, Zoom or
/// Teams: a real integration would be a second implementation of this port.
/// </summary>
public interface IMeetingLinkProvider
{
    Task<MeetingLink?> ProvideAsync(MeetingLinkRequest request, string? template, CancellationToken ct = default);
}

/// <summary>What a messaging reminder or notice needs. The implementation lives with the messaging module and goes through the normal outbound pipeline.</summary>
public sealed record ServiceNoticeRequest(Guid LeadId, Guid? ContactId, Guid ChannelAccountId, string Text, string IdempotencyKey);
public enum ServiceNoticeStatus { Queued = 1, Skipped, Failed }
public sealed record ServiceNoticeOutcome(ServiceNoticeStatus Status, Guid? MessageId, string? Code, string? Reason);

/// <summary>
/// The ONLY way the conversion module messages a customer (demo reminders, trial expiry notices). Policy (docs/SECURITY.md): these are SERVICE messages about something the customer asked for. They are never sent to a
/// suppressed or opted-out number, to an archived or excluded lead or to an invalid number; and only when the customer has a recorded WhatsApp SERVICE consent or wrote to us inside the service window. Otherwise the
/// notice is Skipped with the reason (never silently dropped, never forced). Everything then goes through the Phase 5 outbound pipeline (account state, rate limits, idempotency key).
/// </summary>
public interface IServiceNoticeSender
{
    Task<ServiceNoticeOutcome> SendAsync(ServiceNoticeRequest request, CancellationToken ct = default);
}

// ---------------------------------------------------------------- trials

public sealed record TrialDto(Guid Id, Guid LeadId, string LeadName, Guid? ContactId, Guid? PlanId, string? PlanName, string? ProductName, TrialStatus Status, TrialSource Source, int DurationDays,
    DateTime? StartedAtUtc, DateTime? ExpiresAtUtc, string? ProvisioningNote, string? ExternalAccountRef, string? LoginUrl, DateTime? LastActivityAtUtc, int LoginCount, int ActiveUsers, string? UsageNotes,
    DateTime? ExpiryNoticeAtUtc, DateTime? EndedAtUtc, string? EndReason, Guid? ConvertedCustomerId, DateTime CreatedAtUtc, bool ProvisioningRequiresStaff, string ProvisioningMessage, IReadOnlyList<TrialEventDto> Events);
public sealed record TrialEventDto(string Type, string Summary, string? ActorName, string ActorKind, DateTime OccurredAtUtc);

public sealed record TrialQuery { public TrialStatus? Status { get; init; } public Guid? LeadId { get; init; } public int Page { get; init; } = 1; public int PageSize { get; init; } = 25; }

public sealed record CreateTrialRequest(Guid LeadId, Guid? PlanId = null, int? DurationDays = null, string? Notes = null, Guid? ContactId = null) : TenantScopedRequest;
public sealed record ProvisionTrialRequest(string? ExternalAccountRef, string? LoginUrl, string? Note) : TenantScopedRequest;
public sealed record ExtendTrialRequest(int Days) : TenantScopedRequest;
public sealed record CancelTrialRequest(string? Reason) : TenantScopedRequest;
public sealed record RecordTrialUsageRequest(DateTime? LastActivityUtc, int LoginCount, int ActiveUsers, string? Notes) : TenantScopedRequest;

public interface ITrialService
{
    Task<PagedResult<TrialDto>> ListAsync(TrialQuery query, CancellationToken ct = default);
    Task<Result<TrialDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<TrialDto>> CreateAsync(CreateTrialRequest request, TrialSource source = TrialSource.Staff, CancellationToken ct = default);
    /// <summary>Staff finished creating the account in the product: the trial becomes Active and the clock starts.</summary>
    Task<Result<TrialDto>> MarkProvisionedAsync(Guid id, ProvisionTrialRequest request, CancellationToken ct = default);
    Task<Result<TrialDto>> ExtendAsync(Guid id, ExtendTrialRequest request, CancellationToken ct = default);
    Task<Result<TrialDto>> CancelAsync(Guid id, CancelTrialRequest request, CancellationToken ct = default);
    Task<Result<TrialDto>> RecordUsageAsync(Guid id, RecordTrialUsageRequest request, CancellationToken ct = default);
}

/// <summary>Expires finished trials and sends the expiry notices. Called by the Hangfire sweep inside the tenant scope; idempotent.</summary>
public interface ITrialLifecycleService
{
    Task<int> SweepAsync(CancellationToken ct = default);
}

public sealed record TrialProvisionRequest(Guid TrialId, Guid LeadId, string LeadName, string? PlanName, string? ProductName, int DurationDays);
public sealed record TrialProvisionResult(bool Provisioned, string? ExternalAccountRef, string? LoginUrl, string Message);

/// <summary>
/// Creates the account in the PRODUCT. Provisioning is product-specific and therefore tenant/integration configuration. The shipped backend is record-only: it provisions nothing and says so ("staff must create the
/// account"), so a trial is never reported as ready when it is not. An HTTP/webhook backend would be another implementation of this port.
/// </summary>
public interface ITrialProvisioningBackend
{
    Task<TrialProvisionResult> ProvisionAsync(TrialProvisionRequest request, CancellationToken ct = default);
}

// ---------------------------------------------------------------- customers

public sealed record CustomerListItemDto(Guid Id, string Code, string Name, Guid LeadId, CustomerStatus Status, string? ActivePlan, int PendingSubscriptions, DateTime ConvertedAtUtc);
public sealed record SubscriptionDto(Guid Id, string PlanName, string? ProductName, SubscriptionStatus Status, BillingCycle BillingCycle, DateTime? ActivatedAtUtc, Guid? ActivatedByPaymentId, DateTime? CancelledAtUtc, string? CancelReason);
public sealed record AgreementDto(Guid Id, Guid SubscriptionId, Guid OpportunityId, string Terms, decimal? ListPrice, decimal Price, string Currency, BillingCycle BillingCycle, decimal? DiscountPercent,
    string ApprovedByName, DateTime ApprovedAtUtc);
public sealed record PaymentDto(Guid Id, Guid PaymentRequestId, decimal Amount, string Currency, PaymentMethod Method, VerificationSource Source, string EvidenceReference, string? VerifiedByName, DateTime PaidAtUtc, DateTime VerifiedAtUtc, string? Notes);
public sealed record PaymentRequestDto(Guid Id, Guid CustomerId, string CustomerName, Guid SubscriptionId, string PlanName, string Reference, decimal Amount, string Currency, PaymentRequestStatus Status,
    DateTime? DueAtUtc, string? Instructions, string RequestedVia, DateTime CreatedAtUtc, DateTime? ClosedAtUtc, string? CloseReason, PaymentDto? Payment);
public sealed record TimelineItemDto(DateTime AtUtc, string Kind, string Summary, string? Actor);
public sealed record CustomerDto(Guid Id, string Code, string Name, Guid LeadId, Guid? PrimaryContactId, CustomerStatus Status, string? AccountReference, string? AccountNotes, DateTime ConvertedAtUtc, string? ConvertedByName,
    IReadOnlyList<SubscriptionDto> Subscriptions, IReadOnlyList<AgreementDto> Agreements, IReadOnlyList<PaymentRequestDto> PaymentRequests, IReadOnlyList<TimelineItemDto> Timeline);
public sealed record WonOpportunityDto(Guid OpportunityId, Guid LeadId, string LeadName, string Name, decimal Value, string Currency, DateTime WonAtUtc);

public sealed record CustomerQuery { public string? Search { get; init; } public CustomerStatus? Status { get; init; } public int Page { get; init; } = 1; public int PageSize { get; init; } = 25; }

/// <summary>
/// Converts a WON opportunity. Price and currency default to the catalog plan (never hardcoded); a different price is allowed and recorded together with the list price. The acting person is recorded as the approver.
/// </summary>
public sealed record ConvertOpportunityRequest(Guid OpportunityId, Guid PlanId, string Terms, BillingCycle BillingCycle, decimal? Price = null, string? Currency = null, Guid? TrialId = null, Guid? ContactId = null) : TenantScopedRequest;
public sealed record SetAccountRequest(string? AccountReference, string? AccountNotes) : TenantScopedRequest;
public sealed record CancelSubscriptionRequest(string? Reason) : TenantScopedRequest;

public interface ICustomerService
{
    Task<PagedResult<CustomerListItemDto>> ListAsync(CustomerQuery query, CancellationToken ct = default);
    Task<Result<CustomerDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<CustomerDto>> GetByLeadAsync(Guid leadId, CancellationToken ct = default);
    Task<IReadOnlyList<WonOpportunityDto>> WonOpportunitiesAsync(CancellationToken ct = default);
    Task<Result<CustomerDto>> ConvertAsync(ConvertOpportunityRequest request, CancellationToken ct = default);
    Task<Result<CustomerDto>> SetAccountAsync(Guid id, SetAccountRequest request, CancellationToken ct = default);
    Task<Result<CustomerDto>> CancelSubscriptionAsync(Guid customerId, Guid subscriptionId, CancelSubscriptionRequest request, CancellationToken ct = default);
}

// ---------------------------------------------------------------- payments

public sealed record PaymentRequestQuery { public PaymentRequestStatus? Status { get; init; } public Guid? CustomerId { get; init; } public int Page { get; init; } = 1; public int PageSize { get; init; } = 25; }

public sealed record CreatePaymentRequestRequest(Guid SubscriptionId, int? DueInDays = null, string? Instructions = null) : TenantScopedRequest;
public sealed record CancelPaymentRequestRequest(string? Reason) : TenantScopedRequest;
/// <summary>The evidence is what makes a payment verified. Amount and currency must equal the request exactly (no partial payments).</summary>
public sealed record VerifyPaymentRequest(decimal Amount, string Currency, PaymentMethod Method, string EvidenceReference, DateTime? PaidAtUtc = null, string? Notes = null) : TenantScopedRequest;

public interface IPaymentService
{
    Task<PagedResult<PaymentRequestDto>> ListAsync(PaymentRequestQuery query, CancellationToken ct = default);
    Task<Result<PaymentRequestDto>> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Creates the request for a customer's pending subscription (one open request per subscription). Staff path: the permission is the authorisation.</summary>
    Task<Result<PaymentRequestDto>> CreateAsync(CreatePaymentRequestRequest request, CancellationToken ct = default);
    Task<Result<PaymentRequestDto>> CancelAsync(Guid id, CancelPaymentRequestRequest request, CancellationToken ct = default);
    /// <summary>A person with the verify permission confirms that money arrived, with evidence. Activates the subscription in the same transaction.</summary>
    Task<Result<PaymentRequestDto>> VerifyManuallyAsync(Guid id, VerifyPaymentRequest request, CancellationToken ct = default);
}

// ---------------------------------------------------------------- provider webhooks (provider-neutral)

public enum ProviderEventKind { PaymentSucceeded = 1, PaymentFailed, Other }

public sealed record ParsedProviderEvent(string EventId, string Reference, decimal Amount, string Currency, ProviderEventKind Kind, DateTime OccurredAtUtc, string ProviderPaymentId);

/// <summary>
/// A payment provider's wire format. NO adapter ships with the product (no provider has been chosen): this is the seam where one is added. Verification comes first and uses the tenant's webhook secret; only a
/// body that passed it is parsed, and only a parsed event that matches an open payment request exactly can verify a payment.
/// </summary>
public interface IPaymentProviderAdapter
{
    string ProviderKey { get; }
    string DisplayName { get; }
    bool VerifySignature(IReadOnlyDictionary<string, string> headers, byte[] body, string secret);
    ParsedProviderEvent? Parse(byte[] body);
}

public enum WebhookOutcome { Applied = 1, Duplicate, Rejected, Ignored, InvalidSignature, NotFound, TooLarge, Malformed }
public sealed record WebhookResult(WebhookOutcome Outcome, string? Detail = null);

public sealed record ProviderConfigDto(Guid Id, string ProviderKey, string DisplayName, Guid WebhookId, bool IsActive, bool SecretStored);
public sealed record SaveProviderConfigRequest(string ProviderKey, string Secret, bool IsActive) : TenantScopedRequest;

public interface IPaymentProviderService
{
    IReadOnlyList<(string Key, string Name)> InstalledAdapters();
    Task<IReadOnlyList<ProviderConfigDto>> ListAsync(CancellationToken ct = default);
    Task<Result<ProviderConfigDto>> SaveAsync(SaveProviderConfigRequest request, CancellationToken ct = default);
}

public interface IPaymentWebhookService
{
    /// <summary>Anonymous endpoint core: resolves the tenant from the webhook id, verifies the signature FIRST, then applies the event idempotently inside that tenant.</summary>
    Task<WebhookResult> ReceiveAsync(Guid webhookId, IReadOnlyDictionary<string, string> headers, byte[] body, CancellationToken ct = default);
}
