using LeadPilot.Application.Conversion;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Conversion;
using LeadPilot.Infrastructure.Conversion;

namespace LeadPilot.UnitTests;

public class SlotCalculatorTests
{
    private static readonly Guid Rep = Guid.NewGuid();
    private static RuleInput Mon9to12(DayOfWeek d = DayOfWeek.Monday) => new(d, 9 * 60, 12 * 60);

    private static RepAvailabilityInput Input(string tz, IEnumerable<RuleInput> rules, IEnumerable<ExceptionInput>? ex = null, IEnumerable<BusyInput>? busy = null, int buffer = 0, int max = 6) =>
        new(Rep, tz, buffer, max, rules.ToList(), (ex ?? []).ToList(), (busy ?? []).ToList());

    private static SlotQuery Q(DateTime now, DateTime from, DateTime to, int slot = 60, int notice = 0, int days = 60) => new(now, from, to, slot, notice, days);
    private static DateTime U(int y, int m, int d, int h = 0, int mi = 0) => new(y, m, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void A_simple_week_produces_slots_in_utc_converted_from_the_rep_zone()
    {
        // Karachi is UTC+5 all year: Monday 2026-03-02 09:00-12:00 local = 04:00-07:00 UTC
        var slots = SlotCalculator.Calculate(Input("Asia/Karachi", [Mon9to12()]), Q(U(2026, 3, 1), U(2026, 3, 2), U(2026, 3, 3)));
        Assert.Equal([U(2026, 3, 2, 4), U(2026, 3, 2, 5), U(2026, 3, 2, 6)], slots.Select(s => s.StartUtc).ToArray());
        Assert.All(slots, s => Assert.Equal(TimeSpan.FromMinutes(60), s.EndUtc - s.StartUtc));
    }

    [Fact]
    public void Spring_forward_day_keeps_real_local_hours_and_never_starts_inside_the_gap()
    {
        // US 2026-03-08: 02:00 -> 03:00. Window 01:00-05:00 local spans the gap: 3 elapsed hours (01:00 EST, 03:00 EDT, 04:00 EDT).
        var rule = new RuleInput(DayOfWeek.Sunday, 60, 5 * 60);
        var slots = SlotCalculator.Calculate(Input("America/New_York", [rule]), Q(U(2026, 3, 1), U(2026, 3, 8), U(2026, 3, 9)));
        Assert.Equal([U(2026, 3, 8, 6), U(2026, 3, 8, 7), U(2026, 3, 8, 8)], slots.Select(s => s.StartUtc).ToArray());
        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        Assert.All(slots, s => Assert.False(tz.IsInvalidTime(TimeZoneInfo.ConvertTimeFromUtc(s.StartUtc, tz))));
    }

    [Fact]
    public void A_window_that_starts_inside_the_gap_moves_to_the_first_valid_instant()
    {
        var rule = new RuleInput(DayOfWeek.Sunday, 2 * 60 + 30, 4 * 60);   // 02:30 does not exist on 2026-03-08 in New York
        var slots = SlotCalculator.Calculate(Input("America/New_York", [rule]), Q(U(2026, 3, 1), U(2026, 3, 8), U(2026, 3, 9)));
        Assert.Equal(U(2026, 3, 8, 7), slots.Single().StartUtc);   // 03:00 EDT
    }

    [Fact]
    public void Fall_back_day_the_repeated_hour_counts_as_elapsed_time()
    {
        // US 2026-11-01: 02:00 -> 01:00. Window 00:00-03:00 local = 4 elapsed hours; the first 01:xx is the daylight one.
        var rule = new RuleInput(DayOfWeek.Sunday, 0, 3 * 60);
        var slots = SlotCalculator.Calculate(Input("America/New_York", [rule]), Q(U(2026, 10, 25), U(2026, 11, 1), U(2026, 11, 2)));
        Assert.Equal([U(2026, 11, 1, 4), U(2026, 11, 1, 5), U(2026, 11, 1, 6), U(2026, 11, 1, 7)], slots.Select(s => s.StartUtc).ToArray());
    }

    [Fact]
    public void The_same_wall_clock_hour_has_a_different_utc_instant_before_and_after_the_change()
    {
        var rule = new RuleInput(DayOfWeek.Monday, 9 * 60, 10 * 60);
        var before = SlotCalculator.Calculate(Input("Europe/London", [rule]), Q(U(2026, 3, 1), U(2026, 3, 23), U(2026, 3, 24))).Single();   // GMT
        var after = SlotCalculator.Calculate(Input("Europe/London", [rule]), Q(U(2026, 3, 1), U(2026, 3, 30), U(2026, 3, 31))).Single();   // BST
        Assert.Equal(U(2026, 3, 23, 9), before.StartUtc);
        Assert.Equal(U(2026, 3, 30, 8), after.StartUtc);
    }

    [Fact]
    public void Buffers_block_neighbouring_slots_on_both_sides()
    {
        var busy = new BusyInput(U(2026, 3, 2, 5), U(2026, 3, 2, 6));   // 10:00-11:00 Karachi
        var slots = SlotCalculator.Calculate(Input("Asia/Karachi", [Mon9to12()], busy: [busy], buffer: 15), Q(U(2026, 3, 1), U(2026, 3, 2), U(2026, 3, 3)));
        Assert.Empty(slots);   // 09-10 and 11-12 are both within 15 minutes of the booked hour
        var noBuffer = SlotCalculator.Calculate(Input("Asia/Karachi", [Mon9to12()], busy: [busy], buffer: 0), Q(U(2026, 3, 1), U(2026, 3, 2), U(2026, 3, 3)));
        Assert.Equal([U(2026, 3, 2, 4), U(2026, 3, 2, 6)], noBuffer.Select(s => s.StartUtc).ToArray());
    }

    [Fact]
    public void A_whole_day_closure_removes_the_day_and_an_extra_window_adds_slots()
    {
        var closed = new ExceptionInput(new DateTime(2026, 3, 2), ExceptionKind.Closed, null, null);
        Assert.Empty(SlotCalculator.Calculate(Input("Asia/Karachi", [Mon9to12()], [closed]), Q(U(2026, 3, 1), U(2026, 3, 2), U(2026, 3, 3))));
        var extra = new ExceptionInput(new DateTime(2026, 3, 3), ExceptionKind.ExtraOpen, 14 * 60, 15 * 60);   // Tuesday has no rule
        Assert.Equal(U(2026, 3, 3, 9), SlotCalculator.Calculate(Input("Asia/Karachi", [Mon9to12()], [extra]), Q(U(2026, 3, 1), U(2026, 3, 3), U(2026, 3, 4))).Single().StartUtc);
        var window = new ExceptionInput(new DateTime(2026, 3, 2), ExceptionKind.Closed, 10 * 60, 11 * 60);
        Assert.Equal(2, SlotCalculator.Calculate(Input("Asia/Karachi", [Mon9to12()], [window]), Q(U(2026, 3, 1), U(2026, 3, 2), U(2026, 3, 3))).Count);
    }

    [Fact]
    public void Past_minimum_notice_and_horizon_are_enforced()
    {
        var now = U(2026, 3, 2, 4, 30);   // 09:30 Karachi on the Monday
        var slots = SlotCalculator.Calculate(Input("Asia/Karachi", [Mon9to12()]), Q(now, U(2026, 3, 2), U(2026, 3, 3), notice: 0));
        Assert.DoesNotContain(slots, s => s.StartUtc < now);
        Assert.Equal([U(2026, 3, 2, 5), U(2026, 3, 2, 6)], slots.Select(s => s.StartUtc).ToArray());
        var withNotice = SlotCalculator.Calculate(Input("Asia/Karachi", [Mon9to12()]), Q(now, U(2026, 3, 2), U(2026, 3, 3), notice: 90));
        Assert.Equal([U(2026, 3, 2, 6)], withNotice.Select(s => s.StartUtc).ToArray());
        Assert.Empty(SlotCalculator.Calculate(Input("Asia/Karachi", [Mon9to12()]), Q(U(2026, 2, 1), U(2026, 3, 2), U(2026, 3, 3), days: 10)));   // beyond the horizon
    }

    [Fact]
    public void The_daily_maximum_counts_the_reps_demos_of_that_local_day()
    {
        var busy = new BusyInput(U(2026, 3, 2, 4), U(2026, 3, 2, 5));
        Assert.Empty(SlotCalculator.Calculate(Input("Asia/Karachi", [Mon9to12()], busy: [busy], max: 1), Q(U(2026, 3, 1), U(2026, 3, 2), U(2026, 3, 3))));
        Assert.Equal(2, SlotCalculator.Calculate(Input("Asia/Karachi", [Mon9to12()], busy: [busy], max: 2), Q(U(2026, 3, 1), U(2026, 3, 2), U(2026, 3, 3))).Count);
    }

    [Fact]
    public void Pooled_slots_list_every_free_rep_and_a_time_stays_bookable_while_one_is_free()
    {
        var a = Input("Asia/Karachi", [Mon9to12()]); var b = new RepAvailabilityInput(Guid.NewGuid(), "Asia/Karachi", 0, 6, [Mon9to12()], [], [new BusyInput(U(2026, 3, 2, 4), U(2026, 3, 2, 5))]);
        var pooled = SlotCalculator.Pool([a, b], Q(U(2026, 3, 1), U(2026, 3, 2), U(2026, 3, 3)));
        Assert.Equal(3, pooled.Count);
        Assert.Equal(2, pooled[1].RepUserIds.Count); Assert.Single(pooled[0].RepUserIds);
        Assert.Equal(pooled[0].StartUtc, U(2026, 3, 2, 4));
    }

    [Fact]
    public void Slot_ids_round_trip_and_garbage_is_rejected()
    {
        var id = DemoRules.SlotId(U(2026, 3, 2, 4, 30));
        Assert.True(DemoRules.TryParseSlotId(id, out var back)); Assert.Equal(U(2026, 3, 2, 4, 30), back);
        foreach (var bad in new[] { null, "", "x", "s2026", "s202603021430x", "20260302143", "sABCDEFGHIJKL" }) Assert.False(DemoRules.TryParseSlotId(bad, out _), bad);
    }
}

public class DemoAndTrialRuleTests
{
    private static readonly DateTime Now = new(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);
    private static Demo NewDemo(DateTime? start = null) => Demo.Create(Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid(), start ?? Now.AddDays(1), (start ?? Now.AddDays(1)).AddHours(1), DemoStatus.Booked, DemoSource.Staff, null, null, Now, "t");

