using System.Net;
using System.Net.Sockets;
using System.Text;
using LeadPilot.Application.Discovery;
using LeadPilot.Infrastructure.Discovery.Website;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeadPilot.UnitTests;

public class IpAddressClassifierTests
{
    [Theory]
    [InlineData("127.0.0.1")] [InlineData("127.255.255.254")] [InlineData("10.0.0.1")] [InlineData("172.16.0.1")] [InlineData("172.31.255.255")] [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] [InlineData("169.254.0.1")] [InlineData("100.64.0.1")] [InlineData("0.0.0.0")] [InlineData("224.0.0.1")] [InlineData("255.255.255.255")]
    [InlineData("240.0.0.1")] [InlineData("198.18.0.1")] [InlineData("192.0.2.1")] [InlineData("203.0.113.5")]
    [InlineData("::1")] [InlineData("::")] [InlineData("fe80::1")] [InlineData("fd00:ec2::254")] [InlineData("fc00::1")] [InlineData("ff02::1")] [InlineData("2001:db8::1")]
    [InlineData("::ffff:127.0.0.1")] [InlineData("::ffff:10.1.2.3")] [InlineData("::ffff:169.254.169.254")] [InlineData("64:ff9b::7f00:1")] [InlineData("2002:7f00:1::1")] [InlineData("2002:a9fe:a9fe::1")] [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    public void Non_public_addresses_are_rejected(string ip) => Assert.False(IpAddressClassifier.IsPublic(IPAddress.Parse(ip)), ip);

    [Theory]
    [InlineData("8.8.8.8")] [InlineData("1.1.1.1")] [InlineData("93.184.216.34")] [InlineData("172.15.0.1")] [InlineData("172.32.0.1")] [InlineData("100.63.0.1")] [InlineData("100.128.0.1")]
    [InlineData("2606:4700:4700::1111")] [InlineData("2a00:1450:4001::1")] [InlineData("::ffff:8.8.8.8")]
    public void Public_addresses_are_allowed(string ip) => Assert.True(IpAddressClassifier.IsPublic(IPAddress.Parse(ip)), ip);
}

internal sealed class FakeDns(Dictionary<string, string[]> map) : IDnsResolver
{
    public int Calls;
    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        Calls++;
        return map.TryGetValue(host, out var ips) ? Task.FromResult<IReadOnlyList<IPAddress>>(ips.Select(IPAddress.Parse).ToList()) : throw new SocketException((int)SocketError.HostNotFound);
    }
}

/// <summary>Rebinding-style resolver: public on the first answer, internal afterwards.</summary>
internal sealed class RebindingDns : IDnsResolver
{
    public int Calls;
    public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse(Interlocked.Increment(ref Calls) == 1 ? "93.184.216.34" : "127.0.0.1")]);
}

public class UrlGuardTests
{
    private static UrlGuard Guard(params (string Host, string[] Ips)[] map) => new(new FakeDns(map.ToDictionary(m => m.Host, m => m.Ips)), new PublicOnlyAddressPolicy());
    private static Task<Uri> Check(UrlGuard g, string url) => g.ValidateAsync(new Uri(url), default);

    [Fact] public async Task A_public_host_is_allowed() => await Check(Guard(("school.example.com", ["93.184.216.34"])), "https://school.example.com/about");

    [Theory]
    [InlineData("http://127.0.0.1/")] [InlineData("http://localhost/")] [InlineData("http://LOCALHOST./")] [InlineData("http://169.254.169.254/latest/meta-data/")] [InlineData("http://[::1]/")]
    [InlineData("http://10.0.0.5/admin")] [InlineData("http://192.168.0.1/")] [InlineData("http://2130706433/")] [InlineData("http://0x7f000001/")] [InlineData("http://0177.0.0.1/")] [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://metadata.google.internal/computeMetadata/v1/")] [InlineData("http://printer.local/")] [InlineData("http://intranet/")] [InlineData("http://service.internal/")]
    [InlineData("ftp://school.example.com/")] [InlineData("file:///etc/passwd")] [InlineData("https://user:pass@school.example.com/")] [InlineData("http://school.example.com:8080/")] [InlineData("http://school.example.com:22/")]
    public async Task Internal_or_unsafe_targets_are_blocked_before_any_request(string url) =>
        await Assert.ThrowsAsync<SsrfBlockedException>(() => Check(Guard(("school.example.com", ["93.184.216.34"])), url));

    [Fact] public async Task A_public_name_that_resolves_to_loopback_is_blocked() =>
        await Assert.ThrowsAsync<SsrfBlockedException>(() => Check(Guard(("evil.example.com", ["127.0.0.1"])), "https://evil.example.com/"));

    [Fact] public async Task A_name_with_one_internal_answer_among_public_ones_is_blocked() =>
        await Assert.ThrowsAsync<SsrfBlockedException>(() => Check(Guard(("mixed.example.com", ["93.184.216.34", "10.0.0.7"])), "https://mixed.example.com/"));

    [Fact] public async Task An_unresolvable_host_is_blocked() => await Assert.ThrowsAsync<SsrfBlockedException>(() => Check(Guard(), "https://nxdomain.example.com/"));
}

internal sealed class ScriptedSender(Func<HttpRequestMessage, HttpResponseMessage> respond) : IHttpSender
{
    public List<Uri> Requested { get; } = [];
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Requested.Add(request.RequestUri!); return Task.FromResult(respond(request)); }
}

