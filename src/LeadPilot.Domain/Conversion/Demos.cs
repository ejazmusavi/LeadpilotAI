using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;

namespace LeadPilot.Domain.Conversion;

public enum DemoStatus { Requested = 1, Booked = 2, Confirmed = 3, Completed = 4, NoShow = 5, Cancelled = 6 }
public enum DemoSource { Staff = 1, Automation = 2 }
public enum AssignmentStrategy { RoundRobin = 1, LeastLoaded = 2 }
public enum ExceptionKind { Closed = 1, ExtraOpen = 2 }
public enum ReminderStatus { Scheduled = 1, Processing = 2, Sent = 3, Skipped = 4, Failed = 5, Cancelled = 6 }

/// <summary>Pure state-machine rules of a demo.</summary>
public static class DemoRules
{
    /// <summary>Statuses that hold the rep's time (a slot is not free while a demo is in one of these).</summary>
    public static readonly DemoStatus[] Active = [DemoStatus.Requested, DemoStatus.Booked, DemoStatus.Confirmed];

    public static bool HoldsSlot(DemoStatus s) => s is DemoStatus.Requested or DemoStatus.Booked or DemoStatus.Confirmed;

    public static bool CanMove(DemoStatus from, DemoStatus to) => (from, to) switch
    {
        (DemoStatus.Requested, DemoStatus.Booked) => true,
        (DemoStatus.Requested, DemoStatus.Cancelled) => true,
        (DemoStatus.Booked, DemoStatus.Confirmed) => true,
        (DemoStatus.Booked, DemoStatus.Completed) => true,
        (DemoStatus.Booked, DemoStatus.NoShow) => true,
        (DemoStatus.Booked, DemoStatus.Cancelled) => true,
        (DemoStatus.Confirmed, DemoStatus.Completed) => true,
        (DemoStatus.Confirmed, DemoStatus.NoShow) => true,
        (DemoStatus.Confirmed, DemoStatus.Cancelled) => true,
        _ => false,
    };

    /// <summary>Key of the unique index that makes a double booking impossible even if two transactions race: one active demo per rep and start instant.</summary>
    public static string SlotKey(Guid repUserId, DateTime startUtc) => $"{repUserId:N}:{startUtc:yyyyMMddHHmm}";

