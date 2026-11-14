using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Identity;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Web.Controllers;

[Authorize]
public class TenantsController(
    ITenantAdminService admin, ITenantSwitchService switcher, ICurrentUser me, ITenantContext tenant,
    UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) : AppController
{
    [Authorize(Policy = PermissionCodes.TenantsManage)]
    public async Task<IActionResult> Index(string? q, string? status, int page = 1, CancellationToken ct = default)
    {
        TenantStatus? st = Enum.TryParse<TenantStatus>(status, out var parsed) ? parsed : null;
        Crumbs(("Tenants", null));
        return View(new TenantListViewModel { Query = q, Status = status, Result = await admin.ListAsync(q, st, page, 20, ct) });
    }

    [Authorize(Policy = PermissionCodes.TenantsManage), HttpGet]
    public IActionResult Create()
    {
        Crumbs(("Tenants", Url.Action(nameof(Index))), ("New tenant", null));
        return View(new TenantFormModel());
    }

    [Authorize(Policy = PermissionCodes.TenantsManage), HttpPost]
    public async Task<IActionResult> Create(TenantFormModel model, CancellationToken ct)
    {
        Crumbs(("Tenants", Url.Action(nameof(Index))), ("New tenant", null));
        if (!ModelState.IsValid) return View(model);
        var result = await admin.CreateAsync(new CreateTenantRequest(model.Name, model.Slug, model.Industry, model.Timezone, model.Locale, model.AdminEmail), ct);
        if (result.IsFailure) { AddError(result); return View(model); }
        Success(result.Value.AdminInvited ? "Tenant created and the administrator was invited." : model.AdminEmail is null ? "Tenant created." : "Tenant created, but the administrator invitation e-mail could not be sent. Invite them from inside the tenant.");
        return RedirectToAction(nameof(Details), new { id = result.Value.Tenant.Id });
    }

    [Authorize(Policy = PermissionCodes.TenantsManage)]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var result = await admin.GetAsync(id, ct);
        if (Reject(result) is { } r) return r;
        return View(ToDetails(result.Value));
    }

    [Authorize(Policy = PermissionCodes.TenantsManage), HttpPost]
    public async Task<IActionResult> Edit(Guid id, TenantEditModel model, CancellationToken ct)
    {
        var current = await admin.GetAsync(id, ct);
        if (Reject(current) is { } r) return r;
        if (ModelState.IsValid)
        {
            var result = await admin.UpdateAsync(id, new UpdateTenantProfileRequest(model.Name, model.Industry, model.Timezone, model.Locale), ct);
            if (result.IsSuccess) { Success("Tenant updated."); return RedirectToAction(nameof(Details), new { id }); }
            AddError(result);
        }
        model.Id = id;
        return View(nameof(Details), new TenantDetailsViewModel { Tenant = current.Value, Form = model });
    }

    [Authorize(Policy = PermissionCodes.TenantsManage), HttpPost]
    public async Task<IActionResult> Suspend(Guid id, SuspendModel model, CancellationToken ct)
    {
        var result = await admin.SuspendAsync(id, model.Reason, ct);
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success("Tenant suspended. Its users are blocked until it is activated."); else Failure(result.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }

    [Authorize(Policy = PermissionCodes.TenantsManage), HttpPost]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
    {
        var result = await admin.ActivateAsync(id, ct);
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success("Tenant activated."); else Failure(result.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Switch(CancellationToken ct)
    {
        Crumbs(("Switch workspace", null));
        return View(new SwitchViewModel { Tenants = await switcher.ListAccessibleAsync(me.UserId!.Value, tenant.TenantId, ct) });
    }

    [HttpPost]
    public async Task<IActionResult> Switch(Guid id, CancellationToken ct)
    {
        var result = await switcher.SwitchAsync(me.UserId!.Value, id, tenant.TenantId, ct);
        if (result.IsFailure)
        {
            if (result.Error.Code == "not_found") return NotFound();
            Failure(result.Error.Message);
            return RedirectToAction(nameof(Switch));
        }
        var user = await users.FindByIdAsync(me.UserId!.Value.ToString());
        if (user is null) return Forbid();
        await SignInIntoTenantAsync(signIn, user, id);
        Success($"You are now working in {result.Value.TenantName}.");
        return Redirect("/");
    }

    private static TenantDetailsViewModel ToDetails(TenantDto t) => new()
    {
        Tenant = t,
        Form = new TenantEditModel { Id = t.Id, Name = t.Name, Industry = t.Industry, Timezone = t.Timezone, Locale = t.Locale },
    };
}
