using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Ai;
using LeadPilot.Shared.Results;

namespace LeadPilot.Application.Ai;

// ---------- knowledge base ----------

public sealed record KnowledgeSourceDto(Guid Id, KnowledgeSourceType Type, string Name, string? Url, KnowledgeSourceStatus Status, int DocumentCount, DateTime? LastSyncAtUtc, string? LastSyncError, DateTime CreatedAtUtc);

public sealed record KnowledgeDocumentListItemDto(
    Guid Id, Guid SourceId, string SourceName, string Title, DocumentKind Kind, IndexStatus Status, bool IsEnabled, int LatestVersion, int ChunkCount, DateTime? IndexedAtUtc, string? Error, string? SourceUrl, DateTime UpdatedAtUtc);

public sealed record DocumentVersionDto(
    Guid Id, int VersionNumber, bool IsCurrent, IndexStatus Status, long SizeBytes, int ContentChars, string? FileName, string? MimeType, int ChunkCount, string? EmbeddingModel, DateTime? IndexedAtUtc, string? Error, string? CreatedBy, DateTime CreatedAtUtc);

public sealed record KnowledgeDocumentDto(KnowledgeDocumentListItemDto Summary, IReadOnlyList<DocumentVersionDto> Versions, string? CurrentContentPreview, bool IsEditableText);

public sealed record ChunkDto(Guid Id, int Ordinal, string? Heading, string Text, int CharCount);

public sealed record KnowledgeDocumentQuery
{
    public Guid? SourceId { get; init; }
    public IndexStatus? Status { get; init; }
    public DocumentKind? Kind { get; init; }
    public string? Search { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}

public sealed record CreateTextDocumentRequest(string Title, DocumentKind Kind, string Content, Guid? SourceId = null, string? NewSourceName = null) : TenantScopedRequest;
public sealed record UploadDocumentRequest(string? Title, DocumentKind Kind, string FileName, string? ContentType, byte[] Data, Guid? SourceId = null, string? NewSourceName = null, Guid? ReplaceDocumentId = null) : TenantScopedRequest;
public sealed record UpdateDocumentTextRequest(string Content, string? Title = null, DocumentKind? Kind = null) : TenantScopedRequest;
public sealed record EditDocumentRequest(string Title, DocumentKind Kind) : TenantScopedRequest;
public sealed record AddWebsiteSourceRequest(string Name, string Url, DocumentKind Kind = DocumentKind.Website) : TenantScopedRequest;

public sealed record ReindexResult(int Queued);

public interface IKnowledgeService
{
    Task<IReadOnlyList<KnowledgeSourceDto>> ListSourcesAsync(CancellationToken ct = default);
    Task<PagedResult<KnowledgeDocumentListItemDto>> ListDocumentsAsync(KnowledgeDocumentQuery query, CancellationToken ct = default);
    Task<Result<KnowledgeDocumentDto>> GetDocumentAsync(Guid id, CancellationToken ct = default);
    Task<Result<PagedResult<ChunkDto>>> ChunksAsync(Guid documentId, Guid? versionId, int page, int pageSize, CancellationToken ct = default);
    Task<Result<KnowledgeDocumentListItemDto>> CreateTextAsync(CreateTextDocumentRequest request, CancellationToken ct = default);
    Task<Result<KnowledgeDocumentListItemDto>> UploadAsync(UploadDocumentRequest request, CancellationToken ct = default);
    /// <summary>Creates a NEW version (the old one stays as history) and re-indexes. Unchanged text creates nothing (409).</summary>
    Task<Result<KnowledgeDocumentListItemDto>> UpdateTextAsync(Guid id, UpdateDocumentTextRequest request, CancellationToken ct = default);
    Task<Result<KnowledgeDocumentListItemDto>> EditAsync(Guid id, EditDocumentRequest request, CancellationToken ct = default);
    Task<Result<KnowledgeDocumentListItemDto>> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default);
    Task<Result<KnowledgeDocumentListItemDto>> ReindexAsync(Guid id, CancellationToken ct = default);
    /// <summary>Re-indexes every document (needed after the embedding model changed).</summary>
    Task<Result<ReindexResult>> ReindexAllAsync(CancellationToken ct = default);
    Task<Result> DeleteDocumentAsync(Guid id, CancellationToken ct = default);
    Task<Result<KnowledgeSourceDto>> AddWebsiteSourceAsync(AddWebsiteSourceRequest request, CancellationToken ct = default);
    Task<Result<KnowledgeSourceDto>> SyncWebsiteAsync(Guid sourceId, CancellationToken ct = default);
    Task<Result> DeleteSourceAsync(Guid sourceId, CancellationToken ct = default);
}

