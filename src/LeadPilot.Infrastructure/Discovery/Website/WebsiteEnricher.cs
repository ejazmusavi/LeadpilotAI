using LeadPilot.Application.Discovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Discovery.Website;

/// <summary>
/// Reads a business's own public website: robots.txt first (cached per host for the lifetime of the scope), then the home page and at most one linked contact page.
/// Pages that look like a login, carry noindex, or are disallowed by robots.txt are not read. Scoped: one instance per job.
/// </summary>
public sealed class WebsiteEnricher(SafeWebFetcher fetcher, IOptions<DiscoveryOptions> options, ILogger<WebsiteEnricher> logger) : IWebsiteEnricher
{
    private readonly Dictionary<string, RobotsTxt> _robots = new(StringComparer.OrdinalIgnoreCase);

    public async Task<WebsiteEnrichmentResult> EnrichAsync(string url, CancellationToken ct)
    {
        var o = options.Value.Website;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var start)) return new(WebsiteFetchOutcome.BlockedByPolicy, null, "Not a valid web address.");
        try
        {
            if (!await AllowedAsync(start, o, ct)) return new(WebsiteFetchOutcome.BlockedByRobots, null, "robots.txt does not allow reading this page.");
            var page = await fetcher.FetchHtmlAsync(start, ct);
            if (page.ContentType == "text/plain") return new(WebsiteFetchOutcome.NotHtml, null, "The address does not serve a web page.");
            var home = WebsiteExtractor.Extract(page.Body, page.FinalUri);
            if (home.LooksProtected) return new(WebsiteFetchOutcome.Protected, null, "The page looks like a login page and was not read.");
            if (home.NoIndex) return new(WebsiteFetchOutcome.BlockedByPolicy, null, "The page asks not to be indexed (noindex) and was not read.");

            var pagesRead = new List<string> { page.FinalUri.ToString() };
            var phones = home.Phones.ToList(); var emails = home.Emails.ToList(); var social = home.SocialLinks.ToList();
            var notes = new List<string>();
            if (home.ContactPageUrl is { } contactUrl && o.MaxPagesPerSite > 1 && Uri.TryCreate(contactUrl, UriKind.Absolute, out var contactUri))
            {
                try
                {
                    if (await AllowedAsync(contactUri, o, ct))
                    {
                        var cp = await fetcher.FetchHtmlAsync(contactUri, ct);
                        var contact = WebsiteExtractor.Extract(cp.Body, cp.FinalUri);
                        if (!contact.LooksProtected && !contact.NoIndex)
                        {
                            pagesRead.Add(cp.FinalUri.ToString());
                            phones.AddRange(contact.Phones.Where(p => !phones.Contains(p))); emails.AddRange(contact.Emails.Where(e => !emails.Contains(e))); social.AddRange(contact.SocialLinks.Where(s => !social.Contains(s)));
                        }
                    }
                }
                catch (FetchException ex) { notes.Add($"Contact page not read: {ex.Message}"); }
            }
            return new(WebsiteFetchOutcome.Fetched, new WebsiteInfo(page.FinalUri.ToString(), home.Title, home.BusinessName, home.Address, phones.Take(5).ToList(), emails.Take(5).ToList(), social.Take(8).ToList(), pagesRead, notes), null);
        }
        catch (FetchException ex)
        {
            logger.LogInformation("Website not read ({Reason}) for host {Host}", ex.Reason, start.Host);   // URL path and query are not logged
            var outcome = ex.Reason switch { FetchFailure.Blocked => WebsiteFetchOutcome.BlockedByPolicy, FetchFailure.Protected => WebsiteFetchOutcome.Protected, FetchFailure.NotAllowedContentType => WebsiteFetchOutcome.NotHtml, _ => WebsiteFetchOutcome.Failed };
            return new(outcome, null, ex.Message);
        }
    }

    private async Task<bool> AllowedAsync(Uri uri, WebsiteFetchOptions o, CancellationToken ct)
    {
        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (!_robots.TryGetValue(origin, out var robots))
        {
            try { robots = RobotsTxt.Parse((await fetcher.FetchRobotsAsync(new Uri(origin + "/robots.txt"), ct)).Body); }
            catch (FetchException ex) when (ex.Reason == FetchFailure.NotFound) { robots = RobotsTxt.AllowEverything; }                       // 4xx: no robots.txt, no restrictions (RFC 9309)
            catch (FetchException ex) when (ex.Reason == FetchFailure.NotAllowedContentType) { robots = RobotsTxt.AllowEverything; }
            catch (FetchException ex) when (ex.Reason == FetchFailure.Blocked) { throw; }                                                    // an internal address is a policy block, not a robots decision
            catch (FetchException) { robots = RobotsTxt.BlockEverything; }                                                                  // 5xx / unreachable / protected: assume disallow
            _robots[origin] = robots;
        }
        return robots.IsAllowed(o.RobotsToken, uri.PathAndQuery);
    }
}
