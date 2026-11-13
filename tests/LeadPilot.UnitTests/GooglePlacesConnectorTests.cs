using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LeadPilot.Application.Discovery;
using LeadPilot.Infrastructure.Discovery.Connectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LeadPilot.UnitTests;

/// <summary>
/// Runs the real Google Places adapter against a LOCAL fake HTTP server that answers with recorded-SHAPE JSON (field names and structure of the Places API (New) Text Search response).
/// This proves request construction, paging, mapping and error handling. It does NOT prove behaviour against the live Google service (no key / network in CI).
/// </summary>
public sealed class GooglePlacesConnectorTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _baseUrl;
    private readonly List<(string Path, string? Key, string? Mask, string Body)> _requests = [];
    private Func<int, (int Status, string Body, string? RetryAfter)> _respond = _ => (200, "{}", null);
    private int _n;

    public GooglePlacesConnectorTests()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        _baseUrl = $"http://127.0.0.1:{port}"; _listener.Prefixes.Add(_baseUrl + "/"); _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx; try { ctx = await _listener.GetContextAsync(); } catch { return; }
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8); var body = await reader.ReadToEndAsync();
                _requests.Add((ctx.Request.Url!.AbsolutePath, ctx.Request.Headers["X-Goog-Api-Key"], ctx.Request.Headers["X-Goog-FieldMask"], body));
                var (status, resp, retry) = _respond(Interlocked.Increment(ref _n));
                ctx.Response.StatusCode = status; ctx.Response.ContentType = "application/json"; if (retry is not null) ctx.Response.Headers["Retry-After"] = retry;
                var bytes = Encoding.UTF8.GetBytes(resp); await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
            }
        });
    }

    public void Dispose() => _listener.Close();

    private GooglePlacesConnector Connector()
    {
        var sc = new ServiceCollection(); sc.AddHttpClient(GooglePlacesConnector.HttpClientName, c => c.BaseAddress = new Uri(_baseUrl));
        return new GooglePlacesConnector(sc.BuildServiceProvider().GetRequiredService<IHttpClientFactory>(), Options.Create(new DiscoveryOptions { GoogleContentRetentionDays = 30 }));
    }

    private static ConnectorRequest Req(string? token = null, string? key = "TEST-KEY-1234567890") =>
        new(new ProviderQuery("private school in Lahore, Punjab, Pakistan", "PK", "school", "Pakistan", "Punjab", "Lahore", null, null), key, token, 20, [], 0.035m, new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc));

    private const string Page1 = """
        {"places":[
          {"id":"ChIJ_A","displayName":{"text":"Beaconhouse School System","languageCode":"en"},"formattedAddress":"12 Canal Rd, Lahore, Punjab, Pakistan",
           "addressComponents":[{"longText":"Pakistan","shortText":"PK","types":["country","political"]},{"longText":"Punjab","shortText":"Punjab","types":["administrative_area_level_1","political"]},
             {"longText":"Lahore","shortText":"Lahore","types":["locality","political"]},{"longText":"Gulberg","shortText":"Gulberg","types":["sublocality_level_1","sublocality","political"]}],
           "types":["school","point_of_interest","establishment"],"websiteUri":"https://beaconhouse.example/","internationalPhoneNumber":"+92 42 3575 1234","googleMapsUri":"https://maps.google.com/?cid=1","businessStatus":"OPERATIONAL"},
          {"id":"ChIJ_B","displayName":{"text":"No Details Academy"},"types":["school"]}
        ],"nextPageToken":"TOKEN-2"}
        """;
    private const string Page2 = """{"places":[{"id":"ChIJ_C","displayName":{"text":"Third School"},"businessStatus":"CLOSED_PERMANENTLY","types":["school"]}]}""";

    [Fact]
    public async Task Sends_the_documented_request_with_key_in_header_field_mask_and_body_never_in_the_url()
    {
        _respond = _ => (200, Page1, null);
        await Connector().SearchAsync(Req(), default);
        var r = Assert.Single(_requests);
        Assert.Equal("/v1/places:searchText", r.Path); Assert.Equal("TEST-KEY-1234567890", r.Key); Assert.Equal(GooglePlacesConnector.FieldMask, r.Mask);
        Assert.Contains("places.id", r.Mask); Assert.Contains("nextPageToken", r.Mask); Assert.DoesNotContain("*", r.Mask);
        using var doc = JsonDocument.Parse(r.Body); var b = doc.RootElement;
        Assert.Equal("private school in Lahore, Punjab, Pakistan", b.GetProperty("textQuery").GetString()); Assert.Equal(20, b.GetProperty("pageSize").GetInt32());
        Assert.Equal("PK", b.GetProperty("regionCode").GetString()); Assert.Equal("school", b.GetProperty("includedType").GetString()); Assert.False(b.TryGetProperty("pageToken", out _));
    }

    [Fact]
    public async Task Maps_places_to_candidates_with_attribution_and_retention_and_leaves_missing_fields_null()
    {
        _respond = _ => (200, Page1, null);
        var page = await Connector().SearchAsync(Req(), default);
        Assert.Equal("TOKEN-2", page.NextPageToken);
        var a = page.Candidates[0];
        Assert.Equal("ChIJ_A", a.ExternalId); Assert.Equal("Beaconhouse School System", a.Name); Assert.Equal("Pakistan", a.Country); Assert.Equal("Punjab", a.Province); Assert.Equal("Lahore", a.City); Assert.Equal("Gulberg", a.District);
        Assert.Equal("https://beaconhouse.example/", a.Website); Assert.Equal("+92 42 3575 1234", a.Phone); Assert.Equal("https://maps.google.com/?cid=1", a.SourceUrl); Assert.Equal("OPERATIONAL", a.BusinessStatus);
        Assert.Equal("Place data from Google Maps", a.Attribution); Assert.Equal(SourceKind.StructuredProvider, a.Source);
        Assert.Equal(new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc), a.RetrievedAtUtc); Assert.Equal(a.RetrievedAtUtc.AddDays(30), a.ExpiresAtUtc);
        var b = page.Candidates[1];
        Assert.Equal("ChIJ_B", b.ExternalId); Assert.Null(b.Website); Assert.Null(b.Phone); Assert.Null(b.Address); Assert.Null(b.Email); Assert.Null(b.City);
        var call = Assert.Single(page.Calls); Assert.Equal(("places.searchText", 1, 0.035m, 200), (call.Operation, call.Calls, call.EstimatedCostUsd, call.HttpStatus));
    }

    [Fact]
    public async Task Pages_with_the_returned_token_and_stops_when_there_is_none()
    {
        _respond = n => (200, n == 1 ? Page1 : Page2, null);
        var c = Connector();
        var p1 = await c.SearchAsync(Req(), default);
        var p2 = await c.SearchAsync(Req(p1.NextPageToken), default);
        Assert.Null(p2.NextPageToken); Assert.Equal("ChIJ_C", p2.Candidates.Single().ExternalId);
        using var doc = JsonDocument.Parse(_requests[1].Body); Assert.Equal("TOKEN-2", doc.RootElement.GetProperty("pageToken").GetString());
    }

    [Fact] public async Task An_empty_result_is_an_empty_page_not_an_error() { _respond = _ => (200, "{}", null); var p = await Connector().SearchAsync(Req(), default); Assert.Empty(p.Candidates); Assert.Null(p.NextPageToken); }

    [Fact]
    public async Task Quota_429_is_a_retryable_quota_error_that_costs_nothing_but_is_counted()
    {
        _respond = _ => (429, """{"error":{"code":429,"message":"Quota exceeded for TEST-KEY-1234567890","status":"RESOURCE_EXHAUSTED"}}""", "30");
        var ex = await Assert.ThrowsAsync<ConnectorQuotaException>(() => Connector().SearchAsync(Req(), default));
        Assert.True(ex.Retryable); Assert.Equal("provider_quota_exceeded", ex.Code); Assert.Equal(429, ex.Call!.HttpStatus); Assert.Equal(0m, ex.Call.EstimatedCostUsd); Assert.Equal(1, ex.Call.Calls);
        Assert.DoesNotContain("TEST-KEY-1234567890", ex.Message);   // the key is scrubbed from provider messages
    }

    [Theory] [InlineData(403)] [InlineData(401)]
    public async Task Rejected_key_is_a_non_retryable_auth_error(int status)
    {
        _respond = _ => (status, """{"error":{"code":403,"message":"Places API (New) has not been used in project 123 before or it is disabled.","status":"PERMISSION_DENIED"}}""", null);
        var ex = await Assert.ThrowsAsync<ConnectorAuthException>(() => Connector().SearchAsync(Req(), default));
        Assert.False(ex.Retryable); Assert.Contains("billing", ex.Message); Assert.Contains("disabled", ex.Message);
    }

    [Fact] public async Task Server_errors_are_transient_and_bad_requests_are_permanent()
    {
        _respond = _ => (503, "unavailable", null);
        Assert.True((await Assert.ThrowsAsync<ConnectorTransientException>(() => Connector().SearchAsync(Req(), default))).Retryable);
        _respond = _ => (400, """{"error":{"code":400,"message":"Invalid value at 'region_code'","status":"INVALID_ARGUMENT"}}""", null);
        var ex = await Assert.ThrowsAsync<ConnectorPermanentException>(() => Connector().SearchAsync(Req(), default));
        Assert.False(ex.Retryable); Assert.Contains("region_code", ex.Message);
    }

    [Fact] public async Task A_garbled_success_body_is_transient() { _respond = _ => (200, "<html>proxy error</html>", null); await Assert.ThrowsAsync<ConnectorTransientException>(() => Connector().SearchAsync(Req(), default)); }

    [Fact] public async Task Without_a_key_nothing_is_sent_and_it_is_reported_as_not_configured()
    {
        await Assert.ThrowsAsync<ConnectorNotConfiguredException>(() => Connector().SearchAsync(Req(key: null), default));
        await Assert.ThrowsAsync<ConnectorNotConfiguredException>(() => Connector().SearchAsync(Req(key: " "), default));
        Assert.Empty(_requests);
    }

    [Fact] public async Task An_unreachable_server_is_transient()
    {
        _listener.Close();
        await Assert.ThrowsAsync<ConnectorTransientException>(() => Connector().SearchAsync(Req(), default));
    }
}

public class ExtensionPointConnectorTests
{
    [Fact]
    public async Task Stubs_say_not_configured_and_never_return_data()
    {
        foreach (var c in DiscoveryConnectorRegistry.BuiltInDescriptorConnectors())
        {
            var ex = await Assert.ThrowsAsync<ConnectorNotConfiguredException>(() => c.SearchAsync(new ConnectorRequest(new ProviderQuery("x", null, null, null, null, null, null, null), "k", null, 20, [], 0, DateTime.UtcNow), default));
            Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        }
        var registry = new DiscoveryConnectorRegistry(DiscoveryConnectorRegistry.BuiltInDescriptorConnectors());
        Assert.Equal([ConnectorKeys.AuthorizedDirectory, ConnectorKeys.LeadGenIntegration, ConnectorKeys.MetaLeadAds], registry.All.Where(d => d.Kind == ConnectorKind.ExtensionPoint).Select(d => d.Key).Order().ToArray().Order().ToArray());
        Assert.All(registry.All.Where(d => d.Kind == ConnectorKind.InApp), d => Assert.StartsWith("/", d.InAppPath));
    }
}
