using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Web.Controllers;

[Authorize(Policy = PermissionCodes.SettingsManage)]
public class SettingsController(ITenantSettingsService settings) : AppController
{
    public async Task<IActionResult> Index(CancellationToken ct) => await Render(null, null, ct);

    [HttpPost]
    public async Task<IActionResult> Profile(TenantEditModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid) return await Render(model, null, ct);
        var result = await settings.UpdateProfileAsync(new UpdateTenantProfileRequest(model.Name, model.Industry, model.Timezone, model.Locale), ct);
        if (result.IsFailure) { AddError(result); return await Render(model, null, ct); }
        Success("Workspace profile saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Save(SettingFormModel model, CancellationToken ct)
    {
        if (!ModelState.IsValid) return await Render(null, model, ct);
        var result = await settings.SetAsync(model.Key.Trim(), new SetSettingRequest(model.Value, model.IsSecret), ct);
        if (result.IsFailure) { AddError(result); return await Render(null, model, ct); }
        Success($"Setting '{result.Value.Key}' saved.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(string key, CancellationToken ct)
    {
        var result = await settings.DeleteAsync(key, ct);
        if (Reject(result) is { } r) return r;
        Success($"Setting '{key}' deleted.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult> Render(TenantEditModel? profile, SettingFormModel? form, CancellationToken ct)
    {
        Crumbs(("Settings", null));
        var current = await settings.GetProfileAsync(ct);
        if (Reject(current) is { } r) return r;
        var p = profile ?? new TenantEditModel { Id = current.Value.Id, Name = current.Value.Name, Industry = current.Value.Industry, Timezone = current.Value.Timezone, Locale = current.Value.Locale };
        return View("Index", new SettingsViewModel { Profile = p, Settings = await settings.ListAsync(ct), NewSetting = form ?? new SettingFormModel() });
    }
}
