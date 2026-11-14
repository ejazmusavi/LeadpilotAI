using System.Globalization;
using LeadPilot.Application.Analytics;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Discovery;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using LeadPilot.Application.Ai;

namespace LeadPilot.Infrastructure.Analytics;

public sealed record ReportPage(IReadOnlyList<IReadOnlyList<object?>> Rows, IReadOnlyList<object?>? Totals, long TotalRows, IReadOnlyList<string> Notes);

/// <summary>
/// Row logic of the eleven reports. Unbounded groupings (cities, campaigns) are paged IN SQL; the others are bounded by configuration (pipeline stages, users, agents, currencies, source types, days of a capped range)
/// and are computed by grouped queries too — no query ever materialises the underlying records.
/// </summary>
public sealed class ReportBuilder(AppDbContext db, AnalyticsQueries q)
{
    private Guid T => q.TenantId;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public Task<ReportPage> BuildAsync(string key, ResolvedRange range, TimeZoneInfo tz, int skip, int take, DailyReader daily, CancellationToken ct) => key switch
    {
        ReportKeys.DiscoveryByCity => DiscoveryAsync(range, skip, take, ct),
        ReportKeys.LeadSources => LeadSourcesAsync(range, skip, take, ct),
        ReportKeys.Campaigns => CampaignsAsync(range, skip, take, ct),
        ReportKeys.Pipeline => PipelineAsync(skip, take, ct),
        ReportKeys.Funnel => FunnelAsync(range, skip, take, ct),
        ReportKeys.Reps => RepsAsync(range, skip, take, ct),
        ReportKeys.AiAgent => AiAsync(range, skip, take, ct),
        ReportKeys.Demos => DemosAsync(range, skip, take, ct),
        ReportKeys.Trials => TrialsAsync(range, skip, take, ct),
        ReportKeys.Revenue => RevenueAsync(range, skip, take, ct),
        ReportKeys.Messaging => MessagingAsync(range, tz, skip, take, daily, ct),
        _ => throw new ArgumentException("Unknown report.", nameof(key))
    };

    private static ReportPage Page(List<IReadOnlyList<object?>> all, IReadOnlyList<object?>? totals, int skip, int take, params string[] notes) =>
        new(all.Skip(skip).Take(take).ToList(), totals, all.Count, notes);

    private static IReadOnlyList<object?> Row(params object?[] cells) => cells;
    private static decimal? Pct(long n, long d) => RangeResolver.Percent(n, d);
    private static string MoneyText(IEnumerable<(string Currency, decimal Amount)> parts) =>
        string.Join("; ", parts.Where(p => p.Amount != 0 || true).OrderBy(p => p.Currency, StringComparer.Ordinal).Select(p => $"{p.Currency} {p.Amount.ToString("N2", Inv)}"));

    // ---------- 1. discovery by city ----------

