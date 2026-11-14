using LeadPilot.Application.Common;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using LeadPilot.Application.Crm;
using LeadPilot.Domain.Crm;
using LeadPilot.Application.Agents;
using LeadPilot.Domain.Agents;

namespace LeadPilot.Infrastructure.Messaging.Services;

internal static class MessageMapper
{
    public static MessageDto ToDto(Message m, IReadOnlyList<MessageAttachment> attachments, string? createdByName) =>
        new(m.Id, m.Direction, m.Purpose, m.Status, m.Body, m.OccurredAtUtc, m.SentAtUtc, m.DeliveredAtUtc, m.ReadAtUtc, m.FailureCode, m.FailureMessage, m.ExternalMessageId, m.CorrelationId, createdByName,
            attachments.Select(a => new AttachmentDto(a.Id, a.FileName, a.MimeType, a.SizeBytes, a.Status, a.RejectionReason)).ToList());
}

public sealed class ConversationService(CrmContext crm, OutboundQueueService queue, GatewayConnections connections, Application.Messaging.IWhatsAppGateway gateway, ILogger<ConversationService> logger) : IConversationService
{
    public async Task<PagedResult<ConversationListItemDto>> ListAsync(ConversationQuery q, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(q.Page, q.PageSize);
        var query = db.Conversations.AsNoTracking().Where(c => c.TenantId == tenantId && c.LastMessageAtUtc != null);
        if (q.AccountId is { } a) query = query.Where(c => c.ChannelAccountId == a);
        if (q.Status is { } s) query = query.Where(c => c.Status == s);
        if (q.UnreadOnly) query = query.Where(c => c.UnreadCount > 0);
        if (q.LinkedOnly) query = query.Where(c => c.ContactId != null);
        if (q.UnlinkedOnly) query = query.Where(c => c.ContactId == null);
        if (q.AssignedUserId is { } u) query = query.Where(c => c.AssignedUserId == u);
        if (q.ContactId is { } ct2) query = query.Where(c => c.ContactId == ct2);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var t = q.Search.Trim();
            query = query.Where(c => (c.PeerName != null && c.PeerName.Contains(t)) || (c.PeerPhoneE164 != null && c.PeerPhoneE164.Contains(t)) || (c.LastMessagePreview != null && c.LastMessagePreview.Contains(t))
                || db.Contacts.Any(ct3 => ct3.Id == c.ContactId && ct3.TenantId == tenantId && ct3.Name != null && ct3.Name.Contains(t)));
        }
        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(c => c.LastMessageAtUtc).ThenBy(c => c.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<ConversationListItemDto>(await MapAsync(rows, ct), page, size, total);
    }

