using LeadPilot.Shared;
using LeadPilot.Shared.Results;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Api.Controllers;

[ApiController]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>Maps a failed use-case result to RFC 7807 with the SECURITY.md semantics (401/403/404/409).</summary>
    protected ObjectResult Fail(Result result)
    {
        var status = result.Error.Code switch
        {
            "not_found" => StatusCodes.Status404NotFound,
            "forbidden" or "tenant_suspended" => StatusCodes.Status403Forbidden,
            "conflict" or "duplicate" => StatusCodes.Status409Conflict,
            "too_large" => StatusCodes.Status413PayloadTooLarge,
            "invalid_credentials" or "invalid_refresh_token" or "sign_in_required" => StatusCodes.Status401Unauthorized,
            "locked_out" => StatusCodes.Status423Locked,
            "email_failed" => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status400BadRequest,
        };
        var problem = new ProblemDetails { Status = status, Title = result.Error.Code, Detail = result.Error.Message };
        problem.Extensions["code"] = result.Error.Code;
        problem.Extensions["correlationId"] = HttpContext.Items[CorrelationIds.LogProperty]?.ToString();
        return new ObjectResult(problem) { StatusCode = status };
    }

    protected IActionResult Respond<T>(Result<T> result) => result.IsSuccess ? Ok(result.Value) : Fail(result);
    protected IActionResult Respond(Result result) => result.IsSuccess ? NoContent() : Fail(result);
}