    private async Task<ReportPage> DiscoveryAsync(ResolvedRange r, int skip, int take, CancellationToken ct)
    {
        var results = db.DiscoveryResults.AsNoTracking().Where(x => x.TenantId == T && x.RetrievedAtUtc >= r.FromUtc && x.RetrievedAtUtc < r.ToUtcExclusive);
        var grouped = results.GroupBy(x => x.City ?? "").Select(g => new
        {
            City = g.Key, Found = g.Count(), Approved = g.Count(x => x.ReviewStatus == ReviewStatus.Approved), Rejected = g.Count(x => x.ReviewStatus == ReviewStatus.Rejected),
            Pending = g.Count(x => x.ReviewStatus == ReviewStatus.Pending), Leads = g.Count(x => x.LeadId != null), Dups = g.Count(x => x.DuplicateLeadId != null)
        });
        var total = await grouped.CountAsync(ct);
        var page = await grouped.OrderByDescending(x => x.Found).ThenBy(x => x.City).Skip(skip).Take(take).ToListAsync(ct);
        var cities = page.Select(p => p.City).ToList();
        var jobsQ = db.DiscoveryJobs.AsNoTracking().Where(j => j.TenantId == T && j.CreatedAtUtc >= r.FromUtc && j.CreatedAtUtc < r.ToUtcExclusive);
        var jobs = (await jobsQ.Where(j => cities.Contains(j.City ?? "")).GroupBy(j => j.City ?? "").Select(g => new { City = g.Key, N = g.Count(), Calls = g.Sum(j => j.ApiCalls), Cost = g.Sum(j => j.EstimatedCostUsd) }).ToListAsync(ct)).ToDictionary(j => j.City, StringComparer.OrdinalIgnoreCase);
        var rows = page.Select(p => { jobs.TryGetValue(p.City, out var j); return Row(p.City.Length == 0 ? "(no city)" : p.City, (long)p.Found, (long)p.Approved, (long)p.Rejected, (long)p.Pending, (long)p.Leads, (long)p.Dups, (long)(j?.N ?? 0), (long)(j?.Calls ?? 0), j?.Cost ?? 0m); }).ToList();
        var tot = await results.GroupBy(_ => 1).Select(g => new { Found = g.Count(), Approved = g.Count(x => x.ReviewStatus == ReviewStatus.Approved), Rejected = g.Count(x => x.ReviewStatus == ReviewStatus.Rejected), Pending = g.Count(x => x.ReviewStatus == ReviewStatus.Pending), Leads = g.Count(x => x.LeadId != null), Dups = g.Count(x => x.DuplicateLeadId != null) }).FirstOrDefaultAsync(ct);
        var jt = await jobsQ.GroupBy(_ => 1).Select(g => new { N = g.Count(), Calls = g.Sum(j => j.ApiCalls), Cost = g.Sum(j => j.EstimatedCostUsd) }).FirstOrDefaultAsync(ct);
        IReadOnlyList<object?>? totals = tot is null && jt is null ? null : Row("Total", (long)(tot?.Found ?? 0), (long)(tot?.Approved ?? 0), (long)(tot?.Rejected ?? 0), (long)(tot?.Pending ?? 0), (long)(tot?.Leads ?? 0), (long)(tot?.Dups ?? 0), (long)(jt?.N ?? 0), (long)(jt?.Calls ?? 0), jt?.Cost ?? 0m);
        return new ReportPage(rows, totals, total, ["Search, call and cost columns count ALL discovery jobs created in the period (also for cities without results); the cost is an ESTIMATE from provider price assumptions, not an invoice."]);
    }

    // ---------- 2. lead sources ----------

    private async Task<ReportPage> LeadSourcesAsync(ResolvedRange r, int skip, int take, CancellationToken ct)
    {
        var cohort = db.Leads.AsNoTracking().Where(l => l.TenantId == T && l.CreatedAtUtc >= r.FromUtc && l.CreatedAtUtc < r.ToUtcExclusive);
        var all = await cohort.GroupBy(l => l.PrimarySourceType).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        var qual = await (from l in cohort join x in db.LeadQualifications.Where(x => x.TenantId == T && x.Fit == QualificationFit.Good) on l.Id equals x.LeadId group l by l.PrimarySourceType into g select new { g.Key, N = g.Count() }).ToListAsync(ct);
        var cust = await (from l in cohort join c in db.Customers.Where(c => c.TenantId == T) on l.Id equals c.LeadId group l by l.PrimarySourceType into g select new { g.Key, N = g.Count() }).ToListAsync(ct);
        var rows = all.OrderByDescending(a => a.N).ThenBy(a => a.Key.ToString()).Select(a =>
        {
            var qn = qual.FirstOrDefault(x => x.Key == a.Key)?.N ?? 0; var cn = cust.FirstOrDefault(x => x.Key == a.Key)?.N ?? 0;
            return Row(a.Key.ToString(), (long)a.N, (long)qn, (long)cn, Pct(cn, a.N));
        }).ToList();
        var n = all.Sum(a => a.N); var cs = cust.Sum(x => x.N);
        return Page(rows, rows.Count == 0 ? null : Row("Total", (long)n, (long)qual.Sum(x => x.N), (long)cs, Pct(cs, n)), skip, take, "A lead counts under its primary source type. Customers = leads of the period that are customers today.");
    }

    // ---------- 3. campaigns ----------

