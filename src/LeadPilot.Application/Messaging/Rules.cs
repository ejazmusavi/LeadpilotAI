using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;

namespace LeadPilot.Application.Messaging;

/// <summary>Maps gateway acks to delivery statuses. WAHA: -1 ERROR, 0 PENDING, 1 SERVER, 2 DEVICE, 3 READ, 4 PLAYED.</summary>
public static class AckRules
{
    public static MessageStatus? ToDelivery(int ack) => ack switch { 1 => MessageStatus.Sent, 2 => MessageStatus.Delivered, 3 or 4 => MessageStatus.Read, _ => null };
    public static bool IsError(int ack) => ack < 0;
}

/// <summary>Chat-id → E.164 matching. WAHA ids are digits + "@c.us" (no "+"); "@lid" is a hidden id that carries no phone number.</summary>
public static class ChatIds
{
    public static bool IsDirectChat(string? chatId) => chatId is not null && (chatId.EndsWith("@c.us", StringComparison.Ordinal) || chatId.EndsWith("@lid", StringComparison.Ordinal) || chatId.EndsWith("@s.whatsapp.net", StringComparison.Ordinal));

    /// <summary>E.164 of a phone-based chat id, or null for hidden (@lid), group, channel, broadcast or malformed ids. Never guesses.</summary>
    public static string? ToE164(string? chatId)
    {
        if (chatId is null) return null;
        var at = chatId.IndexOf('@');
        if (at <= 0) return null;
        var domain = chatId[(at + 1)..];
        if (domain is not ("c.us" or "s.whatsapp.net")) return null;
        var user = chatId[..at];
        var colon = user.IndexOf(':');          // device suffix "923001234567:12@s.whatsapp.net"
        if (colon > 0) user = user[..colon];
        return user.All(char.IsAsciiDigit) ? Normalizer.PhoneE164("+" + user) : null;
    }

    /// <summary>"+923001234567" → "923001234567@c.us" (the form every WAHA send endpoint expects).</summary>
    public static string? FromE164(string? e164)
    {
        var n = Normalizer.PhoneE164(e164);
        return n is null ? null : n[1..] + "@c.us";
    }
}

/// <summary>Idempotency keys for outbound messages.</summary>
public static class IdempotencyKeys
{
    public const int MaxLength = 100;

    /// <summary>Client key trimmed and bounded; null/blank → a fresh random key (every call is then a distinct message).</summary>
    public static string Normalize(string? clientKey)
    {
        var k = clientKey?.Trim();
        if (string.IsNullOrEmpty(k)) return "auto-" + Guid.NewGuid().ToString("N");
        return k.Length > MaxLength ? k[..MaxLength] : k;
    }
}

public enum SendBlock { None = 0, MinimumDelay, HourlyCap, DailyCap }

public sealed record SendRateDecision(SendBlock Block, DateTime? RetryAtUtc, int DailyLimitToday)
{
    public bool CanSendNow => Block == SendBlock.None;
}

public sealed record SendRateFacts(int HourlyCap, int DailyCap, int MinDelaySeconds, DateTime? WarmupStartedAtUtc, DateTime? LastSendAtUtc, int SentLastHour, int SentLast24Hours, DateTime? OldestSendInLastHourUtc, DateTime? OldestSendInLast24HoursUtc);

/// <summary>
/// Conservative send pacing for a WhatsApp-Web session: minimum gap between sends, hourly and daily caps, and a warm-up that starts at
/// 20 messages on the first day and grows by 20 per day until the configured daily cap. Pure and unit-tested.
/// </summary>
public static class SendRatePolicy
{
    public const int WarmupFirstDay = 20;
    public const int WarmupDailyIncrease = 20;

    public static int DailyLimitToday(int dailyCap, DateTime? warmupStartedAtUtc, DateTime nowUtc)
    {
        if (warmupStartedAtUtc is null) return Math.Min(dailyCap, WarmupFirstDay);
        var day = Math.Max(0, (int)Math.Floor((nowUtc - warmupStartedAtUtc.Value).TotalDays));
        return Math.Min(dailyCap, WarmupFirstDay + day * WarmupDailyIncrease);
    }

    public static SendRateDecision Evaluate(SendRateFacts f, DateTime nowUtc)
    {
        var dailyLimit = DailyLimitToday(f.DailyCap, f.WarmupStartedAtUtc, nowUtc);
        if (f.SentLast24Hours >= dailyLimit) return new(SendBlock.DailyCap, (f.OldestSendInLast24HoursUtc ?? nowUtc).AddHours(24), dailyLimit);
        if (f.SentLastHour >= f.HourlyCap) return new(SendBlock.HourlyCap, (f.OldestSendInLastHourUtc ?? nowUtc).AddHours(1), dailyLimit);
        if (f.LastSendAtUtc is { } last && nowUtc - last < TimeSpan.FromSeconds(f.MinDelaySeconds)) return new(SendBlock.MinimumDelay, last.AddSeconds(f.MinDelaySeconds), dailyLimit);
        return new(SendBlock.None, null, dailyLimit);
    }

    /// <summary>Random spread (0–50 % of the minimum delay) so sends are never on a fixed beat; injected random keeps it testable.</summary>
    public static TimeSpan Jitter(int minDelaySeconds, double random01) => TimeSpan.FromSeconds(minDelaySeconds * 0.5 * Math.Clamp(random01, 0, 1));
}

