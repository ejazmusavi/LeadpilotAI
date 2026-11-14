using System.Security.Claims;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Serilog.Context;

namespace LeadPilot.Infrastructure.Web;

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;
    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;
    public Guid? UserId
    {
        get
        {
            var v = Principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? Principal?.FindFirstValue("sub");
            return Guid.TryParse(v, out var id) ? id : null;
        }
    }
    public string? Email => Principal?.FindFirstValue(ClaimTypes.Email) ?? Principal?.FindFirstValue("email") ?? Principal?.Identity?.Name;
}

public sealed class HttpRequestInfo(IHttpContextAccessor accessor) : IRequestInfo
{
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public string? CorrelationId => accessor.HttpContext?.Items[CorrelationIds.LogProperty]?.ToString();
}

/// <summary>Used outside HTTP (jobs, seeding): no user, no request.</summary>
public sealed class NoRequestInfo : ICurrentUser, IRequestInfo
{
    public bool IsAuthenticated => false;
    public Guid? UserId => null;
    public string? Email => null;
    public string? IpAddress => null;
    public string? CorrelationId => null;
}

public sealed class InvitationLinkBuilder(IConfiguration config, IHttpContextAccessor accessor) : IInvitationLinkBuilder
{
    public string Build(string rawToken)
    {
        var baseUrl = config["App:PublicWebUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            var r = accessor.HttpContext?.Request;
            baseUrl = r is null ? "http://localhost:5002" : $"{r.Scheme}://{r.Host}";
        }
        return $"{baseUrl.TrimEnd('/')}/Invitations/Accept?token={Uri.EscapeDataString(rawToken)}";
    }
}

public sealed class TenantResolutionOptions
{
    /// <summary>Where signed-in users without any usable workspace are sent (MVC). Null → 403 JSON (API).</summary>
    public string? NoTenantRedirectPath { get; set; }
    /// <summary>When true the cookie is cleared if membership is lost (MVC).</summary>
    public bool SignOutOnAccessLoss { get; set; }
    public string LoginPath { get; set; } = "/Account/Login";
    /// <summary>Paths that stay reachable (without a tenant context) when the tenant is unusable, so users can sign out or switch.</summary>
    public string[] ExemptPathPrefixes { get; set; } = [];
}