public class SafeWebFetcherTests
{
    private static readonly string[] Public = ["93.184.216.34"];
    private static SafeWebFetcher Fetcher(ScriptedSender sender, int maxRedirects = 3, int maxBytes = 1000) =>
        new(new UrlGuard(new FakeDns(new() { ["school.example.com"] = Public, ["other.example.com"] = Public, ["evil.example.com"] = ["127.0.0.1"] }), new PublicOnlyAddressPolicy()), sender,
            Options.Create(new DiscoveryOptions { Website = { MaxRedirects = maxRedirects, MaxBytes = maxBytes, TimeoutSeconds = 5 } }));

    private static HttpResponseMessage Html(string body, string type = "text/html", HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body, Encoding.UTF8, type) };
    private static HttpResponseMessage Redirect(string to, HttpStatusCode code = HttpStatusCode.Found) { var r = new HttpResponseMessage(code); r.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute); return r; }

    [Fact] public async Task Fetches_a_public_html_page_with_a_descriptive_user_agent()
    {
        string? ua = null;
        var s = new ScriptedSender(r => { ua = r.Headers.UserAgent.ToString(); return Html("<html>ok</html>"); });
        var page = await Fetcher(s).FetchHtmlAsync(new Uri("https://school.example.com/"), default);
        Assert.Equal("<html>ok</html>", page.Body); Assert.Contains("LeadPilotAI-Bot", ua); Assert.Contains("robots", ua);
    }

    [Fact] public async Task A_redirect_to_the_metadata_endpoint_is_blocked_on_that_hop()
    {
        var s = new ScriptedSender(r => r.RequestUri!.Host == "school.example.com" ? Redirect("http://169.254.169.254/latest/meta-data/") : Html("secret"));
        var ex = await Assert.ThrowsAsync<FetchException>(() => Fetcher(s).FetchHtmlAsync(new Uri("https://school.example.com/"), default));
        Assert.Equal(FetchFailure.Blocked, ex.Reason); Assert.Single(s.Requested);   // the internal address was never requested
    }

    [Theory] [InlineData("http://127.0.0.1:8080/admin")] [InlineData("http://localhost/")] [InlineData("http://evil.example.com/")] [InlineData("http://[::1]/")] [InlineData("file:///etc/passwd")]
    public async Task A_redirect_to_any_internal_target_is_blocked(string target)
    {
        var s = new ScriptedSender(r => Redirect(target));
        var ex = await Assert.ThrowsAsync<FetchException>(() => Fetcher(s).FetchHtmlAsync(new Uri("https://school.example.com/"), default));
        Assert.Equal(FetchFailure.Blocked, ex.Reason);
    }

    [Fact] public async Task Relative_redirects_are_followed_and_validated()
    {
        var s = new ScriptedSender(r => r.RequestUri!.AbsolutePath == "/" ? Redirect("/home") : Html("home"));
        var page = await Fetcher(s).FetchHtmlAsync(new Uri("https://school.example.com/"), default);
        Assert.Equal("/home", page.FinalUri.AbsolutePath);
    }

    [Fact] public async Task Redirect_loops_stop_at_the_limit()
    {
        var s = new ScriptedSender(r => Redirect("https://school.example.com/again"));
        var ex = await Assert.ThrowsAsync<FetchException>(() => Fetcher(s, maxRedirects: 3).FetchHtmlAsync(new Uri("https://school.example.com/"), default));
        Assert.Equal(FetchFailure.TooManyRedirects, ex.Reason); Assert.Equal(4, s.Requested.Count);
    }

    [Theory] [InlineData(401)] [InlineData(403)] [InlineData(407)]
    public async Task Protected_responses_are_never_read_or_retried(int code)
    {
        var s = new ScriptedSender(r => Html("<form>login</form>", code: (HttpStatusCode)code));
        var ex = await Assert.ThrowsAsync<FetchException>(() => Fetcher(s).FetchHtmlAsync(new Uri("https://school.example.com/members"), default));
        Assert.Equal(FetchFailure.Protected, ex.Reason); Assert.Single(s.Requested);
    }

    [Theory] [InlineData("application/pdf")] [InlineData("image/png")] [InlineData("application/octet-stream")] [InlineData("application/json")]
    public async Task Only_html_and_text_are_read(string type)
    {
        var s = new ScriptedSender(r => Html("x", type));
        var ex = await Assert.ThrowsAsync<FetchException>(() => Fetcher(s).FetchHtmlAsync(new Uri("https://school.example.com/"), default));
        Assert.Equal(FetchFailure.NotAllowedContentType, ex.Reason);
    }

    [Fact] public async Task Oversized_bodies_are_rejected_even_without_a_content_length()
    {
        var s = new ScriptedSender(r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[5000])) { Headers = { ContentType = new("text/html") } } });
        var ex = await Assert.ThrowsAsync<FetchException>(() => Fetcher(s, maxBytes: 1000).FetchHtmlAsync(new Uri("https://school.example.com/"), default));
        Assert.Equal(FetchFailure.TooLarge, ex.Reason);
    }

    [Fact] public async Task Not_found_and_server_errors_are_reported_distinctly()
    {
        Assert.Equal(FetchFailure.NotFound, (await Assert.ThrowsAsync<FetchException>(() => Fetcher(new ScriptedSender(r => Html("", code: HttpStatusCode.NotFound))).FetchHtmlAsync(new Uri("https://school.example.com/"), default))).Reason);
        Assert.Equal(FetchFailure.Failed, (await Assert.ThrowsAsync<FetchException>(() => Fetcher(new ScriptedSender(r => Html("", code: HttpStatusCode.BadGateway))).FetchHtmlAsync(new Uri("https://school.example.com/"), default))).Reason);
    }

    [Fact] public async Task The_initial_url_is_validated_too()
    {
        var s = new ScriptedSender(r => Html("x"));
        await Assert.ThrowsAsync<FetchException>(() => Fetcher(s).FetchHtmlAsync(new Uri("http://169.254.169.254/"), default));
        Assert.Empty(s.Requested);
    }
}

