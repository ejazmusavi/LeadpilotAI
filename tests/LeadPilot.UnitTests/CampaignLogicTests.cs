using LeadPilot.Application.Campaigns;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;

namespace LeadPilot.UnitTests;

public class CampaignStateMachineTests
{
    public static IEnumerable<object[]> Allowed => new[]
    {
        (CampaignStatus.Draft, CampaignStatus.PendingApproval), (CampaignStatus.Draft, CampaignStatus.Scheduled), (CampaignStatus.Draft, CampaignStatus.Cancelled),
        (CampaignStatus.PendingApproval, CampaignStatus.Scheduled), (CampaignStatus.PendingApproval, CampaignStatus.Draft), (CampaignStatus.PendingApproval, CampaignStatus.Cancelled),
        (CampaignStatus.Scheduled, CampaignStatus.Running), (CampaignStatus.Scheduled, CampaignStatus.Draft), (CampaignStatus.Scheduled, CampaignStatus.Cancelled),
        (CampaignStatus.Running, CampaignStatus.Paused), (CampaignStatus.Running, CampaignStatus.Completed), (CampaignStatus.Running, CampaignStatus.Cancelled), (CampaignStatus.Running, CampaignStatus.Failed),
        (CampaignStatus.Paused, CampaignStatus.Running), (CampaignStatus.Paused, CampaignStatus.Cancelled),
    }.Select(t => new object[] { t.Item1, t.Item2 });

    [Theory, MemberData(nameof(Allowed))]
    public void Allowed_transitions_work(CampaignStatus from, CampaignStatus to) => Assert.True(CampaignStateMachine.CanTransition(from, to));

    [Fact]
    public void Every_other_transition_is_refused()
    {
        var allowed = Allowed.Select(a => ((CampaignStatus)a[0], (CampaignStatus)a[1])).ToHashSet();
        foreach (var from in Enum.GetValues<CampaignStatus>())
            foreach (var to in Enum.GetValues<CampaignStatus>())
                if (!allowed.Contains((from, to))) { Assert.False(CampaignStateMachine.CanTransition(from, to), $"{from}->{to}"); Assert.Throws<DomainException>(() => CampaignStateMachine.Ensure(from, to)); }
    }

    [Theory]
    [InlineData(CampaignStatus.Completed)] [InlineData(CampaignStatus.Cancelled)] [InlineData(CampaignStatus.Failed)]
    public void Terminal_states_have_no_exit(CampaignStatus s) { Assert.True(CampaignStateMachine.IsTerminal(s)); Assert.Empty(CampaignStateMachine.Allowed(s)); }

    [Fact] public void Only_running_sends() => Assert.Equal([CampaignStatus.Running], Enum.GetValues<CampaignStatus>().Where(CampaignStateMachine.IsSending));

    private static Campaign NewCampaign(CampaignType type = CampaignType.Manual) => Campaign.Create(Guid.NewGuid(), "C", type, Guid.NewGuid(), "a@b.c", DateTime.UtcNow);

    [Fact]
    public void Entity_follows_the_machine_and_four_eyes_rule()
    {
        var creator = Guid.NewGuid(); var other = Guid.NewGuid(); var now = DateTime.UtcNow;
        var c = Campaign.Create(Guid.NewGuid(), "C", CampaignType.Manual, creator, null, now);
        c.Submit(requiresApproval: true, creator, now);
        Assert.Equal(CampaignStatus.PendingApproval, c.Status);
        Assert.Throws<DomainException>(() => c.Approve(creator, creatorCannotApprove: true, now));
        c.Approve(creator, creatorCannotApprove: false, now);                       // policy off: allowed
        Assert.Equal(CampaignStatus.Scheduled, c.Status);

        var d = Campaign.Create(Guid.NewGuid(), "D", CampaignType.Manual, creator, null, now);
        d.Submit(true, other, now);
        Assert.Throws<DomainException>(() => d.Approve(creator, true, now));        // the creator even when somebody else submitted
        d.Approve(Guid.NewGuid(), true, now);
        Assert.Equal(CampaignStatus.Scheduled, d.Status);
    }

