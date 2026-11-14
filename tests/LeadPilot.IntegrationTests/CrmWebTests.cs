using System.Net;
using System.Text.RegularExpressions;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Crm;

namespace LeadPilot.IntegrationTests;

/// <summary>The MVC UI for the CRM: pages render, forms work, destructive actions are POST + antiforgery, permissions and tenant isolation hold.</summary>
[Collection("app")]
public class CrmWebTests(AppFixture app)
{
    private TestWorld W => app.World;
    private Task<WebSession> Web(TestUser u) => WebSession.LoginAsync(app, u.Email, u.Password);
    private Task<ApiSession> Api(TestUser u) => ApiSession.LoginAsync(app, u.Email, u.Password);

    private static async Task<string> Body(HttpResponseMessage r) => await r.Content.ReadAsStringAsync();
    private static string Location(HttpResponseMessage r) => r.Headers.Location?.OriginalString ?? "";

    private static async Task<HttpResponseMessage> Upload(WebSession s, string url, string fileName, byte[] bytes)
    {
        var token = await AppFixture.GetAntiForgeryTokenAsync(s.Http, "/Profile");
        var form = Crm.File(fileName, bytes);
        form.Add(new StringContent(token), "__RequestVerificationToken");
        return await s.Http.PostAsync(url, form);
    }

    [RequiresMySqlFact]
    public async Task Crm_pages_render_for_a_sales_manager_and_the_sidebar_lists_only_permitted_pages()
    {
        var s = await Web(W.ASalesManager);
        foreach (var (url, text) in new[]
                 {
                     ("/Leads", "Leads"), ("/Leads/Create", "New lead"), ("/Leads/Board", "Pipeline board"), ("/Imports", "Imports"), ("/Imports/New", "Choose a file"), ("/Suppression", "Suppression list"),
                     ("/Scoring", "Scoring rules"), ("/Pipelines", "Pipelines"), ("/Categories", "Categories"), ("/Tags", "Tags"),
                 })
        {
            var html = await s.Html(url);
            Assert.Contains(text, html);
            Assert.DoesNotContain("Lorem", html);
        }
        var page = await s.Html("/Leads");
        foreach (var link in new[] { "/Leads/Board", "/Imports", "/Tags", "/Suppression", "/Pipelines", "/Categories", "/Scoring" }) Assert.Contains($"href=\"{link}\"", page);

        var ro = await (await Web(W.AReadOnly)).Html("/Leads");
        Assert.Contains("href=\"/Leads/Board\"", ro);
        foreach (var link in new[] { "/Imports", "/Suppression", "/Pipelines", "/Categories", "/Scoring" }) Assert.DoesNotContain($"href=\"{link}\"", ro);   // sidebar items only for pages the user may open
        Assert.DoesNotContain("New lead", ro);
        Assert.DoesNotContain(">Export<", ro);

        var none = await (await Web(W.AUsersOnly)).Html("/");
        Assert.DoesNotContain("href=\"/Leads\"", none);
    }