/// <summary>Real sockets: proves the handler itself (not just the pre-check) refuses internal addresses and cannot be rebound.</summary>
public class SafeHttpClientFactoryTests
{
    private static SafeHttpClientFactory Factory(IDnsResolver dns, IAddressPolicy? policy = null) => new(dns, policy ?? new PublicOnlyAddressPolicy(), Options.Create(new DiscoveryOptions { Website = { TimeoutSeconds = 5 } }));
    private sealed class AllowLoopback : IAddressPolicy { public bool IsAllowed(IPAddress a) => IPAddress.IsLoopback(a); }

    [Fact] public async Task A_public_name_rebound_to_loopback_cannot_connect_and_nothing_listens_to_it()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = false; _ = Task.Run(async () => { try { using var c = await listener.AcceptTcpClientAsync(); accepted = true; } catch { } });
        using var factory = Factory(new FakeDns(new() { ["rebind.example.com"] = ["127.0.0.1"] }));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => factory.Create().GetAsync($"http://rebind.example.com:{port}/"));
        Assert.True(ex is SsrfBlockedException || ex.InnerException is SsrfBlockedException, ex.ToString());
        await Task.Delay(200); Assert.False(accepted);
    }

    [Fact] public async Task The_name_is_resolved_once_per_connection_and_the_validated_address_is_used()
    {
        var dns = new RebindingDns(); using var factory = Factory(dns);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try { using var _ = await factory.Create().GetAsync("http://school.example.com/", cts.Token); } catch (Exception) { /* reachability of the public address is irrelevant here */ }
        Assert.Equal(1, dns.Calls);   // a second lookup (which would answer 127.0.0.1) never happens
    }

    [Fact] public async Task Does_not_follow_redirects_and_sends_no_credentials_or_cookies_to_a_permitted_test_server()
    {
        var port = FreePort(); using var tcp = new TcpListener(IPAddress.Loopback, port); tcp.Start();
        string request = "";
        _ = Task.Run(async () =>
        {
            using var c = await tcp.AcceptTcpClientAsync(); var s = c.GetStream(); var buf = new byte[4096]; var sb = new StringBuilder();
            while (!sb.ToString().Contains("\r\n\r\n")) { var n = await s.ReadAsync(buf); if (n == 0) break; sb.Append(Encoding.ASCII.GetString(buf, 0, n)); }
            request = sb.ToString();
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        });
        using var factory = Factory(new FakeDns(new() { ["test.example.com"] = ["127.0.0.1"] }), new AllowLoopback());   // test-only policy
        using var res = await factory.Create().GetAsync($"http://test.example.com:{port}/");
        Assert.Equal(HttpStatusCode.Found, res.StatusCode);   // the 302 is returned to the caller, not followed
        Assert.DoesNotContain("Authorization", request, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("Cookie", request, StringComparison.OrdinalIgnoreCase);
    }

    private static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }
}

