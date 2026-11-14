using LeadPilot.Application.Discovery;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Discovery;

namespace LeadPilot.UnitTests;

public class ConfidenceCalculatorTests
{
    private static ConfidenceInputs Inputs(SourceKind s = SourceKind.StructuredProvider, bool name = true, bool addr = false, bool web = false, bool phone = false, bool email = false, bool cat = false, bool confirmed = false, bool corr = false, bool closed = false)
        => new(s, name, addr, web, phone, email, cat, confirmed, corr, closed);

    [Fact] public void No_name_means_zero_confidence() => Assert.Equal(0, ConfidenceCalculator.Calculate(Inputs(name: false, addr: true, web: true, phone: true)));

    [Theory]
    [InlineData(SourceKind.StructuredProvider, 35)]
    [InlineData(SourceKind.AuthorizedDirectory, 30)]
    [InlineData(SourceKind.UserSupplied, 30)]
    [InlineData(SourceKind.WebsiteSelfPublished, 25)]
    public void Source_base_points(SourceKind s, int expected) => Assert.Equal(expected, ConfidenceCalculator.Calculate(Inputs(s)));

    [Fact]
    public void Each_field_adds_its_documented_points()
    {
        Assert.Equal(35 + 10, ConfidenceCalculator.Calculate(Inputs(addr: true)));
        Assert.Equal(35 + 10, ConfidenceCalculator.Calculate(Inputs(web: true)));
        Assert.Equal(35 + 15, ConfidenceCalculator.Calculate(Inputs(phone: true)));
        Assert.Equal(35 + 5, ConfidenceCalculator.Calculate(Inputs(email: true)));
        Assert.Equal(35 + 10, ConfidenceCalculator.Calculate(Inputs(cat: true)));
        Assert.Equal(35 + 10, ConfidenceCalculator.Calculate(Inputs(confirmed: true)));
        Assert.Equal(35 + 5, ConfidenceCalculator.Calculate(Inputs(corr: true)));
    }

    [Fact] public void A_complete_provider_record_is_capped_at_100() => Assert.Equal(100, ConfidenceCalculator.Calculate(Inputs(addr: true, web: true, phone: true, email: true, cat: true, confirmed: true, corr: true)));
    [Fact] public void A_closed_business_loses_40_points_and_never_goes_below_zero()
    {
        Assert.Equal(35 + 10 + 15 - 40, ConfidenceCalculator.Calculate(Inputs(addr: true, phone: true, closed: true)));
        Assert.Equal(0, ConfidenceCalculator.Calculate(Inputs(SourceKind.WebsiteSelfPublished, closed: true)));
    }

    [Theory]
    [InlineData(100, ConfidenceLevel.High)] [InlineData(75, ConfidenceLevel.High)] [InlineData(74, ConfidenceLevel.Medium)]
    [InlineData(50, ConfidenceLevel.Medium)] [InlineData(49, ConfidenceLevel.Low)] [InlineData(0, ConfidenceLevel.Low)]
    public void Levels(int score, ConfidenceLevel level) => Assert.Equal(level, ConfidenceCalculator.Level(score));

    [Fact] public void Confidence_never_depends_on_anything_that_could_imply_consent() =>
        Assert.DoesNotContain(typeof(ConfidenceInputs).GetProperties(), p => p.Name.Contains("Consent", StringComparison.OrdinalIgnoreCase));
}

public class DiscoveryQueryBuilderTests
{
    [Fact]
    public void Builds_one_query_per_category_phrase_with_the_full_location()
    {
        var q = DiscoveryQueryBuilder.Build(new SearchLocation("Pakistan", "Punjab", "Lahore", "Model Town", "Block C"), ["private school", "academy"], "school", "girls");
        Assert.Equal(2, q.Count);
        Assert.Equal("private school girls in Block C, Model Town, Lahore, Punjab, Pakistan", q[0].Text);
        Assert.Equal("academy girls in Block C, Model Town, Lahore, Punjab, Pakistan", q[1].Text);
        Assert.All(q, x => { Assert.Equal("PK", x.RegionCode); Assert.Equal("school", x.PlaceType); });
    }

