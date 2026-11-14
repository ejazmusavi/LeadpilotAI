using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using HtmlAgilityPack;
using LeadPilot.Application.Ai;
using LeadPilot.Domain.Ai;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Ai.Extraction;

/// <summary>A failure with a message that is safe to show the person who uploaded the file (never a stack trace, never file content).</summary>
public sealed class ExtractionException(string message) : Exception(message);

public sealed record ExtractedText(string Text, string MimeType, string? Title = null);

/// <summary>
/// Turns an uploaded file into plain text. Supported: text/markdown, HTML, PDF and DOCX. The file type is decided by BOTH the extension and the file's leading bytes (a ".pdf" that is not a PDF is refused). Safety:
/// a byte-size limit, a page limit for PDFs, DOCX opened as a zip with entry-count and uncompressed-size limits (zip bombs) and only <c>word/document.xml</c> read, XML parsed with DTDs and external resolution off,
/// HTML stripped of scripts/styles/forms (never executed or rendered), the result clipped, control characters removed. Nothing is written to disk.
/// </summary>
public sealed partial class DocumentTextExtractor(IOptions<AiOptions> options)
{
    private static readonly string[] TextExtensions = [".txt", ".md", ".markdown", ".text"];
    private static readonly string[] HtmlExtensions = [".html", ".htm", ".xhtml"];

    public static IReadOnlyList<string> SupportedExtensions { get; } = [.. TextExtensions, .. HtmlExtensions, ".pdf", ".docx"];

    public ExtractedText Extract(string fileName, byte[] data)
    {
        var o = options.Value.Ingestion;
        if (data.Length == 0) throw new ExtractionException("The file is empty.");
        if (data.Length > o.MaxUploadBytes) throw new ExtractionException($"The file is larger than {o.MaxUploadBytes / (1024 * 1024)} MB.");
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (!SupportedExtensions.Contains(ext)) throw new ExtractionException($"Files of type '{(ext.Length == 0 ? "unknown" : ext)}' are not supported. Use {string.Join(", ", SupportedExtensions)}.");
        var isPdf = StartsWith(data, "%PDF-"); var isZip = data.Length > 3 && data[0] == 0x50 && data[1] == 0x4B && data[2] is 0x03 or 0x05;
        if (ext == ".pdf" != isPdf && (ext == ".pdf" || isPdf)) throw new ExtractionException("The file content does not match its extension.");
        if (ext == ".docx" != isZip && (ext == ".docx" || isZip)) throw new ExtractionException("The file content does not match its extension.");
        if (ext is ".txt" or ".md" or ".markdown" or ".text" or ".html" or ".htm" or ".xhtml" && (isPdf || isZip || LooksBinary(data))) throw new ExtractionException("The file does not look like a text file.");

        ExtractedText result;
        try
        {
            result = ext switch
            {
                ".pdf" => new ExtractedText(ExtractPdf(data, o), "application/pdf"),
                ".docx" => new ExtractedText(ExtractDocx(data, o), "application/vnd.openxmlformats-officedocument.wordprocessingml.document"),
                ".html" or ".htm" or ".xhtml" => FromHtml(Decode(data)),
                _ => new ExtractedText(Decode(data), ext is ".md" or ".markdown" ? "text/markdown" : "text/plain"),
            };
        }
        catch (ExtractionException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException) { throw new ExtractionException("The file could not be read. It may be damaged, encrypted or in an unsupported format."); }

        var text = Clean(result.Text);
        if (text.Length == 0) throw new ExtractionException("The file contains no readable text (a scanned PDF needs OCR, which is not supported).");
        if (text.Length > DocumentVersion.MaxContentChars) throw new ExtractionException($"The text is longer than {DocumentVersion.MaxContentChars:N0} characters.");
        return result with { Text = text };
    }

    public static ExtractedText FromHtml(string html)
    {
        var doc = new HtmlDocument { OptionFixNestedTags = true };
        doc.LoadHtml(html);
        RemoveAll(doc.DocumentNode, "//script|//style|//noscript|//iframe|//object|//embed|//svg|//form|//template|//head/*[not(self::title)]|//comment()");
        var title = HtmlEntity.DeEntitize(doc.DocumentNode.SelectSingleNode("//title")?.InnerText ?? string.Empty).Trim();
        var root = doc.DocumentNode.SelectSingleNode("//main") ?? doc.DocumentNode.SelectSingleNode("//article") ?? doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        RemoveAll(root, ".//nav|.//footer|.//aside");
        var sb = new StringBuilder();
        Walk(root, sb);
        return new ExtractedText(sb.ToString(), "text/html", string.IsNullOrWhiteSpace(title) ? null : title);
    }

    private static void RemoveAll(HtmlNode root, string xpath)
    {
        var nodes = root.SelectNodes(xpath);
        if (nodes is null) return;
        foreach (var n in nodes.ToList()) n.Remove();
    }

