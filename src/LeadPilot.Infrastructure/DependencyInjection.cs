using LeadPilot.Application.Abstractions;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LeadPilot.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionName = "Default";

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var cs = config.GetConnectionString(ConnectionName)
                 ?? throw new InvalidOperationException($"Connection string '{ConnectionName}' is not configured.");

        services.AddDbContext<AppDbContext>(o => o.UseMySQL(cs));
        services.AddSingleton<IClock, SystemClock>();

        services.Configure<SmtpOptions>(config.GetSection(SmtpOptions.Section));
        if (env.IsDevelopment() && string.IsNullOrWhiteSpace(config[$"{SmtpOptions.Section}:Host"]))
            services.AddSingleton<IEmailSender, DevelopmentLogEmailSender>();
        else
            services.AddSingleton<IEmailSender, SmtpEmailSender>();

        services.AddHealthChecks().AddDbContextCheck<AppDbContext>("mysql", tags: ["ready"]);
        return services;
    }
}
