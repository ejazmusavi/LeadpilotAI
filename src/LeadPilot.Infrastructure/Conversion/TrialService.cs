using LeadPilot.Application.Common;
using LeadPilot.Application.Conversion;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Crm;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Conversion;

/// <summary>
/// Trial lifecycle. A trial is RECORDED here; the account in the product is created by the provisioning backend (the shipped one only records and says that staff must do it) or by staff, who then press "mark as
/// provisioned". Only then is the trial Active and its clock running. Usage fields change only through <see cref="RecordUsageAsync"/> (a manual update or a call from the product's own integration with the Trials.Manage
/// permission), and every change is an append-only event.
/// </summary>
public sealed class TrialService(CrmContext crm, ITrialProvisioningBackend backend, ILogger<TrialService> logger) : ITrialService
{
    private ConversionSupport Support => new(crm);

    public async Task<PagedResult<TrialDto>> ListAsync(TrialQuery q, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(q.Page, q.PageSize);
        var query = crm.Db.Trials.AsNoTracking().Where(t => t.TenantId == tenantId);
        if (q.Status is { } st) query = query.Where(t => t.Status == st);
        if (q.LeadId is { } l) query = query.Where(t => t.LeadId == l);
        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(t => t.CreatedAtUtc).ThenBy(t => t.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<TrialDto>(await MapAsync(rows, false, ct), page, size, total);
    }

    public async Task<Result<TrialDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var t = await crm.Db.Trials.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        return t is null ? Result.Failure<TrialDto>(Error.NotFound("Trial")) : Result.Success((await MapAsync([t], true, ct))[0]);
    }

    public Task<Result<TrialDto>> CreateAsync(CreateTrialRequest r, TrialSource source = TrialSource.Staff, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == r.LeadId && l.TenantId == tenantId, ct);
            if (lead is null) return Result.Failure<TrialDto>(Error.NotFound("Lead"));
            if (lead.IsArchived) return Result.Failure<TrialDto>(Error.Validation("Archived leads cannot get a trial. Restore the lead first."));
            Guid? contactId = r.ContactId;
            if (contactId is { } cid && !await db.Contacts.AnyAsync(c => c.Id == cid && c.TenantId == tenantId && c.LeadId == lead.Id && !c.IsDeleted, ct)) return Result.Failure<TrialDto>(Error.NotFound("Contact"));
            contactId ??= (await Support.ContactForNoticeAsync(lead.Id, null, ct))?.Id;
            if (await db.Trials.AnyAsync(t => t.TenantId == tenantId && t.LeadId == lead.Id && (t.Status == TrialStatus.PendingProvisioning || t.Status == TrialStatus.Active), ct))
                return Result.Failure<TrialDto>(Error.Conflict("This lead already has a trial that is waiting to be provisioned or running. Cancel it or let it end first."));

            string? planName = null, productName = null;
            if (r.PlanId is { } pid)
            {
                var plan = await (from p in db.PricingPlans.AsNoTracking() join pr in db.CatalogProducts.AsNoTracking() on p.ProductId equals pr.Id
                                  where p.Id == pid && p.TenantId == tenantId && pr.TenantId == tenantId && p.IsActive && pr.IsActive select new { p.Name, Product = pr.Name }).FirstOrDefaultAsync(ct);
                if (plan is null) return Result.Failure<TrialDto>(Error.Validation("Choose an active plan from the product catalog."));
                planName = plan.Name; productName = plan.Product;
            }
            var s = await Support.SettingsAsync(ct);
            var days = r.DurationDays ?? s.TrialDefaultDays;
            var trial = Trial.Create(tenantId, lead.Id, contactId, r.PlanId, planName, productName, days, source, r.Notes, now, crm.Actor);
            db.Trials.Add(trial);

