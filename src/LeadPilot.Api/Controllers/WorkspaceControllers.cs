using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Api.Controllers;

[Route("api/v1/roles"), Authorize(Policy = PermissionCodes.UsersManage)]
public class RolesController(IRoleService roles) : ApiControllerBase
{
    [HttpGet] public async Task<IReadOnlyList<RoleDto>> List(CancellationToken ct) => await roles.ListAsync(ct);

    [HttpGet("{id:guid}")] public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await roles.GetAsync(id, ct));

    [HttpPost]
    public async Task<IActionResult> Create(SaveRoleRequest request, CancellationToken ct)
    {
        var r = await roles.CreateAsync(request, ct);
        return r.IsSuccess ? CreatedAtAction(nameof(Get), new { id = r.Value.Id }, r.Value) : Fail(r);
    }

    [HttpPut("{id:guid}")] public async Task<IActionResult> Update(Guid id, SaveRoleRequest request, CancellationToken ct) => Respond(await roles.UpdateAsync(id, request, ct));

    [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => Respond(await roles.DeleteAsync(id, ct));
}

[Route("api/v1/permissions"), Authorize(Policy = PermissionCodes.UsersManage)]
public class PermissionsController(IRoleService roles) : ApiControllerBase
{
    /// <summary>Permissions that can be placed on roles of the current tenant.</summary>
    [HttpGet] public async Task<IReadOnlyList<PermissionDto>> List(CancellationToken ct) => await roles.ListAssignablePermissionsAsync(ct);
}

public sealed record SetStatusRequest(bool Active);

[Route("api/v1/users"), Authorize(Policy = PermissionCodes.UsersManage)]
public class UsersController(IUserManagementService users) : ApiControllerBase
{
    [HttpGet] public async Task<PagedResult<MemberDto>> List(string? search, int page = 1, int pageSize = 25, CancellationToken ct = default) => await users.ListAsync(search, page, pageSize, ct);

    [HttpGet("{id:guid}")] public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await users.GetAsync(id, ct));

    [HttpPut("{id:guid}/roles")] public async Task<IActionResult> SetRoles(Guid id, SetUserRolesRequest request, CancellationToken ct) => Respond(await users.SetRolesAsync(id, request, ct));

    [HttpPut("{id:guid}/status")] public async Task<IActionResult> SetStatus(Guid id, SetStatusRequest request, CancellationToken ct) => Respond(await users.SetActiveAsync(id, request.Active, ct));

    [HttpDelete("{id:guid}")] public async Task<IActionResult> Remove(Guid id, CancellationToken ct) => Respond(await users.RemoveAsync(id, ct));
}

[Route("api/v1/invitations")]
public class InvitationsController(IInvitationService invitations, LeadPilot.Application.Abstractions.ICurrentUser me) : ApiControllerBase
{
    [HttpGet, Authorize(Policy = PermissionCodes.UsersManage)]
    public async Task<PagedResult<InvitationDto>> List(int page = 1, int pageSize = 25, CancellationToken ct = default) => await invitations.ListAsync(page, pageSize, ct);

    [HttpPost, Authorize(Policy = PermissionCodes.UsersManage)]
    public async Task<IActionResult> Create(CreateInvitationRequest request, CancellationToken ct)
    {
        var r = await invitations.CreateAsync(request, ct);
        return r.IsSuccess ? StatusCode(StatusCodes.Status201Created, r.Value) : Fail(r);
    }

    [HttpDelete("{id:guid}"), Authorize(Policy = PermissionCodes.UsersManage)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) => Respond(await invitations.RevokeAsync(id, ct));

    [HttpPost("{id:guid}/resend"), Authorize(Policy = PermissionCodes.UsersManage)]
    public async Task<IActionResult> Resend(Guid id, CancellationToken ct) => Respond(await invitations.ResendAsync(id, ct));

    /// <summary>Accepts an invitation. Anonymous for new accounts (display name + password); an existing account must send its own bearer token.</summary>
    [HttpPost("accept"), AllowAnonymous, Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("auth")]
    public async Task<IActionResult> Accept(AcceptInvitationRequest request, CancellationToken ct) => Respond(await invitations.AcceptAsync(request, me.UserId, ct));
}

[Route("api/v1/settings"), Authorize(Policy = PermissionCodes.SettingsManage)]
public class SettingsController(ITenantSettingsService settings) : ApiControllerBase
{
    [HttpGet("profile")] public async Task<IActionResult> Profile(CancellationToken ct) => Respond(await settings.GetProfileAsync(ct));

    [HttpPut("profile")] public async Task<IActionResult> UpdateProfile(UpdateTenantProfileRequest request, CancellationToken ct) => Respond(await settings.UpdateProfileAsync(request, ct));

    [HttpGet] public async Task<IReadOnlyList<TenantSettingDto>> List(CancellationToken ct) => await settings.ListAsync(ct);

    [HttpPut("{key}")] public async Task<IActionResult> Set(string key, SetSettingRequest request, CancellationToken ct) => Respond(await settings.SetAsync(key, request, ct));

    [HttpDelete("{key}")] public async Task<IActionResult> Delete(string key, CancellationToken ct) => Respond(await settings.DeleteAsync(key, ct));
}

[Route("api/v1/audit-logs"), Authorize(Policy = PermissionCodes.AuditView)]
public class AuditLogsController(IAuditService audit) : ApiControllerBase
{
    [HttpGet]
    public async Task<PagedResult<AuditLogDto>> List(string? action, DateTime? from, DateTime? to, int page = 1, int pageSize = 25, CancellationToken ct = default) =>
        await audit.QueryAsync(new AuditQuery(action, from, to, page, pageSize), ct);
}
