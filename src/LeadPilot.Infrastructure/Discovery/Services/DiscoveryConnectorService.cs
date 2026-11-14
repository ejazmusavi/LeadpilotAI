using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Common;
using LeadPilot.Application.Discovery;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Discovery;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Discovery.Services;

public sealed class DiscoveryConnectorService(CrmContext crm, IDiscoveryConnectorRegistry registry, DiscoveryUsage usage, ISecretProtector secrets, ILogger<DiscoveryConnectorService> logger) : IDiscoveryConnectorService
{
    public async Task<IReadOnlyList<ConnectorStatusDto>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<ConnectorStatusDto>();
        foreach (var d in registry.All) list.Add(await StatusAsync(d, ct));
        return list;
    }

    private async Task<ConnectorStatusDto> StatusAsync(ConnectorDescriptor d, CancellationToken ct)
    {
        var s = await usage.SettingsAsync(d.Key, ct); var used = await usage.UsedAsync(d.Key, ct);
        var ready = d.Kind == ConnectorKind.Search && s.Enabled && (!d.RequiresCredentials || s.HasKey);
        var text = d.Kind switch
        {
            ConnectorKind.InApp => "Available in the app",
            ConnectorKind.ExtensionPoint => "Not configured",
            _ when !s.Enabled => "Disabled",
            _ when d.RequiresCredentials && !s.HasKey => "Needs an API key",
            _ => "Ready",
        };
        return new ConnectorStatusDto(d.Key, d.DisplayName, d.Description, d.Kind, d.RequiresCredentials, d.AcceptsSeedUrls, d.IsTestOnly, d.InAppPath, d.NotConfiguredReason, d.Attribution, s.Enabled, s.HasKey,
            s.Config?.KeyHint, ready, text, s.Config?.MonthlyCallLimit, s.CallLimit, s.Config?.MonthlyBudgetUsd, s.Config?.CostPerCallUsd, s.CostPerCall, used.Calls, used.Cost);
    }

    public Task<Result<ConnectorStatusDto>> SaveSettingsAsync(string connectorType, SaveConnectorSettingsRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var descriptor = registry.All.FirstOrDefault(d => d.Key == connectorType);
            if (descriptor is null) return Result.Failure<ConnectorStatusDto>(Error.NotFound("Connector"));
            if (descriptor.Kind != ConnectorKind.Search) return Result.Failure<ConnectorStatusDto>(Error.Validation($"{descriptor.DisplayName} has no settings: it is {(descriptor.Kind == ConnectorKind.InApp ? "handled inside the app" : "not configured")}."));
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            var cfg = await db.DiscoveryConnectorConfigs.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.ConnectorType == connectorType, ct);
            if (cfg is null) { cfg = DiscoveryConnectorConfig.Create(tenantId, connectorType, now); db.DiscoveryConnectorConfigs.Add(cfg); }

            var actions = new List<string>();
            if (r.ClearApiKey) { cfg.ClearKey(now); actions.Add("key removed"); }
            else if (!string.IsNullOrWhiteSpace(r.ApiKey))
            {
                if (!descriptor.RequiresCredentials) return Result.Failure<ConnectorStatusDto>(Error.Validation($"{descriptor.DisplayName} does not use an API key."));
                var key = r.ApiKey.Trim();
                if (key.Length is < 10 or > 300 || key.Any(char.IsWhiteSpace)) return Result.Failure<ConnectorStatusDto>(Error.Validation("That does not look like an API key (10–300 characters, no spaces)."));
                cfg.SetKey(secrets.Protect(key), "••••" + key[^4..], now); actions.Add("key saved");
            }
            if (r.Enabled is { } en) { cfg.SetEnabled(en, now); actions.Add(en ? "enabled" : "disabled"); }
            if (r.UpdateLimits) { cfg.SetLimits(r.MonthlyCallLimit, r.MonthlyBudgetUsd, r.CostPerCallUsd, now); actions.Add("limits updated"); }
            if (actions.Count == 0) return Result.Failure<ConnectorStatusDto>(Error.Validation("Nothing to change."));
            // the key itself never enters the audit trail or the logs
            crm.Record("Connector.Changed", "DiscoveryConnector", Guid.Empty, $"{descriptor.DisplayName}: {string.Join(", ", actions)}.");
            await db.SaveChangesAsync(ct);
            return Result.Success(await StatusAsync(descriptor, ct));
        }, logger);

    public async Task<UsageSummaryDto> UsageAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var (p, size) = CrmFlow.Page(page, pageSize);
        var q = crm.Db.DiscoveryUsageEntries.AsNoTracking().Where(u => u.TenantId == tenantId);
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(u => u.OccurredAtUtc).ThenByDescending(u => u.Id).Skip((p - 1) * size).Take(size)
            .Select(u => new UsageEntryDto(u.Id, u.ConnectorType, u.JobId, u.Operation, u.Calls, u.EstimatedCostUsd, u.HttpStatus, u.OccurredAtUtc)).ToListAsync(ct);
        var connectors = (await ListAsync(ct)).Where(c => c.Kind == ConnectorKind.Search).ToList();
        return new UsageSummaryDto(DiscoveryUsageEntry.PeriodOf(crm.Now), connectors, new PagedResult<UsageEntryDto>(rows, p, size, total));
    }
}
