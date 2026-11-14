using LeadPilot.Application.Config;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Config;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Api.Controllers;

/// <summary>Typed custom fields on leads, contacts, opportunities and customers. Tenant-scoped; another tenant's ids answer 404.</summary>
[Route("api/v1/custom-fields")]
public class CustomFieldsController(ICustomFieldService fields) : ApiControllerBase
{
    /// <summary>The definitions (what forms and filters are built from). Any signed-in member with lead access may read them.</summary>
    [HttpGet, Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IReadOnlyList<CustomFieldDto>> List([FromQuery] CustomEntityType? entityType, [FromQuery] bool includeInactive, CancellationToken ct) => await fields.ListAsync(entityType, includeInactive, ct);

    [HttpPost, Authorize(Policy = PermissionCodes.CustomFieldsManage)]
    public async Task<IActionResult> Create(SaveCustomFieldRequest request, CancellationToken ct) { var r = await fields.CreateAsync(request, ct); return r.IsSuccess ? Created($"api/v1/custom-fields/{r.Value.Id}", r.Value) : Fail(r); }

    /// <summary>Key, type and record type never change.</summary>
    [HttpPut("{id:guid}"), Authorize(Policy = PermissionCodes.CustomFieldsManage)]
    public async Task<IActionResult> Update(Guid id, SaveCustomFieldRequest request, CancellationToken ct) => Respond(await fields.UpdateAsync(id, request, ct));

    [HttpPost("{id:guid}/activate"), Authorize(Policy = PermissionCodes.CustomFieldsManage)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct) => Respond(await fields.SetActiveAsync(id, true, ct));

    /// <summary>Deactivating keeps the stored values but hides the field and stops it scoring or filtering.</summary>
    [HttpPost("{id:guid}/deactivate"), Authorize(Policy = PermissionCodes.CustomFieldsManage)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct) => Respond(await fields.SetActiveAsync(id, false, ct));
}

public sealed record SetCustomValuesRequest(Dictionary<string, string?> Values) : LeadPilot.Application.Crm.TenantScopedRequest;

/// <summary>Values of custom fields on one record. Writing needs the permission that edits that kind of record.</summary>
[Route("api/v1/custom-fields/values"), Authorize]
public class CustomFieldValuesController(ICustomFieldService fields, IPermissionEvaluator permissions) : ApiControllerBase
{
    private static string Needed(CustomEntityType t) => t == CustomEntityType.Customer ? PermissionCodes.CustomersManage : PermissionCodes.LeadsUpdate;
    private static string NeededToRead(CustomEntityType t) => t == CustomEntityType.Customer ? PermissionCodes.CustomersView : PermissionCodes.LeadsView;

    [HttpGet("{entityType}/{entityId:guid}")]
    public async Task<IActionResult> Get(CustomEntityType entityType, Guid entityId, CancellationToken ct)
    {
        if (!await permissions.HasAsync(NeededToRead(entityType), ct)) return Forbid();
        return Respond(await fields.GetValuesAsync(entityType, entityId, ct));
    }

    /// <summary>Validates every value against its field (type, options, range, pattern, required) and stores all or none. null clears a value.</summary>
    [HttpPut("{entityType}/{entityId:guid}")]
    public async Task<IActionResult> Set(CustomEntityType entityType, Guid entityId, SetCustomValuesRequest request, CancellationToken ct)
    {
        if (!await permissions.HasAsync(Needed(entityType), ct)) return Forbid();
        return Respond(await fields.SetValuesAsync(entityType, entityId, request.Values ?? [], ct));
    }
}

/// <summary>Industry template packs: gallery, dry run, import, history, private packs, export.</summary>
[Route("api/v1/packs")]
public class PacksController(IPackService packs) : ApiControllerBase
{
    [HttpGet, Authorize(Policy = PermissionCodes.ConfigPacksImport)]
    public async Task<IReadOnlyList<PackSummaryDto>> List(CancellationToken ct) => await packs.ListAsync(ct);

    [HttpGet("{id:guid}"), Authorize(Policy = PermissionCodes.ConfigPacksImport)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await packs.GetAsync(id, ct));

