using System.Text.Json;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Discovery;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Discovery;
using LeadPilot.Infrastructure.Discovery.Services;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TenancyResultFlow = LeadPilot.Infrastructure.Services.Tenancy.ResultFlow;

namespace LeadPilot.Infrastructure.Discovery.Jobs;

/// <summary>
/// Executes one discovery job inside the ACTIVE tenant scope (the Hangfire entry point establishes it with <see cref="ITenantScopeRunner"/>; without one every call fails closed).
/// Pagination, rate limiting, monthly limit enforcement, cancellation between pages, normalisation, duplicate detection against the CRM, optional website enrichment, confidence
/// calculation, idempotent storage (unique per tenant + connector + external id) and a resume checkpoint after every page.
/// </summary>
public sealed class DiscoveryJobRunner(
    CrmContext crm, IDiscoveryConnectorRegistry registry, DiscoveryUsage usage, DuplicateDetector duplicates, IWebsiteEnricher enricher, ISecretProtector secrets,
    IOptions<DiscoveryOptions> options, ILogger<DiscoveryJobRunner> logger) : IDiscoveryJobRunner
{
    private const int MaxInfrastructureAttempts = 3;
    private sealed record Checkpoint(int Query, string? Token, int Pages);
    private sealed class UsageLimitReached(string reason) : Exception(reason);

    private DateTime _lastCall = DateTime.MinValue;
    private int _enrichedThisRun;

    public async Task RunAsync(Guid jobId, CancellationToken ct)
    {
        var tenantId = crm.TenantId;   // throws TenantContextMissingException outside a tenant scope: fail closed
        var db = crm.Db; var now = crm.Now; var o = options.Value;

        // atomic claim: only one worker can move Queued (or a stale Running) job to Running
        var stale = now - TimeSpan.FromMinutes(o.StaleRunningMinutes);
        var claimed = await db.DiscoveryJobs.Where(j => j.Id == jobId && j.TenantId == tenantId && (j.Status == DiscoveryJobStatus.Queued || (j.Status == DiscoveryJobStatus.Running && j.StartedAtUtc < stale)))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, DiscoveryJobStatus.Running).SetProperty(j => j.StartedAtUtc, now).SetProperty(j => j.CompletedAtUtc, (DateTime?)null)
                .SetProperty(j => j.Attempts, j => j.Attempts + 1).SetProperty(j => j.ErrorCode, (string?)null).SetProperty(j => j.ErrorMessage, (string?)null).SetProperty(j => j.ErrorRetryable, false), ct);
        if (claimed == 0)
        {
            logger.LogWarning("Discovery job {JobId} was not started: unknown for this tenant, already running, finished or cancelled", jobId);
            return;
        }
        var job = await db.DiscoveryJobs.FirstAsync(j => j.Id == jobId && j.TenantId == tenantId, ct);
        try { await ProcessAsync(job, ct); }
        catch (ConnectorException ex)
        {
            logger.LogWarning("Discovery job {JobId} failed: {Code}", jobId, ex.Code);
            await FinishAsync(jobId, j => j.Fail(ex.Code, ex.Message, ex.Retryable, crm.Now), ex.Call, CancellationToken.None);
        }
        catch (UsageLimitReached ex)
        {
            await FinishAsync(jobId, j => j.Fail(Services.DiscoveryJobService.UsageLimitCode, ex.Message, retryable: true, crm.Now), null, CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FinishAsync(jobId, j => j.Requeue("The worker was stopped; the search will resume."), null, CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Discovery job {JobId} hit an unexpected error", jobId);
            var attempts = job.Attempts;
            if (attempts < MaxInfrastructureAttempts)
            {
                await FinishAsync(jobId, j => j.Requeue("A temporary error occurred; the search is retried automatically."), null, CancellationToken.None);
                throw;   // Hangfire's automatic retry runs the job again
            }
            await FinishAsync(jobId, j => j.Fail("internal_error", "The search failed because of an internal error. It can be retried; no data was duplicated.", retryable: true, crm.Now), null, CancellationToken.None);
        }
    }

    /// <summary>Re-reads the job (the tracker may be dirty after a failure), applies the final state and records the failed call's usage.</summary>
    private async Task FinishAsync(Guid jobId, Action<DiscoveryJob> apply, ConnectorCall? call, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        db.ChangeTracker.Clear();
        var job = await db.DiscoveryJobs.FirstAsync(j => j.Id == jobId && j.TenantId == tenantId, ct);
        if (call is not null) { usage.Record(jobId, job.ConnectorType, call); job.AddUsage(call.Calls, call.EstimatedCostUsd); }
        apply(job);
        crm.Audit.Record(new AuditEntry(job.Status switch { DiscoveryJobStatus.Failed => "Discovery.JobFailed", DiscoveryJobStatus.Cancelled => "Discovery.JobCancelled", DiscoveryJobStatus.Completed => "Discovery.JobCompleted", _ => "Discovery.JobInterrupted" },
            "DiscoveryJob", jobId.ToString(), job.Status == DiscoveryJobStatus.Failed ? $"Search failed: {job.ErrorCode}." : $"Search {job.Status.ToString().ToLowerInvariant()}: {job.ResultsNew} new result(s).", ActorUserId: job.RequestedByUserId));
        await db.SaveChangesAsync(ct);
    }

    private async Task ProcessAsync(DiscoveryJob job, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var o = options.Value;
        var connector = registry.Find(job.ConnectorType) ?? throw new ConnectorNotConfiguredException("This connector is no longer available.");
        var settings = await usage.SettingsAsync(job.ConnectorType, ct);
        if (!settings.Enabled) throw new ConnectorNotConfiguredException("The connector is disabled for this workspace.");
        string? apiKey = null;
        if (connector.Descriptor.RequiresCredentials)
        {
            if (!settings.HasKey) throw new ConnectorNotConfiguredException("No API key is configured for this connector in this workspace.");
            apiKey = secrets.Unprotect(settings.Config!.ProtectedApiKey!);   // THIS tenant's key: the config row was read with this tenant's predicate
        }

        var (queries, mapping, category) = await BuildQueriesAsync(job, connector.Descriptor, ct);
        if (queries.Count == 0) throw new ConnectorPermanentException("The search has no usable query (no category phrases or keywords).");
        var seed = (job.SeedUrls ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var cp = ParseCheckpoint(job.Checkpoint);
        var country = await crm.DefaultCountryCodeAsync(ct);
        var used = await usage.UsedAsync(job.ConnectorType, ct);
        var callsUsed = used.Calls; var costUsed = used.Cost;

        for (var qi = cp.Query; qi < queries.Count; qi++)
        {
            var token = qi == cp.Query ? cp.Token : null; var pages = qi == cp.Query ? cp.Pages : 0;
            if (qi == cp.Query && cp.Pages > 0 && cp.Token is null) continue;   // that query finished before the interruption
            do
            {
                ct.ThrowIfCancellationRequested();
                if (await db.DiscoveryJobs.AsNoTracking().Where(j => j.Id == job.Id && j.TenantId == tenantId).Select(j => j.CancelRequested).FirstAsync(ct))
                {
                    await FinishAsync(job.Id, j => j.MarkCancelled(crm.Now), null, ct);
                    return;
                }
                if (job.ResultsFound >= job.MaxResults) break;
                var decision = UsageLimitPolicy.Check(callsUsed, costUsed, settings.CallLimit, settings.Budget, 1, settings.CostPerCall);
                if (!decision.Allowed) throw new UsageLimitReached(decision.Reason!);
                await RateLimitAsync(o.PageDelayMs, ct);

                var page = await connector.SearchAsync(new ConnectorRequest(queries[qi], apiKey, token, Math.Clamp(o.PageSize, 1, 20), seed, settings.CostPerCall, crm.Now), ct);
                _lastCall = DateTime.UtcNow;
                foreach (var c in page.Calls) { usage.Record(job.Id, job.ConnectorType, c); job.AddUsage(c.Calls, c.EstimatedCostUsd); callsUsed += c.Calls; costUsed += c.EstimatedCostUsd; }
                foreach (var w in page.Warnings ?? []) job.AddWarning(w);

                var stats = await StoreAsync(job, page.Candidates.Take(Math.Max(0, job.MaxResults - job.ResultsFound)).ToList(), connector.Descriptor, mapping, category, country, ct);
                token = page.NextPageToken; pages++;
                job.Progress(1, stats.Found, stats.Created, stats.Known, stats.Duplicates, JsonSerializer.Serialize(new Checkpoint(qi, token, pages)));
                if (stats.ClosedSkipped > 0) job.AddWarning($"{stats.ClosedSkipped} permanently closed business(es) skipped.");
                await db.SaveChangesAsync(ct);
            } while (token is not null && job.ResultsFound < job.MaxResults && pages < o.MaxPagesPerQuery);
            if (job.ResultsFound >= job.MaxResults) break;
        }
        await FinishAsync(job.Id, j => j.Complete(crm.Now), null, ct);
    }

    private async Task RateLimitAsync(int delayMs, CancellationToken ct)
    {
        if (delayMs <= 0 || _lastCall == DateTime.MinValue) return;
        var wait = TimeSpan.FromMilliseconds(delayMs) - (DateTime.UtcNow - _lastCall);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
    }

    private async Task<(IReadOnlyList<ProviderQuery> Queries, DiscoveryCategoryMapping? Mapping, LeadCategory? Category)> BuildQueriesAsync(DiscoveryJob job, ConnectorDescriptor d, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        LeadCategory? category = job.CategoryId is { } cid ? await crm.Db.LeadCategories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid && c.TenantId == tenantId, ct) : null;
        DiscoveryCategoryMapping? mapping = job.CategoryId is { } mid ? await crm.Db.DiscoveryCategoryMappings.AsNoTracking().FirstOrDefaultAsync(m => m.CategoryId == mid && m.TenantId == tenantId && m.IsActive, ct) : null;
        if (d.AcceptsSeedUrls) return ([new ProviderQuery("website", null, null, null, null, null, null, null)], mapping, category);
        // a category without its own search phrases is searched by its name
        var terms = mapping?.Terms ?? (category is null ? [] : [category.Name]);
        var queries = DiscoveryQueryBuilder.Build(new SearchLocation(job.Country, job.Province, job.City, job.District, job.Area), terms, mapping?.PlaceType, job.Keywords);
        return (queries, mapping, category);
    }

    private static Checkpoint ParseCheckpoint(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new Checkpoint(0, null, 0);
        try { return JsonSerializer.Deserialize<Checkpoint>(json) ?? new Checkpoint(0, null, 0); } catch (JsonException) { return new Checkpoint(0, null, 0); }
    }

    private sealed record PageStats(int Found, int Created, int Known, int Duplicates, int ClosedSkipped);

    private async Task<PageStats> StoreAsync(DiscoveryJob job, IReadOnlyList<DiscoveryCandidate> candidates, ConnectorDescriptor descriptor, DiscoveryCategoryMapping? mapping, LeadCategory? category, string? country, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        int found = 0, created = 0, known = 0, flagged = 0, closed = 0;
        var items = new List<(DiscoveryCandidate Raw, NormalizedCandidate N)>();
        var seen = new HashSet<string>();
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.ExternalId)) continue;
            if (string.Equals(c.BusinessStatus, "CLOSED_PERMANENTLY", StringComparison.OrdinalIgnoreCase)) { closed++; continue; }
            var n = CandidateNormalizer.Normalize(c, country);
            if (n.Name is null || !seen.Add(n.ExternalId)) continue;   // a record without a name cannot become a lead; the same id twice in one page is one record
            items.Add((c, n));
        }
        found = items.Count + closed;
        if (items.Count == 0) return new PageStats(found, 0, 0, 0, closed);

        var ids = items.Select(i => i.N.ExternalId).ToList();
        var existing = (await db.DiscoveryResults.AsNoTracking().Where(r => r.TenantId == tenantId && r.ConnectorType == job.ConnectorType && ids.Contains(r.ExternalId)).Select(r => r.ExternalId).ToListAsync(ct)).ToHashSet();
        var fresh = items.Where(i => !existing.Contains(i.N.ExternalId)).ToList();
        known = items.Count - fresh.Count;

        var probes = fresh.Select(i => new DuplicateProbe(i.N.Name, i.N.City ?? job.City, i.N.Website, i.N.PhoneE164 is null ? [] : [i.N.PhoneE164])).ToList();
        var dupes = probes.Count == 0 ? [] : await duplicates.FindAsync(probes, ct);

        var results = new List<DiscoveryResult>();
        for (var i = 0; i < fresh.Count; i++)
        {
            var (raw, n) = fresh[i];
            var match = dupes[i].Count > 0 ? dupes[i][0] : null;
            if (match is not null) flagged++;
            results.Add(await BuildResultAsync(job, raw, n, descriptor, mapping, category, country, match, now, ct));
        }

        foreach (var r in results) db.DiscoveryResults.Add(r);
        try { await db.SaveChangesAsync(ct); created = results.Count; }
        catch (DbUpdateException ex) when (TenancyResultFlow.IsDuplicate(ex))
        {
            // a concurrent job stored some of these places first: keep what is new, count the rest as known (idempotent)
            foreach (var e in db.ChangeTracker.Entries<DiscoveryResult>().Where(e => e.State == EntityState.Added).ToList()) e.State = EntityState.Detached;
            foreach (var r in results)
            {
                db.DiscoveryResults.Add(r);
                try { await db.SaveChangesAsync(ct); created++; }
                catch (DbUpdateException inner) when (TenancyResultFlow.IsDuplicate(inner)) { db.Entry(r).State = EntityState.Detached; known++; if (r.DuplicateLeadId is not null) flagged--; }
            }
        }
        return new PageStats(found, created, known, flagged, closed);
    }

    private async Task<DiscoveryResult> BuildResultAsync(DiscoveryJob job, DiscoveryCandidate raw, NormalizedCandidate n, ConnectorDescriptor descriptor, DiscoveryCategoryMapping? mapping, LeadCategory? category,
        string? country, DuplicateMatch? match, DateTime now, CancellationToken ct)
    {
        var provenance = new Dictionary<string, Verification>();
        var providerVerification = raw.Source == SourceKind.StructuredProvider ? Verification.ProviderReported : Verification.Unverified;
        void Mark(string field, object? value, Verification v) { if (value is not null) provenance[field] = v; }
        Mark("Name", n.Name, providerVerification); Mark("Address", n.Address, providerVerification); Mark("Website", n.Website, providerVerification); Mark("Phone", n.PhoneRaw, providerVerification);
        Mark("Email", n.Email, providerVerification);

        var phone = n.PhoneRaw; var email = n.Email; var social = n.SocialLinks.ToList(); var notes = raw.Notes.ToList();
        var websiteConfirmed = false; var corroborated = false; var enrichment = EnrichmentStatus.NotRequested;

        if (job.EnrichWebsites && n.Website is not null && _enrichedThisRun < options.Value.Website.MaxWebsitesPerJob)
        {
            _enrichedThisRun++;
            var e = await enricher.EnrichAsync(n.Website, ct);
            if (e.Outcome == WebsiteFetchOutcome.Fetched && e.Info is { } info)
            {
                enrichment = EnrichmentStatus.Done;
                websiteConfirmed = DiscoveryMatching.SharesDistinctiveWord(n.Name, info.BusinessName ?? info.Title);
                notes.Add(websiteConfirmed ? "The website's name matches the provider's name." : "The website's title/name does not clearly match the provider's name; check it before approving.");
                if (info.Title is not null) notes.Add($"Website title: {info.Title}");
                var sitePhone = info.Phones.FirstOrDefault();
                if (phone is null && sitePhone is not null) { phone = sitePhone; provenance["Phone"] = Verification.Unverified; notes.Add("Phone taken from the website (unverified)."); }
                else if (phone is not null && info.Phones.Any(p => DiscoveryMatching.SamePhone(p, phone))) { corroborated = true; provenance["Phone"] = Verification.Corroborated; notes.Add("The phone number also appears on the website."); }
                else if (phone is not null && sitePhone is not null) notes.Add($"The website lists a different phone number ({sitePhone}); not used (unverified).");
                var siteEmail = info.Emails.FirstOrDefault();
                if (email is null && siteEmail is not null) { email = Normalizer.Email(siteEmail); if (email is not null) { provenance["Email"] = Verification.Unverified; notes.Add("E-mail address published on the website (unverified; it is not consent to be contacted)."); } }
                foreach (var s in info.SocialLinks) if (!social.Contains(s, StringComparer.OrdinalIgnoreCase)) social.Add(s);
                if (social.Count > 0) { provenance["Social"] = Verification.Unverified; notes.Add($"Social profiles listed on the website: {social.Count}."); }
                notes.AddRange(info.Notes);
            }
            else { enrichment = e.Outcome is WebsiteFetchOutcome.Failed ? EnrichmentStatus.Failed : EnrichmentStatus.Skipped; notes.Add($"Website not read: {e.Reason ?? e.Outcome.ToString()}"); }
        }
        if (string.Equals(raw.BusinessStatus, "CLOSED_TEMPORARILY", StringComparison.OrdinalIgnoreCase)) notes.Add("The provider reports this business as temporarily closed.");
        if (match is not null) notes.Add($"Possible duplicate of existing lead '{match.LeadName}' ({string.Join(", ", match.Reasons)}).");

        var categoryMatched = category is not null && DiscoveryMatching.CategoryMatches(mapping?.Terms ?? [category.Name], mapping?.PlaceType, n.Name, raw.Types);
        var confidence = ConfidenceCalculator.Calculate(new ConfidenceInputs(raw.Source, n.Name is not null, n.Address is not null, n.Website is not null, phone is not null, email is not null,
            categoryMatched, websiteConfirmed, corroborated, string.Equals(raw.BusinessStatus, "CLOSED_TEMPORARILY", StringComparison.OrdinalIgnoreCase)));

        var data = new DiscoveryResult.Data(job.ConnectorType, n.ExternalId, n.Name, job.CategoryId, n.CategoryLabel, n.Country, n.Province, n.City, n.District, n.Area, n.Address, n.Website,
            phone, email, social.Count == 0 ? null : JsonSerializer.Serialize(social), raw.SourceName ?? descriptor.DisplayName, raw.SourceUrl, raw.Attribution ?? descriptor.Attribution, raw.RetrievedAtUtc == default ? now : raw.RetrievedAtUtc,
            raw.ExpiresAtUtc, raw.RetrievedAtUtc == default ? now : raw.RetrievedAtUtc, confidence, string.Join("\n", notes), CandidateNormalizer.ProvenanceJson(provenance.Select(p => (p.Key, p.Value))), enrichment);
        return DiscoveryResult.Create(crm.TenantId, job.Id, data, match?.LeadId, match is null ? null : string.Join(", ", match.Reasons), now);
    }
}