    [Fact]
    public void The_state_machine_allows_only_the_documented_moves()
    {
        var all = Enum.GetValues<DemoStatus>();
        var allowed = new HashSet<(DemoStatus, DemoStatus)>
        {
            (DemoStatus.Requested, DemoStatus.Booked), (DemoStatus.Requested, DemoStatus.Cancelled), (DemoStatus.Booked, DemoStatus.Confirmed), (DemoStatus.Booked, DemoStatus.Completed), (DemoStatus.Booked, DemoStatus.NoShow),
            (DemoStatus.Booked, DemoStatus.Cancelled), (DemoStatus.Confirmed, DemoStatus.Completed), (DemoStatus.Confirmed, DemoStatus.NoShow), (DemoStatus.Confirmed, DemoStatus.Cancelled),
        };
        foreach (var f in all) foreach (var t in all) Assert.Equal(allowed.Contains((f, t)), DemoRules.CanMove(f, t));
    }

    [Fact]
    public void Cancelling_frees_the_slot_key_and_a_future_demo_cannot_be_completed()
    {
        var d = NewDemo();
        Assert.NotNull(d.ActiveSlot);
        Assert.Throws<DomainException>(() => d.MoveTo(DemoStatus.Completed, Now));
        Assert.Throws<DomainException>(() => d.MoveTo(DemoStatus.NoShow, Now));
        d.MoveTo(DemoStatus.Cancelled, Now, "no longer needed");
        Assert.Null(d.ActiveSlot); Assert.Equal("no longer needed", d.CancelReason);
        Assert.Throws<DomainException>(() => d.Reschedule(Guid.NewGuid(), Now.AddDays(2), Now.AddDays(2).AddHours(1), Now));
        var past = NewDemo(Now.AddHours(-3)); past.MoveTo(DemoStatus.Completed, Now); Assert.Null(past.ActiveSlot);
    }

