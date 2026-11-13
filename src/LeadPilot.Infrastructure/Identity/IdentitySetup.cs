using System.Security.Claims;
using LeadPilot.Application.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Identity;

public static class TenantClaims
{
    /// <summary>Active-tenant claim (cookie and JWT). Always re-validated against membership on every request.</summary>
    public const string TenantId = "tid";
}

public static class IdentitySetup
{
    /// <summary>Password, lockout and user rules shared by the Web and Api hosts.</summary>
    public static void Configure(IdentityOptions o)
    {
        o.Password.RequiredLength = 10;
        o.Password.RequireDigit = true;
        o.Password.RequireLowercase = true;
        o.Password.RequireUppercase = true;
        o.Password.RequireNonAlphanumeric = true;
        o.User.RequireUniqueEmail = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.Lockout.AllowedForNewUsers = true;
        o.Tokens.PasswordResetTokenProvider = TokenOptions.DefaultProvider;
    }
}

/// <summary>Adds the user's default active tenant to the sign-in principal. Switching replaces this claim (see TenantSwitching).</summary>
public sealed class TenantClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<ApplicationRole> roleManager,
    IOptions<IdentityOptions> options,
    IMembershipResolver membership) : UserClaimsPrincipalFactory<ApplicationUser, ApplicationRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        var tid = await membership.GetDefaultTenantIdAsync(user.Id);
        if (tid is not null) identity.AddClaim(new Claim(TenantClaims.TenantId, tid.Value.ToString()));
        return identity;
    }
}

public static class PrincipalExtensions
{
    /// <summary>Returns a copy of the principal's identity with the active-tenant claim replaced.</summary>
    public static void ReplaceTenantClaim(this ClaimsIdentity identity, Guid tenantId)
    {
        foreach (var c in identity.FindAll(TenantClaims.TenantId).ToList()) identity.RemoveClaim(c);
        identity.AddClaim(new Claim(TenantClaims.TenantId, tenantId.ToString()));
    }
}
