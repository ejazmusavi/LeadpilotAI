using System.Text;
using System.Text.Json;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Messaging;
using LeadPilot.Infrastructure.Messaging.Broker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeadPilot.UnitTests;

public class OptOutDetectorTests
{
    [Theory]
    [InlineData("STOP")] [InlineData("stop")] [InlineData("Stop.")] [InlineData("  stop!!  ")] [InlineData("Unsubscribe")] [InlineData("UNSUBSCRIBE")] [InlineData("cancel")] [InlineData("opt out")] [InlineData("opt-out")] [InlineData("optout")]
    [InlineData("stop please")] [InlineData("Stop sending me messages")] [InlineData("stop messages now")] [InlineData("unsubscribe me")] [InlineData("remove me")] [InlineData("Please stop")]
    public void English_opt_outs_are_detected(string text) => Assert.Equal(!text.StartsWith("Please"), OptOutDetector.IsOptOut(text));   // "Please stop" is not a keyword prefix: documented false negative

    [Theory]
    [InlineData("band karo")] [InlineData("Band Karo!")] [InlineData("band kar do")] [InlineData("bas karo")] [InlineData("mat bhejo")] [InlineData("message band")] [InlineData("nahi chahiye")] [InlineData("mujhe message mat bhejo")] [InlineData("band karo please")] [InlineData("bas kro")]
    public void Roman_urdu_opt_outs_are_detected(string text) => Assert.True(OptOutDetector.IsOptOut(text));

    [Theory]
    [InlineData("بند کریں")] [InlineData("بند کرو")] [InlineData("بس کرو")] [InlineData("مت بھیجو")] [InlineData("نہیں چاہیے")] [InlineData("بند کرو۔")] [InlineData("بند کریں پلیز")] [InlineData("ان سبسکرائب")]
    public void Urdu_script_opt_outs_are_detected(string text) => Assert.True(OptOutDetector.IsOptOut(text));

    [Theory]
    [InlineData("don't stop now")] [InlineData("Don't stop now!")] [InlineData("please stop by tomorrow")] [InlineData("cancel my order #5")] [InlineData("I want to unsubscribe from the newsletter")]
    [InlineData("stop by my office at 5")] [InlineData("end of month report?")] [InlineData("Can you stop at the shop?")] [InlineData("hello")] [InlineData("")] [InlineData("   ")] [InlineData("yes")]
    [InlineData("band karo darwaza")] [InlineData("nahi chahiye abhi tak yeh wala")] [InlineData("کیا آپ بند کریں گے دکان")]
    public void Ordinary_sentences_containing_a_keyword_are_not_opt_outs(string text) => Assert.False(OptOutDetector.IsOptOut(text));

    [Fact] public void A_long_message_is_never_an_opt_out_even_if_it_starts_with_a_keyword() => Assert.False(OptOutDetector.IsOptOut("stop " + new string('a', 200)));
    [Fact] public void Null_is_safe() => Assert.False(OptOutDetector.IsOptOut(null));

    [Fact]
    public void A_tenant_can_replace_the_keyword_list()
    {
        var custom = OptOutDetector.ParseList("khatam\nBASTA, arrêtez");
        Assert.True(OptOutDetector.IsOptOut("Khatam!", custom)); Assert.True(OptOutDetector.IsOptOut("basta", custom)); Assert.True(OptOutDetector.IsOptOut("Arretez", custom));
        Assert.False(OptOutDetector.IsOptOut("stop", custom));   // the tenant list replaces the defaults
    }

    [Fact] public void An_empty_tenant_setting_means_the_defaults() { Assert.Same(OptOutDetector.DefaultKeywords, OptOutDetector.ParseList(null)); Assert.Same(OptOutDetector.DefaultKeywords, OptOutDetector.ParseList("  ")); }
    [Fact] public void Folding_removes_apostrophes_punctuation_and_case() => Assert.Equal("dont stop now", OptOutDetector.Fold("Don’t  STOP, now!!"));
}

public class MessageStatusMonotonicityTests
{
    private static Message Outbound(MessageStatus start)
    {
        var m = Message.Outbound(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, MessagePurpose.Service, "hi", "key-" + Guid.NewGuid(), "c", null, null, DateTime.UtcNow);
        if (start is MessageStatus.Sent or MessageStatus.Delivered or MessageStatus.Read) { m.MarkSent("true_1@c.us_A", DateTime.UtcNow); if (start != MessageStatus.Sent) m.ApplyDelivery(start, DateTime.UtcNow); }
        return m;
    }

