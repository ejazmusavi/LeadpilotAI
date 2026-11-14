using System.Runtime.CompilerServices;
using System.Text.Json;
using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Persistence;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Campaigns;

internal sealed record Candidate(Guid ContactId, Guid LeadId, string LeadName, string? ContactName, string? Phone, string? City, string? Country, bool IsPrimary, DateTime ContactCreatedAtUtc);

internal sealed record ScanOptions(
    Guid? CampaignId, Guid? ExemptParentCampaignId, int CooldownDays, int MaxRecipients, int InitialEligible, Func<Candidate, string?>? MissingVariable);

internal sealed record ScanItem(Candidate Candidate, bool Eligible, string? Reason, string Detail, EligibilityRecord Record);

/// <summary>Outcome counters of one scan (filled while the stream is consumed).</summary>
internal sealed class ScanSession
{
    public int TotalMatching;
    public int Scanned;
    public bool Truncated;
}

/// <summary>Translation of an <see cref="AudienceDefinition"/> into one SQL query over leads and contacts. Always tenant-scoped on top of the global filter. Nothing is loaded here: callers page through it.</summary>
internal static class AudienceQueries
{
    public static IQueryable<Candidate> Candidates(AppDbContext db, Guid tenantId, AudienceDefinition d, DateTime nowUtc)
    {
        var leads = db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId);
        if (d.StatusIds.Count > 0) { var v = d.StatusIds.ToList(); leads = leads.Where(l => v.Contains(l.StatusId)); }
        if (d.StageIds.Count > 0) { var v = d.StageIds.ToList(); leads = leads.Where(l => v.Contains(l.PipelineStageId)); }
        if (d.CategoryIds.Count > 0) { var v = d.CategoryIds.Select(x => (Guid?)x).ToList(); leads = leads.Where(l => v.Contains(l.CategoryId)); }
        if (d.Countries.Count > 0) { var v = d.Countries.ToList(); leads = leads.Where(l => l.Country != null && v.Contains(l.Country)); }
        if (d.Provinces.Count > 0) { var v = d.Provinces.ToList(); leads = leads.Where(l => l.Province != null && v.Contains(l.Province)); }
        if (d.Cities.Count > 0) { var v = d.Cities.ToList(); leads = leads.Where(l => l.City != null && v.Contains(l.City)); }
        if (d.TagIds.Count > 0) { var v = d.TagIds.ToList(); leads = leads.Where(l => db.LeadTagMaps.Any(m => m.TenantId == tenantId && m.LeadId == l.Id && v.Contains(m.TagId))); }
        if (d.ExcludeTagIds.Count > 0) { var v = d.ExcludeTagIds.ToList(); leads = leads.Where(l => !db.LeadTagMaps.Any(m => m.TenantId == tenantId && m.LeadId == l.Id && v.Contains(m.TagId))); }
        if (d.SourceTypes.Count > 0) { var v = d.SourceTypes.ToList(); leads = leads.Where(l => v.Contains(l.PrimarySourceType)); }
        if (d.Unassigned) leads = leads.Where(l => l.OwnerUserId == null);
        else if (d.OwnerUserIds.Count > 0) { var v = d.OwnerUserIds.Select(x => (Guid?)x).ToList(); leads = leads.Where(l => v.Contains(l.OwnerUserId)); }
        if (d.MinScore is { } min) leads = leads.Where(l => l.Score >= min);
        if (d.MaxScore is { } max) leads = leads.Where(l => l.Score <= max);