    private async Task<ReportPage> CampaignsAsync(ResolvedRange r, int skip, int take, CancellationToken ct)
    {
        var rows0 = from c in db.Campaigns.AsNoTracking().Where(c => c.TenantId == T && c.StartedAtUtc != null && c.StartedAtUtc >= r.FromUtc && c.StartedAtUtc < r.ToUtcExclusive)
                    join s in db.CampaignStatsRollups.AsNoTracking().Where(s => s.TenantId == T) on c.Id equals s.CampaignId into sj
                    from s in sj.DefaultIfEmpty()
                    select new { c.Name, c.Status, c.StartedAtUtc, Total = s == null ? 0 : s.Total, Sent = s == null ? 0 : s.Sent + s.Delivered + s.Read + s.Replied, Delivered = s == null ? 0 : s.Delivered + s.Read + s.Replied, Replied = s == null ? 0 : s.Replied, Failed = s == null ? 0 : s.Failed, Conv = s == null ? 0 : s.Conversions, c.Id };
        var total = await rows0.LongCountAsync(ct);
        var page = await rows0.OrderByDescending(x => x.StartedAtUtc).ThenBy(x => x.Id).Skip(skip).Take(take).ToListAsync(ct);
        var rows = page.Select(x => Row(x.Name, x.Status.ToString(), x.StartedAtUtc, (long)x.Total, (long)x.Sent, (long)x.Delivered, (long)x.Replied, (long)x.Failed, (long)x.Conv, Pct(x.Delivered, x.Sent), Pct(x.Replied, x.Sent))).ToList();
        var t = await rows0.GroupBy(_ => 1).Select(g => new { Total = g.Sum(x => x.Total), Sent = g.Sum(x => x.Sent), Delivered = g.Sum(x => x.Delivered), Replied = g.Sum(x => x.Replied), Failed = g.Sum(x => x.Failed), Conv = g.Sum(x => x.Conv) }).FirstOrDefaultAsync(ct);
        return new ReportPage(rows, t is null ? null : Row("Total", null, null, (long)t.Total, (long)t.Sent, (long)t.Delivered, (long)t.Replied, (long)t.Failed, (long)t.Conv, Pct(t.Delivered, t.Sent), Pct(t.Replied, t.Sent)), total,
            ["Statistics come from the campaign statistics rollup, recomputed from the recipient rows by the campaign engine after every change."]);
    }

    // ---------- 4. pipeline ----------

