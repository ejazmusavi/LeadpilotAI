using System.Net;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Crm;

namespace LeadPilot.IntegrationTests;

/// <summary>Tenant-wide CRM configuration: scoring rules (versioned), pipelines/stages, categories and statuses. Runs in throw-away tenants so it cannot disturb other tests.</summary>
[Collection("app")]
public class CrmConfigApiTests(AppFixture app)
{
    private TestWorld W => app.World;

    [RequiresMySqlFact]
    public async Task New_tenants_get_the_default_pipeline_statuses_and_scoring_rules()
    {
        var (tenant, admin, _) = await W.NewTenantAsync("def");
        var a = await ApiSession.LoginAsync(app, admin.Email, admin.Password);
        var pipelines = await Crm.Ok<List<PipelineDto>>(await a.Get("/api/v1/pipelines"));
        var p = Assert.Single(pipelines);
        Assert.True(p.IsDefault);
        Assert.Equal(["New", "Researched", "Contact Eligible", "Contacted", "Responded", "Interested", "Qualified", "Demo Scheduled", "Demo Completed", "Trial", "Negotiation", "Won", "Lost", "Suppressed"], p.Stages.Select(x => x.Name));
        Assert.Equal(Enumerable.Range(1, 14).Select(i => i * 10), p.Stages.Select(x => x.Position));
        Assert.Equal(["Open", "Working", "Nurturing", "Disqualified"], (await Crm.Ok<List<StatusDto>>(await a.Get("/api/v1/lead-statuses"))).Select(x => x.Name));
        var rules = await Crm.Ok<ScoringRuleSetDto>(await a.Get("/api/v1/scoring/rules"));
        Assert.Equal((1, true, 9), (rules.Version, rules.IsActive, rules.Rules.Count));
        // a second tenant has its own copies (different ids)
        var b = await Crm.Ok<List<PipelineDto>>(await (await ApiSession.LoginAsync(app, W.BAdmin.Email, W.BAdmin.Password)).Get("/api/v1/pipelines"));
        Assert.NotEqual(p.Id, b.Single(x => x.IsDefault).Id);
        Assert.Equal(14, b.Single(x => x.IsDefault).Stages.Count);
    }

    [RequiresMySqlFact]
    public async Task Scoring_rules_are_versioned_tenant_data_and_recalculation_is_explicit()
    {
        var (_, admin, ro) = await W.NewTenantAsync("sc");
        var a = await ApiSession.LoginAsync(app, admin.Email, admin.Password);
        var category = await Crm.Ok<CategoryDto>(await a.Post("/api/v1/categories", new { name = "School" }));
        var lead = await Crm.CreateLead(a, categoryId: category.Id, website: "https://scoring.example");
        Assert.Equal(45, lead.Score);                                   // website 10 + valid phone 15 + e-mail 10 + named contact 10 (no default rule for categories)
        Assert.Equal(1, lead.ScoreRuleVersion);

        var v2 = await a.Post("/api/v1/scoring/rules", new
        {
            note = "category focus",
            rules = new object[]
            {
                new { dimension = "BusinessCategory", target = "school", points = 30, description = "Schools are our focus" },
                new { dimension = "Location", target = "City=Riyadh", points = 20 },
            }
        });
        Assert.Equal(HttpStatusCode.Created, v2.StatusCode);
        var published = await ApiSession.Json<ScoringRuleSetDto>(v2);
        Assert.Equal(2, published.Version);
        Assert.Equal(2, published.Rules.Count);

        var versions = await Crm.Ok<List<ScoringRuleSetDto>>(await a.Get("/api/v1/scoring/rules/versions"));
        Assert.Equal([2, 1], versions.Select(v => v.Version));
        Assert.Equal([true, false], versions.Select(v => v.IsActive));          // exactly one active version
        Assert.Equal(9, versions[1].Rules.Count);                                 // the old version is kept as it was

        Assert.Equal(45, (await Crm.Ok<LeadDto>(await a.Get($"/api/v1/leads/{lead.Id}"))).Score);   // unchanged until recalculated

        var rescored = await Crm.Ok<RescoreResult>(await a.Post("/api/v1/scoring/rescore"));
        Assert.Equal(2, rescored.RuleVersion);
        Assert.True(rescored.Processed >= 1 && rescored.Changed >= 1);
        var after = await Crm.Ok<LeadDto>(await a.Get($"/api/v1/leads/{lead.Id}"));
        Assert.Equal(50, after.Score);                                            // category 30 + city 20
        Assert.Equal(2, after.ScoreRuleVersion);
        Assert.Equal(2, after.ScoreBreakdown.Count);

        var history = await Crm.Ok<List<ScoreHistoryDto>>(await a.Get($"/api/v1/leads/{lead.Id}/score-history"));
        Assert.Equal([2, 1], history.Select(h => h.RuleVersion));
        Assert.Equal([50, 45], history.Select(h => h.Score));

        // new leads use the active version straight away; a lead's own rescore works too
        var next = await Crm.CreateLead(a, categoryId: category.Id);
        Assert.Equal((50, 2), (next.Score, next.ScoreRuleVersion));
        Assert.Equal(HttpStatusCode.OK, (await a.Post($"/api/v1/leads/{lead.Id}/rescore")).StatusCode);

        // invalid rules are rejected as a whole and publish nothing
        foreach (var bad in new object[]
                 {
                     new { dimension = "Location", target = "Planet=Mars", points = 5 },
                     new { dimension = "WebsiteAvailability", target = "present", points = 101 },
                     new { dimension = "Activity", target = "recent:0", points = 5 },
                     new { dimension = "ContactQuality", target = "", points = 5 },
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/scoring/rules", new { rules = new[] { bad } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/scoring/rules", new { rules = Enumerable.Range(0, 101).Select(_ => new { dimension = "WebsiteAvailability", target = "present", points = 1 }) })).StatusCode);
        Assert.Equal(2, (await Crm.Ok<ScoringRuleSetDto>(await a.Get("/api/v1/scoring/rules"))).Version);

