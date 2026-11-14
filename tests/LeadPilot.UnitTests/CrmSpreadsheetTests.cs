using System.Text;
using ClosedXML.Excel;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Crm;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.Extensions.Options;

namespace LeadPilot.UnitTests;

public class SpreadsheetSafetyTests
{
    [Theory]
    [InlineData("=SUM(A1:A2)", "'=SUM(A1:A2)")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("+cmd|' /C calc'!A0", "'+cmd|' /C calc'!A0")]
    [InlineData("-2+3+cmd|' /C calc'!A0", "'-2+3+cmd|' /C calc'!A0")]
    [InlineData("\t=1+1", "'\t=1+1")]
    [InlineData("\r=1+1", "'\r=1+1")]
    [InlineData("  =1+1", "'  =1+1")]
    [InlineData("=HYPERLINK(\"http://evil\",\"x\")", "'=HYPERLINK(\"http://evil\",\"x\")")]
    public void Formula_triggers_are_neutralised(string value, string expected) => Assert.Equal(expected, SpreadsheetSafe.Escape(value));

    [Theory]
    [InlineData("+966501234567")]
    [InlineData("-5")]
    [InlineData("+1 (415) 555-2671")]
    [InlineData("Al Noor School")]
    [InlineData("a=b")]
    [InlineData("")]
    public void Ordinary_values_and_phone_numbers_are_left_alone(string value) => Assert.Equal(value, SpreadsheetSafe.Escape(value));

    [Fact]
    public void Csv_cells_are_quoted_and_escaped_after_neutralising()
    {
        Assert.Equal("\"Smith, John\"", SpreadsheetSafe.CsvCell("Smith, John"));
        Assert.Equal("\"say \"\"hi\"\"\"", SpreadsheetSafe.CsvCell("say \"hi\""));
        Assert.Equal("'=1+1", SpreadsheetSafe.CsvCell("=1+1"));
        Assert.Equal("\"line1\r\nline2\"", SpreadsheetSafe.CsvCell("line1\r\nline2"));
    }

    [Fact]
    public void Written_csv_has_a_bom_and_no_live_formulas()
    {
        var bytes = SpreadsheetWriter.Csv(["Name", "Note"], [["=EVIL()", "ok"], ["+cmd|x", "@x"]]);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.DoesNotContain("\n=", text);
        Assert.DoesNotContain(",=", text);
        Assert.Contains("'=EVIL()", text);
        Assert.Contains("'+cmd|x", text);
    }

    [Fact]
    public void Written_xlsx_stores_dangerous_values_as_plain_text()
    {
        var bytes = SpreadsheetWriter.Xlsx("Leads", ["Name"], [["=1+1"], ["@SUM(1)"], ["Fine"]]);
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheet(1);
        foreach (var r in new[] { 2, 3 })
        {
            Assert.False(ws.Cell(r, 1).HasFormula);                 // never a formula cell
            Assert.True(ws.Cell(r, 1).Style.IncludeQuotePrefix);    // Excel's leading-apostrophe text style: shown as text, not evaluated
            Assert.Equal(XLDataType.Text, ws.Cell(r, 1).DataType);
        }
        Assert.Equal("=1+1", ws.Cell(2, 1).GetString());
        Assert.Equal("Fine", ws.Cell(4, 1).GetString());
    }
}

public class CsvImportParsingTests
{
    private static List<string[]> Read(string text) => CsvReader.Parse(text).ToList();

    [Fact]
    public void Parses_quotes_newlines_escaped_quotes_and_empty_cells()
    {
        var rows = Read("Name,Phone,Notes\r\n\"Al Noor, Riyadh\",+966501234567,\"line1\nline2 \"\"quoted\"\"\"\r\nAcme,,\r\n");
        Assert.Equal(2 + 1, rows.Count);
        Assert.Equal(["Al Noor, Riyadh", "+966501234567", "line1\nline2 \"quoted\""], rows[1]);
        Assert.Equal(["Acme", "", ""], rows[2]);
    }

    [Theory]
    [InlineData("Name;Phone\nA;1", ';')]
    [InlineData("Name\tPhone\nA\t1", '\t')]
    [InlineData("Name,Phone\nA,1", ',')]
    [InlineData("\"Na;me\",Phone\nA,1", ',')]
    public void Detects_the_delimiter(string text, char expected) => Assert.Equal(expected, CsvReader.DetectDelimiter(text));

    [Fact]
    public void Blank_lines_and_a_missing_final_newline_are_handled()
    {
        var rows = Read("Name\n\nA\n\n\nB");
        Assert.Equal(3, rows.Count);
        Assert.Equal("B", rows[2][0]);
    }

