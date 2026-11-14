using LeadPilot.Application.Conversion;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Conversion;

public sealed class ConversionSettingsService(CrmContext crm, ILogger<ConversionSettingsService> logger) : IConversionSettingsService
{
    private ConversionSupport Support => new(crm);

    public async Task<ConversionSettingsDto> GetAsync(CancellationToken ct = default) => ConversionSupport.ToDto(await Support.SettingsAsync(ct));

    public Task<Result<ConversionSettingsDto>> SaveAsync(SaveConversionSettingsRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var s = await db.ConversionSettings.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
            var isNew = s is null;
            if (s is null) { s = ConversionSettings.Create(tenantId, crm.Now); db.ConversionSettings.Add(s); }
            else if (r.ExpectedVersion is { } v && v != s.Version) return Result.Failure<ConversionSettingsDto>(Error.Conflict("The settings were changed by someone else. Reload and try again."));
            if (r.ReminderChannelAccountId is { } acc && !await db.ChannelAccounts.AnyAsync(a => a.Id == acc && a.TenantId == tenantId && !a.IsDeleted, ct))
                return Result.Failure<ConversionSettingsDto>(Error.Validation("That WhatsApp account does not exist in this workspace."));
            var bad = ConversionText.Validate(r.ReminderBody ?? ConversionSettings.DefaultReminderBody, ConversionText.DemoPlaceholders) ?? ConversionText.Validate(r.TrialNoticeBody ?? ConversionSettings.DefaultTrialNoticeBody, ConversionText.TrialPlaceholders);
            if (bad is not null) return Result.Failure<ConversionSettingsDto>(Error.Validation(bad));
            s.Update(r.TimeZoneId, r.SlotMinutes, r.MinNoticeMinutes, r.MaxDaysAhead, r.Assignment, r.MeetingLinkTemplate, r.ConfirmAutomatedBookings, r.RemindersEnabled, r.ReminderChannelAccountId, r.ReminderOffsets,
                r.ReminderBody, r.TrialDefaultDays, r.TrialExpiryNoticeHours, r.TrialCustomerNoticesEnabled, r.TrialNoticeBody, r.PaymentDueDays, r.PaymentInstructions, crm.Now);
            crm.Record("ConversionSettings.Updated", "ConversionSettings", s.Id, $"Demo, trial and payment settings saved (time zone {s.TimeZoneId}, slot {s.SlotMinutes} min, reminders {(s.RemindersEnabled ? "on" : "off")}).");
            await db.SaveChangesAsync(ct);
            return Result.Success(ConversionSupport.ToDto(s));
        }, logger);

    // ---------------------------------------------------------------- reps

    public async Task<IReadOnlyList<DemoRepDto>> RepsAsync(CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var reps = await crm.Db.DemoReps.AsNoTracking().Where(r => r.TenantId == tenantId).ToListAsync(ct);
        return await MapAsync(reps, ct);
    }

    public async Task<Result<DemoRepDto>> GetRepAsync(Guid repId, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var rep = await crm.Db.DemoReps.AsNoTracking().FirstOrDefaultAsync(r => r.Id == repId && r.TenantId == tenantId, ct);
        return rep is null ? Result.Failure<DemoRepDto>(Error.NotFound("Sales representative")) : Result.Success((await MapAsync([rep], ct))[0]);
    }

    public Task<Result<DemoRepDto>> SaveRepAsync(SaveRepRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            if (!await crm.IsActiveMemberAsync(r.UserId, ct)) return Result.Failure<DemoRepDto>(Error.Validation("The representative must be an active member of this workspace."));
            var rep = await db.DemoReps.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == r.UserId, ct);
            if (rep is null) { rep = DemoRep.Create(tenantId, r.UserId, r.TimeZoneId, r.BufferMinutes, r.MaxPerDay, crm.Now); db.DemoReps.Add(rep); }
            rep.Update(r.IsActive, r.TimeZoneId, r.BufferMinutes, r.MaxPerDay, crm.Now);
            crm.Record("DemoRep.Saved", "DemoRep", rep.Id, $"Demo representative settings saved (active: {rep.IsActive}).");
            await db.SaveChangesAsync(ct);
            return Result.Success((await MapAsync([rep], ct))[0]);
        }, logger);

    public Task<Result<DemoRepDto>> SaveRulesAsync(Guid repId, SaveRulesRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var rep = await db.DemoReps.AsNoTracking().FirstOrDefaultAsync(x => x.Id == repId && x.TenantId == tenantId, ct);
            if (rep is null) return Result.Failure<DemoRepDto>(Error.NotFound("Sales representative"));
            if (r.Rules.Count > 70) return Result.Failure<DemoRepDto>(Error.Validation("At most 70 working periods."));
            var built = r.Rules.Select(x => DemoAvailabilityRule.Create(tenantId, repId, x.Day, x.StartMinute, x.EndMinute)).ToList();
            foreach (var g in built.GroupBy(x => x.Day))
            {
                var sorted = g.OrderBy(x => x.StartMinute).ToList();
                for (var i = 1; i < sorted.Count; i++) if (sorted[i].StartMinute < sorted[i - 1].EndMinute) return Result.Failure<DemoRepDto>(Error.Validation($"Working periods on {g.Key} overlap."));
            }
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.DemoAvailabilityRules.Where(x => x.TenantId == tenantId && x.RepId == repId).ExecuteDeleteAsync(ct);
            db.DemoAvailabilityRules.AddRange(built);
            crm.Record("DemoRep.HoursSaved", "DemoRep", repId, $"Weekly hours saved ({built.Count} periods).");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Result.Success((await MapAsync([rep], ct))[0]);
        }, logger);

    public Task<Result<DemoRepDto>> AddExceptionAsync(Guid repId, AddExceptionRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var rep = await db.DemoReps.AsNoTracking().FirstOrDefaultAsync(x => x.Id == repId && x.TenantId == tenantId, ct);
            if (rep is null) return Result.Failure<DemoRepDto>(Error.NotFound("Sales representative"));
            if (r.Date.Date < crm.Now.Date.AddDays(-1) || r.Date.Date > crm.Now.Date.AddDays(730)) return Result.Failure<DemoRepDto>(Error.Validation("Choose a date from yesterday to two years ahead."));
            if (await db.DemoAvailabilityExceptions.CountAsync(x => x.TenantId == tenantId && x.RepId == repId, ct) >= 300) return Result.Failure<DemoRepDto>(Error.Validation("This representative has 300 exceptions. Remove old ones first."));
            db.DemoAvailabilityExceptions.Add(DemoAvailabilityException.Create(tenantId, repId, r.Date, r.Kind, r.StartMinute, r.EndMinute, r.Note));
            crm.Record("DemoRep.ExceptionAdded", "DemoRep", repId, $"{r.Kind} exception added for {r.Date:yyyy-MM-dd}.");
            await db.SaveChangesAsync(ct);
            return Result.Success((await MapAsync([rep], ct))[0]);
        }, logger);

    public Task<Result<DemoRepDto>> RemoveExceptionAsync(Guid repId, Guid exceptionId, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var rep = await db.DemoReps.AsNoTracking().FirstOrDefaultAsync(x => x.Id == repId && x.TenantId == tenantId, ct);
            if (rep is null) return Result.Failure<DemoRepDto>(Error.NotFound("Sales representative"));
            var removed = await db.DemoAvailabilityExceptions.Where(x => x.Id == exceptionId && x.RepId == repId && x.TenantId == tenantId).ExecuteDeleteAsync(ct);
            if (removed == 0) return Result.Failure<DemoRepDto>(Error.NotFound("Exception"));
            crm.Record("DemoRep.ExceptionRemoved", "DemoRep", repId, "An availability exception was removed.");
            await db.SaveChangesAsync(ct);
            return Result.Success((await MapAsync([rep], ct))[0]);
        }, logger);

    public async Task<IReadOnlyList<LookupItem>> CandidateRepsAsync(CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var db = crm.Db;
        return await (from m in db.UserTenants.AsNoTracking()
                      join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                      where m.TenantId == tenantId && m.Status == MembershipStatus.Active && u.IsActive && !db.DemoReps.Any(r => r.TenantId == tenantId && r.UserId == u.Id)
                      orderby u.DisplayName
                      select new LookupItem(u.Id, u.DisplayName)).ToListAsync(ct);
    }

    private async Task<IReadOnlyList<DemoRepDto>> MapAsync(List<DemoRep> reps, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var db = crm.Db; var settings = await Support.SettingsAsync(ct);
        var ids = reps.Select(r => r.Id).ToList();
        var rules = await db.DemoAvailabilityRules.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.RepId)).ToListAsync(ct);
        var from = crm.Now.Date.AddDays(-1);
        var ex = await db.DemoAvailabilityExceptions.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.RepId) && x.Date >= from).OrderBy(x => x.Date).ToListAsync(ct);
        var names = await crm.UserNamesAsync(reps.Select(r => (Guid?)r.UserId), ct);
        return reps.OrderBy(r => names.GetValueOrDefault(r.UserId)).Select(r => new DemoRepDto(r.Id, r.UserId, names.GetValueOrDefault(r.UserId) ?? "(removed user)", r.IsActive, r.TimeZoneId, r.TimeZoneId ?? settings.TimeZoneId, r.BufferMinutes, r.MaxPerDay,
            rules.Where(x => x.RepId == r.Id).OrderBy(x => x.Day == DayOfWeek.Sunday ? 7 : (int)x.Day).ThenBy(x => x.StartMinute).Select(x => new RuleDto(x.Day, x.StartMinute, x.EndMinute)).ToList(),
            ex.Where(x => x.RepId == r.Id).Select(x => new ExceptionDto(x.Id, x.Date, x.Kind, x.StartMinute, x.EndMinute, x.Note)).ToList())).ToList();
    }
}
