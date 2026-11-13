using System.Text.Json;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Ai;
using LeadPilot.Application.Common;
using LeadPilot.Domain.Ai;
using LeadPilot.Infrastructure.Ai.Providers;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Ai.Services;

internal static class AiErrors
{
    public static Error From(AiProviderException ex) => ex.Kind switch
    {
        AiFailureKind.NotConfigured => new Error("ai_not_configured", ex.Message),
        AiFailureKind.LimitReached => new Error("usage_limit_reached", ex.Message),
        _ => new Error("ai_provider_error", ex.Message),
    };
}

internal sealed class AiSettingsService(CrmContext crm, AiCredentials credentials, ISecretProtector secrets, IAIProvider chat, IEmbeddingProvider embeddings, IOptions<AiOptions> options, ILogger<AiSettingsService> logger) : IAiSettingsService
{
    public static readonly string[] SuggestedModels = ["claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-5-5", "claude-fable-5-2"];

    private async Task<AiTenantSettings> EnsureAsync(CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var row = await crm.Db.AiTenantSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (row is not null) return row;
        row = AiTenantSettings.Create(tenantId, crm.Now);
        crm.Db.AiTenantSettings.Add(row);
        return row;
    }

    public async Task<AiSettingsDto> GetAsync(CancellationToken ct = default)
    {
        credentials.Invalidate();
        var s = await credentials.SettingsAsync(ct) ?? AiTenantSettings.Create(crm.TenantId, crm.Now);
        return await ToDtoAsync(s, ct);
    }

    private async Task<AiSettingsDto> ToDtoAsync(AiTenantSettings s, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var period = AiUsageEntry.PeriodOf(crm.Now);
        var totals = await crm.Db.AiUsageEntries.AsNoTracking().Where(e => e.TenantId == tenantId && e.PeriodKey == period).GroupBy(_ => 1).Select(g => new { T = g.Sum(e => (long)e.InputTokens + e.OutputTokens), C = g.Sum(e => e.EstimatedCostUsd) }).FirstOrDefaultAsync(ct);
        var needing = await crm.Db.DocumentVersions.AsNoTracking().CountAsync(v => v.TenantId == tenantId && v.Status == IndexStatus.Indexed && v.EmbeddingModel != s.EmbeddingModel, ct);
        return new AiSettingsDto(s.IsEnabled, !string.IsNullOrEmpty(s.ProtectedApiKey), s.KeyHint, !string.IsNullOrEmpty(s.ProtectedEmbeddingKey), s.EmbeddingKeyHint, s.ChatModel, s.ClassifierModel, s.SummaryModel, s.EmbeddingModel,
            s.TopK, s.MinRetrievalScore, s.MinConfidence, s.HistoryMessages, s.MaxContextChars, s.MonthlyTokenLimit, s.MonthlyBudgetUsd, s.UnsupportedReply, s.ToneInstructions, s.EscalationKeywords, options.Value.VectorStore.Provider,
            AiUsageLedger.EffectiveTokenLimit(s, options.Value), totals?.T ?? 0, totals?.C ?? 0, SuggestedModels, needing);
    }

