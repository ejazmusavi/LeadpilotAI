using System.Net;
using LeadPilot.Application.Common;
using LeadPilot.Application.Conversion;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Crm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

[Collection("app")]
public class DemoTests(AppFixture app)
{
    [RequiresMySqlFact]
    public async Task A_demo_is_booked_at_a_free_slot_and_the_slot_disappears()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var lead = await w.LeadAsync(); var slot = await w.SlotAsync();
        var demo = await w.BookAsync(lead, slot.SlotId);
        Assert.Equal(DemoStatus.Booked, demo.Status); Assert.Equal(slot.StartUtc, demo.StartUtc); Assert.Equal(w.AdminUserId, demo.RepUserId);
        Assert.DoesNotContain(await w.SlotsAsync(slot.StartUtc, slot.StartUtc.AddHours(1)), s => s.SlotId == slot.SlotId);
        // the same slot for another lead is refused, nothing is double booked
        var second = await w.BookRawAsync(await w.LeadAsync(), slot.SlotId);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await w.Db(db => db.Demos.IgnoreQueryFilters().CountAsync(d => d.TenantId == w.TenantId && d.StartUtc == slot.StartUtc && d.ActiveSlot != null)));
        // history and lead activity record it; the booking event reaches the outbox
        Assert.Contains((await Crm.Ok<DemoDto>(await w.Admin.Get($"/api/v1/demos/{demo.Id}"))).History, h => h.Type == "Booked");
        Assert.Single(await w.OutboxAsync("demo.booked"));
    }

    [RequiresMySqlFact]
    public async Task A_time_outside_the_available_slots_the_past_and_garbage_are_refused()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var lead = await w.LeadAsync(); var slot = await w.SlotAsync();
        // not an hour boundary of the grid
        var offGrid = DemoRules.SlotId(slot.StartUtc.AddMinutes(17));
        Assert.Equal(HttpStatusCode.Conflict, (await w.BookRawAsync(lead, offGrid)).StatusCode);
        // far beyond the booking horizon (60 days)
        Assert.Equal(HttpStatusCode.Conflict, (await w.BookRawAsync(lead, DemoRules.SlotId(w.Now.Date.AddDays(200).AddHours(10)))).StatusCode);
        // in the past
        Assert.Equal(HttpStatusCode.BadRequest, (await w.BookRawAsync(lead, DemoRules.SlotId(w.Now.AddHours(-5)))).StatusCode);
        foreach (var junk in new[] { "nonsense", "", "s2026", "../../x" }) Assert.Equal(HttpStatusCode.BadRequest, (await w.BookRawAsync(lead, junk)).StatusCode);
        Assert.Equal(0, await w.Db(db => db.Demos.IgnoreQueryFilters().CountAsync(d => d.TenantId == w.TenantId)));
        // outside the rep's weekly hours
        await w.Admin.Put($"/api/v1/demo-settings/reps/{(await w.AddRepAsync(w.AdminUserId, hours: false)).Id}/hours", new { rules = new[] { new { day = "Monday", startMinute = 540, endMinute = 600 } } });
        var next = w.Now.Date.AddDays(1); while (next.DayOfWeek != DayOfWeek.Tuesday) next = next.AddDays(1);
        Assert.Equal(HttpStatusCode.Conflict, (await w.BookRawAsync(lead, DemoRules.SlotId(next.AddHours(9)))).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Concurrent_bookings_of_one_slot_produce_exactly_one_demo_per_free_rep()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var slot = await w.SlotAsync();
        var leads = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => w.LeadAsync()));
        var results = await Task.WhenAll(leads.Select(l => w.BookRawAsync(l, slot.SlotId)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created)); Assert.Equal(7, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await w.Db(db => db.Demos.IgnoreQueryFilters().CountAsync(d => d.TenantId == w.TenantId && d.ActiveSlot != null)));

        // two reps: the same race yields two demos (one per rep), never two for one rep
        var second = await app.World.AddUserAsync(w.TenantId, $"rep{Guid.NewGuid():N}@example.com", "Sales Agent");
        await w.AddRepAsync(second.Id);
        var slot2 = await w.SlotAsync(2);
        var leads2 = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => w.LeadAsync()));
        var res2 = await Task.WhenAll(leads2.Select(l => w.BookRawAsync(l, slot2.SlotId)));
        Assert.Equal(2, res2.Count(r => r.StatusCode == HttpStatusCode.Created));
        var booked = await w.Db(db => db.Demos.IgnoreQueryFilters().Where(d => d.TenantId == w.TenantId && d.StartUtc == slot2.StartUtc && d.ActiveSlot != null).Select(d => d.RepUserId).ToListAsync());
        Assert.Equal(2, booked.Distinct().Count());
    }

    [RequiresMySqlFact]
    public async Task The_unique_index_rejects_a_second_active_demo_for_the_same_rep_and_start_even_without_the_service()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var lead = await w.LeadAsync(); var lead2 = await w.LeadAsync(); var slot = await w.SlotAsync();
        await w.BookAsync(lead, slot.SlotId);
        await Assert.ThrowsAsync<DbUpdateException>(() => w.Db(async db =>
        {
            db.Demos.Add(Demo.Create(w.TenantId, lead2, null, w.AdminUserId, slot.StartUtc, slot.EndUtc, DemoStatus.Booked, DemoSource.Staff, null, null, DateTime.UtcNow, "test"));
            await db.SaveChangesAsync(); return 0;
        }));
    }

    [RequiresMySqlFact]
    public async Task Reschedule_moves_the_demo_frees_the_old_slot_and_refuses_taken_or_past_slots()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var lead = await w.LeadAsync(); var other = await w.LeadAsync();
        var a = await w.SlotAsync(1); var b = await w.SlotAsync(1, 3); var c = await w.SlotAsync(1, 5);
        var demo = await w.BookAsync(lead, a.SlotId); await w.BookAsync(other, c.SlotId);
        var moved = await Crm.Ok<DemoDto>(await w.Admin.Post($"/api/v1/demos/{demo.Id}/reschedule", new { slotId = b.SlotId }));
        Assert.Equal(b.StartUtc, moved.StartUtc); Assert.Equal(2, moved.Generation);
        Assert.Contains(await w.SlotsAsync(a.StartUtc, a.EndUtc), s => s.SlotId == a.SlotId);               // old slot is free again
        Assert.DoesNotContain(await w.SlotsAsync(b.StartUtc, b.EndUtc), s => s.SlotId == b.SlotId);
        Assert.Equal(HttpStatusCode.Conflict, (await w.Admin.Post($"/api/v1/demos/{demo.Id}/reschedule", new { slotId = c.SlotId })).StatusCode);   // taken by someone else
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post($"/api/v1/demos/{demo.Id}/reschedule", new { slotId = DemoRules.SlotId(w.Now.AddHours(-2)) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post($"/api/v1/demos/{demo.Id}/reschedule", new { slotId = b.SlotId })).StatusCode);   // already there
        Assert.Contains((await Crm.Ok<DemoDto>(await w.Admin.Get($"/api/v1/demos/{demo.Id}"))).History, h => h.Type == "Rescheduled");
    }

    [RequiresMySqlFact]
    public async Task Cancel_frees_the_slot_and_the_state_machine_is_enforced_including_never_completing_a_future_demo()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var lead = await w.LeadAsync(); var slot = await w.SlotAsync();
        var demo = await w.BookAsync(lead, slot.SlotId);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post($"/api/v1/demos/{demo.Id}/complete")).StatusCode);   // has not started
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post($"/api/v1/demos/{demo.Id}/no-show")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post($"/api/v1/demos/{demo.Id}/accept")).StatusCode);     // not Requested
        Assert.Equal(DemoStatus.Confirmed, (await Crm.Ok<DemoDto>(await w.Admin.Post($"/api/v1/demos/{demo.Id}/confirm"))).Status);
        var cancelled = await Crm.Ok<DemoDto>(await w.Admin.Post($"/api/v1/demos/{demo.Id}/cancel", new { reason = "customer asked" }));
        Assert.Equal(DemoStatus.Cancelled, cancelled.Status); Assert.Equal("customer asked", cancelled.CancelReason);
        Assert.Contains(await w.SlotsAsync(slot.StartUtc, slot.EndUtc), s => s.SlotId == slot.SlotId);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post($"/api/v1/demos/{demo.Id}/cancel", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post($"/api/v1/demos/{demo.Id}/reschedule", new { slotId = slot.SlotId })).StatusCode);
        await w.BookAsync(lead, slot.SlotId);   // the lead can be booked again after cancelling
    }

    [RequiresMySqlFact]
    public async Task A_completed_demo_publishes_the_event_once_and_a_lead_has_one_upcoming_demo()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var lead = await w.LeadAsync(); var slot = await w.SlotAsync();
        var demo = await w.BookAsync(lead, slot.SlotId);
        Assert.Equal(HttpStatusCode.Conflict, (await w.BookRawAsync(lead, (await w.SlotAsync(1, 4)).SlotId)).StatusCode);
        app.Clock.Offset = slot.StartUtc.AddMinutes(30) - DateTime.UtcNow;
        var done = await Crm.Ok<DemoDto>(await w.Admin.Post($"/api/v1/demos/{demo.Id}/complete"));
        Assert.Equal(DemoStatus.Completed, done.Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await w.Admin.Post($"/api/v1/demos/{demo.Id}/complete")).StatusCode);
        Assert.Single(await w.OutboxAsync("demo.completed"));
    }

    [RequiresMySqlFact]
    public async Task Slots_follow_the_workspace_time_zone_across_a_daylight_saving_change()
    {
        await using var w = await ConvWorld.OpenAsync(app, rep: false, zone: "America/New_York");
        await w.AddRepAsync(w.AdminUserId, "America/New_York", hours: false);
        var rep = (await Crm.Ok<List<DemoRepDto>>(await w.Admin.Get("/api/v1/demo-settings/reps"))).Single();
        await Crm.Ok<DemoRepDto>(await w.Admin.Put($"/api/v1/demo-settings/reps/{rep.Id}/hours", new { rules = Enum.GetValues<DayOfWeek>().Select(d => new { day = d.ToString(), startMinute = 540, endMinute = 600 }) }));
        // 09:00 New York on both sides of the 2026-03-08 change, asked for as one window (fixed in the past of the test clock via the offset)
        app.Clock.Offset = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc) - DateTime.UtcNow;
        var slots = await w.SlotsAsync(new DateTime(2026, 3, 6, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 11, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(5, slots.Count); Assert.All(slots, s => Assert.Equal("09:00", s.LocalStart));
        Assert.Equal(new DateTime(2026, 3, 7, 14, 0, 0, DateTimeKind.Utc), slots.Single(s => s.LocalDate == "2026-03-07").StartUtc);   // EST, UTC-5
        Assert.Equal(new DateTime(2026, 3, 9, 13, 0, 0, DateTimeKind.Utc), slots.Single(s => s.LocalDate == "2026-03-09").StartUtc);    // EDT, UTC-4
        Assert.DoesNotContain(slots, s => s.LocalDate == "2026-03-06" && false);
    }

    [RequiresMySqlFact]
    public async Task Round_robin_alternates_and_least_loaded_prefers_the_free_rep_with_fewer_demos()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var r2 = await app.World.AddUserAsync(w.TenantId, $"rep{Guid.NewGuid():N}@example.com", "Sales Agent");
        await w.AddRepAsync(r2.Id);
        var reps1 = new List<Guid>();
        for (var i = 0; i < 4; i++) reps1.Add((await w.BookAsync(await w.LeadAsync(), (await w.SlotAsync(1, i * 2)).SlotId)).RepUserId);
        Assert.Equal(2, reps1.Distinct().Count()); Assert.Equal(2, reps1.Count(r => r == reps1[0]));
        Assert.NotEqual(reps1[0], reps1[1]);
        await w.SettingsAsync(new() { ["assignment"] = "LeastLoaded" });
        var extra = await w.BookAsync(await w.LeadAsync(), (await w.SlotAsync(3)).SlotId);
        Assert.Contains(extra.RepUserId, new[] { w.AdminUserId, r2.Id });
        // a busy rep is skipped: block one rep's slot, the other one gets it
        var slotX = await w.SlotAsync(4);
        var first = await w.BookAsync(await w.LeadAsync(), slotX.SlotId); var second = await w.BookAsync(await w.LeadAsync(), slotX.SlotId);
        Assert.NotEqual(first.RepUserId, second.RepUserId);
        Assert.Equal(HttpStatusCode.Conflict, (await w.BookRawAsync(await w.LeadAsync(), slotX.SlotId)).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Buffers_exceptions_and_the_daily_maximum_shape_what_can_be_booked()
    {
        await using var w = await ConvWorld.OpenAsync(app, rep: false);
        var rep = await w.AddRepAsync(w.AdminUserId, buffer: 60, maxPerDay: 2, hours: true);
        var day = w.Now.Date.AddDays(2);
        var slots = (await w.SlotsAsync(day, day.AddDays(1))).OrderBy(s => s.StartUtc).ToList();
        var noon = slots.Single(s => s.StartUtc == day.AddHours(12));
        await w.BookAsync(await w.LeadAsync(), noon.SlotId);
        var after = await w.SlotsAsync(day, day.AddDays(1));
        Assert.DoesNotContain(after, s => s.StartUtc == day.AddHours(11) || s.StartUtc == day.AddHours(13) || s.StartUtc == day.AddHours(12));   // the buffer blocks both neighbours
        Assert.Contains(after, s => s.StartUtc == day.AddHours(14));
        await w.BookAsync(await w.LeadAsync(), day.AddHours(15).Let(h => DemoRules.SlotId(h)));
        Assert.Empty(await w.SlotsAsync(day, day.AddDays(1)));   // the daily maximum of 2 is reached
        // time off: the next day is closed
        var next = day.AddDays(1);
        Assert.NotEmpty(await w.SlotsAsync(next, next.AddDays(1)));
        Assert.Equal(HttpStatusCode.OK, (await w.Admin.Post($"/api/v1/demo-settings/reps/{rep.Id}/exceptions", new { date = next, kind = "Closed", note = "holiday" })).StatusCode);
        Assert.Empty(await w.SlotsAsync(next, next.AddDays(1)));
        Assert.Equal(HttpStatusCode.Conflict, (await w.BookRawAsync(await w.LeadAsync(), DemoRules.SlotId(next.AddHours(10)))).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Reminders_go_through_the_pipeline_only_for_eligible_contacts_exactly_once_and_follow_reschedules()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        var account = await w.W.Ai.AccountAsync();
        await w.SettingsAsync(new() { ["remindersEnabled"] = true, ["reminderChannelAccountId"] = account.Id, ["reminderOffsets"] = "1440,60", ["meetingLinkTemplate"] = "https://meet.example.com/{code}" });

        var (leadOk, contactOk, _) = await MessagingKit.LeadWithContactAsync(w.Admin);
        Assert.True((await w.Admin.Post($"/api/v1/contacts/{contactOk}/consent", new { channel = "WhatsApp", purpose = "Service", status = "Granted", source = "Booking form", evidenceRef = "bk-1" })).IsSuccessStatusCode);
        var (leadSup, contactSup, phoneSup) = await MessagingKit.LeadWithContactAsync(w.Admin);
        Assert.True((await w.Admin.Post($"/api/v1/contacts/{contactSup}/consent", new { channel = "WhatsApp", purpose = "Service", status = "Granted", source = "Booking form", evidenceRef = "bk-2" })).IsSuccessStatusCode);
        Assert.True((await w.Admin.Post("/api/v1/suppression", new { channel = "WhatsApp", value = phoneSup, reason = "OptOut" })).IsSuccessStatusCode);
        var (leadNone, _, _) = await MessagingKit.LeadWithContactAsync(w.Admin);   // marketing/service consent never recorded, no inbound message

        var ok = await w.BookAsync(leadOk, (await w.SlotAsync(2, 0)).SlotId);
        var sup = await w.BookAsync(leadSup, (await w.SlotAsync(2, 3)).SlotId);
        var none = await w.BookAsync(leadNone, (await w.SlotAsync(2, 6)).SlotId);
        Assert.NotNull(ok.MeetingLink); Assert.StartsWith("https://meet.example.com/", ok.MeetingLink);
        Assert.Equal(2, ok.Reminders.Count);

        app.Clock.Offset = ok.StartUtc.AddMinutes(-1440 + 1) - DateTime.UtcNow;     // the 24 h reminder of the first demo is due; the other demos' are not yet
        await w.ReminderSweepAsync(); await w.ReminderSweepAsync();                    // twice: still one message
        async Task<List<LeadPilot.Domain.Messaging.Message>> Msgs(Guid demoId) => await w.Db(db => db.Messages.IgnoreQueryFilters().Where(m => m.TenantId == w.TenantId && m.IdempotencyKey!.Contains(demoId.ToString("N"))).ToListAsync());
        Assert.Single(await Msgs(ok.Id));
        var okMsg = (await Msgs(ok.Id)).Single(); Assert.Equal(LeadPilot.Domain.Messaging.MessagePurpose.Service, okMsg.Purpose); Assert.Contains("meet.example.com", okMsg.Body);
        var d = await Crm.Ok<DemoDto>(await w.Admin.Get($"/api/v1/demos/{ok.Id}"));
        Assert.Equal(1, d.Reminders.Count(r => r.Status == ReminderStatus.Sent)); Assert.Equal(1, d.Reminders.Count(r => r.Status == ReminderStatus.Scheduled));

        app.Clock.Offset = sup.StartUtc.AddMinutes(-1440 + 1) - DateTime.UtcNow; await w.ReminderSweepAsync();
        Assert.Empty(await Msgs(sup.Id)); Assert.Empty(await Msgs(none.Id));        // suppressed contact and a contact without any consent get nothing
        var ds = await Crm.Ok<DemoDto>(await w.Admin.Get($"/api/v1/demos/{sup.Id}"));
        Assert.Contains(ds.Reminders, r => r.Status == ReminderStatus.Skipped && r.Reason!.Contains("suppress", StringComparison.OrdinalIgnoreCase));
        app.Clock.Offset = none.StartUtc.AddMinutes(-1440 + 1) - DateTime.UtcNow; await w.ReminderSweepAsync();
        Assert.Empty(await Msgs(none.Id));
        var dn = await Crm.Ok<DemoDto>(await w.Admin.Get($"/api/v1/demos/{none.Id}"));
        Assert.Contains(dn.Reminders, r => r.Status == ReminderStatus.Skipped && r.Reason!.Contains("service consent", StringComparison.OrdinalIgnoreCase));

        // rescheduling withdraws the old reminders and creates new ones
        app.Clock.Offset = TimeSpan.Zero;
        var moved = await Crm.Ok<DemoDto>(await w.Admin.Post($"/api/v1/demos/{ok.Id}/reschedule", new { slotId = (await w.SlotAsync(5)).SlotId }));
        Assert.Equal(2, moved.Generation); Assert.Equal(2, moved.Reminders.Count(r => r.Status == ReminderStatus.Scheduled));
        Assert.Equal(1, moved.Reminders.Count(r => r.Status == ReminderStatus.Cancelled)); Assert.Equal(1, moved.Reminders.Count(r => r.Status == ReminderStatus.Sent));   // the 24 h one was already sent, the 1 h one is withdrawn
    }

    [RequiresMySqlFact]
    public async Task Automated_bookings_can_require_acceptance_and_reminders_wait_for_it()
    {
        await using var w = await ConvWorld.OpenAsync(app);
        await w.SettingsAsync(new() { ["confirmAutomatedBookings"] = true });
        var lead = await w.LeadAsync(); var slot = await w.SlotAsync();
        // the same port the AI tool and the workflow step use
        await w.Scoped(async sp =>
        {
            var port = sp.GetRequiredService<LeadPilot.Application.Agents.IDemoScheduler>();
            var booked = await port.BookAsync(lead, new LeadPilot.Application.Agents.DemoSlot(slot.SlotId, slot.StartUtc, slot.EndUtc), "from automation", default);
            Assert.True(booked.Available && booked.Error is null);
        });
        var demo = (await Crm.Ok<PagedResult<DemoDto>>(await w.Admin.Get($"/api/v1/demos?leadId={lead}"))).Items.Single();
        Assert.Equal(DemoStatus.Requested, demo.Status); Assert.Equal(DemoSource.Automation, demo.Source);
        Assert.Equal(DemoStatus.Booked, (await Crm.Ok<DemoDto>(await w.Admin.Post($"/api/v1/demos/{demo.Id}/accept"))).Status);
    }
}

internal static class Fn { public static R Let<T, R>(this T v, Func<T, R> f) => f(v); }
