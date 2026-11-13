using LeadPilot.Application.Crm;
using LeadPilot.Domain.Crm;

namespace LeadPilot.UnitTests;

public class ScoringTests
{
    private static readonly IReadOnlyDictionary<string, int> Stages = new Dictionary<string, int>
    {
        ["new"] = 10, ["contacted"] = 40, ["responded"] = 50, ["interested"] = 60, ["lost"] = 130,
    };

    private static LeadFacts Facts(string? category = null, string? city = null, bool website = false, int contacts = 0, bool phone = false, bool email = false,
        bool named = false, bool verifiedPhone = false, bool verifiedContact = false, int recent = 0, StageKind kind = StageKind.Open, int position = 10, params string[] tags) =>
        new(category, "Saudi Arabia", null, city, null, null, website, contacts, phone, email, named, verifiedPhone, verifiedContact, recent, kind, position, Stages, tags.ToHashSet());

    private static ScoringRuleSpec Rule(ScoreDimension d, string target, int points, bool enabled = true) => new(Guid.NewGuid(), d, target, points, null, enabled);

    [Fact]
    public void A_lead_with_no_signals_scores_zero()
    {
        var r = LeadScoringEngine.Score(Facts(), LeadScoringEngine.DefaultRules);
        Assert.Equal(0, r.Score);
        Assert.Empty(r.Matches);
    }

    [Fact]
    public void Default_rules_reward_contact_quality_website_and_verification()
    {
        var f = Facts(website: true, contacts: 2, phone: true, email: true, named: true, verifiedPhone: true, recent: 1);
        var r = LeadScoringEngine.Score(f, LeadScoringEngine.DefaultRules);
        Assert.Equal(10 + 15 + 10 + 10 + 5 + 15 + 5, r.Score);
        Assert.Equal(7, r.Matches.Count);
    }

    [Fact]
    public void Category_and_location_rules_are_tenant_data_and_case_and_accent_insensitive()
    {
        var rules = new[]
        {
            Rule(ScoreDimension.BusinessCategory, "private school", 20),
            Rule(ScoreDimension.Location, "City=Riyadh", 10),
            Rule(ScoreDimension.Location, "Country=saudi arabia", 5),
            Rule(ScoreDimension.Location, "City=Jeddah", 50),
        };
        var r = LeadScoringEngine.Score(Facts(category: "Private School", city: "RIYADH"), rules);
        Assert.Equal(35, r.Score);
        Assert.DoesNotContain(r.Matches, m => m.Target == "City=Jeddah");
    }

    [Fact]
    public void Website_absent_rule_matches_only_leads_without_a_website()
    {
        var rules = new[] { Rule(ScoreDimension.WebsiteAvailability, "absent", 8) };
        Assert.Equal(8, LeadScoringEngine.Score(Facts(website: false), rules).Score);
        Assert.Equal(0, LeadScoringEngine.Score(Facts(website: true), rules).Score);
    }

    [Fact]
    public void Engagement_and_interest_follow_the_stage_reached_and_ignore_lost_or_suppressed_leads()
    {
        var rules = new[] { Rule(ScoreDimension.Engagement, "stage:Responded", 15), Rule(ScoreDimension.Interest, "stage:Interested", 20) };
        Assert.Equal(0, LeadScoringEngine.Score(Facts(position: 40), rules).Score);
        Assert.Equal(15, LeadScoringEngine.Score(Facts(position: 50), rules).Score);
        Assert.Equal(35, LeadScoringEngine.Score(Facts(position: 60), rules).Score);
        Assert.Equal(0, LeadScoringEngine.Score(Facts(kind: StageKind.Lost, position: 130), rules).Score);   // Lost sits late in the list but has not progressed
        Assert.Equal(0, LeadScoringEngine.Score(Facts(kind: StageKind.Suppressed, position: 140), rules).Score);
    }

    [Fact]
    public void Interest_can_also_be_a_tag()
    {
        var rules = new[] { Rule(ScoreDimension.Interest, "tag:Hot", 25) };
        Assert.Equal(25, LeadScoringEngine.Score(Facts(tags: "hot"), rules).Score);
        Assert.Equal(0, LeadScoringEngine.Score(Facts(tags: "cold"), rules).Score);
    }