public class WebsiteExtractorTests
{
    private static readonly Uri Page = new("https://alnoor.example.com/");

    [Fact] public void Extracts_public_business_facts_from_json_ld_and_links()
    {
        const string html = """
            <html><head><title> Al Noor   School | Home </title>
            <script type="application/ld+json">{"@context":"https://schema.org","@type":"School","name":"Al Noor School","telephone":"+92 21 3456 7890","email":"mailto:office@alnoor.example.com",
              "address":{"@type":"PostalAddress","streetAddress":"12 Main Rd","addressLocality":"Karachi","addressCountry":"PK"}}</script></head>
            <body><a href="tel:+92-300-1234567">Call</a><a href="mailto:Admissions@AlNoor.example.com?subject=hi">Mail</a>
            <a href="https://www.facebook.com/alnoorschool">fb</a><a href="https://facebook.com/sharer/sharer.php?u=x">share</a><a href="https://instagram.com/alnoor/">ig</a>
            <a href="/contact-us">Contact</a><p>Write to info@alnoor.example.com or logo@2x.png</p></body></html>
            """;
        var x = WebsiteExtractor.Extract(html, Page);
        Assert.Equal("Al Noor School | Home", x.Title); Assert.Equal("Al Noor School", x.BusinessName); Assert.Equal("12 Main Rd, Karachi, PK", x.Address);
        Assert.Contains("+92 21 3456 7890", x.Phones); Assert.Contains("+92-300-1234567", x.Phones);
        Assert.Contains("office@alnoor.example.com", x.Emails); Assert.Contains("admissions@alnoor.example.com", x.Emails); Assert.Contains("info@alnoor.example.com", x.Emails); Assert.DoesNotContain(x.Emails, e => e.EndsWith(".png"));
        Assert.Equal(["https://www.facebook.com/alnoorschool", "https://instagram.com/alnoor"], x.SocialLinks);   // the share button is not a profile
        Assert.Equal("https://alnoor.example.com/contact-us", x.ContactPageUrl);
    }

    [Fact] public void A_bare_page_yields_nothing_beyond_what_is_literally_there_and_never_invents_facts()
    {
        var x = WebsiteExtractor.Extract("<html><head><title>Welcome</title></head><body><p>We teach 1,200 students using Moodle. Principal: Dr. Ahmed Khan. Call 021-3456789.</p></body></html>", Page);
        Assert.Equal("Welcome", x.Title); Assert.Null(x.BusinessName); Assert.Null(x.Address); Assert.Empty(x.Phones); Assert.Empty(x.Emails); Assert.Empty(x.SocialLinks); Assert.Null(x.ContactPageUrl);
        // student strength, software and decision-maker names appear in the text but the extractor has no field for them at all
        Assert.DoesNotContain(typeof(ExtractedPage).GetProperties(), p => new[] { "Student", "Software", "Principal", "Owner", "Decision" }.Any(k => p.Name.Contains(k)));
    }

    [Fact] public void A_login_page_is_not_read() { var x = WebsiteExtractor.Extract("<html><title>Portal</title><body><a href='mailto:a@b.example'>x</a><form><input type='password' name='p'></form></body></html>", Page); Assert.True(x.LooksProtected); Assert.Empty(x.Emails); Assert.Null(x.Title); }
    [Fact] public void A_noindex_page_is_not_read() { var x = WebsiteExtractor.Extract("<html><head><meta name='robots' content='noindex, nofollow'><title>T</title></head><body>a@b.example</body></html>", Page); Assert.True(x.NoIndex); Assert.Empty(x.Emails); }
    [Fact] public void Malformed_json_ld_and_junk_phone_numbers_are_ignored()
    {
        var x = WebsiteExtractor.Extract("<script type='application/ld+json'>{broken</script><a href='tel:12'>x</a><a href='tel:javascript:alert(1)'>y</a><a href='tel:+1 202 555 0100'>ok</a>", Page);
        Assert.Equal(["+1 202 555 0100"], x.Phones);
    }
    [Fact] public void Social_links_are_only_kept_for_known_networks() => Assert.Equal(["https://www.linkedin.com/company/alnoor"], WebsiteExtractor.Extract("<a href='https://www.linkedin.com/company/alnoor'>l</a><a href='https://evil.example/facebook.com/x'>e</a><a href='https://twitter.com/'>root</a>", Page).SocialLinks);
}

