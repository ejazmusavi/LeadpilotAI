using LeadPilot.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace LeadPilot.Web.Services;

/// <summary>
/// Creates the very first administrator account only when the user table is empty AND
/// Seed:AdminEmail / Seed:AdminPassword are supplied (user-secrets or environment variables).
/// Remove those settings after first start.
/// </summary>
public static class IdentitySeeder
{
    public static async Task SeedInitialAdminAsync(IServiceProvider services, IConfiguration config)
    {
        var email = config["Seed:AdminEmail"];
        var password = config["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;

        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("IdentitySeeder");

        if (users.Users.Any()) return;

        var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true, DisplayName = "Administrator" };
        var result = await users.CreateAsync(user, password);
        if (result.Succeeded)
            logger.LogWarning("Initial administrator {Email} created from Seed settings. Remove Seed:AdminPassword from configuration.", email);
        else
            logger.LogError("Could not create initial administrator: {Errors}", string.Join("; ", result.Errors.Select(e => e.Description)));
    }
}
