using System.Text.RegularExpressions;
using LeadPilot.Domain.Common;

namespace LeadPilot.Domain.Tenancy;

public class TenantSetting : AuditableEntity, ITenantOwned
{
    private static readonly Regex KeyPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._:\-]{0,99}$", RegexOptions.Compiled);
    private TenantSetting() { }

    public Guid TenantId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    /// <summary>Plain text, or protected ciphertext when <see cref="IsSecret"/> is true.</summary>
    public string Value { get; private set; } = string.Empty;
    public bool IsSecret { get; private set; }

    public static bool IsValidKey(string? key) => key is not null && KeyPattern.IsMatch(key);

    public static TenantSetting Create(Guid tenantId, string key, string value, bool isSecret, DateTime nowUtc)
    {
        if (!IsValidKey(key)) throw new DomainException("Setting keys may contain letters, digits and . _ : - (max 100 characters).");
        return new TenantSetting { TenantId = tenantId, Key = key, Value = value, IsSecret = isSecret, CreatedAtUtc = nowUtc };
    }

    public void Set(string value, bool isSecret, DateTime nowUtc)
    {
        Value = value; IsSecret = isSecret; UpdatedAtUtc = nowUtc;
    }
}

public class Permission : Entity
{
    private Permission() { }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string Group { get; private set; } = string.Empty;
    /// <summary>Platform permissions may only be granted by roles of the platform tenant.</summary>
    public bool IsPlatform { get; private set; }
    public int SortOrder { get; private set; }

    public static Permission Create(string code, string name, string? description, string group, bool isPlatform, int sortOrder) =>
        new() { Code = code, Name = name, Description = description, Group = group, IsPlatform = isPlatform, SortOrder = sortOrder };

    public void Refresh(string name, string? description, string group, bool isPlatform, int sortOrder)
    {
        Name = name; Description = description; Group = group; IsPlatform = isPlatform; SortOrder = sortOrder;
    }
}

public class Role : AuditableEntity, ITenantOwned
{
    private Role() { }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    /// <summary>System roles are seeded per tenant, cannot be renamed, edited or deleted.</summary>
    public bool IsSystem { get; private set; }

    public static Role Create(Guid tenantId, string name, string? description, bool isSystem, DateTime nowUtc)
    {
        var role = new Role { TenantId = tenantId, IsSystem = isSystem, CreatedAtUtc = nowUtc };
        role.Rename(name, description, nowUtc, touch: false);
        return role;
    }

    public void Rename(string name, string? description, DateTime nowUtc, bool touch = true)
    {
        if (IsSystem && touch) throw new DomainException("System roles cannot be modified.");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100) throw new DomainException("Role name is required (max 100 characters).");
        if (description is { Length: > 500 }) throw new DomainException("Role description must be 500 characters or fewer.");
        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (touch) UpdatedAtUtc = nowUtc;
    }

    public void EnsureDeletable()
    {
        if (IsSystem) throw new DomainException("System roles cannot be deleted.");
    }
}

public class RolePermission : Entity, ITenantOwned
{
    private RolePermission() { }
    public Guid TenantId { get; private set; }
    public Guid RoleId { get; private set; }
    public Guid PermissionId { get; private set; }
    public static RolePermission Create(Guid tenantId, Guid roleId, Guid permissionId) => new() { TenantId = tenantId, RoleId = roleId, PermissionId = permissionId };
}

public class UserRole : Entity, ITenantOwned
{
    private UserRole() { }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }
    public DateTime AssignedAtUtc { get; private set; }
    public Guid? AssignedByUserId { get; private set; }
    public static UserRole Create(Guid tenantId, Guid userId, Guid roleId, Guid? assignedBy, DateTime nowUtc) =>
        new() { TenantId = tenantId, UserId = userId, RoleId = roleId, AssignedByUserId = assignedBy, AssignedAtUtc = nowUtc };
}

public enum MembershipStatus { Active = 1, Disabled = 2 }

/// <summary>Membership of a user in a tenant. One user may belong to several tenants.</summary>
public class UserTenant : Entity, ITenantOwned
{
    private UserTenant() { }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }
    public bool IsDefault { get; private set; }
    public MembershipStatus Status { get; private set; } = MembershipStatus.Active;
    public DateTime JoinedAtUtc { get; private set; }
    public Guid? InvitedByUserId { get; private set; }

    public static UserTenant Create(Guid tenantId, Guid userId, bool isDefault, Guid? invitedBy, DateTime nowUtc) =>
        new() { TenantId = tenantId, UserId = userId, IsDefault = isDefault, InvitedByUserId = invitedBy, JoinedAtUtc = nowUtc };

    public void SetStatus(MembershipStatus status) => Status = status;
    public void SetDefault(bool value) => IsDefault = value;
}

public enum InvitationStatus { Pending = 1, Accepted = 2, Revoked = 3 }

