using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Web.Controllers;

[Authorize(Policy = PermissionCodes.UsersManage)]
public class RolesController(IRoleService roles) : AppController
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("Roles & permissions", null));
        return View(await roles.ListAsync(ct));
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        Crumbs(("Roles & permissions", Url.Action(nameof(Index))), ("New role", null));
        return View("Edit", new RoleFormModel { Catalog = await roles.ListAssignablePermissionsAsync(ct) });
    }

    [HttpPost]
    public async Task<IActionResult> Create(RoleFormModel model, CancellationToken ct)
    {
        Crumbs(("Roles & permissions", Url.Action(nameof(Index))), ("New role", null));
        model.Catalog = await roles.ListAssignablePermissionsAsync(ct);
        if (!ModelState.IsValid) return View("Edit", model);
        var result = await roles.CreateAsync(new SaveRoleRequest(model.Name, model.Description, model.Permissions), ct);
        if (result.IsFailure)
        {
            if (Reject(result) is { } r) return r;
            AddError(result);
            return View("Edit", model);
        }
        Success("Role created.");
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var role = await roles.GetAsync(id, ct);
        if (Reject(role) is { } r) return r;
        Crumbs(("Roles & permissions", Url.Action(nameof(Index))), (role.Value.Name, null));
        return View(new RoleFormModel
        {
            Id = id, Name = role.Value.Name, Description = role.Value.Description, IsSystem = role.Value.IsSystem,
            Permissions = role.Value.Permissions.ToList(), Catalog = await roles.ListAssignablePermissionsAsync(ct)
        });
    }

    [HttpPost]
    public async Task<IActionResult> Edit(Guid id, RoleFormModel model, CancellationToken ct)
    {
        model.Id = id;
        model.Catalog = await roles.ListAssignablePermissionsAsync(ct);
        Crumbs(("Roles & permissions", Url.Action(nameof(Index))), (model.Name, null));
        if (!ModelState.IsValid) return View(model);
        var result = await roles.UpdateAsync(id, new SaveRoleRequest(model.Name, model.Description, model.Permissions), ct);
        if (result.IsFailure)
        {
            if (Reject(result) is { } r) return r;
            AddError(result);
            return View(model);
        }
        Success("Role updated.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var result = await roles.DeleteAsync(id, ct);
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success("Role deleted."); else Failure(result.Error.Message);
        return RedirectToAction(nameof(Index));
    }
}
