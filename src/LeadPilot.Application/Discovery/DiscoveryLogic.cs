using System.Text;
using System.Text.Json;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Discovery;

namespace LeadPilot.Application.Discovery;

public sealed record ConfidenceInputs(
    SourceKind Source, bool HasName, bool HasAddress, bool HasWebsite, bool HasPhone, bool HasEmail, bool CategoryMatched, bool WebsiteConfirmed, bool ContactCorroborated, bool BusinessClosed);

/// <summary>
/// Confidence (0–100) in how reliable a discovered record is. NOT a sales score and never an input to contact eligibility.
/// Points: source base (structured provider 35, authorized directory 30, user supplied 30, website self-published 25)
/// + address 10 + website 10 + phone 15 + e-mail 5 + category matched 10 + website confirmed 10 + contact corroborated by a second source 5;
/// −40 when the provider reports the business closed. No name → 0. Clamped to 0–100. Levels: ≥75 High, ≥50 Medium, otherwise Low.
/// </summary>
public static class ConfidenceCalculator
{
    public static int Calculate(ConfidenceInputs i)
    {
        if (!i.HasName) return 0;
        var score = i.Source switch { SourceKind.StructuredProvider => 35, SourceKind.AuthorizedDirectory => 30, SourceKind.UserSupplied => 30, _ => 25 };
        if (i.HasAddress) score += 10;
        if (i.HasWebsite) score += 10;
        if (i.HasPhone) score += 15;
        if (i.HasEmail) score += 5;
        if (i.CategoryMatched) score += 10;
        if (i.WebsiteConfirmed) score += 10;
        if (i.ContactCorroborated) score += 5;
        if (i.BusinessClosed) score -= 40;
        return Math.Clamp(score, 0, 100);
    }

    public static ConfidenceLevel Level(int confidence) => DiscoveryResult.LevelFor(confidence);
}

public sealed record SearchLocation(string? Country, string? Province, string? City, string? District, string? Area);

/// <summary>Turns category terms + free-text location into provider queries. Region codes are a lookup, never a restriction on where a tenant may search.</summary>
public static class DiscoveryQueryBuilder
{
    private static readonly Dictionary<string, string> Regions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pakistan"] = "PK", ["india"] = "IN", ["bangladesh"] = "BD", ["saudi arabia"] = "SA", ["united arab emirates"] = "AE", ["uae"] = "AE", ["qatar"] = "QA", ["kuwait"] = "KW",
        ["oman"] = "OM", ["bahrain"] = "BH", ["egypt"] = "EG", ["jordan"] = "JO", ["turkey"] = "TR", ["united kingdom"] = "GB", ["uk"] = "GB", ["united states"] = "US", ["usa"] = "US",
        ["canada"] = "CA", ["australia"] = "AU", ["nigeria"] = "NG", ["kenya"] = "KE", ["south africa"] = "ZA", ["indonesia"] = "ID", ["malaysia"] = "MY", ["sri lanka"] = "LK",
    };

    public static string? RegionCode(string? country) => country is not null && Regions.TryGetValue(country.Trim(), out var c) ? c : null;

    public static string LocationText(SearchLocation l) =>
        string.Join(", ", new[] { l.Area, l.District, l.City, l.Province, l.Country }.Select(x => x?.Trim()).Where(x => !string.IsNullOrEmpty(x)));

    /// <summary>One query per category phrase (or one keyword-only query). The text is e.g. "private school in Gulberg, Lahore, Punjab, Pakistan".</summary>
    public static IReadOnlyList<ProviderQuery> Build(SearchLocation location, IReadOnlyList<string> categoryTerms, string? placeType, string? keywords)
    {
        var where = LocationText(location);
        var kw = string.IsNullOrWhiteSpace(keywords) ? null : keywords.Trim();
        var terms = categoryTerms.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (terms.Count == 0 && kw is null) return [];
        if (terms.Count == 0) terms.Add(string.Empty);
        var region = RegionCode(location.Country);
        return terms.Select(t =>
        {
            var what = string.Join(' ', new[] { t, kw }.Where(x => !string.IsNullOrEmpty(x)));
            var text = where.Length == 0 ? what : $"{what} in {where}";
            return new ProviderQuery(text, region, string.IsNullOrEmpty(t) ? null : placeType, location.Country, location.Province, location.City, location.District, location.Area);
        }).ToList();
    }
}

/// <summary>A candidate with every value cleaned. Unknown stays null.</summary>
public sealed record NormalizedCandidate(
    string ExternalId, string? Name, string? Address, string? Website, string? PhoneRaw, string? PhoneE164, string? Email, IReadOnlyList<string> SocialLinks, string? CategoryLabel,
    string? Country, string? Province, string? City, string? District, string? Area);