    [Fact]
    public void Rescheduling_bumps_the_generation_and_asks_for_a_new_confirmation()
    {
        var d = NewDemo(); d.MoveTo(DemoStatus.Confirmed, Now);
        var rep = Guid.NewGuid(); d.Reschedule(rep, Now.AddDays(3), Now.AddDays(3).AddHours(1), Now);
        Assert.Equal(2, d.Generation); Assert.Equal(DemoStatus.Booked, d.Status); Assert.Equal(DemoRules.SlotKey(rep, Now.AddDays(3)), d.ActiveSlot);
    }

    [Theory]
    [InlineData("http://insecure.example/x")] [InlineData("ftp://x/y")] [InlineData("javascript:alert(1)")] [InlineData("not a url")]
    public void Meeting_links_must_be_https(string link) => Assert.Throws<DomainException>(() => Demo.CleanLink(link));

    [Fact]
    public void Settings_validation_covers_zone_template_offsets_and_account()
    {
        var s = ConversionSettings.Create(Guid.NewGuid(), Now);
        Assert.Throws<DomainException>(() => s.Update("Not/AZone", 30, 60, 30, AssignmentStrategy.RoundRobin, null, false, false, null, "60", null, 14, 48, false, null, 7, null, Now));
        Assert.Throws<DomainException>(() => s.Update("UTC", 30, 60, 30, AssignmentStrategy.RoundRobin, "http://x/{code}", false, false, null, "60", null, 14, 48, false, null, 7, null, Now));
        Assert.Throws<DomainException>(() => s.Update("UTC", 30, 60, 30, AssignmentStrategy.RoundRobin, null, false, true, null, "60", null, 14, 48, false, null, 7, null, Now));
        s.Update("Asia/Karachi", 30, 60, 30, AssignmentStrategy.LeastLoaded, "https://meet.example.com/{code}", false, true, Guid.NewGuid(), "60, 1440,60,abc,2", null, 14, 48, false, null, 7, "Pay by bank", Now);
        Assert.Equal("1440,60", s.ReminderOffsets); Assert.Equal(1, s.Version);
    }

