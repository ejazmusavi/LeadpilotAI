using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Agents;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Messaging.Services;

/// <summary>
/// The ONLY way an AI reply reaches a customer. It lives with the messaging module (the AI module may not reference the send pipeline) and goes through <see cref="OutboundQueueService"/>, so suppression,
/// opt-out, account state, rate limits, warm-up and the per-interaction idempotency key all apply. It can only REPLY: it needs the customer's inbound message and an open service window; it cannot open a
/// conversation or send marketing. Every refusal is a plain reason that is stored on the AI interaction.
/// </summary>
internal sealed class AiReplySender(CrmContext crm, OutboundQueueService queue, ITenantSettingsService settings) : IAiReplySender
{
    public async Task<AiSendOutcome> DeliverAsync(AiSendRequest r, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        if (string.IsNullOrWhiteSpace(r.Text)) return Blocked("empty_reply");

        var tenantSettings = await db.AgentTenantSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        if (tenantSettings is not { AiRepliesAllowed: true }) return Blocked("ai_replies_not_enabled_for_workspace");

        var interaction = await db.AiInteractions.AsNoTracking().FirstOrDefaultAsync(i => i.Id == r.InteractionId && i.TenantId == tenantId && i.ConversationId == r.ConversationId && i.TriggerMessageId == r.TriggerMessageId, ct);
        if (interaction is null) return Blocked("interaction_not_found");
        if (interaction.SentMessageId is { } already) return new AiSendOutcome(true, null, already);
        if (interaction.Decision != AiDecision.Draft) return Blocked("not_a_reply");
        var agent = interaction.AgentId is { } aid ? await db.SalesAgents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == aid && a.TenantId == tenantId, ct) : null;
        if (agent is not { IsActive: true, AutoReplyEnabled: true }) return Blocked("agent_auto_reply_off");

        // fresh read: the generation may have taken seconds, and a person may have acted since
        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.ConversationId && c.TenantId == tenantId, ct);
        if (conv is null) return Blocked("conversation_not_found");
        if (conv.AiMode != AiMode.Active) return Blocked(conv.AiMode == AiMode.HumanControl ? "human_control" : "ai_paused");
        if (conv.AiEpoch != r.AiEpoch) return Blocked("control_changed_during_generation");
        if (conv.OptedOutAtUtc is not null) return Blocked("opted_out");

        var trigger = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == r.TriggerMessageId && m.TenantId == tenantId && m.ConversationId == r.ConversationId && m.Direction == MessageDirection.Inbound, ct);
        if (trigger is null) return Blocked("no_customer_message");
        if (await db.Messages.AnyAsync(m => m.TenantId == tenantId && m.ConversationId == r.ConversationId && m.Direction == MessageDirection.Inbound && (m.OccurredAtUtc > trigger.OccurredAtUtc || (m.OccurredAtUtc == trigger.OccurredAtUtc && m.CreatedAtUtc > trigger.CreatedAtUtc)), ct))
            return Blocked("newer_customer_message");

        var window = OutboundPolicy.ParseWindow(await settings.GetValueAsync(OutboundPolicy.WindowSettingKey, ct));
        if (!conv.IsInsideServiceWindow(now, window)) return Blocked("outside_service_window");

        var dayAgo = now.AddHours(-24);
        var recent = await db.Messages.CountAsync(m => m.TenantId == tenantId && m.ConversationId == r.ConversationId && m.IsAiGenerated && m.Status != MessageStatus.Failed && m.CreatedAtUtc >= dayAgo, ct);
        if (recent >= tenantSettings.MaxAutoRepliesPerConversationPerDay) return Blocked("daily_ai_reply_cap_reached");

        var queued = await queue.QueueAsync(new QueueRequest(r.ConversationId, null, null, r.Text, null, "ai:" + r.InteractionId.ToString("N"), true)
            { ForcedPurpose = MessagePurpose.Service, AiInteractionId = r.InteractionId, AiEpoch = r.AiEpoch }, ct);
        return queued.IsSuccess ? new AiSendOutcome(true, null, queued.Value.Id) : Blocked(queued.Error.Code);
    }

    private static AiSendOutcome Blocked(string reason) => new(false, reason, null);
}