    private async Task<ReportPage> PipelineAsync(int skip, int take, CancellationToken ct)
    {
        var stages = await (from s in db.PipelineStages.AsNoTracking().Where(s => s.TenantId == T) join p in db.Pipelines.AsNoTracking().Where(p => p.TenantId == T) on s.PipelineId equals p.Id
                            orderby p.Name, s.Position select new { StageId = s.Id, Pipeline = p.Name, Stage = s.Name, s.Kind }).ToListAsync(ct);
        var leads = (await q.ActiveLeads().GroupBy(l => l.PipelineStageId).Select(g => new { Id = g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Id, x => x.N);
        var oq = db.Opportunities.AsNoTracking().Where(o => o.TenantId == T && o.Status == OpportunityStatus.Open);
        if (q.OwnerScope is { } me) oq = oq.Where(o => o.OwnerUserId == me);
        var opps = await oq.GroupBy(o => new { o.PipelineStageId, o.Currency }).Select(g => new { g.Key.PipelineStageId, g.Key.Currency, N = g.Count(), V = g.Sum(o => o.Value) }).ToListAsync(ct);
        var rows = stages.Select(s =>
        {
            var os = opps.Where(o => o.PipelineStageId == s.StageId).ToList();
            return Row(s.Pipeline, s.Stage, s.Kind.ToString(), (long)leads.GetValueOrDefault(s.StageId), (long)os.Sum(o => o.N), os.Count == 0 ? "" : MoneyText(os.Select(o => (o.Currency, o.V))));
        }).ToList();
        var totals = Row("Total", (long)leads.Where(l => stages.Any(s => s.StageId == l.Key)).Sum(l => l.Value), (long)opps.Sum(o => o.N),
            MoneyText(opps.GroupBy(o => o.Currency).Select(g => (g.Key, g.Sum(o => o.V)))), null);
        // the totals row has one cell fewer than columns would suggest: keep it aligned (6 columns)
        return Page(rows, [totals[0], null, null, totals[1], totals[2], totals[3]], skip, take, "A snapshot of today (not date-filtered). Opportunity values are summed per currency and never added across currencies.");
    }

    // ---------- 5. funnel ----------

    private async Task<ReportPage> FunnelAsync(ResolvedRange r, int skip, int take, CancellationToken ct)
    {
        var cohort = db.Leads.AsNoTracking().Where(l => l.TenantId == T && l.CreatedAtUtc >= r.FromUtc && l.CreatedAtUtc < r.ToUtcExclusive);
        if (q.OwnerScope is { } me) cohort = cohort.Where(l => l.OwnerUserId == me);
        var t = T;
        var byFit = db.LeadQualifications.Where(x => x.TenantId == t && (x.Fit == QualificationFit.Possible || x.Fit == QualificationFit.Good)).Select(x => x.LeadId);
        var intents = new[] { Intents.Pricing, Intents.Demo, Intents.Trial };
        var byIntent = from i in db.AiInteractions.Where(i => i.TenantId == t && intents.Contains(i.Intent)) join cv in db.Conversations.Where(c => c.TenantId == t) on i.ConversationId equals cv.Id join c in db.Contacts.Where(c => c.TenantId == t) on cv.ContactId equals c.Id select c.LeadId;
        var interested = byFit.Union(byIntent);

        var created = await cohort.LongCountAsync(ct);
        var contacted = await cohort.LongCountAsync(l => db.Contacts.Any(c => c.TenantId == t && c.LeadId == l.Id && db.Messages.Any(m => m.TenantId == t && m.ContactId == c.Id && m.Direction == MessageDirection.Outbound && m.SentAtUtc != null)), ct);
        var replied = await cohort.LongCountAsync(l => db.Contacts.Any(c => c.TenantId == t && c.LeadId == l.Id && db.Messages.Any(m => m.TenantId == t && m.ContactId == c.Id && m.Direction == MessageDirection.Inbound)), ct);
        var inter = await cohort.LongCountAsync(l => interested.Contains(l.Id), ct);
        var qualified = await cohort.LongCountAsync(l => db.LeadQualifications.Any(x => x.TenantId == t && x.LeadId == l.Id && x.Fit == QualificationFit.Good), ct);
        var demoBooked = await cohort.LongCountAsync(l => db.Demos.Any(d => d.TenantId == t && d.LeadId == l.Id), ct);
        var demoDone = await cohort.LongCountAsync(l => db.Demos.Any(d => d.TenantId == t && d.LeadId == l.Id && d.Status == DemoStatus.Completed), ct);
        var trial = await cohort.LongCountAsync(l => db.Trials.Any(x => x.TenantId == t && x.LeadId == l.Id), ct);
        var customer = await cohort.LongCountAsync(l => db.Customers.Any(c => c.TenantId == t && c.LeadId == l.Id), ct);
        var steps = new (string, long)[] { ("Leads created", created), ("Contacted (a message was sent)", contacted), ("Replied (an inbound message)", replied), ("Interested (fit Possible/Good or buying intent)", inter), ("Qualified (fit Good)", qualified), ("Demo booked", demoBooked), ("Demo completed", demoDone), ("Trial started", trial), ("Customer", customer) };
        var rows = new List<IReadOnlyList<object?>>(); long prev = created;
        for (var i = 0; i < steps.Length; i++) { rows.Add(Row(steps[i].Item1, steps[i].Item2, i == 0 ? null : Pct(steps[i].Item2, prev), i == 0 ? null : Pct(steps[i].Item2, created))); prev = steps[i].Item2; }
        return Page(rows, null, skip, take, "Each step counts the leads of the cohort that reached it at any time since creation. Steps are independent (a lead can be Qualified without being Contacted), so a step can exceed the one above it.");
    }

    // ---------- 6. representatives ----------

    private async Task<ReportPage> RepsAsync(ResolvedRange r, int skip, int take, CancellationToken ct)
    {
        var me = q.OwnerScope; var t = T;
        var owned = q.ActiveLeads().Where(l => l.OwnerUserId != null).GroupBy(l => l.OwnerUserId!.Value).Select(g => new { Id = g.Key, N = g.Count() });
        var sent = db.Messages.AsNoTracking().Where(m => m.TenantId == t && m.Direction == MessageDirection.Outbound && m.SentAtUtc >= r.FromUtc && m.SentAtUtc < r.ToUtcExclusive && m.CreatedByUserId != null && !m.IsAiGenerated);
        if (me is { } u1) sent = sent.Where(m => m.CreatedByUserId == u1);
        var msgs = sent.GroupBy(m => m.CreatedByUserId!.Value).Select(g => new { Id = g.Key, N = g.Count() });
        var demos = db.Demos.AsNoTracking().Where(d => d.TenantId == t);
        if (me is { } u2) demos = demos.Where(d => d.RepUserId == u2);
        var done = demos.Where(d => d.CompletedAtUtc >= r.FromUtc && d.CompletedAtUtc < r.ToUtcExclusive).GroupBy(d => d.RepUserId).Select(g => new { Id = g.Key, N = g.Count() });
        var noShow = demos.Where(d => d.Status == DemoStatus.NoShow && d.StartUtc >= r.FromUtc && d.StartUtc < r.ToUtcExclusive).GroupBy(d => d.RepUserId).Select(g => new { Id = g.Key, N = g.Count() });
        var opps = db.Opportunities.AsNoTracking().Where(o => o.TenantId == t && o.OwnerUserId != null && (o.Status == OpportunityStatus.Won || o.Status == OpportunityStatus.Lost) && o.UpdatedAtUtc >= r.FromUtc && o.UpdatedAtUtc < r.ToUtcExclusive);
        if (me is { } u3) opps = opps.Where(o => o.OwnerUserId == u3);
        var oppRows = await opps.GroupBy(o => new { Id = o.OwnerUserId!.Value, o.Status, o.Currency }).Select(g => new { g.Key.Id, g.Key.Status, g.Key.Currency, N = g.Count(), V = g.Sum(o => o.Value) }).ToListAsync(ct);

        var ownedD = (await owned.ToListAsync(ct)).ToDictionary(x => x.Id, x => x.N); var msgD = (await msgs.ToListAsync(ct)).ToDictionary(x => x.Id, x => x.N);
        var doneD = (await done.ToListAsync(ct)).ToDictionary(x => x.Id, x => x.N); var nsD = (await noShow.ToListAsync(ct)).ToDictionary(x => x.Id, x => x.N);
        var ids = ownedD.Keys.Concat(msgD.Keys).Concat(doneD.Keys).Concat(nsD.Keys).Concat(oppRows.Select(o => o.Id)).ToHashSet();
        if (me is { } mine) { ids.RemoveWhere(i => i != mine); ids.Add(mine); }
        var names = await db.Users.AsNoTracking().Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.DisplayName }).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        var rows = ids.Select(id =>
        {
            var won = oppRows.Where(o => o.Id == id && o.Status == OpportunityStatus.Won).ToList(); var lost = oppRows.Where(o => o.Id == id && o.Status == OpportunityStatus.Lost).ToList();
            return (Name: names.GetValueOrDefault(id) ?? "(unknown user)", Cells: Row(names.GetValueOrDefault(id) ?? "(unknown user)", (long)ownedD.GetValueOrDefault(id), (long)msgD.GetValueOrDefault(id), (long)doneD.GetValueOrDefault(id), (long)nsD.GetValueOrDefault(id), (long)won.Sum(o => o.N), (long)lost.Sum(o => o.N), won.Count == 0 ? "" : MoneyText(won.Select(o => (o.Currency, o.V)))));
        }).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).Select(x => x.Cells).ToList();
        return Page(rows, null, skip, take, me is null ? "Team view: every person with owned leads or recorded work in the period." : "Own view: only your own recorded work.", "Opportunity won/lost are counted at their last change. Won value is per currency, never added across currencies.");
    }

    // ---------- 7. AI agents ----------

    private async Task<ReportPage> AiAsync(ResolvedRange r, int skip, int take, CancellationToken ct)
    {
        var t = T;
        var inter = db.AiInteractions.AsNoTracking().Where(i => i.TenantId == t && i.CreatedAtUtc >= r.FromUtc && i.CreatedAtUtc < r.ToUtcExclusive);
        var g1 = await inter.GroupBy(i => i.AgentId).Select(g => new
        {
            Agent = g.Key, N = g.Count(), Drafts = g.Count(i => i.Decision == AiDecision.Draft), Esc = g.Count(i => i.Decision == AiDecision.Escalate), NoReply = g.Count(i => i.Decision == AiDecision.NoReply),
            Auto = g.Count(i => i.Status == DraftStatus.AutoSent), Used = g.Count(i => i.Status == DraftStatus.Used), Dis = g.Count(i => i.Status == DraftStatus.Dismissed), Inj = g.Count(i => i.InjectionSuspected), Lat = g.Average(i => (double)i.LatencyMs)
        }).ToListAsync(ct);
        var tools = await db.AiToolExecutions.AsNoTracking().Where(x => x.TenantId == t && x.OccurredAtUtc >= r.FromUtc && x.OccurredAtUtc < r.ToUtcExclusive)
            .GroupBy(x => x.AgentId).Select(g => new { Agent = g.Key, Ok = g.Count(x => x.Outcome == ToolOutcome.Executed), Bad = g.Count(x => x.Outcome == ToolOutcome.Denied || x.Outcome == ToolOutcome.Failed || x.Outcome == ToolOutcome.Invalid) }).ToListAsync(ct);
        var agentIds = g1.Select(x => x.Agent).Concat(tools.Select(x => x.Agent)).Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
        var names = await db.SalesAgents.AsNoTracking().Where(a => a.TenantId == t && agentIds.Contains(a.Id)).Select(a => new { a.Id, a.Name }).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var keys = g1.Select(x => x.Agent).Concat(tools.Select(x => x.Agent)).Distinct().ToList();
        var rows = keys.Select(k =>
        {
            var a = g1.FirstOrDefault(x => x.Agent == k); var tl = tools.FirstOrDefault(x => x.Agent == k);
            var name = k is null ? "(knowledge assistant, no sales agent)" : names.GetValueOrDefault(k.Value) ?? "(deleted agent)";
            long n = a?.N ?? 0, drafts = a?.Drafts ?? 0, esc = a?.Esc ?? 0, auto = a?.Auto ?? 0;
            return (Name: name, Cells: Row(name, n, drafts, esc, (long)(a?.NoReply ?? 0), auto, (long)(a?.Used ?? 0), (long)(a?.Dis ?? 0), (long)(a?.Inj ?? 0), a is null ? 0L : (long)Math.Round(a.Lat), (long)(tl?.Ok ?? 0), (long)(tl?.Bad ?? 0), Pct(auto, drafts), Pct(esc, n)));
        }).OrderBy(x => x.Name).Select(x => x.Cells).ToList();
        var usage = await db.AiUsageEntries.AsNoTracking().Where(u => u.TenantId == t && u.OccurredAtUtc >= r.FromUtc && u.OccurredAtUtc < r.ToUtcExclusive).GroupBy(_ => 1).Select(g => new { Calls = g.Count(), Cost = g.Sum(u => u.EstimatedCostUsd), In = g.Sum(u => (long)u.InputTokens), Out = g.Sum(u => (long)u.OutputTokens) }).FirstOrDefaultAsync(ct);
        var notes = new List<string>();
        if (usage is not null) notes.Add($"ESTIMATED: the AI provider was called {usage.Calls} time(s) in this period ({usage.In:N0} input / {usage.Out:N0} output tokens), costing about USD {usage.Cost.ToString("N4", Inv)} by the configured price assumptions (not an invoice).");
        return Page(rows, null, skip, take, notes.ToArray());
    }

    // ---------- 8. demo conversion ----------

    private async Task<ReportPage> DemosAsync(ResolvedRange r, int skip, int take, CancellationToken ct)
    {
        var t = T;
        var cohort = db.Demos.AsNoTracking().Where(d => d.TenantId == t && d.CreatedAtUtc >= r.FromUtc && d.CreatedAtUtc < r.ToUtcExclusive);
        if (q.OwnerScope is { } me) { var owned = db.Leads.Where(l => l.TenantId == t && l.OwnerUserId == me).Select(l => l.Id); cohort = cohort.Where(d => owned.Contains(d.LeadId)); }
        var by = (await cohort.GroupBy(d => d.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => (long)x.N);
        long C(DemoStatus s) => by.GetValueOrDefault(s);
        var booked = by.Values.Sum(); var open = C(DemoStatus.Requested) + C(DemoStatus.Booked) + C(DemoStatus.Confirmed);
        var completed = cohort.Where(d => d.Status == DemoStatus.Completed && d.CompletedAtUtc != null);
        var completedLeads = await completed.Select(d => d.LeadId).Distinct().LongCountAsync(ct);
        var toTrial = await completed.Where(d => db.Trials.Any(x => x.TenantId == t && x.LeadId == d.LeadId && x.CreatedAtUtc >= d.CompletedAtUtc)).Select(d => d.LeadId).Distinct().LongCountAsync(ct);
        var toCustomer = await completed.Where(d => db.Customers.Any(x => x.TenantId == t && x.LeadId == d.LeadId && x.ConvertedAtUtc >= d.CompletedAtUtc)).Select(d => d.LeadId).Distinct().LongCountAsync(ct);
        var rows = new List<IReadOnlyList<object?>>
        {
            Row("Demos booked", booked, null, "Demos created in the period"), Row("Completed", C(DemoStatus.Completed), Pct(C(DemoStatus.Completed), booked), "completed ÷ booked × 100"),
            Row("No-show", C(DemoStatus.NoShow), Pct(C(DemoStatus.NoShow), booked), "no-show ÷ booked × 100"), Row("Cancelled", C(DemoStatus.Cancelled), Pct(C(DemoStatus.Cancelled), booked), "cancelled ÷ booked × 100"),
            Row("Still open (requested, booked or confirmed)", open, Pct(open, booked), "open ÷ booked × 100"),
            Row("Leads with a completed demo", completedLeads, null, "distinct leads"), Row("…of which started a trial afterwards", toTrial, Pct(toTrial, completedLeads), "leads with a trial created after the demo ÷ leads with a completed demo × 100"),
            Row("…of which became a customer afterwards", toCustomer, Pct(toCustomer, completedLeads), "leads converted after the demo ÷ leads with a completed demo × 100"),
        };
        return Page(rows, null, skip, take);
    }

    // ---------- 9. trial conversion ----------

    private async Task<ReportPage> TrialsAsync(ResolvedRange r, int skip, int take, CancellationToken ct)
    {
        var t = T;
        var cohort = db.Trials.AsNoTracking().Where(x => x.TenantId == t && x.CreatedAtUtc >= r.FromUtc && x.CreatedAtUtc < r.ToUtcExclusive);
        if (q.OwnerScope is { } me) { var owned = db.Leads.Where(l => l.TenantId == t && l.OwnerUserId == me).Select(l => l.Id); cohort = cohort.Where(x => owned.Contains(x.LeadId)); }
        var by = (await cohort.GroupBy(x => x.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct)).ToDictionary(x => x.Key, x => (long)x.N);
        long C(TrialStatus s) => by.GetValueOrDefault(s);
        var started = by.Values.Sum(); var ended = C(TrialStatus.Converted) + C(TrialStatus.Expired) + C(TrialStatus.Cancelled);
        var rows = new List<IReadOnlyList<object?>>
        {
            Row("Trials started", started, null, "Trials created in the period"), Row("Pending provisioning", C(TrialStatus.PendingProvisioning), Pct(C(TrialStatus.PendingProvisioning), started), "count ÷ started × 100"),
            Row("Active", C(TrialStatus.Active), Pct(C(TrialStatus.Active), started), "count ÷ started × 100"), Row("Converted to customer", C(TrialStatus.Converted), Pct(C(TrialStatus.Converted), started), "converted ÷ started × 100 (conversion rate)"),
            Row("Expired without converting", C(TrialStatus.Expired), Pct(C(TrialStatus.Expired), started), "expired ÷ started × 100"), Row("Cancelled", C(TrialStatus.Cancelled), Pct(C(TrialStatus.Cancelled), started), "cancelled ÷ started × 100"),
            Row("Conversion rate of ended trials", C(TrialStatus.Converted), Pct(C(TrialStatus.Converted), ended), "converted ÷ (converted + expired + cancelled) × 100 — ignores trials that are still running"),
        };
        return Page(rows, null, skip, take);
    }

    // ---------- 10. revenue ----------

    private async Task<ReportPage> RevenueAsync(ResolvedRange r, int skip, int take, CancellationToken ct)
    {
        var g = await db.Payments.AsNoTracking().Where(p => p.TenantId == T && p.PaidAtUtc >= r.FromUtc && p.PaidAtUtc < r.ToUtcExclusive)
            .GroupBy(p => p.Currency).Select(x => new { Currency = x.Key, N = x.Count(), C = x.Select(p => p.CustomerId).Distinct().Count(), S = x.Sum(p => p.Amount) }).OrderBy(x => x.Currency).ToListAsync(ct);
        var rows = g.Select(x => Row(x.Currency, (long)x.N, (long)x.C, x.S, x.N == 0 ? null : decimal.Round(x.S / x.N, 2))).ToList();
        return Page(rows, null, skip, take, "Only verified payments are revenue (a payment row exists only after verification). Amounts of different currencies are never added together, so there is no grand total.");
    }

    // ---------- 11. messaging usage ----------

    private async Task<ReportPage> MessagingAsync(ResolvedRange r, TimeZoneInfo tz, int skip, int take, DailyReader daily, CancellationToken ct)
    {
        var keys = new[] { StreamKeys.MessagesSent, StreamKeys.MessagesDelivered, StreamKeys.MessagesFailed, StreamKeys.Responses };
        var cells = await daily.ReadAsync(keys, r.FromLocal, r.ToLocal, r.FromUtc, r.ToUtcExclusive, tz, ct);
        var byDay = cells.GroupBy(c => c.Day).ToDictionary(g => g.Key, g => g.ToDictionary(c => c.Key, c => c.Count));
        var rows = new List<IReadOnlyList<object?>>(); long s = 0, d = 0, f = 0, i = 0;
        for (var day = r.ToLocal; day >= r.FromLocal; day = day.AddDays(-1))
        {
            byDay.TryGetValue(day, out var m); long Get(string k) => m is not null && m.TryGetValue(k, out var v) ? v : 0;
            var sent = Get(StreamKeys.MessagesSent); var del = Get(StreamKeys.MessagesDelivered); var fail = Get(StreamKeys.MessagesFailed); var inb = Get(StreamKeys.Responses);
            s += sent; d += del; f += fail; i += inb;
            rows.Add(Row(day.ToString("yyyy-MM-dd", Inv), sent, del, fail, inb, Pct(del, sent)));
        }
        return Page(rows, Row("Total", s, d, f, i, Pct(d, s)), skip, take, "Days are workspace-local calendar days. Delivered is attributed to the day the message was SENT.");
    }
}