/// <summary>Background work behind the knowledge base: runs inside a tenant scope established by the job.</summary>
public interface IKnowledgeIndexer
{
    Task IndexVersionAsync(Guid versionId, CancellationToken ct);
    Task SyncWebsiteSourceAsync(Guid sourceId, CancellationToken ct);
}

/// <summary>The one scheduler of AI background work (Hangfire in production).</summary>
public interface IAiJobScheduler
{
    string EnqueueIndex(Guid tenantId, Guid versionId);
    string EnqueueWebsiteSync(Guid tenantId, Guid sourceId);
}

// ---------- settings, usage ----------

public enum AiKeyKind { Chat = 1, Embedding = 2 }

public sealed record AiSettingsDto(
    bool IsEnabled, bool HasKey, string? KeyHint, bool HasEmbeddingKey, string? EmbeddingKeyHint, string ChatModel, string ClassifierModel, string SummaryModel, string EmbeddingModel,
    int TopK, decimal MinRetrievalScore, decimal MinConfidence, int HistoryMessages, int MaxContextChars, long? MonthlyTokenLimit, decimal? MonthlyBudgetUsd, string UnsupportedReply,
    string? ToneInstructions, string? EscalationKeywords, string VectorStore, long EffectiveMonthlyTokenLimit, long TokensThisMonth, decimal EstimatedCostThisMonth, IReadOnlyList<string> SuggestedChatModels, int DocumentsNeedingReindex);

public sealed record UpdateAiSettingsRequest(
    string ChatModel, string ClassifierModel, string SummaryModel, string EmbeddingModel, int TopK, decimal MinRetrievalScore, decimal MinConfidence, int HistoryMessages, int MaxContextChars,
    long? MonthlyTokenLimit, decimal? MonthlyBudgetUsd, string UnsupportedReply, string? ToneInstructions, string? EscalationKeywords) : TenantScopedRequest;

public sealed record SetAiKeyRequest(string ApiKey) : TenantScopedRequest;
public sealed record SetAiEnabledRequest(bool Enabled) : TenantScopedRequest;
public sealed record AiConnectionTestResult(bool Ok, string Message);

public interface IAiSettingsService
{
    Task<AiSettingsDto> GetAsync(CancellationToken ct = default);
    Task<Result<AiSettingsDto>> UpdateAsync(UpdateAiSettingsRequest request, CancellationToken ct = default);
    Task<Result<AiSettingsDto>> SetKeyAsync(AiKeyKind kind, SetAiKeyRequest request, CancellationToken ct = default);
    Task<Result<AiSettingsDto>> ClearKeyAsync(AiKeyKind kind, CancellationToken ct = default);
    Task<Result<AiSettingsDto>> SetEnabledAsync(SetAiEnabledRequest request, CancellationToken ct = default);
    /// <summary>One minimal real request to the provider with the saved key (costs a fraction of a cent for chat, nothing measurable for embeddings).</summary>
    Task<AiConnectionTestResult> TestAsync(AiKeyKind kind, CancellationToken ct = default);
}

public sealed record AiUsageBreakdownDto(string Name, int Calls, int Failed, long InputTokens, long OutputTokens, decimal EstimatedCostUsd);

public sealed record AiUsageDayDto(DateOnly Day, int Calls, long Tokens, decimal EstimatedCostUsd);

public sealed record AiUsageSummaryDto(
    int PeriodKey, int Calls, int Failed, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, decimal EstimatedCostUsd, long EffectiveTokenLimit, decimal? BudgetUsd,
    IReadOnlyList<AiUsageBreakdownDto> ByOperation, IReadOnlyList<AiUsageBreakdownDto> ByModel, IReadOnlyList<AiUsageDayDto> Days, IReadOnlyList<string> UnpricedModels);

