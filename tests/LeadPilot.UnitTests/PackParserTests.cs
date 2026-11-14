using System.Text;
using System.Text.Json.Nodes;
using LeadPilot.Application.Config;
using LeadPilot.Infrastructure.Config;

namespace LeadPilot.UnitTests;

public class PackParserTests
{
    private const string Minimal = """{"format":"leadpilot.pack","formatVersion":1,"key":"demo-pack","version":1,"name":"Demo"}""";

    private static string With(Action<JsonObject> edit) { var o = JsonNode.Parse(Minimal)!.AsObject(); edit(o); return o.ToJsonString(); }
    private static JsonNode J(string json) => JsonNode.Parse(json)!;
    private static string FirstProblem(string json) { var r = PackParser.Parse(json); Assert.False(r.IsValid); return r.Problems[0].Message + " @ " + r.Problems[0].Path; }

    [Fact]
    public void A_minimal_pack_is_valid_and_hashed_deterministically()
    {
        var a = PackParser.Parse(Minimal); var b = PackParser.Parse(Minimal.Replace(":", " : "));
        Assert.True(a.IsValid); Assert.Equal(a.Hash, b.Hash);
    }

    [Fact]
    public void Every_builtin_pack_is_valid_sample_marked_and_has_a_unique_key()
    {
        var packs = PackSeeder.Embedded().ToList();
        Assert.True(packs.Count >= 3);
        var keys = new HashSet<string>();
        foreach (var (name, json) in packs)
        {
            var r = PackParser.Parse(json);
            Assert.True(r.IsValid, name + ": " + string.Join("; ", r.Problems.Select(p => p.Path + " " + p.Message)));
            Assert.True(keys.Add(r.Pack!.Key));
            Assert.True(r.Pack.Sample); Assert.Contains("SAMPLE", r.Pack.Notice);
        }
        Assert.Contains("education", keys); Assert.Contains("hotels-hospitality", keys); Assert.Contains("real-estate", keys);
    }

    [Fact]
    public void Builtin_packs_invent_no_prices_and_confirm_nothing()
    {
        foreach (var (_, json) in PackSeeder.Embedded())
        {
            var p = PackParser.Parse(json).Pack!;
            Assert.All(p.Catalog.SelectMany(c => c.Plans), plan => Assert.Null(plan.Price));
            Assert.All(p.Catalog, c => Assert.Contains("[PLACEHOLDER]", c.Summary ?? c.Name + c.Name));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"format\":\"leadpilot.pack\",\"formatVersion\":1,\"key\":\"x\"}")]
    public void Malformed_input_is_rejected_without_throwing(string json) => Assert.False(PackParser.Parse(json).IsValid);

    [Fact]
    public void An_oversized_pack_is_rejected() =>
        Assert.Contains("larger", FirstProblem(With(o => o["description"] = new string('a', PackParser.MaxBytes))));

    [Fact]
    public void Deeply_nested_json_is_rejected() =>
        Assert.False(PackParser.Parse(Minimal.TrimEnd('}') + ",\"x\":" + new string('[', 40) + new string(']', 40) + "}").IsValid);

    [Fact]
    public void Unknown_keys_are_rejected_at_every_level()
    {
        Assert.Contains("Unknown key", FirstProblem(With(o => o["script"] = "x")));
        Assert.Contains("Unknown key", FirstProblem(With(o => o["categories"] = J("""[{"key":"a","name":"A","onload":"x"}]"""))));
    }

