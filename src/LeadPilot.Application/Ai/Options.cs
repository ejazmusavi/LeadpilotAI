namespace LeadPilot.Application.Ai;

public sealed class AiOptions
{
    public const string Section = "Ai";
    public ClaudeOptions Claude { get; set; } = new();
    public VoyageOptions Voyage { get; set; } = new();
    public VectorStoreOptions VectorStore { get; set; } = new();
    public IngestionOptions Ingestion { get; set; } = new();
    /// <summary>Platform default when a tenant sets no monthly token limit (input + output tokens of all operations). 0 = unlimited.</summary>
    public long DefaultMonthlyTokenLimit { get; set; } = 2_000_000;
    public int MaxReplyTokens { get; set; } = 700;
    public int MaxMessageChars { get; set; } = 2000;
    /// <summary>Estimated prices (USD per million tokens). ESTIMATES for the usage page, not billing data: check the provider's price list. Models without an entry are counted in tokens only.</summary>
    public Dictionary<string, ModelPrice> Prices { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-fable-5-2"] = new(8m, 40m, 0.8m, 10m),
        ["claude-opus-5-5"] = new(4m, 20m, 0.2m, 5m),
        ["claude-sonnet-5-5"] = new(2m, 10m, 0.1m, 2.5m),
        ["claude-haiku-5-5"] = new(0.1m, 0.5m, 0.01m, 0.125m),
        ["voyage-3.5"] = new(0.06m, 0m, 0m, 0m),
        ["voyage-3.5-lite"] = new(0.02m, 0m, 0m, 0m),
        ["voyage-3-large"] = new(0.18m, 0m, 0m, 0m),
    };
}

public sealed record ModelPrice(decimal InputPerMTok, decimal OutputPerMTok, decimal CacheReadPerMTok, decimal CacheWritePerMTok)
{
    public decimal Estimate(int input, int output, int cacheRead, int cacheWrite) =>
        (input * InputPerMTok + output * OutputPerMTok + cacheRead * CacheReadPerMTok + cacheWrite * CacheWritePerMTok) / 1_000_000m;
}

public sealed class ClaudeOptions
{
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public string ApiVersion { get; set; } = "2023-06-01";
    public int TimeoutSeconds { get; set; } = 60;
    public int MaxRetries { get; set; } = 2;
    public int RetryBaseDelayMs { get; set; } = 800;
    public int BreakerFailureThreshold { get; set; } = 5;
    public int BreakerOpenSeconds { get; set; } = 30;
    /// <summary>Asks the API to retry a refused request on a fallback model server-side (beta <c>server-side-fallback-2026-07-01</c>). Off until it has been verified against a live key: a refusal is otherwise escalated to a person.</summary>
    public bool ServerSideFallback { get; set; }
}

public sealed class VoyageOptions
{
    public string BaseUrl { get; set; } = "https://api.voyageai.com";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 2;
    public int RetryBaseDelayMs { get; set; } = 500;
    public int BreakerFailureThreshold { get; set; } = 5;
    public int BreakerOpenSeconds { get; set; } = 30;
}

public sealed class VectorStoreOptions
{
    /// <summary>"mysql" (default: brute-force cosine over the tenant's stored vectors, fine up to a few thousand chunks) or "qdrant".</summary>
    public string Provider { get; set; } = "mysql";
    /// <summary>The MySQL store refuses to scan more than this many chunks of one tenant (the tenant should move to Qdrant).</summary>
    public int MysqlMaxScan { get; set; } = 20_000;
    public QdrantOptions Qdrant { get; set; } = new();
}

public sealed class QdrantOptions
{
    public string BaseUrl { get; set; } = "http://localhost:6333";
    /// <summary>Platform-level infrastructure secret (environment/secret store), never a tenant value.</summary>
    public string? ApiKey { get; set; }
    public string CollectionPrefix { get; set; } = "leadpilot_kb";
    public int TimeoutSeconds { get; set; } = 20;
}

public sealed class IngestionOptions
{
    public int MaxUploadBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxPdfPages { get; set; } = 300;
    public long MaxDocxUncompressedBytes { get; set; } = 50L * 1024 * 1024;
    public int MaxDocxEntries { get; set; } = 2000;
    public int ChunkTargetChars { get; set; } = 900;
    public int ChunkMaxChars { get; set; } = 1400;
    public int ChunkOverlapChars { get; set; } = 120;
    public int MaxChunksPerDocument { get; set; } = 2000;
    public int EmbedBatchSize { get; set; } = 64;
    public int WebsiteMaxPages { get; set; } = 10;
    public int MaxDocumentsPerTenant { get; set; } = 500;
    public int MaxSourcesPerTenant { get; set; } = 50;
    /// <summary>An index run that has been "Indexing" for longer than this is taken over (crash recovery).</summary>
    public int StaleIndexingMinutes { get; set; } = 15;
}
