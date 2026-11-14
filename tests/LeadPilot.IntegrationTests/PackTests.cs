using System.Net;
using System.Text.Json.Nodes;
using LeadPilot.Application.Agents;
using LeadPilot.Application.Config;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Config;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.IntegrationTests;

[Collection("app")]
public class PackTests(AppFixture app)
{
    private async Task<(TestUser Admin, ApiSession Api)> FreshTenant(string prefix = "pk")
    {
        var (_, admin, _) = await app.World.NewTenantAsync(prefix);
        return (admin, await ApiSession.LoginAsync(app, admin.Email, admin.Password));
    }

    [RequiresMySqlFact]
    public async Task A_new_workspace_starts_neutral_and_the_gallery_lists_the_three_sample_packs()
    {
        var (admin, api) = await FreshTenant();
        Assert.Empty(await ApiSession.Json<List<CategoryDto>>(await api.Get("/api/v1/categories")));
        var packs = await ApiSession.Json<List<PackSummaryDto>>(await api.Get("/api/v1/packs"));
        foreach (var key in new[] { "education", "hotels-hospitality", "real-estate" })
        {
            var p = packs.Single(x => x.Key == key);
            Assert.True(p.IsSample); Assert.Contains("SAMPLE", p.Notice); Assert.Null(p.AppliedVersion); Assert.Equal(PackScope.Builtin, p.Scope);
        }
        Assert.Equal(0L, await app.World.WithDb(db => db.SalesAgents.IgnoreQueryFilters().LongCountAsync(a => a.TenantId == admin.TenantId)));
        var onboarding = await ApiSession.Json<LeadPilot.Application.Billing.OnboardingOverview>(await api.Get("/api/v1/onboarding"));
        Assert.False(onboarding.Steps.Single(s => s.Key == "industry").Done);
    }

    [RequiresMySqlFact]
    public async Task The_preview_is_the_result_writes_nothing_and_applying_twice_changes_nothing_more()
    {
        var (admin, api) = await FreshTenant();
        var id = await PackKit.BuiltinId(api, "hotels-hospitality");
        var before = await PackKit.Fingerprint(app.World, admin.TenantId);
        var preview = await PackKit.Preview(api, id);
        Assert.Equal(before, await PackKit.Fingerprint(app.World, admin.TenantId));          // a dry run writes nothing
        Assert.All(preview.Items, i => Assert.Equal(PackOutcome.Create, i.Outcome));

        var applied = await PackKit.Apply(api, id);
        Assert.Equal(PackKit.Signature(preview), PackKit.Signature(applied.Plan));            // dry-run == actual
        Assert.NotEqual(before, await PackKit.Fingerprint(app.World, admin.TenantId));

        var again = await PackKit.Preview(api, id);
        Assert.All(again.Items, i => Assert.Equal(PackOutcome.Unchanged, i.Outcome));
        var afterFirst = await PackKit.Fingerprint(app.World, admin.TenantId);
        var second = await PackKit.Apply(api, id);
        Assert.All(second.Plan.Items, i => Assert.Equal(PackOutcome.Unchanged, i.Outcome));
        Assert.Equal(afterFirst.Split('|')[..14], (await PackKit.Fingerprint(app.World, admin.TenantId)).Split('|')[..14]);   // no new configuration rows
        var history = await ApiSession.Json<List<PackApplicationDto>>(await api.Get("/api/v1/packs/history"));
        Assert.Equal(2, history.Count(h => h.PackKey == "hotels-hospitality")); Assert.Equal(PackApplyMode.Reapply, history[0].Mode);
    }

