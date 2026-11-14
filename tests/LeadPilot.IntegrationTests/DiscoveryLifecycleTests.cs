using System.Net;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Discovery;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Discovery;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReviewStatus = LeadPilot.Domain.Discovery.ReviewStatus;

namespace LeadPilot.IntegrationTests;

/// <summary>Full discovery lifecycle on MySQL with the TEST-ONLY fake connector: create → background run → results → review → CRM, plus idempotency, failures, cancellation and limits.</summary>
[Collection("app")]
public class DiscoveryLifecycleTests(AppFixture app)
{
    private TestWorld W => app.World;
    private Task<ApiSession> Api(TestUser u) => ApiSession.LoginAsync(app, u.Email, u.Password);
    private FakeScript Script => app.Script;

    private async Task<(ApiSession Admin, Guid CategoryId)> AdminWithSchoolCategoriesAsync()
    {
        var admin = await Api(W.AAdmin);
        await Crm.Ok<InstallPackResult>(await admin.Post($"/api/v1/discovery/starter-packs/{DiscoveryStarterPacks.EducationPakistan}/install"));
        var maps = await Crm.Ok<List<CategoryMappingDto>>(await admin.Get("/api/v1/discovery/categories"));
        return (admin, maps.Single(m => m.CategoryName == "Private schools").CategoryId);
    }

