using LeadPilot.Application.Discovery;
using LeadPilot.Domain.Discovery;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Discovery.Services;

public sealed record EffectiveConnectorSettings(bool Enabled, bool HasKey, int CallLimit, decimal? Budget, decimal CostPerCall, DiscoveryConnectorConfig? Config);

/// <summary>Per-tenant connector settings and the monthly usage ledger. Every query carries the active tenant; credentials of one tenant can never be read for another.</summary>
public sealed class DiscoveryUsage(CrmContext crm, IOptions<DiscoveryOptions> options)
{
    public async Task<EffectiveConnectorSettings> SettingsAsync(string connectorType, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var cfg = await crm.Db.DiscoveryConnectorConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.ConnectorType == connectorType, ct);
        var o = options.Value;
        return new EffectiveConnectorSettings(cfg?.IsEnabled ?? true, cfg?.ProtectedApiKey is not null, cfg?.MonthlyCallLimit ?? o.DefaultMonthlyCallLimit, cfg?.MonthlyBudgetUsd, cfg?.CostPerCallUsd ?? o.DefaultCostPerCallUsd, cfg);
    }

    public async Task<(int Calls, decimal Cost)> UsedAsync(string connectorType, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var period = DiscoveryUsageEntry.PeriodOf(crm.Now);
        var q = crm.Db.DiscoveryUsageEntries.AsNoTracking().Where(u => u.TenantId == tenantId && u.ConnectorType == connectorType && u.PeriodKey == period);
        return (await q.SumAsync(u => (int?)u.Calls, ct) ?? 0, await q.SumAsync(u => (decimal?)u.EstimatedCostUsd, ct) ?? 0m);
    }

    public void Record(Guid? jobId, string connectorType, ConnectorCall call) =>
        crm.Db.DiscoveryUsageEntries.Add(DiscoveryUsageEntry.Create(crm.TenantId, jobId, connectorType, call.Operation, call.Calls, call.EstimatedCostUsd, call.HttpStatus, crm.Now));

    public async Task<UsageDecision> CheckAsync(string connectorType, CancellationToken ct)
    {
        var s = await SettingsAsync(connectorType, ct); var used = await UsedAsync(connectorType, ct);
        return UsageLimitPolicy.Check(used.Calls, used.Cost, s.CallLimit, s.Budget, 1, s.CostPerCall);
    }
}
