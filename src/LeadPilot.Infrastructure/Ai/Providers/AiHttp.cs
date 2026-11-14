using System.Collections.Concurrent;
using LeadPilot.Application.Ai;

namespace LeadPilot.Infrastructure.Ai.Providers;

/// <summary>
/// Consecutive-failure circuit breaker, keyed (per provider and tenant, so one tenant's bad key or exhausted quota never blocks another tenant). After <c>threshold</c> consecutive transient failures calls are refused
/// for <c>openFor</c>; the first call afterwards is a probe (half-open): success closes the breaker, failure re-opens it.
/// </summary>
public sealed class AiCircuitBreaker(TimeProvider time)
{
    private sealed class State { public int Failures; public DateTimeOffset OpenUntil; public bool Probing; }
    private readonly ConcurrentDictionary<string, State> _states = new();

    public void EnsureClosed(string key, string provider)
    {
        var s = _states.GetOrAdd(key, _ => new State());
        lock (s)
        {
            if (s.OpenUntil == default) return;
            if (time.GetUtcNow() < s.OpenUntil) throw new AiProviderException(AiFailureKind.CircuitOpen, $"The {provider} connection is paused after repeated failures; it will be retried shortly.", retryable: true);
            if (s.Probing) throw new AiProviderException(AiFailureKind.CircuitOpen, $"The {provider} connection is being re-checked.", retryable: true);
            s.Probing = true;
        }
    }

    public void Success(string key) { if (_states.TryGetValue(key, out var s)) lock (s) { s.Failures = 0; s.OpenUntil = default; s.Probing = false; } }

    public void TransientFailure(string key, int threshold, TimeSpan openFor)
    {
        var s = _states.GetOrAdd(key, _ => new State());
        lock (s)
        {
            s.Failures++;
            if (s.Probing || s.Failures >= Math.Max(1, threshold)) { s.OpenUntil = time.GetUtcNow() + openFor; s.Probing = false; }
        }
    }

    /// <summary>A non-transient failure (bad request, auth) says nothing about provider health: only release a probe.</summary>
    public void Release(string key) { if (_states.TryGetValue(key, out var s)) lock (s) s.Probing = false; }
}

/// <summary>Bounded retries with exponential backoff and jitter, <c>Retry-After</c> honoured (capped), a hard timeout per attempt, and the circuit breaker. Used by every AI HTTP adapter.</summary>
public sealed class AiHttpPipeline(HttpClient http, AiCircuitBreaker breaker, TimeProvider time)
{
    public async Task<HttpResponseMessage> SendAsync(string breakerKey, string provider, Func<HttpRequestMessage> build, int maxRetries, int baseDelayMs, TimeSpan timeout, int breakerThreshold, TimeSpan breakerOpen, CancellationToken ct)
    {
        breaker.EnsureClosed(breakerKey, provider);
        AiProviderException? last = null;
        for (var attempt = 0; attempt <= Math.Max(0, maxRetries); attempt++)
        {
            ct.ThrowIfCancellationRequested();
            TimeSpan? retryAfter = null;
            try
            {
                using var request = build();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                HttpResponseMessage response;
                try { response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AiProviderException(AiFailureKind.Timeout, $"{provider} did not answer within {timeout.TotalSeconds:0} seconds.", true); }
                catch (HttpRequestException) { throw new AiProviderException(AiFailureKind.Unavailable, $"{provider} could not be reached.", true); }

                var status = (int)response.StatusCode;
                if (status is >= 200 and < 300) { breaker.Success(breakerKey); return response; }
                retryAfter = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date is { } d ? d - time.GetUtcNow() : null);
                var kind = Classify(status);
                response.Dispose();
                throw new AiProviderException(kind.Kind, kind.Message(provider, status), kind.Retryable, status);
            }
            catch (AiProviderException ex) when (ex.Retryable && ex.Kind != AiFailureKind.CircuitOpen)
            {
                last = ex;
                if (ex.Kind is AiFailureKind.RateLimited or AiFailureKind.Overloaded or AiFailureKind.Unavailable or AiFailureKind.Timeout) breaker.TransientFailure(breakerKey, breakerThreshold, breakerOpen);
                if (attempt >= maxRetries) break;
                var delay = retryAfter is { } ra && ra > TimeSpan.Zero ? TimeSpan.FromSeconds(Math.Min(ra.TotalSeconds, 10)) : Backoff(baseDelayMs, attempt);
                try { await Task.Delay(delay, time, ct); } catch (OperationCanceledException) { throw; }
            }
            catch (AiProviderException ex)
            {
                if (ex.Kind is AiFailureKind.RateLimited or AiFailureKind.Overloaded or AiFailureKind.Unavailable or AiFailureKind.Timeout) breaker.TransientFailure(breakerKey, breakerThreshold, breakerOpen); else breaker.Release(breakerKey);
                throw;
            }
        }
        throw last ?? new AiProviderException(AiFailureKind.Unavailable, $"{provider} is unavailable.", true);
    }

    private static TimeSpan Backoff(int baseMs, int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(15_000, baseMs * Math.Pow(2, attempt)) * (0.75 + Random.Shared.NextDouble() * 0.5));

    private sealed record Classified(AiFailureKind Kind, bool Retryable, Func<string, int, string> Message);

    private static Classified Classify(int status) => status switch
    {
        401 or 403 => new(AiFailureKind.Auth, false, (p, s) => $"{p} rejected the API key (HTTP {s}). Check the key and its permissions."),
        400 or 404 or 413 or 422 => new(AiFailureKind.InvalidRequest, false, (p, s) => $"{p} rejected the request (HTTP {s}); check the model name."),
        429 => new(AiFailureKind.RateLimited, true, (p, s) => $"{p} rate limit or quota reached (HTTP 429)."),
        408 or 409 => new(AiFailureKind.Unavailable, true, (p, s) => $"{p} asked to retry (HTTP {s})."),
        529 => new(AiFailureKind.Overloaded, true, (p, s) => $"{p} is overloaded (HTTP 529)."),
        >= 500 => new(AiFailureKind.Unavailable, true, (p, s) => $"{p} is unavailable (HTTP {s})."),
        _ => new(AiFailureKind.InvalidRequest, false, (p, s) => $"{p} answered HTTP {s}."),
    };
}
