using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Ai;
using LeadPilot.Application.Common;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Messaging;
using LeadPilot.Web.Infrastructure;
using LeadPilot.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LeadPilot.Web.Controllers;

/// <summary>WhatsApp (WAHA) accounts: add with a connection test, connect and scan the QR code, limits, pause/resume, credentials.</summary>
[Authorize(Policy = PermissionCodes.ChannelsView)]
public class ChannelsController(IChannelAccountService accounts, IWebhookEventService events, IPermissionEvaluator permissions, IConfiguration config) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("WhatsApp accounts", null));
        return View(new AccountsViewModel { Accounts = await accounts.ListAsync(ct), CanManage = await permissions.HasAsync(PermissionCodes.ChannelsManage, ct), Status = await events.StatusAsync(ct) });
    }

    [HttpGet, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public IActionResult New()
    {
        Crumbs(("WhatsApp accounts", Url.Action(nameof(Index))), ("Add account", null));
        return View(new AccountFormModel());
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Test(AccountFormModel m, CancellationToken ct)
    {
        var r = await accounts.TestConnectionAsync(new TestConnectionRequest(m.BaseUrl ?? "", m.ApiKey ?? "", m.SessionName), ct);
        if (r.Ok) Success(r.Message); else Failure(r.Message);
        Crumbs(("WhatsApp accounts", Url.Action(nameof(Index))), ("Add account", null));
        ModelState.Clear();
        return View(nameof(New), m);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Create(AccountFormModel m, CancellationToken ct)
    {
        Crumbs(("WhatsApp accounts", Url.Action(nameof(Index))), ("Add account", null));
        if (!ModelState.IsValid) return View(nameof(New), m);
        var r = await accounts.CreateAsync(new CreateChannelAccountRequest(m.DisplayName, m.BaseUrl, m.ApiKey, m.SessionName, m.WebhookSecret, m.HourlyCap, m.DailyCap, m.MinDelaySeconds), ct);
        if (r.IsSuccess) { Success("Account added. Connect it to start the WhatsApp session."); return RedirectToAction(nameof(Details), new { id = r.Value.Id }); }
        if (Reject(r) is { } rej) return rej;
        AddError(r);
        return View(nameof(New), m);
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var a = await accounts.GetAsync(id, ct);
        if (Reject(a) is { } rej) return rej;
        var canManage = await permissions.HasAsync(PermissionCodes.ChannelsManage, ct);
        Crumbs(("WhatsApp accounts", Url.Action(nameof(Index))), (a.Value.DisplayName, null));
        var notes = await accounts.NotificationsAsync(id, ct);
        return View(new AccountDetailsViewModel
        {
            Account = a.Value, CanManage = canManage, Notifications = notes.IsSuccess ? notes.Value : [],
            Setup = canManage ? (await accounts.GetSetupAsync(id, ct)).Value : null,
            Limits = new LimitsFormModel { DisplayName = a.Value.DisplayName, HourlyCap = a.Value.HourlyCap, DailyCap = a.Value.DailyCap, MinDelaySeconds = a.Value.MinDelaySeconds },
            PublicWebhookBase = config["Messaging:PublicWebhookBaseUrl"],
        });
    }

    /// <summary>Polled by the details page while the session waits for a scan. Returns JSON, never the credentials.</summary>
    [HttpGet, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Qr(Guid id, CancellationToken ct)
    {
        var r = await accounts.GetQrAsync(id, ct);
        if (r.IsFailure) return r.Error.Code == "not_found" ? NotFound() : Conflict(new { error = r.Error.Message });
        return Json(new { status = r.Value.Status.ToString(), png = r.Value.PngBase64, raw = r.Value.RawValue });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public Task<IActionResult> Connect(Guid id, CancellationToken ct) => Act(id, () => accounts.ConnectAsync(id, ct), "The WhatsApp session was started and the webhook registered. Scan the QR code if one is shown.");

    [HttpPost]
    public Task<IActionResult> Refresh(Guid id, CancellationToken ct) => Act(id, () => accounts.RefreshStatusAsync(id, ct), "Status refreshed.");

    [HttpPost, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public Task<IActionResult> Disconnect(Guid id, bool logout, CancellationToken ct) => Act(id, () => accounts.DisconnectAsync(id, logout, ct), logout ? "The phone was unlinked from WhatsApp. Connect again and scan a new QR code to use it." : "The session was stopped.");

    [HttpPost, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public Task<IActionResult> Pause(Guid id, CancellationToken ct) => Act(id, () => accounts.PauseAsync(id, ct), "Sending is paused for this account.");

    [HttpPost, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public Task<IActionResult> Resume(Guid id, CancellationToken ct) => Act(id, () => accounts.ResumeAsync(id, ct), "Sending resumed.");

    [HttpPost, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Limits(Guid id, LimitsFormModel m, CancellationToken ct)
    {
        if (!ModelState.IsValid) { Failure("Check the account name and the limits."); return RedirectToAction(nameof(Details), new { id }); }
        return await Act(id, () => accounts.UpdateAsync(id, new UpdateChannelAccountRequest(m.DisplayName, m.HourlyCap, m.DailyCap, m.MinDelaySeconds), ct), "Settings saved.");
    }

    [HttpGet, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Credentials(Guid id, CancellationToken ct)
    {
        var a = await accounts.GetAsync(id, ct);
        if (Reject(a) is { } rej) return rej;
        Crumbs(("WhatsApp accounts", Url.Action(nameof(Index))), (a.Value.DisplayName, Url.Action(nameof(Details), new { id })), ("Credentials", null));
        ViewData["Account"] = a.Value;
        return View(new CredentialsFormModel());
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Credentials(Guid id, CredentialsFormModel m, CancellationToken ct)
    {
        var a = await accounts.GetAsync(id, ct);
        if (Reject(a) is { } rej) return rej;
        ViewData["Account"] = a.Value;
        Crumbs(("WhatsApp accounts", Url.Action(nameof(Index))), (a.Value.DisplayName, Url.Action(nameof(Details), new { id })), ("Credentials", null));
        if (!ModelState.IsValid) return View(m);
        var r = await accounts.RotateCredentialsAsync(id, new RotateCredentialsRequest(m.BaseUrl, m.ApiKey, m.WebhookSecret), ct);
        if (r.IsSuccess) { Success("Credentials replaced. Connect the account again so WAHA learns the webhook secret."); return RedirectToAction(nameof(Details), new { id }); }
        if (Reject(r) is { } rej2) return rej2;
        AddError(r);
        return View(m);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct)
    {
        var r = await accounts.AcknowledgeNotificationsAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var r = await accounts.RemoveAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        Success("The account was removed from LeadPilot.");
        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult> Act(Guid id, Func<Task<LeadPilot.Shared.Results.Result<ChannelAccountDto>>> work, string okMessage)
    {
        var r = await work();
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success(okMessage); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }
}

/// <summary>A working minimal inbox: list with filters, thread with delivery status, reply. The full hand-off experience is Phase 8.</summary>
[Authorize(Policy = PermissionCodes.ConversationsView)]
public class ConversationsController(IConversationService conversations, IChannelAccountService accounts, IMessageTemplateService templates, IPermissionEvaluator permissions, IAiSuggestionService suggestions, ICurrentUser currentUser) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] ConversationQuery query, CancellationToken ct)
    {
        Crumbs(("Conversations", null));
        var vals = new Dictionary<string, string?>
        {
            ["accountId"] = query.AccountId?.ToString(), ["status"] = query.Status?.ToString(), ["unreadOnly"] = query.UnreadOnly ? "true" : null, ["linkedOnly"] = query.LinkedOnly ? "true" : null,
            ["unlinkedOnly"] = query.UnlinkedOnly ? "true" : null, ["search"] = query.Search,
        };
        var result = await conversations.ListAsync(query, ct);
        var accountList = await SafeAccounts(ct);
        return View(new ConversationsViewModel { Result = result, Query = query, Accounts = accountList, QueryValues = vals });
    }

    private async Task<IReadOnlyList<ChannelAccountDto>> SafeAccounts(CancellationToken ct) =>
        await permissions.HasAsync(PermissionCodes.ChannelsView, ct) ? await accounts.ListAsync(ct) : [];

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, int page = 1, CancellationToken ct = default)
    {
        var conv = await conversations.GetAsync(id, ct);
        if (Reject(conv) is { } rej) return rej;
        var msgs = await conversations.MessagesAsync(id, page, 30, ct);
        Crumbs(("Conversations", Url.Action(nameof(Index))), (conv.Value.Summary.PeerName ?? conv.Value.Summary.PeerPhoneE164 ?? "Conversation", null));
        var canReply = await permissions.HasAsync(PermissionCodes.ConversationsReply, ct);
        var latest = await suggestions.LatestAsync(id, ct);
        var notes = await conversations.NotesAsync(id, ct); var history = await conversations.HistoryAsync(id, ct);
        return View(new ThreadViewModel { Conversation = conv.Value, Messages = msgs.Value, CanReply = canReply, Templates = canReply ? await templates.ListAsync(activeOnly: true, ct) : [], Suggestion = latest.IsSuccess ? latest.Value : null, DraftText = TempData["AiDraft"] as string,
            Notes = notes.IsSuccess ? notes.Value : [], History = history.IsSuccess ? history.Value : [], CurrentUserId = currentUser.UserId });
    }

    /// <summary>Takes the AI's suggestion as the starting point of a reply: it is marked as used and put into the reply box for the person to review, edit and send (the send itself is the normal, consent-checked reply).</summary>
    [HttpPost, Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> UseSuggestion(Guid id, Guid suggestionId, CancellationToken ct)
    {
        var s = await suggestions.GetAsync(suggestionId, ct);
        if (Reject(s) is { } rej) return rej;
        if (s.Value.ConversationId != id) return NotFound();
        var r = await suggestions.MarkUsedAsync(suggestionId, ct);
        if (r.IsFailure) Failure(r.Error.Message);
        else { TempData["AiDraft"] = s.Value.DraftText; Success("The AI's suggestion is in the reply box. Read it, change what you need, then send."); }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> DismissSuggestion(Guid id, Guid suggestionId, CancellationToken ct)
    {
        var s = await suggestions.GetAsync(suggestionId, ct);
        if (Reject(s) is { } rej) return rej;
        if (s.Value.ConversationId != id) return NotFound();
        var r = await suggestions.DismissAsync(suggestionId, ct);
        if (r.IsFailure) Failure(r.Error.Message); else Success("Suggestion dismissed.");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> Reply(Guid id, ReplyFormModel m, CancellationToken ct)
    {
        var r = await conversations.ReplyAsync(id, new ReplyRequest(m.Text, m.TemplateId, m.IdempotencyKey), ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Message queued. It is sent in the background; the status below updates as WhatsApp reports back.");
        else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        var r = await conversations.MarkReadAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Human takeover: the AI stops answering at once. The reason is kept in the handoff history.</summary>
    [HttpPost, Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> TakeOver(Guid id, string? reason, CancellationToken ct)
    {
        var r = await conversations.TakeOverAsync(id, reason, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("You are in control. The AI will not answer this conversation until you give it back."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> PauseAi(Guid id, string? reason, CancellationToken ct)
    {
        var r = await conversations.PauseAiAsync(id, reason, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("The AI is paused for this conversation."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> ResumeAi(Guid id, CancellationToken ct)
    {
        var r = await conversations.ResumeAiAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("The AI answers this conversation again."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> AddNote(Guid id, string? body, CancellationToken ct)
    {
        var r = await conversations.AddNoteAsync(id, body ?? string.Empty, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Internal note added. It is never sent to the customer."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> Assign(Guid id, Guid? userId, CancellationToken ct)
    {
        var r = await conversations.AssignAsync(id, userId, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success(userId is null ? "Conversation unassigned." : "Conversation assigned."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ConversationsReply)]
    public async Task<IActionResult> SetStatus(Guid id, ConversationStatus status, CancellationToken ct)
    {
        var r = await conversations.SetStatusAsync(id, status, ct);
        if (Reject(r) is { } rej) return rej;
        Success(status == ConversationStatus.Closed ? "Conversation closed. A new message from the customer reopens it." : "Conversation reopened.");
        return RedirectToAction(nameof(Details), new { id });
    }
}

[Authorize(Policy = PermissionCodes.ConversationsView)]
public class TemplatesController(IMessageTemplateService templates, IPermissionEvaluator permissions) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        Crumbs(("Message templates", null));
        return View(new TemplatesViewModel { Templates = await templates.ListAsync(false, ct), CanManage = await permissions.HasAsync(PermissionCodes.ChannelsManage, ct) });
    }

    [HttpGet, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public IActionResult New() { Crumbs(("Message templates", Url.Action(nameof(Index))), ("New template", null)); return View("Edit", new TemplateFormModel()); }

    [HttpGet, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var t = await templates.GetAsync(id, ct);
        if (Reject(t) is { } rej) return rej;
        Crumbs(("Message templates", Url.Action(nameof(Index))), (t.Value.Name, null));
        return View(new TemplateFormModel { Id = t.Value.Id, Name = t.Value.Name, Body = t.Value.Body, Category = t.Value.Category });
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Save(TemplateFormModel m, CancellationToken ct)
    {
        Crumbs(("Message templates", Url.Action(nameof(Index))), (m.Id is null ? "New template" : m.Name, null));
        if (!ModelState.IsValid) return View("Edit", m);
        var req = new SaveTemplateRequest(m.Name, m.Body, m.Category);
        var r = m.Id is { } id ? await templates.UpdateAsync(id, req, ct) : await templates.CreateAsync(req, ct);
        if (r.IsSuccess) { Success("Template saved."); return RedirectToAction(nameof(Index)); }
        if (Reject(r) is { } rej) return rej;
        AddError(r);
        return View("Edit", m);
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Toggle(Guid id, bool active, CancellationToken ct)
    {
        var r = await templates.SetActiveAsync(id, active, ct);
        if (Reject(r) is { } rej) return rej;
        Success(active ? "Template switched on." : "Template switched off.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, Authorize(Policy = PermissionCodes.ChannelsManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var r = await templates.DeleteAsync(id, ct);
        if (Reject(r) is { } rej) return rej;
        if (r.IsSuccess) Success("Template deleted."); else Failure(r.Error.Message);
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>Received gateway events of this workspace (for support and troubleshooting).</summary>
[Authorize(Policy = PermissionCodes.WebhooksView)]
public class WebhooksController(IWebhookEventService events) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] WebhookEventQuery query, CancellationToken ct)
    {
        Crumbs(("Webhook events", null));
        var vals = new Dictionary<string, string?> { ["status"] = query.Status?.ToString(), ["eventType"] = query.EventType };
        return View(new WebhooksViewModel { Result = await events.ListAsync(query, ct), Query = query, Stats = await events.StatsAsync(ct), Status = await events.StatusAsync(ct), QueryValues = vals });
    }
}

/// <summary>Dead-letter queues are shared by all workspaces, so only platform operators may look at them (headers only, never payloads).</summary>
[Authorize(Policy = PermissionCodes.TenantsManage)]
public class DeadLettersController(IDeadLetterService deadLetters) : AppController
{
    [HttpGet]
    public async Task<IActionResult> Index(string? queue, CancellationToken ct)
    {
        Crumbs(("Dead-letter queues", null));
        var queues = await deadLetters.QueuesAsync(ct);
        IReadOnlyList<DeadLetterMessageDto> messages = [];
        if (!string.IsNullOrEmpty(queue) && queues.Any(q => q.Queue == queue && q.Available)) messages = await deadLetters.PeekAsync(queue, 50, ct);
        return View(new DeadLettersViewModel { Queues = queues, Selected = queue, Messages = messages });
    }
}
