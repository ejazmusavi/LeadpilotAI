using FluentValidation;
using LeadPilot.Domain.Common;
using LeadPilot.Shared;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Web;

/// <summary>Maps exceptions to RFC 7807 problem responses; never leaks internals outside Development.</summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment env) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var correlationId = ctx.Items[CorrelationIds.LogProperty]?.ToString();
        ProblemDetails problem;

        switch (ex)
        {
            case ValidationException ve:
                var errors = ve.Errors.GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
                problem = new ValidationProblemDetails(errors) { Status = StatusCodes.Status400BadRequest, Title = "One or more validation errors occurred." };
                break;
            case DomainException de:
                problem = new ProblemDetails { Status = StatusCodes.Status422UnprocessableEntity, Title = "Business rule violated.", Detail = de.Message };
                break;
            case OperationCanceledException when ctx.RequestAborted.IsCancellationRequested:
                return true; // client went away
            default:
                logger.LogError(ex, "Unhandled exception. CorrelationId={CorrelationId}", correlationId);
                problem = new ProblemDetails
                {
                    Status = StatusCodes.Status500InternalServerError,
                    Title = "An unexpected error occurred.",
                    Detail = env.IsDevelopment() ? ex.ToString() : null
                };
                break;
        }

        problem.Extensions["correlationId"] = correlationId;
        ctx.Response.StatusCode = problem.Status ?? 500;
        await ctx.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}
