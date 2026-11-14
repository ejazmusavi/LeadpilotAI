using LeadPilot.Application.Analytics;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Web.Controllers;

internal static class AnalyticsWeb
{
    public static Dictionary<string, string?> Query(RangeRequest r) => new()
    {
        ["preset"] = r.Preset.ToString(), ["from"] = r.Preset == RangePreset.Custom ? r.From?.ToString("yyyy-MM-dd") : null, ["to"] = r.Preset == RangePreset.Custom ? r.To?.ToString("yyyy-MM-dd") : null, ["compare"] = r.Compare ? "true" : "false"
    };
}

[Authorize(Policy = PermissionCodes.ReportsView)]
public class ReportsController(IAnalyticsService analytics, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("Reports", null));
        var team = await permissions.HasAsync(PermissionCodes.ReportsTeam, ct);
        return View(new ReportsIndexViewModel { Reports = analytics.Reports().Where(r => team || !r.TeamOnly).ToList(), Team = team, CanExport = await permissions.HasAsync(PermissionCodes.ReportsExport, ct) });
    }

    [HttpGet]
    public async Task<IActionResult> Show(string key, [FromQuery] RangeRequest range, int page = 1, CancellationToken ct = default)
    {
        Crumbs(("Reports", Url.Action(nameof(Index))), (ReportCatalog.Find(key)?.Title ?? "Report", null));
        var result = await analytics.ReportAsync(key, range, page, 50, ct);
        if (result.IsFailure)
        {
            if (Reject(result) is { } rejected) return rejected;
            Failure(result.Error.Message);
            return RedirectToAction(nameof(Index));
        }
        return View(new ReportViewModel { Report = result.Value, Request = range, CanExport = await permissions.HasAsync(PermissionCodes.ReportsExport, ct), QueryValues = AnalyticsWeb.Query(range) });
    }

    /// <summary>Streams the CSV; failures (row cap, permission) are shown on the report page instead of a broken download.</summary>
    [HttpGet, Authorize(Policy = PermissionCodes.ReportsExport)]
    public async Task<IActionResult> Export(string key, [FromQuery] RangeRequest range, CancellationToken ct)
    {
        var r = await analytics.ExportCsvAsync(key, range, file =>
        {
            Response.ContentType = file.ContentType;
            Response.Headers.ContentDisposition = $"attachment; filename=\"{file.FileName}\"";
            Response.Headers.CacheControl = "no-store";
            return Response.Body;
        }, ct);
        if (r.IsSuccess) return new EmptyResult();
        if (Reject(r) is { } rejected) return rejected;
        Failure(r.Error.Message);
        return RedirectToAction(nameof(Show), new { key, preset = range.Preset, from = range.From, to = range.To, compare = range.Compare });
    }

    [HttpGet]
    public IActionResult Metrics()
    {
        Crumbs(("Reports", Url.Action(nameof(Index))), ("Metric definitions", null));
        return View(analytics.Metrics());
    }
}
