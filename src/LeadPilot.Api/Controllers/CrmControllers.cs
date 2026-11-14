using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Crm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Api.Controllers;

/// <summary>Leads of the signed-in tenant. The tenant always comes from the access token; ids of other tenants answer 404.</summary>
[Route("api/v1/leads")]
public class LeadsController(ILeadService leads, ILeadExportService export, IScoringService scoring) : ApiControllerBase
{
    /// <summary>Search, filter, sort and page leads.</summary>
    [HttpGet, Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<PagedResult<LeadListItemDto>> List([FromQuery] LeadQuery query, CancellationToken ct) => await leads.ListAsync(query, ct);

    /// <summary>One lead with stage, owner, tags, sources and the score breakdown.</summary>
    [HttpGet("{id:guid}"), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await leads.GetAsync(id, ct));

    /// <summary>Creates a lead (optionally with contacts and tags). Answers 409 when it looks like a duplicate; resend with allowDuplicate=true to override.</summary>
    [HttpPost, Authorize(Policy = PermissionCodes.LeadsCreate)]
    public async Task<IActionResult> Create(CreateLeadRequest request, CancellationToken ct)
    {
        var r = await leads.CreateAsync(request, ct);
        return r.IsSuccess ? CreatedAtAction(nameof(Get), new { id = r.Value.Id }, r.Value) : Fail(r);
    }

    /// <summary>Updates the lead profile. Send expectedVersion to detect concurrent edits (409).</summary>
    [HttpPut("{id:guid}"), Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> Update(Guid id, UpdateLeadRequest request, CancellationToken ct) => Respond(await leads.UpdateAsync(id, request, ct));

    /// <summary>Archives a lead (soft delete; history is kept and the lead can be restored).</summary>
    [HttpDelete("{id:guid}"), Authorize(Policy = PermissionCodes.LeadsDelete)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct) => Respond(await leads.ArchiveAsync(id, ct));

    /// <summary>Restores an archived lead.</summary>
    [HttpPost("{id:guid}/restore"), Authorize(Policy = PermissionCodes.LeadsDelete)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct) => Respond(await leads.RestoreAsync(id, ct));

    /// <summary>Moves the lead to another stage of its pipeline (recorded in the timeline and the audit log).</summary>
    [HttpPut("{id:guid}/stage"), Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> ChangeStage(Guid id, ChangeStageRequest request, CancellationToken ct) => Respond(await leads.ChangeStageAsync(id, request.StageId, ct));

    /// <summary>Assigns the lead to a team member (null unassigns).</summary>
    [HttpPut("{id:guid}/owner"), Authorize(Policy = PermissionCodes.LeadsAssign)]
    public async Task<IActionResult> Assign(Guid id, AssignLeadRequest request, CancellationToken ct) => Respond(await leads.AssignAsync(id, request.OwnerUserId, ct));

    /// <summary>Activity timeline, newest first.</summary>
    [HttpGet("{id:guid}/activities"), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IActionResult> Activities(Guid id, int page = 1, int pageSize = 25, CancellationToken ct = default) => Respond(await leads.ActivitiesAsync(id, page, pageSize, ct));

    /// <summary>History of score calculations with their breakdowns.</summary>
    [HttpGet("{id:guid}/score-history"), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IActionResult> ScoreHistory(Guid id, int take = 20, CancellationToken ct = default) => Respond(await scoring.HistoryAsync(id, take, ct));

    /// <summary>Recalculates this lead's score with the active rules.</summary>
    [HttpPost("{id:guid}/rescore"), Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> Rescore(Guid id, CancellationToken ct) => Respond(await scoring.RescoreLeadAsync(id, ct));

    /// <summary>Checks a name/city/website/phone combination against existing leads (normalised name+city, website domain, phone).</summary>
    [HttpPost("duplicates/check"), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IReadOnlyList<DuplicateMatch>> CheckDuplicates(DuplicateCheckRequest request, CancellationToken ct)
    {
        var phones = (request.Phones ?? []).Select(p => Normalizer.PhoneE164(p)).Where(p => p is not null).Select(p => p!).ToList();
        return await leads.FindDuplicatesAsync(new DuplicateProbe(request.Name, request.City, request.Website, phones), ct);
    }

    /// <summary>Pipeline board: the first cards of every stage plus per-stage counts.</summary>
    [HttpGet("board"), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IActionResult> Board(Guid? pipelineId, int cardsPerColumn = 25, CancellationToken ct = default) => Respond(await leads.BoardAsync(pipelineId, cardsPerColumn, ct));

    /// <summary>Categories, statuses, tags, default pipeline and assignable team members for building forms.</summary>
    [HttpGet("lookups"), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<CrmLookupsDto> Lookups(CancellationToken ct) => await leads.LookupsAsync(ct);

    /// <summary>Assigns many leads. Every id is checked individually; the result lists each outcome.</summary>
    [HttpPost("bulk/assign"), Authorize(Policy = PermissionCodes.LeadsAssign)]
    public async Task<BulkResult> BulkAssign(BulkAssignRequest request, CancellationToken ct) => await leads.BulkAssignAsync(request, ct);

    /// <summary>Adds (or removes) a tag on many leads.</summary>
    [HttpPost("bulk/tag"), Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<BulkResult> BulkTag(BulkTagRequest request, CancellationToken ct) => await leads.BulkTagAsync(request, ct);

    /// <summary>Moves many leads to a stage.</summary>
    [HttpPost("bulk/stage"), Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<BulkResult> BulkStage(BulkStageRequest request, CancellationToken ct) => await leads.BulkStageAsync(request, ct);

    /// <summary>Archives (or restores) many leads.</summary>
    [HttpPost("bulk/archive"), Authorize(Policy = PermissionCodes.LeadsDelete)]
    public async Task<BulkResult> BulkArchive(BulkArchiveRequest request, CancellationToken ct) => await leads.BulkArchiveAsync(request, ct);

    /// <summary>Exports the leads matching the filters as CSV or XLSX (formula-injection safe, audited, row-limited).</summary>
    [HttpGet("export"), Authorize(Policy = PermissionCodes.LeadsExport)]
    public async Task<IActionResult> Export([FromQuery] LeadQuery query, ExportFormat format = ExportFormat.Csv, CancellationToken ct = default)
    {
        var r = await export.ExportAsync(query, format, ct);
        return r.IsSuccess ? File(r.Value.Content, r.Value.ContentType, r.Value.FileName) : Fail(r);
    }
}

[Route("api/v1")]
public class ContactsController(IContactService contacts, IConsentService consent, IContactEligibilityService eligibility) : ApiControllerBase
{
    /// <summary>Contacts of a lead with their current consent state per channel and purpose.</summary>
    [HttpGet("leads/{leadId:guid}/contacts"), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IActionResult> List(Guid leadId, CancellationToken ct) => Respond(await contacts.ListAsync(leadId, ct));

    /// <summary>Adds a contact. Phone numbers are normalised to E.164.</summary>
    [HttpPost("leads/{leadId:guid}/contacts"), Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> Add(Guid leadId, SaveContactRequest request, CancellationToken ct)
    {
        var r = await contacts.AddAsync(leadId, request, ct);
        return r.IsSuccess ? CreatedAtAction(nameof(Get), new { contactId = r.Value.Id }, r.Value) : Fail(r);
    }

    [HttpGet("contacts/{contactId:guid}"), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IActionResult> Get(Guid contactId, CancellationToken ct) => Respond(await contacts.GetAsync(contactId, ct));

    /// <summary>Updates a contact. Changing the phone/e-mail revokes consent given for the old value.</summary>
    [HttpPut("contacts/{contactId:guid}"), Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> Update(Guid contactId, SaveContactRequest request, CancellationToken ct) => Respond(await contacts.UpdateAsync(contactId, request, ct));

    /// <summary>Removes a contact (soft delete; consent history is kept).</summary>
    [HttpDelete("contacts/{contactId:guid}"), Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> Remove(Guid contactId, CancellationToken ct) => Respond(await contacts.RemoveAsync(contactId, ct));

    /// <summary>Consent history of one contact (append-only).</summary>
    [HttpGet("contacts/{contactId:guid}/consent"), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IActionResult> ConsentHistory(Guid contactId, int page = 1, int pageSize = 25, CancellationToken ct = default) => Respond(await consent.HistoryForContactAsync(contactId, page, pageSize, ct));

    /// <summary>Consent history of all contacts of a lead.</summary>
    [HttpGet("leads/{leadId:guid}/consent"), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IActionResult> LeadConsentHistory(Guid leadId, int page = 1, int pageSize = 25, CancellationToken ct = default) => Respond(await consent.HistoryForLeadAsync(leadId, page, pageSize, ct));

    /// <summary>Records a consent grant or revocation. Records can never be edited or deleted; record a revocation instead.</summary>
    [HttpPost("contacts/{contactId:guid}/consent"), Authorize(Policy = PermissionCodes.ConsentManage)]
    public async Task<IActionResult> RecordConsent(Guid contactId, RecordConsentRequest request, CancellationToken ct)
    {
        var r = await consent.RecordAsync(contactId, request, ct);
        return r.IsSuccess ? StatusCode(StatusCodes.Status201Created, r.Value) : Fail(r);
    }

    /// <summary>May this contact be contacted on the channel for the purpose? Consent AND not suppressed AND valid contact value; the lead score is never considered.</summary>
    [HttpGet("contacts/{contactId:guid}/eligibility"), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IActionResult> Eligibility(Guid contactId, ConsentChannel channel, ConsentPurpose purpose = ConsentPurpose.Marketing, CancellationToken ct = default)
    {
        var d = await eligibility.CheckAsync(contactId, channel, purpose, ct);
        if (d.Reasons.Contains(EligibilityReason.ContactNotFound)) return Fail(LeadPilot.Shared.Results.Result.Failure(LeadPilot.Shared.Results.Error.NotFound("Contact")));
        return Ok(new { d.IsEligible, reasons = d.Reasons.Select(r => new { code = r.ToString(), message = EligibilityDecision.Explain(r) }) });
    }
}

[Route("api/v1/audience"), Authorize(Policy = PermissionCodes.CampaignsView), Authorize(Policy = PermissionCodes.LeadsView)]
public class AudienceController(IContactEligibilityService eligibility) : ApiControllerBase
{
    /// <summary>Contacts that may be messaged on a channel for a purpose. Suppressed, unconsented, revoked, invalid, removed and archived-lead contacts are excluded.</summary>
    [HttpGet("eligible-contacts")]
    public async Task<PagedResult<EligibleContactDto>> Eligible([FromQuery] AudienceQuery query, CancellationToken ct) => await eligibility.QueryAudienceAsync(query, ct);
}

[Route("api/v1")]
public class NotesController(INoteService notes) : ApiControllerBase
{
    [HttpGet("leads/{leadId:guid}/notes"), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IActionResult> List(Guid leadId, int page = 1, int pageSize = 25, CancellationToken ct = default) => Respond(await notes.ListAsync(leadId, page, pageSize, ct));

    [HttpPost("leads/{leadId:guid}/notes"), Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> Add(Guid leadId, AddNoteRequest request, CancellationToken ct)
    {
        var r = await notes.AddAsync(leadId, request, ct);
        return r.IsSuccess ? StatusCode(StatusCodes.Status201Created, r.Value) : Fail(r);
    }

    [HttpDelete("notes/{noteId:guid}"), Authorize(Policy = PermissionCodes.LeadsDelete)]
    public async Task<IActionResult> Delete(Guid noteId, CancellationToken ct) => Respond(await notes.DeleteAsync(noteId, ct));
}

[Route("api/v1")]
public class TagsController(ITagService tags) : ApiControllerBase
{
    [HttpGet("tags"), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IReadOnlyList<TagDto>> List(CancellationToken ct) => await tags.ListAsync(ct);

    [HttpPost("tags"), Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> Create(SaveTagRequest request, CancellationToken ct)
    {
        var r = await tags.CreateAsync(request, ct);
        return r.IsSuccess ? StatusCode(StatusCodes.Status201Created, r.Value) : Fail(r);
    }

    [HttpPut("tags/{id:guid}"), Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> Rename(Guid id, SaveTagRequest request, CancellationToken ct) => Respond(await tags.RenameAsync(id, request, ct));

    /// <summary>Deletes a tag and removes it from every lead.</summary>
    [HttpDelete("tags/{id:guid}"), Authorize(Policy = PermissionCodes.LeadsDelete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => Respond(await tags.DeleteAsync(id, ct));

    /// <summary>Replaces the tags of a lead.</summary>
    [HttpPut("leads/{leadId:guid}/tags"), Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> SetLeadTags(Guid leadId, SetLeadTagsRequest request, CancellationToken ct) => Respond(await tags.SetLeadTagsAsync(leadId, request, ct));
}

[Route("api/v1/pipelines")]
public class PipelinesController(IPipelineService pipelines) : ApiControllerBase
{
    [HttpGet, Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IReadOnlyList<PipelineDto>> List(CancellationToken ct) => await pipelines.ListAsync(ct);

    [HttpGet("{id:guid}"), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await pipelines.GetAsync(id, ct));

    /// <summary>Creates a pipeline with the default stages (rename, add, reorder and remove them afterwards).</summary>
    [HttpPost, Authorize(Policy = PermissionCodes.PipelinesManage)]
    public async Task<IActionResult> Create(SavePipelineRequest request, CancellationToken ct)
    {
        var r = await pipelines.CreateAsync(request, ct);
        return r.IsSuccess ? CreatedAtAction(nameof(Get), new { id = r.Value.Id }, r.Value) : Fail(r);
    }

    [HttpPut("{id:guid}"), Authorize(Policy = PermissionCodes.PipelinesManage)]
    public async Task<IActionResult> Rename(Guid id, SavePipelineRequest request, CancellationToken ct) => Respond(await pipelines.RenameAsync(id, request, ct));

    [HttpPost("{id:guid}/default"), Authorize(Policy = PermissionCodes.PipelinesManage)]
    public async Task<IActionResult> SetDefault(Guid id, CancellationToken ct) => Respond(await pipelines.SetDefaultAsync(id, ct));

    [HttpPut("{id:guid}/active"), Authorize(Policy = PermissionCodes.PipelinesManage)]
    public async Task<IActionResult> SetActive(Guid id, SetActiveRequest request, CancellationToken ct) => Respond(await pipelines.SetActiveAsync(id, request.Active, ct));

    [HttpPost("{id:guid}/stages"), Authorize(Policy = PermissionCodes.PipelinesManage)]
    public async Task<IActionResult> AddStage(Guid id, SaveStageRequest request, CancellationToken ct) => Respond(await pipelines.AddStageAsync(id, request, ct));

    [HttpPut("{id:guid}/stages/{stageId:guid}"), Authorize(Policy = PermissionCodes.PipelinesManage)]
    public async Task<IActionResult> UpdateStage(Guid id, Guid stageId, SaveStageRequest request, CancellationToken ct) => Respond(await pipelines.UpdateStageAsync(id, stageId, request, ct));

    /// <summary>Moves a stage up (negative delta) or down (positive) in the order.</summary>
    [HttpPost("{id:guid}/stages/{stageId:guid}/move"), Authorize(Policy = PermissionCodes.PipelinesManage)]
    public async Task<IActionResult> MoveStage(Guid id, Guid stageId, MoveStageRequest request, CancellationToken ct) => Respond(await pipelines.MoveStageAsync(id, stageId, request.Delta, ct));

    /// <summary>Deletes an empty stage (409 while leads or opportunities are in it).</summary>
    [HttpDelete("{id:guid}/stages/{stageId:guid}"), Authorize(Policy = PermissionCodes.PipelinesManage)]
    public async Task<IActionResult> DeleteStage(Guid id, Guid stageId, CancellationToken ct) => Respond(await pipelines.DeleteStageAsync(id, stageId, ct));
}

public sealed record SetActiveRequest(bool Active) : TenantScopedRequest;

[Route("api/v1")]
public class CatalogController(ILeadCatalogService catalog) : ApiControllerBase
{
    [HttpGet("categories"), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IReadOnlyList<CategoryDto>> Categories(CancellationToken ct) => await catalog.ListCategoriesAsync(ct);

    [HttpPost("categories"), Authorize(Policy = PermissionCodes.PipelinesManage)]
    public async Task<IActionResult> CreateCategory(SaveCategoryRequest request, CancellationToken ct)
    {
        var r = await catalog.CreateCategoryAsync(request, ct);
        return r.IsSuccess ? StatusCode(StatusCodes.Status201Created, r.Value) : Fail(r);
    }

    [HttpPut("categories/{id:guid}"), Authorize(Policy = PermissionCodes.PipelinesManage)]
    public async Task<IActionResult> RenameCategory(Guid id, SaveCategoryRequest request, CancellationToken ct) => Respond(await catalog.RenameCategoryAsync(id, request, ct));

    [HttpPut("categories/{id:guid}/active"), Authorize(Policy = PermissionCodes.PipelinesManage)]
    public async Task<IActionResult> SetCategoryActive(Guid id, SetActiveRequest request, CancellationToken ct) => Respond(await catalog.SetCategoryActiveAsync(id, request.Active, ct));

    [HttpGet("lead-statuses"), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IReadOnlyList<StatusDto>> Statuses(CancellationToken ct) => await catalog.ListStatusesAsync(ct);

    [HttpPost("lead-statuses"), Authorize(Policy = PermissionCodes.PipelinesManage)]
    public async Task<IActionResult> CreateStatus(SaveStatusRequest request, CancellationToken ct)
    {
        var r = await catalog.CreateStatusAsync(request, ct);
        return r.IsSuccess ? StatusCode(StatusCodes.Status201Created, r.Value) : Fail(r);
    }

    [HttpPut("lead-statuses/{id:guid}"), Authorize(Policy = PermissionCodes.PipelinesManage)]
    public async Task<IActionResult> UpdateStatus(Guid id, SaveStatusRequest request, CancellationToken ct) => Respond(await catalog.UpdateStatusAsync(id, request, ct));

    [HttpPut("lead-statuses/{id:guid}/active"), Authorize(Policy = PermissionCodes.PipelinesManage)]
    public async Task<IActionResult> SetStatusActive(Guid id, SetActiveRequest request, CancellationToken ct) => Respond(await catalog.SetStatusActiveAsync(id, request.Active, ct));
}

[Route("api/v1/scoring"), Authorize(Policy = PermissionCodes.ScoringManage)]
public class ScoringController(IScoringService scoring) : ApiControllerBase
{
    /// <summary>The active rule set (rules are tenant data and versioned).</summary>
    [HttpGet("rules")]
    public async Task<ScoringRuleSetDto> Active(CancellationToken ct) => await scoring.GetActiveAsync(ct);

    [HttpGet("rules/versions")]
    public async Task<IReadOnlyList<ScoringRuleSetDto>> Versions(CancellationToken ct) => await scoring.ListVersionsAsync(ct);

    /// <summary>Publishes a new version with exactly these rules. Existing scores change only after a recalculation.</summary>
    [HttpPost("rules")]
    public async Task<IActionResult> Publish(PublishScoringRulesRequest request, CancellationToken ct)
    {
        var r = await scoring.PublishAsync(request, ct);
        return r.IsSuccess ? StatusCode(StatusCodes.Status201Created, r.Value) : Fail(r);
    }

    /// <summary>Recalculates the score of every lead with the active rules.</summary>
    [HttpPost("rescore")]
    public async Task<RescoreResult> Rescore(CancellationToken ct) => await scoring.RescoreAllAsync(ct);
}

[Route("api/v1/imports"), Authorize(Policy = PermissionCodes.LeadsImport)]
public class ImportsController(ILeadImportService imports) : ApiControllerBase
{
    [HttpGet]
    public async Task<PagedResult<ImportJobDto>> List(int page = 1, int pageSize = 25, CancellationToken ct = default) => await imports.ListAsync(page, pageSize, ct);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await imports.GetAsync(id, ct));

    /// <summary>Uploads a .csv or .xlsx file (size, row and column limits apply) and returns the detected columns. Creates no leads yet.</summary>
    [HttpPost, Consumes("multipart/form-data"), RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> Upload(IFormFile file, CancellationToken ct)
    {
        if (file is null) return Fail(LeadPilot.Shared.Results.Result.Failure(LeadPilot.Shared.Results.Error.Validation("Attach a file in the 'file' form field.")));
        await using var stream = file.OpenReadStream();
        var r = await imports.UploadAsync(new UploadedFile(file.FileName, file.Length, stream), ct);
        return r.IsSuccess ? CreatedAtAction(nameof(Get), new { id = r.Value.Job.Id }, r.Value) : Fail(r);
    }

    [HttpGet("{id:guid}/preview")]
    public async Task<IActionResult> Preview(Guid id, CancellationToken ct) => Respond(await imports.PreviewAsync(id, ct));

    /// <summary>Runs the import with a column mapping and a duplicate policy (Skip, Merge or Review).</summary>
    [HttpPost("{id:guid}/run")]
    public async Task<IActionResult> Run(Guid id, RunImportRequest request, CancellationToken ct) => Respond(await imports.RunAsync(id, request, ct));

    /// <summary>Rows that were not imported: validation errors and duplicates.</summary>
    [HttpGet("{id:guid}/errors")]
    public async Task<IActionResult> Errors(Guid id, ImportErrorKind? kind, int page = 1, int pageSize = 50, CancellationToken ct = default) => Respond(await imports.ErrorsAsync(id, kind, page, pageSize, ct));

    /// <summary>The error report as CSV (formula-injection safe).</summary>
    [HttpGet("{id:guid}/errors/report")]
    public async Task<IActionResult> ErrorReport(Guid id, CancellationToken ct)
    {
        var r = await imports.ErrorReportAsync(id, ct);
        return r.IsSuccess ? File(r.Value.Content, r.Value.ContentType, r.Value.FileName) : Fail(r);
    }

    /// <summary>Decides a duplicate that was held for review: merge it into the existing lead, or skip it.</summary>
    [HttpPost("{id:guid}/duplicates/{errorId:guid}")]
    public async Task<IActionResult> ResolveDuplicate(Guid id, Guid errorId, ResolveDuplicateRequest request, CancellationToken ct) => Respond(await imports.ResolveDuplicateAsync(id, errorId, request, ct));
}

[Route("api/v1/suppression"), Authorize(Policy = PermissionCodes.SuppressionManage)]
public class SuppressionController(ISuppressionService suppression) : ApiControllerBase
{
    [HttpGet]
    public async Task<PagedResult<SuppressionDto>> List([FromQuery] SuppressionQuery query, CancellationToken ct) => await suppression.ListAsync(query, ct);

    /// <summary>Adds a phone number or e-mail address that must never be contacted (unique per channel and value).</summary>
    [HttpPost]
    public async Task<IActionResult> Add(AddSuppressionRequest request, CancellationToken ct)
    {
        var r = await suppression.AddAsync(request, ct);
        return r.IsSuccess ? StatusCode(StatusCodes.Status201Created, r.Value) : Fail(r);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct) => Respond(await suppression.RemoveAsync(id, ct));
}

[Route("api/v1")]
public class OpportunitiesController(IOpportunityService opportunities) : ApiControllerBase
{
    [HttpGet("leads/{leadId:guid}/opportunities"), Authorize(Policy = PermissionCodes.LeadsView)]
    public async Task<IActionResult> List(Guid leadId, CancellationToken ct) => Respond(await opportunities.ListForLeadAsync(leadId, ct));

    [HttpPost("leads/{leadId:guid}/opportunities"), Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> Create(Guid leadId, SaveOpportunityRequest request, CancellationToken ct)
    {
        var r = await opportunities.CreateAsync(leadId, request, ct);
        return r.IsSuccess ? StatusCode(StatusCodes.Status201Created, r.Value) : Fail(r);
    }

    [HttpPut("opportunities/{id:guid}"), Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> Update(Guid id, SaveOpportunityRequest request, CancellationToken ct) => Respond(await opportunities.UpdateAsync(id, request, ct));

    [HttpPut("opportunities/{id:guid}/stage"), Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> ChangeStage(Guid id, OpportunityStageRequest request, CancellationToken ct) => Respond(await opportunities.ChangeStageAsync(id, request.StageId, ct));
}
