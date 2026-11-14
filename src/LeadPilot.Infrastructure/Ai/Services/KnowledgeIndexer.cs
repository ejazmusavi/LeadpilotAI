using System.Security.Cryptography;
using System.Text;
using HtmlAgilityPack;
using LeadPilot.Application.Ai;
using LeadPilot.Application.Discovery;
using LeadPilot.Domain.Ai;
using LeadPilot.Infrastructure.Ai.Extraction;
using LeadPilot.Infrastructure.Ai.Providers;
using LeadPilot.Infrastructure.Ai.Stores;
using LeadPilot.Infrastructure.Discovery.Website;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Ai.Services;

/// <summary>Creates documents and versions (shared by uploads, pasted text and website sync) and schedules their indexing.</summary>
internal sealed class KnowledgeWriter(CrmContext crm, IAiJobScheduler jobs, IOptions<AiOptions> options, ILogger<KnowledgeWriter> logger)
{
    public static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    public async Task<KnowledgeSource> DefaultSourceAsync(KnowledgeSourceType type, string name, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var existing = await crm.Db.KnowledgeSources.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.Type == type && s.Url == null && s.Name == name, ct);
        if (existing is not null) return existing;
        var created = KnowledgeSource.Create(tenantId, type, name, null, crm.Now);
        crm.Db.KnowledgeSources.Add(created);
        return created;
    }

    public async Task EnsureCapacityAsync(CancellationToken ct)
    {
        var tenantId = crm.TenantId; var o = options.Value.Ingestion;
        if (await crm.Db.KnowledgeDocuments.CountAsync(d => d.TenantId == tenantId, ct) >= o.MaxDocumentsPerTenant)
            throw new Domain.Common.DomainException($"A workspace can hold at most {o.MaxDocumentsPerTenant} documents. Delete documents you no longer need.");
    }

    public async Task<(KnowledgeDocument Doc, DocumentVersion Version)> AddDocumentAsync(Guid sourceId, string title, DocumentKind kind, string? sourceUrl, string content, string? fileName, string? mime, long size, CancellationToken ct)
    {
        await EnsureCapacityAsync(ct);
        var doc = KnowledgeDocument.Create(crm.TenantId, sourceId, title, kind, sourceUrl, crm.Now);
        var version = AddVersion(doc, content, fileName, mime, size);
        crm.Db.KnowledgeDocuments.Add(doc);
        return (doc, version);
    }

    public DocumentVersion AddVersion(KnowledgeDocument doc, string content, string? fileName, string? mime, long size)
    {
        var version = DocumentVersion.Create(crm.TenantId, doc.Id, doc.NextVersionNumber(), content, Hash(content), size, fileName, mime, crm.Actor, crm.Now);
        crm.Db.DocumentVersions.Add(version);
        doc.SetStatus(IndexStatus.Pending, crm.Now);
        return version;
    }

    /// <summary>Queues indexing after the rows are saved. If the scheduler is down the version stays Pending and the sweep job picks it up.</summary>
    public void Schedule(Guid versionId)
    {
        try { jobs.EnqueueIndex(crm.TenantId, versionId); }
        catch (Exception ex) { logger.LogWarning(ex, "Indexing of version {VersionId} could not be queued; the sweep will retry", versionId); }
    }
}

