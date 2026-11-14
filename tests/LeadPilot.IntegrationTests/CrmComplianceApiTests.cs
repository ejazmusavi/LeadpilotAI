using System.Net;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Crm;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeadPilot.IntegrationTests;

/// <summary>Consent history, suppression list and the eligibility gate (single contact and audience query).</summary>
[Collection("app")]
public class CrmComplianceApiTests(AppFixture app)
{
    private TestWorld W => app.World;
    private Task<ApiSession> Login(TestUser u) => ApiSession.LoginAsync(app, u.Email, u.Password);

    private static object Grant(string channel = "WhatsApp", string purpose = "Marketing", string status = "Granted", string source = "web form", string? evidence = "form-123") =>
        new { channel, purpose, status, source, evidenceRef = evidence };

    [RequiresMySqlFact]
    public async Task Consent_is_recorded_append_only_audited_and_visible_in_history()
    {
        var s = await Login(W.ASalesManager);
        var admin = await Login(W.AAdmin);
        var lead = await Crm.CreateLead(s, contact: false);
        var contact = await Crm.Ok<ContactDto>(await s.Post($"/api/v1/leads/{lead.Id}/contacts", new { name = "Sara", phone = Crm.Phone() }));

        var granted = await s.Post($"/api/v1/contacts/{contact.Id}/consent", Grant());
        Assert.Equal(HttpStatusCode.Created, granted.StatusCode);
        var record = await ApiSession.Json<ConsentRecordDto>(granted);
        Assert.Equal(ConsentStatus.Granted, record.Status);
        Assert.Equal("form-123", record.EvidenceRef);
        Assert.NotNull(record.RecordedByName);
        Assert.Equal(ConsentStatus.Granted, (await Crm.Ok<List<ContactDto>>(await s.Get($"/api/v1/leads/{lead.Id}/contacts"))).Single().Consent.Single().Status);

        var revoked = await Crm.Ok<ConsentRecordDto>(await s.Post($"/api/v1/contacts/{contact.Id}/consent", Grant(status: "Revoked", source: "STOP reply", evidence: null)));
        var history = await Crm.Page<ConsentRecordDto>(await s.Get($"/api/v1/contacts/{contact.Id}/consent"));
        Assert.Equal(2, history.TotalCount);
        Assert.Equal([ConsentStatus.Revoked, ConsentStatus.Granted], history.Items.Select(i => i.Status));   // newest first, nothing overwritten
        Assert.Equal(2, (await Crm.Page<ConsentRecordDto>(await s.Get($"/api/v1/leads/{lead.Id}/consent"))).TotalCount);
        Assert.Equal(ConsentStatus.Revoked, (await Crm.Ok<List<ContactDto>>(await s.Get($"/api/v1/leads/{lead.Id}/contacts"))).Single().Consent.Single().Status);

        // validation
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post($"/api/v1/contacts/{contact.Id}/consent", Grant(source: " "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post($"/api/v1/contacts/{contact.Id}/consent", new { channel = "WhatsApp", purpose = "Marketing", status = "Granted", source = "x", occurredAtUtc = DateTime.UtcNow.AddDays(3) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post($"/api/v1/contacts/{contact.Id}/consent", Grant(channel: "Email"))).StatusCode);       // the contact has no e-mail address
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Post($"/api/v1/contacts/{contact.Id}/consent", new { channel = 99, purpose = "Marketing", status = "Granted", source = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Post($"/api/v1/contacts/{Guid.NewGuid()}/consent", Grant())).StatusCode);

        // no edit / delete surface exists
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            var res = await s.Http.SendAsync(new HttpRequestMessage(method, $"/api/v1/contacts/{contact.Id}/consent/{record.Id}"));
            Assert.True(res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, $"{method} consent should not exist but was {(int)res.StatusCode}");
        }

        // audit trail
        var audit = await Crm.Page<AuditLogDto>(await admin.Get("/api/v1/audit-logs?action=Consent.&pageSize=100"));
        Assert.Contains(audit.Items, a => a.Action == "Consent.Granted" && a.EntityId == record.Id.ToString());
        Assert.Contains(audit.Items, a => a.Action == "Consent.Revoked" && a.EntityId == revoked.Id.ToString());
        Assert.All(audit.Items.Where(a => a.Action.StartsWith("Consent.")), a => Assert.DoesNotContain("+9665", a.Summary));   // no raw phone numbers in the audit text

        // permissions: recording needs Consent.Manage, reading needs Leads.View
        var agent = await Login(W.ASalesAgent);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.Post($"/api/v1/contacts/{contact.Id}/consent", Grant())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await agent.Get($"/api/v1/contacts/{contact.Id}/consent")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await Login(W.AReadOnly)).Post($"/api/v1/contacts/{contact.Id}/consent", Grant())).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Consent_rows_cannot_be_changed_or_deleted_even_by_code()
    {
        var s = await Login(W.ASalesManager);
        var lead = await Crm.CreateLead(s);
        var contact = (await Crm.Ok<List<ContactDto>>(await s.Get($"/api/v1/leads/{lead.Id}/contacts"))).Single();
        var rec = await Crm.Ok<ConsentRecordDto>(await s.Post($"/api/v1/contacts/{contact.Id}/consent", Grant()));

        using var scope = W.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<LeadPilot.Application.Abstractions.ITenantContextAccessor>().SetTenant(W.A.Id);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = await db.ConsentRecords.FirstAsync(r => r.Id == rec.Id);
        db.Entry(row).Property(r => r.Source).CurrentValue = "tampered";
        var edit = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        Assert.Contains("append-only", edit.Message);
        db.ChangeTracker.Clear();
        db.ConsentRecords.Remove(await db.ConsentRecords.FirstAsync(r => r.Id == rec.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal("web form", (await db.ConsentRecords.AsNoTracking().FirstAsync(r => r.Id == rec.Id)).Source);

        // the same guard protects the other history tables
        db.ChangeTracker.Clear();
        var activity = await db.LeadActivities.FirstAsync(a => a.LeadId == lead.Id);
        db.LeadActivities.Remove(activity);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.LeadScores.Remove(await db.LeadScores.FirstAsync(a => a.LeadId == lead.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [RequiresMySqlFact]
    public async Task Changing_a_contacts_phone_revokes_consent_given_for_the_old_number()
    {
        var s = await Login(W.ASalesManager);
        var phone = Crm.Phone();
        var lead = await Crm.CreateLead(s, phone: phone);
        var contact = (await Crm.Ok<List<ContactDto>>(await s.Get($"/api/v1/leads/{lead.Id}/contacts"))).Single();
        await Crm.Ok<ConsentRecordDto>(await s.Post($"/api/v1/contacts/{contact.Id}/consent", Grant()));
        Assert.True((await Eligibility(s, contact.Id, "WhatsApp")).IsEligible);

        var changed = await Crm.Ok<ContactDto>(await s.Put($"/api/v1/contacts/{contact.Id}", new { name = "Sara", phone = Crm.Phone(), email = contact.Email }));
        Assert.Equal(ConsentStatus.Revoked, changed.Consent.Single(c => c.Channel == ConsentChannel.WhatsApp).Status);
        Assert.False((await Eligibility(s, contact.Id, "WhatsApp")).IsEligible);
        var history = await Crm.Page<ConsentRecordDto>(await s.Get($"/api/v1/contacts/{contact.Id}/consent"));
        Assert.Contains(history.Items, r => r.Status == ConsentStatus.Revoked && r.Source.StartsWith("system"));
    }

    private sealed record EligibilityResponse(bool IsEligible, List<ReasonDto> Reasons);
    private sealed record ReasonDto(string Code, string Message);
    private static async Task<EligibilityResponse> Eligibility(ApiSession s, Guid contactId, string channel, string purpose = "Marketing") =>
        await Crm.Ok<EligibilityResponse>(await s.Get($"/api/v1/contacts/{contactId}/eligibility?channel={channel}&purpose={purpose}"));

    [RequiresMySqlFact]
    public async Task Suppression_list_normalises_deduplicates_audits_and_is_permissioned()
    {
        var mkt = await Login(W.AMarketing);
        var admin = await Login(W.AAdmin);
        var n = Crm.Phone().Replace("+9665", "");
        var raw = $" 00966 5{n[..3]} {n[3..]} ";
        var added = await mkt.Post("/api/v1/suppression", new { channel = "WhatsApp", value = raw, reason = "OptOut", note = "asked to stop" });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var entry = await ApiSession.Json<SuppressionDto>(added);
        Assert.Equal("+9665" + n, entry.Value);
        Assert.Equal(SuppressionReason.OptOut, entry.Reason);

        Assert.Equal(HttpStatusCode.Conflict, (await mkt.Post("/api/v1/suppression", new { channel = "WhatsApp", value = "+9665" + n, reason = "Blocked" })).StatusCode);   // unique per (channel, value)
        Assert.Equal(HttpStatusCode.Created, (await mkt.Post("/api/v1/suppression", new { channel = "Sms", value = "+9665" + n, reason = "OptOut" })).StatusCode);          // other channel is separate
        var email = $"{Crm.Token("sup")}@Example.com";
        var mail = await Crm.Ok<SuppressionDto>(await mkt.Post("/api/v1/suppression", new { channel = "Email", value = email, reason = "Legal" }));
        Assert.Equal(email.ToLowerInvariant(), mail.Value);
        Assert.Equal(HttpStatusCode.BadRequest, (await mkt.Post("/api/v1/suppression", new { channel = "Email", value = "+966501234567", reason = "OptOut" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mkt.Post("/api/v1/suppression", new { channel = "WhatsApp", value = "abc", reason = "OptOut" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await mkt.Post("/api/v1/suppression", new { channel = "WhatsApp", value = "+966501234567", reason = "Whatever" })).StatusCode);

        var list = await Crm.Page<SuppressionDto>(await mkt.Get($"/api/v1/suppression?search={n}"));
        Assert.Equal(2, list.TotalCount);
        Assert.Single((await Crm.Page<SuppressionDto>(await mkt.Get($"/api/v1/suppression?search={n}&channel=Sms"))).Items);

        Assert.Equal(HttpStatusCode.NoContent, (await mkt.Delete($"/api/v1/suppression/{entry.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mkt.Delete($"/api/v1/suppression/{entry.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await mkt.Post("/api/v1/suppression", new { channel = "WhatsApp", value = "+9665" + n, reason = "OptOut" })).StatusCode);        // re-adding after removal works

        var audit = await Crm.Page<AuditLogDto>(await admin.Get("/api/v1/audit-logs?action=Suppression.&pageSize=100"));
        Assert.Contains(audit.Items, a => a.Action == "Suppression.Added" && a.EntityId == entry.Id.ToString());
        Assert.Contains(audit.Items, a => a.Action == "Suppression.Removed" && a.EntityId == entry.Id.ToString());
        Assert.DoesNotContain(audit.Items, a => a.Summary.Contains("9665" + n) || a.Summary.Contains(email.ToLowerInvariant()));   // masked in the audit text

        Assert.Equal(HttpStatusCode.Forbidden, (await (await Login(W.AReadOnly)).Get("/api/v1/suppression")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await (await Login(W.ASalesAgent)).Post("/api/v1/suppression", new { channel = "Sms", value = "+966500000001", reason = "OptOut" })).StatusCode);
    }

    [RequiresMySqlFact]
    public async Task Suppressed_and_unconsented_contacts_never_enter_the_eligible_audience()
    {
        var s = await Login(W.ASalesManager);
        var mkt = await Login(W.AMarketing);
        var tag = await Crm.Ok<TagDto>(await s.Post("/api/v1/tags", new { name = Crm.Token("aud") }));

        var phones = Enumerable.Range(0, 8).Select(_ => Crm.Phone()).ToArray();
        var lead = await Crm.CreateLead(s, contacts: phones.Select((p, i) => (object)new { name = "c" + i, phone = p, email = $"{Crm.Token("e")}@example.com" }).ToArray());
        await Crm.Ok<List<LookupItem>>(await s.Put($"/api/v1/leads/{lead.Id}/tags", new { tagIds = new[] { tag.Id } }));
        var contacts = (await Crm.Ok<List<ContactDto>>(await s.Get($"/api/v1/leads/{lead.Id}/contacts"))).OrderBy(c => c.Name).ToList();
        Guid Id(int i) => contacts[i].Id;

        async Task Consent(int i, string status = "Granted", string channel = "WhatsApp", string purpose = "Marketing") =>
            Assert.Equal(HttpStatusCode.Created, (await s.Post($"/api/v1/contacts/{Id(i)}/consent", Grant(channel, purpose, status))).StatusCode);

        await Consent(0);                                                   // c0: eligible
        /* c1: never consented */
        await Consent(2); await Consent(2, "Revoked");                      // c2: revoked
        await Consent(3);                                                   // c3: granted, then suppressed on WhatsApp
        await Consent(4);                                                   // c4: granted, then suppressed on every channel
        await Consent(5);                                                   // c5: granted, then removed
        await Consent(6, purpose: "Service");                               // c6: consent for another purpose only
        await Consent(7, channel: "Email");                                 // c7: e-mail consent only
        Assert.Equal(HttpStatusCode.Created, (await mkt.Post("/api/v1/suppression", new { channel = "WhatsApp", value = phones[Array.FindIndex(phones, p => p == contacts[3].Phone)], reason = "OptOut" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await mkt.Post("/api/v1/suppression", new { channel = "Any", value = contacts[4].Phone, reason = "Legal" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Delete($"/api/v1/contacts/{Id(5)}")).StatusCode);

        async Task<HashSet<Guid>> Audience(string channel, string purpose = "Marketing")
        {
            var page = await Crm.Page<EligibleContactDto>(await mkt.Get($"/api/v1/audience/eligible-contacts?channel={channel}&purpose={purpose}&tagId={tag.Id}&pageSize=100"));
            return page.Items.Select(i => i.ContactId).ToHashSet();
        }
        Assert.Equal([Id(0)], await Audience("WhatsApp"));
        Assert.Equal([Id(6)], await Audience("WhatsApp", "Service"));
        Assert.Equal([Id(7)], await Audience("Email"));
        Assert.Empty(await Audience("Sms"));

        // the single-contact gate agrees with the audience query, with a reason for each refusal
        Assert.True((await Eligibility(s, Id(0), "WhatsApp")).IsEligible);
        Assert.Contains("NoConsent", (await Eligibility(s, Id(1), "WhatsApp")).Reasons.Select(r => r.Code));
        Assert.Contains("ConsentRevoked", (await Eligibility(s, Id(2), "WhatsApp")).Reasons.Select(r => r.Code));
        var suppressed = await Eligibility(s, Id(3), "WhatsApp");
        Assert.False(suppressed.IsEligible);
        Assert.Contains("Suppressed", suppressed.Reasons.Select(r => r.Code));
        Assert.Contains("Suppressed", (await Eligibility(s, Id(4), "WhatsApp")).Reasons.Select(r => r.Code));
        Assert.Contains("ContactRemoved", (await Eligibility(s, Id(5), "WhatsApp")).Reasons.Select(r => r.Code));
        Assert.False((await Eligibility(s, Id(6), "WhatsApp")).IsEligible);
        Assert.False((await Eligibility(s, Id(7), "WhatsApp")).IsEligible);
        Assert.True((await Eligibility(s, Id(7), "Email")).IsEligible);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Get($"/api/v1/contacts/{Guid.NewGuid()}/eligibility?channel=WhatsApp")).StatusCode);

        // removing the suppression entry (a deliberate act) makes the still-consented contact eligible again
        var suppressionPage = await Crm.Page<SuppressionDto>(await mkt.Get($"/api/v1/suppression?search={contacts[3].Phone![1..]}"));
        Assert.Equal(HttpStatusCode.NoContent, (await mkt.Delete($"/api/v1/suppression/{suppressionPage.Items.Single(i => i.Channel == SuppressionChannel.WhatsApp).Id}")).StatusCode);
        Assert.Equal([Id(0), Id(3)], (await Audience("WhatsApp")).OrderBy(g => g == Id(0) ? 0 : 1).ToList());

        // leads that are archived or in a suppressed stage are excluded whatever the consent says
        Assert.Equal(HttpStatusCode.NoContent, (await s.Delete($"/api/v1/leads/{lead.Id}")).StatusCode);
        Assert.Empty(await Audience("WhatsApp"));
        Assert.Contains("LeadArchived", (await Eligibility(s, Id(0), "WhatsApp")).Reasons.Select(r => r.Code));
        Assert.Equal(HttpStatusCode.NoContent, (await s.Post($"/api/v1/leads/{lead.Id}/restore")).StatusCode);
        Assert.NotEmpty(await Audience("WhatsApp"));
        var suppressedStage = (await Crm.Ok<List<PipelineDto>>(await s.Get("/api/v1/pipelines"))).Single(p => p.IsDefault).Stages.Single(x => x.Name == "Suppressed");
        Assert.Equal(HttpStatusCode.OK, (await s.Put($"/api/v1/leads/{lead.Id}/stage", new { stageId = suppressedStage.Id })).StatusCode);
        Assert.Empty(await Audience("WhatsApp"));
        Assert.Contains("LeadExcluded", (await Eligibility(s, Id(0), "WhatsApp")).Reasons.Select(r => r.Code));
    }

    [RequiresMySqlFact]
    public async Task A_high_lead_score_never_overrides_missing_consent_or_a_suppression()
    {
        var (tenant, admin, _) = await W.NewTenantAsync("hs");
        var a = await ApiSession.LoginAsync(app, admin.Email, admin.Password);
        // rules that give 100 points to anything with a website
        var published = await a.Post("/api/v1/scoring/rules", new { rules = new[] { new { dimension = "WebsiteAvailability", target = "present", points = 100 } }, note = "max score" });
        Assert.Equal(HttpStatusCode.Created, published.StatusCode);

        var phone = Crm.Phone();
        var lead = await Crm.CreateLead(a, website: $"https://{Crm.Token()}.example", phone: phone);
        Assert.Equal(100, lead.Score);
        var contact = (await Crm.Ok<List<ContactDto>>(await a.Get($"/api/v1/leads/{lead.Id}/contacts"))).Single();

        var noConsent = await Eligibility(a, contact.Id, "WhatsApp");
        Assert.False(noConsent.IsEligible);
        Assert.Contains("NoConsent", noConsent.Reasons.Select(r => r.Code));
        Assert.Empty((await Crm.Page<EligibleContactDto>(await a.Get("/api/v1/audience/eligible-contacts?channel=WhatsApp"))).Items);

        await Crm.Ok<ConsentRecordDto>(await a.Post($"/api/v1/contacts/{contact.Id}/consent", Grant()));
        Assert.Single((await Crm.Page<EligibleContactDto>(await a.Get("/api/v1/audience/eligible-contacts?channel=WhatsApp"))).Items);
        await Crm.Ok<SuppressionDto>(await a.Post("/api/v1/suppression", new { channel = "WhatsApp", value = phone, reason = "OptOut" }));
        Assert.Empty((await Crm.Page<EligibleContactDto>(await a.Get("/api/v1/audience/eligible-contacts?channel=WhatsApp"))).Items);
        Assert.False((await Eligibility(a, contact.Id, "WhatsApp")).IsEligible);
        Assert.Equal(100, (await Crm.Ok<LeadDto>(await a.Get($"/api/v1/leads/{lead.Id}"))).Score);   // still the maximum score, still blocked
    }

    [RequiresMySqlFact]
    public async Task Granting_consent_for_a_suppressed_value_is_refused()
    {
        var s = await Login(W.ASalesManager);
        var mkt = await Login(W.AMarketing);
        var phone = Crm.Phone();
        var lead = await Crm.CreateLead(s, phone: phone);
        var contact = (await Crm.Ok<List<ContactDto>>(await s.Get($"/api/v1/leads/{lead.Id}/contacts"))).Single();
        await Crm.Ok<SuppressionDto>(await mkt.Post("/api/v1/suppression", new { channel = "Sms", value = phone, reason = "Blocked" }));
        Assert.Equal(HttpStatusCode.Conflict, (await s.Post($"/api/v1/contacts/{contact.Id}/consent", Grant(channel: "Sms"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await s.Post($"/api/v1/contacts/{contact.Id}/consent", Grant(channel: "WhatsApp"))).StatusCode);   // not suppressed on WhatsApp
        Assert.Equal(HttpStatusCode.Created, (await s.Post($"/api/v1/contacts/{contact.Id}/consent", Grant(channel: "Sms", status: "Revoked"))).StatusCode); // revoking is always allowed
    }
}
