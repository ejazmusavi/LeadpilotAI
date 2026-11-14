using LeadPilot.Domain.Conversion;

namespace LeadPilot.Application.Conversion;

public sealed record RuleInput(DayOfWeek Day, int StartMinute, int EndMinute);
public sealed record ExceptionInput(DateTime Date, ExceptionKind Kind, int? StartMinute, int? EndMinute);
public sealed record BusyInput(DateTime StartUtc, DateTime EndUtc);

/// <summary>Everything the calculator needs for ONE rep. Pure data: the same function serves the calendar, the AI tool, the workflow action and the booking transaction (which re-runs it after locking the rep).</summary>
public sealed record RepAvailabilityInput(Guid RepUserId, string TimeZoneId, int BufferMinutes, int MaxPerDay, IReadOnlyList<RuleInput> Rules, IReadOnlyList<ExceptionInput> Exceptions, IReadOnlyList<BusyInput> Busy);

public sealed record SlotQuery(DateTime NowUtc, DateTime FromUtc, DateTime ToUtc, int SlotMinutes, int MinNoticeMinutes, int MaxDaysAhead);

public sealed record RepSlot(Guid RepUserId, DateTime StartUtc, DateTime EndUtc);

/// <summary>
/// Slot generation. Rules and exceptions are written in the rep's LOCAL time; the result is UTC. Interpretation (documented, tested):
/// - A working period [start, end) is converted to UTC at both ends and slots are laid out in ELAPSED time from the start: on a spring-forward day a period that spans the gap is one hour shorter, on a fall-back
///   day a period that spans the repeated hour is one hour longer. Slots never start inside a skipped (non-existent) local time.
/// - A local time that happens twice (fall back) resolves to its FIRST occurrence (the daylight-saving offset).
/// - A local boundary that does not exist (spring forward) moves forward to the first valid instant after the gap.
/// - Busy periods block every slot that overlaps them plus the rep's buffer on both sides; the daily maximum counts the rep's demos on the rep's local calendar day.
/// - Nothing earlier than now + minimum notice, nothing later than the horizon, never in the past.
/// </summary>
public static class SlotCalculator
{
    public static IReadOnlyList<RepSlot> Calculate(RepAvailabilityInput rep, SlotQuery q)
    {
        var tz = FindZone(rep.TimeZoneId);
        var earliest = Max(q.FromUtc, q.NowUtc.AddMinutes(q.MinNoticeMinutes));
        var latest = Min(q.ToUtc, q.NowUtc.AddDays(q.MaxDaysAhead));
        if (latest <= earliest || q.SlotMinutes <= 0) return [];
        var slotLen = TimeSpan.FromMinutes(q.SlotMinutes);
        var buffer = TimeSpan.FromMinutes(rep.BufferMinutes);

        // local dates that can contain a slot (one day of margin on both sides covers every offset)
        var firstDate = TimeZoneInfo.ConvertTimeFromUtc(earliest, tz).Date.AddDays(-1);
        var lastDate = TimeZoneInfo.ConvertTimeFromUtc(latest, tz).Date.AddDays(1);

        var windows = new List<(DateTime Start, DateTime End)>();
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            var day = new List<(DateTime Start, DateTime End)>();
            foreach (var r in rep.Rules.Where(r => r.Day == date.DayOfWeek)) day.Add(Window(tz, date, r.StartMinute, r.EndMinute));
            foreach (var e in rep.Exceptions.Where(e => e.Date.Date == date && e.Kind == ExceptionKind.ExtraOpen && e.StartMinute is not null)) day.Add(Window(tz, date, e.StartMinute!.Value, e.EndMinute!.Value));
            var closed = rep.Exceptions.Where(e => e.Date.Date == date && e.Kind == ExceptionKind.Closed).Select(e => e.StartMinute is null ? Window(tz, date, 0, 1440) : Window(tz, date, e.StartMinute.Value, e.EndMinute!.Value)).ToList();
            foreach (var w in day)
            {
                if (w.End <= w.Start) continue;
                var cursor = w.Start;
                while (cursor + slotLen <= w.End)
                {
                    var end = cursor + slotLen;
                    if (!closed.Any(c => cursor < c.End && end > c.Start)) windows.Add((cursor, end));
                    cursor = end;
                }
            }
        }

        var result = new List<RepSlot>();
        var perDay = rep.Busy.GroupBy(b => TimeZoneInfo.ConvertTimeFromUtc(b.StartUtc, tz).Date).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (start, end) in windows.OrderBy(w => w.Start).DistinctBy(w => w.Start))
        {
            if (start < earliest || start >= latest) continue;
            if (rep.Busy.Any(b => start < b.EndUtc + buffer && end > b.StartUtc - buffer)) continue;
            var localDay = TimeZoneInfo.ConvertTimeFromUtc(start, tz).Date;
            if (perDay.GetValueOrDefault(localDay) >= rep.MaxPerDay) continue;
            result.Add(new RepSlot(rep.RepUserId, start, end));
        }
        return result;
    }

    /// <summary>The slots of a pool of reps grouped by start time (a time is bookable while at least one rep is free). The reps are in the order the assignment strategy gave them.</summary>
    public static IReadOnlyList<PooledSlot> Pool(IEnumerable<RepAvailabilityInput> reps, SlotQuery q) =>
        reps.SelectMany(r => Calculate(r, q)).GroupBy(s => s.StartUtc).OrderBy(g => g.Key)
            .Select(g => new PooledSlot(DemoRules.SlotId(g.Key), g.Key, g.First().EndUtc, g.Select(s => s.RepUserId).Distinct().ToList())).ToList();

    public static (DateTime Start, DateTime End) Window(TimeZoneInfo tz, DateTime localDate, int startMinute, int endMinute) =>
        (ToUtc(tz, localDate.Date.AddMinutes(startMinute)), ToUtc(tz, localDate.Date.AddMinutes(endMinute)));

    /// <summary>Local wall-clock time to UTC following the rules in the class summary.</summary>
    public static DateTime ToUtc(TimeZoneInfo tz, DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local))
        {
            // inside the spring-forward gap: the first valid instant after it
            var probe = local;
            for (var i = 0; i < 180 && tz.IsInvalidTime(probe); i++) probe = probe.AddMinutes(1);
            local = probe;
        }
        if (tz.IsAmbiguousTime(local)) return DateTime.SpecifyKind(local - tz.GetAmbiguousTimeOffsets(local).Max(), DateTimeKind.Utc);   // first occurrence = larger (daylight) offset
        return DateTime.SpecifyKind(local - tz.GetUtcOffset(local), DateTimeKind.Utc);
    }

    public static TimeZoneInfo FindZone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}

public sealed record PooledSlot(string SlotId, DateTime StartUtc, DateTime EndUtc, IReadOnlyList<Guid> RepUserIds);