/// <summary>Chunks, embeds and indexes one document version; syncs website sources. Idempotent and safe to repeat after a crash (claim + stale takeover).</summary>
internal sealed class KnowledgeIndexer(
    CrmContext crm, IEmbeddingProvider embeddings, IVectorStore store, AiCredentials credentials, KnowledgeWriter writer, SafeWebFetcher fetcher, IOptions<AiOptions> options,
    IOptions<DiscoveryOptions> discoveryOptions, ILogger<KnowledgeIndexer> logger) : IKnowledgeIndexer
{
    public async Task IndexVersionAsync(Guid versionId, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now; var o = options.Value.Ingestion;
        var version = await db.DocumentVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.TenantId == tenantId, ct);
        if (version is null || version.Status is IndexStatus.Indexed or IndexStatus.Superseded) return;
        var doc = await db.KnowledgeDocuments.FirstOrDefaultAsync(d => d.Id == version.DocumentId && d.TenantId == tenantId, ct);
        if (doc is null) return;

        var stale = now.AddMinutes(-Math.Max(1, o.StaleIndexingMinutes));
        var claimed = await db.DocumentVersions.Where(v => v.Id == versionId && v.TenantId == tenantId && (v.Status == IndexStatus.Pending || v.Status == IndexStatus.Failed || (v.Status == IndexStatus.Indexing && v.IndexingStartedAtUtc < stale)))
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, IndexStatus.Indexing).SetProperty(v => v.IndexingStartedAtUtc, now).SetProperty(v => v.Error, (string?)null), ct);
        if (claimed == 0) return;       // another worker owns it
        await db.Entry(version).ReloadAsync(ct);
        SyncDocStatus(doc, version, IndexStatus.Indexing);
        await db.SaveChangesAsync(ct);

        try
        {
            var settings = await credentials.SettingsAsync(ct);
            if (string.IsNullOrEmpty(settings?.ProtectedEmbeddingKey)) { await FailAsync(doc, version, "No embedding API key is saved for this workspace. Add it under AI settings, then re-index.", ct); return; }

            // leftovers of an earlier attempt of THIS version
            await RemoveVersionIndexAsync(doc.Id, version.Id, ct);

            var chunks = TextChunker.Chunk(version.Content, o.ChunkTargetChars, o.ChunkMaxChars, o.ChunkOverlapChars);
            if (chunks.Count == 0) { await FailAsync(doc, version, "The document has no text to index.", ct); return; }
            if (chunks.Count > o.MaxChunksPerDocument) { await FailAsync(doc, version, $"The document would need {chunks.Count} passages; the limit is {o.MaxChunksPerDocument}. Split it into several documents.", ct); return; }
            var rows = chunks.Select(c => DocumentChunk.Create(tenantId, doc.Id, version.Id, c.Ordinal, c.Text, c.Heading)).ToList();
            db.DocumentChunks.AddRange(rows);
            await db.SaveChangesAsync(ct);

            var records = new List<EmbeddingRecord>(rows.Count); string model = settings.EmbeddingModel;
            for (var i = 0; i < chunks.Count; i += Math.Max(1, o.EmbedBatchSize))
            {
                var batch = chunks.Skip(i).Take(Math.Max(1, o.EmbedBatchSize)).ToList();
                var result = await embeddings.EmbedAsync(batch.Select(c => c.EmbeddingInput).ToList(), EmbeddingInputType.Document, ct);
                if (result.Vectors.Count != batch.Count) throw new AiProviderException(AiFailureKind.BadResponse, "The embedding provider returned the wrong number of vectors.", true);
                model = result.Model;
                for (var j = 0; j < batch.Count; j++)
                    records.Add(EmbeddingRecord.Create(tenantId, rows[i + j].Id, doc.Id, version.Id, embeddings.Name, result.Model, VectorMath.Normalize(result.Vectors[j]), store.Name, crm.Now));
            }
            db.EmbeddingRecords.AddRange(records);
            await db.SaveChangesAsync(ct);
            await store.UpsertAsync(model, records.Select(r => new VectorPoint(r.ChunkId, r.DocumentId, r.VersionId, r.ToArray())).ToList(), ct);

            // switch over: only now do older versions stop being searchable
            var older = await db.DocumentVersions.Where(v => v.TenantId == tenantId && v.DocumentId == doc.Id && v.VersionNumber < version.VersionNumber).ToListAsync(ct);
            foreach (var v in older)
            {
                await RemoveVersionIndexAsync(doc.Id, v.Id, ct);
                if (v.Status != IndexStatus.Superseded) v.MarkSuperseded();
            }
            version.MarkIndexed(rows.Count, model, crm.Now);
            doc.SetCurrent(version.Id, crm.Now);
            doc.SetStatus(version.VersionNumber == doc.LatestVersionNumber ? IndexStatus.Indexed : IndexStatus.Pending, crm.Now);
            crm.Record("Ai.DocumentIndexed", "KnowledgeDocument", doc.Id, $"Document '{Truncate(doc.Title)}' version {version.VersionNumber} indexed ({rows.Count} passages, model {model}).");
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (AiProviderException ex)
        {
            await FailAsync(doc, version, Explain(ex), ct);
            if (ex.Retryable) throw;   // Hangfire's bounded retry
        }
        catch (VectorStoreException ex)
        {
            await FailAsync(doc, version, ex.Message, ct);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Indexing of version {VersionId} failed", versionId);
            await FailAsync(doc, version, "Indexing failed unexpectedly. Try Re-index; if it keeps failing contact support.", ct);
            throw;
        }
    }

    private static string Explain(AiProviderException ex) => ex.Kind switch
    {
        AiFailureKind.NotConfigured => "No embedding API key is saved for this workspace.",
        AiFailureKind.Auth => "The embedding provider rejected the API key. Check it under AI settings.",
        AiFailureKind.LimitReached => ex.Message,
        _ => "The embedding provider failed: " + ex.Message,
    };

    private async Task FailAsync(KnowledgeDocument doc, DocumentVersion version, string error, CancellationToken ct)
    {
        crm.Db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList().ForEach(e => e.State = EntityState.Detached);   // never persist a half-built index
        await RemoveVersionIndexAsync(doc.Id, version.Id, ct);
        version.MarkFailed(error);
        SyncDocStatus(doc, version, IndexStatus.Failed);
        crm.Record("Ai.DocumentIndexFailed", "KnowledgeDocument", doc.Id, $"Indexing of '{Truncate(doc.Title)}' version {version.VersionNumber} failed: {Truncate(error, 200)}");
        await crm.Db.SaveChangesAsync(ct);
    }

    private void SyncDocStatus(KnowledgeDocument doc, DocumentVersion version, IndexStatus status)
    {
        // the document shows the state of its newest version; while that is not searchable yet an older current version keeps answering
        if (version.VersionNumber == doc.LatestVersionNumber) doc.SetStatus(status, crm.Now);
    }

    private async Task RemoveVersionIndexAsync(Guid documentId, Guid versionId, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        await store.DeleteVersionAsync(documentId, versionId, ct);
        await crm.Db.EmbeddingRecords.Where(e => e.TenantId == tenantId && e.VersionId == versionId).ExecuteDeleteAsync(ct);
        await crm.Db.DocumentChunks.Where(c => c.TenantId == tenantId && c.VersionId == versionId).ExecuteDeleteAsync(ct);
    }

    private static string Truncate(string s, int n = 80) => s.Length > n ? s[..n] : s;

    // ------------------------------------------------------------------ website sources

    public async Task SyncWebsiteSourceAsync(Guid sourceId, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var o = options.Value.Ingestion;
        var source = await db.KnowledgeSources.FirstOrDefaultAsync(s => s.Id == sourceId && s.TenantId == tenantId && s.Type == KnowledgeSourceType.Website, ct);
        if (source?.Url is null || !Uri.TryCreate(source.Url, UriKind.Absolute, out var start)) return;
        var kind = await db.KnowledgeDocuments.Where(d => d.TenantId == tenantId && d.SourceId == sourceId).Select(d => (DocumentKind?)d.Kind).FirstOrDefaultAsync(ct) ?? DocumentKind.Website;
        var robots = new Dictionary<string, RobotsTxt>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<Uri>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        queue.Enqueue(start); seen.Add(Canonical(start));
        int added = 0, changed = 0, unchanged = 0, skipped = 0; string? firstError = null;
        var fetched = 0;
        while (queue.Count > 0 && fetched < o.WebsiteMaxPages)
        {
            ct.ThrowIfCancellationRequested();
            var uri = queue.Dequeue();
            try
            {
                if (!await AllowedAsync(uri, robots, ct)) { skipped++; firstError ??= "robots.txt does not allow reading some pages."; continue; }
                var page = await fetcher.FetchHtmlAsync(uri, ct);
                fetched++;
                if (page.ContentType == "text/plain") { skipped++; continue; }
                var htmlDoc = new HtmlDocument(); htmlDoc.LoadHtml(page.Body);
                if (htmlDoc.DocumentNode.SelectSingleNode("//meta[translate(@name,'ROBOTS','robots')='robots']")?.GetAttributeValue("content", "").Contains("noindex", StringComparison.OrdinalIgnoreCase) == true) { skipped++; continue; }
                foreach (var link in Links(htmlDoc, page.FinalUri, start))
                    if (seen.Add(Canonical(link)) && seen.Count <= o.WebsiteMaxPages * 5) queue.Enqueue(link);
                var extracted = DocumentTextExtractor.FromHtml(page.Body);
                var text = DocumentTextExtractor.Clean(extracted.Text);
                if (text.Length < 40) { skipped++; continue; }
                var title = extracted.Title ?? page.FinalUri.Host + page.FinalUri.AbsolutePath;
                var content = text.StartsWith('#') ? text : $"# {title}\n\n{text}";
                if (content.Length > DocumentVersion.MaxContentChars) content = content[..DocumentVersion.MaxContentChars];
                var url = Canonical(page.FinalUri);
                var doc = await db.KnowledgeDocuments.FirstOrDefaultAsync(d => d.TenantId == tenantId && d.SourceId == sourceId && d.SourceUrl == url, ct);
                if (doc is null)
                {
                    var (d, v) = await writer.AddDocumentAsync(sourceId, title.Length > 200 ? title[..200] : title, kind, url, content, null, "text/html", Encoding.UTF8.GetByteCount(content), ct);
                    await db.SaveChangesAsync(ct); writer.Schedule(v.Id); added++;
                }
                else
                {
                    var latestHash = await db.DocumentVersions.AsNoTracking().Where(v => v.TenantId == tenantId && v.DocumentId == doc.Id && v.VersionNumber == doc.LatestVersionNumber).Select(v => v.ContentHash).FirstOrDefaultAsync(ct);
                    if (latestHash == KnowledgeWriter.Hash(content)) { unchanged++; continue; }
                    var v = writer.AddVersion(doc, content, null, "text/html", Encoding.UTF8.GetByteCount(content));
                    await db.SaveChangesAsync(ct); writer.Schedule(v.Id); changed++;
                }
            }
            catch (FetchException ex) { skipped++; firstError ??= ex.Message; logger.LogInformation("Website page not read ({Reason}) for host {Host}", ex.Reason, uri.Host); }
        }
        source.RecordSync(crm.Now, added + changed + unchanged == 0 ? firstError ?? "No readable pages were found." : null);
        crm.Record("Ai.WebsiteSynced", "KnowledgeSource", source.Id, $"Website source '{Truncate(source.Name)}' synced: {added} new, {changed} changed, {unchanged} unchanged, {skipped} skipped.");
        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> AllowedAsync(Uri uri, Dictionary<string, RobotsTxt> cache, CancellationToken ct)
    {
        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (!cache.TryGetValue(origin, out var robots))
        {
            try { robots = RobotsTxt.Parse((await fetcher.FetchRobotsAsync(new Uri(origin + "/robots.txt"), ct)).Body); }
            catch (FetchException ex) when (ex.Reason is FetchFailure.NotFound or FetchFailure.NotAllowedContentType) { robots = RobotsTxt.AllowEverything; }
            catch (FetchException ex) when (ex.Reason == FetchFailure.Blocked) { throw; }
            catch (FetchException) { robots = RobotsTxt.BlockEverything; }
            cache[origin] = robots;
        }
        return robots.IsAllowed(discoveryOptions.Value.Website.RobotsToken, uri.PathAndQuery);
    }

    private static string Canonical(Uri u) => new UriBuilder(u) { Fragment = string.Empty, Port = -1 }.Uri.ToString().TrimEnd('/');

    private static IEnumerable<Uri> Links(HtmlDocument doc, Uri page, Uri start)
    {
        var skipExt = new[] { ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".svg", ".webp", ".zip", ".mp4", ".mp3", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".css", ".js", ".ico" };
        foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]") ?? new HtmlNodeCollection(null))
        {
            var href = a.GetAttributeValue("href", "");
            if (string.IsNullOrWhiteSpace(href) || href.StartsWith('#') || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(page, HtmlEntity.DeEntitize(href), out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) continue;
            if (!string.Equals(u.IdnHost, start.IdnHost, StringComparison.OrdinalIgnoreCase)) continue;      // same host only
            if (skipExt.Any(e => u.AbsolutePath.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
            yield return u;
        }
    }
}