    public Task<Result<AiSettingsDto>> UpdateAsync(UpdateAiSettingsRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var row = await EnsureAsync(ct);
            row.Configure(r.ChatModel, r.ClassifierModel, r.SummaryModel, r.EmbeddingModel, r.TopK, r.MinRetrievalScore, r.MinConfidence, r.HistoryMessages, r.MaxContextChars, r.MonthlyTokenLimit, r.MonthlyBudgetUsd,
                PromptGuard.Neutralize(r.UnsupportedReply, 500, escapeAngles: false), r.ToneInstructions is null ? null : PromptGuard.Neutralize(r.ToneInstructions, 1000, escapeAngles: false), r.EscalationKeywords, crm.Now);
            crm.Record("Ai.SettingsChanged", "AiTenantSettings", row.Id, $"AI settings changed (chat {row.ChatModel}, classifier {row.ClassifierModel}, embeddings {row.EmbeddingModel}, {row.TopK} passages, min score {row.MinRetrievalScore}, min confidence {row.MinConfidence}).");
            await crm.Db.SaveChangesAsync(ct);
            credentials.Invalidate();
            return Result.Success(await ToDtoAsync(row, ct));
        }, logger);

    public Task<Result<AiSettingsDto>> SetKeyAsync(AiKeyKind kind, SetAiKeyRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var key = r.ApiKey?.Trim() ?? string.Empty;
            if (key.Length is < 12 or > 400 || key.Any(char.IsWhiteSpace)) return Result.Failure<AiSettingsDto>(Error.Validation("That does not look like an API key (12 to 400 characters, no spaces)."));
            var row = await EnsureAsync(ct);
            var hint = "…" + key[^4..];
            if (kind == AiKeyKind.Chat) row.SetKey(secrets.Protect(key), hint, crm.Now); else row.SetEmbeddingKey(secrets.Protect(key), hint, crm.Now);
            crm.Record("Ai.KeySaved", "AiTenantSettings", row.Id, $"{(kind == AiKeyKind.Chat ? "AI provider" : "Embedding provider")} key saved (ending {hint}).");   // the key itself is never audited
            await crm.Db.SaveChangesAsync(ct);
            credentials.Invalidate();
            return Result.Success(await ToDtoAsync(row, ct));
        }, logger);

    public Task<Result<AiSettingsDto>> ClearKeyAsync(AiKeyKind kind, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var row = await EnsureAsync(ct);
            if (kind == AiKeyKind.Chat) row.ClearKey(crm.Now); else { row.ClearEmbeddingKey(crm.Now); row.SetEnabled(false, crm.Now); }
            crm.Record("Ai.KeyRemoved", "AiTenantSettings", row.Id, $"{(kind == AiKeyKind.Chat ? "AI provider" : "Embedding provider")} key removed; AI is switched off for this workspace.");
            await crm.Db.SaveChangesAsync(ct);
            credentials.Invalidate();
            return Result.Success(await ToDtoAsync(row, ct));
        }, logger);

    public Task<Result<AiSettingsDto>> SetEnabledAsync(SetAiEnabledRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var row = await EnsureAsync(ct);
            if (r.Enabled && (string.IsNullOrEmpty(row.ProtectedApiKey) || string.IsNullOrEmpty(row.ProtectedEmbeddingKey)))
                return Result.Failure<AiSettingsDto>(Error.Conflict("Save both the AI provider key and the embedding key before enabling the AI."));
            row.SetEnabled(r.Enabled, crm.Now);
            crm.Record(r.Enabled ? "Ai.Enabled" : "Ai.Disabled", "AiTenantSettings", row.Id, r.Enabled ? "AI enabled: conversations in AI mode now receive suggested replies for staff (nothing is sent automatically)." : "AI disabled.");
            await crm.Db.SaveChangesAsync(ct);
            credentials.Invalidate();
            return Result.Success(await ToDtoAsync(row, ct));
        }, logger);

    public async Task<AiConnectionTestResult> TestAsync(AiKeyKind kind, CancellationToken ct = default)
    {
        credentials.Invalidate();
        var s = await credentials.SettingsAsync(ct);
        try
        {
            if (kind == AiKeyKind.Chat)
            {
                if (string.IsNullOrEmpty(s?.ProtectedApiKey)) return new(false, "No AI provider key is saved yet.");
                var r = await chat.CompleteAsync(new AiCompletionRequest(s.ChatModel, "Reply with the single word OK.", null, [new AiMessage("user", "ping")], 16, "test"), ct);
                return new(true, $"Connected: model {r.Model} answered ({r.Usage.InputTokens + r.Usage.OutputTokens} tokens).");
            }
            if (string.IsNullOrEmpty(s?.ProtectedEmbeddingKey)) return new(false, "No embedding key is saved yet.");
            var e = await embeddings.EmbedAsync(["ping"], EmbeddingInputType.Query, ct);
            return new(true, $"Connected: model {e.Model} returned a {e.Dimensions}-dimension vector.");
        }
        catch (AiProviderException ex) { return new(false, ex.Message); }
    }
}