    [Theory]
    [InlineData(MessageStatus.Sent, MessageStatus.Delivered, true, MessageStatus.Delivered)] [InlineData(MessageStatus.Sent, MessageStatus.Read, true, MessageStatus.Read)] [InlineData(MessageStatus.Delivered, MessageStatus.Read, true, MessageStatus.Read)]
    [InlineData(MessageStatus.Read, MessageStatus.Delivered, false, MessageStatus.Read)] [InlineData(MessageStatus.Read, MessageStatus.Sent, false, MessageStatus.Read)] [InlineData(MessageStatus.Delivered, MessageStatus.Sent, false, MessageStatus.Delivered)]
    [InlineData(MessageStatus.Delivered, MessageStatus.Delivered, false, MessageStatus.Delivered)] [InlineData(MessageStatus.Read, MessageStatus.Read, false, MessageStatus.Read)]
    public void A_status_only_ever_moves_forward(MessageStatus from, MessageStatus ack, bool moves, MessageStatus expected)
    {
        var m = Outbound(from);
        Assert.Equal(moves, m.ApplyDelivery(ack, DateTime.UtcNow)); Assert.Equal(expected, m.Status);
    }

    [Fact] public void Every_ordering_of_the_three_acks_ends_at_read()
    {
        var acks = new[] { MessageStatus.Sent, MessageStatus.Delivered, MessageStatus.Read };
        foreach (var perm in Permutations(acks))
        {
            var m = Outbound(MessageStatus.Sent);
            foreach (var a in perm) m.ApplyDelivery(a, DateTime.UtcNow);
            Assert.Equal(MessageStatus.Read, m.Status);
        }
    }

    [Fact] public void A_late_delivered_after_read_still_records_its_time_without_regressing()
    {
        var m = Outbound(MessageStatus.Sent); var t0 = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        m.ApplyDelivery(MessageStatus.Read, t0.AddMinutes(5));
        Assert.False(m.ApplyDelivery(MessageStatus.Delivered, t0.AddMinutes(1)));
        Assert.Equal(MessageStatus.Read, m.Status); Assert.NotNull(m.DeliveredAtUtc); Assert.NotNull(m.ReadAtUtc);
    }

    [Fact] public void An_ack_cannot_revive_or_progress_a_message_the_gateway_never_accepted()
    {
        var queued = Outbound(MessageStatus.Queued); Assert.False(queued.ApplyDelivery(MessageStatus.Read, DateTime.UtcNow)); Assert.Equal(MessageStatus.Queued, queued.Status);
        var failed = Outbound(MessageStatus.Queued); failed.MarkFailed("x", "y", DateTime.UtcNow); Assert.False(failed.ApplyDelivery(MessageStatus.Delivered, DateTime.UtcNow)); Assert.Equal(MessageStatus.Failed, failed.Status);
    }

    [Fact] public void An_unknown_outcome_resolves_to_the_acked_status_when_the_id_is_known()
    {
        var m = Outbound(MessageStatus.Queued); m.MarkUnknown("timeout", DateTime.UtcNow);
        Assert.Equal(MessageStatus.Unknown, m.Status);
        Assert.True(m.ApplyDelivery(MessageStatus.Delivered, DateTime.UtcNow)); Assert.Equal(MessageStatus.Delivered, m.Status);
    }

    [Fact] public void Inbound_messages_have_no_delivery_progression() => Assert.False(Message.Inbound(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "false_1@c.us_A", "x", DateTime.UtcNow, "c", DateTime.UtcNow).ApplyDelivery(MessageStatus.Read, DateTime.UtcNow));

    [Theory] [InlineData(-1, null)] [InlineData(0, null)] [InlineData(1, MessageStatus.Sent)] [InlineData(2, MessageStatus.Delivered)] [InlineData(3, MessageStatus.Read)] [InlineData(4, MessageStatus.Read)] [InlineData(99, null)]
    public void Gateway_acks_map_to_delivery_statuses(int ack, MessageStatus? expected) { Assert.Equal(expected, AckRules.ToDelivery(ack)); Assert.Equal(ack < 0, AckRules.IsError(ack)); }

    private static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length == 1) { yield return items; yield break; }
        for (var i = 0; i < items.Length; i++)
            foreach (var rest in Permutations(items.Where((_, j) => j != i).ToArray())) yield return [items[i], .. rest];
    }
}

public class SendRatePolicyTests
{
    private static readonly DateTime T0 = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    private static SendRateFacts Facts(int hourly = 30, int daily = 150, int delay = 30, DateTime? warm = null, DateTime? last = null, int hour = 0, int day = 0, DateTime? oldestHour = null, DateTime? oldestDay = null)
        => new(hourly, daily, delay, warm ?? T0.AddDays(-30), last, hour, day, oldestHour, oldestDay);

    [Theory]
    [InlineData(0, 20)] [InlineData(1, 40)] [InlineData(2, 60)] [InlineData(5, 120)] [InlineData(6, 140)] [InlineData(7, 150)] [InlineData(30, 150)]
    public void Warm_up_starts_at_20_and_grows_20_a_day_to_the_cap(int daysSinceStart, int expected) => Assert.Equal(expected, SendRatePolicy.DailyLimitToday(150, T0.AddDays(-daysSinceStart).AddHours(-1), T0));

