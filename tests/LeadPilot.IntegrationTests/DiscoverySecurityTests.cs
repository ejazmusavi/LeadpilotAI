using System.Net;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Discovery;
using LeadPilot.Domain.Discovery;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReviewStatus = LeadPilot.Domain.Discovery.ReviewStatus;

namespace LeadPilot.IntegrationTests;

/// <summary>Permissions (401/403/404) and tenant isolation for every discovery entity and endpoint: API, background execution and the database guard.</summary>
[Collection("app")]
public class DiscoverySecurityTests(AppFixture app)
{
    private TestWorld W => app.World;
    private Task<ApiSession> Api(TestUser u) => ApiSession.LoginAsync(app, u.Email, u.Password);
    private FakeScript Script => app.Script;
    private static readonly Guid Any = Guid.NewGuid();

    private sealed record Ep(string Method, string Url, string Permission, object? Body = null);

    private static IEnumerable<Ep> Endpoints()
    {
        const string View = "Discovery.View", Run = "Discovery.Run", Review = "Discovery.Review", Manage = "Discovery.Manage", Integ = "Integrations.Manage";
        yield return new("GET", "/api/v1/discovery/jobs", View); yield return new("GET", $"/api/v1/discovery/jobs/{Any}", View); yield return new("GET", "/api/v1/discovery/jobs/history", View);
        yield return new("GET", $"/api/v1/discovery/jobs/{Any}/results", View); yield return new("GET", "/api/v1/discovery/results", View); yield return new("GET", $"/api/v1/discovery/results/{Any}", View);
        yield return new("GET", "/api/v1/discovery/connectors", View); yield return new("GET", "/api/v1/discovery/categories", View); yield return new("GET", "/api/v1/discovery/locations", View); yield return new("GET", "/api/v1/discovery/starter-packs", View);
        yield return new("POST", "/api/v1/discovery/jobs", Run, new { connectorType = DiscoveryKit.Fake, country = "Pakistan", keywords = "x", maxResults = 1 });
        yield return new("POST", $"/api/v1/discovery/jobs/{Any}/cancel", Run); yield return new("POST", $"/api/v1/discovery/jobs/{Any}/retry", Run); yield return new("POST", $"/api/v1/discovery/jobs/{Any}/rerun", Run);
        yield return new("POST", $"/api/v1/discovery/results/{Any}/approve", Review, new { }); yield return new("POST", $"/api/v1/discovery/results/{Any}/reject", Review, new { });
        yield return new("POST", "/api/v1/discovery/results/bulk-approve", Review, new { resultIds = new[] { Any } }); yield return new("POST", "/api/v1/discovery/results/bulk-reject", Review, new { resultIds = new[] { Any } });
        yield return new("PUT", "/api/v1/discovery/categories", Manage, new { categoryId = Any, searchTerms = "x" }); yield return new("PUT", $"/api/v1/discovery/categories/{Any}/active?active=false", Manage, new { });
        yield return new("POST", "/api/v1/discovery/locations", Manage, new { country = "Nowhere" }); yield return new("DELETE", $"/api/v1/discovery/locations/{Any}", Manage);
        yield return new("POST", $"/api/v1/discovery/starter-packs/{DiscoveryStarterPacks.EducationPakistan}/install", Manage, new { });
        yield return new("PUT", $"/api/v1/discovery/connectors/{DiscoveryKit.Fake}", Integ, new { enabled = true }); yield return new("GET", "/api/v1/discovery/connectors/usage", Integ);
    }

    private static Task<HttpResponseMessage> Send(ApiSession s, Ep e) => e.Method switch
    {
        "GET" => s.Get(e.Url), "POST" => s.Post(e.Url, e.Body), "PUT" => s.Put(e.Url, e.Body ?? new { }), "DELETE" => s.Delete(e.Url), _ => throw new InvalidOperationException(),
    };

