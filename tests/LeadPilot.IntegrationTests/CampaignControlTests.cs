using System.Net;
using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static LeadPilot.IntegrationTests.FakeWahaServer;

namespace LeadPilot.IntegrationTests;

/// <summary>Limits, caps, warm-up, auto-pause, kill switch, usage limits, failure threshold, follow-ups, cool-down, content rules, test sends and statistics reconciliation (fake WAHA only).</summary>
[Collection("app")]
public class CampaignControlTests(AppFixture app)
{
    private TestWorld W => app.World;
    private static Task<T> Ok<T>(HttpResponseMessage r) => Crm.Ok<T>(r);
    private static Task<HttpResponseMessage> Act(CampaignWorld w, Guid id, string action, object? body = null) => w.Admin.Post($"/api/v1/campaigns/{id}/{action}", body);

    // ---------------------------------------------------------------- rate limits, caps, warm-up

    [RequiresMySqlFact]
    public async Task Hourly_and_daily_caps_are_respected_and_never_over_queued()
    {
        await using var w = await CampaignWorld.OpenAsync(app, freshTenant: true, hourly: 3, daily: 5, delay: 10);
        var contacts = await w.ContactsAsync(10);
        var camp = await w.CreateAsync(contacts, batchSize: 10); await w.StartAsync(camp.Id);
        await w.TickAsync(camp.Id);
        Assert.Equal(3, (await w.QueuedMessagesAsync(camp.Id)).Count);                       // the batch is cut to the hourly cap
        await w.SendQueuedAsync(camp.Id);
        Assert.Equal(3, w.TotalSent(contacts));
        await w.RunToEndAsync(camp.Id, 6);
        Assert.Equal(3, w.TotalSent(contacts));                                              // hour cap holds however often the tick runs
        Assert.Equal(CampaignStatus.Running, (await w.GetAsync(camp.Id)).Status);
        app.Clock.Offset += TimeSpan.FromMinutes(61); await w.RunToEndAsync(camp.Id, 6);
        Assert.Equal(5, w.TotalSent(contacts));                                              // daily cap 5 reached
        app.Clock.Offset += TimeSpan.FromHours(2); await w.RunToEndAsync(camp.Id, 6);
        Assert.Equal(5, w.TotalSent(contacts));                                              // still inside 24 h
        var log = await Ok<PagedResult<ExecutionLogDto>>(await w.Admin.Get($"/api/v1/campaigns/{camp.Id}/log"));
        Assert.Contains(log.Items, l => l.Code == "waiting_for_capacity");
        app.Clock.Offset += TimeSpan.FromHours(25); await w.TickAsync(camp.Id); await w.SendQueuedAsync(camp.Id);
        Assert.Equal(8, w.TotalSent(contacts));                                              // the window slid: 3 more (hour cap)
    }

    [RequiresMySqlFact]
    public async Task Warm_up_limits_a_new_number_to_twenty_messages_on_its_first_day()
    {
        await using var w = await CampaignWorld.OpenAsync(app, freshTenant: true, prepareAccount: false);   // WORKING since a moment ago: warm-up day one
        var contacts = await w.ContactsAsync(25);
        var camp = await w.CreateAsync(contacts, batchSize: 10); await w.StartAsync(camp.Id);
        await w.RunToEndAsync(camp.Id, 8);
        Assert.Equal(SendRatePolicy.WarmupFirstDay, w.TotalSent(contacts));
        Assert.Equal(CampaignStatus.Running, (await w.GetAsync(camp.Id)).Status);
    }

