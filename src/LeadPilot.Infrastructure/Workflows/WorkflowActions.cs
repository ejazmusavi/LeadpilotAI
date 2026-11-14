using System.Text.Json.Nodes;
using LeadPilot.Application.Agents;
using LeadPilot.Application.Contracts;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Domain.Workflows;
using LeadPilot.Infrastructure.Agents;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Workflows;

internal enum ActionOutcome { Done = 1, Retry, Fail, Blocked, Stop }

internal sealed record ActionResult(ActionOutcome Outcome, string Message, string? Effect = null)
{
    public static ActionResult Done(string message, string? effect = null) => new(ActionOutcome.Done, message, effect);
    public static ActionResult Retry(string message) => new(ActionOutcome.Retry, message);
    public static ActionResult Fail(string message) => new(ActionOutcome.Fail, message);
    public static ActionResult Blocked(string message) => new(ActionOutcome.Blocked, message);
    public static ActionResult Stop(string message) => new(ActionOutcome.Stop, message);
}

/// <summary>What one step may use. The lead is tracked in a real run (changes commit together with the execution state) and read-only in a simulation.</summary>
internal sealed class ActionContext
{
    public required WorkflowExecution Execution { get; init; }
    public required StepDefinition Step { get; init; }
    public required string StepName { get; init; }
    public required JsonObject Config { get; init; }
    public required StepState State { get; init; }
    public required Lead Lead { get; init; }
    public required DateTime Now { get; init; }
    public bool Simulation { get; init; }
    public Guid? ContactId { get; init; }
    public Guid? ConversationId { get; init; }