    public static string SlotId(DateTime startUtc) => "s" + startUtc.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParseSlotId(string? id, out DateTime startUtc)
    {
        startUtc = default;
        return id is { Length: 13 } && id[0] == 's' && DateTime.TryParseExact(id[1..], "yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out startUtc);
    }
}

/// <summary>Workspace-wide settings of the conversion module (one row per tenant): demo calendar, reminders, trials and payment request defaults.</summary>
public class ConversionSettings : Entity, ITenantOwned
{
    private ConversionSettings() { }
    public Guid TenantId { get; private set; }
    /// <summary>IANA time zone of the workspace: slot times are shown in it, and it is the default of a rep without a zone of their own.</summary>
    public string TimeZoneId { get; private set; } = "UTC";
    public int SlotMinutes { get; private set; } = 30;
    public int MinNoticeMinutes { get; private set; } = 120;
    public int MaxDaysAhead { get; private set; } = 30;
    public AssignmentStrategy Assignment { get; private set; } = AssignmentStrategy.RoundRobin;
    /// <summary>Optional, e.g. https://meet.example.com/{code}. {code} becomes a short random code per demo. Nothing is created at any meeting provider.</summary>
    public string? MeetingLinkTemplate { get; private set; }
    public bool ConfirmAutomatedBookings { get; private set; }
    public bool RemindersEnabled { get; private set; }
    public Guid? ReminderChannelAccountId { get; private set; }
    /// <summary>Minutes before the demo, comma separated (default 1 day and 1 hour).</summary>
    public string ReminderOffsets { get; private set; } = "1440,60";
    public string ReminderBody { get; private set; } = DefaultReminderBody;
    public int TrialDefaultDays { get; private set; } = 14;
    public int TrialExpiryNoticeHours { get; private set; } = 48;
    public bool TrialCustomerNoticesEnabled { get; private set; }
    public string TrialNoticeBody { get; private set; } = DefaultTrialNoticeBody;
    public int PaymentDueDays { get; private set; } = 7;
    /// <summary>Text written by the workspace (bank details, how to pay). The platform never invents payment instructions.</summary>
    public string? PaymentInstructions { get; private set; }
    public int Version { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? UpdatedAtUtc { get; private set; }

    public const string DefaultReminderBody = "Hello {first_name}, this is a reminder of your demo on {date} at {time} ({timezone}). {link}";
    public const string DefaultTrialNoticeBody = "Hello {first_name}, your trial of {plan} ends on {date}. Please reply if you would like to continue.";

    public static ConversionSettings Create(Guid tenantId, DateTime nowUtc) => new() { TenantId = tenantId, CreatedAtUtc = nowUtc };

    public static bool IsValidZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
        try { TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); return true; } catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return false; }
    }

    public static IReadOnlyList<int> ParseOffsets(string? csv)
    {
        var list = new List<int>();
        foreach (var part in (csv ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var m) && m is >= 5 and <= 20160 && !list.Contains(m)) list.Add(m);
        return list.OrderByDescending(x => x).Take(4).ToList();
    }

    public void Update(string timeZoneId, int slotMinutes, int minNoticeMinutes, int maxDaysAhead, AssignmentStrategy assignment, string? meetingLinkTemplate, bool confirmAutomated,
        bool remindersEnabled, Guid? reminderAccountId, string? reminderOffsets, string? reminderBody, int trialDefaultDays, int trialNoticeHours, bool trialCustomerNotices, string? trialNoticeBody,
        int paymentDueDays, string? paymentInstructions, DateTime nowUtc)
    {
        if (!IsValidZone(timeZoneId)) throw new DomainException("Choose a valid IANA time zone, for example Asia/Karachi.");
        if (slotMinutes is < 10 or > 240) throw new DomainException("A demo slot must be between 10 and 240 minutes.");
        if (minNoticeMinutes is < 0 or > 10080) throw new DomainException("Minimum notice must be between 0 minutes and 7 days.");
        if (maxDaysAhead is < 1 or > 180) throw new DomainException("Bookings may reach 1 to 180 days ahead.");
        if (!Enum.IsDefined(assignment)) throw new DomainException("Unknown assignment strategy.");
        var link = Guard.Optional(meetingLinkTemplate, 300, "Meeting link template");
        if (link is not null && (!link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(link.Replace("{code}", "x"), UriKind.Absolute, out _)))
            throw new DomainException("The meeting link template must be an https:// address (optionally containing {code}).");
        var offsets = ParseOffsets(reminderOffsets);
        if (remindersEnabled && offsets.Count == 0) throw new DomainException("Give at least one reminder time (minutes before the demo, 5 to 20160), for example 1440,60.");
        if (remindersEnabled && reminderAccountId is null) throw new DomainException("Choose the WhatsApp account that sends reminders, or switch reminders off.");
        if (trialDefaultDays is < 1 or > 365) throw new DomainException("A trial lasts 1 to 365 days.");
        if (trialNoticeHours is < 1 or > 720) throw new DomainException("The expiry notice is 1 to 720 hours before the end.");
        if (trialCustomerNotices && reminderAccountId is null) throw new DomainException("Customer trial notices need the WhatsApp account chosen for reminders.");
        if (paymentDueDays is < 0 or > 365) throw new DomainException("Payment is due 0 to 365 days after the request.");
        TimeZoneId = timeZoneId.Trim(); SlotMinutes = slotMinutes; MinNoticeMinutes = minNoticeMinutes; MaxDaysAhead = maxDaysAhead; Assignment = assignment;
        MeetingLinkTemplate = link; ConfirmAutomatedBookings = confirmAutomated; RemindersEnabled = remindersEnabled; ReminderChannelAccountId = reminderAccountId;
        ReminderOffsets = offsets.Count == 0 ? "1440,60" : string.Join(',', offsets);
        ReminderBody = Guard.Optional(reminderBody, 600, "Reminder text") ?? DefaultReminderBody;
        TrialDefaultDays = trialDefaultDays; TrialExpiryNoticeHours = trialNoticeHours; TrialCustomerNoticesEnabled = trialCustomerNotices;
        TrialNoticeBody = Guard.Optional(trialNoticeBody, 600, "Trial notice text") ?? DefaultTrialNoticeBody;
        PaymentDueDays = paymentDueDays; PaymentInstructions = Guard.Optional(paymentInstructions, 1500, "Payment instructions");
        Version++; UpdatedAtUtc = nowUtc;
    }
}

