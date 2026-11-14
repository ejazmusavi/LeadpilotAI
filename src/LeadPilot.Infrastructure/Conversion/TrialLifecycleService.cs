using LeadPilot.Application.Conversion;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Crm;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Conversion;

/// <summary>
/// Expires finished trials and announces the ones about to end. Idempotent by construction: each step is claimed with a conditional UPDATE inside the transaction that also writes its effects (the outbox event,
/// the staff follow-up, the trial event), so a second sweep, a second worker or a crash in the middle can neither repeat nor lose an effect. The customer message uses a fixed idempotency key.
/// </summary>
public sealed class TrialLifecycleService(CrmContext crm, IServiceNoticeSender notices, ILogger<TrialLifecycleService> logger) : ITrialLifecycleService
{
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now; var support = new ConversionSupport(crm);
        var s = await support.SettingsAsync(ct);
        var handled = 0;

        // 1) notices for trials that end within the configured number of hours
        var horizon = now.AddHours(s.TrialExpiryNoticeHours);
        var notice = await db.Trials.AsNoTracking().Where(t => t.TenantId == tenantId && t.Status == TrialStatus.Active && t.ExpiresAtUtc != null && t.ExpiresAtUtc <= horizon && t.ExpiresAtUtc > now && t.ExpiryNoticeAtUtc == null)
            .OrderBy(t => t.ExpiresAtUtc).Take(50).Select(t => t.Id).ToListAsync(ct);
        foreach (var id in notice)
            if (await NoticeAsync(id, s, support, ct)) handled++;

        // 2) expiry
        var expired = await db.Trials.AsNoTracking().Where(t => t.TenantId == tenantId && t.Status == TrialStatus.Active && t.ExpiresAtUtc != null && t.ExpiresAtUtc <= now).OrderBy(t => t.ExpiresAtUtc).Take(50).Select(t => t.Id).ToListAsync(ct);
        foreach (var id in expired)
            if (await ExpireAsync(id, ct)) handled++;
        return handled;
    }

    private async Task<bool> NoticeAsync(Guid id, ConversionSettings s, ConversionSupport support, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        Trial? trial; Lead? lead;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var claimed = await db.Trials.Where(t => t.Id == id && t.TenantId == tenantId && t.Status == TrialStatus.Active && t.ExpiryNoticeAtUtc == null).ExecuteUpdateAsync(u => u.SetProperty(t => t.ExpiryNoticeAtUtc, now), ct);
            if (claimed == 0) { await tx.RollbackAsync(ct); return false; }
            trial = await db.Trials.AsNoTracking().FirstAsync(t => t.Id == id && t.TenantId == tenantId, ct);
            lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == trial.LeadId && l.TenantId == tenantId, ct);
            if (lead is not null)
            {
                var owner = lead.OwnerUserId is { } o && await crm.IsActiveMemberAsync(o, ct) ? o : (Guid?)null;
                db.FollowUps.Add(FollowUp.Create(tenantId, lead.Id, null, owner, now, $"Trial of '{lead.Name}' ends {trial.ExpiresAtUtc:yyyy-MM-dd HH:mm} UTC: contact them about converting.", ActorKind.System, now));
                db.TrialEvents.Add(TrialEvent.Create(tenantId, id, "ExpiryNotice", $"Expiry notice: the trial ends {trial.ExpiresAtUtc:yyyy-MM-dd HH:mm} UTC. A follow-up task was created for the team.", null, ConversionSupport.SystemActor, now));
                support.Emit(WorkflowEventTypes.TrialExpires, lead.Id, trial.ContactId, $"trial:{id:N}", $"{id:N}:{trial.ExpiresAtUtc!.Value.Ticks}");
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        if (lead is null || !s.TrialCustomerNoticesEnabled || s.ReminderChannelAccountId is not { } account) return true;

        var contact = await support.ContactForNoticeAsync(lead.Id, trial.ContactId, ct);
        var text = ConversionText.Render(s.TrialNoticeBody, new Dictionary<string, string>
        {
            ["name"] = contact?.Name ?? lead.Name, ["first_name"] = ConversionText.FirstName(contact?.Name ?? lead.Name), ["plan"] = trial.PlanName ?? "the product",
            ["date"] = ConversionSupport.Local(trial.ExpiresAtUtc!.Value, s.TimeZoneId, "yyyy-MM-dd"),
        });
        var outcome = await notices.SendAsync(new ServiceNoticeRequest(lead.Id, contact?.Id, account, text, $"trial-expiry:{id:N}:{trial.ExpiresAtUtc!.Value.Ticks}"), ct);
        db.TrialEvents.Add(TrialEvent.Create(tenantId, id, outcome.Status == ServiceNoticeStatus.Queued ? "NoticeSent" : "NoticeNotSent",
            outcome.Status == ServiceNoticeStatus.Queued ? "The expiry notice to the customer was queued through the WhatsApp pipeline." : $"The expiry notice to the customer was not sent: {outcome.Reason}", null, ConversionSupport.SystemActor, crm.Now));
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<bool> ExpireAsync(Guid id, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.Trials.Where(t => t.Id == id && t.TenantId == tenantId && t.Status == TrialStatus.Active && t.ExpiresAtUtc <= now)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.Status, TrialStatus.Expired).SetProperty(t => t.EndedAtUtc, now).SetProperty(t => t.EndReason, "The trial period ended.").SetProperty(t => t.UpdatedAtUtc, now), ct);
        if (claimed == 0) { await tx.RollbackAsync(ct); return false; }
        var trial = await db.Trials.AsNoTracking().FirstAsync(t => t.Id == id && t.TenantId == tenantId, ct);
        db.TrialEvents.Add(TrialEvent.Create(tenantId, id, "Expired", "The trial period ended; the trial is now Expired.", null, ConversionSupport.SystemActor, now));
        crm.Activity(trial.LeadId, LeadActivityType.Updated, "Trial expired.", new { trial = id });
        crm.Record("Trial.Expired", "Trial", id, "Trial expired.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Trial {TrialId} expired", id);
        return true;
    }
}
