using LeadPilot.Application.Abstractions;

namespace LeadPilot.Infrastructure.Services;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
