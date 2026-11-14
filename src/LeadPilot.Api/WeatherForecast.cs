namespace LeadPilot.Api;

/// <summary>Response for GET /api/v1/system/info.</summary>
public sealed record SystemInfoResponse(string Name, string Version, string Environment, DateTime ServerTimeUtc);