/// <summary>
/// Resolves the active tenant for every authenticated request: the tid claim is only a request, validated against membership each time
/// (suspended tenants, removed members and disabled users are cut off immediately). Never reads TenantId from client input.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next, IOptions<TenantResolutionOptions> options)
{
    public const string HeaderName = "X-Tenant-Id";
    public const string TenantNameItem = "LeadPilot.TenantName";
    public const string PlatformAccessItem = "LeadPilot.PlatformAccess";
    private readonly TenantResolutionOptions _o = options.Value;

    public async Task InvokeAsync(HttpContext ctx, ITenantContextAccessor accessor, IMembershipResolver membership, ICurrentUser user)
    {
        if (!user.IsAuthenticated) { await next(ctx); return; }

        var path = ctx.Request.Path.Value ?? "/";
        var exempt = _o.ExemptPathPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

        if (user.UserId is not { } userId) { await Problem(ctx, 401, "unauthorized", "The token does not identify a user."); return; }
        var claim = ctx.User.FindFirstValue(Identity.TenantClaims.TenantId);
        if (!Guid.TryParse(claim, out var tenantId))
        {
            if (exempt) { await next(ctx); return; }
            await NoUsableTenant(ctx, "no_tenant", "This account has no active workspace.");
            return;
        }

        var access = await membership.ResolveAsync(userId, tenantId, ctx.RequestAborted);
        if (!access.IsGranted)
        {
            if (access.Outcome == TenantAccessOutcome.UserInactive)
            {
                if (_o.SignOutOnAccessLoss) { await ctx.SignOutAsync(IdentityConstants.ApplicationScheme); ctx.Response.Redirect(_o.LoginPath); }
                else await Problem(ctx, 401, "user_disabled", "This account is disabled.");
                return;
            }
            if (exempt) { await next(ctx); return; }
            if (access.Outcome == TenantAccessOutcome.TenantSuspended) { await Problem(ctx, 403, "tenant_suspended", "This workspace is suspended."); return; }
            if (_o.SignOutOnAccessLoss && !HttpMethods.IsPost(ctx.Request.Method)) { await ctx.SignOutAsync(IdentityConstants.ApplicationScheme); ctx.Response.Redirect(_o.LoginPath); return; }
            await Problem(ctx, 403, "tenant_access_denied", "You do not have access to this workspace.");
            return;
        }

        // A client-sent tenant header is never trusted: it may only repeat what the token already says.
        if (ctx.Request.Headers.TryGetValue(HeaderName, out var header) && header.Any(h => !Guid.TryParse(h, out var g) || g != tenantId))
        {
            await Problem(ctx, 400, "tenant_mismatch", $"{HeaderName} does not match the authorized tenant.");
            return;
        }

        accessor.SetTenant(tenantId);
        ctx.Items[TenantNameItem] = access.TenantName;
        ctx.Items[PlatformAccessItem] = access.ViaPlatformAccess;
        using (LogContext.PushProperty("TenantId", tenantId))
            await next(ctx);
    }

    private async Task NoUsableTenant(HttpContext ctx, string code, string detail)
    {
        if (_o.NoTenantRedirectPath is { } p && HttpMethods.IsGet(ctx.Request.Method)) { ctx.Response.Redirect(p); return; }
        await Problem(ctx, 403, code, detail);
    }

    private static Task Problem(HttpContext ctx, int status, string code, string detail)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status, Title = code, Detail = detail,
            Extensions = { ["code"] = code, ["correlationId"] = ctx.Items[CorrelationIds.LogProperty]?.ToString() }
        });
    }
}

/// <summary>
/// Rejects any request that names a tenant other than the authorized one (route, query, form or bound body model).
/// The authorized tenant always comes from <see cref="ITenantContext"/>; client-supplied values may only agree with it.
/// </summary>
public sealed class RejectSpoofedTenantFilter(ITenantContext tenant) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (tenant.TenantId is { } current)
        {
            var http = context.HttpContext;
            var claimed = new List<string?>();
            if (context.RouteData.Values.TryGetValue("tenantId", out var route)) claimed.Add(route?.ToString());
            foreach (var kv in http.Request.Query) if (kv.Key.Equals("tenantId", StringComparison.OrdinalIgnoreCase)) claimed.AddRange(kv.Value);
            if (http.Request.HasFormContentType)
                foreach (var kv in http.Request.Form) if (kv.Key.Equals("tenantId", StringComparison.OrdinalIgnoreCase)) claimed.AddRange(kv.Value);
            foreach (var arg in context.ActionArguments.Values)
                if (arg is ITenantScopedRequest { TenantId: { } t }) claimed.Add(t.ToString());

            if (claimed.Any(c => !Guid.TryParse(c, out var g) || g != current))
            {
                context.Result = new BadRequestObjectResult(new ProblemDetails
                {
                    Status = 400, Title = "tenant_mismatch", Detail = "The request names a tenant other than the authorized one.",
                    Extensions = { ["code"] = "tenant_mismatch" }
                });
                return;
            }
        }
        await next();
    }
}

public sealed record PermissionRequirement(string Code) : IAuthorizationRequirement;

/// <summary>Turns a permission code used as policy name ([Authorize(Policy = "Users.Manage")]) into a requirement. Unknown names are rejected.</summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (PermissionCatalog.Exists(policyName))
            return new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(policyName)).Build();
        return await base.GetPolicyAsync(policyName);
    }
}

public sealed class PermissionAuthorizationHandler(IPermissionEvaluator evaluator, ITenantContext tenant) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true || tenant.TenantId is null) return; // no tenant → fail closed
        if (await evaluator.HasAsync(requirement.Code)) context.Succeed(requirement);
    }
}
