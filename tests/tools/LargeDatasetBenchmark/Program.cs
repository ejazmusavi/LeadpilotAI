using System.Data.Common;
using System.Diagnostics;
using System.Text;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Analytics;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Infrastructure.Analytics;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace LeadPilot.Tools.Benchmark;

/// <summary>
/// Large-dataset benchmark of the Phase 11 queries (TEST TOOL, never part of the product).
///   dotnet run --project tests/tools/LargeDatasetBenchmark -c Release -- [--scale 1.0] [--keep] [--out results.md]
/// Needs LEADPILOT_TEST_CONNECTION (an account that may CREATE/DROP databases). Creates a scratch database, applies the real migrations, generates the volume with INSERT ... SELECT,
/// then measures wall-clock time, managed allocation and peak working set of every dashboard query, report, the rollup refresh and a streamed CSV export, and prints EXPLAIN plans of the heaviest queries.
/// </summary>
public static class Program
{
    private sealed class FixedClock : IClock { public DateTime UtcNow => DateTime.UtcNow; }
    private sealed class User(Guid id) : ICurrentUser { public bool IsAuthenticated => true; public Guid? UserId => id; public string? Email => "bench@example.test"; }
    private sealed class Perms(bool team) : IPermissionEvaluator
    {
        public Task<PermissionSet> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(PermissionSet.Empty);
        public Task<bool> HasAsync(string permissionCode, CancellationToken ct = default) => Task.FromResult(permissionCode != PermissionCodes.ReportsTeam || team);
        public Task<PermissionSet> GetAsync(Guid userId, Guid tenantId, CancellationToken ct = default) => Task.FromResult(PermissionSet.Empty);
    }
    private sealed class Audit : IAuditService
    {
        public int Saved;
        public void Record(AuditEntry entry) { }
        public Task RecordAndSaveAsync(AuditEntry entry, CancellationToken ct = default) { Saved++; return Task.CompletedTask; }
        public Task<LeadPilot.Application.Common.PagedResult<AuditLogDto>> QueryAsync(AuditQuery query, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Capture : DbCommandInterceptor
    {
        public List<(string Sql, List<(string, object?)> Params)> Commands { get; } = [];
        public bool On;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (On && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) Commands.Add((command.CommandText, command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, p.Value)).ToList()));
            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }
    }
    private sealed class CountingStream : Stream
    {
        public long Bytes;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true; public override long Length => Bytes; public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Flush() { } public override int Read(byte[] b, int o, int c) => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => Bytes += c;
        public override Task WriteAsync(byte[] b, int o, int c, CancellationToken ct) { Bytes += c; return Task.CompletedTask; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct = default) { Bytes += b.Length; return ValueTask.CompletedTask; }
    }

    public static async Task<int> Main(string[] args)
    {
        var admin = Environment.GetEnvironmentVariable("LEADPILOT_TEST_CONNECTION");
        if (string.IsNullOrEmpty(admin)) { Console.Error.WriteLine("Set LEADPILOT_TEST_CONNECTION to an account that may create and drop databases."); return 2; }
        double scale = 1.0; var keep = false; string? outPath = null; string? reuse = null;
        for (var i = 0; i < args.Length; i++) { if (args[i] == "--scale") scale = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); else if (args[i] == "--keep") keep = true; else if (args[i] == "--out") outPath = args[++i]; else if (args[i] == "--db") reuse = args[++i]; }
        var dbName = reuse ?? "leadpilot_bench_" + Guid.NewGuid().ToString("N")[..8];
        var cs = new MySqlConnectionStringBuilder(admin) { Database = dbName, DefaultCommandTimeout = 3600 }.ConnectionString;
        var report = new StringBuilder();
        void W(string s = "") { Console.WriteLine(s); report.AppendLine(s); }
        if (reuse is null) await using (var c = new MySqlConnection(admin)) { await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = $"CREATE DATABASE `{dbName}`"; await cmd.ExecuteNonQueryAsync(); }
        try
        {
            var tenantCtx = new TenantContext(); var capture = new Capture();
            DbContextOptions<AppDbContext> Options() => new DbContextOptionsBuilder<AppDbContext>().UseMySQL(cs, o => o.CommandTimeout(3600)).AddInterceptors(capture).Options;
            Guid tenantId, pipelineId, openStage, wonStage, lostStage, statusId;
            await using (var db = new AppDbContext(Options(), tenantCtx))
            {
                var sw = Stopwatch.StartNew(); if (reuse is null) await db.Database.MigrateAsync(); W($"migrations applied in {sw.ElapsedMilliseconds} ms");
                using var _ = tenantCtx.BeginPlatformScope(); var now = DateTime.UtcNow;
                if (reuse is not null)
                {
                    tenantId = await db.Tenants.Where(x => x.Slug == "bench-co").Select(x => x.Id).FirstAsync();
                    var st = await db.PipelineStages.IgnoreQueryFilters().Where(x => x.TenantId == tenantId).OrderBy(x => x.Position).ToListAsync();
                    pipelineId = st[0].PipelineId; openStage = st.First(x => x.Kind == LeadPilot.Domain.Crm.StageKind.Open).Id; wonStage = st.First(x => x.Kind == LeadPilot.Domain.Crm.StageKind.Won).Id; lostStage = st.First(x => x.Kind == LeadPilot.Domain.Crm.StageKind.Lost).Id;
                    statusId = await db.LeadStatuses.IgnoreQueryFilters().Where(x => x.TenantId == tenantId && x.IsDefault).Select(x => x.Id).FirstAsync();
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM DailyMetrics"); await db.Database.ExecuteSqlRawAsync("DELETE FROM AnalyticsRefreshStates");
                    goto Reused;
                }
                var t = Tenant.Create("Bench Co", "bench-co", "Education", "UTC", "en", now); tenantId = t.Id; db.Tenants.Add(t);
                LeadPilot.Infrastructure.Services.Crm.CrmDefaults.AddAll(db, tenantId, now);
                var cset = ConversionSettings.Create(tenantId, now); typeof(ConversionSettings).GetProperty("TimeZoneId")!.SetValue(cset, "Asia/Karachi"); db.ConversionSettings.Add(cset);
                await db.SaveChangesAsync();
                var stages = await db.PipelineStages.IgnoreQueryFilters().Where(s => s.TenantId == tenantId).OrderBy(s => s.Position).ToListAsync();
                pipelineId = stages[0].PipelineId; openStage = stages.First(s => s.Kind == LeadPilot.Domain.Crm.StageKind.Open).Id; wonStage = stages.First(s => s.Kind == LeadPilot.Domain.Crm.StageKind.Won).Id; lostStage = stages.First(s => s.Kind == LeadPilot.Domain.Crm.StageKind.Lost).Id;
                statusId = await db.LeadStatuses.IgnoreQueryFilters().Where(s => s.TenantId == tenantId && s.IsDefault).Select(s => s.Id).FirstAsync();
                Reused:;
            }
            var d = new Generator.Volumes(); int S(int v) => Math.Max(1, (int)(v * scale));
            var vol = new Generator.Volumes(S(d.Leads), S(d.Contacts), S(d.Consents), S(d.Suppressions), S(d.Conversations), S(d.Messages), S(d.StatusEvents), S(d.Demos), S(d.Trials), Math.Min(S(d.Customers), S(d.Leads)), S(d.Payments), S(d.Opportunities), S(d.Handoffs), S(d.AiInteractions), Math.Min(S(d.Qualifications), S(d.Leads)), Math.Max(2, S(d.Campaigns)), Math.Min(S(d.Recipients), S(d.Contacts)), S(d.DiscoveryJobs), S(d.DiscoveryResults), S(d.AiUsage));
            W($"# Large-dataset benchmark ({DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC, scale {scale}, MySQL {await Scalar(admin, "SELECT VERSION()")}, {Environment.ProcessorCount} cores)"); W();
            var gen = new Generator(cs, tenantId, pipelineId, openStage, wonStage, lostStage, statusId); var gw = Stopwatch.StartNew(); if (reuse is null) await gen.RunAsync(vol);
            W($"Data generated in {gw.Elapsed.TotalSeconds:0.0} s:"); W(); W("| Table | Rows | ms |"); W("|---|---:|---:|");
            foreach (var (step, rows, ms) in gen.Log) W($"| {step} | {rows:N0} | {ms:N0} |");
            long total = 0; foreach (var tname in new[] { "Leads", "Contacts", "Messages", "MessageStatusEvents", "CampaignRecipients", "DiscoveryResults" }) { var n = await Scalar(cs, $"SELECT COUNT(*) FROM {tname} WHERE TenantId = '{tenantId}'"); total += Convert.ToInt64(n); W($"- `{tname}`: {Convert.ToInt64(n):N0} rows"); }
            W($"- message + status rows: {Convert.ToInt64(await Scalar(cs, $"SELECT (SELECT COUNT(*) FROM Messages) + (SELECT COUNT(*) FROM MessageStatusEvents)")):N0}"); W();

            // --- measurements ------------------------------------------------------------------------------------------------------------------------------
            var results = new List<(string Name, double Cold, double Warm, long Alloc, long PeakMb, string Note)>(); var explains = new List<(string Name, List<(string Sql, List<(string, object?)> Params)> Cmds)>();
            async Task Measure(string name, Func<AppDbContext, Task<string>> work, bool owner = false, bool explain = false)
            {
                async Task<(double Ms, long Alloc, string Note)> One()
                {
                    await using var db = new AppDbContext(Options(), tenantCtx); GC.Collect(); var a0 = GC.GetTotalAllocatedBytes(true); var sw = Stopwatch.StartNew();
                    var note = await work(db); sw.Stop(); return (sw.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(true) - a0, note);
                }
                tenantCtx.SetTenant(tenantId); capture.Commands.Clear(); capture.On = explain;
                var cold = await One(); capture.On = false; var warms = new List<double>(); long alloc = cold.Alloc; string note = cold.Note;
                for (var i = 0; i < 3; i++) { var w = await One(); warms.Add(w.Ms); alloc = Math.Max(alloc, w.Alloc); note = w.Note; }
                warms.Sort(); results.Add((name, cold.Ms, warms[1], alloc, Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024, note));
                if (explain) explains.Add((name, [.. capture.Commands.Take(4)]));
                Console.WriteLine($"  measured {name}: cold {cold.Ms:N0} ms, warm {warms[1]:N0} ms");
            }
            var tz = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi"); var nowUtc = DateTime.UtcNow; var today = RangeResolver.LocalDay(tz, nowUtc);
            var from30 = RangeResolver.DayStart(tz, today.AddDays(-29)); var to30 = RangeResolver.DayStart(tz, today.AddDays(1)); var from90 = RangeResolver.DayStart(tz, today.AddDays(-89));
            var beforeMb = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;
            await using var scopeDb = new AppDbContext(Options(), tenantCtx); tenantCtx.SetTenant(tenantId);
            var q0 = new AnalyticsQueries(scopeDb, tenantId, null);

            foreach (var key in StreamKeys.All)
                await Measure($"stream {key} (30 d, SQL buckets)", async db => $"{(await new AnalyticsQueries(db, tenantId, null).DaysAsync(key, from30, to30, tz, default)).Sum(x => x.Count):N0} events", explain: key is StreamKeys.LeadsContacted or StreamKeys.Responses or StreamKeys.MessagesSent or StreamKeys.AiConversations or StreamKeys.Revenue);
            await Measure("snapshot: total leads", async db => $"{await new AnalyticsQueries(db, tenantId, null).TotalLeadsAsync(default):N0}");
            await Measure("snapshot: verified leads", async db => $"{await new AnalyticsQueries(db, tenantId, null).VerifiedLeadsAsync(default):N0}", explain: true);
            await Measure("snapshot: contact-eligible leads", async db => $"{await new AnalyticsQueries(db, tenantId, null).EligibleLeadsAsync(default):N0}", explain: true);
            await Measure("snapshot: interested leads", async db => $"{await new AnalyticsQueries(db, tenantId, null).InterestedLeadsAsync(default):N0}", explain: true);
            await Measure("snapshot: qualified leads", async db => $"{await new AnalyticsQueries(db, tenantId, null).QualifiedLeadsAsync(default):N0}");
            await Measure("cohort: lead conversion (30 d)", async db => { var (a, b) = await new AnalyticsQueries(db, tenantId, null).ConversionCohortAsync(from30, to30, default); return $"{b:N0}/{a:N0}"; });

            AnalyticsService Svc(AppDbContext db, bool team, Audit audit, AnalyticsCache cache) { var ten = new TenantContext(); ten.SetTenant(tenantId); return new AnalyticsService(db, ten, new User(Guid.Parse("00000000-0000-0000-0000-0000000000a1")), new Perms(team), new FixedClock(), Options2(), cache, audit); }
            static IOptions<AnalyticsOptions> Options2() => Microsoft.Extensions.Options.Options.Create(new AnalyticsOptions { CacheSeconds = 0 });
            var r30 = new RangeRequest { Preset = RangePreset.Last30Days, Compare = true };
            await Measure("DASHBOARD live, team, 30 d + previous (all 20 tiles + trends, no rollup, no cache)", async db => { var r = await Svc(db, true, new Audit(), new AnalyticsCache()).DashboardAsync(r30); return $"{r.Value.Freshness.Source}, leads.created={r.Value.Metrics.Single(m => m.Key == "leads.created").Value:N0}"; });
            await Measure("DASHBOARD live, own data (one sales rep), 30 d + previous", async db => { var r = await Svc(db, false, new Audit(), new AnalyticsCache()).DashboardAsync(r30); return $"{r.Value.Freshness.Source}, own leads.total={r.Value.Metrics.Single(m => m.Key == "leads.total").Value:N0}"; });

            var rollupSw = Stopwatch.StartNew(); RollupResult full;
            { await using var db = new AppDbContext(Options(), tenantCtx); var ten = new TenantContext(); ten.SetTenant(tenantId); GC.Collect(); var a0 = GC.GetTotalAllocatedBytes(true);
              full = await new AnalyticsRollupService(db, ten, new FixedClock(), Options2(), new AnalyticsCache()).RefreshAsync(true); results.Add(($"ROLLUP full rebuild (400 days, 16 streams)", rollupSw.Elapsed.TotalMilliseconds, double.NaN, GC.GetTotalAllocatedBytes(true) - a0, Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024, $"{full.Rows:N0} rows written")); }
            await Measure("ROLLUP incremental refresh (7 days)", async db => { var ten = new TenantContext(); ten.SetTenant(tenantId); var r = await new AnalyticsRollupService(db, ten, new FixedClock(), Options2(), new AnalyticsCache()).RefreshAsync(false); return $"{r.Rows:N0} rows"; });
            await Measure("DASHBOARD from rollup, team, 30 d + previous", async db => { var r = await Svc(db, true, new Audit(), new AnalyticsCache()).DashboardAsync(r30); return $"{r.Value.Freshness.Source}, leads.created={r.Value.Metrics.Single(m => m.Key == "leads.created").Value:N0}"; }, explain: true);
            await Measure("DASHBOARD from rollup, team, 365 days + previous", async db => { var r = await Svc(db, true, new Audit(), new AnalyticsCache()).DashboardAsync(new RangeRequest { Preset = RangePreset.Custom, From = today.AddDays(-364), To = today }); return r.Value.Freshness.Source; });

            foreach (var rep in ReportCatalog.All)
                await Measure($"REPORT {rep.Key} (page of 50, 90 d)", async db => { var r = await Svc(db, true, new Audit(), new AnalyticsCache()).ReportAsync(rep.Key, new RangeRequest { Preset = RangePreset.Last90Days }, 1, 50); return $"{r.Value.TotalRows:N0} rows"; }, explain: rep.Key is ReportKeys.DiscoveryByCity or ReportKeys.Funnel or ReportKeys.Campaigns or ReportKeys.Reps);

            // --- streamed export: memory stays flat while the report is far larger than the page size ---------------------------------------------------
            foreach (var (key, preset) in new[] { (ReportKeys.DiscoveryByCity, RangePreset.Last90Days), (ReportKeys.Messaging, RangePreset.Last90Days), (ReportKeys.Campaigns, RangePreset.Last90Days) })
            {
                await using var db = new AppDbContext(Options(), tenantCtx); var audit = new Audit(); var counting = new CountingStream(); GC.Collect(); var a0 = GC.GetTotalAllocatedBytes(true); var peak0 = Process.GetCurrentProcess().PeakWorkingSet64; var sw = Stopwatch.StartNew();
                var r = await Svc(db, true, audit, new AnalyticsCache()).ExportCsvAsync(key, new RangeRequest { Preset = preset }, _ => counting);
                results.Add(($"EXPORT CSV {key} (90 d) streamed", sw.Elapsed.TotalMilliseconds, double.NaN, GC.GetTotalAllocatedBytes(true) - a0, Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024, r.IsSuccess ? $"{counting.Bytes / 1024.0:N0} KiB written, audited {audit.Saved}x" : r.Error.Message));
            }

            W(); W("## Timings"); W(); W("| Step | cold (ms) | warm median (ms) | managed allocation | peak working set | result |"); W("|---|---:|---:|---:|---:|---|");
            foreach (var r in results) W($"| {r.Name} | {r.Cold:N0} | {(double.IsNaN(r.Warm) ? "—" : r.Warm.ToString("N0"))} | {r.Alloc / 1024.0 / 1024:N1} MiB | {r.PeakMb:N0} MiB | {r.Note} |");
            W(); W($"Working set after generation, before the measurements: {beforeMb:N0} MiB; peak working set of this process at the end: {Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024:N0} MiB (the process holds no record sets: only grouped result rows).");

            // --- index justification: the same query with and without each new index -------------------------------------------------------------------
            W(); W("## Index justification (same query with the index and with IGNORE INDEX, median of 3 warm runs)"); W();
            W("| Index | Query | with (ms) | without (ms) | plan with: key / rows | plan without: key / rows | verdict |"); W("|---|---|---:|---:|---|---|---|");
            var f30 = from30.ToString("yyyy-MM-dd HH:mm:ss"); var f90 = from90.ToString("yyyy-MM-dd HH:mm:ss"); var e30 = to30.ToString("yyyy-MM-dd HH:mm:ss"); var tid = $"'{tenantId}'";
            var evidence = new (string Index, string Table, string Label, string Sql)[]
            {
                ("IX_Leads_Tenant_Created", "Leads", "new leads (30 d)", $"SELECT COUNT(*) FROM Leads {{I}} WHERE TenantId={tid} AND CreatedAtUtc>='{f30}' AND CreatedAtUtc<'{e30}'"),
                ("IX_Messages_Tenant_Dir_Sent", "Messages", "messages sent (30 d)", $"SELECT COUNT(*) FROM Messages {{I}} WHERE TenantId={tid} AND Direction='Outbound' AND SentAtUtc>='{f30}' AND SentAtUtc<'{e30}'"),
                ("IX_Messages_Tenant_Dir_Occurred", "Messages", "responses (30 d)", $"SELECT COUNT(*) FROM Messages {{I}} WHERE TenantId={tid} AND Direction='Inbound' AND ContactId IS NOT NULL AND OccurredAtUtc>='{f30}' AND OccurredAtUtc<'{e30}'"),
                ("IX_Messages_Tenant_Failed", "Messages", "failed (30 d)", $"SELECT COUNT(*) FROM Messages {{I}} WHERE TenantId={tid} AND Direction='Outbound' AND FailedAtUtc>='{f30}' AND FailedAtUtc<'{e30}'"),
                ("IX_Messages_Tenant_Contact_Dir", "Messages", "funnel 'contacted' (90 d cohort)", $"SELECT COUNT(*) FROM Leads l WHERE l.TenantId={tid} AND l.CreatedAtUtc>='{f90}' AND l.CreatedAtUtc<'{e30}' AND EXISTS (SELECT 1 FROM Contacts c WHERE c.TenantId={tid} AND c.LeadId=l.Id AND EXISTS (SELECT 1 FROM Messages m {{I}} WHERE m.TenantId={tid} AND m.ContactId=c.Id AND m.Direction='Outbound' AND m.SentAtUtc IS NOT NULL))"),
                ("IX_Demos_Tenant_Created", "Demos", "demos scheduled (30 d)", $"SELECT COUNT(*) FROM Demos {{I}} WHERE TenantId={tid} AND CreatedAtUtc>='{f30}' AND CreatedAtUtc<'{e30}'"),
                ("IX_Demos_Tenant_Completed", "Demos", "demos completed (30 d)", $"SELECT COUNT(*) FROM Demos {{I}} WHERE TenantId={tid} AND CompletedAtUtc>='{f30}' AND CompletedAtUtc<'{e30}'"),
                ("IX_Trials_Tenant_Created", "Trials", "trials (30 d)", $"SELECT COUNT(*) FROM Trials {{I}} WHERE TenantId={tid} AND CreatedAtUtc>='{f30}' AND CreatedAtUtc<'{e30}'"),
                ("IX_Customers_Tenant_Converted", "Customers", "customers won (30 d)", $"SELECT COUNT(*) FROM Customers {{I}} WHERE TenantId={tid} AND ConvertedAtUtc>='{f30}' AND ConvertedAtUtc<'{e30}'"),
                ("IX_Payments_Tenant_Paid", "Payments", "revenue (30 d)", $"SELECT Currency, SUM(Amount) FROM Payments {{I}} WHERE TenantId={tid} AND PaidAtUtc>='{f30}' AND PaidAtUtc<'{e30}' GROUP BY Currency"),
                ("IX_Opportunities_Tenant_Status_Updated", "Opportunities", "lost opportunities (30 d)", $"SELECT COUNT(*) FROM Opportunities {{I}} WHERE TenantId={tid} AND Status='Lost' AND UpdatedAtUtc>='{f30}' AND UpdatedAtUtc<'{e30}'"),
                ("IX_HandoffEvents_Tenant_Occurred", "HandoffEvents", "human handoffs (30 d)", $"SELECT COUNT(*) FROM HandoffEvents {{I}} WHERE TenantId={tid} AND Kind IN ('Takeover','Escalated','AutoTakeover') AND OccurredAtUtc>='{f30}' AND OccurredAtUtc<'{e30}'"),
                ("IX_AiInteractions_Tenant_Created", "AiInteractions", "AI report (90 d, by agent)", $"SELECT AgentId, COUNT(*), AVG(LatencyMs) FROM AiInteractions {{I}} WHERE TenantId={tid} AND CreatedAtUtc>='{f90}' AND CreatedAtUtc<'{e30}' GROUP BY AgentId"),
                ("IX_CampaignRecipients_Tenant_Sent", "CampaignRecipients", "campaign messages sent (30 d)", $"SELECT COUNT(*) FROM CampaignRecipients {{I}} WHERE TenantId={tid} AND SentAtUtc>='{f30}' AND SentAtUtc<'{e30}'"),
                ("IX_Campaigns_Tenant_Started", "Campaigns", "campaigns started (30 d)", $"SELECT COUNT(*) FROM Campaigns {{I}} WHERE TenantId={tid} AND StartedAtUtc>='{f30}' AND StartedAtUtc<'{e30}'"),
                ("IX_DiscoveryResults_Tenant_Retrieved", "DiscoveryResults", "discovery by city (30 d)", $"SELECT City, COUNT(*) FROM DiscoveryResults {{I}} WHERE TenantId={tid} AND RetrievedAtUtc>='{f30}' AND RetrievedAtUtc<'{e30}' GROUP BY City"),
            };
            await using (var ec = new MySqlConnection(cs))
            {
                await ec.OpenAsync();
                async Task<(double Ms, string Plan)> Time(string sql)
                {
                    var times = new List<double>();
                    for (var i = 0; i < 4; i++) { var sw = Stopwatch.StartNew(); await using var cmd = ec.CreateCommand(); cmd.CommandText = sql; cmd.CommandTimeout = 3600; await using var rd = await cmd.ExecuteReaderAsync(); while (await rd.ReadAsync()) { } sw.Stop(); if (i > 0) times.Add(sw.Elapsed.TotalMilliseconds); }
                    times.Sort(); string plan; await using (var cmd = ec.CreateCommand()) { cmd.CommandText = "EXPLAIN " + sql; await using var rd = await cmd.ExecuteReaderAsync(); var parts = new List<string>(); while (await rd.ReadAsync()) parts.Add($"{rd.GetValue(rd.GetOrdinal("table"))}: {(rd.IsDBNull(rd.GetOrdinal("key")) ? "(no index)" : rd.GetString(rd.GetOrdinal("key")))} / {rd.GetValue(rd.GetOrdinal("rows"))}"); plan = string.Join("; ", parts); }
                    return (times[1], plan);
                }
                foreach (var (index, table, label, sql) in evidence)
                {
                    if (Convert.ToInt64(await Scalar(cs, $"SELECT COUNT(*) FROM information_schema.statistics WHERE table_schema = DATABASE() AND index_name = '{index}'")) == 0) { W($"| `{index}` | {label} | — | — | — | — | dropped after round 1 (no gain, see phase11-round1-before-optimisation.md) |"); continue; }
                    var with = await Time(sql.Replace("{I}", "")); var without = await Time(sql.Replace("{I}", $"IGNORE INDEX ({index})"));
                    var verdict = without.Ms >= with.Ms * 1.5 && without.Ms - with.Ms > 3 ? "KEEP (faster)" : with.Plan.Contains(index) ? "plan uses it, no measurable gain" : "NOT USED";
                    W($"| `{index}` | {label} | {with.Ms:N1} | {without.Ms:N1} | {with.Plan} | {without.Plan} | {verdict} |");
                }
            }
            W(); W("## EXPLAIN of the heaviest queries"); W();
            await using (var mc = new MySqlConnection(cs))
            {
                await mc.OpenAsync();
                foreach (var (name, cmds) in explains)
                {
                    W($"### {name}"); var i = 0;
                    foreach (var (sql, ps) in cmds)
                    {
                        await using var cmd = mc.CreateCommand(); cmd.CommandText = "EXPLAIN " + sql; foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v is Guid g ? g.ToString() : v);
                        try
                        {
                            await using var rd = await cmd.ExecuteReaderAsync(); W($"Query {++i}:"); W("```");
                            while (await rd.ReadAsync()) W(string.Join(" | ", Enumerable.Range(0, rd.FieldCount).Select(k => $"{rd.GetName(k)}={(rd.IsDBNull(k) ? "NULL" : rd.GetValue(k))}").Where(x => !x.StartsWith("partitions") && !x.StartsWith("filtered") || true)));
                            W("```");
                        }
                        catch (Exception e) { W($"(EXPLAIN failed: {e.Message})"); }
                    }
                    W();
                }
            }
            if (outPath is not null) await File.WriteAllTextAsync(outPath, report.ToString());
            return 0;
        }
        finally
        {
            if (!keep) { MySqlConnection.ClearAllPools(); await using var c = new MySqlConnection(admin); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = $"DROP DATABASE IF EXISTS `{dbName}`"; await cmd.ExecuteNonQueryAsync(); }
            else Console.WriteLine($"Scratch database kept: {dbName}");
        }
    }

    private static async Task<object?> Scalar(string cs, string sql) { await using var c = new MySqlConnection(cs); await c.OpenAsync(); await using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.CommandTimeout = 3600; return await cmd.ExecuteScalarAsync(); }
}