    [Fact] public void A_number_that_never_worked_is_held_at_the_first_day_limit() => Assert.Equal(20, SendRatePolicy.DailyLimitToday(150, null, T0));
    [Fact] public void A_low_daily_cap_wins_over_the_warm_up_allowance() => Assert.Equal(10, SendRatePolicy.DailyLimitToday(10, T0.AddDays(-9), T0));

    [Fact] public void Nothing_blocks_a_quiet_account() { var d = SendRatePolicy.Evaluate(Facts(), T0); Assert.True(d.CanSendNow); Assert.Null(d.RetryAtUtc); }
    [Fact] public void The_minimum_delay_blocks_until_it_has_passed() { var d = SendRatePolicy.Evaluate(Facts(last: T0.AddSeconds(-10)), T0); Assert.Equal(SendBlock.MinimumDelay, d.Block); Assert.Equal(T0.AddSeconds(20), d.RetryAtUtc); }
    [Fact] public void Exactly_the_minimum_delay_later_it_is_allowed() => Assert.True(SendRatePolicy.Evaluate(Facts(last: T0.AddSeconds(-30)), T0).CanSendNow);
    [Fact] public void The_hourly_cap_blocks_until_the_oldest_send_leaves_the_hour() { var d = SendRatePolicy.Evaluate(Facts(hour: 30, day: 30, oldestHour: T0.AddMinutes(-40), oldestDay: T0.AddMinutes(-40)), T0); Assert.Equal(SendBlock.HourlyCap, d.Block); Assert.Equal(T0.AddMinutes(20), d.RetryAtUtc); }
    [Fact] public void The_daily_cap_blocks_until_the_oldest_send_leaves_24_hours() { var d = SendRatePolicy.Evaluate(Facts(hour: 1, day: 150, oldestDay: T0.AddHours(-20)), T0); Assert.Equal(SendBlock.DailyCap, d.Block); Assert.Equal(T0.AddHours(4), d.RetryAtUtc); }
    [Fact] public void The_warm_up_limit_blocks_a_new_account_after_20_messages() { var d = SendRatePolicy.Evaluate(Facts(warm: T0.AddHours(-2), day: 20, oldestDay: T0.AddHours(-1)), T0); Assert.Equal(SendBlock.DailyCap, d.Block); Assert.Equal(20, d.DailyLimitToday); }
    [Fact] public void One_below_each_limit_is_still_allowed() => Assert.True(SendRatePolicy.Evaluate(Facts(hour: 29, day: 149), T0).CanSendNow);
    [Fact] public void The_daily_cap_is_checked_before_the_hourly_cap() => Assert.Equal(SendBlock.DailyCap, SendRatePolicy.Evaluate(Facts(hour: 30, day: 150, oldestDay: T0.AddHours(-1), oldestHour: T0.AddMinutes(-10)), T0).Block);

    [Theory] [InlineData(30, 0.0, 0)] [InlineData(30, 1.0, 15)] [InlineData(30, 0.5, 7.5)] [InlineData(30, 5.0, 15)] [InlineData(30, -1.0, 0)]
    public void Jitter_stays_between_zero_and_half_the_minimum_delay(int delay, double r, double seconds) => Assert.Equal(seconds, SendRatePolicy.Jitter(delay, r).TotalSeconds);

    [Fact] public void Retry_backoff_grows_and_is_bounded() { Assert.Equal(30, SendRetryPolicy.Backoff(1).TotalSeconds); Assert.Equal(120, SendRetryPolicy.Backoff(2).TotalSeconds); Assert.Equal(480, SendRetryPolicy.Backoff(3).TotalSeconds); Assert.Equal(1800, SendRetryPolicy.Backoff(9).TotalSeconds); }
}

public class ChatIdAndPhoneMatchingTests
{
    [Theory]
    [InlineData("923001234567@c.us", "+923001234567")] [InlineData("14155550123@c.us", "+14155550123")] [InlineData("923001234567@s.whatsapp.net", "+923001234567")] [InlineData("923001234567:12@s.whatsapp.net", "+923001234567")]
    [InlineData("99887766@lid", null)] [InlineData("123@g.us", null)] [InlineData("status@broadcast", null)] [InlineData("abc@c.us", null)] [InlineData("12@c.us", null)] [InlineData("", null)] [InlineData(null, null)] [InlineData("923001234567", null)] [InlineData("+923001234567@c.us", null)]
    public void Chat_ids_convert_to_e164_only_when_they_really_carry_a_phone(string? chat, string? e164) => Assert.Equal(e164, ChatIds.ToE164(chat));

    [Theory] [InlineData("+923001234567", "923001234567@c.us")] [InlineData("+92 300 1234567", "923001234567@c.us")] [InlineData("0923001234567", null)] [InlineData("garbage", null)] [InlineData(null, null)]
    public void E164_converts_to_the_gateway_chat_id(string? e164, string? chat) => Assert.Equal(chat, ChatIds.FromE164(e164));