        var contacts = db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && !c.IsDeleted);
        if (d.ContactIds.Count > 0) { var v = d.ContactIds.ToList(); contacts = contacts.Where(c => v.Contains(c.Id)); }
        if (d.NeverContacted) contacts = contacts.Where(c => !db.Messages.Any(m => m.TenantId == tenantId && m.ContactId == c.Id && m.Direction == MessageDirection.Outbound && m.SentAtUtc != null));
        else if (d.LastContactedBeforeDays is { } days) { var cut = nowUtc.AddDays(-days); contacts = contacts.Where(c => !db.Messages.Any(m => m.TenantId == tenantId && m.ContactId == c.Id && m.Direction == MessageDirection.Outbound && m.SentAtUtc != null && m.SentAtUtc >= cut)); }
        if (d.HasReply == true) contacts = contacts.Where(c => db.Messages.Any(m => m.TenantId == tenantId && m.ContactId == c.Id && m.Direction == MessageDirection.Inbound));
        else if (d.HasReply == false) contacts = contacts.Where(c => !db.Messages.Any(m => m.TenantId == tenantId && m.ContactId == c.Id && m.Direction == MessageDirection.Inbound));

        if (d.FollowUp is { } f)
        {
            var statuses = FollowUpRules.Statuses(f.Rule).ToList(); var cut = FollowUpRules.Cutoff(nowUtc, f.AfterDays);
            var parent = db.CampaignRecipients.AsNoTracking().Where(r => r.TenantId == tenantId && r.CampaignId == f.ParentCampaignId && statuses.Contains(r.Status));
            parent = f.Rule switch
            {
                FollowUpRule.SentNotReplied => parent.Where(r => r.SentAtUtc != null && r.SentAtUtc <= cut),
                FollowUpRule.DeliveredNotReplied => parent.Where(r => (r.DeliveredAtUtc ?? r.ReadAtUtc) != null && (r.DeliveredAtUtc ?? r.ReadAtUtc) <= cut),
                FollowUpRule.ReadNotReplied => parent.Where(r => r.ReadAtUtc != null && r.ReadAtUtc <= cut),
                _ => parent.Where(r => r.RepliedAtUtc != null && r.RepliedAtUtc <= cut),
            };
            contacts = contacts.Where(c => parent.Any(r => r.ContactId == c.Id));
            if (f.Rule == FollowUpRule.RepliedNotAdvanced) { var adv = (f.AdvancedStageIds ?? []).ToList(); leads = leads.Where(l => !adv.Contains(l.PipelineStageId)); }
        }

        // stable order: lead by lead, the representative contact first (has a number, primary, oldest); ordered BEFORE the projection so EF can translate it
        return from c in contacts
               join l in leads on c.LeadId equals l.Id
               orderby l.Id, c.PhoneE164 == null ? 1 : 0, c.IsPrimary ? 0 : 1, c.CreatedAtUtc, c.Id
               select new Candidate(c.Id, l.Id, l.Name, c.Name, c.PhoneE164, l.City, l.Country, c.IsPrimary, c.CreatedAtUtc);
    }
}

/// <summary>
/// Streams an audience in pages and decides, per contact, whether it becomes a recipient: one contact per lead (optional), the Phase 3 eligibility gate (<see cref="IContactEligibilityService.CheckManyAsync"/>,
/// marketing purpose), duplicate numbers, the cool-down after other campaigns, missing message variables and the recipient limit. A high lead score never appears here.
/// Used by the preview and by the snapshot builder, so both always agree.
/// </summary>
internal sealed class AudienceScanner(CrmContext crm, IContactEligibilityService eligibility, IOptions<CampaignOptions> options)
{
    private static readonly RecipientStatus[] Messaged = [RecipientStatus.Queued, RecipientStatus.Sent, RecipientStatus.Delivered, RecipientStatus.Read, RecipientStatus.Replied];

