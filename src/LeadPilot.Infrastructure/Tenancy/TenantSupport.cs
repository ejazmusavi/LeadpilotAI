using System.Security.Cryptography;
using System.Text;
using LeadPilot.Application.Abstractions;
using LeadPilot.Domain.Common;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.Infrastructure.Tenancy;

/// <summary>Opaque random tokens (invitations, refresh tokens): only the SHA-256 hash is ever stored.</summary>
public static class TokenHasher
{
    public static string NewToken(int bytes = 32) => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private const string Prefix = "enc:v1:";
    private readonly IDataProtector _protector = provider.CreateProtector("LeadPilot.TenantSecrets.v1");

    public string Protect(string plaintext) => Prefix + _protector.Protect(plaintext);

    public string Unprotect(string protectedValue)
    {
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal)) throw new InvalidOperationException("Value is not a protected secret.");
        return _protector.Unprotect(protectedValue[Prefix.Length..]);
    }
}

/// <summary>
/// Runs background-job / consumer code inside a validated tenant scope.
/// An empty id, an unknown tenant or a suspended tenant throws: jobs fail closed instead of running unscoped.
/// </summary>
public sealed class TenantScopeRunner(IServiceScopeFactory scopes) : ITenantScopeRunner
{
    public async Task RunAsync(Guid tenantId, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) throw new TenantContextMissingException("A background job must carry a TenantId.");

        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var tenant = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => new { t.Status }).FirstOrDefaultAsync(ct)
                     ?? throw new TenantUnavailableException($"Tenant {tenantId} does not exist.");
        if (tenant.Status != LeadPilot.Domain.Tenancy.TenantStatus.Active)
            throw new TenantUnavailableException($"Tenant {tenantId} is not active.");

        sp.GetRequiredService<ITenantContextAccessor>().SetTenant(tenantId);
        await work(sp, ct);
    }

    public async Task RunForOperatorAsync(Guid tenantId, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty) throw new TenantContextMissingException("An operator action must name a tenant.");
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        if (!await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId, ct)) throw new TenantUnavailableException($"Tenant {tenantId} does not exist.");
        sp.GetRequiredService<ITenantContextAccessor>().SetTenant(tenantId);
        await work(sp, ct);
    }

    public async Task RunPlatformJobAsync(Func<IServiceProvider, CancellationToken, Task> work, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        using var _ = scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().BeginPlatformScope();
        await work(scope.ServiceProvider, ct);
    }
}