    [Fact]
    public void Trial_clock_starts_at_provisioning_and_only_active_trials_expire_or_convert()
    {
        var t = Trial.Create(Guid.NewGuid(), Guid.NewGuid(), null, null, null, null, 14, TrialSource.Staff, null, Now, "t");
        Assert.Null(t.ExpiresAtUtc);
        Assert.Throws<DomainException>(() => t.Expire(Now));
        Assert.Throws<DomainException>(() => t.RecordUsage(null, 1, 1, null, Now));
        Assert.Throws<DomainException>(() => t.MarkConverted(Guid.NewGuid(), Now));
        t.MarkProvisioned("acc-1", "https://app.example.com/login", null, Now);
        Assert.Equal(Now.AddDays(14), t.ExpiresAtUtc);
        t.Extend(7, Now.AddDays(1)); Assert.Equal(Now.AddDays(21), t.ExpiresAtUtc); Assert.Equal(21, t.DurationDays);
        t.RecordUsage(Now, 5, 2, "demo data", Now.AddDays(1)); Assert.Equal(5, t.LoginCount);
        Assert.Throws<DomainException>(() => t.RecordUsage(Now.AddDays(10), 1, 1, null, Now));
        t.Cancel("changed mind", Now); Assert.Throws<DomainException>(() => t.Cancel(null, Now)); Assert.Throws<DomainException>(() => t.MarkConverted(Guid.NewGuid(), Now));
    }

