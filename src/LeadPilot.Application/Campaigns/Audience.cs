using System.Text.Json;
using System.Text.Json.Serialization;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Crm;

namespace LeadPilot.Application.Campaigns;

/// <summary>Follow-up / re-engagement: the recipients of a parent campaign that match a rule N days after the relevant event.</summary>
public sealed record FollowUpSpec(Guid ParentCampaignId, FollowUpRule Rule, int AfterDays, IReadOnlyList<Guid>? AdvancedStageIds = null);

/// <summary>
/// The audience of a campaign as data (stored as JSON on the campaign, versioned so the shape can evolve). Every list is an "any of" filter; different filters are AND-ed.
/// Eligibility (consent, suppression, validity) is NOT part of the definition: it is applied by the gate on top of whatever the definition selects.
/// </summary>
public sealed record AudienceDefinition
{
    public const int CurrentVersion = 1;
    public const int MaxListItems = 200;
    public const int MaxExplicitContacts = 500;

    public int Version { get; init; } = CurrentVersion;
    public IReadOnlyList<Guid> StatusIds { get; init; } = [];
    public IReadOnlyList<Guid> StageIds { get; init; } = [];
    public IReadOnlyList<Guid> CategoryIds { get; init; } = [];
    public IReadOnlyList<string> Countries { get; init; } = [];
    public IReadOnlyList<string> Provinces { get; init; } = [];
    public IReadOnlyList<string> Cities { get; init; } = [];
    /// <summary>Lead has at least one of these tags.</summary>
    public IReadOnlyList<Guid> TagIds { get; init; } = [];
    /// <summary>Lead has none of these tags.</summary>
    public IReadOnlyList<Guid> ExcludeTagIds { get; init; } = [];
    public IReadOnlyList<LeadSourceType> SourceTypes { get; init; } = [];
    public IReadOnlyList<Guid> OwnerUserIds { get; init; } = [];
    public bool Unassigned { get; init; }
    public int? MinScore { get; init; }
    public int? MaxScore { get; init; }
    /// <summary>Last outbound message to the contact is older than this many days (contacts never messaged also qualify unless <see cref="NeverContacted"/> is false and this is set).</summary>
    public int? LastContactedBeforeDays { get; init; }
    /// <summary>Only contacts nobody has ever messaged.</summary>
    public bool NeverContacted { get; init; }
    /// <summary>true: the contact has written to us at least once; false: never; null: either.</summary>
    public bool? HasReply { get; init; }
    /// <summary>Manual campaigns: an explicit list of contacts (max 500). Combined with the other filters.</summary>
    public IReadOnlyList<Guid> ContactIds { get; init; } = [];
    /// <summary>One message per lead (its primary contact, else its oldest contact with a number). Turn off to message every contact of a lead.</summary>
    public bool OnePerLead { get; init; } = true;
    public FollowUpSpec? FollowUp { get; init; }

    [JsonIgnore]
    public bool HasAnyFilter => StatusIds.Count + StageIds.Count + CategoryIds.Count + Countries.Count + Provinces.Count + Cities.Count + TagIds.Count + ExcludeTagIds.Count + SourceTypes.Count + OwnerUserIds.Count + ContactIds.Count > 0
        || Unassigned || MinScore is not null || MaxScore is not null || LastContactedBeforeDays is not null || NeverContacted || HasReply is not null || FollowUp is not null;
}