    [Fact]
    public void Pause_resume_cancel_rules()
    {
        var c = NewCampaign(); var now = DateTime.UtcNow;
        Assert.Throws<DomainException>(() => c.Pause("x", false, now));              // draft cannot pause
        c.Submit(false, null, now);
        Assert.Throws<DomainException>(() => c.Resume(now));
        c.Cancel(null, now);
        Assert.Throws<DomainException>(() => c.Cancel(null, now));
        Assert.Throws<DomainException>(() => c.Submit(false, null, now));
    }

    [Fact]
    public void Only_drafts_are_editable()
    {
        var c = NewCampaign(); var now = DateTime.UtcNow;
        var draft = new CampaignDraft("N", null, CampaignType.Manual, null, null, "{}", 1, null, null, null, null, true, 10, 60, 120, 100, null, null, null);
        c.UpdateDraft(draft, now, null);
        c.Submit(false, null, now);
        Assert.Throws<DomainException>(() => c.UpdateDraft(draft, now, null));
    }

    [Theory]
    [InlineData(0, 60, 120, 100)] [InlineData(101, 60, 120, 100)] [InlineData(10, 5, 120, 100)] [InlineData(10, 60, 30, 100)] [InlineData(10, 60, 120, 0)] [InlineData(10, 60, 120, 5001)]
    public void Limits_are_bounded(int batch, int min, int max, int recipients) =>
        Assert.Throws<DomainException>(() => NewCampaign().SetLimits(batch, min, max, recipients, null, null, null));

    [Fact] public void Follow_up_needs_a_parent() => Assert.Throws<DomainException>(() =>
        NewCampaign(CampaignType.FollowUp).UpdateDraft(new CampaignDraft("N", null, CampaignType.FollowUp, null, null, "{}", 1, null, 3, null, null, true, 10, 60, 120, 100, null, null, null), DateTime.UtcNow, null));
}

