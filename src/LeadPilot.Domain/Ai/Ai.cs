using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;

namespace LeadPilot.Domain.Ai;

public enum KnowledgeSourceType { Upload = 1, Text = 2, Website = 3 }

public enum KnowledgeSourceStatus { Active = 1, Paused = 2 }

/// <summary>What a document is for. Free classification for staff; the retriever can use it to filter.</summary>
public enum DocumentKind { Product = 1, Pricing = 2, Faq = 3, Feature = 4, SalesScript = 5, Policy = 6, Support = 7, Website = 8, Other = 9 }

/// <summary>Index state of the CURRENT version of a document (and of every version while it is being processed).</summary>
public enum IndexStatus { Pending = 1, Indexing = 2, Indexed = 3, Failed = 4, Superseded = 5 }

public enum RetrievalSource { Tester = 1, Conversation = 2 }

public enum RetrievalOutcome { Found = 1, NoMatch = 2, NotConfigured = 3, Error = 4 }

public enum AiDecision { Draft = 1, Escalate = 2, NoReply = 3 }

public enum DraftStatus { Pending = 1, Used = 2, Dismissed = 3, Superseded = 4 }

public enum ToolOutcome { Executed = 1, Denied = 2, Failed = 3 }

/// <summary>A place knowledge comes from: an uploaded file, pasted text or a website. Tenant-owned.</summary>
public class KnowledgeSource : AuditableEntity, ITenantOwned
{
    private KnowledgeSource() { }
    public Guid TenantId { get; private set; }
    public KnowledgeSourceType Type { get; private set; }
    public string Name { get; private set; } = string.Empty;
    /// <summary>Website sources only: the public start URL (https). Fetched through the SSRF-safe fetcher.</summary>
    public string? Url { get; private set; }
    public KnowledgeSourceStatus Status { get; private set; } = KnowledgeSourceStatus.Active;
    public DateTime? LastSyncAtUtc { get; private set; }
    public string? LastSyncError { get; private set; }

    public static KnowledgeSource Create(Guid tenantId, KnowledgeSourceType type, string name, string? url, DateTime nowUtc)
    {
        var u = Guard.Optional(url, 500, "URL");
        if (type == KnowledgeSourceType.Website && u is null) throw new DomainException("A website source needs a URL.");
        return new KnowledgeSource { TenantId = tenantId, Type = type, Name = Guard.Required(name, 150, "Name"), Url = type == KnowledgeSourceType.Website ? u : null, CreatedAtUtc = nowUtc };
    }

    public void Rename(string name, DateTime nowUtc) { Name = Guard.Required(name, 150, "Name"); UpdatedAtUtc = nowUtc; }
    public void SetStatus(KnowledgeSourceStatus status, DateTime nowUtc) { Status = status; UpdatedAtUtc = nowUtc; }
    public void RecordSync(DateTime nowUtc, string? error) { LastSyncAtUtc = nowUtc; LastSyncError = error is null ? null : (error.Length > 500 ? error[..500] : error); UpdatedAtUtc = nowUtc; }
}

