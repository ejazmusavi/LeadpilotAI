using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Ai;
using LeadPilot.Infrastructure.Ai.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LeadPilot.UnitTests;

internal sealed class FixedTenant : ITenantContext
{
    public Guid? TenantId { get; set; }
    public bool IsPlatformScope => false;
    public Guid RequireTenantId() => TenantId ?? throw new TenantContextMissingException();
}

/// <summary>
/// TEST-ONLY local server that speaks the subset of the Qdrant REST API the adapter uses (collections, payload index, upsert, search with must-filters, delete by filter, count) with cosine scoring and real filter
/// evaluation, so isolation is proven by what the adapter ASKS for, not by what the fake happens to do. A real Qdrant instance has NOT been exercised (see docs/DEVELOPMENT.md).
/// </summary>
internal sealed class FakeQdrantServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public string BaseUrl { get; }
    public ConcurrentQueue<(string Method, string Path, string Body, string? ApiKey)> Requests { get; } = new();
    public ConcurrentDictionary<string, ConcurrentDictionary<string, (float[] Vector, Dictionary<string, string> Payload)>> Collections { get; } = new();
    /// <summary>Simulates a faulty or compromised server that returns hits without applying the filter.</summary>
    public bool IgnoreFilter;
    public int? FailWith;

    public FakeQdrantServer()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        BaseUrl = $"http://127.0.0.1:{port}"; _listener.Prefixes.Add(BaseUrl + "/"); _listener.Start();
        _ = Task.Run(async () => { while (_listener.IsListening) { HttpListenerContext c; try { c = await _listener.GetContextAsync(); } catch { return; } _ = Task.Run(() => Handle(c)); } });
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8); var body = await reader.ReadToEndAsync();
        var path = ctx.Request.Url!.AbsolutePath.Trim('/'); Requests.Enqueue((ctx.Request.HttpMethod, path, body, ctx.Request.Headers["api-key"]));
        int status = 200; string resp = """{"result":true,"status":"ok"}""";
        if (FailWith is { } f) { status = f; resp = "{}"; }
        else
        {
            var parts = path.Split('/');
            if (path == "collections") resp = JsonSerializer.Serialize(new { result = new { collections = Collections.Keys.Select(k => new { name = k }) } });
            else if (parts.Length == 2 && ctx.Request.HttpMethod == "GET") { if (!Collections.ContainsKey(parts[1])) { status = 404; resp = "{}"; } }
            else if (parts.Length == 2 && ctx.Request.HttpMethod == "PUT") Collections.TryAdd(parts[1], new());
            else if (parts.Length >= 3 && parts[2] == "index") { }
            else if (parts.Length == 3 && parts[2] == "points" && ctx.Request.HttpMethod == "PUT")
            {
                var col = Collections[parts[1]]; using var doc = JsonDocument.Parse(body);
                foreach (var p in doc.RootElement.GetProperty("points").EnumerateArray())
                    col[p.GetProperty("id").GetString()!] = (p.GetProperty("vector").EnumerateArray().Select(x => x.GetSingle()).ToArray(), p.GetProperty("payload").EnumerateObject().ToDictionary(o => o.Name, o => o.Value.GetString()!));
            }
            else if (parts.Length == 4 && parts[3] == "search")
            {
                if (!Collections.TryGetValue(parts[1], out var col)) { status = 404; resp = "{}"; }
                else
                {
                    using var doc = JsonDocument.Parse(body); var q = doc.RootElement.GetProperty("vector").EnumerateArray().Select(x => x.GetSingle()).ToArray(); var limit = doc.RootElement.GetProperty("limit").GetInt32(); var min = doc.RootElement.GetProperty("score_threshold").GetDouble();
                    var hits = col.Where(kv => IgnoreFilter || Matches(doc.RootElement.GetProperty("filter"), kv.Value.Payload)).Select(kv => (Id: kv.Key, Score: kv.Value.Vector.Zip(q, (a, b) => (double)a * b).Sum(), kv.Value.Payload)).Where(h => h.Score >= min).OrderByDescending(h => h.Score).Take(limit);
                    resp = JsonSerializer.Serialize(new { result = hits.Select(h => new { id = h.Id, score = h.Score, payload = h.Payload }) });
                }
            }
            else if (parts.Length == 4 && parts[3] == "delete" && Collections.TryGetValue(parts[1], out var dcol))
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var kv in dcol.ToList()) if (Matches(doc.RootElement.GetProperty("filter"), kv.Value.Payload)) dcol.TryRemove(kv.Key, out _);
            }
            else if (parts.Length == 4 && parts[3] == "count" && Collections.TryGetValue(parts[1], out var ccol))
            {
                using var doc = JsonDocument.Parse(body); resp = JsonSerializer.Serialize(new { result = new { count = ccol.Count(kv => Matches(doc.RootElement.GetProperty("filter"), kv.Value.Payload)) } });
            }
        }
        ctx.Response.StatusCode = status; ctx.Response.ContentType = "application/json"; var bytes = Encoding.UTF8.GetBytes(resp); await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
    }

    private static bool Matches(JsonElement filter, Dictionary<string, string> payload)
    {
        foreach (var cond in filter.GetProperty("must").EnumerateArray())
        {
            var key = cond.GetProperty("key").GetString()!; var match = cond.GetProperty("match");
            if (!payload.TryGetValue(key, out var v)) return false;
            if (match.TryGetProperty("value", out var val)) { if (val.GetString() != v) return false; }
            else if (match.TryGetProperty("any", out var any)) { if (!any.EnumerateArray().Any(x => x.GetString() == v)) return false; }
        }
        return true;
    }

    public void Dispose() => _listener.Close();
}

