using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Web.Controllers;

[Authorize(Policy = PermissionCodes.AuditView)]
public class AuditController(IAuditService audit) : AppController
{
    public async Task<IActionResult> Index(string? q, DateTime? from, DateTime? to, int page = 1, CancellationToken ct = default)
    {
        Crumbs(("Audit log", null));
        var result = await audit.QueryAsync(new AuditQuery(q, from, to?.Date.AddDays(1), page, 25), ct);
        return View(new AuditViewModel { Query = q, From = from, To = to, Result = result });
    }
}