internal sealed class AiUsageService(CrmContext crm, IOptions<AiOptions> options, AiCredentials credentials) : IAiUsageService
{
    public async Task<AiUsageSummaryDto> SummaryAsync(int? periodKey, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var period = periodKey ?? AiUsageEntry.PeriodOf(crm.Now);
        var q = crm.Db.AiUsageEntries.AsNoTracking().Where(e => e.TenantId == tenantId && e.PeriodKey == period);
        var totals = await q.GroupBy(_ => 1).Select(g => new
        {
            Calls = g.Count(), Failed = g.Count(e => !e.Success), In = g.Sum(e => (long)e.InputTokens), Out = g.Sum(e => (long)e.OutputTokens), CR = g.Sum(e => (long)e.CacheReadTokens), CW = g.Sum(e => (long)e.CacheWriteTokens), Cost = g.Sum(e => e.EstimatedCostUsd)
        }).FirstOrDefaultAsync(ct);
        var byOp = await q.GroupBy(e => e.Operation).Select(g => new AiUsageBreakdownDto(g.Key, g.Count(), g.Count(e => !e.Success), g.Sum(e => (long)e.InputTokens), g.Sum(e => (long)e.OutputTokens), g.Sum(e => e.EstimatedCostUsd))).ToListAsync(ct);
        var byModel = await q.GroupBy(e => e.Model).Select(g => new AiUsageBreakdownDto(g.Key, g.Count(), g.Count(e => !e.Success), g.Sum(e => (long)e.InputTokens), g.Sum(e => (long)e.OutputTokens), g.Sum(e => e.EstimatedCostUsd))).ToListAsync(ct);
        var days = (await q.Select(e => new { e.OccurredAtUtc, Tokens = (long)e.InputTokens + e.OutputTokens, e.EstimatedCostUsd }).ToListAsync(ct))
            .GroupBy(e => DateOnly.FromDateTime(e.OccurredAtUtc)).OrderBy(g => g.Key).Select(g => new AiUsageDayDto(g.Key, g.Count(), g.Sum(e => e.Tokens), g.Sum(e => e.EstimatedCostUsd))).ToList();
        var settings = await credentials.SettingsAsync(ct);
        return new AiUsageSummaryDto(period, totals?.Calls ?? 0, totals?.Failed ?? 0, totals?.In ?? 0, totals?.Out ?? 0, totals?.CR ?? 0, totals?.CW ?? 0, totals?.Cost ?? 0, AiUsageLedger.EffectiveTokenLimit(settings, options.Value), settings?.MonthlyBudgetUsd,
            byOp.OrderByDescending(b => b.Calls).ToList(), byModel.OrderByDescending(b => b.Calls).ToList(), days, byModel.Select(m => m.Name).Where(n => !options.Value.Prices.ContainsKey(n)).ToList());
    }

    public async Task<PagedResult<AiUsageEntryDto>> EntriesAsync(int? periodKey, int page, int pageSize, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var period = periodKey ?? AiUsageEntry.PeriodOf(crm.Now); var (p, size) = CrmFlow.Page(page, pageSize);
        var q = crm.Db.AiUsageEntries.AsNoTracking().Where(e => e.TenantId == tenantId && e.PeriodKey == period);
        var total = await q.LongCountAsync(ct);
        var items = await q.OrderByDescending(e => e.OccurredAtUtc).Skip((p - 1) * size).Take(size)
            .Select(e => new AiUsageEntryDto(e.Id, e.Operation, e.Model, e.InputTokens, e.OutputTokens, e.CacheReadTokens, e.CacheWriteTokens, e.EstimatedCostUsd, e.Success, e.ErrorCode, e.LatencyMs, e.OccurredAtUtc)).ToListAsync(ct);
        return new PagedResult<AiUsageEntryDto>(items, p, size, total);
    }
}