    public async Task<Result<ConversationDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var conv = await Load(id, ct);
        return conv is null ? Result.Failure<ConversationDto>(Error.NotFound("Conversation")) : Result.Success(await DetailAsync(conv, ct));
    }

    public async Task<Result<PagedResult<MessageDto>>> MessagesAsync(Guid id, int page, int pageSize, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var (p, size) = CrmFlow.Page(page, pageSize, 200);
        if (!await db.Conversations.AnyAsync(c => c.Id == id && c.TenantId == tenantId, ct)) return Result.Failure<PagedResult<MessageDto>>(Error.NotFound("Conversation"));
        var q = db.Messages.AsNoTracking().Where(m => m.TenantId == tenantId && m.ConversationId == id);
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(m => m.OccurredAtUtc).ThenByDescending(m => m.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        rows.Reverse();
        var ids = rows.Select(m => m.Id).ToList();
        var attachments = await db.MessageAttachments.AsNoTracking().Where(a => a.TenantId == tenantId && ids.Contains(a.MessageId)).ToListAsync(ct);
        var names = await crm.UserNamesAsync(rows.Select(m => m.CreatedByUserId), ct);
        return Result.Success(new PagedResult<MessageDto>(rows.Select(m => MessageMapper.ToDto(m, attachments.Where(a => a.MessageId == m.Id).ToList(), m.CreatedByUserId is { } u ? names.GetValueOrDefault(u) : null)).ToList(), p, size, total));
    }

    public async Task<Result<MessageDto>> ReplyAsync(Guid id, ReplyRequest request, CancellationToken ct = default)
    {
        var r = await queue.QueueAsync(new QueueRequest(id, null, null, request.Text, request.TemplateId, request.IdempotencyKey, true), ct);
        if (r.IsFailure) return Result.Failure<MessageDto>(r.Error);
        // a person answering means a person is in control: the AI must not talk over them
        var conv = await crm.Db.Conversations.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == crm.TenantId, ct);
        if (conv is { AiMode: AiMode.Active })
        {
            conv.Escalate("a person replied", crm.Now);
            Event(id, HandoffKind.AutoTakeover, "A person sent a reply", null, AiMode.Active.ToString(), AiMode.HumanControl.ToString());
            await crm.Db.SaveChangesAsync(ct);
            await WithdrawQueuedAiMessagesAsync(id, ct);
        }
        var names = await crm.UserNamesAsync([r.Value.CreatedByUserId], ct);
        return Result.Success(MessageMapper.ToDto(r.Value, [], r.Value.CreatedByUserId is { } u ? names.GetValueOrDefault(u) : null));
    }

    public async Task<Result> MarkReadAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var conv = await crm.Db.Conversations.FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct);
        if (conv is null) return Result.Failure(Error.NotFound("Conversation"));
        if (conv.UnreadCount > 0)
        {
            var loaded = await connections.LoadAsync(conv.ChannelAccountId, tracked: false, ct);
            if (loaded is not null && loaded.Value.Account.ConnectionStatus == ConnectionStatus.Working)
            {
                // tells WhatsApp the chat was read (the sender sees the blue ticks). Best effort: local state is updated either way.
                try { await gateway.MarkAsReadAsync(loaded.Value.Connection, conv.ExternalChatId, [], ct); }
                catch (GatewayException ex) { logger.LogWarning("Marking conversation {ConversationId} as read at the gateway failed: {Kind}", id, ex.Kind); }
            }
            conv.MarkRead(crm.Now);
            await crm.Db.SaveChangesAsync(ct);
        }
        return Result.Success();
    }

    public Task<Result<ConversationDto>> SetStatusAsync(Guid id, ConversationStatus status, CancellationToken ct = default) =>
        Mutate(id, c =>
        {
            var from = c.Status; c.SetStatus(status, crm.Now);
            Event(c.Id, HandoffKind.StatusChanged, null, null, from.ToString(), status.ToString());
            crm.Record("Conversation.StatusChanged", "Conversation", id, $"Conversation set to {status}.");
        }, ct);

    public Task<Result<ConversationDto>> SetAiModeAsync(Guid id, AiMode mode, CancellationToken ct = default) =>
        mode switch { AiMode.Active => ResumeAiAsync(id, ct), AiMode.Paused => PauseAiAsync(id, null, ct), _ => TakeOverAsync(id, null, ct) };

    public async Task<Result<ConversationDto>> TakeOverAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        var r = await Mutate(id, c =>
        {
            var from = c.AiMode;
            c.Escalate(string.IsNullOrWhiteSpace(reason) ? "taken over by staff" : reason.Trim(), crm.Now);
            if (c.AssignedUserId is null && crm.UserId is { } me) { c.Assign(me, crm.Now); crm.Db.ConversationAssignments.Add(ConversationAssignment.Create(crm.TenantId, c.Id, me, me, crm.Now)); }
            Event(c.Id, HandoffKind.Takeover, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), crm.UserId is { } u ? u : null, from.ToString(), AiMode.HumanControl.ToString());
            crm.Record("Conversation.TakenOver", "Conversation", id, "A person took over the conversation; the AI no longer answers it.");
        }, ct);
        if (r.IsSuccess) await WithdrawQueuedAiMessagesAsync(id, ct);
        return r;
    }

    public async Task<Result<ConversationDto>> PauseAiAsync(Guid id, string? reason, CancellationToken ct = default)
    {
        var r = await Mutate(id, c =>
        {
            var from = c.AiMode; c.SetAiMode(AiMode.Paused, crm.Now);
            Event(c.Id, HandoffKind.AiPaused, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), null, from.ToString(), AiMode.Paused.ToString());
            crm.Record("Conversation.AiModeChanged", "Conversation", id, "AI paused for this conversation.");
        }, ct);
        if (r.IsSuccess) await WithdrawQueuedAiMessagesAsync(id, ct);
        return r;
    }

    public async Task<Result<ConversationDto>> ResumeAiAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var existing = await crm.Db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.TenantId == tenantId, ct);
        if (existing is null) return Result.Failure<ConversationDto>(Error.NotFound("Conversation"));
        if (existing.OptedOutAtUtc is not null) return Result.Failure<ConversationDto>(Error.Conflict("This customer opted out of messages, so the AI cannot be switched on for this conversation."));
        return await Mutate(id, c =>
        {
            var from = c.AiMode; c.SetAiMode(AiMode.Active, crm.Now);
            Event(c.Id, HandoffKind.AiResumed, null, null, from.ToString(), AiMode.Active.ToString());
            crm.Record("Conversation.AiModeChanged", "Conversation", id, "AI resumed for this conversation.");
        }, ct);
    }

    public async Task<Result<ConversationDto>> AssignAsync(Guid id, Guid? userId, CancellationToken ct = default)
    {
        if (userId is { } u && !await crm.IsActiveMemberAsync(u, ct)) return Result.Failure<ConversationDto>(Error.Validation("That user is not an active member of this workspace."));
        return await Mutate(id, c =>
        {
            c.Assign(userId, crm.Now);
            crm.Db.ConversationAssignments.Add(ConversationAssignment.Create(crm.TenantId, id, userId, crm.UserId, crm.Now));
            crm.Db.HandoffEvents.Add(HandoffEvent.Create(crm.TenantId, id, userId is null ? HandoffKind.Unassigned : HandoffKind.Assigned, ActorKind.User, crm.UserId, userId, null, null, null, crm.Now));
            crm.Record("Conversation.Assigned", "Conversation", id, userId is null ? "Conversation unassigned." : "Conversation assigned.");
        }, ct);
    }

    public async Task<Result<ConversationNoteDto>> AddNoteAsync(Guid id, string body, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        if (!await crm.Db.Conversations.AnyAsync(c => c.Id == id && c.TenantId == tenantId, ct)) return Result.Failure<ConversationNoteDto>(Error.NotFound("Conversation"));
        try
        {
            var note = ConversationNote.Create(tenantId, id, body, crm.UserId, ActorKind.User, crm.Now);
            crm.Db.ConversationNotes.Add(note);
            crm.Record("Conversation.NoteAdded", "Conversation", id, "Internal note added.");
            await crm.Db.SaveChangesAsync(ct);
            var names = await crm.UserNamesAsync([note.AuthorUserId], ct);
            return Result.Success(new ConversationNoteDto(note.Id, note.Body, note.AuthorUserId is { } a ? names.GetValueOrDefault(a) : null, note.Author, note.CreatedAtUtc));
        }
        catch (LeadPilot.Domain.Common.DomainException ex) { return Result.Failure<ConversationNoteDto>(Error.Validation(ex.Message)); }
    }

    public async Task<Result<IReadOnlyList<ConversationNoteDto>>> NotesAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        if (!await crm.Db.Conversations.AnyAsync(c => c.Id == id && c.TenantId == tenantId, ct)) return Result.Failure<IReadOnlyList<ConversationNoteDto>>(Error.NotFound("Conversation"));
        var rows = await crm.Db.ConversationNotes.AsNoTracking().Where(n => n.TenantId == tenantId && n.ConversationId == id).OrderByDescending(n => n.CreatedAtUtc).Take(200).ToListAsync(ct);
        var names = await crm.UserNamesAsync(rows.Select(r => r.AuthorUserId), ct);
        return Result.Success<IReadOnlyList<ConversationNoteDto>>(rows.Select(n => new ConversationNoteDto(n.Id, n.Body, n.AuthorUserId is { } a ? names.GetValueOrDefault(a) : null, n.Author, n.CreatedAtUtc)).ToList());
    }

    public async Task<Result<IReadOnlyList<HandoffEventDto>>> HistoryAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        if (!await crm.Db.Conversations.AnyAsync(c => c.Id == id && c.TenantId == tenantId, ct)) return Result.Failure<IReadOnlyList<HandoffEventDto>>(Error.NotFound("Conversation"));
        var rows = await crm.Db.HandoffEvents.AsNoTracking().Where(n => n.TenantId == tenantId && n.ConversationId == id).OrderByDescending(n => n.OccurredAtUtc).Take(200).ToListAsync(ct);
        var names = await crm.UserNamesAsync(rows.SelectMany(r => new[] { r.ActorUserId, r.TargetUserId }), ct);
        return Result.Success<IReadOnlyList<HandoffEventDto>>(rows.Select(e => new HandoffEventDto(e.Id, e.Kind, e.Actor, e.ActorUserId is { } a ? names.GetValueOrDefault(a) : null, e.TargetUserId is { } t ? names.GetValueOrDefault(t) : null, e.Reason, e.FromState, e.ToState, e.OccurredAtUtc)).ToList());
    }

    private void Event(Guid conversationId, HandoffKind kind, string? reason, Guid? target, string? from, string? to) =>
        crm.Db.HandoffEvents.Add(HandoffEvent.Create(crm.TenantId, conversationId, kind, ActorKind.User, crm.UserId, target, reason, from, to, crm.Now));

    /// <summary>An AI reply that was queued but not yet picked up by the sender must not leave after a person took over or the AI was paused. (The send runner re-checks as well; a message already Sending finishes.)</summary>
    internal static async Task WithdrawQueuedAiMessagesAsync(CrmContext crm, Guid conversationId, string code, string text, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var now = crm.Now;
        await crm.Db.Messages.Where(m => m.TenantId == tenantId && m.ConversationId == conversationId && m.IsAiGenerated && m.Status == MessageStatus.Queued)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.Status, MessageStatus.Failed).SetProperty(m => m.FailureCode, code).SetProperty(m => m.FailureMessage, text).SetProperty(m => m.FailedAtUtc, now)
                .SetProperty(m => m.NextAttemptAtUtc, (DateTime?)null).SetProperty(m => m.Version, m => m.Version + 1), ct);
    }

    private Task WithdrawQueuedAiMessagesAsync(Guid conversationId, CancellationToken ct) =>
        WithdrawQueuedAiMessagesAsync(crm, conversationId, "ai_withdrawn", "A person took control (or the AI was paused) before this AI reply was sent.", ct);

    // ------------------------------------------------------------------

    private async Task<Result<ConversationDto>> Mutate(Guid id, Action<Conversation> change, CancellationToken ct)
    {
        var conv = await Load(id, ct, tracked: true);
        if (conv is null) return Result.Failure<ConversationDto>(Error.NotFound("Conversation"));
        change(conv);
        await crm.Db.SaveChangesAsync(ct);
        return Result.Success(await DetailAsync(conv, ct));
    }

    private Task<Conversation?> Load(Guid id, CancellationToken ct, bool tracked = false)
    {
        var tenantId = crm.TenantId;
        var q = crm.Db.Conversations.Where(c => c.Id == id && c.TenantId == tenantId);
        return (tracked ? q : q.AsNoTracking()).FirstOrDefaultAsync(ct);
    }

    private async Task<ConversationDto> DetailAsync(Conversation conv, CancellationToken ct)
    {
        var summary = (await MapAsync([conv], ct))[0];
        var state = await queue.ReplyStateAsync(conv, ct);
        return new ConversationDto(summary, conv.LastInboundAtUtc, conv.LastOutboundAtUtc, state.CanReply, state.Reason, state.Purpose, (int)state.Window.TotalHours, state.InsideWindow, conv.EscalationReason, conv.EscalatedAtUtc, conv.OptedOutAtUtc is not null);
    }

    private async Task<List<ConversationListItemDto>> MapAsync(IReadOnlyList<Conversation> rows, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var accountIds = rows.Select(r => r.ChannelAccountId).Distinct().ToList();
        var contactIds = rows.Where(r => r.ContactId != null).Select(r => r.ContactId!.Value).Distinct().ToList();
        var accounts = await db.ChannelAccounts.AsNoTracking().Where(a => a.TenantId == tenantId && accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.DisplayName, ct);
        var contacts = await db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && contactIds.Contains(c.Id)).Select(c => new { c.Id, c.Name, c.LeadId }).ToDictionaryAsync(c => c.Id, ct);
        var leadIds = contacts.Values.Select(c => c.LeadId).Distinct().ToList();
        var leads = await db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && leadIds.Contains(l.Id)).Select(l => new { l.Id, l.Name }).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var users = await crm.UserNamesAsync(rows.Select(r => r.AssignedUserId), ct);
        return rows.Select(r =>
        {
            var contact = r.ContactId is { } cid && contacts.TryGetValue(cid, out var c) ? c : null;
            return new ConversationListItemDto(r.Id, r.ChannelAccountId, accounts.GetValueOrDefault(r.ChannelAccountId) ?? "(removed account)", r.PeerPhoneE164, r.PeerName, r.ContactId, contact?.Name,
                contact?.LeadId, contact is not null ? leads.GetValueOrDefault(contact.LeadId) : null, r.Status, r.AiMode, r.AssignedUserId, r.AssignedUserId is { } u ? users.GetValueOrDefault(u) : null,
                r.LastMessageAtUtc, r.LastMessagePreview, r.UnreadCount);
        }).ToList();
    }
}

