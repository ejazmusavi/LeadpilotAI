using LeadPilot.Application.Common;
using LeadPilot.Application.Conversion;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Crm;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Infrastructure.Services.Tenancy;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Conversion;

/// <summary>
/// Conversion of a WON opportunity: a separate Customer record that LINKS to the lead, contact, opportunity and trial (history is referenced, never copied, so the lead's timeline stays the single source), a
/// pending subscription for the chosen catalog plan and the recorded commercial agreement. Nothing here activates anything: the subscription stays Pending until a verified payment (see PaymentVerificationCore).
/// </summary>
public sealed class CustomerService(CrmContext crm, ILogger<CustomerService> logger) : ICustomerService
{
    private ConversionSupport Support => new(crm);

    public async Task<PagedResult<CustomerListItemDto>> ListAsync(CustomerQuery q, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var db = crm.Db; var (page, size) = CrmFlow.Page(q.Page, q.PageSize);
        var query = db.Customers.AsNoTracking().Where(c => c.TenantId == tenantId);
        if (q.Status is { } st) query = query.Where(c => c.Status == st);
        if (!string.IsNullOrWhiteSpace(q.Search)) { var t = q.Search.Trim(); query = query.Where(c => c.Name.Contains(t) || (c.AccountReference != null && c.AccountReference.Contains(t))); }
        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(c => c.ConvertedAtUtc).ThenBy(c => c.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var subs = await db.Subscriptions.AsNoTracking().Where(s => s.TenantId == tenantId && ids.Contains(s.CustomerId)).Select(s => new { s.CustomerId, s.PlanName, s.Status }).ToListAsync(ct);
        return new PagedResult<CustomerListItemDto>(rows.Select(c => new CustomerListItemDto(c.Id, c.Code, c.Name, c.LeadId, c.Status,
            subs.Where(s => s.CustomerId == c.Id && s.Status == SubscriptionStatus.Active).Select(s => s.PlanName).FirstOrDefault(), subs.Count(s => s.CustomerId == c.Id && s.Status == SubscriptionStatus.Pending), c.ConvertedAtUtc)).ToList(), page, size, total);
    }

    public async Task<Result<CustomerDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var c = await crm.Db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        return c is null ? Result.Failure<CustomerDto>(Error.NotFound("Customer")) : Result.Success(await MapAsync(c, ct));
    }

