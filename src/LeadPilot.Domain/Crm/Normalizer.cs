using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;

namespace LeadPilot.Domain.Crm;

/// <summary>
/// Pure normalisation used for duplicate detection and for storing comparable values.
/// Names/cities/domains are for MATCHING only; the original text is always kept for display.
/// </summary>
public static partial class Normalizer
{
    private static readonly HashSet<string> LegalSuffixes = new(StringComparer.Ordinal)
    {
        "ltd", "limited", "llc", "llp", "inc", "incorporated", "corp", "corporation", "co", "company", "plc", "gmbh", "pllc", "pty", "sa", "bv", "ag"
    };

    public const int MaxDedupKeyLength = 300;

    /// <summary>Lower-case, accent/diacritic-free, Arabic-variant-folded, punctuation-free, single-spaced, without legal-form suffixes ("Ltd", "LLC", …).</summary>
    public static string Name(string? value)
    {
        var folded = Fold(value);
        if (folded.Length == 0) return string.Empty;
        var words = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count > 1 && words[0] == "the") words.RemoveAt(0);
        while (words.Count > 1 && LegalSuffixes.Contains(words[^1])) words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }

    /// <summary>City/area names compare with the same folding but keep every word.</summary>
    public static string Place(string? value) => Fold(value);

    /// <summary>"normalized name|normalized city". Two leads with the same key are probable duplicates.</summary>
    public static string DedupKey(string? name, string? city)
    {
        var key = Name(name) + "|" + Place(city);
        return key.Length <= MaxDedupKeyLength ? key : key[..MaxDedupKeyLength];
    }

    private static string Fold(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var decomposed = value.Normalize(NormalizationForm.FormKD).ToLowerInvariant();
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue; // accents, Arabic harakat
            if (ch == 'ـ') continue; // tatweel
            var c = ch switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                'ؤ' => 'و',
                'ئ' => 'ي',
                'ß' => 's',
                _ => ch,
            };
            if (c == '&') { sb.Append(" and "); continue; }
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return WhitespaceRun().Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>
    /// Website host without scheme, credentials, port, path, "www." and trailing dot, lower-case (IDN converted to punycode). Null when it is not a plausible domain.
    /// </summary>
    public static string? Domain(string? website)
    {
        if (string.IsNullOrWhiteSpace(website)) return null;
        var s = website.Trim();
        if (s.Length > 2048 || s.Any(char.IsControl) || s.Any(char.IsWhiteSpace)) return null;
        var schemeAt = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeAt >= 0)
        {
            var scheme = s[..schemeAt];
            if (scheme is not ("http" or "https" or "HTTP" or "HTTPS" or "Http" or "Https")) return null; // javascript:, ftp:, data: …
            s = s[(schemeAt + 3)..];
        }
        else if (s.Contains(':') && !PortOnly().IsMatch(s)) return null; // e.g. "mailto:x", "javascript:alert(1)"
        var end = s.IndexOfAny(['/', '?', '#']);
        var host = end >= 0 ? s[..end] : s;
        var at = host.LastIndexOf('@');
        if (at >= 0) host = host[(at + 1)..];
        var colon = host.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!int.TryParse(host[(colon + 1)..], out var port) || port is < 1 or > 65535) return null;
            host = host[..colon];
        }
        host = host.Trim('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (host.Length is 0 or > 253 || !host.Contains('.')) return null;
        try { host = new IdnMapping().GetAscii(host); } catch (ArgumentException) { return null; }
        return HostPattern().IsMatch(host) ? host : null;
    }

    /// <summary>
    /// E.164 normalisation: "+" and 8–15 digits, first digit non-zero. Accepts spaces, dashes, dots, parentheses and a "00" international prefix.
    /// Local numbers with a single leading zero are expanded with <paramref name="defaultCountryCallingCode"/> when given (e.g. "966"); otherwise they are invalid.
    /// Does NOT check that the number exists or follows a country's numbering plan.
    /// </summary>
    public static string? PhoneE164(string? raw, string? defaultCountryCallingCode = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.Length > 40) return null;
        if (s.Any(c => !(char.IsAsciiDigit(c) || c is '+' or ' ' or '-' or '.' or '(' or ')' or ' '))) return null;
        var plus = s.StartsWith('+');
        var digits = new string(s.Where(char.IsAsciiDigit).ToArray());
        if (s.IndexOf('+', plus ? 1 : 0) >= 0) return null; // "+" only as the first character
        if (!plus && digits.StartsWith("00")) { digits = digits[2..]; plus = true; }
        if (!plus)
        {
            var cc = new string((defaultCountryCallingCode ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
            if (cc.Length is 0 or > 3 || !digits.StartsWith('0') || digits.StartsWith("00")) return null;
            digits = cc + digits[1..];
        }
        if (digits.Length is < 8 or > 15 || digits[0] == '0') return null;
        return "+" + digits;
    }

    /// <summary>Lower-cased, trimmed e-mail when it parses as a single plain address with a dotted domain; otherwise null.</summary>
    public static string? Email(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.Length > 256 || s.Any(char.IsWhiteSpace) || s.Contains(',') || s.Contains(';')) return null;
        if (!MailAddress.TryCreate(s, out var addr) || addr.Address != s || addr.DisplayName.Length > 0) return null;
        var at = s.LastIndexOf('@');
        if (at < 1 || !s[(at + 1)..].Contains('.') || s.EndsWith('.')) return null;
        return s.ToLowerInvariant();
    }

    public static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [GeneratedRegex(@"\s+")] private static partial Regex WhitespaceRun();
    [GeneratedRegex(@"^[^:]*:\d+(/.*)?$")] private static partial Regex PortOnly();
    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?\.)+[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")] private static partial Regex HostPattern();
}
