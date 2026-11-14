namespace LeadPilot.Application.Analytics;

/// <summary>Stable keys of the recorded-event streams that are rolled up per day (see docs/METRICS.md).</summary>
public static class StreamKeys
{
    public const string LeadsCreated = "leads.created";
    public const string LeadsContacted = "leads.contacted";
    public const string Responses = "messages.inbound";
    public const string MessagesSent = "messages.sent";
    public const string MessagesDelivered = "messages.delivered";
    public const string MessagesFailed = "messages.failed";
    public const string DemosScheduled = "demos.scheduled";
    public const string DemosCompleted = "demos.completed";
    public const string TrialsStarted = "trials.started";
    public const string CustomersWon = "customers.won";
    public const string OpportunitiesLost = "opportunities.lost";
    public const string AiConversations = "ai.conversations";
    public const string Handoffs = "handoffs.human";
    public const string CampaignSent = "campaigns.sent";
    public const string CampaignReplied = "campaigns.replied";
    public const string Revenue = "revenue.verified";

    public static readonly IReadOnlyList<string> All =
    [
        LeadsCreated, LeadsContacted, Responses, MessagesSent, MessagesDelivered, MessagesFailed, DemosScheduled, DemosCompleted, TrialsStarted, CustomersWon, OpportunitiesLost,
        AiConversations, Handoffs, CampaignSent, CampaignReplied, Revenue
    ];
}

/// <summary>Every dashboard metric with its label, definition and sources. The text here is what the UI shows in tooltips and what docs/METRICS.md documents; a unit test keeps the two in step.</summary>
public static class MetricCatalog
{
    public const string TotalLeads = "leads.total", VerifiedLeads = "leads.verified", EligibleLeads = "leads.eligible", InterestedLeads = "leads.interested", QualifiedLeads = "leads.qualified",
        DeliveryRate = "rate.delivery", ConversionRate = "rate.lead_conversion", CampaignReplyRate = "rate.campaign_reply";

    private const string Snap = "Current state — not affected by the date filter.";