    [RequiresMySqlFact]
    public async Task Every_discovery_endpoint_answers_401_without_a_token_and_403_without_its_permission()
    {
        var anon = ApiSession.Anonymous(app);
        foreach (var e in Endpoints()) Assert.True(HttpStatusCode.Unauthorized == (await Send(anon, e)).StatusCode, $"anonymous {e.Method} {e.Url}");

        var users = new (string Name, TestUser User, string[] Has)[]
        {
            ("read-only", W.AReadOnly, []),
            ("sales agent", W.ASalesAgent, ["Discovery.View"]),
            ("marketing manager", W.AMarketing, ["Discovery.View", "Discovery.Run"]),
            ("sales manager", W.ASalesManager, ["Discovery.View", "Discovery.Run", "Discovery.Review", "Discovery.Manage"]),
        };
        foreach (var (name, user, has) in users)
        {
            var s = await Api(user);
            foreach (var e in Endpoints())
            {
                var status = (await Send(s, e)).StatusCode;
                if (has.Contains(e.Permission)) Assert.True(status is not HttpStatusCode.Forbidden and not HttpStatusCode.Unauthorized, $"{name} should reach {e.Method} {e.Url} but got {(int)status}");
                else Assert.True(HttpStatusCode.Forbidden == status, $"{name} must get 403 on {e.Method} {e.Url} (needs {e.Permission}) but got {(int)status}");
            }
        }
        // the tenant administrator holds everything, including Integrations.Manage
        var admin = await Api(W.AAdmin);
        Assert.Equal(HttpStatusCode.OK, (await admin.Get("/api/v1/discovery/connectors/usage")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Default_role_mappings_follow_the_documented_matrix_and_the_new_permissions_are_tenant_permissions()
    {
        foreach (var code in new[] { "Discovery.View", "Discovery.Run", "Discovery.Review", "Discovery.Manage", "Integrations.Manage" })
        {
            Assert.True(LeadPilot.Application.Security.PermissionCatalog.Exists(code)); Assert.False(LeadPilot.Application.Security.PermissionCatalog.IsPlatform(code));
            var admin = LeadPilot.Application.Security.SystemRoles.All.Single(r => r.Name == "Tenant Administrator");
            Assert.Contains(code, admin.Permissions);
        }
        var perms = app.Web!.Services.CreateScope().ServiceProvider.GetRequiredService<IPermissionEvaluator>();
        Assert.Equal(["Discovery.Manage", "Discovery.Review", "Discovery.Run", "Discovery.View"], (await perms.GetAsync(W.ASalesManager.Id, W.A.Id)).Codes.Where(c => c.StartsWith("Discovery")).Order().ToArray());
        Assert.DoesNotContain("Integrations.Manage", (await perms.GetAsync(W.ASalesManager.Id, W.A.Id)).Codes);
        Assert.DoesNotContain((await perms.GetAsync(W.AReadOnly.Id, W.A.Id)).Codes, c => c.StartsWith("Discovery") || c.StartsWith("Integrations"));
    }

    private sealed record Seed(ApiSession A, ApiSession B, DiscoveryJobDto AJob, List<DiscoveryResultDto> AResults, DiscoveryJobDto BJob, List<DiscoveryResultDto> BResults, LocationPresetDto ALocation, CategoryMappingDto AMapping);

    private async Task<Seed> SeedAsync()
    {
        var a = await Api(W.AAdmin); var b = await Api(W.BAdmin);
        await Crm.Ok<InstallPackResult>(await a.Post($"/api/v1/discovery/starter-packs/{DiscoveryStarterPacks.EducationPakistan}/install"));
        var tokenA = DiscoveryKit.NewToken(); var tokenB = DiscoveryKit.NewToken();
        Script.Set(tokenA, new FakeScript.Behavior { Total = 4 }); Script.Set(tokenB, new FakeScript.Behavior { Total = 4 });
        var ja = await DiscoveryKit.CreateAndRun(app, a, W.A.Id, tokenA, 10); var jb = await DiscoveryKit.CreateAndRun(app, b, W.B.Id, tokenB, 10);
        var loc = await Crm.Ok<LocationPresetDto>(await a.Post("/api/v1/discovery/locations", new { country = "Pakistan", city = "Secret City " + tokenA }));
        var map = (await Crm.Ok<List<CategoryMappingDto>>(await a.Get("/api/v1/discovery/categories"))).First();
        return new Seed(a, b, ja, await DiscoveryKit.Results(a, ja.Id), jb, await DiscoveryKit.Results(b, jb.Id), loc, map);
    }

    [RequiresMySqlFact]
    public async Task Tenant_A_cannot_see_approve_reject_retry_or_cancel_tenant_B_searches_and_results_and_the_reverse()
    {
        var s = await SeedAsync(); var (a, b) = (s.A, s.B);
        async Task Expect(HttpStatusCode code, string what, Task<HttpResponseMessage> call) => Assert.True(code == (await call).StatusCode, what);

        // B tries every operation on A's data: always 404, nothing changes
        await Expect(HttpStatusCode.NotFound, "job get", b.Get($"/api/v1/discovery/jobs/{s.AJob.Id}"));
        await Expect(HttpStatusCode.NotFound, "job results", b.Get($"/api/v1/discovery/jobs/{s.AJob.Id}/results"));
        await Expect(HttpStatusCode.NotFound, "result get", b.Get($"/api/v1/discovery/results/{s.AResults[0].Id}"));
        await Expect(HttpStatusCode.NotFound, "approve", b.Post($"/api/v1/discovery/results/{s.AResults[0].Id}/approve", new { }));
        await Expect(HttpStatusCode.NotFound, "reject", b.Post($"/api/v1/discovery/results/{s.AResults[0].Id}/reject", new { }));
        await Expect(HttpStatusCode.NotFound, "cancel", b.Post($"/api/v1/discovery/jobs/{s.AJob.Id}/cancel"));
        await Expect(HttpStatusCode.NotFound, "retry", b.Post($"/api/v1/discovery/jobs/{s.AJob.Id}/retry"));
        await Expect(HttpStatusCode.NotFound, "rerun", b.Post($"/api/v1/discovery/jobs/{s.AJob.Id}/rerun"));
        await Expect(HttpStatusCode.NotFound, "delete location", b.Delete($"/api/v1/discovery/locations/{s.ALocation.Id}"));
        await Expect(HttpStatusCode.NotFound, "toggle mapping", b.Put($"/api/v1/discovery/categories/{s.AMapping.Id}/active?active=false", new { }));
        await Expect(HttpStatusCode.BadRequest, "mapping for A's category", b.Put("/api/v1/discovery/categories", new { categoryId = s.AMapping.CategoryId, searchTerms = "x" }));
        await Expect(HttpStatusCode.BadRequest, "job with A's category", b.Post("/api/v1/discovery/jobs", new { connectorType = DiscoveryKit.Fake, country = "Pakistan", categoryId = s.AMapping.CategoryId, maxResults = 3 }));
        var bulk = await Crm.Ok<BulkReviewResult>(await b.Post("/api/v1/discovery/results/bulk-approve", new { resultIds = s.AResults.Select(r => r.Id) }));
        Assert.Equal((0, s.AResults.Count), (bulk.Succeeded, bulk.Failed));
        Assert.Equal(s.AResults.Count, (await DiscoveryKit.Results(a, s.AJob.Id)).Count);   // all still pending
        Assert.Empty((await Crm.Page<LeadListItemDto>(await a.Get($"/api/v1/leads?search={s.AResults[0].Name}"))).Items);

        // lists never mix tenants
        var bJobs = await Crm.Page<DiscoveryJobDto>(await b.Get("/api/v1/discovery/jobs?pageSize=100")); Assert.DoesNotContain(bJobs.Items, j => j.Id == s.AJob.Id); Assert.Contains(bJobs.Items, j => j.Id == s.BJob.Id);
        var bResults = await Crm.Page<DiscoveryResultDto>(await b.Get("/api/v1/discovery/results?status=&pageSize=100")); Assert.DoesNotContain(bResults.Items, r => s.AResults.Any(x => x.Id == r.Id));
        Assert.DoesNotContain((await Crm.Ok<List<LocationPresetDto>>(await b.Get("/api/v1/discovery/locations"))), l => l.Id == s.ALocation.Id);
        Assert.DoesNotContain((await Crm.Page<SearchHistoryItemDto>(await b.Get("/api/v1/discovery/jobs/history?pageSize=100"))).Items, i => i.LastJobId == s.AJob.Id);
        Assert.DoesNotContain((await Crm.Ok<UsageSummaryDto>(await b.Get("/api/v1/discovery/connectors/usage?pageSize=100"))).Recent.Items, u => u.JobId == s.AJob.Id);

        // and the reverse direction for one representative call each
        await Expect(HttpStatusCode.NotFound, "A reads B job", a.Get($"/api/v1/discovery/jobs/{s.BJob.Id}"));
        await Expect(HttpStatusCode.NotFound, "A approves B result", a.Post($"/api/v1/discovery/results/{s.BResults[0].Id}/approve", new { }));
        await Expect(HttpStatusCode.NotFound, "A cancels B job", a.Post($"/api/v1/discovery/jobs/{s.BJob.Id}/cancel"));
    }

    [RequiresMySqlFact]
    public async Task A_tenants_connector_credentials_are_encrypted_never_returned_and_never_used_for_another_tenant()
    {
        var a = await Api(W.AAdmin); var b = await Api(W.BAdmin);
        var keyA = "KEY-A-" + Guid.NewGuid().ToString("N"); var keyB = "KEY-B-" + Guid.NewGuid().ToString("N");
        var saved = await Crm.Ok<ConnectorStatusDto>(await a.Put($"/api/v1/discovery/connectors/{DiscoveryKit.FakeKeyed}", new { apiKey = keyA }));
        Assert.True(saved.HasCredential); Assert.Equal("••••" + keyA[^4..], saved.KeyHint); Assert.True(saved.IsReady);

        // never returned: not in the connector list, the usage page or the audit trail; stored only in protected form
        var all = await (await a.Get("/api/v1/discovery/connectors")).Content.ReadAsStringAsync() + await (await a.Get("/api/v1/discovery/connectors/usage")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(keyA, all);
        var stored = await W.WithDb(db => db.DiscoveryConnectorConfigs.IgnoreQueryFilters().Where(c => c.TenantId == W.A.Id && c.ConnectorType == DiscoveryKit.FakeKeyed).Select(c => c.ProtectedApiKey).SingleAsync());
        Assert.StartsWith("enc:v1:", stored); Assert.DoesNotContain(keyA, stored);
        Assert.DoesNotContain(await W.WithDb(db => db.AuditLogs.IgnoreQueryFilters().Where(x => x.TenantId == W.A.Id && x.Action == "Connector.Changed").Select(x => x.Summary).ToListAsync()), s => s.Contains(keyA));
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Put($"/api/v1/discovery/connectors/{DiscoveryKit.FakeKeyed}", new { apiKey = "short" })).StatusCode);

        // B has no key: its search is refused (A's key is NOT borrowed)
        var res = await b.Post("/api/v1/discovery/jobs", new { connectorType = DiscoveryKit.FakeKeyed, country = "Pakistan", keywords = "x", maxResults = 3 });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode); Assert.Equal("connector_not_configured", await Crm.ProblemCode(res));
        Assert.False((await Crm.Ok<List<ConnectorStatusDto>>(await b.Get("/api/v1/discovery/connectors"))).Single(c => c.Key == DiscoveryKit.FakeKeyed).HasCredential);

        // each tenant's runs use only its own key
        var tA = DiscoveryKit.NewToken(); var tB = DiscoveryKit.NewToken(); Script.Set(tA, new FakeScript.Behavior { Total = 1 }); Script.Set(tB, new FakeScript.Behavior { Total = 1 });
        await Crm.Ok<ConnectorStatusDto>(await b.Put($"/api/v1/discovery/connectors/{DiscoveryKit.FakeKeyed}", new { apiKey = keyB }));
        await DiscoveryKit.CreateAndRun(app, a, W.A.Id, tA, 3, DiscoveryKit.FakeKeyed); await DiscoveryKit.CreateAndRun(app, b, W.B.Id, tB, 3, DiscoveryKit.FakeKeyed);
        Assert.Equal(keyA, Script.Calls.Single(c => c.Token == tA).ApiKey); Assert.Equal(keyB, Script.Calls.Single(c => c.Token == tB).ApiKey);

        // removing A's key stops A's searches without touching B's
        await Crm.Ok<ConnectorStatusDto>(await a.Put($"/api/v1/discovery/connectors/{DiscoveryKit.FakeKeyed}", new { clearApiKey = true }));
        Assert.Equal(HttpStatusCode.Conflict, (await a.Post("/api/v1/discovery/jobs", new { connectorType = DiscoveryKit.FakeKeyed, country = "Pakistan", keywords = "x", maxResults = 3 })).StatusCode);
        Assert.True((await Crm.Ok<List<ConnectorStatusDto>>(await b.Get("/api/v1/discovery/connectors"))).Single(c => c.Key == DiscoveryKit.FakeKeyed).HasCredential);
    }

    [RequiresMySqlFact]
    public async Task Background_execution_fails_closed_and_cannot_be_pointed_at_another_tenants_job()
    {
        var s = await SeedAsync(); var token = DiscoveryKit.NewToken(); Script.Set(token, new FakeScript.Behavior { Total = 2 });
        var bQueued = await DiscoveryKit.CreateJob(s.B, token, 5);

        // a job payload (tenant A, B's job id) does nothing: B's job is invisible in A's scope
        await DiscoveryKit.RunAsync(app, W.A.Id, bQueued.Id);
        var after = await DiscoveryKit.GetJob(s.B, bQueued.Id);
        Assert.Equal((DiscoveryJobStatus.Queued, 0, 0), (after.Status, after.ApiCalls, after.Attempts)); Assert.DoesNotContain(Script.Calls, c => c.Token == token);

        // no tenant: refused
        await Assert.ThrowsAsync<TenantContextMissingException>(() => DiscoveryKit.RunAsync(app, Guid.Empty, bQueued.Id));
        using (var scope = app.Web!.Services.CreateScope())
            await Assert.ThrowsAsync<TenantContextMissingException>(() => scope.ServiceProvider.GetRequiredService<IDiscoveryJobRunner>().RunAsync(bQueued.Id, default));
        // unknown or suspended tenants are refused as well
        await Assert.ThrowsAsync<TenantUnavailableException>(() => DiscoveryKit.RunAsync(app, Guid.NewGuid(), bQueued.Id));

        // the rightful tenant runs it fine
        await DiscoveryKit.RunAsync(app, W.B.Id, bQueued.Id);
        Assert.Equal(DiscoveryJobStatus.Completed, (await DiscoveryKit.GetJob(s.B, bQueued.Id)).Status);
        // A's own data was never touched by B's run
        Assert.Equal(s.AResults.Count, (await DiscoveryKit.Results(s.A, s.AJob.Id)).Count);
    }

    [RequiresMySqlFact]
    public async Task A_spoofed_tenant_id_is_rejected_on_every_discovery_request_that_could_carry_one()
    {
        var s = await SeedAsync(); var a = s.A;
        async Task Expect(string what, Task<HttpResponseMessage> call) => Assert.True(HttpStatusCode.BadRequest == (await call).StatusCode, what);
        await Expect("create job", a.Post("/api/v1/discovery/jobs", new { connectorType = DiscoveryKit.Fake, country = "Pakistan", keywords = "x", maxResults = 1, tenantId = W.B.Id }));
        await Expect("approve", a.Post($"/api/v1/discovery/results/{s.AResults[0].Id}/approve", new { tenantId = W.B.Id }));
        await Expect("reject", a.Post($"/api/v1/discovery/results/{s.AResults[0].Id}/reject", new { tenantId = W.B.Id }));
        await Expect("bulk", a.Post("/api/v1/discovery/results/bulk-approve", new { resultIds = new[] { s.AResults[0].Id }, tenantId = W.B.Id }));
        await Expect("connector", a.Put($"/api/v1/discovery/connectors/{DiscoveryKit.Fake}", new { enabled = true, tenantId = W.B.Id }));
        await Expect("mapping", a.Put("/api/v1/discovery/categories", new { categoryId = s.AMapping.CategoryId, searchTerms = "x", tenantId = W.B.Id }));
        await Expect("location", a.Post("/api/v1/discovery/locations", new { country = "X", tenantId = W.B.Id }));
        await Expect("query", a.Get($"/api/v1/discovery/jobs?tenantId={W.B.Id}"));
        await Expect("results query", a.Get($"/api/v1/discovery/results?tenantId={W.B.Id}"));
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/discovery/jobs"); req.Headers.Add("X-Tenant-Id", W.B.Id.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Http.SendAsync(req)).StatusCode);
        Assert.Equal(s.AResults.Count, (await DiscoveryKit.Results(a, s.AJob.Id)).Count);   // nothing was approved by the rejected requests
    }

    [RequiresMySqlFact]
    public async Task The_database_guard_rejects_cross_tenant_writes_on_every_discovery_entity_and_the_filters_hide_other_tenants()
    {
        var s = await SeedAsync();
        await Crm.Ok<CategoryDto>(await s.B.Post("/api/v1/categories", new { name = "B cat " + DiscoveryKit.NewToken() }));
        var bCat = await W.WithDb(db => db.LeadCategories.IgnoreQueryFilters().Where(c => c.TenantId == W.B.Id).Select(c => c.Id).FirstAsync());
        var aJobId = s.AJob.Id; var bJobId = s.BJob.Id; var bResultId = s.BResults[0].Id;
        DiscoveryResult.Data D(string id) => new("test-fake", id, "n", null, null, null, null, null, null, null, null, null, null, null, null, "s", null, null, DateTime.UtcNow, null, null, 50, null, null, EnrichmentStatus.NotRequested);

        await app.Web!.Services.GetRequiredService<ITenantScopeRunner>().RunAsync(W.A.Id, async (sp, ct) =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            // filters: only A's rows are visible in A's scope
            Assert.True(await db.DiscoveryJobs.AllAsync(j => j.TenantId == W.A.Id, ct)); Assert.False(await db.DiscoveryJobs.AnyAsync(j => j.Id == bJobId, ct));
            Assert.False(await db.DiscoveryResults.AnyAsync(r => r.Id == bResultId, ct)); Assert.True(await db.DiscoveryLocationPresets.AllAsync(l => l.TenantId == W.A.Id, ct));
            Assert.True(await db.DiscoveryUsageEntries.AllAsync(u => u.TenantId == W.A.Id, ct)); Assert.True(await db.DiscoveryCategoryMappings.AllAsync(m => m.TenantId == W.A.Id, ct));

            async Task Rejected(string what, Action<AppDbContext> change)
            {
                db.ChangeTracker.Clear(); change(db);
                await Assert.ThrowsAsync<CrossTenantViolationException>(() => db.SaveChangesAsync(ct)); db.ChangeTracker.Clear();
                Assert.True(what.Length > 0);
            }
            await Rejected("result of tenant B", d => d.DiscoveryResults.Add(DiscoveryResult.Create(W.B.Id, aJobId, D("x1" + Guid.NewGuid().ToString("N")), null, null, DateTime.UtcNow)));
            await Rejected("result of A pointing at B's job", d => d.DiscoveryResults.Add(DiscoveryResult.Create(W.A.Id, bJobId, D("x2" + Guid.NewGuid().ToString("N")), null, null, DateTime.UtcNow)));
            await Rejected("job of A with B's category", d => d.DiscoveryJobs.Add(DiscoveryJob.Create(W.A.Id, "test-fake", "Pakistan", null, null, null, null, bCat, "k", null, 5, false, null, null, DateTime.UtcNow)));
            await Rejected("job of tenant B", d => d.DiscoveryJobs.Add(DiscoveryJob.Create(W.B.Id, "test-fake", "Pakistan", null, null, null, null, null, "k", null, 5, false, null, null, DateTime.UtcNow)));
            await Rejected("connector config of B", d => d.DiscoveryConnectorConfigs.Add(DiscoveryConnectorConfig.Create(W.B.Id, "test-fake", DateTime.UtcNow)));
            await Rejected("usage entry of B", d => d.DiscoveryUsageEntries.Add(DiscoveryUsageEntry.Create(W.B.Id, null, "test-fake", "op", 1, 0, 200, DateTime.UtcNow)));
            await Rejected("location of B", d => d.DiscoveryLocationPresets.Add(DiscoveryLocationPreset.Create(W.B.Id, "Pakistan", null, null, null, null, DateTime.UtcNow)));
            await Rejected("mapping of A with B's category", d => d.DiscoveryCategoryMappings.Add(DiscoveryCategoryMapping.Create(W.A.Id, bCat, "x", null, DateTime.UtcNow)));

            // the usage ledger is append-only
            var entry = await db.DiscoveryUsageEntries.FirstAsync(ct);
            db.Entry(entry).Property(nameof(DiscoveryUsageEntry.Calls)).CurrentValue = 999;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(ct)); db.ChangeTracker.Clear();
            db.DiscoveryUsageEntries.Remove(await db.DiscoveryUsageEntries.FirstAsync(ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(ct)); db.ChangeTracker.Clear();
        });

        // and with no tenant at all nothing is readable or writable
        using var scope = app.Web.Services.CreateScope();
        var bare = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await bare.DiscoveryJobs.ToListAsync()); Assert.Empty(await bare.DiscoveryResults.ToListAsync());
        bare.DiscoveryResults.Add(DiscoveryResult.Create(W.A.Id, aJobId, D("x3" + Guid.NewGuid().ToString("N")), null, null, DateTime.UtcNow));
        await Assert.ThrowsAsync<TenantContextMissingException>(() => bare.SaveChangesAsync());
    }
}
