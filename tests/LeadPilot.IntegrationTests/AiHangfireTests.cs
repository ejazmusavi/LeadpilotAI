using System.Net;
using Hangfire;
using Hangfire.Storage;
using LeadPilot.Infrastructure.Discovery.Jobs;
using LeadPilot.Application;
using LeadPilot.Application.Ai;
using LeadPilot.Domain.Ai;
using LeadPilot.Infrastructure;
using LeadPilot.Infrastructure.Ai.Jobs;
using LeadPilot.Infrastructure.Ai.Providers;
using LeadPilot.Infrastructure.Ai.Stores;
using LeadPilot.Infrastructure.Discovery;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LeadPilot.IntegrationTests;

/// <summary>The real Worker composition with a REAL Hangfire server on MySQL runs the knowledge jobs inside the tenant scope (the TEST-ONLY scripted model and embedder replace only the external providers).</summary>
[Collection("app")]
public class AiHangfireTests(AppFixture app)
{
    private IHost BuildWorker(Dictionary<string, string?>? config = null)
    {
        var b = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development" });
        b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = Environment.GetEnvironmentVariable("ConnectionStrings__Default"), ["Discovery:Hangfire:PollSeconds"] = "1" });
        if (config is not null) b.Configuration.AddInMemoryCollection(config);
        b.Services.AddApplication(); b.Services.AddInfrastructure(b.Configuration, b.Environment); b.Services.AddWorkerServices(b.Configuration);
        b.Services.AddSingleton(app.Ai);
        b.Services.RemoveAll<IRawAIProvider>(); b.Services.AddScoped<IRawAIProvider>(_ => app.Ai.Llm);
        b.Services.RemoveAll<IRawEmbeddingProvider>(); b.Services.AddScoped<IRawEmbeddingProvider, FakeEmbeddingProvider>();    // the REAL HangfireAiScheduler stays
        return b.Build();
    }

    private static async Task<T> Eventually<T>(Func<Task<T>> read, Func<T, bool> done, int seconds = 60)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds); T v;
        do { v = await read(); if (done(v)) return v; await Task.Delay(500); } while (DateTime.UtcNow < until);
        return v;
    }

    [RequiresMySqlFact]
    public async Task An_index_job_enqueued_in_hangfire_is_executed_by_the_worker_and_the_document_becomes_searchable()
    {
        await using var w = await AiWorld.OpenAsync(app);
        using var host = BuildWorker(); await host.StartAsync();
        try
        {
            var created = await w.AddTextAsync("Standard plan", AiWorld.StandardPlan, index: false);
            var recorded = app.Ai.Scheduler.Jobs.Single(j => j.TenantId == w.TenantId && j.Kind == "index");     // the API host only recorded it (nothing ran)
            var scheduler = host.Services.GetRequiredService<IAiJobScheduler>();
            Assert.IsType<HangfireAiScheduler>(scheduler);
            var hangfireId = scheduler.EnqueueIndex(w.TenantId, recorded.Id);                                       // the real thing: serialised into the Hangfire tables
            var doc = await Eventually(() => w.GetAsync(created.Id), d => d.Status is IndexStatus.Indexed or IndexStatus.Failed);
            Assert.Equal(IndexStatus.Indexed, doc.Status);
            var state = await Eventually(() => Task.FromResult(JobStorage.Current.GetMonitoringApi().JobDetails(hangfireId)?.History.First().StateName), s => s == "Succeeded", 20);
            Assert.Equal("Succeeded", state);
            Assert.NotEmpty((await w.RetrieveAsync("Standard plan cost")).Chunks);
            Assert.Equal("knowledge", JobStorage.Current.GetMonitoringApi().JobDetails(hangfireId)?.Job.Method.GetCustomAttributes(typeof(QueueAttribute), false).Cast<QueueAttribute>().Single().Queue);
        }
        finally { await host.StopAsync(); app.Ai.Scheduler.Jobs.Clear(); }
    }

    [RequiresMySqlFact]
    public async Task Knowledge_jobs_fail_closed_without_a_tenant_and_do_nothing_for_another_tenants_ids()
    {
        await using var a = await AiWorld.OpenAsync(app); await using var b = await AiWorld.OpenAsync(app);
        using var host = BuildWorker(); await host.StartAsync();
        try
        {
            var bDoc = await b.AddTextAsync("B only", "Business B text about its private price list.", index: false);
            var bVersion = app.Ai.Scheduler.Jobs.Single(j => j.TenantId == b.TenantId).Id;
            var scheduler = host.Services.GetRequiredService<IAiJobScheduler>();
            var noTenant = scheduler.EnqueueIndex(Guid.Empty, bVersion);
            var failure = await Eventually(() => Task.FromResult(JobStorage.Current.GetMonitoringApi().JobDetails(noTenant)?.History.FirstOrDefault(h => h.Data.ContainsKey("ExceptionType"))), h => h is not null, 40);
            Assert.NotNull(failure); Assert.Contains("Tenant", failure!.Data["ExceptionType"]);
            BackgroundJob.Delete(noTenant);

            var foreign = scheduler.EnqueueIndex(a.TenantId, bVersion);                                             // tenant A's scope, B's version id
            var succeeded = await Eventually(() => Task.FromResult(JobStorage.Current.GetMonitoringApi().JobDetails(foreign)?.History.First().StateName), s => s is "Succeeded" or "Failed", 30);
            Assert.Equal("Succeeded", succeeded);
            Assert.Equal(IndexStatus.Pending, (await b.GetAsync(bDoc.Id)).Status);                                   // B's document was not touched
        }
        finally { await host.StopAsync(); app.Ai.Scheduler.Jobs.Clear(); }
    }

    [RequiresMySqlFact]
    public async Task The_sweep_is_registered_as_a_recurring_hangfire_job_every_five_minutes()
    {
        using var host = BuildWorker(); await host.StartAsync();
        try
        {
            HangfireSetup.ScheduleRecurring(host.Services.GetRequiredService<IRecurringJobManager>());
            using var connection = JobStorage.Current.GetConnection();
            var job = Assert.Single(connection.GetRecurringJobs(), j => j.Id == KnowledgeSweepJob.RecurringId);
            Assert.Equal("*/5 * * * *", job.Cron);   // the [Queue("knowledge")] attribute moves each created job to its queue (asserted on the job method above)
        }
        finally { await host.StopAsync(); }
    }

    [RequiresMySqlFact]
    public async Task The_mysql_store_refuses_to_scan_more_chunks_than_its_limit_and_the_consumer_turns_that_into_a_visible_escalation()
    {
        await using var w = await AiWorld.OpenAsync(app, workerConfig: new() { ["Ai:VectorStore:MysqlMaxScan"] = "2" });
        await w.AddTextAsync("Long guide", string.Join("\n\n", Enumerable.Range(0, 12).Select(i => $"# Topic {i}\n\nSection {i} describes the module number {i} for school workflows. " + string.Join(' ', Enumerable.Range(0, 120).Select(k => $"word{i}x{k}")))));
        Assert.True((await w.GetAsync((await Crm.Ok<LeadPilot.Application.Common.PagedResult<KnowledgeDocumentListItemDto>>(await w.Admin.Get("/api/v1/knowledge/documents"))).Items[0].Id)).ChunkCount > 2);
        await Assert.ThrowsAsync<VectorStoreException>(() => w.RetrieveAsync("module number school workflows"));
        var chat = await w.ChatAsync(); await chat.SendAsync("Tell me about the module number 3 for school workflows");
        var s = Assert.Single(await chat.InteractionsAsync());
        Assert.Equal("ai_unavailable:VectorStore", s.EscalationReason); Assert.Equal(AiDecision.Escalate, s.Decision); Assert.Empty(w.Worker.DeadLettered);
    }
}
