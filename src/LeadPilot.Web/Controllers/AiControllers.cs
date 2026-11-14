using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Ai;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Ai;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace LeadPilot.Web.Controllers;

/// <summary>Knowledge base pages. Every mutating action is POST-only (antiforgery) behind Knowledge.Manage; the same use cases back the REST API.</summary>
[Authorize(Policy = PermissionCodes.KnowledgeView)]
public class KnowledgeController(IKnowledgeService knowledge, IAiSettingsService settings, IPermissionEvaluator permissions, IOptions<AiOptions> options) : AppController
{
    private const int UploadLimit = 11 * 1024 * 1024;

    [HttpGet]
    public async Task<IActionResult> Index(KnowledgeDocumentQuery query, CancellationToken ct)
    {
        Crumbs(("AI", null), ("Knowledge base", null));
        var qv = new Dictionary<string, string?> { ["sourceId"] = query.SourceId?.ToString(), ["status"] = query.Status?.ToString(), ["kind"] = query.Kind?.ToString(), ["search"] = query.Search };
        return View(new KnowledgeIndexViewModel { Sources = await knowledge.ListSourcesAsync(ct), Result = await knowledge.ListDocumentsAsync(query, ct), Query = query, QueryValues = qv, CanManage = await permissions.HasAsync(PermissionCodes.KnowledgeManage, ct), Ai = await SummaryAsync(ct) });
    }

    private async Task<AiSettingsSummary> SummaryAsync(CancellationToken ct)
    {
        if (!await permissions.HasAsync(PermissionCodes.AiSettings, ct)) return new AiSettingsSummary { CanSeeSettings = false, HasEmbeddingKey = true, HasKey = true, Enabled = true };
        var s = await settings.GetAsync(ct);
        return new AiSettingsSummary { CanSeeSettings = true, HasEmbeddingKey = s.HasEmbeddingKey, HasKey = s.HasKey, Enabled = s.IsEnabled, NeedingReindex = s.DocumentsNeedingReindex };
    }

