using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LeadPilot.Application.Conversion;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Crm;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Infrastructure.Workflows;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Conversion;

/// <summary>Shared helpers of the conversion services: settings, time zones, the outbox events and the actor labels.</summary>
internal sealed class ConversionSupport(CrmContext crm)
{
    public const string PersonActor = "person", AutomationActor = "automation", SystemActor = "system";

    public async Task<ConversionSettings> SettingsAsync(CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        return await crm.Db.ConversionSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct) ?? ConversionSettings.Create(tenantId, crm.Now);
    }

    public static ConversionSettingsDto ToDto(ConversionSettings s) => new(s.TimeZoneId, s.SlotMinutes, s.MinNoticeMinutes, s.MaxDaysAhead, s.Assignment, s.MeetingLinkTemplate, s.ConfirmAutomatedBookings,
        s.RemindersEnabled, s.ReminderChannelAccountId, s.ReminderOffsets, s.ReminderBody, s.TrialDefaultDays, s.TrialExpiryNoticeHours, s.TrialCustomerNoticesEnabled, s.TrialNoticeBody, s.PaymentDueDays, s.PaymentInstructions, s.Version);

    /// <summary>The conversion events reach the workflow engine through the same transactional outbox as every other domain event: the row is saved with the business change, or not at all.</summary>
    public void Emit(string eventType, Guid leadId, Guid? contactId, string detail, string correlation) => WorkflowEvents.Emit(crm, eventType, leadId, contactId, null, null, detail, correlation);

    public string ActorLabel(string actorKind) => actorKind == AutomationActor ? "Automation (AI assistant or workflow)" : actorKind == SystemActor ? "System" : crm.Actor ?? "A team member";

    public async Task<Contact?> ContactForNoticeAsync(Guid leadId, Guid? contactId, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var q = crm.Db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && c.LeadId == leadId && !c.IsDeleted && c.PhoneE164 != null);
        if (contactId is { } id) q = q.Where(c => c.Id == id);
        return await q.OrderByDescending(c => c.IsPrimary).ThenBy(c => c.CreatedAtUtc).FirstOrDefaultAsync(ct);
    }

    public static void DetachAdded(Microsoft.EntityFrameworkCore.DbContext db)
    {
        foreach (var e in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList()) e.State = EntityState.Detached;
    }

    public static string Local(DateTime utc, string zoneId, string format) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), SlotCalculator.FindZone(zoneId)).ToString(format, CultureInfo.InvariantCulture);
}

/// <summary>The tiny, closed template language of reminders and notices: {placeholder} only, from a fixed list, filled with sanitised values. No expressions, no scripting.</summary>
public static partial class ConversionText
{
    public static readonly IReadOnlyList<string> DemoPlaceholders = ["name", "first_name", "date", "time", "timezone", "link", "rep"];
    public static readonly IReadOnlyList<string> TrialPlaceholders = ["name", "first_name", "plan", "date"];

    [GeneratedRegex(@"\{([a-z_]+)\}")] private static partial Regex Placeholder();

    public static string? Validate(string text, IReadOnlyList<string> allowed)
    {
        foreach (Match m in Placeholder().Matches(text))
            if (!allowed.Contains(m.Groups[1].Value)) return $"Unknown placeholder {{{m.Groups[1].Value}}}. Allowed: " + string.Join(", ", allowed.Select(a => "{" + a + "}")) + ".";
        var stripped = Placeholder().Replace(text, "");
        return stripped.Contains('{') || stripped.Contains('}') ? "Braces are only allowed around a placeholder such as {first_name}." : null;
    }

    public static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? Clean(v) : string.Empty).Trim();

    public static string FirstName(string? name) => (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;

    private static string Clean(string v)
    {
        var sb = new StringBuilder(v.Length);
        foreach (var ch in v) if (!char.IsControl(ch) && ch is not ('{' or '}' or '<' or '>')) sb.Append(ch);
        var s = sb.ToString().Trim();
        return s.Length <= 200 ? s : s[..200];
    }
}