            var result = await backend.ProvisionAsync(new TrialProvisionRequest(trial.Id, lead.Id, lead.Name, planName, productName, days), ct);
            var who = source == TrialSource.Automation ? ConversionSupport.AutomationActor : ConversionSupport.PersonActor;
            db.TrialEvents.Add(TrialEvent.Create(tenantId, trial.Id, "Created", $"{Support.ActorLabel(who)} created the trial{(planName is null ? "" : $" for plan '{planName}'")} ({days} days). {result.Message}", crm.UserId, who, now));
            if (result.Provisioned) { trial.MarkProvisioned(result.ExternalAccountRef, result.LoginUrl, result.Message, now); db.TrialEvents.Add(TrialEvent.Create(tenantId, trial.Id, "Provisioned", "The provisioning backend created the account.", null, ConversionSupport.SystemActor, now)); }
            crm.Activity(lead.Id, LeadActivityType.Updated, $"Trial created{(planName is null ? "" : $" for plan '{planName}'")}: {(result.Provisioned ? "account ready" : "waiting for staff to provision the account")}.", new { trial = trial.Id, by = who });
            crm.Record("Trial.Created", "Trial", trial.Id, $"Trial created for lead '{lead.Name}' ({(result.Provisioned ? "provisioned" : "pending provisioning")}).");
            Support.Emit(WorkflowEventTypes.TrialCreated, lead.Id, contactId, $"trial:{trial.Id:N}", trial.Id.ToString("N"));
            await db.SaveChangesAsync(ct);
            return Result.Success((await MapAsync([trial], true, ct))[0]);
        }, logger);

    public Task<Result<TrialDto>> MarkProvisionedAsync(Guid id, ProvisionTrialRequest r, CancellationToken ct = default) =>
        MutateAsync(id, "Provisioned", (t, now) => { t.MarkProvisioned(r.ExternalAccountRef, r.LoginUrl, r.Note, now); return $"The trial account exists (reference {t.ExternalAccountRef ?? "none"}). The trial started and ends {t.ExpiresAtUtc:yyyy-MM-dd HH:mm} UTC."; }, ct);

    public Task<Result<TrialDto>> ExtendAsync(Guid id, ExtendTrialRequest r, CancellationToken ct = default) =>
        MutateAsync(id, "Extended", (t, now) => { t.Extend(r.Days, now); return $"The trial was extended by {r.Days} days; it now ends {t.ExpiresAtUtc:yyyy-MM-dd HH:mm} UTC."; }, ct);

    public Task<Result<TrialDto>> CancelAsync(Guid id, CancelTrialRequest r, CancellationToken ct = default) =>
        MutateAsync(id, "Cancelled", (t, now) => { t.Cancel(r.Reason, now); return "The trial was cancelled" + (string.IsNullOrWhiteSpace(r.Reason) ? "." : ": " + r.Reason!.Trim()); }, ct);

    public Task<Result<TrialDto>> RecordUsageAsync(Guid id, RecordTrialUsageRequest r, CancellationToken ct = default) =>
        MutateAsync(id, "Usage", (t, now) => { t.RecordUsage(r.LastActivityUtc?.ToUniversalTime(), r.LoginCount, r.ActiveUsers, r.Notes, now); return $"Usage recorded manually: {r.LoginCount} logins, {r.ActiveUsers} active users" + (r.LastActivityUtc is null ? "." : $", last activity {t.LastActivityAtUtc:yyyy-MM-dd HH:mm} UTC."); }, ct);

    private Task<Result<TrialDto>> MutateAsync(Guid id, string eventType, Func<Trial, DateTime, string> apply, CancellationToken ct) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            var trial = await db.Trials.FirstOrDefaultAsync(t => t.Id == id && t.TenantId == tenantId, ct);
            if (trial is null) return Result.Failure<TrialDto>(Error.NotFound("Trial"));
            var summary = apply(trial, now);
            db.TrialEvents.Add(TrialEvent.Create(tenantId, id, eventType, $"{Support.ActorLabel(ConversionSupport.PersonActor)}: {summary}", crm.UserId, ConversionSupport.PersonActor, now));
            if (eventType != "Usage") crm.Activity(trial.LeadId, LeadActivityType.Updated, "Trial: " + summary, new { trial = id });
            crm.Record("Trial." + eventType, "Trial", id, summary);
            await db.SaveChangesAsync(ct);
            return Result.Success((await MapAsync([trial], true, ct))[0]);
        }, logger);

    private async Task<List<TrialDto>> MapAsync(List<Trial> rows, bool detail, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var db = crm.Db;
        var leadIds = rows.Select(r => r.LeadId).Distinct().ToList(); var ids = rows.Select(r => r.Id).ToList();
        var leads = await db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && leadIds.Contains(l.Id)).Select(l => new { l.Id, l.Name }).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var events = detail ? await db.TrialEvents.AsNoTracking().Where(e => e.TenantId == tenantId && ids.Contains(e.TrialId)).OrderBy(e => e.OccurredAtUtc).ToListAsync(ct) : [];
        var actors = await crm.UserNamesAsync(events.Select(e => e.ActorUserId), ct);
        return rows.Select(t =>
        {
            var pending = t.Status == TrialStatus.PendingProvisioning;
            return new TrialDto(t.Id, t.LeadId, leads.GetValueOrDefault(t.LeadId) ?? "(removed lead)", t.ContactId, t.PlanId, t.PlanName, t.ProductName, t.Status, t.Source, t.DurationDays, t.StartedAtUtc, t.ExpiresAtUtc, t.ProvisioningNote,
                t.ExternalAccountRef, t.LoginUrl, t.LastActivityAtUtc, t.LoginCount, t.ActiveUsers, t.UsageNotes, t.ExpiryNoticeAtUtc, t.EndedAtUtc, t.EndReason, t.ConvertedCustomerId, t.CreatedAtUtc, pending,
                pending ? "The account in the product has NOT been created. Staff must create it and then mark the trial as provisioned; the trial period starts at that moment. Nothing was provisioned automatically." : t.Status == TrialStatus.Active ? "The trial account is marked as provisioned." : t.EndReason ?? "",
                events.Where(e => e.TrialId == t.Id).Select(e => new TrialEventDto(e.Type, e.Summary, e.ActorUserId is { } a ? actors.GetValueOrDefault(a) : null, e.ActorKind, e.OccurredAtUtc)).ToList());
        }).ToList();
    }
}

/// <summary>The shipped provisioning backend: it records and provisions NOTHING (no product API is configured), and says so. A trial is therefore never reported as ready when it is not.</summary>
internal sealed class ManualTrialProvisioningBackend : ITrialProvisioningBackend
{
    public Task<TrialProvisionResult> ProvisionAsync(TrialProvisionRequest request, CancellationToken ct = default) =>
        Task.FromResult(new TrialProvisionResult(false, null, null, "Recorded only: the account in the product was NOT created. Staff must create it and then mark the trial as provisioned."));
}
