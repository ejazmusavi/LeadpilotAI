using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Discovery.Website;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Messaging.Waha;

/// <summary>
/// WAHA (WhatsApp HTTP API) adapter for <see cref="IWhatsAppGateway"/>. Endpoints, headers and payloads come from https://waha.devlike.pro/docs/ (see docs/INTEGRATIONS.md);
/// anything the docs leave open is handled defensively. WAHA types never leave this namespace.
/// Resilience: GET/management calls retry with backoff on transient failures; SENDS are never retried here (WAHA has no idempotency key, so a repeat can duplicate a message);
/// a per-host circuit breaker stops hammering a dead gateway; every failure is a typed <see cref="GatewayException"/>.
/// </summary>
public sealed class WahaGateway(WahaHttpClientFactory clients, WahaCircuitBreaker breaker, IOptions<WahaOptions> options, ILogger<WahaGateway> logger) : IWhatsAppGateway
{
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private sealed record HttpResult(HttpStatusCode Status, byte[] Bytes, string? ContentType)
    {
        public string Text => Encoding.UTF8.GetString(Bytes);
    }

    // ------------------------------------------------------------------ sessions

    public async Task<IReadOnlyList<GatewaySession>> GetAccountsAsync(GatewayConnection c, CancellationToken ct = default)
    {
        var res = await SendAsync(c, HttpMethod.Get, "/api/sessions?all=true", null, "application/json", idempotent: true, ct);
        using var doc = ParseJson(res);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new GatewayException(GatewayFailureKind.OutcomeUnknown, "WAHA returned an unexpected session list.", (int)res.Status);
        return doc.RootElement.EnumerateArray().Select(ToSession).Where(s => s is not null).Select(s => s!).ToList();
    }

    public async Task<GatewaySession?> GetAccountStatusAsync(GatewayConnection c, CancellationToken ct = default)
    {
        try
        {
            var res = await SendAsync(c, HttpMethod.Get, $"/api/sessions/{Seg(c.AccountId)}", null, "application/json", idempotent: true, ct);
            using var doc = ParseJson(res);
            return ToSession(doc.RootElement);
        }
        catch (GatewayException ex) when (ex.Kind == GatewayFailureKind.NotFound) { return null; }
    }

    public async Task<GatewaySession> ConnectAccountAsync(GatewayConnection c, GatewayWebhookRegistration webhook, CancellationToken ct = default)
    {
        var config = new Dictionary<string, object?>
        {
            ["webhooks"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["url"] = webhook.Url, ["events"] = webhook.Events, ["hmac"] = new { key = webhook.Secret },
                    ["retries"] = new { policy = "exponential", delaySeconds = 2, attempts = 8 },
                },
            },
            ["metadata"] = new Dictionary<string, string> { ["app"] = "LeadPilotAI" },
            // only direct chats are handled: do not even send group/channel/status/broadcast traffic
            ["ignore"] = new { status = true, groups = true, channels = true, broadcast = true },
        };
        if (webhook.EnableAckTracking) config["webjs"] = new { tagsEventsOn = true };   // WEBJS only emits message.ack with this flag; other engines ignore it

