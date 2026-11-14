using System.Data;
using LeadPilot.Application.Common;
using LeadPilot.Application.Conversion;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Infrastructure.Services.Tenancy;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Conversion;

/// <summary>
/// Demo calendar and lifecycle. The rules that can never be bent:
/// 1. A demo exists only at a slot the calculator produces for a rep (weekly hours, exceptions, buffer, daily maximum, minimum notice, horizon, never in the past). The client sends a slot id, never a free time.
/// 2. Booking, rescheduling and the checks run in ONE transaction that first takes the rep's row lock, then re-reads the rep's demos and recalculates; a unique index on (tenant, rep, start) is the last line.
/// 3. Everything is tenant-scoped through <see cref="CrmContext"/>; a demo, lead, contact or rep of another workspace is "not found".
/// </summary>
public sealed class DemoService(CrmContext crm, IMeetingLinkProvider links, ILogger<DemoService> logger) : IDemoService
{
    private ConversionSupport Support => new(crm);
    private static readonly Error SlotTaken = new("conflict", "That time is no longer available (it was just taken, or it is not an available slot). Choose another slot.");

    // ---------------------------------------------------------------- slots

    public async Task<IReadOnlyList<SlotDto>> SlotsAsync(DateTime fromUtc, DateTime toUtc, Guid? repUserId = null, CancellationToken ct = default)
    {
        var s = await Support.SettingsAsync(ct);
        fromUtc = Utc(fromUtc); toUtc = Utc(toUtc);
        if (toUtc <= fromUtc) return [];
        if (toUtc > fromUtc.AddDays(35)) toUtc = fromUtc.AddDays(35);
        var inputs = await InputsAsync(s, fromUtc, toUtc, repUserId, null, ct);
        var pooled = SlotCalculator.Pool(inputs, Query(s, fromUtc, toUtc));
        return pooled.Select(p => new SlotDto(p.SlotId, p.StartUtc, p.EndUtc, ConversionSupport.Local(p.StartUtc, s.TimeZoneId, "HH:mm"), ConversionSupport.Local(p.StartUtc, s.TimeZoneId, "yyyy-MM-dd"), p.RepUserIds.Count)).ToList();
    }

    private SlotQuery Query(ConversionSettings s, DateTime from, DateTime to) => new(crm.Now, from, to, s.SlotMinutes, s.MinNoticeMinutes, s.MaxDaysAhead);

