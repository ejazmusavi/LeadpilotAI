using System.Security.Cryptography;
using System.Text;

namespace LeadPilot.Infrastructure.Messaging.Waha;

/// <summary>
/// WAHA webhook signature: <c>X-Webhook-Hmac</c> = lower-case hex HMAC-SHA512 of the RAW request body, key = the webhook's hmac.key
/// (docs: https://waha.devlike.pro/docs/how-to/events/ "HMAC authentication"). Comparison is constant-time.
/// </summary>
public static class WahaSignature
{
    public const string Algorithm = "sha512";

    public static string Compute(string secret, ReadOnlySpan<byte> body)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var mac = HMACSHA512.HashData(key, body);
        return Convert.ToHexStringLower(mac);
    }

    /// <summary>True only for a well-formed hex digest equal to the expected one. No early exit on the first differing byte; wrong length or non-hex input is a plain false.</summary>
    public static bool Verify(string secret, ReadOnlySpan<byte> body, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signatureHeader)) return false;
        var sig = signatureHeader.Trim();
        if (sig.Length != 128) return false;               // SHA-512 = 64 bytes = 128 hex chars
        byte[] provided;
        try { provided = Convert.FromHexString(sig); } catch (FormatException) { return false; }
        var expected = HMACSHA512.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