    [Fact] public void A_round_trip_through_the_chat_id_matches_the_contact_number() => Assert.Equal("+923001234567", ChatIds.ToE164(ChatIds.FromE164("+92 (300) 123-4567")));

    [Theory] [InlineData("1@c.us", true)] [InlineData("1@lid", true)] [InlineData("1@g.us", false)] [InlineData("1@newsletter", false)] [InlineData("status@broadcast", false)] [InlineData(null, false)]
    public void Only_direct_chats_are_direct(string? id, bool direct) => Assert.Equal(direct, ChatIds.IsDirectChat(id));
}

public class IdempotencyKeyTests
{
    [Fact] public void A_client_key_is_kept_trimmed() => Assert.Equal("order-42", IdempotencyKeys.Normalize("  order-42 "));
    [Fact] public void A_blank_key_becomes_a_fresh_unique_key() { var a = IdempotencyKeys.Normalize(null); var b = IdempotencyKeys.Normalize(" "); Assert.NotEqual(a, b); Assert.StartsWith("auto-", a); }
    [Fact] public void An_overlong_key_is_bounded_deterministically() { var k = IdempotencyKeys.Normalize(new string('k', 500)); Assert.Equal(IdempotencyKeys.MaxLength, k.Length); Assert.Equal(k, IdempotencyKeys.Normalize(new string('k', 500))); }
}

public class TemplateRendererTests
{
    private static readonly Dictionary<string, string> Values = new() { ["name"] = "Ayesha Khan", ["first_name"] = "Ayesha", ["phone"] = "+923001234567", ["business_name"] = "Bright School" };
    [Fact] public void Placeholders_are_filled_case_insensitively_and_with_spaces() => Assert.Equal("Hi Ayesha, welcome Ayesha Khan!", TemplateRenderer.Render("Hi {{first_name}}, welcome {{ NAME }}!", Values));
    [Fact] public void A_template_without_placeholders_is_unchanged() => Assert.Equal("Hello", TemplateRenderer.Render("Hello", Values));
    [Fact] public void A_missing_or_blank_value_is_an_error_not_a_literal_placeholder()
    {
        var ex = Assert.Throws<TemplateRenderException>(() => TemplateRenderer.Render("Hi {{first_name}} from {{business_name}}", new Dictionary<string, string> { ["first_name"] = "A", ["business_name"] = " " }));
        Assert.Contains("business_name", ex.Message);
    }
    [Fact] public void Unknown_variables_are_found_before_saving() { Assert.Equal(["nmae"], TemplateRenderer.UnknownVariables("Hi {{nmae}} {{name}}")); Assert.Empty(TemplateRenderer.UnknownVariables("Hi {{name}}")); }
    [Fact] public void Variable_discovery_is_distinct() => Assert.Equal(["name", "phone"], TemplateRenderer.VariablesIn("{{name}} {{Name}} {{phone}}"));
    [Theory] [InlineData("Ayesha Khan", "Ayesha")] [InlineData("Ali", "Ali")] [InlineData("  ", "")] [InlineData(null, "")]
    public void First_name_is_the_first_word(string? full, string first) => Assert.Equal(first, TemplateRenderer.FirstName(full));
    [Fact] public void Rendered_values_are_not_re_scanned_for_placeholders() => Assert.Equal("Hi {{phone}}", TemplateRenderer.Render("Hi {{name}}", new Dictionary<string, string> { ["name"] = "{{phone}}", ["phone"] = "x" }));
}

public class OutboundPolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    [Fact] public void A_reply_inside_the_window_is_a_service_message() => Assert.Equal(MessagePurpose.Service, OutboundPolicy.PurposeFor(Now.AddHours(-3), Now, TimeSpan.FromHours(24)));
    [Fact] public void Exactly_at_the_window_edge_it_is_still_service() => Assert.Equal(MessagePurpose.Service, OutboundPolicy.PurposeFor(Now.AddHours(-24), Now, TimeSpan.FromHours(24)));
    [Fact] public void Just_outside_the_window_it_is_marketing() => Assert.Equal(MessagePurpose.Marketing, OutboundPolicy.PurposeFor(Now.AddHours(-24).AddSeconds(-1), Now, TimeSpan.FromHours(24)));
    [Fact] public void No_inbound_message_ever_means_marketing() => Assert.Equal(MessagePurpose.Marketing, OutboundPolicy.PurposeFor(null, Now, TimeSpan.FromHours(24)));
    [Theory] [InlineData(null, 24)] [InlineData("", 24)] [InlineData("12", 12)] [InlineData("168", 168)] [InlineData("169", 24)] [InlineData("0", 24)] [InlineData("-5", 24)] [InlineData("abc", 24)]
    public void The_window_setting_is_parsed_defensively(string? setting, int hours) => Assert.Equal(hours, OutboundPolicy.ParseWindow(setting).TotalHours);
}