    public static readonly IReadOnlyList<MetricDefinition> All =
    [
        new(TotalLeads, "Total leads", MetricKind.Verified, "Leads that exist and are not archived, right now. " + Snap, "Leads", null, "count", true, true, "/Leads"),
        new(StreamKeys.LeadsCreated, "New leads", MetricKind.Verified, "Leads created in the period (counted by creation time, including ones archived later).", "Leads.CreatedAtUtc", null, "count", false, true, "/Leads?CreatedFromUtc={fromUtc}&CreatedToUtc={toUtc}&Archived=All"),
        new(VerifiedLeads, "Verified leads", MetricKind.Verified, "Non-archived leads with at least one non-deleted contact whose data origin is Verified or whose phone was verified by the team. " + Snap, "Leads, Contacts.DataOrigin, Contacts.PhoneVerifiedAtUtc", null, "count", true, true),
        new(EligibleLeads, "Contact-eligible leads", MetricKind.Calculated, "Non-archived leads, not in a Suppressed pipeline stage, with a non-deleted contact that has a phone number, is not on the suppression list (WhatsApp or any channel) and whose latest WhatsApp marketing consent decision is Granted (a revocation wins a tie). Same rules as the audience builder (ConsentService.QueryAudienceAsync); the send-time pipeline additionally checks the account, limits and templates. " + Snap, "Leads, PipelineStages, Contacts, ConsentRecords, SuppressionEntries", "consent granted AND NOT suppressed AND stage is not Suppressed", "count", true, true),
        new(StreamKeys.LeadsContacted, "Contacted leads", MetricKind.Verified, "Leads whose FIRST outbound message was sent in the period (each lead counts once, ever).", "Messages (Direction=Outbound, SentAtUtc) → Contacts.LeadId", null, "count", false, true),
        new(StreamKeys.Responses, "Responses", MetricKind.Verified, "Inbound messages from known contacts received in the period.", "Messages (Direction=Inbound, OccurredAtUtc, ContactId set)", null, "count", false, true),
        new(InterestedLeads, "Interested prospects", MetricKind.Calculated, "Non-archived leads that the AI qualified as Possible or Good fit, or whose conversation had a pricing, demo-request or trial-request intent. " + Snap, "LeadQualifications.Fit, AiInteractions.Intent → Conversations → Contacts", "fit ∈ {Possible, Good} OR intent ∈ {pricing, demo_request, trial_request}", "count", true, true),
        new(QualifiedLeads, "Qualified leads", MetricKind.Verified, "Non-archived leads whose recorded qualification fit is Good. " + Snap, "LeadQualifications.Fit", null, "count", true, true),
        new(StreamKeys.DemosScheduled, "Demos scheduled", MetricKind.Verified, "Demo bookings created in the period (any status afterwards).", "Demos.CreatedAtUtc", null, "count", false, true),
        new(StreamKeys.DemosCompleted, "Demos completed", MetricKind.Verified, "Demos marked completed in the period.", "Demos.CompletedAtUtc", null, "count", false, true),
        new(StreamKeys.TrialsStarted, "Trials", MetricKind.Verified, "Trials created in the period.", "Trials.CreatedAtUtc", null, "count", false, true),
        new(StreamKeys.CustomersWon, "Customers won", MetricKind.Verified, "Leads converted to customers in the period.", "Customers.ConvertedAtUtc", null, "count", false, true),
        new(StreamKeys.OpportunitiesLost, "Lost opportunities", MetricKind.Verified, "Opportunities whose status is Lost, counted at their last change (the move to a Lost stage).", "Opportunities (Status=Lost, UpdatedAtUtc)", null, "count", false, true),
        new(StreamKeys.CampaignSent, "Campaign messages sent", MetricKind.Verified, "Campaign recipients whose message was sent in the period.", "CampaignRecipients.SentAtUtc", null, "count", false, true),
        new(CampaignReplyRate, "Campaign reply rate", MetricKind.Calculated, "Share of campaign messages sent in the period that received a reply.", "CampaignRecipients (SentAtUtc, RepliedAtUtc)", "replied ÷ sent × 100 (no rate when nothing was sent)", "percent", false, true),
        new(StreamKeys.AiConversations, "AI conversations", MetricKind.Verified, "Conversations in which the AI first acted (recorded an interaction) in the period; each conversation counts once, ever.", "AiInteractions.CreatedAtUtc by conversation", null, "count", false, true),
        new(StreamKeys.Handoffs, "Human handoffs", MetricKind.Verified, "Conversations handed to a person in the period: takeover, escalation or automatic takeover.", "HandoffEvents (Takeover, Escalated, AutoTakeover)", null, "count", false, true),
        new(DeliveryRate, "Message delivery rate", MetricKind.Calculated, "Share of outbound messages sent in the period that the gateway reported delivered (or read). Receipts that arrive later count as soon as the rollup is refreshed.", "Messages (SentAtUtc, DeliveredAtUtc)", "delivered ÷ sent × 100 (no rate when nothing was sent)", "percent", false, true),
        new(ConversionRate, "Lead conversion rate", MetricKind.Calculated, "Of the leads created in the period, the share that is (today) a customer. A cohort rate: later conversions of the same leads still count.", "Leads, Customers", "leads created in the period that have a customer ÷ leads created in the period × 100", "percent", false, true),
        new(StreamKeys.Revenue, "Verified revenue", MetricKind.Verified, "Payments verified by staff or by a signed provider event, per currency. Never added across currencies; unverified payment requests are not included.", "Payments (PaidAtUtc, Amount, Currency)", null, "money", false, false),
        new(StreamKeys.MessagesSent, "Messages sent", MetricKind.Verified, "Outbound messages the gateway accepted (status Sent or later) in the period.", "Messages.SentAtUtc", null, "count", false, true),
        new(StreamKeys.MessagesDelivered, "Messages delivered", MetricKind.Verified, "Of the messages sent in the period, those with a delivered or read receipt.", "Messages.DeliveredAtUtc", null, "count", false, true),
        new(StreamKeys.MessagesFailed, "Messages failed", MetricKind.Verified, "Of the messages that were attempted in the period, those that failed.", "Messages.FailedAtUtc", null, "count", false, true),
    ];

    public static MetricDefinition Get(string key) => All.First(m => m.Key == key);
}

public static class ReportKeys
{
    public const string DiscoveryByCity = "discovery-by-city", LeadSources = "lead-sources", Campaigns = "campaigns", Pipeline = "sales-pipeline", Funnel = "conversion-funnel",
        Reps = "rep-performance", AiAgent = "ai-agent", Demos = "demo-conversion", Trials = "trial-conversion", Revenue = "subscription-revenue", Messaging = "messaging-usage";
}