    private static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : d, DateTimeKind.Utc);

    /// <summary>Availability input of the active reps (members that are still active). Busy = the reps' demos that hold their time, padded by two days so the daily maximum sees whole local days.</summary>
    private async Task<List<RepAvailabilityInput>> InputsAsync(ConversionSettings s, DateTime fromUtc, DateTime toUtc, Guid? onlyRepUser, Guid? excludeDemoId, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var db = crm.Db;
        var q = from r in db.DemoReps.AsNoTracking()
                join m in db.UserTenants.AsNoTracking() on r.UserId equals m.UserId
                join u in db.Users.AsNoTracking() on r.UserId equals u.Id
                where r.TenantId == tenantId && r.IsActive && m.TenantId == tenantId && m.Status == MembershipStatus.Active && u.IsActive
                select r;
        if (onlyRepUser is { } only) q = q.Where(r => r.UserId == only);
        var reps = await q.ToListAsync(ct);
        if (reps.Count == 0) return [];
        var repIds = reps.Select(r => r.Id).ToList(); var userIds = reps.Select(r => r.UserId).ToList();
        var rules = await db.DemoAvailabilityRules.AsNoTracking().Where(x => x.TenantId == tenantId && repIds.Contains(x.RepId)).ToListAsync(ct);
        var dFrom = fromUtc.Date.AddDays(-3); var dTo = toUtc.Date.AddDays(3);
        var exceptions = await db.DemoAvailabilityExceptions.AsNoTracking().Where(x => x.TenantId == tenantId && repIds.Contains(x.RepId) && x.Date >= dFrom && x.Date <= dTo).ToListAsync(ct);
        var busyFrom = fromUtc.AddDays(-2); var busyTo = toUtc.AddDays(2);
        var busyQuery = db.Demos.AsNoTracking().Where(d => d.TenantId == tenantId && userIds.Contains(d.RepUserId) && d.ActiveSlot != null && d.EndUtc > busyFrom && d.StartUtc < busyTo);
        if (excludeDemoId is { } ex) busyQuery = busyQuery.Where(d => d.Id != ex);
        var busy = await busyQuery.Select(d => new { d.RepUserId, d.StartUtc, d.EndUtc }).ToListAsync(ct);
        return reps.Select(r => new RepAvailabilityInput(r.UserId, r.TimeZoneId ?? s.TimeZoneId, r.BufferMinutes, r.MaxPerDay,
            rules.Where(x => x.RepId == r.Id).Select(x => new RuleInput(x.Day, x.StartMinute, x.EndMinute)).ToList(),
            exceptions.Where(x => x.RepId == r.Id).Select(x => new ExceptionInput(x.Date, x.Kind, x.StartMinute, x.EndMinute)).ToList(),
            busy.Where(b => b.RepUserId == r.UserId).Select(b => new BusyInput(b.StartUtc, b.EndUtc)).ToList())).ToList();
    }

    /// <summary>Reps that are free at exactly this slot start, in the order the assignment strategy wants them (a free preferred rep first).</summary>
    private async Task<List<Guid>> CandidatesAsync(ConversionSettings s, DateTime start, Guid? preferred, Guid? excludeDemoId, Guid? currentRep, CancellationToken ct)
    {
        var inputs = await InputsAsync(s, start, start.AddMinutes(s.SlotMinutes), null, excludeDemoId, ct);
        var q = Query(s, start, start.AddMinutes(1));
        var free = inputs.Where(i => SlotCalculator.Calculate(i, q).Any(x => x.StartUtc == start)).Select(i => i.RepUserId).ToList();
        if (free.Count == 0) return [];
        var tenantId = crm.TenantId; var now = crm.Now;
        var reps = await crm.Db.DemoReps.AsNoTracking().Where(r => r.TenantId == tenantId && free.Contains(r.UserId)).ToListAsync(ct);
        var load = await crm.Db.Demos.AsNoTracking().Where(d => d.TenantId == tenantId && free.Contains(d.RepUserId) && d.ActiveSlot != null && d.StartUtc > now).GroupBy(d => d.RepUserId).Select(g => new { Rep = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Rep, x => x.N, ct);
        IEnumerable<DemoRep> ordered = s.Assignment == AssignmentStrategy.LeastLoaded
            ? reps.OrderBy(r => load.GetValueOrDefault(r.UserId)).ThenBy(r => r.LastAssignedAtUtc ?? DateTime.MinValue).ThenBy(r => r.UserId)
            : reps.OrderBy(r => r.LastAssignedAtUtc ?? DateTime.MinValue).ThenBy(r => r.UserId);
        var list = ordered.Select(r => r.UserId).ToList();
        foreach (var first in new[] { currentRep, preferred }.Where(x => x is not null).Select(x => x!.Value).Reverse()) if (list.Remove(first)) list.Insert(0, first);
        return list;
    }

    /// <summary>One attempt for one rep: lock the rep's row, re-read, recalculate, apply, commit. False = the slot is not free for this rep (any more).</summary>
    private async Task<bool> TryReserveAsync(Guid repUserId, ConversionSettings s, DateTime start, Guid? excludeDemoId, Func<DateTime, CancellationToken, Task> apply, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            var locked = await db.DemoReps.Where(r => r.TenantId == tenantId && r.UserId == repUserId && r.IsActive).ExecuteUpdateAsync(u => u.SetProperty(r => r.Version, r => r.Version + 1), ct);
            if (locked == 0) { await tx.RollbackAsync(ct); return false; }
            var inputs = await InputsAsync(s, start, start.AddMinutes(s.SlotMinutes), repUserId, excludeDemoId, ct);
            if (inputs.Count != 1 || !SlotCalculator.Calculate(inputs[0], Query(s, start, start.AddMinutes(1))).Any(x => x.StartUtc == start)) { await tx.RollbackAsync(ct); return false; }
            await apply(start.AddMinutes(s.SlotMinutes), ct);
            await tx.CommitAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ResultFlow.IsDuplicate(ex))
        {
            logger.LogInformation("Slot {Start:u} of rep {Rep} was taken concurrently (unique index)", start, repUserId);
            await tx.RollbackAsync(ct); ConversionSupport.DetachAdded(db);
            return false;
        }
    }

    // ---------------------------------------------------------------- booking

    public Task<Result<DemoDto>> BookAsync(BookDemoRequest r, DemoSource source = DemoSource.Staff, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            if (!DemoRules.TryParseSlotId(r.SlotId, out var start)) return Result.Failure<DemoDto>(Error.Validation("That is not a slot id. Choose one of the available slots."));
            if (start <= now) return Result.Failure<DemoDto>(Error.Validation("A demo cannot be booked in the past."));
            var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == r.LeadId && l.TenantId == tenantId, ct);
            if (lead is null) return Result.Failure<DemoDto>(Error.NotFound("Lead"));
            if (lead.IsArchived) return Result.Failure<DemoDto>(Error.Validation("Archived leads cannot be booked. Restore the lead first."));
            Guid? contactId = null;
            if (r.ContactId is { } cid)
            {
                if (!await db.Contacts.AnyAsync(c => c.Id == cid && c.TenantId == tenantId && c.LeadId == lead.Id && !c.IsDeleted, ct)) return Result.Failure<DemoDto>(Error.NotFound("Contact"));
                contactId = cid;
            }
            else contactId = (await Support.ContactForNoticeAsync(lead.Id, null, ct))?.Id;
            var link = string.IsNullOrWhiteSpace(r.MeetingLink) ? null : Demo.CleanLink(r.MeetingLink);
            if (await db.Demos.AnyAsync(d => d.TenantId == tenantId && d.LeadId == lead.Id && d.ActiveSlot != null && d.EndUtc > now, ct))
                return Result.Failure<DemoDto>(Error.Conflict("This lead already has an upcoming demo. Reschedule or cancel it first."));

            var s = await Support.SettingsAsync(ct);
            var candidates = await CandidatesAsync(s, start, r.PreferredRepUserId, null, null, ct);
            var initial = source == DemoSource.Automation && s.ConfirmAutomatedBookings ? DemoStatus.Requested : DemoStatus.Booked;
            Demo? created = null;
            foreach (var repUser in candidates)
            {
                var placed = await TryReserveAsync(repUser, s, start, null, async (end, token) =>
                {
                    var demo = Demo.Create(tenantId, lead.Id, contactId, repUser, start, end, initial, source, r.Notes, link, now, crm.Actor);
                    if (demo.MeetingLink is null && await links.ProvideAsync(new MeetingLinkRequest(demo.Id, start, lead.Name), s.MeetingLinkTemplate, token) is { } provided) demo.SetMeetingLink(provided.Url, now);
                    db.Demos.Add(demo);
                    var who = source == DemoSource.Automation ? ConversionSupport.AutomationActor : ConversionSupport.PersonActor;
                    db.DemoHistory.Add(DemoHistory.Create(tenantId, demo.Id, "Booked", $"{Support.ActorLabel(who)} booked the demo for {ConversionSupport.Local(start, s.TimeZoneId, "yyyy-MM-dd HH:mm")} ({s.TimeZoneId}). Status: {initial}.", crm.UserId, who, now));
                    AddReminders(demo, s, now);
                    await db.DemoReps.Where(x => x.TenantId == tenantId && x.UserId == repUser).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastAssignedAtUtc, now), token);
                    crm.Activity(lead.Id, LeadActivityType.Updated, $"Demo booked for {ConversionSupport.Local(start, s.TimeZoneId, "yyyy-MM-dd HH:mm")} ({s.TimeZoneId}) by {Support.ActorLabel(who)}.", new { demo = demo.Id, by = who });
                    crm.Record("Demo.Booked", "Demo", demo.Id, $"Demo booked for lead '{lead.Name}' at {start:u}.");
                    Support.Emit(WorkflowEventTypes.DemoBooked, lead.Id, contactId, $"demo:{demo.Id:N}", demo.Id.ToString("N"));
                    await db.SaveChangesAsync(token);
                    created = demo;
                }, ct);
                if (placed) break;
            }
            return created is null ? Result.Failure<DemoDto>(SlotTaken) : Result.Success(await MapOneAsync(created.Id, ct));
        }, logger);

    private void AddReminders(Demo demo, ConversionSettings s, DateTime now)
    {
        if (!s.RemindersEnabled || demo.Status is DemoStatus.Requested) return;
        foreach (var offset in ConversionSettings.ParseOffsets(s.ReminderOffsets))
        {
            var due = demo.StartUtc.AddMinutes(-offset);
            if (due > now) crm.Db.DemoReminders.Add(DemoReminder.Create(crm.TenantId, demo.Id, demo.Generation, offset, due, now));
        }
    }

    // ---------------------------------------------------------------- reschedule / cancel / status

    public Task<Result<DemoDto>> RescheduleAsync(Guid id, RescheduleDemoRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            var demo = await db.Demos.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
            if (demo is null) return Result.Failure<DemoDto>(Error.NotFound("Demo"));
            if (!DemoRules.HoldsSlot(demo.Status)) return Result.Failure<DemoDto>(Error.Validation($"A demo that is {demo.Status} cannot be rescheduled."));
            if (!DemoRules.TryParseSlotId(r.SlotId, out var start)) return Result.Failure<DemoDto>(Error.Validation("That is not a slot id. Choose one of the available slots."));
            if (start <= now) return Result.Failure<DemoDto>(Error.Validation("A demo cannot be moved into the past."));
            if (start == demo.StartUtc) return Result.Failure<DemoDto>(Error.Validation("The demo is already at that time."));
            var s = await Support.SettingsAsync(ct);
            var candidates = await CandidatesAsync(s, start, r.PreferredRepUserId, id, demo.RepUserId, ct);
            var moved = false;
            foreach (var repUser in candidates)
            {
                moved = await TryReserveAsync(repUser, s, start, id, async (end, token) =>
                {
                    var tracked = await db.Demos.FirstAsync(d => d.Id == id && d.TenantId == tenantId, token);
                    var oldStart = tracked.StartUtc;
                    tracked.Reschedule(repUser, start, end, now);
                    await db.DemoReminders.Where(x => x.DemoId == id && x.TenantId == tenantId && (x.Status == ReminderStatus.Scheduled || x.Status == ReminderStatus.Processing))
                        .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, ReminderStatus.Cancelled).SetProperty(x => x.Reason, "The demo was rescheduled."), token);
                    AddReminders(tracked, s, now);
                    db.DemoHistory.Add(DemoHistory.Create(tenantId, id, "Rescheduled", $"{Support.ActorLabel(ConversationActor())} moved the demo from {ConversionSupport.Local(oldStart, s.TimeZoneId, "yyyy-MM-dd HH:mm")} to {ConversionSupport.Local(start, s.TimeZoneId, "yyyy-MM-dd HH:mm")} ({s.TimeZoneId}).", crm.UserId, ConversationActor(), now));
                    await db.DemoReps.Where(x => x.TenantId == tenantId && x.UserId == repUser).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastAssignedAtUtc, now), token);
                    crm.Activity(tracked.LeadId, LeadActivityType.Updated, $"Demo rescheduled to {ConversionSupport.Local(start, s.TimeZoneId, "yyyy-MM-dd HH:mm")} ({s.TimeZoneId}).", new { demo = id });
                    crm.Record("Demo.Rescheduled", "Demo", id, $"Demo rescheduled to {start:u}.");
                    await db.SaveChangesAsync(token);
                }, ct);
                if (moved) break;
            }
            return moved ? Result.Success(await MapOneAsync(id, ct)) : Result.Failure<DemoDto>(SlotTaken);
        }, logger);

    private static string ConversationActor() => ConversionSupport.PersonActor;

    public Task<Result<DemoDto>> CancelAsync(Guid id, CancelDemoRequest r, CancellationToken ct = default) =>
        ChangeAsync(id, DemoStatus.Cancelled, r.Reason, "Cancelled", d => $"Demo cancelled{(string.IsNullOrWhiteSpace(r.Reason) ? "." : ": " + r.Reason!.Trim())}", ct);

    public Task<Result<DemoDto>> AcceptAsync(Guid id, CancellationToken ct = default) => ChangeAsync(id, DemoStatus.Booked, null, "Accepted", d => "Demo request accepted: the demo is booked.", ct);
    public Task<Result<DemoDto>> ConfirmAsync(Guid id, CancellationToken ct = default) => ChangeAsync(id, DemoStatus.Confirmed, null, "Confirmed", d => "The customer confirmed the demo.", ct);
    public Task<Result<DemoDto>> CompleteAsync(Guid id, CancellationToken ct = default) => ChangeAsync(id, DemoStatus.Completed, null, "Completed", d => "Demo completed.", ct);
    public Task<Result<DemoDto>> NoShowAsync(Guid id, CancellationToken ct = default) => ChangeAsync(id, DemoStatus.NoShow, null, "NoShow", d => "The customer did not attend the demo (no-show).", ct);

    private Task<Result<DemoDto>> ChangeAsync(Guid id, DemoStatus next, string? reason, string historyType, Func<Demo, string> summary, CancellationToken ct) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            var demo = await db.Demos.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
            if (demo is null) return Result.Failure<DemoDto>(Error.NotFound("Demo"));
            var wasHolding = DemoRules.HoldsSlot(demo.Status);
            demo.MoveTo(next, now, reason);
            if (next == DemoStatus.Booked && wasHolding)
            {
                var s = await Support.SettingsAsync(ct);
                AddReminders(demo, s, now);   // a requested demo gets its reminders once it is accepted
            }
            if (!DemoRules.HoldsSlot(next))
                await db.DemoReminders.Where(x => x.DemoId == id && x.TenantId == tenantId && (x.Status == ReminderStatus.Scheduled || x.Status == ReminderStatus.Processing))
                    .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, ReminderStatus.Cancelled).SetProperty(x => x.Reason, $"The demo is now {next}."), ct);
            var text = summary(demo);
            db.DemoHistory.Add(DemoHistory.Create(tenantId, id, historyType, $"{Support.ActorLabel(ConversionSupport.PersonActor)}: {text}", crm.UserId, ConversionSupport.PersonActor, now));
            crm.Activity(demo.LeadId, LeadActivityType.Updated, text, new { demo = id, status = next.ToString() });
            crm.Record("Demo." + historyType, "Demo", id, text);
            if (next == DemoStatus.Completed) Support.Emit(WorkflowEventTypes.DemoCompleted, demo.LeadId, demo.ContactId, $"demo:{id:N}", id.ToString("N"));
            await db.SaveChangesAsync(ct);
            return Result.Success(await MapOneAsync(id, ct));
        }, logger);

    public Task<Result<DemoDto>> UpdateAsync(Guid id, UpdateDemoRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var demo = await db.Demos.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
            if (demo is null) return Result.Failure<DemoDto>(Error.NotFound("Demo"));
            demo.SetMeetingLink(r.MeetingLink, crm.Now); demo.SetNotes(r.Notes, crm.Now);
            db.DemoHistory.Add(DemoHistory.Create(tenantId, id, "Edited", $"{Support.ActorLabel(ConversionSupport.PersonActor)} updated the meeting link or notes.", crm.UserId, ConversionSupport.PersonActor, crm.Now));
            crm.Record("Demo.Updated", "Demo", id, "Demo meeting link or notes updated.");
            await db.SaveChangesAsync(ct);
            return Result.Success(await MapOneAsync(id, ct));
        }, logger);

    // ---------------------------------------------------------------- reads

    public async Task<PagedResult<DemoDto>> ListAsync(DemoQuery q, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(q.Page, q.PageSize, 200);
        var query = crm.Db.Demos.AsNoTracking().Where(d => d.TenantId == tenantId);
        if (q.FromUtc is { } f) { var from = Utc(f); query = query.Where(d => d.StartUtc >= from); }
        if (q.ToUtc is { } t) { var to = Utc(t); query = query.Where(d => d.StartUtc < to); }
        if (q.Status is { } st) query = query.Where(d => d.Status == st);
        if (q.LeadId is { } l) query = query.Where(d => d.LeadId == l);
        if (q.RepUserId is { } rp) query = query.Where(d => d.RepUserId == rp);
        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderBy(d => d.StartUtc).ThenBy(d => d.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<DemoDto>(await MapAsync(rows, false, ct), page, size, total);
    }

    public async Task<Result<DemoDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var d = await crm.Db.Demos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        return d is null ? Result.Failure<DemoDto>(Error.NotFound("Demo")) : Result.Success((await MapAsync([d], true, ct))[0]);
    }

    private async Task<DemoDto> MapOneAsync(Guid id, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var d = await crm.Db.Demos.AsNoTracking().FirstAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        return (await MapAsync([d], true, ct))[0];
    }

    private async Task<List<DemoDto>> MapAsync(List<Demo> rows, bool detail, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var db = crm.Db; var s = await Support.SettingsAsync(ct);
        var leadIds = rows.Select(r => r.LeadId).Distinct().ToList(); var contactIds = rows.Where(r => r.ContactId != null).Select(r => r.ContactId!.Value).Distinct().ToList();
        var leads = await db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && leadIds.Contains(l.Id)).Select(l => new { l.Id, l.Name }).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var contacts = await db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && contactIds.Contains(c.Id)).Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var users = await crm.UserNamesAsync(rows.Select(r => (Guid?)r.RepUserId), ct);
        var ids = rows.Select(r => r.Id).ToList();
        var reminders = detail ? await db.DemoReminders.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.DemoId)).OrderBy(x => x.DueAtUtc).ToListAsync(ct) : [];
        var history = detail ? await db.DemoHistory.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.DemoId)).OrderBy(x => x.OccurredAtUtc).ToListAsync(ct) : [];
        var actors = await crm.UserNamesAsync(history.Select(h => h.ActorUserId), ct);
        return rows.Select(d => new DemoDto(d.Id, d.LeadId, leads.GetValueOrDefault(d.LeadId) ?? "(removed lead)", d.ContactId, d.ContactId is { } c ? contacts.GetValueOrDefault(c) : null, d.RepUserId, users.GetValueOrDefault(d.RepUserId) ?? "(removed user)",
            d.StartUtc, d.EndUtc, ConversionSupport.Local(d.StartUtc, s.TimeZoneId, "HH:mm"), ConversionSupport.Local(d.EndUtc, s.TimeZoneId, "HH:mm"), ConversionSupport.Local(d.StartUtc, s.TimeZoneId, "yyyy-MM-dd"), s.TimeZoneId,
            d.Status, d.Source, d.MeetingLink, d.Notes, d.CancelReason, d.Generation, d.CreatedAtUtc,
            reminders.Where(x => x.DemoId == d.Id).Select(x => new DemoReminderDto(x.Id, x.OffsetMinutes, x.DueAtUtc, x.Status, x.Reason, x.MessageId, x.ProcessedAtUtc)).ToList(),
            history.Where(h => h.DemoId == d.Id).Select(h => new DemoHistoryDto(h.Type, h.Summary, h.ActorUserId is { } a ? actors.GetValueOrDefault(a) : null, h.ActorKind, h.OccurredAtUtc)).ToList())).ToList();
    }
}