/// <summary>One logical document. Its text lives in versions; only the current version is searchable.</summary>
public class KnowledgeDocument : AuditableEntity, ITenantOwned
{
    private KnowledgeDocument() { }
    public Guid TenantId { get; private set; }
    public Guid SourceId { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public DocumentKind Kind { get; private set; }
    /// <summary>Public page URL for website documents.</summary>
    public string? SourceUrl { get; private set; }
    public Guid? CurrentVersionId { get; private set; }
    public int LatestVersionNumber { get; private set; }
    public IndexStatus Status { get; private set; } = IndexStatus.Pending;
    /// <summary>Disabled documents stay stored but are never retrieved (vectors are removed).</summary>
    public bool IsEnabled { get; private set; } = true;

    public static KnowledgeDocument Create(Guid tenantId, Guid sourceId, string title, DocumentKind kind, string? sourceUrl, DateTime nowUtc) =>
        new() { TenantId = tenantId, SourceId = sourceId, Title = Guard.Required(title, 200, "Title"), Kind = kind, SourceUrl = Guard.Optional(sourceUrl, 500, "URL"), CreatedAtUtc = nowUtc };

    public int NextVersionNumber() => ++LatestVersionNumber;
    public void Edit(string title, DocumentKind kind, DateTime nowUtc) { Title = Guard.Required(title, 200, "Title"); Kind = kind; UpdatedAtUtc = nowUtc; }
    public void SetEnabled(bool enabled, DateTime nowUtc) { IsEnabled = enabled; UpdatedAtUtc = nowUtc; }
    public void SetCurrent(Guid versionId, DateTime nowUtc) { CurrentVersionId = versionId; Status = IndexStatus.Pending; UpdatedAtUtc = nowUtc; }
    public void SetStatus(IndexStatus status, DateTime nowUtc) { Status = status; UpdatedAtUtc = nowUtc; }
}

/// <summary>Immutable snapshot of a document's extracted text. A new upload or edit creates a new version; old ones are kept as history.</summary>
public class DocumentVersion : Entity, ITenantOwned
{
    public const int MaxContentChars = 2_000_000;
    private DocumentVersion() { }
    public Guid TenantId { get; private set; }
    public Guid DocumentId { get; private set; }
    public int VersionNumber { get; private set; }
    public string Content { get; private set; } = string.Empty;
    public string ContentHash { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }
    public string? FileName { get; private set; }
    public string? MimeType { get; private set; }
    public IndexStatus Status { get; private set; } = IndexStatus.Pending;
    public string? Error { get; private set; }
    public int ChunkCount { get; private set; }
    public string? EmbeddingModel { get; private set; }
    public DateTime? IndexedAtUtc { get; private set; }
    public DateTime? IndexingStartedAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public string? CreatedBy { get; private set; }

    public static DocumentVersion Create(Guid tenantId, Guid documentId, int number, string content, string hash, long sizeBytes, string? fileName, string? mimeType, string? createdBy, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new DomainException("The document has no readable text.");
        if (content.Length > MaxContentChars) throw new DomainException($"The extracted text is longer than {MaxContentChars:N0} characters.");
        return new DocumentVersion
        {
            TenantId = tenantId, DocumentId = documentId, VersionNumber = number, Content = content, ContentHash = hash, SizeBytes = sizeBytes,
            FileName = Guard.Optional(fileName, 260, "File name"), MimeType = Guard.Optional(mimeType, 100, "Type"), CreatedBy = createdBy, CreatedAtUtc = nowUtc
        };
    }

    public void MarkIndexing(DateTime nowUtc) { Status = IndexStatus.Indexing; IndexingStartedAtUtc = nowUtc; Error = null; }
    public void MarkIndexed(int chunks, string model, DateTime nowUtc) { Status = IndexStatus.Indexed; ChunkCount = chunks; EmbeddingModel = model; IndexedAtUtc = nowUtc; Error = null; }
    public void MarkFailed(string error) { Status = IndexStatus.Failed; Error = error.Length > 500 ? error[..500] : error; }
    public void MarkSuperseded() { Status = IndexStatus.Superseded; }
    public void ResetForReindex() { Status = IndexStatus.Pending; Error = null; }
}

/// <summary>A passage of one document version, the unit that is embedded and retrieved.</summary>
public class DocumentChunk : Entity, ITenantOwned
{
    private DocumentChunk() { }
    public Guid TenantId { get; private set; }
    public Guid DocumentId { get; private set; }
    public Guid VersionId { get; private set; }
    public int Ordinal { get; private set; }
    public string Text { get; private set; } = string.Empty;
    public string? Heading { get; private set; }
    public int CharCount { get; private set; }

