using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LeadPilot.Application.Discovery;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Discovery.Connectors;

/// <summary>
/// Google Places API (New) — Text Search. Real HTTP adapter; the API key is sent in the X-Goog-Api-Key header (never in the URL, never logged) and is supplied per call by the engine from the
/// ACTIVE tenant's encrypted configuration. A field mask limits the response to the fields we use. Not verified against the live service in this repository's CI (no key / no network): see docs/INTEGRATIONS.md.
/// </summary>
public sealed class GooglePlacesConnector(IHttpClientFactory http, IOptions<DiscoveryOptions> options) : IDiscoveryConnector
{
    public const string HttpClientName = "google-places";
    public const string Operation = "places.searchText";
    /// <summary>Everything the adapter reads. Requesting websiteUri/internationalPhoneNumber places the call in Google's Text Search Enterprise SKU (billed at the highest SKU of the fields requested).</summary>
    public const string FieldMask = "places.id,places.displayName,places.formattedAddress,places.addressComponents,places.types,places.websiteUri,places.internationalPhoneNumber,places.googleMapsUri,places.businessStatus,nextPageToken";
    public const string AttributionText = "Place data from Google Maps";

    public ConnectorDescriptor Descriptor { get; } = new(ConnectorKeys.GooglePlaces, "Google Places", "Searches businesses by text query and location through the official Google Places API (New). Needs your own API key; usage is billed by Google to your Cloud account.",
        ConnectorKind.Search, RequiresCredentials: true, Attribution: AttributionText);

    public async Task<ConnectorPage> SearchAsync(ConnectorRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey)) throw new ConnectorNotConfiguredException("No Google Places API key is configured for this workspace.");
        var o = options.Value;
        var body = new Dictionary<string, object?>
        {
            ["textQuery"] = request.Query.Text, ["pageSize"] = Math.Clamp(request.PageSize, 1, 20),
        };
        if (request.Query.RegionCode is { } rc) body["regionCode"] = rc;
        if (request.Query.PlaceType is { } pt) body["includedType"] = pt;
        if (!string.IsNullOrEmpty(request.PageToken)) body["pageToken"] = request.PageToken;

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/places:searchText") { Content = JsonContent.Create(body) };
        msg.Headers.Add("X-Goog-Api-Key", request.ApiKey);
        msg.Headers.Add("X-Goog-FieldMask", FieldMask);

        HttpResponseMessage response;
        try { response = await http.CreateClient(HttpClientName).SendAsync(msg, ct); }
        catch (HttpRequestException ex) { throw new ConnectorTransientException("Google Places could not be reached.", new ConnectorCall(Operation, 1, 0, null), ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new ConnectorTransientException("Google Places did not answer in time.", new ConnectorCall(Operation, 1, 0, null), ex); }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                var call = new ConnectorCall(Operation, 1, 0m, status);
                var detail = Scrub(await ReadErrorAsync(response, ct), request.ApiKey);
                throw status switch
                {
                    429 => new ConnectorQuotaException($"Google Places quota or rate limit reached (HTTP 429). {detail}".Trim(), call),
                    401 or 403 => new ConnectorAuthException($"Google rejected the API key or the request (HTTP {status}). Check the key, that Places API (New) is enabled and that billing is active. {detail}".Trim(), call),
                    >= 500 => new ConnectorTransientException($"Google Places is temporarily unavailable (HTTP {status}).", call),
                    _ => new ConnectorPermanentException($"Google Places rejected the request (HTTP {status}). {detail}".Trim(), call),
                };
            }

            PlacesResponse? parsed;
            try { parsed = await response.Content.ReadFromJsonAsync<PlacesResponse>(ct); }
            catch (JsonException ex) { throw new ConnectorTransientException("Google Places returned an unreadable response.", new ConnectorCall(Operation, 1, request.CostPerCallUsd, status), ex); }

            var expires = request.NowUtc.AddDays(Math.Max(1, o.GoogleContentRetentionDays));
            var candidates = (parsed?.Places ?? []).Where(p => !string.IsNullOrWhiteSpace(p.Id)).Select(p => Map(p, request.NowUtc, expires)).ToList();
            return new ConnectorPage(candidates, string.IsNullOrEmpty(parsed?.NextPageToken) ? null : parsed!.NextPageToken, [new ConnectorCall(Operation, 1, request.CostPerCallUsd, status)]);
        }
    }

    internal static DiscoveryCandidate Map(Place p, DateTime now, DateTime expires)
    {
        string? Component(params string[] types) => p.AddressComponents?.FirstOrDefault(c => c.Types?.Any(t => types.Contains(t)) == true)?.LongText;
        return new DiscoveryCandidate
        {
            ExternalId = p.Id!, Name = p.DisplayName?.Text, Types = p.Types ?? [], Address = p.FormattedAddress,
            Country = Component("country"), Province = Component("administrative_area_level_1"), City = Component("locality", "administrative_area_level_2"),
            District = Component("sublocality_level_1", "sublocality"), Area = Component("neighborhood"),
            Website = p.WebsiteUri, Phone = p.InternationalPhoneNumber, SourceName = "Google Places", SourceUrl = p.GoogleMapsUri, Attribution = AttributionText,
            BusinessStatus = p.BusinessStatus, Source = SourceKind.StructuredProvider, RetrievedAtUtc = now, ExpiresAtUtc = expires,
        };
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage r, CancellationToken ct)
    {
        try
        {
            var doc = await r.Content.ReadFromJsonAsync<JsonElement>(ct);
            var msg = doc.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() : null;
            return msg is null ? "" : msg.Length > 200 ? msg[..200] : msg;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return ""; }
    }

    private static string Scrub(string text, string key) => string.IsNullOrEmpty(key) ? text : text.Replace(key, "[key]");

    // ---- response shape (Places API New, Text Search) ----
    internal sealed class PlacesResponse { [JsonPropertyName("places")] public List<Place>? Places { get; set; } [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; } }
    internal sealed class Place
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("displayName")] public LocalizedText? DisplayName { get; set; }
        [JsonPropertyName("formattedAddress")] public string? FormattedAddress { get; set; }
        [JsonPropertyName("addressComponents")] public List<AddressComponent>? AddressComponents { get; set; }
        [JsonPropertyName("types")] public List<string>? Types { get; set; }
        [JsonPropertyName("websiteUri")] public string? WebsiteUri { get; set; }
        [JsonPropertyName("internationalPhoneNumber")] public string? InternationalPhoneNumber { get; set; }
        [JsonPropertyName("googleMapsUri")] public string? GoogleMapsUri { get; set; }
        [JsonPropertyName("businessStatus")] public string? BusinessStatus { get; set; }
    }
    internal sealed class LocalizedText { [JsonPropertyName("text")] public string? Text { get; set; } }
    internal sealed class AddressComponent { [JsonPropertyName("longText")] public string? LongText { get; set; } [JsonPropertyName("types")] public List<string>? Types { get; set; } }
}