public sealed class QdrantVectorStoreTests : IDisposable
{
    private readonly FakeQdrantServer _server = new(); private readonly FixedTenant _tenant = new();
    private readonly Guid _a = Guid.NewGuid(), _b = Guid.NewGuid();
    public void Dispose() => _server.Dispose();

    private QdrantVectorStore Store(string? apiKey = null, string? baseUrl = null)
    {
        var sc = new ServiceCollection(); sc.AddHttpClient(QdrantVectorStore.HttpClientName);
        var o = new AiOptions(); o.VectorStore.Qdrant.BaseUrl = baseUrl ?? _server.BaseUrl; o.VectorStore.Qdrant.ApiKey = apiKey; o.VectorStore.Qdrant.CollectionPrefix = "kb" + Guid.NewGuid().ToString("N")[..6];
        return new QdrantVectorStore(sc.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), _tenant, Options.Create(o));
    }

    private static float[] V(params float[] x) => VectorMath.Normalize(x);
    private static VectorPoint P(Guid doc, params float[] x) => new(Guid.NewGuid(), doc, Guid.NewGuid(), V(x));

    [Fact]
    public async Task Upsert_then_search_returns_the_closest_points_above_the_threshold_with_ids_and_scores()
    {
        var store = Store(); _tenant.TenantId = _a; var doc = Guid.NewGuid();
        var near = P(doc, 1, 0.1f, 0); var mid = P(doc, 1, 1, 0); var far = P(doc, 0, 0, 1);
        await store.UpsertAsync("voyage-3.5", [near, mid, far], default);
        var hits = await store.SearchAsync("voyage-3.5", new VectorQuery(V(1, 0, 0), 2, 0.1), default);
        Assert.Equal([near.ChunkId, mid.ChunkId], hits.Select(h => h.ChunkId)); Assert.True(hits[0].Score > hits[1].Score); Assert.Equal(doc, hits[0].DocumentId); Assert.Equal(near.VersionId, hits[0].VersionId);
        Assert.Empty(await store.SearchAsync("voyage-3.5", new VectorQuery(V(0, 0, 1), 5, 0.99), default).ContinueWith(t => t.Result.Where(h => h.ChunkId != far.ChunkId)));
        Assert.Equal(3, await store.CountAsync(default));
    }

    [Fact]
    public async Task Search_can_be_narrowed_to_documents_of_the_same_tenant()
    {
        var store = Store(); _tenant.TenantId = _a; var d1 = Guid.NewGuid(); var d2 = Guid.NewGuid();
        var p1 = P(d1, 1, 0); var p2 = P(d2, 1, 0.1f); await store.UpsertAsync("m", [p1, p2], default);
        var hits = await store.SearchAsync("m", new VectorQuery(V(1, 0), 5, 0.0, [d2]), default);
        Assert.Equal([p2.ChunkId], hits.Select(h => h.ChunkId));
    }

    [Fact]
    public async Task Two_tenants_in_the_same_collection_never_see_each_others_points_for_search_count_and_delete()
    {
        var store = Store(); var docA = Guid.NewGuid(); var docB = Guid.NewGuid();
        var pa = P(docA, 1, 0); var pb = P(docB, 1, 0);        // identical vectors
        _tenant.TenantId = _a; await store.UpsertAsync("m", [pa], default);
        _tenant.TenantId = _b; await store.UpsertAsync("m", [pb], default);

        _tenant.TenantId = _a;
        Assert.Equal([pa.ChunkId], (await store.SearchAsync("m", new VectorQuery(V(1, 0), 10, 0.0), default)).Select(h => h.ChunkId)); Assert.Equal(1, await store.CountAsync(default));
        // asking for B's document inside A's narrowing finds nothing; deleting B's document id as A deletes nothing
        Assert.Empty(await store.SearchAsync("m", new VectorQuery(V(1, 0), 10, 0.0, [docB]), default));
        await store.DeleteDocumentAsync(docB, default); await store.DeleteVersionAsync(docB, pb.VersionId, default);
        _tenant.TenantId = _b; Assert.Equal(1, await store.CountAsync(default));
        // A wiping everything leaves B alone
        _tenant.TenantId = _a; await store.DeleteAllAsync(default); Assert.Equal(0, await store.CountAsync(default));
        _tenant.TenantId = _b; Assert.Equal([pb.ChunkId], (await store.SearchAsync("m", new VectorQuery(V(1, 0), 10, 0.0), default)).Select(h => h.ChunkId));
    }

    [Fact]
    public async Task Every_search_count_and_delete_request_carries_the_callers_tenant_condition_and_the_caller_cannot_change_it()
    {
        var store = Store(); _tenant.TenantId = _a; var doc = Guid.NewGuid(); var p = P(doc, 1, 0);
        await store.UpsertAsync("m", [p], default);
        await store.SearchAsync("m", new VectorQuery(V(1, 0), 3, 0.0), default); await store.SearchAsync("m", new VectorQuery(V(1, 0), 3, 0.0, [doc]), default);
        await store.CountAsync(default); await store.DeleteDocumentAsync(doc, default); await store.DeleteVersionAsync(doc, p.VersionId, default); await store.DeleteAllAsync(default);
        var scoped = _server.Requests.Where(r => r.Path.EndsWith("/points/search") || r.Path.EndsWith("/points/delete") || r.Path.EndsWith("/points/count")).ToList();
        Assert.Equal(6, scoped.Count);   // 2 searches, 1 count, 3 deletes
        foreach (var r in scoped)
        {
            using var doc2 = JsonDocument.Parse(r.Body); var must = doc2.RootElement.GetProperty("filter").GetProperty("must").EnumerateArray().ToList();
            Assert.Contains(must, c => c.GetProperty("key").GetString() == "tenant_id" && c.GetProperty("match").GetProperty("value").GetString() == _a.ToString());
            Assert.DoesNotContain(r.Body, _b.ToString());
        }
        // the payload of what was written carries the tenant too
        var upsert = _server.Requests.Single(r => r.Method == "PUT" && r.Path.EndsWith("/points")); Assert.Contains($"\"tenant_id\":\"{_a}\"", upsert.Body);
    }

    [Fact]
    public async Task A_server_that_ignores_the_filter_still_cannot_leak_because_the_adapter_drops_hits_of_other_tenants()
    {
        var store = Store(); var pa = P(Guid.NewGuid(), 1, 0); var pb = P(Guid.NewGuid(), 1, 0.01f);
        _tenant.TenantId = _a; await store.UpsertAsync("m", [pa], default); _tenant.TenantId = _b; await store.UpsertAsync("m", [pb], default);
        _server.IgnoreFilter = true; _tenant.TenantId = _a;
        var hits = await store.SearchAsync("m", new VectorQuery(V(1, 0), 10, 0.0), default);
        Assert.Equal([pa.ChunkId], hits.Select(h => h.ChunkId));
    }

    [Fact]
    public async Task Different_models_and_sizes_live_in_separate_collections_and_a_document_delete_reaches_all_of_them()
    {
        var store = Store(); _tenant.TenantId = _a; var doc = Guid.NewGuid();
        await store.UpsertAsync("voyage-3.5", [P(doc, 1, 0)], default); await store.UpsertAsync("voyage-3.5-lite", [P(doc, 1, 0, 0)], default);
        Assert.Equal(2, _server.Collections.Count); Assert.Equal(2, await store.CountAsync(default));
        Assert.Single(await store.SearchAsync("voyage-3.5", new VectorQuery(V(1, 0), 5, 0.0), default));   // vectors of another model are never compared
        await store.DeleteDocumentAsync(doc, default); Assert.Equal(0, await store.CountAsync(default));
        Assert.Equal("kb_voyage_3_5_1024", QdrantVectorStore.CollectionName("kb", "voyage-3.5", 1024));
    }

    [Fact]
    public async Task Searching_before_anything_was_indexed_is_empty_not_an_error_and_a_missing_tenant_never_reaches_the_network()
    {
        var store = Store(); _tenant.TenantId = _a;
        Assert.Empty(await store.SearchAsync("m", new VectorQuery(V(1, 0), 5, 0.0), default));
        var calls = _server.Requests.Count; _tenant.TenantId = null;
        await Assert.ThrowsAsync<TenantContextMissingException>(() => store.SearchAsync("m", new VectorQuery(V(1, 0), 5, 0.0), default));
        await Assert.ThrowsAsync<TenantContextMissingException>(() => store.UpsertAsync("m", [P(Guid.NewGuid(), 1, 0)], default));
        await Assert.ThrowsAsync<TenantContextMissingException>(() => store.DeleteAllAsync(default));
        await Assert.ThrowsAsync<TenantContextMissingException>(() => store.CountAsync(default));
        Assert.Equal(calls, _server.Requests.Count);
    }

    [Fact]
    public async Task Outages_are_reported_as_vector_store_errors_and_the_api_key_is_sent_only_in_the_header()
    {
        _tenant.TenantId = _a;
        var down = Store(baseUrl: "http://127.0.0.1:1");
        await Assert.ThrowsAsync<VectorStoreException>(() => down.CountAsync(default));
        var store = Store(apiKey: "PLATFORM-QDRANT-KEY");
        await store.UpsertAsync("m", [P(Guid.NewGuid(), 1, 0)], default);
        Assert.All(_server.Requests, r => Assert.Equal("PLATFORM-QDRANT-KEY", r.ApiKey)); Assert.DoesNotContain(_server.Requests, r => r.Body.Contains("PLATFORM-QDRANT-KEY") || r.Path.Contains("PLATFORM-QDRANT-KEY"));
        _server.FailWith = 500;
        var ex = await Assert.ThrowsAsync<VectorStoreException>(() => store.SearchAsync("m", new VectorQuery(V(1, 0), 5, 0.0), default)); Assert.Contains("HTTP 500", ex.Message); Assert.DoesNotContain("PLATFORM-QDRANT-KEY", ex.Message);
    }
}
