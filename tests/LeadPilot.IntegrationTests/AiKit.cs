using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Ai;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Ai.Providers;
using LeadPilot.Infrastructure.Discovery.Website;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LeadPilot.IntegrationTests;

/// <summary>TEST-ONLY scripted language model. Behaves like a well-behaved grounded model by default; tests override single operations to simulate a model that invents prices, obeys injected instructions, leaks its prompt, fails or is slow.</summary>
public sealed class ScriptedLlm : IRawAIProvider
{
    public string Name => "claude";
    private readonly ConcurrentQueue<AiCompletionRequest> _requests = new();
    public IReadOnlyList<AiCompletionRequest> Requests => _requests.ToList();
    public Func<AiCompletionRequest, string>? Classify, Generate, Summarize;
    public Func<AiCompletionRequest, Exception?>? Fault;
    public AiUsage Usage { get; set; } = new(100, 50, 80, 0);
    public int Calls => _requests.Count;

    public void Reset() { _requests.Clear(); Classify = Generate = Summarize = null; Fault = null; Usage = new(100, 50, 80, 0); }
    public IEnumerable<AiCompletionRequest> Of(string operation) => Requests.Where(r => r.Operation == operation);

    public Task<AiCompletion> CompleteAsync(AiCompletionRequest request, CancellationToken ct)
    {
        _requests.Enqueue(request);
        if (Fault?.Invoke(request) is { } ex) throw ex;
        var text = request.Operation switch
        {
            "classify" => (Classify ?? DefaultClassify)(request),
            "generate" => (Generate ?? DefaultGenerate)(request),
            "summarize" => (Summarize ?? (_ => "Summary: the customer asked about plans and pricing."))(request),
            _ => "OK",
        };
        return Task.FromResult(new AiCompletion(text, Usage, request.Model, "end_turn", 5));
    }

    public static string Latest(AiCompletionRequest r) => Regex.Match(r.Messages[^1].Content, "<customer_message>(.*?)</customer_message>", RegexOptions.Singleline).Groups[1].Value;
    public static List<(string Ref, string Text)> Chunks(AiCompletionRequest r) =>
        Regex.Matches(r.Messages[^1].Content, "<knowledge_chunk id=\"(K\\d+)\"[^>]*>\\n(.*?)\\n</knowledge_chunk>", RegexOptions.Singleline).Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
    public static string Prompt(AiCompletionRequest r) => r.StableSystem + "\n" + r.DynamicSystem + "\n" + string.Join("\n", r.Messages.Select(m => m.Content));

    public static string Json(object o) => JsonSerializer.Serialize(o);

    private static string DefaultClassify(AiCompletionRequest r)
    {
        var m = Latest(r); var l = m.ToLowerInvariant();
        var intent = Regex.IsMatch(l, "price|cost|how much|fee|plan|pricing") ? "pricing" : Regex.IsMatch(l, "^(hello|hi|good morning|salam)\\b") ? "greeting" : Regex.IsMatch(l, "thank") ? "thanks" : Regex.IsMatch(l, "demo|trial") ? "demo_request" : "product_question";
        var extracted = new Dictionary<string, string>(); var students = Regex.Match(l, "(\\d+) students"); if (students.Success) extracted["student_count"] = students.Groups[1].Value;
        return Json(new { intent, needs_human = false, standalone_query = m, language = "en", confidence = 0.9, extracted });
    }

    private static string DefaultGenerate(AiCompletionRequest r)
    {
        var chunks = Chunks(r);
        if (chunks.Count == 0) return Json(new { answer = "Hello! How can I help you today?", citations = Array.Empty<string>(), confidence = 0.9, needs_human = false, reason = "", next_action = "Wait for the customer's question.", actions = Array.Empty<object>() });
        var first = chunks[0].Text.Split('.')[0].Trim() + ".";
        return Json(new { answer = first + " Would you like more details?", citations = new[] { chunks[0].Ref }, confidence = 0.9, needs_human = false, reason = "", next_action = "Offer a demo.", actions = Array.Empty<object>() });
    }
}