    [HttpGet, Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> Add(string tab = "text", Guid? sourceId = null, CancellationToken ct = default)
    {
        Crumbs(("AI", null), ("Knowledge base", Url.Action(nameof(Index))), ("Add knowledge", null));
        return View(await AddModelAsync(tab, ct, sourceId));
    }

    private async Task<KnowledgeAddViewModel> AddModelAsync(string tab, CancellationToken ct, Guid? sourceId = null) => new()
    {
        Tab = tab, Sources = (await knowledge.ListSourcesAsync(ct)).Where(s => s.Type != KnowledgeSourceType.Website).ToList(), SupportedExtensions = Infrastructure_Extensions(), MaxUploadMb = options.Value.Ingestion.MaxUploadBytes / (1024 * 1024),
        Text = new KnowledgeTextFormModel { SourceId = sourceId }, Upload = new KnowledgeUploadFormModel { SourceId = sourceId },
    };

    private static IReadOnlyList<string> Infrastructure_Extensions() => [".txt", ".md", ".markdown", ".html", ".htm", ".pdf", ".docx"];

    [HttpPost, Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> AddText([Bind(Prefix = "Text")] KnowledgeTextFormModel m, CancellationToken ct)
    {
        Crumbs(("AI", null), ("Knowledge base", Url.Action(nameof(Index))), ("Add knowledge", null));
        if (ModelState.IsValid)
        {
            var r = await knowledge.CreateTextAsync(new CreateTextDocumentRequest(m.Title, m.Kind, m.Content, m.SourceId), ct);
            if (r.IsSuccess) { Success("Document saved. It is being indexed; its status turns to Indexed in a moment."); return RedirectToAction(nameof(Document), new { id = r.Value.Id }); }
            AddError(r);
        }
        var vm = await AddModelAsync("text", ct); vm.Text = m;
        return View(nameof(Add), vm);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.KnowledgeManage), RequestSizeLimit(UploadLimit)]
    public async Task<IActionResult> Upload([Bind(Prefix = "Upload")] KnowledgeUploadFormModel m, CancellationToken ct)
    {
        Crumbs(("AI", null), ("Knowledge base", Url.Action(nameof(Index))), ("Add knowledge", null));
        if (m.File is null || m.File.Length == 0) ModelState.AddModelError("Upload.File", "Choose a file.");
        else if (m.File.Length > UploadLimit) ModelState.AddModelError("Upload.File", "The file is too large.");
        if (ModelState.IsValid)
        {
            await using var ms = new MemoryStream(); await m.File!.CopyToAsync(ms, ct);
            var r = await knowledge.UploadAsync(new UploadDocumentRequest(m.Title, m.Kind, m.File.FileName, m.File.ContentType, ms.ToArray(), m.SourceId, null, m.ReplaceDocumentId), ct);
            if (r.IsSuccess) { Success(m.ReplaceDocumentId is null ? "File uploaded. It is being indexed." : "New version uploaded. The previous version keeps answering until the new one is indexed."); return RedirectToAction(nameof(Document), new { id = r.Value.Id }); }
            if (Reject(r) is { } rej) return rej;
            AddError(r);
        }
        if (m.ReplaceDocumentId is { } rid) { Failure(string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage))); return RedirectToAction(nameof(Document), new { id = rid }); }
        var vm = await AddModelAsync("upload", ct); vm.Upload = new KnowledgeUploadFormModel { Title = m.Title, Kind = m.Kind, SourceId = m.SourceId };
        return View(nameof(Add), vm);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> AddWebsite([Bind(Prefix = "Website")] KnowledgeWebsiteFormModel m, CancellationToken ct)
    {
        Crumbs(("AI", null), ("Knowledge base", Url.Action(nameof(Index))), ("Add knowledge", null));
        if (ModelState.IsValid)
        {
            var r = await knowledge.AddWebsiteSourceAsync(new AddWebsiteSourceRequest(m.Name, m.Url, m.Kind), ct);
            if (r.IsSuccess) { Success("Website added. Its pages are being read in the background (public pages of the same site only, robots.txt respected)."); return RedirectToAction(nameof(Index)); }
            AddError(r);
        }
        var vm = await AddModelAsync("website", ct); vm.Website = m;
        return View(nameof(Add), vm);
    }

