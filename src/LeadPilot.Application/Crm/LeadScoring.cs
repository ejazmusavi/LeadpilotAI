using System.Text.Json;
using LeadPilot.Domain.Crm;

namespace LeadPilot.Application.Crm;

/// <summary>Everything the scoring engine may look at. Built from the database by the scoring service; plain data so rules are unit-testable.</summary>
public sealed record LeadFacts(
    string? CategoryName, string? Country, string? Province, string? City, string? District, string? Area,
    bool HasWebsite, int ContactCount, bool HasValidPhone, bool HasEmail, bool HasNamedContact, bool HasVerifiedPhone, bool HasVerifiedContact,
    int RecentActivityCount, StageKind StageKind, int StagePosition,
    IReadOnlyDictionary<string, int> PipelineStagePositions, IReadOnlySet<string> TagNames);

public sealed record ScoringRuleSpec(Guid? Id, ScoreDimension Dimension, string Target, int Points, string? Description, bool Enabled = true);

public sealed record RuleMatch(Guid? RuleId, ScoreDimension Dimension, string Target, int Points, string? Description);

public sealed record ScoreResult(int Score, int RawTotal, IReadOnlyList<RuleMatch> Matches)
{
    public string ToJson() => JsonSerializer.Serialize(new
    {
        rawTotal = RawTotal, score = Score,
        matches = Matches.Select(m => new { ruleId = m.RuleId, dimension = m.Dimension.ToString(), target = m.Target, points = m.Points, description = m.Description })
    });

    public static IReadOnlyList<RuleMatch> ParseMatches(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("matches", out var arr)) return [];
            var list = new List<RuleMatch>();
            foreach (var e in arr.EnumerateArray())
            {
                Guid? id = e.TryGetProperty("ruleId", out var r) && r.ValueKind == JsonValueKind.String && Guid.TryParse(r.GetString(), out var g) ? g : null;
                Enum.TryParse<ScoreDimension>(e.GetProperty("dimension").GetString(), out var dim);
                list.Add(new RuleMatch(id, dim, e.GetProperty("target").GetString() ?? "", e.GetProperty("points").GetInt32(), e.TryGetProperty("description", out var d) ? d.GetString() : null));
            }
            return list;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return []; }
    }
}

