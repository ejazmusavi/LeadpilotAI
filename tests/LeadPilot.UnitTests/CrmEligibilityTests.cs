using LeadPilot.Application.Crm;
using LeadPilot.Domain.Crm;

namespace LeadPilot.UnitTests;

public class EligibilityRuleTests
{
    private static EligibilityFacts Good(Func<EligibilityFacts, EligibilityFacts>? tweak = null)
    {
        var f = new EligibilityFacts(true, false, false, false, "+966501234567", "+966501234567", false, ConsentStatus.Granted);
        return tweak?.Invoke(f) ?? f;
    }

    [Fact]
    public void Granted_consent_valid_value_and_not_suppressed_is_eligible()
    {
        var d = EligibilityRules.Evaluate(Good());
        Assert.True(d.IsEligible);
        Assert.Empty(d.Reasons);
    }

    [Fact]
    public void No_consent_record_blocks()
    {
        var d = EligibilityRules.Evaluate(Good(f => f with { LatestConsent = null }));
        Assert.False(d.IsEligible);
        Assert.Contains(EligibilityReason.NoConsent, d.Reasons);
    }

    [Fact]
    public void Revoked_consent_blocks()
    {
        var d = EligibilityRules.Evaluate(Good(f => f with { LatestConsent = ConsentStatus.Revoked }));
        Assert.False(d.IsEligible);
        Assert.Contains(EligibilityReason.ConsentRevoked, d.Reasons);
    }

    [Fact]
    public void Suppression_blocks_even_when_consent_is_granted()
    {
        var d = EligibilityRules.Evaluate(Good(f => f with { Suppressed = true }));
        Assert.False(d.IsEligible);
        Assert.Equal([EligibilityReason.Suppressed], d.Reasons);
    }

    [Fact]
    public void Missing_or_invalid_contact_value_blocks()
    {
        Assert.Contains(EligibilityReason.NoContactValue, EligibilityRules.Evaluate(Good(f => f with { RawValue = null, NormalizedValue = null })).Reasons);
        Assert.Contains(EligibilityReason.InvalidContactValue, EligibilityRules.Evaluate(Good(f => f with { RawValue = "12", NormalizedValue = null })).Reasons);
    }

    [Fact]
    public void Removed_contacts_archived_leads_and_suppressed_stage_block()
    {
        Assert.Contains(EligibilityReason.ContactRemoved, EligibilityRules.Evaluate(Good(f => f with { ContactRemoved = true })).Reasons);
        Assert.Contains(EligibilityReason.LeadArchived, EligibilityRules.Evaluate(Good(f => f with { LeadArchived = true })).Reasons);
        Assert.Contains(EligibilityReason.LeadExcluded, EligibilityRules.Evaluate(Good(f => f with { LeadInSuppressedStage = true })).Reasons);
    }

    [Fact]
    public void Unknown_contact_is_never_eligible()
    {
        var d = EligibilityRules.Evaluate(Good(f => f with { ContactExists = false }));
        Assert.False(d.IsEligible);
        Assert.Equal([EligibilityReason.ContactNotFound], d.Reasons);
    }

    [Fact]
    public void Every_blocker_is_reported_not_just_the_first()
    {
        var d = EligibilityRules.Evaluate(Good(f => f with { Suppressed = true, LatestConsent = ConsentStatus.Revoked, LeadArchived = true }));
        Assert.Equal(3, d.Reasons.Count);
        Assert.All(d.Reasons, r => Assert.False(string.IsNullOrWhiteSpace(EligibilityDecision.Explain(r))));
    }

    [Fact]
    public void Latest_consent_is_the_most_recent_occurrence()
    {
        var t = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(ConsentStatus.Granted, EligibilityRules.Latest([(ConsentStatus.Revoked, t), (ConsentStatus.Granted, t.AddDays(1))]));
        Assert.Equal(ConsentStatus.Revoked, EligibilityRules.Latest([(ConsentStatus.Granted, t), (ConsentStatus.Revoked, t.AddDays(1))]));
        Assert.Null(EligibilityRules.Latest([]));
    }

