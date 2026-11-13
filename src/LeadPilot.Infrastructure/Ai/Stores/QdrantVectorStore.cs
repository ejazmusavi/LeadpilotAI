using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Ai;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Ai.Stores;

/// <summary>
/// Qdrant adapter (REST). One collection per embedding model and vector size (<c>{prefix}_{model}_{dims}</c>), all tenants in it, separated by a mandatory <c>tenant_id</c> payload condition. Every request body is
/// produced by <see cref="Filter"/>, which ALWAYS contains the tenant condition taken from the tenant context: the interface has no tenant parameter and no code path builds a filter without it. Tested against a local
/// Qdrant-compatible server (see docs/DEVELOPMENT.md for the status of a real instance).
/// </summary>
public sealed class QdrantVectorStore(IHttpClientFactory httpFactory, ITenantContext tenant, IOptions<AiOptions> options) : IVectorStore
{
    public const string StoreName = "qdrant";
    public const string HttpClientName = "ai-qdrant";
    public string Name => StoreName;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> EnsuredCollections = new();
    private QdrantOptions Q => options.Value.VectorStore.Qdrant;

    public static string CollectionName(string prefix, string model, int dims) => $"{Regex.Replace(prefix, "[^A-Za-z0-9_]", "_")}_{Regex.Replace(model.ToLowerInvariant(), "[^a-z0-9]", "_")}_{dims}";

    /// <summary>The ONLY place a Qdrant filter is built. The tenant condition is unconditional.</summary>
    private object Filter(params object[] extraMust)
    {
        var must = new List<object> { new { key = "tenant_id", match = new { value = tenant.RequireTenantId().ToString() } } };
        must.AddRange(extraMust);
        return new { must };
    }

    public async Task UpsertAsync(string model, IReadOnlyList<VectorPoint> points, CancellationToken ct)
    {
        if (points.Count == 0) return;
        var tenantId = tenant.RequireTenantId().ToString();
        var dims = points[0].Vector.Length;
        var collection = CollectionName(Q.CollectionPrefix, model, dims);
        await EnsureCollectionAsync(collection, dims, ct);
        foreach (var batch in points.Chunk(100))
        {
            var body = new { points = batch.Select(p => new { id = p.ChunkId.ToString(), vector = p.Vector, payload = new { tenant_id = tenantId, document_id = p.DocumentId.ToString(), version_id = p.VersionId.ToString(), chunk_id = p.ChunkId.ToString() } }) };
            await SendAsync(HttpMethod.Put, $"collections/{collection}/points?wait=true", body, ct);
        }
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(string model, VectorQuery query, CancellationToken ct)
    {
        var collection = CollectionName(Q.CollectionPrefix, model, query.Vector.Length);
        var extra = query.DocumentIds is { Count: > 0 } docs ? [new { key = "document_id", match = new { any = docs.Select(d => d.ToString()).ToArray() } }] : Array.Empty<object>();
        var body = new { vector = query.Vector, limit = query.TopK, score_threshold = query.MinScore, with_payload = true, filter = Filter(extra) };
        using var response = await SendAsync(HttpMethod.Post, $"collections/{collection}/points/search", body, ct, allowNotFound: true);
        if (response is null) return [];   // no collection yet: nothing indexed
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var hits = new List<VectorHit>();
        foreach (var r in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            var payload = r.GetProperty("payload");
            // defence in depth: a hit whose payload does not carry OUR tenant is dropped even if the server returned it
            if (payload.GetProperty("tenant_id").GetString() != tenant.RequireTenantId().ToString()) continue;
            hits.Add(new VectorHit(Guid.Parse(payload.GetProperty("chunk_id").GetString()!), Guid.Parse(payload.GetProperty("document_id").GetString()!), Guid.Parse(payload.GetProperty("version_id").GetString()!), r.GetProperty("score").GetDouble()));
        }
        return hits.OrderByDescending(h => h.Score).ToList();
    }

    public Task DeleteDocumentAsync(Guid documentId, CancellationToken ct) => DeleteAsync(Filter(new { key = "document_id", match = new { value = documentId.ToString() } }), ct);

    public Task DeleteVersionAsync(Guid documentId, Guid versionId, CancellationToken ct) =>
        DeleteAsync(Filter(new { key = "document_id", match = new { value = documentId.ToString() } }, new { key = "version_id", match = new { value = versionId.ToString() } }), ct);

    public Task DeleteAllAsync(CancellationToken ct) => DeleteAsync(Filter(), ct);

    public async Task<long> CountAsync(CancellationToken ct)
    {
        long total = 0;
        foreach (var c in await CollectionsAsync(ct))
        {
            using var r = await SendAsync(HttpMethod.Post, $"collections/{c}/points/count", new { filter = Filter(), exact = true }, ct, allowNotFound: true);
            if (r is null) continue;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
            total += doc.RootElement.GetProperty("result").GetProperty("count").GetInt64();
        }
        return total;
    }

    private async Task DeleteAsync(object filter, CancellationToken ct)
    {
        foreach (var c in await CollectionsAsync(ct))
        {
            using var r = await SendAsync(HttpMethod.Post, $"collections/{c}/points/delete?wait=true", new { filter }, ct, allowNotFound: true);
        }
    }

    private async Task<List<string>> CollectionsAsync(CancellationToken ct)
    {
        using var r = await SendAsync(HttpMethod.Get, "collections", null, ct);
        using var doc = JsonDocument.Parse(await r!.Content.ReadAsStringAsync(ct));
        var prefix = Regex.Replace(Q.CollectionPrefix, "[^A-Za-z0-9_]", "_") + "_";
        return doc.RootElement.GetProperty("result").GetProperty("collections").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    private async Task EnsureCollectionAsync(string collection, int dims, CancellationToken ct)
    {
        var key = Q.BaseUrl + "|" + collection;
        if (EnsuredCollections.ContainsKey(key)) return;
        using var get = await SendAsync(HttpMethod.Get, $"collections/{collection}", null, ct, allowNotFound: true);
        if (get is null)
        {
            using var create = await SendAsync(HttpMethod.Put, $"collections/{collection}", new { vectors = new { size = dims, distance = "Cosine" } }, ct);
            using var index = await SendAsync(HttpMethod.Put, $"collections/{collection}/index?wait=true", new { field_name = "tenant_id", field_schema = "keyword" }, ct);
            using var docIndex = await SendAsync(HttpMethod.Put, $"collections/{collection}/index?wait=true", new { field_name = "document_id", field_schema = "keyword" }, ct);
        }
        EnsuredCollections[key] = true;
    }

    private async Task<HttpResponseMessage?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, bool allowNotFound = false)
    {
        var http = httpFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(method, new Uri(new Uri(Q.BaseUrl.TrimEnd('/') + "/"), path));
        if (!string.IsNullOrEmpty(Q.ApiKey)) request.Headers.TryAddWithoutValidation("api-key", Q.ApiKey);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new VectorStoreException("The vector store (Qdrant) could not be reached.", ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new VectorStoreException("The vector store (Qdrant) did not answer in time.", ex); }
        if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound) { response.Dispose(); return null; }
        if (!response.IsSuccessStatusCode) { var s = (int)response.StatusCode; response.Dispose(); throw new VectorStoreException($"The vector store (Qdrant) answered HTTP {s}."); }
        return response;
    }
}