public class RecipientSyncTests
{
    private static CampaignRecipient Queued()
    {
        var r = CampaignRecipient.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "+923001234567", "{}", DateTime.UtcNow);
        r.Skip("x", "x", null, DateTime.UtcNow);    // not queued: make a fresh one below
        return CampaignRecipient.Create(r.TenantId, r.CampaignId, r.SnapshotId, Guid.NewGuid(), Guid.NewGuid(), 1, "+923001234567", "{}", DateTime.UtcNow);
    }

    private static CampaignRecipient AsQueued()
    {
        // recipients become Queued through guarded SQL in the runner; here the sync rules start from a recipient we move to Queued via the rules' own entry point
        var r = Queued();
        typeof(CampaignRecipient).GetProperty(nameof(CampaignRecipient.Status))!.SetValue(r, RecipientStatus.Queued);
        return r;
    }

    [Fact]
    public void Delivery_only_moves_forward_and_a_reply_is_never_undone()
    {
        var now = DateTime.UtcNow; var r = AsQueued();
        Assert.Equal(SyncResult.Changed, r.SyncFromMessage(MessageStatus.Sent, now, null, null, null, null, now));
        Assert.Equal(RecipientStatus.Sent, r.Status);
        r.SyncFromMessage(MessageStatus.Read, now, now, now, null, null, now);
        Assert.Equal(RecipientStatus.Read, r.Status);
        r.SyncFromMessage(MessageStatus.Delivered, now, now, null, null, null, now);                 // a late lower ack
        Assert.Equal(RecipientStatus.Read, r.Status);
        Assert.True(r.MarkReplied(now, now));
        r.SyncFromMessage(MessageStatus.Read, now, now, now, null, null, now);
        Assert.Equal(RecipientStatus.Replied, r.Status);
        Assert.False(r.MarkReplied(now, now));
    }

    [Fact] public void A_reply_needs_a_message_that_reached_the_gateway() => Assert.False(AsQueued().MarkReplied(DateTime.UtcNow, DateTime.UtcNow));

    [Theory]
    [InlineData("campaign_paused", RecipientStatus.Pending)]
    [InlineData("campaign_cancelled", RecipientStatus.Cancelled)]
    [InlineData("campaign_kill_switch", RecipientStatus.Pending)]
    [InlineData("suppressed", RecipientStatus.Skipped)]
    [InlineData("opted_out", RecipientStatus.Skipped)]
    [InlineData("not_eligible", RecipientStatus.Skipped)]
    [InlineData("delivery_unconfirmed", RecipientStatus.Failed)]
    [InlineData("rejected", RecipientStatus.Failed)]
    public void A_failed_message_maps_to_the_right_recipient_state(string code, RecipientStatus expected)
    {
        var r = AsQueued(); var now = DateTime.UtcNow;
        r.SyncFromMessage(MessageStatus.Failed, null, null, null, code, "detail", now);
        Assert.Equal(expected, r.Status);
    }

    [Fact]
    public void A_pause_withdrawal_gives_the_next_message_a_new_idempotency_key()
    {
        var r = AsQueued(); var first = r.IdempotencyKey;
        r.SyncFromMessage(MessageStatus.Failed, null, null, null, "campaign_paused", null, DateTime.UtcNow);
        Assert.Equal(RecipientStatus.Pending, r.Status); Assert.Equal(1, r.Round); Assert.NotEqual(first, r.IdempotencyKey); Assert.Null(r.MessageId);
    }

    [Fact]
    public void Idempotency_keys_are_deterministic_per_campaign_recipient_and_round()
    {
        var c = Guid.NewGuid(); var r = Guid.NewGuid();
        Assert.Equal(CampaignKeys.ForRecipient(c, r, 0), CampaignKeys.ForRecipient(c, r, 0));
        Assert.NotEqual(CampaignKeys.ForRecipient(c, r, 0), CampaignKeys.ForRecipient(c, r, 1));
        Assert.NotEqual(CampaignKeys.ForRecipient(c, r, 0), CampaignKeys.ForRecipient(c, Guid.NewGuid(), 0));
        Assert.NotEqual(CampaignKeys.ForRecipient(c, r, 0), CampaignKeys.ForRecipient(Guid.NewGuid(), r, 0));
        Assert.True(CampaignKeys.ForRecipient(c, r, 12).Length <= IdempotencyKeys.MaxLength);
        Assert.True(CampaignKeys.IsCampaignKey(CampaignKeys.ForRecipient(c, r, 0)));
    }

    [Fact] public void Skipped_recipients_ignore_message_events() { var r = AsQueued(); r.Skip("suppressed", "x", null, DateTime.UtcNow); Assert.Equal(SyncResult.None, r.SyncFromMessage(MessageStatus.Sent, DateTime.UtcNow, null, null, null, null, DateTime.UtcNow)); Assert.Equal(RecipientStatus.Skipped, r.Status); }
}