    public string? Str(string key) => Config[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s?.Trim() : null;
    public int? Int(string key) => Config[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    public bool Bool(string key) => Config[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public Guid? Id(string key) => Guid.TryParse(Str(key), out var g) && g != Guid.Empty ? g : null;
}

/// <summary>One kind of step. <see cref="ExecuteAsync"/> performs it; <see cref="DescribeAsync"/> says what it would do (simulation: no writes, no messages, no schedules).</summary>
internal interface IWorkflowAction
{
    StepType Type { get; }
    Task<ActionResult> ExecuteAsync(ActionContext c, CancellationToken ct);
    Task<string> DescribeAsync(ActionContext c, CancellationToken ct);
}

internal static class WorkflowText
{
    /// <summary>Fills {{placeholders}} from the lead. Values come from CRM data (possibly scraped), so they are sanitised: no braces, formatting marks, control or bidi characters.</summary>
    public static string Render(string text, Lead lead, Contact? contact)
    {
        var values = new Dictionary<string, string>
        {
            ["name"] = TemplateRenderer.Sanitize(contact?.Name ?? lead.Name), ["first_name"] = TemplateRenderer.Sanitize(TemplateRenderer.FirstName(contact?.Name ?? lead.Name)), ["business_name"] = TemplateRenderer.Sanitize(lead.Name),
            ["phone"] = contact?.PhoneE164 ?? string.Empty, ["city"] = TemplateRenderer.Sanitize(lead.City), ["country"] = TemplateRenderer.Sanitize(lead.Country),
        };
        return TemplateRenderer.Render(text, values);
    }
}

internal sealed class AssignLeadAction(CrmContext crm) : IWorkflowAction
{
    public StepType Type => StepType.AssignLead;
    public async Task<string> DescribeAsync(ActionContext c, CancellationToken ct) => c.Bool("unassign") ? "would remove the owner of the lead" : $"would assign the lead to {await NameAsync(c.Id("userId"), ct)}";

    public async Task<ActionResult> ExecuteAsync(ActionContext c, CancellationToken ct)
    {
        if (c.Bool("unassign")) { if (c.Lead.OwnerUserId is null) return ActionResult.Done("The lead already has no owner."); c.Lead.AssignTo(null, c.Now, "workflow"); crm.Activity(c.Lead.Id, LeadActivityType.Unassigned, "Lead unassigned by a workflow."); return ActionResult.Done("Owner removed."); }
        var user = c.Id("userId");
        if (user is null || !await crm.IsActiveMemberAsync(user.Value, ct)) return ActionResult.Fail("The person is not an active member of this workspace.");
        if (c.Lead.OwnerUserId == user) return ActionResult.Done("The lead already belongs to that person.");
        c.Lead.AssignTo(user, c.Now, "workflow");
        crm.Activity(c.Lead.Id, LeadActivityType.Assigned, $"Lead assigned to {await NameAsync(user, ct)} by a workflow.");
        return ActionResult.Done("Lead assigned.", user.ToString());
    }

    private async Task<string> NameAsync(Guid? id, CancellationToken ct) => id is { } u ? (await crm.UserNamesAsync([u], ct)).GetValueOrDefault(u) ?? "a team member" : "nobody";
}

internal sealed class ChangeLeadStatusAction(CrmContext crm) : IWorkflowAction
{
    public StepType Type => StepType.ChangeLeadStatus;
    public Task<string> DescribeAsync(ActionContext c, CancellationToken ct) => Task.FromResult($"would set the status to '{c.Str("status")}'");

    public async Task<ActionResult> ExecuteAsync(ActionContext c, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var name = c.Str("status")!;
        var status = await crm.Db.LeadStatuses.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.IsActive && s.Name == name, ct);
        if (status is null) return ActionResult.Fail($"There is no status '{name}'.");
        if (status.IsClosed) return ActionResult.Fail("Closing or disqualifying a lead is a decision for a person; a workflow cannot do it.");
        if (c.Lead.StatusId == status.Id) return ActionResult.Done($"The status is already '{status.Name}'.");
        c.Lead.SetStatus(status.Id, c.Now, "workflow");
        crm.Activity(c.Lead.Id, LeadActivityType.Updated, $"A workflow set the status to '{status.Name}'.", new { by = "workflow", status = status.Name });
        return ActionResult.Done($"Status set to '{status.Name}'.");
    }
}

internal sealed class ChangeLeadStageAction(CrmContext crm, ILeadService leads) : IWorkflowAction
{
    public StepType Type => StepType.ChangeLeadStage;
    public Task<string> DescribeAsync(ActionContext c, CancellationToken ct) => Task.FromResult($"would move the lead to the stage '{c.Str("stage")}'");

    public async Task<ActionResult> ExecuteAsync(ActionContext c, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var name = c.Str("stage")!; var pipeline = c.Lead.PipelineId;
        var stage = await crm.Db.PipelineStages.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.PipelineId == pipeline && s.Name == name, ct);
        if (stage is null) return ActionResult.Fail($"The lead's pipeline has no stage '{name}'.");
        if (stage.Id == c.Lead.PipelineStageId) return ActionResult.Done($"The lead is already in '{stage.Name}'.");
        // the CRM's own stage change: activity, audit, campaign attribution and re-scoring are the same as for a person
        var r = await leads.ChangeStageAsync(c.Lead.Id, stage.Id, ct);
        return r.IsSuccess ? ActionResult.Done($"Moved to '{stage.Name}'.") : ActionResult.Fail(r.Error.Message);
    }
}

internal sealed class CreateFollowUpAction(CrmContext crm) : IWorkflowAction
{
    public StepType Type => StepType.CreateFollowUp;
    public Task<string> DescribeAsync(ActionContext c, CancellationToken ct) => Task.FromResult($"would create a follow-up task due in {c.Int("dueInHours")} h (nothing is sent to the customer)");

