using System.Diagnostics;
using LeadPilot.Application.Analytics;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Shared;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LeadPilot.Web.Controllers;

public class HomeController(HealthCheckService health, IWebHostEnvironment env, IAnalyticsService analytics, IPermissionEvaluator permissions) : Controller
{
    public async Task<IActionResult> Index([FromQuery] RangeRequest range, CancellationToken ct)
    {
        var report = await health.CheckHealthAsync(c => c.Tags.Contains("ready"), ct);
        DashboardDto? dashboard = null; string? error = null;
        if (await permissions.HasAsync(PermissionCodes.DashboardView, ct))
        {
            var r = await analytics.DashboardAsync(range, ct);
            if (r.IsSuccess) dashboard = r.Value; else error = r.Error.Message;
        }
        return View(new DashboardViewModel
        {
            Dashboard = dashboard, Error = error, Request = range, DatabaseHealthy = report.Status == HealthStatus.Healthy, Environment = env.EnvironmentName,
            Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"
        });
    }

    [AllowAnonymous, ResponseCache(Duration = 0, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel { CorrelationId = HttpContext.Items[CorrelationIds.LogProperty]?.ToString() ?? Activity.Current?.Id });

    [AllowAnonymous, ResponseCache(Duration = 0, NoStore = true)]
    public IActionResult StatusPage(int code) =>
        View("Error", new ErrorViewModel { StatusCode = code, CorrelationId = HttpContext.Items[CorrelationIds.LogProperty]?.ToString() });
}
