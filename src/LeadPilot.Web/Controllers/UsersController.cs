using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Web.Controllers;

[Authorize(Policy = PermissionCodes.UsersManage)]
public class UsersController(IUserManagementService users, IRoleService roles, ICurrentUser me) : AppController
{
    public async Task<IActionResult> Index(string? q, int page = 1, CancellationToken ct = default)
    {
        Crumbs(("Users", null));
        return View(new UserListViewModel { Query = q, CurrentUserId = me.UserId, Result = await users.ListAsync(q, page, 20, ct) });
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var member = await users.GetAsync(id, ct);
        if (Reject(member) is { } r) return r;
        Crumbs(("Users", Url.Action(nameof(Index))), (member.Value.DisplayName, null));
        return View(new UserEditViewModel { Member = member.Value, Roles = await roles.ListAsync(ct), SelectedRoleIds = member.Value.Roles.Select(x => x.Id).ToList() });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(Guid id, List<Guid> selectedRoleIds, CancellationToken ct)
    {
        var result = await users.SetRolesAsync(id, new SetUserRolesRequest(selectedRoleIds ?? []), ct);
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) { Success("Roles updated."); return RedirectToAction(nameof(Index)); }
        Failure(result.Error.Message);
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> SetActive(Guid id, bool active, CancellationToken ct)
    {
        var result = await users.SetActiveAsync(id, active, ct);
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success(active ? "User re-enabled." : "User disabled."); else Failure(result.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var result = await users.RemoveAsync(id, ct);
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success("User removed from this workspace."); else Failure(result.Error.Message);
        return RedirectToAction(nameof(Index));
    }
}
