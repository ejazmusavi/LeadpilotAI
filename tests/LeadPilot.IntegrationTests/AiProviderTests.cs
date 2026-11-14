using System.Net;
using System.Text.Json;
using LeadPilot.Application.Ai;
using LeadPilot.Domain.Ai;
using LeadPilot.Infrastructure.Ai.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace LeadPilot.IntegrationTests;

/// <summary>TEST-ONLY: keeps every log line of a host so tests can prove that keys and message text never reach the logs.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Capture(this);
    public void Dispose() { }
    private sealed class Capture(CapturingLoggerProvider p) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            p.Lines.Enqueue(formatter(state, exception) + (exception is null ? "" : "\n" + exception));
    }
}

/// <summary>
/// The REAL Claude Messages adapter and the REAL Voyage embeddings adapter, run end to end through the application against a LOCAL server that speaks their request/response shapes. This proves request
/// construction (tenant key, version header, prompt caching, no sampling parameters), usage metering of cache tokens, retry/failure behaviour and that keys never leak. It does NOT prove behaviour against the live services.
/// </summary>
[Collection("app")]
public class AiProviderTests(AppFixture app)
{
    private static Action<IServiceCollection> RealAdapters(CapturingLoggerProvider? logs = null) => s =>
    {
        s.RemoveAll<IRawAIProvider>(); s.AddScoped<IRawAIProvider, ClaudeAIProvider>();
        s.RemoveAll<IRawEmbeddingProvider>(); s.AddScoped<IRawEmbeddingProvider, VoyageEmbeddingProvider>();
        if (logs is not null) s.AddLogging(b => b.AddProvider(logs));
    };

    private static Dictionary<string, string?> Urls(FakeProviderServer server) => new()
    {
        ["Ai:Claude:BaseUrl"] = server.BaseUrl, ["Ai:Voyage:BaseUrl"] = server.BaseUrl, ["Ai:Claude:RetryBaseDelayMs"] = "5", ["Ai:Voyage:RetryBaseDelayMs"] = "5", ["Ai:Claude:MaxRetries"] = "2", ["Ai:Claude:BreakerFailureThreshold"] = "100",
    };

