using LeadPilot.Application.Crm;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Messaging.Services;

/// <summary>
/// The ONLY way a workflow reaches a customer. It lives with the messaging module (workflows may not reference the send pipeline or the gateway): eligibility (consent, suppression, opt-out, archived or
/// excluded lead, valid number) is re-checked at execution time, then the message goes through <see cref="OutboundQueueService"/> as MARKETING (consent mandatory) with the step's idempotency key, so
/// account state, limits, warm-up, opt-out and the pre-send eligibility check of the send runner all apply. There is no overload that sends free text or skips a check.
/// </summary>
internal sealed class WorkflowMessageSender(CrmContext crm, OutboundQueueService queue, IContactEligibilityService eligibility) : IWorkflowMessageSender
{
    public async Task<WorkflowSendOutcome> SendAsync(WorkflowSendRequest r, CancellationToken ct = default)
    {
        var contact = await ResolveContactAsync(r.LeadId, r.ContactId, ct);
        if (contact is null) return new(WorkflowSendStatus.Blocked, null, "no_contact", "The lead has no contact with a phone number.");
        var decision = await eligibility.CheckAsync(contact.Id, ConsentChannel.WhatsApp, ConsentPurpose.Marketing, ct);
        if (!decision.IsEligible) return new(WorkflowSendStatus.Blocked, null, "not_eligible", "Not allowed to message this contact: " + string.Join(" ", decision.Reasons.Select(EligibilityDecision.Explain)));

        // the text is filled from the lead here (name, city, country, business name: the pipeline's own renderer only knows name and phone), with sanitised values
        var tenantId = crm.TenantId;
        var template = await crm.Db.MessageTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == r.TemplateId && t.TenantId == tenantId, ct);
        if (template is null) return new(WorkflowSendStatus.Failed, null, "not_found", "The message template does not exist.");
        if (!template.IsActive) return new(WorkflowSendStatus.Failed, null, "validation", "That template is switched off.");
        var lead = await crm.Db.Leads.AsNoTracking().FirstAsync(l => l.Id == r.LeadId && l.TenantId == tenantId, ct);
        string text;
        try { text = Workflows.WorkflowText.Render(template.Body, lead, contact); }
        catch (TemplateRenderException ex) { return new(WorkflowSendStatus.Failed, null, "validation", "The template could not be filled in: " + ex.Message); }

        var res = await queue.QueueAsync(new QueueRequest(null, contact.Id, r.ChannelAccountId, text, r.TemplateId, r.IdempotencyKey, false) { ForcedPurpose = MessagePurpose.Marketing, TextIsRendered = true }, ct);
        if (res.IsSuccess) return new(WorkflowSendStatus.Queued, res.Value.Id, null, null);
        return res.Error.Code is "not_eligible" or "suppressed" or "opted_out"
            ? new(WorkflowSendStatus.Blocked, null, res.Error.Code, res.Error.Message)
            : new(WorkflowSendStatus.Failed, null, res.Error.Code, res.Error.Message);
    }

    public async Task<WorkflowSentMessage?> FindAsync(string idempotencyKey, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var m = await crm.Db.Messages.AsNoTracking().Where(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey).Select(x => new { x.Id, x.Status }).FirstOrDefaultAsync(ct);
        return m is null ? null : new WorkflowSentMessage(m.Id, m.Status == MessageStatus.Failed);
    }

    public async Task<(bool Allowed, string Reason)> CheckAsync(Guid leadId, Guid? contactId, CancellationToken ct = default)
    {
        var contact = await ResolveContactAsync(leadId, contactId, ct);
        if (contact is null) return (false, "The lead has no contact with a phone number.");
        var d = await eligibility.CheckAsync(contact.Id, ConsentChannel.WhatsApp, ConsentPurpose.Marketing, ct);
        return d.IsEligible ? (true, "eligible") : (false, string.Join(" ", d.Reasons.Select(EligibilityDecision.Explain)));
    }

    private async Task<Contact?> ResolveContactAsync(Guid leadId, Guid? contactId, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var q = crm.Db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && c.LeadId == leadId && !c.IsDeleted && c.PhoneE164 != null);
        if (contactId is { } id) q = q.Where(c => c.Id == id);
        return await q.OrderByDescending(c => c.IsPrimary).ThenBy(c => c.CreatedAtUtc).FirstOrDefaultAsync(ct);
    }
}