/// <summary>TEST-ONLY deterministic embedder: hashed bag of words (so similar texts really are close and unrelated texts are far), with failure injection.</summary>
public sealed class FakeEmbeddingProvider(AiCredentials credentials, AiTestControls controls) : IRawEmbeddingProvider
{
    public string Name => "voyage";
    public const int Dimensions = 256;

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, EmbeddingInputType type, CancellationToken ct)
    {
        var settings = await credentials.SettingsAsync(ct);
        if (string.IsNullOrEmpty(settings?.ProtectedEmbeddingKey)) throw new AiProviderException(AiFailureKind.NotConfigured, "No embedding API key is saved for this workspace.", false);
        controls.EmbeddingCalls += texts.Count;
        if (controls.EmbedFault?.Invoke() is { } ex) throw ex;
        var tokens = 0;
        var vectors = texts.Select(t => { var (v, n) = Vector(t); tokens += n; return v; }).ToList();
        return new EmbeddingResult(vectors, settings.EmbeddingModel, Dimensions, tokens);
    }

    private static readonly HashSet<string> Stop = ["the", "and", "for", "with", "you", "your", "our", "how", "what", "does", "much", "are", "can", "this", "that", "have", "per", "month"];

    public static (float[] Vector, int Tokens) Vector(string text)
    {
        var v = new float[Dimensions]; var n = 0;
        foreach (var raw in Regex.Split(text.ToLowerInvariant(), "[^\\p{L}\\p{Nd}]+").Where(w => w.Length >= 3 && !Stop.Contains(w)))
        {
            var w = raw.Length > 4 && raw.EndsWith('s') ? raw[..^1] : raw;   // crude stemming so "costs" and "cost" meet
            n++;
            var h = 17; foreach (var ch in w) h = h * 31 + ch;
            v[(h & 0x7fffffff) % Dimensions] += 1f;
        }
        return (v, Math.Max(1, n));
    }
}

/// <summary>TEST-ONLY recording scheduler: the tests run the queued indexing jobs themselves, deterministically.</summary>
public sealed class RecordingAiScheduler : IAiJobScheduler
{
    public sealed record Job(string Kind, Guid TenantId, Guid Id);
    public ConcurrentQueue<Job> Jobs { get; } = new();
    public bool ThrowOnEnqueue;
    public string EnqueueIndex(Guid tenantId, Guid versionId) { if (ThrowOnEnqueue) throw new InvalidOperationException("scheduler down"); Jobs.Enqueue(new("index", tenantId, versionId)); return "j" + Jobs.Count; }
    public string EnqueueWebsiteSync(Guid tenantId, Guid sourceId) { if (ThrowOnEnqueue) throw new InvalidOperationException("scheduler down"); Jobs.Enqueue(new("sync", tenantId, sourceId)); return "j" + Jobs.Count; }
}

public sealed class AiTestControls
{
    public ScriptedLlm Llm { get; } = new();
    public RecordingAiScheduler Scheduler { get; } = new();
    public int EmbeddingCalls;
    public Func<Exception?>? EmbedFault;
    public void Reset() { Llm.Reset(); EmbeddingCalls = 0; EmbedFault = null; Scheduler.ThrowOnEnqueue = false; }
}

/// <summary>TEST-ONLY website: scripted pages for the website knowledge source, with a DNS resolver that maps its host to a public address.</summary>
public sealed class ScriptedWebsite : IHttpSender, IDnsResolver
{
    public sealed record Page(int Status, string ContentType, string Body, string? Location = null);
    public Dictionary<string, Page> Pages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentQueue<string> Fetched { get; } = new();
    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("93.184.216.34")]);
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.GetLeftPart(UriPartial.Path).TrimEnd('/'); Fetched.Enqueue(url);
        var p = Pages.GetValueOrDefault(url) ?? new Page(404, "text/html", "");
        var res = new HttpResponseMessage((HttpStatusCode)p.Status) { Content = new StringContent(p.Body, Encoding.UTF8, p.ContentType) };
        if (p.Location is not null) res.Headers.Location = new Uri(p.Location);
        return Task.FromResult(res);
    }
}