public class MessagingDomainTests
{
    [Fact] public void An_outbox_row_backs_off_exponentially_up_to_fifteen_minutes() { Assert.Equal(5, OutboxMessage.BackoffFor(1).TotalSeconds); Assert.Equal(10, OutboxMessage.BackoffFor(2).TotalSeconds); Assert.Equal(40, OutboxMessage.BackoffFor(4).TotalSeconds); Assert.Equal(900, OutboxMessage.BackoffFor(20).TotalSeconds); }

    [Fact]
    public void An_outbox_row_is_parked_after_the_maximum_number_of_attempts_and_published_rows_are_final()
    {
        var now = DateTime.UtcNow; var row = OutboxMessage.Create(Guid.NewGuid(), "message.received", "{}", "c", null, now);
        for (var i = 0; i < 2; i++) row.MarkAttemptFailed("boom", 3, now);
        Assert.Equal(OutboxStatus.Pending, row.Status); Assert.True(row.NextAttemptAtUtc > now);
        row.MarkAttemptFailed("boom", 3, now); Assert.Equal(OutboxStatus.Failed, row.Status);
        var ok = OutboxMessage.Create(Guid.NewGuid(), "message.received", "{}", "c", null, now); ok.MarkPublished(now); Assert.Equal(OutboxStatus.Published, ok.Status); Assert.NotNull(ok.PublishedAtUtc);
    }

    [Fact] public void Attachments_are_checked_against_the_type_and_size_policy()
    {
        var t = Guid.NewGuid(); var m = Guid.NewGuid(); var now = DateTime.UtcNow;
        Assert.Equal(AttachmentStatus.NotDownloaded, MessageAttachment.Describe(t, m, "a.jpg", "image/jpeg", 1000, now).Status);
        Assert.Equal(AttachmentStatus.NotDownloaded, MessageAttachment.Describe(t, m, "a.pdf", "application/pdf", null, now).Status);
        Assert.Equal(AttachmentStatus.Rejected, MessageAttachment.Describe(t, m, "a.exe", "application/x-msdownload", 10, now).Status);
        Assert.Equal(AttachmentStatus.Rejected, MessageAttachment.Describe(t, m, "big.mp4", "video/mp4", MessageAttachment.MaxSizeBytes + 1, now).Status);
        Assert.Equal(AttachmentStatus.Rejected, MessageAttachment.Describe(t, m, "x", null, 10, now).Status);   // unknown type = octet-stream = rejected
        Assert.Equal("passwd", MessageAttachment.Describe(t, m, "../../etc/passwd", "text/plain", 1, now).FileName);   // no path traversal in stored names
        Assert.Null(MessageAttachment.Describe(t, m, "a.jpg", "image/jpeg", 1, now).StorageKey);   // content is not downloaded in Phase 5
    }

    [Fact] public void Account_limits_are_validated_against_the_hard_ceilings()
    {
        var a = ChannelAccount.Create(Guid.NewGuid(), Guid.NewGuid(), "Sales", "sales-line", null, DateTime.UtcNow);
        a.SetLimits(30, 150, 30, DateTime.UtcNow);
        Assert.Throws<DomainException>(() => a.SetLimits(0, 150, 30, DateTime.UtcNow));
        Assert.Throws<DomainException>(() => a.SetLimits(ChannelAccount.HardMaxHourlyCap + 1, 500, 30, DateTime.UtcNow));
        Assert.Throws<DomainException>(() => a.SetLimits(30, ChannelAccount.HardMaxDailyCap + 1, 30, DateTime.UtcNow));
        Assert.Throws<DomainException>(() => a.SetLimits(100, 50, 30, DateTime.UtcNow));
        Assert.Throws<DomainException>(() => a.SetLimits(30, 150, ChannelAccount.HardMinDelaySeconds - 1, DateTime.UtcNow));
    }

    [Theory] [InlineData("sales-line", true)] [InlineData("abc", true)] [InlineData("A_b-9", true)] [InlineData("ab", false)] [InlineData("-abc", false)] [InlineData("a b c", false)] [InlineData("../x", false)] [InlineData("", false)] [InlineData(null, false)]
    public void Session_names_are_restricted(string? name, bool valid) => Assert.Equal(valid, ChannelAccount.IsValidSessionName(name));

    [Fact] public void The_webhook_key_is_random_unguessable_and_unique_per_account()
    {
        var keys = Enumerable.Range(0, 50).Select(_ => ChannelAccount.Create(Guid.NewGuid(), Guid.NewGuid(), "a", "session-1", null, DateTime.UtcNow).WebhookKey).ToList();
        Assert.Equal(50, keys.Distinct().Count()); Assert.All(keys, k => { Assert.Equal(48, k.Length); Assert.Matches("^[0-9a-f]{48}$", k); });
    }