    [Fact]
    public void Unbalanced_quotes_binary_data_wide_files_and_huge_cells_are_rejected()
    {
        Assert.Throws<SpreadsheetFormatException>(() => Read("Name\n\"unterminated"));
        Assert.Throws<SpreadsheetFormatException>(() => CsvReader.Read(new MemoryStream([0x50, 0x00, 0x01, 0x02])).ToList());
        Assert.Throws<SpreadsheetFormatException>(() => Read(string.Join(',', Enumerable.Range(0, 150).Select(i => "c" + i)) + "\n1"));
        Assert.Throws<SpreadsheetFormatException>(() => Read("Name\n" + new string('x', CsvReader.MaxCellLength + 5)));
    }

    [Fact]
    public void Utf8_bom_is_skipped_and_invalid_utf8_is_rejected()
    {
        var withBom = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("Name,City\nمدرسة,الرياض")).ToArray();
        var rows = CsvReader.Read(new MemoryStream(withBom)).ToList();
        Assert.Equal("Name", rows[0][0]);
        Assert.Equal("مدرسة", rows[1][0]);
        Assert.Throws<SpreadsheetFormatException>(() => CsvReader.Read(new MemoryStream([0x4E, 0xFF, 0xFE, 0x41, 0xC3, 0x28])).ToList());
    }
}

public class ImportMappingTests
{
    private static readonly string[] Headers = ["School Name", "Type", "City", "Website", "Mobile", "E-mail", "Contact Person", "Tags", "Stage", "Owner"];

    [Fact]
    public void Headers_are_mapped_by_common_aliases_each_field_at_most_once()
    {
        var map = ImportRowMapper.Guess(Headers).ToDictionary(m => m.ColumnIndex, m => m.Field);
        Assert.Equal(ImportField.Name, map[0]);
        Assert.Equal(ImportField.Category, map[1]);
        Assert.Equal(ImportField.City, map[2]);
        Assert.Equal(ImportField.Website, map[3]);
        Assert.Equal(ImportField.Phone, map[4]);
        Assert.Equal(ImportField.Email, map[5]);
        Assert.Equal(ImportField.ContactName, map[6]);
        Assert.Equal(ImportField.Tags, map[7]);
        Assert.Equal(ImportField.Stage, map[8]);
        Assert.Equal(ImportField.OwnerEmail, map[9]);
        var twice = ImportRowMapper.Guess(["Name", "Company"]);
        Assert.Equal(ImportField.Name, twice[0].Field);
        Assert.Null(twice[1].Field);
    }

    [Fact]
    public void Mapping_validation_requires_a_name_column_and_rejects_duplicates_and_bad_indexes()
    {
        Assert.Null(ImportRowMapper.ValidateMapping([new(0, ImportField.Name)], 3));
        Assert.NotNull(ImportRowMapper.ValidateMapping([new(0, ImportField.City)], 3));
        Assert.NotNull(ImportRowMapper.ValidateMapping([new(0, ImportField.Name), new(1, ImportField.Name)], 3));
        Assert.NotNull(ImportRowMapper.ValidateMapping([new(5, ImportField.Name)], 3));
    }

    private static MappedRow Map(params string[] cells) => ImportRowMapper.Map(2, cells, Headers, ImportRowMapper.Guess(Headers), "966");

    [Fact]
    public void A_valid_row_is_normalised()
    {
        var r = Map("Al Noor School", "Private school", "Riyadh", "https://WWW.alnoor.example/x", "050 123 4567", "INFO@AlNoor.example", "Sara", "vip; north ,VIP", "Contacted", "Boss@Example.com");
        Assert.Empty(r.Issues);
        var d = r.Data!;
        Assert.Equal("+966501234567", d.PhoneE164);
        Assert.Equal("info@alnoor.example", d.Email);
        Assert.Equal(["vip", "north"], d.Tags);
        Assert.Equal("boss@example.com", d.OwnerEmail);
        Assert.Equal("Private school", d.Category);
    }

    [Fact]
    public void Invalid_values_are_reported_per_column()
    {
        var r = Map("", "", "", "not a site", "12", "nope", "", "", "", "");
        Assert.Null(r.Data);
        Assert.Contains(r.Issues, i => i.Column == "School Name" && i.Message.Contains("required"));
        Assert.Contains(r.Issues, i => i.Column == "Website");
        Assert.Contains(r.Issues, i => i.Column == "Mobile");
        Assert.Contains(r.Issues, i => i.Column == "E-mail");
    }

