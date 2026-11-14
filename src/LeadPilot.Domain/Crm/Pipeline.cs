using LeadPilot.Domain.Common;

namespace LeadPilot.Domain.Crm;

public class Pipeline : AuditableEntity, ITenantOwned
{
    private Pipeline() { }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public bool IsDefault { get; private set; }
    public bool IsActive { get; private set; } = true;

    public static Pipeline Create(Guid tenantId, string name, bool isDefault, DateTime nowUtc) =>
        new() { TenantId = tenantId, Name = Guard.Required(name, 100, "Pipeline name"), IsDefault = isDefault, CreatedAtUtc = nowUtc };

    public void Rename(string name, DateTime nowUtc) { Name = Guard.Required(name, 100, "Pipeline name"); UpdatedAtUtc = nowUtc; }
    public void SetDefault(bool value, DateTime nowUtc) { IsDefault = value; UpdatedAtUtc = nowUtc; }
    public void SetActive(bool active, DateTime nowUtc)
    {
        if (IsDefault && !active) throw new DomainException("The default pipeline cannot be deactivated. Make another pipeline the default first.");
        IsActive = active; UpdatedAtUtc = nowUtc;
    }
}

public class PipelineStage : AuditableEntity, ITenantOwned
{
    private PipelineStage() { }
    public Guid TenantId { get; private set; }
    public Guid PipelineId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int Position { get; private set; }
    public StageKind Kind { get; private set; } = StageKind.Open;

    public static PipelineStage Create(Guid tenantId, Guid pipelineId, string name, int position, StageKind kind, DateTime nowUtc) =>
        new() { TenantId = tenantId, PipelineId = pipelineId, Name = Guard.Required(name, 100, "Stage name"), Position = position, Kind = kind, CreatedAtUtc = nowUtc };

    public void Update(string name, StageKind kind, DateTime nowUtc) { Name = Guard.Required(name, 100, "Stage name"); Kind = kind; UpdatedAtUtc = nowUtc; }
    public void MoveTo(int position, DateTime nowUtc) { Position = position; UpdatedAtUtc = nowUtc; }
}

/// <summary>Default stages seeded for every tenant. Tenants may rename, reorder, add and remove stages.</summary>
public static class DefaultPipeline
{
    public const string Name = "Sales pipeline";

    public static readonly IReadOnlyList<(string Name, StageKind Kind)> Stages =
    [
        ("New", StageKind.Open), ("Researched", StageKind.Open), ("Contact Eligible", StageKind.Open), ("Contacted", StageKind.Open),
        ("Responded", StageKind.Open), ("Interested", StageKind.Open), ("Qualified", StageKind.Open), ("Demo Scheduled", StageKind.Open),
        ("Demo Completed", StageKind.Open), ("Trial", StageKind.Open), ("Negotiation", StageKind.Open), ("Won", StageKind.Won),
        ("Lost", StageKind.Lost), ("Suppressed", StageKind.Suppressed),
    ];

    public static readonly IReadOnlyList<(string Name, bool Closed, bool Default)> Statuses =
    [
        ("Open", false, true), ("Working", false, false), ("Nurturing", false, false), ("Disqualified", true, false),
    ];
}

public class Opportunity : AuditableEntity, ITenantOwned
{
    private Opportunity() { }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }
    public Guid PipelineStageId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public decimal Value { get; private set; }
    public string Currency { get; private set; } = "USD";
    public DateTime? ExpectedCloseDateUtc { get; private set; }
    public Guid? OwnerUserId { get; private set; }
    public OpportunityStatus Status { get; private set; } = OpportunityStatus.Open;

    public static Opportunity Create(Guid tenantId, Guid leadId, Guid stageId, string name, decimal value, string currency, DateTime? expectedClose, Guid? owner, DateTime nowUtc)
    {
        var o = new Opportunity { TenantId = tenantId, LeadId = leadId, PipelineStageId = stageId, OwnerUserId = owner, CreatedAtUtc = nowUtc };
        o.Set(name, value, currency, expectedClose);
        return o;
    }

    public void Update(string name, decimal value, string currency, DateTime? expectedClose, Guid? owner, DateTime nowUtc) { Set(name, value, currency, expectedClose); OwnerUserId = owner; UpdatedAtUtc = nowUtc; }

    public void MoveToStage(Guid stageId, StageKind kind, DateTime nowUtc)
    {
        PipelineStageId = stageId;
        Status = kind switch { StageKind.Won => OpportunityStatus.Won, StageKind.Lost => OpportunityStatus.Lost, _ => OpportunityStatus.Open };
        UpdatedAtUtc = nowUtc;
    }

    private void Set(string name, decimal value, string currency, DateTime? expectedClose)
    {
        Name = Guard.Required(name, 200, "Opportunity name");
        if (value < 0 || value > 999_999_999_999m) throw new DomainException("Value must be zero or positive.");
        Value = decimal.Round(value, 2);
        var cur = (currency ?? string.Empty).Trim().ToUpperInvariant();
        if (cur.Length != 3 || !cur.All(char.IsAsciiLetterUpper)) throw new DomainException("Currency must be a 3-letter code such as USD.");
        Currency = cur;
        ExpectedCloseDateUtc = expectedClose?.Date;
    }
}

public class OpportunityActivity : Entity, ITenantOwned, IAppendOnly
{
    private OpportunityActivity() { }
    public Guid TenantId { get; private set; }
    public Guid OpportunityId { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string Summary { get; private set; } = string.Empty;
    public Guid? ActorUserId { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    public static OpportunityActivity Create(Guid tenantId, Guid opportunityId, string type, string summary, Guid? actor, DateTime nowUtc) =>
        new() { TenantId = tenantId, OpportunityId = opportunityId, Type = type, Summary = summary.Length <= 500 ? summary : summary[..500], ActorUserId = actor, OccurredAtUtc = nowUtc };
}
