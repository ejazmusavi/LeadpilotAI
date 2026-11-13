using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Web.Controllers;

/// <summary>Pages that exist for signed-in users regardless of the active workspace.</summary>
[Authorize]
public class WorkspaceController(IProfileService profile) : AppController
{
    [HttpGet("/Account/NoWorkspace")]
    public IActionResult NoWorkspace() => View();

    [HttpGet("/Profile")]
    public async Task<IActionResult> Profile(CancellationToken ct)
    {
        var p = await profile.GetAsync(ct);
        if (Reject(p) is { } r) return r;
        Crumbs(("My profile", null));
        return View(new ProfileFormModel { Email = p.Value.Email, DisplayName = p.Value.DisplayName, Phone = p.Value.Phone });
    }

    [HttpPost("/Profile")]
    public async Task<IActionResult> Profile(ProfileFormModel model, CancellationToken ct)
    {
        Crumbs(("My profile", null));
        if (!ModelState.IsValid) return View(model);
        var result = await profile.UpdateAsync(new UpdateProfileRequest(model.DisplayName, model.Phone), ct);
        if (result.IsFailure) { AddError(result); return View(model); }
        Success("Profile saved.");
        return Redirect("/Profile");
    }
}
