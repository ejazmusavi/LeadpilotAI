using System.Text;
using LeadPilot.Application.Discovery;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Discovery.Website;

public enum FetchFailure { Blocked = 1, Protected, NotFound, TooLarge, NotAllowedContentType, TooManyRedirects, Failed, Timeout }

public sealed class FetchException(FetchFailure reason, string message) : Exception(message)
{
    public FetchFailure Reason { get; } = reason;
}

public sealed record FetchedPage(Uri FinalUri, string ContentType, string Body, int StatusCode);

/// <summary>Sends one request. Production uses the SSRF-safe client; tests substitute a fake to script redirects.</summary>
public interface IHttpSender
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct);
}

public sealed class SafeHttpSender(ISafeHttpClientFactory factory) : IHttpSender
{
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => factory.Create().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
}

/// <summary>
/// Fetches ONE public page: every hop (first request and each redirect) is validated by <see cref="UrlGuard"/> before it is sent; redirects are capped; only HTML/plain-text bodies up to
/// a size limit are read; 401/403/407 are treated as protected content and never retried; no credentials or cookies are ever sent.
/// </summary>
public sealed class SafeWebFetcher(UrlGuard guard, IHttpSender sender, IOptions<DiscoveryOptions> options)
{
    private static readonly string[] HtmlTypes = ["text/html", "application/xhtml+xml"];
    public static readonly string[] TextTypes = ["text/plain"];

    public Task<FetchedPage> FetchHtmlAsync(Uri uri, CancellationToken ct) => FetchAsync(uri, [.. HtmlTypes, .. TextTypes], ct);
    public Task<FetchedPage> FetchRobotsAsync(Uri uri, CancellationToken ct) => FetchAsync(uri, [.. TextTypes, .. HtmlTypes], ct);

    private async Task<FetchedPage> FetchAsync(Uri start, string[] allowedTypes, CancellationToken outer)
    {
        var o = options.Value.Website;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(outer);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(2, o.TimeoutSeconds)));
        var ct = timeout.Token;
        var current = start;
        try
        {
            for (var hop = 0; hop <= o.MaxRedirects; hop++)
            {
                try { await guard.ValidateAsync(current, ct); }
                catch (SsrfBlockedException ex) { throw new FetchException(FetchFailure.Blocked, ex.Message); }

                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.UserAgent.ParseAdd(o.UserAgent);
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.8");
                request.Headers.AcceptLanguage.ParseAdd("en,*;q=0.5");
                HttpResponseMessage response;
                try { response = await sender.SendAsync(request, ct); }
                catch (HttpRequestException ex) when (ex.InnerException is SsrfBlockedException blocked) { throw new FetchException(FetchFailure.Blocked, blocked.Message); }
                catch (SsrfBlockedException blocked) { throw new FetchException(FetchFailure.Blocked, blocked.Message); }
                catch (HttpRequestException ex) { throw new FetchException(FetchFailure.Failed, "The site could not be reached (" + ex.Message + ")."); }

                using (response)
                {
                    var status = (int)response.StatusCode;
                    if (status is 301 or 302 or 303 or 307 or 308)
                    {
                        var location = response.Headers.Location;
                        if (location is null) throw new FetchException(FetchFailure.Failed, "Redirect without a Location header.");
                        current = location.IsAbsoluteUri ? location : new Uri(current, location);
                        continue;
                    }
                    if (status is 401 or 403 or 407) throw new FetchException(FetchFailure.Protected, $"The server refused access (HTTP {status}); protected content is never fetched.");
                    if (status is 404 or 410) throw new FetchException(FetchFailure.NotFound, $"Not found (HTTP {status}).");
                    if (status is < 200 or >= 300) throw new FetchException(FetchFailure.Failed, $"The server answered HTTP {status}.");

                    var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
                    if (!allowedTypes.Contains(type)) throw new FetchException(FetchFailure.NotAllowedContentType, $"Content type '{(type.Length == 0 ? "unknown" : type)}' is not read.");
                    if (response.Content.Headers.ContentLength is { } len && len > o.MaxBytes) throw new FetchException(FetchFailure.TooLarge, "The page is larger than the size limit.");
                    var body = await ReadLimitedAsync(response, o.MaxBytes, ct);
                    return new FetchedPage(current, type, body, status);
                }
            }
            throw new FetchException(FetchFailure.TooManyRedirects, $"More than {o.MaxRedirects} redirects.");
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested) { throw new FetchException(FetchFailure.Timeout, "The site did not answer in time."); }
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024]; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + read > max) throw new FetchException(FetchFailure.TooLarge, "The page is larger than the size limit.");
            ms.Write(buffer, 0, read);
        }
        Encoding enc;
        try { enc = Encoding.GetEncoding(response.Content.Headers.ContentType?.CharSet?.Trim('"') ?? "utf-8"); } catch (ArgumentException) { enc = Encoding.UTF8; }
        return enc.GetString(ms.ToArray());
    }
}
