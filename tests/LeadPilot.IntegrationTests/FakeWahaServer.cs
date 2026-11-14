using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Messaging;
using LeadPilot.Infrastructure.Messaging.Waha;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LeadPilot.IntegrationTests;

/// <summary>
/// TEST-ONLY stand-in for a WAHA server, built from the shapes in the documentation (docs/INTEGRATIONS.md): X-Api-Key auth, /api/sessions*, /api/{session}/auth/qr,
/// /api/sendText, sendSeen, startTyping/stopTyping, chat message lookup. It is a real HTTP server on a loopback port, so the production HttpClient, timeouts and error mapping are exercised.
/// It lives in the test assembly and is never registered by any host. It proves our adapter against the DOCUMENTED behaviour, not against a live WAHA.
/// </summary>
public sealed class FakeWahaServer : IAsyncDisposable
{
    public const string ApiKey = "test-waha-api-key-0001";

    public enum SendFault { Http500NotSent, Http500ButSent, TimeoutNotSent, TimeoutButSent, Http429, Http422Error463, Http401, NoIdButSent, ConnectionDroppedButSent, Http400 }

    public sealed record Call(string Method, string PathAndQuery, string? Body, string? ApiKeyHeader);
    public sealed record Sent(string Id, string Session, string ChatId, string Text, long TimestampSeconds, int Ack);
    public sealed class SessionState
    {
        public string Name = ""; public string Status = "STOPPED"; public string? MeId; public string? PushName; public string Engine = "GOWS";
        public JsonElement? Config; public string? WebhookUrl; public string? WebhookHmacKey; public List<string> WebhookEvents = [];
    }

    private WebApplication? _app;
    private readonly ConcurrentQueue<SendFault> _faults = new();
    public ConcurrentQueue<Call> Calls { get; } = new();
    public ConcurrentDictionary<string, SessionState> Sessions { get; } = new();
    public ConcurrentQueue<Sent> SentMessages { get; } = new();
    public string BaseUrl { get; private set; } = "";
    public volatile bool Offline;      // when true the server answers every request with a dropped connection
    public string RequiredKey = ApiKey;
    public int SendDelayMs;            // delay the (successful) send answer

    public static async Task<FakeWahaServer> StartAsync()
    {
        var s = new FakeWahaServer();
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        b.Logging.ClearProviders();
        s._app = b.Build();
        s.Map(s._app);
        await s._app.StartAsync();
        s.BaseUrl = s._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        return s;
    }

    public void FaultNext(params SendFault[] faults) { foreach (var f in faults) _faults.Enqueue(f); }
    public void ClearFaults() { while (_faults.TryDequeue(out _)) { } }

    public SessionState Working(string name, string phone = "923009990000")
    {
        var st = Sessions.GetOrAdd(name, n => new SessionState { Name = n });
        st.Status = "WORKING"; st.MeId = phone + "@c.us"; st.PushName = "Fake WAHA";
        return st;
    }

    public IEnumerable<Sent> SentTo(string chatId) => SentMessages.Where(m => m.ChatId == chatId);
    public IEnumerable<Call> CallsTo(string pathPrefix) => Calls.Where(c => c.PathAndQuery.StartsWith(pathPrefix, StringComparison.Ordinal));
    /// <summary>Puts a message into the fake as if WAHA had sent it earlier (for crash-window tests where our process died before saving the id).</summary>
    public Sent AddSent(string session, string chatId, string text) => Record(session, chatId, text);
    public void SetAck(string messageId, int ack) => _acks[messageId] = ack;
    private readonly ConcurrentDictionary<string, int> _acks = new();

    private static string NewHex() => Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToUpperInvariant();