/// <summary>TEST-ONLY local HTTP server that speaks the shape of the Anthropic Messages API and the Voyage embeddings API, so the REAL adapters run end to end through the application.</summary>
public sealed class FakeProviderServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    public string BaseUrl { get; }
    public ConcurrentQueue<(string Path, string? ApiKey, string? Auth, string? Version, string Body)> Requests { get; } = new();
    public Func<int, (int Status, string Body, int DelayMs)>? ClaudeScript;
    private int _claudeCalls;

    public FakeProviderServer()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        BaseUrl = $"http://127.0.0.1:{port}"; _listener.Prefixes.Add(BaseUrl + "/"); _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx; try { ctx = await _listener.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Handle(ctx));
            }
        });
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8); var body = await reader.ReadToEndAsync();
            Requests.Enqueue((ctx.Request.Url!.AbsolutePath, ctx.Request.Headers["x-api-key"], ctx.Request.Headers["Authorization"], ctx.Request.Headers["anthropic-version"], body));
            int status = 200; string resp; var delay = 0;
            if (ctx.Request.Url.AbsolutePath == "/v1/messages")
            {
                var n = Interlocked.Increment(ref _claudeCalls);
                if (ClaudeScript is not null) (status, resp, delay) = ClaudeScript(n); else resp = ClaudeAnswer(body);
            }
            else if (ctx.Request.Url.AbsolutePath == "/v1/embeddings") resp = VoyageAnswer(body);
            else { status = 404; resp = "{}"; }
            if (delay > 0) await Task.Delay(delay);
            ctx.Response.StatusCode = status; ctx.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(resp); await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
        }
        catch { /* client gave up */ }
    }

    private static string ClaudeAnswer(string body)
    {
        using var doc = JsonDocument.Parse(body); var user = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        var latest = Regex.Match(user, "<customer_message>(.*?)</customer_message>", RegexOptions.Singleline).Groups[1].Value;
        var chunk = Regex.Match(user, "<knowledge_chunk id=\"(K\\d+)\"[^>]*>\\n(.*?)\\n</knowledge_chunk>", RegexOptions.Singleline);
        var text = user.Contains("triage step") || doc.RootElement.GetProperty("system")[0].GetProperty("text").GetString()!.Contains("triage step")
            ? ScriptedLlm.Json(new { intent = "pricing", needs_human = false, standalone_query = latest, language = "en", confidence = 0.9, extracted = new { } })
            : ScriptedLlm.Json(new { answer = chunk.Success ? chunk.Groups[2].Value.Split('.')[0] + "." : "OK", citations = chunk.Success ? new[] { chunk.Groups[1].Value } : [], confidence = 0.9, needs_human = false, reason = "", next_action = "", actions = Array.Empty<object>() });
        return ScriptedLlm.Json(new { id = "msg_test", type = "message", role = "assistant", model = doc.RootElement.GetProperty("model").GetString(), stop_reason = "end_turn", content = new[] { new { type = "text", text } }, usage = new { input_tokens = 321, output_tokens = 45, cache_creation_input_tokens = 0, cache_read_input_tokens = 200 } });
    }

    private static string VoyageAnswer(string body)
    {
        using var doc = JsonDocument.Parse(body); var inputs = doc.RootElement.GetProperty("input").EnumerateArray().Select(i => i.GetString()!).ToList();
        var data = inputs.Select((t, i) => new { index = i, embedding = FakeEmbeddingProvider.Vector(t).Vector }).ToList();
        return ScriptedLlm.Json(new { data, model = doc.RootElement.GetProperty("model").GetString(), usage = new { total_tokens = inputs.Sum(t => Math.Max(1, t.Split(' ').Length)) } });
    }

    public ValueTask DisposeAsync() { _listener.Close(); return ValueTask.CompletedTask; }
}

public static class AiKit
{
    public const string ChatKey = "sk-ant-test-0123456789abcdef";
    public const string EmbedKey = "pa-test-0123456789abcdef";

    public static void Register(IServiceCollection s, AiTestControls c)
    {
        s.AddSingleton(c);
        s.RemoveAll<IRawAIProvider>(); s.AddScoped<IRawAIProvider>(_ => c.Llm);
        s.RemoveAll<IRawEmbeddingProvider>(); s.AddScoped<IRawEmbeddingProvider, FakeEmbeddingProvider>();
        s.RemoveAll<IAiJobScheduler>(); s.AddSingleton<IAiJobScheduler>(c.Scheduler);
    }
}

