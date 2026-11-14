using LeadPilot.Domain.Common;

namespace LeadPilot.Domain.Crm;

/// <summary>Timeline entry of a lead. Append-only.</summary>
public class LeadActivity : Entity, ITenantOwned, IAppendOnly
{
    private LeadActivity() { }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }
    public LeadActivityType Type { get; private set; }
    public string Summary { get; private set; } = string.Empty;
    /// <summary>Optional JSON detail (e.g. from/to stage ids).</summary>
    public string? Payload { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    public static LeadActivity Create(Guid tenantId, Guid leadId, LeadActivityType type, string summary, string? payload, Guid? actor, DateTime nowUtc) =>
        new() { TenantId = tenantId, LeadId = leadId, Type = type, Summary = summary.Length <= 500 ? summary : summary[..500], Payload = payload, ActorUserId = actor, OccurredAtUtc = nowUtc };
}

public class LeadNote : Entity, ITenantOwned
{
    private LeadNote() { }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }
    public string Body { get; private set; } = string.Empty;
    public Guid? AuthorUserId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }

    public static LeadNote Create(Guid tenantId, Guid leadId, string body, Guid? author, DateTime nowUtc)
    {
        var b = body?.Trim();
        if (string.IsNullOrEmpty(b)) throw new DomainException("A note cannot be empty.");
        if (b.Length > 4000) throw new DomainException("A note must be 4000 characters or fewer.");
        return new LeadNote { TenantId = tenantId, LeadId = leadId, Body = b, AuthorUserId = author, CreatedAtUtc = nowUtc };
    }

    public void Delete(DateTime nowUtc) { IsDeleted = true; DeletedAtUtc = nowUtc; }
}

public class LeadTag : AuditableEntity, ITenantOwned
{
    private LeadTag() { }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string NormalizedName { get; private set; } = string.Empty;

    public static LeadTag Create(Guid tenantId, string name, DateTime nowUtc)
    {
        var t = new LeadTag { TenantId = tenantId, CreatedAtUtc = nowUtc };
        t.Rename(name, nowUtc, touch: false);
        return t;
    }

    public void Rename(string name, DateTime nowUtc, bool touch = true)
    {
        var n = Guard.Required(name, 50, "Tag name");
        Name = n; NormalizedName = n.ToLowerInvariant(); if (touch) UpdatedAtUtc = nowUtc;
    }
}

public class LeadTagMap : Entity, ITenantOwned
{
    private LeadTagMap() { }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }
    public Guid TagId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public static LeadTagMap Create(Guid tenantId, Guid leadId, Guid tagId, DateTime nowUtc) => new() { TenantId = tenantId, LeadId = leadId, TagId = tagId, CreatedAtUtc = nowUtc };
}

/// <summary>One scoring run for a lead. Append-only history; the lead carries only the latest score.</summary>
public class LeadScore : Entity, ITenantOwned, IAppendOnly
{
    private LeadScore() { }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }
    public int Score { get; private set; }
    public int RuleVersion { get; private set; }
    /// <summary>JSON: every rule that matched, with its points, plus the raw sum.</summary>
    public string Breakdown { get; private set; } = "{}";
    public DateTime CalculatedAtUtc { get; private set; }

    public static LeadScore Create(Guid tenantId, Guid leadId, int score, int ruleVersion, string breakdown, DateTime nowUtc) =>
        new() { TenantId = tenantId, LeadId = leadId, Score = score, RuleVersion = ruleVersion, Breakdown = breakdown, CalculatedAtUtc = nowUtc };
}
