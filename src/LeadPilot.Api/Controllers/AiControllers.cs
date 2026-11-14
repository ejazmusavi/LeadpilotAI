using LeadPilot.Application.Ai;
using LeadPilot.Application.Common;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Api.Controllers;

/// <summary>
/// The knowledge base the AI answers from: sources (uploads, written text, websites), documents, versions and re-indexing. Everything is scoped to the signed-in workspace; ids of other workspaces answer 404. Text is
/// extracted safely (size, page and zip limits) and website content is fetched through the SSRF-safe fetcher.
/// </summary>
[Route("api/v1/knowledge")]
public class KnowledgeController(IKnowledgeService knowledge) : ApiControllerBase
{
    private const int UploadLimit = 11 * 1024 * 1024;

    [HttpGet("sources"), Authorize(Policy = PermissionCodes.KnowledgeView)]
    public async Task<IReadOnlyList<KnowledgeSourceDto>> Sources(CancellationToken ct) => await knowledge.ListSourcesAsync(ct);

    /// <summary>Adds a public website. Its pages (same host, robots.txt respected, a few pages only) are read in the background.</summary>
    [HttpPost("sources/website"), Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> AddWebsite(AddWebsiteSourceRequest request, CancellationToken ct) { var r = await knowledge.AddWebsiteSourceAsync(request, ct); return r.IsSuccess ? Created($"api/v1/knowledge/sources", r.Value) : Fail(r); }

    [HttpPost("sources/{id:guid}/sync"), Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> Sync(Guid id, CancellationToken ct) => Respond(await knowledge.SyncWebsiteAsync(id, ct));

    /// <summary>Deletes the source with all its documents, versions, passages and vectors.</summary>
    [HttpDelete("sources/{id:guid}"), Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> DeleteSource(Guid id, CancellationToken ct) => Respond(await knowledge.DeleteSourceAsync(id, ct));

    [HttpGet("documents"), Authorize(Policy = PermissionCodes.KnowledgeView)]
    public async Task<PagedResult<KnowledgeDocumentListItemDto>> Documents([FromQuery] KnowledgeDocumentQuery query, CancellationToken ct) => await knowledge.ListDocumentsAsync(query, ct);

    [HttpGet("documents/{id:guid}"), Authorize(Policy = PermissionCodes.KnowledgeView)]
    public async Task<IActionResult> Document(Guid id, CancellationToken ct) => Respond(await knowledge.GetDocumentAsync(id, ct));

    [HttpGet("documents/{id:guid}/chunks"), Authorize(Policy = PermissionCodes.KnowledgeView)]
    public async Task<IActionResult> Chunks(Guid id, [FromQuery] Guid? versionId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) => Respond(await knowledge.ChunksAsync(id, versionId, page, pageSize, ct));

    /// <summary>Adds a written document (product description, pricing, FAQ, policy…). Indexed in the background.</summary>
    [HttpPost("documents/text"), Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> CreateText(CreateTextDocumentRequest request, CancellationToken ct) { var r = await knowledge.CreateTextAsync(request, ct); return r.IsSuccess ? CreatedAtAction(nameof(Document), new { id = r.Value.Id }, r.Value) : Fail(r); }

    /// <summary>Uploads a .txt, .md, .html, .pdf or .docx file (10 MB, 300 PDF pages). With <c>replaceDocumentId</c> the file becomes a new version of that document.</summary>
    [HttpPost("documents/upload"), Consumes("multipart/form-data"), RequestSizeLimit(UploadLimit), Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> Upload(IFormFile file, [FromForm] DocumentKind kind = DocumentKind.Other, [FromForm] string? title = null, [FromForm] Guid? sourceId = null, [FromForm] Guid? replaceDocumentId = null, CancellationToken ct = default)
    {
        if (file is null || file.Length == 0) return Fail(LeadPilot.Shared.Results.Result.Failure(LeadPilot.Shared.Results.Error.Validation("Attach a file in the 'file' form field.")));
        if (file.Length > UploadLimit) return Fail(LeadPilot.Shared.Results.Result.Failure(LeadPilot.Shared.Results.Error.TooLarge("The file is too large.")));
        await using var ms = new MemoryStream(); await file.CopyToAsync(ms, ct);
        var r = await knowledge.UploadAsync(new UploadDocumentRequest(title, kind, file.FileName, file.ContentType, ms.ToArray(), sourceId, null, replaceDocumentId), ct);
        return r.IsSuccess ? CreatedAtAction(nameof(Document), new { id = r.Value.Id }, r.Value) : Fail(r);
    }

    /// <summary>New version of a written document (the old version stays as history and keeps answering until the new one is indexed).</summary>
    [HttpPut("documents/{id:guid}/text"), Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> UpdateText(Guid id, UpdateDocumentTextRequest request, CancellationToken ct) => Respond(await knowledge.UpdateTextAsync(id, request, ct));

    [HttpPut("documents/{id:guid}"), Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> Edit(Guid id, EditDocumentRequest request, CancellationToken ct) => Respond(await knowledge.EditAsync(id, request, ct));

    /// <summary>A disabled document stays stored but is never used by the AI.</summary>
    [HttpPost("documents/{id:guid}/enable"), Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> Enable(Guid id, CancellationToken ct) => Respond(await knowledge.SetEnabledAsync(id, true, ct));

    [HttpPost("documents/{id:guid}/disable"), Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct) => Respond(await knowledge.SetEnabledAsync(id, false, ct));

    [HttpPost("documents/{id:guid}/reindex"), Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> Reindex(Guid id, CancellationToken ct) => Respond(await knowledge.ReindexAsync(id, ct));

    /// <summary>Re-indexes every document (after changing the embedding model).</summary>
    [HttpPost("reindex-all"), Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> ReindexAll(CancellationToken ct) => Respond(await knowledge.ReindexAllAsync(ct));

    /// <summary>Deletes the document with all versions, passages and vectors.</summary>
    [HttpDelete("documents/{id:guid}"), Authorize(Policy = PermissionCodes.KnowledgeManage)]
    public async Task<IActionResult> DeleteDocument(Guid id, CancellationToken ct) => Respond(await knowledge.DeleteDocumentAsync(id, ct));
}

/// <summary>
/// AI settings, usage, the retrieval tester and the AI's suggestions. Provider keys are stored encrypted per workspace and are never returned (only the last four characters). The AI only DRAFTS replies for staff in
/// this phase: nothing is sent to a customer automatically.
/// </summary>
[Route("api/v1/ai")]
public class AiController(IAiSettingsService settings, IAiUsageService usage, IAiTesterService tester, IAiSuggestionService suggestions) : ApiControllerBase
{
    [HttpGet("settings"), Authorize(Policy = PermissionCodes.AiSettings)]
    public async Task<AiSettingsDto> GetSettings(CancellationToken ct) => await settings.GetAsync(ct);

    [HttpPut("settings"), Authorize(Policy = PermissionCodes.AiSettings)]
    public async Task<IActionResult> UpdateSettings(UpdateAiSettingsRequest request, CancellationToken ct) => Respond(await settings.UpdateAsync(request, ct));

    [HttpPut("settings/keys/chat"), Authorize(Policy = PermissionCodes.AiSettings)]
    public async Task<IActionResult> SetChatKey(SetAiKeyRequest request, CancellationToken ct) => Respond(await settings.SetKeyAsync(AiKeyKind.Chat, request, ct));

    [HttpPut("settings/keys/embedding"), Authorize(Policy = PermissionCodes.AiSettings)]
    public async Task<IActionResult> SetEmbeddingKey(SetAiKeyRequest request, CancellationToken ct) => Respond(await settings.SetKeyAsync(AiKeyKind.Embedding, request, ct));

    [HttpDelete("settings/keys/chat"), Authorize(Policy = PermissionCodes.AiSettings)]
    public async Task<IActionResult> ClearChatKey(CancellationToken ct) => Respond(await settings.ClearKeyAsync(AiKeyKind.Chat, ct));

    [HttpDelete("settings/keys/embedding"), Authorize(Policy = PermissionCodes.AiSettings)]
    public async Task<IActionResult> ClearEmbeddingKey(CancellationToken ct) => Respond(await settings.ClearKeyAsync(AiKeyKind.Embedding, ct));

    /// <summary>Turns the AI on or off for the workspace (409 while a key is missing).</summary>
    [HttpPut("settings/enabled"), Authorize(Policy = PermissionCodes.AiSettings)]
    public async Task<IActionResult> SetEnabled(SetAiEnabledRequest request, CancellationToken ct) => Respond(await settings.SetEnabledAsync(request, ct));

    /// <summary>One minimal real request with the saved key (chat or embedding).</summary>
    [HttpPost("settings/test/{kind}"), Authorize(Policy = PermissionCodes.AiSettings)]
    public async Task<AiConnectionTestResult> Test(AiKeyKind kind, CancellationToken ct) => await settings.TestAsync(kind, ct);

    [HttpGet("usage"), Authorize(Policy = PermissionCodes.AiView)]
    public async Task<AiUsageSummaryDto> Usage([FromQuery] int? period, CancellationToken ct) => await usage.SummaryAsync(period, ct);

    [HttpGet("usage/entries"), Authorize(Policy = PermissionCodes.AiView)]
    public async Task<PagedResult<AiUsageEntryDto>> UsageEntries([FromQuery] int? period, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) => await usage.EntriesAsync(period, page, pageSize, ct);

    /// <summary>Asks a question against the workspace's knowledge: returns the passages with scores and, unless <c>retrieveOnly</c>, the validated answer with citations. Uses AI tokens.</summary>
    [HttpPost("test"), Authorize(Policy = PermissionCodes.AiTest)]
    public async Task<IActionResult> Ask(AskQuestionRequest request, CancellationToken ct) => Respond(await tester.AskAsync(request, ct));

    [HttpGet("activity"), Authorize(Policy = PermissionCodes.AiView)]
    public async Task<PagedResult<AiActivityItemDto>> Activity([FromQuery] AiActivityQuery query, CancellationToken ct) => await suggestions.ActivityAsync(query, ct);

    /// <summary>The newest AI suggestion for a conversation (204 when there is none).</summary>
    [HttpGet("conversations/{conversationId:guid}/suggestion"), Authorize(Policy = PermissionCodes.ConversationsView)]
    public async Task<IActionResult> LatestSuggestion(Guid conversationId, CancellationToken ct)
    {
        var r = await suggestions.LatestAsync(conversationId, ct);
        return r.IsFailure ? Fail(r) : r.Value is null ? NoContent() : Ok(r.Value);
    }

    [HttpGet("suggestions/{id:guid}"), Authorize(Policy = PermissionCodes.ConversationsView)]
    public async Task<IActionResult> Suggestion(Guid id, CancellationToken ct) => Respond(await suggestions.GetAsync(id, ct));

    /// <summary>Marks the suggestion as used (the reply itself is sent through the normal conversation reply, with all its consent checks).</summary>
    [HttpPost("suggestions/{id:guid}/use"), Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> Use(Guid id, CancellationToken ct) => Respond(await suggestions.MarkUsedAsync(id, ct));

    [HttpPost("suggestions/{id:guid}/dismiss"), Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> Dismiss(Guid id, CancellationToken ct) => Respond(await suggestions.DismissAsync(id, ct));
}