    [Fact]
    public void On_equal_timestamps_a_revocation_wins_whatever_the_order()
    {
        var t = new DateTime(2026, 5, 1, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(ConsentStatus.Revoked, EligibilityRules.Latest([(ConsentStatus.Granted, t), (ConsentStatus.Revoked, t)]));
        Assert.Equal(ConsentStatus.Revoked, EligibilityRules.Latest([(ConsentStatus.Revoked, t), (ConsentStatus.Granted, t)]));
    }

    [Fact]
    public void Email_channel_validates_addresses_and_phone_channels_validate_E164()
    {
        Assert.Equal("a@b.com", EligibilityRules.NormalizeFor(ConsentChannel.Email, "A@B.com"));
        Assert.Null(EligibilityRules.NormalizeFor(ConsentChannel.Email, "+966501234567"));
        Assert.Equal("+966501234567", EligibilityRules.NormalizeFor(ConsentChannel.WhatsApp, "0501234567", "966"));
        Assert.Null(EligibilityRules.NormalizeFor(ConsentChannel.Sms, "a@b.com"));
    }
}

public class ConsentRecordTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Consent_records_need_a_source_and_cannot_be_dated_in_the_future()
    {
        Assert.Throws<LeadPilot.Domain.Common.DomainException>(() => ConsentRecord.Create(Guid.NewGuid(), Guid.NewGuid(), ConsentChannel.WhatsApp, ConsentPurpose.Marketing, ConsentStatus.Granted, " ", null, null, Now, Now, null));
        Assert.Throws<LeadPilot.Domain.Common.DomainException>(() => ConsentRecord.Create(Guid.NewGuid(), Guid.NewGuid(), ConsentChannel.WhatsApp, ConsentPurpose.Marketing, ConsentStatus.Granted, "web form", null, null, Now.AddDays(2), Now, null));
        var ok = ConsentRecord.Create(Guid.NewGuid(), Guid.NewGuid(), ConsentChannel.Email, ConsentPurpose.Service, ConsentStatus.Revoked, "STOP reply", "msg-1", null, Now, Now, null);
        Assert.True(ok.IsRevocation);
        Assert.IsAssignableFrom<LeadPilot.Domain.Common.IAppendOnly>(ok);
    }

    [Fact]
    public void Contacts_release_their_unique_keys_when_removed()
    {
        var c = Contact.Create(Guid.NewGuid(), Guid.NewGuid(), "Sara", null, "+966 50 123 4567", Normalizer.PhoneE164("+966501234567"), "SARA@x.com", DataOrigin.Provided, true, Now, null);
        Assert.Equal("+966501234567", c.PhoneUniqueKey);
        Assert.Equal("sara@x.com", c.EmailUniqueKey);
        c.Remove(Now, null);
        Assert.Null(c.PhoneUniqueKey);
        Assert.Null(c.EmailUniqueKey);
        Assert.False(c.IsPrimary);
    }

    [Fact]
    public void Contacts_validate_their_values()
    {
        var t = Guid.NewGuid();
        Assert.Throws<LeadPilot.Domain.Common.DomainException>(() => Contact.Create(t, t, null, null, null, null, null, DataOrigin.Public, false, Now, null));
        Assert.Throws<LeadPilot.Domain.Common.DomainException>(() => Contact.Create(t, t, "x", null, "12", null, null, DataOrigin.Public, false, Now, null));
        Assert.Throws<LeadPilot.Domain.Common.DomainException>(() => Contact.Create(t, t, "x", null, null, null, "nope", DataOrigin.Public, false, Now, null));
    }

    [Fact]
    public void Verified_origin_stamps_phone_verification()
    {
        var c = Contact.Create(Guid.NewGuid(), Guid.NewGuid(), "x", null, "+966501234567", "+966501234567", null, DataOrigin.Verified, false, Now, null);
        Assert.Equal(Now, c.PhoneVerifiedAtUtc);
        c.Update("x", null, "+966509999999", "+966509999999", null, DataOrigin.Provided, Now.AddDays(1), null);
        Assert.Null(c.PhoneVerifiedAtUtc);
    }
}