    public async IAsyncEnumerable<ScanItem> ScanAsync(AudienceDefinition def, ScanOptions o, ScanSession session, [EnumeratorCancellation] CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now; var opt = options.Value;
        var query = AudienceQueries.Candidates(db, tenantId, def, now);
        session.TotalMatching = await query.CountAsync(ct);
        var pageSize = Math.Clamp(opt.PageSize, 20, 500);
        var seenLeads = new HashSet<Guid>(); var destinations = new HashSet<string>(StringComparer.Ordinal);
        var eligibleCount = o.InitialEligible;
        var cooldownCut = o.CooldownDays > 0 ? now.AddDays(-o.CooldownDays) : (DateTime?)null;

        for (var offset = 0; offset < Math.Min(session.TotalMatching, opt.ScanLimit); offset += pageSize)
        {
            ct.ThrowIfCancellationRequested();
            var take = Math.Min(pageSize, opt.ScanLimit - offset);
            var page = await query.Skip(offset).Take(take).ToListAsync(ct);
            if (page.Count == 0) break;
            var ids = page.Select(c => c.ContactId).ToList();
            var phones = page.Where(p => p.Phone != null).Select(p => p.Phone!).Distinct().ToList();
            var decisions = await eligibility.CheckManyAsync(ids, ConsentChannel.WhatsApp, ConsentPurpose.Marketing, ct);

            var existing = o.CampaignId is { } cid
                ? await db.CampaignRecipients.AsNoTracking().Where(r => r.TenantId == tenantId && r.CampaignId == cid && (ids.Contains(r.ContactId) || (r.Destination != null && phones.Contains(r.Destination))))
                    .Select(r => new { r.ContactId, r.LeadId, r.Destination, r.Status }).ToListAsync(ct)
                : [];
            var existingContacts = existing.Select(e => e.ContactId).ToHashSet();
            foreach (var e in existing) { seenLeads.Add(e.LeadId); if (e.Destination is not null && e.Status != RecipientStatus.Skipped) destinations.Add(e.Destination); }

            var inCooldown = cooldownCut is { } cut
                ? (await db.CampaignRecipients.AsNoTracking().Where(r => r.TenantId == tenantId && ids.Contains(r.ContactId) && r.CampaignId != o.CampaignId && r.CampaignId != o.ExemptParentCampaignId
                        && r.QueuedAtUtc != null && r.QueuedAtUtc >= cut && Messaged.Contains(r.Status)).Select(r => r.ContactId).Distinct().ToListAsync(ct)).ToHashSet()
                : [];

            foreach (var c in page)
            {
                session.Scanned++;
                if (existingContacts.Contains(c.ContactId)) continue;   // a resumed build: already has its row
                var decision = decisions.TryGetValue(c.ContactId, out var dd) ? dd : EligibilityDecision.NotFound;
                var reasons = decision.Reasons.Select(ExclusionReasons.Code).ToList();
                EligibilityRecord Rec(bool ok, params string[] more) => new(now, ok, reasons.Concat(more).ToList());

                if (def.OnePerLead && !seenLeads.Add(c.LeadId)) { yield return new(c, false, ExclusionReasons.SameLead, ExclusionReasons.Explain(ExclusionReasons.SameLead), Rec(false, ExclusionReasons.SameLead)); continue; }
                if (!def.OnePerLead) seenLeads.Add(c.LeadId);
                if (!decision.IsEligible) { var code = ExclusionReasons.Primary(decision.Reasons); yield return new(c, false, code, string.Join(" ", decision.Reasons.Select(EligibilityDecision.Explain)), Rec(false)); continue; }
                if (c.Phone is not null && destinations.Contains(c.Phone)) { yield return new(c, false, ExclusionReasons.DuplicateNumber, ExclusionReasons.Explain(ExclusionReasons.DuplicateNumber), Rec(false, ExclusionReasons.DuplicateNumber)); continue; }
                if (inCooldown.Contains(c.ContactId)) { yield return new(c, false, ExclusionReasons.Cooldown, ExclusionReasons.Explain(ExclusionReasons.Cooldown), Rec(false, ExclusionReasons.Cooldown)); continue; }
                if (o.MissingVariable?.Invoke(c) is { } missing) { yield return new(c, false, ExclusionReasons.MissingVariable, $"Missing value for {{{{{missing}}}}}.", Rec(false, ExclusionReasons.MissingVariable)); continue; }
                if (eligibleCount >= o.MaxRecipients) { yield return new(c, false, ExclusionReasons.OverLimit, ExclusionReasons.Explain(ExclusionReasons.OverLimit), Rec(false, ExclusionReasons.OverLimit)); continue; }
                eligibleCount++;
                if (c.Phone is not null) destinations.Add(c.Phone);
                yield return new(c, true, null, "Eligible for WhatsApp marketing.", Rec(true));
            }
        }
        session.Truncated = session.TotalMatching > opt.ScanLimit;
    }

    public static string? MissingVariableFor(string? body, string? footer, Candidate c)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var built = CampaignContent.Build(body, footer, CampaignValues.For(c.ContactName, c.LeadName, c.Phone, c.City, c.Country));
        return built.Missing.Count > 0 ? built.Missing[0] : null;
    }
}