    [Fact]
    public void Negative_rules_and_clamping()
    {
        var rules = new[] { Rule(ScoreDimension.WebsiteAvailability, "absent", -30), Rule(ScoreDimension.ContactQuality, "valid_phone", 100), Rule(ScoreDimension.ContactQuality, "email", 100) };
        Assert.Equal(0, LeadScoringEngine.Score(Facts(), rules).Score); // -30 → clamped to 0
        var high = LeadScoringEngine.Score(Facts(website: true, phone: true, email: true), rules);
        Assert.Equal(100, high.Score);
        Assert.Equal(200, high.RawTotal);
    }

    [Fact]
    public void Disabled_zero_point_and_invalid_rules_never_score()
    {
        var rules = new[]
        {
            Rule(ScoreDimension.WebsiteAvailability, "present", 10, enabled: false),
            Rule(ScoreDimension.WebsiteAvailability, "present", 0),
            Rule(ScoreDimension.WebsiteAvailability, "sometimes", 10),   // invalid target
            Rule(ScoreDimension.ContactQuality, "valid_phone", 500),     // out of range
        };
        Assert.Equal(0, LeadScoringEngine.Score(Facts(website: true, phone: true), rules).Score);
    }

    [Fact]
    public void Activity_rule_needs_the_requested_number_of_recent_activities()
    {
        var rules = new[] { Rule(ScoreDimension.Activity, "recent:3", 12) };
        Assert.Equal(0, LeadScoringEngine.Score(Facts(recent: 2), rules).Score);
        Assert.Equal(12, LeadScoringEngine.Score(Facts(recent: 3), rules).Score);
    }

    [Fact]
    public void Breakdown_json_round_trips_and_names_every_matched_rule()
    {
        var rules = new[] { Rule(ScoreDimension.WebsiteAvailability, "present", 10), Rule(ScoreDimension.ContactQuality, "email", 5) };
        var r = LeadScoringEngine.Score(Facts(website: true, email: true), rules);
        var parsed = ScoreResult.ParseMatches(r.ToJson());
        Assert.Equal(2, parsed.Count);
        Assert.Equal(15, parsed.Sum(m => m.Points));
        Assert.Contains(parsed, m => m.Dimension == ScoreDimension.WebsiteAvailability && m.Target == "present");
        Assert.Empty(ScoreResult.ParseMatches("not json"));
    }

    [Theory]
    [InlineData(ScoreDimension.BusinessCategory, "School", 10, true)]
    [InlineData(ScoreDimension.BusinessCategory, "", 10, false)]
    [InlineData(ScoreDimension.Location, "City=Riyadh", 10, true)]
    [InlineData(ScoreDimension.Location, "Planet=Mars", 10, false)]
    [InlineData(ScoreDimension.Location, "City=", 10, false)]
    [InlineData(ScoreDimension.WebsiteAvailability, "present", 10, true)]
    [InlineData(ScoreDimension.WebsiteAvailability, "maybe", 10, false)]
    [InlineData(ScoreDimension.ContactQuality, "multiple_contacts", 10, true)]
    [InlineData(ScoreDimension.ContactQuality, "has_cat", 10, false)]
    [InlineData(ScoreDimension.VerifiedInfo, "verified_phone", 10, true)]
    [InlineData(ScoreDimension.VerifiedInfo, "verified", 10, false)]
    [InlineData(ScoreDimension.Activity, "recent:5", 10, true)]
    [InlineData(ScoreDimension.Activity, "recent:0", 10, false)]
    [InlineData(ScoreDimension.Activity, "recent:x", 10, false)]
    [InlineData(ScoreDimension.Engagement, "stage:Responded", 10, true)]
    [InlineData(ScoreDimension.Engagement, "stage:", 10, false)]
    [InlineData(ScoreDimension.Interest, "tag:Hot", 10, true)]
    [InlineData(ScoreDimension.Interest, "hot", 10, false)]
    [InlineData(ScoreDimension.WebsiteAvailability, "present", 101, false)]
    [InlineData(ScoreDimension.WebsiteAvailability, "present", -101, false)]
    public void Rule_targets_are_validated(ScoreDimension d, string target, int points, bool valid) =>
        Assert.Equal(valid, ScoringRuleParser.Validate(d, target, points) is null);

    [Fact]
    public void Score_has_no_influence_on_eligibility_rules()
    {
        // The eligibility facts do not even carry a score: structurally impossible for a high score to unlock a contact.
        Assert.DoesNotContain(typeof(EligibilityFacts).GetProperties(), p => p.Name.Contains("Score", StringComparison.OrdinalIgnoreCase));
    }
}
