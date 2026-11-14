using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LeadPilot.Api.Controllers;

[Route("api/v1/auth")]
public class AuthController(IAuthTokenService tokens, ITenantSwitchService switcher, ICurrentUser me, ITenantContext tenant) : ApiControllerBase
{
    /// <summary>Signs in and returns a short-lived access token (claims: sub, tid) and a rotating refresh token.</summary>
    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("auth")]
    [ProducesResponseType<AuthTokens>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Login(LoginRequest request, CancellationToken ct) => Respond(await tokens.LoginAsync(request, ct));

    /// <summary>Exchanges a refresh token for a new pair. Re-checks membership and tenant status; reuse of a rotated token revokes all sessions.</summary>
    [HttpPost("refresh"), AllowAnonymous, EnableRateLimiting("auth")]
    [ProducesResponseType<AuthTokens>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Refresh(RefreshRequest request, CancellationToken ct) => Respond(await tokens.RefreshAsync(request.RefreshToken, ct));

    [HttpPost("logout"), AllowAnonymous, EnableRateLimiting("auth")]
    public async Task<IActionResult> Logout(RefreshRequest request, CancellationToken ct) => Respond(await tokens.RevokeAsync(request.RefreshToken, ct));

    /// <summary>Switches the active tenant (members: their own tenants; platform users: any active tenant). Audited. Returns tokens bound to the new tenant.</summary>
    [HttpPost("switch-tenant"), Authorize, EnableRateLimiting("auth")]
    [ProducesResponseType<AuthTokens>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SwitchTenant(SwitchTenantRequest request, CancellationToken ct)
    {
        var userId = me.UserId!.Value;
        var switched = await switcher.SwitchAsync(userId, request.TenantId, tenant.TenantId, ct);
        if (switched.IsFailure) return Fail(switched);
        return Respond(await tokens.IssueForTenantAsync(userId, request.TenantId, ct));
    }
}
