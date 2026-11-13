using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LeadPilot.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef`. Connection comes from LEADPILOT_CONNECTION or a local dev default.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("LEADPILOT_CONNECTION")
                 ?? "Server=localhost;Port=3306;Database=leadpilot_dev;User=leadpilot;Password=leadpilot_dev;";
        var options = new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs).Options;
        return new AppDbContext(options, new TenantContext());
    }
}
