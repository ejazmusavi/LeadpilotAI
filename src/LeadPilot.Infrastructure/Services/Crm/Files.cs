using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Tenancy;
using LeadPilot.Domain.Crm;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Services.Crm;

/// <summary>Stores tenant files on local disk under <c>{root}/tenants/{tenantId}/{area}/{guid}.{ext}</c>. Keys are tenant-relative; opening checks the key belongs to the active tenant.</summary>
public sealed class LocalTenantFileStore(ITenantContext tenant, IOptions<CrmOptions> options, IHostEnvironment env) : ITenantFileStore
{
    private string Root => Path.GetFullPath(Path.IsPathRooted(options.Value.StorageRoot) ? options.Value.StorageRoot : Path.Combine(env.ContentRootPath, options.Value.StorageRoot));

    public async Task<string> SaveAsync(string area, string fileName, Stream content, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext.Length > 8 || ext.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) ext = ".bin";
        var key = TenantPaths.For(tenant, $"{area}/{Guid.NewGuid():N}{ext}");
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(fs, ct);
        return key;
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) => Task.FromResult<Stream>(new FileStream(Resolve(key), FileMode.Open, FileAccess.Read, FileShare.Read));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = Resolve(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string Resolve(string key)
    {
        var tenantId = tenant.RequireTenantId();
        // Only keys inside the ACTIVE tenant's folder are reachable, whatever a stored key says.
        var prefix = $"tenants/{tenantId:N}/";
        if (!key.StartsWith(prefix, StringComparison.Ordinal)) throw new CrossTenantViolationException("The file does not belong to the active tenant.");
        var full = Path.GetFullPath(Path.Combine(Root, key));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new CrossTenantViolationException("Invalid file key.");
        return full;
    }
}

/// <summary>Reads CSV and XLSX. XLSX formulas are never evaluated (the cached value is read as text) and archives are checked for zip-bomb size before parsing.</summary>
public sealed class TabularFileReader(IOptions<CrmOptions> options) : ITabularFileReader
{
    public IEnumerable<string[]> ReadRows(Stream content, ImportFileType type) => type == ImportFileType.Csv ? CsvReader.Read(content) : ReadXlsx(content);

    public static bool LooksLikeZip(ReadOnlySpan<byte> head) => head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04;

    private IEnumerable<string[]> ReadXlsx(Stream content)
    {
        if (!content.CanSeek) throw new SpreadsheetFormatException("Unsupported stream.");
        content.Position = 0;
        try
        {
            using (var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (zip.Entries.Count > 500) throw new SpreadsheetFormatException("The workbook contains too many parts.");
                long total = 0;
                foreach (var e in zip.Entries) { total += e.Length; if (total > options.Value.MaxXlsxUncompressedBytes) throw new SpreadsheetFormatException("The workbook expands to an unreasonable size and was rejected."); }
            }
        }
        catch (InvalidDataException) { throw new SpreadsheetFormatException("The file is not a valid .xlsx workbook."); }

        content.Position = 0;
        var rows = new List<string[]>();
        try
        {
            using var doc = SpreadsheetDocument.Open(content, false);
            var wb = doc.WorkbookPart ?? throw new SpreadsheetFormatException("The workbook has no sheets.");
            var first = wb.Workbook!.Descendants<Sheet>().FirstOrDefault() ?? throw new SpreadsheetFormatException("The workbook has no sheets.");
            var sheet = (WorksheetPart)wb.GetPartById(first.Id!.Value!);
            var shared = wb.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(i => i.InnerText).ToArray() ?? [];
            using var reader = OpenXmlReader.Create(sheet);
            while (reader.Read())
            {
                if (reader.ElementType != typeof(Row) || !reader.IsStartElement) continue;
                var row = (Row)reader.LoadCurrentElement()!;
                var cells = new List<string>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var col = ColumnIndex(cell.CellReference?.Value, cells.Count);
                    if (col >= CsvReader.MaxColumns) throw new SpreadsheetFormatException($"The file has more than {CsvReader.MaxColumns} columns.");
                    while (cells.Count < col) cells.Add(string.Empty);
                    var text = CellText(cell, shared);
                    if (text.Length > CsvReader.MaxCellLength) throw new SpreadsheetFormatException($"A cell is longer than {CsvReader.MaxCellLength} characters.");
                    if (cells.Count == col) cells.Add(text); else cells[col] = text;
                }
                if (cells.Any(c => c.Length > 0)) rows.Add(cells.ToArray());
            }
        }
        catch (SpreadsheetFormatException) { throw; }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or System.Xml.XmlException or FileFormatException or IOException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            throw new SpreadsheetFormatException("The file is not a valid .xlsx workbook.");
        }
        return rows;
    }

    private static string CellText(Cell cell, string[] shared)
    {
        var raw = cell.CellValue?.Text ?? string.Empty;
        var type = cell.DataType?.Value;
        if (type == CellValues.SharedString) return int.TryParse(raw, out var i) && i >= 0 && i < shared.Length ? shared[i].Trim() : string.Empty;
        if (type == CellValues.InlineString) return (cell.InlineString?.InnerText ?? string.Empty).Trim();
        if (type == CellValues.Boolean) return raw == "1" ? "TRUE" : "FALSE";
        if (type == CellValues.Error) return string.Empty;
        return raw.Trim(); // numbers are returned exactly as stored (phone numbers keep all their digits)
    }

    private static int ColumnIndex(string? reference, int fallback)
    {
        if (string.IsNullOrEmpty(reference)) return fallback;
        var n = 0;
        foreach (var ch in reference)
        {
            if (!char.IsAsciiLetter(ch)) break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return n == 0 ? fallback : n - 1;
    }
}

/// <summary>Writes CSV (UTF-8 with BOM) and XLSX with every cell passed through <see cref="SpreadsheetSafe"/>.</summary>
public static class SpreadsheetWriter
{
    public const string CsvType = "text/csv; charset=utf-8";
    public const string XlsxType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static byte[] Csv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', headers.Select(SpreadsheetSafe.CsvCell))).Append("\r\n");
        foreach (var r in rows) sb.Append(string.Join(',', r.Select(SpreadsheetSafe.CsvCell))).Append("\r\n");
        return new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    public static byte[] Xlsx(string sheetName, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(sheetName);
        for (var c = 0; c < headers.Count; c++) { var cell = ws.Cell(1, c + 1); cell.Value = SpreadsheetSafe.Escape(headers[c]); cell.Style.Font.Bold = true; }
        var r = 2;
        foreach (var row in rows)
        {
            for (var c = 0; c < row.Count; c++) ws.Cell(r, c + 1).Value = SpreadsheetSafe.Escape(row[c]);
            r++;
        }
        ws.SheetView.FreezeRows(1);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