    public async Task<ActionResult> ExecuteAsync(ActionContext c, CancellationToken ct)
    {
        string note;
        try { note = WorkflowText.Render(c.Str("note")!, c.Lead, null); } catch (TemplateRenderException ex) { return ActionResult.Fail("The task text could not be filled in: " + ex.Message); }
        var owner = c.Bool("assignToOwner") && c.Lead.OwnerUserId is { } o && await crm.IsActiveMemberAsync(o, ct) ? o : (Guid?)null;
        var due = c.Now.AddHours(c.Int("dueInHours")!.Value);
        var fu = FollowUp.Create(crm.TenantId, c.Lead.Id, c.ConversationId, owner, due, note.Length > 500 ? note[..500] : note, ActorKind.System, c.Now);
        crm.Db.FollowUps.Add(fu);
        crm.Activity(c.Lead.Id, LeadActivityType.Updated, $"A workflow created a follow-up task due {due:u}.", new { by = "workflow", followUpId = fu.Id });
        return ActionResult.Done("Follow-up task created.", fu.Id.ToString());
    }
}

internal sealed class NotifyUserAction(CrmContext crm) : IWorkflowAction
{
    public StepType Type => StepType.NotifyUser;
    public Task<string> DescribeAsync(ActionContext c, CancellationToken ct) => Task.FromResult(c.Str("target") == "owner" ? "would notify the owner of the lead" : "would notify the chosen team member");

    public async Task<ActionResult> ExecuteAsync(ActionContext c, CancellationToken ct)
    {
        Guid? target = c.Str("target") == "owner" ? c.Lead.OwnerUserId : c.Id("target");
        if (target is null) return ActionResult.Fail("The lead has no owner to notify.");
        if (!await crm.IsActiveMemberAsync(target.Value, ct)) return ActionResult.Fail("The person to notify is not an active member of this workspace.");
        string title, body;
        try { title = WorkflowText.Render(c.Str("title")!, c.Lead, null); body = WorkflowText.Render(c.Str("message")!, c.Lead, null); }
        catch (TemplateRenderException ex) { return ActionResult.Fail("The notice could not be filled in: " + ex.Message); }
        var n = WorkflowNotification.Create(crm.TenantId, target.Value, c.Lead.Id, c.Execution.Id, c.Step.Key, title.Length > 150 ? title[..150] : title, body.Length > 1000 ? body[..1000] : body, c.Now);
        crm.Db.WorkflowNotifications.Add(n);
        return ActionResult.Done("Notice created.", n.Id.ToString());
    }
}

internal sealed class EscalateToHumanAction(CrmContext crm) : IWorkflowAction
{
    public StepType Type => StepType.EscalateToHuman;
    public Task<string> DescribeAsync(ActionContext c, CancellationToken ct) => Task.FromResult("would hand the lead's conversation to a person");

    public async Task<ActionResult> ExecuteAsync(ActionContext c, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var db = crm.Db;
        var conv = c.ConversationId is { } cid ? await db.Conversations.FirstOrDefaultAsync(x => x.Id == cid && x.TenantId == tenantId, ct) : null;
        conv ??= await (from x in db.Conversations join ct2 in db.Contacts on x.ContactId equals ct2.Id
                        where x.TenantId == tenantId && ct2.TenantId == tenantId && ct2.LeadId == c.Lead.Id orderby x.LastMessageAtUtc descending select x).FirstOrDefaultAsync(ct);
        if (conv is null) return ActionResult.Fail("The lead has no conversation to hand over.");
        if (conv.EscalatedAtUtc is not null && conv.AiMode == AiMode.HumanControl) return ActionResult.Done("The conversation was already handed to a person.");
        var reason = c.Str("reason") is { Length: > 0 } r ? r : "Escalated by a workflow";
        var (assigned, _) = await HandoffActions.EscalateAsync(crm, conv, c.Lead.Id, reason, null, ActorKind.System, ct);
        return ActionResult.Done(assigned ? "Handed to a person and assigned to the lead's owner." : "Handed to a person (nobody was assigned).", conv.Id.ToString());
    }
}

internal sealed class UpdateLeadFieldsAction(CrmContext crm) : IWorkflowAction
{
    public StepType Type => StepType.UpdateLeadFields;
    public Task<string> DescribeAsync(ActionContext c, CancellationToken ct) => Task.FromResult($"would set {c.Str("field")} to '{c.Str("value")}'");

