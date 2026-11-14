using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeadPilot.Application.Ai;

// ===================================================================================================================================================================
// Pure, deterministic AI logic: chunking, untrusted-input handling, sensitivity rules, response validation and model-output parsing. No I/O: unit-tested in isolation.
// ===================================================================================================================================================================

public sealed record TextChunk(int Ordinal, string Text, string? Heading)
{
    /// <summary>What is embedded: the heading gives short passages their context.</summary>
    public string EmbeddingInput => string.IsNullOrEmpty(Heading) ? Text : Heading + "\n\n" + Text;
}

public static partial class TextChunker
{
    [GeneratedRegex(@"(?<=[\.\!\?؟。])\s+")] private static partial Regex SentenceSplit();
    [GeneratedRegex(@"\n{2,}")] private static partial Regex BlankLines();

    /// <summary>
    /// Splits text into passages of about <paramref name="target"/> characters (never more than <paramref name="max"/>), on paragraph, then sentence, then word boundaries, carrying a markdown heading
    /// along and repeating up to <paramref name="overlap"/> characters between neighbours. Deterministic: the same text always yields the same chunks.
    /// </summary>
    public static IReadOnlyList<TextChunk> Chunk(string? text, int target = 900, int max = 1400, int overlap = 120)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        target = Math.Max(100, target); max = Math.Max(target, max); overlap = Math.Clamp(overlap, 0, target / 2);
        var normalised = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ');
        var chunks = new List<TextChunk>();
        var buffer = new StringBuilder();
        string? heading = null, bufferHeading = null;
        var carryLength = 0;   // the start of the buffer that is only a repeat of the previous chunk's end

        void Flush()
        {
            var t = buffer.ToString().Trim();
            if (t.Length > 0 && buffer.Length > carryLength) chunks.Add(new TextChunk(chunks.Count, t, bufferHeading));
            var carry = overlap > 0 && t.Length > overlap ? TailAtWordBoundary(t, overlap) : string.Empty;
            buffer.Clear(); buffer.Append(carry); carryLength = carry.Length;
            bufferHeading = heading;
        }

        foreach (var block in BlankLines().Split(normalised))
        {
            var para = block.Trim();
            if (para.Length == 0) continue;
            if (para.StartsWith('#') && !para.Contains('\n') && para.TrimStart('#').Trim() is { Length: > 0 } h) { Flush(); heading = h.Length > 200 ? h[..200] : h; bufferHeading = heading; buffer.Clear(); carryLength = 0; continue; }
            foreach (var unit in Units(para, max - overlap - 1))
            {
                if (buffer.Length > carryLength && buffer.Length + unit.Length + 1 > target) Flush();
                if (buffer.Length == 0) bufferHeading = heading;
                if (buffer.Length > 0) buffer.Append('\n');
                buffer.Append(unit);
            }
        }
        var rest = buffer.ToString().Trim();
        if (rest.Length > 0 && buffer.Length > carryLength) chunks.Add(new TextChunk(chunks.Count, rest, bufferHeading));
        return chunks;
    }

    private static IEnumerable<string> Units(string paragraph, int max)
    {
        if (paragraph.Length <= max) { yield return paragraph; yield break; }
        foreach (var sentence in SentenceSplit().Split(paragraph))
        {
            var s = sentence.Trim();
            if (s.Length == 0) continue;
            if (s.Length <= max) { yield return s; continue; }
            var rest = s;
            while (rest.Length > max)
            {
                var cut = rest.LastIndexOf(' ', max - 1);
                if (cut < max / 2) cut = max;
                yield return rest[..cut].Trim();
                rest = rest[cut..].Trim();
            }
            if (rest.Length > 0) yield return rest;
        }
    }

    private static string TailAtWordBoundary(string t, int length)
    {
        var start = t.Length - length;
        var space = t.IndexOf(' ', start);
        return space < 0 ? string.Empty : t[(space + 1)..];
    }
}

public sealed record InjectionAnalysis(bool Suspected, IReadOnlyList<string> Signals);