/// <summary>A team member who takes demos. Bookings of one rep are serialised through this row (it is locked inside the booking transaction).</summary>
public class DemoRep : Entity, ITenantOwned
{
    private DemoRep() { }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public bool IsActive { get; private set; } = true;
    public string? TimeZoneId { get; private set; }
    public int BufferMinutes { get; private set; }
    public int MaxPerDay { get; private set; } = 6;
    public DateTime? LastAssignedAtUtc { get; private set; }
    /// <summary>Bumped by every booking transaction: the UPDATE takes the row lock that serialises the rep's bookings.</summary>
    public int Version { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? UpdatedAtUtc { get; private set; }

    public static DemoRep Create(Guid tenantId, Guid userId, string? timeZoneId, int buffer, int maxPerDay, DateTime nowUtc)
    {
        var r = new DemoRep { TenantId = tenantId, UserId = userId, CreatedAtUtc = nowUtc };
        r.Set(timeZoneId, buffer, maxPerDay);
        return r;
    }

    public void Update(bool active, string? timeZoneId, int buffer, int maxPerDay, DateTime nowUtc) { Set(timeZoneId, buffer, maxPerDay); IsActive = active; UpdatedAtUtc = nowUtc; }
    public void MarkAssigned(DateTime nowUtc) => LastAssignedAtUtc = nowUtc;

    private void Set(string? timeZoneId, int buffer, int maxPerDay)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId) && !ConversionSettings.IsValidZone(timeZoneId)) throw new DomainException("Choose a valid IANA time zone.");
        if (buffer is < 0 or > 120) throw new DomainException("The buffer between demos is 0 to 120 minutes.");
        if (maxPerDay is < 1 or > 30) throw new DomainException("A rep takes 1 to 30 demos per day.");
        TimeZoneId = string.IsNullOrWhiteSpace(timeZoneId) ? null : timeZoneId.Trim(); BufferMinutes = buffer; MaxPerDay = maxPerDay;
    }
}

/// <summary>Weekly hours of a rep in the rep's local time (minutes since local midnight). Several rows per weekday are allowed (a lunch break is two rows).</summary>
public class DemoAvailabilityRule : Entity, ITenantOwned
{
    private DemoAvailabilityRule() { }
    public Guid TenantId { get; private set; }
    public Guid RepId { get; private set; }
    public DayOfWeek Day { get; private set; }
    public int StartMinute { get; private set; }
    public int EndMinute { get; private set; }

    public static DemoAvailabilityRule Create(Guid tenantId, Guid repId, DayOfWeek day, int startMinute, int endMinute)
    {
        if (startMinute < 0 || endMinute > 1440 || endMinute <= startMinute) throw new DomainException("A working period must end after it starts, within one day.");
        return new() { TenantId = tenantId, RepId = repId, Day = day, StartMinute = startMinute, EndMinute = endMinute };
    }
}

/// <summary>A date exception: time off (whole day, or a window) or extra open hours.</summary>
public class DemoAvailabilityException : Entity, ITenantOwned
{
    private DemoAvailabilityException() { }
    public Guid TenantId { get; private set; }
    public Guid RepId { get; private set; }
    public DateTime Date { get; private set; }
    public ExceptionKind Kind { get; private set; }
    public int? StartMinute { get; private set; }
    public int? EndMinute { get; private set; }
    public string? Note { get; private set; }

    public static DemoAvailabilityException Create(Guid tenantId, Guid repId, DateTime date, ExceptionKind kind, int? startMinute, int? endMinute, string? note)
    {
        if (!Enum.IsDefined(kind)) throw new DomainException("Unknown exception type.");
        if (startMinute is null != endMinute is null) throw new DomainException("Give both a start and an end, or neither (whole day).");
        if (startMinute is { } s && (s < 0 || endMinute!.Value > 1440 || endMinute.Value <= s)) throw new DomainException("The window must end after it starts, within one day.");
        if (kind == ExceptionKind.ExtraOpen && startMinute is null) throw new DomainException("Extra opening hours need a start and an end.");
        return new() { TenantId = tenantId, RepId = repId, Date = date.Date, Kind = kind, StartMinute = startMinute, EndMinute = endMinute, Note = Guard.Optional(note, 200, "Note") };
    }
}

