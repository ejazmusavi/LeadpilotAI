using System.Net;
using System.Text;
using ClosedXML.Excel;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Crm;

namespace LeadPilot.IntegrationTests;

[Collection("app")]
public class CrmImportExportApiTests(AppFixture app)
{
    private TestWorld W => app.World;
    private Task<ApiSession> Login(TestUser u) => ApiSession.LoginAsync(app, u.Email, u.Password);
    private const string Header = "Name,Category,City,Website,Phone,E-mail,Contact,Tags";

    [RequiresMySqlFact]
    public async Task Csv_import_validates_rows_creates_leads_and_reports_every_error()
    {
        var s = await Login(W.ASalesManager);
        var t = Crm.Token();
        var p1 = Crm.Phone(); var p2 = Crm.Phone();
        var csv = Crm.CsvBytes(Header,
            $"{t} North School,,Riyadh,https://www.{t}-north.example,{p1},north@{t}.example,Sara,vip; north",
            $"{t} South School,,Jeddah,,{p2},,Omar,",
            $"{t} Bad Phone School,,Riyadh,,12345,,,",
            $",,Riyadh,,,,,",
            $"{t} Bad Site School,,Riyadh,not a site,,,,",
            $"{t} bad email School,,Riyadh,,,nope,,",
            $"{t} NORTH school,,riyadh,,,,,");   // same name+city as row 2 (inside the same file)

        var preview = await Crm.Upload(s, "leads.csv", csv);
        Assert.Equal(7, preview.Job.TotalRows);
        Assert.Equal(ImportStatus.Uploaded, preview.Job.Status);
        Assert.Equal(ImportField.Name, preview.Columns[0].SuggestedField);
        Assert.Equal(ImportField.Phone, preview.Columns[4].SuggestedField);
        Assert.Equal(5, preview.SampleRows.Count);

        var job = await Crm.RunImport(s, preview);
        Assert.Equal(ImportStatus.Completed, job.Status);
        Assert.Equal(2, job.CreatedCount);
        Assert.Equal(1, job.SkippedDuplicateCount);
        Assert.Equal(4, job.ErrorCount);

        var errors = await Crm.Page<ImportRowErrorDto>(await s.Get($"/api/v1/imports/{job.Id}/errors"));
        Assert.Equal(5, errors.TotalCount);
        Assert.Equal([4, 5, 6, 7, 8], errors.Items.Select(e => e.RowNumber));          // spreadsheet row numbers (the header is row 1)
        Assert.Contains(errors.Items, e => e.RowNumber == 4 && e.Column == "Phone" && e.Kind == ImportErrorKind.Validation);
        Assert.Contains(errors.Items, e => e.RowNumber == 5 && e.Column == "Name" && e.Message.Contains("required"));
        Assert.Contains(errors.Items, e => e.RowNumber == 6 && e.Column == "Website");
        Assert.Contains(errors.Items, e => e.RowNumber == 7 && e.Column == "E-mail");
        Assert.Contains(errors.Items, e => e.RowNumber == 8 && e.Kind == ImportErrorKind.Duplicate && e.Resolution == DuplicateResolution.Skipped && e.DuplicateLeadName == $"{t} North School");
        Assert.Single((await Crm.Page<ImportRowErrorDto>(await s.Get($"/api/v1/imports/{job.Id}/errors?kind=Duplicate"))).Items);

        // the valid rows really became leads, with contacts, tags, an import source and the Imported activity
        var created = await Crm.Page<LeadListItemDto>(await s.Get($"/api/v1/leads?search={t}&pageSize=50"));
        Assert.Equal(2, created.TotalCount);
        var north = created.Items.Single(i => i.Name.Contains("North"));
        Assert.Equal(p1, north.PrimaryPhone);
        Assert.Equal(["north", "vip"], north.Tags.Select(x => x.ToLowerInvariant()).Order());
        var detail = await Crm.Ok<LeadDto>(await s.Get($"/api/v1/leads/{north.Id}"));
        Assert.Equal(LeadSourceType.Import, detail.PrimarySourceType);
        Assert.Contains(detail.Sources, x => x.Type == LeadSourceType.Import && x.Name == "leads.csv");
        Assert.Contains((await Crm.Page<ActivityDto>(await s.Get($"/api/v1/leads/{north.Id}/activities"))).Items, a => a.Type == LeadActivityType.Imported);
        Assert.True(detail.Score > 0);                                                   // scored on import

        // an imported contact carries NO consent
        var contact = (await Crm.Ok<List<ContactDto>>(await s.Get($"/api/v1/leads/{north.Id}/contacts"))).Single();
        Assert.Empty(contact.Consent);
        Assert.Equal(DataOrigin.Public, contact.DataOrigin);

        // the error report is CSV and cannot be run again
        var report = await s.Get($"/api/v1/imports/{job.Id}/errors/report");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        var text = await report.Content.ReadAsStringAsync();
        Assert.Contains("Row,Type,Column,Message,Resolution", text);
        Assert.Equal(5, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Post($"/api/v1/imports/{job.Id}/run", new { mapping = Crm.MappingFrom(preview) })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Get($"/api/v1/imports/{job.Id}/preview")).StatusCode);
        Assert.Contains((await Crm.Page<ImportJobDto>(await s.Get("/api/v1/imports"))).Items, j => j.Id == job.Id);
    }

    [RequiresMySqlFact]
    public async Task Duplicate_policies_skip_merge_and_review()
    {
        var s = await Login(W.ASalesManager);
        var t = Crm.Token();
        var category = await Crm.Ok<CategoryDto>(await s.Post("/api/v1/categories", new { name = "Existing " + t }));
        await Crm.Ok<CategoryDto>(await s.Post("/api/v1/categories", new { name = "Other " + t }));
        var existing = await Crm.CreateLead(s, name: $"{t} Heritage Academy", city: "Riyadh", categoryId: category.Id, contact: false);
        var phone = Crm.Phone();
        var row = $"{t} heritage academy,Other {t},Riyadh,https://{t}-heritage.example,{phone},info@{t}.example,Dina,warm";

        // Skip: nothing changes
        var skip = await Crm.RunImport(s, await Crm.Upload(s, "skip.csv", Crm.CsvBytes(Header, row)), DuplicatePolicy.Skip);
        Assert.Equal((0, 0, 1), (skip.CreatedCount, skip.MergedCount, skip.SkippedDuplicateCount));
        var untouched = await Crm.Ok<LeadDto>(await s.Get($"/api/v1/leads/{existing.Id}"));
        Assert.Null(untouched.Website);
        Assert.Equal(0, untouched.ContactCount);

        // Review: held until a person decides
        var review = await Crm.RunImport(s, await Crm.Upload(s, "review.csv", Crm.CsvBytes(Header, row)), DuplicatePolicy.Review);
        Assert.Equal(1, review.PendingDuplicateCount);
        var pending = (await Crm.Page<ImportRowErrorDto>(await s.Get($"/api/v1/imports/{review.Id}/errors?kind=Duplicate"))).Items.Single();
        Assert.Equal(DuplicateResolution.Pending, pending.Resolution);
        Assert.Equal(existing.Id, pending.DuplicateLeadId);
        Assert.Equal(0, (await Crm.Ok<LeadDto>(await s.Get($"/api/v1/leads/{existing.Id}"))).ContactCount);   // still untouched

        var merged = await Crm.Ok<ImportJobDto>(await s.Post($"/api/v1/imports/{review.Id}/duplicates/{pending.Id}", new { merge = true }));
        Assert.Equal((0, 1, 0), (merged.PendingDuplicateCount, merged.MergedCount, merged.SkippedDuplicateCount));
        var after = await Crm.Ok<LeadDto>(await s.Get($"/api/v1/leads/{existing.Id}"));
        Assert.Equal($"{t}-heritage.example", after.WebsiteDomain);      // blank field filled
        Assert.Equal(category.Id, after.CategoryId);                      // existing value NOT overwritten by the file's category
        Assert.Equal(1, after.ContactCount);
        Assert.Contains(after.Tags, x => x.Name == "warm");
        Assert.Contains(after.Sources, x => x.Type == LeadSourceType.Import);
        Assert.Contains((await Crm.Page<ActivityDto>(await s.Get($"/api/v1/leads/{existing.Id}/activities"))).Items, a => a.Type == LeadActivityType.Merged);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Post($"/api/v1/imports/{review.Id}/duplicates/{pending.Id}", new { merge = true })).StatusCode);   // decided once

        // Review → skip
        var review2 = await Crm.RunImport(s, await Crm.Upload(s, "review2.csv", Crm.CsvBytes(Header, $"{t} Heritage Academy,,Riyadh,,{Crm.Phone()},,Zed,")), DuplicatePolicy.Review);
        var p2 = (await Crm.Page<ImportRowErrorDto>(await s.Get($"/api/v1/imports/{review2.Id}/errors?kind=Duplicate"))).Items.Single();
        var skipped = await Crm.Ok<ImportJobDto>(await s.Post($"/api/v1/imports/{review2.Id}/duplicates/{p2.Id}", new { merge = false }));
        Assert.Equal((0, 0, 1), (skipped.PendingDuplicateCount, skipped.MergedCount, skipped.SkippedDuplicateCount));
        Assert.Equal(1, (await Crm.Ok<LeadDto>(await s.Get($"/api/v1/leads/{existing.Id}"))).ContactCount);

        // Merge policy applies immediately; a repeat adds nothing new
        var phone3 = Crm.Phone();
        var mergeCsv = Crm.CsvBytes(Header, $"{t} Heritage Academy,,Riyadh,,{phone3},,Eve,");
        var m1 = await Crm.RunImport(s, await Crm.Upload(s, "merge1.csv", mergeCsv), DuplicatePolicy.Merge);
        Assert.Equal(1, m1.MergedCount);
        Assert.Equal(2, (await Crm.Ok<LeadDto>(await s.Get($"/api/v1/leads/{existing.Id}"))).ContactCount);
        var m2 = await Crm.RunImport(s, await Crm.Upload(s, "merge2.csv", mergeCsv), DuplicatePolicy.Merge);
        Assert.Equal((0, 1), (m2.MergedCount, m2.SkippedDuplicateCount));
        Assert.Equal(2, (await Crm.Ok<LeadDto>(await s.Get($"/api/v1/leads/{existing.Id}"))).ContactCount);

        // an archived duplicate is never modified by a merge
        Assert.Equal(HttpStatusCode.NoContent, (await s.Delete($"/api/v1/leads/{existing.Id}")).StatusCode);
        var m3 = await Crm.RunImport(s, await Crm.Upload(s, "merge3.csv", Crm.CsvBytes(Header, $"{t} Heritage Academy,,Riyadh,,{Crm.Phone()},,Fay,")), DuplicatePolicy.Merge);
        Assert.Equal((0, 1), (m3.MergedCount, m3.SkippedDuplicateCount));
    }

    [RequiresMySqlFact]
    public async Task Categories_stages_and_owners_in_a_file_are_resolved_or_reported()
    {
        var s = await Login(W.ASalesManager);
        var t = Crm.Token();
        var category = await Crm.Ok<CategoryDto>(await s.Post("/api/v1/categories", new { name = "Known " + t }));
        var csv = Crm.CsvBytes("Name,Category,Stage,Owner,Phone",
            $"{t} A,Known {t},Contacted,{W.ASalesAgent.Email},{Crm.Phone()}",
            $"{t} B,Mystery {t},,,",
            $"{t} C,,Nonexistent stage,,",
            $"{t} D,,,nobody@elsewhere.test,",
            $"{t} E,,,{W.BAdmin.Email},");              // a member of ANOTHER tenant

        var strict = await Crm.RunImport(s, await Crm.Upload(s, "refs.csv", csv));
        Assert.Equal(1, strict.CreatedCount);
        Assert.Equal(4, strict.ErrorCount);
        var a = (await Crm.Page<LeadListItemDto>(await s.Get($"/api/v1/leads?search={t}%20A"))).Items.Single();
        Assert.Equal("Contacted", a.Stage);
        Assert.Equal("Known " + t, a.Category);
        Assert.Equal(W.ASalesAgent.Id, a.OwnerUserId);

        var errors = (await Crm.Page<ImportRowErrorDto>(await s.Get($"/api/v1/imports/{strict.Id}/errors"))).Items;
        Assert.Contains(errors, e => e.Column == "Category" && e.Message.Contains("Mystery"));
        Assert.Contains(errors, e => e.Column == "Stage");
        Assert.Equal(2, errors.Count(e => e.Column == "Owner"));

        // with "create missing categories" the unknown category is created (once)
        var lenient = await Crm.RunImport(s, await Crm.Upload(s, "refs2.csv", Crm.CsvBytes("Name,Category", $"{t} F,Fresh {t}", $"{t} G,fresh {t.ToUpper()}")), createCategories: true);
        Assert.Equal(2, lenient.CreatedCount);
        Assert.Single((await Crm.Ok<List<CategoryDto>>(await s.Get("/api/v1/categories"))), c => c.Name.Equals($"Fresh {t}", StringComparison.OrdinalIgnoreCase));
    }

    [RequiresMySqlFact]
    public async Task Import_limits_and_file_validation()
    {
        var s = await Login(W.ASalesManager);
        async Task<HttpResponseMessage> Post(string name, byte[] bytes) => await s.Http.PostAsync("/api/v1/imports", Crm.File(name, bytes));

        Assert.Equal(HttpStatusCode.BadRequest, (await Post("leads.txt", Crm.CsvBytes(Header, "a,,,,,,,"))).StatusCode);                   // wrong extension
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("leads.exe", Encoding.UTF8.GetBytes("MZ"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("empty.csv", [])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("header-only.csv", Crm.CsvBytes(Header))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("fake.xlsx", Crm.CsvBytes(Header, "a,,,,,,,"))).StatusCode);                  // text pretending to be a workbook
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("binary.csv", [0x4E, 0x61, 0x6D, 0x65, 0x00, 0x01, 0x02, 0x0A, 0x41])).StatusCode);   // NUL bytes
        var xlsx = SpreadsheetBytes();
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("workbook.csv", xlsx)).StatusCode);                                             // workbook pretending to be CSV
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("bad-quote.csv", Encoding.UTF8.GetBytes("Name\n\"unterminated"))).StatusCode);

        var tooManyRows = Crm.CsvBytes(Enumerable.Range(0, 5002).Select(i => i == 0 ? "Name" : $"Row {i}").ToArray());
        var many = await Post("many.csv", tooManyRows);
        Assert.Equal(HttpStatusCode.BadRequest, many.StatusCode);
        Assert.Contains("5000", await Crm.ProblemDetail(many));
        Assert.Equal(HttpStatusCode.Created, (await Post("exactly.csv", Crm.CsvBytes(Enumerable.Range(0, 5001).Select(i => i == 0 ? "Name" : $"Row {i}").ToArray()))).StatusCode);

        var huge = await Post("huge.csv", Encoding.UTF8.GetBytes("Name\n" + new string('a', 6 * 1024 * 1024)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);

        // mapping problems are 400 and do not consume the upload
        var preview = await Crm.Upload(s, "map.csv", Crm.CsvBytes("A,B", "x,y"));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post($"/api/v1/imports/{preview.Job.Id}/run", new { mapping = new object[] { new { columnIndex = 0, field = "City" } } })).StatusCode);                // no Name column
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post($"/api/v1/imports/{preview.Job.Id}/run", new { mapping = new object[] { new { columnIndex = 0, field = "Name" }, new { columnIndex = 1, field = "Name" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post($"/api/v1/imports/{preview.Job.Id}/run", new { mapping = new object[] { new { columnIndex = 9, field = "Name" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Post($"/api/v1/imports/{preview.Job.Id}/run", new { mapping = new object[] { new { columnIndex = 0, field = "Name" } } })).StatusCode);

        // permission
        var agent = await Login(W.ASalesAgent);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.Http.PostAsync("/api/v1/imports", Crm.File("x.csv", Crm.CsvBytes("Name", "a")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.Get("/api/v1/imports")).StatusCode);
    }

    private static byte[] SpreadsheetBytes(params string[][] rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Leads");
        var all = rows.Length == 0 ? [["Name", "City", "Phone"]] : rows;
        for (var r = 0; r < all.Length; r++) for (var c = 0; c < all[r].Length; c++) ws.Cell(r + 1, c + 1).Value = all[r][c];
        using var ms = new MemoryStream(); wb.SaveAs(ms); return ms.ToArray();
    }

    [RequiresMySqlFact]
    public async Task Excel_xlsx_import_works_like_csv()
    {
        var s = await Login(W.ASalesManager);
        var t = Crm.Token();
        var phone = Crm.Phone();
        var bytes = SpreadsheetBytes(["Name", "City", "Phone", "Website"], [$"{t} Excel School", "Dammam", phone, $"{t}.example"], ["", "Dammam", "", ""], [$"{t} Second School", "Dammam", "bad", ""]);
        var preview = await Crm.Upload(s, "leads.xlsx", bytes);
        Assert.Equal(ImportFileType.Xlsx, preview.Job.FileType);
        Assert.Equal(3, preview.Job.TotalRows);
        var job = await Crm.RunImport(s, preview);
        Assert.Equal((1, 2), (job.CreatedCount, job.ErrorCount));
        var lead = (await Crm.Page<LeadListItemDto>(await s.Get($"/api/v1/leads?search={t}"))).Items.Single();
        Assert.Equal(phone, lead.PrimaryPhone);
    }

    [RequiresMySqlFact]
    public async Task Imports_of_a_numeric_phone_column_keep_every_digit()
    {
        var s = await Login(W.ASalesManager);
        var t = Crm.Token();
        var phone = Crm.Phone();
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Leads");
        ws.Cell(1, 1).Value = "Name"; ws.Cell(1, 2).Value = "Phone";
        ws.Cell(2, 1).Value = $"{t} Numeric"; ws.Cell(2, 2).Value = double.Parse(phone[1..], System.Globalization.CultureInfo.InvariantCulture);   // stored as a number, as Excel does
        using var ms = new MemoryStream(); wb.SaveAs(ms);
        var preview = await Crm.Upload(s, "numeric.xlsx", ms.ToArray());
        // 966xxxxxxxxx without "+" and without a leading zero cannot be told apart from a local number: reported, never guessed
        var job = await Crm.RunImport(s, preview);
        Assert.Equal(1, job.ErrorCount);
        var err = (await Crm.Page<ImportRowErrorDto>(await s.Get($"/api/v1/imports/{job.Id}/errors"))).Items.Single();
        Assert.Equal("Phone", err.Column);
    }

    [RequiresMySqlFact]
    public async Task Export_is_permissioned_audited_filtered_and_formula_injection_safe()
    {
        var s = await Login(W.ASalesManager);
        var admin = await Login(W.AAdmin);
        var t = Crm.Token();
        var evil = $"=HYPERLINK(\"http://evil.example\",\"{t}\")";
        await Crm.CreateLead(s, name: evil, city: "Riyadh");
        await Crm.CreateLead(s, name: $"+cmd|' /C calc'!A0 {t}", city: "@SUM(1)");
        await Crm.CreateLead(s, name: $"{t} Plain, with comma \"quotes\"", city: "Jeddah");
        var b = await Login(W.BSalesManager);
        await Crm.CreateLead(b, name: $"{t} Other tenant secret");

        var csvRes = await s.Get($"/api/v1/leads/export?search={t}&format=Csv");
        Assert.Equal(HttpStatusCode.OK, csvRes.StatusCode);
        Assert.StartsWith("text/csv", csvRes.Content.Headers.ContentType!.MediaType + ";");
        Assert.EndsWith(".csv", csvRes.Content.Headers.ContentDisposition!.FileName);
        var csv = Encoding.UTF8.GetString(await csvRes.Content.ReadAsByteArrayAsync());
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + 3, lines.Length);
        Assert.StartsWith("﻿Name,Category", lines[0]);
        Assert.DoesNotContain(lines.Skip(1), l => l.StartsWith('=') || l.StartsWith('+') || l.StartsWith('@') || l.Contains(",=") || l.Contains(",@") || l.Contains(",+cmd"));
        Assert.Contains(lines, l => l.StartsWith("\"'=HYPERLINK(\"\"http://evil.example\"\"") || l.Contains("'=HYPERLINK"));
        Assert.Contains("'+cmd", csv);
        Assert.Contains("'@SUM(1)", csv);
        Assert.Contains($"\"{t} Plain, with comma \"\"quotes\"\"\"", csv);
        Assert.DoesNotContain("Other tenant secret", csv);                       // never another tenant's rows

        var xlsxRes = await s.Get($"/api/v1/leads/export?search={t}&format=Xlsx");
        Assert.Equal(HttpStatusCode.OK, xlsxRes.StatusCode);
        using (var wb = new XLWorkbook(new MemoryStream(await xlsxRes.Content.ReadAsByteArrayAsync())))
        {
            var ws = wb.Worksheet(1);
            Assert.Equal("Name", ws.Cell(1, 1).GetString());
            for (var r = 2; r <= 4; r++) Assert.False(ws.Cell(r, 1).HasFormula || ws.Cell(r, 2).HasFormula);
            Assert.Contains(Enumerable.Range(2, 3).Select(r => ws.Cell(r, 1)), c => c.GetString().StartsWith("=HYPERLINK") && c.Style.IncludeQuotePrefix);
        }

        var filtered = await s.Get($"/api/v1/leads/export?search={t}&city=Jeddah&format=Csv");
        Assert.Equal(2, (await filtered.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);

        var audit = await Crm.Page<AuditLogDto>(await admin.Get("/api/v1/audit-logs?action=Leads.Exported&pageSize=100"));
        Assert.True(audit.Items.Count(a => a.ActorEmail == W.ASalesManager.Email) >= 3);

        // permission: Leads.Export only
        Assert.Equal(HttpStatusCode.Forbidden, (await (await Login(W.ASalesAgent)).Get($"/api/v1/leads/export?search={t}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await Login(W.AReadOnly)).Get($"/api/v1/leads/export?search={t}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ApiSession.Anonymous(app).Get($"/api/v1/leads/export?search={t}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await (await Login(W.AMarketing)).Get($"/api/v1/leads/export?search={t}")).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Export_refuses_more_rows_than_the_limit()
    {
        var (tenant, admin, _) = await W.NewTenantAsync("exp");
        var a = await ApiSession.LoginAsync(app, admin.Email, admin.Password);
        var rows = Enumerable.Range(0, 102).Select(i => i == 0 ? "Name" : $"Export Row {i}").ToArray();   // header + 101 leads (limit is 100)
        var job = await Crm.RunImport(a, await Crm.Upload(a, "many.csv", Crm.CsvBytes(rows)));
        Assert.Equal(101, job.CreatedCount);
        var res = await a.Get("/api/v1/leads/export");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("limit is 100", await Crm.ProblemDetail(res));
        Assert.Equal(HttpStatusCode.OK, (await a.Get("/api/v1/leads/export?search=Export%20Row%201")).StatusCode);   // narrowing the filter works
    }
}
