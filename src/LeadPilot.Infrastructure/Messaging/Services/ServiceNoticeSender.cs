using LeadPilot.Application.Contracts;
using LeadPilot.Application.Conversion;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Infrastructure.Services.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Messaging.Services;

/// <summary>
/// The ONLY way the conversion module (demo reminders, trial expiry notices) reaches a customer. It lives with the messaging module: nothing outside it references the send pipeline.
/// Policy, in this order:
/// 1. Eligibility is checked for the SERVICE purpose: an invalid or missing number, an archived or excluded lead, a suppressed or opted-out number, a removed contact all stop the notice.
/// 2. Without a recorded WhatsApp service consent the notice is only allowed when the customer wrote to us inside the service window (then the pipeline treats it as the service reply it is). A REVOKED consent always
///    stops it. Marketing consent is irrelevant here, and nothing is ever forced as "service" without consent.
/// 3. The Phase 5 pipeline then applies account state, new-contact restrictions, rate limits, warm-up and the idempotency key (same key = same message).
/// A refusal is returned as Skipped with a reason; the caller records it.
/// </summary>
internal sealed class ServiceNoticeSender(CrmContext crm, OutboundQueueService queue, IContactEligibilityService eligibility, ITenantSettingsService settings) : IServiceNoticeSender
{
    public async Task<ServiceNoticeOutcome> SendAsync(ServiceNoticeRequest r, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var db = crm.Db;
        var q = db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && c.LeadId == r.LeadId && !c.IsDeleted && c.PhoneE164 != null);
        if (r.ContactId is { } id) q = q.Where(c => c.Id == id);
        var contact = await q.OrderByDescending(c => c.IsPrimary).ThenBy(c => c.CreatedAtUtc).FirstOrDefaultAsync(ct);
        if (contact is null) return new(ServiceNoticeStatus.Skipped, null, "no_contact", "The lead has no contact with a phone number.");

        var decision = await eligibility.CheckAsync(contact.Id, ConsentChannel.WhatsApp, ConsentPurpose.Service, ct);
        var otherReasons = decision.Reasons.Where(x => x != EligibilityReason.NoConsent).ToList();
        if (otherReasons.Count > 0) return new(ServiceNoticeStatus.Skipped, null, "not_eligible", string.Join(" ", otherReasons.Select(EligibilityDecision.Explain)));

        var hasConsent = decision.IsEligible;
        if (!hasConsent)
        {
            var account = await db.ChannelAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == r.ChannelAccountId && a.TenantId == tenantId && !a.IsDeleted, ct);
            if (account is null) return new(ServiceNoticeStatus.Failed, null, "not_found", "The WhatsApp account does not exist.");
            var chatId = ChatIds.FromE164(contact.PhoneE164);
            var lastInbound = chatId is null ? null : await db.Conversations.AsNoTracking().Where(c => c.TenantId == tenantId && c.ChannelAccountId == account.Id && c.ExternalChatId == chatId).Select(c => c.LastInboundAtUtc).FirstOrDefaultAsync(ct);
            var window = OutboundPolicy.ParseWindow(await settings.GetValueAsync(OutboundPolicy.WindowSettingKey, ct));
            if (OutboundPolicy.PurposeFor(lastInbound, crm.Now, window) != MessagePurpose.Service)
                return new(ServiceNoticeStatus.Skipped, null, "no_service_consent", "There is no recorded WhatsApp service consent for this contact and the customer has not written to us within the service window.");
        }

        var res = await queue.QueueAsync(new QueueRequest(null, contact.Id, r.ChannelAccountId, r.Text, null, r.IdempotencyKey, false) { ForcedPurpose = hasConsent ? MessagePurpose.Service : null, TextIsRendered = true }, ct);
        if (res.IsSuccess) return new(ServiceNoticeStatus.Queued, res.Value.Id, null, null);
        return res.Error.Code is "not_eligible" or "suppressed" or "opted_out"
            ? new(ServiceNoticeStatus.Skipped, null, res.Error.Code, res.Error.Message)
            : new(ServiceNoticeStatus.Failed, null, res.Error.Code, res.Error.Message);
    }
}
