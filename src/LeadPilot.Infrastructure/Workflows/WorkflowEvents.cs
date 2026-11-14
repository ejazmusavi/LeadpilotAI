using System.Text.Json;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Services.Crm;

namespace LeadPilot.Infrastructure.Workflows;

/// <summary>
/// Publishes the workflow domain events through the transactional outbox (the same relay and broker as every other event): the row is written in the caller's unit of work, so it exists if and only if
/// the business change was saved. Ids only; consumers re-read inside the tenant scope.
/// </summary>
internal static class WorkflowEvents
{
    public static void Emit(CrmContext crm, string eventType, Guid leadId, Guid? contactId = null, Guid? conversationId = null, Guid? messageId = null, string? detail = null, string? correlationId = null) =>
        crm.Db.OutboxMessages.Add(OutboxMessage.Create(crm.TenantId, eventType, JsonSerializer.Serialize(new WorkflowEventPayload(leadId, contactId, conversationId, messageId, detail)),
            correlationId ?? Guid.NewGuid().ToString("N"), leadId.ToString(), crm.Now));
}
