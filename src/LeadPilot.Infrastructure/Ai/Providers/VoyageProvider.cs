using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LeadPilot.Application.Ai;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Ai.Providers;

/// <summary>Voyage AI embeddings over HTTP (recommended provider, see docs/OWNER_DECISIONS.md). The tenant's embedding key and model come from the encrypted tenant settings.</summary>
public sealed class VoyageEmbeddingProvider(AiCredentials credentials, AiHttpPipeline pipeline, Application.Abstractions.ITenantContext tenant, IOptions<AiOptions> options) : IRawEmbeddingProvider
{
    public const int MaxInputsPerRequest = 128;
    public string Name => "voyage";

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, EmbeddingInputType type, CancellationToken ct)
    {
        if (texts.Count == 0) return new EmbeddingResult([], string.Empty, 0, 0);
        var o = options.Value.Voyage;
        var settings = await credentials.SettingsAsync(ct);
        var key = await credentials.EmbeddingKeyAsync(ct) ?? throw new AiProviderException(AiFailureKind.NotConfigured, "No embedding API key is saved for this workspace.", false);
        var model = settings?.EmbeddingModel ?? "voyage-3.5";
        var vectors = new List<float[]>(texts.Count); var tokens = 0;
        foreach (var batch in texts.Chunk(MaxInputsPerRequest))
        {
            var body = JsonSerializer.Serialize(new { input = batch, model, input_type = type == EmbeddingInputType.Query ? "query" : "document", truncation = true });
            using var response = await pipeline.SendAsync($"voyage:{tenant.RequireTenantId()}", "Voyage AI", () =>
            {
                var msg = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(o.BaseUrl.TrimEnd('/') + "/"), "v1/embeddings")) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                return msg;
            }, o.MaxRetries, o.RetryBaseDelayMs, TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSeconds)), o.BreakerFailureThreshold, TimeSpan.FromSeconds(o.BreakerOpenSeconds), ct);
            var (v, t) = Parse(await response.Content.ReadAsStringAsync(ct), batch.Length);
            vectors.AddRange(v); tokens += t;
        }
        var dims = vectors.Count > 0 ? vectors[0].Length : 0;
        if (vectors.Any(v => v.Length != dims || dims == 0)) throw new AiProviderException(AiFailureKind.BadResponse, "Voyage AI returned vectors of different sizes.", true);
        return new EmbeddingResult(vectors, model, dims, tokens);
    }

    public static (List<float[]> Vectors, int Tokens) Parse(string json, int expected)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data").EnumerateArray().OrderBy(d => d.GetProperty("index").GetInt32()).Select(d => d.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToList();
            if (data.Count != expected) throw new AiProviderException(AiFailureKind.BadResponse, "Voyage AI returned the wrong number of vectors.", true);
            var tokens = doc.RootElement.TryGetProperty("usage", out var u) && u.TryGetProperty("total_tokens", out var t) && t.TryGetInt32(out var n) ? n : 0;
            return (data, tokens);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { throw new AiProviderException(AiFailureKind.BadResponse, "Voyage AI returned an unreadable response.", true); }
    }
}
