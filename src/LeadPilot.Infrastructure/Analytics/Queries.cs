using LeadPilot.Application.Ai;
using LeadPilot.Application.Analytics;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Analytics;

/// <summary>One recorded event as the analytics see it: when, which lead (when known), money dimension and amount.</summary>
public sealed class EventRow
{
    public DateTime At { get; set; }
    public Guid? LeadId { get; set; }
    public string Dim { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public sealed record DayCell(DateOnly Day, string Dim, long Count, decimal Amount);

/// <summary>
/// Every analytics query. Aggregation happens in SQL (COUNT / SUM / GROUP BY); only grouped result rows are materialised, never the underlying records. Every root query names the tenant
/// (on top of the global query filter) so the tenant index is used, and an optional owner scope restricts everything to the leads of one person.
/// </summary>
public sealed class AnalyticsQueries(AppDbContext db, Guid tenantId, Guid? ownerScope)
{
    private const int BucketMinutes = 15;
    private static readonly HandoffKind[] HumanHandoffs = [HandoffKind.Takeover, HandoffKind.Escalated, HandoffKind.AutoTakeover];
    private static readonly string[] InterestIntents = [Intents.Pricing, Intents.Demo, Intents.Trial];

    public Guid TenantId => tenantId;
    public Guid? OwnerScope => ownerScope;

    // ---------- scoping helpers ----------

    private IQueryable<Guid> OwnedLeadIds() => db.Leads.Where(l => l.TenantId == tenantId && l.OwnerUserId == ownerScope).Select(l => l.Id);
    private IQueryable<Guid> OwnedContactIds() { var owned = OwnedLeadIds(); return db.Contacts.Where(c => c.TenantId == tenantId && owned.Contains(c.LeadId)).Select(c => c.Id); }
    private IQueryable<Guid> OwnedConversationIds() { var contacts = OwnedContactIds(); return db.Conversations.Where(c => c.TenantId == tenantId && c.ContactId != null && contacts.Contains(c.ContactId.Value)).Select(c => c.Id); }

    public IQueryable<Lead> ActiveLeads()
    {
        var q = db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && !l.IsArchived);
        return ownerScope is null ? q : q.Where(l => l.OwnerUserId == ownerScope);
    }

    private IQueryable<Message> OutboundMessages()
    {
        var q = db.Messages.AsNoTracking().Where(m => m.TenantId == tenantId && m.Direction == MessageDirection.Outbound);
        if (ownerScope is null) return q;
        var contacts = OwnedContactIds();
        return q.Where(m => m.ContactId != null && contacts.Contains(m.ContactId.Value));
    }

    // ---------- the recorded-event streams (what the rollup stores per day) ----------

    /// <summary>The stream of recorded events behind one period metric. Each element is ONE recorded fact, so counting elements in a range is exactly the metric.</summary>
    public IQueryable<EventRow> Stream(string key, DateTime fromUtc, DateTime toUtc)
    {
        var t = tenantId;
        switch (key)
        {
            case StreamKeys.LeadsCreated:
                return Scope(db.Leads.AsNoTracking().Where(l => l.TenantId == t)).Select(l => new EventRow { At = l.CreatedAtUtc, LeadId = l.Id, Dim = "", Amount = 0m });
            case StreamKeys.LeadsContacted:
            {
                // a lead counts once, on the day of its FIRST outbound message: candidates are the leads with a send inside [from, to); a lead that was messaged before `from` is not new
                var inRange = from m in OutboundMessages() where m.SentAtUtc >= fromUtc && m.SentAtUtc < toUtc && m.ContactId != null
                              join c in db.Contacts.Where(c => c.TenantId == t) on m.ContactId equals c.Id
                              group m by c.LeadId into g
                              select new { LeadId = g.Key, First = g.Min(x => x.SentAtUtc!.Value) };
                return inRange.Where(x => !db.Contacts.Any(c2 => c2.TenantId == t && c2.LeadId == x.LeadId && db.Messages.Any(m2 => m2.TenantId == t && m2.ContactId == c2.Id && m2.Direction == MessageDirection.Outbound && m2.SentAtUtc < fromUtc)))
                              .Select(x => new EventRow { At = x.First, LeadId = x.LeadId, Dim = "", Amount = 0m });
            }
            case StreamKeys.Responses:
            {
                var q = db.Messages.AsNoTracking().Where(m => m.TenantId == t && m.Direction == MessageDirection.Inbound && m.ContactId != null);
                if (ownerScope is not null) { var contacts = OwnedContactIds(); q = q.Where(m => contacts.Contains(m.ContactId!.Value)); }
                return q.Select(m => new EventRow { At = m.OccurredAtUtc, Dim = "", Amount = 0m });
            }
            case StreamKeys.MessagesSent:
                return OutboundMessages().Where(m => m.SentAtUtc != null).Select(m => new EventRow { At = m.SentAtUtc!.Value, Dim = "", Amount = 0m });
            case StreamKeys.MessagesDelivered:
                return OutboundMessages().Where(m => m.SentAtUtc != null && m.DeliveredAtUtc != null).Select(m => new EventRow { At = m.SentAtUtc!.Value, Dim = "", Amount = 0m });
            case StreamKeys.MessagesFailed:
                return OutboundMessages().Where(m => m.FailedAtUtc != null).Select(m => new EventRow { At = m.FailedAtUtc!.Value, Dim = "", Amount = 0m });
            case StreamKeys.DemosScheduled:
                return Scope(db.Demos.AsNoTracking().Where(d => d.TenantId == t), d => d.LeadId).Select(d => new EventRow { At = d.CreatedAtUtc, LeadId = d.LeadId, Dim = "", Amount = 0m });
            case StreamKeys.DemosCompleted:
                return Scope(db.Demos.AsNoTracking().Where(d => d.TenantId == t && d.CompletedAtUtc != null), d => d.LeadId).Select(d => new EventRow { At = d.CompletedAtUtc!.Value, LeadId = d.LeadId, Dim = "", Amount = 0m });
            case StreamKeys.TrialsStarted:
                return Scope(db.Trials.AsNoTracking().Where(x => x.TenantId == t), x => x.LeadId).Select(x => new EventRow { At = x.CreatedAtUtc, LeadId = x.LeadId, Dim = "", Amount = 0m });
            case StreamKeys.CustomersWon:
                return Scope(db.Customers.AsNoTracking().Where(x => x.TenantId == t), x => x.LeadId).Select(x => new EventRow { At = x.ConvertedAtUtc, LeadId = x.LeadId, Dim = "", Amount = 0m });
            case StreamKeys.OpportunitiesLost:
                return Scope(db.Opportunities.AsNoTracking().Where(o => o.TenantId == t && o.Status == OpportunityStatus.Lost && o.UpdatedAtUtc != null), o => o.LeadId).Select(o => new EventRow { At = o.UpdatedAtUtc!.Value, LeadId = o.LeadId, Dim = "", Amount = 0m });
            case StreamKeys.AiConversations:
            {
                var q = db.AiInteractions.AsNoTracking().Where(i => i.TenantId == t);
                if (ownerScope is not null) { var convs = OwnedConversationIds(); q = q.Where(i => convs.Contains(i.ConversationId)); }
                return q.GroupBy(i => i.ConversationId).Select(g => new EventRow { At = g.Min(x => x.CreatedAtUtc), Dim = "", Amount = 0m });
            }
            case StreamKeys.Handoffs:
            {
                var q = db.HandoffEvents.AsNoTracking().Where(h => h.TenantId == t && HumanHandoffs.Contains(h.Kind));
                if (ownerScope is not null) { var convs = OwnedConversationIds(); q = q.Where(h => convs.Contains(h.ConversationId)); }
                return q.Select(h => new EventRow { At = h.OccurredAtUtc, Dim = "", Amount = 0m });
            }
            case StreamKeys.CampaignSent:
                return Scope(db.CampaignRecipients.AsNoTracking().Where(r => r.TenantId == t && r.SentAtUtc != null), r => r.LeadId).Select(r => new EventRow { At = r.SentAtUtc!.Value, LeadId = r.LeadId, Dim = "", Amount = 0m });
            case StreamKeys.CampaignReplied:
                return Scope(db.CampaignRecipients.AsNoTracking().Where(r => r.TenantId == t && r.SentAtUtc != null && r.RepliedAtUtc != null), r => r.LeadId).Select(r => new EventRow { At = r.SentAtUtc!.Value, LeadId = r.LeadId, Dim = "", Amount = 0m });
            case StreamKeys.Revenue:
                return from p in db.Payments.AsNoTracking().Where(p => p.TenantId == t)
                       join c in db.Customers.Where(c => c.TenantId == t) on p.CustomerId equals c.Id
                       select new EventRow { At = p.PaidAtUtc, LeadId = c.LeadId, Dim = p.Currency, Amount = p.Amount };
            default: throw new ArgumentException($"Unknown stream '{key}'.", nameof(key));
        }
    }

    private IQueryable<Lead> Scope(IQueryable<Lead> q) => ownerScope is null ? q : q.Where(l => l.OwnerUserId == ownerScope);

    private IQueryable<T> Scope<T>(IQueryable<T> q, System.Linq.Expressions.Expression<Func<T, Guid>> leadId)
    {
        if (ownerScope is null) return q;
        var owned = OwnedLeadIds();
        var param = leadId.Parameters[0];
        var contains = System.Linq.Expressions.Expression.Call(typeof(Queryable), nameof(Queryable.Contains), [typeof(Guid)], owned.Expression, leadId.Body);
        return q.Where(System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(contains, param));
    }

    /// <summary>One 15-minute bucket of a stream (a grouped result row, never a record).</summary>
    public sealed class BucketRow { public int Year { get; set; } public int Month { get; set; } public int Day { get; set; } public int Hour { get; set; } public int Q { get; set; } public string Dim { get; set; } = ""; public long N { get; set; } public decimal S { get; set; } }

    /// <summary>The grouped query behind <see cref="DaysAsync"/> (exposed so its SQL and plan can be inspected).</summary>
    public IQueryable<BucketRow> Buckets(string key, DateTime fromUtc, DateTime toUtc) =>
        Stream(key, fromUtc, toUtc).Where(e => e.At >= fromUtc && e.At < toUtc)
            .GroupBy(e => new { e.At.Year, e.At.Month, e.At.Day, e.At.Hour, Q = e.At.Minute < 15 ? 0 : e.At.Minute < 30 ? 1 : e.At.Minute < 45 ? 2 : 3, e.Dim })
            .Select(g => new BucketRow { Year = g.Key.Year, Month = g.Key.Month, Day = g.Key.Day, Hour = g.Key.Hour, Q = g.Key.Q, Dim = g.Key.Dim, N = g.LongCount(), S = g.Sum(x => x.Amount) });

    /// <summary>Events of a stream in [fromUtc, toUtc), grouped in SQL into 15-minute buckets (every real time zone's midnight falls on a bucket edge) and folded into workspace-local days here.</summary>
    public async Task<List<DayCell>> DaysAsync(string key, DateTime fromUtc, DateTime toUtc, TimeZoneInfo tz, CancellationToken ct)
    {
        var rows = await Buckets(key, fromUtc, toUtc).ToListAsync(ct);
        var cells = new Dictionary<(DateOnly Day, string Dim), (long N, decimal S)>();
        foreach (var r in rows)
        {
            var day = RangeResolver.LocalDay(tz, new DateTime(r.Year, r.Month, r.Day, r.Hour, r.Q * BucketMinutes, 0, DateTimeKind.Utc));
            var k = (day, r.Dim);
            cells[k] = cells.TryGetValue(k, out var cur) ? (cur.N + r.N, cur.S + r.S) : (r.N, r.S);
        }
        return cells.Select(kv => new DayCell(kv.Key.Day, kv.Key.Dim, kv.Value.N, kv.Value.S)).OrderBy(c => c.Day).ThenBy(c => c.Dim).ToList();
    }

    /// <summary>Total of a stream in [fromUtc, toUtc) per dimension (one grouped SQL query).</summary>
    public async Task<List<(string Dim, long Count, decimal Amount)>> TotalsAsync(string key, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var rows = await Stream(key, fromUtc, toUtc).Where(e => e.At >= fromUtc && e.At < toUtc).GroupBy(e => e.Dim).Select(g => new { Dim = g.Key, N = g.LongCount(), S = g.Sum(x => x.Amount) }).ToListAsync(ct);
        return rows.Select(r => (r.Dim, r.N, r.S)).ToList();
    }

    // ---------- snapshot metrics ----------

    public const string SnapshotPrefix = "snapshot:";

    /// <summary>All current-state metrics in one go (what the rollup stores per refresh).</summary>
    public async Task<Dictionary<string, long>> SnapshotsAsync(CancellationToken ct) => new()
    {
        [MetricCatalog.TotalLeads] = await TotalLeadsAsync(ct), [MetricCatalog.VerifiedLeads] = await VerifiedLeadsAsync(ct), [MetricCatalog.EligibleLeads] = await EligibleLeadsAsync(ct),
        [MetricCatalog.InterestedLeads] = await InterestedLeadsAsync(ct), [MetricCatalog.QualifiedLeads] = await QualifiedLeadsAsync(ct),
    };

    public Task<long> TotalLeadsAsync(CancellationToken ct) => ActiveLeads().LongCountAsync(ct);

    public Task<long> VerifiedLeadsAsync(CancellationToken ct) =>
        ActiveLeads().LongCountAsync(l => db.Contacts.Any(c => c.TenantId == tenantId && c.LeadId == l.Id && !c.IsDeleted && (c.DataOrigin == DataOrigin.Verified || c.PhoneVerifiedAtUtc != null)), ct);

    public Task<long> QualifiedLeadsAsync(CancellationToken ct) =>
        ActiveLeads().LongCountAsync(l => db.LeadQualifications.Any(q => q.TenantId == tenantId && q.LeadId == l.Id && q.Fit == QualificationFit.Good), ct);

    public Task<long> InterestedLeadsAsync(CancellationToken ct)
    {
        var t = tenantId;
        var byFit = db.LeadQualifications.Where(q => q.TenantId == t && (q.Fit == QualificationFit.Possible || q.Fit == QualificationFit.Good)).Select(q => q.LeadId);
        var byIntent = from i in db.AiInteractions.Where(i => i.TenantId == t && InterestIntents.Contains(i.Intent))
                       join cv in db.Conversations.Where(c => c.TenantId == t) on i.ConversationId equals cv.Id
                       join c in db.Contacts.Where(c => c.TenantId == t) on cv.ContactId equals c.Id
                       select c.LeadId;
        var ids = byFit.Union(byIntent);
        return (from l in ActiveLeads() join id in ids on l.Id equals id select l.Id).LongCountAsync(ct);
    }

    /// <summary>Mirrors ConsentService.QueryAudienceAsync for WhatsApp / Marketing, counted per lead.</summary>
    public Task<long> EligibleLeadsAsync(CancellationToken ct)
    {
        var t = tenantId; var channel = ConsentChannel.WhatsApp; var purpose = ConsentPurpose.Marketing;
        var contacts = db.Contacts.Where(c => c.TenantId == t && !c.IsDeleted && c.PhoneE164 != null
            && !db.SuppressionEntries.Any(s => s.TenantId == t && s.ValueNormalized == c.PhoneE164 && (s.Channel == SuppressionChannel.Any || s.Channel == SuppressionChannel.WhatsApp))
            && db.ConsentRecords.Where(r => r.TenantId == t && r.ContactId == c.Id && r.Channel == channel && r.Purpose == purpose)
                .OrderByDescending(r => r.OccurredAtUtc).ThenByDescending(r => r.IsRevocation).Select(r => r.Status).FirstOrDefault() == ConsentStatus.Granted)
            .Select(c => c.LeadId);
        return (from l in ActiveLeads().Where(l => !db.PipelineStages.Any(s => s.Id == l.PipelineStageId && s.Kind == StageKind.Suppressed)) join id in contacts.Distinct() on l.Id equals id select l.Id).LongCountAsync(ct);
    }

    /// <summary>Leads created in [from, to) and how many of them are customers today (cohort).</summary>
    public async Task<(long Created, long Converted)> ConversionCohortAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var cohort = Scope(db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && l.CreatedAtUtc >= fromUtc && l.CreatedAtUtc < toUtc));
        var row = await cohort.GroupBy(_ => 1).Select(g => new { N = g.LongCount(), C = g.Count(l => db.Customers.Any(c => c.TenantId == tenantId && c.LeadId == l.Id)) }).FirstOrDefaultAsync(ct);
        return row is null ? (0, 0) : (row.N, row.C);
    }
}