public class AudienceDefinitionTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a"); private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    [Fact]
    public void Normalisation_dedupes_trims_sorts_and_is_canonical()
    {
        var d1 = new AudienceDefinition { StageIds = [B, A, B, Guid.Empty], Cities = [" Lahore ", "lahore", "", "Karachi"] };
        var d2 = new AudienceDefinition { StageIds = [A, B], Cities = ["Karachi", "Lahore"] };
        Assert.Equal(AudienceDefinitions.Serialize(d2), AudienceDefinitions.Serialize(d1));
        var n = AudienceDefinitions.Normalize(d1);
        Assert.Equal([A, B], n.StageIds); Assert.Equal(2, n.Cities.Count);
    }

    [Fact]
    public void Round_trips_and_is_versioned()
    {
        var d = new AudienceDefinition { MinScore = 40, MaxScore = 90, HasReply = false, SourceTypes = [LeadSourceType.Import], FollowUp = new FollowUpSpec(A, FollowUpRule.ReadNotReplied, 5) };
        var json = AudienceDefinitions.Serialize(d);
        Assert.Contains("\"version\":1", json);
        var (back, err) = AudienceDefinitions.Parse(json);
        Assert.Null(err); Assert.Equal(40, back!.MinScore); Assert.Equal(FollowUpRule.ReadNotReplied, back.FollowUp!.Rule); Assert.Equal(false, back.HasReply);
    }

    [Theory] [InlineData("{\"version\":2}")] [InlineData("not json")] [InlineData("{\"minScore\":\"abc\"}")] [InlineData("[]")]
    public void Hostile_or_foreign_json_gives_an_error_not_an_exception(string json) { var (d, err) = AudienceDefinitions.Parse(json); Assert.Null(d); Assert.NotNull(err); }

    [Fact] public void Oversized_json_is_refused() => Assert.NotNull(AudienceDefinitions.Parse("{\"cities\":[\"" + new string('a', 70_000) + "\"]}").Error);

    [Fact] public void Lists_are_capped() => Assert.Equal(AudienceDefinition.MaxListItems, AudienceDefinitions.Normalize(new AudienceDefinition { TagIds = Enumerable.Range(1, 1000).Select(i => new Guid(i, 0, 0, new byte[8])).ToList() }).TagIds.Count);

    [Fact] public void A_campaign_needs_a_filter() => Assert.Contains(AudienceDefinitions.Validate(new AudienceDefinition(), CampaignType.Manual), m => m.Contains("at least one filter"));
    [Fact] public void Score_range_must_be_sane() => Assert.NotEmpty(AudienceDefinitions.Validate(new AudienceDefinition { MinScore = 80, MaxScore = 20 }, CampaignType.Manual));
    [Fact] public void Contradictions_are_reported()
    {
        Assert.NotEmpty(AudienceDefinitions.Validate(new AudienceDefinition { TagIds = [A], ExcludeTagIds = [A] }, CampaignType.Manual));
        Assert.NotEmpty(AudienceDefinitions.Validate(new AudienceDefinition { Unassigned = true, OwnerUserIds = [A], MinScore = 1 }, CampaignType.Manual));
        Assert.NotEmpty(AudienceDefinitions.Validate(new AudienceDefinition { NeverContacted = true, LastContactedBeforeDays = 3 }, CampaignType.Manual));
    }
    [Fact] public void Follow_up_rules_are_validated()
    {
        Assert.NotEmpty(AudienceDefinitions.Validate(new AudienceDefinition(), CampaignType.FollowUp));
        Assert.NotEmpty(AudienceDefinitions.Validate(new AudienceDefinition { FollowUp = new FollowUpSpec(A, FollowUpRule.RepliedNotAdvanced, 2) }, CampaignType.FollowUp));   // needs advanced stages
        Assert.Empty(AudienceDefinitions.Validate(new AudienceDefinition { FollowUp = new FollowUpSpec(A, FollowUpRule.RepliedNotAdvanced, 2, [B]) }, CampaignType.FollowUp));
        Assert.NotEmpty(AudienceDefinitions.Validate(new AudienceDefinition { MinScore = 1, FollowUp = new FollowUpSpec(A, FollowUpRule.SentNotReplied, 2) }, CampaignType.Manual));   // parent only for follow-up types
        Assert.NotEmpty(AudienceDefinitions.Validate(new AudienceDefinition { MinScore = 1 }, CampaignType.ReEngagement));
        Assert.Empty(AudienceDefinitions.Validate(new AudienceDefinition { LastContactedBeforeDays = 60 }, CampaignType.ReEngagement));
    }
}

public class ExclusionReasonTests
{
    [Fact] public void Primary_reason_follows_the_precedence_so_the_breakdown_adds_up() =>
        Assert.Equal(ExclusionReasons.Suppressed, ExclusionReasons.Primary([EligibilityReason.NoConsent, EligibilityReason.Suppressed, EligibilityReason.LeadArchived]));