/// <summary>
/// Takes the audience snapshot of a campaign when it starts: streams the scan and stores one row per contact (eligible → Pending, everything else → Skipped with its reason and the decision). Resumable:
/// the unique (campaign, contact) key and the "existing rows" check make a restarted build continue instead of duplicating.
/// </summary>
internal sealed class AudienceSnapshotBuilder(CrmContext crm, AudienceScanner scanner, CampaignSettingsStore settings, CampaignCore core, IOptions<CampaignOptions> options)
{
    public async Task<CampaignAudienceSnapshot?> BuildAsync(Campaign campaign, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var (def, error) = AudienceDefinitions.Parse(campaign.AudienceJson);
        if (def is null) throw new InvalidOperationException("The stored audience definition is invalid: " + error);
        var tenantSettings = await settings.GetAsync(false, ct);

        var snap = campaign.SnapshotId is { } sid ? await db.CampaignAudienceSnapshots.FirstOrDefaultAsync(s => s.Id == sid && s.TenantId == tenantId, ct) : null;
        if (snap is { Status: SnapshotStatus.Ready }) return snap;
        if (snap is null)
        {
            snap = CampaignAudienceSnapshot.Start(tenantId, campaign.Id, campaign.AudienceVersion, campaign.AudienceJson, crm.UserId, now);
            db.CampaignAudienceSnapshots.Add(snap);
            await db.SaveChangesAsync(ct);
            await db.Campaigns.Where(c => c.Id == campaign.Id && c.TenantId == tenantId).ExecuteUpdateAsync(s => s.SetProperty(c => c.SnapshotId, snap.Id), ct);
        }

        var cooldown = campaign.CooldownDays ?? tenantSettings.CooldownDays;
        var initialEligible = await db.CampaignRecipients.AsNoTracking().CountAsync(r => r.TenantId == tenantId && r.CampaignId == campaign.Id && r.Status != RecipientStatus.Skipped, ct);
        var sequence = await db.CampaignRecipients.AsNoTracking().Where(r => r.TenantId == tenantId && r.CampaignId == campaign.Id).MaxAsync(r => (int?)r.Sequence, ct) ?? 0;
        var body = campaign.MessageBodySnapshot; var footer = campaign.IncludeOptOutFooter ? campaign.OptOutFooterSnapshot : null;
        var opts = new ScanOptions(campaign.Id, def.FollowUp?.ParentCampaignId, cooldown, campaign.MaxRecipients, initialEligible, c => AudienceScanner.MissingVariableFor(body, footer, c));
        var session = new ScanSession();
        var batch = 0;
        await foreach (var item in scanner.ScanAsync(def, opts, session, ct))
        {
            sequence++;
            var trail = EligibilityTrail.Empty.WithSelection(item.Record).ToJson();
            var c = item.Candidate;
            db.CampaignRecipients.Add(item.Eligible
                ? CampaignRecipient.Create(tenantId, campaign.Id, snap.Id, c.ContactId, c.LeadId, sequence, c.Phone, trail, now)
                : CampaignRecipient.CreateSkipped(tenantId, campaign.Id, snap.Id, c.ContactId, c.LeadId, sequence, c.Phone, item.Reason!, item.Detail, trail, now));
            if (++batch >= 200) { await db.SaveChangesAsync(ct); batch = 0; }
        }
        if (batch > 0) await db.SaveChangesAsync(ct);

        var rows = await db.CampaignRecipients.AsNoTracking().Where(r => r.TenantId == tenantId && r.CampaignId == campaign.Id).GroupBy(r => new { r.Status, r.StatusReason }).Select(g => new { g.Key.Status, g.Key.StatusReason, N = g.Count() }).ToListAsync(ct);
        var excluded = rows.Where(r => r.Status == RecipientStatus.Skipped).ToList();
        var breakdown = excluded.GroupBy(r => r.StatusReason ?? "unknown").ToDictionary(g => g.Key, g => g.Sum(x => x.N));
        var total = rows.Sum(r => r.N);
        snap = await db.CampaignAudienceSnapshots.FirstAsync(s => s.Id == snap.Id && s.TenantId == tenantId, ct);
        snap.Complete(total, total - excluded.Sum(x => x.N), excluded.Sum(x => x.N), JsonSerializer.Serialize(breakdown), session.Truncated, crm.Now);
        core.Log(campaign.Id, ExecutionLogLevel.Info, "audience_ready", $"Audience snapshot taken: {total} contact(s) matched, {snap.Recipients} will be messaged, {snap.Excluded} excluded{(session.Truncated ? $" (scan stopped at the limit of {options.Value.ScanLimit})" : "")}.");
        await db.SaveChangesAsync(ct);
        await core.RecomputeRollupAsync(campaign.Id, ct);
        return snap;
    }
}
