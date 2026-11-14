using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;

namespace LeadPilot.Domain.Conversion;

public enum TrialStatus { PendingProvisioning = 1, Active = 2, Expired = 3, Converted = 4, Cancelled = 5 }
public enum TrialSource { Staff = 1, Automation = 2 }

public class Trial : AuditableEntity, ITenantOwned
{
    private Trial() { }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }
    public Guid? ContactId { get; private set; }
    public Guid? PlanId { get; private set; }
    public string? PlanName { get; private set; }
    public string? ProductName { get; private set; }
    public TrialStatus Status { get; private set; } = TrialStatus.PendingProvisioning;
    public TrialSource Source { get; private set; }
    public int DurationDays { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }
    public string? ProvisioningNote { get; private set; }
    /// <summary>Account id / tenant id in the PRODUCT (entered by staff or returned by a provisioning backend).</summary>
    public string? ExternalAccountRef { get; private set; }
    public string? LoginUrl { get; private set; }
    // usage: only ever set from a recorded event or a manual update (see TrialEvent)
    public DateTime? LastActivityAtUtc { get; private set; }
    public int LoginCount { get; private set; }
    public int ActiveUsers { get; private set; }
    public string? UsageNotes { get; private set; }
    public DateTime? ExpiryNoticeAtUtc { get; private set; }
    public DateTime? EndedAtUtc { get; private set; }
    public string? EndReason { get; private set; }
    public Guid? ConvertedCustomerId { get; private set; }

    public static Trial Create(Guid tenantId, Guid leadId, Guid? contactId, Guid? planId, string? planName, string? productName, int durationDays, TrialSource source, string? notes, DateTime nowUtc, string? actor)
    {
        if (durationDays is < 1 or > 365) throw new DomainException("A trial lasts 1 to 365 days.");
        return new()
        {
            TenantId = tenantId, LeadId = leadId, ContactId = contactId, PlanId = planId, PlanName = Guard.Optional(planName, 100, "Plan"), ProductName = Guard.Optional(productName, 100, "Product"),
            DurationDays = durationDays, Source = source, ProvisioningNote = Guard.Optional(notes, 500, "Notes"), CreatedAtUtc = nowUtc, CreatedBy = actor,
        };
    }

    /// <summary>The product account exists: the clock starts now.</summary>
    public void MarkProvisioned(string? externalRef, string? loginUrl, string? note, DateTime nowUtc)
    {
        if (Status != TrialStatus.PendingProvisioning) throw new DomainException("This trial is already provisioned or has ended.");
        ExternalAccountRef = Guard.Optional(externalRef, 150, "Account reference");
        var url = Guard.Optional(loginUrl, 500, "Login address");
        if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)) throw new DomainException("The login address must be an https:// address.");
        LoginUrl = url; ProvisioningNote = Guard.Optional(note, 500, "Note") ?? ProvisioningNote;
        Status = TrialStatus.Active; StartedAtUtc = nowUtc; ExpiresAtUtc = nowUtc.AddDays(DurationDays); UpdatedAtUtc = nowUtc;
    }

    public void Extend(int days, DateTime nowUtc)
    {
        if (Status is not (TrialStatus.Active or TrialStatus.Expired)) throw new DomainException("Only an active or expired trial can be extended.");
        if (days is < 1 or > 90) throw new DomainException("Extend by 1 to 90 days.");
        var from = Status == TrialStatus.Expired || ExpiresAtUtc is null || ExpiresAtUtc < nowUtc ? nowUtc : ExpiresAtUtc.Value;
        ExpiresAtUtc = from.AddDays(days); DurationDays += days; Status = TrialStatus.Active; ExpiryNoticeAtUtc = null; EndedAtUtc = null; EndReason = null; UpdatedAtUtc = nowUtc;
    }

    public void Expire(DateTime nowUtc)
    {
        if (Status != TrialStatus.Active) throw new DomainException("Only an active trial can expire.");
        Status = TrialStatus.Expired; EndedAtUtc = nowUtc; EndReason = "The trial period ended."; UpdatedAtUtc = nowUtc;
    }

    public void Cancel(string? reason, DateTime nowUtc)
    {
        if (Status is TrialStatus.Cancelled or TrialStatus.Converted) throw new DomainException($"A trial that is {Status} cannot be cancelled.");
        Status = TrialStatus.Cancelled; EndedAtUtc = nowUtc; EndReason = Guard.Optional(reason, 300, "Reason") ?? "Cancelled."; UpdatedAtUtc = nowUtc;
    }

    public void MarkConverted(Guid customerId, DateTime nowUtc)
    {
        if (Status is not (TrialStatus.Active or TrialStatus.Expired)) throw new DomainException($"A trial that is {Status} cannot be converted. Only a trial that was provisioned and not cancelled can.");
        Status = TrialStatus.Converted; ConvertedCustomerId = customerId; EndedAtUtc = nowUtc; EndReason = "Converted to a customer."; UpdatedAtUtc = nowUtc;
    }

    public void RecordUsage(DateTime? lastActivityUtc, int loginCount, int activeUsers, string? notes, DateTime nowUtc)
    {
        if (Status is TrialStatus.PendingProvisioning) throw new DomainException("Usage can only be recorded once the trial account exists.");
        if (loginCount < 0 || loginCount > 10_000_000 || activeUsers < 0 || activeUsers > 1_000_000) throw new DomainException("Usage numbers must be zero or positive.");
        if (lastActivityUtc is { } l && l > nowUtc.AddMinutes(5)) throw new DomainException("The last activity cannot be in the future.");
        LastActivityAtUtc = lastActivityUtc; LoginCount = loginCount; ActiveUsers = activeUsers; UsageNotes = Guard.Optional(notes, 500, "Usage notes"); UpdatedAtUtc = nowUtc;
    }

    public void MarkExpiryNotice(DateTime nowUtc) { ExpiryNoticeAtUtc = nowUtc; }
}

public class TrialEvent : Entity, ITenantOwned, IAppendOnly
{
    private TrialEvent() { }
    public Guid TenantId { get; private set; }
    public Guid TrialId { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string Summary { get; private set; } = string.Empty;
    public Guid? ActorUserId { get; private set; }
    public string ActorKind { get; private set; } = "person";
    public DateTime OccurredAtUtc { get; private set; }

    public static TrialEvent Create(Guid tenantId, Guid trialId, string type, string summary, Guid? actorUserId, string actorKind, DateTime nowUtc) =>
        new() { TenantId = tenantId, TrialId = trialId, Type = type, Summary = summary.Length <= 500 ? summary : summary[..500], ActorUserId = actorUserId, ActorKind = actorKind, OccurredAtUtc = nowUtc };
}
