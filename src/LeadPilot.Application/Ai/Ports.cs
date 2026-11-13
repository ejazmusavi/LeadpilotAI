using LeadPilot.Domain.Ai;

namespace LeadPilot.Application.Ai;

// ===================================================================================================================================================================
// The provider-independent AI ports. Business code depends on these only; adapters (Claude, Voyage, MySQL/Qdrant vector stores) live in Infrastructure/Ai.
// ===================================================================================================================================================================

public enum AiFailureKind { NotConfigured = 1, Auth, RateLimited, Overloaded, Timeout, InvalidRequest, Refused, CircuitOpen, Unavailable, BadResponse, LimitReached }

/// <summary>A provider failure with the information callers need: is it worth retrying later, and a message that is safe to show (never contains keys or message text).</summary>
public sealed class AiProviderException(AiFailureKind kind, string message, bool retryable, int? httpStatus = null) : Exception(message)
{
    public AiFailureKind Kind { get; } = kind;
    public bool Retryable { get; } = retryable;
    public int? HttpStatus { get; } = httpStatus;
}

public sealed record AiMessage(string Role, string Content);

/// <summary>
/// <paramref name="StableSystem"/> never changes between requests (prompt caching applies to it); <paramref name="DynamicSystem"/> may (per-request reference codes, tone).
/// <paramref name="Operation"/> labels the ledger entry (classify, generate, summarize, test).
/// </summary>
public sealed record AiCompletionRequest(string Model, string StableSystem, string? DynamicSystem, IReadOnlyList<AiMessage> Messages, int MaxTokens, string Operation, Guid? ConversationId = null);

public sealed record AiUsage(int InputTokens, int OutputTokens, int CacheReadTokens, int CacheWriteTokens);

public sealed record AiCompletion(string Text, AiUsage Usage, string Model, string? StopReason, int LatencyMs);

/// <summary>Text generation. Resolves the tenant's credentials itself (from the tenant context); callers never see an API key.</summary>
public interface IAIProvider
{
    string Name { get; }
    Task<AiCompletion> CompleteAsync(AiCompletionRequest request, CancellationToken ct);
}

public enum EmbeddingInputType { Document = 1, Query = 2 }

public sealed record EmbeddingResult(IReadOnlyList<float[]> Vectors, string Model, int Dimensions, int TotalTokens);

public interface IEmbeddingProvider
{
    string Name { get; }
    Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, EmbeddingInputType type, CancellationToken ct);
}

public sealed record VectorPoint(Guid ChunkId, Guid DocumentId, Guid VersionId, float[] Vector);

public sealed record VectorHit(Guid ChunkId, Guid DocumentId, Guid VersionId, double Score);

/// <param name="DocumentIds">Optional narrowing to some documents of the SAME tenant.</param>
public sealed record VectorQuery(float[] Vector, int TopK, double MinScore, IReadOnlyCollection<Guid>? DocumentIds = null);

/// <summary>
/// Similarity search over chunk embeddings. THE TENANT IS NOT A PARAMETER: every implementation takes it from <c>ITenantContext</c> and applies it to every read, write and delete itself, so no caller can omit it
/// or ask for another tenant's vectors. <paramref name="model"/> selects the embedding space (vectors of different models are never compared).
/// </summary>
public interface IVectorStore
{
    string Name { get; }
    Task UpsertAsync(string model, IReadOnlyList<VectorPoint> points, CancellationToken ct);
    Task<IReadOnlyList<VectorHit>> SearchAsync(string model, VectorQuery query, CancellationToken ct);
    Task DeleteDocumentAsync(Guid documentId, CancellationToken ct);
    Task DeleteVersionAsync(Guid documentId, Guid versionId, CancellationToken ct);
    Task DeleteAllAsync(CancellationToken ct);
    Task<long> CountAsync(CancellationToken ct);
}

// ---------- retrieval ----------

public sealed record RetrievalRequest(string Query, RetrievalSource Source, Guid? ConversationId = null, int? TopK = null, decimal? MinScore = null, Guid? UserId = null, bool StoreQueryText = false);