    [Fact]
    public void A_subscription_cannot_be_activated_without_a_payment_id_or_twice()
    {
        var s = Subscription.CreatePending(Guid.NewGuid(), Guid.NewGuid(), null, "Standard", null, BillingCycle.Monthly, Now, null);
        Assert.Equal(SubscriptionStatus.Pending, s.Status);
        Assert.Throws<DomainException>(() => s.ActivateWithVerifiedPayment(Guid.Empty, Now));
        s.ActivateWithVerifiedPayment(Guid.NewGuid(), Now); Assert.Equal(SubscriptionStatus.Active, s.Status); Assert.NotNull(s.ActivatedByPaymentId);
        Assert.Throws<DomainException>(() => s.ActivateWithVerifiedPayment(Guid.NewGuid(), Now));
    }

    [Fact]
    public void Money_and_payment_evidence_rules()
    {
        Assert.Equal(10.5m, Money.Amount(10.5m, "x")); Assert.Throws<DomainException>(() => Money.Amount(10.555m, "x")); Assert.Throws<DomainException>(() => Money.Amount(-1m, "x")); Assert.Throws<DomainException>(() => Money.Amount(0m, "x", false));
        Assert.Equal("USD", Money.Currency(" usd ")); Assert.Throws<DomainException>(() => Money.Currency("US")); Assert.Throws<DomainException>(() => Money.Currency("U$D"));
        Assert.Equal("TRX123AB", Money.EvidenceKey("trx-123 ab"));
        var t = Guid.NewGuid();
        Assert.Throws<DomainException>(() => Payment.Create(t, Guid.NewGuid(), Guid.NewGuid(), 10, "USD", PaymentMethod.BankTransfer, VerificationSource.Manual, "  ", Guid.NewGuid(), null, null, Now, null, Now));
        Assert.Throws<DomainException>(() => Payment.Create(t, Guid.NewGuid(), Guid.NewGuid(), 10, "USD", PaymentMethod.BankTransfer, VerificationSource.Manual, "R-1", Guid.NewGuid(), null, null, Now, null, Now));   // too short once normalised
        Assert.Throws<DomainException>(() => Payment.Create(t, Guid.NewGuid(), Guid.NewGuid(), 10, "USD", PaymentMethod.BankTransfer, VerificationSource.Manual, "RECEIPT-1", null, null, null, Now, null, Now));
        Assert.Throws<DomainException>(() => Payment.Create(t, Guid.NewGuid(), Guid.NewGuid(), 10, "USD", PaymentMethod.BankTransfer, VerificationSource.Manual, "RECEIPT-1", Guid.NewGuid(), null, null, Now.AddDays(1), null, Now));
    }
}

public class ConversionTextTests
{
    [Fact]
    public void Only_listed_placeholders_are_allowed_and_values_are_sanitised()
    {
        Assert.Null(ConversionText.Validate("Hi {first_name}, {date} {time}", ConversionText.DemoPlaceholders));
        Assert.NotNull(ConversionText.Validate("Hi {admin}", ConversionText.DemoPlaceholders));
        Assert.NotNull(ConversionText.Validate("Hi {first_name", ConversionText.DemoPlaceholders));
        var text = ConversionText.Render("Hi {first_name}! {link}", new Dictionary<string, string> { ["first_name"] = "Sara<script>{x}\n", ["link"] = "" });
        Assert.Equal("Hi Sarascriptx!", text);
        Assert.Equal("Sara", ConversionText.FirstName("Sara Khan"));
    }
}
