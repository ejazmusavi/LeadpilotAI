using System.Text;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Campaigns;

namespace LeadPilot.Application.Campaigns;

/// <summary>
/// Pacing of the campaign runner. The WhatsApp account's own limits (minimum gap, hourly/daily caps, warm-up: <see cref="SendRatePolicy"/>) always apply on top.
/// The random pause between batches exists to avoid a mechanical rhythm that WhatsApp's automation detection reacts to; it is a risk reduction for an unofficial gateway, NOT a way
/// to hide unwanted messages: consent, suppression and caps are enforced regardless.
/// </summary>
public static class CampaignPacing
{
    /// <summary>Uniform random delay in [min, max] seconds; <paramref name="random01"/> is injected so it is testable.</summary>
    public static TimeSpan NextBatchDelay(int minSeconds, int maxSeconds, double random01)
    {
        var lo = Math.Max(1, minSeconds); var hi = Math.Max(lo, maxSeconds);
        return TimeSpan.FromSeconds(Math.Round(lo + (hi - lo) * Math.Clamp(random01, 0, 1)));
    }

    /// <summary>
    /// How many recipients the next batch may queue: the batch size, the recipients still waiting, the room left on the account (hour, day incl. warm-up, minus what is already queued),
    /// the campaign's own daily limit and the room left in the workspace's monthly allowance. Never negative.
    /// </summary>
    public static int BatchAllowance(int batchSize, int pendingRecipients, SendRateFacts account, int accountQueued, DateTime nowUtc, int? campaignDailyLimit, int campaignQueuedLast24h, int? monthlyRemaining)
    {
        var dailyLeft = SendRatePolicy.DailyLimitToday(account.DailyCap, account.WarmupStartedAtUtc, nowUtc) - account.SentLast24Hours - accountQueued;
        var hourLeft = account.HourlyCap - account.SentLastHour - accountQueued;
        var n = Math.Min(batchSize, pendingRecipients);
        n = Math.Min(n, Math.Min(dailyLeft, hourLeft));
        if (campaignDailyLimit is { } dl) n = Math.Min(n, dl - campaignQueuedLast24h);
        if (monthlyRemaining is { } mr) n = Math.Min(n, mr);
        return Math.Max(0, n);
    }

    /// <summary>When the account has no room, the earliest time a retry makes sense (the rate policy's own answer), else null.</summary>
    public static DateTime? AccountRetryAt(SendRateFacts account, DateTime nowUtc)
    {
        var d = SendRatePolicy.Evaluate(account, nowUtc);
        return d.CanSendNow ? null : d.RetryAtUtc;
    }

    /// <summary>Messages allowed in flight (queued, sending, unknown) before the runner stops adding more: the next batch only starts once the previous one has drained mostly.</summary>
    public static int MaxInFlight(int batchSize) => Math.Max(2, batchSize * 2);
}

/// <summary>Pause the campaign when too many sends failed. Skipped recipients (consent/suppression/etc.) are protection working as intended and never count as failures.</summary>
public static class FailureRatePolicy
{
    public static bool ShouldPause(int failed, int messagedOrFailed, int thresholdPercent, int minSample) =>
        messagedOrFailed >= Math.Max(1, minSample) && failed * 100 >= thresholdPercent * messagedOrFailed && failed > 0;

    public static double Percent(int failed, int attempted) => attempted == 0 ? 0 : Math.Round(failed * 100.0 / attempted, 1);
}

public sealed record AttributionCandidate(Guid RecipientId, Guid CampaignId, DateTime SentAtUtc);

/// <summary>Attribution of a reply or a CRM event to a campaign message: last touch inside the window, never before the message, never from the future.</summary>
public static class AttributionRules
{
    public static bool IsInside(DateTime sentAtUtc, DateTime eventAtUtc, TimeSpan window) => eventAtUtc >= sentAtUtc && eventAtUtc - sentAtUtc <= window;

    /// <summary>The most recent message sent at or before the event that is still inside the window; null when none qualifies.</summary>
    public static AttributionCandidate? Pick(IEnumerable<AttributionCandidate> candidates, DateTime eventAtUtc, TimeSpan window) =>
        candidates.Where(c => IsInside(c.SentAtUtc, eventAtUtc, window)).OrderByDescending(c => c.SentAtUtc).ThenBy(c => c.RecipientId).FirstOrDefault();
}

/// <summary>Cut-off times and status sets used when a follow-up audience is derived from a parent campaign.</summary>
public static class FollowUpRules
{
    /// <summary>The recipient statuses a rule starts from.</summary>
    public static IReadOnlyList<RecipientStatus> Statuses(FollowUpRule rule) => rule switch
    {
        FollowUpRule.SentNotReplied => [RecipientStatus.Sent, RecipientStatus.Delivered, RecipientStatus.Read],
        FollowUpRule.DeliveredNotReplied => [RecipientStatus.Delivered, RecipientStatus.Read],
        FollowUpRule.ReadNotReplied => [RecipientStatus.Read],
        FollowUpRule.RepliedNotAdvanced => [RecipientStatus.Replied],
        _ => [],
    };

    /// <summary>The event must be at or before this instant (N days ago).</summary>
    public static DateTime Cutoff(DateTime nowUtc, int afterDays) => nowUtc.AddDays(-Math.Max(0, afterDays));