    public async Task<Result<CustomerDto>> GetByLeadAsync(Guid leadId, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var c = await crm.Db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.LeadId == leadId && x.TenantId == tenantId, ct);
        return c is null ? Result.Failure<CustomerDto>(Error.NotFound("Customer")) : Result.Success(await MapAsync(c, ct));
    }

    public async Task<IReadOnlyList<WonOpportunityDto>> WonOpportunitiesAsync(CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var db = crm.Db;
        return await (from o in db.Opportunities.AsNoTracking() join l in db.Leads.AsNoTracking() on o.LeadId equals l.Id
                      where o.TenantId == tenantId && l.TenantId == tenantId && o.Status == OpportunityStatus.Won && !db.CommercialAgreements.Any(a => a.TenantId == tenantId && a.OpportunityId == o.Id)
                      orderby o.UpdatedAtUtc descending
                      select new WonOpportunityDto(o.Id, l.Id, l.Name, o.Name, o.Value, o.Currency, o.UpdatedAtUtc ?? o.CreatedAtUtc)).Take(100).ToListAsync(ct);
    }

    public async Task<Result<CustomerDto>> ConvertAsync(ConvertOpportunityRequest r, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await CrmFlow.GuardAsync(() => ConvertOnceAsync(r, ct), logger);
            if (result.IsSuccess || result.Error.Code != "retry" || attempt >= 4) return result.IsFailure && result.Error.Code == "retry" ? Result.Failure<CustomerDto>(Error.Conflict("The customer could not be created because of a concurrent change. Try again.")) : result;
        }
    }

    private async Task<Result<CustomerDto>> ConvertOnceAsync(ConvertOpportunityRequest r, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        if (crm.UserId is not { } approver) return Result.Failure<CustomerDto>(Error.Forbidden("A person must record the commercial agreement."));
        var opp = await db.Opportunities.AsNoTracking().FirstOrDefaultAsync(o => o.Id == r.OpportunityId && o.TenantId == tenantId, ct);
        if (opp is null) return Result.Failure<CustomerDto>(Error.NotFound("Opportunity"));
        if (opp.Status != OpportunityStatus.Won) return Result.Failure<CustomerDto>(Error.Validation("Only a Won opportunity can be converted into a customer. Move it to the Won stage first."));
        var already = await db.CommercialAgreements.AsNoTracking().Where(a => a.TenantId == tenantId && a.OpportunityId == opp.Id).Select(a => a.CustomerId).FirstOrDefaultAsync(ct);
        if (already != Guid.Empty) return Result.Failure<CustomerDto>(Error.Conflict("This opportunity was already converted into a customer."));
        var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == opp.LeadId && l.TenantId == tenantId, ct);
        if (lead is null) return Result.Failure<CustomerDto>(Error.NotFound("Lead"));

        var plan = await (from p in db.PricingPlans.AsNoTracking() join pr in db.CatalogProducts.AsNoTracking() on p.ProductId equals pr.Id
                          where p.Id == r.PlanId && p.TenantId == tenantId && pr.TenantId == tenantId && p.IsActive && pr.IsActive select new { p.Id, p.Name, p.Price, p.Currency, Product = pr.Name }).FirstOrDefaultAsync(ct);
        if (plan is null) return Result.Failure<CustomerDto>(Error.Validation("Choose an active plan from the product catalog."));
        if (string.IsNullOrWhiteSpace(r.Terms)) return Result.Failure<CustomerDto>(Error.Validation("Write the commercial terms that were agreed."));
        var price = r.Price ?? plan.Price;
        if (price is null) return Result.Failure<CustomerDto>(Error.Validation("This plan has no price in the catalog. Enter the agreed price."));
        if (price <= 0) return Result.Failure<CustomerDto>(Error.Validation("The agreed price must be greater than zero: a subscription is activated by a verified payment."));
        var currency = Money.Currency(r.Currency ?? plan.Currency);
        decimal? list = plan.Price is not null && string.Equals(plan.Currency, currency, StringComparison.OrdinalIgnoreCase) ? plan.Price : null;
        decimal? discount = list is > 0 && price < list ? decimal.Round((1 - price.Value / list.Value) * 100, 2) : null;

        Trial? trial = null;
        if (r.TrialId is { } tid)
        {
            trial = await db.Trials.FirstOrDefaultAsync(t => t.Id == tid && t.TenantId == tenantId, ct);
            if (trial is null || trial.LeadId != lead.Id) return Result.Failure<CustomerDto>(Error.NotFound("Trial"));
            if (trial.Status is not (TrialStatus.Active or TrialStatus.Expired)) return Result.Failure<CustomerDto>(Error.Validation($"A trial that is {trial.Status} cannot be converted."));
        }
        Guid? contactId = r.ContactId;
        if (contactId is { } cid && !await db.Contacts.AnyAsync(c => c.Id == cid && c.TenantId == tenantId && c.LeadId == lead.Id && !c.IsDeleted, ct)) return Result.Failure<CustomerDto>(Error.NotFound("Contact"));
        contactId ??= (await Support.ContactForNoticeAsync(lead.Id, null, ct))?.Id;

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.LeadId == lead.Id, ct);
        var isNew = customer is null;
        if (customer is null)
        {
            var number = (await db.Customers.Where(c => c.TenantId == tenantId).MaxAsync(c => (int?)c.Number, ct) ?? 0) + 1;
            customer = Customer.Create(tenantId, number, lead.Id, contactId, lead.Name, approver, now, crm.Actor);
            db.Customers.Add(customer);
        }
        var sub = Subscription.CreatePending(tenantId, customer.Id, plan.Id, plan.Name, plan.Product, r.BillingCycle, now, crm.Actor);
        db.Subscriptions.Add(sub);
        var agreement = CommercialAgreement.Create(tenantId, customer.Id, sub.Id, opp.Id, r.Terms, list, price.Value, currency, r.BillingCycle, discount, approver, now);
        db.CommercialAgreements.Add(agreement);
        trial?.MarkConverted(customer.Id, now);
        if (trial is not null) db.TrialEvents.Add(TrialEvent.Create(tenantId, trial.Id, "Converted", $"Converted into customer {customer.Code}.", approver, ConversionSupport.PersonActor, now));
        db.OpportunityActivities.Add(OpportunityActivity.Create(tenantId, opp.Id, "Converted", $"Converted into customer {customer.Code} (plan {plan.Name}, {price:0.00} {currency}).", approver, now));
        crm.Activity(lead.Id, LeadActivityType.Updated, $"Converted into customer {customer.Code}: plan '{plan.Name}', {price:0.00} {currency} ({r.BillingCycle}). Waiting for a verified payment.", new { customer = customer.Id, opportunity = opp.Id });
        crm.Record("Customer.Converted", "Customer", customer.Id, $"Opportunity '{opp.Name}' converted into customer {customer.Code}; agreement approved by the acting user.");
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ResultFlow.IsDuplicate(ex))
        {
            db.ChangeTracker.Clear();   // the customer number (or the same conversion) raced: the next attempt re-checks everything from scratch
            return Result.Failure<CustomerDto>(new Error("retry", "concurrent change"));
        }
        logger.LogInformation("Opportunity {OpportunityId} converted into customer {CustomerId} (new customer: {IsNew})", opp.Id, customer.Id, isNew);
        return Result.Success(await MapAsync(customer, ct));
    }

    public Task<Result<CustomerDto>> SetAccountAsync(Guid id, SetAccountRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var c = await crm.Db.Customers.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            if (c is null) return Result.Failure<CustomerDto>(Error.NotFound("Customer"));
            c.SetAccount(r.AccountReference, r.AccountNotes, crm.Now);
            crm.Record("Customer.AccountSet", "Customer", id, "The customer's product account reference was recorded.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await MapAsync(c, ct));
        }, logger);

    public Task<Result<CustomerDto>> CancelSubscriptionAsync(Guid customerId, Guid subscriptionId, CancelSubscriptionRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == customerId && x.TenantId == tenantId, ct);
            var sub = c is null ? null : await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId && s.CustomerId == customerId && s.TenantId == tenantId, ct);
            if (c is null || sub is null) return Result.Failure<CustomerDto>(Error.NotFound("Subscription"));
            sub.Cancel(r.Reason, now);
            foreach (var open in await db.PaymentRequests.Where(p => p.TenantId == tenantId && p.SubscriptionId == sub.Id && p.Status == PaymentRequestStatus.Open).ToListAsync(ct)) open.Cancel("The subscription was cancelled.", now);
            if (!await db.Subscriptions.AnyAsync(s => s.TenantId == tenantId && s.CustomerId == customerId && s.Id != sub.Id && s.Status != SubscriptionStatus.Cancelled, ct)) c.MarkCancelled(now);
            crm.Activity(c.LeadId, LeadActivityType.Updated, $"Subscription '{sub.PlanName}' cancelled.", new { customer = c.Id, subscription = sub.Id });
            crm.Record("Subscription.Cancelled", "Subscription", sub.Id, $"Subscription '{sub.PlanName}' of {c.Code} cancelled.");
            await db.SaveChangesAsync(ct);
            return Result.Success(await MapAsync(c, ct));
        }, logger);

    internal async Task<CustomerDto> MapAsync(Customer c, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var db = crm.Db;
        var subs = await db.Subscriptions.AsNoTracking().Where(s => s.TenantId == tenantId && s.CustomerId == c.Id).OrderBy(s => s.CreatedAtUtc).ToListAsync(ct);
        var agreements = await db.CommercialAgreements.AsNoTracking().Where(a => a.TenantId == tenantId && a.CustomerId == c.Id).OrderBy(a => a.ApprovedAtUtc).ToListAsync(ct);
        var requests = await db.PaymentRequests.AsNoTracking().Where(p => p.TenantId == tenantId && p.CustomerId == c.Id).OrderByDescending(p => p.CreatedAtUtc).ToListAsync(ct);
        var payments = await db.Payments.AsNoTracking().Where(p => p.TenantId == tenantId && p.CustomerId == c.Id).ToListAsync(ct);
        var names = await crm.UserNamesAsync(agreements.Select(a => (Guid?)a.ApprovedByUserId).Concat(payments.Select(p => p.VerifiedByUserId)).Append(c.ConvertedByUserId), ct);
        var planBySub = subs.ToDictionary(s => s.Id, s => s.PlanName);
        return new CustomerDto(c.Id, c.Code, c.Name, c.LeadId, c.PrimaryContactId, c.Status, c.AccountReference, c.AccountNotes, c.ConvertedAtUtc, c.ConvertedByUserId is { } u ? names.GetValueOrDefault(u) : null,
            subs.Select(s => new SubscriptionDto(s.Id, s.PlanName, s.ProductName, s.Status, s.BillingCycle, s.ActivatedAtUtc, s.ActivatedByPaymentId, s.CancelledAtUtc, s.CancelReason)).ToList(),
            agreements.Select(a => new AgreementDto(a.Id, a.SubscriptionId, a.OpportunityId, a.Terms, a.ListPrice, a.Price, a.Currency, a.BillingCycle, a.DiscountPercent, names.GetValueOrDefault(a.ApprovedByUserId) ?? "", a.ApprovedAtUtc)).ToList(),
            requests.Select(p => PaymentService.ToDto(p, c.Name, planBySub.GetValueOrDefault(p.SubscriptionId) ?? "", payments.FirstOrDefault(x => x.PaymentRequestId == p.Id), names)).ToList(),
            await TimelineAsync(c, ct));
    }

    /// <summary>The lead's whole history plus the conversion events, read from where each fact lives (links, not copies).</summary>
    private async Task<IReadOnlyList<TimelineItemDto>> TimelineAsync(Customer c, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var db = crm.Db; var items = new List<TimelineItemDto>();
        var lead = await db.LeadActivities.AsNoTracking().Where(a => a.TenantId == tenantId && a.LeadId == c.LeadId).OrderByDescending(a => a.OccurredAtUtc).Take(200).ToListAsync(ct);
        var opps = await db.Opportunities.AsNoTracking().Where(o => o.TenantId == tenantId && o.LeadId == c.LeadId).Select(o => o.Id).ToListAsync(ct);
        var oppActs = await db.OpportunityActivities.AsNoTracking().Where(a => a.TenantId == tenantId && opps.Contains(a.OpportunityId)).OrderByDescending(a => a.OccurredAtUtc).Take(100).ToListAsync(ct);
        var demoIds = await db.Demos.AsNoTracking().Where(d => d.TenantId == tenantId && d.LeadId == c.LeadId).Select(d => d.Id).ToListAsync(ct);
        var demoHist = await db.DemoHistory.AsNoTracking().Where(h => h.TenantId == tenantId && demoIds.Contains(h.DemoId)).OrderByDescending(h => h.OccurredAtUtc).Take(100).ToListAsync(ct);
        var trialIds = await db.Trials.AsNoTracking().Where(t => t.TenantId == tenantId && t.LeadId == c.LeadId).Select(t => t.Id).ToListAsync(ct);
        var trialEvents = await db.TrialEvents.AsNoTracking().Where(e => e.TenantId == tenantId && trialIds.Contains(e.TrialId)).OrderByDescending(e => e.OccurredAtUtc).Take(100).ToListAsync(ct);
        var users = await crm.UserNamesAsync(lead.Select(a => a.ActorUserId).Concat(oppActs.Select(a => a.ActorUserId)).Concat(demoHist.Select(h => h.ActorUserId)).Concat(trialEvents.Select(e => e.ActorUserId)), ct);
        string? Name(Guid? id) => id is { } i ? users.GetValueOrDefault(i) : null;
        items.AddRange(lead.Select(a => new TimelineItemDto(a.OccurredAtUtc, "Lead", a.Summary, Name(a.ActorUserId))));
        items.AddRange(oppActs.Select(a => new TimelineItemDto(a.OccurredAtUtc, "Opportunity", a.Summary, Name(a.ActorUserId))));
        items.AddRange(demoHist.Select(h => new TimelineItemDto(h.OccurredAtUtc, "Demo", h.Summary, Name(h.ActorUserId))));
        items.AddRange(trialEvents.Select(e => new TimelineItemDto(e.OccurredAtUtc, "Trial", e.Summary, Name(e.ActorUserId))));
        foreach (var p in await db.Payments.AsNoTracking().Where(p => p.TenantId == tenantId && p.CustomerId == c.Id).ToListAsync(ct))
            items.Add(new TimelineItemDto(p.VerifiedAtUtc, "Payment", $"Payment of {p.Amount:0.00} {p.Currency} verified ({p.Source}; evidence {p.EvidenceReference}).", Name(p.VerifiedByUserId)));
        foreach (var p in await db.PaymentRequests.AsNoTracking().Where(p => p.TenantId == tenantId && p.CustomerId == c.Id).ToListAsync(ct))
            items.Add(new TimelineItemDto(p.CreatedAtUtc, "Payment", $"Payment request {p.Reference} for {p.Amount:0.00} {p.Currency} created ({p.RequestedVia}).", null));
        return items.OrderByDescending(i => i.AtUtc).Take(300).ToList();
    }
}
