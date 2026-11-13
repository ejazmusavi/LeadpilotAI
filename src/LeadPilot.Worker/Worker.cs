using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Worker;

/// <summary>
/// Verifies database connectivity at start and registers the recurring jobs. Job processing itself is done by the Hangfire server
/// (discovery searches, retention). RabbitMQ consumers are added in the phases that need durable events (messaging).
/// </summary>
public sealed class Worker(IServiceScopeFactory scopes, Hangfire.IRecurringJobManager recurring, ILogger<Worker> logger) : BackgroundService
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
        LeadPilot.Infrastructure.Discovery.Jobs.HangfireSetup.ScheduleRecurring(recurring);
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { });
    }
}
