namespace LeadPilot.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();
}

/// <summary>Marks an entity that belongs to exactly one tenant (enforced from Phase 2).</summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}

public abstract class AuditableEntity : Entity
{
    public DateTime CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

public class DomainException(string message) : Exception(message);
