using LeadPilot.Application.Agents;
using LeadPilot.Application.Common;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LeadPilot.Api.Controllers;

/// <summary>
/// WAHA webhook receiver. Anonymous by design: authenticity comes from the account's secret URL key AND the HMAC-SHA512 signature header checked in constant time.
/// It answers fast: the event is stored with an outbox row and processed asynchronously.
/// </summary>
[Route("api/v1/webhooks/waha")]
public class WahaWebhooksController(IWebhookIngestionService ingestion) : ApiControllerBase
{
    /// <summary>Receives one WAHA event. 200 = stored (or recognised duplicate/ignored), 401 bad signature, 400 malformed, 413 too large, 403 workspace suspended, 429 rate limited.</summary>
    [HttpPost("{key}"), AllowAnonymous, EnableRateLimiting("webhook")]
    public async Task<IActionResult> Receive(string key, CancellationToken ct)
    {
        var max = ingestion.MaxBodyBytes;
        if (Request.ContentLength > max) return StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024]; int read;
        while ((read = await Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            ms.Write(buffer, 0, read);
            if (ms.Length > max) return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        var headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);   // the adapter picks the signature headers it knows
        var result = await ingestion.ReceiveAsync(key, ms.ToArray(), headers, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        return result.Status switch
        {
            WebhookReceiveStatus.Accepted or WebhookReceiveStatus.Duplicate or WebhookReceiveStatus.Ignored => Ok(new { status = result.Status.ToString().ToLowerInvariant() }),
            WebhookReceiveStatus.Rejected => Problem(statusCode: StatusCodes.Status401Unauthorized, title: "webhook_rejected", detail: "The webhook could not be authenticated."),
            WebhookReceiveStatus.Malformed => Problem(statusCode: StatusCodes.Status400BadRequest, title: "webhook_malformed", detail: "The webhook payload is not valid."),
            WebhookReceiveStatus.TooLarge => StatusCode(StatusCodes.Status413PayloadTooLarge),
            _ => Problem(statusCode: StatusCodes.Status403Forbidden, title: "tenant_suspended", detail: "This workspace is suspended."),
        };
    }
}

/// <summary>WhatsApp (WAHA) accounts of the signed-in workspace. Credentials are write-only: they are encrypted at rest and never returned.</summary>
[Route("api/v1/channels/accounts")]
public class ChannelAccountsController(IChannelAccountService accounts) : ApiControllerBase
{
    /// <summary>Accounts with status, limits and usage.</summary>
    [HttpGet, Authorize(Policy = PermissionCodes.ChannelsView)]
    public async Task<IReadOnlyList<ChannelAccountDto>> List(CancellationToken ct) => await accounts.ListAsync(ct);

