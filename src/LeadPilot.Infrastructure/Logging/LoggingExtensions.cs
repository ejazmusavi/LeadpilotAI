using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Formatting.Compact;

namespace LeadPilot.Infrastructure.Logging;

public static class LoggingExtensions
{
    /// <summary>Structured logging: readable console in Development, compact JSON elsewhere. Enriched with app name + context properties.</summary>
    public static IHostBuilder UseLeadPilotSerilog(this IHostBuilder host, string appName) =>
        host.UseSerilog((ctx, services, cfg) =>
        {
            cfg.ReadFrom.Configuration(ctx.Configuration)
               .Enrich.FromLogContext()
               .Enrich.WithProperty("Application", appName);
            if (ctx.HostingEnvironment.IsDevelopment()) cfg.WriteTo.Console();
            else cfg.WriteTo.Console(new RenderedCompactJsonFormatter());
        });
}

public static class RequestLoggingExtensions
{
    public static Microsoft.AspNetCore.Builder.IApplicationBuilder UseSerilogRequestLoggingSafe(this Microsoft.AspNetCore.Builder.IApplicationBuilder app) =>
        Serilog.SerilogApplicationBuilderExtensions.UseSerilogRequestLogging(app);
}

public static class WorkerLoggingExtensions
{
    /// <summary>Serilog for generic hosts (Worker).</summary>
    public static IServiceCollection AddSerilog(this IServiceCollection services, IConfiguration config, IHostEnvironment env, string appName)
    {
        var cfg = new LoggerConfiguration().ReadFrom.Configuration(config).Enrich.FromLogContext().Enrich.WithProperty("Application", appName);
        if (env.IsDevelopment()) cfg.WriteTo.Console(); else cfg.WriteTo.Console(new RenderedCompactJsonFormatter());
        Log.Logger = cfg.CreateLogger();
        return services.AddLogging(b => b.ClearProviders().AddSerilog(Log.Logger, dispose: true));
    }
}
