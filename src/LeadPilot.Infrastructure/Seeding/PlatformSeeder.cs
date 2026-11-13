using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Identity;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Seeding;

/// <summary>
/// Idempotent startup seeding: permission catalog, platform tenant, system roles of every tenant (re-synced so new permissions reach them),
/// and the first platform owner from Seed:AdminEmail / Seed:AdminPassword (user-secrets or environment; remove after first start).
/// </summary>
public static class PlatformSeeder
{
    public static async Task SeedAsync(IServiceProvider services, IConfiguration config, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var clock = sp.GetRequiredService<IClock>();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("PlatformSeeder");
        using var _ = sp.GetRequiredService<ITenantContextAccessor>().BeginPlatformScope();
        var now = clock.UtcNow;

        var permissions = await RoleProvisioner.SyncPermissionsAsync(db, ct);
        var platform = await db.Tenants.FirstOrDefaultAsync(t => t.IsPlatform, ct);
        if (platform is null)
        {
            platform = Tenant.Create("LeadPilot Platform", Tenant.PlatformSlug, "Platform", "UTC", "en", now, isPlatform: true);
            db.Tenants.Add(platform);
            logger.LogInformation("Platform tenant created");
        }
        await db.SaveChangesAsync(ct);

        foreach (var tenant in await db.Tenants.ToListAsync(ct))
            await RoleProvisioner.SyncSystemRolesAsync(db, tenant, permissions, now, ct);
        await db.SaveChangesAsync(ct);

        await EnsurePlatformOwnerAsync(db, users, config, platform, now, logger, ct);
    }

    private static async Task EnsurePlatformOwnerAsync(AppDbContext db, UserManager<ApplicationUser> users, IConfiguration config, Tenant platform, DateTime now, ILogger logger, CancellationToken ct)
    {
        var email = config["Seed:AdminEmail"];
        var password = config["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email)) return;

        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            // Only the very first account may be created from seed settings.
            if (string.IsNullOrWhiteSpace(password) || await users.Users.AnyAsync(ct)) return;
            user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Administrator", CreatedAtUtc = now };
            var result = await users.CreateAsync(user, password);
            if (!result.Succeeded)
            {
                logger.LogError("Could not create initial administrator: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
                return;
            }
            logger.LogWarning("Initial platform owner {Email} created from Seed settings. Remove Seed:AdminPassword from configuration.", email);
        }
        else if (await db.UserTenants.IgnoreQueryFilters().AnyAsync(m => m.UserId == user.Id, ct)) return; // already placed somewhere: never silently escalate

        var owner = await db.TenantRoles.IgnoreQueryFilters().FirstAsync(r => r.TenantId == platform.Id && r.Name == SystemRoles.PlatformOwner, ct);
        db.UserTenants.Add(UserTenant.Create(platform.Id, user.Id, isDefault: true, invitedBy: null, now));
        db.TenantUserRoles.Add(UserRole.Create(platform.Id, user.Id, owner.Id, assignedBy: null, now));
        if (!await db.UserProfiles.AnyAsync(p => p.UserId == user.Id, ct)) db.UserProfiles.Add(UserProfile.Create(user.Id, user.DisplayName, null, now));
        db.AuditLogs.Add(AuditLog.Create(platform.Id, null, null, null, "Platform.OwnerSeeded", "User", user.Id.ToString(), $"{email} placed in the platform tenant as Platform Owner.", null, null, now));
        await db.SaveChangesAsync(ct);
    }
}
