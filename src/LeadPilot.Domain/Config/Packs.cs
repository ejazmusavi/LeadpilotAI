using LeadPilot.Domain.Common;

namespace LeadPilot.Domain.Config;

/// <summary>Builtin = shipped in the repository (read-only). Platform = published by a platform administrator for all tenants. Tenant = private to its owner.</summary>
public enum PackScope { Builtin = 1, Platform = 2, Tenant = 3 }

public enum PackApplyMode { Initial = 1, Upgrade = 2, Reapply = 3, Migrated = 4 }

/// <summary>
/// A stored industry template pack: declarative JSON data (never code). NOT tenant-owned: builtin and platform packs are readable by every tenant,
/// private packs only by <see cref="OwnerTenantId"/>; every query goes through <c>PackCatalog</c>, which applies that rule. (Same pattern as plans.)
/// </summary>
public class PackDefinition : AuditableEntity
{
    public const int MaxContentBytes = 256 * 1024;

    private PackDefinition() { }
    public string Key { get; private set; } = string.Empty;
    public int VersionNumber { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string? Industry { get; private set; }
    public PackScope Scope { get; private set; }
    /// <summary>Only for <see cref="PackScope.Tenant"/> packs.</summary>
    public Guid? OwnerTenantId { get; private set; }
    /// <summary>"builtin", "platform" or the owner tenant id: part of the unique key (NULLs would not be unique in MySQL).</summary>
    public string ScopeKey { get; private set; } = string.Empty;
    /// <summary>Platform packs are visible to tenants only while published.</summary>
    public bool IsPublished { get; private set; }
    public bool IsSample { get; private set; }
    public string ContentJson { get; private set; } = "{}";
    public string ContentHash { get; private set; } = string.Empty;

    public static PackDefinition Create(PackScope scope, Guid? ownerTenantId, string key, int version, string name, string? description, string? industry, bool sample, string contentJson, string contentHash, bool published, DateTime nowUtc, string? actor)
    {
        if ((scope == PackScope.Tenant) != (ownerTenantId is not null)) throw new DomainException("Only private packs have an owner tenant.");
        if (contentJson.Length > MaxContentBytes) throw new DomainException("The pack is too large.");
        return new PackDefinition
        {
            Scope = scope, OwnerTenantId = ownerTenantId, Key = key, VersionNumber = version, Name = name, Description = description, Industry = industry, IsSample = sample,
            ScopeKey = scope switch { PackScope.Builtin => "builtin", PackScope.Platform => "platform", _ => ownerTenantId!.Value.ToString("N") },
            ContentJson = contentJson, ContentHash = contentHash, IsPublished = published, CreatedAtUtc = nowUtc, CreatedBy = actor
        };
    }

    public void SetPublished(bool published, DateTime nowUtc, string? actor)
    {
        if (Scope == PackScope.Tenant) throw new DomainException("Private packs are never published to other tenants.");
        IsPublished = published; UpdatedAtUtc = nowUtc; UpdatedBy = actor;
    }
}

/// <summary>Append-only history of what a tenant applied: which pack, which version, in which mode, with which result.</summary>
public class TenantPackApplication : Entity, ITenantOwned, IAppendOnly
{
    private TenantPackApplication() { }
    public Guid TenantId { get; private set; }
    public string PackKey { get; private set; } = string.Empty;
    public int PackVersion { get; private set; }
    public string PackName { get; private set; } = string.Empty;
    public string ScopeKey { get; private set; } = string.Empty;
    public string ContentHash { get; private set; } = string.Empty;
    public PackApplyMode Mode { get; private set; }
    /// <summary>"KeepMine" or "UseThePack" (the policy for items the tenant edited).</summary>
    public string ConflictPolicy { get; private set; } = string.Empty;
    public string SummaryJson { get; private set; } = "{}";
    public Guid? AppliedByUserId { get; private set; }
    public string? AppliedBy { get; private set; }
    public DateTime AppliedAtUtc { get; private set; }

    public static TenantPackApplication Create(Guid tenantId, string key, int version, string name, string scopeKey, string hash, PackApplyMode mode, string policy, string summaryJson, Guid? userId, string? by, DateTime nowUtc) =>
        new() { TenantId = tenantId, PackKey = key, PackVersion = version, PackName = name, ScopeKey = scopeKey, ContentHash = hash, Mode = mode, ConflictPolicy = policy, SummaryJson = summaryJson, AppliedByUserId = userId, AppliedBy = by, AppliedAtUtc = nowUtc };
}

/// <summary>
/// Remembers which tenant entity a pack item created or adopted and the fingerprint of the content as the pack left it (<see cref="AppliedHash"/>).
/// A later import compares the entity's current fingerprint with it: different = the tenant edited it, so it is never overwritten silently.
/// </summary>
public class PackItemLink : Entity, ITenantOwned
{
    private PackItemLink() { }
    public Guid TenantId { get; private set; }
    public string PackKey { get; private set; } = string.Empty;
    public string ItemType { get; private set; } = string.Empty;
    public string ItemKey { get; private set; } = string.Empty;
    public Guid EntityId { get; private set; }
    public string AppliedHash { get; private set; } = string.Empty;
    public int PackVersion { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    public static PackItemLink Create(Guid tenantId, string packKey, string type, string key, Guid entityId, string hash, int version, DateTime nowUtc) =>
        new() { TenantId = tenantId, PackKey = packKey, ItemType = type, ItemKey = key, EntityId = entityId, AppliedHash = hash, PackVersion = version, UpdatedAtUtc = nowUtc };

    public void Refresh(Guid entityId, string hash, int version, DateTime nowUtc) { EntityId = entityId; AppliedHash = hash; PackVersion = version; UpdatedAtUtc = nowUtc; }
}