    /// <summary>The pack's JSON, so it can be read before it is applied.</summary>
    [HttpGet("{id:guid}/content"), Authorize(Policy = PermissionCodes.ConfigPacksImport)]
    public async Task<IActionResult> Content(Guid id, CancellationToken ct) { var r = await packs.GetContentAsync(id, ct); return r.IsSuccess ? Content(r.Value, "application/json") : Fail(r); }

    /// <summary>Dry run: exactly what an import would create, update, keep or skip. Writes nothing.</summary>
    [HttpPost("{id:guid}/preview"), Authorize(Policy = PermissionCodes.ConfigPacksImport)]
    public async Task<IActionResult> Preview(Guid id, ApplyPackOptions options, CancellationToken ct) => Respond(await packs.PreviewAsync(id, options ?? new ApplyPackOptions(), ct));

    /// <summary>Applies the pack in one transaction (all or nothing).</summary>
    [HttpPost("{id:guid}/apply"), Authorize(Policy = PermissionCodes.ConfigPacksImport)]
    public async Task<IActionResult> Apply(Guid id, ApplyPackOptions options, CancellationToken ct) => Respond(await packs.ApplyAsync(id, options ?? new ApplyPackOptions(), ct));

    [HttpGet("history"), Authorize(Policy = PermissionCodes.ConfigPacksImport)]
    public async Task<IReadOnlyList<PackApplicationDto>> History(CancellationToken ct) => await packs.HistoryAsync(ct);

    /// <summary>Checks pack JSON without storing it.</summary>
    [HttpPost("validate"), Authorize(Policy = PermissionCodes.ConfigPacksManage)]
    public async Task<PackValidationDto> Validate(UploadPackRequest request, CancellationToken ct) => await packs.ValidateAsync(request.Json ?? "", ct);

    /// <summary>Stores a private pack of this workspace (or a new version of one).</summary>
    [HttpPost, Authorize(Policy = PermissionCodes.ConfigPacksManage)]
    public async Task<IActionResult> Upload(UploadPackRequest request, CancellationToken ct) { var r = await packs.UploadPrivateAsync(request.Json ?? "", ct); return r.IsSuccess ? Created($"api/v1/packs/{r.Value.Id}", r.Value) : Fail(r); }

    [HttpDelete("{id:guid}"), Authorize(Policy = PermissionCodes.ConfigPacksManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => Respond(await packs.DeletePrivateAsync(id, ct));

    /// <summary>Exports this workspace's configuration as a pack file. Never people, credentials, keys or settings.</summary>
    [HttpPost("export"), Authorize(Policy = PermissionCodes.ConfigPacksExport)]
    public async Task<IActionResult> Export(PackExportRequest request, CancellationToken ct) => Respond(await packs.ExportAsync(request, ct));
}

/// <summary>Platform administrators publish packs for every workspace. Needs Platform.Packs.Manage, which only platform roles can hold.</summary>
[Route("api/v1/platform/packs")]
public class PlatformPacksController(IPlatformPackService packs) : ApiControllerBase
{
    [HttpGet, Authorize(Policy = PermissionCodes.PlatformPacksManage)]
    public async Task<IReadOnlyList<PackSummaryDto>> List(CancellationToken ct) => await packs.ListAsync(ct);

    [HttpPost, Authorize(Policy = PermissionCodes.PlatformPacksManage)]
    public async Task<IActionResult> Publish(UploadPackRequest request, CancellationToken ct) { var r = await packs.PublishAsync(request.Json ?? "", request.Publish, ct); return r.IsSuccess ? Ok(r.Value) : Fail(r); }

    [HttpPost("{id:guid}/publish"), Authorize(Policy = PermissionCodes.PlatformPacksManage)]
    public async Task<IActionResult> SetPublished(Guid id, CancellationToken ct) => Respond(await packs.SetPublishedAsync(id, true, ct));

    [HttpPost("{id:guid}/withdraw"), Authorize(Policy = PermissionCodes.PlatformPacksManage)]
    public async Task<IActionResult> Withdraw(Guid id, CancellationToken ct) => Respond(await packs.SetPublishedAsync(id, false, ct));
}
