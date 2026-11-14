namespace LeadPilot.Shared;

public static class CorrelationIds
{
    public const string HeaderName = "X-Correlation-ID";
    public const string LogProperty = "CorrelationId";
    public static string New() => Guid.NewGuid().ToString("N");
}
