using System.Text.Json.Nodes;
using LeadPilot.Application.Config;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Config;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.IntegrationTests;

/// <summary>Helpers for the Phase 13 tests: small hand-written packs (data only) and API shortcuts.</summary>
public static class PackKit
{
    public static string Key(string prefix = "t") => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    public static JsonObject Base(string key, int version = 1, string? name = null) => new()
    {
        ["format"] = "leadpilot.pack", ["formatVersion"] = 1, ["key"] = key, ["version"] = version, ["name"] = name ?? "Test pack " + key,
    };

    /// <summary>A pack touching most sections. Names carry the key so parallel tests never collide.</summary>
    public static JsonObject Rich(string key, int version = 1, string templateBody = "Hello {{first_name}}, thanks for contacting {{business_name}}.", string categoryName = "Cat A", string fieldLabel = "Size")
    {
        var p = Base(key, version);
        p["categories"] = JsonNode.Parse($$"""[{"key":"root","name":"{{key}} Root"},{"key":"cat-a","name":"{{key}} {{categoryName}}","parent":"root"}]""");
        p["pipelines"] = JsonNode.Parse($$"""[{"key":"pl","name":"{{key}} pipeline","stages":[{"name":"{{key}} new","kind":"Open"},{"name":"{{key}} won","kind":"Won"},{"name":"{{key}} lost","kind":"Lost"}]}]""");
        p["customFields"] = JsonNode.Parse($$"""[{"key":"size_{{key.Replace("-", "_")}}","entity":"Lead","label":"{{fieldLabel}}","type":"Number","min":0,"filterable":true}]""");
        p["messageTemplates"] = JsonNode.Parse($$"""[{"key":"tpl","name":"{{key}} template","body":"{{templateBody}}","category":"Service"}]""");
        p["agents"] = JsonNode.Parse($$"""[{"key":"ag","name":"{{key}} agent","instructions":"Be helpful.","tools":["GetProductInformation"],"qualificationQuestions":[{"key":"q1","question":"How many?","type":"Number"}]}]""");
        p["catalog"] = JsonNode.Parse($$"""[{"key":"prod","kind":"Service","name":"{{key}} service","summary":"[PLACEHOLDER]","plans":[{"key":"pl1","name":"{{key}} plan","currency":"USD"}]}]""");
        p["knowledgeBase"] = JsonNode.Parse($$"""[{"key":"kb","name":"{{key}} facts"}]""");
        p["workflows"] = JsonNode.Parse("""[{"key":"wf","name":"@@K@@ workflow","trigger":{"event":"lead.created"},"steps":[{"key":"task","type":"CreateFollowUp","name":"Task","config":{"dueInHours":24,"note":"Call {{name}}","assignToOwner":true}}]}]""".Replace("@@K@@", key));
        p["discovery"] = JsonNode.Parse($$"""{"categories":[{"key":"d1","category":"cat-a","searchPhrases":["{{key}} phrase"]}],"locations":[{"key":"l1","country":"Testland","city":"{{key}} City"}]}""");
        return p;
    }

    public static string Json(JsonNode n) => n.ToJsonString();

    public static async Task<PackSummaryDto> Upload(ApiSession s, JsonNode pack, bool expectSuccess = true)
    {
        var r = await s.Post("/api/v1/packs", new { json = Json(pack) });
        if (expectSuccess) Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return expectSuccess ? await ApiSession.Json<PackSummaryDto>(r) : default!;
    }

    public static async Task<PackPlanDto> Preview(ApiSession s, Guid id, object? options = null)
    {
        var r = await s.Post($"/api/v1/packs/{id}/preview", options ?? new { });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return await ApiSession.Json<PackPlanDto>(r);
    }

    public static async Task<PackApplyResultDto> Apply(ApiSession s, Guid id, object? options = null)
    {
        var r = await s.Post($"/api/v1/packs/{id}/apply", options ?? new { });
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return await ApiSession.Json<PackApplyResultDto>(r);
    }

    public static async Task<Guid> BuiltinId(ApiSession s, string key) => (await ApiSession.Json<List<PackSummaryDto>>(await s.Get("/api/v1/packs"))).Single(p => p.Key == key).Id;

    public static IEnumerable<string> Signature(PackPlanDto plan) => plan.Items.Select(i => $"{i.Type}:{i.Key}={i.Outcome}").OrderBy(x => x, StringComparer.Ordinal);

    /// <summary>Row counts of every table a pack writes to, for one tenant (so "nothing was written" can be asserted).</summary>
    public static Task<string> Fingerprint(TestWorld w, Guid tenantId) => w.WithDb(async db => string.Join("|", new[]
    {
        await db.LeadCategories.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId), await db.Pipelines.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId),
        await db.PipelineStages.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId), await db.CustomFieldDefinitions.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId),
        await db.MessageTemplates.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId), await db.ScoringRuleSets.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId),
        await db.SalesAgents.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId), await db.AgentQualificationQuestions.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId),
        await db.CatalogProducts.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId), await db.PricingPlans.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId),
        await db.KnowledgeSources.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId), await db.Workflows.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId),
        await db.DiscoveryCategoryMappings.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId), await db.DiscoveryLocationPresets.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId),
        await db.PackItemLinks.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId), await db.TenantPackApplications.IgnoreQueryFilters().CountAsync(x => x.TenantId == tenantId),
    }.Select(n => n.ToString())));
}
