using LeadPilot.Domain.Common;

namespace LeadPilot.Domain.Crm;

/// <summary>A person (or reachable endpoint) at a lead. One lead has many contacts. Never merged with Conversation or Customer.</summary>
public class Contact : AuditableEntity, ITenantOwned
{
    private Contact() { }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }
    public string? Name { get; private set; }
    public string? Role { get; private set; }
    public string? PhoneRaw { get; private set; }
    public string? PhoneE164 { get; private set; }
    public string? Email { get; private set; }
    public DataOrigin DataOrigin { get; private set; }
    public DateTime? PhoneVerifiedAtUtc { get; private set; }
    public bool IsPrimary { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }
    /// <summary>Equals <see cref="PhoneE164"/> while the contact is live, otherwise null: lets the database enforce "one phone per lead" without blocking re-adding a removed contact.</summary>
    public string? PhoneUniqueKey { get; private set; }
    public string? EmailUniqueKey { get; private set; }

    /// <summary>The value messages on <paramref name="channel"/> would be sent to, or null when the contact has none.</summary>
    public string? ValueFor(ConsentChannel channel) => channel == ConsentChannel.Email ? Email : PhoneE164;

    public static Contact Create(Guid tenantId, Guid leadId, string? name, string? role, string? phoneRaw, string? phoneE164, string? email, DataOrigin origin, bool isPrimary, DateTime nowUtc, string? actor)
    {
        var c = new Contact { TenantId = tenantId, LeadId = leadId, CreatedAtUtc = nowUtc, CreatedBy = actor };
        c.Set(name, role, phoneRaw, phoneE164, email, origin, nowUtc, touch: false);
        c.IsPrimary = isPrimary;
        return c;
    }

    public void Update(string? name, string? role, string? phoneRaw, string? phoneE164, string? email, DataOrigin origin, DateTime nowUtc, string? actor)
    {
        if (IsDeleted) throw new DomainException("This contact was removed.");
        Set(name, role, phoneRaw, phoneE164, email, origin, nowUtc, touch: true);
        UpdatedBy = actor;
    }

    private void Set(string? name, string? role, string? phoneRaw, string? phoneE164, string? email, DataOrigin origin, DateTime nowUtc, bool touch)
    {
        Name = Opt(name, 200, "Contact name");
        Role = Opt(role, 100, "Role");
        PhoneRaw = Opt(phoneRaw, 40, "Phone");
        if (PhoneRaw is not null && phoneE164 is null) throw new DomainException("Phone number is not valid. Use international format, e.g. +966501234567.");
        var phoneChanged = PhoneE164 != phoneE164;
        PhoneE164 = PhoneRaw is null ? null : phoneE164;
        var mail = Opt(email, 256, "E-mail");
        if (mail is not null) mail = Normalizer.Email(mail) ?? throw new DomainException("E-mail address is not valid.");
        Email = mail;
        if (Name is null && PhoneE164 is null && Email is null) throw new DomainException("A contact needs at least a name, a phone number or an e-mail address.");
        DataOrigin = origin;
        if (PhoneE164 is null) PhoneVerifiedAtUtc = null;
        else if (origin == DataOrigin.Verified && (PhoneVerifiedAtUtc is null || phoneChanged)) PhoneVerifiedAtUtc = nowUtc;
        else if (phoneChanged && origin != DataOrigin.Verified) PhoneVerifiedAtUtc = null;
        RefreshUniqueKeys();
        if (touch) UpdatedAtUtc = nowUtc;
    }

    public void SetPrimary(bool value, DateTime nowUtc) { IsPrimary = value; UpdatedAtUtc = nowUtc; }

    public void Remove(DateTime nowUtc, string? actor)
    {
        if (IsDeleted) throw new DomainException("This contact was already removed.");
        IsDeleted = true; DeletedAtUtc = nowUtc; IsPrimary = false; UpdatedAtUtc = nowUtc; UpdatedBy = actor;
        RefreshUniqueKeys();
    }

    private void RefreshUniqueKeys()
    {
        PhoneUniqueKey = IsDeleted ? null : PhoneE164;
        EmailUniqueKey = IsDeleted ? null : Email;
    }

    private static string? Opt(string? v, int max, string label)
    {
        var t = v?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (t.Length > max) throw new DomainException($"{label} must be {max} characters or fewer.");
        return t;
    }
}