    private static void Walk(HtmlNode node, StringBuilder sb)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child.NodeType)
            {
                case HtmlNodeType.Text: sb.Append(HtmlEntity.DeEntitize(child.InnerText)); break;
                case HtmlNodeType.Element:
                    var name = child.Name.ToLowerInvariant();
                    if (name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6') { sb.Append("\n\n").Append('#', Math.Min(3, name[1] - '0')).Append(' '); Walk(child, sb); sb.Append("\n\n"); }
                    else if (name == "li") { sb.Append("\n- "); Walk(child, sb); }
                    else if (name is "p" or "div" or "section" or "ul" or "ol" or "table" or "tr" or "blockquote" or "pre" or "br" or "dl" or "dt" or "dd") { sb.Append('\n'); Walk(child, sb); sb.Append('\n'); }
                    else if (name is "td" or "th") { Walk(child, sb); sb.Append(" | "); }
                    else Walk(child, sb);
                    break;
            }
        }
    }

    private static string ExtractPdf(byte[] data, IngestionOptions o)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(data, new UglyToad.PdfPig.ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true });
        if (pdf.NumberOfPages > o.MaxPdfPages) throw new ExtractionException($"The PDF has {pdf.NumberOfPages} pages; the limit is {o.MaxPdfPages}.");
        var sb = new StringBuilder();
        foreach (var page in pdf.GetPages())
        {
            sb.Append(UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page)).Append("\n\n");
            if (sb.Length > DocumentVersion.MaxContentChars) throw new ExtractionException($"The text is longer than {DocumentVersion.MaxContentChars:N0} characters.");
        }
        return sb.ToString();
    }

    private static string ExtractDocx(byte[] data, IngestionOptions o)
    {
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        if (zip.Entries.Count > o.MaxDocxEntries) throw new ExtractionException("The document has too many internal parts.");
        long total = 0; foreach (var e in zip.Entries) { total += e.Length; if (total > o.MaxDocxUncompressedBytes) throw new ExtractionException("The document expands to more than the allowed size."); }
        var entry = zip.GetEntry("word/document.xml") ?? throw new ExtractionException("This is not a Word (.docx) document.");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0, MaxCharactersInDocument = o.MaxDocxUncompressedBytes, IgnoreComments = true });
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var sb = new StringBuilder(); var heading = 0; var inParagraph = false; var paragraphStart = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == w)
            {
                switch (reader.LocalName)
                {
                    case "p": inParagraph = true; heading = 0; paragraphStart = sb.Length; break;
                    case "pStyle": { var v = reader.GetAttribute("val", w) ?? string.Empty; if (v.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(v.AsSpan(7), out var lvl)) heading = Math.Clamp(lvl, 1, 3); else if (v.Equals("Title", StringComparison.OrdinalIgnoreCase)) heading = 1; break; }
                    case "t": if (!reader.IsEmptyElement) sb.Append(reader.ReadElementContentAsString()); break;
                    case "tab": sb.Append(' '); break;
                    case "br": sb.Append('\n'); break;
                    case "tc": break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.NamespaceURI == w)
            {
                if (reader.LocalName == "p" && inParagraph)
                {
                    if (heading > 0) sb.Insert(paragraphStart, new string('#', heading) + " ");
                    sb.Append("\n\n"); inParagraph = false;
                }
                else if (reader.LocalName == "tc") sb.Append(" | ");
            }
            if (sb.Length > DocumentVersion.MaxContentChars) throw new ExtractionException($"The text is longer than {DocumentVersion.MaxContentChars:N0} characters.");
        }
        return sb.ToString();
    }

    private static string Decode(byte[] data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        return new UTF8Encoding(false, false).GetString(data);
    }

    private static bool StartsWith(byte[] data, string ascii) => data.Length >= ascii.Length && Encoding.ASCII.GetString(data, 0, ascii.Length) == ascii;

    private static bool LooksBinary(byte[] data)
    {
        var sample = Math.Min(data.Length, 4096); var bad = 0;
        for (var i = 0; i < sample; i++) if (data[i] == 0 || (data[i] < 0x09) || (data[i] is > 0x0D and < 0x20 && data[i] != 0x1B)) bad++;
        return bad > sample / 50 && !(data.Length >= 2 && ((data[0] == 0xFF && data[1] == 0xFE) || (data[0] == 0xFE && data[1] == 0xFF)));
    }

    [GeneratedRegex(@"[ \t]+")] private static partial Regex Spaces();
    [GeneratedRegex(@"\n[ \t]+")] private static partial Regex LeadingSpace();
    [GeneratedRegex(@"\n{3,}")] private static partial Regex Newlines();

    public static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (ch == '\n' || ch == '\t') sb.Append(ch);
            else if (ch < 0x20 || (ch >= 0x7F && ch <= 0x9F) || ch is '​' or '‌' or '‍' or '⁠' or '﻿' or '‮' or '‭' or '‪' or '‫' or '‬') continue;
            else sb.Append(ch);
        }
        var s = Spaces().Replace(sb.ToString(), " ");
        s = LeadingSpace().Replace(s, "\n");
        return Newlines().Replace(s, "\n\n").Trim();
    }
}