/// <summary>The reports and their columns (label kind + definition per column). Row logic lives in Infrastructure; this is the contract the UI, API, export and docs share.</summary>
public static class ReportCatalog
{
    private static ReportColumnDto C(string key, string label, MetricKind kind, string def, string format = "int") => new(key, label, kind, def, format);
    private static readonly MetricKind V = MetricKind.Verified, Ca = MetricKind.Calculated, E = MetricKind.Estimated;

    public static readonly IReadOnlyList<ReportDefinitionDto> All =
    [
        new(ReportKeys.DiscoveryByCity, "Lead discovery by city", "What the approved discovery searches found, by the city of each result.", true, true,
        [
            C("city", "City", V, "City recorded on the discovery result ('(no city)' when empty).", "text"),
            C("found", "Results found", V, "Discovery results retrieved in the period.", "int"),
            C("approved", "Approved", V, "Results a person approved.", "int"),
            C("rejected", "Rejected", V, "Results a person rejected.", "int"),
            C("pending", "Pending review", V, "Results not yet reviewed.", "int"),
            C("leads", "Leads created", V, "Results that became a lead.", "int"),
            C("duplicates", "Duplicates flagged", V, "Results matching an existing lead.", "int"),
            C("jobs", "Searches", V, "Discovery jobs created in the period for this city.", "int"),
            C("calls", "Provider calls", V, "API calls recorded by those jobs.", "int"),
            C("cost", "Est. cost (USD)", E, "Sum of the estimated provider cost of those jobs (a provider price assumption, not an invoice).", "money"),
        ]),
        new(ReportKeys.LeadSources, "Lead source performance", "How leads created in the period, grouped by their primary source, progressed.", true, true,
        [
            C("source", "Source", V, "Primary source type of the lead.", "text"),
            C("leads", "Leads", V, "Leads created in the period.", "int"),
            C("qualified", "Qualified", V, "Of those, leads whose qualification fit is Good.", "int"),
            C("customers", "Customers", V, "Of those, leads that are customers today.", "int"),
            C("conversion", "Conversion rate", Ca, "customers ÷ leads × 100.", "percent"),
        ]),
        new(ReportKeys.Campaigns, "Campaign performance", "Campaigns started in the period with their recomputed delivery statistics.", true, true,
        [
            C("name", "Campaign", V, "Campaign name.", "text"), C("status", "Status", V, "Current campaign status.", "text"), C("started", "Started (UTC)", V, "When the campaign started sending.", "date"),
            C("recipients", "Recipients", V, "Recipients in the audience snapshot.", "int"), C("sent", "Sent", V, "Recipients whose message reached the gateway (sent, delivered, read or replied).", "int"),
            C("delivered", "Delivered", V, "Delivered, read or replied.", "int"), C("replied", "Replied", V, "Recipients who replied.", "int"),
            C("failed", "Failed", V, "Failed sends.", "int"), C("conversions", "Conversions", V, "Replies and stage changes attributed within the attribution window.", "int"),
            C("deliveryRate", "Delivery rate", Ca, "delivered ÷ sent × 100.", "percent"), C("replyRate", "Reply rate", Ca, "replied ÷ sent × 100.", "percent"),
        ]),
        new(ReportKeys.Pipeline, "Sales pipeline", "Where today's non-archived leads and open opportunities are, by pipeline stage.", false, false,
        [
            C("pipeline", "Pipeline", V, "Pipeline name.", "text"), C("stage", "Stage", V, "Stage name.", "text"), C("kind", "Stage type", V, "Open, Won, Lost or Suppressed.", "text"),
            C("leads", "Leads", V, "Non-archived leads in the stage.", "int"), C("opportunities", "Open opportunities", V, "Opportunities in the stage with status Open.", "int"),
            C("value", "Open value", V, "Sum of open opportunity values per currency (never added across currencies).", "text"),
        ]),
        new(ReportKeys.Funnel, "Conversion funnel", "Leads created in the period and how many reached each step (at any time since).", false, true,
        [
            C("step", "Step", V, "Funnel step.", "text"), C("count", "Leads", V, "Leads of the cohort that reached the step.", "int"),
            C("ofPrevious", "% of previous step", Ca, "count ÷ count of the step above × 100.", "percent"), C("ofCohort", "% of leads created", Ca, "count ÷ leads created × 100.", "percent"),
        ]),
        new(ReportKeys.Reps, "Sales representative performance", "Work recorded per person in the period (managers see the team, everyone else only their own row).", false, true,
        [
            C("rep", "Representative", V, "User name.", "text"), C("owned", "Leads owned", V, "Non-archived leads owned today.", "int"), C("messages", "Messages sent", V, "Outbound messages created by this person (not AI) and sent in the period.", "int"),
            C("demosCompleted", "Demos completed", V, "Demos run by this person, completed in the period.", "int"), C("noShows", "Demo no-shows", V, "Demos of this person marked no-show that were scheduled to start in the period.", "int"),
            C("won", "Opportunities won", V, "Opportunities owned by this person that became Won in the period.", "int"), C("lost", "Opportunities lost", V, "Opportunities owned by this person that became Lost in the period.", "int"),
            C("wonValue", "Won value", V, "Sum of the value of those won opportunities per currency (never added across currencies).", "text"),
        ]),
        new(ReportKeys.AiAgent, "AI agent performance", "What the AI did in the period, by sales agent (replies without an agent are the plain knowledge assistant).", true, true,
        [
            C("agent", "Agent", V, "Sales agent name.", "text"), C("interactions", "Interactions", V, "AI interactions recorded.", "int"), C("drafts", "Drafts", V, "Interactions that produced a reply draft.", "int"),
            C("escalations", "Escalations", V, "Interactions that escalated to a person.", "int"), C("noReply", "No reply", V, "Interactions that decided not to reply.", "int"),
            C("autoSent", "Auto-sent", V, "Drafts sent automatically.", "int"), C("used", "Used by staff", V, "Drafts a person used.", "int"), C("dismissed", "Dismissed", V, "Drafts a person dismissed.", "int"),
            C("injection", "Injection suspected", V, "Interactions flagged as possible prompt injection.", "int"), C("latency", "Avg latency (ms)", V, "Mean recorded model latency.", "int"),
            C("toolsExecuted", "Tools executed", V, "Tool calls that ran.", "int"), C("toolsRefused", "Tools denied/failed", V, "Tool calls denied, failed or invalid.", "int"),
            C("autoRate", "Auto-send rate", Ca, "auto-sent ÷ drafts × 100.", "percent"), C("escRate", "Escalation rate", Ca, "escalations ÷ interactions × 100.", "percent"),
        ]),
        new(ReportKeys.Demos, "Demo conversion", "Demos booked in the period and what became of them.", false, true,
        [C("step", "Step", V, "Outcome.", "text"), C("count", "Count", V, "Demos (or leads) in the outcome.", "int"), C("rate", "Rate", Ca, "Stated in the basis column.", "percent"), C("basis", "Basis", Ca, "How the rate is calculated.", "text")]),
        new(ReportKeys.Trials, "Trial conversion", "Trials started in the period and their outcomes.", false, true,
        [C("step", "Step", V, "Outcome.", "text"), C("count", "Trials", V, "Trials in the outcome.", "int"), C("rate", "Rate", Ca, "Stated in the basis column.", "percent"), C("basis", "Basis", Ca, "How the rate is calculated.", "text")]),
        new(ReportKeys.Revenue, "Subscription revenue", "Verified payments received in the period, per currency. Payment requests that were never verified are not revenue.", true, true,
        [
            C("currency", "Currency", V, "Currency of the payments.", "text"), C("payments", "Payments", V, "Verified payments.", "int"), C("customers", "Paying customers", V, "Distinct customers who paid.", "int"),
            C("revenue", "Revenue", V, "Sum of verified payment amounts in this currency.", "money"), C("average", "Average payment", Ca, "revenue ÷ payments.", "money"),
        ]),
        new(ReportKeys.Messaging, "Messaging usage", "Messages per day in the workspace time zone.", true, true,
        [
            C("day", "Day", V, "Workspace-local calendar day.", "text"), C("sent", "Sent", V, "Outbound messages sent.", "int"), C("delivered", "Delivered", V, "Of those, delivered or read.", "int"),
            C("failed", "Failed", V, "Failed attempts.", "int"), C("inbound", "Inbound", V, "Messages received from known contacts.", "int"), C("deliveryRate", "Delivery rate", Ca, "delivered ÷ sent × 100.", "percent"),
        ]),
    ];

    public static ReportDefinitionDto? Find(string key) => All.FirstOrDefault(r => r.Key == key);
}