    public Task<ActionResult> ExecuteAsync(ActionContext c, CancellationToken ct)
    {
        var field = c.Str("field")!; var value = TemplateRenderer.Sanitize(c.Str("value"));
        var l = c.Lead;
        var p = new LeadProfile(l.Name, l.CategoryId, field == "country" ? value : l.Country, field == "province" ? value : l.Province, field == "city" ? value : l.City,
            field == "district" ? value : l.District, field == "area" ? value : l.Area, l.Address, l.Website, l.Confidence);
        var changed = l.UpdateProfile(p, c.Now, "workflow");
        if (changed.Count == 0) return Task.FromResult(ActionResult.Done($"{field} already has that value."));
        crm.Activity(l.Id, LeadActivityType.Updated, $"A workflow updated: {string.Join(", ", changed)}.", new { by = "workflow" });
        return Task.FromResult(ActionResult.Done($"{field} updated."));
    }
}

internal sealed class AddTagAction(CrmContext crm) : IWorkflowAction
{
    public StepType Type => StepType.AddTag;
    public Task<string> DescribeAsync(ActionContext c, CancellationToken ct) => Task.FromResult($"would add the tag '{c.Str("tag")}'");

    public async Task<ActionResult> ExecuteAsync(ActionContext c, CancellationToken ct)
    {
        var tenantId = crm.TenantId; var db = crm.Db; var name = c.Str("tag")!; var norm = name.ToLowerInvariant();
        var tag = await db.LeadTags.FirstOrDefaultAsync(t => t.TenantId == tenantId && t.NormalizedName == norm, ct);
        if (tag is null) { tag = LeadTag.Create(tenantId, name, c.Now); db.LeadTags.Add(tag); }
        else if (await db.LeadTagMaps.AnyAsync(m => m.TenantId == tenantId && m.LeadId == c.Lead.Id && m.TagId == tag.Id, ct)) return ActionResult.Done($"The lead already has the tag '{tag.Name}'.");
        db.LeadTagMaps.Add(LeadTagMap.Create(tenantId, c.Lead.Id, tag.Id, c.Now));
        crm.Activity(c.Lead.Id, LeadActivityType.TagAdded, $"Tag '{tag.Name}' added by a workflow.");
        return ActionResult.Done($"Tag '{tag.Name}' added.");
    }
}

internal sealed class ScheduleDemoAction(IDemoScheduler demos, CrmContext crm) : IWorkflowAction
{
    public StepType Type => StepType.ScheduleDemo;

    public async Task<string> DescribeAsync(ActionContext c, CancellationToken ct)
    {
        var r = await demos.AvailabilityAsync(c.Now, c.Now.AddDays(c.Int("withinDays") ?? 7), ct);
        return r.Available ? "would book the next free demo slot" : "NOT AVAILABLE: demo scheduling is not connected yet (Phase 10). This step would fail.";
    }

    public async Task<ActionResult> ExecuteAsync(ActionContext c, CancellationToken ct)
    {
        var avail = await demos.AvailabilityAsync(c.Now, c.Now.AddDays(c.Int("withinDays") ?? 7), ct);
        if (!avail.Available) return ActionResult.Fail("Demo scheduling is not connected yet (it arrives in Phase 10). Nothing was booked.");
        if (avail.Error is not null) return ActionResult.Retry("Availability could not be read right now.");
        var slot = (avail.Value ?? []).Where(s => s.StartUtc > c.Now).OrderBy(s => s.StartUtc).FirstOrDefault();
        if (slot is null) return ActionResult.Fail("There is no free demo slot in that period.");
        var booked = await demos.BookAsync(c.Lead.Id, slot, c.Str("notes"), ct);
        if (!booked.Available) return ActionResult.Fail("Demo scheduling is not connected yet (it arrives in Phase 10). Nothing was booked.");
        if (booked.Error is not null || booked.Value is null) return ActionResult.Retry("The slot could not be booked (it may have just been taken).");
        crm.Activity(c.Lead.Id, LeadActivityType.Updated, $"Demo booked by a workflow for {booked.Value.StartUtc:u}.", new { by = "workflow", booking = booked.Value.BookingId });
        return ActionResult.Done($"Demo booked for {booked.Value.StartUtc:u}.", booked.Value.BookingId);
    }
}

internal sealed class SendMessageAction(CrmContext crm, IWorkflowMessageSender sender) : IWorkflowAction
{
    public StepType Type => StepType.SendMessage;
    private static readonly HashSet<string> Transient = ["account_paused", "account_not_connected", "rate_limited", "new_contacts_blocked"];