    [Fact] public void Warm_up_starts_when_the_session_is_first_seen_working_and_never_restarts()
    {
        var a = ChannelAccount.Create(Guid.NewGuid(), Guid.NewGuid(), "a", "session-1", null, DateTime.UtcNow);
        Assert.Null(a.WarmupStartedAtUtc);
        var t1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        a.ApplyConnection(ConnectionStatus.ScanQrCode, null, null, null, t1); Assert.Null(a.WarmupStartedAtUtc);
        a.ApplyConnection(ConnectionStatus.Working, "+923001234567", "Bob", "GOWS", t1.AddMinutes(1)); Assert.Equal(t1.AddMinutes(1), a.WarmupStartedAtUtc);
        a.ApplyConnection(ConnectionStatus.Failed, null, null, null, t1.AddDays(1)); a.ApplyConnection(ConnectionStatus.Working, null, null, null, t1.AddDays(2)); Assert.Equal(t1.AddMinutes(1), a.WarmupStartedAtUtc);
    }

    [Fact] public void Pause_and_resume_track_who_and_why()
    {
        var a = ChannelAccount.Create(Guid.NewGuid(), Guid.NewGuid(), "a", "session-1", null, DateTime.UtcNow);
        a.RegisterFailure(DateTime.UtcNow); a.Pause("too many failures", automatic: true, DateTime.UtcNow);
        Assert.True(a.IsPaused && a.PausedAutomatically); Assert.Equal("too many failures", a.PauseReason);
        a.Resume(DateTime.UtcNow); Assert.False(a.IsPaused); Assert.Equal(0, a.ConsecutiveFailures); Assert.Null(a.PauseReason);
    }

    [Fact] public void The_service_window_follows_the_last_inbound_message()
    {
        var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        var c = Conversation.Create(Guid.NewGuid(), Guid.NewGuid(), "923001234567@c.us", "+923001234567", "Ali", now);
        Assert.False(c.IsInsideServiceWindow(now, TimeSpan.FromHours(24)));
        c.RegisterInbound(now.AddHours(-2), "hi", now); Assert.True(c.IsInsideServiceWindow(now, TimeSpan.FromHours(24)));
        Assert.Equal(1, c.UnreadCount); c.MarkRead(now); Assert.Equal(0, c.UnreadCount);
        c.RegisterInbound(now.AddHours(-30), "older", now); Assert.Equal(now.AddHours(-2), c.LastInboundAtUtc);   // an older (late-delivered) message never moves the window back
        Assert.Equal("hi", c.LastMessagePreview);
    }

    [Fact] public void A_message_must_have_text_within_the_limit()
    {
        Assert.Throws<DomainException>(() => Message.Outbound(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, MessagePurpose.Service, "  ", "k", "c", null, null, DateTime.UtcNow));
        Assert.Throws<DomainException>(() => Message.Outbound(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, MessagePurpose.Service, new string('x', Message.MaxBodyLength + 1), "k", "c", null, null, DateTime.UtcNow));
    }

    [Fact] public void A_webhook_event_purge_keeps_the_row_so_a_late_duplicate_is_still_recognised()
    {
        var w = WebhookEvent.Create(Guid.NewGuid(), Guid.NewGuid(), "evt_1", "message", "{\"big\":1}", "c", "1.2.3.4", DateTime.UtcNow, TimeSpan.FromDays(30));
        w.PurgePayload(); Assert.Equal("{}", w.PayloadJson); Assert.Equal("evt_1", w.ExternalEventId);
    }
}

public class MessageProcessorTests
{
    private sealed class StubHandler(Func<EventEnvelope, int, Task> body) : IMessagingEventHandler
    {
        public string ConsumerName => "messaging.inbound";
        public int Calls;
        public Task HandleAsync(EventEnvelope e, int attempt, CancellationToken ct) { Calls++; return body(e, attempt); }
    }