public sealed record RetrievedChunk(Guid ChunkId, Guid DocumentId, string DocumentTitle, DocumentKind Kind, int VersionNumber, int Ordinal, string? Heading, string Text, double Score, string? SourceName);

public sealed record RetrievalResult(IReadOnlyList<RetrievedChunk> Chunks, RetrievalOutcome Outcome, string? Note, Guid? LogId, string? EmbeddingModel, string StoreName, int LatencyMs);

public interface IKnowledgeRetriever
{
    /// <summary>Tenant-scoped retrieval. Returns only chunks of enabled documents' CURRENT indexed version that the tenant owns, above the score threshold. Never throws for "nothing found".</summary>
    Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken ct);
}

// ---------- conversation memory ----------

public sealed record ConversationTurn(Guid MessageId, bool FromCustomer, string Text, DateTime AtUtc);

public sealed record ConversationContext(Guid ConversationId, string? Summary, IReadOnlyList<ConversationTurn> Turns, int OlderMessageCount);

public interface IConversationMemory
{
    /// <summary>Recent messages (oldest first, up to and including <paramref name="upToMessageId"/> when given) plus the rolling summary of what came before. Failed and withdrawn messages are left out.</summary>
    Task<ConversationContext?> LoadAsync(Guid conversationId, Guid? upToMessageId, int maxMessages, CancellationToken ct);
    /// <summary>Summarises the messages older than the recent window into the stored summary. Returns false when there was nothing to do.</summary>
    Task<bool> RefreshSummaryAsync(Guid conversationId, int recentWindow, CancellationToken ct);
}

// ---------- orchestration ----------

public sealed record Citation(string Ref, Guid ChunkId, Guid DocumentId, string DocumentTitle, int VersionNumber, int Ordinal);

public sealed record ValidationIssue(string Code, string Detail);

public sealed record ToolRequest(string Name, IReadOnlyDictionary<string, string> Arguments);

public sealed record ToolResultItem(string Name, ToolOutcome Outcome, string? Reason);

public sealed record AiRunResult
{
    public bool Skipped { get; init; }
    public string? SkippedReason { get; init; }
    public Guid? InteractionId { get; init; }
    public string Intent { get; init; } = "unknown";
    public AiDecision Decision { get; init; } = AiDecision.NoReply;
    public decimal Confidence { get; init; }
    public string? ReplyText { get; init; }
    public IReadOnlyList<Citation> Citations { get; init; } = [];
    public IReadOnlyList<ValidationIssue> Issues { get; init; } = [];
    public string? EscalationReason { get; init; }
    public string? NextAction { get; init; }
    public bool InjectionSuspected { get; init; }
    public IReadOnlyDictionary<string, string> Extracted { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<RetrievedChunk> Chunks { get; init; } = [];
    public IReadOnlyList<ToolResultItem> Tools { get; init; } = [];
    /// <summary>What the model asked for (before the action gate decided). Only the orchestrator's executor acts on it.</summary>
    public IReadOnlyList<ToolRequest> ToolRequests { get; init; } = [];
    public Guid? RetrievalLogId { get; init; }
    public string Model { get; init; } = string.Empty;
    public int LatencyMs { get; init; }

    public static AiRunResult Skip(string reason) => new() { Skipped = true, SkippedReason = reason };
}

public interface IAIOrchestrator
{
    /// <summary>The full RAG workflow for an inbound message of a conversation (history, intent, retrieval, generation, validation, permitted actions). Stores the outcome as a staff-visible suggestion; never sends.</summary>
    Task<AiRunResult> RunForConversationAsync(Guid conversationId, Guid triggerMessageId, CancellationToken ct);
    /// <summary>Same retrieval + generation + validation for a question typed by staff (retrieval tester). Nothing is stored except usage and the retrieval log; no action is executed.</summary>
    Task<AiRunResult> AskAsync(string question, CancellationToken ct);
}
