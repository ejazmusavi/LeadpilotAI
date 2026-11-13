using System.Diagnostics;
using LeadPilot.Shared;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LeadPilot.Web.Controllers;

public class HomeController(HealthCheckService health, IWebHostEnvironment env) : Controller
{
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var report = await health.CheckHealthAsync(c => c.Tags.Contains("ready"), ct);
        ViewBag.DatabaseHealthy = report.Status == HealthStatus.Healthy;
        ViewBag.Environment = env.EnvironmentName;
        ViewBag.Version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        return View();
    }

    [AllowAnonymous, ResponseCache(Duration = 0, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel { CorrelationId = HttpContext.Items[CorrelationIds.LogProperty]?.ToString() ?? Activity.Current?.Id });

    [AllowAnonymous, ResponseCache(Duration = 0, NoStore = true)]
    public IActionResult StatusPage(int code) =>
        View("Error", new ErrorViewModel { StatusCode = code, CorrelationId = HttpContext.Items[CorrelationIds.LogProperty]?.ToString() });
}