    private sealed class StubRunner(IServiceProvider services, Func<Guid, Exception?>? gate = null) : ITenantScopeRunner
    {
        public List<Guid> Scopes { get; } = [];
        public async Task RunAsync(Guid tenantId, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken ct = default)
        {
            if (tenantId == Guid.Empty) throw new TenantContextMissingException();
            if (gate?.Invoke(tenantId) is { } ex) throw ex;
            Scopes.Add(tenantId);
            using var scope = services.CreateScope();
            await work(scope.ServiceProvider, ct);
        }
        public Task RunPlatformJobAsync(Func<IServiceProvider, CancellationToken, Task> work, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RunForOperatorAsync(Guid tenantId, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static readonly ConsumerQueue Queue = new("q.inbound", "messaging.inbound", [MessagingEventTypes.MessageReceived]);
    private static readonly RabbitMqOptions Rmq = new() { RetryDelaysSeconds = [1, 2, 3] };   // max 4 attempts

    private static (MessageProcessor P, StubHandler H, StubRunner R) Build(Func<EventEnvelope, int, Task>? body = null, Func<Guid, Exception?>? gate = null)
    {
        var handler = new StubHandler(body ?? ((_, _) => Task.CompletedTask));
        var services = new ServiceCollection().AddSingleton<IMessagingEventHandler>(handler).BuildServiceProvider();
        var runner = new StubRunner(services, gate);
        return (new MessageProcessor(runner, Options.Create(Rmq), NullLogger<MessageProcessor>.Instance), handler, runner);
    }

    private static (byte[] Body, Dictionary<string, object?> Headers) Delivery(Guid tenant, Guid? envelopeTenant = null, string type = MessagingEventTypes.MessageReceived, object? tenantHeader = null, int attempt = 1, bool omitTenant = false)
    {
        var env = new EventEnvelope(Guid.NewGuid(), envelopeTenant ?? tenant, "corr", "cause", type, DateTime.UtcNow, 1, "{}");
        var h = new Dictionary<string, object?> { [MessagingTopology.AttemptHeader] = attempt };
        if (!omitTenant) h[MessagingTopology.TenantHeader] = tenantHeader ?? Encoding.UTF8.GetBytes(tenant.ToString());   // RabbitMQ delivers string headers as byte[]
        return (JsonSerializer.SerializeToUtf8Bytes(env), h);
    }

    [Fact] public async Task A_valid_message_runs_the_handler_inside_the_tenant_scope_and_is_acked()
    {
        var (p, h, r) = Build(); var tenant = Guid.NewGuid(); var (body, headers) = Delivery(tenant);
        var o = await p.ProcessAsync(Queue, body, headers, default);
        Assert.Equal(DeliveryAction.Ack, o.Action); Assert.Equal(1, h.Calls); Assert.Equal([tenant], r.Scopes);
    }

    [Fact] public async Task A_missing_tenant_header_is_dead_lettered_and_nothing_runs()
    {
        var (p, h, r) = Build(); var (body, headers) = Delivery(Guid.NewGuid(), omitTenant: true);
        var o = await p.ProcessAsync(Queue, body, headers, default);
        Assert.Equal(DeliveryAction.DeadLetter, o.Action); Assert.Contains("tenant", o.Reason); Assert.Equal(0, h.Calls); Assert.Empty(r.Scopes);
    }

    [Theory] [InlineData("not-a-guid")] [InlineData("")] [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_malformed_or_empty_tenant_header_is_dead_lettered(string header)
    {
        var (p, h, r) = Build(); var (body, headers) = Delivery(Guid.NewGuid(), tenantHeader: header);
        Assert.Equal(DeliveryAction.DeadLetter, (await p.ProcessAsync(Queue, body, headers, default)).Action); Assert.Equal(0, h.Calls); Assert.Empty(r.Scopes);
    }

    [Fact] public async Task A_header_that_disagrees_with_the_envelope_is_a_forgery_and_is_dead_lettered()
    {
        var (p, h, r) = Build(); var (body, headers) = Delivery(Guid.NewGuid(), envelopeTenant: Guid.NewGuid());
        var o = await p.ProcessAsync(Queue, body, headers, default);
        Assert.Equal(DeliveryAction.DeadLetter, o.Action); Assert.Contains("does_not_match", o.Reason); Assert.Equal(0, h.Calls); Assert.Empty(r.Scopes);
    }

    [Fact] public async Task A_garbled_envelope_is_dead_lettered()
    {
        var (p, h, _) = Build(); var headers = new Dictionary<string, object?> { [MessagingTopology.TenantHeader] = Guid.NewGuid().ToString() };
        Assert.Equal(DeliveryAction.DeadLetter, (await p.ProcessAsync(Queue, "{{{"u8.ToArray(), headers, default)).Action);
        Assert.Equal(DeliveryAction.DeadLetter, (await p.ProcessAsync(Queue, "null"u8.ToArray(), headers, default)).Action);
        Assert.Equal(0, h.Calls);
    }

    [Fact] public async Task An_event_type_the_queue_does_not_handle_is_dead_lettered()
    {
        var (p, h, _) = Build(); var (body, headers) = Delivery(Guid.NewGuid(), type: MessagingEventTypes.AccountStatusChanged);
        Assert.Equal(DeliveryAction.DeadLetter, (await p.ProcessAsync(Queue, body, headers, default)).Action); Assert.Equal(0, h.Calls);
    }

    [Fact] public async Task An_unknown_or_suspended_tenant_is_dead_lettered_not_retried_forever()
    {
        var (p, _, _) = Build(gate: _ => new TenantUnavailableException("Tenant does not exist."));
        var (body, headers) = Delivery(Guid.NewGuid());
        var o = await p.ProcessAsync(Queue, body, headers, default);
        Assert.Equal(DeliveryAction.DeadLetter, o.Action); Assert.Contains("tenant_unavailable", o.Reason);
    }

    [Fact] public async Task A_poison_message_goes_straight_to_the_dead_letter_queue() =>
        Assert.Equal(DeliveryAction.DeadLetter, (await Build((_, _) => throw new PoisonMessageException("message_not_found_for_tenant")).P.ProcessAsync(Queue, Delivery(Guid.NewGuid()).Body, Delivery(Guid.NewGuid()).Headers, default)).Action);

    [Fact] public async Task A_transient_failure_is_retried_with_a_higher_attempt_number_until_the_limit_then_dead_lettered()
    {
        var (p, _, _) = Build((_, _) => throw new InvalidOperationException("database busy")); var tenant = Guid.NewGuid();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var (body, headers) = Delivery(tenant, attempt: attempt);
            var o = await p.ProcessAsync(Queue, body, headers, default);
            Assert.Equal(DeliveryAction.Retry, o.Action); Assert.Equal(attempt + 1, o.NextAttempt);
        }
        var (b4, h4) = Delivery(tenant, attempt: 4);
        var last = await p.ProcessAsync(Queue, b4, h4, default);
        Assert.Equal(DeliveryAction.DeadLetter, last.Action); Assert.Contains("max_attempts_exceeded", last.Reason);
    }

    [Fact] public async Task The_handler_receives_the_attempt_number() { int seen = 0; var (p, _, _) = Build((_, a) => { seen = a; return Task.CompletedTask; }); var (b, h) = Delivery(Guid.NewGuid(), attempt: 3); await p.ProcessAsync(Queue, b, h, default); Assert.Equal(3, seen); }

    [Fact] public async Task Cancellation_is_not_turned_into_a_retry_or_a_dead_letter()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var (p, _, _) = Build((_, _) => throw new OperationCanceledException(cts.Token)); var (b, h) = Delivery(Guid.NewGuid());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.ProcessAsync(Queue, b, h, cts.Token));
    }

