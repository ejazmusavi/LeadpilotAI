using LeadPilot.Application.Crm;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Workflows;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Infrastructure.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Workflows;

/// <summary>Shared by the engine, the trigger service, the simulation and the settings page: tenant settings, time zone, and the facts conditions are evaluated against.</summary>
internal sealed class WorkflowSupport(CrmContext crm, IContactEligibilityService eligibility)
{
    public async Task<WorkflowTenantSettings> SettingsAsync(CancellationToken ct, bool track = false)
    {
        var tenantId = crm.TenantId;
        var s = await (track ? crm.Db.WorkflowTenantSettings : crm.Db.WorkflowTenantSettings.AsNoTracking()).FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if (s is not null) return s;
        var fresh = WorkflowTenantSettings.Create(tenantId, crm.Now);
        if (!track) return fresh;
        crm.Db.WorkflowTenantSettings.Add(fresh);
        try { await crm.Db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ResultFlow.IsDuplicate(ex))
        {
            crm.Db.Entry(fresh).State = EntityState.Detached;
            return await crm.Db.WorkflowTenantSettings.FirstAsync(x => x.TenantId == tenantId, ct);
        }
        return fresh;
    }

    public async Task<TimeZoneInfo> TimeZoneAsync(CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var id = await crm.Db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Timezone).FirstOrDefaultAsync(ct);
        try { return string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Utc : TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>Everything a condition may look at, read fresh (steps change the lead). Values are plain data; nothing here is ever evaluated as code.</summary>
    public async Task<Dictionary<string, object?>> FactsAsync(Guid leadId, Guid? contactId, Guid? conversationId, Guid? messageId, string eventType, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var facts = new Dictionary<string, object?> { ["event.type"] = eventType };
        var lead = await db.Leads.AsNoTracking().FirstOrDefaultAsync(l => l.Id == leadId && l.TenantId == tenantId, ct);
        if (lead is null) return facts;

        facts["lead.name"] = lead.Name; facts["lead.city"] = lead.City; facts["lead.province"] = lead.Province; facts["lead.country"] = lead.Country;
        facts["lead.score"] = (decimal)lead.Score; facts["lead.confidence"] = (decimal)lead.Confidence; facts["lead.has_owner"] = lead.OwnerUserId is not null;
        facts["lead.source"] = lead.PrimarySourceType.ToString();
        facts["lead.inactive_days"] = lead.LastActivityAtUtc is { } la ? (decimal)Math.Max(0, Math.Floor((now - la).TotalDays)) : 0m;
        facts["lead.age_days"] = (decimal)Math.Max(0, Math.Floor((now - lead.CreatedAtUtc).TotalDays));
        facts["lead.stage"] = await db.PipelineStages.AsNoTracking().Where(s => s.Id == lead.PipelineStageId && s.TenantId == tenantId).Select(s => s.Name).FirstOrDefaultAsync(ct);
        facts["lead.status"] = await db.LeadStatuses.AsNoTracking().Where(s => s.Id == lead.StatusId && s.TenantId == tenantId).Select(s => s.Name).FirstOrDefaultAsync(ct);
        facts["lead.tags"] = (IReadOnlyList<string>)await (from m in db.LeadTagMaps.AsNoTracking() join t in db.LeadTags.AsNoTracking() on m.TagId equals t.Id
                                                           where m.TenantId == tenantId && t.TenantId == tenantId && m.LeadId == leadId select t.Name).ToListAsync(ct);

        var contacts = await db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && c.LeadId == leadId && !c.IsDeleted).ToListAsync(ct);
        var contact = contacts.FirstOrDefault(c => c.Id == contactId) ?? contacts.Where(c => c.PhoneE164 != null).OrderByDescending(c => c.IsPrimary).FirstOrDefault() ?? contacts.OrderByDescending(c => c.IsPrimary).FirstOrDefault();
        facts["contact.has_phone"] = contacts.Any(c => c.PhoneE164 != null); facts["contact.has_email"] = contacts.Any(c => c.Email != null);
        facts["contact.whatsapp_marketing_eligible"] = contact?.PhoneE164 is not null && (await eligibility.CheckAsync(contact.Id, ConsentChannel.WhatsApp, ConsentPurpose.Marketing, ct)).IsEligible;

        var contactIds = contacts.Select(c => c.Id).ToList();
        var conv = conversationId is { } cid ? await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid && c.TenantId == tenantId, ct) : null;
        conv ??= await db.Conversations.AsNoTracking().Where(c => c.TenantId == tenantId && c.ContactId != null && contactIds.Contains(c.ContactId.Value)).OrderByDescending(c => c.LastMessageAtUtc).FirstOrDefaultAsync(ct);
        facts["conversation.exists"] = conv is not null; facts["conversation.ai_mode"] = conv?.AiMode.ToString(); facts["conversation.opted_out"] = conv?.OptedOutAtUtc is not null;
        if (messageId is { } mid) facts["message.text"] = await db.Messages.AsNoTracking().Where(m => m.Id == mid && m.TenantId == tenantId).Select(m => m.Body).FirstOrDefaultAsync(ct);

        var last = await (from r in db.CampaignRecipients.AsNoTracking() join camp in db.Campaigns.AsNoTracking() on r.CampaignId equals camp.Id
                          where r.TenantId == tenantId && camp.TenantId == tenantId && r.LeadId == leadId orderby r.CreatedAtUtc descending select camp.Name).FirstOrDefaultAsync(ct);
        facts["campaign.recipient_of_any"] = last is not null; facts["campaign.last_name"] = last;
        return facts;
    }

    public async Task<Contact?> ContactForAsync(Guid leadId, Guid? contactId, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var q = crm.Db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && c.LeadId == leadId && !c.IsDeleted);
        if (contactId is { } id) q = q.Where(c => c.Id == id);
        return await q.OrderByDescending(c => c.PhoneE164 != null).ThenByDescending(c => c.IsPrimary).FirstOrDefaultAsync(ct);
    }
}