public static class CandidateNormalizer
{
    public static NormalizedCandidate Normalize(DiscoveryCandidate c, string? defaultCountryCode)
    {
        var name = Squash(c.Name, 300);
        var website = NormalizeWebsite(c.Website);
        var phoneRaw = Squash(c.Phone, 40);
        var e164 = phoneRaw is null ? null : Normalizer.PhoneE164(phoneRaw, defaultCountryCode);
        var email = c.Email is null ? null : Normalizer.Email(c.Email.Trim());
        var social = c.SocialLinks.Select(NormalizeWebsite).Where(s => s is not null).Select(s => s!).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        var label = c.Types.Count == 0 ? null : Squash(string.Join(", ", c.Types.Take(5).Select(t => t.Replace('_', ' '))), 300);
        return new NormalizedCandidate(c.ExternalId.Trim(), name, Squash(c.Address, 500), website, phoneRaw, e164, email, social, label,
            Squash(c.Country, 100), Squash(c.Province, 100), Squash(c.City, 100), Squash(c.District, 100), Squash(c.Area, 100));
    }

    public static string? Squash(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var sb = new StringBuilder(value.Length); var lastSpace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch)) { if (!lastSpace) sb.Append(' '); lastSpace = true; }
            else { sb.Append(ch); lastSpace = false; }
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? null : s.Length > max ? s[..max] : s;
    }

    /// <summary>http(s) only, no credentials, no fragment; a bare domain gets https://. Anything else is dropped.</summary>
    public static string? NormalizeWebsite(string? raw)
    {
        var v = raw?.Trim();
        if (string.IsNullOrEmpty(v) || v.Length > 500) return null;
        if (!v.Contains("://", StringComparison.Ordinal)) v = "https://" + v;
        if (!Uri.TryCreate(v, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host) || !uri.Host.Contains('.')) return null;
        var b = new UriBuilder(uri) { Fragment = string.Empty };
        var s = b.Uri.GetLeftPart(UriPartial.Path) + b.Uri.Query;
        return s.Length > 500 ? null : s;
    }

    public static string ProvenanceJson(IEnumerable<(string Field, Verification Verification)> fields) =>
        JsonSerializer.Serialize(fields.ToDictionary(f => f.Field, f => f.Verification.ToString()));
}

public sealed record UsageDecision(bool Allowed, string? Reason, int? RemainingCalls);

/// <summary>Monthly limit enforcement (calls and optional budget). Pure so it is testable without a database.</summary>
public static class UsageLimitPolicy
{
    public static UsageDecision Check(int callsUsed, decimal costUsed, int callLimit, decimal? budgetUsd, int callsRequested = 1, decimal costPerCall = 0m)
    {
        if (callsRequested < 1) callsRequested = 1;
        var remaining = Math.Max(0, callLimit - callsUsed);
        if (callsUsed + callsRequested > callLimit)
            return new UsageDecision(false, $"The monthly limit of {callLimit} call(s) for this connector is used up ({callsUsed} used). Raise the limit under Connectors or wait for next month.", remaining);
        if (budgetUsd is { } b && costUsed + costPerCall * callsRequested > b)
            return new UsageDecision(false, $"The monthly budget of {b:0.00} USD for this connector would be exceeded ({costUsed:0.00} USD estimated so far).", remaining);
        return new UsageDecision(true, null, remaining - callsRequested);
    }
}

/// <summary>robots.txt (RFC 9309 semantics): group selection by user-agent token, longest matching rule wins, Allow beats Disallow on a tie, '*' and '$' wildcards.</summary>
public sealed class RobotsTxt
{
    private sealed record Rule(bool Allow, string Pattern);
    private readonly List<(List<string> Agents, List<Rule> Rules)> _groups;
    public bool DisallowAll { get; }

    private RobotsTxt(List<(List<string>, List<Rule>)> groups, bool disallowAll) { _groups = groups; DisallowAll = disallowAll; }

    public static RobotsTxt AllowEverything { get; } = new([], false);
    /// <summary>Used when robots.txt could not be fetched because of a server error: RFC 9309 says to assume everything is disallowed.</summary>
    public static RobotsTxt BlockEverything { get; } = new([], true);

