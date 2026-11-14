using System.Data;
using System.Security.Cryptography;
using LeadPilot.Application.Agents;
using LeadPilot.Application.Common;
using LeadPilot.Application.Conversion;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Crm;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Infrastructure.Services.Tenancy;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Conversion;

/// <summary>
/// Payment requests and their verification. THE rule: a subscription becomes Active only inside <see cref="PaymentVerificationCore"/>, which is reached from exactly two places: a person with Payments.Verify who
/// recorded evidence (<see cref="VerifyManuallyAsync"/>) and a signature-verified provider event that matches an open request exactly (PaymentWebhookService). A payment request, an AI tool call, a workflow action,
/// a chat message or a "paid" claim never reach it.
/// </summary>
public sealed class PaymentService(CrmContext crm, PaymentVerificationCore core, ILogger<PaymentService> logger) : IPaymentService
{
    private ConversionSupport Support => new(crm);
    private const string RefAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public async Task<PagedResult<PaymentRequestDto>> ListAsync(PaymentRequestQuery q, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(q.Page, q.PageSize);
        var query = crm.Db.PaymentRequests.AsNoTracking().Where(p => p.TenantId == tenantId);
        if (q.Status is { } st) query = query.Where(p => p.Status == st);
        if (q.CustomerId is { } c) query = query.Where(p => p.CustomerId == c);
        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(p => p.CreatedAtUtc).ThenBy(p => p.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<PaymentRequestDto>(await MapAsync(rows, ct), page, size, total);
    }

    public async Task<Result<PaymentRequestDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var p = await crm.Db.PaymentRequests.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        return p is null ? Result.Failure<PaymentRequestDto>(Error.NotFound("Payment request")) : Result.Success((await MapAsync([p], ct))[0]);
    }

