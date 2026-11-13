using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Worker;

/// <summary>
/// Phase 1 host: verifies database connectivity at start and then idles.
/// Hangfire, RabbitMQ consumers and job handlers are added in the phases that need them
/// (discovery, messaging, campaigns) — no job logic exists yet by design.
/// </summary>
public sealed class Worker(IServiceScopeFactory scopes, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ok = await db.Database.CanConnectAsync(stoppingToken);
            if (ok) logger.LogInformation("Worker started; database connection OK");
            else logger.LogError("Worker started but the database is unreachable");
        }
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { });
    }
}
