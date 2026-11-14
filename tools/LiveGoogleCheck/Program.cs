// Minimal LIVE check of the Google Places adapter: ONE request, pageSize 5. Prints counts and field presence only (no names, no raw response, no key).
using LeadPilot.Application.Discovery;
using LeadPilot.Infrastructure.Discovery.Connectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

var sc = new ServiceCollection();
sc.AddHttpClient(GooglePlacesConnector.HttpClientName, c => c.BaseAddress = new Uri("https://places.googleapis.com"));
var sp = sc.BuildServiceProvider();
var connector = new GooglePlacesConnector(sp.GetRequiredService<IHttpClientFactory>(), Options.Create(new DiscoveryOptions()));
// Key: GOOGLE_PLACES_API_KEY if set (never printed), otherwise a dummy value for environments whose network proxy injects the real key into X-Goog-Api-Key.
var key = Environment.GetEnvironmentVariable("GOOGLE_PLACES_API_KEY") ?? "DUMMY-KEY-REPLACED-BY-PROXY";
var q = DiscoveryQueryBuilder.Build(new SearchLocation("Pakistan", "Punjab", "Lahore", null, null), ["private school"], "school", null)[0];
Console.WriteLine("query: " + q.Text + " | region " + q.RegionCode + " | type " + q.PlaceType);
for (var attempt = 1; attempt <= 2; attempt++)
{
    try
    {
        var page = await connector.SearchAsync(new ConnectorRequest(q, key, null, 5, [], 0.035m, DateTime.UtcNow), default);
        var c = page.Candidates;
        Console.WriteLine($"OK http={page.Calls[0].HttpStatus} calls={page.Calls.Sum(x => x.Calls)} candidates={c.Count} nextPageToken={(page.NextPageToken is null ? "none" : "present")}");
        Console.WriteLine($"with name={c.Count(x => x.Name != null)} address={c.Count(x => x.Address != null)} website={c.Count(x => x.Website != null)} phone={c.Count(x => x.Phone != null)} city={c.Count(x => x.City != null)} province={c.Count(x => x.Province != null)} country={c.Count(x => x.Country != null)} sourceUrl={c.Count(x => x.SourceUrl != null)} status={string.Join(",", c.Select(x => x.BusinessStatus).Distinct())}");
        Console.WriteLine("types seen: " + string.Join(",", c.SelectMany(x => x.Types).Distinct().Take(8)));
        Console.WriteLine("ids look like place ids: " + c.All(x => x.ExternalId.StartsWith("ChIJ")) + " | attribution=" + c.FirstOrDefault()?.Attribution + " | expires in days=" + (c.FirstOrDefault()?.ExpiresAtUtc - c.FirstOrDefault()?.RetrievedAtUtc)?.TotalDays);
        break;
    }
    catch (ConnectorException ex)
    {
        Console.WriteLine($"ATTEMPT {attempt} FAILED {ex.GetType().Name} code={ex.Code} retryable={ex.Retryable} http={ex.Call?.HttpStatus} :: {ex.Message}");
        if (attempt == 1) await Task.Delay(65000);
    }
}
