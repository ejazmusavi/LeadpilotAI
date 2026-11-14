using LeadPilot.Application.Abstractions;
using LeadPilot.Domain.Tenancy;

namespace LeadPilot.Application.Contracts;

public sealed record TenantDto(Guid Id, string Name, string Slug, string Status, bool IsPlatform, string? Industry, string Timezone, string Locale,
    DateTime CreatedAtUtc, DateTime? SuspendedAtUtc, string? SuspensionReason, int MemberCount);

public sealed record CreateTenantRequest(string Name, string? Slug, string? Industry, string? Timezone, string? Locale, string? AdminEmail);
public sealed record CreateTenantResult(TenantDto Tenant, bool AdminInvited);
public sealed record UpdateTenantProfileRequest(string Name, string? Industry, string? Timezone, string? Locale) : ITenantScopedRequest
{
    public Guid? TenantId { get; init; }
}
public sealed record SuspendTenantRequest(string? Reason);

public sealed record TenantSettingDto(string Key, string Value, bool IsSecret, DateTime? UpdatedAtUtc);
public sealed record SetSettingRequest(string Value, bool IsSecret) : ITenantScopedRequest
{
    public Guid? TenantId { get; init; }
}

public sealed record PermissionDto(string Code, string Name, string Group, string? Description, bool IsPlatform);
public sealed record RoleDto(Guid Id, string Name, string? Description, bool IsSystem, int PermissionCount, int UserCount);
public sealed record RoleDetailDto(Guid Id, string Name, string? Description, bool IsSystem, IReadOnlyList<string> Permissions, int UserCount);
public sealed record SaveRoleRequest(string Name, string? Description, IReadOnlyList<string> Permissions) : ITenantScopedRequest
{
    public Guid? TenantId { get; init; }
}

public sealed record RoleRef(Guid Id, string Name);
public sealed record MemberDto(Guid UserId, string Email, string DisplayName, bool IsActive, IReadOnlyList<RoleRef> Roles, DateTime JoinedAtUtc, DateTime? LastLoginAtUtc);
public sealed record SetUserRolesRequest(IReadOnlyList<Guid> RoleIds) : ITenantScopedRequest
{
    public Guid? TenantId { get; init; }
}

public sealed record InvitationDto(Guid Id, string Email, Guid RoleId, string RoleName, string Status, DateTime ExpiresAtUtc, DateTime CreatedAtUtc);
public sealed record CreateInvitationRequest(string Email, Guid RoleId) : ITenantScopedRequest
{
    public Guid? TenantId { get; init; }
}
public sealed record InvitationPreview(string TenantName, string Email, string RoleName, DateTime ExpiresAtUtc, bool AccountExists);
public sealed record AcceptInvitationRequest(string Token, string? DisplayName, string? Password);
public sealed record AcceptInvitationResult(Guid UserId, Guid TenantId, string Email);

public sealed record AuditLogDto(Guid Id, DateTime CreatedAtUtc, string Action, string? EntityType, string? EntityId, string Summary, string? ActorEmail, Guid? ActorTenantId, string? IpAddress, string? CorrelationId);
public sealed record AuditQuery(string? Action, DateTime? FromUtc, DateTime? ToUtc, int Page = 1, int PageSize = 25);
public sealed record AuditEntry(string Action, string? EntityType, string? EntityId, string Summary, Guid? TargetTenantId = null, Guid? ActorUserId = null, string? ActorEmail = null);

public sealed record AccessibleTenantDto(Guid TenantId, string Name, string Slug, string Status, bool ViaPlatformAccess, bool IsCurrent);
public sealed record SwitchTenantResult(Guid TenantId, string TenantName, bool ViaPlatformAccess);

public sealed record LoginRequest(string Email, string Password, Guid? TenantId = null);
public sealed record RefreshRequest(string RefreshToken);
public sealed record SwitchTenantRequest(Guid TenantId);
public sealed record AuthTokens(string AccessToken, int ExpiresInSeconds, string RefreshToken, Guid TenantId, string TokenType = "Bearer");

public enum TenantAccessOutcome { Granted, UserInactive, TenantNotFound, NotAMember, TenantSuspended }
public sealed record TenantAccessResult(TenantAccessOutcome Outcome, Guid TenantId, string? TenantName, bool ViaPlatformAccess)
{
    public bool IsGranted => Outcome == TenantAccessOutcome.Granted;
}

public sealed record ProfileDto(string Email, string DisplayName, string? Phone);
public sealed record UpdateProfileRequest(string DisplayName, string? Phone);

public static class TenantMapping
{
    public static string StatusName(TenantStatus s) => s.ToString();
}