    [Theory]
    [InlineData(EligibilityReason.NoConsent, ExclusionReasons.NoConsent)] [InlineData(EligibilityReason.ConsentRevoked, ExclusionReasons.ConsentRevoked)] [InlineData(EligibilityReason.Suppressed, ExclusionReasons.Suppressed)]
    [InlineData(EligibilityReason.InvalidContactValue, ExclusionReasons.InvalidContact)] [InlineData(EligibilityReason.ContactRemoved, ExclusionReasons.ContactRemoved)] [InlineData(EligibilityReason.LeadArchived, ExclusionReasons.LeadArchived)] [InlineData(EligibilityReason.LeadExcluded, ExclusionReasons.LeadExcluded)]
    public void Every_eligibility_reason_has_a_code_and_an_explanation(EligibilityReason r, string code)
    {
        Assert.Equal(code, ExclusionReasons.Code(r));
        Assert.NotEqual(code, ExclusionReasons.Explain(code));
    }

    [Fact] public void A_high_score_cannot_change_the_decision() =>
        Assert.False(EligibilityRules.Evaluate(new EligibilityFacts(true, false, false, false, "+923001234567", "+923001234567", Suppressed: false, null)).IsEligible);   // no consent, whatever the lead's score

    [Fact]
    public void Eligibility_trail_keeps_both_decisions()
    {
        var sel = new EligibilityRecord(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), true, []);
        var send = new EligibilityRecord(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), false, [ExclusionReasons.ConsentRevoked]);
        var parsed = EligibilityTrail.Parse(EligibilityTrail.Empty.WithSelection(sel).WithSend(send).ToJson());
        Assert.True(parsed.Selection!.Eligible); Assert.False(parsed.Send!.Eligible); Assert.Equal([ExclusionReasons.ConsentRevoked], parsed.Send.Reasons);
        Assert.Equal(EligibilityTrail.Empty, EligibilityTrail.Parse("garbage"));
    }
}

public class CampaignContentTests
{
    private static Dictionary<string, string> Values(string? name = "Ayesha Khan", string? lead = "Al Noor School", string? city = "Lahore") => CampaignValues.For(name, lead, "+923001234567", city, "Pakistan");

    [Fact] public void Renders_known_variables_and_appends_the_footer() =>
        Assert.Equal("Hi Ayesha, from Al Noor School in Lahore\n\nReply STOP to opt out.", CampaignContent.Build("Hi {{first_name}}, from {{business_name}} in {{city}}", "Reply STOP to opt out.", Values()).Text);

    [Fact] public void Missing_value_without_fallback_is_reported_and_nothing_is_rendered()
    {
        var b = CampaignContent.Build("Hi {{first_name}}", "Reply STOP", Values(name: null));
        Assert.Equal(["first_name"], b.Missing); Assert.Equal("", b.Text);
    }

    [Fact] public void Inline_fallback_covers_a_missing_value() => Assert.Equal("Hi there\n\nSTOP to opt out", CampaignContent.Build("Hi {{first_name|there}}", "STOP to opt out", Values(name: null)).Text);
    [Fact] public void A_blank_value_counts_as_missing() => Assert.NotEmpty(CampaignContent.Build("{{city}}", null, Values(city: "   ")).Missing);

    [Fact]
    public void Hostile_values_cannot_inject_placeholders_formatting_or_control_characters()
    {
        var b = CampaignContent.Build("Hello {{name}}", null, Values(name: "{{phone}} *bold* _it_ ~x~ `code` ‮ evil\r\nnew line​"));
        Assert.DoesNotContain("{{", b.Text); Assert.DoesNotContain("*", b.Text); Assert.DoesNotContain("_", b.Text); Assert.DoesNotContain("`", b.Text);
        Assert.DoesNotContain('‮', b.Text); Assert.DoesNotContain('​', b.Text); Assert.DoesNotContain('\r', b.Text);
        Assert.Single(b.Text.Split('\n'));
    }

    [Fact] public void Values_are_length_limited() => Assert.True(CampaignContent.Build("{{name}}", null, Values(name: new string('x', 10_000))).Text.Length <= TemplateRenderer.MaxValueLength);