public sealed class OutboundMessageService(CrmContext crm, OutboundQueueService queue) : IOutboundMessageService
{
    public async Task<Result<MessageDto>> SendToContactAsync(SendMessageRequest request, CancellationToken ct = default)
    {
        var r = await queue.QueueAsync(new QueueRequest(null, request.ContactId, request.ChannelAccountId, request.Text, request.TemplateId, request.IdempotencyKey, false), ct);
        if (r.IsFailure) return Result.Failure<MessageDto>(r.Error);
        var names = await crm.UserNamesAsync([r.Value.CreatedByUserId], ct);
        return Result.Success(MessageMapper.ToDto(r.Value, [], r.Value.CreatedByUserId is { } u ? names.GetValueOrDefault(u) : null));
    }

    public async Task<Result<MessageDto>> GetMessageAsync(Guid messageId, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var m = await crm.Db.Messages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == messageId && x.TenantId == tenantId, ct);
        if (m is null) return Result.Failure<MessageDto>(Error.NotFound("Message"));
        var attachments = await crm.Db.MessageAttachments.AsNoTracking().Where(a => a.TenantId == tenantId && a.MessageId == messageId).ToListAsync(ct);
        var names = await crm.UserNamesAsync([m.CreatedByUserId], ct);
        return Result.Success(MessageMapper.ToDto(m, attachments, m.CreatedByUserId is { } u ? names.GetValueOrDefault(u) : null));
    }
}