    public static RobotsTxt Parse(string? content)
    {
        var groups = new List<(List<string>, List<Rule>)>();
        List<string>? agents = null; List<Rule>? rules = null; var lastWasAgent = false;
        foreach (var raw in (content ?? string.Empty).Split('\n'))
        {
            var line = raw; var hash = line.IndexOf('#'); if (hash >= 0) line = line[..hash];
            line = line.Trim(); var colon = line.IndexOf(':'); if (colon <= 0) continue;
            var field = line[..colon].Trim().ToLowerInvariant(); var value = line[(colon + 1)..].Trim();
            if (field == "user-agent")
            {
                if (!lastWasAgent || agents is null) { agents = []; rules = []; groups.Add((agents, rules)); }
                agents.Add(value.ToLowerInvariant()); lastWasAgent = true;
            }
            else if (field is "allow" or "disallow")
            {
                lastWasAgent = false;
                if (rules is null) continue;
                if (value.Length == 0) { if (field == "disallow") continue; }   // empty Disallow = allow all
                rules.Add(new Rule(field == "allow", value));
            }
            else lastWasAgent = false;
        }
        return new RobotsTxt(groups, false);
    }

    public bool IsAllowed(string userAgentToken, string pathAndQuery)
    {
        if (DisallowAll) return false;
        var token = userAgentToken.ToLowerInvariant();
        var group = _groups.Where(g => g.Agents.Any(a => a != "*" && token.Contains(a))).OrderByDescending(g => g.Agents.Where(a => a != "*" && token.Contains(a)).Max(a => a.Length)).Select(g => ((List<string>, List<Rule>)?)g).FirstOrDefault()
                    ?? _groups.Where(g => g.Agents.Contains("*")).Select(g => ((List<string>, List<Rule>)?)g).FirstOrDefault();
        if (group is null) return true;
        var path = string.IsNullOrEmpty(pathAndQuery) ? "/" : pathAndQuery;
        Rule? best = null; var bestLen = -1;
        foreach (var r in group.Value.Item2)
        {
            if (!Matches(r.Pattern, path)) continue;
            var len = r.Pattern.Length;
            if (len > bestLen || (len == bestLen && r.Allow && best is { Allow: false })) { best = r; bestLen = len; }
        }
        return best?.Allow ?? true;
    }

    private static bool Matches(string pattern, string path)
    {
        var anchored = pattern.EndsWith('$'); if (anchored) pattern = pattern[..^1];
        var parts = pattern.Split('*');
        var pos = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (i == 0) { if (!path.StartsWith(p, StringComparison.Ordinal)) return false; pos = p.Length; continue; }
            var idx = path.IndexOf(p, pos, StringComparison.Ordinal);
            if (idx < 0) return false;
            pos = idx + p.Length;
        }
        return !anchored || (parts.Length == 1 ? path.Length == pattern.Length : path.EndsWith(parts[^1], StringComparison.Ordinal));
    }
}

/// <summary>Cheap, explainable text checks used when enriching candidates. They only ever add evidence; they never create data.</summary>
public static class DiscoveryMatching
{
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "school", "schools", "college", "academy", "the", "and", "for", "home", "welcome", "official", "website", "site", "public", "private", "high", "system", "institute", "education", "educational",
        "campus", "branch", "page", "main", "online", "www", "com",
    };

    private static IEnumerable<string> Tokens(string? s) =>
        Normalizer.Name(s).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length >= 3 && !Generic.Contains(t));

    /// <summary>True when two names share at least one distinctive word (e.g. "Beaconhouse School Gulberg" vs "Beaconhouse – Home").</summary>
    public static bool SharesDistinctiveWord(string? a, string? b)
    {
        var set = Tokens(a).ToHashSet(); return set.Count > 0 && Tokens(b).Any(set.Contains);
    }

    /// <summary>The candidate belongs to the searched category: the provider type equals the mapping's place type, or a search phrase appears in the name or in the provider's types.</summary>
    public static bool CategoryMatches(IReadOnlyList<string> terms, string? placeType, string? name, IReadOnlyList<string> types)
    {
        if (placeType is not null && types.Contains(placeType, StringComparer.OrdinalIgnoreCase)) return true;
        var hay = ((name ?? "") + " " + string.Join(' ', types.Select(t => t.Replace('_', ' ')))).ToLowerInvariant();
        return terms.Any(t => t.Length > 0 && hay.Contains(t.ToLowerInvariant()));
    }

    public static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    /// <summary>Two phone numbers match when their last 9 digits agree (ignores country code / trunk prefix formatting).</summary>
    public static bool SamePhone(string? a, string? b)
    {
        var x = Digits(a); var y = Digits(b);
        if (x.Length < 7 || y.Length < 7) return false;
        var n = Math.Min(9, Math.Min(x.Length, y.Length));
        return x[^n..] == y[^n..];
    }
}