    [Fact] public void Expressions_are_not_placeholders_and_are_never_evaluated()
    {
        Assert.NotEmpty(CampaignContent.Validate("Total {{ 7*7 }}", null));            // rejected at save time
        Assert.NotEmpty(CampaignContent.Validate("{{name.ToUpper()}}", null));
        Assert.Equal("Hi 77", CampaignContent.Build("Hi {{name}}", null, Values(name: "7*7")).Text);   // a value is text, only text
    }

    [Theory]
    [InlineData("Hi {{nmae}}")] [InlineData("Hi {{ name }")] [InlineData("Hi {name}")] [InlineData("Hi {{name")] [InlineData("Hi {{na me}}")] [InlineData("Hi {{name|a|b}}")]
    public void Broken_or_unknown_placeholders_are_rejected_when_the_template_is_validated(string body) => Assert.NotEmpty(CampaignContent.Validate(body, "Reply STOP"));

    [Fact] public void A_good_template_validates() => Assert.Empty(CampaignContent.Validate("Hi {{first_name|there}}, we help {{business_name}}.", "Reply STOP to opt out."));
    [Fact] public void Empty_and_oversized_bodies_are_refused() { Assert.NotEmpty(CampaignContent.Validate("  ", null)); Assert.NotEmpty(CampaignContent.Validate(new string('a', 4000), "Reply STOP")); }
    [Fact] public void The_footer_must_contain_an_opt_out_word_the_detector_knows()
    {
        Assert.True(CampaignContent.FooterContainsOptOutKeyword("Reply STOP to opt out.", OptOutDetector.DefaultKeywords));
        Assert.True(CampaignContent.FooterContainsOptOutKeyword("Band karo likh kar bhejein", OptOutDetector.DefaultKeywords));
        Assert.False(CampaignContent.FooterContainsOptOutKeyword("Thanks for reading", OptOutDetector.DefaultKeywords));
        Assert.False(CampaignContent.FooterContainsOptOutKeyword("Do not stopover", OptOutDetector.DefaultKeywords));
    }
    [Fact] public void Test_sends_are_labelled() => Assert.StartsWith(TestSendRules.Label, TestSendRules.Decorate("x"));
    [Fact] public void Test_numbers_must_be_on_the_allow_list() { Assert.True(TestSendRules.IsAllowed("+923001234567", ["+923001234567"])); Assert.False(TestSendRules.IsAllowed("+923001234568", ["+923001234567"])); Assert.False(TestSendRules.IsAllowed(null, ["+923001234567"])); }
}

