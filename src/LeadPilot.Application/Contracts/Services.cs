using LeadPilot.Application.Common;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Shared.Results;

namespace LeadPilot.Application.Contracts;

/// <summary>Platform operations on tenants. Callers must hold Tenants.Manage (enforced by policy at the edge).</summary>
public interface ITenantAdminService
{
    Task<PagedResult<TenantDto>> ListAsync(string? search, TenantStatus? status, int page, int pageSize, CancellationToken ct = default);
    Task<Result<TenantDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<CreateTenantResult>> CreateAsync(CreateTenantRequest request, CancellationToken ct = default);
    Task<Result<TenantDto>> UpdateAsync(Guid id, UpdateTenantProfileRequest request, CancellationToken ct = default);
    Task<Result<TenantDto>> SuspendAsync(Guid id, string? reason, CancellationToken ct = default);
    /// <summary>The ONE suspension path: manual, abuse and non-payment suspensions all end here (blocks sign-in, API, sending and jobs through the Phase 2 tenant status).</summary>
    Task<Result<TenantDto>> SuspendAsync(Guid id, string? reason, SuspensionKind kind, CancellationToken ct = default);
    Task<Result<TenantDto>> ActivateAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Settings and profile of the CURRENT tenant only.</summary>
public interface ITenantSettingsService
{
    Task<Result<TenantDto>> GetProfileAsync(CancellationToken ct = default);
    Task<Result<TenantDto>> UpdateProfileAsync(UpdateTenantProfileRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<TenantSettingDto>> ListAsync(CancellationToken ct = default);
    Task<Result<TenantSettingDto>> SetAsync(string key, SetSettingRequest request, CancellationToken ct = default);
    Task<Result> DeleteAsync(string key, CancellationToken ct = default);
    /// <summary>Plain value (decrypted when secret) for server-side use by other modules. Never expose to clients.</summary>
    Task<string?> GetValueAsync(string key, CancellationToken ct = default);
}

public interface IRoleService
{
    Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken ct = default);
    Task<Result<RoleDetailDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<RoleDetailDto>> CreateAsync(SaveRoleRequest request, CancellationToken ct = default);
    Task<Result<RoleDetailDto>> UpdateAsync(Guid id, SaveRoleRequest request, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid id, CancellationToken ct = default);
    /// <summary>Permissions that can be placed on roles of the current tenant.</summary>
    Task<IReadOnlyList<PermissionDto>> ListAssignablePermissionsAsync(CancellationToken ct = default);
}

public interface IUserManagementService
{
    Task<PagedResult<MemberDto>> ListAsync(string? search, int page, int pageSize, CancellationToken ct = default);
    Task<Result<MemberDto>> GetAsync(Guid userId, CancellationToken ct = default);
    Task<Result<MemberDto>> SetRolesAsync(Guid userId, SetUserRolesRequest request, CancellationToken ct = default);
    Task<Result> SetActiveAsync(Guid userId, bool active, CancellationToken ct = default);
    Task<Result> RemoveAsync(Guid userId, CancellationToken ct = default);
}

public interface IInvitationService
{
    Task<PagedResult<InvitationDto>> ListAsync(int page, int pageSize, CancellationToken ct = default);
    Task<Result<InvitationDto>> CreateAsync(CreateInvitationRequest request, CancellationToken ct = default);
    Task<Result> RevokeAsync(Guid id, CancellationToken ct = default);
    Task<Result<InvitationDto>> ResendAsync(Guid id, CancellationToken ct = default);
    /// <summary>Anonymous: describes a valid invitation, or null for any invalid/expired/used token (no distinction, no enumeration).</summary>
    Task<InvitationPreview?> PreviewAsync(string token, CancellationToken ct = default);
    /// <summary>Anonymous or authenticated. A new account is created when none exists for the invited e-mail; an existing account must be the signed-in user.</summary>
    Task<Result<AcceptInvitationResult>> AcceptAsync(AcceptInvitationRequest request, Guid? signedInUserId, CancellationToken ct = default);
}

public interface IAuditService
{
    /// <summary>Adds an entry to the current unit of work; it is persisted by the caller's SaveChanges (atomic with the change).</summary>
    void Record(AuditEntry entry);
    /// <summary>Records and saves immediately (for events with no other change, e.g. sign-in, tenant switch).</summary>
    Task RecordAndSaveAsync(AuditEntry entry, CancellationToken ct = default);
    Task<PagedResult<AuditLogDto>> QueryAsync(AuditQuery query, CancellationToken ct = default);
}

public interface ITenantSwitchService
{
    Task<IReadOnlyList<AccessibleTenantDto>> ListAccessibleAsync(Guid userId, Guid? currentTenantId, CancellationToken ct = default);
    /// <summary>Authorizes (membership, or platform switch permission for active tenants), audits, and returns the target. Callers then re-issue the cookie/JWT.</summary>
    Task<Result<SwitchTenantResult>> SwitchAsync(Guid userId, Guid targetTenantId, Guid? currentTenantId, CancellationToken ct = default);
}

/// <summary>Validates, on every request, that a user may act inside a tenant.</summary>
public interface IMembershipResolver
{
    Task<TenantAccessResult> ResolveAsync(Guid userId, Guid tenantId, CancellationToken ct = default);
    Task<Guid?> GetDefaultTenantIdAsync(Guid userId, CancellationToken ct = default);
    Task<bool> HasPlatformSwitchAsync(Guid userId, CancellationToken ct = default);
}

public interface IPermissionEvaluator
{
    /// <summary>Effective permissions of the signed-in user in the current tenant (cached for the request).</summary>
    Task<LeadPilot.Application.Security.PermissionSet> GetCurrentAsync(CancellationToken ct = default);
    Task<bool> HasAsync(string permissionCode, CancellationToken ct = default);
    Task<LeadPilot.Application.Security.PermissionSet> GetAsync(Guid userId, Guid tenantId, CancellationToken ct = default);
}

public interface IAuthTokenService
{
    Task<Result<AuthTokens>> LoginAsync(LoginRequest request, CancellationToken ct = default);
    Task<Result<AuthTokens>> RefreshAsync(string refreshToken, CancellationToken ct = default);
    Task<Result> RevokeAsync(string refreshToken, CancellationToken ct = default);
    Task<Result<AuthTokens>> IssueForTenantAsync(Guid userId, Guid tenantId, CancellationToken ct = default);
}

public interface IProfileService
{
    Task<Result<ProfileDto>> GetAsync(CancellationToken ct = default);
    Task<Result<ProfileDto>> UpdateAsync(UpdateProfileRequest request, CancellationToken ct = default);
}