internal sealed class AiSuggestionService(CrmContext crm, ILogger<AiSuggestionService> logger) : IAiSuggestionService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Result<AiSuggestionDto?>> LatestAsync(Guid conversationId, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        if (!await crm.Db.Conversations.AsNoTracking().AnyAsync(c => c.Id == conversationId && c.TenantId == tenantId, ct)) return Result.Failure<AiSuggestionDto?>(Error.NotFound("The conversation"));
        var row = await crm.Db.AiInteractions.AsNoTracking().Where(i => i.TenantId == tenantId && i.ConversationId == conversationId).OrderByDescending(i => i.CreatedAtUtc).FirstOrDefaultAsync(ct);
        return Result.Success<AiSuggestionDto?>(row is null ? null : await ToDtoAsync(row, ct));
    }

    public async Task<Result<AiSuggestionDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var row = await crm.Db.AiInteractions.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);
        return row is null ? Result.Failure<AiSuggestionDto>(Error.NotFound("The suggestion")) : Result.Success(await ToDtoAsync(row, ct));
    }

    public async Task<PagedResult<AiActivityItemDto>> ActivityAsync(AiActivityQuery query, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(query.Page, query.PageSize);
        var q = crm.Db.AiInteractions.AsNoTracking().Where(i => i.TenantId == tenantId);
        if (query.Decision is { } d) q = q.Where(i => i.Decision == d);
        if (query.Status is { } s) q = q.Where(i => i.Status == s);
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(i => i.CreatedAtUtc).Skip((page - 1) * size).Take(size).Select(i => new
        {
            i.Id, i.ConversationId, Peer = crm.Db.Conversations.Where(c => c.Id == i.ConversationId).Select(c => new { c.PeerName, c.PeerPhoneE164 }).FirstOrDefault(),
            i.Intent, i.Decision, i.Confidence, i.Status, i.EscalationReason, i.InjectionSuspected, i.CreatedAtUtc
        }).ToListAsync(ct);
        return new PagedResult<AiActivityItemDto>(rows.Select(r => new AiActivityItemDto(r.Id, r.ConversationId, r.Peer?.PeerName, r.Peer?.PeerPhoneE164, r.Intent, r.Decision, r.Confidence, r.Status, r.EscalationReason, r.InjectionSuspected, r.CreatedAtUtc)).ToList(), page, size, total);
    }

    public Task<Result<AiSuggestionDto>> MarkUsedAsync(Guid id, CancellationToken ct = default) => HandleAsync(id, used: true, ct);
    public Task<Result<AiSuggestionDto>> DismissAsync(Guid id, CancellationToken ct = default) => HandleAsync(id, used: false, ct);

    private Task<Result<AiSuggestionDto>> HandleAsync(Guid id, bool used, CancellationToken ct) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var row = await crm.Db.AiInteractions.FirstOrDefaultAsync(i => i.Id == id && i.TenantId == tenantId, ct);
            if (row is null) return Result.Failure<AiSuggestionDto>(Error.NotFound("The suggestion"));
            if (used) row.MarkUsed(crm.UserId, crm.Now); else row.Dismiss(crm.UserId, crm.Now);
            crm.Record(used ? "Ai.SuggestionUsed" : "Ai.SuggestionDismissed", "AiInteraction", row.Id, used ? "A staff member used the AI suggestion for a reply." : "A staff member dismissed the AI suggestion.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await ToDtoAsync(row, ct));
        }, logger);

    private async Task<AiSuggestionDto> ToDtoAsync(AiInteraction i, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var tools = await crm.Db.AiToolExecutions.AsNoTracking().Where(t => t.TenantId == tenantId && t.InteractionId == i.Id).OrderBy(t => t.OccurredAtUtc).Select(t => new AiToolExecutionDto(t.ToolName, t.ArgumentsSummary, t.Outcome, t.Reason, t.OccurredAtUtc)).ToListAsync(ct);
        var names = await crm.UserNamesAsync([i.HandledByUserId], ct);
        return new AiSuggestionDto(i.Id, i.ConversationId, i.TriggerMessageId, i.Intent, i.Decision, i.Confidence, i.DraftText, Read<List<Citation>>(i.CitationsJson) ?? [], Read<List<ValidationIssue>>(i.ValidationJson) ?? [], i.EscalationReason, i.NextAction,
            Read<Dictionary<string, string>>(i.ExtractedJson) ?? new Dictionary<string, string>(), i.InjectionSuspected, i.Status, i.Model, i.HandledByUserId is { } u && names.TryGetValue(u, out var n) ? n : null, i.HandledAtUtc, i.CreatedAtUtc, tools);
    }

    private static T? Read<T>(string json) { try { return JsonSerializer.Deserialize<T>(json, Json); } catch (JsonException) { return default; } }
}

internal sealed class AiTesterService(CrmContext crm, IAIOrchestrator orchestrator, IKnowledgeRetriever retriever, IOptions<AiOptions> options, ILogger<AiTesterService> logger) : IAiTesterService
{
    public async Task<Result<AiTestResultDto>> AskAsync(AskQuestionRequest r, CancellationToken ct = default)
    {
        var q = r.Question?.Trim() ?? string.Empty;
        if (q.Length == 0) return Result.Failure<AiTestResultDto>(Error.Validation("Type a question."));
        if (q.Length > options.Value.MaxMessageChars) return Result.Failure<AiTestResultDto>(Error.Validation($"The question is longer than {options.Value.MaxMessageChars} characters."));
        try
        {
            if (r.RetrieveOnly)
                return Result.Success(new AiTestResultDto(q, await retriever.RetrieveAsync(new RetrievalRequest(q, Domain.Ai.RetrievalSource.Tester, null, null, null, crm.UserId, StoreQueryText: true), ct), null));
            var run = await orchestrator.AskAsync(q, ct);
            if (run.Skipped) return Result.Failure<AiTestResultDto>(new Error("ai_not_configured", run.SkippedReason ?? "The AI is not configured."));
            var retrieval = new RetrievalResult(run.Chunks, run.Chunks.Count > 0 ? RetrievalOutcome.Found : RetrievalOutcome.NoMatch, null, run.RetrievalLogId, null, string.Empty, run.LatencyMs);
            return Result.Success(new AiTestResultDto(q, retrieval, run));
        }
        catch (AiProviderException ex) { return Result.Failure<AiTestResultDto>(AiErrors.From(ex)); }
        catch (Infrastructure.Ai.Stores.VectorStoreException ex) { logger.LogWarning(ex, "Vector store failed in the tester"); return Result.Failure<AiTestResultDto>(new Error("ai_provider_error", ex.Message)); }
    }
}