    [RequiresMySqlFact]
    public async Task The_campaign_daily_limit_applies_on_top_of_the_account()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var contacts = await w.ContactsAsync(6);
        var camp = await w.CreateAsync(contacts, daily: 2); await w.StartAsync(camp.Id);
        await w.RunToEndAsync(camp.Id, 6);
        Assert.Equal(2, w.TotalSent(contacts));
    }

    // ---------------------------------------------------------------- auto-pause

    [RequiresMySqlFact]
    public async Task When_the_account_auto_pauses_the_campaign_pauses_with_it()
    {
        await using var w = await CampaignWorld.OpenAsync(app, freshTenant: true);
        var contacts = await w.ContactsAsync(3);
        var camp = await w.CreateAsync(contacts); await w.StartAsync(camp.Id); await w.TickAsync(camp.Id);
        for (var i = 0; i < 3; i++) await w.RunAsync(sp => sp.GetRequiredService<IAccountHealthService>().RecordSendFailureAsync(w.Account.Id, "gateway unreachable"));   // 3 hard failures: Phase 5 auto-pause
        var c = await w.GetAsync(camp.Id);
        Assert.Equal(CampaignStatus.Paused, c.Status); Assert.True(c.PausedAutomatically); Assert.Contains("account", c.PauseReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await w.QueuedMessagesAsync(camp.Id));
        Assert.Equal(0, w.TotalSent(contacts));
    }

    [RequiresMySqlFact]
    public async Task A_disconnected_session_pauses_the_running_campaign()
    {
        await using var w = await CampaignWorld.OpenAsync(app, freshTenant: true);
        var contacts = await w.ContactsAsync(2);
        var camp = await w.CreateAsync(contacts); await w.StartAsync(camp.Id); await w.TickAsync(camp.Id);
        var res = await MessagingKit.PostWebhookAsync(app, w.Account, WahaEvents.SessionStatus(w.Account.Session, "FAILED"), sentAt: DateTime.UtcNow + app.Clock.Offset);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await w.Worker.PumpAsync();
        var c = await w.GetAsync(camp.Id);
        Assert.Equal(CampaignStatus.Paused, c.Status); Assert.True(c.PausedAutomatically);
        await w.SendQueuedAsync(camp.Id);
        Assert.Equal(0, w.TotalSent(contacts));
        // a person pausing the account does the same; resuming the campaign while the account is down pauses it again at the next tick
        Assert.Equal(CampaignStatus.Running, (await Ok<CampaignDto>(await Act(w, camp.Id, "resume"))).Status);
        await w.TickAsync(camp.Id);
        Assert.Equal(CampaignStatus.Paused, (await w.GetAsync(camp.Id)).Status);
    }

    [RequiresMySqlFact]
    public async Task Pausing_the_account_by_hand_pauses_running_campaigns_and_a_paused_account_stops_the_tick()
    {
        await using var w = await CampaignWorld.OpenAsync(app, freshTenant: true);
        var contacts = await w.ContactsAsync(2);
        var camp = await w.CreateAsync(contacts); await w.StartAsync(camp.Id);
        Assert.True((await w.Admin.Post($"/api/v1/channels/accounts/{w.Account.Id}/pause")).IsSuccessStatusCode);
        Assert.Equal(CampaignStatus.Paused, (await w.GetAsync(camp.Id)).Status);
        await app.Db(db => db.Campaigns.IgnoreQueryFilters().Where(x => x.Id == camp.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, CampaignStatus.Running)));   // as if resumed behind the scenes
        await w.TickAsync(camp.Id);                                                           // the tick itself notices
        Assert.Equal(CampaignStatus.Paused, (await w.GetAsync(camp.Id)).Status);
        Assert.Equal(0, w.TotalSent(contacts));
    }

    [RequiresMySqlFact]
    public async Task A_failure_rate_above_the_threshold_pauses_the_campaign()
    {
        await using var w = await CampaignWorld.OpenAsync(app, freshTenant: true);
        await w.SetSettingsAsync(b => b["failureMinSample"] = 2);
        var contacts = await w.ContactsAsync(6);
        var camp = await w.CreateAsync(contacts, failurePercent: 50); await w.StartAsync(camp.Id); await w.TickAsync(camp.Id);
        app.Waha.FaultNext(Enumerable.Repeat(SendFault.Http400, 6).ToArray());                // the gateway rejects every send
        await w.SendQueuedAsync(camp.Id);
        var c = await w.GetAsync(camp.Id);
        Assert.Equal(CampaignStatus.Paused, c.Status); Assert.True(c.PausedAutomatically); Assert.Contains("failed", c.PauseReason);
        var rec = await w.RecipientsAsync(camp.Id);
        Assert.Equal(2, rec.Count(r => r.Status == RecipientStatus.Failed));                  // paused at the second failure: the rest wait
        Assert.Equal(4, rec.Count(r => r.Status == RecipientStatus.Pending));
        Assert.DoesNotContain(app.Waha.SentMessages, m => m.Session == w.Account.Session);
        var log = await Ok<PagedResult<ExecutionLogDto>>(await w.Admin.Get($"/api/v1/campaigns/{camp.Id}/log"));
        Assert.Contains(log.Items, l => l.Code == "failure_threshold"); Assert.Contains(log.Items, l => l.Code == "recipient_failed");
    }

    // ---------------------------------------------------------------- kill switch

    [RequiresMySqlFact]
    public async Task The_kill_switch_pauses_every_running_campaign_and_blocks_all_campaign_sends()
    {
        await using var w = await CampaignWorld.OpenAsync(app, freshTenant: true);
        var a = await w.ContactsAsync(2); var b = await w.ContactsAsync(2);
        var c1 = await w.CreateAsync(a); var c2 = await w.CreateAsync(b);
        await w.StartAsync(c1.Id); await w.StartAsync(c2.Id); await w.TickAsync(c1.Id); await w.TickAsync(c2.Id);
        var queued = (await w.QueuedMessagesAsync(c1.Id)).Concat(await w.QueuedMessagesAsync(c2.Id)).ToList(); Assert.Equal(4, queued.Count);
        var s = await Ok<CampaignSettingsDto>(await w.Admin.Put("/api/v1/campaigns/settings/kill-switch", new { enabled = true, reason = "incident" }));
        Assert.True(s.KillSwitch);
        foreach (var id in new[] { c1.Id, c2.Id }) { var c = await w.GetAsync(id); Assert.Equal(CampaignStatus.Paused, c.Status); Assert.True(c.PausedAutomatically); }
        foreach (var id in queued) await w.Worker.RunSendAsync(w.TenantId, id);               // stale send jobs
        Assert.Equal(0, w.TotalSent(a.Concat(b)));
        Assert.Equal(409, (int)(await Act(w, c1.Id, "resume")).StatusCode);
        var third = await w.CreateAsync(await w.ContactsAsync(1)); await w.SubmitAsync(third.Id);
        Assert.Equal(409, (int)(await Act(w, third.Id, "start")).StatusCode);
        await w.TickAsync(c1.Id); Assert.Empty(await w.QueuedMessagesAsync(c1.Id));
        // a campaign that is Running behind the scenes is still stopped by the tick and by the send guard
        await app.Db(db => db.Campaigns.IgnoreQueryFilters().Where(x => x.Id == c2.Id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, CampaignStatus.Running)));
        await w.TickAsync(c2.Id);
        Assert.Equal(CampaignStatus.Paused, (await w.GetAsync(c2.Id)).Status);

        await Ok<CampaignSettingsDto>(await w.Admin.Put("/api/v1/campaigns/settings/kill-switch", new { enabled = false }));
        Assert.Equal(CampaignStatus.Paused, (await w.GetAsync(c1.Id)).Status);                // turning it off resumes nothing by itself
        Assert.Equal(CampaignStatus.Running, (await Ok<CampaignDto>(await Act(w, c1.Id, "resume"))).Status);
        Assert.Equal(CampaignStatus.Completed, (await w.RunToEndAsync(c1.Id)).Status);
        Assert.All(a, c => Assert.Single(w.SentTo(c)));
    }

    [RequiresMySqlFact]
    public async Task The_kill_switch_of_one_workspace_does_not_touch_another()
    {
        await using var a = await CampaignWorld.OpenAsync(app, freshTenant: true); await using var b = await CampaignWorld.OpenAsync(app, freshTenant: true);
        var ca = await a.CreateAsync(await a.ContactsAsync(1)); var cb = await b.CreateAsync(await b.ContactsAsync(1));
        await a.StartAsync(ca.Id); await b.StartAsync(cb.Id);
        await Ok<CampaignSettingsDto>(await a.Admin.Put("/api/v1/campaigns/settings/kill-switch", new { enabled = true }));
        Assert.Equal(CampaignStatus.Paused, (await a.GetAsync(ca.Id)).Status); Assert.Equal(CampaignStatus.Running, (await b.GetAsync(cb.Id)).Status);
        Assert.False((await Ok<CampaignSettingsDto>(await b.Admin.Get("/api/v1/campaigns/settings"))).KillSwitch);
    }

    // ---------------------------------------------------------------- usage limits

    [RequiresMySqlFact]
    public async Task The_monthly_usage_limit_stops_the_campaign_and_raising_it_lets_it_finish()
    {
        await using var w = await CampaignWorld.OpenAsync(app, freshTenant: true);
        await w.SetSettingsAsync(b => b["monthlyMessageLimit"] = 2);
        var contacts = await w.ContactsAsync(5);
        var camp = await w.CreateAsync(contacts); await w.StartAsync(camp.Id);
        await w.RunToEndAsync(camp.Id, 6);
        var c = await w.GetAsync(camp.Id);
        Assert.Equal(CampaignStatus.Paused, c.Status); Assert.Contains("monthly", c.PauseReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, w.TotalSent(contacts));
        var s = await Ok<CampaignSettingsDto>(await w.Admin.Get("/api/v1/campaigns/settings"));
        Assert.Equal(2, s.UsedThisMonth); Assert.Equal(2, s.EffectiveMonthlyLimit);
        Assert.Equal(2, await app.Db(db => db.CampaignUsageEntries.IgnoreQueryFilters().CountAsync(u => u.CampaignId == camp.Id)));   // the ledger: one row per message that left
        await w.SetSettingsAsync(b => b["monthlyMessageLimit"] = 50);
        await Ok<CampaignDto>(await Act(w, camp.Id, "resume"));
        Assert.Equal(CampaignStatus.Completed, (await w.RunToEndAsync(camp.Id)).Status);
        Assert.All(contacts, x => Assert.Single(w.SentTo(x)));
    }

    // ---------------------------------------------------------------- content

    [RequiresMySqlFact]
    public async Task A_missing_variable_skips_the_recipient_and_an_inline_fallback_covers_it()
    {
        await using var w = await CampaignWorld.OpenAsync(app, body: "Hi {{first_name}}, how is {{city}}?");
        var withCity = await w.ContactAsync(city: "Lahore"); var noCity = await w.ContactAsync(city: null);
        var camp = await w.CreateAsync([withCity, noCity]); await w.StartAsync(camp.Id); await w.RunToEndAsync(camp.Id);
        Assert.Single(w.SentTo(withCity)); Assert.Empty(w.SentTo(noCity));                      // never a literal "{{city}}" or an empty hole
        Assert.Equal(ExclusionReasons.MissingVariable, (await w.RecipientOfAsync(camp.Id, noCity)).StatusReason);

        await w.NewTemplateAsync("Hi {{first_name}}, how is {{city|your city}}?");
        var camp2 = await w.CreateAsync([noCity]); await w.StartAsync(camp2.Id); await w.RunToEndAsync(camp2.Id);
        Assert.Contains("how is your city?", Assert.Single(w.SentTo(noCity)).Text);
    }

    [RequiresMySqlFact]
    public async Task Hostile_contact_data_cannot_inject_placeholders_or_formatting_into_the_message()
    {
        await using var w = await CampaignWorld.OpenAsync(app, body: "Hi {{name}}, {{business_name}} is on our list.");
        var phone = "+9230" + Random.Shared.Next(10_000_000, 99_999_999);
        var lead = await Crm.CreateLead(w.Admin, name: "{{phone}} *Shop* _x_", contacts: [new { name = "{{business_name}}‮`evil`\r\nSecond line", phone }]);
        var contact = (await Crm.Ok<List<ContactDto>>(await w.Admin.Get($"/api/v1/leads/{lead.Id}/contacts"))).Single(); await MessagingKit.GrantConsentAsync(w.Admin, contact.Id);
        var tc = new TestContact(lead.Id, contact.Id, phone);
        var camp = await w.CreateAsync([tc]); await w.StartAsync(camp.Id); await w.RunToEndAsync(camp.Id);
        var text = Assert.Single(w.SentTo(tc)).Text;
        Assert.DoesNotContain("{{", text); Assert.DoesNotContain("*", text); Assert.DoesNotContain("`", text); Assert.DoesNotContain('‮', text); Assert.DoesNotContain('\r', text);
        Assert.StartsWith("Hi ", text); Assert.EndsWith("Reply STOP to opt out.", text);
    }

    [RequiresMySqlFact]
    public async Task Submit_rejects_service_templates_unknown_placeholders_and_a_missing_opt_out_path()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var c = await w.ContactAsync();
        var svc = await w.NewTemplateAsync("Hello {{first_name}}", "Service");
        var camp = await w.CreateAsync([c], templateId: svc);
        var res = await Act(w, camp.Id, "submit"); Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode); Assert.Contains("Marketing", await Crm.ProblemDetail(res));
        var ok = await w.NewTemplateAsync("Hello {{first_name}}");
        var noFooter = await Ok<CampaignDto>(await w.Admin.Put($"/api/v1/campaigns/{camp.Id}", new { name = camp.Name, type = "Manual", templateId = ok, channelAccountId = w.Account.Id, includeOptOutFooter = false, audience = new { contactIds = new[] { c.ContactId } } }));
        var res2 = await Act(w, noFooter.Id, "submit"); Assert.Equal(HttpStatusCode.BadRequest, res2.StatusCode); Assert.Contains("opt out", await Crm.ProblemDetail(res2));
        var withKeyword = await w.NewTemplateAsync("Hello {{first_name}}, reply STOP to unsubscribe");
        await Ok<CampaignDto>(await w.Admin.Put($"/api/v1/campaigns/{camp.Id}", new { name = camp.Name, type = "Manual", templateId = withKeyword, channelAccountId = w.Account.Id, includeOptOutFooter = false, audience = new { contactIds = new[] { c.ContactId } } }));
        Assert.Equal(CampaignStatus.Scheduled, (await Ok<CampaignDto>(await Act(w, camp.Id, "submit"))).Status);
    }

    [RequiresMySqlFact]
    public async Task The_approved_text_is_frozen_later_template_edits_do_not_change_a_scheduled_campaign()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var c = await w.ContactAsync();
        var camp = await w.CreateAsync([c]); await w.SubmitAsync(camp.Id);
        Assert.True((await w.Admin.Put($"/api/v1/message-templates/{w.TemplateId}", new { name = "Renamed " + Guid.NewGuid().ToString("N")[..6], body = "CHANGED {{first_name}}", category = "Marketing" })).IsSuccessStatusCode);
        await w.StartAsync(camp.Id); await w.RunToEndAsync(camp.Id);
        Assert.StartsWith("Hello", Assert.Single(w.SentTo(c)).Text);
        Assert.Equal(409, (int)(await w.Admin.Put($"/api/v1/campaigns/{camp.Id}", new { name = "x", type = "Manual", audience = new { contactIds = new[] { c.ContactId } } })).StatusCode);   // not editable any more
    }

    // ---------------------------------------------------------------- cool-down

    [RequiresMySqlFact]
    public async Task A_contact_messaged_by_another_campaign_inside_the_cool_down_is_skipped()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var c = await w.ContactAsync(); var d = await w.ContactAsync();
        var first = await w.CreateAsync([c]); await w.StartAsync(first.Id); await w.RunToEndAsync(first.Id);
        var second = await w.CreateAsync([c, d], cooldownDays: 7); await w.StartAsync(second.Id); await w.RunToEndAsync(second.Id);
        Assert.Single(w.SentTo(c)); Assert.Single(w.SentTo(d));
        Assert.Equal(ExclusionReasons.Cooldown, (await w.RecipientOfAsync(second.Id, c)).StatusReason);
        var third = await w.CreateAsync([c], cooldownDays: 0); await w.StartAsync(third.Id); await w.RunToEndAsync(third.Id);   // cool-down 0 disables it
        Assert.Equal(2, w.SentTo(c).Count());
    }

    // ---------------------------------------------------------------- follow-up and re-engagement

    private async Task<(CampaignWorld W, CampaignDto Parent, List<TestContact> C)> ParentRunAsync()
    {
        var w = await CampaignWorld.OpenAsync(app);
        var cs = await w.ContactsAsync(4);
        var parent = await w.CreateAsync(cs); await w.StartAsync(parent.Id); await w.RunToEndAsync(parent.Id);
        await w.AckAsync(cs[0], 2); await w.AckAsync(cs[1], 3); await w.AckAsync(cs[2], 2); await w.InboundAsync(cs[3], "interested");
        app.Clock.Offset += TimeSpan.FromSeconds(10);   // the events are now in the past, so a follow-up 'after 0 days' sees them
        return (w, await w.GetAsync(parent.Id), cs);
    }

    [RequiresMySqlFact]
    public async Task A_follow_up_targets_the_outcomes_of_its_parent_with_the_same_eligibility_check()
    {
        var (w, parent, cs) = await ParentRunAsync(); await using var _ = w;
        Assert.Equal(CampaignStatus.Completed, parent.Status);
        async Task<AudiencePreviewDto> Preview(object followUp, string type = "FollowUp") => await Ok<AudiencePreviewDto>(await w.Admin.Post("/api/v1/campaigns/audience/preview", new { type, audience = new { followUp } }));
        Assert.Equal(3, (await Preview(new { parentCampaignId = parent.Id, rule = "DeliveredNotReplied", afterDays = 0 })).Eligible);   // delivered + read, not the replier
        Assert.Equal(1, (await Preview(new { parentCampaignId = parent.Id, rule = "ReadNotReplied", afterDays = 0 })).Eligible);
        Assert.Equal(0, (await Preview(new { parentCampaignId = parent.Id, rule = "DeliveredNotReplied", afterDays = 3 })).Eligible);    // nothing is 3 days old yet
        var lead = await Ok<LeadDto>(await w.Admin.Get($"/api/v1/leads/{cs[3].LeadId}"));
        Assert.Equal(1, (await Preview(new { parentCampaignId = parent.Id, rule = "RepliedNotAdvanced", afterDays = 0, advancedStageIds = new[] { Guid.NewGuid() } })).Eligible);   // replied, not in an "advanced" stage
        Assert.Equal(0, (await Preview(new { parentCampaignId = parent.Id, rule = "RepliedNotAdvanced", afterDays = 0, advancedStageIds = new[] { lead.StageId } })).Eligible);

        // one of them opts out before the follow-up: the same gate excludes them
        Assert.True((await w.Admin.Post("/api/v1/suppression", new { channel = "WhatsApp", value = cs[2].Phone, reason = "OptOut" })).IsSuccessStatusCode);
        var fu = await Ok<CampaignDto>(await Act0(w, new { name = "Follow-up " + Guid.NewGuid().ToString("N")[..6], type = "FollowUp", templateId = w.TemplateId, channelAccountId = w.Account.Id, cooldownDays = 7,
            audience = new { followUp = new { parentCampaignId = parent.Id, rule = "DeliveredNotReplied", afterDays = 0 } } }));
        Assert.Equal(parent.Id, fu.ParentCampaignId);
        await w.StartAsync(fu.Id); var done = await w.RunToEndAsync(fu.Id);
        Assert.Equal(CampaignStatus.Completed, done.Status);
        Assert.Equal(2, w.SentTo(cs[0]).Count()); Assert.Equal(2, w.SentTo(cs[1]).Count());      // second message to each, despite the 7-day cool-down (the parent is exempt)
        Assert.Single(w.SentTo(cs[2])); Assert.Single(w.SentTo(cs[3]));                          // the suppressed one and the replier got nothing more
        var rec = await w.RecipientsAsync(fu.Id);
        Assert.DoesNotContain(rec, r => r.ContactId == cs[3].ContactId);                         // never in the follow-up audience
        Assert.Equal(ExclusionReasons.Suppressed, rec.Single(r => r.ContactId == cs[2].ContactId).StatusReason);
    }

    private static Task<HttpResponseMessage> Act0(CampaignWorld w, object body) => w.Admin.Post("/api/v1/campaigns", body);

    [RequiresMySqlFact]
    public async Task A_follow_up_that_waits_for_its_parent_is_scheduled_by_the_sweep_when_the_parent_completes()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var cs = await w.ContactsAsync(2);
        var parent = await w.CreateAsync(cs);
        var fu = await Ok<CampaignDto>(await Act0(w, new { name = "FU " + Guid.NewGuid().ToString("N")[..6], type = "FollowUp", templateId = w.TemplateId, channelAccountId = w.Account.Id, followUpDelayDays = 1, cooldownDays = 0,
            audience = new { followUp = new { parentCampaignId = parent.Id, rule = "SentNotReplied", afterDays = 0 } } }));
        await w.SubmitAsync(fu.Id);
        Assert.Null((await w.GetAsync(fu.Id)).ScheduledAtUtc);
        await w.SweepAsync(); Assert.Null((await w.GetAsync(fu.Id)).ScheduledAtUtc);              // the parent has not run: nothing is scheduled
        await w.StartAsync(parent.Id); await w.RunToEndAsync(parent.Id);
        await w.SweepAsync();
        var scheduled = await w.GetAsync(fu.Id);
        Assert.NotNull(scheduled.ScheduledAtUtc);
        Assert.Equal((await w.GetAsync(parent.Id)).CompletedAtUtc!.Value.AddDays(1), scheduled.ScheduledAtUtc!.Value, TimeSpan.FromSeconds(2));
        Assert.Contains(app.Campaigns.For(fu.Id), j => j.Kind == "start");                         // the Hangfire timer, the only scheduler
        app.Clock.Offset += TimeSpan.FromHours(25);
        await w.RunAsync(sp => sp.GetRequiredService<ICampaignRunner>().StartAsync(fu.Id, default));
        Assert.Equal(CampaignStatus.Completed, (await w.RunToEndAsync(fu.Id)).Status);
        Assert.All(cs, c => Assert.Equal(2, w.SentTo(c).Count()));
    }

    [RequiresMySqlFact]
    public async Task A_re_engagement_campaign_selects_by_inactivity()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var quiet = await w.ContactAsync(); var recent = await w.ContactAsync();
        var first = await w.CreateAsync([recent]); await w.StartAsync(first.Id); await w.RunToEndAsync(first.Id);       // contacted just now
        var ids = new[] { quiet.ContactId, recent.ContactId };
        var p = await Ok<AudiencePreviewDto>(await w.Admin.Post("/api/v1/campaigns/audience/preview", new { type = "ReEngagement", audience = new { contactIds = ids, lastContactedBeforeDays = 30 } }));
        Assert.Equal(1, p.Eligible);                                                              // only the one nobody messaged for 30 days
        var never = await Ok<AudiencePreviewDto>(await w.Admin.Post("/api/v1/campaigns/audience/preview", new { type = "ReEngagement", audience = new { contactIds = ids, neverContacted = true } }));
        Assert.Equal(1, never.Eligible);
        var noReply = await Ok<AudiencePreviewDto>(await w.Admin.Post("/api/v1/campaigns/audience/preview", new { type = "Manual", audience = new { contactIds = ids, hasReply = true } }));
        Assert.Equal(0, noReply.Eligible);
    }

    // ---------------------------------------------------------------- audience filters (real SQL)

    [RequiresMySqlFact]
    public async Task Audience_filters_translate_to_the_right_contacts()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var token = Crm.Token("flt"); var tag = await Ok<TagDto>(await w.Admin.Post("/api/v1/tags", new { name = token }));
        var city = "Zz" + token;
        var lead = await Crm.CreateLead(w.Admin, name: token + " One", city: city); var other = await Crm.CreateLead(w.Admin, name: token + " Two", city: "Elsewhere" + token);
        foreach (var l in new[] { lead, other })
        {
            var c = (await Crm.Ok<List<ContactDto>>(await w.Admin.Get($"/api/v1/leads/{l.Id}/contacts"))).Single(); await MessagingKit.GrantConsentAsync(w.Admin, c.Id);
        }
        Assert.True((await w.Admin.Put($"/api/v1/leads/{lead.Id}/tags", new { tagIds = new[] { tag.Id } })).IsSuccessStatusCode);
        async Task<int> Eligible(object audience) => (await Ok<AudiencePreviewDto>(await w.Admin.Post("/api/v1/campaigns/audience/preview", new { type = "Manual", audience }))).Eligible;
        Assert.Equal(1, await Eligible(new { tagIds = new[] { tag.Id } }));
        Assert.Equal(1, await Eligible(new { cities = new[] { city } }));
        Assert.Equal(1, await Eligible(new { cities = new[] { city, "Nowhere" } }));
        Assert.Equal(0, await Eligible(new { cities = new[] { city }, excludeTagIds = new[] { tag.Id } }));
        Assert.Equal(1, await Eligible(new { cities = new[] { "Elsewhere" + token }, excludeTagIds = new[] { tag.Id } }));
        Assert.Equal(1, await Eligible(new { stageIds = new[] { lead.StageId }, cities = new[] { city } }));
        Assert.Equal(0, await Eligible(new { stageIds = new[] { Guid.NewGuid() }, cities = new[] { city } }));
        Assert.Equal(1, await Eligible(new { sourceTypes = new[] { lead.PrimarySourceType.ToString() }, cities = new[] { city } }));
        Assert.Equal(0, await Eligible(new { sourceTypes = new[] { "Import" }, cities = new[] { city } }));
        Assert.Equal(1, await Eligible(new { minScore = 0, maxScore = 100, cities = new[] { city } }));
    }

    [RequiresMySqlFact]
    public async Task An_invalid_or_filterless_audience_is_a_validation_error_not_everyone()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var none = await w.Admin.Post("/api/v1/campaigns/audience/preview", new { type = "Manual", audience = new { } });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode); Assert.Contains("filter", await Crm.ProblemDetail(none));
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post("/api/v1/campaigns/audience/preview", new { type = "Manual", audience = new { minScore = 90, maxScore = 10 } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post("/api/v1/campaigns/audience/preview", new { type = "FollowUp", audience = new { minScore = 1 } })).StatusCode);
    }

    // ---------------------------------------------------------------- test send, dry run, settings

    [RequiresMySqlFact]
    public async Task A_test_send_goes_only_to_an_allow_listed_number_is_labelled_and_is_not_part_of_the_campaign()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var internalPhone = "+9230" + Random.Shared.Next(10_000_000, 99_999_999);
        await w.SetSettingsAsync(b => b["testNumbers"] = new[] { internalPhone });
        var customer = await w.ContactAsync();
        var camp = await w.CreateAsync([customer]);
        var refused = await w.Admin.Post($"/api/v1/campaigns/{camp.Id}/test-send", new { phoneE164 = customer.Phone });          // a real customer is never an allowed target
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode); Assert.Contains("internal numbers", await Crm.ProblemDetail(refused));
        Assert.Empty(w.SentTo(customer));
        var res = await w.Admin.Post($"/api/v1/campaigns/{camp.Id}/test-send", new { phoneE164 = internalPhone });
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var msg = await ApiSession.Json<MessageDto>(res);
        await w.Worker.RunSendAsync(w.TenantId, msg.Id);
        var sent = Assert.Single(app.Waha.SentTo(internalPhone.TrimStart('+') + "@c.us"), m => m.Session == w.Account.Session);
        Assert.StartsWith(TestSendRules.Label, sent.Text); Assert.EndsWith("Reply STOP to opt out.", sent.Text); Assert.Contains("Hello Test, we help schools in Test City.", sent.Text);
        Assert.Empty(w.SentTo(customer));
        Assert.Empty(await w.RecipientsAsync(camp.Id));                                                                           // not a recipient, no statistics
        Assert.Equal(0, (await w.StatsAsync(camp.Id)).Messaged);
        var dto = await app.Db(db => db.Messages.IgnoreQueryFilters().SingleAsync(m => m.Id == msg.Id));
        Assert.Equal(camp.Id, dto.CampaignId); Assert.Null(dto.CampaignRecipientId); Assert.Equal(MessagePurpose.Service, dto.Purpose);
        // the same click twice inside a minute does not create a second message
        var again = await ApiSession.Json<MessageDto>(await w.Admin.Post($"/api/v1/campaigns/{camp.Id}/test-send", new { phoneE164 = internalPhone }));
        Assert.Equal(msg.Id, again.Id);
    }

    [RequiresMySqlFact]
    public async Task A_dry_run_reports_everything_and_sends_nothing()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var ok = await w.ContactAsync(); var no = await w.ContactAsync(consent: false);
        var camp = await w.CreateAsync([ok, no]);
        var before = await app.Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.CampaignId == camp.Id));
        var dry = await Ok<DryRunDto>(await Act(w, camp.Id, "dry-run"));
        Assert.True(dry.TemplateValid); Assert.True(dry.AccountReady); Assert.Equal(1, dry.Audience.Eligible); Assert.Equal(1, dry.Audience.Excluded); Assert.NotEmpty(dry.Audience.Samples);
        Assert.StartsWith("Hello", dry.Audience.Samples[0].RenderedMessage);
        Assert.Equal(before, await app.Db(db => db.Messages.IgnoreQueryFilters().CountAsync(m => m.CampaignId == camp.Id)));
        Assert.Equal(0, w.TotalSent([ok, no])); Assert.Equal(CampaignStatus.Draft, (await w.GetAsync(camp.Id)).Status);
    }

    [RequiresMySqlFact]
    public async Task Campaign_settings_are_validated_and_audited()
    {
        await using var w = await CampaignWorld.OpenAsync(app, freshTenant: true);
        var s = await Ok<CampaignSettingsDto>(await w.Admin.Get("/api/v1/campaigns/settings"));
        Assert.Equal(CampaignTenantSettings.DefaultCooldownDays, s.CooldownDays); Assert.False(s.ApprovalRequired); Assert.True(s.CreatorCannotApprove); Assert.False(s.KillSwitch); Assert.Contains("STOP", s.OptOutFooter);
        async Task<HttpStatusCode> Put(Action<Dictionary<string, object?>> edit)
        {
            var b = new Dictionary<string, object?> { ["cooldownDays"] = 3, ["approvalRequired"] = false, ["creatorCannotApprove"] = true, ["attributionWindowDays"] = 5, ["failureThresholdPercent"] = 40, ["failureMinSample"] = 8, ["optOutFooter"] = "Reply STOP to unsubscribe", ["monthlyMessageLimit"] = null, ["testNumbers"] = new[] { "+923001112233" } };
            edit(b); return (await w.Admin.Put("/api/v1/campaigns/settings", b)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.OK, await Put(_ => { }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(b => b["optOutFooter"] = "Thanks for reading"));                          // no way to opt out the detector understands
        Assert.Equal(HttpStatusCode.BadRequest, await Put(b => b["testNumbers"] = new[] { "not a number" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(b => b["testNumbers"] = Enumerable.Range(0, 11).Select(i => $"+92300111{i:0000}").ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(b => b["cooldownDays"] = -1));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(b => b["attributionWindowDays"] = 0));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(b => b["failureThresholdPercent"] = 101));
        var now = await Ok<CampaignSettingsDto>(await w.Admin.Get("/api/v1/campaigns/settings"));
        Assert.Equal(3, now.CooldownDays); Assert.Equal(5, now.AttributionWindowDays); Assert.Equal(["+923001112233"], now.TestNumbers);
        var audit = await app.Db(db => db.AuditLogs.IgnoreQueryFilters().Where(a => a.TenantId == w.TenantId && a.Action == "Campaign.SettingsChanged").CountAsync());
        Assert.True(audit >= 1);
    }

    [RequiresMySqlFact]
    public async Task Without_an_approval_policy_submitting_schedules_directly_and_the_creator_may_approve_when_the_policy_allows()
    {
        await using var w = await CampaignWorld.OpenAsync(app, freshTenant: true);
        var c = await w.ContactAsync();
        var plain = await w.CreateAsync([c], scheduledAtUtc: DateTime.UtcNow.AddHours(3));
        Assert.Equal(CampaignStatus.Scheduled, (await w.SubmitAsync(plain.Id)).Status);
        Assert.Equal(CampaignStatus.Draft, (await Ok<CampaignDto>(await Act(w, plain.Id, "unschedule"))).Status);
        Assert.NotEmpty(app.Campaigns.Deleted);
        await w.SetSettingsAsync(b => { b["approvalRequired"] = true; b["creatorCannotApprove"] = false; });
        var camp = await w.CreateAsync([c]); Assert.Equal(CampaignStatus.PendingApproval, (await w.SubmitAsync(camp.Id)).Status);
        Assert.Equal(CampaignStatus.Scheduled, (await Ok<CampaignDto>(await Act(w, camp.Id, "approve"))).Status);                   // policy allows self-approval
        var rejected = await w.CreateAsync([c]); await w.SubmitAsync(rejected.Id);
        var rj = await Ok<CampaignDto>(await Act(w, rejected.Id, "reject", new { reason = "Wrong audience" }));
        Assert.Equal(CampaignStatus.Draft, rj.Status); Assert.Equal("Wrong audience", rj.RejectedReason);
        await w.SetSettingsAsync(b => b["approvalRequired"] = false);                                                                // a scheduled-but-never-approved campaign cannot start once approval became mandatory
        await w.SetSettingsAsync(b => b["approvalRequired"] = true);
        await app.Db(db => db.Campaigns.IgnoreQueryFilters().Where(x => x.Id == plain.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, CampaignStatus.Scheduled)));
        await w.RunAsync(sp => sp.GetRequiredService<ICampaignRunner>().StartAsync(plain.Id, default));
        Assert.Equal(CampaignStatus.Scheduled, (await w.GetAsync(plain.Id)).Status);
    }

    // ---------------------------------------------------------------- statistics reconcile with the raw events

    [RequiresMySqlFact]
    public async Task Statistics_and_the_rollup_equal_the_raw_message_status_and_recipient_rows()
    {
        await using var w = await CampaignWorld.OpenAsync(app);
        var cs = await w.ContactsAsync(6); var suppressed = await w.ContactAsync(consent: false);
        var camp = await w.CreateAsync(cs.Append(suppressed), cooldownDays: 0); await w.StartAsync(camp.Id); await w.RunToEndAsync(camp.Id);
        await w.AckAsync(cs[0], 2); await w.AckAsync(cs[1], 3); await w.AckAsync(cs[1], 2);        // READ then a late DEVICE ack: never regresses
        await w.AckAsync(cs[2], 3); await w.InboundAsync(cs[2], "yes please"); await w.AckAsync(cs[2], 3);   // replied, then another READ ack
        await w.InboundAsync(cs[3], "STOP");                                                           // an opt-out after the send: not a reply
        var stats = await w.StatsAsync(camp.Id);

        var raw = await app.Db(async db =>
        {
            var rows = await db.CampaignRecipients.IgnoreQueryFilters().Where(r => r.CampaignId == camp.Id).ToListAsync();
            var msgs = await db.Messages.IgnoreQueryFilters().Where(m => m.CampaignId == camp.Id).ToDictionaryAsync(m => m.Id);
            var inbound = await db.Messages.IgnoreQueryFilters().Where(m => m.Direction == MessageDirection.Inbound && rows.Select(r => r.ConversationId).Contains(m.ConversationId)).ToListAsync();
            var convs = await db.CampaignConversions.IgnoreQueryFilters().Where(c => c.CampaignId == camp.Id).ToListAsync();
            return (rows, msgs, inbound, convs);
        });
        // every recipient agrees with its message; Replied needs a real inbound message after the send
        foreach (var r in raw.rows.Where(r => r.MessageId is not null))
        {
            var m = raw.msgs[r.MessageId!.Value];
            var expected = m.Status switch { MessageStatus.Sent => RecipientStatus.Sent, MessageStatus.Delivered => RecipientStatus.Delivered, MessageStatus.Read => RecipientStatus.Read, _ => r.Status };
            if (r.Status == RecipientStatus.Replied) { Assert.Contains(raw.inbound, i => i.ConversationId == r.ConversationId && i.OccurredAtUtc >= m.SentAtUtc); Assert.Contains(raw.convs, c => c.RecipientId == r.Id && c.Kind == ConversionKind.Reply); }
            else Assert.Equal(expected, r.Status);
        }
        Assert.Equal(raw.rows.Count(r => r.Status == RecipientStatus.Replied), raw.convs.Count(c => c.Kind == ConversionKind.Reply));
        Assert.Equal(1, stats.Counts.Replied);                                                          // only cs[2]: the STOP is not a reply
        Assert.Equal(raw.rows.Count, stats.Counts.Total);
        foreach (var s in Enum.GetValues<RecipientStatus>())
            Assert.Equal(raw.rows.Count(r => r.Status == s), s switch { RecipientStatus.Pending => stats.Counts.Pending, RecipientStatus.Skipped => stats.Counts.Skipped, RecipientStatus.Queued => stats.Counts.Queued, RecipientStatus.Sent => stats.Counts.Sent,
                RecipientStatus.Delivered => stats.Counts.Delivered, RecipientStatus.Read => stats.Counts.Read, RecipientStatus.Replied => stats.Counts.Replied, RecipientStatus.Failed => stats.Counts.Failed, _ => stats.Counts.Cancelled });
        Assert.Equal(raw.msgs.Values.Count(m => m.Status is MessageStatus.Sent or MessageStatus.Delivered or MessageStatus.Read), stats.Messaged);   // messaged = messages the gateway accepted
        Assert.Equal(6, w.TotalSent(cs)); Assert.Equal(stats.Messaged, w.TotalSent(cs));                // the fake gateway's own count is the third witness
        Assert.True(stats.RollupMatchesLive);

        // the rollup is a read model that is recomputed, never incremented: corrupt it and the next event repairs it
        await app.Db(db => db.CampaignStatsRollups.IgnoreQueryFilters().Where(r => r.CampaignId == camp.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Sent, 99).SetProperty(r => r.Total, 1)));
        Assert.False((await w.StatsAsync(camp.Id)).RollupMatchesLive);
        await w.AckAsync(cs[4], 2);
        var repaired = await w.StatsAsync(camp.Id);
        Assert.True(repaired.RollupMatchesLive); Assert.Equal(raw.rows.Count, repaired.Counts.Total);
    }
}