/// <summary>Validates the target syntax of each scoring dimension (rules are tenant data, so they are validated, never trusted).</summary>
public static class ScoringRuleParser
{
    public static readonly IReadOnlySet<string> LocationFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Country", "Province", "City", "District", "Area" };
    public static readonly IReadOnlySet<string> ContactQualityTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "valid_phone", "email", "named_contact", "multiple_contacts" };
    public static readonly IReadOnlySet<string> VerifiedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "verified_phone", "verified_contact" };

    public static string Describe(ScoreDimension d) => d switch
    {
        ScoreDimension.BusinessCategory => "Category name, e.g. Private school",
        ScoreDimension.Location => "Field=Value, e.g. City=Riyadh (fields: Country, Province, City, District, Area)",
        ScoreDimension.WebsiteAvailability => "present or absent",
        ScoreDimension.ContactQuality => "valid_phone, email, named_contact or multiple_contacts",
        ScoreDimension.VerifiedInfo => "verified_phone or verified_contact",
        ScoreDimension.Activity => "recent:N (at least N activities in the last 30 days)",
        ScoreDimension.Engagement => "stage:<stage name> (the lead has reached that stage)",
        ScoreDimension.Interest => "stage:<stage name> or tag:<tag name>",
        _ => string.Empty,
    };

    /// <summary>Null when valid, otherwise a message for the user.</summary>
    public static string? Validate(ScoreDimension dimension, string? target, int points)
    {
        if (points is < -100 or > 100) return "Points must be between -100 and 100.";
        if (string.IsNullOrWhiteSpace(target) || target.Length > 200) return "A target is required (max 200 characters).";
        var t = target.Trim();
        switch (dimension)
        {
            case ScoreDimension.BusinessCategory: return null;
            case ScoreDimension.Location:
                var eq = t.IndexOf('=');
                return eq > 0 && eq < t.Length - 1 && LocationFields.Contains(t[..eq].Trim()) && t[(eq + 1)..].Trim().Length > 0 ? null : $"Location rules look like {Describe(dimension)}.";
            case ScoreDimension.WebsiteAvailability: return t is "present" or "absent" ? null : "Website rules use 'present' or 'absent'.";
            case ScoreDimension.ContactQuality: return ContactQualityTargets.Contains(t) ? null : $"Contact quality rules use: {Describe(dimension)}.";
            case ScoreDimension.VerifiedInfo: return VerifiedTargets.Contains(t) ? null : $"Verified-info rules use: {Describe(dimension)}.";
            case ScoreDimension.Activity:
                return t.StartsWith("recent:", StringComparison.OrdinalIgnoreCase) && int.TryParse(t[7..], out var n) && n is >= 1 and <= 100 ? null : $"Activity rules look like {Describe(dimension)}.";
            case ScoreDimension.Engagement:
                return t.StartsWith("stage:", StringComparison.OrdinalIgnoreCase) && t[6..].Trim().Length > 0 ? null : $"Engagement rules look like {Describe(dimension)}.";
            case ScoreDimension.Interest:
                return (t.StartsWith("stage:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)) && t[(t.IndexOf(':') + 1)..].Trim().Length > 0
                    ? null : $"Interest rules look like {Describe(dimension)}.";
            default: return "Unknown scoring dimension.";
        }
    }
}

/// <summary>Rule-driven lead scoring. The score ranks sales potential only: it never grants permission to contact anyone (see the eligibility service).</summary>
public static class LeadScoringEngine
{
    public const int MaxScore = 100;

    public static ScoreResult Score(LeadFacts f, IEnumerable<ScoringRuleSpec> rules)
    {
        var matches = new List<RuleMatch>();
        foreach (var r in rules)
        {
            if (!r.Enabled || r.Points == 0) continue;
            if (ScoringRuleParser.Validate(r.Dimension, r.Target, r.Points) is not null) continue; // invalid rule data never scores
            if (Matches(f, r.Dimension, r.Target.Trim())) matches.Add(new RuleMatch(r.Id, r.Dimension, r.Target.Trim(), r.Points, r.Description));
        }
        var raw = matches.Sum(m => m.Points);
        return new ScoreResult(Math.Clamp(raw, 0, MaxScore), raw, matches);
    }

    private static bool Matches(LeadFacts f, ScoreDimension d, string target)
    {
        switch (d)
        {
            case ScoreDimension.BusinessCategory: return f.CategoryName is not null && Same(f.CategoryName, target);
            case ScoreDimension.Location:
                var eq = target.IndexOf('=');
                var value = target[(eq + 1)..].Trim();
                var actual = target[..eq].Trim().ToLowerInvariant() switch
                {
                    "country" => f.Country, "province" => f.Province, "city" => f.City, "district" => f.District, "area" => f.Area, _ => null
                };
                return actual is not null && Same(actual, value);
            case ScoreDimension.WebsiteAvailability: return target.Equals("present", StringComparison.OrdinalIgnoreCase) == f.HasWebsite;
            case ScoreDimension.ContactQuality:
                return target.ToLowerInvariant() switch
                {
                    "valid_phone" => f.HasValidPhone, "email" => f.HasEmail, "named_contact" => f.HasNamedContact, "multiple_contacts" => f.ContactCount >= 2, _ => false
                };
            case ScoreDimension.VerifiedInfo:
                return target.Equals("verified_phone", StringComparison.OrdinalIgnoreCase) ? f.HasVerifiedPhone : f.HasVerifiedContact;
            case ScoreDimension.Activity:
                return int.TryParse(target[7..], out var n) && f.RecentActivityCount >= n;
            case ScoreDimension.Engagement:
            case ScoreDimension.Interest:
                if (target.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)) return f.TagNames.Contains(target[4..].Trim().ToLowerInvariant());
                return ReachedStage(f, target[6..].Trim());
            default: return false;
        }
    }

    /// <summary>"Reached" means at or beyond the stage in the lead's pipeline. Lost and Suppressed leads have not progressed, whatever their position.</summary>
    private static bool ReachedStage(LeadFacts f, string stageName)
    {
        if (f.StageKind is StageKind.Lost or StageKind.Suppressed) return false;
        return f.PipelineStagePositions.TryGetValue(stageName.ToLowerInvariant(), out var pos) && f.StagePosition >= pos;
    }

    private static bool Same(string a, string b) => Normalizer.Place(a) == Normalizer.Place(b);

    /// <summary>Starter rules every new tenant receives (version 1). Pure business-neutral signals; tenants edit them freely.</summary>
    public static readonly IReadOnlyList<ScoringRuleSpec> DefaultRules =
    [
        new(null, ScoreDimension.WebsiteAvailability, "present", 10, "Has a website", true),
        new(null, ScoreDimension.ContactQuality, "valid_phone", 15, "Has a valid phone number", true),
        new(null, ScoreDimension.ContactQuality, "email", 10, "Has an e-mail address", true),
        new(null, ScoreDimension.ContactQuality, "named_contact", 10, "Has a named contact person", true),
        new(null, ScoreDimension.ContactQuality, "multiple_contacts", 5, "Has two or more contacts", true),
        new(null, ScoreDimension.VerifiedInfo, "verified_phone", 15, "Phone number verified by the team", true),
        new(null, ScoreDimension.Activity, "recent:1", 5, "Activity in the last 30 days", true),
        new(null, ScoreDimension.Engagement, "stage:Responded", 15, "Reached Responded", true),
        new(null, ScoreDimension.Interest, "stage:Interested", 15, "Reached Interested", true),
    ];
}