public sealed class MessageTemplateService(CrmContext crm, ILogger<MessageTemplateService> logger) : IMessageTemplateService
{
    public async Task<IReadOnlyList<TemplateDto>> ListAsync(bool activeOnly, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var q = crm.Db.MessageTemplates.AsNoTracking().Where(t => t.TenantId == tenantId);
        if (activeOnly) q = q.Where(t => t.IsActive);
        return (await q.OrderBy(t => t.Name).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<Result<TemplateDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var t = await crm.Db.MessageTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        return t is null ? Result.Failure<TemplateDto>(Error.NotFound("Message template")) : Result.Success(ToDto(t));
    }

    public Task<Result<TemplateDto>> CreateAsync(SaveTemplateRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            if (Validate(r) is { } bad) return Result.Failure<TemplateDto>(bad);
            var t = MessageTemplate.Create(crm.TenantId, r.Name, r.Body, r.Category, crm.Actor, crm.Now);
            crm.Db.MessageTemplates.Add(t);
            crm.Record("Template.Created", "MessageTemplate", t.Id, $"Message template '{t.Name}' created.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(ToDto(t));
        }, logger);

    public Task<Result<TemplateDto>> UpdateAsync(Guid id, SaveTemplateRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var t = await crm.Db.MessageTemplates.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            if (t is null) return Result.Failure<TemplateDto>(Error.NotFound("Message template"));
            if (Validate(r) is { } bad) return Result.Failure<TemplateDto>(bad);
            t.Update(r.Name, r.Body, r.Category, crm.Actor, crm.Now);
            crm.Record("Template.Updated", "MessageTemplate", id, $"Message template '{t.Name}' updated.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(ToDto(t));
        }, logger);

    public Task<Result<TemplateDto>> SetActiveAsync(Guid id, bool active, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var t = await crm.Db.MessageTemplates.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            if (t is null) return Result.Failure<TemplateDto>(Error.NotFound("Message template"));
            t.SetActive(active, crm.Now);
            crm.Record("Template.Updated", "MessageTemplate", id, $"Message template '{t.Name}' switched {(active ? "on" : "off")}.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(ToDto(t));
        }, logger);

    public Task<Result> DeleteAsync(Guid id, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var t = await crm.Db.MessageTemplates.FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
            if (t is null) return Result.Failure(Error.NotFound("Message template"));
            if (await crm.Db.Messages.AnyAsync(m => m.TenantId == tenantId && m.TemplateId == id, ct))
                return Result.Failure(Error.Conflict("Messages were sent with this template, so it cannot be deleted. Switch it off instead."));
            crm.Db.MessageTemplates.Remove(t);
            crm.Record("Template.Deleted", "MessageTemplate", id, $"Message template '{t.Name}' deleted.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success();
        }, logger);

    public async Task<Result<TemplatePreviewDto>> PreviewAsync(Guid id, Guid? contactId, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var t = await crm.Db.MessageTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.TenantId == tenantId, ct);
        if (t is null) return Result.Failure<TemplatePreviewDto>(Error.NotFound("Message template"));
        Contact? contact = null;
        if (contactId is { } cid)
        {
            contact = await crm.Db.Contacts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid && c.TenantId == tenantId, ct);
            if (contact is null) return Result.Failure<TemplatePreviewDto>(Error.NotFound("Contact"));
        }
        var values = contact is null
            ? new Dictionary<string, string> { ["name"] = "Sample Customer", ["first_name"] = "Sample", ["phone"] = "+10000000000", ["business_name"] = "Your business" }
            : OutboundQueueService.TemplateValues(contact.Name, contact.PhoneE164);
        try { return Result.Success(new TemplatePreviewDto(TemplateRenderer.Render(t.Body, values), TemplateRenderer.VariablesIn(t.Body))); }
        catch (TemplateRenderException ex) { return Result.Failure<TemplatePreviewDto>(Error.Validation("This contact has no value for: " + ex.Message)); }
    }

    private static Error? Validate(SaveTemplateRequest r)
    {
        if (!Enum.IsDefined(r.Category)) return Error.Validation("Choose a category.");
        var unknown = TemplateRenderer.UnknownVariables(r.Body ?? string.Empty);
        return unknown.Count == 0 ? null : Error.Validation($"Unknown placeholder(s): {string.Join(", ", unknown.Select(u => "{{" + u + "}}"))}. Allowed: {string.Join(", ", TemplateRenderer.KnownVariables.Select(u => "{{" + u + "}}"))}.");
    }

    private static TemplateDto ToDto(MessageTemplate t) => new(t.Id, t.Name, t.Body, t.Category, t.IsActive, TemplateRenderer.VariablesIn(t.Body), t.CreatedAtUtc);
}