    [RequiresMySqlFact]
    public async Task A_user_can_search_schools_in_a_pakistani_city_and_the_results_are_saved_reviewed_and_approved_into_the_crm_with_their_source()
    {
        var (a, categoryId) = await AdminWithSchoolCategoriesAsync();
        var token = DiscoveryKit.NewToken(); Script.Set(token, new FakeScript.Behavior { Total = 7, PageSize = 3 });

        // 1. the search is created, tenant-scoped, and queued for the background worker
        var created = await DiscoveryKit.CreateJob(a, token, max: 7, categoryId: categoryId);
        Assert.Equal(DiscoveryJobStatus.Queued, created.Status); Assert.Equal("Private schools", created.CategoryName);
        var queued = app.Scheduler.LastFor(created.Id); Assert.Equal(W.A.Id, queued.TenantId);
        Assert.Equal(queued.Id, await W.WithDb(db => db.DiscoveryJobs.IgnoreQueryFilters().Where(j => j.Id == created.Id).Select(j => j.HangfireJobId).SingleAsync()));

        // 2. the worker runs it
        await DiscoveryKit.RunAsync(app, W.A.Id, created.Id);
        var job = await DiscoveryKit.GetJob(a, created.Id);
        Assert.Equal(DiscoveryJobStatus.Completed, job.Status); Assert.Equal(100, job.Progress);
        Assert.Equal((7, 7, 3, 3), (job.ResultsFound, job.ResultsNew, job.PagesFetched, job.ApiCalls)); Assert.Equal(3 * 0.035m, job.EstimatedCostUsd); Assert.Equal(7, job.PendingResults);
        Assert.Contains(Script.Calls, c => c.Token == token && c.QueryText.StartsWith("private school " + token) && c.QueryText.EndsWith("in Lahore, Punjab, Pakistan"));   // the category's search phrase + location were used

        // 3. results are stored with source information, retrieval time, provenance and a documented confidence
        var results = await DiscoveryKit.Results(a, created.Id);
        Assert.Equal(7, results.Count); Assert.All(results, r =>
        {
            Assert.Equal(ReviewStatus.Pending, r.ReviewStatus); Assert.Equal("Fake connector (TEST ONLY)", r.SourceName); Assert.NotNull(r.SourceUrl); Assert.Equal("Fake data for automated tests", r.Attribution);
            Assert.True(r.RetrievedAtUtc > DateTime.UtcNow.AddMinutes(-5)); Assert.Equal(r.RetrievedAtUtc.AddDays(30), r.ExpiresAtUtc); Assert.NotNull(r.LastVerifiedAtUtc);
            Assert.Equal("Private schools", r.CategoryId is null ? null : "Private schools"); Assert.Equal("Lahore", r.City); Assert.Equal("ProviderReported", r.Provenance["Name"]); Assert.Equal("ProviderReported", r.Provenance["Phone"]);
            Assert.Equal(35 + 10 + 15 + 10, r.Confidence);   // provider 35 + address 10 + phone 15 + category matched ("school" type / "private school" phrase) 10
            Assert.Equal(ConfidenceLevel.Medium, r.ConfidenceLevel);
        });
        Assert.Empty((await Crm.Page<LeadListItemDto>(await a.Get($"/api/v1/leads?search={token}"))).Items);   // nothing reaches the CRM before approval

        // 4. approve one: a lead with source + retrieval time + public contact
        var first = results.First(r => r.ExternalId == FakeScript.ExternalId(token, 0));
        var outcome = await Crm.Ok<ApprovalOutcomeDto>(await a.Post($"/api/v1/discovery/results/{first.Id}/approve", new { }));
        Assert.Equal(ApprovalAction.Created, outcome.Action);
        var lead = await Crm.Ok<LeadDto>(await a.Get($"/api/v1/leads/{outcome.LeadId}"));
        Assert.Equal(FakeScript.Name(token, 0), lead.Name); Assert.Equal(LeadSourceType.Discovery, lead.PrimarySourceType); Assert.Equal("Private schools", lead.CategoryName); Assert.Equal("Lahore", lead.City);
        var src = Assert.Single(lead.Sources); Assert.Equal(LeadSourceType.Discovery, src.Type); Assert.Equal("test-fake:" + first.ExternalId, src.ExternalId); Assert.Equal(first.RetrievedAtUtc, src.RetrievedAtUtc, TimeSpan.FromSeconds(1));
        var contact = Assert.Single(await Crm.Ok<List<ContactDto>>(await a.Get($"/api/v1/leads/{lead.Id}/contacts")));
        Assert.Equal(DataOrigin.Public, contact.DataOrigin); Assert.Empty(contact.Consent); Assert.NotNull(contact.Phone);
        var approved = await Crm.Ok<DiscoveryResultDto>(await a.Get($"/api/v1/discovery/results/{first.Id}"));
        Assert.Equal((ReviewStatus.Approved, outcome.LeadId, ApprovalAction.Created), (approved.ReviewStatus, approved.LeadId, approved.Approval));
        Assert.Single((await Crm.Page<LeadListItemDto>(await a.Get($"/api/v1/leads?search={token}"))).Items);   // visible in the CRM now

        // 5. a decided result cannot be decided again; reject another; bulk approve and bulk reject the rest
        Assert.Equal(HttpStatusCode.Conflict, (await a.Post($"/api/v1/discovery/results/{first.Id}/approve", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Post($"/api/v1/discovery/results/{first.Id}/reject", new { })).StatusCode);
        var second = results.First(r => r.ExternalId == FakeScript.ExternalId(token, 1));
        var rejected = await Crm.Ok<DiscoveryResultDto>(await a.Post($"/api/v1/discovery/results/{second.Id}/reject", new { note = "not a school" }));
        Assert.Equal((ReviewStatus.Rejected, "not a school"), (rejected.ReviewStatus, rejected.ReviewNote));
        var ids = results.Select(r => r.Id).Where(i => i != first.Id && i != second.Id).ToList();
        var bulk = await Crm.Ok<BulkReviewResult>(await a.Post("/api/v1/discovery/results/bulk-approve", new { resultIds = ids.Take(3) }));
        Assert.Equal((3, 3, 0), (bulk.Requested, bulk.Succeeded, bulk.Failed)); Assert.All(bulk.Items, i => Assert.Equal(ApprovalAction.Created, i.Action));
        var bulkReject = await Crm.Ok<BulkReviewResult>(await a.Post("/api/v1/discovery/results/bulk-reject", new { resultIds = ids.Skip(3), note = "bulk" }));
        Assert.Equal(2, bulkReject.Succeeded);
        Assert.Empty(await DiscoveryKit.Results(a, created.Id));
        Assert.Equal(4, (await DiscoveryKit.Results(a, created.Id, "Approved")).Count); Assert.Equal(3, (await DiscoveryKit.Results(a, created.Id, "Rejected")).Count);
        Assert.Equal(4, (await Crm.Page<LeadListItemDto>(await a.Get($"/api/v1/leads?search={token}"))).Items.Count);
        var audit = await W.WithDb(db => db.AuditLogs.IgnoreQueryFilters().Where(x => x.TenantId == W.A.Id && x.Action.StartsWith("Discovery.") && x.EntityId == created.Id.ToString()).Select(x => x.Action).ToListAsync());
        Assert.Contains("Discovery.JobCreated", audit); Assert.Contains("Discovery.JobCompleted", audit);
    }

    [RequiresMySqlFact]
    public async Task Discovered_contacts_have_no_consent_and_the_eligibility_gate_blocks_them_until_consent_is_recorded()
    {
        var a = await Api(W.AAdmin); var token = DiscoveryKit.NewToken(); Script.Set(token, new FakeScript.Behavior { Total = 1, WithEmail = true });
        var job = await DiscoveryKit.CreateAndRun(app, a, W.A.Id, token, 5);
        var result = (await DiscoveryKit.Results(a, job.Id)).Single();
        Assert.NotNull(result.PublicPhone); Assert.NotNull(result.PublicEmail);
        var outcome = await Crm.Ok<ApprovalOutcomeDto>(await a.Post($"/api/v1/discovery/results/{result.Id}/approve", new { }));
        var contact = Assert.Single(await Crm.Ok<List<ContactDto>>(await a.Get($"/api/v1/leads/{outcome.LeadId}/contacts")));
        Assert.Equal(DataOrigin.Public, contact.DataOrigin);

        // no consent record exists for it, on any channel
        Assert.Equal(0, await W.WithDb(db => db.ConsentRecords.IgnoreQueryFilters().CountAsync(c => c.ContactId == contact.Id)));
        using var scope = app.Web!.Services.CreateScope();
        await app.Web.Services.GetRequiredService<LeadPilot.Application.Abstractions.ITenantScopeRunner>().RunAsync(W.A.Id, async (sp, ct) =>
        {
            var gate = sp.GetRequiredService<IContactEligibilityService>();
            foreach (var channel in new[] { ConsentChannel.WhatsApp, ConsentChannel.Email, ConsentChannel.Sms })
                foreach (var purpose in new[] { ConsentPurpose.Marketing, ConsentPurpose.Service })
                    Assert.False((await gate.CheckAsync(contact.Id, channel, purpose, ct)).IsEligible, $"{channel}/{purpose} must be blocked for a discovered contact");
            var audience = await gate.QueryAudienceAsync(new AudienceQuery { Channel = ConsentChannel.WhatsApp, Purpose = ConsentPurpose.Marketing, PageSize = 100 }, ct);
            Assert.DoesNotContain(audience.Items, i => i.ContactId == contact.Id);
        });

        // the gate is not simply "closed": recording consent opens it (so the block above is caused by the missing consent)
        await Crm.Ok<ConsentRecordDto>(await a.Post($"/api/v1/contacts/{contact.Id}/consent", new { channel = "WhatsApp", purpose = "Marketing", status = "Granted", source = "signed form" }));
        await app.Web.Services.GetRequiredService<LeadPilot.Application.Abstractions.ITenantScopeRunner>().RunAsync(W.A.Id, async (sp, ct) =>
            Assert.True((await sp.GetRequiredService<IContactEligibilityService>().CheckAsync(contact.Id, ConsentChannel.WhatsApp, ConsentPurpose.Marketing, ct)).IsEligible));
    }

    [RequiresMySqlFact]
    public async Task Existing_leads_are_detected_as_duplicates_and_approval_merges_without_overwriting_or_creates_a_new_lead_on_request()
    {
        var a = await Api(W.AAdmin); var token = DiscoveryKit.NewToken(); Script.Set(token, new FakeScript.Behavior { Total = 3 });
        // an existing lead with the same name + city as result 0, another with the same phone as result 1
        var existing = await Crm.CreateLead(a, name: FakeScript.Name(token, 0), city: "Lahore", contact: false);
        var byPhone = await Crm.CreateLead(a, name: "Completely different name " + token, city: "Karachi", phone: FakeScript.Phone(token, 1).Replace(" ", ""));
        var job = await DiscoveryKit.CreateAndRun(app, a, W.A.Id, token, 10);
        Assert.Equal(2, job.DuplicatesFlagged);
        var results = (await DiscoveryKit.Results(a, job.Id)).OrderBy(r => r.ExternalId).ToList();
        Assert.Equal(existing.Id, results[0].DuplicateLeadId); Assert.Contains("same name and city", results[0].DuplicateReasons);
        Assert.Equal(byPhone.Id, results[1].DuplicateLeadId); Assert.Contains("same phone number", results[1].DuplicateReasons);
        Assert.Null(results[2].DuplicateLeadId);
        Assert.Equal(2, (await Crm.Page<DiscoveryResultDto>(await a.Get($"/api/v1/discovery/results?jobId={job.Id}&possibleDuplicate=true"))).TotalCount);

        // merge: the existing lead gets the missing data, keeps what it had, and gains the source; no second lead is created
        var merged = await Crm.Ok<ApprovalOutcomeDto>(await a.Post($"/api/v1/discovery/results/{results[0].Id}/approve", new { }));
        Assert.Equal((existing.Id, ApprovalAction.Merged), (merged.LeadId, merged.Action));
        var lead = await Crm.Ok<LeadDto>(await a.Get($"/api/v1/leads/{existing.Id}"));
        Assert.Equal(FakeScript.Name(token, 0), lead.Name); Assert.Equal("Lahore", lead.City); Assert.Equal("Punjab", lead.Province); Assert.NotNull(lead.Address);
        Assert.Contains(lead.Sources, s => s.Type == LeadSourceType.Discovery); Assert.Equal(1, lead.ContactCount);
        Assert.Single((await Crm.Page<LeadListItemDto>(await a.Get($"/api/v1/leads?search={FakeScript.Name(token, 0)}"))).Items);

        // explicit "create anyway" makes a second lead
        var again = await Crm.Ok<ApprovalOutcomeDto>(await a.Post($"/api/v1/discovery/results/{results[1].Id}/approve", new { onDuplicate = "CreateNew" }));
        Assert.Equal(ApprovalAction.Created, again.Action); Assert.NotEqual(byPhone.Id, again.LeadId);
        // the clean result simply creates
        Assert.Equal(ApprovalAction.Created, (await Crm.Ok<ApprovalOutcomeDto>(await a.Post($"/api/v1/discovery/results/{results[2].Id}/approve", new { }))).Action);
    }

    [RequiresMySqlFact]
    public async Task A_retried_job_resumes_from_its_checkpoint_and_never_duplicates_results_or_leads()
    {
        var a = await Api(W.AAdmin); var token = DiscoveryKit.NewToken();
        Script.Set(token, new FakeScript.Behavior { Total = 7, FailAtPage = 1, FailuresRemaining = 1, Failure = () => new ConnectorTransientException("Google Places is temporarily unavailable (HTTP 503).", new ConnectorCall("fake.search", 1, 0m, 503)) });
        var job = await DiscoveryKit.CreateAndRun(app, a, W.A.Id, token, 7);

        // failure is reported with its details, partial results are kept
        Assert.Equal(DiscoveryJobStatus.Failed, job.Status); Assert.Equal("provider_unavailable", job.ErrorCode); Assert.True(job.ErrorRetryable); Assert.Contains("503", job.ErrorMessage);
        Assert.Equal((3, 1), (job.ResultsNew, job.PagesFetched)); Assert.Equal(2, job.ApiCalls);   // page 0 succeeded, page 1 failed (still counted)
        Assert.Equal(3, (await DiscoveryKit.Results(a, job.Id)).Count);

        // retry resumes at page 1 (does not re-fetch page 0) and completes
        var retried = await Crm.Ok<DiscoveryJobDto>(await a.Post($"/api/v1/discovery/jobs/{job.Id}/retry"));
        Assert.Equal(DiscoveryJobStatus.Queued, retried.Status); Assert.Equal(2, app.Scheduler.Enqueued.Count(e => e.JobId == job.Id));
        await DiscoveryKit.RunAsync(app, W.A.Id, job.Id);
        var done = await DiscoveryKit.GetJob(a, job.Id);
        Assert.Equal(DiscoveryJobStatus.Completed, done.Status); Assert.Equal(7, done.ResultsNew); Assert.Equal(2, done.Attempts); Assert.Null(done.ErrorCode);
        Assert.Equal(1, Script.Calls.Count(c => c.Token == token && c.PageToken is null)); Assert.Equal(2, Script.Calls.Count(c => c.Token == token && c.PageToken == "3"));
        var results = await DiscoveryKit.Results(a, job.Id);
        Assert.Equal(7, results.Count); Assert.Equal(7, results.Select(r => r.ExternalId).Distinct().Count());

        // a completed job cannot be retried; running it again is a no-op
        Assert.Equal(HttpStatusCode.Conflict, (await a.Post($"/api/v1/discovery/jobs/{job.Id}/retry")).StatusCode);
        await DiscoveryKit.RunAsync(app, W.A.Id, job.Id);
        Assert.Equal(7, (await DiscoveryKit.Results(a, job.Id)).Count);

        // approving before AND after retries yields exactly one lead per place
        var ids = results.Select(r => r.Id).ToList();
        await Crm.Ok<BulkReviewResult>(await a.Post("/api/v1/discovery/results/bulk-approve", new { resultIds = ids }));
        Assert.Equal(7, (await Crm.Page<LeadListItemDto>(await a.Get($"/api/v1/leads?search={token}&pageSize=50"))).TotalCount);
    }

    [RequiresMySqlFact]
    public async Task A_repeated_search_finds_the_same_places_without_creating_duplicate_results_and_the_database_enforces_it()
    {
        var a = await Api(W.AAdmin); var token = DiscoveryKit.NewToken(); Script.Set(token, new FakeScript.Behavior { Total = 5 });
        var first = await DiscoveryKit.CreateAndRun(app, a, W.A.Id, token, 10);
        var second = await DiscoveryKit.CreateAndRun(app, a, W.A.Id, token, 10);
        Assert.Equal((5, 0, 5), (first.ResultsNew, second.ResultsNew, second.ResultsAlreadyKnown));
        Assert.Equal(5, (await Crm.Page<DiscoveryResultDto>(await a.Get($"/api/v1/discovery/results?search={token}&status=&pageSize=100"))).TotalCount);
        Assert.Empty(await DiscoveryKit.Results(a, second.Id));   // results belong to the first search

        // rerun from search history = a new job, still no duplicates
        var rerun = await Crm.Ok<DiscoveryJobDto>(await a.Post($"/api/v1/discovery/jobs/{second.Id}/rerun"));
        Assert.NotEqual(second.Id, rerun.Id); await DiscoveryKit.RunAsync(app, W.A.Id, rerun.Id);
        Assert.Equal(0, (await DiscoveryKit.GetJob(a, rerun.Id)).ResultsNew);
        var hist = await Crm.Page<SearchHistoryItemDto>(await a.Get("/api/v1/discovery/jobs/history?pageSize=100"));
        Assert.Equal(3, hist.Items.Single(h => h.Keywords == token).Runs);

        // the unique index (tenant, connector, external id) rejects a duplicate row even when service code is bypassed
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => W.WithDb(async db =>
        {
            var existing = await db.DiscoveryResults.IgnoreQueryFilters().AsNoTracking().FirstAsync(r => r.TenantId == W.A.Id && r.ExternalId == FakeScript.ExternalId(token, 0));
            db.DiscoveryResults.Add(DiscoveryResult.Create(W.A.Id, existing.JobId, new("test-fake", existing.ExternalId, "dup", null, null, null, null, null, null, null, null, null, null, null, null, "x", null, null, DateTime.UtcNow, null, null, 50, null, null, EnrichmentStatus.NotRequested), null, null, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }));
        Assert.Contains("Duplicate entry", ex.InnerException?.Message ?? ex.Message);
    }

    [RequiresMySqlFact]
    public async Task Provider_failures_are_reported_honestly_with_their_code_and_whether_a_retry_makes_sense()
    {
        var a = await Api(W.AAdmin);
        async Task<DiscoveryJobDto> Fail(Func<Exception> ex)
        {
            var token = DiscoveryKit.NewToken(); Script.Set(token, new FakeScript.Behavior { Total = 5, FailAtPage = 0, FailuresRemaining = 5, Failure = ex });
            return await DiscoveryKit.CreateAndRun(app, a, W.A.Id, token, 5);
        }
        var quota = await Fail(() => new ConnectorQuotaException("Google Places quota or rate limit reached (HTTP 429).", new ConnectorCall("fake.search", 1, 0m, 429)));
        Assert.Equal((DiscoveryJobStatus.Failed, "provider_quota_exceeded", true), (quota.Status, quota.ErrorCode, quota.ErrorRetryable)); Assert.Equal(1, quota.ApiCalls);
        var auth = await Fail(() => new ConnectorAuthException("Google rejected the API key (HTTP 403).", new ConnectorCall("fake.search", 1, 0m, 403)));
        Assert.Equal(("connector_auth_failed", false), (auth.ErrorCode, auth.ErrorRetryable)); Assert.Contains("403", auth.ErrorMessage);
        var bad = await Fail(() => new ConnectorPermanentException("rejected", new ConnectorCall("fake.search", 1, 0m, 400)));
        Assert.Equal(("provider_rejected_request", false), (bad.ErrorCode, bad.ErrorRetryable));
        Assert.Equal(0, auth.ResultsNew);

        // calls that failed are in the usage ledger with their HTTP status
        var usage = await Crm.Ok<UsageSummaryDto>(await a.Get("/api/v1/discovery/connectors/usage?pageSize=100"));
        Assert.Contains(usage.Recent.Items, u => u.JobId == quota.Id && u.HttpStatus == 429 && u.EstimatedCostUsd == 0m);
        // retrying a still-failing search fails again (and says so) instead of looping
        var retried = await Crm.Ok<DiscoveryJobDto>(await a.Post($"/api/v1/discovery/jobs/{quota.Id}/retry"));
        await DiscoveryKit.RunAsync(app, W.A.Id, retried.Id);
        Assert.Equal((DiscoveryJobStatus.Failed, 2), ((await DiscoveryKit.GetJob(a, quota.Id)).Status, (await DiscoveryKit.GetJob(a, quota.Id)).Attempts));
    }

    [RequiresMySqlFact]
    public async Task Unexpected_errors_are_retried_automatically_a_bounded_number_of_times_and_then_reported_as_failed()
    {
        var a = await Api(W.AAdmin); var token = DiscoveryKit.NewToken();
        Script.Set(token, new FakeScript.Behavior { Total = 5, FailAtPage = 0, FailuresRemaining = 99, Failure = () => new InvalidOperationException("boom with secret details") });
        var job = await DiscoveryKit.CreateJob(a, token, 5);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => DiscoveryKit.RunAsync(app, W.A.Id, job.Id));   // rethrown so Hangfire schedules its automatic retry
            var q = await DiscoveryKit.GetJob(a, job.Id); Assert.Equal((DiscoveryJobStatus.Queued, attempt), (q.Status, q.Attempts));
        }
        await DiscoveryKit.RunAsync(app, W.A.Id, job.Id);   // third attempt: give up, mark failed, do not throw
        var failed = await DiscoveryKit.GetJob(a, job.Id);
        Assert.Equal((DiscoveryJobStatus.Failed, "internal_error", 3), (failed.Status, failed.ErrorCode, failed.Attempts)); Assert.DoesNotContain("secret", failed.ErrorMessage);   // internal details are not shown
    }

