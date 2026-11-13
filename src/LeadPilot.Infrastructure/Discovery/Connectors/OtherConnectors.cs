using LeadPilot.Application.Discovery;
using LeadPilot.Infrastructure.Discovery.Website;

namespace LeadPilot.Infrastructure.Discovery.Connectors;

/// <summary>Reads public business facts from website addresses the user supplies (seed URLs). Robots-obeying, SSRF-safe; a site that cannot be read yields a warning, never made-up data.</summary>
public sealed class WebsiteConnector(IWebsiteEnricher enricher) : IDiscoveryConnector
{
    public const int PerPage = 5;
    public ConnectorDescriptor Descriptor { get; } = new(ConnectorKeys.Website, "Public websites", "Reads the name, public phone/e-mail and social links from website addresses you provide. Only public pages permitted by robots.txt are read; nothing is guessed.",
        ConnectorKind.Search, RequiresCredentials: false, AcceptsSeedUrls: true);

    public async Task<ConnectorPage> SearchAsync(ConnectorRequest request, CancellationToken ct)
    {
        var urls = request.SeedUrls;
        _ = int.TryParse(request.PageToken, out var start);
        var slice = urls.Skip(start).Take(PerPage).ToList();
        var candidates = new List<DiscoveryCandidate>(); var warnings = new List<string>();
        foreach (var raw in slice)
        {
            var url = CandidateNormalizer.NormalizeWebsite(raw);
            if (url is null) { warnings.Add($"'{Trunc(raw)}' is not a valid public web address."); continue; }
            var result = await enricher.EnrichAsync(url, ct);
            if (result.Outcome != WebsiteFetchOutcome.Fetched || result.Info is null) { warnings.Add($"{new Uri(url).Host}: {result.Reason ?? result.Outcome.ToString()}"); continue; }
            var info = result.Info; var host = new Uri(info.FinalUrl).Host.ToLowerInvariant();
            var name = info.BusinessName ?? info.Title;
            if (name is null) { warnings.Add($"{host}: the page has no business name or title; nothing to record."); continue; }
            candidates.Add(new DiscoveryCandidate
            {
                ExternalId = "website:" + (host.StartsWith("www.") ? host[4..] : host), Name = name, Address = info.Address, Website = info.FinalUrl,
                Phone = info.Phones.FirstOrDefault(), Email = info.Emails.FirstOrDefault(), SocialLinks = info.SocialLinks, SourceName = "Public website", SourceUrl = info.FinalUrl,
                Source = SourceKind.WebsiteSelfPublished, RetrievedAtUtc = request.NowUtc, Notes = info.Notes,
            });
        }
        var next = start + PerPage < urls.Count ? (start + PerPage).ToString() : null;
        return new ConnectorPage(candidates, next, [new ConnectorCall("website.fetch", slice.Count, 0m, null)], warnings);
    }

    private static string Trunc(string s) => s.Length <= 60 ? s : s[..60] + "…";
}

/// <summary>Descriptor-only connectors: the in-app paths (CSV/Excel import, manual entry) and the documented extension points. They never return data.</summary>
public sealed class DescriptorOnlyConnector(ConnectorDescriptor descriptor) : IDiscoveryConnector
{
    public ConnectorDescriptor Descriptor { get; } = descriptor;

    public Task<ConnectorPage> SearchAsync(ConnectorRequest request, CancellationToken ct) =>
        throw new ConnectorNotConfiguredException(Descriptor.Kind == ConnectorKind.InApp
            ? $"{Descriptor.DisplayName} is not run as a discovery search. Use {Descriptor.InAppPath}."
            : Descriptor.NotConfiguredReason ?? $"{Descriptor.DisplayName} is not configured.");
}

public sealed class DiscoveryConnectorRegistry(IEnumerable<IDiscoveryConnector> connectors) : IDiscoveryConnectorRegistry
{
    private readonly IReadOnlyList<IDiscoveryConnector> _all = connectors.OrderBy(c => c.Descriptor.Kind).ThenBy(c => c.Descriptor.DisplayName).ToList();
    public IReadOnlyList<ConnectorDescriptor> All => _all.Select(c => c.Descriptor).ToList();
    public IDiscoveryConnector? Find(string key) => _all.FirstOrDefault(c => c.Descriptor.Key == key);

    public static IEnumerable<IDiscoveryConnector> BuiltInDescriptorConnectors() =>
    [
        new DescriptorOnlyConnector(new(ConnectorKeys.CsvImport, "CSV / Excel import", "Upload a spreadsheet of leads. Uses the Phase 3 import pipeline with the same duplicate detection.", ConnectorKind.InApp, false, InAppPath: "/Imports/New")),
        new DescriptorOnlyConnector(new(ConnectorKeys.ManualEntry, "Manual entry", "Add a lead by hand. Uses the Phase 3 lead form with the same duplicate detection.", ConnectorKind.InApp, false, InAppPath: "/Leads/Create")),
        new DescriptorOnlyConnector(new(ConnectorKeys.AuthorizedDirectory, "Authorized directories", "Extension point for a directory you are licensed to use (API or data feed). Not configured: no adapter exists yet, so it never returns data.", ConnectorKind.ExtensionPoint, true,
            NotConfiguredReason: "No authorized directory adapter is installed. Add one only for a source you have written permission or API terms to use.")),
        new DescriptorOnlyConnector(new(ConnectorKeys.LeadGenIntegration, "Authorized lead-gen integrations", "Extension point for a lead-generation provider you have a contract with. Not configured: it never returns data.", ConnectorKind.ExtensionPoint, true,
            NotConfiguredReason: "No lead-generation provider adapter is installed.")),
        new DescriptorOnlyConnector(new(ConnectorKeys.MetaLeadAds, "Meta Lead Ads", "Extension point for leads people submitted through your own Meta lead forms (webhook with their consent). Not configured: it never returns data.", ConnectorKind.ExtensionPoint, true,
            NotConfiguredReason: "Meta Lead Ads is not connected. It needs a Meta app, page access and a webhook, planned with the messaging phases.")),
    ];
}