    public Task<Result<PaymentRequestDto>> CreateAsync(CreatePaymentRequestRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var created = await CreateCoreAsync(r.SubscriptionId, r.DueInDays, r.Instructions, "staff", null, ct);
            return created.IsFailure ? Result.Failure<PaymentRequestDto>(created.Error) : Result.Success((await MapAsync([created.Value], ct))[0]);
        }, logger);

    /// <summary>Creates the request for a PENDING subscription from its recorded agreement (amount and currency are the agreement's: never typed in, never from a chat).</summary>
    internal async Task<Result<PaymentRequest>> CreateCoreAsync(Guid subscriptionId, int? dueInDays, string? instructions, string via, Guid? approvalId, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var sub = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == subscriptionId && s.TenantId == tenantId, ct);
        if (sub is null) return Result.Failure<PaymentRequest>(Error.NotFound("Subscription"));
        if (sub.Status != SubscriptionStatus.Pending) return Result.Failure<PaymentRequest>(Error.Validation($"A payment request is only for a pending subscription (this one is {sub.Status})."));
        var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == sub.CustomerId && c.TenantId == tenantId, ct);
        var agreement = await db.CommercialAgreements.AsNoTracking().FirstOrDefaultAsync(a => a.SubscriptionId == sub.Id && a.TenantId == tenantId, ct);
        if (agreement is null) return Result.Failure<PaymentRequest>(Error.Validation("There is no recorded commercial agreement for this subscription."));
        if (await db.PaymentRequests.AnyAsync(p => p.TenantId == tenantId && p.SubscriptionId == sub.Id && p.Status == PaymentRequestStatus.Open, ct))
            return Result.Failure<PaymentRequest>(Error.Conflict("This subscription already has an open payment request. Cancel it first to create another."));
        var s = await Support.SettingsAsync(ct);
        var days = dueInDays ?? s.PaymentDueDays;
        if (days is < 0 or > 365) return Result.Failure<PaymentRequest>(Error.Validation("Payment is due 0 to 365 days after the request."));
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var request = PaymentRequest.Create(tenantId, customer.Id, sub.Id, agreement.Id, NewReference(), agreement.Price, agreement.Currency, now.AddDays(days), instructions ?? s.PaymentInstructions, via, approvalId, now, crm.Actor);
            db.PaymentRequests.Add(request);
            crm.Activity(customer.LeadId, LeadActivityType.Updated, $"Payment request {request.Reference} created for {request.Amount:0.00} {request.Currency} ({via}). Nothing is charged and the subscription stays pending until a verified payment.", new { request = request.Id, via });
            crm.Record("PaymentRequest.Created", "PaymentRequest", request.Id, $"Payment request {request.Reference} created for {request.Amount:0.00} {request.Currency} ({via}).");
            try { await db.SaveChangesAsync(ct); return Result.Success(request); }
            catch (DbUpdateException ex) when (ResultFlow.IsDuplicate(ex))
            {
                ConversionSupport.DetachAdded(db);
                if (await db.PaymentRequests.AnyAsync(p => p.TenantId == tenantId && p.SubscriptionId == sub.Id && p.Status == PaymentRequestStatus.Open, ct))
                    return Result.Failure<PaymentRequest>(Error.Conflict("This subscription already has an open payment request."));   // lost a race for the one open request
            }
        }
        return Result.Failure<PaymentRequest>(Error.Conflict("A payment reference could not be generated. Try again."));
    }

    private static string NewReference() => "PR-" + new string(Enumerable.Range(0, 8).Select(_ => RefAlphabet[RandomNumberGenerator.GetInt32(RefAlphabet.Length)]).ToArray());

    public Task<Result<PaymentRequestDto>> CancelAsync(Guid id, CancelPaymentRequestRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            var p = await db.PaymentRequests.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            if (p is null) return Result.Failure<PaymentRequestDto>(Error.NotFound("Payment request"));
            p.Cancel(r.Reason, now);
            var leadId = await db.Customers.AsNoTracking().Where(c => c.Id == p.CustomerId && c.TenantId == tenantId).Select(c => c.LeadId).FirstAsync(ct);
            crm.Activity(leadId, LeadActivityType.Updated, $"Payment request {p.Reference} cancelled.", new { request = id });
            crm.Record("PaymentRequest.Cancelled", "PaymentRequest", id, $"Payment request {p.Reference} cancelled.");
            await db.SaveChangesAsync(ct);
            return Result.Success((await MapAsync([p], ct))[0]);
        }, logger);

    public Task<Result<PaymentRequestDto>> VerifyManuallyAsync(Guid id, VerifyPaymentRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            if (crm.UserId is not { } user) return Result.Failure<PaymentRequestDto>(Error.Forbidden("A person must verify a payment."));
            if (r.Method == PaymentMethod.Provider) return Result.Failure<PaymentRequestDto>(Error.Validation("The 'Provider' method is only used for signature-verified provider events."));
            var applied = await core.ApplyAsync(id, r.Amount, r.Currency, r.Method, VerificationSource.Manual, r.EvidenceReference, user, null, null, (r.PaidAtUtc ?? crm.Now).ToUniversalTime(), r.Notes, null, ct);
            if (applied.IsFailure) return Result.Failure<PaymentRequestDto>(applied.Error);
            return await GetAsync(id, ct);
        }, logger);

    private async Task<List<PaymentRequestDto>> MapAsync(List<PaymentRequest> rows, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var db = crm.Db;
        var custIds = rows.Select(r => r.CustomerId).Distinct().ToList(); var subIds = rows.Select(r => r.SubscriptionId).Distinct().ToList(); var reqIds = rows.Select(r => r.Id).ToList();
        var customers = await db.Customers.AsNoTracking().Where(c => c.TenantId == tenantId && custIds.Contains(c.Id)).Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var subs = await db.Subscriptions.AsNoTracking().Where(s => s.TenantId == tenantId && subIds.Contains(s.Id)).Select(s => new { s.Id, s.PlanName }).ToDictionaryAsync(s => s.Id, s => s.PlanName, ct);
        var payments = await db.Payments.AsNoTracking().Where(p => p.TenantId == tenantId && reqIds.Contains(p.PaymentRequestId)).ToListAsync(ct);
        var names = await crm.UserNamesAsync(payments.Select(p => p.VerifiedByUserId), ct);
        return rows.Select(r => ToDto(r, customers.GetValueOrDefault(r.CustomerId) ?? "", subs.GetValueOrDefault(r.SubscriptionId) ?? "", payments.FirstOrDefault(p => p.PaymentRequestId == r.Id), names)).ToList();
    }

    internal static PaymentRequestDto ToDto(PaymentRequest p, string customerName, string planName, Payment? pay, IReadOnlyDictionary<Guid, string> names) =>
        new(p.Id, p.CustomerId, customerName, p.SubscriptionId, planName, p.Reference, p.Amount, p.Currency, p.Status, p.DueAtUtc, p.Instructions, p.RequestedVia, p.CreatedAtUtc, p.ClosedAtUtc, p.CloseReason,
            pay is null ? null : new PaymentDto(pay.Id, pay.PaymentRequestId, pay.Amount, pay.Currency, pay.Method, pay.Source, pay.EvidenceReference, pay.VerifiedByUserId is { } u ? names.GetValueOrDefault(u) : pay.ProviderKey, pay.PaidAtUtc, pay.VerifiedAtUtc, pay.Notes));
}

