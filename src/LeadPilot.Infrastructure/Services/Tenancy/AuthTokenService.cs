using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Identity;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Tenancy;
using LeadPilot.Shared.Results;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace LeadPilot.Infrastructure.Services.Tenancy;

public sealed class JwtSettings
{
    public const string DevelopmentOnlyKey = "development-only-signing-key-do-not-use-in-production-0123456789";
    public string Issuer { get; init; } = "leadpilot";
    public string Audience { get; init; } = "leadpilot-api";
    public string SigningKey { get; init; } = string.Empty;
    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 14;

    /// <summary>Jwt:SigningKey (>= 32 chars) is mandatory outside Development; Development falls back to a fixed, clearly-labelled dev key.</summary>
    public static JwtSettings Resolve(IConfiguration config, IHostEnvironment env)
    {
        var key = config["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
        {
            if (!env.IsDevelopment())
                throw new InvalidOperationException("Jwt:SigningKey must be configured (at least 32 characters) outside Development.");
            key = DevelopmentOnlyKey;
        }
        return new JwtSettings
        {
            SigningKey = key,
            Issuer = config["Jwt:Issuer"] ?? "leadpilot",
            Audience = config["Jwt:Audience"] ?? "leadpilot-api",
            AccessTokenMinutes = Math.Clamp(config.GetValue("Jwt:AccessTokenMinutes", 15), 1, 120),
            RefreshTokenDays = Math.Clamp(config.GetValue("Jwt:RefreshTokenDays", 14), 1, 90),
        };
    }

    public SymmetricSecurityKey Key => new(Encoding.UTF8.GetBytes(SigningKey));
}

public sealed class AuthTokenService(
    AppDbContext db, UserManager<ApplicationUser> users, IMembershipResolver membership, IClock clock, JwtSettings jwt,
    IRequestInfo request, ITenantContextAccessor accessor, IAuditService audit) : IAuthTokenService
{
    private static readonly Error InvalidCredentials = new("invalid_credentials", "Invalid e-mail or password.");

    public async Task<Result<AuthTokens>> LoginAsync(LoginRequest login, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(login.Email) || string.IsNullOrEmpty(login.Password)) return Result.Failure<AuthTokens>(InvalidCredentials);
        var user = await users.FindByEmailAsync(login.Email.Trim());
        if (user is null || !user.IsActive)
        {
            await users.CheckPasswordAsync(new ApplicationUser(), login.Password).ConfigureAwait(false); // comparable work: no account enumeration by timing
            return Result.Failure<AuthTokens>(InvalidCredentials);
        }
        if (await users.IsLockedOutAsync(user)) return Result.Failure<AuthTokens>(new Error("locked_out", "The account is temporarily locked. Try again later."));
        if (!await users.CheckPasswordAsync(user, login.Password))
        {
            await users.AccessFailedAsync(user);
            return Result.Failure<AuthTokens>(InvalidCredentials);
        }
        await users.ResetAccessFailedCountAsync(user);

        var tenantId = login.TenantId ?? await membership.GetDefaultTenantIdAsync(user.Id, ct);
        if (tenantId is null) return Result.Failure<AuthTokens>(Error.Forbidden("This account does not belong to an active workspace."));
        var access = await membership.ResolveAsync(user.Id, tenantId.Value, ct);
        if (!access.IsGranted) return Result.Failure<AuthTokens>(Denied(access));

        user.LastLoginAtUtc = clock.UtcNow;
        await users.UpdateAsync(user);
        // Signing straight into a tenant via platform access is a tenant switch like any other: it must leave an audit trail.
        if (access.ViaPlatformAccess)
            await audit.RecordAndSaveAsync(new AuditEntry("Tenant.Switched", "Tenant", tenantId.Value.ToString(),
                $"{user.Email} signed into '{access.TenantName}' using platform access.", tenantId.Value, user.Id, user.Email), ct);
        return Result.Success(await IssueAsync(user, tenantId.Value, ct));
    }

    public async Task<Result<AuthTokens>> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var invalid = Result.Failure<AuthTokens>(new Error("invalid_refresh_token", "The refresh token is invalid or expired."));
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 200) return invalid;
        var hash = TokenHasher.Hash(refreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null) return invalid;
        var now = clock.UtcNow;

        if (token.RevokedAtUtc is not null)
        {
            // A rotated/revoked token was presented again: assume theft and kill every session of the user.
            foreach (var t in await db.RefreshTokens.Where(t => t.UserId == token.UserId && t.RevokedAtUtc == null).ToListAsync(ct)) t.Revoke(now);
            using (accessor.BeginPlatformScope())
                audit.Record(new AuditEntry("Auth.RefreshTokenReuse", "User", token.UserId.ToString(), "A revoked refresh token was reused; all sessions of the user were revoked.", token.TenantId, token.UserId));
            using (accessor.BeginPlatformScope()) await db.SaveChangesAsync(ct);
            return invalid;
        }
        if (!token.IsActive(now)) return invalid;

        var user = await users.FindByIdAsync(token.UserId.ToString());
        if (user is null || !user.IsActive) return invalid;
        var access = await membership.ResolveAsync(user.Id, token.TenantId, ct);
        if (!access.IsGranted) return Result.Failure<AuthTokens>(Denied(access));

        var tokens = await IssueAsync(user, token.TenantId, ct, save: false);
        token.Revoke(now, TokenHasher.Hash(tokens.RefreshToken));
        await db.SaveChangesAsync(ct);
        return Result.Success(tokens);
    }

    public async Task<Result> RevokeAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 200) return Result.Success();
        var hash = TokenHasher.Hash(refreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null) return Result.Success(); // idempotent, reveals nothing
        token.Revoke(clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<AuthTokens>> IssueForTenantAsync(Guid userId, Guid tenantId, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive) return Result.Failure<AuthTokens>(Error.Forbidden());
        var access = await membership.ResolveAsync(userId, tenantId, ct);
        return access.IsGranted ? Result.Success(await IssueAsync(user, tenantId, ct)) : Result.Failure<AuthTokens>(Denied(access));
    }

    private static Error Denied(TenantAccessResult a) => a.Outcome switch
    {
        TenantAccessOutcome.TenantSuspended => new Error("tenant_suspended", "This workspace is suspended."),
        TenantAccessOutcome.UserInactive => Error.Forbidden("This account is disabled."),
        _ => Error.Forbidden("You do not have access to that workspace."),
    };

    private async Task<AuthTokens> IssueAsync(ApplicationUser user, Guid tenantId, CancellationToken ct, bool save = true)
    {
        var now = clock.UtcNow;
        var expires = now.AddMinutes(jwt.AccessTokenMinutes);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer, Audience = jwt.Audience, IssuedAt = now, NotBefore = now, Expires = expires,
            SigningCredentials = new SigningCredentials(jwt.Key, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = user.Id.ToString(), ["email"] = user.Email ?? "", ["name"] = user.DisplayName,
                [TenantClaims.TenantId] = tenantId.ToString(), ["jti"] = Guid.NewGuid().ToString("N"),
            },
        };
        var accessToken = new JsonWebTokenHandler().CreateToken(descriptor);

        var raw = TokenHasher.NewToken(48);
        db.RefreshTokens.Add(RefreshToken.Create(user.Id, tenantId, TokenHasher.Hash(raw), now.AddDays(jwt.RefreshTokenDays), request.IpAddress, now));
        if (save) await db.SaveChangesAsync(ct);
        return new AuthTokens(accessToken, jwt.AccessTokenMinutes * 60, raw, tenantId);
    }
}