    [Fact] public void Skips_blank_location_parts() => Assert.Equal("clinic in Dubai", DiscoveryQueryBuilder.Build(new SearchLocation(null, " ", "Dubai", "", null), ["clinic"], null, null)[0].Text);
    [Fact] public void Keywords_only_search_is_allowed_and_has_no_place_type() { var q = DiscoveryQueryBuilder.Build(new SearchLocation("Kenya", null, "Nairobi", null, null), [], "school", "coffee roasters"); Assert.Single(q); Assert.Equal("coffee roasters in Nairobi, Kenya", q[0].Text); Assert.Null(q[0].PlaceType); }
    [Fact] public void Nothing_to_search_for_gives_no_queries() => Assert.Empty(DiscoveryQueryBuilder.Build(new SearchLocation("Pakistan", null, null, null, null), [" "], null, null));
    [Fact] public void Duplicate_phrases_are_collapsed() => Assert.Single(DiscoveryQueryBuilder.Build(new SearchLocation("Pakistan", null, "Lahore", null, null), ["School", "school "], null, null));
    [Fact] public void Unknown_countries_get_no_region_code_and_are_not_rejected() { Assert.Null(DiscoveryQueryBuilder.RegionCode("Atlantis")); Assert.Equal("PK", DiscoveryQueryBuilder.RegionCode("pakistan")); }
    [Fact] public void Categories_are_data_the_starter_pack_includes_the_school_categories_and_nothing_else_is_required()
    {
        var pack = DiscoveryStarterPacks.Find(DiscoveryStarterPacks.EducationPakistan)!;
        var names = pack.Categories.Select(c => c.Name).ToList();
        foreach (var n in new[] { "Private schools", "Primary schools", "Secondary schools", "Higher secondary schools", "Colleges", "Academies", "Educational institutions" }) Assert.Contains(n, names);
        Assert.Contains(pack.Locations, l => l.City == "Karachi");
        Assert.Equal(DiscoveryStarterPacks.EducationPakistan, DiscoveryStarterPacks.AutoPackForIndustry(" education "));
        Assert.Null(DiscoveryStarterPacks.AutoPackForIndustry("Retail"));
        Assert.Null(DiscoveryStarterPacks.AutoPackForIndustry(null));
    }
}

public class CandidateNormalizerTests
{
    private static DiscoveryCandidate C(string? name = "  Al   Noor\tSchool ", string? web = "WWW.AlNoor.edu.pk/#top", string? phone = "+92 21 3456 7890", string? email = " Info@AlNoor.edu.pk ") =>
        new() { ExternalId = " p1 ", Name = name, Website = web, Phone = phone, Email = email, Types = ["primary_school", "school"], City = "Karachi" };

    [Fact]
    public void Cleans_every_field()
    {
        var n = CandidateNormalizer.Normalize(C(), null);
        Assert.Equal("p1", n.ExternalId); Assert.Equal("Al Noor School", n.Name); Assert.Equal("https://www.alnoor.edu.pk/", n.Website?.ToLowerInvariant());
        Assert.Equal("+922134567890", n.PhoneE164); Assert.Equal("info@alnoor.edu.pk", n.Email); Assert.Equal("primary school, school", n.CategoryLabel);
    }

    [Fact] public void Unknown_values_stay_null_and_are_never_invented()
    {
        var n = CandidateNormalizer.Normalize(new DiscoveryCandidate { ExternalId = "x", Name = "Only A Name" }, "92");
        Assert.Null(n.Website); Assert.Null(n.PhoneRaw); Assert.Null(n.PhoneE164); Assert.Null(n.Email); Assert.Null(n.Address); Assert.Null(n.CategoryLabel); Assert.Empty(n.SocialLinks);
    }

    [Theory] [InlineData("javascript:alert(1)")] [InlineData("ftp://x.example/")] [InlineData("http://user:pw@school.example/")] [InlineData("not a url")] [InlineData("http://localhost/")]
    public void Unsafe_or_invalid_websites_are_dropped(string url) => Assert.Null(CandidateNormalizer.NormalizeWebsite(url));

    [Fact] public void Local_phone_numbers_use_the_tenant_country_code_or_stay_unnormalised()
    {
        Assert.Equal("+923001234567", CandidateNormalizer.Normalize(C(phone: "0300 1234567"), "92").PhoneE164);
        Assert.Null(CandidateNormalizer.Normalize(C(phone: "0300 1234567"), null).PhoneE164);
    }

    [Fact] public void Invalid_email_becomes_null() => Assert.Null(CandidateNormalizer.Normalize(C(email: "not-an-email"), null).Email);
}