    [RequiresMySqlFact]
    public async Task A_queued_search_is_cancelled_at_once_and_a_running_one_stops_after_the_current_page_keeping_its_results()
    {
        var a = await Api(W.AAdmin);
        var queuedToken = DiscoveryKit.NewToken(); Script.Set(queuedToken, new FakeScript.Behavior { Total = 5 });
        var q = await DiscoveryKit.CreateJob(a, queuedToken, 5);
        var cancelled = await Crm.Ok<DiscoveryJobDto>(await a.Post($"/api/v1/discovery/jobs/{q.Id}/cancel"));
        Assert.Equal(DiscoveryJobStatus.Cancelled, cancelled.Status); Assert.Contains(app.Scheduler.LastFor(q.Id).Id, app.Scheduler.Deleted);
        await DiscoveryKit.RunAsync(app, W.A.Id, q.Id);   // a worker that still picks it up does nothing
        Assert.Equal((DiscoveryJobStatus.Cancelled, 0), ((await DiscoveryKit.GetJob(a, q.Id)).Status, (await DiscoveryKit.GetJob(a, q.Id)).ApiCalls));
        Assert.Equal(HttpStatusCode.OK, (await a.Post($"/api/v1/discovery/jobs/{q.Id}/retry")).StatusCode);   // cancelled searches can be retried

        var token = DiscoveryKit.NewToken(); Guid jobId = Guid.Empty;
        Script.Set(token, new FakeScript.Behavior { Total = 9, OnCall = (_, page) => { if (page == 1) a.Post($"/api/v1/discovery/jobs/{jobId}/cancel").GetAwaiter().GetResult(); } });
        var running = await DiscoveryKit.CreateJob(a, token, 9); jobId = running.Id;
        await DiscoveryKit.RunAsync(app, W.A.Id, running.Id);
        var stopped = await DiscoveryKit.GetJob(a, running.Id);
        Assert.Equal(DiscoveryJobStatus.Cancelled, stopped.Status); Assert.Equal((6, 2), (stopped.ResultsNew, stopped.PagesFetched));   // pages 0 and 1 were stored; page 2 was never requested
        Assert.Equal(2, Script.Calls.Count(c => c.Token == token)); Assert.Equal(6, (await DiscoveryKit.Results(a, running.Id)).Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post($"/api/v1/discovery/jobs/{(await DiscoveryKit.CreateAndRun(app, a, W.A.Id, DiscoveryKit.NewToken().Also(t => Script.Set(t, new FakeScript.Behavior { Total = 1 })), 1)).Id}/cancel")).StatusCode);   // finished: cannot be cancelled
    }

    [RequiresMySqlFact]
    public async Task The_monthly_limit_is_enforced_when_creating_and_while_running_and_usage_and_estimated_cost_are_tracked()
    {
        var (tenant, admin, _) = await W.NewTenantAsync("lim");
        var a = await ApiSession.LoginAsync(app, admin.Email, admin.Password);
        await Crm.Ok<ConnectorStatusDto>(await a.Put($"/api/v1/discovery/connectors/{DiscoveryKit.Fake}", new { updateLimits = true, monthlyCallLimit = 2, costPerCallUsd = 0.5 }));
        var token = DiscoveryKit.NewToken(); Script.Set(token, new FakeScript.Behavior { Total = 9 });

        var job = await DiscoveryKit.CreateAndRun(app, a, tenant.Id, token, 9);   // would need 3 pages
        Assert.Equal((DiscoveryJobStatus.Failed, "usage_limit_reached", true), (job.Status, job.ErrorCode, job.ErrorRetryable)); Assert.Equal((2, 6), (job.ApiCalls, job.ResultsNew)); Assert.Equal(1.0m, job.EstimatedCostUsd);
        Assert.Equal(2, Script.Calls.Count(c => c.Token == token));   // the third call was never made
        var status = (await Crm.Ok<List<ConnectorStatusDto>>(await a.Get("/api/v1/discovery/connectors"))).Single(c => c.Key == DiscoveryKit.Fake);
        Assert.Equal((2, 2, 1.0m), (status.CallsThisMonth, status.EffectiveMonthlyCallLimit, status.CostThisMonth));

        // creating or retrying is refused (429) while the limit is used up
        var blocked = await a.Post("/api/v1/discovery/jobs", new { connectorType = DiscoveryKit.Fake, country = "Pakistan", keywords = token, maxResults = 5 });
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode); Assert.Equal("usage_limit_reached", await Crm.ProblemCode(blocked));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await a.Post($"/api/v1/discovery/jobs/{job.Id}/retry")).StatusCode);

        // raising the limit lets the retry finish (resuming; no duplicates)
        await Crm.Ok<ConnectorStatusDto>(await a.Put($"/api/v1/discovery/connectors/{DiscoveryKit.Fake}", new { updateLimits = true, monthlyCallLimit = 10, costPerCallUsd = 0.5 }));
        await Crm.Ok<DiscoveryJobDto>(await a.Post($"/api/v1/discovery/jobs/{job.Id}/retry"));
        await DiscoveryKit.RunAsync(app, tenant.Id, job.Id);
        var done = await DiscoveryKit.GetJob(a, job.Id);
        Assert.Equal((DiscoveryJobStatus.Completed, 9, 3), (done.Status, done.ResultsNew, done.ApiCalls)); Assert.Equal(9, (await DiscoveryKit.Results(a, job.Id)).Count);
    }

    [RequiresMySqlFact]
    public async Task Connectors_that_are_not_configured_say_so_and_never_produce_data()
    {
        var a = await Api(W.AAdmin);
        foreach (var key in new[] { ConnectorKeys.AuthorizedDirectory, ConnectorKeys.LeadGenIntegration, ConnectorKeys.MetaLeadAds, ConnectorKeys.GooglePlaces, ConnectorKeys.CsvImport, ConnectorKeys.ManualEntry })
        {
            var res = await a.Post("/api/v1/discovery/jobs", new { connectorType = key, country = "Pakistan", city = "Lahore", keywords = "school", maxResults = 5 });
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode); Assert.Equal("connector_not_configured", await Crm.ProblemCode(res));
        }
        Assert.Contains("API key", await Crm.ProblemDetail(await a.Post("/api/v1/discovery/jobs", new { connectorType = ConnectorKeys.GooglePlaces, country = "Pakistan", keywords = "school", maxResults = 5 })));
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/discovery/jobs", new { connectorType = "nonsense", country = "Pakistan", keywords = "x", maxResults = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/discovery/jobs", new { connectorType = DiscoveryKit.Fake, keywords = "x", maxResults = 5 })).StatusCode);          // no location
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/discovery/jobs", new { connectorType = DiscoveryKit.Fake, country = "Pakistan", maxResults = 5 })).StatusCode);  // nothing to search for
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Post("/api/v1/discovery/jobs", new { connectorType = DiscoveryKit.Fake, country = "Pakistan", keywords = "x", maxResults = 100000 })).StatusCode);
        var list = await Crm.Ok<List<ConnectorStatusDto>>(await a.Get("/api/v1/discovery/connectors"));
        Assert.Equal(["Needs an API key"], list.Where(c => c.Key == ConnectorKeys.GooglePlaces).Select(c => c.StatusText));
        Assert.All(list.Where(c => c.Kind == ConnectorKind.ExtensionPoint), c => { Assert.False(c.IsReady); Assert.Equal("Not configured", c.StatusText); });
        Assert.True(list.Single(c => c.Key == DiscoveryKit.Fake).IsTestOnly);
    }

    [RequiresMySqlFact]
    public async Task Website_enrichment_adds_only_what_the_site_publishes_marks_it_unverified_and_corroborates_matching_phones()
    {
        var a = await Api(W.AAdmin); var token = DiscoveryKit.NewToken();
        Script.Set(token, new FakeScript.Behavior { Total = 3, WithWebsite = true });
        // 0: site matches and repeats the provider phone; 1: site shows another phone + an e-mail; 2: site cannot be read
        FakeEnricher.ByHost[$"{token}-0.school.example"] = new(WebsiteFetchOutcome.Fetched, new WebsiteInfo($"https://{token}-0.school.example/", $"{token} School 000 | Home", $"{token} School 000", null, [FakeScript.Phone(token, 0)], [], [], [], []), null);
        FakeEnricher.ByHost[$"{token}-1.school.example"] = new(WebsiteFetchOutcome.Fetched, new WebsiteInfo($"https://{token}-1.school.example/", "Welcome", null, null, ["+92 21 999 8888"], ["office@school.example"], ["https://facebook.com/x"], [], []), null);
        var job = await DiscoveryKit.CreateAndRun(app, a, W.A.Id, token, 5, enrich: true);
        var r = (await DiscoveryKit.Results(a, job.Id)).OrderBy(x => x.ExternalId).ToList();

        Assert.Equal("Corroborated", r[0].Provenance["Phone"]); Assert.Equal(EnrichmentStatus.Done, r[0].Enrichment); Assert.Contains("also appears on the website", r[0].ResearchNotes);
        Assert.Equal(35 + 10 + 10 + 15 + 10 + 5, r[0].Confidence);   // provider 35 + address 10 + website 10 + phone 15 + site confirmed 10 + corroborated 5 (no category searched, so no category points)
        Assert.Equal(FakeScript.Phone(token, 1), r[1].PublicPhone);       // the provider's phone is kept; the site's different number is not substituted
        Assert.Contains("different phone number (+92 21 999 8888)", r[1].ResearchNotes); Assert.Equal("office@school.example", r[1].PublicEmail); Assert.Equal("Unverified", r[1].Provenance["Email"]);
        Assert.Contains("does not clearly match", r[1].ResearchNotes); Assert.Contains("not consent", r[1].ResearchNotes); Assert.Equal(["https://facebook.com/x"], r[1].SocialLinks);
        Assert.Equal(EnrichmentStatus.Skipped, r[2].Enrichment); Assert.Contains("Website not read", r[2].ResearchNotes); Assert.Null(r[2].PublicEmail);
        Assert.DoesNotContain("students", r.Select(x => x.ResearchNotes ?? "").Aggregate((x, y) => x + y), StringComparison.OrdinalIgnoreCase);
    }

    [RequiresMySqlFact]
    public async Task The_website_connector_reads_the_addresses_it_is_given_records_warnings_and_invents_nothing()
    {
        var (tenant, admin, _) = await W.NewTenantAsync("web");
        var a = await ApiSession.LoginAsync(app, admin.Email, admin.Password); var host = $"{DiscoveryKit.NewToken()}.school.example";
        // the fake enricher is the website connector's only dependency here, so no network is used
        FakeEnricher.ByHost[host] = new(WebsiteFetchOutcome.Fetched, new WebsiteInfo($"https://{host}/", "Bright Future Academy", "Bright Future Academy", "1 Mall Road, Lahore", ["+92 42 111 000"], ["info@" + host], [], [], []), null);
        var res = await a.Post("/api/v1/discovery/jobs", new { connectorType = ConnectorKeys.Website, seedUrls = $"https://{host}/\nhttps://blocked-{host}/\nnot a url", maxResults = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);   // the invalid line is rejected up front
        res = await a.Post("/api/v1/discovery/jobs", new { connectorType = ConnectorKeys.Website, seedUrls = $"https://{host}/ https://blocked-{host}/", maxResults = 10 });
        var job = await ApiSession.Json<DiscoveryJobDto>(res);
        await DiscoveryKit.RunAsync(app, tenant.Id, job.Id);
        job = await DiscoveryKit.GetJob(a, job.Id);
        Assert.Equal((DiscoveryJobStatus.Completed, 1, 0m), (job.Status, job.ResultsNew, job.EstimatedCostUsd)); Assert.Contains($"blocked-{host}", job.Warnings);
        var r = (await DiscoveryKit.Results(a, job.Id)).Single();
        Assert.Equal("Bright Future Academy", r.Name); Assert.Equal("1 Mall Road, Lahore", r.Address); Assert.Equal("+92 42 111 000", r.PublicPhone);
        Assert.All(new[] { "Name", "Address", "Phone", "Email" }, f => Assert.Equal("Unverified", r.Provenance[f]));   // self-published values are unverified
        Assert.Null(r.City); Assert.Null(r.Country); Assert.Null(r.CategoryLabel); Assert.Equal("website:" + host, r.ExternalId);
    }

    [RequiresMySqlFact]
    public async Task Provider_content_is_removed_after_its_retention_period_but_the_external_id_is_kept()
    {
        var a = await Api(W.AAdmin); var token = DiscoveryKit.NewToken(); Script.Set(token, new FakeScript.Behavior { Total = 2 });
        var job = await DiscoveryKit.CreateAndRun(app, a, W.A.Id, token, 5);
        var r = await DiscoveryKit.Results(a, job.Id);
        await W.WithDb(db => db.DiscoveryResults.IgnoreQueryFilters().Where(x => x.Id == r[0].Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAtUtc, DateTime.UtcNow.AddDays(-1))));
        int purged = 0;
        await app.Web!.Services.GetRequiredService<LeadPilot.Application.Abstractions.ITenantScopeRunner>().RunAsync(W.A.Id, async (sp, ct) => purged = await sp.GetRequiredService<IDiscoveryRetention>().PurgeExpiredAsync(ct));
        Assert.True(purged >= 1);
        var after = await Crm.Ok<DiscoveryResultDto>(await a.Get($"/api/v1/discovery/results/{r[0].Id}"));
        Assert.True(after.ContentPurged); Assert.Null(after.Name); Assert.Null(after.PublicPhone); Assert.Null(after.Address); Assert.Equal(r[0].ExternalId, after.ExternalId);
        Assert.Equal(HttpStatusCode.Conflict, (await a.Post($"/api/v1/discovery/results/{r[0].Id}/approve", new { })).StatusCode);   // content is gone: re-run the search
        Assert.Equal(HttpStatusCode.OK, (await a.Post($"/api/v1/discovery/results/{r[1].Id}/approve", new { })).StatusCode);          // others unaffected
        var again = await DiscoveryKit.CreateAndRun(app, a, W.A.Id, token, 5);
        Assert.Equal(0, again.ResultsNew);   // the purged place is still known by its id, so it is not offered a second time
    }

    [RequiresMySqlFact]
    public async Task Education_workspaces_get_the_school_categories_automatically_other_workspaces_do_not_and_everything_is_editable()
    {
        var (a, _) = await AdminWithSchoolCategoriesAsync();
        var maps = await Crm.Ok<List<CategoryMappingDto>>(await a.Get("/api/v1/discovery/categories"));
        foreach (var n in new[] { "Private schools", "Primary schools", "Secondary schools", "Higher secondary schools", "Colleges", "Academies", "Educational institutions" }) Assert.Contains(maps, m => m.CategoryName == n);
        Assert.Contains((await Crm.Ok<List<LocationPresetDto>>(await a.Get("/api/v1/discovery/locations"))), l => l.City == "Lahore" && l.Country == "Pakistan");
        var again = await Crm.Ok<InstallPackResult>(await a.Post($"/api/v1/discovery/starter-packs/{DiscoveryStarterPacks.EducationPakistan}/install"));
        Assert.Equal((0, 0, 0), (again.CategoriesCreated, again.MappingsCreated, again.LocationsCreated));   // idempotent

        // editable: change phrases, deactivate, saved locations
        var primary = maps.Single(m => m.CategoryName == "Primary schools");
        var edited = await Crm.Ok<CategoryMappingDto>(await a.Put("/api/v1/discovery/categories", new { categoryId = primary.CategoryId, searchTerms = "primary school\nmontessori", placeType = "primary_school" }));
        Assert.Equal(["primary school", "montessori"], edited.SearchTerms);
        again = await Crm.Ok<InstallPackResult>(await a.Post($"/api/v1/discovery/starter-packs/{DiscoveryStarterPacks.EducationPakistan}/install"));
        Assert.Equal(["primary school", "montessori"], (await Crm.Ok<List<CategoryMappingDto>>(await a.Get("/api/v1/discovery/categories"))).Single(m => m.CategoryId == primary.CategoryId).SearchTerms);   // re-installing never overwrites edits
        Assert.False((await Crm.Ok<CategoryMappingDto>(await a.Put($"/api/v1/discovery/categories/{primary.Id}/active?active=false", new { }))).IsActive);
        var loc = await Crm.Ok<LocationPresetDto>(await a.Post("/api/v1/discovery/locations", new { country = "Saudi Arabia", city = "Riyadh " + DiscoveryKit.NewToken() }));
        Assert.Equal(HttpStatusCode.NoContent, (await a.Delete($"/api/v1/discovery/locations/{loc.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Put("/api/v1/discovery/categories", new { categoryId = primary.CategoryId, searchTerms = " " })).StatusCode);

        // not hard-coded: a Retail workspace has no school categories and no mappings unless it asks
        var b = await Api(W.BAdmin);
        Assert.Empty(await Crm.Ok<List<CategoryMappingDto>>(await b.Get("/api/v1/discovery/categories")));
        Assert.DoesNotContain((await Crm.Ok<CrmLookupsDto>(await b.Get("/api/v1/leads/lookups"))).Categories, c => c.Name == "Private schools");
        // a category without phrases is searched by its own name
        var cat = await Crm.Ok<CategoryDto>(await b.Post("/api/v1/categories", new { name = "Coffee roasters " + DiscoveryKit.NewToken() }));
        var token = DiscoveryKit.NewToken(); Script.Set(token, new FakeScript.Behavior { Total = 1 });
        await DiscoveryKit.CreateAndRun(app, b, W.B.Id, token, 3, categoryId: cat.Id);
        Assert.Contains(Script.Calls, c => c.Token == token && c.QueryText.StartsWith(cat.Name.ToLowerInvariant().Length > 0 ? cat.Name : ""));
    }
}

internal static class TestExtensions { public static T Also<T>(this T value, Action<T> action) { action(value); return value; } }
