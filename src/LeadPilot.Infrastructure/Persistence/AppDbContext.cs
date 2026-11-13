using LeadPilot.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace LeadPilot.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options)
{
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(b =>
        {
            b.Property(u => u.DisplayName).HasMaxLength(200).IsRequired();
            b.Property(u => u.CreatedAtUtc).HasPrecision(6);
            b.Property(u => u.LastLoginAtUtc).HasPrecision(6);
        });
    }
}

public static class DatabaseInitializer
{
    /// <summary>Applies pending migrations when Database:MigrateOnStartup is true (Development/docker). Production uses an explicit migration step.</summary>
    public static async Task MigrateIfConfiguredAsync(IServiceProvider services, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        if (!config.GetValue<bool>("Database:MigrateOnStartup")) return;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }
}
