using LeadPilot.Application.Conversion;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Crm;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Conversion;

/// <summary>
/// Sends the reminders that are due (called by the Hangfire sweep, once per tenant, inside the tenant scope). Each reminder is CLAIMED with an atomic status change, so two workers never send it twice, and its
/// idempotency key (demo, generation, offset) makes a crash between "queued" and "recorded" harmless: the same key returns the same message. Every decision is recorded on the reminder, nothing is dropped silently.
/// </summary>
public sealed class DemoReminderService(CrmContext crm, IServiceNoticeSender notices, IOptions<ConversionOptions> options, ILogger<DemoReminderService> logger) : IDemoReminderService
{
    private static readonly HashSet<string> Transient = ["account_paused", "account_not_connected", "rate_limited", "new_contacts_blocked"];

    public async Task<int> ProcessDueAsync(CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now; var o = options.Value;
        var stale = now.AddMinutes(-o.StaleProcessingMinutes);
        var due = await db.DemoReminders.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.DueAtUtc <= now && (r.Status == ReminderStatus.Scheduled || (r.Status == ReminderStatus.Processing && r.ProcessedAtUtc < stale)))
            .OrderBy(r => r.DueAtUtc).Take(o.SweepBatch).Select(r => r.Id).ToListAsync(ct);
        var handled = 0;
        foreach (var id in due)
        {
            var claimed = await db.DemoReminders.Where(r => r.Id == id && r.TenantId == tenantId && (r.Status == ReminderStatus.Scheduled || (r.Status == ReminderStatus.Processing && r.ProcessedAtUtc < stale)))
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, ReminderStatus.Processing).SetProperty(r => r.ProcessedAtUtc, now), ct);
            if (claimed == 0) continue;
            try { await HandleOneAsync(id, ct); handled++; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Demo reminder {ReminderId} failed unexpectedly", id);
                await SetAsync(id, ReminderStatus.Scheduled, "Will retry: " + ex.GetType().Name, null, ct);
            }
        }
        return handled;
    }

    private async Task HandleOneAsync(Guid id, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now; var o = options.Value;
        var rem = await db.DemoReminders.AsNoTracking().FirstAsync(r => r.Id == id && r.TenantId == tenantId, ct);
        var demo = await db.Demos.AsNoTracking().FirstOrDefaultAsync(d => d.Id == rem.DemoId && d.TenantId == tenantId, ct);
        if (demo is null || rem.Generation != demo.Generation || demo.Status is not (DemoStatus.Booked or DemoStatus.Confirmed)) { await SetAsync(id, ReminderStatus.Cancelled, "The demo changed or was cancelled.", null, ct); return; }
        if (demo.StartUtc <= now) { await SetAsync(id, ReminderStatus.Skipped, "The demo already started.", null, ct); return; }
        if (now - rem.DueAtUtc > TimeSpan.FromMinutes(o.ReminderMaxLatenessMinutes)) { await SetAsync(id, ReminderStatus.Skipped, "Too late: the reminder was more than " + o.ReminderMaxLatenessMinutes + " minutes overdue (the system was not running).", null, ct); return; }
        var s = await new ConversionSupport(crm).SettingsAsync(ct);
        if (!s.RemindersEnabled || s.ReminderChannelAccountId is not { } account) { await SetAsync(id, ReminderStatus.Skipped, "Reminders are switched off or no WhatsApp account is chosen.", null, ct); return; }
        var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == demo.LeadId && l.TenantId == tenantId, ct);
        if (lead is null) { await SetAsync(id, ReminderStatus.Skipped, "The lead no longer exists.", null, ct); return; }
        var contact = await new ConversionSupport(crm).ContactForNoticeAsync(demo.LeadId, demo.ContactId, ct);
        var rep = (await crm.UserNamesAsync([demo.RepUserId], ct)).GetValueOrDefault(demo.RepUserId) ?? "";
        var values = new Dictionary<string, string>
        {
            ["name"] = contact?.Name ?? lead.Name, ["first_name"] = ConversionText.FirstName(contact?.Name ?? lead.Name),
            ["date"] = ConversionSupport.Local(demo.StartUtc, s.TimeZoneId, "yyyy-MM-dd"), ["time"] = ConversionSupport.Local(demo.StartUtc, s.TimeZoneId, "HH:mm"), ["timezone"] = s.TimeZoneId,
            ["link"] = demo.MeetingLink is { } l ? "Join: " + l : string.Empty, ["rep"] = rep,
        };
        var text = ConversionText.Render(s.ReminderBody, values);
        var outcome = await notices.SendAsync(new ServiceNoticeRequest(demo.LeadId, contact?.Id, account, text, rem.IdempotencyKey), ct);
        switch (outcome.Status)
        {
            case ServiceNoticeStatus.Queued:
                await SetAsync(id, ReminderStatus.Sent, null, outcome.MessageId, ct);
                db.DemoHistory.Add(DemoHistory.Create(tenantId, demo.Id, "ReminderSent", $"Reminder ({rem.OffsetMinutes} min before) queued through the WhatsApp pipeline.", null, ConversionSupport.SystemActor, now));
                await db.SaveChangesAsync(ct);
                break;
            case ServiceNoticeStatus.Skipped:
                await SetAsync(id, ReminderStatus.Skipped, $"Not sent: {outcome.Reason}", null, ct);
                db.DemoHistory.Add(DemoHistory.Create(tenantId, demo.Id, "ReminderSkipped", $"Reminder ({rem.OffsetMinutes} min before) not sent: {outcome.Reason}", null, ConversionSupport.SystemActor, now));
                await db.SaveChangesAsync(ct);
                break;
            default:
                if (outcome.Code is { } code && Transient.Contains(code)) await SetAsync(id, ReminderStatus.Scheduled, $"Will retry: {outcome.Reason}", null, ct);
                else await SetAsync(id, ReminderStatus.Failed, outcome.Reason ?? "The reminder could not be queued.", null, ct);
                break;
        }
    }

    private Task SetAsync(Guid id, ReminderStatus status, string? reason, Guid? messageId, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var now = crm.Now;
        return crm.Db.DemoReminders.Where(r => r.Id == id && r.TenantId == tenantId)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, status).SetProperty(r => r.Reason, reason).SetProperty(r => r.MessageId, messageId).SetProperty(r => r.ProcessedAtUtc, now), ct);
    }
}
