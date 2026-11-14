using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace LeadPilot.Infrastructure.Discovery.Website;

public sealed record ExtractedPage(
    string? Title, string? BusinessName, string? Address, IReadOnlyList<string> Phones, IReadOnlyList<string> Emails, IReadOnlyList<string> SocialLinks, string? ContactPageUrl,
    bool LooksProtected, bool NoIndex);

/// <summary>
/// Reads PUBLIC business facts from HTML: page title, schema.org JSON-LD name/address/phone/e-mail, tel:/mailto: links, published e-mail addresses in the visible text and links to the
/// business's own social profiles. It never infers anything: a value is returned only if it literally appears on the page; everything else stays null/empty.
/// </summary>
public static partial class WebsiteExtractor
{
    private static readonly string[] SocialHosts = ["facebook.com", "instagram.com", "linkedin.com", "twitter.com", "x.com", "youtube.com", "tiktok.com"];
    private static readonly string[] ShareMarkers = ["/sharer", "/share", "/intent/", "/dialog/", "/plugins/", "/tr?", "/embed", "/watch?"];
    private static readonly string[] AssetSuffixes = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".css", ".js", ".woff", ".ico"];
    private static readonly string[] BusinessTypes = ["school", "educationalorganization", "collegeoruniversity", "localbusiness", "organization", "preschool", "elementaryschool", "middleschool", "highschool", "childcare", "library", "store"];

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}")] private static partial Regex EmailPattern();

    public static ExtractedPage Extract(string html, Uri pageUri)
    {
        var doc = new HtmlDocument { OptionMaxNestedChildNodes = 200 };
        doc.LoadHtml(html);

        var robotsMeta = doc.DocumentNode.SelectSingleNode("//meta[translate(@name,'ROBOTS','robots')='robots']")?.GetAttributeValue("content", "")?.ToLowerInvariant() ?? "";
        var noIndex = robotsMeta.Contains("noindex") || robotsMeta.Split(',', StringSplitOptions.TrimEntries).Contains("none");
        var protectedPage = doc.DocumentNode.SelectSingleNode("//input[translate(@type,'PASWORD','pasword')='password']") is not null;
        if (protectedPage || noIndex) return new ExtractedPage(null, null, null, [], [], [], null, protectedPage, noIndex);

        var title = Clean(WebUtility.HtmlDecode(doc.DocumentNode.SelectSingleNode("//title")?.InnerText), 200);
        var ogName = Clean(doc.DocumentNode.SelectSingleNode("//meta[@property='og:site_name']")?.GetAttributeValue("content", null), 200);

        string? name = null, address = null; var phones = new List<string>(); var emails = new List<string>();
        foreach (var script in doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']") ?? Enumerable.Empty<HtmlNode>())
        {
            try { using var json = JsonDocument.Parse(WebUtility.HtmlDecode(script.InnerText)); Walk(json.RootElement, ref name, ref address, phones, emails, 0); }
            catch (JsonException) { /* malformed JSON-LD is ignored */ }
        }

        var social = new List<string>(); string? contact = null;
        foreach (var a in doc.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
        {
            var href = WebUtility.HtmlDecode(a.GetAttributeValue("href", "")).Trim();
            if (href.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)) { AddPhone(phones, href[4..]); continue; }
            if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) { AddEmail(emails, href[7..].Split('?')[0]); continue; }
            if (!Uri.TryCreate(pageUri, href, out var abs) || (abs.Scheme != "http" && abs.Scheme != "https")) continue;
            var host = abs.Host.ToLowerInvariant();
            if (SocialHosts.Any(h => host == h || host.EndsWith("." + h)))
            {
                var path = abs.AbsolutePath.TrimEnd('/');
                if (path.Length > 1 && !ShareMarkers.Any(m => (abs.AbsolutePath + abs.Query).Contains(m, StringComparison.OrdinalIgnoreCase)))
                { var clean = abs.GetLeftPart(UriPartial.Path).TrimEnd('/'); if (!social.Contains(clean, StringComparer.OrdinalIgnoreCase)) social.Add(clean); }
            }
            else if (contact is null && host == pageUri.Host.ToLowerInvariant() && abs.AbsolutePath != pageUri.AbsolutePath
                     && (abs.AbsolutePath.Contains("contact", StringComparison.OrdinalIgnoreCase) || a.InnerText.Contains("contact", StringComparison.OrdinalIgnoreCase)))
                contact = abs.GetLeftPart(UriPartial.Path);
        }

        // e-mail addresses written out in the visible text (scripts/styles removed)
        foreach (var n in doc.DocumentNode.SelectNodes("//script|//style|//noscript")?.ToList() ?? []) n.Remove();
        var text = WebUtility.HtmlDecode(doc.DocumentNode.InnerText);
        foreach (Match m in EmailPattern().Matches(text)) { if (emails.Count >= 5) break; AddEmail(emails, m.Value); }

        return new ExtractedPage(title, Clean(name ?? ogName, 200), Clean(address, 500), phones.Take(5).ToList(), emails.Take(5).ToList(), social.Take(8).ToList(), contact, false, false);
    }

    private static void Walk(JsonElement el, ref string? name, ref string? address, List<string> phones, List<string> emails, int depth)
    {
        if (depth > 6) return;
        switch (el.ValueKind)
        {
            case JsonValueKind.Array: foreach (var i in el.EnumerateArray()) Walk(i, ref name, ref address, phones, emails, depth + 1); break;
            case JsonValueKind.Object:
                if (el.TryGetProperty("@graph", out var graph)) Walk(graph, ref name, ref address, phones, emails, depth + 1);
                var isBusiness = false;
                if (el.TryGetProperty("@type", out var t))
                    isBusiness = (t.ValueKind == JsonValueKind.String ? [t.GetString()] : t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString()).ToArray() : [])
                        .Any(x => x is not null && BusinessTypes.Contains(x.Split('/', '#').Last().ToLowerInvariant()));
                if (!isBusiness) break;
                if (name is null && el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString();
                if (el.TryGetProperty("telephone", out var tel) && tel.ValueKind == JsonValueKind.String) AddPhone(phones, tel.GetString());
                if (el.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String) AddEmail(emails, em.GetString()?.Replace("mailto:", ""));
                if (address is null && el.TryGetProperty("address", out var ad))
                {
                    if (ad.ValueKind == JsonValueKind.String) address = ad.GetString();
                    else if (ad.ValueKind == JsonValueKind.Object)
                        address = string.Join(", ", new[] { "streetAddress", "addressLocality", "addressRegion", "postalCode", "addressCountry" }
                            .Select(k => ad.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null).Where(v => !string.IsNullOrWhiteSpace(v)));
                    if (string.IsNullOrWhiteSpace(address)) address = null;
                }
                break;
        }
    }

    private static void AddPhone(List<string> phones, string? raw)
    {
        var v = Uri.UnescapeDataString(raw ?? "").Trim();   // not UrlDecode: a literal '+' is the international prefix, not a space
        var digits = v.Count(char.IsDigit);
        if (digits is < 7 or > 15 || v.Any(c => !(char.IsDigit(c) || " +-().".Contains(c)))) return;
        if (!phones.Contains(v)) phones.Add(v);
    }

    private static void AddEmail(List<string> emails, string? raw)
    {
        var v = raw?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(v) || v.Length > 254 || !EmailPattern().IsMatch(v)) return;
        if (AssetSuffixes.Any(v.EndsWith) || v.EndsWith("@example.com") || v.Contains("sentry") || v.Contains("wixpress")) return;
        if (!emails.Contains(v)) emails.Add(v);
    }

    private static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var c = Regex.Replace(s.Trim(), @"\s+", " ");
        return c.Length > max ? c[..max] : c;
    }
}
