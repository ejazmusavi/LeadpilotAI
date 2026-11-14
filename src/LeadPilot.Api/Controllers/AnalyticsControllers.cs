using LeadPilot.Application.Analytics;
using LeadPilot.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Api.Controllers;

/// <summary>
/// Dashboard metrics and reports of the signed-in workspace. Every number is computed from recorded rows and carries a label (<c>Verified</c>, <c>Calculated</c> or <c>Estimated</c>) plus its definition.
/// Date filters use workspace-local calendar days with BOTH ends inclusive (<c>from</c>/<c>to</c>) or a <c>preset</c>. Without the Reports.Team permission every answer is limited to the caller's own leads and team-only
/// reports answer 403. Unknown report keys answer 404.
/// </summary>
[Route("api/v1/analytics")]
public class AnalyticsController(IAnalyticsService analytics) : ApiControllerBase
{
    /// <summary>The dashboard: every metric (value, previous period, definition, label) and the trend series, plus how fresh the data is.</summary>
    [HttpGet("dashboard"), Authorize(Policy = PermissionCodes.DashboardView)]
    public async Task<IActionResult> Dashboard([FromQuery] RangeRequest range, CancellationToken ct) => Respond(await analytics.DashboardAsync(range, ct));

    /// <summary>The documented definition, label, formula and source tables of every metric.</summary>
    [HttpGet("metrics"), Authorize(Policy = PermissionCodes.DashboardView)]
    public IReadOnlyList<MetricDefinition> Metrics() => analytics.Metrics();

    /// <summary>The available reports with their columns (label and definition per column).</summary>
    [HttpGet("reports"), Authorize(Policy = PermissionCodes.ReportsView)]
    public IReadOnlyList<ReportDefinitionDto> Reports() => analytics.Reports();

    /// <summary>One page of a report plus its totals row (totals cover all rows, not just the page).</summary>
    [HttpGet("reports/{key}"), Authorize(Policy = PermissionCodes.ReportsView)]
    public async Task<IActionResult> Report(string key, [FromQuery] RangeRequest range, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        Respond(await analytics.ReportAsync(key, range, page, pageSize, ct));

    /// <summary>
    /// Streams the whole report as CSV (UTF-8 with BOM, spreadsheet-formula-injection safe, audited with the row count before any data is sent). Reports above the row cap answer 413: narrow the date range.
    /// </summary>
    [HttpGet("reports/{key}/export"), Authorize(Policy = PermissionCodes.ReportsExport)]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "text/csv")]
    public async Task<IActionResult> Export(string key, [FromQuery] RangeRequest range, CancellationToken ct)
    {
        var r = await analytics.ExportCsvAsync(key, range, file =>
        {
            Response.ContentType = file.ContentType;
            Response.Headers.ContentDisposition = $"attachment; filename=\"{file.FileName}\"";
            Response.Headers.CacheControl = "no-store";
            return Response.Body;
        }, ct);
        return r.IsSuccess ? new EmptyResult() : Fail(r);
    }
}