public class Demo : AuditableEntity, ITenantOwned
{
    private Demo() { }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }
    public Guid? ContactId { get; private set; }
    public Guid RepUserId { get; private set; }
    public DateTime StartUtc { get; private set; }
    public DateTime EndUtc { get; private set; }
    public DemoStatus Status { get; private set; }
    public DemoSource Source { get; private set; }
    public string? MeetingLink { get; private set; }
    public string? Notes { get; private set; }
    public string? CancelReason { get; private set; }
    /// <summary>Set while the demo holds the rep's time; unique per tenant. Cleared by cancel / no-show / completion.</summary>
    public string? ActiveSlot { get; private set; }
    /// <summary>Incremented by every reschedule: reminders and their idempotency keys belong to one generation.</summary>
    public int Generation { get; private set; } = 1;
    public DateTime? CompletedAtUtc { get; private set; }

    public static Demo Create(Guid tenantId, Guid leadId, Guid? contactId, Guid repUserId, DateTime startUtc, DateTime endUtc, DemoStatus initial, DemoSource source, string? notes, string? link, DateTime nowUtc, string? actor)
    {
        if (initial is not (DemoStatus.Requested or DemoStatus.Booked)) throw new DomainException("A demo starts as Requested or Booked.");
        if (endUtc <= startUtc) throw new DomainException("A demo must end after it starts.");
        return new()
        {
            TenantId = tenantId, LeadId = leadId, ContactId = contactId, RepUserId = repUserId, StartUtc = startUtc, EndUtc = endUtc, Status = initial, Source = source,
            Notes = Guard.Optional(notes, 500, "Notes"), MeetingLink = CleanLink(link), ActiveSlot = DemoRules.SlotKey(repUserId, startUtc), CreatedAtUtc = nowUtc, CreatedBy = actor,
        };
    }

    public static string? CleanLink(string? link)
    {
        var l = Guard.Optional(link, 500, "Meeting link");
        if (l is null) return null;
        if (!Uri.TryCreate(l, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) throw new DomainException("The meeting link must be an https:// address.");
        return l;
    }

    public void MoveTo(DemoStatus next, DateTime nowUtc, string? reason = null)
    {
        if (!DemoRules.CanMove(Status, next)) throw new DomainException($"A demo that is {Status} cannot become {next}.");
        if (next is DemoStatus.Completed or DemoStatus.NoShow && nowUtc < StartUtc) throw new DomainException("A demo that has not started yet cannot be completed or marked as a no-show.");
        Status = next; UpdatedAtUtc = nowUtc;
        if (!DemoRules.HoldsSlot(next)) ActiveSlot = null;
        if (next == DemoStatus.Cancelled) CancelReason = Guard.Optional(reason, 300, "Reason");
        if (next == DemoStatus.Completed) CompletedAtUtc = nowUtc;
    }

    public void Reschedule(Guid repUserId, DateTime startUtc, DateTime endUtc, DateTime nowUtc)
    {
        if (!DemoRules.HoldsSlot(Status)) throw new DomainException($"A demo that is {Status} cannot be rescheduled.");
        if (endUtc <= startUtc) throw new DomainException("A demo must end after it starts.");
        RepUserId = repUserId; StartUtc = startUtc; EndUtc = endUtc; ActiveSlot = DemoRules.SlotKey(repUserId, startUtc); Generation++; UpdatedAtUtc = nowUtc;
        if (Status == DemoStatus.Confirmed) Status = DemoStatus.Booked;   // the customer has to confirm the new time again
    }

    public void SetMeetingLink(string? link, DateTime nowUtc) { MeetingLink = CleanLink(link); UpdatedAtUtc = nowUtc; }
    public void SetNotes(string? notes, DateTime nowUtc) { Notes = Guard.Optional(notes, 500, "Notes"); UpdatedAtUtc = nowUtc; }
}

public class DemoHistory : Entity, ITenantOwned, IAppendOnly
{
    private DemoHistory() { }
    public Guid TenantId { get; private set; }
    public Guid DemoId { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string Summary { get; private set; } = string.Empty;
    public Guid? ActorUserId { get; private set; }
    public string ActorKind { get; private set; } = "person";
    public DateTime OccurredAtUtc { get; private set; }

    public static DemoHistory Create(Guid tenantId, Guid demoId, string type, string summary, Guid? actorUserId, string actorKind, DateTime nowUtc) =>
        new() { TenantId = tenantId, DemoId = demoId, Type = type, Summary = summary.Length <= 500 ? summary : summary[..500], ActorUserId = actorUserId, ActorKind = actorKind, OccurredAtUtc = nowUtc };
}

public class DemoReminder : Entity, ITenantOwned
{
    private DemoReminder() { }
    public Guid TenantId { get; private set; }
    public Guid DemoId { get; private set; }
    public int Generation { get; private set; }
    public int OffsetMinutes { get; private set; }
    public DateTime DueAtUtc { get; private set; }
    public ReminderStatus Status { get; private set; } = ReminderStatus.Scheduled;
    public string? Reason { get; private set; }
    public Guid? MessageId { get; private set; }
    public DateTime? ProcessedAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static DemoReminder Create(Guid tenantId, Guid demoId, int generation, int offsetMinutes, DateTime dueAtUtc, DateTime nowUtc) =>
        new() { TenantId = tenantId, DemoId = demoId, Generation = generation, OffsetMinutes = offsetMinutes, DueAtUtc = dueAtUtc, CreatedAtUtc = nowUtc };

    public string IdempotencyKey => $"demo-reminder:{DemoId:N}:{Generation}:{OffsetMinutes}";
}