/// <summary>Parsing, normalising and validating audience definitions. Hostile or oversized JSON never throws: it produces validation messages.</summary>
public static class AudienceDefinitions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false, Converters = { new JsonStringEnumConverter() }, MaxDepth = 8 };
    public const int MaxJsonLength = 64 * 1024;

    public static string Serialize(AudienceDefinition d) => JsonSerializer.Serialize(Normalize(d), Json);

    public static (AudienceDefinition? Definition, string? Error) Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (new AudienceDefinition(), null);
        if (json.Length > MaxJsonLength) return (null, "The audience definition is too large.");
        try
        {
            var d = JsonSerializer.Deserialize<AudienceDefinition>(json, Json);
            if (d is null) return (null, "The audience definition is empty.");
            if (d.Version != AudienceDefinition.CurrentVersion) return (null, $"Unsupported audience definition version {d.Version}.");
            return (Normalize(d), null);
        }
        catch (JsonException) { return (null, "The audience definition is not valid JSON for this version."); }
        catch (NotSupportedException) { return (null, "The audience definition is not valid."); }
    }

    /// <summary>Trims, de-duplicates, drops empty entries and caps list sizes so the stored form is canonical (equal definitions serialise equally).</summary>
    public static AudienceDefinition Normalize(AudienceDefinition d)
    {
        static IReadOnlyList<Guid> G(IEnumerable<Guid>? v, int max = AudienceDefinition.MaxListItems) => (v ?? []).Where(x => x != Guid.Empty).Distinct().OrderBy(x => x).Take(max).ToList();
        static IReadOnlyList<string> S(IEnumerable<string>? v) => (v ?? []).Select(x => x?.Trim() ?? "").Where(x => x.Length > 0 && x.Length <= 100).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(AudienceDefinition.MaxListItems).ToList();
        return d with
        {
            Version = AudienceDefinition.CurrentVersion,
            StatusIds = G(d.StatusIds), StageIds = G(d.StageIds), CategoryIds = G(d.CategoryIds), TagIds = G(d.TagIds), ExcludeTagIds = G(d.ExcludeTagIds), OwnerUserIds = G(d.OwnerUserIds),
            ContactIds = G(d.ContactIds, AudienceDefinition.MaxExplicitContacts),
            Countries = S(d.Countries), Provinces = S(d.Provinces), Cities = S(d.Cities),
            SourceTypes = (d.SourceTypes ?? []).Where(Enum.IsDefined).Distinct().OrderBy(x => x).ToList(),
            FollowUp = d.FollowUp is null ? null : d.FollowUp with { AdvancedStageIds = G(d.FollowUp.AdvancedStageIds) },
        };
    }

    /// <summary>Messages for a person to fix. Empty = valid.</summary>
    public static IReadOnlyList<string> Validate(AudienceDefinition d, CampaignType type)
    {
        var e = new List<string>();
        if (d.MinScore is < 0 or > 100) e.Add("The minimum score must be between 0 and 100.");
        if (d.MaxScore is < 0 or > 100) e.Add("The maximum score must be between 0 and 100.");
        if (d.MinScore is { } a && d.MaxScore is { } b && a > b) e.Add("The minimum score cannot be higher than the maximum score.");
        if (d.LastContactedBeforeDays is < 0 or > 3650) e.Add("'Last contacted before' must be between 0 and 3650 days.");
        if (d.NeverContacted && d.LastContactedBeforeDays is not null) e.Add("Choose either 'never contacted' or 'last contacted before', not both.");
        if (d.Unassigned && d.OwnerUserIds.Count > 0) e.Add("Choose either 'unassigned' or specific owners, not both.");
        if (d.TagIds.Intersect(d.ExcludeTagIds).Any()) e.Add("A tag cannot be both required and excluded.");
        if (d.ContactIds.Count > AudienceDefinition.MaxExplicitContacts) e.Add($"At most {AudienceDefinition.MaxExplicitContacts} contacts can be listed by hand.");
        if (d.FollowUp is { } f)
        {
            if (f.ParentCampaignId == Guid.Empty) e.Add("Choose the parent campaign.");
            if (f.AfterDays is < 0 or > 365) e.Add("'After days' must be between 0 and 365.");
            if (!Enum.IsDefined(f.Rule)) e.Add("Choose a follow-up rule.");
            if (f.Rule == FollowUpRule.RepliedNotAdvanced && (f.AdvancedStageIds is null || f.AdvancedStageIds.Count == 0)) e.Add("'Replied but not advanced' needs at least one stage that counts as advanced (for example 'Demo booked').");
        }
        else if (type == CampaignType.FollowUp) e.Add("A follow-up campaign needs a follow-up rule based on a parent campaign.");
        if (type != CampaignType.FollowUp && type != CampaignType.ReEngagement && d.FollowUp is not null) e.Add("Only follow-up and re-engagement campaigns can use a parent campaign.");
        if (type == CampaignType.ReEngagement && d.FollowUp is null && d.LastContactedBeforeDays is null && !d.NeverContacted && d.HasReply is null) e.Add("A re-engagement campaign needs a parent campaign rule or an inactivity filter ('last contacted before', 'has reply').");
        if (type != CampaignType.FollowUp && !d.HasAnyFilter) e.Add("Choose at least one filter: a campaign without any filter would target every contact of the workspace.");
        return e;
    }
}

