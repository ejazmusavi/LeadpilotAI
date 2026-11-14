using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Messaging.Services;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Agents;

/// <summary>
/// The one implementation of "hand this conversation to a person" (Phase 8): used by the AI agent's TransferToHuman tool and by the workflow engine's Escalate step.
/// The conversation switches to human control, the lead's owner is assigned when nobody is, the handover is recorded and an AI reply that was still waiting is withdrawn.
/// </summary>
internal static class HandoffActions
{
    public static async Task<(bool Assigned, Guid? Owner)> EscalateAsync(CrmContext crm, Conversation conv, Guid? leadId, string reason, string? summary, ActorKind actor, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var from = conv.AiMode.ToString();
        conv.Escalate(reason, crm.Now);
        Guid? assigned = null;
        if (conv.AssignedUserId is null && leadId is { } lid)
        {
            var ownerId = await crm.Db.Leads.AsNoTracking().Where(l => l.Id == lid && l.TenantId == tenantId && !l.IsArchived).Select(l => l.OwnerUserId).FirstOrDefaultAsync(ct);
            if (ownerId is { } owner && await crm.IsActiveMemberAsync(owner, ct))
            { conv.Assign(owner, crm.Now); crm.Db.ConversationAssignments.Add(ConversationAssignment.Create(tenantId, conv.Id, owner, null, crm.Now)); assigned = owner; }
        }
        crm.Db.HandoffEvents.Add(HandoffEvent.Create(tenantId, conv.Id, HandoffKind.Escalated, actor, null, assigned, reason, from, AiMode.HumanControl.ToString(), crm.Now));
        if (summary is not null) crm.Db.ConversationNotes.Add(ConversationNote.Create(tenantId, conv.Id, (actor == ActorKind.Ai ? "[AI handover summary] " : "[Workflow handover] ") + summary, null, actor, crm.Now));
        await crm.Db.SaveChangesAsync(ct);
        await ConversationService.WithdrawQueuedAiMessagesAsync(crm, conv.Id, "ai_withdrawn", "The conversation was handed to a person before this reply was sent.", ct);
        return (assigned is not null, assigned);
    }
}
