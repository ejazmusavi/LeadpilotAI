using System.Collections.Concurrent;
using System.Net;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Discovery;
using LeadPilot.Domain.Discovery;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>
/// TEST-ONLY discovery connector. It is defined in the test assembly and registered only by the test hosts, so no production or development host can ever contain it.
/// Behaviour is scripted per search token (a word the test puts into the search keywords).
/// </summary>
public sealed class FakeScript
{
    public sealed class Behavior
    {
        public int Total { get; init; } = 7;
        public int PageSize { get; init; } = 3;
        /// <summary>Zero-based page index at which the next <see cref="FailuresRemaining"/> calls fail.</summary>
        public int? FailAtPage { get; init; }
        public int FailuresRemaining;
        public Func<Exception>? Failure { get; init; }
        public Action<ConnectorRequest, int>? OnCall { get; init; }
        public bool WithWebsite { get; init; }
        public bool WithEmail { get; init; }
        public string? Status { get; init; }
    }

    public sealed record Call(string Token, string ConnectorKey, string? ApiKey, string? PageToken, string QueryText);

    private readonly ConcurrentDictionary<string, Behavior> _behaviors = new();
    public ConcurrentQueue<Call> Calls { get; } = new();
    public Behavior Set(string token, Behavior b) { _behaviors[token] = b; return b; }
    public Behavior? Find(string text) => _behaviors.FirstOrDefault(kv => text.Contains(kv.Key)).Value;
    public string? TokenOf(string text) => _behaviors.Keys.FirstOrDefault(k => text.Contains(k));
    public static string Phone(string token, int i) => "+92 300 " + (1_000_000 + (Math.Abs((token + i).GetHashCode()) % 8_999_999)).ToString();
    public static string Name(string token, int i) => $"{token} School {i:000}";
    public static string ExternalId(string token, int i) => $"fake:{token}:{i}";
}

public sealed class FakeDiscoveryConnector(FakeScript script, ConnectorDescriptor descriptor) : IDiscoveryConnector
{
    public ConnectorDescriptor Descriptor { get; } = descriptor;

    public Task<ConnectorPage> SearchAsync(ConnectorRequest request, CancellationToken ct)
    {
        var text = request.Query.Text;
        var token = script.TokenOf(text) ?? throw new InvalidOperationException("No fake behaviour is registered for query: " + text);
        var b = script.Find(text)!;
        var start = request.PageToken is null ? 0 : int.Parse(request.PageToken);
        script.Calls.Enqueue(new FakeScript.Call(token, Descriptor.Key, request.ApiKey, request.PageToken, text));
        b.OnCall?.Invoke(request, start / b.PageSize);
        if (b.FailAtPage == start / b.PageSize && Interlocked.Decrement(ref b.FailuresRemaining) >= 0) throw b.Failure!();
        var end = Math.Min(start + b.PageSize, b.Total);
        var items = Enumerable.Range(start, Math.Max(0, end - start)).Select(i => new DiscoveryCandidate
        {
            ExternalId = FakeScript.ExternalId(token, i), Name = FakeScript.Name(token, i), Types = ["school"], Country = "Pakistan", Province = "Punjab", City = "Lahore", Address = $"{i + 1} Canal Road, Lahore",
            Phone = FakeScript.Phone(token, i), Website = b.WithWebsite ? $"https://{token}-{i}.school.example/" : null, Email = b.WithEmail ? $"{token}-{i}@school.example" : null,
            SourceName = "Fake connector (TEST ONLY)", SourceUrl = $"https://fake.test/{token}/{i}", Attribution = "Fake data for automated tests", BusinessStatus = b.Status ?? "OPERATIONAL",
            RetrievedAtUtc = request.NowUtc, ExpiresAtUtc = request.NowUtc.AddDays(30),
        }).ToList();
        return Task.FromResult(new ConnectorPage(items, end < b.Total ? end.ToString() : null, [new ConnectorCall("fake.search", 1, request.CostPerCallUsd, 200)]));
    }
}

public sealed class RecordingScheduler : IDiscoveryJobScheduler
{
    private int _n;
    public ConcurrentQueue<(Guid TenantId, Guid JobId, string Id)> Enqueued { get; } = new();
    public ConcurrentQueue<string> Deleted { get; } = new();
    public string Enqueue(Guid tenantId, Guid jobId) { var id = "test-" + Interlocked.Increment(ref _n); Enqueued.Enqueue((tenantId, jobId, id)); return id; }
    public void TryDelete(string? id) { if (id is not null) Deleted.Enqueue(id); }
    public (Guid TenantId, Guid JobId, string Id) LastFor(Guid jobId) => Enqueued.Last(e => e.JobId == jobId);
}