/// <summary>
/// The only code that turns money into an activated subscription. In one transaction: claim the open request (atomic conditional update, so two verifications race to exactly one winner), insert the Payment
/// (unique per request and per evidence reference), activate the pending subscription with that payment's id, activate the customer, write the lead history and the audit entry, and publish payment.confirmed
/// through the outbox. Amount and currency must equal the request exactly: no partial payments, no currency guessing.
/// </summary>
public sealed class PaymentVerificationCore(CrmContext crm, ILogger<PaymentVerificationCore> logger)
{
    public async Task<Result<Guid>> ApplyAsync(Guid requestId, decimal amount, string currency, PaymentMethod method, VerificationSource source, string evidence, Guid? verifiedBy,
        string? providerKey, string? providerEventId, DateTime paidAtUtc, string? notes, PaymentEvent? eventRow, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var request = await db.PaymentRequests.AsNoTracking().FirstOrDefaultAsync(p => p.Id == requestId && p.TenantId == tenantId, ct);
        if (request is null) return Result.Failure<Guid>(Error.NotFound("Payment request"));
        if (request.Status != PaymentRequestStatus.Open) return Result.Failure<Guid>(Error.Conflict($"This payment request is already {request.Status}."));
        string cur;
        try { cur = Money.Currency(currency); } catch (LeadPilot.Domain.Common.DomainException ex) { return Result.Failure<Guid>(Error.Validation(ex.Message)); }
        if (cur != request.Currency) return Result.Failure<Guid>(Error.Validation($"The currency must be {request.Currency}, as requested."));
        if (amount != request.Amount) return Result.Failure<Guid>(Error.Validation($"The amount must be exactly {request.Amount:0.00} {request.Currency}. Partial or different amounts are not accepted."));
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == request.SubscriptionId && s.TenantId == tenantId, ct);
        if (sub is null || sub.Status != SubscriptionStatus.Pending) return Result.Failure<Guid>(Error.Validation($"The subscription is {sub?.Status.ToString() ?? "missing"}, so it cannot be activated by this payment."));
        var customer = await db.Customers.FirstAsync(c => c.Id == request.CustomerId && c.TenantId == tenantId, ct);
        var payment = Payment.Create(tenantId, requestId, customer.Id, request.Amount, request.Currency, method, source, evidence, verifiedBy, providerKey, providerEventId, paidAtUtc, notes, now);

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            var claimed = await db.PaymentRequests.Where(p => p.Id == requestId && p.TenantId == tenantId && p.Status == PaymentRequestStatus.Open)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PaymentRequestStatus.Paid).SetProperty(p => p.OpenForSubscription, (Guid?)null).SetProperty(p => p.ClosedAtUtc, now).SetProperty(p => p.UpdatedAtUtc, now), ct);
            if (claimed == 0) { await tx.RollbackAsync(ct); return Result.Failure<Guid>(Error.Conflict("This payment request was just paid or cancelled.")); }
            db.Payments.Add(payment);
            sub.ActivateWithVerifiedPayment(payment.Id, now);
            customer.Activate(now);
            if (eventRow is not null) db.PaymentEvents.Add(eventRow);
            crm.Activity(customer.LeadId, LeadActivityType.Updated, $"Payment of {payment.Amount:0.00} {payment.Currency} verified ({source}, evidence {payment.EvidenceReference}). Subscription '{sub.PlanName}' is now active.", new { payment = payment.Id, request = requestId });
            crm.Record("Payment.Verified", "Payment", payment.Id, $"Payment of {payment.Amount:0.00} {payment.Currency} verified ({source}); subscription '{sub.PlanName}' activated.");
            WorkflowEventsBridge.Emit(crm, WorkflowEventTypes.PaymentConfirmed, customer.LeadId, customer.PrimaryContactId, $"payment:{payment.Id:N}", payment.Id.ToString("N"));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ResultFlow.IsDuplicate(ex))
        {
            await tx.RollbackAsync(ct); ConversionSupport.DetachAdded(db);
            logger.LogWarning("A payment verification collided with an existing payment for request {RequestId} or evidence {Evidence}", requestId, Money.EvidenceKey(evidence));
            return Result.Failure<Guid>(Error.Conflict(ex.ToString().Contains("EvidenceKey", StringComparison.OrdinalIgnoreCase) || ex.ToString().Contains("IX_Payments_TenantId_EvidenceKey", StringComparison.OrdinalIgnoreCase)
                ? "That evidence reference (receipt or transfer id) was already used to verify another payment."
                : "This payment (or this provider event) was already recorded."));
        }
        return Result.Success(payment.Id);
    }
}

