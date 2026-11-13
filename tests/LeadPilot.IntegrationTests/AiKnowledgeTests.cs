using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LeadPilot.Application.Ai;
using LeadPilot.Application.Common;
using LeadPilot.Domain.Ai;
using LeadPilot.Infrastructure.Ai.Jobs;
using LeadPilot.Infrastructure.Discovery.Website;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LeadPilot.IntegrationTests;

/// <summary>Ingestion (text, uploads, websites), versions, re-indexing, disabling and deleting, through the REST API and the real job classes. Embeddings come from the TEST-ONLY hashed bag-of-words embedder.</summary>
[Collection("app")]
public class AiKnowledgeTests(AppFixture app)
{
    private static async Task<T> Ok<T>(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        Assert.True(r.StatusCode == expected, $"expected {expected} but got {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await ApiSession.Json<T>(r);
    }

    private static MultipartFormDataContent File(string name, byte[] data, string type = "application/octet-stream", params (string Key, string Value)[] fields)
    {
        var c = new MultipartFormDataContent(); var f = new ByteArrayContent(data); f.Headers.ContentType = new MediaTypeHeaderValue(type); c.Add(f, "file", name);
        foreach (var (k, v) in fields) c.Add(new StringContent(v), k);
        return c;
    }

    private static byte[] Docx(params (string Style, string Text)[] paragraphs)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart(); main.Document = new Document(new Body());
            foreach (var (style, text) in paragraphs)
                main.Document.Body!.Append(style.Length == 0 ? new Paragraph(new Run(new Text(text))) : new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = style }), new Run(new Text(text))));
            main.Document.Save();
        }
        return ms.ToArray();
    }

    private static byte[] Pdf(string text)
    {
        var b = new UglyToad.PdfPig.Writer.PdfDocumentBuilder(); var font = b.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        b.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText(text, 12, new UglyToad.PdfPig.Core.PdfPoint(25, 700), font);
        return b.Build();
    }

    [RequiresMySqlFact]
    public async Task A_text_document_is_indexed_in_the_background_searchable_and_audited()
    {
        await using var w = await AiWorld.OpenAsync(app);
        var created = await w.AddTextAsync("Standard plan", AiWorld.StandardPlan, DocumentKind.Pricing, index: false);
        Assert.Equal(IndexStatus.Pending, created.Status);
        Assert.Equal(1, app.Ai.Scheduler.Jobs.Count(j => j.TenantId == w.TenantId && j.Kind == "index"));
        Assert.Equal(1, await w.DrainAsync());

        var doc = await w.GetAsync(created.Id);
        Assert.Equal(IndexStatus.Indexed, doc.Status); Assert.True(doc.ChunkCount >= 1); Assert.NotNull(doc.IndexedAtUtc); Assert.Null(doc.Error);
        var full = await Ok<KnowledgeDocumentDto>(await w.Admin.Get($"/api/v1/knowledge/documents/{created.Id}"));
        var v1 = Assert.Single(full.Versions); Assert.True(v1.IsCurrent); Assert.Equal(1, v1.VersionNumber); Assert.Equal(IndexStatus.Indexed, v1.Status); Assert.Equal("voyage-3.5", v1.EmbeddingModel);
        Assert.Contains("500 USD", full.CurrentContentPreview);

        // one embedding row per passage, normalised, belonging to this tenant
        var rows = await app.Db(db => db.EmbeddingRecords.IgnoreQueryFilters().Where(e => e.DocumentId == created.Id).ToListAsync());
        Assert.Equal(doc.ChunkCount, rows.Count); Assert.All(rows, r => { Assert.Equal(w.TenantId, r.TenantId); Assert.Equal(FakeEmbeddingProvider.Dimensions, r.Dimensions); Assert.Equal(1.0, Math.Sqrt(r.ToArray().Sum(x => (double)x * x)), 3); });

        var found = await w.RetrieveAsync("How much does the Standard plan cost?");
        var top = Assert.Single(found.Chunks.Take(1)); Assert.Equal(created.Id, top.DocumentId); Assert.Contains("500 USD", top.Text); Assert.True(top.Score > 0.35);
        Assert.Equal(RetrievalOutcome.Found, found.Outcome);

        var audit = await app.Db(db => db.AuditLogs.IgnoreQueryFilters().Where(a => a.TenantId == w.TenantId && a.EntityId == created.Id.ToString()).Select(a => a.Action).ToListAsync());
        Assert.Contains("Ai.DocumentAdded", audit); Assert.Contains("Ai.DocumentIndexed", audit);
    }

    [RequiresMySqlFact]
    public async Task A_long_document_is_split_into_passages_and_the_right_passage_is_found()
    {
        await using var w = await AiWorld.OpenAsync(app);
        var sb = new StringBuilder("# School ERP guide\n\n");
        for (var i = 0; i < 30; i++) sb.Append($"## Topic {i}\n\nThis section number {i} explains the module called Module{i} used for topic{i} workflows in the school. ").Append(string.Join(' ', Enumerable.Range(0, 60).Select(k => $"filler{i}x{k}"))).Append("\n\n");
        sb.Append("## Transport\n\nThe transport module tracks school buses, routes and driver schedules for parents.\n\n");
        var doc = await w.AddTextAsync("Guide", sb.ToString(), DocumentKind.Feature);
        Assert.True(doc.ChunkCount > 10, $"expected many passages, got {doc.ChunkCount}");
        var found = await w.RetrieveAsync("tracks school buses routes driver schedules");
        Assert.Contains("buses", found.Chunks[0].Text); Assert.Equal("Transport", found.Chunks[0].Heading);
    }

    [RequiresMySqlFact]
    public async Task Uploads_of_text_markdown_html_docx_and_pdf_are_extracted_indexed_and_searchable()
    {
        await using var w = await AiWorld.OpenAsync(app);
        var files = new (string Name, byte[] Data, string Expect)[]
        {
            ("faq.txt", Encoding.UTF8.GetBytes("Our support desk answers every ticket within four business hours."), "four business hours"),
            ("policy.md", Encoding.UTF8.GetBytes("# Refund policy\n\nRefunds are possible within thirty days of purchase."), "thirty days"),
            ("page.html", Encoding.UTF8.GetBytes("<html><body><script>evil()</script><h1>Integrations</h1><p>We integrate with Google Classroom and Microsoft Teams.</p></body></html>"), "Google Classroom"),
            ("brochure.docx", Docx(("Heading1", "Security"), ("", "All data is encrypted at rest using AES-256 and backups run nightly.")), "AES-256"),
            ("sheet.pdf", Pdf("Training is included: two onsite days for administrators and teachers"), "onsite days"),
        };
        foreach (var (name, data, expect) in files)
        {
            var doc = await Ok<KnowledgeDocumentListItemDto>(await w.Admin.Http.PostAsync("/api/v1/knowledge/documents/upload", File(name, data, "application/octet-stream", ("kind", "Support"))), HttpStatusCode.Created);
            Assert.Equal(IndexStatus.Pending, doc.Status); Assert.Equal("Uploaded files", doc.SourceName);
            await w.DrainAsync();
            Assert.Equal(IndexStatus.Indexed, (await w.GetAsync(doc.Id)).Status);
            var found = await w.RetrieveAsync(expect);
            Assert.Contains(expect.Split(' ')[0], found.Chunks.First().Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(found.Chunks, c => c.Text.Contains("evil()"));
        }
        Assert.Equal(5, (await Ok<PagedResult<KnowledgeDocumentListItemDto>>(await w.Admin.Get("/api/v1/knowledge/documents"))).TotalCount);
        Assert.Single(await Ok<List<KnowledgeSourceDto>>(await w.Admin.Get("/api/v1/knowledge/sources")));   // all uploads share one default source
    }

    [RequiresMySqlFact]
    public async Task Unsafe_or_unsupported_uploads_are_refused_without_creating_anything()
    {
        await using var w = await AiWorld.OpenAsync(app);
        async Task Refused(string name, byte[] data, string why)
        {
            var res = await w.Admin.Http.PostAsync("/api/v1/knowledge/documents/upload", File(name, data));
            Assert.True(res.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge, $"{why}: {(int)res.StatusCode}");
        }
        await Refused("run.exe", [0x4D, 0x5A, 1, 2, 3], "executable");
        await Refused("fake.pdf", Encoding.UTF8.GetBytes("not a pdf"), "extension mismatch");
        await Refused("empty.txt", [], "empty");
        await Refused("book.xlsx", [1, 2, 3], "spreadsheet");
        await Refused("big.txt", new byte[11 * 1024 * 1024 + 10], "too large");
        Assert.Equal(0, (await Ok<PagedResult<KnowledgeDocumentListItemDto>>(await w.Admin.Get("/api/v1/knowledge/documents"))).TotalCount);
        Assert.DoesNotContain(app.Ai.Scheduler.Jobs, j => j.TenantId == w.TenantId);
    }

    [RequiresMySqlFact]
    public async Task Updating_a_document_creates_a_new_version_the_old_one_keeps_answering_until_the_new_one_is_indexed()
    {
        await using var w = await AiWorld.OpenAsync(app);
        var doc = await w.AddTextAsync("Plan", "The Basic plan costs 100 USD per month for small schools.");
        var res = await w.Admin.Put($"/api/v1/knowledge/documents/{doc.Id}/text", new { content = "The Basic plan costs 120 USD per month for small schools." });
        var updated = await Ok<KnowledgeDocumentListItemDto>(res);
        Assert.Equal(2, updated.LatestVersion); Assert.Equal(IndexStatus.Pending, updated.Status);

        // before the job runs: the previous version is still what the AI finds
        var before = await w.RetrieveAsync("Basic plan cost small schools");
        Assert.Contains("100 USD", before.Chunks[0].Text); Assert.Equal(1, before.Chunks[0].VersionNumber);

        await w.DrainAsync();
        var after = await w.RetrieveAsync("Basic plan cost small schools");
        Assert.Contains("120 USD", after.Chunks[0].Text); Assert.Equal(2, after.Chunks[0].VersionNumber); Assert.DoesNotContain(after.Chunks, c => c.Text.Contains("100 USD"));

        var full = await Ok<KnowledgeDocumentDto>(await w.Admin.Get($"/api/v1/knowledge/documents/{doc.Id}"));
        Assert.Equal([2, 1], full.Versions.Select(v => v.VersionNumber)); Assert.True(full.Versions[0].IsCurrent); Assert.Equal(IndexStatus.Superseded, full.Versions[1].Status);
        // the old text is kept as history, its passages and vectors are gone
        Assert.Equal(0, await app.Db(db => db.DocumentChunks.IgnoreQueryFilters().CountAsync(c => c.DocumentId == doc.Id && c.VersionId == full.Versions[1].Id)));
        Assert.Equal(0, await app.Db(db => db.EmbeddingRecords.IgnoreQueryFilters().CountAsync(c => c.DocumentId == doc.Id && c.VersionId == full.Versions[1].Id)));
        Assert.Equal(1, await app.Db(db => db.DocumentVersions.IgnoreQueryFilters().CountAsync(v => v.Id == full.Versions[1].Id && v.Content.Contains("100 USD"))));

        // identical text creates nothing
        Assert.Equal(HttpStatusCode.Conflict, (await w.Admin.Put($"/api/v1/knowledge/documents/{doc.Id}/text", new { content = "The Basic plan costs 120 USD per month for small schools." })).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task A_new_file_version_replaces_an_uploaded_document()
    {
        await using var w = await AiWorld.OpenAsync(app);
        var doc = await Ok<KnowledgeDocumentListItemDto>(await w.Admin.Http.PostAsync("/api/v1/knowledge/documents/upload", File("terms.txt", Encoding.UTF8.GetBytes("Notice period is thirty days for cancellations."))), HttpStatusCode.Created);
        await w.DrainAsync();
        var v2 = await Ok<KnowledgeDocumentListItemDto>(await w.Admin.Http.PostAsync("/api/v1/knowledge/documents/upload", File("terms-2027.txt", Encoding.UTF8.GetBytes("Notice period is sixty days for cancellations."), "text/plain", ("replaceDocumentId", doc.Id.ToString()))), HttpStatusCode.Created);
        Assert.Equal(doc.Id, v2.Id); Assert.Equal(2, v2.LatestVersion);
        await w.DrainAsync();
        Assert.Contains("sixty days", (await w.RetrieveAsync("notice period cancellations")).Chunks[0].Text);
    }

    [RequiresMySqlFact]
    public async Task A_disabled_document_is_never_retrieved_and_enabling_it_needs_no_new_embeddings()
    {
        await using var w = await AiWorld.OpenAsync(app);
        var doc = await w.AddTextAsync("Plan", AiWorld.StandardPlan);
        Assert.NotEmpty((await w.RetrieveAsync("Standard plan cost")).Chunks);
        await Ok<KnowledgeDocumentListItemDto>(await w.Admin.Post($"/api/v1/knowledge/documents/{doc.Id}/disable"));
        var hidden = await w.RetrieveAsync("Standard plan cost"); Assert.Empty(hidden.Chunks); Assert.Equal(RetrievalOutcome.NoMatch, hidden.Outcome);
        var embeddedBefore = app.Ai.EmbeddingCalls;
        await Ok<KnowledgeDocumentListItemDto>(await w.Admin.Post($"/api/v1/knowledge/documents/{doc.Id}/enable"));
        Assert.NotEmpty((await w.RetrieveAsync("Standard plan cost")).Chunks);
        Assert.Equal(embeddedBefore + 1, app.Ai.EmbeddingCalls);   // only the two query embeddings after the toggle, nothing re-embedded
    }

    [RequiresMySqlFact]
    public async Task Reindexing_one_or_all_documents_rebuilds_the_passages()
    {
        await using var w = await AiWorld.OpenAsync(app);
        var a = await w.AddTextAsync("A", "Alpha document about attendance tracking with biometric devices."); var b = await w.AddTextAsync("B", "Beta document about fee vouchers and online payment gateways.");
        var firstChunks = await app.Db(db => db.DocumentChunks.IgnoreQueryFilters().Where(c => c.DocumentId == a.Id).Select(c => c.Id).ToListAsync());
        var res = await Ok<KnowledgeDocumentListItemDto>(await w.Admin.Post($"/api/v1/knowledge/documents/{a.Id}/reindex"));
        Assert.Equal(IndexStatus.Pending, res.Status);
        await w.DrainAsync();
        Assert.Equal(IndexStatus.Indexed, (await w.GetAsync(a.Id)).Status);
        var secondChunks = await app.Db(db => db.DocumentChunks.IgnoreQueryFilters().Where(c => c.DocumentId == a.Id).Select(c => c.Id).ToListAsync());
        Assert.Equal(firstChunks.Count, secondChunks.Count); Assert.Empty(firstChunks.Intersect(secondChunks));   // rebuilt, not duplicated
        Assert.Equal(2, (await Ok<ReindexResult>(await w.Admin.Post("/api/v1/knowledge/reindex-all"))).Queued);
        Assert.Equal(2, await w.DrainAsync());
        Assert.NotEmpty((await w.RetrieveAsync("biometric attendance")).Chunks); Assert.NotEmpty((await w.RetrieveAsync("fee vouchers payment gateways")).Chunks);
    }

    [RequiresMySqlFact]
    public async Task Deleting_a_document_removes_versions_passages_embeddings_and_vectors_and_deleting_a_source_removes_its_documents()
    {
        await using var w = await AiWorld.OpenAsync(app);
        var doc = await w.AddTextAsync("To delete", "Secret roadmap: the mobile app launches in March with offline attendance.");
        var keep = await w.AddTextAsync("To keep", "The helpdesk is open on weekdays from nine to five.");
        await w.Admin.Put($"/api/v1/knowledge/documents/{doc.Id}/text", new { content = "Secret roadmap v2: the mobile app launches in April." }); await w.DrainAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await w.Admin.Delete($"/api/v1/knowledge/documents/{doc.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await w.Admin.Get($"/api/v1/knowledge/documents/{doc.Id}")).StatusCode);
        Assert.Empty((await w.RetrieveAsync("secret roadmap mobile app launches")).Chunks);
        await app.Db(async db =>
        {
            Assert.Equal(0, await db.KnowledgeDocuments.IgnoreQueryFilters().CountAsync(d => d.Id == doc.Id));
            Assert.Equal(0, await db.DocumentVersions.IgnoreQueryFilters().CountAsync(d => d.DocumentId == doc.Id));
            Assert.Equal(0, await db.DocumentChunks.IgnoreQueryFilters().CountAsync(d => d.DocumentId == doc.Id));
            Assert.Equal(0, await db.EmbeddingRecords.IgnoreQueryFilters().CountAsync(d => d.DocumentId == doc.Id));
            Assert.Equal(1, await db.KnowledgeDocuments.IgnoreQueryFilters().CountAsync(d => d.Id == keep.Id));
        });
        Assert.Contains("Ai.DocumentDeleted", await app.Db(db => db.AuditLogs.IgnoreQueryFilters().Where(a => a.TenantId == w.TenantId && a.EntityId == doc.Id.ToString()).Select(a => a.Action).ToListAsync()));

        var source = (await Ok<List<KnowledgeSourceDto>>(await w.Admin.Get("/api/v1/knowledge/sources"))).Single();
        Assert.Equal(HttpStatusCode.NoContent, (await w.Admin.Delete($"/api/v1/knowledge/sources/{source.Id}")).StatusCode);
        Assert.Empty(await Ok<List<KnowledgeSourceDto>>(await w.Admin.Get("/api/v1/knowledge/sources")));
        Assert.Equal(0, await app.Db(db => db.EmbeddingRecords.IgnoreQueryFilters().CountAsync(e => e.TenantId == w.TenantId)));
        Assert.Equal(0, await w.InScope(async sp => await sp.GetRequiredService<IVectorStore>().CountAsync(default)));
    }

    [RequiresMySqlFact]
    public async Task Indexing_fails_visibly_without_an_embedding_key_and_recovers_after_re_indexing()
    {
        await using var w = await AiWorld.OpenAsync(app, configure: false);
        var doc = await w.AddTextAsync("Plan", AiWorld.StandardPlan);
        var failed = await w.GetAsync(doc.Id);
        Assert.Equal(IndexStatus.Failed, failed.Status); Assert.Contains("embedding API key", failed.Error);
        await w.ConfigureAsync();
        await w.Admin.Post($"/api/v1/knowledge/documents/{doc.Id}/reindex"); await w.DrainAsync();
        Assert.Equal(IndexStatus.Indexed, (await w.GetAsync(doc.Id)).Status);
    }

    [RequiresMySqlFact]
    public async Task A_provider_outage_marks_the_version_failed_and_retries_through_the_job_then_succeeds()
    {
        await using var w = await AiWorld.OpenAsync(app);
        var created = await w.AddTextAsync("Plan", AiWorld.StandardPlan, index: false);
        app.Ai.EmbedFault = () => new LeadPilot.Application.Ai.AiProviderException(AiFailureKind.Unavailable, "Voyage AI is unavailable (HTTP 503).", true);
        var job = new KnowledgeJob(w.Worker.Scopes); var versionId = app.Ai.Scheduler.Jobs.Single(j => j.TenantId == w.TenantId).Id; app.Ai.Scheduler.Jobs.Clear();
        await Assert.ThrowsAsync<LeadPilot.Application.Ai.AiProviderException>(() => job.IndexAsync(w.TenantId, versionId, default));   // Hangfire would retry
        var failed = await w.GetAsync(created.Id);
        Assert.Equal(IndexStatus.Failed, failed.Status); Assert.Contains("503", failed.Error);
        Assert.Equal(0, await app.Db(db => db.DocumentChunks.IgnoreQueryFilters().CountAsync(c => c.DocumentId == created.Id)));   // no half-built index
        app.Ai.EmbedFault = null;
        await job.IndexAsync(w.TenantId, versionId, default);                                                                       // the retry
        Assert.Equal(IndexStatus.Indexed, (await w.GetAsync(created.Id)).Status);
    }

    [RequiresMySqlFact]
    public async Task Running_the_same_index_job_twice_or_concurrently_creates_no_duplicates()
    {
        await using var w = await AiWorld.OpenAsync(app);
        var created = await w.AddTextAsync("Plan", AiWorld.StandardPlan, index: false);
        var versionId = app.Ai.Scheduler.Jobs.Single(j => j.TenantId == w.TenantId).Id; app.Ai.Scheduler.Jobs.Clear();
        var job = new KnowledgeJob(w.Worker.Scopes);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => job.IndexAsync(w.TenantId, versionId, default)));
        await job.IndexAsync(w.TenantId, versionId, default);
        var d = await w.GetAsync(created.Id);
        Assert.Equal(IndexStatus.Indexed, d.Status);
        Assert.Equal(d.ChunkCount, await app.Db(db => db.EmbeddingRecords.IgnoreQueryFilters().CountAsync(e => e.DocumentId == created.Id)));
        Assert.Equal(d.ChunkCount, await app.Db(db => db.DocumentChunks.IgnoreQueryFilters().CountAsync(e => e.DocumentId == created.Id)));
    }

    [RequiresMySqlFact]
    public async Task A_lost_job_is_picked_up_by_the_sweep_and_a_crashed_run_is_taken_over()
    {
        await using var w = await AiWorld.OpenAsync(app);
        var created = await w.AddTextAsync("Plan", AiWorld.StandardPlan, index: false);
        app.Ai.Scheduler.Jobs.Clear();                                                             // the scheduler lost the job
        await app.Db(db => db.DocumentVersions.IgnoreQueryFilters().Where(v => v.DocumentId == created.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-10))));
        using (var scope = w.Worker.Services.CreateScope()) await scope.ServiceProvider.GetRequiredService<KnowledgeSweepJob>().RunAsync(default);   // the real sweep job from the worker composition
        Assert.Single(app.Ai.Scheduler.Jobs, j => j.TenantId == w.TenantId && j.Kind == "index");

        // a worker that died mid-way: the version is stuck in Indexing since long ago, the next run takes over
        await app.Db(db => db.DocumentVersions.IgnoreQueryFilters().Where(v => v.DocumentId == created.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, IndexStatus.Indexing).SetProperty(v => v.IndexingStartedAtUtc, DateTime.UtcNow.AddHours(-2))));
        await w.DrainAsync();
        Assert.Equal(IndexStatus.Indexed, (await w.GetAsync(created.Id)).Status);
    }

    [RequiresMySqlFact]
    public async Task The_scheduler_being_down_does_not_lose_the_document_it_stays_pending()
    {
        await using var w = await AiWorld.OpenAsync(app);
        app.Ai.Scheduler.ThrowOnEnqueue = true;
        var doc = await w.AddTextAsync("Plan", AiWorld.StandardPlan, index: false);
        Assert.Equal(IndexStatus.Pending, doc.Status);
        app.Ai.Scheduler.ThrowOnEnqueue = false;
        Assert.Equal(HttpStatusCode.OK, (await w.Admin.Post($"/api/v1/knowledge/documents/{doc.Id}/reindex")).StatusCode);
        await w.DrainAsync();
        Assert.Equal(IndexStatus.Indexed, (await w.GetAsync(doc.Id)).Status);
    }

    // ---------------------------------------------------------------------------------------------------------------- website sources

    private static string Html(string title, string body, params string[] links) =>
        $"<html><head><title>{title}</title></head><body><main><h1>{title}</h1><p>{body}</p>{string.Concat(links.Select(l => $"<a href=\"{l}\">link</a>"))}</main></body></html>";

    [RequiresMySqlFact]
    public async Task A_website_source_reads_same_site_pages_respects_robots_and_noindex_and_versions_changed_pages()
    {
        var site = new ScriptedWebsite();
        site.Pages["https://school.example/robots.txt"] = new(200, "text/plain", "User-agent: *\nDisallow: /private");
        site.Pages["https://school.example"] = new(200, "text/html", Html("Home", "Welcome to Example School ERP. We help schools manage attendance and fees every day.", "/pricing", "/private/staff", "https://other.example/offsite", "/hidden", "/file.pdf", "mailto:x@y.z"));
        site.Pages["https://school.example/pricing"] = new(200, "text/html", Html("Pricing", "The Standard plan costs 500 USD per month for up to 1000 students, billed monthly."));
        site.Pages["https://school.example/private/staff"] = new(200, "text/html", Html("Staff", "Internal staff only page that must never be read by the crawler at all."));
        site.Pages["https://school.example/hidden"] = new(200, "text/html", "<html><head><meta name=\"robots\" content=\"noindex\"><title>Hidden</title></head><body><main><p>This page asks not to be indexed so it must not be stored anywhere.</p></main></body></html>");
        site.Pages["https://other.example/offsite"] = new(200, "text/html", Html("Offsite", "A different site that must not be followed from the school site at all ever."));

        await using var w = await AiWorld.OpenAsync(app, configureWorker: s => { s.RemoveAll<IHttpSender>(); s.AddSingleton<IHttpSender>(site); s.RemoveAll<IDnsResolver>(); s.AddSingleton<IDnsResolver>(site); });
        var added = await Ok<KnowledgeSourceDto>(await w.Admin.Post("/api/v1/knowledge/sources/website", new { name = "Our website", url = "https://school.example/" }), HttpStatusCode.Created);
        Assert.Equal(KnowledgeSourceType.Website, added.Type);
        Assert.Equal(3, await w.DrainAsync());     // the sync job and the two index jobs it queued

        var docs = (await Ok<PagedResult<KnowledgeDocumentListItemDto>>(await w.Admin.Get("/api/v1/knowledge/documents"))).Items;
        Assert.Equal(["https://school.example", "https://school.example/pricing"], docs.Select(d => d.SourceUrl).OrderBy(x => x));
        Assert.All(docs, d => Assert.Equal(IndexStatus.Indexed, d.Status));
        Assert.DoesNotContain(site.Fetched, u => u.Contains("/private/staff") || u.Contains("other.example") || u.EndsWith(".pdf"));
        Assert.Contains((await w.RetrieveAsync("Standard plan costs 500 USD students")).Chunks, c => c.DocumentTitle == "Pricing");
        Assert.DoesNotContain((await w.RetrieveAsync("Internal staff only page crawler")).Chunks, c => c.Text.Contains("Internal staff"));
        Assert.DoesNotContain((await w.RetrieveAsync("asks not to be indexed stored anywhere")).Chunks, c => c.Text.Contains("not be indexed"));
        var source = (await Ok<List<KnowledgeSourceDto>>(await w.Admin.Get("/api/v1/knowledge/sources"))).Single(); Assert.NotNull(source.LastSyncAtUtc); Assert.Null(source.LastSyncError);

        // second sync: nothing changed → no new versions; a changed page → exactly one new version
        site.Pages["https://school.example/pricing"] = new(200, "text/html", Html("Pricing", "The Standard plan costs 550 USD per month for up to 1000 students, billed monthly."));
        await Ok<KnowledgeSourceDto>(await w.Admin.Post($"/api/v1/knowledge/sources/{added.Id}/sync")); await w.DrainAsync(); await w.DrainAsync();
        var after = (await Ok<PagedResult<KnowledgeDocumentListItemDto>>(await w.Admin.Get("/api/v1/knowledge/documents"))).Items;
        Assert.Equal(1, after.Single(d => d.Title == "Pricing").LatestVersion - 1); Assert.Equal(1, after.Single(d => d.Title == "Home").LatestVersion);
        Assert.Contains("550 USD", (await w.RetrieveAsync("Standard plan costs students")).Chunks[0].Text);
    }

    [RequiresMySqlFact]
    public async Task A_website_source_on_an_internal_address_is_blocked_by_the_ssrf_guard()
    {
        await using var w = await AiWorld.OpenAsync(app);
        foreach (var url in new[] { "http://127.0.0.1/", "http://169.254.169.254/latest/meta-data/", "http://localhost/", "https://10.0.0.5/", "http://[::1]/", "https://intranet.local/" })
        {
            var added = await Ok<KnowledgeSourceDto>(await w.Admin.Post("/api/v1/knowledge/sources/website", new { name = "Internal " + url, url }), HttpStatusCode.Created);
            await w.DrainAsync();
            var source = (await Ok<List<KnowledgeSourceDto>>(await w.Admin.Get("/api/v1/knowledge/sources"))).Single(s => s.Id == added.Id);
            Assert.True(source.LastSyncError is not null && source.DocumentCount == 0, $"{url}: {source.LastSyncError}");
        }
        Assert.Equal(0, (await Ok<PagedResult<KnowledgeDocumentListItemDto>>(await w.Admin.Get("/api/v1/knowledge/documents"))).TotalCount);
        foreach (var bad in new[] { "ftp://example.com/", "javascript:alert(1)", "https://user:pw@example.com/", "not a url" })
            Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post("/api/v1/knowledge/sources/website", new { name = "Bad", url = bad })).StatusCode);
    }
}
