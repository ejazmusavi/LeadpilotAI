using System.Globalization;
using System.Text.Json;
using LeadPilot.Application.Agents;
using LeadPilot.Application.Ai;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Messaging.Services;
using LeadPilot.Infrastructure.Services.Crm;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Agents;

/// <summary>
/// The business rules of each tool. Every handler works on the lead of THIS conversation (resolved server-side), reads and writes only through tenant-scoped queries, and returns structured data. Tools that need a
/// Phase 10 service call its port; when the port is not connected the result is "not available" and nothing happens.
/// </summary>
public sealed class AgentToolHandlers(CrmContext crm, IDemoScheduler demos, ITrialProvisioner trials, IPaymentRequestGateway payments, OptOutProcessor optOut)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async Task<AgentTenantSettings> SettingsAsync(CancellationToken ct) =>
        await crm.Db.AgentTenantSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == crm.TenantId, ct) ?? AgentTenantSettings.Create(crm.TenantId, crm.Now);

    public async Task<Guid?> LeadIdOfAsync(Conversation conv, CancellationToken ct)
    {
        if (conv.ContactId is not { } cid) return null;
        var tenantId = crm.TenantId;
        return await crm.Db.Contacts.AsNoTracking().Where(c => c.Id == cid && c.TenantId == tenantId && !c.IsDeleted).Select(c => (Guid?)c.LeadId).FirstOrDefaultAsync(ct);
    }

    private async Task<Lead?> LeadAsync(Conversation conv, CancellationToken ct)
    {
        var id = await LeadIdOfAsync(conv, ct); var tenantId = crm.TenantId;
        return id is null ? null : await crm.Db.Leads.FirstOrDefaultAsync(l => l.Id == id && l.TenantId == tenantId && !l.IsArchived, ct);
    }

    private static readonly ToolOutput NoLead = ToolOutput.Deny("no_linked_lead", "This conversation is not linked to a lead in the CRM, so this action is not possible.");

    internal async Task<ToolOutput> ExecuteAsync(ToolDefinition def, ToolArgsResult a, ToolCallContext ctx, CancellationToken ct)
    {
        var conv = ctx.Conversation;
        switch (def.Name)
        {
            case AgentTools.GetProductInformation: return await ProductsAsync(a.Str("topic"), ct);
            case AgentTools.GetPricingPlans: return await PlansAsync(a.Str("product"), ct);
            case AgentTools.GetLeadProfile: return await ProfileAsync(conv, ct);
            case AgentTools.UpdateLeadStatus: return await UpdateStatusAsync(conv, a, ct);
            case AgentTools.QualifyLead: return await QualifyAsync(conv, ctx.Agent, a, ct);
            case AgentTools.CheckDemoAvailability: return await AvailabilityAsync(a.Int("days") ?? 7, ct);
            case AgentTools.BookDemo: return await BookDemoAsync(conv, a, ct);
            case AgentTools.ScheduleFollowUp: return await FollowUpAsync(conv, a, ct);
            case AgentTools.CreateTrialAccount: return await TrialAsync(conv, a, ctx, ct);
            case AgentTools.CreatePaymentRequest: return await PaymentAsync(conv, a, ctx, ct);
            case AgentTools.TransferToHuman: return await TransferAsync(conv, a, ctx, ct);
            case AgentTools.StopMarketingMessages: return await StopAsync(conv, a, ctx, ct);
            case AgentTools.AddLeadNote: return await NoteAsync(conv, a, ct);
            default: return ToolOutput.Deny("not_implemented", "This tool is not implemented.");
        }
    }

    // ---------------- reads ----------------

    private async Task<ToolOutput> ProductsAsync(string? topic, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var all = await crm.Db.CatalogProducts.AsNoTracking().Where(p => p.TenantId == tenantId && p.IsActive).OrderBy(p => p.Name).ToListAsync(ct);
        var confirmed = all.Where(p => p.IsConfirmed).ToList();
        if (!string.IsNullOrWhiteSpace(topic))
        {
            var t = topic.Trim();
            var narrowed = confirmed.Where(p => p.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || (p.Summary?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false) || p.FeatureList.Any(f => f.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
            if (narrowed.Count > 0) confirmed = narrowed;
        }
        if (confirmed.Count == 0)
            return ToolOutput.Ok(new { known = false, note = "No confirmed product information is available. Say that you do not have this information and that a colleague will follow up. Do not guess." });
        return ToolOutput.Ok(new
        {
            known = true,
            products = confirmed.Take(5).Select(p => new { name = p.Name, summary = p.Summary, features = p.FeatureList.Take(30), demoProcess = p.DemoProcess, trialProcess = p.TrialProcess }),
            note = "Only the facts listed here are confirmed. Anything not listed is unknown.",
        });
    }

    private async Task<ToolOutput> PlansAsync(string? product, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var rows = await (from p in crm.Db.PricingPlans.AsNoTracking()
                          join pr in crm.Db.CatalogProducts.AsNoTracking() on p.ProductId equals pr.Id
                          where p.TenantId == tenantId && pr.TenantId == tenantId && p.IsActive && p.IsConfirmed && pr.IsActive && pr.IsConfirmed
                          orderby pr.Name, p.Price
                          select new { Product = pr.Name, p.Name, p.Price, p.Currency, p.BillingPeriod, p.Details }).ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(product)) { var t = product.Trim(); var n = rows.Where(r => r.Product.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList(); if (n.Count > 0) rows = n; }
        if (rows.Count == 0)
            return ToolOutput.Ok(new { known = false, note = "No confirmed pricing is available. Say that you do not have the price and that a colleague will send it. Never estimate a price." });
        return ToolOutput.Ok(new { known = true, plans = rows.Take(12).Select(r => new { product = r.Product, plan = r.Name, price = r.Price, currency = r.Currency, billingPeriod = r.BillingPeriod, details = r.Details }), note = "These are the only confirmed prices. Do not offer any reduction." });
    }

    private async Task<ToolOutput> ProfileAsync(Conversation conv, CancellationToken ct)
    {
        var lead = await LeadAsync(conv, ct); if (lead is null) return NoLead;
        var tenantId = crm.TenantId; var db = crm.Db;
        var category = lead.CategoryId is { } cid ? await db.LeadCategories.AsNoTracking().Where(c => c.Id == cid && c.TenantId == tenantId).Select(c => c.Name).FirstOrDefaultAsync(ct) : null;
        var stage = await db.PipelineStages.AsNoTracking().Where(s => s.Id == lead.PipelineStageId && s.TenantId == tenantId).Select(s => s.Name).FirstOrDefaultAsync(ct);
        var status = await db.LeadStatuses.AsNoTracking().Where(s => s.Id == lead.StatusId && s.TenantId == tenantId).Select(s => s.Name).FirstOrDefaultAsync(ct);
        var qual = await db.LeadQualifications.AsNoTracking().FirstOrDefaultAsync(q => q.LeadId == lead.Id && q.TenantId == tenantId, ct);
        var open = await db.FollowUps.CountAsync(f => f.TenantId == tenantId && f.LeadId == lead.Id && f.Status == FollowUpStatus.Open, ct);
        return ToolOutput.Ok(new
        {
            name = lead.Name, category, city = lead.City, country = lead.Country, stage, status,
            qualification = qual is null ? null : new { fit = qual.Fit.ToString().ToLowerInvariant(), answers = JsonSerializer.Deserialize<Dictionary<string, string>>(qual.AnswersJson), summary = qual.Summary },
            openFollowUps = open,
        });
    }

    // ---------------- CRM writes ----------------

    private async Task<ToolOutput> UpdateStatusAsync(Conversation conv, ToolArgsResult a, CancellationToken ct)
    {
        var lead = await LeadAsync(conv, ct); if (lead is null) return NoLead;
        var tenantId = crm.TenantId; var name = a.Str("status")!;
        var status = await crm.Db.LeadStatuses.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId && s.IsActive && s.Name == name, ct);
        if (status is null) return ToolOutput.Invalid("unknown_status", "There is no such lead status.");
        if (status.IsClosed) return ToolOutput.Deny("closed_status_requires_staff", "Closing or disqualifying a lead is a decision for a person.");
        if (lead.StatusId == status.Id) return ToolOutput.Ok(new { changed = false, status = status.Name });
        lead.SetStatus(status.Id, crm.Now, "AI assistant");
        crm.Activity(lead.Id, LeadActivityType.Updated, $"AI assistant set the status to '{status.Name}': {a.Str("reason")}", new { by = "ai", status = status.Name });
        return ToolOutput.Ok(new { changed = true, status = status.Name });
    }

    private async Task<ToolOutput> QualifyAsync(Conversation conv, SalesAgent? agent, ToolArgsResult a, CancellationToken ct)
    {
        var lead = await LeadAsync(conv, ct); if (lead is null) return NoLead;
        if (agent is null) return ToolOutput.Deny("no_agent", "No agent with qualification questions is configured.");
        var tenantId = crm.TenantId;
        var questions = await crm.Db.AgentQualificationQuestions.AsNoTracking().Where(q => q.TenantId == tenantId && q.AgentId == agent.Id).OrderBy(q => q.SortOrder).ToListAsync(ct);
        var existing = await crm.Db.LeadQualifications.FirstOrDefaultAsync(q => q.TenantId == tenantId && q.LeadId == lead.Id, ct);
        var answers = existing is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(existing.AnswersJson) ?? [];
        var errors = new List<string>();
        foreach (var (key, value) in a.Map("answers")!)
        {
            var q = questions.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (q is null) { errors.Add($"unknown_question_key:{key}"); continue; }
            switch (q.AnswerType)
            {
                case AnswerType.Number:
                    if (!decimal.TryParse(value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0 || n > 1_000_000_000m) { errors.Add($"not_a_number:{q.Key}"); continue; }
                    answers[q.Key] = n.ToString(CultureInfo.InvariantCulture); break;
                case AnswerType.Choice:
                    var choice = q.Choices.FirstOrDefault(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase));
                    if (choice is null) { errors.Add($"not_an_allowed_choice:{q.Key}"); continue; }
                    answers[q.Key] = choice; break;
                default: answers[q.Key] = value; break;
            }
        }
        if (errors.Count > 0 && answers.Count == (existing is null ? 0 : (JsonSerializer.Deserialize<Dictionary<string, string>>(existing.AnswersJson)?.Count ?? 0)))
            return ToolOutput.Invalid("invalid_answers", "None of the answers could be saved: " + string.Join(", ", errors));
        var fit = Enum.TryParse<QualificationFit>(a.Str("fit"), true, out var f) ? f : QualificationFit.Unknown;
        var json = JsonSerializer.Serialize(answers);
        if (existing is null) crm.Db.LeadQualifications.Add(LeadQualification.Create(tenantId, lead.Id, agent.Id, json, fit, a.Str("summary"), crm.Now));
        else existing.Update(json, fit, a.Str("summary"), crm.Now);
        crm.Activity(lead.Id, LeadActivityType.Updated, "AI assistant recorded qualification answers", new { by = "ai", keys = answers.Keys });
        var missing = questions.Where(q => q.Required && !answers.ContainsKey(q.Key)).Select(q => new { key = q.Key, question = q.Question }).ToList();
        return ToolOutput.Ok(new { saved = true, answered = answers.Keys, rejected = errors, missingRequired = missing });
    }

    private async Task<ToolOutput> NoteAsync(Conversation conv, ToolArgsResult a, CancellationToken ct)
    {
        var lead = await LeadAsync(conv, ct); if (lead is null) return NoLead;
        var note = LeadNote.Create(crm.TenantId, lead.Id, "[AI assistant] " + PromptGuard.Neutralize(a.Str("note"), 600, escapeAngles: false), null, crm.Now);
        crm.Db.LeadNotes.Add(note);
        crm.Activity(lead.Id, LeadActivityType.NoteAdded, "AI assistant added a note", new { noteId = note.Id, by = "ai" });
        return ToolOutput.Ok(new { saved = true });
    }

    // ---------------- demo (Phase 10 port) ----------------

    private async Task<ToolOutput> AvailabilityAsync(int days, CancellationToken ct)
    {
        var from = crm.Now; var r = await demos.AvailabilityAsync(from, from.AddDays(Math.Clamp(days, 1, 14)), ct);
        if (!r.Available) return ToolOutput.NotAvailable("Demo scheduling is not connected yet. Do not propose any time; say a colleague will arrange the demo.");
        if (r.Error is not null) return new ToolOutput(ToolOutcome.Failed, "scheduler_error", new { message = "Availability could not be read right now." });
        var slots = (r.Value ?? []).Where(s => s.StartUtc > from).OrderBy(s => s.StartUtc).Take(10).ToList();
        return ToolOutput.Ok(new { slots = slots.Select(s => new { slotId = s.SlotId, startUtc = s.StartUtc, endUtc = s.EndUtc }), note = slots.Count == 0 ? "No free slots in this period." : "Only these slots exist." });
    }

    private async Task<ToolOutput> BookDemoAsync(Conversation conv, ToolArgsResult a, CancellationToken ct)
    {
        var lead = await LeadAsync(conv, ct); if (lead is null) return NoLead;
        if (conv.OptedOutAtUtc is not null) return ToolOutput.Deny("opted_out", "The customer opted out.");
        var from = crm.Now; var avail = await demos.AvailabilityAsync(from, from.AddDays(14), ct);
        if (!avail.Available) return ToolOutput.NotAvailable("Demo booking is not connected yet. Nothing was booked; say a colleague will arrange the demo.");
        var slot = (avail.Value ?? []).FirstOrDefault(s => s.StartUtc > from && string.Equals(s.SlotId, a.Str("slotId"), StringComparison.Ordinal));
        if (slot is null) return ToolOutput.Deny("slot_not_available", "That is not an available slot. Call CheckDemoAvailability and offer one of the slots it returns.");
        var booked = await demos.BookAsync(lead.Id, slot, a.Str("notes"), ct);
        if (!booked.Available) return ToolOutput.NotAvailable("Demo booking is not connected yet. Nothing was booked.");
        if (booked.Error is not null || booked.Value is null) return new ToolOutput(ToolOutcome.Failed, "booking_failed", new { message = "The slot could not be booked (it may have just been taken). Offer another slot." });
        crm.Activity(lead.Id, LeadActivityType.Updated, $"Demo booked by the AI assistant for {booked.Value.StartUtc:u}", new { by = "ai", booking = booked.Value.BookingId });
        return ToolOutput.Ok(new { booked = true, startUtc = booked.Value.StartUtc, endUtc = booked.Value.EndUtc });
    }

    // ---------------- follow-up ----------------

    private async Task<ToolOutput> FollowUpAsync(Conversation conv, ToolArgsResult a, CancellationToken ct)
    {
        var lead = await LeadAsync(conv, ct); if (lead is null) return NoLead;
        var tenantId = crm.TenantId; var settings = await SettingsAsync(ct);
        var open = await crm.Db.FollowUps.CountAsync(f => f.TenantId == tenantId && f.LeadId == lead.Id && f.Status == FollowUpStatus.Open, ct);
        if (open >= settings.MaxOpenFollowUpsPerLead) return ToolOutput.Deny("too_many_open_follow_ups", "This lead already has the maximum number of open follow-ups.");
        var due = crm.Now.AddHours(a.Int("dueInHours")!.Value);
        var owner = lead.OwnerUserId is { } o && await crm.IsActiveMemberAsync(o, ct) ? o : (Guid?)null;
        var fu = FollowUp.Create(tenantId, lead.Id, conv.Id, owner, due, PromptGuard.Neutralize(a.Str("note"), 500, escapeAngles: false), ActorKind.Ai, crm.Now);
        crm.Db.FollowUps.Add(fu);
        crm.Activity(lead.Id, LeadActivityType.Updated, $"AI assistant scheduled a follow-up for {due:u}", new { by = "ai", followUpId = fu.Id });
        return ToolOutput.Ok(new { scheduled = true, dueAtUtc = due, assignedToOwner = owner is not null, note = "An internal task was created. Nothing is sent to the customer automatically." });
    }

    // ---------------- sensitive actions ----------------

    private async Task<ToolOutput> TrialAsync(Conversation conv, ToolArgsResult a, ToolCallContext ctx, CancellationToken ct)
    {
        var lead = await LeadAsync(conv, ct); if (lead is null) return NoLead;
        if (conv.OptedOutAtUtc is not null) return ToolOutput.Deny("opted_out", "The customer opted out.");
        var tenantId = crm.TenantId; var settings = await SettingsAsync(ct);
        var done = await crm.Db.AiToolExecutions.CountAsync(e => e.TenantId == tenantId && e.LeadId == lead.Id && e.ToolName == AgentTools.CreateTrialAccount && e.Outcome == ToolOutcome.Executed, ct);
        if (done >= settings.MaxTrialsPerLead && !ctx.Approved)
            return ToolOutput.NeedsApproval("trial_limit_reached", $"Trial account for '{lead.Name}' beyond the limit of {settings.MaxTrialsPerLead} per lead ({done} already created).");
        var r = await trials.CreateAsync(lead.Id, a.Str("plan"), a.Str("notes"), ct);
        if (!r.Available) return ToolOutput.NotAvailable("Trial accounts cannot be created yet (the trial service is not connected). Nothing was created; say a colleague will set up the trial.");
        if (r.Error is not null || r.Value is null) return new ToolOutput(ToolOutcome.Failed, "trial_failed", new { message = "The trial could not be created." });
        crm.Activity(lead.Id, LeadActivityType.Updated, "Trial account created (requested by the AI assistant)", new { by = "ai", trial = r.Value.TrialId });
        return ToolOutput.Ok(new { created = true, status = r.Value.Status, expiresAtUtc = r.Value.ExpiresAtUtc, note = r.Value.Message });
    }

    private async Task<ToolOutput> PaymentAsync(Conversation conv, ToolArgsResult a, ToolCallContext ctx, CancellationToken ct)
    {
        var lead = await LeadAsync(conv, ct); if (lead is null) return NoLead;
        if (conv.OptedOutAtUtc is not null) return ToolOutput.Deny("opted_out", "The customer opted out.");
        var tenantId = crm.TenantId; var planName = a.Str("plan")!;
        var plan = await (from p in crm.Db.PricingPlans.AsNoTracking()
                          join pr in crm.Db.CatalogProducts.AsNoTracking() on p.ProductId equals pr.Id
                          where p.TenantId == tenantId && pr.TenantId == tenantId && p.IsActive && p.IsConfirmed && pr.IsActive && pr.IsConfirmed && p.Name == planName && p.Price != null
                          select p).FirstOrDefaultAsync(ct);
        if (plan is null) return ToolOutput.Invalid("unknown_plan", "There is no confirmed plan with that name and price.");
        var discount = a.Dec("discountPercent"); var terms = a.Str("customTerms");
        if (!ctx.Approved)
        {
            var why = discount is > 0 && (plan.MaxDiscountPercent is null || discount > plan.MaxDiscountPercent) ? "discount_above_limit" : discount is > 0 ? "discount_requested" : terms is not null ? "unusual_commitment" : "payment_request";
            return ToolOutput.NeedsApproval(why, $"Payment request for '{lead.Name}': plan {plan.Name} ({plan.Price:0.##} {plan.Currency}){(discount is > 0 ? $", discount {discount:0.##}% requested" : "")}{(terms is not null ? $", custom terms: {terms}" : "")}.");
        }
        var r = await payments.CreateAsync(lead.Id, plan.Name, plan.Price!.Value, plan.Currency, discount, terms, ct);
        if (!r.Available) return ToolOutput.NotAvailable("Payment requests cannot be created yet (the payment service is not connected). Nothing was created or charged.");
        if (r.Error is not null || r.Value is null) return new ToolOutput(ToolOutcome.Failed, "payment_request_failed", new { message = "The payment request could not be created." });
        crm.Activity(lead.Id, LeadActivityType.Updated, $"Payment request created for plan {plan.Name} (approved by a person)", new { by = "ai", request = r.Value.RequestId });
        return ToolOutput.Ok(new { created = true, note = "A payment REQUEST exists. No payment was received and nothing is activated until a verified payment confirmation arrives." });
    }

    // ---------------- handover and compliance ----------------

    private async Task<ToolOutput> TransferAsync(Conversation conv, ToolArgsResult a, ToolCallContext ctx, CancellationToken ct)
    {
        var reason = PromptGuard.Neutralize(a.Str("reason"), 200, escapeAngles: false);
        var summary = a.Str("summary") is { } sm ? PromptGuard.Neutralize(sm, 500, escapeAngles: false) : null;
        var (assigned, _) = await HandoffActions.EscalateAsync(crm, conv, await LeadIdOfAsync(conv, ct), reason, summary, ActorKind.Ai, ct);
        return ToolOutput.Ok(new { handedOver = true, assigned, note = "A person will take over. You will not answer this conversation any more." });
    }

    private async Task<ToolOutput> StopAsync(Conversation conv, ToolArgsResult a, ToolCallContext ctx, CancellationToken ct)
    {
        var from = conv.AiMode.ToString();
        await optOut.ApplyAsync(conv, "Opt-out requested by the customer (recognised by the AI assistant)", ctx.TriggerMessageId?.ToString("N"), "Recorded through the AI tool StopMarketingMessages", ct);
        conv.SetAiMode(AiMode.Paused, crm.Now);
        crm.Db.HandoffEvents.Add(HandoffEvent.Create(crm.TenantId, conv.Id, HandoffKind.OptOut, ActorKind.Ai, null, null, a.Str("reason"), from, AiMode.Paused.ToString(), crm.Now));
        await crm.Db.SaveChangesAsync(ct);
        await ConversationService.WithdrawQueuedAiMessagesAsync(crm, conv.Id, "opted_out", "The customer opted out before this reply was sent.", ct);
        return ToolOutput.Ok(new { stopped = true, note = "The customer will receive no further messages. Do not write a reply." });
    }
}