/// <summary>
/// Everything a customer writes and everything a document says is UNTRUSTED. <see cref="Analyze"/> flags well-known manipulation patterns (the flag only influences escalation and logging, it is never the
/// only defence); <see cref="Neutralize"/> makes text inert before it is placed into a prompt: control/bidi characters removed, our delimiter characters escaped, length capped.
/// </summary>
public static partial class PromptGuard
{
    private static readonly (string Signal, Regex Pattern)[] Signals =
    [
        ("ignore_instructions", new Regex(@"\b(ignore|disregard|forget|override|bypass)\b[^.\n]{0,40}\b(previous|prior|above|earlier|all|any|your|the)\b[^.\n]{0,40}\b(instructions?|rules?|prompts?|guidelines?|polic(y|ies)|restrictions?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("reveal_prompt", new Regex(@"\b(reveal|show|print|repeat|output|display|tell me|leak|share)\b[^.\n]{0,40}\b(system prompt|your prompt|your instructions|developer message|hidden (prompt|instructions)|initial prompt|api[- ]?key|secret|credentials?|password)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("role_override", new Regex(@"\b(you are now|from now on you|act as (an? )?(unrestricted|jailbroken|dan)|pretend (to be|you are)|developer mode|jailbreak|do anything now)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("fake_markup", new Regex(@"(</?\s*(system|assistant|instructions?|knowledge|knowledge_chunk|customer_message|conversation|tool_use)\b|\[/?INST\]|<<\s*/?SYS\s*>>|<\|im_(start|end)\|>|^\s*(system|assistant)\s*:)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled)),
        ("cross_tenant_probe", new Regex(@"\b(other|another|different|all|every)\b[^.\n]{0,25}\b(tenants?|workspaces?|companies|customers?|schools?|clients?|accounts?)\b[^.\n]{0,25}\b(data|documents?|knowledge|prices?|pricing|messages?|conversations?|files?|records?)\b|\b(data|documents?|knowledge|prices?|pricing|messages?|conversations?|records?|files?)\b[^.\n]{0,30}\b(of|from|for)\b[^.\n]{0,15}\b(other|another|different)\b[^.\n]{0,15}\b(tenants?|workspaces?|compan(?:y|ies)|customers?|schools?|clients?|accounts?)\b|\btenant[ _-]?id\b|\bother tenant", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("policy_override", new Regex(@"\b(you (must|have to|are required to)|i (order|command|authorize|insist))\b[^.\n]{0,20}\b(give|apply|offer|approve|confirm|promise)\b[^.\n]{0,40}\b(discount|refund|free|waiver|price)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("encoded_payload", new Regex(@"\b(base64|rot13|hex[- ]?encoded)\b[^.\n]{0,40}\b(decode|decoded|instructions?|prompt)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    ];

    public static InjectionAnalysis Analyze(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(false, []);
        var plain = Neutralize(text, 20_000, escapeAngles: false);
        var hits = Signals.Where(s => s.Pattern.IsMatch(plain)).Select(s => s.Signal).ToList();
        return new(hits.Count > 0, hits);
    }

    private static readonly string[] KnowledgeSignals = ["ignore_instructions", "role_override", "fake_markup", "policy_override", "encoded_payload"];

    /// <summary>
    /// For passages of the knowledge base (website text, uploaded files): only the signals that cannot occur in honest business content. Words such as "password" or "other customers" are normal in policies and
    /// are not treated as an attack here. A passage that matches is left out of the AI's context and reported to staff.
    /// </summary>
    public static InjectionAnalysis AnalyzeKnowledge(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new(false, []);
        var plain = Neutralize(text, 20_000, escapeAngles: false);
        var hits = Signals.Where(s => KnowledgeSignals.Contains(s.Signal) && s.Pattern.IsMatch(plain)).Select(s => s.Signal).ToList();
        return new(hits.Count > 0, hits);
    }

    [GeneratedRegex(@"\n{3,}")] private static partial Regex ManyNewlines();

    public static string Neutralize(string? text, int maxChars, bool escapeAngles = true)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder(Math.Min(text.Length, maxChars));
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            if (v is '\n' or '\t') { sb.Append((char)v); continue; }
            if (v < 0x20 || v is >= 0x7F and <= 0x9F) continue;                                    // control characters
            if (v is >= 0x200B and <= 0x200F or >= 0x202A and <= 0x202E or >= 0x2060 and <= 0x2064 or 0xFEFF or >= 0x2066 and <= 0x2069 or 0x00AD) continue;   // zero-width, bidi, soft hyphen
            if (escapeAngles && v == '<') { sb.Append('‹'); continue; }
            if (escapeAngles && v == '>') { sb.Append('›'); continue; }
            sb.Append(rune.ToString());
            if (sb.Length >= maxChars) break;
        }
        return ManyNewlines().Replace(sb.ToString(), "\n\n").Trim();
    }
}

/// <summary>Cheap deterministic triage BEFORE any model is called: requests that always belong to a person.</summary>
public static class SensitivityRules
{
    private static readonly (string Reason, Regex Pattern)[] Rules =
    [
        ("payment_dispute", new Regex(@"\b(refund|chargeback|charged (me|twice|again)|double[- ]charged|card number|cvv|iban|i (have )?(paid|transferred|sent (the )?(money|payment))|(payment|transfer) (failed|not (received|going|working)|was not)|invoice (is )?(wrong|incorrect))\b|\b(paise|paisay|raqam) wapas\b|\brefund (chahiye|karo|kar do|kardo)\b|(پیسے|رقم) واپس|ریفنڈ|\b(maine|main ne|humne) (payment|paise|raqam) (kar di|bhej di|de di)\b|\b(payment|transfer|deposit) (went through|was made|has been made|is done|was done|was sent)\b|\b(already|just) paid\b|\bwe (have )?paid\b|\bpayment (has been )?(completed|made)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("legal_or_privacy", new Regex(@"\b(lawyer|attorney|lawsuit|sue (you|us)|legal action|court|gdpr|data protection|delete my (data|information|account)|privacy (request|complaint)|subpoena)\b|\b(wakeel|wakil|adalat|case karunga|case kar dunga)\b|وکیل|عدالت", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("complaint", new Regex(@"\b(complain(t)?|unacceptable|scam|fraud|rip[- ]?off|terrible service|worst|furious|report you|cancel (my|the) (contract|subscription|agreement))\b|\b(shikayat|shikaayat|dhoka|dhokha|bakwas service)\b|شکایت|دھوکہ|دھوکا", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("human_requested", new Regex(@"\b(speak|talk|chat|connect me|transfer me)\b[^.\n]{0,25}\b(to|with)\b[^.\n]{0,20}\b(human|person|agent|manager|representative|someone|staff|team|owner)\b|\b(real|live) (person|human|agent)\b|\bcall me\b|\b(insan|insaan|kisi (insan|bande|shakhs)|manager|owner|staff) se (baat|bat) (karni|karna|kara|karwa)|\bmujhe (call|phone) (karo|karen|kijiye|kar do)\b|(کسی|انسان|منیجر|مالک) سے بات|مجھے (کال|فون) کریں", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("credentials", new Regex(@"\b(password|passcode|otp|one[- ]time (code|password)|verification code|login (details|credentials)|api[- ]?key|secret key)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("safety", new Regex(@"\b(suicide|kill (myself|you|someone)|self[- ]harm|bomb|threat(en)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    ];

    public static IReadOnlyList<string> Detect(string? text, string? tenantKeywords = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var reasons = Rules.Where(r => r.Pattern.IsMatch(text)).Select(r => r.Reason).ToList();
        foreach (var keyword in ParseKeywords(tenantKeywords))
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase)) { reasons.Add("tenant_keyword"); break; }
        return reasons;
    }

    public static IReadOnlyList<string> ParseKeywords(string? raw) =>
        (raw ?? string.Empty).Split(['\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(k => k.Length is >= 2 and <= 60).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();
}

public static class Intents
{
    public const string Greeting = "greeting", Thanks = "thanks", Product = "product_question", Pricing = "pricing", Features = "features", Demo = "demo_request", Trial = "trial_request",
        Support = "support", Complaint = "complaint", OptOut = "opt_out", Payment = "payment", OffTopic = "off_topic", Other = "other", Unknown = "unknown";

    public static readonly IReadOnlyList<string> All = [Greeting, Thanks, Product, Pricing, Features, Demo, Trial, Support, Complaint, OptOut, Payment, OffTopic, Other];

    public static string Normalise(string? raw)
    {
        var v = (raw ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        return All.Contains(v) ? v : Unknown;
    }

    /// <summary>Intents that need grounded knowledge to be answered. Greetings and thanks do not; everything else must be backed by retrieved passages.</summary>
    public static bool RequiresKnowledge(string intent) => intent is not (Greeting or Thanks or OptOut or OffTopic);

    /// <summary>Always handled by a person, whatever the model says.</summary>
    public static bool AlwaysHuman(string intent) => intent is Complaint or Payment or OptOut;
}

public sealed record ClassifierOutput(string Intent, bool NeedsHuman, string StandaloneQuery, string? Language, decimal Confidence, IReadOnlyDictionary<string, string> Extracted);

public sealed record GeneratorAction(string Name, IReadOnlyDictionary<string, string> Arguments);

public sealed record AgentToolCallRequest(string Name, string ArgumentsJson);

public sealed record AgentOutput(string Answer, IReadOnlyList<string> Citations, decimal Confidence, bool NeedsHuman, string? Reason, string? NextAction, IReadOnlyList<AgentToolCallRequest> ToolCalls);

public sealed record GeneratorOutput(string Answer, IReadOnlyList<string> Citations, decimal Confidence, bool NeedsHuman, string? Reason, string? NextAction, IReadOnlyList<GeneratorAction> Actions);

/// <summary>Strict parsing of what the models return. Anything malformed yields null (the caller then escalates); nothing here trusts the model's types or sizes.</summary>
public static class ModelOutput
{
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 8, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static JsonElement? ExtractObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{'); var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonDocument.Parse(text[start..(end + 1)], Options).RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    public static ClassifierOutput? ParseClassifier(string? text, string fallbackQuery)
    {
        if (ExtractObject(text) is not { ValueKind: JsonValueKind.Object } o) return null;
        var intent = Intents.Normalise(Str(o, "intent", 40));
        var query = Str(o, "standalone_query", 400);
        return new ClassifierOutput(intent, Bool(o, "needs_human"), string.IsNullOrWhiteSpace(query) ? fallbackQuery : query!, Str(o, "language", 20), Num(o, "confidence"), Dictionary(o, "extracted", 10));
    }

    public static GeneratorOutput? ParseGenerator(string? text)
    {
        if (ExtractObject(text) is not { ValueKind: JsonValueKind.Object } o) return null;
        var answer = Str(o, "answer", 4000);
        if (answer is null && !Bool(o, "needs_human")) return null;
        var citations = new List<string>();
        if (o.TryGetProperty("citations", out var c) && c.ValueKind == JsonValueKind.Array)
            foreach (var item in c.EnumerateArray().Take(12)) if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 and <= 20 } r) citations.Add(r.Trim().Trim('[', ']').ToUpperInvariant());
        var actions = new List<GeneratorAction>();
        if (o.TryGetProperty("actions", out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var item in a.EnumerateArray().Take(5))
                if (item.ValueKind == JsonValueKind.Object && Str(item, "name", 60) is { } name) actions.Add(new GeneratorAction(name, Dictionary(item, "arguments", 8)));
        return new GeneratorOutput(answer ?? string.Empty, citations.Distinct().ToList(), Num(o, "confidence"), Bool(o, "needs_human"), Str(o, "reason", 200), Str(o, "next_action", 300), actions);
    }

    /// <summary>The sales-agent protocol: like <see cref="ParseGenerator"/> but the model may ask for tools ("tool_calls": [{"name", "arguments": {…}}]) and may leave the answer empty while it does.</summary>
    public static AgentOutput? ParseAgent(string? text)
    {
        if (ExtractObject(text) is not { ValueKind: JsonValueKind.Object } o) return null;
        var calls = new List<AgentToolCallRequest>();
        foreach (var key in new[] { "tool_calls", "actions" })
            if (o.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var item in a.EnumerateArray().Take(6))
                    if (item.ValueKind == JsonValueKind.Object && Str(item, "name", 60) is { } name)
                    {
                        var args = item.TryGetProperty("arguments", out var av) && av.ValueKind == JsonValueKind.Object ? av.GetRawText() : "{}";
                        if (args.Length > 4000) args = "{\"__too_large\":true}";
                        calls.Add(new AgentToolCallRequest(name, args));
                    }
        var answer = Str(o, "answer", 4000);
        if (answer is null && calls.Count == 0 && !Bool(o, "needs_human")) return null;
        var citations = new List<string>();
        if (o.TryGetProperty("citations", out var c) && c.ValueKind == JsonValueKind.Array)
            foreach (var item in c.EnumerateArray().Take(12)) if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 and <= 20 } r) citations.Add(r.Trim().Trim('[', ']').ToUpperInvariant());
        return new AgentOutput(answer ?? string.Empty, citations.Distinct().ToList(), Num(o, "confidence"), Bool(o, "needs_human"), Str(o, "reason", 200), Str(o, "next_action", 300), calls);
    }

    private static string? Str(JsonElement o, string name, int max)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        var s = v.GetString()?.Trim();
        return string.IsNullOrEmpty(s) ? null : s.Length > max ? s[..max] : s;
    }

    private static bool Bool(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static decimal Num(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return Math.Clamp(d, 0, 1);
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return Math.Clamp(p, 0, 1);
        return 0;
    }

    private static Dictionary<string, string> Dictionary(JsonElement o, string name, int maxItems)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) return result;
        foreach (var p in v.EnumerateObject())
        {
            if (result.Count >= maxItems) break;
            var key = p.Name.Length > 40 ? p.Name[..40] : p.Name;
            var value = p.Value.ValueKind switch { JsonValueKind.String => p.Value.GetString(), JsonValueKind.Number => p.Value.GetRawText(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => null };
            if (!string.IsNullOrWhiteSpace(value)) result[key] = value.Length > 300 ? value[..300] : value;
        }
        return result;
    }
}

public sealed record ValidationInput(
    string Answer, IReadOnlyList<string> CitedRefs, IReadOnlyDictionary<string, string> ContextByRef, IReadOnlyList<string> CustomerTexts,
    IReadOnlyList<string> ProtectedTexts, string? Canary, bool RequireCitation, int MaxChars = 1500, IReadOnlySet<string>? VerifiedCompletions = null);

/// <summary>
/// Deterministic check of a generated reply against what the AI is allowed to say. A single issue means the reply is NOT used as written. It cannot prove a reply is true, but it does enforce: grounded in
/// cited passages, no prices/percentages/discounts that the cited passages do not contain, no claim that a demo/payment/account action happened, no leaked prompts/keys/canary, no contact details or links
/// that appear nowhere in the knowledge.
/// </summary>
public static partial class ResponseValidator
{
    public const string Empty = "empty_answer", TooLong = "too_long", NoCitation = "no_citation", UnknownCitation = "unknown_citation", UnsupportedAmount = "unsupported_price_or_percentage",
        UnsupportedDiscount = "unsupported_discount_or_offer", CompletionClaim = "unverified_completion_claim", PromptLeak = "prompt_or_secret_leak", UnsupportedContact = "unsupported_contact_detail";

    private static readonly string[] OfferTerms = ["discount", "% off", "percent off", "promo", "coupon", "voucher", "free trial", "free of charge", "for free", "complimentary", "waive", "special offer", "cashback", "price match", "guarantee", "no cost"];

    [GeneratedRegex(@"(?:USD|US\$|SAR|SR|PKR|AED|INR|EUR|GBP|Rs\.?|₹|€|£|\$)\s?(?<n>\d[\d,]*(?:\.\d+)?)|(?<n>\d[\d,]*(?:\.\d+)?)\s?(?:USD|SAR|SR|PKR|AED|INR|EUR|GBP|Rs\.?|dollars?|riyals?|rupees?|dirhams?|euros?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Money();
    [GeneratedRegex(@"(?<n>\d+(?:[.,]\d+)?)\s?%")] private static partial Regex Percent();
    [GeneratedRegex(@"\d[\d,]*(?:\.\d+)?")] private static partial Regex AnyNumber();
    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+")] private static partial Regex Email();
    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""')\]]+", RegexOptions.IgnoreCase)] private static partial Regex Url();
    [GeneratedRegex(@"\+?\d[\d\s\-()]{7,}\d")] private static partial Regex Phone();
    [GeneratedRegex(@"sk-[A-Za-z0-9_\-]{16,}|pa-[A-Za-z0-9_\-]{20,}|Bearer\s+[A-Za-z0-9._\-]{16,}|-----BEGIN [A-Z ]*KEY-----|AKIA[0-9A-Z]{16}")] private static partial Regex SecretShape();
    [GeneratedRegex(@"\b(?:your|the|our)\s+(?:demo|meeting|call|appointment|trial|account|subscription|order|booking|payment|refund|invoice)\b[^.\n!?]{0,50}?\b(?:is|has been|have been|was|are|were|will be)\s+(?:now\s+|already\s+|successfully\s+)?(?:booked|scheduled|confirmed|created|activated|set up|ready|received|processed|completed|upgraded|cancelled|canceled|refunded|sent|approved)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompletionPassive();
    [GeneratedRegex(@"\b(?:i(?:'ve| have)?|we(?:'ve| have)?)\s+(?:already\s+|just\s+|now\s+|successfully\s+)?(?:booked|scheduled|created|activated|processed|refunded|charged|upgraded|approved|confirmed|sent (?:you )?(?:the )?(?:invoice|payment link|invitation|login))\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompletionActive();
    [GeneratedRegex(@"\bpayment\b[^.\n!?]{0,30}\b(?:received|confirmed|completed|successful|went through|has been made)\b|\b(?:received|confirmed) (?:your )?payment\b", RegexOptions.IgnoreCase)]
    private static partial Regex PaymentClaim();

    public static IReadOnlyList<ValidationIssue> Validate(ValidationInput input)
    {
        var issues = new List<ValidationIssue>();
        var answer = input.Answer?.Trim() ?? string.Empty;
        if (answer.Length == 0) { issues.Add(new(Empty, "The reply is empty.")); return issues; }
        if (answer.Length > input.MaxChars) issues.Add(new(TooLong, $"The reply has {answer.Length} characters (limit {input.MaxChars})."));

        var known = input.CitedRefs.Where(input.ContextByRef.ContainsKey).Distinct().ToList();
        foreach (var unknown in input.CitedRefs.Where(r => !input.ContextByRef.ContainsKey(r)).Distinct()) issues.Add(new(UnknownCitation, $"Cites '{unknown}' which was not provided."));
        if (input.RequireCitation && known.Count == 0) issues.Add(new(NoCitation, "The reply cites no knowledge passage."));
        var citedText = string.Join("\n", known.Select(r => input.ContextByRef[r]));
        var allKnowledge = string.Join("\n", input.ContextByRef.Values);

        // numbers: every amount / percentage must appear in a CITED passage
        var allowed = Numbers(citedText);
        foreach (var amount in AmountsIn(answer).Where(a => !allowed.Contains(a)).Distinct())
            issues.Add(new(UnsupportedAmount, $"The amount or percentage {amount.ToString(CultureInfo.InvariantCulture)} does not appear in the cited knowledge."));

        // offers: a discount/free/guarantee wording needs the same wording in a cited passage
        foreach (var term in OfferTerms)
            if (answer.Contains(term, StringComparison.OrdinalIgnoreCase) && !citedText.Contains(term, StringComparison.OrdinalIgnoreCase))
                issues.Add(new(UnsupportedDiscount, $"Mentions '{term}' but the cited knowledge does not."));

        // completion claims: nothing in this phase can verify that a demo / payment / account action happened
        // (a claim is allowed only when a tool of THIS run really did it: "demo" after BookDemo succeeded, "trial" after CreateTrialAccount succeeded; a payment is never claimed)
        var verified = input.VerifiedCompletions ?? new HashSet<string>();
        var unverifiedClaim = PaymentClaim().IsMatch(answer)
            || CompletionPassive().Matches(answer).Any(m => !IsVerifiedClaim(m.Value, verified))
            || CompletionActive().Matches(answer).Any(m => !IsVerifiedClaim(m.Value, verified));
        if (unverifiedClaim) issues.Add(new(CompletionClaim, "States that a booking, payment or account action has been completed; this cannot be verified."));

        // leaks
        if (!string.IsNullOrEmpty(input.Canary) && answer.Contains(input.Canary, StringComparison.OrdinalIgnoreCase)) issues.Add(new(PromptLeak, "Contains the internal reference code."));
        else if (SecretShape().IsMatch(answer)) issues.Add(new(PromptLeak, "Contains something shaped like a key or credential."));
        else if (LeaksProtectedText(answer, input.ProtectedTexts)) issues.Add(new(PromptLeak, "Repeats internal instructions."));

        // contact details and links must come from the knowledge (a phone/e-mail the customer wrote may be repeated; a link may not)
        var knowledgeAndCustomer = allKnowledge + "\n" + string.Join("\n", input.CustomerTexts);
        foreach (Match m in Url().Matches(answer))
            if (!allKnowledge.Contains(m.Value.TrimEnd('.', ',', ';'), StringComparison.OrdinalIgnoreCase)) issues.Add(new(UnsupportedContact, "Contains a link that is not in the knowledge."));
        foreach (Match m in Email().Matches(answer))
            if (!knowledgeAndCustomer.Contains(m.Value.TrimEnd('.', '-'), StringComparison.OrdinalIgnoreCase)) issues.Add(new(UnsupportedContact, "Contains an e-mail address that is not in the knowledge."));
        var knownDigits = Digits(knowledgeAndCustomer);
        foreach (Match m in Phone().Matches(answer))
            if (Digits(m.Value) is { Length: >= 8 } d && !knownDigits.Contains(d)) issues.Add(new(UnsupportedContact, "Contains a phone number that is not in the knowledge."));

        return issues.GroupBy(i => (i.Code, i.Detail)).Select(g => g.First()).ToList();
    }

    private static readonly Regex DemoWords = new(@"\b(demo|meeting|call|appointment|booking|booked|schedule[d]?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrialWords = new(@"\b(trial|account)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsVerifiedClaim(string matched, IReadOnlySet<string> verified) =>
        (verified.Contains("demo") && DemoWords.IsMatch(matched)) || (verified.Contains("trial") && TrialWords.IsMatch(matched));

    private static HashSet<decimal> Numbers(string text)
    {
        var set = new HashSet<decimal>();
        foreach (Match m in AnyNumber().Matches(text)) if (TryNumber(m.Value, out var n)) set.Add(n);
        return set;
    }

    private static IEnumerable<decimal> AmountsIn(string text)
    {
        foreach (Match m in Money().Matches(text)) if (TryNumber(m.Groups["n"].Value, out var n)) yield return n;
        foreach (Match m in Percent().Matches(text)) if (TryNumber(m.Groups["n"].Value.Replace(',', '.'), out var n)) yield return n;
    }

    private static bool TryNumber(string raw, out decimal value) =>
        decimal.TryParse(raw.Replace(",", string.Empty).TrimEnd('.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

    private static bool LeaksProtectedText(string answer, IReadOnlyList<string> protectedTexts)
    {
        var a = " " + string.Join(' ', Words(answer)) + " ";
        foreach (var p in protectedTexts)
        {
            var words = Words(p).ToArray();
            for (var i = 0; i + 8 <= words.Length; i++)
                if (a.Contains(" " + string.Join(' ', words, i, 8) + " ", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static IEnumerable<string> Words(string s) =>
        s.ToLowerInvariant().Split([' ', '\n', '\r', '\t', ',', '.', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '-', '*', '#', '`'], StringSplitOptions.RemoveEmptyEntries);
}