public class WebsiteEnricherTests
{
    private static WebsiteEnricher Enricher(ScriptedSender sender) =>
        new(new SafeWebFetcher(new UrlGuard(new FakeDns(new() { ["school.example.com"] = ["93.184.216.34"] }), new PublicOnlyAddressPolicy()), sender, Options.Create(new DiscoveryOptions())), Options.Create(new DiscoveryOptions()), NullLogger<WebsiteEnricher>.Instance);

    private static HttpResponseMessage Resp(string body, string type = "text/html", HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body, Encoding.UTF8, type) };

    [Fact] public async Task Reads_robots_first_then_the_home_page_and_one_contact_page()
    {
        var s = new ScriptedSender(r => r.RequestUri!.AbsolutePath switch
        {
            "/robots.txt" => Resp("User-agent: *\nDisallow: /private/", "text/plain"),
            "/" => Resp("<title>Al Noor</title><a href='/contact'>Contact</a><a href='mailto:a@alnoor.example'>m</a>"),
            "/contact" => Resp("<a href='tel:+92 21 111 2222'>t</a>"),
            _ => Resp("", code: HttpStatusCode.NotFound),
        });
        var res = await Enricher(s).EnrichAsync("https://school.example.com/", default);
        Assert.Equal(WebsiteFetchOutcome.Fetched, res.Outcome); Assert.Equal("/robots.txt", s.Requested[0].AbsolutePath);
        Assert.Contains("a@alnoor.example", res.Info!.Emails); Assert.Contains("+92 21 111 2222", res.Info.Phones); Assert.Equal(2, res.Info.PagesRead.Count);
    }

    [Fact] public async Task A_disallowed_page_is_never_requested()
    {
        var s = new ScriptedSender(r => r.RequestUri!.AbsolutePath == "/robots.txt" ? Resp("User-agent: *\nDisallow: /", "text/plain") : Resp("<title>x</title>"));
        var res = await Enricher(s).EnrichAsync("https://school.example.com/about", default);
        Assert.Equal(WebsiteFetchOutcome.BlockedByRobots, res.Outcome); Assert.Single(s.Requested);
    }

    [Fact] public async Task Our_own_bot_name_in_robots_is_honoured()
    {
        var s = new ScriptedSender(r => r.RequestUri!.AbsolutePath == "/robots.txt" ? Resp("User-agent: LeadPilotAI-Bot\nDisallow: /", "text/plain") : Resp("<title>x</title>"));
        Assert.Equal(WebsiteFetchOutcome.BlockedByRobots, (await Enricher(s).EnrichAsync("https://school.example.com/", default)).Outcome);
    }

    [Fact] public async Task A_missing_robots_file_means_no_restrictions_but_a_server_error_means_do_not_crawl()
    {
        var ok = new ScriptedSender(r => r.RequestUri!.AbsolutePath == "/robots.txt" ? Resp("", code: HttpStatusCode.NotFound) : Resp("<title>Al Noor</title>"));
        Assert.Equal(WebsiteFetchOutcome.Fetched, (await Enricher(ok).EnrichAsync("https://school.example.com/", default)).Outcome);
        var bad = new ScriptedSender(r => r.RequestUri!.AbsolutePath == "/robots.txt" ? Resp("", code: HttpStatusCode.InternalServerError) : Resp("<title>Al Noor</title>"));
        var res = await Enricher(bad).EnrichAsync("https://school.example.com/", default);
        Assert.Equal(WebsiteFetchOutcome.BlockedByRobots, res.Outcome); Assert.DoesNotContain(bad.Requested, u => u.AbsolutePath == "/");
    }

    [Fact] public async Task Login_pages_and_internal_addresses_yield_no_data()
    {
        var login = new ScriptedSender(r => r.RequestUri!.AbsolutePath == "/robots.txt" ? Resp("", code: HttpStatusCode.NotFound) : Resp("<input type='password'>"));
        Assert.Equal(WebsiteFetchOutcome.Protected, (await Enricher(login).EnrichAsync("https://school.example.com/", default)).Outcome);
        var internalSite = new ScriptedSender(r => Resp("x"));
        var res = await Enricher(internalSite).EnrichAsync("http://169.254.169.254/", default);
        Assert.Equal(WebsiteFetchOutcome.BlockedByPolicy, res.Outcome); Assert.Empty(internalSite.Requested); Assert.Null(res.Info);
    }
}