/// <summary>Replaces website fetching in tests: no network. Unknown hosts are reported as not readable, never invented.</summary>
public sealed class FakeEnricher : IWebsiteEnricher
{
    public static readonly ConcurrentDictionary<string, WebsiteEnrichmentResult> ByHost = new();
    public Task<WebsiteEnrichmentResult> EnrichAsync(string url, CancellationToken ct) =>
        Task.FromResult(ByHost.TryGetValue(new Uri(url).Host, out var r) ? r : new WebsiteEnrichmentResult(WebsiteFetchOutcome.BlockedByPolicy, null, "not reachable in tests"));
}

public static class DiscoveryKit
{
    public const string Fake = "test-fake";
    public const string FakeKeyed = "test-fake-keyed";

    public static void RegisterTestServices(IServiceCollection s, FakeScript script, RecordingScheduler? scheduler)
    {
        s.AddSingleton(script);
        s.AddScoped<IDiscoveryConnector>(sp => new FakeDiscoveryConnector(script, new ConnectorDescriptor(Fake, "Fake connector", "TEST ONLY: scripted results for automated tests.", ConnectorKind.Search, RequiresCredentials: false, IsTestOnly: true)));
        s.AddScoped<IDiscoveryConnector>(sp => new FakeDiscoveryConnector(script, new ConnectorDescriptor(FakeKeyed, "Fake keyed connector", "TEST ONLY: needs an API key, records which key it received.", ConnectorKind.Search, RequiresCredentials: true, IsTestOnly: true)));
        if (scheduler is not null) { s.RemoveAllOf<IDiscoveryJobScheduler>(); s.AddSingleton<IDiscoveryJobScheduler>(scheduler); }
        s.RemoveAllOf<IWebsiteEnricher>(); s.AddScoped<IWebsiteEnricher, FakeEnricher>();
    }

    private static void RemoveAllOf<T>(this IServiceCollection s) { foreach (var d in s.Where(d => d.ServiceType == typeof(T)).ToList()) s.Remove(d); }

    /// <summary>Executes a job exactly as the Hangfire job does: inside the tenant scope runner, through the runner resolved from that scope.</summary>
    public static Task RunAsync(AppFixture app, Guid tenantId, Guid jobId) =>
        app.Web!.Services.GetRequiredService<ITenantScopeRunner>().RunAsync(tenantId, (sp, ct) => sp.GetRequiredService<IDiscoveryJobRunner>().RunAsync(jobId, ct));

    public static string NewToken() => "zq" + Guid.NewGuid().ToString("N")[..8];

    public static async Task<DiscoveryJobDto> CreateJob(ApiSession s, string token, int max = 40, string connector = Fake, bool enrich = false, Guid? categoryId = null, string? city = "Lahore")
    {
        var res = await s.Post("/api/v1/discovery/jobs", new { connectorType = connector, country = "Pakistan", province = "Punjab", city, categoryId, keywords = token, maxResults = max, enrichWebsites = enrich });
        Assert.True(res.StatusCode == HttpStatusCode.Accepted, $"create job failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return await ApiSession.Json<DiscoveryJobDto>(res);
    }

    public static async Task<DiscoveryJobDto> GetJob(ApiSession s, Guid id) => await Crm.Ok<DiscoveryJobDto>(await s.Get($"/api/v1/discovery/jobs/{id}"));

    public static async Task<List<DiscoveryResultDto>> Results(ApiSession s, Guid jobId, string? status = "Pending")
    {
        var all = new List<DiscoveryResultDto>();
        for (var page = 1; ; page++)
        {
            var p = await Crm.Page<DiscoveryResultDto>(await s.Get($"/api/v1/discovery/results?jobId={jobId}&status={status}&page={page}&pageSize=100"));
            all.AddRange(p.Items);
            if (page >= p.TotalPages) return all;
        }
    }

    public static async Task<DiscoveryJobDto> CreateAndRun(AppFixture app, ApiSession s, Guid tenantId, string token, int max = 40, string connector = Fake, bool enrich = false, Guid? categoryId = null)
    {
        var job = await CreateJob(s, token, max, connector, enrich, categoryId);
        await RunAsync(app, tenantId, job.Id);
        return await GetJob(s, job.Id);
    }
}
