using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Crm;
using LeadPilot.Shared.Results;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Web.Controllers;

[Authorize(Policy = PermissionCodes.LeadsView)]
public class LeadsController(ILeadService leads, IContactService contacts, IConsentService consent, INoteService notes, ITagService tags, IPipelineService pipelines,
    ILeadExportService export, IPermissionEvaluator permissions) : AppController
{
    private async Task<LeadAbilities> AbilitiesAsync(CancellationToken ct)
    {
        var p = await permissions.GetCurrentAsync(ct);
        return new LeadAbilities(p.Has(PermissionCodes.LeadsCreate), p.Has(PermissionCodes.LeadsUpdate), p.Has(PermissionCodes.LeadsDelete), p.Has(PermissionCodes.LeadsAssign),
            p.Has(PermissionCodes.LeadsImport), p.Has(PermissionCodes.LeadsExport), p.Has(PermissionCodes.ConsentManage));
    }

    // ---------- list / board ----------
    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] LeadQuery query, CancellationToken ct)
    {
        Crumbs(("Leads", null));
        return View(new LeadListViewModel { Query = query, Result = await leads.ListAsync(query, ct), Lookups = await leads.LookupsAsync(ct), Can = await AbilitiesAsync(ct) });
    }

    [HttpGet]
    public async Task<IActionResult> Board(Guid? pipelineId, CancellationToken ct)
    {
        var board = await leads.BoardAsync(pipelineId, 25, ct);
        if (Reject(board) is { } r) return r;
        Crumbs(("Leads", Url.Action(nameof(Index))), ("Pipeline board", null));
        return View(new BoardViewModel { Board = board.Value, Pipelines = await pipelines.ListAsync(ct), CanUpdate = (await AbilitiesAsync(ct)).Update });
    }

    // ---------- create / edit ----------
    [HttpGet, Authorize(Policy = PermissionCodes.LeadsCreate)]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        Crumbs(("Leads", Url.Action(nameof(Index))), ("New lead", null));
        return View("Form", await NewForm(new LeadFormModel(), ct));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.LeadsCreate)]
    public async Task<IActionResult> Create(LeadFormModel model, CancellationToken ct)
    {
        Crumbs(("Leads", Url.Action(nameof(Index))), ("New lead", null));
        if (!ModelState.IsValid) return View("Form", await NewForm(model, ct));
        var contactInput = string.IsNullOrWhiteSpace(model.ContactName) && string.IsNullOrWhiteSpace(model.ContactPhone) && string.IsNullOrWhiteSpace(model.ContactEmail)
            ? null : new[] { new ContactInput(model.ContactName, model.ContactRole, model.ContactPhone, model.ContactEmail, DataOrigin.Provided, true) };
        var result = await leads.CreateAsync(new CreateLeadRequest(model.Name, model.CategoryId, model.Country, model.Province, model.City, model.District, model.Area, model.Address, model.Website,
            model.Confidence, model.StageId, null, model.OwnerUserId, LeadSourceType.Manual, model.SourceName, null, contactInput, null, model.AllowDuplicate), ct);
        if (result.IsFailure)
        {
            if (Reject(result) is { } r) return r;
            AddError(result);
            model.ShowDuplicateOverride = result.Error.Code == "duplicate";
            return View("Form", await NewForm(model, ct));
        }
        Success("Lead created.");
        return RedirectToAction(nameof(Details), new { id = result.Value.Id });
    }

    [HttpGet, Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var lead = await leads.GetAsync(id, ct);
        if (Reject(lead) is { } r) return r;
        var l = lead.Value;
        Crumbs(("Leads", Url.Action(nameof(Index))), (l.Name, Url.Action(nameof(Details), new { id })), ("Edit", null));
        return View("Form", await NewForm(new LeadFormModel
        {
            Id = id, Name = l.Name, CategoryId = l.CategoryId, Country = l.Country, Province = l.Province, City = l.City, District = l.District, Area = l.Area, Address = l.Address,
            Website = l.Website, Confidence = l.Confidence, StatusId = l.StatusId, Version = l.Version
        }, ct));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> Edit(Guid id, LeadFormModel model, CancellationToken ct)
    {
        model.Id = id;
        Crumbs(("Leads", Url.Action(nameof(Index))), (model.Name, Url.Action(nameof(Details), new { id })), ("Edit", null));
        if (!ModelState.IsValid) return View("Form", await NewForm(model, ct));
        var result = await leads.UpdateAsync(id, new UpdateLeadRequest(model.Name, model.CategoryId, model.Country, model.Province, model.City, model.District, model.Area, model.Address, model.Website,
            model.Confidence, model.StatusId, model.Version), ct);
        if (result.IsFailure)
        {
            if (Reject(result) is { } r) return r;
            AddError(result);
            return View("Form", await NewForm(model, ct));
        }
        Success("Lead updated.");
        return RedirectToAction(nameof(Details), new { id });
    }

    private async Task<LeadFormModel> NewForm(LeadFormModel model, CancellationToken ct)
    {
        model.Lookups = await leads.LookupsAsync(ct);
        model.CanAssign = (await AbilitiesAsync(ct)).Assign;
        return model;
    }

    // ---------- details ----------
    [HttpGet]
    public async Task<IActionResult> Details(Guid id, string? tab, int page = 1, CancellationToken ct = default)
    {
        var lead = await leads.GetAsync(id, ct);
        if (Reject(lead) is { } r) return r;
        tab = tab?.ToLowerInvariant() is "contacts" or "activity" or "notes" or "consent" ? tab.ToLowerInvariant() : "overview";
        var vm = new LeadDetailsViewModel { Lead = lead.Value, Tab = tab, Can = await AbilitiesAsync(ct), Lookups = await leads.LookupsAsync(ct) };
        var pipeline = await pipelines.GetAsync(lead.Value.PipelineId, ct);
        vm.Stages = pipeline.IsSuccess ? pipeline.Value.Stages : [];
        var contactList = await contacts.ListAsync(id, ct);
        vm.Contacts = contactList.IsSuccess ? contactList.Value : [];
        vm.ContactForm.LeadId = id;
        switch (tab)
        {
            case "activity": vm.Activities = (await leads.ActivitiesAsync(id, page, 25, ct)).Value; break;
            case "notes": vm.Notes = (await notes.ListAsync(id, page, 25, ct)).Value; break;
            case "consent": vm.ConsentHistory = (await consent.HistoryForLeadAsync(id, page, 25, ct)).Value; break;
        }
        Crumbs(("Leads", Url.Action(nameof(Index))), (lead.Value.Name, null));
        return View(vm);
    }

    private IActionResult BackTo(Guid id, string tab) => RedirectToAction(nameof(Details), new { id, tab });

    private IActionResult Outcome(Result result, Guid id, string tab, string success)
    {
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success(success); else Failure(result.Error.Message);
        return BackTo(id, tab);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.LeadsDelete)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken ct) => Outcome(await leads.ArchiveAsync(id, ct), id, "overview", "Lead archived.");

    [HttpPost, Authorize(Policy = PermissionCodes.LeadsDelete)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken ct) => Outcome(await leads.RestoreAsync(id, ct), id, "overview", "Lead restored.");

    [HttpPost, Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> ChangeStage(Guid id, Guid stageId, string? returnTo, CancellationToken ct)
    {
        var result = await leads.ChangeStageAsync(id, stageId, ct);
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success($"Stage changed to {result.Value.StageName}."); else Failure(result.Error.Message);
        return returnTo == "board" ? RedirectToAction(nameof(Board), new { pipelineId = result.IsSuccess ? result.Value.PipelineId : (Guid?)null }) : BackTo(id, "overview");
    }

    [HttpPost, Authorize(Policy = PermissionCodes.LeadsAssign)]
    public async Task<IActionResult> Assign(Guid id, Guid? ownerUserId, CancellationToken ct)
    {
        var result = await leads.AssignAsync(id, ownerUserId, ct);
        if (Reject(result) is { } r) return r;
        if (result.IsSuccess) Success(ownerUserId is null ? "Lead unassigned." : $"Lead assigned to {result.Value.OwnerName}."); else Failure(result.Error.Message);
        return BackTo(id, "overview");
    }

    [HttpPost, Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> SetTags(Guid id, List<Guid>? tagIds, CancellationToken ct)
    {
        var result = await tags.SetLeadTagsAsync(id, new SetLeadTagsRequest(tagIds ?? []), ct);
        return Outcome(result, id, "overview", "Tags updated.");
    }

    // ---------- notes ----------
    [HttpPost, Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> AddNote(Guid id, [Bind(Prefix = "NoteForm")] NoteFormModel form, CancellationToken ct)
    {
        if (!ModelState.IsValid) { Failure("A note cannot be empty and may have at most 4000 characters."); return BackTo(id, "notes"); }
        return Outcome(await notes.AddAsync(id, new AddNoteRequest(form.Body), ct), id, "notes", "Note added.");
    }

    [HttpPost, Authorize(Policy = PermissionCodes.LeadsDelete)]
    public async Task<IActionResult> DeleteNote(Guid id, Guid noteId, CancellationToken ct) => Outcome(await notes.DeleteAsync(noteId, ct), id, "notes", "Note deleted.");

    // ---------- contacts ----------
    [HttpPost, Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> AddContact(Guid id, [Bind(Prefix = "ContactForm")] ContactFormModel form, CancellationToken ct)
    {
        var result = await contacts.AddAsync(id, new SaveContactRequest(form.Name, form.Role, form.Phone, form.Email, form.DataOrigin, form.IsPrimary), ct);
        return Outcome(result, id, "contacts", "Contact added.");
    }

    [HttpGet, Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> EditContact(Guid id, Guid contactId, CancellationToken ct)
    {
        var lead = await leads.GetAsync(id, ct);
        if (Reject(lead) is { } r) return r;
        var contact = await contacts.GetAsync(contactId, ct);
        if (Reject(contact) is { } r2) return r2;
        if (contact.Value.LeadId != id) return NotFound();
        Crumbs(("Leads", Url.Action(nameof(Index))), (lead.Value.Name, Url.Action(nameof(Details), new { id, tab = "contacts" })), ("Edit contact", null));
        var c = contact.Value;
        return View("EditContact", new ContactFormModel { Id = c.Id, LeadId = id, Name = c.Name, Role = c.Role, Phone = c.PhoneRaw ?? c.Phone, Email = c.Email, DataOrigin = c.DataOrigin, IsPrimary = c.IsPrimary });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> EditContact(Guid id, Guid contactId, ContactFormModel form, CancellationToken ct)
    {
        form.Id = contactId; form.LeadId = id;
        var current = await contacts.GetAsync(contactId, ct);
        if (Reject(current) is { } r0) return r0;
        if (current.Value.LeadId != id) return NotFound();
        var result = await contacts.UpdateAsync(contactId, new SaveContactRequest(form.Name, form.Role, form.Phone, form.Email, form.DataOrigin, form.IsPrimary), ct);
        if (result.IsFailure)
        {
            if (Reject(result) is { } r) return r;
            AddError(result);
            var lead = await leads.GetAsync(id, ct);
            Crumbs(("Leads", Url.Action(nameof(Index))), (lead.IsSuccess ? lead.Value.Name : "Lead", Url.Action(nameof(Details), new { id, tab = "contacts" })), ("Edit contact", null));
            return View("EditContact", form);
        }
        Success("Contact updated.");
        return BackTo(id, "contacts");
    }

    [HttpPost, Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> RemoveContact(Guid id, Guid contactId, CancellationToken ct)
    {
        var current = await contacts.GetAsync(contactId, ct);
        if (Reject(current) is { } r0) return r0;
        if (current.Value.LeadId != id) return NotFound();
        return Outcome(await contacts.RemoveAsync(contactId, ct), id, "contacts", "Contact removed.");
    }

    // ---------- consent ----------
    [HttpPost, Authorize(Policy = PermissionCodes.ConsentManage)]
    public async Task<IActionResult> RecordConsent(Guid id, [Bind(Prefix = "ConsentForm")] ConsentFormModel form, CancellationToken ct)
    {
        if (!ModelState.IsValid || form.ContactId is not { } contactId) { Failure("Choose a contact and say how the consent was given or withdrawn."); return BackTo(id, "consent"); }
        var current = await contacts.GetAsync(contactId, ct);
        if (Reject(current) is { } r0) return r0;
        if (current.Value.LeadId != id) return NotFound();
        var result = await consent.RecordAsync(contactId, new RecordConsentRequest(form.Channel, form.Purpose, form.Status, form.Source, form.EvidenceRef, form.Note, form.OccurredAtUtc), ct);
        return Outcome(result, id, "consent", form.Status == ConsentStatus.Granted ? "Consent recorded." : "Revocation recorded.");
    }

    // ---------- bulk ----------
    [HttpPost, Authorize(Policy = PermissionCodes.LeadsAssign)]
    public async Task<IActionResult> BulkAssign(List<Guid>? ids, Guid? ownerUserId, string? returnQuery, CancellationToken ct) =>
        BulkDone(await leads.BulkAssignAsync(new BulkAssignRequest(ids ?? [], ownerUserId), ct), "assigned", returnQuery);

    [HttpPost, Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> BulkTag(List<Guid>? ids, Guid? tagId, bool remove, string? returnQuery, CancellationToken ct)
    {
        if (tagId is null) { Failure("Choose a tag."); return ReturnToList(returnQuery); }
        return BulkDone(await leads.BulkTagAsync(new BulkTagRequest(ids ?? [], tagId.Value, remove), ct), remove ? "untagged" : "tagged", returnQuery);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.LeadsUpdate)]
    public async Task<IActionResult> BulkStage(List<Guid>? ids, Guid? stageId, string? returnQuery, CancellationToken ct)
    {
        if (stageId is null) { Failure("Choose a stage."); return ReturnToList(returnQuery); }
        return BulkDone(await leads.BulkStageAsync(new BulkStageRequest(ids ?? [], stageId.Value), ct), "moved", returnQuery);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.LeadsDelete)]
    public async Task<IActionResult> BulkArchive(List<Guid>? ids, bool restore, string? returnQuery, CancellationToken ct) =>
        BulkDone(await leads.BulkArchiveAsync(new BulkArchiveRequest(ids ?? [], restore), ct), restore ? "restored" : "archived", returnQuery);

    private IActionResult BulkDone(BulkResult r, string verb, string? returnQuery)
    {
        if (r.Requested == 0) Failure("Select at least one lead.");
        else if (r.Failed == 0) Success($"{r.Succeeded} lead(s) {verb}.");
        else Failure($"{r.Succeeded} lead(s) {verb}, {r.Failed} skipped: {string.Join("; ", r.Items.Where(i => !i.Success).Select(i => i.Error).Distinct().Take(3))}");
        return ReturnToList(returnQuery);
    }

    private IActionResult ReturnToList(string? returnQuery) =>
        Redirect(Url.Action(nameof(Index)) + (string.IsNullOrEmpty(returnQuery) || !returnQuery.StartsWith('?') || returnQuery.Contains('\n') || returnQuery.Contains('\r') ? "" : returnQuery));

    // ---------- export ----------
    [HttpGet, Authorize(Policy = PermissionCodes.LeadsExport)]
    public async Task<IActionResult> Export([FromQuery] LeadQuery query, ExportFormat format = ExportFormat.Csv, CancellationToken ct = default)
    {
        var result = await export.ExportAsync(query, format, ct);
        if (result.IsFailure)
        {
            if (Reject(result) is { } r) return r;
            Failure(result.Error.Message);
            return RedirectToAction(nameof(Index), LeadFilterQuery.ToValues(query).ToDictionary(k => k.Key, k => (object?)k.Value));
        }
        return File(result.Value.Content, result.Value.ContentType, result.Value.FileName);
    }
}