/// <summary>A tenant with the AI configured (keys saved through the API, AI enabled), its admin session, and helpers to add documents and run the indexing jobs.</summary>
public sealed class AiWorld : IAsyncDisposable
{
    public AppFixture App { get; private init; } = default!;
    public Guid TenantId { get; private init; }
    public TestUser AdminUser { get; private init; } = default!;
    public ApiSession Admin { get; private init; } = default!;
    public WorkerHarness Worker { get; private init; } = default!;
    public AiTestControls Controls => App.Ai;

    public static async Task<AiWorld> OpenAsync(AppFixture app, bool configure = true, bool enable = true, Action<IServiceCollection>? configureWorker = null, Dictionary<string, string?>? workerConfig = null)
    {
        var (tenant, adm, _) = await app.World.NewTenantAsync("ai");
        var admin = await ApiSession.LoginAsync(app, adm.Email, adm.Password);
        var w = new AiWorld { App = app, TenantId = tenant.Id, AdminUser = adm, Admin = admin, Worker = BuildWorker(app, configureWorker, workerConfig) };
        app.Ai.Reset();
        if (configure) await w.ConfigureAsync(enable);
        return w;
    }

    public static WorkerHarness BuildWorker(AppFixture app, Action<IServiceCollection>? configure = null, Dictionary<string, string?>? config = null) =>
        MessagingKit.BuildWorker(app, configure: configure, extraConfig: config);

    public async Task ConfigureAsync(bool enable = true)
    {
        Assert.Equal(HttpStatusCode.OK, (await Admin.Put("/api/v1/ai/settings/keys/chat", new { apiKey = AiKit.ChatKey })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin.Put("/api/v1/ai/settings/keys/embedding", new { apiKey = AiKit.EmbedKey })).StatusCode);
        // the TEST-ONLY hashed bag-of-words embedder scores lower than a real embedding model: the minimum score is set to suit it (the default 0.35 is tuned for Voyage and checked in the settings tests)
        await UpdateSettingsAsync(r => r with { MinRetrievalScore = 0.2m });
        if (enable) Assert.Equal(HttpStatusCode.OK, (await Admin.Put("/api/v1/ai/settings/enabled", new { enabled = true })).StatusCode);
    }

    public async Task<AiSettingsDto> UpdateSettingsAsync(Func<UpdateAiSettingsRequest, UpdateAiSettingsRequest> change)
    {
        var s = await ApiSession.Json<AiSettingsDto>(await Admin.Get("/api/v1/ai/settings"));
        var req = change(new UpdateAiSettingsRequest(s.ChatModel, s.ClassifierModel, s.SummaryModel, s.EmbeddingModel, s.TopK, s.MinRetrievalScore, s.MinConfidence, s.HistoryMessages, s.MaxContextChars, s.MonthlyTokenLimit, s.MonthlyBudgetUsd, s.UnsupportedReply, s.ToneInstructions, s.EscalationKeywords));
        var res = await Admin.Put("/api/v1/ai/settings", req);
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"settings update failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return await ApiSession.Json<AiSettingsDto>(res);
    }

    public async Task<KnowledgeDocumentListItemDto> AddTextAsync(string title, string content, DocumentKind kind = DocumentKind.Product, bool index = true)
    {
        var res = await Admin.Post("/api/v1/knowledge/documents/text", new { title, kind, content });
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"create failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        var doc = await ApiSession.Json<KnowledgeDocumentListItemDto>(res);
        if (index) await DrainAsync();
        return await GetAsync(doc.Id);
    }

    public async Task<KnowledgeDocumentListItemDto> GetAsync(Guid id) => (await ApiSession.Json<KnowledgeDocumentDto>(await Admin.Get($"/api/v1/knowledge/documents/{id}"))).Summary;

    /// <summary>Runs every queued Hangfire job of this tenant (indexing, website sync) through the real job classes in the worker's tenant scope, as the Worker would.</summary>
    public async Task<int> DrainAsync()
    {
        var run = 0; var sched = App.Ai.Scheduler;
        while (sched.Jobs.TryDequeue(out var job))
        {
            if (job.TenantId != TenantId) { sched.Jobs.Enqueue(job); if (sched.Jobs.All(j => j.TenantId != TenantId)) break; continue; }
            var j = new LeadPilot.Infrastructure.Ai.Jobs.KnowledgeJob(Worker.Scopes);
            if (job.Kind == "index") await j.IndexAsync(job.TenantId, job.Id, default); else await j.SyncWebsiteAsync(job.TenantId, job.Id, default);
            run++;
        }
        return run;
    }

