using LeadPilot.Application.Config;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Config;
using LeadPilot.Domain.Crm;

namespace LeadPilot.UnitTests;

public class CustomFieldTests
{
    private static CustomFieldDefinition Def(CustomFieldType type, string[]? options = null, bool required = false, decimal? min = null, decimal? max = null, string? pattern = null) =>
        CustomFieldDefinition.Create(Guid.NewGuid(), CustomEntityType.Lead, "f", "Field", type, options, required, min, max, pattern, null, true, 0, DateTime.UtcNow, null);

    [Theory]
    [InlineData("", false)] [InlineData("Bad Key", false)] [InlineData("1abc", false)] [InlineData("UPPER", true)] [InlineData("ok_key9", true)]
    public void Keys_follow_the_rule(string key, bool ok)
    {
        if (ok) Assert.NotNull(CustomFieldDefinition.Create(Guid.NewGuid(), CustomEntityType.Lead, key, "L", CustomFieldType.Text, null, false, null, null, null, null, false, 0, DateTime.UtcNow, null));
        else Assert.Throws<DomainException>(() => CustomFieldDefinition.Create(Guid.NewGuid(), CustomEntityType.Lead, key, "L", CustomFieldType.Text, null, false, null, null, null, null, false, 0, DateTime.UtcNow, null));
    }

    [Fact]
    public void Text_respects_length_and_pattern_and_rejects_control_characters()
    {
        var f = Def(CustomFieldType.Text, max: 5, pattern: "[A-Z]{2}\\d{2}");
        Assert.NotNull(f.Normalize("AB12", out var e)); Assert.Null(e);
        Assert.Null(f.Normalize("ab12", out e)); Assert.Contains("format", e);
        Assert.Null(f.Normalize("AB1234", out e)); Assert.Contains("5 characters", e);
        Assert.Null(Def(CustomFieldType.Text).Normalize("a\u0001b", out e)); Assert.Contains("control", e);
    }

    [Fact]
    public void Number_is_parsed_invariantly_and_range_checked()
    {
        var f = Def(CustomFieldType.Number, min: 0, max: 1000);
        Assert.Equal(12.5m, f.Normalize("12.5", out _)!.Number);
        Assert.Null(f.Normalize("12,5x", out var e)); Assert.Contains("number", e);
        Assert.Null(f.Normalize("-1", out e)); Assert.Contains("at least", e);
        Assert.Null(f.Normalize("1001", out e)); Assert.Contains("at most", e);
        Assert.Null(f.Normalize("1e30", out e));
    }

    [Fact]
    public void Dates_must_be_iso_and_booleans_accept_yes_no()
    {
        Assert.Equal(new DateTime(2026, 3, 9), Def(CustomFieldType.Date).Normalize("2026-03-09", out _)!.Date);
        Assert.Null(Def(CustomFieldType.Date).Normalize("09/03/2026", out var e)); Assert.Contains("yyyy-MM-dd", e);
        Assert.Null(Def(CustomFieldType.Date).Normalize("2026-02-30", out e));
        Assert.True(Def(CustomFieldType.Bool).Normalize("Yes", out _)!.Bool);
        Assert.False(Def(CustomFieldType.Bool).Normalize("0", out _)!.Bool);
        Assert.Null(Def(CustomFieldType.Bool).Normalize("maybe", out e));
    }

    [Fact]
    public void Select_matches_options_case_insensitively_and_multi_select_accepts_lists()
    {
        var s = Def(CustomFieldType.Select, ["Red", "Green"]);
        Assert.Equal("Red", s.Normalize("red", out _)!.Text);
        Assert.Null(s.Normalize("Blue", out var e)); Assert.Contains("one of", e);
        var m = Def(CustomFieldType.MultiSelect, ["Pool", "Spa", "Gym"]);
        var v = m.Normalize("pool; Spa|pool", out _)!;
        Assert.Equal("|pool|spa|", v.Search); Assert.Equal("Pool, Spa", v.Display);
        Assert.Equal("|gym|", m.Normalize("[\"Gym\"]", out _)!.Search);
        Assert.Null(m.Normalize("Pool, Sauna", out e)); Assert.Contains("Sauna", e);
        Assert.Null(m.Normalize("[not json", out e));
    }

    [Fact]
    public void Required_empty_values_are_errors_and_optional_empty_means_no_value()
    {
        Assert.Null(Def(CustomFieldType.Text, required: true).Normalize("  ", out var e)); Assert.Contains("required", e);
        Assert.Null(Def(CustomFieldType.Text).Normalize("", out e)); Assert.Null(e);
    }

    [Fact]
    public void Definitions_validate_their_own_shape()
    {
        Assert.Throws<DomainException>(() => Def(CustomFieldType.Select, ["only"]));
        Assert.Throws<DomainException>(() => Def(CustomFieldType.Select, ["a", "A"]));
        Assert.Throws<DomainException>(() => Def(CustomFieldType.Text, options: ["a", "b"]));
        Assert.Throws<DomainException>(() => Def(CustomFieldType.Bool, min: 1));
        Assert.Throws<DomainException>(() => Def(CustomFieldType.Number, min: 5, max: 1));
        Assert.Throws<DomainException>(() => Def(CustomFieldType.Number, pattern: "a"));
        Assert.Throws<DomainException>(() => Def(CustomFieldType.Text, pattern: "(a)\\1"));
    }

