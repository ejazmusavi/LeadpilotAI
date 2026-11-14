using LeadPilot.Domain.Crm;

namespace LeadPilot.Application.Crm;

public enum EligibilityReason
{
    ContactNotFound = 1, ContactRemoved, LeadArchived, LeadExcluded, NoContactValue, InvalidContactValue, Suppressed, NoConsent, ConsentRevoked
}

public sealed record EligibilityDecision(bool IsEligible, IReadOnlyList<EligibilityReason> Reasons)
{
    public static EligibilityDecision Eligible { get; } = new(true, []);
    public static EligibilityDecision NotFound { get; } = new(false, [EligibilityReason.ContactNotFound]);
    public static string Explain(EligibilityReason r) => r switch
    {
        EligibilityReason.ContactNotFound => "Contact not found.",
        EligibilityReason.ContactRemoved => "The contact was removed.",
        EligibilityReason.LeadArchived => "The lead is archived.",
        EligibilityReason.LeadExcluded => "The lead is in a suppressed pipeline stage.",
        EligibilityReason.NoContactValue => "The contact has no phone number or e-mail for this channel.",
        EligibilityReason.InvalidContactValue => "The contact's phone number or e-mail is not valid.",
        EligibilityReason.Suppressed => "The phone number or e-mail is on the suppression list.",
        EligibilityReason.NoConsent => "No consent has been recorded for this channel and purpose.",
        EligibilityReason.ConsentRevoked => "Consent for this channel and purpose was revoked.",
        _ => r.ToString(),
    };
}

/// <summary>What the gate needs to know about one contact on one channel/purpose. Facts are loaded by the service; the decision is pure.</summary>
public sealed record EligibilityFacts(
    bool ContactExists, bool ContactRemoved, bool LeadArchived, bool LeadInSuppressedStage,
    string? RawValue, string? NormalizedValue, bool Suppressed, ConsentStatus? LatestConsent);

/// <summary>
/// The single rule for "may we contact this person on this channel for this purpose": consent granted AND not suppressed AND the contact value valid
/// (and the lead not archived/excluded). Lead score, status and stage-progress never appear here: a high score cannot override a missing consent or a suppression.
/// </summary>
public static class EligibilityRules
{
    public static EligibilityDecision Evaluate(EligibilityFacts f)
    {
        if (!f.ContactExists) return EligibilityDecision.NotFound;
        var reasons = new List<EligibilityReason>();
        if (f.ContactRemoved) reasons.Add(EligibilityReason.ContactRemoved);
        if (f.LeadArchived) reasons.Add(EligibilityReason.LeadArchived);
        if (f.LeadInSuppressedStage) reasons.Add(EligibilityReason.LeadExcluded);
        if (string.IsNullOrWhiteSpace(f.RawValue)) reasons.Add(EligibilityReason.NoContactValue);
        else if (f.NormalizedValue is null) reasons.Add(EligibilityReason.InvalidContactValue);
        if (f.Suppressed) reasons.Add(EligibilityReason.Suppressed);
        if (f.LatestConsent is null) reasons.Add(EligibilityReason.NoConsent);
        else if (f.LatestConsent == ConsentStatus.Revoked) reasons.Add(EligibilityReason.ConsentRevoked);
        return reasons.Count == 0 ? EligibilityDecision.Eligible : new EligibilityDecision(false, reasons);
    }

    /// <summary>Latest consent decision: most recent <c>OccurredAtUtc</c>; on equal timestamps a revocation wins.</summary>
    public static ConsentStatus? Latest(IEnumerable<(ConsentStatus Status, DateTime OccurredAtUtc)> records)
    {
        (ConsentStatus Status, DateTime At)? best = null;
        foreach (var r in records)
            if (best is null || r.OccurredAtUtc > best.Value.At || (r.OccurredAtUtc == best.Value.At && r.Status == ConsentStatus.Revoked && best.Value.Status != ConsentStatus.Revoked))
                best = (r.Status, r.OccurredAtUtc);
        return best?.Status;
    }

    /// <summary>The value a channel sends to: e-mail for Email, E.164 phone for everything else.</summary>
    public static string? NormalizeFor(ConsentChannel channel, string? raw, string? defaultCountryCode = null) =>
        channel == ConsentChannel.Email ? Normalizer.Email(raw) : Normalizer.PhoneE164(raw, defaultCountryCode);
}
