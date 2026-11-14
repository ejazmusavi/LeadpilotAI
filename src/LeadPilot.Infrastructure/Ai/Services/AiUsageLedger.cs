using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Ai;
using LeadPilot.Application.Billing;
using LeadPilot.Domain.Billing;
using LeadPilot.Domain.Ai;
using LeadPilot.Infrastructure.Ai.Providers;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Ai.Services;

/// <summary>Appends provider calls to the tenant's AI usage ledger and enforces the monthly token limit and budget BEFORE a call is made.</summary>
public sealed class AiUsageLedger(IEntitlementService entitlements, AppDbContext db, ITenantContext tenant, IClock clock, IOptions<AiOptions> options, AiCredentials credentials, ILogger<AiUsageLedger> logger)
{
    /// <summary>
    /// Limits BEFORE a call: the workspace's own AI settings (token limit, budget) AND the plan (feature 'ai' + the plan's token allowance, with a hold of <paramref name="estimatedTokens"/> so concurrent calls
    /// cannot both spend the last tokens). Dispose the returned hold after the usage was recorded.
    /// </summary>
    public async Task<IAsyncDisposable> EnsureWithinLimitsAsync(long estimatedTokens, CancellationToken ct)
    {
        await EnsureWorkspaceLimitsAsync(ct);
        if (await entitlements.RequireFeatureAsync(Features.Ai, ct) is { IsFailure: true } noFeature) throw new AiProviderException(AiFailureKind.LimitReached, noFeature.Error.Message, retryable: false);
        var hold = await entitlements.HoldAsync(UsageDimension.MonthlyAiTokens, estimatedTokens, ct);
        if (hold.IsFailure) throw new AiProviderException(AiFailureKind.LimitReached, hold.Error.Message, retryable: false);
        return hold.Value;
    }

    private async Task EnsureWorkspaceLimitsAsync(CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var settings = await credentials.SettingsAsync(ct);
        var limit = EffectiveTokenLimit(settings, options.Value);
        var budget = settings?.MonthlyBudgetUsd;
        if (limit == 0 && budget is null) return;
        var period = AiUsageEntry.PeriodOf(clock.UtcNow);
        var totals = await db.AiUsageEntries.AsNoTracking().Where(e => e.TenantId == tenantId && e.PeriodKey == period)
            .GroupBy(_ => 1).Select(g => new { Tokens = g.Sum(e => (long)e.InputTokens + e.OutputTokens), Cost = g.Sum(e => e.EstimatedCostUsd) }).FirstOrDefaultAsync(ct);
        if (limit > 0 && (totals?.Tokens ?? 0) >= limit) throw new AiProviderException(AiFailureKind.LimitReached, "The monthly AI token limit of this workspace is reached.", retryable: false);
        if (budget is { } b && (totals?.Cost ?? 0) >= b) throw new AiProviderException(AiFailureKind.LimitReached, "The monthly AI budget of this workspace is reached.", retryable: false);
    }

    /// <summary>A cheap upper estimate used only for the concurrency hold: output cap plus about four characters per input token.</summary>
    public static long Estimate(AiCompletionRequest r) => r.MaxTokens + (r.StableSystem.Length + (r.DynamicSystem?.Length ?? 0) + r.Messages.Sum(m => (long)m.Content.Length)) / 4 + 1;

    public static long EffectiveTokenLimit(AiTenantSettings? settings, AiOptions o) => settings?.MonthlyTokenLimit ?? o.DefaultMonthlyTokenLimit;

    public async Task RecordAsync(string operation, string provider, string model, AiUsage usage, bool success, string? errorCode, int latencyMs, Guid? conversationId, CancellationToken ct)
    {
        try
        {
            var price = options.Value.Prices.TryGetValue(model, out var p) ? p : null;
            var cost = price?.Estimate(usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens) ?? 0m;
            db.AiUsageEntries.Add(AiUsageEntry.Create(tenant.RequireTenantId(), operation, provider, model, usage.InputTokens, usage.OutputTokens, usage.CacheReadTokens, usage.CacheWriteTokens, cost, success, errorCode, latencyMs, conversationId, clock.UtcNow));
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "AI usage could not be recorded");   // never fail a customer reply because the ledger write failed
        }
    }
}

/// <summary>Decorator: limits before, ledger after. Everything in the application talks to this, never to the raw adapter.</summary>
public sealed class MeteredAIProvider(IRawAIProvider inner, AiUsageLedger ledger) : IAIProvider
{
    public string Name => inner.Name;

    public async Task<AiCompletion> CompleteAsync(AiCompletionRequest request, CancellationToken ct)
    {
        await using var hold = await ledger.EnsureWithinLimitsAsync(AiUsageLedger.Estimate(request), ct);
        try
        {
            var result = await inner.CompleteAsync(request, ct);
            await ledger.RecordAsync(request.Operation, inner.Name, result.Model, result.Usage, true, null, result.LatencyMs, request.ConversationId, ct);
            return result;
        }
        catch (AiProviderException ex) when (ex.Kind is not (AiFailureKind.NotConfigured or AiFailureKind.LimitReached or AiFailureKind.CircuitOpen))
        {
            await ledger.RecordAsync(request.Operation, inner.Name, request.Model, new AiUsage(0, 0, 0, 0), false, ex.Kind.ToString(), 0, request.ConversationId, ct);
            throw;
        }
    }
}

public sealed class MeteredEmbeddingProvider(IRawEmbeddingProvider inner, AiUsageLedger ledger, AiCredentials credentials) : IEmbeddingProvider
{
    public string Name => inner.Name;

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, EmbeddingInputType type, CancellationToken ct)
    {
        await using var hold = await ledger.EnsureWithinLimitsAsync(texts.Sum(t => (long)t.Length / 4 + 1), ct);
        try
        {
            var result = await inner.EmbedAsync(texts, type, ct);
            await ledger.RecordAsync("embed", inner.Name, result.Model, new AiUsage(result.TotalTokens, 0, 0, 0), true, null, 0, null, ct);
            return result;
        }
        catch (AiProviderException ex) when (ex.Kind is not (AiFailureKind.NotConfigured or AiFailureKind.LimitReached or AiFailureKind.CircuitOpen))
        {
            await ledger.RecordAsync("embed", inner.Name, (await credentials.SettingsAsync(ct))?.EmbeddingModel ?? "unknown", new AiUsage(0, 0, 0, 0), false, ex.Kind.ToString(), 0, null, ct);
            throw;
        }
    }
}