public sealed record AiUsageEntryDto(Guid Id, string Operation, string Model, int InputTokens, int OutputTokens, int CacheReadTokens, int CacheWriteTokens, decimal EstimatedCostUsd, bool Success, string? ErrorCode, int LatencyMs, DateTime OccurredAtUtc);

public interface IAiUsageService
{
    Task<AiUsageSummaryDto> SummaryAsync(int? periodKey, CancellationToken ct = default);
    Task<PagedResult<AiUsageEntryDto>> EntriesAsync(int? periodKey, int page, int pageSize, CancellationToken ct = default);
}

// ---------- tester, suggestions ----------

public sealed record AskQuestionRequest(string Question, bool RetrieveOnly = false) : TenantScopedRequest;

public sealed record AiTestResultDto(string Question, RetrievalResult Retrieval, AiRunResult? Answer);

public interface IAiTesterService
{
    Task<Result<AiTestResultDto>> AskAsync(AskQuestionRequest request, CancellationToken ct = default);
}

public sealed record AiToolExecutionDto(string Tool, string Arguments, ToolOutcome Outcome, string? Reason, DateTime OccurredAtUtc);

public sealed record AiSuggestionDto(
    Guid Id, Guid ConversationId, Guid TriggerMessageId, string Intent, AiDecision Decision, decimal Confidence, string? DraftText, IReadOnlyList<Citation> Citations, IReadOnlyList<ValidationIssue> Issues,
    string? EscalationReason, string? NextAction, IReadOnlyDictionary<string, string> Extracted, bool InjectionSuspected, DraftStatus Status, string Model, string? HandledByName, DateTime? HandledAtUtc,
    DateTime CreatedAtUtc, IReadOnlyList<AiToolExecutionDto> Tools);

public sealed record AiActivityItemDto(Guid Id, Guid ConversationId, string? PeerName, string? PeerPhoneE164, string Intent, AiDecision Decision, decimal Confidence, DraftStatus Status, string? EscalationReason, bool InjectionSuspected, DateTime CreatedAtUtc);

public sealed record AiActivityQuery
{
    public AiDecision? Decision { get; init; }
    public DraftStatus? Status { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}

public interface IAiSuggestionService
{
    /// <summary>The newest suggestion of the conversation (any status), or null. 404 for a conversation of another tenant.</summary>
    Task<Result<AiSuggestionDto?>> LatestAsync(Guid conversationId, CancellationToken ct = default);
    Task<Result<AiSuggestionDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<AiActivityItemDto>> ActivityAsync(AiActivityQuery query, CancellationToken ct = default);
    /// <summary>Staff copied the draft into their reply (the reply itself still goes through the normal, consent-checked send).</summary>
    Task<Result<AiSuggestionDto>> MarkUsedAsync(Guid id, CancellationToken ct = default);
    Task<Result<AiSuggestionDto>> DismissAsync(Guid id, CancellationToken ct = default);
}

/// <summary>The only actions the AI may ask for. Anything else is denied and logged (see <c>AiActionCatalog</c>).</summary>
public static class AiActionCatalog
{
    public const string EscalateToHuman = "escalate_to_human";
    public const string AddLeadNote = "add_lead_note";

    public sealed record Definition(string Name, string Description, string Capability);

    public static readonly IReadOnlyList<Definition> Permitted =
    [
        new(EscalateToHuman, "Hand the conversation to a member of staff (pauses the AI for this conversation).", "conversation.escalate"),
        new(AddLeadNote, "Add a short internal note to the linked lead (arguments: note).", "lead.note"),
    ];

    /// <summary>What the AI "principal" may do in this phase. Nothing that contacts a customer, changes money, a plan, a stage or deletes data is in this set.</summary>
    public static readonly IReadOnlySet<string> Capabilities = new HashSet<string> { "conversation.escalate", "lead.note" };

    public static Definition? Find(string name) => Permitted.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));
}
