using LeadPilot.Domain.Crm;

namespace LeadPilot.UnitTests;

public class NormalizationTests
{
    [Theory]
    [InlineData("Al-Noor International School", "al noor international school")]
    [InlineData("  ACME   Trading Ltd. ", "acme trading")]
    [InlineData("Acme Trading LLC", "acme trading")]
    [InlineData("The Bright Future Academy", "bright future academy")]
    [InlineData("Café Müller & Söhne GmbH", "cafe muller and sohne")]
    [InlineData("Ltd", "ltd")] // never reduced to nothing
    public void Names_normalize_for_matching(string raw, string expected) => Assert.Equal(expected, Normalizer.Name(raw));

    [Fact]
    public void Same_business_written_differently_gets_the_same_dedup_key()
    {
        Assert.Equal(Normalizer.DedupKey("Al-Noor School", "Riyadh"), Normalizer.DedupKey("  AL NOOR  school ", "riyadh "));
        Assert.Equal(Normalizer.DedupKey("Acme Ltd", "Dubai"), Normalizer.DedupKey("ACME Limited", "Dubai"));
        Assert.NotEqual(Normalizer.DedupKey("Acme", "Dubai"), Normalizer.DedupKey("Acme", "Abu Dhabi"));
        Assert.NotEqual(Normalizer.DedupKey("Acme School", "Dubai"), Normalizer.DedupKey("Acme Clinic", "Dubai"));
    }

    [Fact]
    public void Arabic_variants_and_diacritics_fold_together()
    {
        Assert.Equal(Normalizer.Name("مدرسة الأمل"), Normalizer.Name("مدرسه الامل"));
        Assert.Equal(Normalizer.Name("مَدْرَسَة النور"), Normalizer.Name("مدرسة النور"));
    }

    [Fact]
    public void Dedup_key_is_capped_so_it_fits_the_indexed_column()
    {
        Assert.True(Normalizer.DedupKey(new string('a', 400), new string('b', 100)).Length <= Normalizer.MaxDedupKeyLength);
    }

    [Theory]
    [InlineData("https://www.Example.com/about?x=1", "example.com")]
    [InlineData("http://example.com:8080/path", "example.com")]
    [InlineData("example.com", "example.com")]
    [InlineData("WWW.Example.COM.", "example.com")]
    [InlineData("https://user:pw@shop.example.co.uk/#top", "shop.example.co.uk")]
    [InlineData("example.com:8443", "example.com")]
    [InlineData("https://bücher.example/", "xn--bcher-kva.example")]
    public void Website_domains_normalize(string raw, string expected) => Assert.Equal(expected, Normalizer.Domain(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost")]
    [InlineData("not a url")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com")]
    [InlineData("mailto:a@example.com")]
    [InlineData("https://exa mple.com")]
    [InlineData("http://-bad-.com")]
    [InlineData("https://example.com:99999")]
    public void Invalid_websites_have_no_domain(string raw) => Assert.Null(Normalizer.Domain(raw));

    [Theory]
    [InlineData("+966 50 123 4567", null, "+966501234567")]
    [InlineData("00966501234567", null, "+966501234567")]
    [InlineData("+1 (415) 555-2671", null, "+14155552671")]
    [InlineData("+44 20 7946 0958", null, "+442079460958")]
    [InlineData("0501234567", "966", "+966501234567")]
    [InlineData("0501234567", "+966", "+966501234567")]
    [InlineData("966.501.234.567", null, null)]   // no "+" / "00" and no leading 0: ambiguous
    [InlineData("00501234567", "966", "+501234567")] // "00" always means international, whatever the default country
    public void Phones_normalize_to_E164(string raw, string? cc, string? expected) => Assert.Equal(expected, Normalizer.PhoneE164(raw, cc));

    [Theory]
    [InlineData("0501234567", null)]          // local number without a default country code
    [InlineData("+0501234567", null)]         // country code cannot start with 0
    [InlineData("+123", null)]                // too short
    [InlineData("+1234567890123456", null)]   // too long (16 digits)
    [InlineData("call me", null)]
    [InlineData("+966-50-abc-4567", null)]
    [InlineData("+966+501234567", null)]
    [InlineData("", null)]
    [InlineData("0012", "966")]               // "00" is the international prefix: too short after it
    public void Invalid_phones_are_rejected(string raw, string? cc) => Assert.Null(Normalizer.PhoneE164(raw, cc));

    [Fact]
    public void Equivalent_phone_spellings_match()
    {
        var a = Normalizer.PhoneE164("+966 (50) 123-4567");
        Assert.Equal(a, Normalizer.PhoneE164("00966501234567"));
        Assert.Equal(a, Normalizer.PhoneE164("050 123 4567", "966"));
    }

    [Theory]
    [InlineData("Sales@Example.COM ", "sales@example.com")]
    [InlineData("a.b+tag@sub.example.org", "a.b+tag@sub.example.org")]
    public void Emails_normalize(string raw, string expected) => Assert.Equal(expected, Normalizer.Email(raw));

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    [InlineData("a@b.com, c@d.com")]
    [InlineData("Name <a@b.com>")]
    [InlineData("a b@c.com")]
    [InlineData("")]
    public void Invalid_emails_are_rejected(string raw) => Assert.Null(Normalizer.Email(raw));

    [Fact]
    public void Suppression_values_normalize_per_channel()
    {
        Assert.Equal("+966501234567", SuppressionEntry.NormalizeValue(SuppressionChannel.WhatsApp, "00966 50 123 4567"));
        Assert.Equal("x@y.com", SuppressionEntry.NormalizeValue(SuppressionChannel.Email, " X@Y.com "));
        Assert.Null(SuppressionEntry.NormalizeValue(SuppressionChannel.Email, "+966501234567"));
        Assert.Null(SuppressionEntry.NormalizeValue(SuppressionChannel.Sms, "x@y.com"));
        Assert.Equal("x@y.com", SuppressionEntry.NormalizeValue(SuppressionChannel.Any, "x@y.com"));
        Assert.Equal("+966501234567", SuppressionEntry.NormalizeValue(SuppressionChannel.Any, "+966501234567"));
    }
}