    [HttpGet]
    public async Task<IActionResult> Document(Guid id, Guid? versionId, int page = 1, CancellationToken ct = default)
    {
        var d = await knowledge.GetDocumentAsync(id, ct);
        if (Reject(d) is { } rej) return rej;
        var chunks = await knowledge.ChunksAsync(id, versionId, page, 10, ct);
        Crumbs(("AI", null), ("Knowledge base", Url.Action(nameof(Index))), (d.Value.Summary.Title, null));
        var current = d.Value.Versions.FirstOrDefault(v => v.IsCurrent)?.Id;
        return View(new KnowledgeDocumentViewModel
        {
            Document = d.Value, Chunks = chunks.Value, ShownVersionId = versionId ?? current, CanManage = await permissions.HasAsync(PermissionCodes.KnowledgeManage, ct), MaxUploadMb = options.Value.Ingestion.MaxUploadBytes / (1024 * 1024),
            Edit = new KnowledgeTextFormModel { Title = d.Value.Summary.Title, Kind = d.Value.Summary.Kind },
        });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> UpdateText(Guid id, [Bind(Prefix = "Edit")] KnowledgeTextFormModel m, CancellationToken ct)
    {
        var r = await knowledge.UpdateTextAsync(id, new UpdateDocumentTextRequest(m.Content ?? string.Empty, m.Title, m.Kind), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("New version saved. It is being indexed; the previous version keeps answering until then."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Document), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> Edit(Guid id, string title, DocumentKind kind, CancellationToken ct)
    {
        var r = await knowledge.EditAsync(id, new EditDocumentRequest(title, kind), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Document updated."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Document), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> SetEnabled(Guid id, bool enabled, CancellationToken ct)
    {
        var r = await knowledge.SetEnabledAsync(id, enabled, ct);
        if (Reject(r) is { } rej) return rej;
        Success(enabled ? "Document enabled: the AI may use it again." : "Document disabled: the AI no longer uses it.");
        return RedirectToAction(nameof(Document), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> Reindex(Guid id, CancellationToken ct)
    {
        var r = await knowledge.ReindexAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Re-index queued."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Document), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> ReindexAll(CancellationToken ct)
    {
        var r = await knowledge.ReindexAllAsync(ct);
        if (r.IsSuccess) Success($"{r.Value.Queued} document(s) queued for re-indexing."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var r = await knowledge.DeleteDocumentAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Document deleted with all its versions, passages and vectors."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> SyncSource(Guid id, CancellationToken ct)
    {
        var r = await knowledge.SyncWebsiteAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Website sync queued."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> DeleteSource(Guid id, CancellationToken ct)
    {
        var r = await knowledge.DeleteSourceAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Source deleted with all its documents."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>Ask a question against the workspace's knowledge and see exactly which passages were found, with what score, and what the AI would answer.</summary>
[Authorize(Policy = PermissionCodes.AiTest)]
public class AiTesterController(IAiTesterService tester, IAiSettingsService settings, IKnowledgeService knowledge, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("AI", null), ("Retrieval tester", null));
        return View(await ModelAsync(null, null, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Ask(AiTesterViewModel m, CancellationToken ct)
    {
        Crumbs(("AI", null), ("Retrieval tester", null));
        if (!ModelState.IsValid) return View(nameof(Index), await ModelAsync(m, null, ct));
        var r = await tester.AskAsync(new AskQuestionRequest(m.Question, m.RetrieveOnly), ct);
        return View(nameof(Index), await ModelAsync(m, r, ct));
    }

    private async Task<AiTesterViewModel> ModelAsync(AiTesterViewModel? form, LeadPilot.Shared.Results.Result<AiTestResultDto>? r, CancellationToken ct)
    {
        var vm = form ?? new AiTesterViewModel();
        if (await permissions.HasAsync(PermissionCodes.AiSettings, ct)) { var s = await settings.GetAsync(ct); vm.Configured = s.HasKey && s.HasEmbeddingKey; }
        else vm.Configured = true;
        vm.IndexedDocuments = (int)(await knowledge.ListDocumentsAsync(new KnowledgeDocumentQuery { Status = IndexStatus.Indexed, PageSize = 1 }, ct)).TotalCount;
        if (r is not null) { if (r.IsSuccess) vm.Result = r.Value; else vm.Error = r.Error.Message; }
        return vm;
    }
}

[Authorize(Policy = PermissionCodes.AiSettings)]
public class AiSettingsController(IAiSettingsService settings, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("AI", null), ("AI settings", null));
        return View(await ModelAsync(await settings.GetAsync(ct), null, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Save([Bind(Prefix = "Form")] AiSettingsFormModel m, CancellationToken ct)
    {
        Crumbs(("AI", null), ("AI settings", null));
        if (!ModelState.IsValid) return View(nameof(Index), await ModelAsync(await settings.GetAsync(ct), m, ct));
        var r = await settings.UpdateAsync(new UpdateAiSettingsRequest(m.ChatModel, m.ClassifierModel, m.SummaryModel, m.EmbeddingModel, m.TopK, m.MinRetrievalScore, m.MinConfidence, m.HistoryMessages, m.MaxContextChars, m.MonthlyTokenLimit, m.MonthlyBudgetUsd, m.UnsupportedReply, m.ToneInstructions, m.EscalationKeywords), ct);
        if (r.IsSuccess) { Success("AI settings saved."); return RedirectToAction(nameof(Index)); }
        AddError(r);
        return View(nameof(Index), await ModelAsync(await settings.GetAsync(ct), m, ct));
    }

    [HttpPost]
    public async Task<IActionResult> SetKey(AiKeyKind kind, AiKeyFormModel m, CancellationToken ct)
    {
        if (!ModelState.IsValid) { Failure("Enter the API key (at least 12 characters)."); return RedirectToAction(nameof(Index)); }
        var r = await settings.SetKeyAsync(kind, new SetAiKeyRequest(m.ApiKey), ct);
        if (r.IsSuccess) Success("Key saved (encrypted). It is never shown again."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> ClearKey(AiKeyKind kind, CancellationToken ct)
    {
        var r = await settings.ClearKeyAsync(kind, ct);
        if (r.IsSuccess) Success("Key removed. The AI is switched off until a key is saved and it is enabled again."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Test(AiKeyKind kind, CancellationToken ct)
    {
        var r = await settings.TestAsync(kind, ct);
        if (r.Ok) Success(r.Message); else Failure(r.Message);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> SetEnabled(bool enabled, CancellationToken ct)
    {
        var r = await settings.SetEnabledAsync(new SetAiEnabledRequest(enabled), ct);
        if (r.IsSuccess) Success(enabled ? "AI enabled. Conversations in AI mode now get suggested replies for your team; nothing is sent automatically." : "AI disabled."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }

    private async Task<AiSettingsViewModel> ModelAsync(AiSettingsDto s, AiSettingsFormModel? form, CancellationToken ct) => new()
    {
        Settings = s, CanManageKnowledge = await permissions.HasAsync(PermissionCodes.KnowledgeManage, ct),
        Form = form ?? new AiSettingsFormModel
        {
            ChatModel = s.ChatModel, ClassifierModel = s.ClassifierModel, SummaryModel = s.SummaryModel, EmbeddingModel = s.EmbeddingModel, TopK = s.TopK, MinRetrievalScore = s.MinRetrievalScore, MinConfidence = s.MinConfidence,
            HistoryMessages = s.HistoryMessages, MaxContextChars = s.MaxContextChars, MonthlyTokenLimit = s.MonthlyTokenLimit, MonthlyBudgetUsd = s.MonthlyBudgetUsd, UnsupportedReply = s.UnsupportedReply, ToneInstructions = s.ToneInstructions, EscalationKeywords = s.EscalationKeywords,
        },
    };
}

/// <summary>What the AI did (suggestions, escalations, manipulation attempts) and what it costs.</summary>
[Authorize(Policy = PermissionCodes.AiView)]
public class AiController(IAiUsageService usage, IAiSuggestionService suggestions, IClock clock) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Usage(int? period, int page = 1, CancellationToken ct = default)
    {
        Crumbs(("AI", null), ("AI usage", null));
        var now = clock.UtcNow; var p = period ?? (now.Year * 100 + now.Month);
        var periods = Enumerable.Range(0, 12).Select(i => now.AddMonths(-i)).Select(d => (Key: d.Year * 100 + d.Month, Label: d.ToString("MMMM yyyy"))).ToList();
        return View(new AiUsageViewModel { Summary = await usage.SummaryAsync(p, ct), Entries = await usage.EntriesAsync(p, page, 25, ct), Periods = periods, Period = p, QueryValues = new Dictionary<string, string?> { ["period"] = p.ToString() } });
    }

    [HttpGet]
    public async Task<IActionResult> Activity(AiActivityQuery query, CancellationToken ct)
    {
        Crumbs(("AI", null), ("AI activity", null));
        var qv = new Dictionary<string, string?> { ["decision"] = query.Decision?.ToString(), ["status"] = query.Status?.ToString() };
        return View(new AiActivityViewModel { Result = await suggestions.ActivityAsync(query, ct), Query = query, QueryValues = qv });
    }
}