    [RequiresMySqlFact]
    public async Task Applying_a_pack_creates_safe_drafts_with_nothing_switched_on_or_confirmed()
    {
        var (admin, api) = await FreshTenant();
        await PackKit.Apply(api, await PackKit.BuiltinId(api, "real-estate"));
        await app.World.WithDb(async db =>
        {
            var t = admin.TenantId;
            var agent = await db.SalesAgents.IgnoreQueryFilters().SingleAsync(a => a.TenantId == t);
            Assert.False(agent.AutoReplyEnabled); Assert.True(agent.IsSample); Assert.True(agent.IsDefault);
            Assert.All(await db.CatalogProducts.IgnoreQueryFilters().Where(p => p.TenantId == t).ToListAsync(), p => { Assert.False(p.IsConfirmed); Assert.Equal(CatalogItemKind.Service, p.Kind); });
            Assert.All(await db.PricingPlans.IgnoreQueryFilters().Where(p => p.TenantId == t).ToListAsync(), p => { Assert.False(p.IsConfirmed); Assert.Null(p.Price); });
            Assert.All(await db.Workflows.IgnoreQueryFilters().Where(w => w.TenantId == t).ToListAsync(), w => Assert.Equal(LeadPilot.Domain.Workflows.WorkflowStatus.Draft, w.Status));
            var cats = await db.LeadCategories.IgnoreQueryFilters().Where(c => c.TenantId == t).ToListAsync();
            Assert.Equal(7, cats.Count); Assert.Equal(2, cats.Count(c => c.ParentId == null && (c.Name == "Residential" || c.Name == "Commercial")));
            Assert.Equal("Offices", cats.Single(c => c.ParentId == cats.Single(x => x.Name == "Commercial").Id && c.Name == "Offices").Name);
        });
        // the pack's pipeline is NOT made the default unless asked; the neutral default keeps working
        var pipelines = await ApiSession.Json<List<PipelineDto>>(await api.Get("/api/v1/pipelines"));
        Assert.Single(pipelines, p => p.IsDefault); Assert.Equal("Sales pipeline", pipelines.Single(p => p.IsDefault).Name);
    }

    [RequiresMySqlFact]
    public async Task A_pack_can_make_its_pipeline_the_default_for_new_leads()
    {
        var (_, api) = await FreshTenant();
        var id = await PackKit.BuiltinId(api, "real-estate");
        await PackKit.Apply(api, id, new { defaultPipelineKey = "property-sales" });
        var pipelines = await ApiSession.Json<List<PipelineDto>>(await api.Get("/api/v1/pipelines"));
        Assert.Equal("Property enquiries", pipelines.Single(p => p.IsDefault).Name);
        var lead = await Crm.CreateLead(api);
        Assert.Equal("New enquiry", (await ApiSession.Json<LeadDto>(await api.Get($"/api/v1/leads/{lead.Id}"))).StageName);
    }

