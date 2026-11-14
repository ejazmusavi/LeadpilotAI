using LeadPilot.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Api.Controllers;

[ApiController]
[Route("api/v1/system")]
[Produces("application/json")]
public class SystemController(IClock clock, IWebHostEnvironment env) : ControllerBase
{
    /// <summary>Basic service information. Anonymous: it exposes no tenant data.</summary>
    [HttpGet("info")]
    [AllowAnonymous]
    [ProducesResponseType<SystemInfoResponse>(StatusCodes.Status200OK)]
    public ActionResult<SystemInfoResponse> Info() =>
        new SystemInfoResponse("LeadPilot.Api", typeof(SystemController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", env.EnvironmentName, clock.UtcNow);
}
