using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Ai;
using LeadPilot.Domain.Ai;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Ai.Stores;

/// <summary>
/// Default vector store: the tenant's <c>EmbeddingRecords</c> rows are the index, searched by brute-force cosine similarity (vectors are stored L2-normalised, so cosine = dot product). Fine for the knowledge base
/// of one business (a few thousand chunks); beyond <c>Ai:VectorStore:MysqlMaxScan</c> it refuses instead of becoming slow, and the Qdrant adapter should be used. The tenant comes from the tenant context, is applied to
/// EVERY query here (in addition to the global EF filter) and is not a parameter anyone can change.
/// </summary>
public sealed class MySqlVectorStore(AppDbContext db, ITenantContext tenant, IOptions<AiOptions> options) : IVectorStore
{
    public const string StoreName = "mysql";
    public string Name => StoreName;

    /// <summary>The embedding rows ARE the index: the indexer writes them first, so an upsert only verifies that they exist for this tenant.</summary>
    public async Task UpsertAsync(string model, IReadOnlyList<VectorPoint> points, CancellationToken ct)
    {
        if (points.Count == 0) return;
        var tenantId = tenant.RequireTenantId();
        var ids = points.Select(p => p.ChunkId).ToList();
        var present = await db.EmbeddingRecords.AsNoTracking().CountAsync(e => e.TenantId == tenantId && e.Model == model && ids.Contains(e.ChunkId), ct);
        if (present != ids.Count) throw new VectorStoreException("Embedding rows are missing for some chunks of this tenant.");
    }

    public async Task<IReadOnlyList<VectorHit>> SearchAsync(string model, VectorQuery query, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        var q = db.EmbeddingRecords.AsNoTracking().Where(e => e.TenantId == tenantId && e.Model == model);
        if (query.DocumentIds is { Count: > 0 } docs) { var list = docs.ToList(); q = q.Where(e => list.Contains(e.DocumentId)); }
        var max = options.Value.VectorStore.MysqlMaxScan;
        if (await q.CountAsync(ct) > max) throw new VectorStoreException($"This workspace has more than {max:N0} indexed passages: switch the vector store to Qdrant.");
        var top = new List<VectorHit>(query.TopK + 1);
        // streamed in pages by id so memory stays flat
        Guid? after = null;
        while (true)
        {
            var page = after is null ? q.OrderBy(e => e.Id) : q.Where(e => e.Id.CompareTo(after.Value) > 0).OrderBy(e => e.Id);
            var rows = await page.Take(500).Select(e => new { e.Id, e.ChunkId, e.DocumentId, e.VersionId, e.Vector }).ToListAsync(ct);
            if (rows.Count == 0) break;
            foreach (var r in rows)
            {
                var v = new float[r.Vector.Length / sizeof(float)]; Buffer.BlockCopy(r.Vector, 0, v, 0, r.Vector.Length);
                var score = VectorMath.Dot(query.Vector, v);
                if (double.IsNaN(score) || score < query.MinScore) continue;
                top.Add(new VectorHit(r.ChunkId, r.DocumentId, r.VersionId, score));
                if (top.Count > query.TopK) { top.Sort((a, b) => b.Score.CompareTo(a.Score)); top.RemoveAt(top.Count - 1); }
            }
            after = rows[^1].Id;
        }
        top.Sort((a, b) => b.Score.CompareTo(a.Score));
        return top;
    }

    public async Task DeleteDocumentAsync(Guid documentId, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        await db.EmbeddingRecords.Where(e => e.TenantId == tenantId && e.DocumentId == documentId).ExecuteDeleteAsync(ct);
    }

    public async Task DeleteVersionAsync(Guid documentId, Guid versionId, CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        await db.EmbeddingRecords.Where(e => e.TenantId == tenantId && e.DocumentId == documentId && e.VersionId == versionId).ExecuteDeleteAsync(ct);
    }

    public async Task DeleteAllAsync(CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        await db.EmbeddingRecords.Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(ct);
    }

    public async Task<long> CountAsync(CancellationToken ct)
    {
        var tenantId = tenant.RequireTenantId();
        return await db.EmbeddingRecords.AsNoTracking().LongCountAsync(e => e.TenantId == tenantId, ct);
    }
}