    [RequiresMySqlFact]
    public async Task Upgrade_updates_untouched_items_keeps_tenant_edits_and_lets_the_person_choose_per_item()
    {
        var (admin, api) = await FreshTenant();
        var key = PackKit.Key("up");
        var v1 = await PackKit.Upload(api, PackKit.Rich(key, 1));
        await PackKit.Apply(api, v1.Id);
        // the tenant edits a template (through the normal API) and renames a category
        var tpl = (await ApiSession.Json<List<LeadPilot.Application.Messaging.TemplateDto>>(await api.Get("/api/v1/message-templates"))).Single(t => t.Name == key + " template");
        Assert.True((await api.Put($"/api/v1/message-templates/{tpl.Id}", new { name = tpl.Name, body = "My own wording for {{first_name}}.", category = "Service" })).IsSuccessStatusCode);
        var catA = (await ApiSession.Json<List<CategoryDto>>(await api.Get("/api/v1/categories"))).Single(c => c.Name == $"{key} Cat A");
        Assert.True((await api.Put($"/api/v1/categories/{catA.Id}", new { name = "My renamed category" })).IsSuccessStatusCode);

        // v2: new template wording (conflicts with the tenant edit), new field label (the tenant did not touch it), category renamed in the pack (conflict)
        var v2 = await PackKit.Upload(api, PackKit.Rich(key, 2, templateBody: "Pack v2 wording for {{first_name}}.", categoryName: "Cat A2", fieldLabel: "Size v2"));
        var preview = await PackKit.Preview(api, v2.Id);
        Assert.Equal(PackApplyMode.Upgrade, preview.Mode);
        Assert.Equal(PackOutcome.KeepYours, preview.Items.Single(i => i.Type == "messageTemplate").Outcome);
        Assert.True(preview.Items.Single(i => i.Type == "messageTemplate").IsConflict);
        Assert.Equal(PackOutcome.Update, preview.Items.Single(i => i.Type == "customField").Outcome);
        Assert.Equal(PackOutcome.KeepYours, preview.Items.Single(i => i.Key == "cat-a").Outcome);
        Assert.Contains(preview.Items.Single(i => i.Type == "messageTemplate").Changes, c => c.Field == "body" && c.Before!.StartsWith("My own") && c.After!.StartsWith("Pack v2"));

        var applied = await PackKit.Apply(api, v2.Id);
        Assert.Equal(PackKit.Signature(preview), PackKit.Signature(applied.Plan));
        var tpl2 = (await ApiSession.Json<List<LeadPilot.Application.Messaging.TemplateDto>>(await api.Get("/api/v1/message-templates"))).Single(t => t.Id == tpl.Id);
        Assert.Equal("My own wording for {{first_name}}.", tpl2.Body);                         // never overwritten silently
        Assert.Equal("Size v2", (await ApiSession.Json<List<CustomFieldDto>>(await api.Get("/api/v1/custom-fields"))).Single(f => f.Key.StartsWith("size_")).Label);

        // the person now decides item by item: replace the template, keep the category
        var again = await PackKit.Preview(api, v2.Id, new { resolutions = new Dictionary<string, string> { ["messageTemplate:tpl"] = "Replace", ["category:cat-a"] = "Keep" } });
        Assert.Equal(PackOutcome.Overwrite, again.Items.Single(i => i.Type == "messageTemplate").Outcome);
        Assert.Equal(PackOutcome.KeepYours, again.Items.Single(i => i.Key == "cat-a").Outcome);
        var result = await PackKit.Apply(api, v2.Id, new { resolutions = new Dictionary<string, string> { ["messageTemplate:tpl"] = "Replace", ["category:cat-a"] = "Keep" } });
        Assert.Equal(PackKit.Signature(again), PackKit.Signature(result.Plan));
        Assert.Equal("Pack v2 wording for {{first_name}}.", (await ApiSession.Json<List<LeadPilot.Application.Messaging.TemplateDto>>(await api.Get("/api/v1/message-templates"))).Single(t => t.Id == tpl.Id).Body);
        Assert.Contains("My renamed category", (await ApiSession.Json<List<CategoryDto>>(await api.Get("/api/v1/categories"))).Select(c => c.Name));
        // the overwrite is audited with before and after
        Assert.True(await app.World.WithDb(db => db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.TenantId == admin.TenantId && a.Action == "Pack.ItemOverwritten" && a.Summary.Contains("My own wording"))));
    }

    [RequiresMySqlFact]
    public async Task Whole_policy_UseThePack_overwrites_conflicts_but_still_reports_them()
    {
        var (_, api) = await FreshTenant();
        var key = PackKit.Key("pol");
        await PackKit.Apply(api, (await PackKit.Upload(api, PackKit.Rich(key, 1))).Id);
        var tpl = (await ApiSession.Json<List<LeadPilot.Application.Messaging.TemplateDto>>(await api.Get("/api/v1/message-templates"))).Single(t => t.Name == key + " template");
        await api.Put($"/api/v1/message-templates/{tpl.Id}", new { name = tpl.Name, body = "mine", category = "Service" });
        var v2 = await PackKit.Upload(api, PackKit.Rich(key, 2, templateBody: "pack two for {{first_name}}"));
        var plan = await PackKit.Preview(api, v2.Id, new { policy = "UseThePack" });
        var item = plan.Items.Single(i => i.Type == "messageTemplate");
        Assert.Equal(PackOutcome.Overwrite, item.Outcome); Assert.True(item.IsConflict); Assert.Equal(1, plan.Counts.Overwrite);
    }

    [RequiresMySqlFact]
    public async Task A_tenant_that_deleted_a_pack_item_keeps_it_deleted_unless_it_chooses_to_restore_it()
    {
        var (_, api) = await FreshTenant();
        var key = PackKit.Key("del");
        var v1 = await PackKit.Upload(api, PackKit.Rich(key, 1));
        await PackKit.Apply(api, v1.Id);
        var tpl = (await ApiSession.Json<List<LeadPilot.Application.Messaging.TemplateDto>>(await api.Get("/api/v1/message-templates"))).Single(t => t.Name == key + " template");
        await app.World.WithDb(db => db.MessageTemplates.IgnoreQueryFilters().Where(t => t.Id == tpl.Id).ExecuteDeleteAsync());
        var plan = await PackKit.Preview(api, v1.Id);
        Assert.Equal(PackOutcome.LeaveDeleted, plan.Items.Single(i => i.Type == "messageTemplate").Outcome);
        var restore = await PackKit.Preview(api, v1.Id, new { resolutions = new Dictionary<string, string> { ["messageTemplate:tpl"] = "Replace" } });
        Assert.Equal(PackOutcome.Recreate, restore.Items.Single(i => i.Type == "messageTemplate").Outcome);
    }

    [RequiresMySqlFact]
    public async Task A_failure_part_way_rolls_everything_back_and_is_audited()
    {
        var (admin, api) = await FreshTenant();
        var key = PackKit.Key("rb");
        var bad = PackKit.Rich(key, 1);
        // two locations that differ only by letter case pass the pack rules but collide in the database, AFTER earlier sections were already written
        bad["discovery"] = JsonNode.Parse("""{"locations":[{"key":"l1","country":"Testland","city":"Paris"},{"key":"l2","country":"testland","city":"paris"}]}""");
        bad.Remove("workflows");
        var pack = await PackKit.Upload(api, bad);
        var before = await PackKit.Fingerprint(app.World, admin.TenantId);
        var r = await api.Post($"/api/v1/packs/{pack.Id}/apply", new { });
        Assert.False(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Equal(before, await PackKit.Fingerprint(app.World, admin.TenantId));           // categories, fields, templates, agents ... none of it remains
        Assert.True(await app.World.WithDb(db => db.AuditLogs.IgnoreQueryFilters().AnyAsync(a => a.TenantId == admin.TenantId && a.Action == "Pack.ApplyFailed")));
        Assert.Empty(await ApiSession.Json<List<PackApplicationDto>>(await api.Get("/api/v1/packs/history")));
    }

    [RequiresMySqlFact]
    public async Task Hostile_or_malformed_uploads_are_refused_and_nothing_is_stored()
    {
        var (admin, api) = await FreshTenant();
        var key = PackKit.Key("bad");
        var cases = new List<string>
        {
            "not json", "[]", new string('x', 300 * 1024),
            PackKit.Json(PackKit.Base(key).Also(o => o["evil"] = "x")),
            PackKit.Json(PackKit.Base(key).Also(o => o["categories"] = JsonNode.Parse("""[{"key":"a","name":"<script>alert(1)</script>"}]"""))),
            PackKit.Json(PackKit.Base(key).Also(o => o["categories"] = JsonNode.Parse("""[{"key":"a","name":"x","parent":"ghost"}]"""))),
            PackKit.Json(PackKit.Base(key).Also(o => o["categories"] = JsonNode.Parse("""[{"key":"a","name":"=cmd|' /C calc'!A0"}]"""))),
        };
        foreach (var json in cases) Assert.Equal(HttpStatusCode.BadRequest, (await api.Post("/api/v1/packs", new { json })).StatusCode);
        Assert.DoesNotContain(await ApiSession.Json<List<PackSummaryDto>>(await api.Get("/api/v1/packs")), p => p.Scope == PackScope.Tenant);
        var check = await ApiSession.Json<PackValidationDto>(await api.Post("/api/v1/packs/validate", new { json = cases[3] }));
        Assert.False(check.IsValid); Assert.Contains(check.Problems, p => p.Message.Contains("Unknown key"));
        Assert.Equal(0L, await app.World.WithDb(db => db.PackDefinitions.LongCountAsync(p => p.OwnerTenantId == admin.TenantId)));
    }

    [RequiresMySqlFact]
    public async Task A_private_key_cannot_shadow_a_builtin_pack_and_versions_must_move_forward()
    {
        var (_, api) = await FreshTenant();
        var shadow = PackKit.Base("education", 9);
        Assert.Equal(HttpStatusCode.Conflict, (await api.Post("/api/v1/packs", new { json = PackKit.Json(shadow) })).StatusCode);
        var key = PackKit.Key("ver");
        await PackKit.Upload(api, PackKit.Rich(key, 2));
        Assert.Equal(HttpStatusCode.Conflict, (await api.Post("/api/v1/packs", new { json = PackKit.Json(PackKit.Rich(key, 1)) })).StatusCode);                          // older
        Assert.Equal(HttpStatusCode.Conflict, (await api.Post("/api/v1/packs", new { json = PackKit.Json(PackKit.Rich(key, 2, fieldLabel: "different")) })).StatusCode);  // same version, different content
        Assert.True((await api.Post("/api/v1/packs", new { json = PackKit.Json(PackKit.Rich(key, 2)) })).IsSuccessStatusCode);                                            // identical re-upload is idempotent
    }

    [RequiresMySqlFact]
    public async Task Export_writes_only_configuration_never_people_or_secrets_and_round_trips_into_another_workspace()
    {
        var (admin, api) = await FreshTenant("ex");
        var token = Crm.Token("exp");
        await PackKit.Apply(api, await PackKit.BuiltinId(api, "hotels-hospitality"));
        var lead = await Crm.CreateLead(api, name: $"{token} Secret Hotel", phone: Crm.Phone());
        await api.Post($"/api/v1/leads/{lead.Id}/notes", new { body = $"note {token} someone@private.example" });
        // free text with contact details is cleaned
        var listed = (await ApiSession.Json<System.Text.Json.JsonElement>(await api.Get("/api/v1/agents"))).EnumerateArray().Single().GetProperty("id").GetGuid();
        var a = await ApiSession.Json<AgentDto>(await api.Get($"/api/v1/agents/{listed}"));
        var put = await api.Put($"/api/v1/agents/{a.Id}", new { name = a.Name, description = "call +966 50 123 4567 or mail boss@private.example", instructions = a.Instructions ?? "Be helpful.", enabledTools = a.EnabledTools, offTopicReply = a.OffTopicReply });
        Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
        var r = await api.Post("/api/v1/packs/export", new { key = "exported-" + token[3..], name = "Exported", industry = "Test" });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        var export = await ApiSession.Json<PackExportDto>(r);
        Assert.DoesNotContain(token, export.Json);                                           // no lead, note or contact data
        Assert.DoesNotContain("private.example", export.Json); Assert.DoesNotContain("123 4567", export.Json);
        Assert.Contains("[email removed]", export.Json); Assert.Contains("[number removed]", export.Json);
        Assert.DoesNotContain("password", export.Json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("apikey", export.Json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(export.Warnings, w => w.Contains("Never exported"));
        Assert.True(PackParser.Parse(export.Json).IsValid);
        // another workspace can use it as a private pack
        var (_, other) = await FreshTenant("ex2");
        var uploaded = await PackKit.Upload(other, JsonNode.Parse(export.Json)!);
        var plan = await PackKit.Preview(other, uploaded.Id);
        Assert.True(plan.Counts.Create > 10);
    }

    [RequiresMySqlFact]
    public async Task Permissions_anonymous_401_readonly_403_and_other_tenants_get_404()
    {
        var anon = ApiSession.Anonymous(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Get("/api/v1/packs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Post($"/api/v1/packs/{Guid.NewGuid()}/apply")).StatusCode);

        var ro = await ApiSession.LoginAsync(app, app.World.AReadOnly.Email, app.World.AReadOnly.Password);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Get("/api/v1/packs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post($"/api/v1/packs/{Guid.NewGuid()}/apply")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post("/api/v1/packs/export", new { key = "abc-def", name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post("/api/v1/packs", new { json = "{}" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ro.Post("/api/v1/custom-fields", new { entityType = "Lead", key = "x", label = "x", type = "Text" })).StatusCode);

        var sm = await ApiSession.LoginAsync(app, app.World.ASalesManager.Email, app.World.ASalesManager.Password);        // Sales Manager: custom fields yes, packs no
        Assert.Equal(HttpStatusCode.Forbidden, (await sm.Get("/api/v1/packs")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await sm.Post("/api/v1/custom-fields", new { entityType = "Lead", key = "sm_" + Guid.NewGuid().ToString("N")[..6], label = "SM field", type = "Text" })).StatusCode);

        var a = await ApiSession.LoginAsync(app, app.World.AAdmin.Email, app.World.AAdmin.Password);
        var b = await ApiSession.LoginAsync(app, app.World.BAdmin.Email, app.World.BAdmin.Password);
        var secret = await PackKit.Upload(a, PackKit.Rich(PackKit.Key("sec"), 1));
        Assert.DoesNotContain(await ApiSession.Json<List<PackSummaryDto>>(await b.Get("/api/v1/packs")), p => p.Id == secret.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Get($"/api/v1/packs/{secret.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Get($"/api/v1/packs/{secret.Id}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Post($"/api/v1/packs/{secret.Id}/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Post($"/api/v1/packs/{secret.Id}/apply")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Delete($"/api/v1/packs/{secret.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.Delete($"/api/v1/packs/{secret.Id}")).StatusCode);
        // a spoofed tenant id is rejected
        var spoof = await a.Post($"/api/v1/packs/{await PackKit.BuiltinId(a, "education")}/preview", new { tenantId = app.World.B.Id });
        Assert.Equal(HttpStatusCode.BadRequest, spoof.StatusCode); Assert.Equal("tenant_mismatch", await Crm.ProblemCode(spoof));
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/packs", new { json = PackKit.Json(PackKit.Base("x-y")), tenantId = app.World.B.Id })).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Platform_packs_are_readable_by_all_tenants_but_only_platform_roles_can_publish_or_withdraw()
    {
        var owner = await ApiSession.LoginAsync(app, AppFixture.AdminEmail, AppFixture.AdminPassword);
        var a = await ApiSession.LoginAsync(app, app.World.AAdmin.Email, app.World.AAdmin.Password);
        var key = PackKit.Key("plat");
        // a workspace administrator can neither publish nor list platform packs
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Post("/api/v1/platform/packs", new { json = PackKit.Json(PackKit.Rich(key)), publish = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Get("/api/v1/platform/packs")).StatusCode);
        // unpublished: invisible to tenants
        var saved = await ApiSession.Json<PackSummaryDto>(await owner.Post("/api/v1/platform/packs", new { json = PackKit.Json(PackKit.Rich(key)), publish = false }));
        Assert.DoesNotContain(await ApiSession.Json<List<PackSummaryDto>>(await a.Get("/api/v1/packs")), p => p.Key == key);
        Assert.Equal(HttpStatusCode.NotFound, (await a.Get($"/api/v1/packs/{saved.Id}")).StatusCode);
        // published: visible and usable by every tenant, but still not writable by them
        Assert.True((await owner.Post($"/api/v1/platform/packs/{saved.Id}/publish")).IsSuccessStatusCode);
        var b = await ApiSession.LoginAsync(app, app.World.BAdmin.Email, app.World.BAdmin.Password);
        foreach (var t in new[] { a, b }) Assert.Contains(await ApiSession.Json<List<PackSummaryDto>>(await t.Get("/api/v1/packs")), p => p.Id == saved.Id && p.Scope == PackScope.Platform);
        Assert.True((await b.Post($"/api/v1/packs/{saved.Id}/preview")).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Delete($"/api/v1/packs/{saved.Id}")).StatusCode);                  // not theirs to delete
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Post($"/api/v1/platform/packs/{saved.Id}/withdraw")).StatusCode);
        // builtin packs cannot be withdrawn, even by the platform owner
        var builtin = await PackKit.BuiltinId(owner, "education");
        Assert.Equal(HttpStatusCode.NotFound, (await owner.Post($"/api/v1/platform/packs/{builtin}/withdraw")).StatusCode);
        Assert.True((await owner.Post($"/api/v1/platform/packs/{saved.Id}/withdraw")).IsSuccessStatusCode);
        Assert.DoesNotContain(await ApiSession.Json<List<PackSummaryDto>>(await b.Get("/api/v1/packs")), p => p.Id == saved.Id);
        // applying a platform pack to one tenant touches only that tenant
        var fpB = await PackKit.Fingerprint(app.World, app.World.B.Id);
        await owner.Post($"/api/v1/platform/packs/{saved.Id}/publish");
        await PackKit.Apply(a, saved.Id);
        Assert.Equal(fpB, await PackKit.Fingerprint(app.World, app.World.B.Id));
        await owner.Post($"/api/v1/platform/packs/{saved.Id}/withdraw");
    }

    [RequiresMySqlFact]
    public async Task Tenants_cannot_see_each_others_applied_state_or_history()
    {
        var (adminA, a) = await FreshTenant("ia");
        var (_, b) = await FreshTenant("ib");
        await PackKit.Apply(a, await PackKit.BuiltinId(a, "hotels-hospitality"));
        Assert.Contains(await ApiSession.Json<List<PackApplicationDto>>(await a.Get("/api/v1/packs/history")), h => h.PackKey == "hotels-hospitality");
        Assert.Empty(await ApiSession.Json<List<PackApplicationDto>>(await b.Get("/api/v1/packs/history")));
        Assert.Null((await ApiSession.Json<List<PackSummaryDto>>(await b.Get("/api/v1/packs"))).Single(p => p.Key == "hotels-hospitality").AppliedVersion);
        Assert.Empty(await ApiSession.Json<List<CategoryDto>>(await b.Get("/api/v1/categories")));
        Assert.NotEmpty(await ApiSession.Json<List<CategoryDto>>(await a.Get("/api/v1/categories")));
        _ = adminA;
    }

    [RequiresMySqlFact]
    public async Task The_education_pack_recreates_the_former_school_erp_sample_exactly_and_adopts_what_a_workspace_already_has()
    {
        var (admin, api) = await FreshTenant("edu");
        var id = await PackKit.BuiltinId(api, "education");
        await PackKit.Apply(api, id);
        await app.World.WithDb(async db =>
        {
            var t = admin.TenantId;
            var agent = await db.SalesAgents.IgnoreQueryFilters().SingleAsync(a => a.TenantId == t);
            Assert.Equal("School ERP sales assistant (SAMPLE)", agent.Name); Assert.True(agent.IsSample); Assert.False(agent.AutoReplyEnabled); Assert.True(agent.IsDefault);
            Assert.Equal(["AddLeadNote", "BookDemo", "CheckDemoAvailability", "GetLeadProfile", "GetPricingPlans", "GetProductInformation", "QualifyLead", "ScheduleFollowUp", "UpdateLeadStatus"], agent.EnabledTools);
            Assert.Equal(5, await db.AgentQualificationQuestions.IgnoreQueryFilters().CountAsync(q => q.TenantId == t && q.AgentId == agent.Id));
            var product = await db.CatalogProducts.IgnoreQueryFilters().SingleAsync(p => p.TenantId == t);
            Assert.False(product.IsConfirmed); Assert.True(product.IsSample); Assert.Equal(7, product.FeatureList.Count); Assert.All(product.FeatureList, f => Assert.StartsWith("[PLACEHOLDER]", f));
            Assert.Null((await db.PricingPlans.IgnoreQueryFilters().SingleAsync(p => p.TenantId == t)).Price);
            Assert.Equal(7, await db.DiscoveryCategoryMappings.IgnoreQueryFilters().CountAsync(m => m.TenantId == t));
            Assert.Equal(11, await db.DiscoveryLocationPresets.IgnoreQueryFilters().CountAsync(l => l.TenantId == t));
            Assert.Equal(7, await db.LeadCategories.IgnoreQueryFilters().CountAsync(c => c.TenantId == t));
        });
        // a workspace that migrated from the old built-in sample has the same rows but no pack links: importing the pack adopts them, creating nothing twice
        await app.World.WithDb(async db => { await db.PackItemLinks.IgnoreQueryFilters().Where(l => l.TenantId == admin.TenantId).ExecuteDeleteAsync(); await db.TenantPackApplications.IgnoreQueryFilters().Where(l => l.TenantId == admin.TenantId).ExecuteDeleteAsync(); });
        var plan = await PackKit.Preview(api, id);
        Assert.Equal(0, plan.Counts.Create); Assert.Equal(0, plan.Counts.KeepYours); Assert.Equal(plan.Items.Count, plan.Counts.Link);
        await PackKit.Apply(api, id);
        Assert.All((await PackKit.Preview(api, id)).Items, i => Assert.Equal(PackOutcome.Unchanged, i.Outcome));
    }

    [RequiresMySqlFact]
    public async Task Packs_can_define_scoring_rules_on_custom_fields_without_code()
    {
        var (_, api) = await FreshTenant("sc");
        await PackKit.Apply(api, await PackKit.BuiltinId(api, "real-estate"));
        var fields = await ApiSession.Json<List<CustomFieldDto>>(await api.Get("/api/v1/custom-fields?entityType=Lead"));
        Assert.Contains(fields, f => f.Key == "budget_max" && f.Type == CustomFieldType.Number && f.IsFilterable);
        var lead = await Crm.CreateLead(api, contact: false);
        var before = (await ApiSession.Json<LeadDto>(await api.Get($"/api/v1/leads/{lead.Id}"))).Score;
        var set = await api.Put($"/api/v1/custom-fields/values/Lead/{lead.Id}", new { values = new Dictionary<string, string?> { ["budget_max"] = "250000", ["listing_type"] = "Buy" } });
        Assert.True(set.IsSuccessStatusCode, await set.Content.ReadAsStringAsync());
        Assert.True((await api.Post($"/api/v1/scoring/rescore")).IsSuccessStatusCode);
        var after = await ApiSession.Json<LeadDto>(await api.Get($"/api/v1/leads/{lead.Id}"));
        Assert.Equal(before + 15, after.Score);                                                                // 10 (budget stated) + 5 (wants to buy)
    }
}

internal static class JsonNodeExtensions
{
    public static JsonObject Also(this JsonObject o, Action<JsonObject> edit) { edit(o); return o; }
}