public class CampaignPacingTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static SendRateFacts Facts(int hourly = 30, int daily = 150, int sentHour = 0, int sent24 = 0, DateTime? warm = null) => new(hourly, daily, 30, warm ?? Now.AddDays(-30), null, sentHour, sent24, null, null);

    [Fact] public void Delay_is_within_the_bounds_and_random() { Assert.Equal(60, CampaignPacing.NextBatchDelay(60, 180, 0).TotalSeconds); Assert.Equal(180, CampaignPacing.NextBatchDelay(60, 180, 1).TotalSeconds); Assert.Equal(120, CampaignPacing.NextBatchDelay(60, 180, 0.5).TotalSeconds); Assert.Equal(60, CampaignPacing.NextBatchDelay(60, 180, -3).TotalSeconds); Assert.Equal(180, CampaignPacing.NextBatchDelay(60, 180, 9).TotalSeconds); Assert.Equal(90, CampaignPacing.NextBatchDelay(90, 30, 0.7).TotalSeconds); }
    [Fact] public void Batch_is_limited_by_size_and_pending() { Assert.Equal(10, CampaignPacing.BatchAllowance(10, 500, Facts(), 0, Now, null, 0, null)); Assert.Equal(3, CampaignPacing.BatchAllowance(10, 3, Facts(), 0, Now, null, 0, null)); }
    [Fact] public void Hourly_cap_and_queue_reduce_the_batch() => Assert.Equal(2, CampaignPacing.BatchAllowance(10, 500, Facts(hourly: 30, sentHour: 25), 3, Now, null, 0, null));
    [Fact] public void Daily_cap_with_warmup_applies() => Assert.Equal(2, CampaignPacing.BatchAllowance(10, 500, Facts(hourly: 120, daily: 150, sent24: 18, warm: Now), 0, Now, null, 0, null));   // day one allows 20
    [Fact] public void Warmup_day_one_allows_twenty() => Assert.Equal(SendRatePolicy.WarmupFirstDay, SendRatePolicy.DailyLimitToday(150, Now, Now));
    [Fact] public void Warm_account_remaining_day_is_counted() => Assert.Equal(4, CampaignPacing.BatchAllowance(10, 500, Facts(hourly: 120, daily: 150, sent24: 146), 0, Now, null, 0, null));
    [Fact] public void Campaign_daily_limit_and_monthly_allowance_apply() { Assert.Equal(1, CampaignPacing.BatchAllowance(10, 500, Facts(), 0, Now, 20, 19, null)); Assert.Equal(0, CampaignPacing.BatchAllowance(10, 500, Facts(), 0, Now, 20, 25, null)); Assert.Equal(2, CampaignPacing.BatchAllowance(10, 500, Facts(), 0, Now, null, 0, 2)); Assert.Equal(0, CampaignPacing.BatchAllowance(10, 500, Facts(), 0, Now, null, 0, -5)); }
    [Fact] public void Never_negative() => Assert.Equal(0, CampaignPacing.BatchAllowance(10, 500, Facts(sentHour: 99, sent24: 999), 50, Now, null, 0, null));
    [Fact] public void Retry_time_comes_from_the_rate_policy() { Assert.Null(CampaignPacing.AccountRetryAt(Facts(), Now)); Assert.NotNull(CampaignPacing.AccountRetryAt(Facts(hourly: 5, sentHour: 5), Now)); }
    [Fact] public void In_flight_ceiling_scales_with_the_batch() { Assert.Equal(20, CampaignPacing.MaxInFlight(10)); Assert.Equal(2, CampaignPacing.MaxInFlight(1)); }
}

public class FailureRateTests
{
    [Theory]
    [InlineData(3, 10, 30, 10, true)] [InlineData(2, 10, 30, 10, false)] [InlineData(3, 9, 30, 10, false)] [InlineData(0, 100, 1, 10, false)] [InlineData(10, 10, 100, 10, true)] [InlineData(1, 1, 50, 1, true)]
    public void Pauses_only_with_enough_samples_at_the_threshold(int failed, int attempted, int threshold, int min, bool pause) => Assert.Equal(pause, FailureRatePolicy.ShouldPause(failed, attempted, threshold, min));
    [Fact] public void Percent_is_rounded_and_safe() { Assert.Equal(0, FailureRatePolicy.Percent(0, 0)); Assert.Equal(33.3, FailureRatePolicy.Percent(1, 3)); }
}

public class AttributionTests
{
    private static readonly DateTime T = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc); private static readonly TimeSpan W = TimeSpan.FromDays(7);
    [Fact] public void Inside_the_window_counts() { Assert.True(AttributionRules.IsInside(T, T.AddDays(7), W)); Assert.True(AttributionRules.IsInside(T, T, W)); }
    [Fact] public void Outside_the_window_or_before_the_message_does_not() { Assert.False(AttributionRules.IsInside(T, T.AddDays(7).AddSeconds(1), W)); Assert.False(AttributionRules.IsInside(T, T.AddSeconds(-1), W)); }
    [Fact] public void Last_touch_wins_and_old_messages_are_ignored()
    {
        var a = new AttributionCandidate(Guid.NewGuid(), Guid.NewGuid(), T); var b = new AttributionCandidate(Guid.NewGuid(), Guid.NewGuid(), T.AddDays(2)); var old = new AttributionCandidate(Guid.NewGuid(), Guid.NewGuid(), T.AddDays(-30));
        Assert.Equal(b, AttributionRules.Pick([a, b, old], T.AddDays(3), W));
        Assert.Equal(a, AttributionRules.Pick([a, b], T.AddDays(1), W));          // b was sent after the event
        Assert.Null(AttributionRules.Pick([old], T, W)); Assert.Null(AttributionRules.Pick([], T, W));
    }
}