public class LeadDomainTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    private static LeadProfile Profile(string name = "Al Noor School", string? city = "Riyadh", string? website = "https://www.alnoor.example") =>
        new(name, null, "Saudi Arabia", null, city, null, null, null, website, 50);

    [Fact]
    public void Creating_a_lead_computes_dedup_keys()
    {
        var l = Lead.Create(Guid.NewGuid(), Profile(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, LeadSourceType.Manual, Now, null);
        Assert.Equal("alnoor.example", l.WebsiteDomain);
        Assert.Equal("al noor school|riyadh", l.DedupKey);
        Assert.Equal(1, l.Version);
    }

    [Fact]
    public void Updating_reports_changed_fields_recomputes_keys_and_bumps_the_version()
    {
        var l = Lead.Create(Guid.NewGuid(), Profile(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, LeadSourceType.Manual, Now, null);
        var changed = l.UpdateProfile(Profile(city: "Jeddah"), Now.AddHours(1), "me");
        Assert.Equal(["City"], changed);
        Assert.Equal("al noor school|jeddah", l.DedupKey);
        Assert.Equal(2, l.Version);
        Assert.Empty(l.UpdateProfile(Profile(city: "Jeddah"), Now.AddHours(2), "me")); // no change, no version bump
        Assert.Equal(2, l.Version);
    }

    [Fact]
    public void Invalid_profiles_are_rejected()
    {
        var t = Guid.NewGuid();
        Assert.Throws<LeadPilot.Domain.Common.DomainException>(() => Lead.Create(t, Profile(name: "  "), t, t, t, null, LeadSourceType.Manual, Now, null));
        Assert.Throws<LeadPilot.Domain.Common.DomainException>(() => Lead.Create(t, Profile(name: "!!!"), t, t, t, null, LeadSourceType.Manual, Now, null));
        Assert.Throws<LeadPilot.Domain.Common.DomainException>(() => Lead.Create(t, Profile(website: "javascript:alert(1)"), t, t, t, null, LeadSourceType.Manual, Now, null));
        Assert.Throws<LeadPilot.Domain.Common.DomainException>(() => Lead.Create(t, Profile() with { Confidence = 101 }, t, t, t, null, LeadSourceType.Manual, Now, null));
        Assert.Throws<LeadPilot.Domain.Common.DomainException>(() => Lead.Create(t, Profile(name: new string('x', 301)), t, t, t, null, LeadSourceType.Manual, Now, null));
    }

    [Fact]
    public void Archived_leads_are_frozen_until_restored()
    {
        var l = Lead.Create(Guid.NewGuid(), Profile(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, LeadSourceType.Manual, Now, null);
        l.Archive(Now, null);
        Assert.Throws<LeadPilot.Domain.Common.DomainException>(() => l.UpdateProfile(Profile(city: "x"), Now, null));
        Assert.Throws<LeadPilot.Domain.Common.DomainException>(() => l.MoveToStage(l.PipelineId, Guid.NewGuid(), Now, null));
        Assert.Throws<LeadPilot.Domain.Common.DomainException>(() => l.AssignTo(Guid.NewGuid(), Now, null));
        Assert.Throws<LeadPilot.Domain.Common.DomainException>(() => l.Archive(Now, null));
        l.Restore(Now, null);
        Assert.False(l.IsArchived);
    }

    [Fact]
    public void Default_pipeline_has_the_fourteen_specified_stages_in_order()
    {
        Assert.Equal(
            ["New", "Researched", "Contact Eligible", "Contacted", "Responded", "Interested", "Qualified", "Demo Scheduled", "Demo Completed", "Trial", "Negotiation", "Won", "Lost", "Suppressed"],
            DefaultPipeline.Stages.Select(s => s.Name));
        Assert.Equal(StageKind.Won, DefaultPipeline.Stages.Single(s => s.Name == "Won").Kind);
        Assert.Equal(StageKind.Lost, DefaultPipeline.Stages.Single(s => s.Name == "Lost").Kind);
        Assert.Equal(StageKind.Suppressed, DefaultPipeline.Stages.Single(s => s.Name == "Suppressed").Kind);
    }
}