/// <summary>Why a contact is not (or no longer) a recipient. Codes are stored on recipients and shown in the exclusion breakdown.</summary>
public static class ExclusionReasons
{
    public const string NoConsent = "no_consent";
    public const string ConsentRevoked = "consent_revoked";
    public const string Suppressed = "suppressed";
    public const string InvalidContact = "invalid_contact";
    public const string NoContactValue = "no_contact_value";
    public const string ContactRemoved = "contact_removed";
    public const string LeadArchived = "lead_archived";
    public const string LeadExcluded = "lead_excluded";
    public const string ContactNotFound = "contact_not_found";
    public const string Cooldown = "cooldown";
    public const string DuplicateNumber = "duplicate_number";
    public const string SameLead = "same_lead_other_contact";
    public const string MissingVariable = "missing_variable";
    public const string OverLimit = "over_recipient_limit";
    public const string OptedOutConversation = "opted_out_conversation";
    public const string ContactChanged = "contact_changed";
    public const string CampaignStopped = "campaign_stopped";
    public const string AccountUnavailable = "account_unavailable";
    public const string SendFailed = "send_failed";

    /// <summary>Precedence used to name ONE primary reason when a contact fails several rules (so the breakdown adds up to the excluded count).</summary>
    private static readonly EligibilityReason[] Precedence =
    [
        EligibilityReason.ContactNotFound, EligibilityReason.ContactRemoved, EligibilityReason.NoContactValue, EligibilityReason.InvalidContactValue, EligibilityReason.Suppressed,
        EligibilityReason.ConsentRevoked, EligibilityReason.NoConsent, EligibilityReason.LeadArchived, EligibilityReason.LeadExcluded,
    ];

    public static string Primary(IReadOnlyList<EligibilityReason> reasons)
    {
        foreach (var r in Precedence) if (reasons.Contains(r)) return Code(r);
        return reasons.Count > 0 ? Code(reasons[0]) : "unknown";
    }

    public static string Code(EligibilityReason r) => r switch
    {
        EligibilityReason.NoConsent => NoConsent, EligibilityReason.ConsentRevoked => ConsentRevoked, EligibilityReason.Suppressed => Suppressed, EligibilityReason.InvalidContactValue => InvalidContact,
        EligibilityReason.NoContactValue => NoContactValue, EligibilityReason.ContactRemoved => ContactRemoved, EligibilityReason.LeadArchived => LeadArchived, EligibilityReason.LeadExcluded => LeadExcluded,
        EligibilityReason.ContactNotFound => ContactNotFound, _ => r.ToString().ToLowerInvariant(),
    };

    public static string Explain(string code) => code switch
    {
        NoConsent => "No marketing consent recorded.", ConsentRevoked => "Marketing consent was revoked.", Suppressed => "On the suppression list (opted out, blocked or excluded).",
        ContactChanged => "The contact's number changed after the audience was selected.", InvalidContact => "The phone number is not valid.", NoContactValue => "The contact has no phone number.", ContactRemoved => "The contact was removed.", LeadArchived => "The lead is archived.",
        LeadExcluded => "The lead is in a suppressed pipeline stage.", ContactNotFound => "The contact no longer exists.", Cooldown => "Messaged by another campaign inside the cool-down period.",
        DuplicateNumber => "Another contact of this campaign has the same number.", SameLead => "Another contact of the same lead is already in the audience.",
        MissingVariable => "The message needs a value this contact does not have.", OverLimit => "Beyond the campaign's recipient limit.", OptedOutConversation => "The person asked to stop in the WhatsApp conversation.",
        CampaignStopped => "The campaign was paused, cancelled or stopped before the message was sent.", AccountUnavailable => "The WhatsApp account was unavailable.", SendFailed => "The message could not be sent.",
        _ => code,
    };
}
