using System.Net;
using System.Text.Json;
using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static LeadPilot.IntegrationTests.FakeWahaServer;

namespace LeadPilot.IntegrationTests;

/// <summary>
/// Campaigns end to end against the fake WAHA (never a real gateway): create → approve → schedule → run → sent/delivered/read/replied, compliance at selection AND at send time,
/// duplicate prevention across retries and restarts, pause/resume/cancel, limits, auto-pause, kill switch, usage limits and statistics reconciliation.
/// </summary>
[Collection("app")]
public class CampaignExecutionTests(AppFixture app)
{
    private TestWorld W => app.World;
    private static async Task<T> Ok<T>(HttpResponseMessage r) => await Crm.Ok<T>(r);

    // ---------------------------------------------------------------- lifecycle

    [RequiresMySqlFact]
    public async Task Create_approve_schedule_run_and_track_sent_delivered_read_replied()
    {
        await using var w = await CampaignWorld.OpenAsync(app, freshTenant: true);
        await w.SetSettingsAsync(b => { b["approvalRequired"] = true; b["creatorCannotApprove"] = true; });
        var contacts = await w.ContactsAsync(3);
        var at = DateTime.UtcNow.AddHours(2);
        var draft = await w.CreateAsync(contacts, type: "Scheduled", scheduledAtUtc: at);
        Assert.Equal(CampaignStatus.Draft, draft.Status);
        Assert.Empty(app.Campaigns.For(draft.Id));                                       // nothing scheduled until approved

        var submitted = await w.SubmitAsync(draft.Id);
        Assert.Equal(CampaignStatus.PendingApproval, submitted.Status);
        Assert.Contains("Hello", submitted.MessageBody);

        // four eyes: the creator cannot approve their own campaign (policy default)
        var self = await w.Admin.Post($"/api/v1/campaigns/{draft.Id}/approve");
        Assert.Equal(HttpStatusCode.BadRequest, self.StatusCode);
        Assert.Contains("cannot approve", await Crm.ProblemDetail(self));

        var approver = await W.AddUserAsync(w.TenantId, $"appr-{Guid.NewGuid():N}@c.test", LeadPilot.Application.Security.SystemRoles.SalesManager);
        var approverSession = await ApiSession.LoginAsync(app, approver.Email, approver.Password);
        var approved = await Ok<CampaignDto>(await approverSession.Post($"/api/v1/campaigns/{draft.Id}/approve"));
        Assert.Equal(CampaignStatus.Scheduled, approved.Status); Assert.NotNull(approved.ApprovedByName);
        var start = Assert.Single(app.Campaigns.For(draft.Id), j => j.Kind == "start");   // ONE Hangfire start job, due at the scheduled UTC time
        Assert.Equal(at, start.AtUtc!.Value, TimeSpan.FromSeconds(1));

        // the due time arrives: the Hangfire job runs the start, then the ticks
        await w.RunAsync(sp => sp.GetRequiredService<ICampaignRunner>().StartAsync(draft.Id, default));
        Assert.Equal(CampaignStatus.Scheduled, (await w.GetAsync(draft.Id)).Status);       // too early: a no-op
        app.Clock.Offset += TimeSpan.FromHours(2).Add(TimeSpan.FromMinutes(1));
        await w.RunAsync(sp => sp.GetRequiredService<ICampaignRunner>().StartAsync(draft.Id, default));
        Assert.Equal(CampaignStatus.Running, (await w.GetAsync(draft.Id)).Status);
        var done = await w.RunToEndAsync(draft.Id);
        Assert.Equal(CampaignStatus.Completed, done.Status);

        foreach (var c in contacts)
        {
            var sent = Assert.Single(w.SentTo(c));
            Assert.StartsWith("Hello Sara, we help schools in Lahore.", sent.Text);
            Assert.EndsWith("Reply STOP to opt out.", sent.Text);                            // footer on every marketing message
        }
        Assert.All(await w.RecipientsAsync(draft.Id), r => Assert.Equal(RecipientStatus.Sent, r.Status));

        // acks and a reply arrive through the real webhook → outbox → consumer path
        await w.AckAsync(contacts[0], 2); await w.AckAsync(contacts[1], 2); await w.AckAsync(contacts[1], 3);
        await w.InboundAsync(contacts[2], "Yes, tell me more");
        var rec = await w.RecipientsAsync(draft.Id);
        Assert.Equal(RecipientStatus.Delivered, rec.Single(r => r.ContactId == contacts[0].ContactId).Status);
        Assert.Equal(RecipientStatus.Read, rec.Single(r => r.ContactId == contacts[1].ContactId).Status);
        Assert.Equal(RecipientStatus.Replied, rec.Single(r => r.ContactId == contacts[2].ContactId).Status);

        var stats = await w.StatsAsync(draft.Id);
        Assert.Equal(3, stats.Messaged); Assert.Equal(1, stats.Counts.Delivered); Assert.Equal(1, stats.Counts.Read); Assert.Equal(1, stats.Counts.Replied);
        Assert.Equal(1, stats.ReplyConversions); Assert.True(stats.RollupMatchesLive);
        var log = await Ok<PagedResult<ExecutionLogDto>>(await w.Admin.Get($"/api/v1/campaigns/{draft.Id}/log"));
        Assert.Contains(log.Items, l => l.Code == "completed"); Assert.Contains(log.Items, l => l.Code == "started");
    }