    public static DocumentChunk Create(Guid tenantId, Guid documentId, Guid versionId, int ordinal, string text, string? heading) =>
        new() { TenantId = tenantId, DocumentId = documentId, VersionId = versionId, Ordinal = ordinal, Text = text, CharCount = text.Length, Heading = Guard.Optional(heading is { Length: > 200 } ? heading[..200] : heading, 200, "Heading") };
}

/// <summary>The embedding of one chunk. The vector is kept here (so a vector store can be rebuilt without paying for embeddings again); the vector store holds the searchable copy.</summary>
public class EmbeddingRecord : Entity, ITenantOwned
{
    private EmbeddingRecord() { }
    public Guid TenantId { get; private set; }
    public Guid ChunkId { get; private set; }
    public Guid DocumentId { get; private set; }
    public Guid VersionId { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public int Dimensions { get; private set; }
    /// <summary>float32 little-endian, L2-normalised.</summary>
    public byte[] Vector { get; private set; } = [];
    public string StoreName { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }

    public static EmbeddingRecord Create(Guid tenantId, Guid chunkId, Guid documentId, Guid versionId, string provider, string model, float[] normalised, string store, DateTime nowUtc)
    {
        var bytes = new byte[normalised.Length * sizeof(float)];
        Buffer.BlockCopy(normalised, 0, bytes, 0, bytes.Length);
        return new EmbeddingRecord { TenantId = tenantId, ChunkId = chunkId, DocumentId = documentId, VersionId = versionId, Provider = provider, Model = model, Dimensions = normalised.Length, Vector = bytes, StoreName = store, CreatedAtUtc = nowUtc };
    }

    public float[] ToArray() { var v = new float[Vector.Length / sizeof(float)]; Buffer.BlockCopy(Vector, 0, v, 0, Vector.Length); return v; }
}

/// <summary>Why the AI saw what it saw. Append-only. Customer message text is never stored here (only a hash and length); a tester question typed by staff is.</summary>
public class RetrievalLog : Entity, ITenantOwned, IAppendOnly
{
    private RetrievalLog() { }
    public Guid TenantId { get; private set; }
    public RetrievalSource Source { get; private set; }
    public Guid? ConversationId { get; private set; }
    public Guid? UserId { get; private set; }
    public string QueryHash { get; private set; } = string.Empty;
    public int QueryLength { get; private set; }
    public string? QueryText { get; private set; }
    public int TopK { get; private set; }
    public decimal MinScore { get; private set; }
    public int ResultCount { get; private set; }
    public decimal? TopScore { get; private set; }
    public string ResultsJson { get; private set; } = "[]";
    public string? EmbeddingModel { get; private set; }
    public string StoreName { get; private set; } = string.Empty;
    public int LatencyMs { get; private set; }
    public RetrievalOutcome Outcome { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static RetrievalLog Create(Guid tenantId, RetrievalSource source, Guid? conversationId, Guid? userId, string queryHash, int queryLength, string? queryText, int topK, decimal minScore,
        int resultCount, decimal? topScore, string resultsJson, string? embeddingModel, string storeName, int latencyMs, RetrievalOutcome outcome, DateTime nowUtc) =>
        new()
        {
            TenantId = tenantId, Source = source, ConversationId = conversationId, UserId = userId, QueryHash = queryHash, QueryLength = queryLength,
            QueryText = queryText is { Length: > 1000 } ? queryText[..1000] : queryText, TopK = topK, MinScore = minScore, ResultCount = resultCount, TopScore = topScore, ResultsJson = resultsJson,
            EmbeddingModel = embeddingModel, StoreName = storeName, LatencyMs = latencyMs, Outcome = outcome, CreatedAtUtc = nowUtc
        };
}

/// <summary>AI configuration of one tenant: encrypted provider keys, models, thresholds and limits. One row per tenant.</summary>
public class AiTenantSettings : AuditableEntity, ITenantOwned
{
    public const string ProviderClaude = "claude";
    public const string EmbeddingVoyage = "voyage";
    private AiTenantSettings() { }
    public Guid TenantId { get; private set; }
    public bool IsEnabled { get; private set; }
    public string? ProtectedApiKey { get; private set; }
    public string? KeyHint { get; private set; }
    public string ChatModel { get; private set; } = "claude-sonnet-5-5";
    public string ClassifierModel { get; private set; } = "claude-haiku-5-5";
    public string SummaryModel { get; private set; } = "claude-haiku-5-5";
    public string? ProtectedEmbeddingKey { get; private set; }
    public string? EmbeddingKeyHint { get; private set; }
    public string EmbeddingModel { get; private set; } = "voyage-3.5";
    public int TopK { get; private set; } = 5;
    public decimal MinRetrievalScore { get; private set; } = 0.35m;
    public decimal MinConfidence { get; private set; } = 0.6m;
    public int HistoryMessages { get; private set; } = 12;
    public int MaxContextChars { get; private set; } = 6000;
    public long? MonthlyTokenLimit { get; private set; }
    public decimal? MonthlyBudgetUsd { get; private set; }
    public string UnsupportedReply { get; private set; } = "Thank you for your message. I don't have reliable information on that, so a member of our team will get back to you shortly.";
    public string? ToneInstructions { get; private set; }
    /// <summary>One keyword or phrase per line: a customer message containing one is always handed to a person.</summary>
    public string? EscalationKeywords { get; private set; }
    public int Version { get; private set; } = 1;

