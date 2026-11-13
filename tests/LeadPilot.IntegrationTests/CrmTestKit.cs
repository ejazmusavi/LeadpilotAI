using System.Net;
using System.Net.Http.Json;
using System.Text;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Crm;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>Helpers shared by the CRM integration tests (unique names/phones so tests never collide in the shared database).</summary>
public static class Crm
{
    private static int _phone = 10_000_000;
    public static string Phone() => "+9665" + Interlocked.Increment(ref _phone);
    public static string Token(string prefix = "zq") => prefix + Guid.NewGuid().ToString("N")[..10];
    public static string Name(string token, int i = 0) => $"{token} School {i:000}";

    public static async Task<LeadDto> CreateLead(ApiSession s, string? name = null, string? city = "Riyadh", string? website = null, bool contact = true, Guid? categoryId = null, string? phone = null, object[]? contacts = null)
    {
        var res = await s.Post("/api/v1/leads", new
        {
            name = name ?? Name(Token()), city, website, categoryId,
            contacts = contacts ?? (contact ? [new { name = "Sara", phone = phone ?? Phone(), email = $"{Token("m")}@example.com" }] : null)
        });
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"create lead failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return await ApiSession.Json<LeadDto>(res);
    }

    public static async Task<T> Ok<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"expected success, got {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await ApiSession.Json<T>(r);
    }

    public static async Task<string> ProblemCode(HttpResponseMessage r)
    {
        var doc = System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        return doc.TryGetProperty("code", out var c) ? c.GetString() ?? "" : doc.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
    }

    public static async Task<string> ProblemDetail(HttpResponseMessage r)
    {
        var doc = System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        return doc.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : "";
    }

    public static MultipartFormDataContent File(string fileName, byte[] content, string contentType = "application/octet-stream")
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(part, "file", fileName);
        return form;
    }

    public static byte[] CsvBytes(params string[] lines) => Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");

    public static async Task<ImportPreviewDto> Upload(ApiSession s, string fileName, byte[] bytes)
    {
        var res = await s.Http.PostAsync("/api/v1/imports", File(fileName, bytes));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"upload failed: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return await ApiSession.Json<ImportPreviewDto>(res);
    }

    public static object MappingFrom(ImportPreviewDto preview) => preview.Columns.Select(c => new { columnIndex = c.Index, field = c.SuggestedField }).ToArray();

    public static async Task<ImportJobDto> RunImport(ApiSession s, ImportPreviewDto preview, DuplicatePolicy policy = DuplicatePolicy.Skip, bool createCategories = false)
    {
        var res = await s.Post($"/api/v1/imports/{preview.Job.Id}/run", new { mapping = MappingFrom(preview), duplicatePolicy = policy, createMissingCategories = createCategories });
        return await Ok<ImportJobDto>(res);
    }

    public static async Task<PagedResult<T>> Page<T>(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"expected success, got {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        return await ApiSession.Json<PagedResult<T>>(r);
    }
}

public static class WorldExtensions
{
    /// <summary>A brand-new tenant (default pipeline, statuses and scoring rules provisioned) with an administrator, for tests that change tenant-wide configuration.</summary>
    public static async Task<(TenantDto Tenant, TestUser Admin, TestUser ReadOnly)> NewTenantAsync(this TestWorld w, string prefix = "c")
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        TenantDto tenant;
        using (var scope = w.Services.CreateScope())
            tenant = (await scope.ServiceProvider.GetRequiredService<ITenantAdminService>().CreateAsync(new CreateTenantRequest($"Tenant {prefix.ToUpper()} {suffix}", $"tenant-{prefix}-{suffix}", "Retail", "UTC", "en", null))).Value.Tenant;
        var admin = await w.AddUserAsync(tenant.Id, $"{prefix}-admin-{suffix}@c.test", SystemRoles.TenantAdministrator);
        var ro = await w.AddUserAsync(tenant.Id, $"{prefix}-ro-{suffix}@c.test", SystemRoles.ReadOnly);
        return (tenant, admin, ro);
    }
}

/// <summary>One tenant's worth of CRM data, created through the API, used by the isolation tests.</summary>
public sealed record SeededCrm(LeadDto Lead, ContactDto Contact, NoteDto Note, TagDto Tag, ConsentRecordDto Consent, SuppressionDto Suppression, ImportPreviewDto Upload, PipelineDto Pipeline,
    OpportunityDto Opportunity, CategoryDto Category, string Phone, string Token)
{
    public static async Task<SeededCrm> CreateAsync(ApiSession s, ApiSession marketing)
    {
        var token = Crm.Token("iso");
        var phone = Crm.Phone();
        var category = await Crm.Ok<CategoryDto>(await s.Post("/api/v1/categories", new { name = "Cat " + token }));
        var lead = await Crm.CreateLead(s, name: $"{token} Secret School", city: "Riyadh", website: $"https://{token}.example", phone: phone, categoryId: category.Id);
        var contact = (await Crm.Ok<List<ContactDto>>(await s.Get($"/api/v1/leads/{lead.Id}/contacts"))).Single();
        var note = await Crm.Ok<NoteDto>(await s.Post($"/api/v1/leads/{lead.Id}/notes", new { body = "secret note " + token }));
        var tag = await Crm.Ok<TagDto>(await s.Post("/api/v1/tags", new { name = "tag-" + token }));
        await Crm.Ok<List<LookupItem>>(await s.Put($"/api/v1/leads/{lead.Id}/tags", new { tagIds = new[] { tag.Id } }));
        var consent = await Crm.Ok<ConsentRecordDto>(await s.Post($"/api/v1/contacts/{contact.Id}/consent", new { channel = "WhatsApp", purpose = "Marketing", status = "Granted", source = "web form" }));
        var suppression = await Crm.Ok<SuppressionDto>(await marketing.Post("/api/v1/suppression", new { channel = "Email", value = $"{token}@blocked.example", reason = "OptOut" }));
        var upload = await Crm.Upload(s, $"{token}.csv", Crm.CsvBytes("Name,City", $"{token} Imported School,Riyadh"));
        var pipeline = await Crm.Ok<PipelineDto>(await s.Post("/api/v1/pipelines", new { name = "Pipeline " + token }));
        var opportunity = await Crm.Ok<OpportunityDto>(await s.Post($"/api/v1/leads/{lead.Id}/opportunities", new { name = "Deal " + token, value = 100 }));
        return new SeededCrm(lead, contact, note, tag, consent, suppression, upload, pipeline, opportunity, category, phone, token);
    }
}
