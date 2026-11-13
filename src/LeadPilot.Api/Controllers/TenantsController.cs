using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Api.Controllers;

/// <summary>Platform-level tenant management (Tenants.Manage).</summary>
[Route("api/v1/tenants"), Authorize(Policy = PermissionCodes.TenantsManage)]
public class TenantsController(ITenantAdminService admin) : ApiControllerBase
{
    [HttpGet]
    public async Task<PagedResult<TenantDto>> List(string? search, TenantStatus? status, int page = 1, int pageSize = 25, CancellationToken ct = default) =>
        await admin.ListAsync(search, status, page, pageSize, ct);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await admin.GetAsync(id, ct));

    [HttpPost]
    public async Task<IActionResult> Create(CreateTenantRequest request, CancellationToken ct)
    {
        var r = await admin.CreateAsync(request, ct);
        return r.IsSuccess ? CreatedAtAction(nameof(Get), new { id = r.Value.Tenant.Id }, r.Value) : Fail(r);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, UpdateTenantProfileRequest request, CancellationToken ct) => Respond(await admin.UpdateAsync(id, request, ct));

    [HttpPost("{id:guid}/suspend")]
    public async Task<IActionResult> Suspend(Guid id, SuspendTenantRequest request, CancellationToken ct) => Respond(await admin.SuspendAsync(id, request.Reason, ct));

    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct) => Respond(await admin.ActivateAsync(id, ct));
}