public sealed record DailyCell(DateOnly Day, string Key, long Count, decimal Amount);

/// <summary>Reads per-day counts either from the rollup or live, whichever the caller decided (so the dashboard and the reports share one source rule).</summary>
public sealed class DailyReader(AppDbContext db, AnalyticsQueries q, bool useRollup)
{
    public async Task<List<DailyCell>> ReadAsync(IReadOnlyList<string> keys, DateOnly fromDay, DateOnly toDay, DateTime fromUtc, DateTime toUtcExclusive, TimeZoneInfo tz, CancellationToken ct)
    {
        if (useRollup)
        {
            DateTime a = fromDay.ToDateTime(TimeOnly.MinValue), b = toDay.ToDateTime(TimeOnly.MinValue);
            var rows = await db.DailyMetrics.AsNoTracking().Where(m => m.TenantId == q.TenantId && keys.Contains(m.MetricKey) && m.Day >= a && m.Day <= b).Select(m => new { m.Day, m.MetricKey, m.Count, m.Amount }).ToListAsync(ct);
            return rows.Select(m => new DailyCell(DateOnly.FromDateTime(m.Day), m.MetricKey, m.Count, m.Amount)).ToList();
        }
        var all = new List<DailyCell>();
        foreach (var k in keys) all.AddRange((await q.DaysAsync(k, fromUtc, toUtcExclusive, tz, ct)).Select(c => new DailyCell(c.Day, k, c.Count, c.Amount)));
        return all;
    }
}
