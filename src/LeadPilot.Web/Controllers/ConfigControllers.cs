using System.Text;
using LeadPilot.Application.Config;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Config;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Web.Controllers;

/// <summary>Industry template packs: gallery, import wizard (dry run, conflict choices, apply), history, private packs and export.</summary>
[Authorize(Policy = PermissionCodes.ConfigPacksImport)]
public class PacksController(IPackService packs, IPermissionEvaluator permissions) : AppController
{
    private const long MaxUploadBytes = PackParser.MaxBytes;

    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("Industry templates", null));
        var p = await permissions.GetCurrentAsync(ct);
        return View(new PackGalleryViewModel { Packs = await packs.ListAsync(ct), CanManage = p.Has(PermissionCodes.ConfigPacksManage), CanExport = p.Has(PermissionCodes.ConfigPacksExport) });
    }

    private async Task<PackImportViewModel?> LoadAsync(Guid id, CancellationToken ct)
    {
        var pack = await packs.GetAsync(id, ct);
        if (!pack.IsSuccess) return null;
        var content = await packs.GetContentAsync(id, ct);
        var doc = content.IsSuccess ? PackParser.Parse(content.Value).Pack : null;
        return new PackImportViewModel { Pack = pack.Value, Pipelines = doc?.Pipelines.Select(x => (x.Key, x.Name)).ToList() ?? [] };
    }

    [HttpGet]
    public async Task<IActionResult> Import(Guid id, CancellationToken ct)
    {
        var vm = await LoadAsync(id, ct);
        if (vm is null) return NotFound();
        Crumbs(("Industry templates", Url.Action(nameof(Index))), (vm.Pack.Name, null));
        return View(vm);
    }

    /// <summary>Dry run: shows exactly what applying would do; writes nothing.</summary>
    [HttpPost]
    public async Task<IActionResult> Preview(Guid id, PackImportViewModel form, CancellationToken ct)
    {
        var vm = await LoadAsync(id, ct);
        if (vm is null) return NotFound();
        vm.Sections = form.Sections; vm.Policy = form.Policy; vm.DefaultPipelineKey = form.DefaultPipelineKey; vm.Res = form.Res ?? new();
        var plan = await packs.PreviewAsync(id, vm.ToOptions(), ct);
        if (Reject(plan) is { } r) return r;
        if (plan.IsFailure) Failure(plan.Error.Message); else vm.Plan = plan.Value;
        Crumbs(("Industry templates", Url.Action(nameof(Index))), (vm.Pack.Name, null));
        return View("Import", vm);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ConfigPacksImport)]
    public async Task<IActionResult> Apply(Guid id, PackImportViewModel form, CancellationToken ct)
    {
        var vm = await LoadAsync(id, ct);
        if (vm is null) return NotFound();
        vm.Sections = form.Sections; vm.Policy = form.Policy; vm.DefaultPipelineKey = form.DefaultPipelineKey; vm.Res = form.Res ?? new();
        var result = await packs.ApplyAsync(id, vm.ToOptions(), ct);
        if (Reject(result) is { } r) return r;
        Crumbs(("Industry templates", Url.Action(nameof(Index))), (vm.Pack.Name, null));
        if (result.IsFailure) { Failure(result.Error.Message); return View("Import", vm); }
        vm.Applied = result.Value; vm.Plan = result.Value.Plan;
        Success($"'{vm.Pack.Name}' was applied. Nothing was switched on: review what it created before you rely on it.");
        return View("Import", vm);
    }

    [HttpGet]
    public async Task<IActionResult> History(CancellationToken ct)
    {
        Crumbs(("Industry templates", Url.Action(nameof(Index))), ("History", null));
        return View(new PackHistoryViewModel { History = await packs.HistoryAsync(ct) });
    }

    [HttpGet, Authorize(Policy = PermissionCodes.ConfigPacksManage)]
    public async Task<IActionResult> Manage(CancellationToken ct)
    {
        Crumbs(("Industry templates", Url.Action(nameof(Index))), ("Private packs and export", null));
        return View(await ManageModel(ct));
    }

    private async Task<PackManageViewModel> ManageModel(CancellationToken ct) =>
        new() { Private = (await packs.ListAsync(ct)).Where(p => p.Scope == PackScope.Tenant).ToList() };

    [HttpPost, Authorize(Policy = PermissionCodes.ConfigPacksManage), RequestSizeLimit(MaxUploadBytes + 4096)]
    public async Task<IActionResult> Upload(IFormFile? file, bool validateOnly, CancellationToken ct)
    {
        if (file is null || file.Length == 0) { Failure("Choose a pack file (.json)."); return RedirectToAction(nameof(Manage)); }
        if (file.Length > MaxUploadBytes) { Failure($"The file is larger than {MaxUploadBytes / 1024} KB."); return RedirectToAction(nameof(Manage)); }
        string text;
        using (var reader = new StreamReader(file.OpenReadStream(), new UTF8Encoding(false, true), true, 4096)) { try { text = await reader.ReadToEndAsync(ct); } catch (DecoderFallbackException) { Failure("The file is not valid UTF-8 text."); return RedirectToAction(nameof(Manage)); } }
        if (validateOnly)
        {
            var model = await ManageModel(ct); model.Validation = await packs.ValidateAsync(text, ct);
            Crumbs(("Industry templates", Url.Action(nameof(Index))), ("Private packs and export", null));
            return View("Manage", model);
        }
        var r = await packs.UploadPrivateAsync(text, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success($"Pack '{r.Value.Name}' (version {r.Value.Version}) is available to this workspace only."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Manage));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ConfigPacksManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var r = await packs.DeletePrivateAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Pack deleted. What it created stays."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Manage));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ConfigPacksExport)]
    public async Task<IActionResult> Export(string key, string name, string? description, string? industry, int version = 1, CancellationToken ct = default)
    {
        var r = await packs.ExportAsync(new PackExportRequest(key?.Trim() ?? "", name ?? "", description, industry, version), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsFailure) { Failure(r.Error.Message); return RedirectToAction(nameof(Manage)); }
        return File(Encoding.UTF8.GetBytes(r.Value.Json), "application/json", r.Value.FileName);
    }
}