    [Fact] public void Header_values_arrive_as_bytes_strings_or_numbers_and_all_are_understood()
    {
        var h = new Dictionary<string, object?> { ["a"] = Encoding.UTF8.GetBytes("x"), ["b"] = "y", ["c"] = 5, ["d"] = 6L, ["e"] = Encoding.UTF8.GetBytes("7"), ["f"] = null };
        Assert.Equal("x", MessageProcessor.HeaderString(h, "a")); Assert.Equal("y", MessageProcessor.HeaderString(h, "b")); Assert.Null(MessageProcessor.HeaderString(h, "f")); Assert.Null(MessageProcessor.HeaderString(h, "zz")); Assert.Null(MessageProcessor.HeaderString(null, "a"));
        Assert.Equal(5, MessageProcessor.HeaderInt(h, "c")); Assert.Equal(6, MessageProcessor.HeaderInt(h, "d")); Assert.Equal(7, MessageProcessor.HeaderInt(h, "e")); Assert.Null(MessageProcessor.HeaderInt(h, "a"));
    }

    [Fact] public void The_retry_queues_are_one_per_backoff_step_and_the_attempt_limit_follows_them() { Assert.Equal(4, Rmq.MaxAttempts); Assert.Equal("q.inbound.retry.2", Queue.Retry(2)); Assert.Equal("q.inbound.dlq", Queue.Dlq); }
}

public class RabbitMqOptionsTests
{
    [Fact] public void Defaults_apply_when_nothing_is_configured() { var o = new RabbitMqOptions(); Assert.Equal([5, 30, 120, 600], o.RetryDelays); Assert.Equal(5, o.MaxAttempts); Assert.False(o.IsConfigured); }

    [Fact]
    public void Configured_retry_delays_replace_the_defaults_instead_of_being_appended_to_them()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["RabbitMq:Host"] = "h", ["RabbitMq:RetryDelaysSeconds:0"] = "1", ["RabbitMq:RetryDelaysSeconds:1"] = "2", ["RabbitMq:RetryDelaysSeconds:2"] = "3" }).Build();
        var o = cfg.GetSection(RabbitMqOptions.Section).Get<RabbitMqOptions>()!;
        Assert.Equal([1, 2, 3], o.RetryDelays); Assert.Equal(4, o.MaxAttempts); Assert.True(o.IsConfigured);   // regression: binding used to append to a non-empty default → [5,30,120,600,1,2,3]
    }

    [Fact] public void Queue_names_follow_the_prefix_and_every_consumer_queue_has_a_dlq() { var o = new RabbitMqOptions { Prefix = "x" }; Assert.All(MessagingTopology.Queues(o), q => { Assert.StartsWith("x.", q.Name); Assert.Equal(q.Name + ".dlq", q.Dlq); }); Assert.Equal("x", o.Exchange); }
}