public class UsageLimitPolicyTests
{
    [Fact] public void Allows_below_the_limit() { var d = UsageLimitPolicy.Check(99, 0, 100, null); Assert.True(d.Allowed); Assert.Equal(0, d.RemainingCalls); }
    [Fact] public void Blocks_at_the_limit_with_a_reason() { var d = UsageLimitPolicy.Check(100, 0, 100, null); Assert.False(d.Allowed); Assert.Contains("100", d.Reason); }
    [Fact] public void A_zero_limit_blocks_everything() => Assert.False(UsageLimitPolicy.Check(0, 0, 0, null).Allowed);
    [Fact] public void Blocks_when_a_request_would_cross_the_limit() => Assert.False(UsageLimitPolicy.Check(98, 0, 100, null, callsRequested: 3).Allowed);
    [Fact] public void Budget_is_enforced_with_the_estimated_cost() { Assert.True(UsageLimitPolicy.Check(1, 0.30m, 100, 0.40m, 1, 0.035m).Allowed); Assert.False(UsageLimitPolicy.Check(1, 0.39m, 100, 0.40m, 1, 0.035m).Allowed); }
    [Fact] public void A_period_key_is_year_and_month() { Assert.Equal(202611, DiscoveryUsageEntry.PeriodOf(new DateTime(2026, 11, 30, 23, 59, 0, DateTimeKind.Utc))); Assert.Equal(202701, DiscoveryUsageEntry.PeriodOf(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc))); }
}

public class RobotsTxtTests
{
    private const string Ua = "LeadPilotAI-Bot";

    [Fact] public void No_rules_allows_everything() { Assert.True(RobotsTxt.Parse("").IsAllowed(Ua, "/any")); Assert.True(RobotsTxt.AllowEverything.IsAllowed(Ua, "/")); }
    [Fact] public void Unreachable_robots_blocks_everything() => Assert.False(RobotsTxt.BlockEverything.IsAllowed(Ua, "/"));
    [Fact] public void Star_group_disallow_all() => Assert.False(RobotsTxt.Parse("User-agent: *\nDisallow: /").IsAllowed(Ua, "/index.html"));
    [Fact] public void Empty_disallow_allows_all() => Assert.True(RobotsTxt.Parse("User-agent: *\nDisallow:").IsAllowed(Ua, "/x"));
    [Fact] public void Specific_group_beats_the_star_group() { var r = RobotsTxt.Parse("User-agent: *\nDisallow: /\n\nUser-agent: LeadPilotAI-Bot\nDisallow: /private/"); Assert.True(r.IsAllowed(Ua, "/about")); Assert.False(r.IsAllowed(Ua, "/private/x")); Assert.False(r.IsAllowed("OtherBot", "/about")); }
    [Fact] public void Our_token_blocked_by_name() => Assert.False(RobotsTxt.Parse("User-agent: leadpilotai-bot\nDisallow: /").IsAllowed("LeadPilotAI-Bot/1.0", "/"));
    [Fact] public void Longest_match_wins_and_allow_wins_a_tie() { var r = RobotsTxt.Parse("User-agent: *\nDisallow: /a\nAllow: /a/public\nDisallow: /b\nAllow: /b"); Assert.False(r.IsAllowed(Ua, "/a/secret")); Assert.True(r.IsAllowed(Ua, "/a/public/page")); Assert.True(r.IsAllowed(Ua, "/b/x")); }
    [Fact] public void Wildcards_and_end_anchors() { var r = RobotsTxt.Parse("User-agent: *\nDisallow: /*.pdf$\nDisallow: /tmp*/cache"); Assert.False(r.IsAllowed(Ua, "/docs/a.pdf")); Assert.True(r.IsAllowed(Ua, "/docs/a.pdf?x=1")); Assert.False(r.IsAllowed(Ua, "/tmp123/cache")); Assert.True(r.IsAllowed(Ua, "/docs/a.html")); }
    [Fact] public void Comments_blank_lines_and_consecutive_agents() { var r = RobotsTxt.Parse("# hi\nUser-agent: badbot\nUser-agent: leadpilotai-bot # us\nDisallow: /x # no\n\nUser-agent: *\nDisallow:"); Assert.False(r.IsAllowed(Ua, "/x/1")); Assert.True(r.IsAllowed(Ua, "/y")); }
    [Fact] public void Query_strings_are_part_of_the_match() => Assert.False(RobotsTxt.Parse("User-agent: *\nDisallow: /search?").IsAllowed(Ua, "/search?q=1"));
}

public class DiscoveryMatchingTests
{
    [Fact] public void Shared_distinctive_word() { Assert.True(DiscoveryMatching.SharesDistinctiveWord("Beaconhouse School Gulberg", "Beaconhouse – Home")); Assert.False(DiscoveryMatching.SharesDistinctiveWord("Beaconhouse School", "Welcome to our school")); Assert.False(DiscoveryMatching.SharesDistinctiveWord(null, "x")); }
    [Fact] public void Category_matches_by_type_or_phrase() { Assert.True(DiscoveryMatching.CategoryMatches(["x"], "primary_school", "A", ["school", "primary_school"])); Assert.True(DiscoveryMatching.CategoryMatches(["academy"], null, "Bright Academy", [])); Assert.False(DiscoveryMatching.CategoryMatches(["academy"], null, "Joe's Cafe", ["cafe"])); }
    [Fact] public void Phones_match_across_formats() { Assert.True(DiscoveryMatching.SamePhone("+92 21 3456 7890", "(021) 34567890")); Assert.False(DiscoveryMatching.SamePhone("+92 21 3456 7890", "021 3456 7891")); Assert.False(DiscoveryMatching.SamePhone("123", "123")); }
}

