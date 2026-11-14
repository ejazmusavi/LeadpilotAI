using LeadPilot.Application.Ai;
using LeadPilot.Application.Common;
using LeadPilot.Domain.Ai;
using LeadPilot.Infrastructure.Ai.Extraction;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Ai.Services;

/// <summary>Knowledge base management used by the MVC pages and the REST API. Every read and write is scoped to the active tenant; foreign ids answer "not found".</summary>
internal sealed class KnowledgeService(
    CrmContext crm, KnowledgeWriter writer, IAiJobScheduler jobs, IVectorStore store, DocumentTextExtractor extractor, IOptions<AiOptions> options, ILogger<KnowledgeService> logger) : IKnowledgeService
{
    public const string UploadsSourceName = "Uploaded files";
    public const string WrittenSourceName = "Written documents";

    public async Task<IReadOnlyList<KnowledgeSourceDto>> ListSourcesAsync(CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var counts = await crm.Db.KnowledgeDocuments.AsNoTracking().Where(d => d.TenantId == tenantId).GroupBy(d => d.SourceId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        var rows = await crm.Db.KnowledgeSources.AsNoTracking().Where(s => s.TenantId == tenantId).OrderBy(s => s.Name).ToListAsync(ct);
        return rows.Select(s => ToDto(s, counts.GetValueOrDefault(s.Id))).ToList();
    }

    private static KnowledgeSourceDto ToDto(KnowledgeSource s, int docs) => new(s.Id, s.Type, s.Name, s.Url, s.Status, docs, s.LastSyncAtUtc, s.LastSyncError, s.CreatedAtUtc);

    public async Task<PagedResult<KnowledgeDocumentListItemDto>> ListDocumentsAsync(KnowledgeDocumentQuery query, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(query.Page, query.PageSize);
        var q = crm.Db.KnowledgeDocuments.AsNoTracking().Where(d => d.TenantId == tenantId);
        if (query.SourceId is { } sid) q = q.Where(d => d.SourceId == sid);
        if (query.Status is { } st) q = q.Where(d => d.Status == st);
        if (query.Kind is { } k) q = q.Where(d => d.Kind == k);
        if (!string.IsNullOrWhiteSpace(query.Search)) { var term = query.Search.Trim(); q = q.Where(d => d.Title.Contains(term)); }
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(d => d.UpdatedAtUtc ?? d.CreatedAtUtc).Skip((page - 1) * size).Take(size).Select(d => new
        {
            d.Id, d.SourceId, SourceName = crm.Db.KnowledgeSources.Where(s => s.Id == d.SourceId).Select(s => s.Name).FirstOrDefault()!, d.Title, d.Kind, d.Status, d.IsEnabled, d.LatestVersionNumber, d.SourceUrl,
            Chunks = crm.Db.DocumentChunks.Count(c => c.DocumentId == d.Id), Latest = crm.Db.DocumentVersions.Where(v => v.DocumentId == d.Id && v.VersionNumber == d.LatestVersionNumber).Select(v => new { v.IndexedAtUtc, v.Error }).FirstOrDefault(), Updated = d.UpdatedAtUtc ?? d.CreatedAtUtc,
        }).ToListAsync(ct);
        return new PagedResult<KnowledgeDocumentListItemDto>(rows.Select(r => new KnowledgeDocumentListItemDto(r.Id, r.SourceId, r.SourceName, r.Title, r.Kind, r.Status, r.IsEnabled, r.LatestVersionNumber, r.Chunks, r.Latest?.IndexedAtUtc, r.Latest?.Error, r.SourceUrl, r.Updated)).ToList(), page, size, total);
    }

    private async Task<KnowledgeDocumentListItemDto?> SingleAsync(Guid id, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var d = await crm.Db.KnowledgeDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (d is null) return null;
        var source = await crm.Db.KnowledgeSources.AsNoTracking().Where(s => s.Id == d.SourceId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        var chunks = await crm.Db.DocumentChunks.CountAsync(c => c.DocumentId == id && c.TenantId == tenantId, ct);
        var latest = await crm.Db.DocumentVersions.AsNoTracking().Where(v => v.DocumentId == id && v.TenantId == tenantId && v.VersionNumber == d.LatestVersionNumber).Select(v => new { v.IndexedAtUtc, v.Error }).FirstOrDefaultAsync(ct);
        return new KnowledgeDocumentListItemDto(d.Id, d.SourceId, source, d.Title, d.Kind, d.Status, d.IsEnabled, d.LatestVersionNumber, chunks, latest?.IndexedAtUtc, latest?.Error, d.SourceUrl, d.UpdatedAtUtc ?? d.CreatedAtUtc);
    }

    public async Task<Result<KnowledgeDocumentDto>> GetDocumentAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var summary = await SingleAsync(id, ct);
        if (summary is null) return Result.Failure<KnowledgeDocumentDto>(Error.NotFound("The document"));
        var doc = await crm.Db.KnowledgeDocuments.AsNoTracking().FirstAsync(d => d.Id == id && d.TenantId == tenantId, ct);
        var versions = await crm.Db.DocumentVersions.AsNoTracking().Where(v => v.DocumentId == id && v.TenantId == tenantId).OrderByDescending(v => v.VersionNumber)
            .Select(v => new { v.Id, v.VersionNumber, v.Status, v.SizeBytes, Chars = v.Content.Length, v.FileName, v.MimeType, v.ChunkCount, v.EmbeddingModel, v.IndexedAtUtc, v.Error, v.CreatedBy, v.CreatedAtUtc }).ToListAsync(ct);
        var shownId = doc.CurrentVersionId ?? versions.FirstOrDefault()?.Id;
        var preview = shownId is null ? null : await crm.Db.DocumentVersions.AsNoTracking().Where(v => v.Id == shownId && v.TenantId == tenantId).Select(v => v.Content.Substring(0, Math.Min(v.Content.Length, 4000))).FirstOrDefaultAsync(ct);
        var sourceType = await crm.Db.KnowledgeSources.AsNoTracking().Where(s => s.Id == doc.SourceId).Select(s => s.Type).FirstAsync(ct);
        return Result.Success(new KnowledgeDocumentDto(summary,
            versions.Select(v => new DocumentVersionDto(v.Id, v.VersionNumber, v.Id == doc.CurrentVersionId, v.Status, v.SizeBytes, v.Chars, v.FileName, v.MimeType, v.ChunkCount, v.EmbeddingModel, v.IndexedAtUtc, v.Error, v.CreatedBy, v.CreatedAtUtc)).ToList(),
            preview, sourceType == KnowledgeSourceType.Text));
    }

    public async Task<Result<PagedResult<ChunkDto>>> ChunksAsync(Guid documentId, Guid? versionId, int page, int pageSize, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var (p, size) = CrmFlow.Page(page, pageSize, 100);
        var doc = await crm.Db.KnowledgeDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId && d.TenantId == tenantId, ct);
        if (doc is null) return Result.Failure<PagedResult<ChunkDto>>(Error.NotFound("The document"));
        var vid = versionId ?? doc.CurrentVersionId;
        var q = crm.Db.DocumentChunks.AsNoTracking().Where(c => c.TenantId == tenantId && c.DocumentId == documentId && c.VersionId == vid);
        var total = await q.LongCountAsync(ct);
        var items = await q.OrderBy(c => c.Ordinal).Skip((p - 1) * size).Take(size).Select(c => new ChunkDto(c.Id, c.Ordinal, c.Heading, c.Text, c.CharCount)).ToListAsync(ct);
        return Result.Success(new PagedResult<ChunkDto>(items, p, size, total));
    }

    public Task<Result<KnowledgeDocumentListItemDto>> CreateTextAsync(CreateTextDocumentRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var content = DocumentTextExtractor.Clean(r.Content ?? string.Empty);
            if (content.Length == 0) return Result.Failure<KnowledgeDocumentListItemDto>(Error.Validation("The text is empty."));
            if (content.Length > DocumentVersion.MaxContentChars) return Result.Failure<KnowledgeDocumentListItemDto>(Error.Validation($"The text is longer than {DocumentVersion.MaxContentChars:N0} characters."));
            var source = await ResolveSourceAsync(r.SourceId, r.NewSourceName, KnowledgeSourceType.Text, WrittenSourceName, ct);
            if (source.IsFailure) return Result.Failure<KnowledgeDocumentListItemDto>(source.Error);
            var (doc, version) = await writer.AddDocumentAsync(source.Value.Id, r.Title, r.Kind, null, content, null, "text/plain", System.Text.Encoding.UTF8.GetByteCount(content), ct);
            crm.Record("Ai.DocumentAdded", "KnowledgeDocument", doc.Id, $"Document '{Cut(doc.Title)}' added as text ({content.Length:N0} characters).");
            await crm.Db.SaveChangesAsync(ct);
            writer.Schedule(version.Id);
            return Result.Success((await SingleAsync(doc.Id, ct))!);
        }, logger);

    public Task<Result<KnowledgeDocumentListItemDto>> UploadAsync(UploadDocumentRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            ExtractedText extracted;
            try { extracted = extractor.Extract(r.FileName, r.Data); }
            catch (ExtractionException ex) { return Result.Failure<KnowledgeDocumentListItemDto>(Error.Validation(ex.Message)); }
            if (r.ReplaceDocumentId is { } replaceId)
            {
                var tenantId = crm.TenantId;
                var existing = await crm.Db.KnowledgeDocuments.FirstOrDefaultAsync(d => d.Id == replaceId && d.TenantId == tenantId, ct);
                if (existing is null) return Result.Failure<KnowledgeDocumentListItemDto>(Error.NotFound("The document"));
                return await NewVersionAsync(existing, extracted.Text, r.FileName, extracted.MimeType, r.Data.Length, ct);
            }
            var source = await ResolveSourceAsync(r.SourceId, r.NewSourceName, KnowledgeSourceType.Upload, UploadsSourceName, ct);
            if (source.IsFailure) return Result.Failure<KnowledgeDocumentListItemDto>(source.Error);
            var title = string.IsNullOrWhiteSpace(r.Title) ? (extracted.Title ?? Path.GetFileNameWithoutExtension(r.FileName)) : r.Title;
            if (string.IsNullOrWhiteSpace(title)) title = "Untitled document";
            var (doc, version) = await writer.AddDocumentAsync(source.Value.Id, title.Length > 200 ? title[..200] : title, r.Kind, null, extracted.Text, Path.GetFileName(r.FileName), extracted.MimeType, r.Data.Length, ct);
            crm.Record("Ai.DocumentAdded", "KnowledgeDocument", doc.Id, $"Document '{Cut(doc.Title)}' uploaded ({extracted.MimeType}, {r.Data.Length:N0} bytes).");
            await crm.Db.SaveChangesAsync(ct);
            writer.Schedule(version.Id);
            return Result.Success((await SingleAsync(doc.Id, ct))!);
        }, logger);

    public Task<Result<KnowledgeDocumentListItemDto>> UpdateTextAsync(Guid id, UpdateDocumentTextRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var doc = await crm.Db.KnowledgeDocuments.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
            if (doc is null) return Result.Failure<KnowledgeDocumentListItemDto>(Error.NotFound("The document"));
            var content = DocumentTextExtractor.Clean(r.Content ?? string.Empty);
            if (content.Length == 0) return Result.Failure<KnowledgeDocumentListItemDto>(Error.Validation("The text is empty."));
            if (r.Title is not null || r.Kind is not null) doc.Edit(r.Title ?? doc.Title, r.Kind ?? doc.Kind, crm.Now);
            return await NewVersionAsync(doc, content, null, "text/plain", System.Text.Encoding.UTF8.GetByteCount(content), ct);
        }, logger);

    private async Task<Result<KnowledgeDocumentListItemDto>> NewVersionAsync(KnowledgeDocument doc, string content, string? fileName, string? mime, long size, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var latestHash = await crm.Db.DocumentVersions.AsNoTracking().Where(v => v.TenantId == tenantId && v.DocumentId == doc.Id && v.VersionNumber == doc.LatestVersionNumber).Select(v => v.ContentHash).FirstOrDefaultAsync(ct);
        if (latestHash == KnowledgeWriter.Hash(content)) return Result.Failure<KnowledgeDocumentListItemDto>(Error.Conflict("The text is identical to the current version; nothing was changed."));
        var version = writer.AddVersion(doc, content, fileName, mime, size);
        crm.Record("Ai.DocumentVersionAdded", "KnowledgeDocument", doc.Id, $"Document '{Cut(doc.Title)}' got version {version.VersionNumber}; it replaces the current version once indexed.");
        await crm.Db.SaveChangesAsync(ct);
        writer.Schedule(version.Id);
        return Result.Success((await SingleAsync(doc.Id, ct))!);
    }

    public Task<Result<KnowledgeDocumentListItemDto>> EditAsync(Guid id, EditDocumentRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var doc = await crm.Db.KnowledgeDocuments.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
            if (doc is null) return Result.Failure<KnowledgeDocumentListItemDto>(Error.NotFound("The document"));
            doc.Edit(r.Title, r.Kind, crm.Now);
            crm.Record("Ai.DocumentEdited", "KnowledgeDocument", doc.Id, $"Document '{Cut(doc.Title)}' renamed or reclassified ({doc.Kind}).");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success((await SingleAsync(id, ct))!);
        }, logger);

    public Task<Result<KnowledgeDocumentListItemDto>> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var doc = await crm.Db.KnowledgeDocuments.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
            if (doc is null) return Result.Failure<KnowledgeDocumentListItemDto>(Error.NotFound("The document"));
            doc.SetEnabled(enabled, crm.Now);
            crm.Record(enabled ? "Ai.DocumentEnabled" : "Ai.DocumentDisabled", "KnowledgeDocument", doc.Id, $"Document '{Cut(doc.Title)}' {(enabled ? "enabled: the AI may use it" : "disabled: the AI no longer uses it")}.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success((await SingleAsync(id, ct))!);
        }, logger);

    public Task<Result<KnowledgeDocumentListItemDto>> ReindexAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var doc = await crm.Db.KnowledgeDocuments.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
            if (doc is null) return Result.Failure<KnowledgeDocumentListItemDto>(Error.NotFound("The document"));
            var versionId = await ResetLatestAsync(doc, ct);
            if (versionId is null) return Result.Failure<KnowledgeDocumentListItemDto>(Error.Conflict("This document is already being indexed."));
            crm.Record("Ai.DocumentReindex", "KnowledgeDocument", doc.Id, $"Re-index of '{Cut(doc.Title)}' requested.");
            await crm.Db.SaveChangesAsync(ct);
            writer.Schedule(versionId.Value);
            return Result.Success((await SingleAsync(id, ct))!);
        }, logger);

    public Task<Result<ReindexResult>> ReindexAllAsync(CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId; var queued = new List<Guid>();
            var docs = await crm.Db.KnowledgeDocuments.Where(d => d.TenantId == tenantId).ToListAsync(ct);
            foreach (var doc in docs) if (await ResetLatestAsync(doc, ct) is { } v) queued.Add(v);
            crm.Record("Ai.ReindexAll", "KnowledgeDocument", Guid.Empty, $"Re-index of all documents requested ({queued.Count} queued).");
            await crm.Db.SaveChangesAsync(ct);
            foreach (var v in queued) writer.Schedule(v);
            return Result.Success(new ReindexResult(queued.Count));
        }, logger);

    /// <summary>Puts the newest version back to Pending (unless a worker is busy with it right now). Returns its id, or null when it must be left alone.</summary>
    private async Task<Guid?> ResetLatestAsync(KnowledgeDocument doc, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var v = await crm.Db.DocumentVersions.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.DocumentId == doc.Id && x.VersionNumber == doc.LatestVersionNumber, ct);
        if (v is null) return null;
        var stale = crm.Now.AddMinutes(-Math.Max(1, options.Value.Ingestion.StaleIndexingMinutes));
        if (v.Status == IndexStatus.Indexing && v.IndexingStartedAtUtc >= stale) return null;
        v.ResetForReindex();
        doc.SetStatus(IndexStatus.Pending, crm.Now);
        return v.Id;
    }

    public Task<Result> DeleteDocumentAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var doc = await crm.Db.KnowledgeDocuments.FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantId, ct);
            if (doc is null) return Result.Failure(Error.NotFound("The document"));
            var title = doc.Title;
            await PurgeDocumentAsync(doc.Id, ct);
            crm.Record("Ai.DocumentDeleted", "KnowledgeDocument", id, $"Document '{Cut(title)}' deleted with all versions, passages and vectors.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success();
        }, logger);

    /// <summary>Vectors first (if the store is down nothing is deleted and the user is told), then every row of the document.</summary>
    private async Task PurgeDocumentAsync(Guid documentId, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var db = crm.Db;
        await store.DeleteDocumentAsync(documentId, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.EmbeddingRecords.Where(e => e.TenantId == tenantId && e.DocumentId == documentId).ExecuteDeleteAsync(ct);
        await db.DocumentChunks.Where(c => c.TenantId == tenantId && c.DocumentId == documentId).ExecuteDeleteAsync(ct);
        await db.DocumentVersions.Where(v => v.TenantId == tenantId && v.DocumentId == documentId).ExecuteDeleteAsync(ct);
        await db.KnowledgeDocuments.Where(d => d.TenantId == tenantId && d.Id == documentId).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        foreach (var e in db.ChangeTracker.Entries<KnowledgeDocument>().Where(e => e.Entity.Id == documentId).ToList()) e.State = EntityState.Detached;
    }

    public Task<Result<KnowledgeSourceDto>> AddWebsiteSourceAsync(AddWebsiteSourceRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            if (!Uri.TryCreate(r.Url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp || !string.IsNullOrEmpty(uri.UserInfo))
                return Result.Failure<KnowledgeSourceDto>(Error.Validation("Enter a public web address starting with https://"));
            var tenantId = crm.TenantId;
            if (await crm.Db.KnowledgeSources.CountAsync(s => s.TenantId == tenantId, ct) >= options.Value.Ingestion.MaxSourcesPerTenant) return Result.Failure<KnowledgeSourceDto>(Error.Validation("The workspace has reached its limit of knowledge sources."));
            var url = uri.GetLeftPart(UriPartial.Path) + uri.Query;
            if (await crm.Db.KnowledgeSources.AnyAsync(s => s.TenantId == tenantId && s.Type == KnowledgeSourceType.Website && s.Url == url, ct)) return Result.Failure<KnowledgeSourceDto>(Error.Duplicate("This website is already a knowledge source."));
            var source = KnowledgeSource.Create(tenantId, KnowledgeSourceType.Website, r.Name, url, crm.Now);
            crm.Db.KnowledgeSources.Add(source);
            crm.Record("Ai.SourceAdded", "KnowledgeSource", source.Id, $"Website source '{Cut(source.Name)}' added ({uri.Host}).");
            await crm.Db.SaveChangesAsync(ct);
            try { jobs.EnqueueWebsiteSync(tenantId, source.Id); } catch (Exception ex) { logger.LogWarning(ex, "Website sync could not be queued"); }
            return Result.Success(ToDto(source, 0));
        }, logger);

    public Task<Result<KnowledgeSourceDto>> SyncWebsiteAsync(Guid sourceId, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var source = await crm.Db.KnowledgeSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId && s.TenantId == tenantId, ct);
            if (source is null) return Result.Failure<KnowledgeSourceDto>(Error.NotFound("The source"));
            if (source.Type != KnowledgeSourceType.Website) return Result.Failure<KnowledgeSourceDto>(Error.Conflict("Only website sources can be synced."));
            jobs.EnqueueWebsiteSync(tenantId, sourceId);
            crm.Record("Ai.SourceSync", "KnowledgeSource", sourceId, $"Sync of website source '{Cut(source.Name)}' requested.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(ToDto(source, await crm.Db.KnowledgeDocuments.CountAsync(d => d.TenantId == tenantId && d.SourceId == sourceId, ct)));
        }, logger);

    public Task<Result> DeleteSourceAsync(Guid sourceId, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var source = await crm.Db.KnowledgeSources.FirstOrDefaultAsync(s => s.Id == sourceId && s.TenantId == tenantId, ct);
            if (source is null) return Result.Failure(Error.NotFound("The source"));
            var ids = await crm.Db.KnowledgeDocuments.AsNoTracking().Where(d => d.TenantId == tenantId && d.SourceId == sourceId).Select(d => d.Id).ToListAsync(ct);
            foreach (var id in ids) await PurgeDocumentAsync(id, ct);
            var name = source.Name;
            crm.Db.KnowledgeSources.Remove(source);
            crm.Record("Ai.SourceDeleted", "KnowledgeSource", sourceId, $"Source '{Cut(name)}' deleted with its {ids.Count} document(s).");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success();
        }, logger);

    private async Task<Result<KnowledgeSource>> ResolveSourceAsync(Guid? sourceId, string? newName, KnowledgeSourceType type, string defaultName, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        if (sourceId is { } id)
        {
            var s = await crm.Db.KnowledgeSources.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            if (s is null) return Result.Failure<KnowledgeSource>(Error.NotFound("The source"));
            if (s.Type == KnowledgeSourceType.Website) return Result.Failure<KnowledgeSource>(Error.Conflict("Documents cannot be added by hand to a website source."));
            return Result.Success(s);
        }
        if (!string.IsNullOrWhiteSpace(newName))
        {
            if (await crm.Db.KnowledgeSources.CountAsync(x => x.TenantId == tenantId, ct) >= options.Value.Ingestion.MaxSourcesPerTenant) return Result.Failure<KnowledgeSource>(Error.Validation("The workspace has reached its limit of knowledge sources."));
            var created = KnowledgeSource.Create(tenantId, type, newName, null, crm.Now);
            crm.Db.KnowledgeSources.Add(created);
            return Result.Success(created);
        }
        return Result.Success(await writer.DefaultSourceAsync(type, defaultName, ct));
    }

    private static string Cut(string s) => s.Length > 80 ? s[..80] : s;
}