    [RequiresMySqlFact]
    public async Task The_campaign_message_goes_through_the_phase_5_pipeline_and_is_always_marketing()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var c = await w.ContactAsync();
        // the contact wrote to us a minute ago: inside the 24 h service window a normal reply would be a Service message and need no consent
        await w.InboundAsync(c, "hello");
        var camp = await w.CreateAsync([c]); await w.StartAsync(camp.Id); await w.TickAsync(camp.Id);
        var msg = await app.Db(db => db.Messages.IgnoreQueryFilters().SingleAsync(m => m.CampaignId == camp.Id));
        Assert.Equal(MessagePurpose.Marketing, msg.Purpose);                                 // forced, consent is required
        Assert.Equal(CampaignKeys.ForRecipient(camp.Id, msg.CampaignRecipientId!.Value, 0), msg.IdempotencyKey);
        Assert.Equal(MessageStatus.Queued, msg.Status);
        await w.SendQueuedAsync(camp.Id);
        Assert.Single(w.SentTo(c));
    }

    // ---------------------------------------------------------------- compliance

    [RequiresMySqlFact]
    public async Task Suppressed_no_consent_revoked_invalid_archived_and_removed_contacts_never_receive_a_message()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var ok = await w.ContactAsync();
        var noConsent = await w.ContactAsync(consent: false);
        var suppressed = await w.ContactAsync();
        var revoked = await w.ContactAsync();
        var archived = await w.ContactAsync();
        Assert.True((await w.Admin.Post("/api/v1/suppression", new { channel = "WhatsApp", value = suppressed.Phone, reason = "Blocked", note = "test" })).IsSuccessStatusCode);
        Assert.True((await w.Admin.Post($"/api/v1/contacts/{revoked.ContactId}/consent", new { channel = "WhatsApp", purpose = "Marketing", status = "Revoked", source = "test" })).IsSuccessStatusCode);
        Assert.True((await w.Admin.Delete($"/api/v1/leads/{archived.LeadId}")).IsSuccessStatusCode);
        // an e-mail-only contact has no number to message
        var lead = await Crm.CreateLead(w.Admin, contacts: [new { name = "Mail Only", email = Crm.Token("m") + "@example.com" }]);
        var emailOnly = (await Crm.Ok<List<LeadPilot.Application.Crm.ContactDto>>(await w.Admin.Get($"/api/v1/leads/{lead.Id}/contacts"))).Single();
        var all = new[] { ok, noConsent, suppressed, revoked, archived };

        var preview = await Ok<AudiencePreviewDto>(await w.Admin.Post("/api/v1/campaigns/audience/preview", new { type = "Manual", audience = new { contactIds = all.Select(c => c.ContactId).Append(emailOnly.Id).ToArray() }, templateId = w.TemplateId }));
        Assert.Equal(6, preview.Candidates); Assert.Equal(1, preview.Eligible); Assert.Equal(5, preview.Excluded);
        var reasons = preview.Breakdown.ToDictionary(b => b.Reason, b => b.Count);
        Assert.Equal(1, reasons[ExclusionReasons.NoConsent] ); Assert.Equal(1, reasons[ExclusionReasons.Suppressed]); Assert.Equal(1, reasons[ExclusionReasons.ConsentRevoked]); Assert.Equal(1, reasons[ExclusionReasons.LeadArchived]);
        Assert.Equal(1, reasons[ExclusionReasons.NoContactValue]); Assert.Equal(preview.Excluded, reasons.Values.Sum());

        var camp = await w.CreateAsync(all, audienceOverride: new { contactIds = all.Select(c => c.ContactId).Append(emailOnly.Id).ToArray() });
        await w.StartAsync(camp.Id);
        var done = await w.RunToEndAsync(camp.Id);
        Assert.Equal(CampaignStatus.Completed, done.Status);
        Assert.Single(w.SentTo(ok));
        Assert.Equal(0, w.TotalSent([noConsent, suppressed, revoked, archived]));          // asserted on the fake WAHA's received requests
        Assert.DoesNotContain(app.Waha.SentMessages, m => m.ChatId.StartsWith("0"));
        var rec = await w.RecipientsAsync(camp.Id);
        Assert.Equal(6, rec.Count); Assert.Equal(5, rec.Count(r => r.Status == RecipientStatus.Skipped));
        Assert.Equal(ExclusionReasons.Suppressed, rec.Single(r => r.ContactId == suppressed.ContactId).StatusReason);
        Assert.Equal(ExclusionReasons.ConsentRevoked, rec.Single(r => r.ContactId == revoked.ContactId).StatusReason);
        Assert.Equal(ExclusionReasons.NoConsent, rec.Single(r => r.ContactId == noConsent.ContactId).StatusReason);
        var stats = await w.StatsAsync(camp.Id);
        Assert.Equal(5, stats.SkipReasons.Sum(s => s.Count));
    }

    [RequiresMySqlFact]
    public async Task A_high_lead_score_never_overrides_the_gate()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var c = await w.ContactAsync(consent: false);
        var camp = await w.CreateAsync([c], audienceOverride: new { contactIds = new[] { c.ContactId }, minScore = 0, maxScore = 100 });
        var p = await Ok<AudiencePreviewDto>(await w.Admin.Post("/api/v1/campaigns/audience/preview", new { type = "Manual", audience = new { contactIds = new[] { c.ContactId }, minScore = 0 } }));
        Assert.Equal(0, p.Eligible);
        await w.StartAsync(camp.Id); await w.RunToEndAsync(camp.Id);
        Assert.Empty(w.SentTo(c));
    }

    public static IEnumerable<object[]> SendTimeCases() => new[] { "consent_revoked", "suppression_added", "lead_archived", "phone_edited", "contact_removed" }.Select(x => new object[] { x });

    private async Task SendTimeAsync(string change, string expectedReason, bool whilePending)
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var contacts = await w.ContactsAsync(3); var victim = contacts[1];
        var camp = await w.CreateAsync(contacts, batchSize: whilePending ? 1 : 10);
        await w.StartAsync(camp.Id); await w.TickAsync(camp.Id);                           // snapshot taken, first batch handed to the outbound queue
        // after audience selection (and after queueing, unless the recipient is still waiting) the person changes
        switch (change)
        {
            case "consent_revoked": Assert.True((await w.Admin.Post($"/api/v1/contacts/{victim.ContactId}/consent", new { channel = "WhatsApp", purpose = "Marketing", status = "Revoked", source = "test" })).IsSuccessStatusCode); break;
            case "suppression_added": Assert.True((await w.Admin.Post("/api/v1/suppression", new { channel = "WhatsApp", value = victim.Phone, reason = "OptOut" })).IsSuccessStatusCode); break;
            case "lead_archived": Assert.True((await w.Admin.Delete($"/api/v1/leads/{victim.LeadId}")).IsSuccessStatusCode); break;
            case "phone_edited": Assert.True((await w.Admin.Put($"/api/v1/contacts/{victim.ContactId}", new { phone = "+9230" + Random.Shared.Next(10_000_000, 99_999_999) })).IsSuccessStatusCode); break;
            case "contact_removed": Assert.True((await w.Admin.Delete($"/api/v1/contacts/{victim.ContactId}")).IsSuccessStatusCode); break;
        }
        var done = await w.RunToEndAsync(camp.Id);
        Assert.Equal(CampaignStatus.Completed, done.Status);
        Assert.Empty(w.SentTo(victim));                                                    // never reached the gateway
        Assert.Single(w.SentTo(contacts[0])); Assert.Single(w.SentTo(contacts[2]));
        var r = await w.RecipientOfAsync(camp.Id, victim);
        Assert.Equal(RecipientStatus.Skipped, r.Status);
        Assert.True(r.StatusReason == expectedReason || (change == "phone_edited" && r.StatusReason is ExclusionReasons.ContactChanged or ExclusionReasons.NoConsent or ExclusionReasons.ConsentRevoked), $"reason was {r.StatusReason}");
        var trail = EligibilityTrail.Parse(r.EligibilityJson);
        Assert.True(trail.Selection!.Eligible);                                            // eligible when the audience was selected …
        Assert.NotNull(trail.Send); Assert.False(trail.Send!.Eligible);                    // … and the send-time decision is recorded next to it
    }

    [RequiresMySqlFact] public Task Consent_revoked_after_selection_prevents_the_queued_send() => SendTimeAsync("consent_revoked", ExclusionReasons.ConsentRevoked, false);
    [RequiresMySqlFact] public Task Suppression_added_after_selection_prevents_the_queued_send() => SendTimeAsync("suppression_added", ExclusionReasons.Suppressed, false);
    [RequiresMySqlFact] public Task Lead_archived_after_selection_prevents_the_queued_send() => SendTimeAsync("lead_archived", ExclusionReasons.LeadArchived, false);
    [RequiresMySqlFact] public Task Contact_edited_after_selection_prevents_the_queued_send() => SendTimeAsync("phone_edited", ExclusionReasons.ContactChanged, false);
    [RequiresMySqlFact] public Task Contact_removed_after_selection_prevents_the_queued_send() => SendTimeAsync("contact_removed", ExclusionReasons.ContactRemoved, false);
    [RequiresMySqlFact] public Task Consent_revoked_while_the_recipient_is_still_waiting_is_caught_by_the_runner() => SendTimeAsync("consent_revoked", ExclusionReasons.ConsentRevoked, true);
    [RequiresMySqlFact] public Task Suppression_added_while_the_recipient_is_still_waiting_is_caught_by_the_runner() => SendTimeAsync("suppression_added", ExclusionReasons.Suppressed, true);

    [RequiresMySqlFact]
    public async Task An_opt_out_arriving_mid_run_through_the_inbound_flow_stops_that_contact_only()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var contacts = await w.ContactsAsync(3);
        var camp = await w.CreateAsync(contacts);
        await w.StartAsync(camp.Id); await w.TickAsync(camp.Id);                           // all three queued, none sent yet
        await w.InboundAsync(contacts[0], "STOP");                                         // the Phase 5 opt-out flow: suppression + revoked consent + blocked chat
        var done = await w.RunToEndAsync(camp.Id);
        Assert.Equal(CampaignStatus.Completed, done.Status);
        Assert.Empty(w.SentTo(contacts[0])); Assert.Single(w.SentTo(contacts[1])); Assert.Single(w.SentTo(contacts[2]));
        var r = await w.RecipientOfAsync(camp.Id, contacts[0]);
        Assert.Equal(RecipientStatus.Skipped, r.Status);
        Assert.Contains(r.StatusReason, new[] { ExclusionReasons.Suppressed, ExclusionReasons.ConsentRevoked, ExclusionReasons.OptedOutConversation });
        var stats = await w.StatsAsync(camp.Id);
        Assert.Equal(0, stats.Counts.Replied);                                             // an opt-out is not a reply conversion
    }

    // ---------------------------------------------------------------- duplicates, retries, crashes

    [RequiresMySqlFact]
    public async Task Repeated_ticks_and_repeated_send_jobs_create_no_duplicate_messages()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var contacts = await w.ContactsAsync(4);
        var camp = await w.CreateAsync(contacts); await w.StartAsync(camp.Id);
        await w.TickAsync(camp.Id); await w.TickAsync(camp.Id); await w.TickAsync(camp.Id);   // a retried / concurrent Hangfire job
        var ids = await w.QueuedMessagesAsync(camp.Id); Assert.Equal(4, ids.Count);
        foreach (var id in ids) { await w.Worker.RunSendAsync(w.TenantId, id); await w.Worker.RunSendAsync(w.TenantId, id); app.Clock.Offset += TimeSpan.FromSeconds(31); }   // a retried send job
        await w.RunToEndAsync(camp.Id);
        Assert.All(contacts, c => Assert.Single(w.SentTo(c)));
        Assert.Equal(4, await app.Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.CampaignId == camp.Id)));
        Assert.Equal(4, await app.Db(db => db.CampaignRecipients.IgnoreQueryFilters().CountAsync(r => r.CampaignId == camp.Id && r.Status == RecipientStatus.Sent)));
    }

    [RequiresMySqlFact]
    public async Task A_worker_that_crashed_after_queueing_but_before_linking_creates_no_second_message()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var c = await w.ContactAsync();
        var camp = await w.CreateAsync([c]); await w.StartAsync(camp.Id); await w.TickAsync(camp.Id);
        var firstMessage = (await w.QueuedMessagesAsync(camp.Id)).Single();
        // simulate the crash window: the Phase 5 message exists, the recipient update was lost
        await app.Db(db => db.CampaignRecipients.IgnoreQueryFilters().Where(r => r.CampaignId == camp.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RecipientStatus.Pending).SetProperty(r => r.MessageId, (Guid?)null).SetProperty(r => r.QueuedAtUtc, (DateTime?)null)));
        await app.Db(db => db.Campaigns.IgnoreQueryFilters().Where(x => x.Id == camp.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntilUtc, (DateTime?)null)));
        await using var restarted = MessagingKit.BuildWorker(app);                          // a brand-new worker process
        await restarted.Scopes.RunAsync(w.TenantId, (sp, ct) => sp.GetRequiredService<ICampaignRunner>().TickAsync(camp.Id, ct));
        var messages = await app.Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.CampaignId == camp.Id).ToListAsync());
        Assert.Single(messages); Assert.Equal(firstMessage, messages[0].Id);                // the same key returned the existing message
        Assert.Equal(firstMessage, (await w.RecipientOfAsync(camp.Id, c)).MessageId);
        await w.RunToEndAsync(camp.Id);
        Assert.Single(w.SentTo(c));
    }

    [RequiresMySqlFact]
    public async Task A_gateway_that_may_have_sent_the_message_never_leads_to_a_second_one_and_the_recipient_is_reconciled()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var c = await w.ContactAsync();
        var camp = await w.CreateAsync([c]); await w.StartAsync(camp.Id); await w.TickAsync(camp.Id);
        app.Waha.FaultNext(SendFault.Http500ButSent);                                       // sent, but the answer was an error
        var id = (await w.QueuedMessagesAsync(camp.Id)).Single();
        await w.Worker.RunSendAsync(w.TenantId, id);
        Assert.Equal(MessageStatus.Unknown, (await app.Db(db => db.Messages.IgnoreQueryFilters().SingleAsync(m => m.Id == id))).Status);
        Assert.Single(w.SentTo(c));
        app.Clock.Offset += TimeSpan.FromMinutes(4);
        await w.Worker.RunReconcileAsync(w.TenantId, id);                                   // asks the gateway: found → Sent, never re-sent
        await w.TickAsync(camp.Id);                                                         // the campaign tick repairs the recipient from the message
        Assert.Single(w.SentTo(c));
        Assert.Equal(RecipientStatus.Sent, (await w.RecipientOfAsync(camp.Id, c)).Status);
        Assert.Equal(1, await app.Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.CampaignId == camp.Id)));
    }

    [RequiresMySqlFact]
    public async Task A_crashed_tick_that_still_holds_its_lease_is_recovered_by_the_sweep()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var contacts = await w.ContactsAsync(3);
        var camp = await w.CreateAsync(contacts); await w.StartAsync(camp.Id);
        await app.Db(db => db.Campaigns.IgnoreQueryFilters().Where(x => x.Id == camp.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseUntilUtc, DateTime.UtcNow.AddMinutes(5) + app.Clock.Offset).SetProperty(x => x.NextRunAtUtc, (DateTime?)null)));
        await w.TickAsync(camp.Id);                                                         // the lease is held: this tick must do nothing
        Assert.Empty(await w.QueuedMessagesAsync(camp.Id));
        app.Clock.Offset += TimeSpan.FromMinutes(10);                                       // the dead worker's lease expires
        app.Campaigns.Jobs.Clear();
        await w.SweepAsync();
        Assert.Contains(app.Campaigns.For(camp.Id), j => j.Kind == "tick");                 // the sweep pushed a tick
        var tenants = await w.Worker.Services.CreateScope().ServiceProvider.GetRequiredService<LeadPilot.Infrastructure.Campaigns.CampaignDirectory>().TenantsNeedingAttentionAsync(DateTime.UtcNow + app.Clock.Offset, 3, default);
        Assert.Contains(w.TenantId, tenants);
        var done = await w.RunToEndAsync(camp.Id);
        Assert.Equal(CampaignStatus.Completed, done.Status); Assert.All(contacts, c => Assert.Single(w.SentTo(c)));
    }

    [RequiresMySqlFact]
    public async Task An_interrupted_audience_snapshot_resumes_without_duplicate_recipients()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var contacts = await w.ContactsAsync(5);
        var camp = await w.CreateAsync(contacts); await w.StartAsync(camp.Id); await w.TickAsync(camp.Id);
        // pretend the build died before it was marked complete, with only part of the rows written
        await app.Db(async db =>
        {
            await db.CampaignAudienceSnapshots.IgnoreQueryFilters().Where(s => s.CampaignId == camp.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SnapshotStatus.Building));
            var drop = await db.CampaignRecipients.IgnoreQueryFilters().Where(r => r.CampaignId == camp.Id && r.Status == RecipientStatus.Queued).OrderBy(r => r.Sequence).Take(2).Select(r => r.Id).ToListAsync();
            Assert.Equal(2, drop.Count);
        });
        await w.TickAsync(camp.Id);                                                         // resumes: Building → completes
        var rows = await app.Db(db => db.CampaignRecipients.IgnoreQueryFilters().Where(r => r.CampaignId == camp.Id).Select(r => r.ContactId).ToListAsync());
        Assert.Equal(5, rows.Count); Assert.Equal(5, rows.Distinct().Count());               // unique (campaign, contact)
        var snap = await app.Db(db => db.CampaignAudienceSnapshots.IgnoreQueryFilters().SingleAsync(s => s.CampaignId == camp.Id));
        Assert.Equal(SnapshotStatus.Ready, snap.Status); Assert.Equal(5, snap.Recipients);
        await w.RunToEndAsync(camp.Id);
        Assert.All(contacts, c => Assert.Single(w.SentTo(c)));
    }

    [RequiresMySqlFact]
    public async Task The_database_refuses_to_queue_a_contact_twice_in_one_campaign()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var c = await w.ContactAsync();
        var camp = await w.CreateAsync([c]); await w.StartAsync(camp.Id); await w.TickAsync(camp.Id);
        var r = await app.Db(db => db.CampaignRecipients.IgnoreQueryFilters().SingleAsync(x => x.CampaignId == camp.Id));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => app.Db(async db =>
        {
            db.CampaignRecipients.Add(LeadPilot.Domain.Campaigns.CampaignRecipient.Create(w.TenantId, camp.Id, r.SnapshotId, r.ContactId, r.LeadId, 99, r.Destination, "{}", DateTime.UtcNow));
            await db.SaveChangesAsync();
        }));
        Assert.Contains("Duplicate", ex.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [RequiresMySqlFact]
    public async Task Two_contacts_with_the_same_number_or_of_the_same_lead_get_one_message()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var phone = "+9230" + Random.Shared.Next(10_000_000, 99_999_999);
        var lead = await Crm.CreateLead(w.Admin, contacts: [new { name = "Primary One", phone, isPrimary = true }, new { name = "Second Contact", phone = "+9230" + Random.Shared.Next(10_000_000, 99_999_999) }]);
        var cs = await Crm.Ok<List<LeadPilot.Application.Crm.ContactDto>>(await w.Admin.Get($"/api/v1/leads/{lead.Id}/contacts"));
        foreach (var c in cs) await MessagingKit.GrantConsentAsync(w.Admin, c.Id);
        var otherRes = await w.Admin.Post("/api/v1/leads", new { name = Crm.Name(Crm.Token()), city = "Lahore", allowDuplicate = true, contacts = new[] { new { name = "Dup", phone } } });   // a different lead with the SAME number
        var other = await Crm.Ok<LeadPilot.Application.Crm.LeadDto>(otherRes);
        var oc = (await Crm.Ok<List<LeadPilot.Application.Crm.ContactDto>>(await w.Admin.Get($"/api/v1/leads/{other.Id}/contacts"))).Single(); await MessagingKit.GrantConsentAsync(w.Admin, oc.Id);
        var camp = await w.CreateAsync([], audienceOverride: new { contactIds = cs.Select(c => c.Id).Append(oc.Id).ToArray(), onePerLead = true });
        await w.StartAsync(camp.Id); await w.RunToEndAsync(camp.Id);
        Assert.Single(app.Waha.SentTo(phone.TrimStart('+') + "@c.us"), m => m.Session == w.Account.Session);
        var rec = await w.RecipientsAsync(camp.Id);
        Assert.Contains(rec, r => r.StatusReason == ExclusionReasons.SameLead); Assert.Contains(rec, r => r.StatusReason == ExclusionReasons.DuplicateNumber);
    }

    // ---------------------------------------------------------------- pause / resume / cancel

    [RequiresMySqlFact]
    public async Task Pause_withdraws_queued_messages_resume_sends_each_exactly_once()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var contacts = await w.ContactsAsync(3);
        var camp = await w.CreateAsync(contacts); await w.StartAsync(camp.Id); await w.TickAsync(camp.Id);
        var oldIds = await w.QueuedMessagesAsync(camp.Id); Assert.Equal(3, oldIds.Count);
        var paused = await Ok<CampaignDto>(await w.Admin.Post($"/api/v1/campaigns/{camp.Id}/pause"));
        Assert.Equal(CampaignStatus.Paused, paused.Status); Assert.False(paused.PausedAutomatically);
        Assert.Empty(await w.QueuedMessagesAsync(camp.Id));                                 // queued sends withdrawn
        foreach (var id in oldIds) await w.Worker.RunSendAsync(w.TenantId, id);             // the stale Hangfire jobs run: nothing happens
        Assert.Equal(0, w.TotalSent(contacts));
        Assert.All(await w.RecipientsAsync(camp.Id), r => Assert.Equal(RecipientStatus.Pending, r.Status));
        await w.TickAsync(camp.Id); Assert.Empty(await w.QueuedMessagesAsync(camp.Id));      // a paused campaign does not tick

        Assert.Equal(CampaignStatus.Running, (await Ok<CampaignDto>(await w.Admin.Post($"/api/v1/campaigns/{camp.Id}/resume"))).Status);
        Assert.Contains(app.Campaigns.For(camp.Id), j => j.Kind == "tick");
        var done = await w.RunToEndAsync(camp.Id);
        Assert.Equal(CampaignStatus.Completed, done.Status);
        Assert.All(contacts, c => Assert.Single(w.SentTo(c)));                              // exactly once each: the new round has its own key
        var keys = await app.Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.CampaignId == camp.Id).Select(m => m.IdempotencyKey).ToListAsync());
        Assert.Equal(6, keys.Count); Assert.Equal(6, keys.Distinct().Count()); Assert.Equal(3, keys.Count(k => k!.EndsWith(":1")));
    }

    [RequiresMySqlFact]
    public async Task A_message_already_queued_when_the_campaign_is_paused_behind_the_scenes_is_blocked_at_send_time()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var c = await w.ContactAsync();
        var camp = await w.CreateAsync([c]); await w.StartAsync(camp.Id); await w.TickAsync(camp.Id);
        await app.Db(db => db.Campaigns.IgnoreQueryFilters().Where(x => x.Id == camp.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, CampaignStatus.Paused).SetProperty(x => x.PauseReason, "x")));
        var id = (await w.QueuedMessagesAsync(camp.Id)).Single();
        await w.Worker.RunSendAsync(w.TenantId, id);
        Assert.Empty(w.SentTo(c));
        var msg = await app.Db(db => db.Messages.IgnoreQueryFilters().SingleAsync(m => m.Id == id));
        Assert.Equal(MessageStatus.Failed, msg.Status); Assert.Equal("campaign_paused", msg.FailureCode);
    }

    [RequiresMySqlFact]
    public async Task Cancel_stops_everything_withdraws_the_queue_and_cancels_waiting_recipients()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var contacts = await w.ContactsAsync(5);
        var camp = await w.CreateAsync(contacts, batchSize: 2); await w.StartAsync(camp.Id); await w.TickAsync(camp.Id);   // 2 queued, 3 waiting
        var cancelled = await Ok<CampaignDto>(await w.Admin.Post($"/api/v1/campaigns/{camp.Id}/cancel"));
        Assert.Equal(CampaignStatus.Cancelled, cancelled.Status);
        Assert.Empty(await w.QueuedMessagesAsync(camp.Id));
        await w.TickAsync(camp.Id); await w.SendQueuedAsync(camp.Id);
        Assert.Equal(0, w.TotalSent(contacts));
        Assert.All(await w.RecipientsAsync(camp.Id), r => Assert.Equal(RecipientStatus.Cancelled, r.Status));
        Assert.Equal(409, (int)(await w.Admin.Post($"/api/v1/campaigns/{camp.Id}/resume")).StatusCode);   // terminal
        Assert.Equal(409, (int)(await w.Admin.Post($"/api/v1/campaigns/{camp.Id}/pause")).StatusCode);
    }
}
