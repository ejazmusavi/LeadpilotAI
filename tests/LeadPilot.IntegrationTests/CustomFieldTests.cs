using System.Net;
using LeadPilot.Application.Common;
using LeadPilot.Application.Config;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Config;
using LeadPilot.Domain.Crm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.IntegrationTests;

[Collection("app")]
public class CustomFieldApiTests(AppFixture app)
{
    private async Task<(TestUser Admin, ApiSession Api)> Fresh(string p = "cf")
    {
        var (_, admin, _) = await app.World.NewTenantAsync(p);
        return (admin, await ApiSession.LoginAsync(app, admin.Email, admin.Password));
    }

    private static Task<HttpResponseMessage> Field(ApiSession s, string key, string type, object? extra = null, CustomEntityType entity = CustomEntityType.Lead)
    {
        var body = new Dictionary<string, object?> { ["entityType"] = entity.ToString(), ["key"] = key, ["label"] = "Label " + key, ["type"] = type };
        foreach (var p in extra?.GetType().GetProperties() ?? []) body[p.Name] = p.GetValue(extra);
        return s.Post("/api/v1/custom-fields", body);
    }

    private static async Task<string> Err(HttpResponseMessage r) => await Crm.ProblemDetail(r);

    [RequiresMySqlFact]
    public async Task Fields_are_created_validated_and_values_are_checked_by_type()
    {
        var (_, api) = await Fresh();
        Assert.Equal(HttpStatusCode.Created, (await Field(api, "rooms", "Number", new { minValue = 0, maxValue = 500, isFilterable = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Field(api, "board", "Select", new { options = new[] { "Alpha", "Beta" }, isRequired = false, isFilterable = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Field(api, "perks", "MultiSelect", new { options = new[] { "Pool", "Spa", "Gym" }, isFilterable = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Field(api, "opened", "Date", new { isFilterable = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Field(api, "vip", "Bool", new { isFilterable = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Field(api, "code", "Text", new { pattern = "[A-Z]{3}\\d{2}", maxValue = 6, isFilterable = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Field(api, "rooms", "Number")).StatusCode);                                     // duplicate key
        Assert.Equal(HttpStatusCode.BadRequest, (await Field(api, "Bad Key", "Text")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Field(api, "sel", "Select", new { options = new[] { "only" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Field(api, "bad_pat", "Text", new { pattern = "(?<=a)b" })).StatusCode);

        var lead = await Crm.CreateLead(api);
        string Url() => $"/api/v1/custom-fields/values/Lead/{lead.Id}";
        async Task<HttpResponseMessage> Set(params (string, string?)[] v) => await api.Put(Url(), new { values = v.ToDictionary(x => x.Item1, x => x.Item2) });

        var ok = await Set(("rooms", "120"), ("board", "beta"), ("perks", "Pool, gym"), ("opened", "2024-05-01"), ("vip", "yes"), ("code", "ABC12"));
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
        var values = await ApiSession.Json<List<CustomFieldValueDto>>(await api.Get(Url()));
        Assert.Equal("Beta", values.Single(v => v.Field.Key == "board").Display); Assert.Equal("Pool, Gym", values.Single(v => v.Field.Key == "perks").Display);
        Assert.Equal("Yes", values.Single(v => v.Field.Key == "vip").Display); Assert.Equal("120", values.Single(v => v.Field.Key == "rooms").Raw);

        foreach (var (key, bad) in new[] { ("rooms", "abc"), ("rooms", "501"), ("rooms", "-1"), ("board", "Gamma"), ("perks", "Pool, Sauna"), ("opened", "01/05/2024"), ("vip", "maybe"), ("code", "abc12"), ("code", "ABC1234") })
        {
            var r = await Set((key, bad));
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        }
        Assert.Equal("120", (await ApiSession.Json<List<CustomFieldValueDto>>(await api.Get(Url()))).Single(v => v.Field.Key == "rooms").Raw);   // a refused save changed nothing
        Assert.Equal(HttpStatusCode.BadRequest, (await Set(("nope", "x"))).StatusCode);                                                          // unknown field
        Assert.Equal(HttpStatusCode.BadRequest, (await Set(("rooms", "5"), ("board", "Gamma"))).StatusCode);                                     // all or none
        Assert.Equal("120", (await ApiSession.Json<List<CustomFieldValueDto>>(await api.Get(Url()))).Single(v => v.Field.Key == "rooms").Raw);
        Assert.True((await Set(("vip", null))).IsSuccessStatusCode);                                                                              // clearing
        Assert.Null((await ApiSession.Json<List<CustomFieldValueDto>>(await api.Get(Url()))).Single(v => v.Field.Key == "vip").Raw);
    }

    [RequiresMySqlFact]
    public async Task Key_type_and_record_type_are_fixed_required_fields_are_enforced_and_deactivation_keeps_values()
    {
        var (_, api) = await Fresh();
        var created = await ApiSession.Json<CustomFieldDto>(await Field(api, "must", "Text", new { isRequired = true }));
        var lead = await Crm.CreateLead(api);
        // required: saving the record's custom fields without a value is refused
        var refused = await api.Put($"/api/v1/custom-fields/values/Lead/{lead.Id}", new { values = new Dictionary<string, string?>() });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode); Assert.Contains("required", await Err(refused));
        Assert.True((await api.Put($"/api/v1/custom-fields/values/Lead/{lead.Id}", new { values = new Dictionary<string, string?> { ["must"] = "present" } })).IsSuccessStatusCode);
        // immutable parts
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/v1/custom-fields/{created.Id}", new { entityType = "Lead", key = "other", label = "x", type = "Text" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/v1/custom-fields/{created.Id}", new { entityType = "Lead", key = "must", label = "x", type = "Number" })).StatusCode);
        var edited = await api.Put($"/api/v1/custom-fields/{created.Id}", new { entityType = "Lead", key = "must", label = "Renamed", type = "Text", isFilterable = true });
        Assert.True(edited.IsSuccessStatusCode, await edited.Content.ReadAsStringAsync());
        // stale edit
        Assert.Equal(HttpStatusCode.Conflict, (await api.Put($"/api/v1/custom-fields/{created.Id}", new { entityType = "Lead", key = "must", label = "Again", type = "Text", expectedVersion = created.Version })).StatusCode);
        // deactivate: hidden from forms, value kept
        Assert.True((await api.Post($"/api/v1/custom-fields/{created.Id}/deactivate")).IsSuccessStatusCode);
        Assert.DoesNotContain(await ApiSession.Json<List<CustomFieldValueDto>>(await api.Get($"/api/v1/custom-fields/values/Lead/{lead.Id}")), v => v.Field.Key == "must");
        Assert.Equal(1L, await app.World.WithDb(db => db.CustomFieldValues.IgnoreQueryFilters().LongCountAsync(v => v.FieldId == created.Id)));
        Assert.True((await api.Post($"/api/v1/custom-fields/{created.Id}/activate")).IsSuccessStatusCode);
        Assert.Equal("present", (await ApiSession.Json<List<CustomFieldValueDto>>(await api.Get($"/api/v1/custom-fields/values/Lead/{lead.Id}"))).Single().Raw);
    }

    [RequiresMySqlFact]
    public async Task Fields_work_on_contacts_opportunities_and_customers_too()
    {
        var (_, api) = await Fresh();
        var lead = await Crm.CreateLead(api);
        var contact = (await Crm.Ok<List<ContactDto>>(await api.Get($"/api/v1/leads/{lead.Id}/contacts"))).Single();
        var opp = await Crm.Ok<OpportunityDto>(await api.Post($"/api/v1/leads/{lead.Id}/opportunities", new { name = "Deal", value = 10 }));
        await Field(api, "c_pref", "Select", new { options = new[] { "Phone", "Email" } }, CustomEntityType.Contact);
        await Field(api, "o_prob", "Number", new { minValue = 0, maxValue = 100 }, CustomEntityType.Opportunity);
        Assert.True((await api.Put($"/api/v1/custom-fields/values/Contact/{contact.Id}", new { values = new Dictionary<string, string?> { ["c_pref"] = "email" } })).IsSuccessStatusCode);
        Assert.True((await api.Put($"/api/v1/custom-fields/values/Opportunity/{opp.Id}", new { values = new Dictionary<string, string?> { ["o_prob"] = "40" } })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/v1/custom-fields/values/Opportunity/{opp.Id}", new { values = new Dictionary<string, string?> { ["o_prob"] = "140" } })).StatusCode);
        // a lead field key does not exist for contacts and vice versa
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/v1/custom-fields/values/Contact/{contact.Id}", new { values = new Dictionary<string, string?> { ["o_prob"] = "1" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Put($"/api/v1/custom-fields/values/Customer/{Guid.NewGuid()}", new { values = new Dictionary<string, string?>() })).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Leads_can_be_filtered_by_custom_fields_and_exported_with_them()
    {
        var (_, api) = await Fresh();
        await Field(api, "rooms", "Number", new { isFilterable = true });
        await Field(api, "board", "Select", new { options = new[] { "Alpha", "Beta" }, isFilterable = true });
        await Field(api, "perks", "MultiSelect", new { options = new[] { "Pool", "Spa" }, isFilterable = true });
        await Field(api, "note_txt", "Text", new { isFilterable = true });
        await Field(api, "secret", "Text", new { isFilterable = false });
        await Field(api, "opened", "Date", new { isFilterable = true });
        var token = Crm.Token("flt");
        var a = await Crm.CreateLead(api, name: $"{token} A"); var b = await Crm.CreateLead(api, name: $"{token} B"); var c = await Crm.CreateLead(api, name: $"{token} C");
        async Task Set(Guid id, Dictionary<string, string?> v) => Assert.True((await api.Put($"/api/v1/custom-fields/values/Lead/{id}", new { values = v })).IsSuccessStatusCode);
        await Set(a.Id, new() { ["rooms"] = "10", ["board"] = "Alpha", ["perks"] = "Pool", ["note_txt"] = "Harbour view", ["secret"] = "s1", ["opened"] = "2020-01-01" });
        await Set(b.Id, new() { ["rooms"] = "75", ["board"] = "Beta", ["perks"] = "Pool, Spa", ["note_txt"] = "City centre", ["secret"] = "s2", ["opened"] = "2023-06-01" });
        await Set(c.Id, new() { ["rooms"] = "200", ["secret"] = "s3" });
        async Task<List<string>> Names(string query) => (await Crm.Page<LeadListItemDto>(await api.Get($"/api/v1/leads?search={token}&{query}"))).Items.Select(i => i.Name[^1..]).Order().ToList();
        Assert.Equal(["A", "B", "C"], await Names(""));
        Assert.Equal(["B", "C"], await Names("cf.rooms.min=50")); Assert.Equal(["A", "B"], await Names("cf.rooms.max=100")); Assert.Equal(["B"], await Names("cf.rooms.min=50&cf.rooms.max=100"));
        Assert.Equal(["A"], await Names("cf.board=alpha")); Assert.Equal(["B"], await Names("cf.board=Beta&cf.rooms.min=1"));
        Assert.Equal(["A", "B"], await Names("cf.perks=Pool")); Assert.Equal(["B"], await Names("cf.perks=Spa"));
        Assert.Equal(["A"], await Names("cf.note_txt.contains=harbour"));
        Assert.Equal(["B"], await Names("cf.opened.min=2022-01-01&cf.opened.max=2024-01-01"));
        Assert.Empty(await Names("cf.secret=s1"));                    // not filterable: matches nothing
        Assert.Empty(await Names("cf.unknown=1"));                    // unknown key: matches nothing
        Assert.Equal(["A", "B", "C"], await Names("cf.rooms.min=notanumber".Replace("notanumber", "0")));
        // export has the columns and the typed display values
        var export = await api.Get($"/api/v1/leads/export?search={token}&cf.board=Beta");
        Assert.True(export.IsSuccessStatusCode);
        var csv = await export.Content.ReadAsStringAsync();
        Assert.Contains("Label rooms", csv); Assert.Contains("Pool, Spa", csv); Assert.Contains("City centre", csv); Assert.DoesNotContain("Harbour view", csv);
    }

    [RequiresMySqlFact]
    public async Task Csv_import_maps_columns_to_custom_fields_and_reports_invalid_values_per_row()
    {
        var (_, api) = await Fresh();
        await Field(api, "rooms", "Number", new { minValue = 0, maxValue = 1000 });
        await Field(api, "board", "Select", new { options = new[] { "Alpha", "Beta" } });
        await Field(api, "req", "Text", new { isRequired = true });
        var token = Crm.Token("imp");
        var preview = await Crm.Upload(api, "f.csv", Crm.CsvBytes("Name,City,Rooms,Label board,Req", $"{token} one,Riyadh,50,beta,x", $"{token} two,Riyadh,abc,Alpha,y", $"{token} three,Riyadh,5,Gamma,z", $"{token} four,Riyadh,7,Alpha,"));
        Assert.Equal("rooms", preview.Columns.Single(c => c.Header == "Rooms").SuggestedCustomKey);              // guessed from the header
        Assert.Equal("board", preview.Columns.Single(c => c.Header == "Label board").SuggestedCustomKey);         // by label
        var mapping = preview.Columns.Select(c => new { columnIndex = c.Index, field = c.SuggestedField, customKey = c.SuggestedCustomKey }).ToArray();
        var run = await api.Post($"/api/v1/imports/{preview.Job.Id}/run", new { mapping, duplicatePolicy = "Skip" });
        Assert.True(run.IsSuccessStatusCode, await run.Content.ReadAsStringAsync());
        var job = await ApiSession.Json<ImportJobDto>(run);
        Assert.Equal(1, job.CreatedCount); Assert.Equal(3, job.ErrorCount);                                         // only the clean row is created
        var leads = (await Crm.Page<LeadListItemDto>(await api.Get($"/api/v1/leads?search={token}"))).Items;
        var one = Assert.Single(leads);
        var values = await ApiSession.Json<List<CustomFieldValueDto>>(await api.Get($"/api/v1/custom-fields/values/Lead/{one.Id}"));
        Assert.Equal("50", values.Single(v => v.Field.Key == "rooms").Raw); Assert.Equal("Beta", values.Single(v => v.Field.Key == "board").Display);
        var errors = await api.Get($"/api/v1/imports/{preview.Job.Id}/errors");
        var text = await errors.Content.ReadAsStringAsync();
        Assert.Contains("must be a number", text); Assert.Contains("one of", text); Assert.Contains("required", text);
        // an unknown custom key in the mapping is refused up front
        var preview2 = await Crm.Upload(api, "g.csv", Crm.CsvBytes("Name,X", $"{token} five,1"));
        var bad = await api.Post($"/api/v1/imports/{preview2.Job.Id}/run", new { mapping = new object[] { new { columnIndex = 0, field = "Name" }, new { columnIndex = 1, customKey = "ghost" } }, duplicatePolicy = "Skip" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Scoring_rules_and_workflow_conditions_over_custom_fields_work_without_code()
    {
        var (_, api) = await Fresh();
        await Field(api, "rooms", "Number", new { isFilterable = true });
        var lead = await Crm.CreateLead(api, contact: false);
        var rules = await ApiSession.Json<ScoringRuleSetDto>(await api.Get("/api/v1/scoring/rules"));
        var all = rules.Rules.Select(r => new { dimension = r.Dimension, target = r.Target, points = r.Points, description = r.Description, isEnabled = r.IsEnabled }).ToList();
        all.Add(new { dimension = ScoreDimension.CustomField, target = "rooms gte 100", points = 20, description = (string?)"big property", isEnabled = true });
        var publish = await api.Post("/api/v1/scoring/rules", new { rules = all });
        Assert.Equal(HttpStatusCode.Created, publish.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/api/v1/scoring/rules", new { rules = new[] { new { dimension = "CustomField", target = "nonsense", points = 5, isEnabled = true } } })).StatusCode);
        await api.Put($"/api/v1/custom-fields/values/Lead/{lead.Id}", new { values = new Dictionary<string, string?> { ["rooms"] = "150" } });
        await api.Post("/api/v1/scoring/rescore");
        var scored = await ApiSession.Json<LeadDto>(await api.Get($"/api/v1/leads/{lead.Id}"));
        Assert.Equal(20, scored.Score); Assert.Contains(scored.ScoreBreakdown, m => m.Target == "rooms gte 100");
        await api.Put($"/api/v1/custom-fields/values/Lead/{lead.Id}", new { values = new Dictionary<string, string?> { ["rooms"] = "50" } });
        await api.Post("/api/v1/scoring/rescore");
        Assert.Equal(0, (await ApiSession.Json<LeadDto>(await api.Get($"/api/v1/leads/{lead.Id}"))).Score);

        // a workflow condition may use the field; an unknown field is refused when the workflow is saved
        object Wf(string field) => new { name = "wf " + Guid.NewGuid().ToString("N")[..5], definition = new { triggers = new[] { new { eventType = "lead.created" } },
            steps = new[] { new { key = "t", type = "CreateFollowUp", name = "Task", config = new { dueInHours = 24, note = "Call", assignToOwner = true }, condition = new { field, op = "gte", value = 100 } } } } };
        var good = await api.Post("/api/v1/workflows", Wf("lead.cf.rooms"));
        Assert.True(good.IsSuccessStatusCode, await good.Content.ReadAsStringAsync());
        var unknown = await api.Post("/api/v1/workflows", Wf("lead.cf.ghost"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode); Assert.Contains("ghost", await Err(unknown));
    }

    [RequiresMySqlFact]
    public async Task Categories_form_a_tree_without_loops_and_a_sub_category_lead_matches_a_parent_rule()
    {
        var (_, api) = await Fresh();
        var root = await Crm.Ok<CategoryDto>(await api.Post("/api/v1/categories", new { name = "Root " + Crm.Token() }));
        var child = await Crm.Ok<CategoryDto>(await api.Post("/api/v1/categories", new { name = "Child " + Crm.Token(), parentId = root.Id }));
        var grand = await Crm.Ok<CategoryDto>(await api.Post("/api/v1/categories", new { name = "Grand " + Crm.Token(), parentId = child.Id }));
        var list = await ApiSession.Json<List<CategoryDto>>(await api.Get("/api/v1/categories"));
        Assert.Equal([0, 1, 2], list.Select(c => c.Depth).ToArray()); Assert.Contains(root.Name, list.Single(c => c.Id == grand.Id).Path);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/v1/categories/{root.Id}/parent", new { parentId = grand.Id })).StatusCode);      // loop
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Put($"/api/v1/categories/{root.Id}/parent", new { parentId = root.Id })).StatusCode);       // itself
        var l3 = await Crm.Ok<CategoryDto>(await api.Post("/api/v1/categories", new { name = "L3 " + Crm.Token(), parentId = grand.Id }));
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/api/v1/categories", new { name = "L4 " + Crm.Token(), parentId = l3.Id })).StatusCode); // too deep
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/api/v1/categories", new { name = "Orphan " + Crm.Token(), parentId = Guid.NewGuid() })).StatusCode);

        var rules = await ApiSession.Json<ScoringRuleSetDto>(await api.Get("/api/v1/scoring/rules"));
        var all = rules.Rules.Select(r => new { dimension = r.Dimension, target = r.Target, points = r.Points, description = r.Description, isEnabled = r.IsEnabled }).ToList();
        all.Add(new { dimension = ScoreDimension.BusinessCategory, target = root.Name, points = 30, description = (string?)"root category", isEnabled = true });
        await api.Post("/api/v1/scoring/rules", new { rules = all });
        var lead = await Crm.CreateLead(api, contact: false, categoryId: grand.Id);
        await api.Post("/api/v1/scoring/rescore");
        Assert.Equal(30, (await ApiSession.Json<LeadDto>(await api.Get($"/api/v1/leads/{lead.Id}"))).Score);
    }

    [RequiresMySqlFact]
    public async Task Custom_fields_are_isolated_between_tenants()
    {
        var (_, a) = await Fresh("ca"); var (_, b) = await Fresh("cb");
        var field = await ApiSession.Json<CustomFieldDto>(await Field(a, "shared_key", "Number", new { isFilterable = true }));
        var leadA = await Crm.CreateLead(a); var leadB = await Crm.CreateLead(b);
        await a.Put($"/api/v1/custom-fields/values/Lead/{leadA.Id}", new { values = new Dictionary<string, string?> { ["shared_key"] = "99" } });
        // B sees nothing of A's definitions, values or filters
        Assert.DoesNotContain(await ApiSession.Json<List<CustomFieldDto>>(await b.Get("/api/v1/custom-fields")), f => f.Id == field.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Put($"/api/v1/custom-fields/{field.Id}", new { entityType = "Lead", key = "shared_key", label = "hack", type = "Number" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Post($"/api/v1/custom-fields/{field.Id}/deactivate")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Get($"/api/v1/custom-fields/values/Lead/{leadA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Put($"/api/v1/custom-fields/values/Lead/{leadA.Id}", new { values = new Dictionary<string, string?> { ["shared_key"] = "1" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Put($"/api/v1/custom-fields/values/Lead/{leadB.Id}", new { values = new Dictionary<string, string?> { ["shared_key"] = "1" } })).StatusCode);   // B has no such field
        Assert.Equal(0, (await Crm.Page<LeadListItemDto>(await b.Get("/api/v1/leads?cf.shared_key.min=0"))).Items.Count(i => i.Id == leadA.Id));
        Assert.Equal("Label shared_key", (await ApiSession.Json<List<CustomFieldDto>>(await a.Get("/api/v1/custom-fields"))).Single(f => f.Id == field.Id).Label);
        // B can create the same key for itself, with its own values
        Assert.Equal(HttpStatusCode.Created, (await Field(b, "shared_key", "Text")).StatusCode);
        // spoofed tenant id in the body
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Post("/api/v1/custom-fields", new { entityType = "Lead", key = "spoof", label = "x", type = "Text", tenantId = app.World.A.Id })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.Put($"/api/v1/custom-fields/values/Lead/{leadB.Id}", new { values = new Dictionary<string, string?>(), tenantId = app.World.A.Id })).StatusCode);
        // the database guard: a value row cannot be written for another tenant's field
        await Assert.ThrowsAnyAsync<Exception>(() => app.World.WithDb(async db =>
        {
            using var s = app.World.Services.CreateScope();
            var accessor = s.ServiceProvider.GetRequiredService<LeadPilot.Application.Abstractions.ITenantContextAccessor>();
            accessor.SetTenant(app.World.B.Id);
            var cdb = s.ServiceProvider.GetRequiredService<LeadPilot.Infrastructure.Persistence.AppDbContext>();
            var def = await cdb.CustomFieldDefinitions.IgnoreQueryFilters().FirstAsync(d => d.Id == field.Id);
            cdb.CustomFieldValues.Add(CustomFieldValue.Create(app.World.A.Id, def, leadA.Id, def.Normalize("1", out _)!, DateTime.UtcNow));
            await cdb.SaveChangesAsync();
        }));
    }

    [RequiresMySqlFact]
    public async Task Catalog_entries_can_be_services_with_decimal_prices_and_a_currency()
    {
        var (_, api) = await Fresh("cat");
        var created = await api.Post("/api/v1/catalog/products", new { name = "Consulting hour", summary = "A service", features = "one", kind = "Service", isActive = true });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var product = await ApiSession.Json<LeadPilot.Application.Agents.ProductDto>(created);
        Assert.Equal(LeadPilot.Domain.Agents.CatalogItemKind.Service, product.Kind);
        var plan = await api.Post($"/api/v1/catalog/products/{product.Id}/plans", new { name = "Hourly", price = 12.34m, currency = "eur", billingPeriod = "hour", isActive = true });
        Assert.True(plan.IsSuccessStatusCode, await plan.Content.ReadAsStringAsync());
        var plans = (await ApiSession.Json<LeadPilot.Application.Agents.ProductDto>(await api.Get($"/api/v1/catalog/products/{product.Id}"))).Plans;
        Assert.Equal(12.34m, plans.Single().Price); Assert.Equal("EUR", plans.Single().Currency);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Post($"/api/v1/catalog/products/{product.Id}/plans", new { name = "Bad", price = -1m, currency = "USD", isActive = true })).StatusCode);
    }
}
