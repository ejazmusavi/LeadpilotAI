using LeadPilot.Domain.Common;

namespace LeadPilot.Domain.Crm;

/// <summary>
/// One immutable version of a tenant's scoring rules. Editing rules publishes a new version; exactly one version per tenant is active.
/// Every stored lead score names the version that produced it.
/// </summary>
public class ScoringRuleSet : AuditableEntity, ITenantOwned
{
    private ScoringRuleSet() { }
    public Guid TenantId { get; private set; }
    public int Version { get; private set; }
    public bool IsActive { get; private set; }
    public string? Note { get; private set; }

    public static ScoringRuleSet Create(Guid tenantId, int version, string? note, DateTime nowUtc, string? actor) =>
        new() { TenantId = tenantId, Version = version, IsActive = true, Note = note is { Length: > 300 } ? note[..300] : note, CreatedAtUtc = nowUtc, CreatedBy = actor };

    public void Deactivate() => IsActive = false;
}

public class ScoringRule : Entity, ITenantOwned
{
    private ScoringRule() { }
    public Guid TenantId { get; private set; }
    public Guid RuleSetId { get; private set; }
    public ScoreDimension Dimension { get; private set; }
    /// <summary>What the rule looks at; syntax depends on the dimension (validated by <c>ScoringRuleParser</c>).</summary>
    public string Target { get; private set; } = string.Empty;
    public int Points { get; private set; }
    public string? Description { get; private set; }
    public bool IsEnabled { get; private set; } = true;

    public static ScoringRule Create(Guid tenantId, Guid ruleSetId, ScoreDimension dimension, string target, int points, string? description, bool enabled)
    {
        if (points is < -100 or > 100) throw new DomainException("Rule points must be between -100 and 100.");
        if (string.IsNullOrWhiteSpace(target) || target.Length > 200) throw new DomainException("Rule target is required (max 200 characters).");
        return new ScoringRule { TenantId = tenantId, RuleSetId = ruleSetId, Dimension = dimension, Target = target.Trim(), Points = points, Description = description is { Length: > 200 } ? description[..200] : description, IsEnabled = enabled };
    }
}