    [HttpGet("{id:guid}"), Authorize(Policy = PermissionCodes.ChannelsView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await accounts.GetAsync(id, ct));

    /// <summary>Webhook address and events WAHA must call (contains the account's secret key).</summary>
    [HttpGet("{id:guid}/setup"), Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Setup(Guid id, CancellationToken ct) => Respond(await accounts.GetSetupAsync(id, ct));

    /// <summary>Checks the base URL and API key against WAHA without saving anything.</summary>
    [HttpPost("test-connection"), Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<ConnectionTestResult> Test(TestConnectionRequest request, CancellationToken ct) => await accounts.TestConnectionAsync(request, ct);

    /// <summary>Adds an account. The connection test must pass. 409 when the session name is taken.</summary>
    [HttpPost, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Create(CreateChannelAccountRequest request, CancellationToken ct)
    {
        var r = await accounts.CreateAsync(request, ct);
        return r.IsSuccess ? CreatedAtAction(nameof(Get), new { id = r.Value.Id }, r.Value) : Fail(r);
    }

    [HttpPut("{id:guid}"), Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Update(Guid id, UpdateChannelAccountRequest request, CancellationToken ct) => Respond(await accounts.UpdateAsync(id, request, ct));

    /// <summary>Replaces base URL, API key and (optionally) webhook secret. Connect the account again afterwards.</summary>
    [HttpPut("{id:guid}/credentials"), Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Rotate(Guid id, RotateCredentialsRequest request, CancellationToken ct) => Respond(await accounts.RotateCredentialsAsync(id, request, ct));

    /// <summary>Creates/starts the WAHA session and registers our webhook. Idempotent.</summary>
    [HttpPost("{id:guid}/connect"), Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Connect(Guid id, CancellationToken ct) => Respond(await accounts.ConnectAsync(id, ct));

    [HttpPost("{id:guid}/refresh"), Authorize(Policy = PermissionCodes.ChannelsView)]
    public async Task<IActionResult> Refresh(Guid id, CancellationToken ct) => Respond(await accounts.RefreshStatusAsync(id, ct));

    /// <summary>QR code to scan (only while the session waits for it).</summary>
    [HttpGet("{id:guid}/qr"), Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Qr(Guid id, CancellationToken ct) => Respond(await accounts.GetQrAsync(id, ct));

    /// <summary>Stops the session; <c>logout=true</c> also unlinks the phone.</summary>
    [HttpPost("{id:guid}/disconnect"), Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Disconnect(Guid id, [FromQuery] bool logout = false, CancellationToken ct = default) => Respond(await accounts.DisconnectAsync(id, logout, ct));

    [HttpPost("{id:guid}/pause"), Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Pause(Guid id, CancellationToken ct) => Respond(await accounts.PauseAsync(id, ct));

    [HttpPost("{id:guid}/resume"), Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Resume(Guid id, CancellationToken ct) => Respond(await accounts.ResumeAsync(id, ct));

    /// <summary>Removes the account from LeadPilot (the WAHA session itself is not touched).</summary>
    [HttpDelete("{id:guid}"), Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct) => Respond(await accounts.RemoveAsync(id, ct));

    [HttpGet("{id:guid}/notifications"), Authorize(Policy = PermissionCodes.ChannelsView)]
    public async Task<IActionResult> Notifications(Guid id, CancellationToken ct) => Respond(await accounts.NotificationsAsync(id, ct));

    [HttpPost("{id:guid}/notifications/acknowledge"), Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct) => Respond(await accounts.AcknowledgeNotificationsAsync(id, ct));
}

/// <summary>Conversations and their messages. Other workspaces' ids answer 404.</summary>
[Route("api/v1/conversations")]
public class ConversationsController(IConversationService conversations) : ApiControllerBase
{
    [HttpGet, Authorize(Policy = PermissionCodes.ConversationsView)]
    public async Task<PagedResult<ConversationListItemDto>> List([FromQuery] ConversationQuery query, CancellationToken ct) => await conversations.ListAsync(query, ct);

    /// <summary>One conversation with the current reply policy (<c>canReply</c>, purpose, service window).</summary>
    [HttpGet("{id:guid}"), Authorize(Policy = PermissionCodes.ConversationsView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await conversations.GetAsync(id, ct));

    /// <summary>Messages, oldest first within the page; page 1 is the newest page.</summary>
    [HttpGet("{id:guid}/messages"), Authorize(Policy = PermissionCodes.ConversationsView)]
    public async Task<IActionResult> Messages(Guid id, int page = 1, int pageSize = 50, CancellationToken ct = default) => Respond(await conversations.MessagesAsync(id, page, pageSize, ct));

    /// <summary>
    /// Queues a reply. 202: it is sent by the background worker. Inside the customer-initiated window it is a service message; outside it needs marketing consent.
    /// Send an <c>idempotencyKey</c> to make retries safe. 409 when the number is suppressed/opted out, the contact is not eligible or the account is paused/disconnected; 429 at the sending limits.
    /// </summary>
    [HttpPost("{id:guid}/messages"), Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> Reply(Guid id, ReplyRequest request, CancellationToken ct)
    {
        var r = await conversations.ReplyAsync(id, request, ct);
        return r.IsSuccess ? Accepted(r.Value) : Fail(r);
    }

    /// <summary>Marks the chat as read (also tells WhatsApp, best effort).</summary>
    [HttpPost("{id:guid}/read"), Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct) => Respond(await conversations.MarkReadAsync(id, ct));

    [HttpPut("{id:guid}/status"), Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> SetStatus(Guid id, [FromQuery] ConversationStatus status, CancellationToken ct) => Respond(await conversations.SetStatusAsync(id, status, ct));

    /// <summary>Sets who answers: Active (the AI), Paused (nobody automatic) or HumanControl (a person). The AI never replies unless the mode is Active, even to a message it is still working on.</summary>
    [HttpPut("{id:guid}/ai-mode"), Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> SetAiMode(Guid id, SetAiModeRequest request, CancellationToken ct) => Respond(await conversations.SetAiModeAsync(id, request.Mode, ct));

    [HttpPut("{id:guid}/assignee"), Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> Assign(Guid id, AssignConversationRequest request, CancellationToken ct) => Respond(await conversations.AssignAsync(id, request.UserId, ct));

    /// <summary>Human takeover: the AI stops answering immediately; an AI reply still waiting to be sent is withdrawn. The reason is recorded in the handoff history.</summary>
    [HttpPost("{id:guid}/takeover"), Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> TakeOver(Guid id, HandoffReasonRequest request, CancellationToken ct) => Respond(await conversations.TakeOverAsync(id, request.Reason, ct));

    [HttpPost("{id:guid}/ai/pause"), Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> PauseAi(Guid id, HandoffReasonRequest request, CancellationToken ct) => Respond(await conversations.PauseAiAsync(id, request.Reason, ct));

    /// <summary>Gives the conversation back to the AI. 409 when the customer opted out.</summary>
    [HttpPost("{id:guid}/ai/resume"), Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> ResumeAi(Guid id, CancellationToken ct) => Respond(await conversations.ResumeAiAsync(id, ct));

    [HttpGet("{id:guid}/notes"), Authorize(Policy = PermissionCodes.ConversationsView)]
    public async Task<IActionResult> Notes(Guid id, CancellationToken ct) => Respond(await conversations.NotesAsync(id, ct));

    /// <summary>Internal note (never sent to the customer).</summary>
    [HttpPost("{id:guid}/notes"), Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> AddNote(Guid id, ConversationNoteRequest request, CancellationToken ct) { var r = await conversations.AddNoteAsync(id, request.Body, ct); return r.IsSuccess ? Created($"api/v1/conversations/{id}/notes", r.Value) : Fail(r); }

    /// <summary>Takeovers, pauses, escalations (with reasons), assignments and status changes, newest first.</summary>
    [HttpGet("{id:guid}/handoff-history"), Authorize(Policy = PermissionCodes.ConversationsView)]
    public async Task<IActionResult> History(Guid id, CancellationToken ct) => Respond(await conversations.HistoryAsync(id, ct));
}

/// <summary>Messages to contacts. Consent, suppression and the sending limits always apply.</summary>
[Route("api/v1/messages")]
public class MessagesController(IOutboundMessageService messages) : ApiControllerBase
{
    /// <summary>Queues a WhatsApp message to a contact. Outside a conversation the customer started this is marketing: the contact needs recorded consent and must not be suppressed.</summary>
    [HttpPost, Authorize(Policy = PermissionCodes.MessagesSend)]
    public async Task<IActionResult> Send(SendMessageRequest request, CancellationToken ct)
    {
        var r = await messages.SendToContactAsync(request, ct);
        return r.IsSuccess ? AcceptedAtAction(nameof(Get), new { id = r.Value.Id }, r.Value) : Fail(r);
    }

    /// <summary>Delivery state of a message (Queued, Sending, Sent, Delivered, Read, Failed, Unknown).</summary>
    [HttpGet("{id:guid}"), Authorize(Policy = PermissionCodes.ConversationsView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await messages.GetMessageAsync(id, ct));
}

/// <summary>Tenant-managed text templates, rendered locally ({{name}}, {{first_name}}, {{business_name}}, {{phone}}); WAHA has no official templates.</summary>
[Route("api/v1/message-templates")]
public class MessageTemplatesController(IMessageTemplateService templates) : ApiControllerBase
{
    [HttpGet, Authorize(Policy = PermissionCodes.ConversationsView)]
    public async Task<IReadOnlyList<TemplateDto>> List(bool activeOnly = false, CancellationToken ct = default) => await templates.ListAsync(activeOnly, ct);

    [HttpGet("{id:guid}"), Authorize(Policy = PermissionCodes.ConversationsView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Respond(await templates.GetAsync(id, ct));

    [HttpGet("{id:guid}/preview"), Authorize(Policy = PermissionCodes.ConversationsView)]
    public async Task<IActionResult> Preview(Guid id, Guid? contactId, CancellationToken ct) => Respond(await templates.PreviewAsync(id, contactId, ct));

    [HttpPost, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Create(SaveTemplateRequest request, CancellationToken ct)
    {
        var r = await templates.CreateAsync(request, ct);
        return r.IsSuccess ? CreatedAtAction(nameof(Get), new { id = r.Value.Id }, r.Value) : Fail(r);
    }

    [HttpPut("{id:guid}"), Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Update(Guid id, SaveTemplateRequest request, CancellationToken ct) => Respond(await templates.UpdateAsync(id, request, ct));

    [HttpPut("{id:guid}/active"), Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> SetActive(Guid id, [FromQuery] bool active, CancellationToken ct) => Respond(await templates.SetActiveAsync(id, active, ct));

    [HttpDelete("{id:guid}"), Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => Respond(await templates.DeleteAsync(id, ct));
}

/// <summary>Received gateway events of this workspace (payloads are kept for the retention period).</summary>
[Route("api/v1/webhook-events")]
public class WebhookEventsController(IWebhookEventService events) : ApiControllerBase
{
    [HttpGet, Authorize(Policy = PermissionCodes.WebhooksView)]
    public async Task<PagedResult<WebhookEventDto>> List([FromQuery] WebhookEventQuery query, CancellationToken ct) => await events.ListAsync(query, ct);

    [HttpGet("stats"), Authorize(Policy = PermissionCodes.WebhooksView)]
    public async Task<WebhookEventStatsDto> Stats(CancellationToken ct) => await events.StatsAsync(ct);

    /// <summary>Is the broker configured/reachable, and how many events wait in the outbox.</summary>
    [HttpGet("status"), Authorize(Policy = PermissionCodes.WebhooksView)]
    public async Task<MessagingStatusDto> Status(CancellationToken ct) => await events.StatusAsync(ct);
}

/// <summary>Dead-letter queues. PLATFORM operators only: the queues hold every tenant's failed events, and only headers are shown.</summary>
[Route("api/v1/admin/dead-letters")]
public class DeadLettersController(IDeadLetterService deadLetters) : ApiControllerBase
{
    [HttpGet, Authorize(Policy = PermissionCodes.TenantsManage)]
    public async Task<IReadOnlyList<DeadLetterQueueDto>> Queues(CancellationToken ct) => await deadLetters.QueuesAsync(ct);

    [HttpGet("{queue}"), Authorize(Policy = PermissionCodes.TenantsManage)]
    public async Task<IActionResult> Peek(string queue, int max = 20, CancellationToken ct = default)
    {
        try { return Ok(await deadLetters.PeekAsync(queue, max, ct)); }
        catch (ArgumentException) { return NotFound(); }
    }
}