    [Fact]
    public void Overlong_values_and_too_many_tags_are_errors()
    {
        Assert.Contains(Map(new string('x', 301)).Issues, i => i.Message.Contains("300"));
        Assert.NotEmpty(Map("A", "", "", "", "", "", "", string.Join(",", Enumerable.Range(0, 11).Select(i => "t" + i)), "", "").Issues);
    }

    [Fact]
    public void Row_data_round_trips_through_json_for_later_merging()
    {
        var d = Map("Acme", "", "Dubai", "", "+971 4 123 4567", "", "", "a,b", "", "").Data!;
        var back = ImportRowData.FromJson(d.ToJson())!;
        Assert.Equal(d.Name, back.Name);
        Assert.Equal(d.PhoneE164, back.PhoneE164);
        Assert.Equal(d.Tags, back.Tags);
        Assert.Null(ImportRowData.FromJson("garbage"));
    }

    [Fact]
    public void Phone_without_country_code_needs_the_tenant_default()
    {
        var withoutDefault = ImportRowMapper.Map(2, ["A", "", "", "", "0501234567"], Headers, ImportRowMapper.Guess(Headers), null);
        Assert.Contains(withoutDefault.Issues, i => i.Column == "Mobile");
    }
}

public class XlsxImportParsingTests
{
    private static TabularFileReader Reader(long maxUncompressed = 50L * 1024 * 1024) => new(Options.Create(new CrmOptions { MaxXlsxUncompressedBytes = maxUncompressed }));

    private static MemoryStream Workbook(Action<IXLWorksheet> fill)
    {
        using var wb = new XLWorkbook();
        fill(wb.Worksheets.Add("Leads"));
        var ms = new MemoryStream();
        wb.SaveAs(ms); ms.Position = 0;
        return ms;
    }

    [Fact]
    public void Reads_the_first_sheet_with_text_numbers_and_gaps()
    {
        using var ms = Workbook(ws =>
        {
            ws.Cell(1, 1).Value = "Name"; ws.Cell(1, 2).Value = "Phone"; ws.Cell(1, 4).Value = "City";
            ws.Cell(2, 1).Value = "Al Noor"; ws.Cell(2, 2).Value = 966501234567d; ws.Cell(2, 4).Value = "Riyadh";
            ws.Cell(3, 1).Value = "Acme";
        });
        var rows = Reader().ReadRows(ms, ImportFileType.Xlsx).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(["Name", "Phone", "", "City"], rows[0]);
        Assert.Equal("966501234567", rows[1][1]);   // a numeric phone keeps every digit
        Assert.Equal("Riyadh", rows[1][3]);
        Assert.Equal(["Acme"], rows[2]);
    }

    [Fact]
    public void Formulas_are_never_evaluated_their_cached_value_is_read_as_text()
    {
        using var ms = Workbook(ws =>
        {
            ws.Cell(1, 1).Value = "Name";
            ws.Cell(2, 1).FormulaA1 = "1+1";
        });
        var rows = Reader().ReadRows(ms, ImportFileType.Xlsx).ToList();
        Assert.DoesNotContain(rows.SelectMany(r => r), c => c.StartsWith('='));
    }

    [Fact]
    public void Rejects_non_workbooks_and_zip_bombs()
    {
        Assert.Throws<SpreadsheetFormatException>(() => Reader().ReadRows(new MemoryStream(Encoding.UTF8.GetBytes("Name,City\nA,B")), ImportFileType.Xlsx).ToList());
        using var ms = Workbook(ws => { ws.Cell(1, 1).Value = "Name"; ws.Cell(2, 1).Value = new string('x', 5000); });
        Assert.Throws<SpreadsheetFormatException>(() => Reader(maxUncompressed: 1000).ReadRows(ms, ImportFileType.Xlsx).ToList());
        Assert.True(TabularFileReader.LooksLikeZip([0x50, 0x4B, 0x03, 0x04]));
        Assert.False(TabularFileReader.LooksLikeZip(Encoding.UTF8.GetBytes("Name")));
    }

    [Fact]
    public void Round_trips_an_exported_workbook()
    {
        var bytes = SpreadsheetWriter.Xlsx("Leads", ["Name", "City"], [["Al Noor", "Riyadh"], ["=cmd()", "x"]]);
        var rows = Reader().ReadRows(new MemoryStream(bytes), ImportFileType.Xlsx).ToList();
        Assert.Equal("Name", rows[0][0]);
        Assert.Equal("=cmd()", rows[2][0]); // read back as plain text (quote-prefix style), never evaluated
    }
}