    [RequiresMySqlFact]
    public async Task Permission_semantics_in_the_ui()
    {
        var anon = WebSession.Anonymous(app);
        foreach (var url in new[] { "/Leads", "/Leads/Create", "/Imports", "/Suppression", "/Scoring", "/Pipelines", "/Categories", "/Tags", "/Leads/Board" })
        {
            var r = await anon.Get(url);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/Account/Login", Location(r));
        }
        var ro = await Web(W.AReadOnly);
        foreach (var url in new[] { "/Leads/Create", "/Imports", "/Imports/New", "/Suppression", "/Scoring", "/Pipelines", "/Categories", "/Leads/Export" })
            Assert.Equal(HttpStatusCode.Forbidden, (await ro.Get(url)).StatusCode);
        foreach (var url in new[] { "/Leads/Create", "/Suppression/Add", "/Pipelines/Create", "/Scoring/Rescore", "/Categories/CreateCategory", "/Tags/Create", $"/Leads/Archive/{Guid.NewGuid()}", "/Leads/BulkArchive", "/Imports/Upload" })
            Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post(url, ("name", "x"), ("Name", "x"))).StatusCode);
        // agents work with leads but cannot import, export, manage consent or configure
        var agent = await Web(W.ASalesAgent);
        Assert.Equal(HttpStatusCode.OK, (await agent.Get("/Leads/Create")).StatusCode);
        foreach (var url in new[] { "/Imports", "/Suppression", "/Scoring", "/Pipelines", "/Categories", "/Leads/Export" }) Assert.Equal(HttpStatusCode.Forbidden, (await agent.Get(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.Get("/Tags")).StatusCode);
        // marketing: suppression + export yes, creating leads no
        var mkt = await Web(W.AMarketing);
        Assert.Equal(HttpStatusCode.OK, (await mkt.Get("/Suppression")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mkt.Get("/Leads/Create")).StatusCode);
        // posts without an antiforgery token are rejected
        var plain = await Web(W.ASalesManager);
        Assert.Equal(HttpStatusCode.BadRequest, (await plain.Http.PostAsync("/Leads/Create", new FormUrlEncodedContent(new Dictionary<string, string> { ["Name"] = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await plain.Http.PostAsync($"/Leads/Archive/{Guid.NewGuid()}", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Create_view_edit_archive_restore_a_lead_through_forms()
    {
        var s = await Web(W.ASalesManager);
        var token = Crm.Token();
        var name = $"{token} Academy";
        var created = await s.Post("/Leads/Create", ("Name", name), ("City", "Riyadh"), ("Website", $"https://{token}.example"), ("Confidence", "60"), ("ContactName", "Sara"), ("ContactPhone", Crm.Phone()), ("ContactEmail", $"{token}@example.com"));
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var details = Location(created);
        Assert.Matches(@"^/Leads/Details/[0-9a-f-]{36}$", details);
        var id = details[^36..];

        var page = await s.Html(details);
        Assert.Contains(name, page);
        Assert.Contains("Lead created.", page);
        foreach (var tab in new[] { "contacts", "activity", "notes", "consent" }) Assert.Equal(HttpStatusCode.OK, (await s.Get($"{details}?tab={tab}")).StatusCode);
        Assert.Contains("Sara", await s.Html($"{details}?tab=contacts"));
        Assert.Contains("Lead created", await s.Html($"{details}?tab=activity"));

        // duplicate warning with an explicit override
        var dup = await s.Post("/Leads/Create", ("Name", name.ToUpper()), ("City", "riyadh"), ("Confidence", "50"));
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);
        var dupHtml = await Body(dup);
        Assert.Contains("Possible duplicate", dupHtml);
        Assert.Contains("Create it anyway", dupHtml);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post("/Leads/Create", ("Name", name.ToUpper()), ("City", "riyadh"), ("Confidence", "50"), ("AllowDuplicate", "true"))).StatusCode);

        // validation: missing name, bad website
        var bad = await s.Post("/Leads/Create", ("Name", ""), ("Confidence", "50"));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        var badSite = await s.Post("/Leads/Create", ("Name", Crm.Name(Crm.Token())), ("Website", "javascript:alert(1)"), ("Confidence", "50"));
        Assert.Contains("Website is not a valid address", await Body(badSite));

        // edit (optimistic concurrency token is a hidden field)
        var edit = await s.Html($"/Leads/Edit/{id}");
        var version = Regex.Match(edit, "name=\"Version\"[^>]*value=\"(\\d+)\"").Groups[1].Value;
        Assert.NotEmpty(version);
        var saved = await s.Post($"/Leads/Edit/{id}", ("Name", name), ("City", "Jeddah"), ("Confidence", "70"), ("Version", version));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Contains("Jeddah", await s.Html(details));
        var stale = await s.Post($"/Leads/Edit/{id}", ("Name", name), ("City", "Dammam"), ("Confidence", "70"), ("Version", version));   // same old version again
        Assert.Equal(HttpStatusCode.OK, stale.StatusCode);
        Assert.Contains("changed by someone else", await Body(stale));

        // archive (confirm dialog is wired through data-confirm on a POST form) and restore
        Assert.Contains("data-confirm=\"Archive", page);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Leads/Archive/{id}")).StatusCode);
        var archived = await s.Html(details);
        Assert.Contains("archived", archived);
        Assert.DoesNotContain("Move</button>", archived);                            // frozen: no stage change on archived leads
        Assert.DoesNotContain($"/Leads/Details/{id}", await s.Html($"/Leads?search={token}"));                     // archived leads leave the default list
        Assert.Contains($"/Leads/Details/{id}", await s.Html($"/Leads?search={token}&archived=Archived"));
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Leads/Restore/{id}")).StatusCode);
        Assert.DoesNotContain("badge text-bg-warning align-middle fs-6\">archived", await s.Html(details));
    }

    [RequiresMySqlFact]
    public async Task Contacts_notes_stage_tags_and_consent_through_the_lead_page()
    {
        var s = await Web(W.ASalesManager);
        var api = await Api(W.ASalesManager);
        var lead = await Crm.CreateLead(api, contact: false);
        var id = lead.Id;
        var phone = Crm.Phone();

        // contacts
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Leads/AddContact/{id}", ("ContactForm.Name", "Omar"), ("ContactForm.Phone", phone.Insert(4, " ")), ("ContactForm.Email", "omar@example.com"), ("ContactForm.DataOrigin", "Verified"), ("ContactForm.IsPrimary", "true"))).StatusCode);
        var contactsHtml = await s.Html($"/Leads/Details/{id}?tab=contacts");
        Assert.Contains(System.Text.Encodings.Web.HtmlEncoder.Default.Encode(phone), contactsHtml);   // Razor HTML-encodes "+"
        Assert.Contains("verified", contactsHtml);
        var contactId = (await Crm.Ok<List<ContactDto>>(await api.Get($"/api/v1/leads/{id}/contacts"))).Single().Id;
        Assert.Contains("Edit contact", await s.Html($"/Leads/EditContact/{id}?contactId={contactId}"));
        var bad = await s.Post($"/Leads/EditContact/{id}?contactId={contactId}", ("Name", "Omar"), ("Phone", "12"), ("DataOrigin", "Public"));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);
        Assert.Contains("Phone number is not valid", await Body(bad));
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Leads/AddContact/{id}", ("ContactForm.Name", "Dup"), ("ContactForm.Phone", phone))).StatusCode);   // conflict surfaces as a message, not a crash
        Assert.Contains("already has a contact", await s.Html($"/Leads/Details/{id}?tab=contacts"));

        // consent through the UI (and agents cannot)
        var consent = await s.Post($"/Leads/RecordConsent/{id}", ("ConsentForm.ContactId", contactId.ToString()), ("ConsentForm.Channel", "WhatsApp"), ("ConsentForm.Purpose", "Marketing"), ("ConsentForm.Status", "Granted"), ("ConsentForm.Source", "web form"), ("ConsentForm.EvidenceRef", "form-9"));
        Assert.Equal(HttpStatusCode.Redirect, consent.StatusCode);
        var consentHtml = await s.Html($"/Leads/Details/{id}?tab=consent");
        Assert.Contains("form-9", consentHtml);
        Assert.Contains("append-only", consentHtml);
        Assert.DoesNotContain("Edit consent", consentHtml);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await Web(W.ASalesAgent)).Post($"/Leads/RecordConsent/{id}", ("ConsentForm.ContactId", contactId.ToString()), ("ConsentForm.Channel", "Sms"), ("ConsentForm.Purpose", "Marketing"), ("ConsentForm.Status", "Granted"), ("ConsentForm.Source", "x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Leads/RecordConsent/{id}", ("ConsentForm.ContactId", contactId.ToString()), ("ConsentForm.Channel", "WhatsApp"), ("ConsentForm.Purpose", "Marketing"), ("ConsentForm.Status", "Revoked"), ("ConsentForm.Source", "STOP reply"))).StatusCode);
        Assert.Contains("STOP reply", await s.Html($"/Leads/Details/{id}?tab=consent"));
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Leads/RecordConsent/{id}", ("ConsentForm.ContactId", contactId.ToString()), ("ConsentForm.Channel", "WhatsApp"), ("ConsentForm.Purpose", "Marketing"), ("ConsentForm.Status", "Granted"), ("ConsentForm.Source", ""))).StatusCode);
        Assert.Contains("say how the consent", await s.Html($"/Leads/Details/{id}?tab=consent"));

        // notes
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Leads/AddNote/{id}", ("NoteForm.Body", "<b>Called</b> the head teacher"))).StatusCode);
        var notes = await s.Html($"/Leads/Details/{id}?tab=notes");
        Assert.Contains("&lt;b&gt;Called&lt;/b&gt;", notes);                                // output is HTML-encoded
        var noteId = (await Crm.Page<NoteDto>(await api.Get($"/api/v1/leads/{id}/notes"))).Items.Single().Id;
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Leads/DeleteNote/{id}?noteId={noteId}")).StatusCode);
        Assert.DoesNotContain("head teacher", await s.Html($"/Leads/Details/{id}?tab=notes"));

        // stage + tags + assignment
        var stage = (await Crm.Ok<List<PipelineDto>>(await api.Get("/api/v1/pipelines"))).Single(p => p.IsDefault).Stages.Single(x => x.Name == "Qualified");
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Leads/ChangeStage/{id}", ("stageId", stage.Id.ToString()))).StatusCode);
        Assert.Contains("Qualified", await s.Html($"/Leads/Details/{id}"));
        Assert.Contains("Stage changed from New to Qualified", await s.Html($"/Leads/Details/{id}?tab=activity"));
        var tag = await Crm.Ok<TagDto>(await api.Post("/api/v1/tags", new { name = Crm.Token("t") }));
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Leads/SetTags/{id}", ("tagIds", tag.Id.ToString()))).StatusCode);
        Assert.Contains(tag.Name, await s.Html($"/Leads/Details/{id}"));
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Leads/Assign/{id}", ("ownerUserId", W.ASalesAgent.Id.ToString()))).StatusCode);
        Assert.Equal(W.ASalesAgent.Id, (await Crm.Ok<LeadDto>(await api.Get($"/api/v1/leads/{id}"))).OwnerUserId);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await Web(W.ASalesAgent)).Post($"/Leads/Assign/{id}", ("ownerUserId", W.ASalesManager.Id.ToString()))).StatusCode);

        // board shows the lead in its stage and can move it
        var board = await s.Html($"/Leads/Board");
        Assert.Contains("Pipeline board", board);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Leads/ChangeStage/{id}", ("stageId", stage.Id.ToString()), ("returnTo", "board"))).StatusCode);

        // remove a contact
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Leads/RemoveContact/{id}?contactId={contactId}")).StatusCode);
        Assert.Empty(await Crm.Ok<List<ContactDto>>(await api.Get($"/api/v1/leads/{id}/contacts")));
    }

    [RequiresMySqlFact]
    public async Task Lead_list_filters_sorting_paging_bulk_actions_and_export()
    {
        var s = await Web(W.ASalesManager);
        var api = await Api(W.ASalesManager);
        var token = Crm.Token();
        var ids = new List<Guid>();
        for (var i = 1; i <= 7; i++) ids.Add((await Crm.CreateLead(api, name: Crm.Name(token, i), city: i % 2 == 0 ? "Jeddah" : "Riyadh")).Id);

        var list = await s.Html($"/Leads?search={token}&pageSize=3&sort=Name&descending=false");
        Assert.Contains(Crm.Name(token, 1), list);
        Assert.DoesNotContain(Crm.Name(token, 4), list);
        Assert.Contains("href=\"/Leads?", list);                                          // pager links keep the filters
        Assert.Contains("search=" + token, list);
        Assert.Contains("7 total", list);
        Assert.Contains(Crm.Name(token, 4), await s.Html($"/Leads?search={token}&pageSize=3&page=2&sort=Name&descending=false"));
        var jeddah = await s.Html($"/Leads?search={token}&city=Jeddah");
        Assert.Contains(Crm.Name(token, 2), jeddah);
        Assert.DoesNotContain(Crm.Name(token, 1), jeddah);
        Assert.Contains("No leads match these filters", await s.Html($"/Leads?search={token}nomatch"));

        // XSS: a hostile name is encoded everywhere
        var evil = await Crm.CreateLead(api, name: $"{token} <script>alert('x')</script>", city: "Riyadh");
        var evilList = await s.Html($"/Leads?search={token}%20%3Cscript");
        Assert.DoesNotContain("<script>alert('x')</script>", evilList);
        Assert.Contains("&lt;script&gt;", evilList);
        Assert.DoesNotContain("<script>alert('x')</script>", await s.Html($"/Leads/Details/{evil.Id}"));

        // bulk actions through the list form
        var tag = await Crm.Ok<TagDto>(await api.Post("/api/v1/tags", new { name = Crm.Token("b") }));
        var chosen = ids.Take(3).ToList();
        var ownerRes = await s.Post("/Leads/BulkAssign", chosen.Select(i => ("ids", i.ToString())).Append(("ownerUserId", W.ASalesAgent.Id.ToString())).Append(("returnQuery", $"?search={token}")).ToArray());
        Assert.Equal(HttpStatusCode.Redirect, ownerRes.StatusCode);
        Assert.Equal($"/Leads?search={token}", Location(ownerRes));
        Assert.Contains("3 lead(s) assigned", await s.Html($"/Leads?search={token}"));
        Assert.All(chosen, i => Assert.Equal(W.ASalesAgent.Id, Crm.Ok<LeadDto>(api.Get($"/api/v1/leads/{i}").Result).Result.OwnerUserId));
        var tagRes = await s.Post("/Leads/BulkTag", chosen.Select(i => ("ids", i.ToString())).Append(("tagId", tag.Id.ToString())).ToArray());
        Assert.Equal(HttpStatusCode.Redirect, tagRes.StatusCode);
        Assert.Equal(3, (await Crm.Page<LeadListItemDto>(await api.Get($"/api/v1/leads?tagId={tag.Id}"))).TotalCount);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post("/Leads/BulkArchive", ("ids", ids[6].ToString()))).StatusCode);
        Assert.True((await Crm.Ok<LeadDto>(await api.Get($"/api/v1/leads/{ids[6]}"))).IsArchived);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post("/Leads/BulkArchive")).StatusCode);
        Assert.Contains("Select at least one lead", await s.Html("/Leads"));
        // an open redirect through returnQuery is not possible
        var redirect = await s.Post("/Leads/BulkArchive", ("ids", Guid.NewGuid().ToString()), ("returnQuery", "https://evil.example/"));
        Assert.Equal("/Leads", Location(redirect));
        // bulk permission: agent cannot archive or assign
        var agent = await Web(W.ASalesAgent);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.Post("/Leads/BulkArchive", ("ids", ids[0].ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.Post("/Leads/BulkAssign", ("ids", ids[0].ToString()))).StatusCode);

        // export from the list keeps the filters
        var export = await s.Get($"/Leads/Export?search={token}&format=Csv");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Contains("attachment", export.Content.Headers.ContentDisposition!.ToString());
        var csv = await Body(export);
        Assert.Contains(Crm.Name(token, 1), csv);
        Assert.Equal(HttpStatusCode.OK, (await s.Get($"/Leads/Export?search={token}&format=Xlsx")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.Get($"/Leads/Export?search={token}")).StatusCode);
        Assert.Contains($"/Leads/Export?", await s.Html($"/Leads?search={token}"));
    }

    [RequiresMySqlFact]
    public async Task Import_wizard_upload_map_run_review_and_error_report()
    {
        var s = await Web(W.ASalesManager);
        var api = await Api(W.ASalesManager);
        var t = Crm.Token();
        var existing = await Crm.CreateLead(api, name: $"{t} Existing School", city: "Riyadh", contact: false);
        var csv = Crm.CsvBytes("School,City,Mobile,Website",
            $"{t} Fresh School,Riyadh,{Crm.Phone()},",
            $"{t} Broken School,Riyadh,12,",
            $"{t} existing school,riyadh,{Crm.Phone()},");

        // wrong type and empty submissions are explained, not crashes
        var wrong = await Upload(s, "/Imports/Upload", "leads.txt", csv);
        Assert.Equal(HttpStatusCode.Redirect, wrong.StatusCode);
        Assert.Contains("Only .csv and .xlsx", await s.Html("/Imports/New"));

        var up = await Upload(s, "/Imports/Upload", "leads.csv", csv);
        Assert.Equal(HttpStatusCode.Redirect, up.StatusCode);
        var mapUrl = Location(up);
        Assert.Matches(@"^/Imports/Map/[0-9a-f-]{36}$", mapUrl);
        var jobId = mapUrl[^36..];
        var map = await s.Html(mapUrl);
        Assert.Contains("Map columns", map);
        Assert.Contains("Mobile", map);
        Assert.Contains("value=\"Phone\" selected", map);                                // the header "Mobile" was recognised as the phone column
        Assert.Contains("Hold for my review", map);

        // run with the suggested mapping (the form posts one select per column, in order) and the review policy
        var run = await s.Post($"/Imports/Run/{jobId}", ("fields", "Name"), ("fields", "City"), ("fields", "Phone"), ("fields", ""), ("duplicatePolicy", "Review"));
        Assert.Equal(HttpStatusCode.Redirect, run.StatusCode);
        var result = await s.Html($"/Imports/Result/{jobId}");
        Assert.Contains("Leads created", result);
        Assert.Contains("Phone number is not valid", result);
        Assert.Contains("Possible duplicate", result);
        Assert.Contains("Awaiting your decision", result);
        Assert.Contains("Download error report", result);
        Assert.Contains("data-confirm=\"Merge this row", result);

        var errors = (await Crm.Page<ImportRowErrorDto>(await api.Get($"/api/v1/imports/{jobId}/errors?kind=Duplicate"))).Items.Single();
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Imports/ResolveDuplicate/{jobId}?errorId={errors.Id}", ("merge", "true"))).StatusCode);
        Assert.Equal(1, (await Crm.Ok<LeadDto>(await api.Get($"/api/v1/leads/{existing.Id}"))).ContactCount);
        Assert.Contains("Merged", await s.Html($"/Imports/Result/{jobId}?kind=Duplicate"));

        var report = await s.Get($"/Imports/ErrorReport/{jobId}");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.Contains("Row,Type,Column,Message,Resolution", await Body(report));
        Assert.Contains(jobId, await s.Html("/Imports"));
        // running again is refused with a message
        var again = await s.Post($"/Imports/Run/{jobId}", ("fields", "Name"));
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.Contains($"/Imports/Result/{jobId}", Location(again));
        // a mapping without a name column is explained
        var up2 = await Upload(s, "/Imports/Upload", "other.csv", Crm.CsvBytes("A,B", "x,y"));
        var jobId2 = Location(up2)[^36..];
        var noName = await s.Post($"/Imports/Run/{jobId2}", ("fields", "City"), ("fields", ""), ("duplicatePolicy", "Skip"));
        Assert.Contains($"/Imports/Map/{jobId2}", Location(noName));
        Assert.Contains("Map one column to", await s.Html(Location(noName)));
        // agents and read-only users cannot import
        Assert.Equal(HttpStatusCode.Forbidden, (await Upload(await Web(W.ASalesAgent), "/Imports/Upload", "x.csv", csv)).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Suppression_scoring_pipeline_tag_and_category_pages_work()
    {
        var s = await Web(W.AAdmin);
        var token = Crm.Token();

        // suppression
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post("/Suppression/Add", ("Form.Value", $"{token}@Example.com"), ("Form.Channel", "Email"), ("Form.Reason", "OptOut"), ("Form.Note", "asked by phone"))).StatusCode);
        var sup = await s.Html($"/Suppression?search={token}");
        Assert.Contains($"{token}@example.com", sup);
        Assert.Contains("asked by phone", sup);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post("/Suppression/Add", ("Form.Value", $"{token}@Example.com"), ("Form.Channel", "Email"), ("Form.Reason", "OptOut"))).StatusCode);
        Assert.Contains("already on the suppression list", await s.Html("/Suppression"));
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post("/Suppression/Add", ("Form.Value", "not valid"), ("Form.Channel", "Email"), ("Form.Reason", "OptOut"))).StatusCode);
        Assert.Contains("Enter a valid e-mail address", await s.Html("/Suppression"));
        var api = await Api(W.AAdmin);
        var entry = (await Crm.Page<SuppressionDto>(await api.Get($"/api/v1/suppression?search={token}"))).Items.Single();
        Assert.Contains("data-confirm=\"Remove", sup);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Suppression/Remove/{entry.Id}")).StatusCode);
        Assert.Empty((await Crm.Page<SuppressionDto>(await api.Get($"/api/v1/suppression?search={token}"))).Items);

        // tags
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post("/Tags/Create", ("name", "tag-" + token))).StatusCode);
        Assert.Contains("tag-" + token, await s.Html("/Tags"));
        var tag = (await Crm.Ok<List<TagDto>>(await api.Get("/api/v1/tags"))).Single(x => x.Name == "tag-" + token);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Tags/Rename/{tag.Id}", ("name", "renamed-" + token))).StatusCode);
        Assert.Contains("renamed-" + token, await s.Html("/Tags"));
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Tags/Delete/{tag.Id}")).StatusCode);
        Assert.DoesNotContain("renamed-" + token, await s.Html("/Tags"));

        // categories and statuses
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post("/Categories/CreateCategory", ("name", "Cat " + token))).StatusCode);
        Assert.Contains("Cat " + token, await s.Html("/Categories"));
        var cat = (await Crm.Ok<List<CategoryDto>>(await api.Get("/api/v1/categories"))).Single(x => x.Name == "Cat " + token);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Categories/SetCategoryActive/{cat.Id}", ("active", "false"))).StatusCode);
        Assert.False((await Crm.Ok<List<CategoryDto>>(await api.Get("/api/v1/categories"))).Single(x => x.Id == cat.Id).IsActive);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post("/Categories/CreateStatus", ("name", "Status " + token), ("isClosed", "true"))).StatusCode);
        Assert.Contains("Status " + token, await s.Html("/Categories"));
    }

    [RequiresMySqlFact]
    public async Task Scoring_and_pipeline_pages_publish_versions_and_edit_stages()
    {
        var (tenant, admin, _) = await W.NewTenantAsync("ui");
        var s = await Web(admin);
        var api = await ApiSession.LoginAsync(app, admin.Email, admin.Password);

        var scoring = await s.Html("/Scoring");
        Assert.Contains("Active version 1", scoring);
        Assert.Contains("Has a website", scoring);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post("/Scoring/AddRule", ("Form.Dimension", "BusinessCategory"), ("Form.Target", "School"), ("Form.Points", "25"), ("Form.Description", "Schools"))).StatusCode);
        var v2 = await s.Html("/Scoring");
        Assert.Contains("Active version 2", v2);
        Assert.Contains("Published as version 2", v2);
        Assert.Contains("Schools", v2);
        var bad = await s.Post("/Scoring/AddRule", ("Form.Dimension", "Location"), ("Form.Target", "Planet=Mars"), ("Form.Points", "5"));
        Assert.Equal(HttpStatusCode.Redirect, bad.StatusCode);
        Assert.Contains("Location rules look like", await s.Html("/Scoring"));
        Assert.Contains("Active version 2", await s.Html("/Scoring"));
        var rule = (await Crm.Ok<ScoringRuleSetDto>(await api.Get("/api/v1/scoring/rules"))).Rules.Single(r => r.Target == "School");
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Scoring/ToggleRule?ruleId={rule.Id}")).StatusCode);
        Assert.Contains("Active version 3", await s.Html("/Scoring"));
        var rule3 = (await Crm.Ok<ScoringRuleSetDto>(await api.Get("/api/v1/scoring/rules"))).Rules.Single(r => r.Target == "School");
        Assert.False(rule3.IsEnabled);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Scoring/DeleteRule?ruleId={rule3.Id}")).StatusCode);
        Assert.DoesNotContain((await Crm.Ok<ScoringRuleSetDto>(await api.Get("/api/v1/scoring/rules"))).Rules, r => r.Target == "School");
        Assert.Equal(HttpStatusCode.NotFound, (await s.Post($"/Scoring/ToggleRule?ruleId={Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post("/Scoring/Rescore")).StatusCode);
        Assert.Contains("Scores recalculated", await s.Html("/Scoring"));

        // pipelines
        var pipelines = await s.Html("/Pipelines");
        Assert.Contains("Sales pipeline", pipelines);
        Assert.Contains("Demo Scheduled", pipelines);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post("/Pipelines/Create", ("NewPipeline.Name", "Partners"))).StatusCode);
        var partners = (await Crm.Ok<List<PipelineDto>>(await api.Get("/api/v1/pipelines"))).Single(p => p.Name == "Partners");
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Pipelines/AddStage/{partners.Id}", ("Name", "Kick-off"), ("Kind", "Open"))).StatusCode);
        var stage = (await Crm.Ok<PipelineDto>(await api.Get($"/api/v1/pipelines/{partners.Id}"))).Stages.Single(x => x.Name == "Kick-off");
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Pipelines/MoveStage/{partners.Id}?stageId={stage.Id}", ("delta", "-1"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Pipelines/UpdateStage/{partners.Id}?stageId={stage.Id}", ("Name", "Kick-off call"), ("Kind", "Won"))).StatusCode);
        Assert.Contains("Kick-off call", await s.Html("/Pipelines"));
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Pipelines/DeleteStage/{partners.Id}?stageId={stage.Id}")).StatusCode);
        Assert.DoesNotContain("Kick-off call", await s.Html("/Pipelines"));
        var lead = await Crm.CreateLead(api);
        var def = (await Crm.Ok<List<PipelineDto>>(await api.Get("/api/v1/pipelines"))).Single(p => p.IsDefault);
        var newStage = def.Stages.Single(x => x.Name == "New");
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Pipelines/DeleteStage/{def.Id}?stageId={newStage.Id}")).StatusCode);
        Assert.Contains("lead(s) are in this stage", await s.Html("/Pipelines"));
        Assert.Equal(HttpStatusCode.Redirect, (await s.Post($"/Pipelines/SetDefault/{partners.Id}")).StatusCode);
        Assert.Contains("Default pipeline changed", await s.Html("/Pipelines"));
        Assert.Contains(partners.Name, await s.Html("/Leads/Board"));
    }

    [RequiresMySqlFact]
    public async Task Tenant_A_cannot_open_or_change_tenant_B_records_through_the_ui()
    {
        var a = await Web(W.ASalesManager);
        var bApi = await Api(W.BSalesManager);
        var bMkt = await Api(W.BAdmin);
        var b = await SeededCrm.CreateAsync(bApi, bMkt);
        var L = b.Lead.Id;

        // lists never contain B's data
        foreach (var url in new[] { "/Leads/Board", "/Tags", "/Categories", "/Pipelines", "/Imports" })
        {
            var html = await a.Html(url);
            Assert.DoesNotContain(b.Token, html);
            Assert.DoesNotContain(b.Lead.Name, html);
        }
        foreach (var url in new[] { $"/Leads?search={b.Token}", $"/Leads?tagId={b.Tag.Id}", $"/Suppression?search={b.Token}" })    // (the page may echo the user's own search text, never B's rows)
        {
            var html = await a.Html(url);
            Assert.DoesNotContain(b.Lead.Name, html);
            Assert.DoesNotContain($"{b.Token}@blocked.example", html);
        }
        // every detail/edit page of B's records is 404
        foreach (var url in new[]
                 {
                     $"/Leads/Details/{L}", $"/Leads/Details/{L}?tab=contacts", $"/Leads/Details/{L}?tab=notes", $"/Leads/Details/{L}?tab=consent", $"/Leads/Details/{L}?tab=activity", $"/Leads/Edit/{L}",
                     $"/Leads/EditContact/{L}?contactId={b.Contact.Id}", $"/Imports/Map/{b.Upload.Job.Id}", $"/Imports/Result/{b.Upload.Job.Id}", $"/Imports/ErrorReport/{b.Upload.Job.Id}",
                 })
            Assert.Equal(HttpStatusCode.NotFound, (await a.Get(url)).StatusCode);

        // every state-changing action on B's records is 404 and B is untouched
        var stage = b.Pipeline.Stages[1].Id.ToString();
        foreach (var (url, fields) in new (string, (string, string)[])[]
                 {
                     ($"/Leads/Edit/{L}", [("Name", "hijacked"), ("Confidence", "50")]), ($"/Leads/Archive/{L}", []), ($"/Leads/Restore/{L}", []), ($"/Leads/ChangeStage/{L}", [("stageId", stage)]),
                     ($"/Leads/Assign/{L}", [("ownerUserId", W.ASalesAgent.Id.ToString())]), ($"/Leads/SetTags/{L}", []), ($"/Leads/AddNote/{L}", [("NoteForm.Body", "x")]),
                     ($"/Leads/DeleteNote/{L}?noteId={b.Note.Id}", []), ($"/Leads/AddContact/{L}", [("ContactForm.Name", "x"), ("ContactForm.Phone", Crm.Phone())]), ($"/Leads/EditContact/{L}?contactId={b.Contact.Id}", [("Name", "x"), ("Phone", "")]),
                     ($"/Leads/RemoveContact/{L}?contactId={b.Contact.Id}", []),
                     ($"/Leads/RecordConsent/{L}", [("ConsentForm.ContactId", b.Contact.Id.ToString()), ("ConsentForm.Channel", "Sms"), ("ConsentForm.Purpose", "Marketing"), ("ConsentForm.Status", "Revoked"), ("ConsentForm.Source", "x")]),
                     ($"/Imports/Run/{b.Upload.Job.Id}", [("fields", "Name")]), ($"/Imports/ResolveDuplicate/{b.Upload.Job.Id}?errorId={Guid.NewGuid()}", [("merge", "true")]),
                     ($"/Suppression/Remove/{b.Suppression.Id}", []), ($"/Pipelines/Rename/{b.Pipeline.Id}", [("Name", "x")]), ($"/Pipelines/SetDefault/{b.Pipeline.Id}", []),
                     ($"/Pipelines/AddStage/{b.Pipeline.Id}", [("Name", "x")]), ($"/Pipelines/DeleteStage/{b.Pipeline.Id}?stageId={b.Pipeline.Stages[0].Id}", []),
                     ($"/Tags/Rename/{b.Tag.Id}", [("name", "x")]), ($"/Tags/Delete/{b.Tag.Id}", []), ($"/Categories/RenameCategory/{b.Category.Id}", [("name", "x")]),
                 })
        {
            var res = await a.Post(url, fields);
            Assert.True(res.StatusCode is HttpStatusCode.NotFound, $"POST {url} should be 404 for another tenant's record but was {(int)res.StatusCode}");
        }
        var lead = await Crm.Ok<LeadDto>(await bApi.Get($"/api/v1/leads/{L}"));
        Assert.Equal((b.Lead.Name, false, "New", b.Lead.Version), (lead.Name, lead.IsArchived, lead.StageName, lead.Version));
        Assert.Equal(ImportStatus.Uploaded, (await Crm.Ok<ImportJobDto>(await bApi.Get($"/api/v1/imports/{b.Upload.Job.Id}"))).Status);
        Assert.NotEmpty((await Crm.Page<SuppressionDto>(await bMkt.Get($"/api/v1/suppression?search={b.Token}"))).Items);
        Assert.Contains(await Crm.Ok<List<TagDto>>(await bApi.Get("/api/v1/tags")), t => t.Id == b.Tag.Id);

        // bulk with B's id changes nothing and says "not found"
        var bulk = await a.Post("/Leads/BulkArchive", ("ids", L.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, bulk.StatusCode);
        Assert.False((await Crm.Ok<LeadDto>(await bApi.Get($"/api/v1/leads/{L}"))).IsArchived);
        Assert.Contains("Lead not found", await a.Html("/Leads"));

        // a spoofed tenant id in a form is rejected before anything runs
        var spoof = await a.Post("/Leads/Create", ("Name", Crm.Name(Crm.Token())), ("Confidence", "50"), ("tenantId", W.B.Id.ToString()));
        Assert.Equal(HttpStatusCode.BadRequest, spoof.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Get($"/Leads?tenantId={W.B.Id}")).StatusCode);
    }
}

[Collection("app")]
public class StaticAssetTests(AppFixture app)
{
    [RequiresMySqlFact]
    public async Task Static_assets_are_served_to_anonymous_visitors_so_the_login_page_is_styled()
    {
        var anon = WebSession.Anonymous(app);
        foreach (var url in new[] { "/css/site.css", "/js/site.js", "/lib/bootstrap/dist/css/bootstrap.min.css", "/lib/bootstrap/dist/js/bootstrap.bundle.min.js", "/lib/jquery/dist/jquery.min.js" })
        {
            var res = await anon.Get(url);
            Assert.True(res.StatusCode == HttpStatusCode.OK, $"{url} answered {(int)res.StatusCode} for an anonymous visitor");
        }
        Assert.Equal(HttpStatusCode.Redirect, (await anon.Get("/Leads")).StatusCode);   // application pages are still protected
    }
}