    [Fact]
    public void A_catastrophic_pattern_cannot_hang_validation()
    {
        var f = Def(CustomFieldType.Text, pattern: "(a+)+$");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        f.Normalize(new string('a', 400) + "!", out _);
        Assert.True(sw.ElapsedMilliseconds < 1000);
    }

    // ---- scoring and workflow conditions over custom fields

    [Theory]
    [InlineData("student_count gte 500", true)] [InlineData("student_count gt 500", false)] [InlineData("student_count lt 600", true)] [InlineData("student_count eq 500", true)]
    [InlineData("board eq federal", true)] [InlineData("board neq federal", false)] [InlineData("amenities contains pool", true)] [InlineData("amenities contains sauna", false)]
    [InlineData("accredited eq yes", true)] [InlineData("opened lt 2020-01-01", false)] [InlineData("opened gte 2020-01-01", true)] [InlineData("missing is_empty", true)] [InlineData("board is_not_empty", true)] [InlineData("missing eq x", false)] [InlineData("missing neq x", true)]
    public void Scoring_rules_on_custom_fields_match_by_type(string target, bool expected)
    {
        var values = new Dictionary<string, object?> { ["student_count"] = 500m, ["board"] = "Federal", ["amenities"] = (IReadOnlyCollection<string>)new List<string> { "Pool", "Spa" }, ["accredited"] = true, ["opened"] = "2021-06-01" };
        var facts = new LeadFacts(null, null, null, null, null, null, false, 0, false, false, false, false, false, 0, StageKind.Open, 0, new Dictionary<string, int>(), new HashSet<string>(), values);
        var r = LeadScoringEngine.Score(facts, [new ScoringRuleSpec(null, ScoreDimension.CustomField, target, 10, null)]);
        Assert.Equal(expected ? 10 : 0, r.Score);
    }

    [Theory]
    [InlineData("nonsense")] [InlineData("a b c d")] [InlineData("key gte")] [InlineData("key is_empty value")] [InlineData("key weird 5")] [InlineData("Key gte 5")]
    public void Malformed_custom_field_rules_are_rejected(string target) => Assert.NotNull(ScoringRuleParser.Validate(ScoreDimension.CustomField, target, 5));

    [Fact]
    public void A_rule_on_a_missing_value_never_matches_a_comparison()
    {
        var facts = new LeadFacts(null, null, null, null, null, null, false, 0, false, false, false, false, false, 0, StageKind.Open, 0, new Dictionary<string, int>(), new HashSet<string>(), new Dictionary<string, object?>());
        Assert.Equal(0, LeadScoringEngine.Score(facts, [new ScoringRuleSpec(null, ScoreDimension.CustomField, "student_count gte 1", 10, null)]).Score);
    }

    [Fact]
    public void A_sub_category_lead_also_matches_a_rule_on_its_parent_category()
    {
        var facts = new LeadFacts("Private schools", null, null, null, null, null, false, 0, false, false, false, false, false, 0, StageKind.Open, 0, new Dictionary<string, int>(), new HashSet<string>(), null, ["Education"]);
        Assert.Equal(7, LeadScoringEngine.Score(facts, [new ScoringRuleSpec(null, ScoreDimension.BusinessCategory, "education", 7, null)]).Score);
    }

    [Fact]
    public void Workflow_conditions_can_use_custom_fields_with_typed_values()
    {
        var c = ConditionParser.Parse("""{"all":[{"field":"lead.cf.student_count","op":"gte","value":500},{"field":"lead.cf.board","op":"eq","value":"Federal"},{"field":"lead.cf.accredited","op":"eq","value":true}]}""");
        Assert.True(c.IsValid, string.Join(";", c.Errors));
        var facts = new Dictionary<string, object?> { ["lead.cf.student_count"] = 800m, ["lead.cf.board"] = "federal", ["lead.cf.accredited"] = true };
        Assert.True(ConditionEvaluator.Evaluate(c.Condition!, facts, DateTime.UtcNow, TimeZoneInfo.Utc));
        facts["lead.cf.student_count"] = 100m;
        Assert.False(ConditionEvaluator.Evaluate(c.Condition!, facts, DateTime.UtcNow, TimeZoneInfo.Utc));
        Assert.Equal(["student_count", "board", "accredited"], ConditionParser.Parse("""{"all":[{"field":"lead.cf.student_count","op":"gte","value":1},{"field":"lead.cf.board","op":"eq","value":"x"},{"field":"lead.cf.accredited","op":"eq","value":true}]}""").Condition is { } cc ? ConditionFields.CustomKeysIn(cc) : []);
    }

    [Theory]
    [InlineData("""{"field":"lead.cf.Bad Key","op":"eq","value":1}""")]
    [InlineData("""{"field":"lead.cf.x","op":"starts_with","value":"a"}""")]
    [InlineData("""{"field":"lead.cf.x","op":"eq"}""")]
    [InlineData("""{"field":"lead.cf.x","op":"eq","value":{"a":1}}""")]
    public void Malformed_custom_conditions_are_rejected(string json) => Assert.False(ConditionParser.Parse(json).IsValid);

    [Fact]
    public void Filters_are_built_from_query_keys_and_ignore_junk()
    {
        var f = CustomFieldFilters.FromQuery(new Dictionary<string, string?> { ["cf.board"] = "Federal", ["cf.rooms.min"] = "10", ["cf.rooms.max"] = "50", ["cf.x.weird"] = "1", ["cf.Bad Key"] = "1", ["search"] = "x", ["cf.empty"] = "" });
        Assert.Equal(3, f.Count); Assert.Contains(f, x => x is { Key: "rooms", Op: "min", Value: "10" });
    }
}
