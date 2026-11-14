using System.Net;
using System.Text.Json;

namespace LeadPilot.IntegrationTests;

/// <summary>The MVC pages: they render, the builder saves through the same service, permissions and tenant isolation hold.</summary>
[Collection("app")]
public class WorkflowWebTests(AppFixture app)
{
    [RequiresMySqlFact]
    public async Task Workflow_pages_render_the_builder_saves_and_other_workspaces_and_roles_are_kept_out()
    {
        await using var w = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var web = await WebSession.LoginAsync(app, w.AdminUser.Email, w.AdminUser.Password);
        var wf = await w.CreateAsync(Wf.Definition([Wf.Trigger("lead.created")], [Wf.Step("t", "AddTag", new { tag = "x" })]), "Page test");
        var lead = await Crm.CreateLead(w.Admin, phone: "+9230" + Random.Shared.Next(10_000_000, 99_999_999));

        Assert.Contains("Page test", await web.Html("/Workflows"));
        var builder = await web.Html("/Workflows/New");
        Assert.Contains("wf-builder", builder); Assert.Contains("workflow-builder.js", builder); Assert.Contains("lead.created", builder);
        var details = await web.Html($"/Workflows/Details/{wf.Id}"); Assert.Contains("Edit draft v1", details); Assert.Contains("Activate", details);
        Assert.Contains("Add tag", await web.Html($"/Workflows/Version/{wf.Id}?number=1"));
        Assert.Contains("wf-definition", await web.Html($"/Workflows/Edit/{wf.Id}"));
        Assert.Contains("Simulate", await web.Html($"/Workflows/Simulate/{wf.Id}"));
        Assert.Contains("kill switch", (await web.Html("/Workflows/Settings")).ToLowerInvariant());
        Assert.Contains("Workflow runs", await web.Html("/WorkflowExecutions"));
        Assert.Contains("My notices", await web.Html("/WorkflowNotifications"));

        // saving through the page posts the builder's JSON; an invalid one comes back with a message and is not saved
        var def = JsonSerializer.Serialize(new { triggers = new[] { new { eventType = "lead.created" } }, steps = new[] { new { key = "a", type = "AddTag", name = "Tag it", config = new { tag = "from-page" }, onFailure = "Retry", maxRetries = 2 } }, reEntry = "AfterCompletion", cooldownHours = 0, maxStartsPerLeadPer30Days = 3 });
        var save = await web.PostWithToken("/Workflows/New", "/Workflows/Save", ("Name", "Saved from page"), ("DefinitionJson", def));
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);
        Assert.Contains("Saved from page", await web.Html("/Workflows"));
        var bad = await web.PostWithToken("/Workflows/New", "/Workflows/Save", ("Name", "Broken"), ("DefinitionJson", """{"triggers":[],"steps":[]}"""));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode); Assert.Contains("trigger", await bad.Content.ReadAsStringAsync());
        var run = await web.PostWithToken("/Workflows/New", $"/Workflows/Validate", ("definitionJson", def));
        Assert.Contains("\"errors\":[]", await run.Content.ReadAsStringAsync());
        var sim = await web.PostWithToken($"/Workflows/Simulate/{wf.Id}", $"/Workflows/Simulate/{wf.Id}", ("leadId", lead.Id.ToString()));
        Assert.Contains("Nothing was changed", await sim.Content.ReadAsStringAsync());

        // another workspace gets 404 for these ids, a read-only user is refused the builder and every mutation
        var other = await WorkflowWorld.OpenAsync(app, freshTenant: true);
        var otherWeb = await WebSession.LoginAsync(app, other.AdminUser.Email, other.AdminUser.Password);
        foreach (var url in new[] { $"/Workflows/Details/{wf.Id}", $"/Workflows/Edit/{wf.Id}", $"/Workflows/Simulate/{wf.Id}", $"/Workflows/Version/{wf.Id}?number=1" })
            Assert.Equal(HttpStatusCode.NotFound, (await otherWeb.Get(url)).StatusCode);
        var ro = await WebSession.LoginAsync(app, app.World.AReadOnly.Email, app.World.AReadOnly.Password);
        Assert.Equal(HttpStatusCode.OK, (await ro.Get("/Workflows")).StatusCode);
        foreach (var url in new[] { "/Workflows/New", $"/Workflows/Edit/{wf.Id}", $"/Workflows/Simulate/{wf.Id}" }) Assert.NotEqual(HttpStatusCode.OK, (await ro.Get(url)).StatusCode);
        Assert.NotEqual(HttpStatusCode.Redirect, (await ro.PostWithToken("/Workflows", $"/Workflows/KillSwitch/{wf.Id}", ("on", "true"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await WebSession.Anonymous(app).Get("/Workflows")).StatusCode);   // anonymous: to the login page
        await other.DisposeAsync();
    }
}
