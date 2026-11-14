using System.Data;
using System.Globalization;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Billing;
using LeadPilot.Application.Contracts;
using LeadPilot.Domain.Billing;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Services.Tenancy;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Billing;

public sealed record InvoiceLineSpec(string Description, decimal Quantity, decimal UnitPrice);

/// <summary>
/// Creates and issues invoices. The number comes from the single platform-wide counter, incremented with one atomic UPDATE inside the same transaction that saves the invoice: two concurrent issues can never share a
/// number, and a failed save gives the number back (no gaps). An invoice with an idempotency key (the renewal job) is created at most once per (tenant, key).
/// </summary>
public sealed class InvoiceIssuer(AppDbContext db, ITenantContext tenant, IPlatformSettings settings, IClock clock, ICurrentUser user, ILogger<InvoiceIssuer> logger)
{
    public async Task<Invoice> CreateAndIssueAsync(Plan plan, InvoiceKind kind, DateTime periodStart, DateTime periodEnd, IReadOnlyList<InvoiceLineSpec> lines, string? idempotencyKey, Guid? planChangeId, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId(); var now = clock.UtcNow;
        if (idempotencyKey is not null)
        {
            var existing = await db.Invoices.Include(i => i.Lines).FirstOrDefaultAsync(i => i.TenantId == tenantId && i.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null) return existing;
        }
        var s = await settings.GetAllAsync(ct);
        var billTo = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Name).FirstAsync(ct);
        var owns = db.Database.CurrentTransaction is null;
        var tx = owns ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct) : null;
        try
        {
            var invoice = Invoice.CreateDraft(tenantId, kind, plan, periodStart, periodEnd, billTo, s.TaxName, s.TaxRatePercent, s.IssuerText, s.PaymentInstructions, idempotencyKey, planChangeId, now, user.Email);
            foreach (var l in lines) invoice.AddLine(l.Description, l.Quantity, l.UnitPrice);
            var number = s.InvoicePrefix + (await NextNumberAsync(ct)).ToString("000000", CultureInfo.InvariantCulture);
            invoice.Issue(number, now, now.AddDays(s.InvoiceDueDays));
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return invoice;
        }
        catch (DbUpdateException ex) when (idempotencyKey is not null && ResultFlow.IsDuplicate(ex))
        {
            // lost a race for the same (tenant, key): the winner's invoice is the answer
            if (tx is not null) await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            logger.LogInformation("Invoice for key {Key} was created concurrently", idempotencyKey);
            return await db.Invoices.Include(i => i.Lines).FirstAsync(i => i.TenantId == tenantId && i.IdempotencyKey == idempotencyKey, ct);
        }
        catch
        {
            if (tx is not null) { try { await tx.RollbackAsync(CancellationToken.None); } catch { } }
            throw;
        }
        finally { if (tx is not null) await tx.DisposeAsync(); }
    }

    /// <summary>Atomic increment: <c>LAST_INSERT_ID(expr)</c> is connection-local, so concurrent callers each read their own value, and the row lock serialises them until their transaction ends.</summary>
    private async Task<long> NextNumberAsync(CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var conn = db.Database.GetDbConnection(); var dbTx = db.Database.CurrentTransaction?.GetDbTransaction();
            await using (var up = conn.CreateCommand())
            {
                up.Transaction = dbTx; up.CommandText = "UPDATE InvoiceSequences SET LastNumber = LAST_INSERT_ID(LastNumber + 1) WHERE Id = 1";
                if (await up.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("The invoice number sequence is missing.");
            }
            await using var sel = conn.CreateCommand();
            sel.Transaction = dbTx; sel.CommandText = "SELECT LAST_INSERT_ID()";
            return Convert.ToInt64(await sel.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}

public static class InvoiceMap
{
    public static InvoiceDto ToDto(Invoice i, string planName, bool hasOpenReport) =>
        new(i.Id, i.Number, i.Kind, i.Status, i.Currency, i.Subtotal, i.TaxName, i.TaxRatePercent, i.TaxAmount, i.Total, i.IssuedAtUtc, i.DueAtUtc, i.PaidAtUtc, i.VoidedAtUtc, i.VoidReason, i.PeriodStartUtc, i.PeriodEndUtc,
            planName, i.BillToName, i.SellerText, i.PaymentInstructions, hasOpenReport, i.Lines.OrderBy(l => l.Position).Select(l => new InvoiceLineDto(l.Position, l.Description, l.Quantity, l.UnitPrice, l.Amount)).ToList(), i.TenantId);
}

/// <summary>
/// The only code that turns a tenant's money into an activated or renewed subscription (the platform-side sibling of Phase 10's <c>PaymentVerificationCore</c>, with the same rules). In ONE transaction: claim the
/// issued invoice (atomic conditional update: concurrent verifications race to exactly one winner), insert the verified <see cref="BillingPayment"/> (unique per invoice and per evidence reference), apply the invoice's
/// effect to the subscription, lift a non-payment suspension, close the tenant's payment reports and write the history and audit entries. Amount and currency must equal the invoice exactly. It is reached only from
/// <c>PlatformBillingService.VerifyPaymentAsync</c> (permission Platform.Billing.Verify); an architecture test pins the callers of the domain method it uses.
/// </summary>
public sealed class BillingPaymentVerifier(AppDbContext db, ITenantContext tenant, IClock clock, IAuditService audit, ILogger<BillingPaymentVerifier> logger)
{
    public async Task<Result<Guid>> ApplyAsync(Guid invoiceId, decimal amount, string currency, PaymentMethod method, VerificationSource source, string evidence, Guid? verifiedBy, DateTime paidAtUtc, string? notes, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId(); var now = clock.UtcNow;
        var invoice = await db.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId, ct);
        if (invoice is null) return Result.Failure<Guid>(Error.NotFound("Invoice"));
        if (invoice.Status != InvoiceStatus.Issued) return Result.Failure<Guid>(Error.Conflict($"This invoice is {invoice.Status}; only an issued invoice can be paid."));
        string cur;
        try { cur = Money.Currency(currency); } catch (LeadPilot.Domain.Common.DomainException ex) { return Result.Failure<Guid>(Error.Validation(ex.Message)); }
        if (cur != invoice.Currency) return Result.Failure<Guid>(Error.Validation($"The currency must be {invoice.Currency}, as invoiced."));
        if (invoice.Total <= 0 || amount != invoice.Total) return Result.Failure<Guid>(Error.Validation($"The amount must be exactly {invoice.Total:0.00} {invoice.Currency}. Partial or different amounts are not accepted."));
        if (method == PaymentMethod.Provider) return Result.Failure<Guid>(Error.Validation("The 'Provider' method is only used for signature-verified provider events (no provider is installed)."));
        var plan = await db.Plans.AsNoTracking().Include(p => p.Limits).FirstAsync(p => p.Id == invoice.PlanId, ct);
        var sub = await db.TenantSubscriptions.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (sub is null) return Result.Failure<Guid>(Error.Validation("The tenant has no subscription."));
        BillingPayment payment;
        try { payment = BillingPayment.Create(tenantId, invoiceId, invoice.Total, invoice.Currency, method, source, evidence, verifiedBy, paidAtUtc, notes, now); }
        catch (LeadPilot.Domain.Common.DomainException ex) { return Result.Failure<Guid>(Error.Validation(ex.Message)); }

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            var claimed = await db.Invoices.Where(i => i.Id == invoiceId && i.TenantId == tenantId && i.Status == InvoiceStatus.Issued)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.Status, InvoiceStatus.Paid).SetProperty(i => i.PaidAtUtc, now).SetProperty(i => i.UpdatedAtUtc, now), ct);
            if (claimed == 0) { await tx.RollbackAsync(ct); return Result.Failure<Guid>(Error.Conflict("This invoice was just paid or voided.")); }
            db.BillingPayments.Add(payment);

            var from = sub.PlanId;
            var activatesNow = invoice.Kind != InvoiceKind.Renewal || invoice.PeriodStartUtc <= now;
            string effect;
            if (activatesNow)
            {
                // a renewal that is paid late (its period already began or is over) and every plan change / initial invoice start a fresh period when it is not contiguous
                var start = invoice.Kind == InvoiceKind.Renewal && invoice.PeriodEndUtc > now ? invoice.PeriodStartUtc : now;
                var end = invoice.Kind == InvoiceKind.Renewal && invoice.PeriodEndUtc > now ? invoice.PeriodEndUtc : plan.PeriodEnd(start);
                sub.StartPaidPeriodWithVerifiedPayment(payment.Id, plan, start, end, now);
                effect = $"plan '{plan.Name}' active until {end:yyyy-MM-dd}";
            }
            else
            {
                sub.RecordEarlyPayment(payment.Id, now);
                effect = $"renewal for the period starting {invoice.PeriodStartUtc:yyyy-MM-dd} paid in advance; it starts when the current period ends";
            }

            if (invoice.PlanChangeRequestId is { } pcId)
            {
                var pc = await db.PlanChangeRequests.FirstOrDefaultAsync(p => p.Id == pcId && p.TenantId == tenantId, ct);
                if (pc is { IsOpen: true }) pc.Close(PlanChangeStatus.Applied, "Paid", now);
            }
            foreach (var r in await db.PaymentReports.Where(r => r.TenantId == tenantId && r.InvoiceId == invoiceId && r.Status == PaymentReportStatus.Open).ToListAsync(ct)) r.Close(now);

            var t = await db.Tenants.FirstAsync(x => x.Id == tenantId, ct);
            var reactivated = false;
            var billingState = await db.TenantBillingStates.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
            if (t.Status == TenantStatus.Suspended && billingState?.SuspensionKind == SuspensionKind.NonPayment) { t.Activate(now); billingState.SetSuspensionKind(null, now); reactivated = true; }
            db.SubscriptionEvents.Add(SubscriptionEvent.Create(tenantId, "PaymentVerified", $"Invoice {invoice.Number} paid ({payment.Amount:0.00} {payment.Currency}, evidence {payment.EvidenceReference}): {effect}.", from, plan.Id, verifiedBy?.ToString(), now));
            audit.Record(new AuditEntry("Platform.Payment.Verified", "BillingPayment", payment.Id.ToString(), $"Payment of {payment.Amount:0.00} {payment.Currency} verified for invoice {invoice.Number} ({source}); {effect}{(reactivated ? "; the tenant was reactivated" : "")}.", tenantId));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ResultFlow.IsDuplicate(ex))
        {
            await tx.RollbackAsync(ct); db.ChangeTracker.Clear();
            logger.LogWarning("A billing verification collided with an existing payment for invoice {InvoiceId} or evidence {Evidence}", invoiceId, Money.EvidenceKey(evidence));
            return Result.Failure<Guid>(Error.Conflict(ex.ToString().Contains("EvidenceKey", StringComparison.OrdinalIgnoreCase) ? "That evidence reference (receipt or transfer id) was already used to verify another payment." : "This invoice already has a verified payment."));
        }
        return Result.Success(payment.Id);
    }
}