public class TenantInvitation : AuditableEntity, ITenantOwned
{
    private TenantInvitation() { }
    public Guid TenantId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public Guid RoleId { get; private set; }
    /// <summary>SHA-256 (hex) of the token. The raw token is only ever sent by e-mail and never stored.</summary>
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; private set; }
    public InvitationStatus Status { get; private set; } = InvitationStatus.Pending;
    public Guid? InvitedByUserId { get; private set; }
    public DateTime? AcceptedAtUtc { get; private set; }
    public Guid? AcceptedByUserId { get; private set; }

    public static string NormalizeEmail(string email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    public static TenantInvitation Create(Guid tenantId, string email, Guid roleId, string tokenHash, DateTime expiresAtUtc, Guid? invitedBy, DateTime nowUtc)
    {
        var normalized = NormalizeEmail(email);
        if (normalized.Length is 0 or > 256 || !normalized.Contains('@')) throw new DomainException("A valid e-mail address is required.");
        if (expiresAtUtc <= nowUtc) throw new DomainException("Invitation expiry must be in the future.");
        return new TenantInvitation { TenantId = tenantId, Email = normalized, RoleId = roleId, TokenHash = tokenHash, ExpiresAtUtc = expiresAtUtc, InvitedByUserId = invitedBy, CreatedAtUtc = nowUtc };
    }

    public bool IsExpired(DateTime nowUtc) => nowUtc >= ExpiresAtUtc;
    public bool CanBeAccepted(DateTime nowUtc) => Status == InvitationStatus.Pending && !IsExpired(nowUtc);

    /// <summary>Pending / Accepted / Revoked / Expired, for display.</summary>
    public string EffectiveStatus(DateTime nowUtc) => Status == InvitationStatus.Pending && IsExpired(nowUtc) ? "Expired" : Status.ToString();

    public void MarkAccepted(Guid userId, DateTime nowUtc)
    {
        if (!CanBeAccepted(nowUtc)) throw new DomainException("This invitation can no longer be accepted.");
        Status = InvitationStatus.Accepted; AcceptedAtUtc = nowUtc; AcceptedByUserId = userId; UpdatedAtUtc = nowUtc;
    }

    public void Revoke(DateTime nowUtc)
    {
        if (Status != InvitationStatus.Pending) throw new DomainException("Only pending invitations can be revoked.");
        Status = InvitationStatus.Revoked; UpdatedAtUtc = nowUtc;
    }

    public void ReplaceToken(string tokenHash, DateTime expiresAtUtc, DateTime nowUtc)
    {
        if (Status != InvitationStatus.Pending) throw new DomainException("Only pending invitations can be re-sent.");
        TokenHash = tokenHash; ExpiresAtUtc = expiresAtUtc; UpdatedAtUtc = nowUtc;
    }
}

/// <summary>Append-only record of security-relevant actions. TenantId is the tenant the action concerns.</summary>
public class AuditLog : Entity, ITenantOwned
{
    private AuditLog() { }
    public Guid TenantId { get; private set; }
    /// <summary>Tenant the actor was operating from (differs from TenantId for platform actions).</summary>
    public Guid? ActorTenantId { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string? ActorEmail { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string? EntityType { get; private set; }
    public string? EntityId { get; private set; }
    public string Summary { get; private set; } = string.Empty;
    public string? IpAddress { get; private set; }
    public string? CorrelationId { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static AuditLog Create(Guid tenantId, Guid? actorTenantId, Guid? actorUserId, string? actorEmail, string action, string? entityType, string? entityId,
        string summary, string? ip, string? correlationId, DateTime nowUtc) =>
        new()
        {
            TenantId = tenantId, ActorTenantId = actorTenantId, ActorUserId = actorUserId, ActorEmail = Trim(actorEmail, 256), Action = Trim(action, 100)!,
            EntityType = Trim(entityType, 100), EntityId = Trim(entityId, 64), Summary = Trim(summary, 1000)!, IpAddress = Trim(ip, 64), CorrelationId = Trim(correlationId, 64), CreatedAtUtc = nowUtc
        };

    private static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}

public class UserProfile : AuditableEntity
{
    private UserProfile() { }
    public Guid UserId { get; private set; }
    public string DisplayName { get; private set; } = string.Empty;
    public string? Phone { get; private set; }

    public static UserProfile Create(Guid userId, string displayName, string? phone, DateTime nowUtc)
    {
        var p = new UserProfile { UserId = userId, CreatedAtUtc = nowUtc };
        p.Update(displayName, phone, nowUtc, touch: false);
        return p;
    }

    public void Update(string displayName, string? phone, DateTime nowUtc, bool touch = true)
    {
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 200) throw new DomainException("Display name is required (max 200 characters).");
        if (phone is { Length: > 32 }) throw new DomainException("Phone must be 32 characters or fewer.");
        DisplayName = displayName.Trim();
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        if (touch) UpdatedAtUtc = nowUtc;
    }
}

/// <summary>API refresh token. Only the hash is stored; tokens rotate on every use.</summary>
public class RefreshToken : Entity
{
    private RefreshToken() { }
    public Guid UserId { get; private set; }
    public Guid TenantId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }
    public DateTime? RevokedAtUtc { get; private set; }
    public string? ReplacedByTokenHash { get; private set; }
    public string? CreatedByIp { get; private set; }

    public static RefreshToken Create(Guid userId, Guid tenantId, string tokenHash, DateTime expiresAtUtc, string? ip, DateTime nowUtc) =>
        new() { UserId = userId, TenantId = tenantId, TokenHash = tokenHash, ExpiresAtUtc = expiresAtUtc, CreatedByIp = ip, CreatedAtUtc = nowUtc };

    public bool IsActive(DateTime nowUtc) => RevokedAtUtc is null && nowUtc < ExpiresAtUtc;
    public void Revoke(DateTime nowUtc, string? replacedBy = null) { RevokedAtUtc ??= nowUtc; ReplacedByTokenHash = replacedBy ?? ReplacedByTokenHash; }
}