    [RequiresMySqlFact]
    public async Task The_real_adapters_index_and_answer_with_the_tenants_own_keys_prompt_caching_and_metered_cache_tokens_and_leak_nothing_into_the_logs()
    {
        await using var server = new FakeProviderServer(); var logs = new CapturingLoggerProvider();
        await using var w = await AiWorld.OpenAsync(app, configureWorker: RealAdapters(logs), workerConfig: Urls(server));
        // the Web/Api hosts still use the scripted fakes; this worker composition uses the real adapters, so indexing is run by the worker's own job classes
        var created = await w.AddTextAsync("Standard plan", AiWorld.StandardPlan, DocumentKind.Pricing, index: false);
        await w.DrainAsync();
        Assert.Equal(IndexStatus.Indexed, (await w.GetAsync(created.Id)).Status);
        var voyage = server.Requests.Where(r => r.Path == "/v1/embeddings").ToList();
        Assert.NotEmpty(voyage); Assert.All(voyage, r => Assert.Equal("Bearer " + AiKit.EmbedKey, r.Auth));       // the TENANT'S key, from the encrypted settings
        using (var body = JsonDocument.Parse(voyage[0].Body)) { Assert.Equal("voyage-3.5", body.RootElement.GetProperty("model").GetString()); Assert.Equal("document", body.RootElement.GetProperty("input_type").GetString()); Assert.True(body.RootElement.GetProperty("truncation").GetBoolean()); }

        var customerText = "How much does the Standard plan cost for my school?";
        var run = await w.AskAsync(customerText);
        Assert.Equal(AiDecision.Draft, run.Decision); Assert.Contains("500 USD", run.ReplyText); Assert.Single(run.Citations);
        var claude = server.Requests.Where(r => r.Path == "/v1/messages").ToList(); Assert.Equal(2, claude.Count);           // triage + answer
        Assert.All(claude, r => { Assert.Equal(AiKit.ChatKey, r.ApiKey); Assert.Equal("2023-06-01", r.Version); });
        foreach (var r in claude)
        {
            using var doc = JsonDocument.Parse(r.Body); var root = doc.RootElement;
            var system = root.GetProperty("system"); Assert.Equal("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());   // the stable instructions are the cached prefix
            foreach (var banned in new[] { "temperature", "top_p", "top_k", "thinking", "budget_tokens", "fallbacks" }) Assert.False(root.TryGetProperty(banned, out _), banned);
            Assert.Equal(1, root.GetProperty("messages").GetArrayLength()); Assert.Equal("user", root.GetProperty("messages")[0].GetProperty("role").GetString());
        }
        Assert.Equal("claude-haiku-5-5", JsonDocument.Parse(claude[0].Body).RootElement.GetProperty("model").GetString()); Assert.Equal("claude-sonnet-5-5", JsonDocument.Parse(claude[1].Body).RootElement.GetProperty("model").GetString());

        // the response's cache and token numbers are what the ledger records
        var entries = (await w.UsageAsync()).Where(e => e.Operation is "classify" or "generate").ToList();
        Assert.All(entries, e => Assert.Equal((321, 45, 200, 0), (e.InputTokens, e.OutputTokens, e.CacheReadTokens, e.CacheWriteTokens)));
        Assert.All(entries, e => Assert.True(e.EstimatedCostUsd > 0));

        // neither key and not one word the customer wrote was logged
        var all = string.Join("\n", logs.Lines);
        Assert.DoesNotContain(AiKit.ChatKey, all); Assert.DoesNotContain(AiKit.EmbedKey, all); Assert.DoesNotContain("0123456789abcdef", all);
        Assert.DoesNotContain("Standard plan cost for my school", all); Assert.DoesNotContain("500 USD", all);
    }

    [RequiresMySqlFact]
    public async Task Rate_limits_are_retried_failures_are_classified_metered_and_never_expose_the_key()
    {
        await using var server = new FakeProviderServer(); var logs = new CapturingLoggerProvider();
        await using var w = await AiWorld.OpenAsync(app, configureWorker: RealAdapters(logs), workerConfig: Urls(server));
        await w.AddTextAsync("Standard plan", AiWorld.StandardPlan, index: false); await w.DrainAsync();

        // 429 then OK: retried inside the adapter, one successful ledger entry
        server.ClaudeScript = n => n == 1 ? (429, """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""", 0) : (200, ClaudeBody("hello"), 0);
        var ok = await w.InScope(sp => sp.GetRequiredService<IAIProvider>().CompleteAsync(new AiCompletionRequest("claude-haiku-5-5", "rules", null, [new AiMessage("user", "hi")], 50, "test"), default));
        Assert.Equal("hello", ok.Text); Assert.Equal(2, server.Requests.Count(r => r.Path == "/v1/messages"));
        Assert.Equal(1, (await w.UsageAsync()).Count(e => e.Operation == "test" && e.Success));

        // 401: no retry, classified, the failure is metered, the message names no key
        var before = server.Requests.Count(r => r.Path == "/v1/messages");
        server.ClaudeScript = _ => (401, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""", 0);
        var auth = await Assert.ThrowsAsync<AiProviderException>(() => w.InScope(sp => sp.GetRequiredService<IAIProvider>().CompleteAsync(new AiCompletionRequest("claude-haiku-5-5", "rules", null, [new AiMessage("user", "hi")], 50, "test"), default)));
        Assert.Equal(AiFailureKind.Auth, auth.Kind); Assert.False(auth.Retryable); Assert.Equal(before + 1, server.Requests.Count(r => r.Path == "/v1/messages")); Assert.DoesNotContain(AiKit.ChatKey, auth.Message);
        Assert.Contains(await w.UsageAsync(), e => !e.Success && e.ErrorCode == "Auth");

        // 500 on every attempt: bounded retries, then a retryable Unavailable
        before = server.Requests.Count(r => r.Path == "/v1/messages");
        server.ClaudeScript = _ => (500, "{}", 0);
        var down = await Assert.ThrowsAsync<AiProviderException>(() => w.InScope(sp => sp.GetRequiredService<IAIProvider>().CompleteAsync(new AiCompletionRequest("claude-haiku-5-5", "rules", null, [new AiMessage("user", "hi")], 50, "test"), default)));
        Assert.Equal((AiFailureKind.Unavailable, true), (down.Kind, down.Retryable)); Assert.Equal(before + 3, server.Requests.Count(r => r.Path == "/v1/messages"));

        // a response without text and one that is not JSON are bad responses, a refusal is final
        server.ClaudeScript = _ => (200, """{"content":[],"usage":{}}""", 0);
        Assert.Equal(AiFailureKind.BadResponse, (await Assert.ThrowsAsync<AiProviderException>(() => w.InScope(sp => sp.GetRequiredService<IAIProvider>().CompleteAsync(new AiCompletionRequest("claude-haiku-5-5", "r", null, [new AiMessage("user", "hi")], 50, "test"), default)))).Kind);
        server.ClaudeScript = _ => (200, """{"stop_reason":"refusal","content":[],"usage":{}}""", 0);
        var refused = await Assert.ThrowsAsync<AiProviderException>(() => w.InScope(sp => sp.GetRequiredService<IAIProvider>().CompleteAsync(new AiCompletionRequest("claude-haiku-5-5", "r", null, [new AiMessage("user", "hi")], 50, "test"), default)));
        Assert.Equal((AiFailureKind.Refused, false), (refused.Kind, refused.Retryable));

        // a customer message through the whole workflow while the provider is down: a visible escalation, no secrets in any log
        server.ClaudeScript = _ => (503, "{}", 0);
        var chat = await w.ChatAsync();
        // the Web/Api composition (scripted model) is not involved: the worker consumes the event with the real adapter against the failing server
        await chat.SendAsync("How much is the Standard plan?");
        var s = Assert.Single(await chat.InteractionsAsync()); Assert.Equal("ai_unavailable:Unavailable", s.EscalationReason);
        var all = string.Join("\n", logs.Lines); Assert.DoesNotContain(AiKit.ChatKey, all); Assert.DoesNotContain(AiKit.EmbedKey, all); Assert.DoesNotContain("How much is the Standard plan", all);
    }

    [RequiresMySqlFact]
    public async Task The_connection_test_uses_the_saved_keys_and_reports_success_or_the_provider_error()
    {
        await using var server = new FakeProviderServer();
        await using var w = await AiWorld.OpenAsync(app, configureWorker: RealAdapters(), workerConfig: Urls(server));
        var chat = await w.InScope(sp => sp.GetRequiredService<IAiSettingsService>().TestAsync(AiKeyKind.Chat, default));
        Assert.True(chat.Ok, chat.Message); Assert.Contains("claude-sonnet-5-5", chat.Message);
        var embed = await w.InScope(sp => sp.GetRequiredService<IAiSettingsService>().TestAsync(AiKeyKind.Embedding, default));
        Assert.True(embed.Ok, embed.Message); Assert.Contains($"{FakeEmbeddingProvider.Dimensions}-dimension", embed.Message);
        server.ClaudeScript = _ => (401, "{}", 0);
        var rejected = await w.InScope(sp => sp.GetRequiredService<IAiSettingsService>().TestAsync(AiKeyKind.Chat, default));
        Assert.False(rejected.Ok); Assert.Contains("rejected the API key", rejected.Message); Assert.DoesNotContain(AiKit.ChatKey, rejected.Message);
        // without a saved key there is nothing to test, and nothing is sent
        await using var bare = await AiWorld.OpenAsync(app, configure: false, configureWorker: RealAdapters(), workerConfig: Urls(server));
        var calls = server.Requests.Count;
        var none = await bare.InScope(sp => sp.GetRequiredService<IAiSettingsService>().TestAsync(AiKeyKind.Chat, default));
        Assert.False(none.Ok); Assert.Contains("No AI provider key", none.Message); Assert.Equal(calls, server.Requests.Count);
    }

    private static string ClaudeBody(string text) => ScriptedLlm.Json(new { id = "m", type = "message", role = "assistant", model = "claude-haiku-5-5", stop_reason = "end_turn", content = new[] { new { type = "text", text } }, usage = new { input_tokens = 10, output_tokens = 5 } });
}
