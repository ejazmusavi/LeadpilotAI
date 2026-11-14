using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LeadPilot.Application.Ai;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Ai.Providers;

/// <summary>The provider adapters sit BEHIND the metering decorator: services depend on <see cref="IAIProvider"/>, tests replace this raw layer.</summary>
public interface IRawAIProvider : IAIProvider;

public interface IRawEmbeddingProvider : IEmbeddingProvider;

/// <summary>
/// Claude Messages API adapter (raw HTTP: the base URL is configurable so the adapter is tested against a local server, and no SDK dependency is added). The tenant's key is read from the encrypted settings per
/// call. The stable system block carries <c>cache_control</c> (prompt caching); sampling parameters are not sent (current models reject non-default values). Neither the key nor any message text is ever logged or put
/// into an exception: failures carry the HTTP status only.
/// </summary>
public sealed class ClaudeAIProvider(AiCredentials credentials, AiHttpPipeline pipeline, Application.Abstractions.ITenantContext tenant, IOptions<AiOptions> options) : IRawAIProvider
{
    public string Name => "claude";

    public async Task<AiCompletion> CompleteAsync(AiCompletionRequest request, CancellationToken ct)
    {
        var o = options.Value.Claude;
        var key = await credentials.ChatKeyAsync(ct) ?? throw new AiProviderException(AiFailureKind.NotConfigured, "No Claude API key is saved for this workspace.", retryable: false);
        var body = BuildBody(request, o.ServerSideFallback);
        var sw = Stopwatch.StartNew();
        using var response = await pipeline.SendAsync($"claude:{tenant.RequireTenantId()}", "Claude", () =>
        {
            var msg = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(o.BaseUrl.TrimEnd('/') + "/"), "v1/messages")) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            msg.Headers.TryAddWithoutValidation("x-api-key", key);
            msg.Headers.TryAddWithoutValidation("anthropic-version", o.ApiVersion);
            if (o.ServerSideFallback) msg.Headers.TryAddWithoutValidation("anthropic-beta", "server-side-fallback-2026-07-01");
            msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return msg;
        }, o.MaxRetries, o.RetryBaseDelayMs, TimeSpan.FromSeconds(Math.Max(5, o.TimeoutSeconds)), o.BreakerFailureThreshold, TimeSpan.FromSeconds(o.BreakerOpenSeconds), ct);
        sw.Stop();
        return Parse(await response.Content.ReadAsStringAsync(ct), request.Model, (int)sw.ElapsedMilliseconds);
    }

    public static string BuildBody(AiCompletionRequest r, bool serverSideFallback)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", r.Model);
            w.WriteNumber("max_tokens", Math.Clamp(r.MaxTokens, 16, 8192));
            if (serverSideFallback) w.WriteString("fallbacks", "default");
            w.WriteStartArray("system");
            w.WriteStartObject(); w.WriteString("type", "text"); w.WriteString("text", r.StableSystem);
            w.WriteStartObject("cache_control"); w.WriteString("type", "ephemeral"); w.WriteEndObject();      // the stable block is the cached prefix
            w.WriteEndObject();
            if (!string.IsNullOrWhiteSpace(r.DynamicSystem)) { w.WriteStartObject(); w.WriteString("type", "text"); w.WriteString("text", r.DynamicSystem); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteStartArray("messages");
            foreach (var m in r.Messages) { w.WriteStartObject(); w.WriteString("role", m.Role); w.WriteString("content", m.Content); w.WriteEndObject(); }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static AiCompletion Parse(string json, string requestedModel, int latencyMs)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); } catch (JsonException) { throw new AiProviderException(AiFailureKind.BadResponse, "Claude returned an unreadable response.", retryable: true); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new AiProviderException(AiFailureKind.BadResponse, "Claude returned an unexpected response.", true);
            var stop = root.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String ? sr.GetString() : null;
            if (stop == "refusal") throw new AiProviderException(AiFailureKind.Refused, "The model declined to answer this request.", retryable: false);
            var text = new StringBuilder();
            if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var block in content.EnumerateArray())
                    if (block.TryGetProperty("type", out var t) && t.GetString() == "text" && block.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String) text.Append(tx.GetString());
            var usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object
                ? new AiUsage(Int(u, "input_tokens"), Int(u, "output_tokens"), Int(u, "cache_read_input_tokens"), Int(u, "cache_creation_input_tokens"))
                : new AiUsage(0, 0, 0, 0);
            var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? requestedModel : requestedModel;
            if (text.Length == 0) throw new AiProviderException(AiFailureKind.BadResponse, "Claude returned no text.", retryable: true);
            return new AiCompletion(text.ToString(), usage, model, stop, latencyMs);
        }
    }

    private static int Int(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}