    public static AiTenantSettings Create(Guid tenantId, DateTime nowUtc) => new() { TenantId = tenantId, CreatedAtUtc = nowUtc };

    public void SetEnabled(bool enabled, DateTime nowUtc) { IsEnabled = enabled; Touch(nowUtc); }
    public void SetKey(string protectedKey, string hint, DateTime nowUtc) { ProtectedApiKey = protectedKey; KeyHint = hint; Touch(nowUtc); }
    public void ClearKey(DateTime nowUtc) { ProtectedApiKey = null; KeyHint = null; IsEnabled = false; Touch(nowUtc); }
    public void SetEmbeddingKey(string protectedKey, string hint, DateTime nowUtc) { ProtectedEmbeddingKey = protectedKey; EmbeddingKeyHint = hint; Touch(nowUtc); }
    public void ClearEmbeddingKey(DateTime nowUtc) { ProtectedEmbeddingKey = null; EmbeddingKeyHint = null; Touch(nowUtc); }

    public void Configure(string chatModel, string classifierModel, string summaryModel, string embeddingModel, int topK, decimal minScore, decimal minConfidence, int historyMessages, int maxContextChars,
        long? monthlyTokens, decimal? monthlyBudget, string unsupportedReply, string? tone, string? escalationKeywords, DateTime nowUtc)
    {
        ChatModel = ModelId(chatModel, "Chat model"); ClassifierModel = ModelId(classifierModel, "Classifier model"); SummaryModel = ModelId(summaryModel, "Summary model");
        EmbeddingModel = ModelId(embeddingModel, "Embedding model");
        if (topK is < 1 or > 12) throw new DomainException("Passages per question must be between 1 and 12.");
        if (minScore is < 0 or > 1) throw new DomainException("The minimum retrieval score must be between 0 and 1.");
        if (minConfidence is < 0 or > 1) throw new DomainException("The minimum confidence must be between 0 and 1.");
        if (historyMessages is < 2 or > 50) throw new DomainException("History messages must be between 2 and 50.");
        if (maxContextChars is < 1000 or > 40000) throw new DomainException("Context size must be between 1,000 and 40,000 characters.");
        if (monthlyTokens is < 0) throw new DomainException("The monthly token limit cannot be negative.");
        if (monthlyBudget is < 0 or > 10_000_000m) throw new DomainException("The monthly budget must be between 0 and 10,000,000.");
        TopK = topK; MinRetrievalScore = minScore; MinConfidence = minConfidence; HistoryMessages = historyMessages; MaxContextChars = maxContextChars;
        MonthlyTokenLimit = monthlyTokens; MonthlyBudgetUsd = monthlyBudget;
        UnsupportedReply = Guard.Required(unsupportedReply, 500, "The reply for unsupported questions");
        ToneInstructions = Guard.Optional(tone, 1000, "Tone instructions");
        EscalationKeywords = Guard.Optional(escalationKeywords, 2000, "Escalation keywords");
        Touch(nowUtc);
    }

