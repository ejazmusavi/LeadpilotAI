using System.Text.RegularExpressions;
using LeadPilot.Domain.Common;

namespace LeadPilot.Domain.Tenancy;

public enum TenantStatus { Active = 1, Suspended = 2 }

/// <summary>Why a tenant is suspended. Only a NonPayment suspension is lifted automatically by a verified payment; Manual and Abuse need an operator. Stored in <c>TenantBillingState</c>.</summary>
public enum SuspensionKind { Manual = 1, NonPayment = 2, Abuse = 3 }

/// <summary>A customer workspace. Global entity (no TenantId): it *is* the tenant.</summary>
public class Tenant : AuditableEntity
{
    public const string PlatformSlug = "platform";
    private static readonly Regex SlugPattern = new("^[a-z0-9](?:[a-z0-9-]{1,61})[a-z0-9]$", RegexOptions.Compiled);
    private static readonly Regex TimezonePattern = new(@"^[A-Za-z_]+(?:/[A-Za-z0-9_+\-]+)*$", RegexOptions.Compiled);
    private static readonly HashSet<string> ReservedSlugs = ["admin", "api", "www", "app", "root", "system", "support", "null", "undefined"];

    private Tenant() { }

    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public TenantStatus Status { get; private set; } = TenantStatus.Active;
    /// <summary>The single operator tenant that holds platform staff. It can never be suspended.</summary>
    public bool IsPlatform { get; private set; }
    public string? Industry { get; private set; }
    public string Timezone { get; private set; } = "UTC";
    public string Locale { get; private set; } = "en";
    public DateTime? SuspendedAtUtc { get; private set; }
    public string? SuspensionReason { get; private set; }

    public bool IsActive => Status == TenantStatus.Active;

    public static Tenant Create(string name, string slug, string? industry, string? timezone, string? locale, DateTime nowUtc, bool isPlatform = false)
    {
        var tenant = new Tenant { IsPlatform = isPlatform, CreatedAtUtc = nowUtc };
        tenant.SetSlug(slug, allowReserved: isPlatform);
        tenant.UpdateProfile(name, industry, timezone, locale, nowUtc, touch: false);
        return tenant;
    }

    public void UpdateProfile(string name, string? industry, string? timezone, string? locale, DateTime nowUtc, bool touch = true)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Tenant name is required.");
        if (name.Trim().Length > 200) throw new DomainException("Tenant name must be 200 characters or fewer.");
        var tz = string.IsNullOrWhiteSpace(timezone) ? "UTC" : timezone.Trim();
        if (tz.Length > 64 || !TimezonePattern.IsMatch(tz)) throw new DomainException("Timezone must be an IANA name such as 'Asia/Karachi' or 'UTC'.");
        var loc = string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim();
        if (loc.Length > 16 || !Regex.IsMatch(loc, "^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")) throw new DomainException("Locale must look like 'en' or 'en-GB'.");
        if (industry is { Length: > 100 }) throw new DomainException("Industry must be 100 characters or fewer.");

        Name = name.Trim();
        Industry = string.IsNullOrWhiteSpace(industry) ? null : industry.Trim();
        Timezone = tz;
        Locale = loc;
        if (touch) UpdatedAtUtc = nowUtc;
    }

    private void SetSlug(string slug, bool allowReserved)
    {
        var s = NormalizeSlug(slug);
        if (!IsValidSlug(s)) throw new DomainException("Slug must be 3-63 characters: lowercase letters, digits and hyphens, not starting or ending with a hyphen.");
        if (!allowReserved && (ReservedSlugs.Contains(s) || s == PlatformSlug)) throw new DomainException($"The slug '{s}' is reserved.");
        Slug = s;
    }

    public void Suspend(string? reason, DateTime nowUtc)
    {
        if (IsPlatform) throw new DomainException("The platform tenant cannot be suspended.");
        if (Status == TenantStatus.Suspended) throw new DomainException("The tenant is already suspended.");
        Status = TenantStatus.Suspended;
        SuspendedAtUtc = nowUtc;
        SuspensionReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()[..Math.Min(reason.Trim().Length, 500)];
        UpdatedAtUtc = nowUtc;
    }

    public void Activate(DateTime nowUtc)
    {
        if (Status == TenantStatus.Active) throw new DomainException("The tenant is already active.");
        Status = TenantStatus.Active;
        SuspendedAtUtc = null;
        SuspensionReason = null;
        UpdatedAtUtc = nowUtc;
    }

    public static string NormalizeSlug(string? slug) => (slug ?? string.Empty).Trim().ToLowerInvariant();
    public static bool IsValidSlug(string slug) => SlugPattern.IsMatch(slug);

    /// <summary>Derives a candidate slug from a display name ("Acme Ltd." → "acme-ltd").</summary>
    public static string SlugFromName(string name)
    {
        var s = Regex.Replace(NormalizeSlug(name), "[^a-z0-9]+", "-").Trim('-');
        if (s.Length > 63) s = s[..63].Trim('-');
        return s;
    }
}
