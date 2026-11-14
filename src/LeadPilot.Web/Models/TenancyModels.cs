using System.ComponentModel.DataAnnotations;
using LeadPilot.Application.Contracts;
using LeadPilot.Domain.Tenancy;

namespace LeadPilot.Web.Models;

public class TenantListViewModel
{
    public string? Query { get; set; }
    public string? Status { get; set; }
    public required LeadPilot.Application.Common.PagedResult<TenantDto> Result { get; set; }
}

public class TenantFormModel
{
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(63), Display(Name = "Slug (optional)")] public string? Slug { get; set; }
    [StringLength(100)] public string? Industry { get; set; }
    [StringLength(64)] public string Timezone { get; set; } = "UTC";
    [StringLength(16)] public string Locale { get; set; } = "en";
    [EmailAddress, StringLength(256), Display(Name = "Administrator e-mail (optional)")] public string? AdminEmail { get; set; }
}

public class TenantEditModel
{
    public Guid Id { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(100)] public string? Industry { get; set; }
    [StringLength(64)] public string Timezone { get; set; } = "UTC";
    [StringLength(16)] public string Locale { get; set; } = "en";
}

public class TenantDetailsViewModel
{
    public required TenantDto Tenant { get; set; }
    public required TenantEditModel Form { get; set; }
}

public class SuspendModel
{
    [StringLength(500)] public string? Reason { get; set; }
}

public class SwitchViewModel
{
    public required IReadOnlyList<AccessibleTenantDto> Tenants { get; set; }
}

public class UserListViewModel
{
    public string? Query { get; set; }
    public required LeadPilot.Application.Common.PagedResult<MemberDto> Result { get; set; }
    public Guid? CurrentUserId { get; set; }
}

public class UserEditViewModel
{
    public required MemberDto Member { get; set; }
    public required IReadOnlyList<RoleDto> Roles { get; set; }
    public List<Guid> SelectedRoleIds { get; set; } = [];
}

public class InvitationListViewModel
{
    public required LeadPilot.Application.Common.PagedResult<InvitationDto> Result { get; set; }
}

public class InviteFormModel
{
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = string.Empty;
    [Required, Display(Name = "Role")] public Guid? RoleId { get; set; }
    public IReadOnlyList<RoleDto> Roles { get; set; } = [];
}

public class AcceptInvitationViewModel
{
    public string Token { get; set; } = string.Empty;
    public InvitationPreview? Preview { get; set; }
    public bool SignedInAsInvitee { get; set; }
    [StringLength(200), Display(Name = "Your name")] public string? DisplayName { get; set; }
    [DataType(DataType.Password)] public string? Password { get; set; }
    [DataType(DataType.Password), Display(Name = "Confirm password")] public string? ConfirmPassword { get; set; }
}

public class RoleFormModel
{
    public Guid? Id { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [StringLength(500)] public string? Description { get; set; }
    public List<string> Permissions { get; set; } = [];
    public bool IsSystem { get; set; }
    public IReadOnlyList<PermissionDto> Catalog { get; set; } = [];
}

public class SettingsViewModel
{
    public required TenantEditModel Profile { get; set; }
    public required IReadOnlyList<TenantSettingDto> Settings { get; set; }
    public SettingFormModel NewSetting { get; set; } = new();
}

public class SettingFormModel
{
    [Required, StringLength(100), Display(Name = "Key")] public string Key { get; set; } = string.Empty;
    [Required, StringLength(4000)] public string Value { get; set; } = string.Empty;
    [Display(Name = "Secret (masked after saving, encrypted at rest)")] public bool IsSecret { get; set; }
}

public class AuditViewModel
{
    public string? Query { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public required LeadPilot.Application.Common.PagedResult<AuditLogDto> Result { get; set; }
}

public class ProfileFormModel
{
    public string Email { get; set; } = string.Empty;
    [Required, StringLength(200), Display(Name = "Display name")] public string DisplayName { get; set; } = string.Empty;
    [StringLength(32)] public string? Phone { get; set; }
}