    private void Map(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            string? body = null;
            if (ctx.Request.ContentLength > 0 || ctx.Request.Method is "POST" or "PUT")
            {
                ctx.Request.EnableBuffering();
                using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8, leaveOpen: true);
                body = await reader.ReadToEndAsync();
                ctx.Request.Body.Position = 0;
            }
            Calls.Enqueue(new Call(ctx.Request.Method, ctx.Request.Path + ctx.Request.QueryString, body, ctx.Request.Headers["X-Api-Key"].FirstOrDefault()));
            if (Offline) { ctx.Abort(); return; }
            if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Headers["X-Api-Key"].FirstOrDefault() != RequiredKey)
            {
                ctx.Response.StatusCode = 401; ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("""{"statusCode":401,"message":"Unauthorized"}""");
                return;
            }
            await next();
        });

        app.MapGet("/api/sessions", (HttpContext c) => Results.Json(Sessions.Values.Where(s => c.Request.Query["all"] == "true" || s.Status != "STOPPED").Select(ToJson).ToList()));
        app.MapGet("/api/sessions/{name}", (string name) => Sessions.TryGetValue(name, out var s) ? Results.Json(ToJson(s)) : NotFound($"Session '{name}' does not exist"));
        app.MapPost("/api/sessions", async (HttpContext c) =>
        {
            using var doc = await JsonDocument.ParseAsync(c.Request.Body);
            var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString()! : "session_" + NewHex()[..6];
            if (Sessions.ContainsKey(name)) return Results.Json(new { statusCode = 422, message = $"Session '{name}' already exists" }, statusCode: 422);
            var s = new SessionState { Name = name, Status = (doc.RootElement.TryGetProperty("start", out var st) && st.ValueKind == JsonValueKind.False) ? "STOPPED" : "SCAN_QR_CODE" };
            ApplyConfig(s, doc.RootElement);
            Sessions[name] = s;
            return Results.Json(ToJson(s), statusCode: 201);
        });
        app.MapPut("/api/sessions/{name}", async (string name, HttpContext c) =>
        {
            if (!Sessions.TryGetValue(name, out var s)) return NotFound($"Session '{name}' does not exist");
            using var doc = await JsonDocument.ParseAsync(c.Request.Body);
            ApplyConfig(s, doc.RootElement);
            return Results.Json(ToJson(s));
        });
        app.MapPost("/api/sessions/{name}/start", (string name) => Sessions.TryGetValue(name, out var s) ? Mutate(s, "SCAN_QR_CODE") : NotFound("no session"));
        app.MapPost("/api/sessions/{name}/stop", (string name) => Sessions.TryGetValue(name, out var s) ? Mutate(s, "STOPPED") : NotFound("no session"));
        app.MapPost("/api/sessions/{name}/logout", (string name) => Sessions.TryGetValue(name, out var s) ? Mutate(s, "STOPPED", clearMe: true) : NotFound("no session"));
        app.MapDelete("/api/sessions/{name}", (string name) => Sessions.TryRemove(name, out _) ? Results.Ok() : NotFound("no session"));

        app.MapGet("/api/{session}/auth/qr", (string session, HttpContext c) =>
        {
            if (!Sessions.TryGetValue(session, out var s)) return NotFound("no session");
            if (s.Status != "SCAN_QR_CODE") return Results.Json(new { statusCode = 422, message = "Session is not waiting for a QR code" }, statusCode: 422);
            if (c.Request.Query["format"] == "raw") return Results.Json(new { value = "2@FAKEQR," + NewHex() });
            if (c.Request.Headers.Accept.ToString().Contains("application/json")) return Results.Json(new { mimetype = "image/png", data = Convert.ToBase64String(TinyPng) });
            return Results.Bytes(TinyPng, "image/png");
        });

        app.MapPost("/api/sendText", async (HttpContext c) =>
        {
            using var doc = await JsonDocument.ParseAsync(c.Request.Body);
            var session = doc.RootElement.GetProperty("session").GetString()!; var chat = doc.RootElement.GetProperty("chatId").GetString()!; var text = doc.RootElement.GetProperty("text").GetString()!;
            if (!Sessions.TryGetValue(session, out var s)) return NotFound($"Session '{session}' does not exist");
            if (s.Status != "WORKING") return Results.Json(new { statusCode = 422, message = $"Session status is not as expected [expected: WORKING, actual: {s.Status}]" }, statusCode: 422);

            if (_faults.TryDequeue(out var fault))
            {
                switch (fault)
                {
                    case SendFault.Http401: return Results.Json(new { statusCode = 401, message = "Unauthorized" }, statusCode: 401);
                    case SendFault.Http429: return Results.Json(new { statusCode = 429, message = "Too Many Requests" }, statusCode: 429);
                    case SendFault.Http400: return Results.Json(new { statusCode = 400, message = "chatId is invalid" }, statusCode: 400);
                    case SendFault.Http422Error463: return Results.Json(new { statusCode = 422, message = "server returned error 463" }, statusCode: 422);
                    case SendFault.Http500NotSent: return Results.Json(new { statusCode = 500, message = "Internal server error" }, statusCode: 500);
                    case SendFault.TimeoutNotSent: await Task.Delay(TimeSpan.FromSeconds(4.5)); return Results.Json(new { }, statusCode: 504);
                    case SendFault.Http500ButSent: Record(session, chat, text); return Results.Json(new { statusCode = 500, message = "Internal server error" }, statusCode: 500);
                    case SendFault.TimeoutButSent: Record(session, chat, text); await Task.Delay(TimeSpan.FromSeconds(4.5)); return Results.Json(new { }, statusCode: 200);
                    case SendFault.NoIdButSent: Record(session, chat, text); return Results.Json(new { });
                    case SendFault.ConnectionDroppedButSent: Record(session, chat, text); c.Abort(); return Results.Empty;
                }
            }
            if (SendDelayMs > 0) await Task.Delay(SendDelayMs);
            var sent = Record(session, chat, text);
            return Results.Json(new { id = sent.Id, timestamp = sent.TimestampSeconds, from = (s.MeId ?? ""), fromMe = true, to = chat, body = text, hasMedia = false, ack = 1, vCards = new object[0], _data = new { } });
        });
        app.MapPost("/api/sendSeen", () => Results.Json(new { }));
        app.MapPost("/api/startTyping", () => Results.Json(new { }));
        app.MapPost("/api/stopTyping", () => Results.Json(new { }));

        app.MapGet("/api/{session}/chats/{chatId}/messages", (string session, string chatId, HttpContext c) =>
        {
            var q = c.Request.Query;
            var since = long.TryParse(q["filter.timestamp.gte"], out var g) ? g : 0;
            var items = SentMessages.Where(m => m.Session == session && m.ChatId == chatId && m.TimestampSeconds >= since).OrderByDescending(m => m.TimestampSeconds)
                .Select(m => MessageJson(m)).ToList();
            return Results.Json(items);
        });
        app.MapGet("/api/{session}/chats/{chatId}/messages/{messageId}", (string session, string chatId, string messageId) =>
        {
            var m = SentMessages.FirstOrDefault(x => x.Session == session && x.ChatId == chatId && x.Id == messageId);
            return m is null ? NotFound("message not found") : Results.Json(MessageJson(m));
        });
        app.MapGet("/api/contacts/check-exists", (string phone) => Results.Json(new { numberExists = true, chatId = phone + "@c.us", pn = (string?)null }));
    }

    private Sent Record(string session, string chat, string text)
    {
        var m = new Sent("true_" + chat + "_" + NewHex(), session, chat, text, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1);
        SentMessages.Enqueue(m);
        return m;
    }

    private IResult NotFound(string message) => Results.Json(new { statusCode = 404, message }, statusCode: 404);

    private IResult Mutate(SessionState s, string status, bool clearMe = false)
    {
        s.Status = status; if (clearMe) { s.MeId = null; }
        return Results.Json(ToJson(s));
    }

    private static void ApplyConfig(SessionState s, JsonElement root)
    {
        if (!root.TryGetProperty("config", out var cfg) || cfg.ValueKind != JsonValueKind.Object) return;
        s.Config = cfg.Clone();
        if (cfg.TryGetProperty("webhooks", out var hooks) && hooks.ValueKind == JsonValueKind.Array && hooks.GetArrayLength() > 0)
        {
            var h = hooks[0];
            s.WebhookUrl = h.GetProperty("url").GetString();
            s.WebhookEvents = h.GetProperty("events").EnumerateArray().Select(e => e.GetString()!).ToList();
            s.WebhookHmacKey = h.TryGetProperty("hmac", out var hm) && hm.ValueKind == JsonValueKind.Object ? hm.GetProperty("key").GetString() : null;
        }
    }

    private static object ToJson(SessionState s) => new
    {
        name = s.Name, status = s.Status, config = new { proxy = (object?)null, webhooks = new object[0], debug = false },
        me = s.MeId is null ? null : new { id = s.MeId, pushName = s.PushName }, engine = new { engine = s.Engine },
    };

    private object MessageJson(Sent m) => new
    {
        id = m.Id, timestamp = m.TimestampSeconds, from = m.Session, fromMe = true, to = m.ChatId, body = m.Text, hasMedia = false,
        ack = _acks.TryGetValue(m.Id, out var a) ? a : m.Ack, ackName = "SERVER", replyTo = (object?)null, _data = new { },
    };

    private static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public async ValueTask DisposeAsync() { if (_app is not null) await _app.DisposeAsync(); }
}