    [Fact]
    public void Duplicate_keys_in_the_json_are_rejected() =>
        Assert.Contains("twice", FirstProblem("""{"format":"leadpilot.pack","formatVersion":1,"key":"demo-pack","version":1,"name":"A","name":"B"}"""));

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("hello <b>x</b>")]
    [InlineData("a &#60;script&#62;")]
    [InlineData("=HYPERLINK(\"http://evil\")")]
    [InlineData("@SUM(A1)")]
    [InlineData("a\u0000b")]
    [InlineData("a‮b")]
    [InlineData("{{name}} in a name")]
    public void Hostile_strings_are_rejected(string name) => Assert.False(PackParser.Parse(With(o => o["categories"] = new JsonArray(new JsonObject { ["key"] = "a", ["name"] = name }))).IsValid);

    [Fact]
    public void Sql_like_text_is_just_data_and_stays_plain_text()
    {
        var r = PackParser.Parse(With(o => o["categories"] = new JsonArray(new JsonObject { ["key"] = "a", ["name"] = "Robert'); DROP TABLE Leads;--" })));
        Assert.True(r.IsValid); Assert.Equal("Robert'); DROP TABLE Leads;--", r.Pack!.Categories[0].Name);
    }

    [Fact]
    public void References_are_checked()
    {
        Assert.Contains("not in the pack", FirstProblem(With(o => o["categories"] = J("""[{"key":"a","name":"A","parent":"missing"}]"""))));
        Assert.Contains("loop", FirstProblem(With(o => o["categories"] = J("""[{"key":"a","name":"A","parent":"b"},{"key":"b","name":"B","parent":"a"}]"""))));
        Assert.Contains("deep", FirstProblem(With(o => o["categories"] = J("""[{"key":"a","name":"A"},{"key":"b","name":"B","parent":"a"},{"key":"c","name":"C","parent":"b"},{"key":"d","name":"D","parent":"c"},{"key":"e","name":"E","parent":"d"}]"""))));
        Assert.Contains("used twice", FirstProblem(With(o => o["categories"] = J("""[{"key":"a","name":"A"},{"key":"a","name":"B"}]"""))));
        Assert.Contains("not in the pack", FirstProblem(With(o => o["discovery"] = J("""{"categories":[{"key":"s","category":"nope","searchPhrases":["x"]}]}"""))));
    }

    [Fact]
    public void Field_template_rule_and_pipeline_values_run_through_the_normal_domain_rules()
    {
        Assert.Contains("options", FirstProblem(With(o => o["customFields"] = J("""[{"key":"f","entity":"Lead","label":"F","type":"Select","options":["only"]}]"""))));
        Assert.Contains("Type is", FirstProblem(With(o => o["customFields"] = J("""[{"key":"f","entity":"Lead","label":"F","type":"Json"}]"""))));
        Assert.Contains("pattern", FirstProblem(With(o => o["customFields"] = J("""[{"key":"f","entity":"Lead","label":"F","type":"Text","pattern":"(?<=a)b"}]"""))));
        Assert.Contains("Unknown placeholder", FirstProblem(With(o => o["messageTemplates"] = J("""[{"key":"t","name":"T","body":"Hi {{secret}}","category":"Service"}]"""))));
        Assert.Contains("rules look like", FirstProblem(With(o => o["scoringRules"] = J("""[{"dimension":"CustomField","target":"nonsense","points":5}]"""))));
        Assert.Contains("from -100 to 100", FirstProblem(With(o => o["scoringRules"] = J("""[{"dimension":"Location","target":"City=X","points":500}]"""))));
        Assert.Contains("Won and a Lost", FirstProblem(With(o => o["pipelines"] = J("""[{"key":"p","name":"P","stages":[{"name":"a","kind":"Open"},{"name":"b","kind":"Open"}]}]"""))));
        Assert.Contains("price", FirstProblem(With(o => o["catalog"] = J("""[{"key":"c","kind":"Product","name":"C","plans":[{"key":"p","name":"P","price":-1,"currency":"USD"}]}]"""))));
        Assert.Contains("currency", FirstProblem(With(o => o["catalog"] = J("""[{"key":"c","kind":"Product","name":"C","plans":[{"key":"p","name":"P","currency":"DOLLARS"}]}]"""))));
        Assert.Contains("not a tool", FirstProblem(With(o => o["agents"] = J("""[{"key":"a","name":"A","instructions":"x","tools":["CreatePaymentRequest"]}]"""))));
    }

    [Fact]
    public void A_sample_pack_needs_a_notice() =>
        Assert.Contains("notice", FirstProblem(With(o => o["sample"] = true)));

    [Fact]
    public void Workflows_cannot_carry_people_or_messages_and_are_validated()
    {
        string Wf(string step) => With(o => o["workflows"] = J("""[{"key":"w","name":"W","trigger":{"event":"lead.created"},"steps":[""" + step + "]}]"));
        Assert.Contains("cannot add steps", FirstProblem(Wf("""{"key":"s","type":"SendMessage","name":"S","config":{}}""")));
        Assert.Contains("cannot add steps", FirstProblem(Wf("""{"key":"s","type":"AssignLead","name":"S","config":{}}""")));
        Assert.Contains("owner", FirstProblem(Wf("""{"key":"s","type":"NotifyUser","name":"S","config":{"target":"11111111-1111-1111-1111-111111111111","title":"t","message":"m"}}""")));
        Assert.False(PackParser.Parse(With(o => o["workflows"] = J("""[{"key":"w","name":"W","trigger":{"event":"nope"},"steps":[{"key":"s","type":"Wait","name":"S","config":{"minutes":5}}]}]"""))).IsValid);
        Assert.True(PackParser.Parse(Wf("""{"key":"s","type":"Wait","name":"S","config":{"minutes":5}}""")).IsValid);
    }

    [Fact]
    public void Limits_on_list_sizes_hold()
    {
        var many = new JsonArray(Enumerable.Range(0, 301).Select(i => (JsonNode)new JsonObject { ["key"] = "k" + i, ["name"] = "N" + i }).ToArray());
        Assert.Contains("At most", FirstProblem(With(o => o["categories"] = many)));
    }

    [Theory]
    [InlineData("Hotel Sunrise", "hotel-sunrise")]
    [InlineData("  A & B / C ", "a-b-c")]
    [InlineData("###", "item-")]
    public void Export_keys_are_slugs(string name, string expected) => Assert.StartsWith(expected, PackExporter.Slug(name));

    [Fact]
    public void Untrusted_text_never_reaches_the_encoder_unescaped_because_markup_is_refused_up_front() =>
        Assert.False(PackParser.Parse(With(o => o["name"] = "<img src=x onerror=alert(1)>")).IsValid);

    [Fact]
    public void Cyrillic_arabic_and_cjk_text_is_accepted()
    {
        var r = PackParser.Parse(With(o => o["categories"] = new JsonArray(new JsonObject { ["key"] = "a", ["name"] = "مدارس خاصة" }, new JsonObject { ["key"] = "b", ["name"] = "Школы" }, new JsonObject { ["key"] = "c", ["name"] = "学校" })));
        Assert.True(r.IsValid);
        _ = Encoding.UTF8;
    }
}