/// <summary>
/// One consent decision about one contact, channel and purpose. APPEND-ONLY: the current state is the latest record
/// (by <see cref="OccurredAtUtc"/>; a revocation wins a tie). Records are never edited or deleted.
/// </summary>
public class ConsentRecord : Entity, ITenantOwned, IAppendOnly
{
    private ConsentRecord() { }
    public Guid TenantId { get; private set; }
    public Guid ContactId { get; private set; }
    public ConsentChannel Channel { get; private set; }
    public ConsentPurpose Purpose { get; private set; }
    public ConsentStatus Status { get; private set; }
    /// <summary>Mirrors <c>Status == Revoked</c>; stored so "latest, revocation first" is a plain indexed sort in SQL.</summary>
    public bool IsRevocation { get; private set; }
    /// <summary>How consent was obtained or withdrawn (web form, verbal, STOP reply, …).</summary>
    public string Source { get; private set; } = string.Empty;
    /// <summary>Pointer to proof (form submission id, recording id, message id). Not the proof itself.</summary>
    public string? EvidenceRef { get; private set; }
    public string? Note { get; private set; }
    public DateTime OccurredAtUtc { get; private set; }
    public DateTime RecordedAtUtc { get; private set; }
    public Guid? RecordedByUserId { get; private set; }

    public static ConsentRecord Create(Guid tenantId, Guid contactId, ConsentChannel channel, ConsentPurpose purpose, ConsentStatus status, string source, string? evidenceRef, string? note,
        DateTime occurredAtUtc, DateTime recordedAtUtc, Guid? recordedBy)
    {
        var src = source?.Trim();
        if (string.IsNullOrEmpty(src)) throw new DomainException("The source of the consent (how it was given or withdrawn) is required.");
        if (src.Length > 200) throw new DomainException("Consent source must be 200 characters or fewer.");
        if (evidenceRef?.Trim() is { Length: > 500 }) throw new DomainException("Evidence reference must be 500 characters or fewer.");
        if (note?.Trim() is { Length: > 1000 }) throw new DomainException("Note must be 1000 characters or fewer.");
        if (occurredAtUtc > recordedAtUtc.AddMinutes(5)) throw new DomainException("Consent cannot be dated in the future.");
        if (occurredAtUtc < new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)) throw new DomainException("Consent date is not plausible.");
        return new ConsentRecord
        {
            TenantId = tenantId, ContactId = contactId, Channel = channel, Purpose = purpose, Status = status, IsRevocation = status == ConsentStatus.Revoked,
            Source = src, EvidenceRef = string.IsNullOrWhiteSpace(evidenceRef) ? null : evidenceRef.Trim(), Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            OccurredAtUtc = occurredAtUtc, RecordedAtUtc = recordedAtUtc, RecordedByUserId = recordedBy
        };
    }
}

/// <summary>A phone number or e-mail address that must never be contacted on the given channel. Unique per (tenant, channel, value).</summary>
public class SuppressionEntry : Entity, ITenantOwned
{
    private SuppressionEntry() { }
    public Guid TenantId { get; private set; }
    public SuppressionChannel Channel { get; private set; }
    /// <summary>E.164 phone or lower-case e-mail.</summary>
    public string ValueNormalized { get; private set; } = string.Empty;
    /// <summary>SHA-256 hex of the normalised value (for matching against lists shared without raw values).</summary>
    public string ValueHash { get; private set; } = string.Empty;
    public SuppressionReason Reason { get; private set; }
    public string? Note { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public Guid? CreatedByUserId { get; private set; }

    public static SuppressionEntry Create(Guid tenantId, SuppressionChannel channel, string valueNormalized, SuppressionReason reason, string? note, Guid? by, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(valueNormalized) || valueNormalized.Length > 320) throw new DomainException("A suppression value is required.");
        if (note?.Trim() is { Length: > 500 }) throw new DomainException("Note must be 500 characters or fewer.");
        return new SuppressionEntry
        {
            TenantId = tenantId, Channel = channel, ValueNormalized = valueNormalized, ValueHash = Normalizer.Sha256Hex(valueNormalized), Reason = reason,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), CreatedByUserId = by, CreatedAtUtc = nowUtc
        };
    }

    /// <summary>Normalises the value for the channel, or returns null when it is not a valid phone/e-mail for it. <see cref="SuppressionChannel.Any"/> accepts either.</summary>
    public static string? NormalizeValue(SuppressionChannel channel, string? raw, string? defaultCountryCode = null) => channel switch
    {
        SuppressionChannel.Email => Normalizer.Email(raw),
        SuppressionChannel.Any => raw?.Contains('@') == true ? Normalizer.Email(raw) : Normalizer.PhoneE164(raw, defaultCountryCode),
        _ => Normalizer.PhoneE164(raw, defaultCountryCode),
    };
}
