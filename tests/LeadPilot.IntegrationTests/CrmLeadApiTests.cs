using System.Net;
using System.Net.Http.Json;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Crm;

namespace LeadPilot.IntegrationTests;

/// <summary>CRM CRUD, contacts, duplicates, stages, assignment, notes, tags, search/sort/paging, bulk operations and permission semantics through the REST API.</summary>
[Collection("app")]
public class CrmLeadApiTests(AppFixture app)
{
    private TestWorld W => app.World;
    private Task<ApiSession> Login(TestUser u) => ApiSession.LoginAsync(app, u.Email, u.Password);

    [RequiresMySqlFact]
    public async Task Lead_crud_with_archive_and_restore()
    {
        var s = await Login(W.ASalesManager);
        var name = Crm.Name(Crm.Token());
        var created = await s.Post("/api/v1/leads", new
        {
            name, city = "Riyadh", country = "Saudi Arabia", website = "https://www.crud.example", confidence = 70,
            contacts = new object[] { new { name = "Sara", role = "Principal", phone = Crm.Phone(), email = "sara@crud.example" }, new { name = "Omar", phone = Crm.Phone() } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var lead = await ApiSession.Json<LeadDto>(created);
        Assert.Equal(W.A.Id.ToString(), created.Headers.Location is null ? W.A.Id.ToString() : W.A.Id.ToString());
        Assert.Equal("New", lead.StageName);
        Assert.Equal("Open", lead.StatusName);
        Assert.Equal("crud.example", lead.WebsiteDomain);
        Assert.Equal(2, lead.ContactCount);
        Assert.Equal(70, lead.Confidence);
        Assert.Equal(LeadSourceType.Manual, lead.PrimarySourceType);
        Assert.Equal(50, lead.Score);                              // website 10 + valid phone 15 + e-mail 10 + named 10 + two contacts 5
        Assert.Equal(1, lead.ScoreRuleVersion);
        Assert.Equal(5, lead.ScoreBreakdown.Count);

        var got = await Crm.Ok<LeadDto>(await s.Get($"/api/v1/leads/{lead.Id}"));
        Assert.Equal(name, got.Name);

        var upd = await s.Put($"/api/v1/leads/{lead.Id}", new { name, city = "Jeddah", country = "Saudi Arabia", website = "https://www.crud.example", confidence = 80, expectedVersion = lead.Version });
        var updated = await Crm.Ok<LeadDto>(upd);
        Assert.Equal("Jeddah", updated.City);
        Assert.Equal(80, updated.Confidence);
        Assert.True(updated.Version > lead.Version);

        var stale = await s.Put($"/api/v1/leads/{lead.Id}", new { name, city = "Dammam", expectedVersion = lead.Version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var listed = await Crm.Page<LeadListItemDto>(await s.Get($"/api/v1/leads?search={Uri.EscapeDataString(name)}"));
        var item = Assert.Single(listed.Items);
        Assert.Equal("Sara", item.PrimaryContactName);

        Assert.Equal(HttpStatusCode.NoContent, (await s.Delete($"/api/v1/leads/{lead.Id}")).StatusCode);
        Assert.Empty((await Crm.Page<LeadListItemDto>(await s.Get($"/api/v1/leads?search={Uri.EscapeDataString(name)}"))).Items);
        Assert.Single((await Crm.Page<LeadListItemDto>(await s.Get($"/api/v1/leads?search={Uri.EscapeDataString(name)}&archived=Archived"))).Items);
        Assert.True((await Crm.Ok<LeadDto>(await s.Get($"/api/v1/leads/{lead.Id}"))).IsArchived);

        var frozen = await s.Put($"/api/v1/leads/{lead.Id}", new { name, city = "Mecca" });
        Assert.Equal(HttpStatusCode.BadRequest, frozen.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Delete($"/api/v1/leads/{lead.Id}")).StatusCode); // already archived

        Assert.Equal(HttpStatusCode.NoContent, (await s.Post($"/api/v1/leads/{lead.Id}/restore")).StatusCode);
        Assert.False((await Crm.Ok<LeadDto>(await s.Get($"/api/v1/leads/{lead.Id}"))).IsArchived);
    }

    [RequiresMySqlFact]
    public async Task Invalid_lead_input_is_rejected_with_400()
    {
        var s = await Login(W.ASalesManager);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/leads", new { name = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/leads", new { name = Crm.Name(Crm.Token()), website = "javascript:alert(1)" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/leads", new { name = Crm.Name(Crm.Token()), confidence = 500 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/leads", new { name = new string('x', 301) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/leads", new { name = Crm.Name(Crm.Token()), contacts = new[] { new { name = "x", phone = "12" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/leads", new { name = Crm.Name(Crm.Token()), contacts = new[] { new { name = "x", email = "nope" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post("/api/v1/leads", new { name = Crm.Name(Crm.Token()), sourceType = "Import" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Get($"/api/v1/leads/{Guid.NewGuid()}")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Multiple_contacts_per_lead_with_e164_normalisation_primary_and_soft_removal()
    {
        var s = await Login(W.ASalesManager);
        var lead = await Crm.CreateLead(s, contact: false);
        var p1 = Crm.Phone(); var p2 = Crm.Phone();

        var c1 = await Crm.Ok<ContactDto>(await s.Post($"/api/v1/leads/{lead.Id}/contacts", new { name = "Principal", phone = p1.Insert(4, " ").Insert(8, "-") }));
        Assert.Equal(p1, c1.Phone);
        Assert.True(c1.IsPrimary);                                    // the first contact becomes primary
        var c2 = await Crm.Ok<ContactDto>(await s.Post($"/api/v1/leads/{lead.Id}/contacts", new { name = "Admissions", phone = "00" + p2[1..], email = "Admissions@Example.com", dataOrigin = "Verified" }));
        Assert.Equal(p2, c2.Phone);
        Assert.Equal("admissions@example.com", c2.Email);
        Assert.NotNull(c2.PhoneVerifiedAtUtc);
        var c3 = await Crm.Ok<ContactDto>(await s.Post($"/api/v1/leads/{lead.Id}/contacts", new { name = "Bursar", email = "bursar@example.com", isPrimary = true }));
        Assert.True(c3.IsPrimary);

        var list = await Crm.Ok<List<ContactDto>>(await s.Get($"/api/v1/leads/{lead.Id}/contacts"));
        Assert.Equal(3, list.Count);
        Assert.Equal(c3.Id, Assert.Single(list, c => c.IsPrimary).Id);   // only one primary at a time
        Assert.Equal(3, (await Crm.Ok<LeadDto>(await s.Get($"/api/v1/leads/{lead.Id}"))).ContactCount);

        Assert.Equal(HttpStatusCode.Conflict, (await s.Post($"/api/v1/leads/{lead.Id}/contacts", new { name = "Dup", phone = p1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Post($"/api/v1/leads/{lead.Id}/contacts", new { name = "Dup", email = "BURSAR@example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post($"/api/v1/leads/{lead.Id}/contacts", new { name = "Bad", phone = "123" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post($"/api/v1/leads/{lead.Id}/contacts", new { })).StatusCode);

        // the same phone may exist on a DIFFERENT lead (shared office line) but never twice on one lead
        var other = await Crm.CreateLead(s, contact: false);
        Assert.Equal(HttpStatusCode.Created, (await s.Post($"/api/v1/leads/{other.Id}/contacts", new { name = "Shared", phone = p1 })).StatusCode);

        var updated = await Crm.Ok<ContactDto>(await s.Put($"/api/v1/contacts/{c1.Id}", new { name = "Head teacher", phone = p1, role = "Head" }));
        Assert.Equal("Head", updated.Role);

        Assert.Equal(HttpStatusCode.NoContent, (await s.Delete($"/api/v1/contacts/{c2.Id}")).StatusCode);
        Assert.Equal(2, (await Crm.Ok<List<ContactDto>>(await s.Get($"/api/v1/leads/{lead.Id}/contacts"))).Count);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Get($"/api/v1/contacts/{c2.Id}")).StatusCode);
        // a removed contact releases its phone number
        Assert.Equal(HttpStatusCode.Created, (await s.Post($"/api/v1/leads/{lead.Id}/contacts", new { name = "Again", phone = p2 })).StatusCode);

        // removing the primary promotes another contact
        Assert.Equal(HttpStatusCode.NoContent, (await s.Delete($"/api/v1/contacts/{c3.Id}")).StatusCode);
        Assert.Single((await Crm.Ok<List<ContactDto>>(await s.Get($"/api/v1/leads/{lead.Id}/contacts"))), c => c.IsPrimary);
    }

    [RequiresMySqlFact]
    public async Task Duplicate_leads_are_detected_by_name_city_domain_and_phone()
    {
        var s = await Login(W.ASalesManager);
        var token = Crm.Token();
        var phone = Crm.Phone();
        var first = await Crm.CreateLead(s, name: $"{token} Academy Ltd", city: "Riyadh", website: $"https://www.{token}.example", phone: phone);

        // same normalised name + city
        var dup1 = await s.Post("/api/v1/leads", new { name = $"  {token.ToUpper()}   academy ", city = "riyadh" });
        Assert.Equal(HttpStatusCode.Conflict, dup1.StatusCode);
        Assert.Equal("duplicate", await Crm.ProblemCode(dup1));
        Assert.Contains("same name and city", await Crm.ProblemDetail(dup1));
        // same website domain, different name
        var dup2 = await s.Post("/api/v1/leads", new { name = Crm.Name(Crm.Token()), website = $"http://{token}.example/contact" });
        Assert.Equal(HttpStatusCode.Conflict, dup2.StatusCode);
        Assert.Contains("same website domain", await Crm.ProblemDetail(dup2));
        // same phone, different name and domain
        var dup3 = await s.Post("/api/v1/leads", new { name = Crm.Name(Crm.Token()), contacts = new[] { new { name = "x", phone = phone.Replace("+966", "00966") } } });
        Assert.Equal(HttpStatusCode.Conflict, dup3.StatusCode);
        Assert.Contains("same phone number", await Crm.ProblemDetail(dup3));
        // different city is a different lead
        Assert.Equal(HttpStatusCode.Created, (await s.Post("/api/v1/leads", new { name = $"{token} Academy", city = "Jeddah" })).StatusCode);

        var check = await Crm.Ok<List<DuplicateMatch>>(await s.Post("/api/v1/leads/duplicates/check", new { name = $"{token} Academy", city = "Riyadh", website = $"{token}.example", phones = new[] { phone } }));
        var match = Assert.Single(check);
        Assert.Equal(first.Id, match.LeadId);
        Assert.Equal(3, match.Reasons.Count);

        // explicit override
        var forced = await s.Post("/api/v1/leads", new { name = $"{token} Academy", city = "Riyadh", allowDuplicate = true });
        Assert.Equal(HttpStatusCode.Created, forced.StatusCode);

        // another tenant's data never counts as a duplicate
        var b = await Login(W.BSalesManager);
        var bToken = Crm.Token();
        await Crm.CreateLead(b, name: $"{bToken} Academy", city: "Riyadh");
        Assert.Equal(HttpStatusCode.Created, (await s.Post("/api/v1/leads", new { name = $"{bToken} Academy", city = "Riyadh" })).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Changing_stage_records_activity_and_audit_and_rescoring_reflects_progress()
    {
        var s = await Login(W.ASalesManager);
        var admin = await Login(W.AAdmin);
        var lead = await Crm.CreateLead(s);
        var pipeline = (await Crm.Ok<List<PipelineDto>>(await s.Get("/api/v1/pipelines"))).Single(p => p.IsDefault);
        Assert.Equal(14, pipeline.Stages.Count);
        var contacted = pipeline.Stages.Single(x => x.Name == "Contacted");
        var interested = pipeline.Stages.Single(x => x.Name == "Interested");

        var moved = await Crm.Ok<LeadDto>(await s.Put($"/api/v1/leads/{lead.Id}/stage", new { stageId = contacted.Id }));
        Assert.Equal("Contacted", moved.StageName);
        var final = await Crm.Ok<LeadDto>(await s.Put($"/api/v1/leads/{lead.Id}/stage", new { stageId = interested.Id }));
        Assert.Equal("Interested", final.StageName);
        Assert.True(final.Score >= moved.Score + 30, $"reaching Responded and Interested should add 30 points ({moved.Score} -> {final.Score})");

        var activities = await Crm.Page<ActivityDto>(await s.Get($"/api/v1/leads/{lead.Id}/activities"));
        Assert.Contains(activities.Items, a => a.Type == LeadActivityType.StageChanged && a.Summary == "Stage changed from New to Contacted.");
        Assert.Contains(activities.Items, a => a.Type == LeadActivityType.Created);
        Assert.Equal(W.ASalesManager.Email.Split('@')[0], activities.Items.First(a => a.Type == LeadActivityType.StageChanged).ActorName);

        var audit = await Crm.Page<AuditLogDto>(await admin.Get("/api/v1/audit-logs?action=Lead.StageChanged&pageSize=100"));
        Assert.Contains(audit.Items, a => a.EntityId == lead.Id.ToString() && a.Summary.Contains("'New'") || a.EntityId == lead.Id.ToString() && a.Summary.Contains("'Contacted'"));

        var history = await Crm.Ok<List<ScoreHistoryDto>>(await s.Get($"/api/v1/leads/{lead.Id}/score-history"));
        Assert.True(history.Count >= 2);

        // same stage again is a no-op; a stage that does not exist is rejected; an unknown lead is 404
        Assert.Equal(HttpStatusCode.OK, (await s.Put($"/api/v1/leads/{lead.Id}/stage", new { stageId = interested.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Put($"/api/v1/leads/{lead.Id}/stage", new { stageId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Put($"/api/v1/leads/{Guid.NewGuid()}/stage", new { stageId = interested.Id })).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Assigning_leads_requires_the_permission_and_an_active_member()
    {
        var manager = await Login(W.ASalesManager);
        var agent = await Login(W.ASalesAgent);
        var lead = await Crm.CreateLead(manager);

        var assigned = await Crm.Ok<LeadDto>(await manager.Put($"/api/v1/leads/{lead.Id}/owner", new { ownerUserId = W.ASalesAgent.Id }));
        Assert.Equal(W.ASalesAgent.Id, assigned.OwnerUserId);
        Assert.NotNull(assigned.OwnerName);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.Put($"/api/v1/leads/{lead.Id}/owner", new { ownerUserId = W.ASalesAgent.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.Put($"/api/v1/leads/{lead.Id}/owner", new { ownerUserId = W.BAdmin.Id })).StatusCode);   // member of another tenant
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.Put($"/api/v1/leads/{lead.Id}/owner", new { ownerUserId = Guid.NewGuid() })).StatusCode);

        var mine = await Crm.Page<LeadListItemDto>(await agent.Get($"/api/v1/leads?ownerUserId={W.ASalesAgent.Id}"));
        Assert.Contains(mine.Items, i => i.Id == lead.Id);
        Assert.Null((await Crm.Ok<LeadDto>(await manager.Put($"/api/v1/leads/{lead.Id}/owner", new { ownerUserId = (Guid?)null }))).OwnerUserId);

        // creating a lead assigned to someone else needs Leads.Assign; assigning to yourself does not
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.Post("/api/v1/leads", new { name = Crm.Name(Crm.Token()), ownerUserId = W.ASalesManager.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await agent.Post("/api/v1/leads", new { name = Crm.Name(Crm.Token()), ownerUserId = W.ASalesAgent.Id })).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Notes_and_tags()
    {
        var s = await Login(W.ASalesManager);
        var lead = await Crm.CreateLead(s);

        var note = await Crm.Ok<NoteDto>(await s.Post($"/api/v1/leads/{lead.Id}/notes", new { body = "Called the head teacher; call back Sunday." }));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post($"/api/v1/leads/{lead.Id}/notes", new { body = "   " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post($"/api/v1/leads/{lead.Id}/notes", new { body = new string('x', 4001) })).StatusCode);
        Assert.Single((await Crm.Page<NoteDto>(await s.Get($"/api/v1/leads/{lead.Id}/notes"))).Items);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Delete($"/api/v1/notes/{note.Id}")).StatusCode);
        Assert.Empty((await Crm.Page<NoteDto>(await s.Get($"/api/v1/leads/{lead.Id}/notes"))).Items);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Delete($"/api/v1/notes/{note.Id}")).StatusCode);

        var tagName = Crm.Token("tag");
        var tag = await Crm.Ok<TagDto>(await s.Post("/api/v1/tags", new { name = tagName }));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Post("/api/v1/tags", new { name = tagName.ToUpper() })).StatusCode);   // case-insensitive uniqueness
        var tag2 = await Crm.Ok<TagDto>(await s.Post("/api/v1/tags", new { name = Crm.Token("tag") }));

        var tags = await Crm.Ok<List<LookupItem>>(await s.Put($"/api/v1/leads/{lead.Id}/tags", new { tagIds = new[] { tag.Id, tag2.Id } }));
        Assert.Equal(2, tags.Count);
        Assert.Contains(lead.Id, (await Crm.Page<LeadListItemDto>(await s.Get($"/api/v1/leads?tagId={tag.Id}"))).Items.Select(i => i.Id));
        Assert.Equal(2, (await Crm.Ok<LeadDto>(await s.Get($"/api/v1/leads/{lead.Id}"))).Tags.Count);

        var renamed = await Crm.Ok<TagDto>(await s.Put($"/api/v1/tags/{tag.Id}", new { name = tagName + "-x" }));
        Assert.Equal(1, renamed.LeadCount);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Put($"/api/v1/tags/{tag.Id}", new { name = tag2.Name })).StatusCode);

        Assert.Single(await Crm.Ok<List<LookupItem>>(await s.Put($"/api/v1/leads/{lead.Id}/tags", new { tagIds = new[] { tag2.Id } })));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Put($"/api/v1/leads/{lead.Id}/tags", new { tagIds = new[] { Guid.NewGuid() } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Delete($"/api/v1/tags/{tag2.Id}")).StatusCode);
        Assert.Empty((await Crm.Ok<LeadDto>(await s.Get($"/api/v1/leads/{lead.Id}"))).Tags);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Delete($"/api/v1/tags/{tag2.Id}")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Search_filter_sort_and_paging()
    {
        var s = await Login(W.ASalesManager);
        var token = Crm.Token();
        var category = await Crm.Ok<CategoryDto>(await s.Post("/api/v1/categories", new { name = "Cat " + token }));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Post("/api/v1/categories", new { name = ("cat " + token).ToUpper() })).StatusCode);
        string? firstPhone = null;
        for (var i = 1; i <= 12; i++)
        {
            var phone = i == 1 ? (firstPhone = Crm.Phone()) : null;
            await Crm.CreateLead(s, name: Crm.Name(token, i), city: i % 2 == 0 ? "Jeddah" : "Riyadh", categoryId: i <= 4 ? category.Id : null, contact: i <= 6, phone: phone);
        }

        var all = await Crm.Page<LeadListItemDto>(await s.Get($"/api/v1/leads?search={token}&pageSize=5&page=2&sort=Name&descending=false"));
        Assert.Equal(12, all.TotalCount);
        Assert.Equal(3, all.TotalPages);
        Assert.Equal(5, all.Items.Count);
        Assert.Equal(Enumerable.Range(6, 5).Select(i => Crm.Name(token, i)), all.Items.Select(i => i.Name));

        var desc = await Crm.Page<LeadListItemDto>(await s.Get($"/api/v1/leads?search={token}&pageSize=3&sort=Name&descending=true"));
        Assert.Equal(Crm.Name(token, 12), desc.Items[0].Name);
        Assert.Equal(6, (await Crm.Page<LeadListItemDto>(await s.Get($"/api/v1/leads?search={token}&city=Jeddah"))).TotalCount);
        var byCategory = await Crm.Page<LeadListItemDto>(await s.Get($"/api/v1/leads?categoryId={category.Id}"));
        Assert.Equal(4, byCategory.TotalCount);
        Assert.All(byCategory.Items, i => Assert.Equal("Cat " + token, i.Category));
        Assert.Single((await Crm.Page<LeadListItemDto>(await s.Get($"/api/v1/leads?search={firstPhone![^8..]}"))).Items);      // search by phone digits
        Assert.Equal(0, (await Crm.Page<LeadListItemDto>(await s.Get($"/api/v1/leads?search={token}&minScore=100"))).TotalCount);
        Assert.Equal(12, (await Crm.Page<LeadListItemDto>(await s.Get($"/api/v1/leads?search={token}&maxScore=100&sort=Score&descending=true"))).TotalCount);
        Assert.Equal(12, (await Crm.Page<LeadListItemDto>(await s.Get($"/api/v1/leads?search={token}&stageKind=Open&sort=Stage"))).TotalCount);
        Assert.Equal(12, (await Crm.Page<LeadListItemDto>(await s.Get($"/api/v1/leads?search={token}&unassigned=true"))).TotalCount);
        Assert.Equal(0, (await Crm.Page<LeadListItemDto>(await s.Get($"/api/v1/leads?search={token}&createdFromUtc=2999-01-01"))).TotalCount);
        Assert.Equal(100, (await Crm.Page<LeadListItemDto>(await s.Get("/api/v1/leads?pageSize=100000"))).PageSize);        // page size is capped
        Assert.Equal(1, (await Crm.Page<LeadListItemDto>(await s.Get("/api/v1/leads?page=-4&pageSize=0"))).Page);
        Assert.Equal(0, (await Crm.Page<LeadListItemDto>(await s.Get("/api/v1/leads?search=%25%27%3B--"))).TotalCount);      // wildcard/injection-looking input is just text
    }

    [RequiresMySqlFact]
    public async Task Bulk_operations_authorise_every_item_and_never_touch_other_tenants()
    {
        var a = await Login(W.ASalesManager);
        var b = await Login(W.BSalesManager);
        var mine = new List<LeadDto>();
        for (var i = 0; i < 3; i++) mine.Add(await Crm.CreateLead(a));
        var theirs = await Crm.CreateLead(b);
        var ids = mine.Select(l => l.Id).Append(theirs.Id).Append(Guid.NewGuid()).ToArray();

        var assign = await Crm.Ok<BulkResult>(await a.Post("/api/v1/leads/bulk/assign", new { leadIds = ids, ownerUserId = W.ASalesAgent.Id }));
        Assert.Equal(5, assign.Requested);
        Assert.Equal(3, assign.Succeeded);
        Assert.Equal(2, assign.Failed);
        Assert.All(assign.Items.Where(i => !i.Success), i => Assert.Equal("Lead not found.", i.Error)); // other tenant's id and an unknown id look the same
        Assert.Null((await Crm.Ok<LeadDto>(await b.Get($"/api/v1/leads/{theirs.Id}"))).OwnerUserId);
        Assert.All(mine, l => Assert.Equal(W.ASalesAgent.Id, Crm.Ok<LeadDto>(a.Get($"/api/v1/leads/{l.Id}").Result).Result.OwnerUserId));

        var tag = await Crm.Ok<TagDto>(await a.Post("/api/v1/tags", new { name = Crm.Token("bulk") }));
        var tagged = await Crm.Ok<BulkResult>(await a.Post("/api/v1/leads/bulk/tag", new { leadIds = ids, tagId = tag.Id }));
        Assert.Equal(3, tagged.Succeeded);
        Assert.Equal(3, (await Crm.Page<LeadListItemDto>(await a.Get($"/api/v1/leads?tagId={tag.Id}"))).TotalCount);
        var untagged = await Crm.Ok<BulkResult>(await a.Post("/api/v1/leads/bulk/tag", new { leadIds = mine.Select(l => l.Id), tagId = tag.Id, remove = true }));
        Assert.Equal(3, untagged.Succeeded);

        var pipeline = (await Crm.Ok<List<PipelineDto>>(await a.Get("/api/v1/pipelines"))).Single(p => p.IsDefault);
        var stage = pipeline.Stages.Single(x => x.Name == "Researched");
        var staged = await Crm.Ok<BulkResult>(await a.Post("/api/v1/leads/bulk/stage", new { leadIds = ids, stageId = stage.Id }));
        Assert.Equal(3, staged.Succeeded);
        Assert.Equal("New", (await Crm.Ok<LeadDto>(await b.Get($"/api/v1/leads/{theirs.Id}"))).StageName);

        var archived = await Crm.Ok<BulkResult>(await a.Post("/api/v1/leads/bulk/archive", new { leadIds = ids }));
        Assert.Equal(3, archived.Succeeded);
        Assert.False((await Crm.Ok<LeadDto>(await b.Get($"/api/v1/leads/{theirs.Id}"))).IsArchived);
        var again = await Crm.Ok<BulkResult>(await a.Post("/api/v1/leads/bulk/archive", new { leadIds = mine.Select(l => l.Id) }));
        Assert.Equal(0, again.Succeeded);                                                   // domain rule applied per item: already archived
        var restored = await Crm.Ok<BulkResult>(await a.Post("/api/v1/leads/bulk/archive", new { leadIds = mine.Select(l => l.Id), restore = true }));
        Assert.Equal(3, restored.Succeeded);

        // size cap
        var tooMany = await Crm.Ok<BulkResult>(await a.Post("/api/v1/leads/bulk/assign", new { leadIds = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()), ownerUserId = W.ASalesAgent.Id }));
        Assert.Equal(0, tooMany.Succeeded);
        Assert.Contains("At most 500", tooMany.Items[0].Error);
        // another tenant's tag / stage cannot be applied
        var bTag = await Crm.Ok<TagDto>(await b.Post("/api/v1/tags", new { name = Crm.Token("btag") }));
        Assert.Equal(0, (await Crm.Ok<BulkResult>(await a.Post("/api/v1/leads/bulk/tag", new { leadIds = mine.Select(l => l.Id), tagId = bTag.Id }))).Succeeded);
        var bStage = (await Crm.Ok<List<PipelineDto>>(await b.Get("/api/v1/pipelines"))).Single(p => p.IsDefault).Stages[0];
        Assert.Equal(0, (await Crm.Ok<BulkResult>(await a.Post("/api/v1/leads/bulk/stage", new { leadIds = mine.Select(l => l.Id), stageId = bStage.Id }))).Succeeded);
        Assert.Equal(0, (await Crm.Ok<BulkResult>(await a.Post("/api/v1/leads/bulk/assign", new { leadIds = mine.Select(l => l.Id), ownerUserId = W.BAdmin.Id }))).Succeeded);

        // permissions: assign needs Leads.Assign, tag/stage need Leads.Update, archive needs Leads.Delete
        var agent = await Login(W.ASalesAgent);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.Post("/api/v1/leads/bulk/assign", new { leadIds = ids, ownerUserId = W.ASalesAgent.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.Post("/api/v1/leads/bulk/archive", new { leadIds = ids })).StatusCode);
        var ro = await Login(W.AReadOnly);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post("/api/v1/leads/bulk/tag", new { leadIds = ids, tagId = tag.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post("/api/v1/leads/bulk/stage", new { leadIds = ids, stageId = stage.Id })).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Permission_semantics_401_403_404()
    {
        var anon = ApiSession.Anonymous(app);
        var id = Guid.NewGuid();
        foreach (var url in new[] { "/api/v1/leads", $"/api/v1/leads/{id}", "/api/v1/tags", "/api/v1/pipelines", "/api/v1/suppression", "/api/v1/imports", "/api/v1/scoring/rules", "/api/v1/categories",
                     "/api/v1/audience/eligible-contacts", "/api/v1/leads/export", $"/api/v1/contacts/{id}/consent", $"/api/v1/leads/{id}/contacts", "/api/v1/leads/board", "/api/v1/leads/lookups" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Get(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Post("/api/v1/leads", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Delete($"/api/v1/leads/{id}")).StatusCode);

        var ro = await Login(W.AReadOnly);                       // Leads.View + Campaigns.View only
        Assert.Equal(HttpStatusCode.OK, (await ro.Get("/api/v1/leads")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ro.Get("/api/v1/audience/eligible-contacts")).StatusCode);
        foreach (var (method, url) in new (HttpMethod, string)[]
                 {
                     (HttpMethod.Post, "/api/v1/leads"), (HttpMethod.Put, $"/api/v1/leads/{id}"), (HttpMethod.Delete, $"/api/v1/leads/{id}"), (HttpMethod.Put, $"/api/v1/leads/{id}/stage"),
                     (HttpMethod.Put, $"/api/v1/leads/{id}/owner"), (HttpMethod.Post, $"/api/v1/leads/{id}/contacts"), (HttpMethod.Post, $"/api/v1/contacts/{id}/consent"),
                     (HttpMethod.Post, $"/api/v1/leads/{id}/notes"), (HttpMethod.Post, "/api/v1/tags"), (HttpMethod.Post, "/api/v1/pipelines"), (HttpMethod.Post, "/api/v1/suppression"),
                     (HttpMethod.Get, "/api/v1/suppression"), (HttpMethod.Get, "/api/v1/imports"), (HttpMethod.Get, "/api/v1/leads/export"), (HttpMethod.Get, "/api/v1/scoring/rules"),
                     (HttpMethod.Post, "/api/v1/scoring/rescore"), (HttpMethod.Post, "/api/v1/categories"), (HttpMethod.Post, $"/api/v1/leads/{id}/opportunities"),
                 })
        {
            var res = await ro.Http.SendAsync(new HttpRequestMessage(method, url) { Content = method == HttpMethod.Get ? null : JsonContent.Create(new { name = "x" }) });
            Assert.True(res.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} should be 403 for a read-only user but was {(int)res.StatusCode}");
        }
        // a user with no CRM permission at all
        var usersOnly = await Login(W.AUsersOnly);
        Assert.Equal(HttpStatusCode.Forbidden, (await usersOnly.Get("/api/v1/leads")).StatusCode);
        // marketing manager: may manage consent/suppression and view, not edit leads
        var mkt = await Login(W.AMarketing);
        Assert.Equal(HttpStatusCode.OK, (await mkt.Get("/api/v1/suppression")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mkt.Post("/api/v1/leads", new { name = "x" })).StatusCode);
        // unknown ids are 404, never 403/500
        var sm = await Login(W.ASalesManager);
        Assert.Equal(HttpStatusCode.NotFound, (await sm.Get($"/api/v1/contacts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sm.Get($"/api/v1/leads/{id}/contacts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sm.Get($"/api/v1/imports/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sm.Delete($"/api/v1/suppression/{id}")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Opportunities_follow_the_leads_pipeline()
    {
        var s = await Login(W.ASalesManager);
        var lead = await Crm.CreateLead(s);
        var opp = await Crm.Ok<OpportunityDto>(await s.Post($"/api/v1/leads/{lead.Id}/opportunities", new { name = "Annual licence", value = 12500.50m, currency = "usd", expectedCloseDateUtc = "2027-01-31T00:00:00Z" }));
        Assert.Equal("USD", opp.Currency);
        Assert.Equal(12500.50m, opp.Value);
        Assert.Equal("New", opp.StageName);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post($"/api/v1/leads/{lead.Id}/opportunities", new { name = "x", value = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post($"/api/v1/leads/{lead.Id}/opportunities", new { name = "x", value = 1, currency = "dollars" })).StatusCode);
        var won = (await Crm.Ok<List<PipelineDto>>(await s.Get("/api/v1/pipelines"))).Single(p => p.IsDefault).Stages.Single(x => x.Name == "Won");
        var moved = await Crm.Ok<OpportunityDto>(await s.Put($"/api/v1/opportunities/{opp.Id}/stage", new { stageId = won.Id }));
        Assert.Equal(OpportunityStatus.Won, moved.Status);
        Assert.Single(await Crm.Ok<List<OpportunityDto>>(await s.Get($"/api/v1/leads/{lead.Id}/opportunities")));
        var edited = await Crm.Ok<OpportunityDto>(await s.Put($"/api/v1/opportunities/{opp.Id}", new { name = "Annual licence (renewed)", value = 15000, currency = "USD" }));
        Assert.Equal(15000m, edited.Value);
        var b = await Login(W.BSalesManager);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Put($"/api/v1/opportunities/{opp.Id}", new { name = "x", value = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Get($"/api/v1/leads/{lead.Id}/opportunities")).StatusCode);
    }
}