    public Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work)
    {
        var tcs = new TaskCompletionSource<T>();
        return Worker.Scopes.RunAsync(TenantId, async (sp, _) => tcs.SetResult(await work(sp))).ContinueWith(t => { if (t.IsFaulted) throw t.Exception!.GetBaseException(); return tcs.Task; }).Unwrap();
    }

    public async Task<RetrievalResult> RetrieveAsync(string question, decimal? minScore = null) =>
        await InScope(sp => sp.GetRequiredService<IKnowledgeRetriever>().RetrieveAsync(new RetrievalRequest(question, RetrievalSource.Tester, null, null, minScore, null, true), default));

    public async Task<AiRunResult> AskAsync(string question) => await InScope(sp => sp.GetRequiredService<IAIOrchestrator>().AskAsync(question, default));

    private TestAccount? _account;
    public async Task<TestAccount> AccountAsync()
    {
        if (_account is not null) return _account;
        App.Waha.ClearFaults();
        _account = await MessagingKit.CreateAccountAsync(App, Admin, TenantId, hourly: 120, daily: 1000, delay: 10);
        await MessagingKit.PrepareAccountAsync(App, _account.Id);
        return _account;
    }

    /// <summary>A customer conversation on the tenant's WhatsApp account. The first message creates the conversation (AI mode is Paused by default); then AI mode is switched on, as staff would.</summary>
    public async Task<ChatThread> ChatAsync(string? phone = null, bool linkLead = false, bool aiActive = true, string first = "hello there")
    {
        var account = await AccountAsync(); phone ??= Crm.Phone();
        if (linkLead) await Crm.CreateLead(Admin, phone: phone);
        var conv = await MessagingKit.InboundAsync(App, Worker, account, Admin, phone, first);
        if (aiActive) Assert.Equal(HttpStatusCode.OK, (await Admin.Put($"/api/v1/conversations/{conv.Id}/ai-mode", new { mode = "Active" })).StatusCode);
        return new ChatThread(this, account, phone, conv.Id);
    }

    public Task<List<AiInteraction>> InteractionsAsync(Guid? conversationId = null) =>
        App.Db(db => db.AiInteractions.IgnoreQueryFilters().Where(i => i.TenantId == TenantId && (conversationId == null || i.ConversationId == conversationId)).OrderBy(i => i.CreatedAtUtc).ToListAsync());

    public Task<List<AiUsageEntry>> UsageAsync() => App.Db(db => db.AiUsageEntries.IgnoreQueryFilters().Where(e => e.TenantId == TenantId).OrderBy(e => e.OccurredAtUtc).ToListAsync());

    public async ValueTask DisposeAsync() => await Worker.DisposeAsync();

    public const string StandardPlan = "The Standard plan costs 500 USD per month and includes attendance, fee collection and report cards for up to 1000 students. Annual billing is 5,400 USD.";
    public const string DemoPolicy = "Demos are free of charge and last 30 minutes. Our sales team arranges them on weekdays between 9am and 5pm Pakistan time.";
}

public sealed class ChatThread(AiWorld world, TestAccount account, string phone, Guid conversationId)
{
    public Guid ConversationId => conversationId;
    public string Phone => phone;
    public TestAccount Account => account;

    /// <summary>A customer message through the real webhook, then every event it causes through the real consumers (inbound handling, the AI reply event).</summary>
    public async Task SendAsync(string text, bool pump = true)
    {
        var res = await MessagingKit.PostWebhookAsync(world.App, account, WahaEvents.Message(account.Session, account.ChatId(phone), text));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        if (pump) await world.Worker.PumpAsync();
    }

    public async Task<AiSuggestionDto?> LatestAsync()
    {
        var res = await world.Admin.Get($"/api/v1/ai/conversations/{conversationId}/suggestion");
        if (res.StatusCode == HttpStatusCode.NoContent) return null;
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await ApiSession.Json<AiSuggestionDto>(res);
    }

    public Task<List<AiInteraction>> InteractionsAsync() => world.InteractionsAsync(conversationId);
    public Task<AiMode> ModeAsync() => world.App.Db(async db => (await db.Conversations.IgnoreQueryFilters().SingleAsync(c => c.Id == conversationId)).AiMode);
}