/// <summary>Typed custom fields of leads, contacts, opportunities and customers.</summary>
[Authorize(Policy = PermissionCodes.CustomFieldsManage)]
public class CustomFieldsController(ICustomFieldService fields) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CustomEntityType entity = CustomEntityType.Lead, Guid? edit = null, CancellationToken ct = default)
    {
        Crumbs(("Custom fields", null));
        var all = await fields.ListAsync(entity, true, ct);
        var vm = new CustomFieldsViewModel { Fields = all, Entity = entity, Form = new CustomFieldEditModel { EntityType = entity } };
        if (edit is { } id && all.FirstOrDefault(f => f.Id == id) is { } f)
        {
            vm.EditingId = id;
            vm.Form = new CustomFieldEditModel { EntityType = f.EntityType, Key = f.Key, Label = f.Label, Type = f.Type, Options = string.Join("\n", f.Options), IsRequired = f.IsRequired, MinValue = f.MinValue, MaxValue = f.MaxValue, Pattern = f.Pattern, HelpText = f.HelpText, IsFilterable = f.IsFilterable, SortOrder = f.SortOrder, ExpectedVersion = f.Version };
        }
        return View(vm);
    }

    [HttpPost]
    public async Task<IActionResult> Create(CustomFieldEditModel form, CancellationToken ct)
    {
        if (!ModelState.IsValid) { Failure("Check the values: " + string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage))); return RedirectToAction(nameof(Index), new { entity = form.EntityType }); }
        var r = await fields.CreateAsync(form.ToRequest(), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success($"Custom field '{r.Value.Label}' created."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index), new { entity = form.EntityType });
    }

    [HttpPost]
    public async Task<IActionResult> Update(Guid id, CustomFieldEditModel form, CancellationToken ct)
    {
        var r = await fields.UpdateAsync(id, form.ToRequest(), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Custom field saved. Existing values were not changed."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index), new { entity = form.EntityType });
    }

    [HttpPost]
    public async Task<IActionResult> SetActive(Guid id, bool active, CustomEntityType entity, CancellationToken ct)
    {
        var r = await fields.SetActiveAsync(id, active, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success(active ? "Custom field activated." : "Custom field deactivated. Its values are kept."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index), new { entity });
    }
}

/// <summary>Platform administrators publish packs for every workspace.</summary>
[Authorize(Policy = PermissionCodes.PlatformPacksManage)]
public class PlatformPacksController(IPlatformPackService packs) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("Platform", null), ("Industry packs", null));
        return View(new PlatformPacksViewModel { Packs = await packs.ListAsync(ct) });
    }

    [HttpPost, RequestSizeLimit(PackParser.MaxBytes + 4096)]
    public async Task<IActionResult> Upload(IFormFile? file, bool publish, CancellationToken ct)
    {
        if (file is null || file.Length == 0 || file.Length > PackParser.MaxBytes) { Failure("Choose a pack file (.json) of at most 256 KB."); return RedirectToAction(nameof(Index)); }
        string text;
        using (var reader = new StreamReader(file.OpenReadStream(), new UTF8Encoding(false, true))) { try { text = await reader.ReadToEndAsync(ct); } catch (DecoderFallbackException) { Failure("The file is not valid UTF-8 text."); return RedirectToAction(nameof(Index)); } }
        var r = await packs.PublishAsync(text, publish, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success($"Pack '{r.Value.Name}' v{r.Value.Version} saved{(r.Value.IsPublished ? " and published to all workspaces" : " (not published)")}."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> SetPublished(Guid id, bool published, CancellationToken ct)
    {
        var r = await packs.SetPublishedAsync(id, published, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success(published ? "Pack published to all workspaces." : "Pack withdrawn. Workspaces that applied it keep what it created."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }
}