    /// <summary>Evaluates a rule for one parent recipient (pure mirror of the SQL used by the audience builder; kept for tests and the preview explanation).</summary>
    public static bool Matches(FollowUpRule rule, RecipientStatus status, DateTime? sentAt, DateTime? deliveredAt, DateTime? readAt, DateTime? repliedAt, bool leadInAdvancedStage, int afterDays, DateTime nowUtc)
    {
        if (!Statuses(rule).Contains(status)) return false;
        var cut = Cutoff(nowUtc, afterDays);
        return rule switch
        {
            FollowUpRule.SentNotReplied => sentAt is { } s && s <= cut,
            FollowUpRule.DeliveredNotReplied => (deliveredAt ?? readAt) is { } d && d <= cut,
            FollowUpRule.ReadNotReplied => readAt is { } r && r <= cut,
            FollowUpRule.RepliedNotAdvanced => repliedAt is { } p && p <= cut && !leadInAdvancedStage,
            _ => false,
        };
    }

    /// <summary>When a follow-up that waits for its parent may start: parent completion + delay. Null while the parent has not completed.</summary>
    public static DateTime? StartAfterParent(DateTime? parentCompletedAtUtc, int? delayDays) => parentCompletedAtUtc is { } c ? c.AddDays(Math.Max(0, delayDays ?? 0)) : null;
}

public sealed record BuiltMessage(string Text, IReadOnlyList<string> Missing);

/// <summary>
/// Builds the text of one campaign message from the approved body: safe variable values (<see cref="TemplateRenderer.Sanitize"/>), the documented missing-variable policy and the opt-out footer.
/// MISSING-VARIABLE POLICY: a placeholder without a value uses its inline fallback ({{first_name|there}}) when it has one; otherwise the recipient is skipped
/// (reason <c>missing_variable</c>). A message with a literal "{{name}}" or an empty hole never reaches a customer. No code is evaluated: placeholders are looked up in a fixed dictionary.
/// </summary>
public static class CampaignContent
{
    public const string FooterSeparator = "\n\n";

    /// <summary>Problems with a body that make it unusable (unknown placeholders, broken braces, empty, too long once the footer is added).</summary>
    public static IReadOnlyList<string> Validate(string? body, string? footer)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(body)) { problems.Add("The message text is empty."); return problems; }
        problems.AddRange(TemplateRenderer.Problems(body));
        var total = body.Trim().Length + (string.IsNullOrWhiteSpace(footer) ? 0 : FooterSeparator.Length + footer.Trim().Length);
        if (total > Domain.Messaging.Message.MaxBodyLength - 300) problems.Add($"The message (with the opt-out text) is too long: keep it under {Domain.Messaging.Message.MaxBodyLength - 300} characters so variable values fit.");
        return problems;
    }

    public static BuiltMessage Build(string body, string? footer, IReadOnlyDictionary<string, string> values)
    {
        var safe = values.ToDictionary(kv => kv.Key, kv => TemplateRenderer.Sanitize(kv.Value), StringComparer.OrdinalIgnoreCase);
        var missing = TemplateRenderer.MissingFor(body, safe);
        if (missing.Count > 0) return new BuiltMessage(string.Empty, missing);
        var text = TemplateRenderer.Render(body, safe);
        if (text.Contains("{{", StringComparison.Ordinal) || text.Contains("}}", StringComparison.Ordinal)) return new BuiltMessage(string.Empty, ["unresolved_placeholder"]);
        var f = footer?.Trim();
        if (!string.IsNullOrEmpty(f)) text = text.TrimEnd() + FooterSeparator + TemplateRenderer.Sanitize(f, keepNewlines: false, maxLength: 200);
        return new BuiltMessage(text, []);
    }

    /// <summary>Does the footer give people a way to opt out that the inbound opt-out detector actually understands?</summary>
    public static bool FooterContainsOptOutKeyword(string footer, IReadOnlyList<string> keywords)
    {
        var folded = OptOutDetector.Fold(footer);
        return keywords.Any(k => OptOutDetector.Fold(k) is { Length: > 0 } kw && (" " + folded + " ").Contains(" " + kw + " ", StringComparison.Ordinal));
    }
}

/// <summary>Test-send allow-list rules: only numbers the workspace listed explicitly may receive a test.</summary>
public static class TestSendRules
{
    public static bool IsAllowed(string? e164, IReadOnlyCollection<string> allowList) => e164 is not null && allowList.Contains(e164, StringComparer.Ordinal);
    public const string Label = "[TEST - LeadPilot campaign preview, not sent to customers]";
    public static string Decorate(string text) => Label + "\n" + text;
}

/// <summary>The values a campaign message can use. Everything comes from CRM data and is sanitised by <see cref="CampaignContent.Build"/>.</summary>
public static class CampaignValues
{
    public static Dictionary<string, string> For(string? contactName, string? leadName, string? phoneE164, string? city, string? country)
    {
        var name = string.IsNullOrWhiteSpace(contactName) ? leadName : contactName;
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = name ?? string.Empty,
            ["first_name"] = TemplateRenderer.FirstName(string.IsNullOrWhiteSpace(contactName) ? null : contactName),
            ["business_name"] = leadName ?? string.Empty,
            ["phone"] = phoneE164 ?? string.Empty,
            ["city"] = city ?? string.Empty,
            ["country"] = country ?? string.Empty,
        };
    }
}

public sealed record EligibilityRecord(DateTime AtUtc, bool Eligible, IReadOnlyList<string> Reasons);

/// <summary>The eligibility decisions stored on a recipient: the one taken when the audience was selected and the one taken immediately before the send. Both are kept.</summary>
public sealed record EligibilityTrail(EligibilityRecord? Selection, EligibilityRecord? Send)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);
    public static EligibilityTrail Empty { get; } = new(null, null);

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, Json);

    public static EligibilityTrail Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;
        try { return System.Text.Json.JsonSerializer.Deserialize<EligibilityTrail>(json, Json) ?? Empty; } catch (System.Text.Json.JsonException) { return Empty; }
    }

    public EligibilityTrail WithSelection(EligibilityRecord r) => this with { Selection = r };
    public EligibilityTrail WithSend(EligibilityRecord r) => this with { Send = r };
}
