using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using LeadPilot.Application.Ai;
using LeadPilot.Infrastructure.Ai.Extraction;
using LeadPilot.Infrastructure.Ai.Providers;
using Microsoft.Extensions.Options;

namespace LeadPilot.UnitTests;

/// <summary>TEST-ONLY clock for the breaker.</summary>
internal sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => TimeProvider.System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
}

public class AiCircuitBreakerTests
{
    private readonly ManualTime _time = new();

    [Fact]
    public void It_opens_after_the_threshold_refuses_calls_then_probes_and_closes_on_success()
    {
        var b = new AiCircuitBreaker(_time); var open = TimeSpan.FromSeconds(30);
        b.EnsureClosed("k", "Claude");
        for (var i = 0; i < 3; i++) b.TransientFailure("k", 3, open);
        var ex = Assert.Throws<AiProviderException>(() => b.EnsureClosed("k", "Claude")); Assert.Equal(AiFailureKind.CircuitOpen, ex.Kind); Assert.True(ex.Retryable);
        _time.Now += TimeSpan.FromSeconds(31);
        b.EnsureClosed("k", "Claude");                                           // the single probe
        Assert.Throws<AiProviderException>(() => b.EnsureClosed("k", "Claude")); // a second concurrent call is refused while probing
        b.Success("k");
        b.EnsureClosed("k", "Claude");
    }

    [Fact]
    public void A_failed_probe_reopens_it()
    {
        var b = new AiCircuitBreaker(_time); var open = TimeSpan.FromSeconds(30);
        b.TransientFailure("k", 1, open); _time.Now += TimeSpan.FromSeconds(31); b.EnsureClosed("k", "x"); b.TransientFailure("k", 1, open);
        Assert.Throws<AiProviderException>(() => b.EnsureClosed("k", "x"));
    }

    [Fact]
    public void Success_resets_the_failure_count_and_keys_are_independent()
    {
        var b = new AiCircuitBreaker(_time); var open = TimeSpan.FromSeconds(30);
        b.TransientFailure("a", 3, open); b.TransientFailure("a", 3, open); b.Success("a"); b.TransientFailure("a", 3, open); b.EnsureClosed("a", "x");
        for (var i = 0; i < 3; i++) b.TransientFailure("tenant-1", 3, open);
        Assert.Throws<AiProviderException>(() => b.EnsureClosed("tenant-1", "x")); b.EnsureClosed("tenant-2", "x");   // one tenant's outage never blocks another
    }
}

/// <summary>Runs the retry / timeout / breaker pipeline against a LOCAL HTTP server that scripts status codes (no provider involved).</summary>
public sealed class AiHttpPipelineTests : IDisposable
{
    private readonly HttpListener _listener = new(); private readonly string _url; private int _n; private readonly List<int> _seen = [];
    private Func<int, (int Status, string Body, string? RetryAfter, int DelayMs)> _script = _ => (200, "{}", null, 0);

