using System.Net;
using System.Net.Http.Json;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Crm;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>Tenant A must never see, change, export, import into, or reference tenant B's CRM data — through the REST API and at the database layer.</summary>
[Collection("app")]
public class CrmTenantIsolationApiTests(AppFixture app)
{
    private TestWorld W => app.World;
    private async Task<(ApiSession A, ApiSession B, SeededCrm DataB, SeededCrm DataA)> SetupAsync()
    {
        var a = await ApiSession.LoginAsync(app, W.ASalesManager.Email, W.ASalesManager.Password);
        var b = await ApiSession.LoginAsync(app, W.BSalesManager.Email, W.BSalesManager.Password);
        var bMarketing = await ApiSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password);       // Tenant Administrator holds Suppression.Manage
        var aMarketing = await ApiSession.LoginAsync(app, W.AMarketing.Email, W.AMarketing.Password);
        return (a, b, await SeededCrm.CreateAsync(b, bMarketing), await SeededCrm.CreateAsync(a, aMarketing));
    }

    private static async Task Expect(HttpStatusCode code, string what, Task<HttpResponseMessage> call)
    {
        var res = await call;
        Assert.True(res.StatusCode == code, $"{what}: expected {(int)code} but got {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
    }

    [RequiresMySqlFact]
    public async Task Tenant_A_cannot_list_or_find_tenant_B_data()
    {
        var (a, b, bd, ad) = await SetupAsync();

        Assert.Empty((await Crm.Page<LeadListItemDto>(await a.Get($"/api/v1/leads?search={bd.Token}"))).Items);
        Assert.Empty((await Crm.Page<LeadListItemDto>(await a.Get($"/api/v1/leads?search={bd.Phone[^8..]}"))).Items);                 // not by phone either
        Assert.Empty((await Crm.Page<LeadListItemDto>(await a.Get($"/api/v1/leads?categoryId={bd.Category.Id}"))).Items);
        Assert.Empty((await Crm.Page<LeadListItemDto>(await a.Get($"/api/v1/leads?tagId={bd.Tag.Id}"))).Items);
        Assert.DoesNotContain(await Crm.Ok<List<TagDto>>(await a.Get("/api/v1/tags")), t => t.Id == bd.Tag.Id);
        Assert.DoesNotContain(await Crm.Ok<List<CategoryDto>>(await a.Get("/api/v1/categories")), c => c.Id == bd.Category.Id);
        Assert.DoesNotContain(await Crm.Ok<List<PipelineDto>>(await a.Get("/api/v1/pipelines")), p => p.Id == bd.Pipeline.Id);
        Assert.DoesNotContain((await Crm.Page<ImportJobDto>(await a.Get("/api/v1/imports?pageSize=100"))).Items, j => j.Id == bd.Upload.Job.Id);
        var am = await ApiSession.LoginAsync(app, W.AMarketing.Email, W.AMarketing.Password);
        Assert.Empty((await Crm.Page<SuppressionDto>(await am.Get($"/api/v1/suppression?search={bd.Token}"))).Items);
        Assert.Contains((await Crm.Page<SuppressionDto>(await am.Get($"/api/v1/suppression?search={ad.Token}"))).Items, s => s.Id == ad.Suppression.Id);   // sanity: own data is visible
        var lookups = await Crm.Ok<CrmLookupsDto>(await a.Get("/api/v1/leads/lookups"));
        Assert.DoesNotContain(lookups.Tags, t => t.Id == bd.Tag.Id);
        Assert.DoesNotContain(lookups.Categories, c => c.Id == bd.Category.Id);
        Assert.DoesNotContain(lookups.Owners, o => o.UserId == W.BAdmin.Id);

        var export = await (await a.Get($"/api/v1/leads/export?search={bd.Token}")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(bd.Token, export);
        Assert.Contains(ad.Token, await (await a.Get($"/api/v1/leads/export?search={ad.Token}")).Content.ReadAsStringAsync());

        // the eligible audience is computed per tenant
        var aAudience = await Crm.Page<EligibleContactDto>(await a.Get("/api/v1/audience/eligible-contacts?channel=WhatsApp&pageSize=100"));
        Assert.DoesNotContain(aAudience.Items, i => i.ContactId == bd.Contact.Id);
        Assert.Contains(aAudience.Items, i => i.ContactId == ad.Contact.Id);
        var bAudience = await Crm.Page<EligibleContactDto>(await b.Get("/api/v1/audience/eligible-contacts?channel=WhatsApp&pageSize=100"));
        Assert.Contains(bAudience.Items, i => i.ContactId == bd.Contact.Id);
        Assert.DoesNotContain(bAudience.Items, i => i.ContactId == ad.Contact.Id);

        var board = await Crm.Ok<BoardDto>(await a.Get("/api/v1/leads/board"));
        Assert.DoesNotContain(board.Columns.SelectMany(c => c.Cards), c => c.Id == bd.Lead.Id);
    }

    [RequiresMySqlFact]
    public async Task Tenant_A_gets_404_for_every_read_of_a_tenant_B_resource()
    {
        var (a, _, bd, _) = await SetupAsync();
        var L = bd.Lead.Id;
        foreach (var url in new[]
                 {
                     $"/api/v1/leads/{L}", $"/api/v1/leads/{L}/contacts", $"/api/v1/leads/{L}/notes", $"/api/v1/leads/{L}/activities", $"/api/v1/leads/{L}/consent", $"/api/v1/leads/{L}/opportunities",
                     $"/api/v1/leads/{L}/score-history", $"/api/v1/contacts/{bd.Contact.Id}", $"/api/v1/contacts/{bd.Contact.Id}/consent", $"/api/v1/contacts/{bd.Contact.Id}/eligibility?channel=WhatsApp",
                     $"/api/v1/pipelines/{bd.Pipeline.Id}", $"/api/v1/imports/{bd.Upload.Job.Id}", $"/api/v1/imports/{bd.Upload.Job.Id}/preview", $"/api/v1/imports/{bd.Upload.Job.Id}/errors",
                     $"/api/v1/imports/{bd.Upload.Job.Id}/errors/report",
                 })
            await Expect(HttpStatusCode.NotFound, "GET " + url, a.Get(url));
    }

    [RequiresMySqlFact]
    public async Task Tenant_A_cannot_update_delete_or_act_on_tenant_B_resources_and_B_is_left_untouched()
    {
        var (a, b, bd, _) = await SetupAsync();
        var L = bd.Lead.Id; var C = bd.Contact.Id;
        var bStage = bd.Pipeline.Stages[2];

        await Expect(HttpStatusCode.NotFound, "update lead", a.Put($"/api/v1/leads/{L}", new { name = "hijacked", city = "Nowhere" }));
        await Expect(HttpStatusCode.NotFound, "archive lead", a.Delete($"/api/v1/leads/{L}"));
        await Expect(HttpStatusCode.NotFound, "restore lead", a.Post($"/api/v1/leads/{L}/restore"));
        await Expect(HttpStatusCode.NotFound, "stage", a.Put($"/api/v1/leads/{L}/stage", new { stageId = bStage.Id }));
        await Expect(HttpStatusCode.NotFound, "assign", a.Put($"/api/v1/leads/{L}/owner", new { ownerUserId = W.ASalesAgent.Id }));
        await Expect(HttpStatusCode.NotFound, "rescore", a.Post($"/api/v1/leads/{L}/rescore"));
        await Expect(HttpStatusCode.NotFound, "add contact", a.Post($"/api/v1/leads/{L}/contacts", new { name = "intruder", phone = Crm.Phone() }));
        await Expect(HttpStatusCode.NotFound, "update contact", a.Put($"/api/v1/contacts/{C}", new { name = "intruder" }));
        await Expect(HttpStatusCode.NotFound, "delete contact", a.Delete($"/api/v1/contacts/{C}"));
        await Expect(HttpStatusCode.NotFound, "add note", a.Post($"/api/v1/leads/{L}/notes", new { body = "intruder" }));
        await Expect(HttpStatusCode.NotFound, "delete note", a.Delete($"/api/v1/notes/{bd.Note.Id}"));
        await Expect(HttpStatusCode.NotFound, "rename tag", a.Put($"/api/v1/tags/{bd.Tag.Id}", new { name = "hijacked" }));
        await Expect(HttpStatusCode.NotFound, "delete tag", a.Delete($"/api/v1/tags/{bd.Tag.Id}"));
        await Expect(HttpStatusCode.NotFound, "set tags", a.Put($"/api/v1/leads/{L}/tags", new { tagIds = Array.Empty<Guid>() }));
        await Expect(HttpStatusCode.NotFound, "record consent", a.Post($"/api/v1/contacts/{C}/consent", new { channel = "WhatsApp", purpose = "Marketing", status = "Revoked", source = "x" }));
        await Expect(HttpStatusCode.NotFound, "delete suppression", (await ApiSession.LoginAsync(app, W.AMarketing.Email, W.AMarketing.Password)).Delete($"/api/v1/suppression/{bd.Suppression.Id}"));
        await Expect(HttpStatusCode.NotFound, "rename pipeline", a.Put($"/api/v1/pipelines/{bd.Pipeline.Id}", new { name = "hijacked" }));
        await Expect(HttpStatusCode.NotFound, "default pipeline", a.Post($"/api/v1/pipelines/{bd.Pipeline.Id}/default"));
        await Expect(HttpStatusCode.NotFound, "deactivate pipeline", a.Put($"/api/v1/pipelines/{bd.Pipeline.Id}/active", new { active = false }));
        await Expect(HttpStatusCode.NotFound, "add stage", a.Post($"/api/v1/pipelines/{bd.Pipeline.Id}/stages", new { name = "x" }));
        await Expect(HttpStatusCode.NotFound, "update stage", a.Put($"/api/v1/pipelines/{bd.Pipeline.Id}/stages/{bStage.Id}", new { name = "x" }));
        await Expect(HttpStatusCode.NotFound, "move stage", a.Post($"/api/v1/pipelines/{bd.Pipeline.Id}/stages/{bStage.Id}/move", new { delta = 1 }));
        await Expect(HttpStatusCode.NotFound, "delete stage", a.Delete($"/api/v1/pipelines/{bd.Pipeline.Id}/stages/{bStage.Id}"));
        await Expect(HttpStatusCode.NotFound, "run import", a.Post($"/api/v1/imports/{bd.Upload.Job.Id}/run", new { mapping = Crm.MappingFrom(bd.Upload) }));
        await Expect(HttpStatusCode.NotFound, "resolve duplicate", a.Post($"/api/v1/imports/{bd.Upload.Job.Id}/duplicates/{Guid.NewGuid()}", new { merge = true }));
        await Expect(HttpStatusCode.NotFound, "add opportunity", a.Post($"/api/v1/leads/{L}/opportunities", new { name = "x", value = 1 }));
        await Expect(HttpStatusCode.NotFound, "update opportunity", a.Put($"/api/v1/opportunities/{bd.Opportunity.Id}", new { name = "x", value = 1 }));
        await Expect(HttpStatusCode.NotFound, "opportunity stage", a.Put($"/api/v1/opportunities/{bd.Opportunity.Id}/stage", new { stageId = bStage.Id }));
        await Expect(HttpStatusCode.NotFound, "rename category", a.Put($"/api/v1/categories/{bd.Category.Id}", new { name = "x" }));
        await Expect(HttpStatusCode.NotFound, "deactivate category", a.Put($"/api/v1/categories/{bd.Category.Id}/active", new { active = false }));

        // bulk operations simply cannot see B's leads
        foreach (var (url, body) in new (string, object)[]
                 {
                     ("/api/v1/leads/bulk/assign", new { leadIds = new[] { L }, ownerUserId = W.ASalesAgent.Id }), ("/api/v1/leads/bulk/archive", new { leadIds = new[] { L } }),
                     ("/api/v1/leads/bulk/stage", new { leadIds = new[] { L }, stageId = bStage.Id }), ("/api/v1/leads/bulk/tag", new { leadIds = new[] { L }, tagId = bd.Tag.Id }),
                 })
        {
            var result = await Crm.Ok<BulkResult>(await a.Post(url, body));
            Assert.Equal(0, result.Succeeded);
        }

        // ...and B's data is exactly as B left it
        var lead = await Crm.Ok<LeadDto>(await b.Get($"/api/v1/leads/{L}"));
        Assert.Equal(bd.Lead.Name, lead.Name);
        Assert.False(lead.IsArchived);
        Assert.Null(lead.OwnerUserId);
        Assert.Equal("New", lead.StageName);
        Assert.Equal(bd.Lead.Version, lead.Version);
        Assert.Single(lead.Tags, t => t.Id == bd.Tag.Id);
        Assert.Single(await Crm.Ok<List<ContactDto>>(await b.Get($"/api/v1/leads/{L}/contacts")));
        Assert.Single((await Crm.Page<NoteDto>(await b.Get($"/api/v1/leads/{L}/notes"))).Items);
        Assert.Equal(1, (await Crm.Page<ConsentRecordDto>(await b.Get($"/api/v1/contacts/{C}/consent"))).TotalCount);
        Assert.Equal(ImportStatus.Uploaded, (await Crm.Ok<ImportJobDto>(await b.Get($"/api/v1/imports/{bd.Upload.Job.Id}"))).Status);
        Assert.Equal(bd.Pipeline.Name, (await Crm.Ok<PipelineDto>(await b.Get($"/api/v1/pipelines/{bd.Pipeline.Id}"))).Name);
        Assert.Contains(await Crm.Ok<List<TagDto>>(await b.Get("/api/v1/tags")), t => t.Id == bd.Tag.Id && t.Name == bd.Tag.Name);
        Assert.Single(await Crm.Ok<List<OpportunityDto>>(await b.Get($"/api/v1/leads/{L}/opportunities")));
    }

    [RequiresMySqlFact]
    public async Task References_to_tenant_B_records_inside_requests_are_rejected()
    {
        var (a, _, bd, ad) = await SetupAsync();
        var name = () => Crm.Name(Crm.Token());
        await Expect(HttpStatusCode.BadRequest, "category", a.Post("/api/v1/leads", new { name = name(), categoryId = bd.Category.Id }));
        await Expect(HttpStatusCode.BadRequest, "stage", a.Post("/api/v1/leads", new { name = name(), stageId = bd.Pipeline.Stages[0].Id }));
        await Expect(HttpStatusCode.BadRequest, "status", a.Post("/api/v1/leads", new { name = name(), statusId = Guid.NewGuid() }));
        await Expect(HttpStatusCode.BadRequest, "tag", a.Post("/api/v1/leads", new { name = name(), tagIds = new[] { bd.Tag.Id } }));
        await Expect(HttpStatusCode.BadRequest, "owner", a.Post("/api/v1/leads", new { name = name(), ownerUserId = W.BAdmin.Id }));
        await Expect(HttpStatusCode.BadRequest, "update category", a.Put($"/api/v1/leads/{ad.Lead.Id}", new { name = ad.Lead.Name, city = "Riyadh", categoryId = bd.Category.Id }));
        await Expect(HttpStatusCode.BadRequest, "set tags", a.Put($"/api/v1/leads/{ad.Lead.Id}/tags", new { tagIds = new[] { bd.Tag.Id } }));
        await Expect(HttpStatusCode.BadRequest, "move own lead to B's stage", a.Put($"/api/v1/leads/{ad.Lead.Id}/stage", new { stageId = bd.Pipeline.Stages[0].Id }));
        await Expect(HttpStatusCode.BadRequest, "own opportunity to B's stage", a.Put($"/api/v1/opportunities/{ad.Opportunity.Id}/stage", new { stageId = bd.Pipeline.Stages[0].Id }));
        await Expect(HttpStatusCode.BadRequest, "import default category", a.Post($"/api/v1/imports/{ad.Upload.Job.Id}/run", new { mapping = Crm.MappingFrom(ad.Upload), defaultCategoryId = bd.Category.Id }));
        await Expect(HttpStatusCode.BadRequest, "import default owner", a.Post($"/api/v1/imports/{ad.Upload.Job.Id}/run", new { mapping = Crm.MappingFrom(ad.Upload), defaultOwnerUserId = W.BAdmin.Id }));
        Assert.Equal(ImportStatus.Uploaded, (await Crm.Ok<ImportJobDto>(await a.Get($"/api/v1/imports/{ad.Upload.Job.Id}"))).Status);   // the refused runs did not consume it
    }

    [RequiresMySqlFact]
    public async Task A_spoofed_tenant_id_is_rejected_everywhere_a_client_could_send_one()
    {
        var (a, _, bd, ad) = await SetupAsync();
        var bTenant = W.B.Id;
        await Expect(HttpStatusCode.BadRequest, "body", a.Post("/api/v1/leads", new { name = Crm.Name(Crm.Token()), tenantId = bTenant }));
        await Expect(HttpStatusCode.BadRequest, "update body", a.Put($"/api/v1/leads/{ad.Lead.Id}", new { name = ad.Lead.Name, tenantId = bTenant }));
        await Expect(HttpStatusCode.BadRequest, "contact body", a.Post($"/api/v1/leads/{ad.Lead.Id}/contacts", new { name = "x", tenantId = bTenant }));
        await Expect(HttpStatusCode.BadRequest, "consent body", a.Post($"/api/v1/contacts/{ad.Contact.Id}/consent", new { channel = "Sms", purpose = "Marketing", status = "Revoked", source = "x", tenantId = bTenant }));
        await Expect(HttpStatusCode.BadRequest, "tag body", a.Post("/api/v1/tags", new { name = Crm.Token("x"), tenantId = bTenant }));
        await Expect(HttpStatusCode.BadRequest, "bulk body", a.Post("/api/v1/leads/bulk/archive", new { leadIds = new[] { ad.Lead.Id }, tenantId = bTenant }));
        await Expect(HttpStatusCode.BadRequest, "query", a.Get($"/api/v1/leads?tenantId={bTenant}"));
        await Expect(HttpStatusCode.BadRequest, "query on export", a.Get($"/api/v1/leads/export?tenantId={bTenant}"));
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/leads"); req.Headers.Add("X-Tenant-Id", bTenant.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Http.SendAsync(req)).StatusCode);
        // its own tenant id is harmless (it only repeats what the token says)
        Assert.Equal(HttpStatusCode.OK, (await a.Get($"/api/v1/leads?tenantId={W.A.Id}")).StatusCode);
        Assert.Empty((await Crm.Page<LeadListItemDto>(await a.Get($"/api/v1/leads?search={bd.Token}"))).Items);
    }
}

/// <summary>The SaveChanges guard and query filters on every CRM entity, independent of any service code.</summary>
[Collection("app")]
public class CrmDatabaseIsolationTests(AppFixture app)
{
    private TestWorld W => app.World;
    private static readonly DateTime Now = DateTime.UtcNow;

    private sealed class Ctx(IServiceScope scope) : IDisposable
    {
        public AppDbContext Db { get; } = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        public ITenantContextAccessor Accessor { get; } = scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>();
        public void Dispose() => scope.Dispose();
    }

    private Ctx Open(Guid? tenantId = null)
    {
        var c = new Ctx(W.Services.CreateScope());
        if (tenantId is { } t) c.Accessor.SetTenant(t);
        return c;
    }

    private async Task<(SeededCrm A, SeededCrm B, Guid StageA, Guid StatusA, Guid StatusB, Guid RuleSetB, Guid ImportJobB)> SeedAsync()
    {
        var a = await ApiSession.LoginAsync(app, W.ASalesManager.Email, W.ASalesManager.Password);
        var b = await ApiSession.LoginAsync(app, W.BSalesManager.Email, W.BSalesManager.Password);
        var am = await ApiSession.LoginAsync(app, W.AMarketing.Email, W.AMarketing.Password);
        var bm = await ApiSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password);
        var da = await SeededCrm.CreateAsync(a, am); var db = await SeededCrm.CreateAsync(b, bm);
        var ids = await W.WithDb(async d => new
        {
            StatusA = await d.LeadStatuses.IgnoreQueryFilters().Where(s => s.TenantId == W.A.Id).Select(s => s.Id).FirstAsync(),
            StatusB = await d.LeadStatuses.IgnoreQueryFilters().Where(s => s.TenantId == W.B.Id).Select(s => s.Id).FirstAsync(),
            RuleSetB = await d.ScoringRuleSets.IgnoreQueryFilters().Where(s => s.TenantId == W.B.Id).Select(s => s.Id).FirstAsync(),
        });
        var stageA = (await Crm.Ok<List<PipelineDto>>(await a.Get("/api/v1/pipelines"))).Single(p => p.IsDefault).Stages[0].Id;
        return (da, db, stageA, ids.StatusA, ids.StatusB, ids.RuleSetB, db.Upload.Job.Id);
    }

    [RequiresMySqlFact]
    public async Task Every_crm_table_returns_nothing_without_a_tenant_and_only_own_rows_with_one()
    {
        await SeedAsync();
        using (var none = Open())
        {
            Assert.Equal(0, await none.Db.Leads.CountAsync());
            Assert.Equal(0, await none.Db.Contacts.CountAsync());
            Assert.Equal(0, await none.Db.LeadSources.CountAsync());
            Assert.Equal(0, await none.Db.LeadActivities.CountAsync());
            Assert.Equal(0, await none.Db.LeadNotes.CountAsync());
            Assert.Equal(0, await none.Db.LeadTags.CountAsync());
            Assert.Equal(0, await none.Db.LeadTagMaps.CountAsync());
            Assert.Equal(0, await none.Db.LeadScores.CountAsync());
            Assert.Equal(0, await none.Db.LeadCategories.CountAsync());
            Assert.Equal(0, await none.Db.LeadStatuses.CountAsync());
            Assert.Equal(0, await none.Db.Pipelines.CountAsync());
            Assert.Equal(0, await none.Db.PipelineStages.CountAsync());
            Assert.Equal(0, await none.Db.Opportunities.CountAsync());
            Assert.Equal(0, await none.Db.OpportunityActivities.CountAsync());
            Assert.Equal(0, await none.Db.ConsentRecords.CountAsync());
            Assert.Equal(0, await none.Db.SuppressionEntries.CountAsync());
            Assert.Equal(0, await none.Db.ScoringRuleSets.CountAsync());
            Assert.Equal(0, await none.Db.ScoringRules.CountAsync());
            Assert.Equal(0, await none.Db.ImportJobs.CountAsync());
            Assert.Equal(0, await none.Db.ImportRowErrors.CountAsync());
        }
        using var a = Open(W.A.Id);
        Assert.True(await a.Db.Leads.CountAsync() > 0);
        Assert.All(await a.Db.Leads.ToListAsync(), l => Assert.Equal(W.A.Id, l.TenantId));
        Assert.All(await a.Db.Contacts.ToListAsync(), l => Assert.Equal(W.A.Id, l.TenantId));
        Assert.All(await a.Db.ConsentRecords.ToListAsync(), l => Assert.Equal(W.A.Id, l.TenantId));
        Assert.All(await a.Db.SuppressionEntries.ToListAsync(), l => Assert.Equal(W.A.Id, l.TenantId));
        Assert.All(await a.Db.ImportJobs.ToListAsync(), l => Assert.Equal(W.A.Id, l.TenantId));
        Assert.All(await a.Db.PipelineStages.ToListAsync(), l => Assert.Equal(W.A.Id, l.TenantId));
    }

    [RequiresMySqlFact]
    public async Task Cross_tenant_foreign_keys_are_rejected_for_every_new_entity()
    {
        var s = await SeedAsync();
        var (a, b) = (s.A, s.B);
        var leadProfile = new LeadProfile("Guard Test " + Guid.NewGuid().ToString("N")[..6], null, null, null, null, null, null, null, null, 50);

        async Task<CrossTenantViolationException> Rejects(string what, Action<AppDbContext> add)
        {
            using var c = Open(W.A.Id);
            add(c.Db);
            var ex = await Assert.ThrowsAsync<CrossTenantViolationException>(() => c.Db.SaveChangesAsync());
            return ex;
        }

        await Rejects("lead → category of B", d => d.Leads.Add(Lead.Create(W.A.Id, leadProfile with { CategoryId = b.Category.Id }, b.Pipeline.Id, b.Pipeline.Stages[0].Id, s.StatusB, null, LeadSourceType.Manual, Now, null)));
        await Rejects("lead → stage of B", d => d.Leads.Add(Lead.Create(W.A.Id, leadProfile, a.Lead.PipelineId, b.Pipeline.Stages[0].Id, s.StatusA, null, LeadSourceType.Manual, Now, null)));
        await Rejects("lead → status of B", d => d.Leads.Add(Lead.Create(W.A.Id, leadProfile, a.Lead.PipelineId, s.StageA, s.StatusB, null, LeadSourceType.Manual, Now, null)));
        await Rejects("lead → pipeline of B", d => d.Leads.Add(Lead.Create(W.A.Id, leadProfile, b.Pipeline.Id, s.StageA, s.StatusA, null, LeadSourceType.Manual, Now, null)));
        await Rejects("contact → lead of B", d => d.Contacts.Add(Contact.Create(W.A.Id, b.Lead.Id, "x", null, null, null, "x@example.com", DataOrigin.Public, false, Now, null)));
        await Rejects("source → lead of B", d => d.LeadSources.Add(LeadSource.Create(W.A.Id, b.Lead.Id, LeadSourceType.Manual, null, null, null, null, Now)));
        await Rejects("note → lead of B", d => d.LeadNotes.Add(LeadNote.Create(W.A.Id, b.Lead.Id, "x", null, Now)));
        await Rejects("activity → lead of B", d => d.LeadActivities.Add(LeadActivity.Create(W.A.Id, b.Lead.Id, LeadActivityType.Updated, "x", null, null, Now)));
        await Rejects("tag map: A lead, B tag", d => d.LeadTagMaps.Add(LeadTagMap.Create(W.A.Id, a.Lead.Id, b.Tag.Id, Now)));
        await Rejects("tag map: B lead, A tag", d => d.LeadTagMaps.Add(LeadTagMap.Create(W.A.Id, b.Lead.Id, a.Tag.Id, Now)));
        await Rejects("score → lead of B", d => d.LeadScores.Add(LeadScore.Create(W.A.Id, b.Lead.Id, 10, 1, "{}", Now)));
        await Rejects("consent → contact of B", d => d.ConsentRecords.Add(ConsentRecord.Create(W.A.Id, b.Contact.Id, ConsentChannel.Sms, ConsentPurpose.Marketing, ConsentStatus.Granted, "x", null, null, Now, Now, null)));
        await Rejects("opportunity → lead of B", d => d.Opportunities.Add(Opportunity.Create(W.A.Id, b.Lead.Id, s.StageA, "x", 1, "USD", null, null, Now)));
        await Rejects("opportunity → stage of B", d => d.Opportunities.Add(Opportunity.Create(W.A.Id, a.Lead.Id, b.Pipeline.Stages[0].Id, "x", 1, "USD", null, null, Now)));
        await Rejects("opportunity activity → opportunity of B", d => d.OpportunityActivities.Add(OpportunityActivity.Create(W.A.Id, b.Opportunity.Id, "x", "x", null, Now)));
        await Rejects("stage → pipeline of B", d => d.PipelineStages.Add(PipelineStage.Create(W.A.Id, b.Pipeline.Id, "Intruder", 999, StageKind.Open, Now)));
        await Rejects("scoring rule → rule set of B", d => d.ScoringRules.Add(ScoringRule.Create(W.A.Id, s.RuleSetB, ScoreDimension.WebsiteAvailability, "present", 5, null, true)));
        await Rejects("import error → import of B", d => d.ImportRowErrors.Add(ImportRowError.Validation(W.A.Id, s.ImportJobB, 2, null, "x", null)));
        await Rejects("import error duplicate → lead of B", d => d.ImportRowErrors.Add(ImportRowError.Duplicate(W.A.Id, a.Upload.Job.Id, 2, "x", "{}", b.Lead.Id, DuplicateResolution.Pending)));

        // same-unit-of-work principals, even inside a platform scope
        using (var c = Open())
        {
            using var _ = c.Accessor.BeginPlatformScope();
            var lead = Lead.Create(W.A.Id, leadProfile, a.Lead.PipelineId, s.StageA, s.StatusA, null, LeadSourceType.Manual, Now, null);
            c.Db.Leads.Add(lead);
            c.Db.Contacts.Add(Contact.Create(W.B.Id, lead.Id, "x", null, null, null, "x@example.com", DataOrigin.Public, false, Now, null));   // B's contact on A's (tracked) lead
            await Assert.ThrowsAsync<CrossTenantViolationException>(() => c.Db.SaveChangesAsync());
        }
    }

    [RequiresMySqlFact]
    public async Task Writes_for_another_tenant_and_tenant_id_changes_are_rejected()
    {
        var s = await SeedAsync();
        using (var c = Open(W.A.Id))
        {
            c.Db.Leads.Add(Lead.Create(W.B.Id, new LeadProfile("Wrong tenant " + Guid.NewGuid().ToString("N")[..6], null, null, null, null, null, null, null, null, 50), s.B.Lead.PipelineId, s.B.Pipeline.Stages[0].Id, s.StatusB, null, LeadSourceType.Manual, Now, null));
            await Assert.ThrowsAsync<CrossTenantViolationException>(() => c.Db.SaveChangesAsync());
        }
        using (var c = Open(W.A.Id))
        {
            var lead = await c.Db.Leads.FirstAsync(l => l.Id == s.A.Lead.Id);
            c.Db.Entry(lead).Property(l => l.TenantId).CurrentValue = W.B.Id;
            await Assert.ThrowsAsync<CrossTenantViolationException>(() => c.Db.SaveChangesAsync());
        }
        using (var c = Open(W.A.Id))
        {
            Assert.Null(await c.Db.Leads.FirstOrDefaultAsync(l => l.Id == s.B.Lead.Id));                 // B's row is invisible to A: nothing to update or delete
            Assert.Null(await c.Db.SuppressionEntries.FirstOrDefaultAsync(l => l.Id == s.B.Suppression.Id));
            Assert.Null(await c.Db.ConsentRecords.FirstOrDefaultAsync(l => l.Id == s.B.Consent.Id));
        }
        using (var none = Open())
        {
            none.Db.SuppressionEntries.Add(SuppressionEntry.Create(W.A.Id, SuppressionChannel.Email, "x@example.com", SuppressionReason.OptOut, null, null, Now));
            await Assert.ThrowsAsync<TenantContextMissingException>(() => none.Db.SaveChangesAsync());   // writes without any tenant fail closed
        }
    }

    [RequiresMySqlFact]
    public async Task Unique_constraints_are_scoped_per_tenant()
    {
        // the same tag/category/suppression value/pipeline name can exist in two tenants
        var a = await ApiSession.LoginAsync(app, W.AAdmin.Email, W.AAdmin.Password);
        var b = await ApiSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password);
        var token = Crm.Token();
        foreach (var s in new[] { a, b })
        {
            Assert.Equal(HttpStatusCode.Created, (await s.Post("/api/v1/tags", new { name = "shared-" + token })).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await s.Post("/api/v1/categories", new { name = "Shared " + token })).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await s.Post("/api/v1/suppression", new { channel = "Email", value = $"{token}@shared.example", reason = "OptOut" })).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await s.Post("/api/v1/pipelines", new { name = "Shared " + token })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Conflict, (await a.Post("/api/v1/tags", new { name = "shared-" + token })).StatusCode);
    }
}