public class FollowUpRuleTests
{
    private static readonly DateTime Now = new(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);
    [Fact] public void Delivered_not_replied_after_n_days()
    {
        var d = Now.AddDays(-4);
        Assert.True(FollowUpRules.Matches(FollowUpRule.DeliveredNotReplied, RecipientStatus.Delivered, d, d, null, null, false, 3, Now));
        Assert.True(FollowUpRules.Matches(FollowUpRule.DeliveredNotReplied, RecipientStatus.Read, d, d, d, null, false, 3, Now));
        Assert.False(FollowUpRules.Matches(FollowUpRule.DeliveredNotReplied, RecipientStatus.Delivered, d, Now.AddDays(-2), null, null, false, 3, Now));   // too recent
        Assert.False(FollowUpRules.Matches(FollowUpRule.DeliveredNotReplied, RecipientStatus.Replied, d, d, d, d, false, 3, Now));                          // replied
        Assert.False(FollowUpRules.Matches(FollowUpRule.DeliveredNotReplied, RecipientStatus.Sent, d, null, null, null, false, 3, Now));                    // never delivered
    }
    [Fact] public void Read_and_sent_rules()
    {
        var d = Now.AddDays(-4);
        Assert.True(FollowUpRules.Matches(FollowUpRule.ReadNotReplied, RecipientStatus.Read, d, d, d, null, false, 3, Now));
        Assert.False(FollowUpRules.Matches(FollowUpRule.ReadNotReplied, RecipientStatus.Delivered, d, d, null, null, false, 3, Now));
        Assert.True(FollowUpRules.Matches(FollowUpRule.SentNotReplied, RecipientStatus.Sent, d, null, null, null, false, 3, Now));
        Assert.False(FollowUpRules.Matches(FollowUpRule.SentNotReplied, RecipientStatus.Failed, d, null, null, null, false, 3, Now));
    }
    [Fact] public void Replied_but_not_advanced()
    {
        var d = Now.AddDays(-5);
        Assert.True(FollowUpRules.Matches(FollowUpRule.RepliedNotAdvanced, RecipientStatus.Replied, d, d, d, d, leadInAdvancedStage: false, 3, Now));
        Assert.False(FollowUpRules.Matches(FollowUpRule.RepliedNotAdvanced, RecipientStatus.Replied, d, d, d, d, leadInAdvancedStage: true, 3, Now));
        Assert.False(FollowUpRules.Matches(FollowUpRule.RepliedNotAdvanced, RecipientStatus.Read, d, d, d, null, false, 3, Now));
    }
    [Fact] public void A_follow_up_waiting_for_its_parent_starts_after_completion_plus_delay()
    {
        Assert.Null(FollowUpRules.StartAfterParent(null, 3));
        Assert.Equal(Now.AddDays(3), FollowUpRules.StartAfterParent(Now, 3)); Assert.Equal(Now, FollowUpRules.StartAfterParent(Now, null));
    }
}

public class TemplateRendererCampaignTests
{
    [Fact] public void Fallback_syntax_is_understood_by_the_shared_renderer() => Assert.Equal("Hi there", TemplateRenderer.Render("Hi {{first_name|there}}", new Dictionary<string, string>()));
    [Fact] public void Fallback_is_not_used_when_a_value_exists() => Assert.Equal("Hi Sara", TemplateRenderer.Render("Hi {{first_name|there}}", new Dictionary<string, string> { ["first_name"] = "Sara" }));
    [Fact] public void New_variables_are_known() => Assert.Empty(TemplateRenderer.UnknownVariables("{{city}} {{country}} {{name}}"));
}