        var existing = await GetAccountStatusAsync(c, ct);
        if (existing is null)
        {
            await SendAsync(c, HttpMethod.Post, "/api/sessions", new { name = c.AccountId, start = true, config }, "application/json", idempotent: true, ct);
        }
        else
        {
            // Docs are inconsistent about the verb (prose: PUT /api/sessions/{name}; feature table: POST .../{name}/). PUT is what the examples use.
            await SendAsync(c, HttpMethod.Put, $"/api/sessions/{Seg(c.AccountId)}", new { name = c.AccountId, config }, "application/json", idempotent: true, ct);
            if (existing.Status is GatewaySessionStatus.Stopped or GatewaySessionStatus.Failed)
                await SendAsync(c, HttpMethod.Post, $"/api/sessions/{Seg(c.AccountId)}/start", null, "application/json", idempotent: true, ct);
        }
        return await GetAccountStatusAsync(c, ct) ?? throw new GatewayException(GatewayFailureKind.OutcomeUnknown, "The session was created but WAHA does not list it.");
    }

    public async Task<GatewayQrCode> GetQrCodeAsync(GatewayConnection c, CancellationToken ct = default)
    {
        var res = await SendAsync(c, HttpMethod.Get, $"/api/{Seg(c.AccountId)}/auth/qr?format=image", null, "application/json", idempotent: true, ct);
        if (res.Bytes.Length > 0 && res.Bytes[0] == (byte)'{')
        {
            using var doc = ParseJson(res);
            var r = doc.RootElement;
            if (r.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String && TryBase64(data.GetString(), out var png)) return new GatewayQrCode(null, png);
            if (r.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String) return new GatewayQrCode(value.GetString(), null);
            throw new GatewayException(GatewayFailureKind.OutcomeUnknown, "WAHA returned a QR response without an image.", (int)res.Status);
        }
        if (res.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true && res.Bytes.Length > 0) return new GatewayQrCode(null, res.Bytes);
        throw new GatewayException(GatewayFailureKind.OutcomeUnknown, "WAHA returned an unreadable QR response.", (int)res.Status);
    }

    public Task DisconnectAccountAsync(GatewayConnection c, bool logout, CancellationToken ct = default) =>
        SendAsync(c, HttpMethod.Post, $"/api/sessions/{Seg(c.AccountId)}/{(logout ? "logout" : "stop")}", null, "application/json", idempotent: true, ct);

    // ------------------------------------------------------------------ sending

    public async Task<GatewaySendResult> SendTextAsync(GatewayConnection c, GatewaySendRequest request, CancellationToken ct = default)
    {
        if (!ChatIds.IsDirectChat(request.ChatId)) throw new GatewayException(GatewayFailureKind.Rejected, "Only direct chats can be messaged.");
        var body = new { session = c.AccountId, chatId = request.ChatId, text = request.Text, reply_to = request.ReplyToExternalId };
        // NOT idempotent: no retry. The caller owns the idempotency/reconciliation story.
        var res = await SendAsync(c, HttpMethod.Post, "/api/sendText", body, "application/json", idempotent: false, ct);
        using var doc = ParseJson(res);
        var id = ExtractMessageId(doc.RootElement, request.ChatId)
                 ?? throw new GatewayException(GatewayFailureKind.OutcomeUnknown, "WAHA accepted the request but returned no message id; the message may have been sent.", (int)res.Status);
        var ts = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetDouble(out var d) ? WahaWebhookNormalizer.FromUnixSeconds(d) : null;
        return new GatewaySendResult(id, ts);
    }

    public Task<GatewaySendResult> SendTemplateAsync(GatewayConnection c, GatewayTemplateSend request, CancellationToken ct = default)
    {
        string text;
        try { text = TemplateRenderer.Render(request.TemplateBody, request.Variables); }
        catch (TemplateRenderException ex) { throw new GatewayException(GatewayFailureKind.Rejected, "The template could not be filled in: " + ex.Message); }
        return SendTextAsync(c, new GatewaySendRequest(request.ChatId, text), ct);
    }

    public Task MarkAsReadAsync(GatewayConnection c, string chatId, IReadOnlyList<string> externalMessageIds, CancellationToken ct = default) =>
        SendAsync(c, HttpMethod.Post, "/api/sendSeen", new { session = c.AccountId, chatId, messageIds = externalMessageIds.Count == 0 ? null : externalMessageIds }, "application/json", idempotent: true, ct);

    public Task SetTypingAsync(GatewayConnection c, string chatId, bool typing, CancellationToken ct = default) =>
        SendAsync(c, HttpMethod.Post, typing ? "/api/startTyping" : "/api/stopTyping", new { session = c.AccountId, chatId }, "application/json", idempotent: true, ct);

    // ------------------------------------------------------------------ lookup

    public async Task<GatewayMessageInfo?> GetMessageStatusAsync(GatewayConnection c, string chatId, string externalMessageId, CancellationToken ct = default)
    {
        try
        {
            var res = await SendAsync(c, HttpMethod.Get, $"/api/{Seg(c.AccountId)}/chats/{Seg(chatId)}/messages/{Seg(externalMessageId)}?downloadMedia=false", null, "application/json", idempotent: true, ct);
            using var doc = ParseJson(res);
            return ToMessageInfo(doc.RootElement, chatId);
        }
        catch (GatewayException ex) when (ex.Kind == GatewayFailureKind.NotFound) { return null; }
    }

    public async Task<GatewayMessageInfo?> FindSentMessageAsync(GatewayConnection c, string chatId, DateTime sinceUtc, string body, CancellationToken ct = default)
    {
        var since = new DateTimeOffset(DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var path = $"/api/{Seg(c.AccountId)}/chats/{Seg(chatId)}/messages?limit=30&downloadMedia=false&filter.fromMe=true&filter.timestamp.gte={since}";
        try
        {
            var res = await SendAsync(c, HttpMethod.Get, path, null, "application/json", idempotent: true, ct);
            using var doc = ParseJson(res);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            return doc.RootElement.EnumerateArray().Select(e => ToMessageInfo(e, chatId))
                .Where(m => m is { FromMe: true } && string.Equals(m.Body?.Trim(), body.Trim(), StringComparison.Ordinal))
                .OrderByDescending(m => m!.TimestampUtc).FirstOrDefault();
        }
        catch (GatewayException ex) when (ex.Kind == GatewayFailureKind.NotFound) { return null; }
    }

    // ------------------------------------------------------------------ webhooks

    public Task<WebhookProcessingResult> ProcessWebhookAsync(WebhookRequest request, string secret, DateTime nowUtc, CancellationToken ct = default)
    {
        string? Header(string name) => request.Headers.TryGetValue(name, out var v) ? v : null;

        var signature = Header("X-Webhook-Hmac");
        if (string.IsNullOrWhiteSpace(signature)) return Task.FromResult(WebhookProcessingResult.Rejected(WebhookRejection.MissingSignature));
        var algorithm = Header("X-Webhook-Hmac-Algorithm");
        if (!string.IsNullOrWhiteSpace(algorithm) && !string.Equals(algorithm.Trim(), WahaSignature.Algorithm, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(WebhookProcessingResult.Rejected(WebhookRejection.UnsupportedAlgorithm, algorithm.Length > 20 ? algorithm[..20] : algorithm));
        if (!WahaSignature.Verify(secret, request.Body, signature)) return Task.FromResult(WebhookProcessingResult.Rejected(WebhookRejection.InvalidSignature));

        // The signature does not cover the timestamp header (WAHA scheme), so this is only a coarse freshness check: it must stay wider than WAHA's retry horizon.
        if (Header("X-Webhook-Timestamp") is { } tsRaw)
        {
            if (!long.TryParse(tsRaw, out var ms) || WahaWebhookNormalizer.FromUnixMs(ms) is not { } sent)
                return Task.FromResult(WebhookProcessingResult.Rejected(WebhookRejection.StaleTimestamp, "unreadable timestamp"));
            if ((nowUtc - sent).Duration() > TimeSpan.FromMinutes(Math.Max(1, options.Value.WebhookToleranceMinutes)))
                return Task.FromResult(WebhookProcessingResult.Rejected(WebhookRejection.StaleTimestamp));
        }
        return Task.FromResult(WahaWebhookNormalizer.Normalize(request.Body));
    }

    // ------------------------------------------------------------------ http core

    private async Task<HttpResult> SendAsync(GatewayConnection c, HttpMethod method, string path, object? json, string accept, bool idempotent, CancellationToken ct)
    {
        if (!WahaEndpoint.TryNormalize(c.BaseUrl, out var baseUrl, out var urlError)) throw new GatewayException(GatewayFailureKind.UnsafeEndpoint, urlError);
        var uri = new Uri(baseUrl + path);
        var host = uri.Authority;
        var attempts = idempotent ? 1 + Math.Clamp(options.Value.ReadRetries, 0, 5) : 1;

        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (breaker.IsOpen(host)) throw new GatewayException(GatewayFailureKind.Unreachable, "The WhatsApp gateway is temporarily marked unavailable after repeated failures; nothing was sent.");
            GatewayException failure;
            try
            {
                using var req = new HttpRequestMessage(method, uri);
                req.Headers.TryAddWithoutValidation("X-Api-Key", c.ApiKey);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
                if (json is not null) req.Content = new StringContent(JsonSerializer.Serialize(json, JsonOpts), Encoding.UTF8, "application/json");
                using var resp = await clients.Create().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                var bytes = await ReadCappedAsync(resp, ct);
                var result = new HttpResult(resp.StatusCode, bytes, resp.Content.Headers.ContentType?.MediaType);
                if (resp.IsSuccessStatusCode) { breaker.Success(host); return result; }
                failure = Classify(result);
            }
            catch (GatewayException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex) { failure = new GatewayException(GatewayFailureKind.OutcomeUnknown, "The gateway did not answer in time.", null, ex); }
            catch (HttpRequestException ex) { failure = ClassifyTransport(ex); }

            if (failure.Kind is GatewayFailureKind.Unreachable or GatewayFailureKind.OutcomeUnknown) breaker.Failure(host);
            var retryable = idempotent && attempt < attempts && (failure.Kind is GatewayFailureKind.Unreachable or GatewayFailureKind.RateLimited or GatewayFailureKind.OutcomeUnknown);
            if (!retryable) { logger.LogWarning("WAHA call {Method} {Path} failed: {Kind} (HTTP {Status})", method, path.Split('?')[0], failure.Kind, failure.HttpStatus); throw failure; }
            var delay = TimeSpan.FromMilliseconds(options.Value.RetryBaseDelayMs * Math.Pow(3, attempt - 1));
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024]; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > MaxResponseBytes) throw new GatewayException(GatewayFailureKind.OutcomeUnknown, "The gateway response is too large.", (int)resp.StatusCode);
        }
        return ms.ToArray();
    }

    /// <summary>HTTP status → typed failure. 463/475 text means WhatsApp itself refused the message (nothing was sent), whatever status WAHA wrapped it in.</summary>
    private static GatewayException Classify(HttpResult r) => Classify((int)r.Status, r.Text);
    /// <summary>Classifies an HTTP failure of a gateway call (public so the mapping is unit-tested).</summary>

    public static GatewayException Classify(int status, string bodyText)
    {
        var detail = Sanitize(bodyText);
        var lower = detail.ToLowerInvariant();
        if (lower.Contains("error 463") || lower.Contains("error 475") || lower.Contains("timelock") || lower.Contains("capping"))
            return new GatewayException(GatewayFailureKind.NewContactRestricted, "WhatsApp is not accepting messages to new contacts from this number right now. " + detail, status);
        return status switch
        {
            401 or 403 => new GatewayException(GatewayFailureKind.Unauthorized, "WAHA rejected the API key.", status),
            404 => new GatewayException(GatewayFailureKind.NotFound, "WAHA does not know that session or chat. " + detail, status),
            408 => new GatewayException(GatewayFailureKind.OutcomeUnknown, "WAHA timed out. " + detail, status),
            429 => new GatewayException(GatewayFailureKind.RateLimited, "WAHA is rate limiting requests.", status),
            >= 500 => new GatewayException(GatewayFailureKind.OutcomeUnknown, $"WAHA answered HTTP {status}. " + detail, status),
            _ => new GatewayException(GatewayFailureKind.Rejected, $"WAHA refused the request (HTTP {status}). " + detail, status),
        };
    }

    private static GatewayException ClassifyTransport(HttpRequestException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is SsrfBlockedException) return new GatewayException(GatewayFailureKind.UnsafeEndpoint, "That address is not allowed for a WhatsApp gateway.", null, ex);
        // The request never left (could not resolve/connect/negotiate TLS): nothing was sent, safe to repeat. Anything else mid-flight may have been processed.
        return ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError || ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.HostNotFound or SocketError.HostUnreachable or SocketError.NetworkUnreachable }
            ? new GatewayException(GatewayFailureKind.Unreachable, "The WhatsApp gateway could not be reached.", null, ex)
            : new GatewayException(GatewayFailureKind.OutcomeUnknown, "The connection to the WhatsApp gateway broke while the request was in flight.", null, ex);
    }

    internal static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var t = new string(text.Where(ch => !char.IsControl(ch) || ch == ' ').ToArray()).Trim();
        return t.Length > 200 ? t[..200] : t;
    }

    // ------------------------------------------------------------------ parsing helpers

    private static string Seg(string s) => Uri.EscapeDataString(s);

    private static JsonDocument ParseJson(HttpResult r)
    {
        try { return JsonDocument.Parse(r.Bytes.Length == 0 ? "null"u8.ToArray() : r.Bytes); }
        catch (JsonException ex) { throw new GatewayException(GatewayFailureKind.OutcomeUnknown, "WAHA returned a response that is not valid JSON.", (int)r.Status, ex); }
    }

    private static bool TryBase64(string? s, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(s)) return false;
        try { bytes = Convert.FromBase64String(s); return bytes.Length > 0; } catch (FormatException) { return false; }
    }

    private static GatewaySession? ToSession(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) return null;
        var raw = e.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        string? phone = null, push = null, engine = null;
        if (e.TryGetProperty("me", out var me) && me.ValueKind == JsonValueKind.Object)
        {
            phone = me.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? ChatIds.ToE164(id.GetString()) : null;
            push = me.TryGetProperty("pushName", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() : null;
        }
        if (e.TryGetProperty("engine", out var eng) && eng.ValueKind == JsonValueKind.Object && eng.TryGetProperty("engine", out var en) && en.ValueKind == JsonValueKind.String) engine = en.GetString();
        return new GatewaySession(n.GetString()!, WahaWebhookNormalizer.MapSessionStatus(raw), phone, push, engine, raw);
    }

    /// <summary>The sendText response shape is not shown in the prose docs. Accepts: {"id":"true_chat_HEX"}, {"id":{"_serialized":"…"}}, {"_data":{"id":{"_serialized":"…"}}}, or a bare HEX id in _data.key.id / id.id (rebuilt with the chat id).</summary>
    public static string? ExtractMessageId(JsonElement root, string chatId)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("id", out var id))
        {
            if (id.ValueKind == JsonValueKind.String && IsPlausibleId(id.GetString())) return id.GetString();
            if (id.ValueKind == JsonValueKind.Object)
            {
                if (id.TryGetProperty("_serialized", out var ser) && ser.ValueKind == JsonValueKind.String && IsPlausibleId(ser.GetString())) return ser.GetString();
                if (id.TryGetProperty("id", out var hex) && hex.ValueKind == JsonValueKind.String && IsHex(hex.GetString())) return $"true_{chatId}_{hex.GetString()}";
            }
        }
        if (root.TryGetProperty("_data", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            if (d.TryGetProperty("id", out var did) && did.ValueKind == JsonValueKind.Object && did.TryGetProperty("_serialized", out var ds) && ds.ValueKind == JsonValueKind.String && IsPlausibleId(ds.GetString())) return ds.GetString();
            if (d.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.Object && key.TryGetProperty("id", out var kid) && kid.ValueKind == JsonValueKind.String && IsHex(kid.GetString())) return $"true_{chatId}_{kid.GetString()}";
        }
        return null;
    }

    private static bool IsPlausibleId(string? s) => !string.IsNullOrWhiteSpace(s) && s.Length <= 200 && !s.Any(char.IsWhiteSpace);
    private static bool IsHex(string? s) => !string.IsNullOrEmpty(s) && s.Length is >= 8 and <= 64 && s.All(char.IsAsciiHexDigit);

    private static GatewayMessageInfo? ToMessageInfo(JsonElement e, string chatId)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var id = ExtractMessageId(e, chatId);
        if (id is null) return null;
        var fromMe = e.TryGetProperty("fromMe", out var fm) && fm.ValueKind == JsonValueKind.True;
        var body = e.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        DateTime? ts = e.TryGetProperty("timestamp", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetDouble(out var d) ? WahaWebhookNormalizer.FromUnixSeconds(d) : null;
        int? ack = e.TryGetProperty("ack", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var ai) ? ai : null;
        return new GatewayMessageInfo(id, fromMe, body, ts, ack is { } v ? AckRules.ToDelivery(v) : null, ack);
    }
}