    private static string ModelId(string? v, string label)
    {
        var t = Guard.Required(v, 100, label);
        if (!System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z0-9][A-Za-z0-9._:\-/]{0,99}$")) throw new DomainException($"{label} contains invalid characters.");
        return t;
    }

    private void Touch(DateTime nowUtc) { UpdatedAtUtc = nowUtc; Version++; }
}

/// <summary>One billable (or counted) provider call. Append-only ledger; monthly usage is the sum for a period. Costs are ESTIMATES from the configured price table.</summary>
public class AiUsageEntry : Entity, ITenantOwned, IAppendOnly
{
    private AiUsageEntry() { }
    public Guid TenantId { get; private set; }
    public string Operation { get; private set; } = string.Empty;
    public string Provider { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public int CacheReadTokens { get; private set; }
    public int CacheWriteTokens { get; private set; }
    public decimal EstimatedCostUsd { get; private set; }
    public bool Success { get; private set; }
    public string? ErrorCode { get; private set; }
    public int LatencyMs { get; private set; }
    public Guid? ConversationId { get; private set; }
    public int PeriodKey { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    public static int PeriodOf(DateTime utc) => utc.Year * 100 + utc.Month;

    public static AiUsageEntry Create(Guid tenantId, string operation, string provider, string model, int input, int output, int cacheRead, int cacheWrite, decimal cost, bool success, string? errorCode,
        int latencyMs, Guid? conversationId, DateTime nowUtc) =>
        new()
        {
            TenantId = tenantId, Operation = operation.Length > 40 ? operation[..40] : operation, Provider = provider, Model = model.Length > 100 ? model[..100] : model, InputTokens = input, OutputTokens = output,
            CacheReadTokens = cacheRead, CacheWriteTokens = cacheWrite, EstimatedCostUsd = cost, Success = success, ErrorCode = errorCode is { Length: > 60 } ? errorCode[..60] : errorCode,
            LatencyMs = latencyMs, ConversationId = conversationId, PeriodKey = PeriodOf(nowUtc), OccurredAtUtc = nowUtc
        };
}

/// <summary>Rolling summary of the older part of a conversation (the recent messages are always sent verbatim).</summary>
public class ConversationSummary : Entity, ITenantOwned
{
    private ConversationSummary() { }
    public Guid TenantId { get; private set; }
    public Guid ConversationId { get; private set; }
    public string Summary { get; private set; } = string.Empty;
    /// <summary>Messages created at or before this instant are covered by the summary.</summary>
    public DateTime CoveredUntilUtc { get; private set; }
    public int CoveredMessageCount { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }
    public int Version { get; private set; } = 1;

    public static ConversationSummary Create(Guid tenantId, Guid conversationId, string summary, DateTime coveredUntilUtc, int count, DateTime nowUtc) =>
        new() { TenantId = tenantId, ConversationId = conversationId, Summary = Clip(summary), CoveredUntilUtc = coveredUntilUtc, CoveredMessageCount = count, UpdatedAtUtc = nowUtc };

    public void Update(string summary, DateTime coveredUntilUtc, int count, DateTime nowUtc) { Summary = Clip(summary); CoveredUntilUtc = coveredUntilUtc; CoveredMessageCount = count; UpdatedAtUtc = nowUtc; Version++; }
    private static string Clip(string s) => s.Length > 4000 ? s[..4000] : s;
}

/// <summary>The result of one AI run for a conversation: the draft reply or the escalation. Staff see it next to the conversation. Never sent by Phase 7.</summary>
public class AiInteraction : Entity, ITenantOwned
{
    private AiInteraction() { }
    public Guid TenantId { get; private set; }
    public Guid ConversationId { get; private set; }
    public Guid TriggerMessageId { get; private set; }
    public string Intent { get; private set; } = string.Empty;
    public AiDecision Decision { get; private set; }
    public decimal Confidence { get; private set; }
    public string? DraftText { get; private set; }
    public string CitationsJson { get; private set; } = "[]";
    public string ValidationJson { get; private set; } = "[]";
    public string? EscalationReason { get; private set; }
    public string? NextAction { get; private set; }
    public string ExtractedJson { get; private set; } = "{}";
    public bool InjectionSuspected { get; private set; }
    public DraftStatus Status { get; private set; } = DraftStatus.Pending;
    public string Model { get; private set; } = string.Empty;
    public int LatencyMs { get; private set; }
    public Guid? RetrievalLogId { get; private set; }
    public Guid? HandledByUserId { get; private set; }
    public DateTime? HandledAtUtc { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public static AiInteraction Create(Guid tenantId, Guid conversationId, Guid triggerMessageId, string intent, AiDecision decision, decimal confidence, string? draft, string citationsJson,
        string validationJson, string? escalationReason, string? nextAction, string extractedJson, bool injectionSuspected, string model, int latencyMs, Guid? retrievalLogId, DateTime nowUtc) =>
        new()
        {
            TenantId = tenantId, ConversationId = conversationId, TriggerMessageId = triggerMessageId, Intent = intent.Length > 60 ? intent[..60] : intent, Decision = decision, Confidence = Math.Clamp(confidence, 0, 1),
            DraftText = draft, CitationsJson = citationsJson, ValidationJson = validationJson, EscalationReason = escalationReason is { Length: > 200 } ? escalationReason[..200] : escalationReason,
            NextAction = nextAction is { Length: > 300 } ? nextAction[..300] : nextAction, ExtractedJson = extractedJson, InjectionSuspected = injectionSuspected, Model = model, LatencyMs = latencyMs, RetrievalLogId = retrievalLogId, CreatedAtUtc = nowUtc,
            Status = string.IsNullOrWhiteSpace(draft) ? DraftStatus.Dismissed : DraftStatus.Pending
        };

    public void MarkUsed(Guid? userId, DateTime nowUtc) { if (Status != DraftStatus.Pending) throw new DomainException("This suggestion was already handled."); Status = DraftStatus.Used; HandledByUserId = userId; HandledAtUtc = nowUtc; }
    public void Dismiss(Guid? userId, DateTime nowUtc) { if (Status != DraftStatus.Pending) throw new DomainException("This suggestion was already handled."); Status = DraftStatus.Dismissed; HandledByUserId = userId; HandledAtUtc = nowUtc; }
    public void Supersede(DateTime nowUtc) { if (Status == DraftStatus.Pending) { Status = DraftStatus.Superseded; HandledAtUtc = nowUtc; } }
}

/// <summary>Every action the AI asked for, allowed or not. Append-only audit of tool execution.</summary>
public class AiToolExecution : Entity, ITenantOwned, IAppendOnly
{
    private AiToolExecution() { }
    public Guid TenantId { get; private set; }
    public Guid InteractionId { get; private set; }
    public string ToolName { get; private set; } = string.Empty;
    public string ArgumentsSummary { get; private set; } = string.Empty;
    public ToolOutcome Outcome { get; private set; }
    public string? Reason { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }

    public static AiToolExecution Create(Guid tenantId, Guid interactionId, string tool, string args, ToolOutcome outcome, string? reason, DateTime nowUtc) =>
        new()
        {
            TenantId = tenantId, InteractionId = interactionId, ToolName = tool.Length > 60 ? tool[..60] : tool, ArgumentsSummary = args.Length > 500 ? args[..500] : args, Outcome = outcome,
            Reason = reason is { Length: > 200 } ? reason[..200] : reason, OccurredAtUtc = nowUtc
        };
}
