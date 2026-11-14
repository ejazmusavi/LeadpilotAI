using LeadPilot.Shared;
using Microsoft.AspNetCore.Http;
using Serilog.Context;

namespace LeadPilot.Infrastructure.Web;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        var id = ctx.Request.Headers[CorrelationIds.HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) id = CorrelationIds.New();
        ctx.Items[CorrelationIds.LogProperty] = id;
        ctx.Response.Headers[CorrelationIds.HeaderName] = id;
        using (LogContext.PushProperty(CorrelationIds.LogProperty, id))
            await next(ctx);
    }
}