/// <summary>
/// Opt-out keyword detection for inbound messages.
/// POLICY (documented in SECURITY.md): a message is an opt-out only when, after folding case, punctuation, accents and Arabic-script variants, it is
/// EXACTLY a keyword/phrase, or starts with a keyword and the rest consists only of filler words ("please", "me", "all", "sending", "messages"…), up to 3 extra words.
/// Keywords inside a longer sentence never match ("don't stop now", "please stop by tomorrow", "cancel my order #5" stay normal messages): a false opt-out silently
/// loses a customer, a missed one is caught by the next, shorter "STOP". Tenants may replace the list (setting <c>messaging.optout_keywords</c>).
/// </summary>
public static class OptOutDetector
{
    public const string SettingKey = "messaging.optout_keywords";

    public static readonly IReadOnlyList<string> DefaultKeywords =
    [
        // English
        "stop", "unsubscribe", "cancel", "opt out", "optout", "end", "quit", "remove me", "stop messages", "stop messaging", "do not contact", "dont contact",
        // Roman Urdu
        "band karo", "band kardo", "band kar do", "band kro", "bas karo", "bas kro", "mat bhejo", "na bhejo", "message band", "messages band", "nahi chahiye", "nahin chahiye", "mujhe message mat bhejo",
        // Urdu script
        "بند کریں", "بند کرو", "بند کر دو", "بس کرو", "مت بھیجو", "نہ بھیجیں", "نہیں چاہیے", "ان سبسکرائب",
    ];

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "please", "pls", "plz", "now", "me", "all", "sending", "send", "messages", "message", "msgs", "from", "this", "number", "thanks", "thank", "you", "it", "my", "kindly", "immediately",
        "mujhe", "abhi", "meharbani", "shukriya", "مہربانی", "شکریہ", "مجھے", "ابھی", "پلیز",
    };

    public static IReadOnlyList<string> ParseList(string? setting) =>
        string.IsNullOrWhiteSpace(setting) ? DefaultKeywords : setting.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => s.Length > 0).Distinct().ToList();

    public static bool IsOptOut(string? body, IReadOnlyList<string>? keywords = null)
    {
        var text = Fold(body);
        if (text.Length == 0 || text.Length > 80) return false;
        foreach (var raw in keywords ?? DefaultKeywords)
        {
            var kw = Fold(raw);
            if (kw.Length == 0) continue;
            if (text == kw) return true;
            if (!text.StartsWith(kw + " ", StringComparison.Ordinal)) continue;
            var rest = text[(kw.Length + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length <= 3 && rest.All(Filler.Contains)) return true;
        }
        return false;
    }

    /// <summary>Lower-case, accent-free, punctuation → space, Arabic variants folded, single-spaced. Apostrophes are removed first ("don't" → "dont").</summary>
    public static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormKD).ToLowerInvariant();
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            if (ch is '\'' or '’' or '`' or 'ـ') continue;
            var c = ch switch { 'ي' or 'ى' or 'ئ' => 'ی', 'ك' => 'ک', 'ة' => 'ہ', 'ھ' => 'ہ', 'أ' or 'إ' or 'آ' => 'ا', _ => ch };
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}

public sealed class TemplateRenderException(string message) : Exception(message);

/// <summary>Renders <c>{{variable}}</c> placeholders locally (the gateway has no template feature).</summary>
public static partial class TemplateRenderer
{
    public static readonly IReadOnlyList<string> KnownVariables = ["name", "first_name", "business_name", "phone"];

    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]{0,30})\s*\}\}")]
    private static partial Regex Placeholder();

    public static IReadOnlyList<string> VariablesIn(string body) => Placeholder().Matches(body).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();

    /// <summary>Unknown variable names are rejected when the template is saved, so a typo can never reach a customer as literal "{{nmae}}".</summary>
    public static IReadOnlyList<string> UnknownVariables(string body) => VariablesIn(body).Where(v => !KnownVariables.Contains(v)).ToList();

    public static string Render(string body, IReadOnlyDictionary<string, string> values)
    {
        var missing = new List<string>();
        var result = Placeholder().Replace(body, m =>
        {
            var key = m.Groups[1].Value.ToLowerInvariant();
            if (values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
            missing.Add(key);
            return m.Value;
        });
        if (missing.Count > 0) throw new TemplateRenderException("No value for: " + string.Join(", ", missing.Distinct()) + ".");
        return result;
    }

    public static string FirstName(string? fullName)
    {
        var t = fullName?.Trim();
        if (string.IsNullOrEmpty(t)) return string.Empty;
        var i = t.IndexOf(' ');
        return i > 0 ? t[..i] : t;
    }
}

/// <summary>Outbound policy: when is a send a service reply (inside the customer-initiated window) and when is it marketing.</summary>
public static class OutboundPolicy
{
    /// <summary>Default customer-service window (hours). Mirrors the official platform's 24-hour rule even though WAHA does not enforce it.</summary>
    public const int DefaultServiceWindowHours = 24;
    public const string WindowSettingKey = "messaging.service_window_hours";
    public const string AutoCreateLeadsSettingKey = "messaging.auto_create_leads";

    public static MessagePurpose PurposeFor(DateTime? lastInboundAtUtc, DateTime nowUtc, TimeSpan window) =>
        lastInboundAtUtc is { } t && nowUtc - t <= window ? MessagePurpose.Service : MessagePurpose.Marketing;

    public static TimeSpan ParseWindow(string? setting) =>
        int.TryParse(setting, NumberStyles.None, CultureInfo.InvariantCulture, out var h) && h is >= 1 and <= 168 ? TimeSpan.FromHours(h) : TimeSpan.FromHours(DefaultServiceWindowHours);
}

/// <summary>Status transitions for the send runner, as pure decisions.</summary>
public static class SendRetryPolicy
{
    public const int MaxSafeRetries = 4;

    /// <summary>Backoff before another attempt after a safe-to-retry failure: 30 s, 2 m, 8 m, 30 m (bounded).</summary>
    public static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(1800, 30 * Math.Pow(4, Math.Max(0, attempt - 1))));
}