public class DiscoveryDomainTests
{
    private static DiscoveryJob Job(int max = 40) => DiscoveryJob.Create(Guid.NewGuid(), "google_places", "Pakistan", null, "Lahore", null, null, null, "school", null, max, true, Guid.NewGuid(), "u@x.test", DateTime.UtcNow);

    [Fact] public void Jobs_validate_their_limits() { Assert.Throws<DomainException>(() => Job(0)); Assert.Throws<DomainException>(() => Job(501)); }
    [Fact] public void Cancelling_a_queued_job_cancels_it_immediately_a_running_one_only_flags_it()
    {
        var q = Job(); q.RequestCancel(DateTime.UtcNow); Assert.Equal(DiscoveryJobStatus.Cancelled, q.Status);
        var r = Job(); r.Start(DateTime.UtcNow); r.RequestCancel(DateTime.UtcNow); Assert.Equal(DiscoveryJobStatus.Running, r.Status); Assert.True(r.CancelRequested);
    }
    [Fact] public void Finished_jobs_cannot_be_cancelled_and_only_failed_or_cancelled_can_be_retried()
    {
        var j = Job(); j.Start(DateTime.UtcNow); j.Complete(DateTime.UtcNow);
        Assert.Throws<DomainException>(() => j.RequestCancel(DateTime.UtcNow)); Assert.Throws<DomainException>(() => j.PrepareRetry());
        var f = Job(); f.Start(DateTime.UtcNow); f.Fail("provider_unavailable", "x", true, DateTime.UtcNow); f.PrepareRetry(); Assert.Equal(DiscoveryJobStatus.Queued, f.Status); Assert.Equal(1, f.Attempts);
    }
    [Fact] public void A_stale_running_job_may_be_taken_over_a_fresh_one_may_not()
    {
        var j = Job(); var t = DateTime.UtcNow; j.Start(t);
        Assert.False(j.CanStart(t.AddMinutes(5), TimeSpan.FromMinutes(30))); Assert.True(j.CanStart(t.AddMinutes(31), TimeSpan.FromMinutes(30)));
    }
    [Fact] public void Results_can_be_decided_once_and_purged_results_cannot_be_approved()
    {
        DiscoveryResult R() => DiscoveryResult.Create(Guid.NewGuid(), Guid.NewGuid(), new("google_places", "p1", "Name", null, null, null, null, null, null, null, null, null, null, null, null, "src", null, null, DateTime.UtcNow, DateTime.UtcNow.AddDays(30), DateTime.UtcNow, 80, null, null, EnrichmentStatus.NotRequested), null, null, DateTime.UtcNow);
        var a = R(); a.Approve(Guid.NewGuid(), ApprovalAction.Created, null, DateTime.UtcNow); Assert.Throws<DomainException>(() => a.Reject(null, null, DateTime.UtcNow)); Assert.Throws<DomainException>(() => a.Approve(Guid.NewGuid(), ApprovalAction.Created, null, DateTime.UtcNow));
        var p = R(); p.PurgeContent(DateTime.UtcNow); Assert.Null(p.Name); Assert.Equal("p1", p.ExternalId); Assert.Throws<DomainException>(() => p.Approve(Guid.NewGuid(), ApprovalAction.Created, null, DateTime.UtcNow));
        Assert.Equal(ConfidenceLevel.High, R().ConfidenceLevel);
    }
    [Fact] public void Usage_ledger_and_results_never_expose_public_mutators()
    {
        Assert.True(typeof(IAppendOnly).IsAssignableFrom(typeof(DiscoveryUsageEntry)));
        Assert.Throws<DomainException>(() => DiscoveryConnectorConfig.Create(Guid.NewGuid(), "x", DateTime.UtcNow).SetLimits(-1, null, null, DateTime.UtcNow));
        Assert.Throws<DomainException>(() => DiscoveryCategoryMapping.Create(Guid.NewGuid(), Guid.NewGuid(), " ", null, DateTime.UtcNow));
        Assert.Equal(["a", "b"], DiscoveryCategoryMapping.Create(Guid.NewGuid(), Guid.NewGuid(), "a;b\nA", null, DateTime.UtcNow).Terms);
    }
}
