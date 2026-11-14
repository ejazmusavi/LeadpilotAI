using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Infrastructure.Identity;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LeadPilot.Web.Controllers;

public class InvitationsController(
    IInvitationService invitations, IRoleService roles, ICurrentUser me,
    UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) : AppController
{
    [Authorize(Policy = PermissionCodes.UsersManage)]
    public async Task<IActionResult> Index(int page = 1, CancellationToken ct = default)
    {
        Crumbs(("Invitations", null));
        return View(new InvitationListViewModel { Result = await invitations.ListAsync(page, 20, ct) });
    }

    [Authorize(Policy = PermissionCodes.UsersManage), HttpGet]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        Crumbs(("Invitations", Url.Action(nameof(Index))), ("Invite user", null));
        return View(new InviteFormModel { Roles = await roles.ListAsync(ct) });
    }

    [Authorize(Policy = PermissionCodes.UsersManage), HttpPost]
    public async Task<IActionResult> Create(InviteFormModel model, CancellationToken ct)
    {
        Crumbs(("Invitations", Url.Action(nameof(Index))), ("Invite user", null));
        model.Roles = await roles.ListAsync(ct);
        if (!ModelState.IsValid) return View(model);
        var result = await invitations.CreateAsync(new CreateInvitationRequest(model.Email, model.RoleId!.Value), ct);
        if (result.IsFailure)
        {
            if (result.Error.Code == "email_failed") { Failure(result.Error.Message); return RedirectToAction(nameof(Index)); }
            if (Reject(result) is { } r) return r;
            AddError(result);
            return View(model);
        }
        Success($"Invitation sent to {result.Value.Email}.");
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = PermissionCodes.UsersManage), HttpPost]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var result = await invitations.RevokeAsync(id, ct);
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success("Invitation revoked."); else Failure(result.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [Authorize(Policy = PermissionCodes.UsersManage), HttpPost]
    public async Task<IActionResult> Resend(Guid id, CancellationToken ct)
    {
        var result = await invitations.ResendAsync(id, ct);
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success("Invitation re-sent. The previous link no longer works."); else Failure(result.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    // ---- Accept flow (anonymous: the token is the credential) ----

    [AllowAnonymous, EnableRateLimiting("auth"), HttpGet]
    public async Task<IActionResult> Accept(string? token, CancellationToken ct)
    {
        var model = await BuildAcceptModel(token, ct);
        if (model.Preview is null) { Response.StatusCode = StatusCodes.Status400BadRequest; return View("AcceptInvalid"); }
        return View(model);
    }

    [AllowAnonymous, EnableRateLimiting("auth"), HttpPost]
    public async Task<IActionResult> Accept(AcceptInvitationViewModel model, CancellationToken ct)
    {
        var fresh = await BuildAcceptModel(model.Token, ct);
        if (fresh.Preview is null) { Response.StatusCode = StatusCodes.Status400BadRequest; return View("AcceptInvalid"); }
        fresh.DisplayName = model.DisplayName;

        if (!fresh.Preview.AccountExists && model.Password != model.ConfirmPassword)
        {
            ModelState.AddModelError(nameof(model.ConfirmPassword), "The passwords do not match.");
            return View(fresh);
        }

        var result = await invitations.AcceptAsync(new AcceptInvitationRequest(model.Token, model.DisplayName, model.Password), me.UserId, ct);
        if (result.IsFailure)
        {
            if (result.Error.Code == "invalid_invitation") { Response.StatusCode = StatusCodes.Status400BadRequest; return View("AcceptInvalid"); }
            AddError(result);
            return View(fresh);
        }

        var user = await users.FindByIdAsync(result.Value.UserId.ToString());
        if (user is null) return Forbid();
        await SignInIntoTenantAsync(signIn, user, result.Value.TenantId);
        Success($"Welcome! You have joined {fresh.Preview.TenantName}.");
        return Redirect("/");
    }

    private async Task<AcceptInvitationViewModel> BuildAcceptModel(string? token, CancellationToken ct)
    {
        var preview = string.IsNullOrWhiteSpace(token) ? null : await invitations.PreviewAsync(token, ct);
        var signedInAsInvitee = false;
        if (preview is not null && me.UserId is { } uid && await users.FindByIdAsync(uid.ToString()) is { } u)
            signedInAsInvitee = string.Equals(u.Email, preview.Email, StringComparison.OrdinalIgnoreCase);
        return new AcceptInvitationViewModel { Token = token ?? "", Preview = preview, SignedInAsInvitee = signedInAsInvitee };
    }
}