        // an empty rule set is allowed (everything scores 0)
        Assert.Equal(HttpStatusCode.Created, (await a.Post("/api/v1/scoring/rules", new { rules = Array.Empty<object>() })).StatusCode);

        var r = await ApiSession.LoginAsync(app, ro.Email, ro.Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Get("/api/v1/scoring/rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Post("/api/v1/scoring/rescore")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Pipelines_and_stages_are_configurable_and_protected_while_in_use()
    {
        var (_, admin, ro) = await W.NewTenantAsync("pl");
        var a = await ApiSession.LoginAsync(app, admin.Email, admin.Password);
        var def = (await Crm.Ok<List<PipelineDto>>(await a.Get("/api/v1/pipelines"))).Single();
        var lead = await Crm.CreateLead(a);

        // create + duplicate name
        var partners = await Crm.Ok<PipelineDto>(await a.Post("/api/v1/pipelines", new { name = "Partners" }));
        Assert.False(partners.IsDefault);
        Assert.Equal(14, partners.Stages.Count);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Post("/api/v1/pipelines", new { name = "Partners" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/pipelines", new { name = "" })).StatusCode);
        Assert.Equal("Partners 2", (await Crm.Ok<PipelineDto>(await a.Put($"/api/v1/pipelines/{partners.Id}", new { name = "Partners 2" }))).Name);

        // stages: add, rename, change kind, reorder, delete
        var withStage = await Crm.Ok<PipelineDto>(await a.Post($"/api/v1/pipelines/{partners.Id}/stages", new { name = "Onboarding", kind = "Open" }));
        var onboarding = withStage.Stages.Last();
        Assert.Equal("Onboarding", onboarding.Name);
        Assert.Equal(15, withStage.Stages.Count);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Post($"/api/v1/pipelines/{partners.Id}/stages", new { name = "onboarding" })).StatusCode);
        var renamed = await Crm.Ok<PipelineDto>(await a.Put($"/api/v1/pipelines/{partners.Id}/stages/{onboarding.Id}", new { name = "Kick-off", kind = "Won" }));
        Assert.Equal((StageKind.Won, "Kick-off"), (renamed.Stages.Last().Kind, renamed.Stages.Last().Name));
        var moved = await Crm.Ok<PipelineDto>(await a.Post($"/api/v1/pipelines/{partners.Id}/stages/{onboarding.Id}/move", new { delta = -14 }));
        Assert.Equal("Kick-off", moved.Stages.First().Name);
        Assert.Equal(Enumerable.Range(1, 15).Select(i => i * 10), moved.Stages.Select(x => x.Position));
        Assert.Equal(14, (await Crm.Ok<PipelineDto>(await a.Delete($"/api/v1/pipelines/{partners.Id}/stages/{onboarding.Id}"))).Stages.Count);

        // a stage that holds leads cannot be deleted
        var newStage = def.Stages.Single(s => s.Name == "New");
        var blocked = await a.Delete($"/api/v1/pipelines/{def.Id}/stages/{newStage.Id}");
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("1 lead", await Crm.ProblemDetail(blocked));

        // leads move only inside their own pipeline
        var partnerStage = partners.Stages[3];
        var cross = await a.Put($"/api/v1/leads/{lead.Id}/stage", new { stageId = partnerStage.Id });
        Assert.Equal(HttpStatusCode.BadRequest, cross.StatusCode);
        Assert.Contains("different pipeline", await Crm.ProblemDetail(cross));

        // default switching: new leads follow the default; the default cannot be deactivated
        Assert.True((await Crm.Ok<PipelineDto>(await a.Post($"/api/v1/pipelines/{partners.Id}/default"))).IsDefault);
        var inPartners = await Crm.CreateLead(a);
        Assert.Equal(partners.Id, inPartners.PipelineId);
        Assert.Equal("Kick-off" == inPartners.StageName ? "Kick-off" : "New", inPartners.StageName);
        var lists = await Crm.Ok<List<PipelineDto>>(await a.Get("/api/v1/pipelines"));
        Assert.Single(lists, p => p.IsDefault);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Put($"/api/v1/pipelines/{partners.Id}/active", new { active = false })).StatusCode);   // in use
        Assert.Equal(HttpStatusCode.OK, (await a.Post($"/api/v1/pipelines/{def.Id}/default")).StatusCode);
        var empty = await Crm.Ok<PipelineDto>(await a.Post("/api/v1/pipelines", new { name = "Empty" }));
        Assert.False((await Crm.Ok<PipelineDto>(await a.Put($"/api/v1/pipelines/{empty.Id}/active", new { active = false }))).IsActive);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post($"/api/v1/pipelines/{empty.Id}/default")).StatusCode);   // inactive cannot be default
        Assert.Equal(HttpStatusCode.OK, (await a.Put($"/api/v1/pipelines/{empty.Id}/active", new { active = true })).StatusCode);

        // board shows every stage with counts
        var board = await Crm.Ok<BoardDto>(await a.Get($"/api/v1/leads/board?pipelineId={def.Id}"));
        Assert.Equal(14, board.Columns.Count);
        Assert.Equal(1, board.Columns.Single(c => c.Stage.Name == "New").Stage.LeadCount);
        Assert.Contains(board.Columns.Single(c => c.Stage.Name == "New").Cards, c => c.Id == lead.Id);

        var r = await ApiSession.LoginAsync(app, ro.Email, ro.Password);
        Assert.Equal(HttpStatusCode.OK, (await r.Get("/api/v1/pipelines")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Post($"/api/v1/pipelines/{def.Id}/stages", new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await r.Delete($"/api/v1/pipelines/{def.Id}/stages/{newStage.Id}")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Categories_and_statuses_are_tenant_configurable()
    {
        var (_, admin, _) = await W.NewTenantAsync("cat");
        var a = await ApiSession.LoginAsync(app, admin.Email, admin.Password);
        var cat = await Crm.Ok<CategoryDto>(await a.Post("/api/v1/categories", new { name = "Dental clinic" }));
        Assert.Equal("Dental clinic 2", (await Crm.Ok<CategoryDto>(await a.Put($"/api/v1/categories/{cat.Id}", new { name = "Dental clinic 2" }))).Name);
        var lead = await Crm.CreateLead(a, categoryId: cat.Id);
        Assert.Equal("Dental clinic 2", lead.CategoryName);
        Assert.False((await Crm.Ok<CategoryDto>(await a.Put($"/api/v1/categories/{cat.Id}/active", new { active = false }))).IsActive);
        var res = await a.Post("/api/v1/leads", new { name = Crm.Name(Crm.Token()), categoryId = cat.Id });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);                  // inactive categories cannot be chosen for new leads
        Assert.Equal(HttpStatusCode.OK, (await a.Put($"/api/v1/leads/{lead.Id}", new { name = lead.Name, categoryId = cat.Id, city = lead.City })).StatusCode);   // but existing leads may keep theirs
        Assert.Contains(await Crm.Ok<List<CategoryDto>>(await a.Get("/api/v1/categories")), c => c.Id == cat.Id && !c.IsActive && c.LeadCount == 1);

        var status = await Crm.Ok<StatusDto>(await a.Post("/api/v1/lead-statuses", new { name = "Pilot customer", isClosed = false }));
        Assert.Equal(HttpStatusCode.Conflict, (await a.Post("/api/v1/lead-statuses", new { name = "pilot customer" })).StatusCode);
        var set = await Crm.Ok<LeadDto>(await a.Put($"/api/v1/leads/{lead.Id}", new { name = lead.Name, categoryId = cat.Id, city = lead.City, statusId = status.Id }));
        Assert.Equal("Pilot customer", set.StatusName);
        Assert.True((await Crm.Ok<StatusDto>(await a.Put($"/api/v1/lead-statuses/{status.Id}", new { name = "Pilot customer", isClosed = true }))).IsClosed);
        var open = (await Crm.Ok<List<StatusDto>>(await a.Get("/api/v1/lead-statuses"))).Single(s => s.IsDefault);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Put($"/api/v1/lead-statuses/{open.Id}/active", new { active = false })).StatusCode);   // the default stays active
        Assert.False((await Crm.Ok<StatusDto>(await a.Put($"/api/v1/lead-statuses/{status.Id}/active", new { active = false }))).IsActive);
    }

    [RequiresMySqlFact]
    public async Task Local_phone_numbers_need_the_tenant_default_country_code()
    {
        var (_, admin, _) = await W.NewTenantAsync("cc");
        var a = await ApiSession.LoginAsync(app, admin.Email, admin.Password);
        var t = Crm.Token();
        // without a default country code a local number is ambiguous: rejected, never guessed
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/leads", new { name = $"{t} A", contacts = new[] { new { name = "x", phone = "0501234567" } } })).StatusCode);
        var imp = await Crm.RunImport(a, await Crm.Upload(a, "local.csv", Crm.CsvBytes("Name,Phone", $"{t} B,0501234567")));
        Assert.Equal((0, 1), (imp.CreatedCount, imp.ErrorCount));

        Assert.Equal(HttpStatusCode.OK, (await a.Put("/api/v1/settings/Crm.DefaultCountryCode", new { value = "966", isSecret = false })).StatusCode);
        var lead = await Crm.Ok<LeadDto>(await a.Post("/api/v1/leads", new { name = $"{t} C", contacts = new[] { new { name = "x", phone = "0501234567" } } }));
        var contact = (await Crm.Ok<List<ContactDto>>(await a.Get($"/api/v1/leads/{lead.Id}/contacts"))).Single();
        Assert.Equal("+966501234567", contact.Phone);
        Assert.Equal("0501234567", contact.PhoneRaw);                      // what was typed is kept for display
        var imp2 = await Crm.RunImport(a, await Crm.Upload(a, "local2.csv", Crm.CsvBytes("Name,Phone", $"{t} D,0509999999")));
        Assert.Equal(1, imp2.CreatedCount);
        var sup = await Crm.Ok<SuppressionDto>(await a.Post("/api/v1/suppression", new { channel = "Sms", value = "0508888888", reason = "OptOut" }));
        Assert.Equal("+966508888888", sup.Value);
    }

    [RequiresMySqlFact]
    public async Task Swagger_documents_the_crm_endpoints_with_descriptions()
    {
        var json = await (await ApiSession.Anonymous(app).Get("/swagger/v1/swagger.json")).Content.ReadAsStringAsync();
        foreach (var path in new[] { "/api/v1/leads", "/api/v1/leads/{id}/stage", "/api/v1/leads/bulk/assign", "/api/v1/imports/{id}/run", "/api/v1/suppression", "/api/v1/contacts/{contactId}/consent",
                     "/api/v1/audience/eligible-contacts", "/api/v1/scoring/rules", "/api/v1/pipelines/{id}/stages", "/api/v1/leads/export" })
            Assert.Contains($"\"{path}\"", json);
        Assert.Contains("Moves the lead to another stage of its pipeline", json);          // XML documentation comments are published
        Assert.Contains("append-only", json);
        Assert.Contains("\"Bearer\"", json);
    }
}