    public async Task<string> DescribeAsync(ActionContext c, CancellationToken ct)
    {
        var (allowed, reason) = await sender.CheckAsync(c.Lead.Id, c.ContactId, ct);
        return allowed ? "would queue the approved message (the contact is eligible right now)" : "would NOT send: " + reason;
    }

    public async Task<ActionResult> ExecuteAsync(ActionContext c, CancellationToken ct)
    {
        var template = c.Id("templateId"); var account = c.Id("channelAccountId");
        if (template is null || account is null) return ActionResult.Fail("The step has no template or WhatsApp account.");

        // step-level idempotency: the key is stable across redelivery and crash recovery. An earlier attempt that left a message behind is never repeated; only a message that FAILED
        // (provably not sent) moves the step to a new generation, so a manual retry cannot produce a duplicate.
        var key = WorkflowIdempotency.StepKey(c.Execution.Id, c.Step.Key, c.State.Generation);
        var earlier = await sender.FindAsync(key, ct);
        if (earlier is { Failed: false }) return ActionResult.Done("This message was already queued by an earlier attempt; it is not sent again.", earlier.MessageId.ToString());
        if (earlier is { Failed: true })
        {
            c.State.Generation++;
            key = WorkflowIdempotency.StepKey(c.Execution.Id, c.Step.Key, c.State.Generation);
            earlier = await sender.FindAsync(key, ct);
            if (earlier is { Failed: false }) return ActionResult.Done("This message was already queued by an earlier attempt; it is not sent again.", earlier.MessageId.ToString());
        }

        // usage limit of workflows (on top of the account, warm-up and workspace limits of the Phase 5 pipeline)
        var settings = await crm.Db.WorkflowTenantSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == crm.TenantId, ct);
        var limit = settings?.DailyMessageLimit ?? WorkflowTenantSettings.Create(crm.TenantId, c.Now).DailyMessageLimit;
        var since = c.Now.AddHours(-24); var tenantId = crm.TenantId;
        var used = await crm.Db.Messages.CountAsync(m => m.TenantId == tenantId && m.IdempotencyKey != null && m.IdempotencyKey.StartsWith("wf:") && m.CreatedAtUtc >= since && m.Status != MessageStatus.Failed, ct);
        if (used >= limit) return ActionResult.Retry($"The workspace reached its limit of {limit} workflow messages per 24 hours.");

        var outcome = await sender.SendAsync(new WorkflowSendRequest(c.Lead.Id, c.ContactId, c.ConversationId, template.Value, account.Value, key), ct);
        return outcome.Status switch
        {
            WorkflowSendStatus.Queued => ActionResult.Done("Message queued through the sending pipeline.", outcome.MessageId?.ToString()),
            WorkflowSendStatus.Blocked => ActionResult.Blocked(outcome.Reason ?? "Not allowed to message this contact."),
            _ => outcome.Code is not null && Transient.Contains(outcome.Code) ? ActionResult.Retry(outcome.Reason ?? "The message could not be queued right now.") : ActionResult.Fail(outcome.Reason ?? "The message could not be queued."),
        };
    }
}