    public AiHttpPipelineTests()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        _url = $"http://127.0.0.1:{port}/"; _listener.Prefixes.Add(_url); _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx; try { ctx = await _listener.GetContextAsync(); } catch { return; }
                var n = Interlocked.Increment(ref _n); lock (_seen) _seen.Add(n);
                var (status, body, retry, delay) = _script(n);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (delay > 0) await Task.Delay(delay);
                        ctx.Response.StatusCode = status; if (retry is not null) ctx.Response.Headers["Retry-After"] = retry;
                        var bytes = Encoding.UTF8.GetBytes(body); await ctx.Response.OutputStream.WriteAsync(bytes); ctx.Response.Close();
                    }
                    catch { /* client gave up */ }
                });
            }
        });
    }

    public void Dispose() => _listener.Close();

    private AiHttpPipeline Pipeline(AiCircuitBreaker? breaker = null) => new(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, breaker ?? new AiCircuitBreaker(TimeProvider.System), TimeProvider.System);

    private Task<HttpResponseMessage> Send(AiHttpPipeline p, string key = "k", int retries = 2, int timeoutMs = 2000, int threshold = 5) =>
        p.SendAsync(key, "Claude", () => new HttpRequestMessage(HttpMethod.Post, _url) { Content = new StringContent("{}") }, retries, 5, TimeSpan.FromMilliseconds(timeoutMs), threshold, TimeSpan.FromSeconds(30), default);

    [Fact]
    public async Task A_rate_limit_is_retried_honouring_retry_after_and_then_succeeds()
    {
        _script = n => n == 1 ? (429, "{}", "0", 0) : (200, "ok", null, 0);
        using var r = await Send(Pipeline());
        Assert.Equal("ok", await r.Content.ReadAsStringAsync()); Assert.Equal(2, _n);
    }

    [Fact]
    public async Task Server_errors_are_retried_a_bounded_number_of_times_then_fail_as_retryable()
    {
        _script = _ => (503, "{}", null, 0);
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Send(Pipeline(), retries: 2));
        Assert.Equal(AiFailureKind.Unavailable, ex.Kind); Assert.True(ex.Retryable); Assert.Equal(3, _n);
    }

    [Theory] [InlineData(401, AiFailureKind.Auth)] [InlineData(403, AiFailureKind.Auth)] [InlineData(400, AiFailureKind.InvalidRequest)] [InlineData(404, AiFailureKind.InvalidRequest)]
    public async Task Client_errors_are_never_retried(int status, AiFailureKind kind)
    {
        _script = _ => (status, "{}", null, 0);
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Send(Pipeline())); Assert.Equal(kind, ex.Kind); Assert.False(ex.Retryable); Assert.Equal(1, _n);
    }

    [Fact]
    public async Task A_slow_provider_times_out_without_hanging()
    {
        _script = _ => (200, "late", null, 1500);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Send(Pipeline(), retries: 0, timeoutMs: 150));
        Assert.Equal(AiFailureKind.Timeout, ex.Kind); Assert.True(ex.Retryable); Assert.True(sw.ElapsedMilliseconds < 1200);
    }

    [Fact]
    public async Task Overload_529_is_classified_and_retried()
    {
        _script = n => n < 3 ? (529, "{}", null, 0) : (200, "ok", null, 0);
        using var r = await Send(Pipeline(), retries: 3); Assert.Equal(3, _n);
    }

    [Fact]
    public async Task After_repeated_failures_the_breaker_refuses_further_calls_without_touching_the_server()
    {
        var breaker = new AiCircuitBreaker(TimeProvider.System); var p = Pipeline(breaker);
        _script = _ => (503, "{}", null, 0);
        await Assert.ThrowsAsync<AiProviderException>(() => Send(p, "t1", retries: 1, threshold: 2));
        var before = _n;
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => Send(p, "t1", retries: 1, threshold: 2));
        Assert.Equal(AiFailureKind.CircuitOpen, ex.Kind); Assert.Equal(before, _n);
        // another tenant's key is unaffected
        _script = _ => (200, "fine", null, 0);
        using var ok = await Send(p, "t2", retries: 0); Assert.Equal(200, (int)ok.StatusCode);
    }

    [Fact]
    public async Task A_connection_failure_is_retryable_and_message_free_of_details()
    {
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        var p = new AiHttpPipeline(new HttpClient(), new AiCircuitBreaker(TimeProvider.System), TimeProvider.System);
        var ex = await Assert.ThrowsAsync<AiProviderException>(() => p.SendAsync("x", "Voyage AI", () => new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/secret-path?key=SUPERSECRET"), 0, 5, TimeSpan.FromSeconds(2), 9, TimeSpan.FromSeconds(1), default));
        Assert.Equal(AiFailureKind.Unavailable, ex.Kind); Assert.DoesNotContain("SUPERSECRET", ex.Message); Assert.DoesNotContain("secret-path", ex.Message);
    }
}

public class DocumentTextExtractorTests
{
    private static DocumentTextExtractor Extractor(Action<IngestionOptions>? tweak = null) { var o = new AiOptions(); tweak?.Invoke(o.Ingestion); return new DocumentTextExtractor(Options.Create(o)); }
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Text_and_markdown_are_read_and_cleaned()
    {
        var r = Extractor().Extract("faq.md", Utf8("# Pricing\r\n\r\nStandard:   500 USD​\u0007\r\n\r\n\r\n\r\nPremium: 900 USD"));
        Assert.Equal("text/markdown", r.MimeType); Assert.Equal("# Pricing\n\nStandard: 500 USD\n\nPremium: 900 USD", r.Text);
    }

    [Fact] public void A_utf8_bom_and_utf16_are_decoded() { Assert.Equal("héllo", Extractor().Extract("a.txt", [0xEF, 0xBB, 0xBF, .. Utf8("héllo")]).Text); Assert.Equal("hello", Extractor().Extract("a.txt", [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("hello")]).Text); }

    [Fact]
    public void Html_drops_scripts_styles_forms_and_navigation_but_keeps_headings_and_lists()
    {
        var html = "<html><head><title>School ERP</title><style>.x{}</style><script>alert('xss')</script></head><body><nav>Menu Home</nav><main><h1>Plans</h1><p>Standard costs <b>500</b> USD.</p><ul><li>Attendance</li><li>Fees</li></ul><form><input value='secret'></form><script>steal()</script></main><footer>© 2026</footer></body></html>";
        var r = Extractor().Extract("page.html", Utf8(html));
        Assert.Contains("# Plans", r.Text); Assert.Contains("Standard costs 500 USD.", r.Text); Assert.Contains("- Attendance", r.Text);
        Assert.DoesNotContain("alert", r.Text); Assert.DoesNotContain("steal", r.Text); Assert.DoesNotContain("Menu", r.Text); Assert.DoesNotContain("secret", r.Text); Assert.DoesNotContain("2026", r.Text);
        Assert.Equal("School ERP", r.Title);
    }

    private static byte[] Docx(Action<Body> build)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart(); main.Document = new Document(new Body()); build(main.Document.Body!); main.Document.Save();
        }
        return ms.ToArray();
    }

    [Fact]
    public void A_docx_is_read_with_headings_and_paragraphs()
    {
        var bytes = Docx(b =>
        {
            b.Append(new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }), new Run(new Text("Pricing"))));
            b.Append(new Paragraph(new Run(new Text("Standard plan: 500 USD"))));
            b.Append(new Paragraph(new Run(new Text("Premium plan: 900 USD"))));
        });
        var r = Extractor().Extract("plans.docx", bytes);
        Assert.StartsWith("# Pricing", r.Text); Assert.Contains("Standard plan: 500 USD", r.Text); Assert.Contains("Premium plan: 900 USD", r.Text);
    }

    [Fact]
    public void A_docx_with_an_external_entity_is_refused_not_resolved()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var e = zip.CreateEntry("word/document.xml");
            using var w = new StreamWriter(e.Open());
            w.Write("<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>&x;</w:t></w:r></w:p></w:body></w:document>");
        }
        var ex = Assert.Throws<ExtractionException>(() => Extractor().Extract("evil.docx", ms.ToArray()));
        Assert.DoesNotContain("root:", ex.Message);
    }

    [Fact]
    public void A_zip_bomb_docx_is_refused_by_the_uncompressed_size_limit()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var e = zip.CreateEntry("word/document.xml"); using var s = e.Open(); var chunk = new byte[1024 * 1024];
            for (var i = 0; i < 6; i++) s.Write(chunk);
        }
        Assert.True(ms.Length < 100_000);
        var ex = Assert.Throws<ExtractionException>(() => Extractor(o => o.MaxDocxUncompressedBytes = 5 * 1024 * 1024).Extract("bomb.docx", ms.ToArray()));
        Assert.Contains("expands", ex.Message);
    }

    [Fact]
    public void A_pdf_is_read_page_by_page_and_the_page_limit_is_enforced()
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var font = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        for (var i = 1; i <= 3; i++) builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText($"Page {i}: the Standard plan costs 500 USD", 12, new UglyToad.PdfPig.Core.PdfPoint(25, 700), font);
        var bytes = builder.Build();
        var r = Extractor().Extract("brochure.pdf", bytes);
        Assert.Contains("Page 1", r.Text); Assert.Contains("Page 3", r.Text); Assert.Contains("500 USD", r.Text);
        Assert.Contains("pages", Assert.Throws<ExtractionException>(() => Extractor(o => o.MaxPdfPages = 2).Extract("brochure.pdf", bytes)).Message);
    }

    [Fact]
    public void A_file_whose_content_does_not_match_its_extension_is_refused()
    {
        Assert.Throws<ExtractionException>(() => Extractor().Extract("fake.pdf", Utf8("just text")));
        Assert.Throws<ExtractionException>(() => Extractor().Extract("fake.docx", Utf8("just text")));
        Assert.Throws<ExtractionException>(() => Extractor().Extract("renamed.txt", Utf8("%PDF-1.4 binary")));
        Assert.Throws<ExtractionException>(() => Extractor().Extract("renamed.md", [0x50, 0x4B, 0x03, 0x04, 1, 2, 3]));
    }

    [Theory] [InlineData("malware.exe")] [InlineData("sheet.xlsx")] [InlineData("archive.zip")] [InlineData("noextension")] [InlineData("image.png")]
    public void Unsupported_types_are_refused(string name) => Assert.Throws<ExtractionException>(() => Extractor().Extract(name, Utf8("hello")));

    [Fact]
    public void Empty_oversized_and_binary_files_are_refused()
    {
        Assert.Throws<ExtractionException>(() => Extractor().Extract("a.txt", []));
        Assert.Throws<ExtractionException>(() => Extractor(o => o.MaxUploadBytes = 100).Extract("a.txt", new byte[101]));
        Assert.Throws<ExtractionException>(() => Extractor().Extract("a.txt", Enumerable.Range(0, 2000).Select(i => (byte)(i % 7)).ToArray()));
        Assert.Throws<ExtractionException>(() => Extractor().Extract("a.txt", Utf8(" \n​ \n ")));
    }

    [Fact] public void Corrupt_pdfs_fail_with_a_safe_message_not_a_stack_trace() { var ex = Assert.Throws<ExtractionException>(() => Extractor().Extract("bad.pdf", Utf8("%PDF-1.7\nthis is not a pdf"))); Assert.DoesNotContain("   at ", ex.Message); }
}