internal static class WorkflowEventsBridge
{
    public static void Emit(CrmContext crm, string eventType, Guid leadId, Guid? contactId, string detail, string correlation) => new ConversionSupport(crm).Emit(eventType, leadId, contactId, detail, correlation);
}

/// <summary>The port Phase 8 defined for the AI's CreatePaymentRequest tool (reached only after a person approved the tool call). It creates a REQUEST for a customer's pending subscription; it cannot confirm or activate anything.</summary>
internal sealed class PaymentRequestGateway(CrmContext crm, PaymentService payments) : IPaymentRequestGateway
{
    public async Task<PortResult<PaymentRequestInfo>> CreateAsync(Guid leadId, string plan, decimal amount, string currency, decimal? discountPercent, string? customTerms, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var db = crm.Db;
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.LeadId == leadId, ct);
        if (customer is null) return PortResult<PaymentRequestInfo>.Fail("This lead is not a customer yet. A person must convert the won opportunity and record the commercial agreement first; nothing was created.");
        var sub = await db.Subscriptions.AsNoTracking().Where(s => s.TenantId == tenantId && s.CustomerId == customer.Id && s.Status == SubscriptionStatus.Pending && s.PlanName == plan).OrderByDescending(s => s.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (sub is null) return PortResult<PaymentRequestInfo>.Fail($"There is no pending subscription for plan '{plan}'. Nothing was created.");
        var created = await payments.CreateCoreAsync(sub.Id, null, null, "ai-approved", null, ct);
        return created.IsFailure ? PortResult<PaymentRequestInfo>.Fail(created.Error.Message) : PortResult<PaymentRequestInfo>.Ok(new PaymentRequestInfo(created.Value.Reference, null));
    }
}