public sealed class WebhookEventService(CrmContext crm, IEventPublisher publisher) : IWebhookEventService
{
    public async Task<PagedResult<WebhookEventDto>> ListAsync(WebhookEventQuery q, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(q.Page, q.PageSize);
        var query = db.WebhookEvents.AsNoTracking().Where(w => w.TenantId == tenantId);
        if (q.AccountId is { } a) query = query.Where(w => w.ChannelAccountId == a);
        if (q.Status is { } s) query = query.Where(w => w.Status == s);
        if (!string.IsNullOrWhiteSpace(q.EventType)) { var t = q.EventType.Trim(); query = query.Where(w => w.EventType == t); }
        var total = await query.LongCountAsync(ct);
        var rows = await query.OrderByDescending(w => w.ReceivedAtUtc).ThenBy(w => w.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var ids = rows.Select(r => r.ChannelAccountId).Distinct().ToList();
        var names = await db.ChannelAccounts.AsNoTracking().Where(x => x.TenantId == tenantId && ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        return new PagedResult<WebhookEventDto>(rows.Select(w => new WebhookEventDto(w.Id, w.ChannelAccountId, names.GetValueOrDefault(w.ChannelAccountId) ?? "(removed account)", w.ExternalEventId, w.EventType, w.Status, w.Note, w.CorrelationId, w.ReceivedAtUtc, w.ProcessedAtUtc)).ToList(), page, size, total);
    }

    public async Task<WebhookEventStatsDto> StatsAsync(CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var by = await db.WebhookEvents.AsNoTracking().Where(w => w.TenantId == tenantId).GroupBy(w => w.Status).Select(g => new { g.Key, N = g.Count() }).ToListAsync(ct);
        int Of(WebhookEventStatus s) => by.FirstOrDefault(x => x.Key == s)?.N ?? 0;
        var pending = await db.OutboxMessages.CountAsync(o => o.TenantId == tenantId && o.Status == OutboxStatus.Pending, ct);
        var failed = await db.OutboxMessages.CountAsync(o => o.TenantId == tenantId && o.Status == OutboxStatus.Failed, ct);
        return new WebhookEventStatsDto(Of(WebhookEventStatus.Received), Of(WebhookEventStatus.Processed), Of(WebhookEventStatus.Ignored), Of(WebhookEventStatus.Failed), pending, failed);
    }

    public async Task<MessagingStatusDto> StatusAsync(CancellationToken ct = default)
    {
        var stats = await StatsAsync(ct);
        if (!publisher.IsConfigured) return new MessagingStatusDto(false, false, "RabbitMQ is not configured on this server: incoming messages are stored but not processed until it is.", stats.PendingOutbox, stats.FailedOutbox);
        var ok = await publisher.PingAsync(ct);
        return new MessagingStatusDto(true, ok, ok ? null : "RabbitMQ is not reachable right now.", stats.PendingOutbox, stats.FailedOutbox);
    }
}
