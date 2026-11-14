using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Ai;
using LeadPilot.Domain.Campaigns;
using LeadPilot.Domain.Conversion;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Discovery;
using LeadPilot.Domain.Messaging;

namespace LeadPilot.IntegrationTests;

/// <summary>
/// The deterministic data set of the reconciliation tests (workspace zone Asia/Karachi = UTC+5). RANGE = [D0, D0+1] (two local days, both inclusive); PREVIOUS = [D0-2, D0-1].
/// Edge cases on purpose: events at local 00:00:00 (belongs to the NEW day), at 23:59:59 (the old day) and at D0+2 00:00:00 (outside the range); an archived lead; a soft-deleted contact; a suppressed number; a revoked consent;
/// duplicate events (several messages to one lead, two AI interactions in one conversation); inbound from an unknown number; payments in two currencies plus a payment request that was never verified.
/// </summary>
public sealed class Scenario
{
    public required AnalyticsWorld W { get; init; }
    public DateOnly D0 { get; init; }
    public DateOnly D1 => D0.AddDays(1);
    public Lead A1 = null!, A2 = null!, A3 = null!, A4 = null!, A5 = null!, A6 = null!, A7 = null!;
    public Customer Cust1 = null!;
    public Guid AgentId, ManagerId;

    public DateOnly From => D0; public DateOnly To => D1;
    public DateTime FromUtc => W.L(D0); public DateTime ToUtc => W.L(D0.AddDays(2));
    public DateTime PrevFromUtc => W.L(D0.AddDays(-2)); public DateTime PrevToUtc => W.L(D0);

    public static async Task<Scenario> SeedAsync(AnalyticsWorld w, int daysAgo = 40)
    {
        var s = new Scenario { W = w, D0 = w.Today.AddDays(-daysAgo), AgentId = w.Agent.Id, ManagerId = w.Manager.Id };
        var d = s.D0; DateTime T(int dayOffset, int h = 0, int m = 0, int sec = 0) => w.L(d.AddDays(dayOffset), h, m, sec);
        var agent = w.Agent.Id; var mgr = w.Manager.Id;

        await w.SeedAsync(async db =>
        {
            // leads ------------------------------------------------------------
            s.A1 = w.NewLead("A1 Alpha School", T(0), agent, "Lahore", LeadSourceType.Manual);                          // local midnight: belongs to D0
            s.A2 = w.NewLead("A2 Beta School", T(-1, 23, 59, 59), agent, "Lahore", LeadSourceType.Manual);              // one second before: previous period
            s.A3 = w.NewLead("A3 Gamma School", T(0, 12), mgr, "Lahore", LeadSourceType.Discovery);
            s.A4 = w.NewLead("A4 Delta School", T(1), agent, "Karachi", LeadSourceType.Import);                         // local midnight of D1
            s.A5 = w.NewLead("A5 Epsilon School", T(1, 10), agent, "Karachi", LeadSourceType.Manual, archived: true);   // archived: counted as created, not in totals
            s.A6 = w.NewLead("A6 Zeta School", T(2), mgr, null, LeadSourceType.Api);                                    // local midnight of D0+2: outside the range
            s.A7 = w.NewLead("A7 Eta School", T(-2, 8), agent, "Karachi", LeadSourceType.Referral, stage: w.WonStageId);
            db.Leads.AddRange(s.A1, s.A2, s.A3, s.A4, s.A5, s.A6, s.A7);

            // contacts ---------------------------------------------------------
            var c1 = w.NewContact(s.A1, DataOrigin.Verified); var c1x = w.NewContact(s.A1, DataOrigin.Public);
            var c2 = w.NewContact(s.A2); var c3 = w.NewContact(s.A3, DataOrigin.Public); var c3x = w.NewContact(s.A3, DataOrigin.Verified, deleted: true);
            var c4 = w.NewContact(s.A4, DataOrigin.Provided, phoneVerified: T(1, 5)); var c5 = w.NewContact(s.A5, DataOrigin.Verified); var c6 = w.NewContact(s.A6); var c7 = w.NewContact(s.A7);
            db.Contacts.AddRange(c1, c1x, c2, c3, c3x, c4, c5, c6, c7);

            // consent / suppression -------------------------------------------
            db.ConsentRecords.AddRange(w.Consent(c1, ConsentStatus.Granted, T(-5)), w.Consent(c2, ConsentStatus.Granted, T(-5)), w.Consent(c3, ConsentStatus.Granted, T(-5)), w.Consent(c3, ConsentStatus.Revoked, T(-4)),
                w.Consent(c4, ConsentStatus.Granted, T(-5)), w.Consent(c5, ConsentStatus.Granted, T(-5)), w.Consent(c6, ConsentStatus.Granted, T(-5)), w.Consent(c7, ConsentStatus.Granted, T(-5), ConsentChannel.Email));
            db.SuppressionEntries.Add(w.M<SuppressionEntry>(("Channel", SuppressionChannel.WhatsApp), ("ValueNormalized", c4.PhoneE164), ("ValueHash", "h"), ("Reason", SuppressionReason.OptOut), ("CreatedAtUtc", T(-3))));

            // conversations + messages ----------------------------------------
            var v1 = w.Conv(c1); var v2 = w.Conv(c2); var v3 = w.Conv(c3); var v4 = w.Conv(c4); var vx = w.Conv(null);
            db.Conversations.AddRange(v1, v2, v3, v4, vx);
            db.Messages.AddRange(
                w.Out(v1, c1, T(-2, 9), T(-2, 9, 5), by: mgr),                      // A1's FIRST contact is in the previous period
                w.Out(v1, c1, T(0, 9), T(0, 9, 5), by: agent),                      // sent + delivered in range (duplicate contact of one lead)
                w.Out(v1, c1, T(1, 9), ai: true),                                   // sent, never delivered, AI generated
                w.Out(v3, c3, T(1), T(1, 0, 1), by: mgr),                           // sent at local midnight of D1 (first contact of A3), delivered
                w.Out(v4, c4, null, failed: T(0, 10), by: agent),                   // failed attempt
                w.Out(v2, c2, T(-1, 23, 59, 59), T(-1, 23, 59, 59).AddSeconds(2)),  // previous period, first contact of A2
                w.In(v1, c1, T(0, 11)), w.In(v1, c1, T(1, 12)), w.In(v3, c3, T(2)), w.In(vx, null, T(0, 12)), w.In(v2, c2, T(-1, 12)));

            // AI -----------------------------------------------------------------
            var sa = w.M<SalesAgent>(("Name", "Sales agent")); db.SalesAgents.Add(sa);
            AiInteraction Ai(Conversation cv, DateTime at, string intent, AiDecision dec, DraftStatus st, Guid? agentId = null, int latency = 100, bool inj = false) =>
                w.M<AiInteraction>(("ConversationId", cv.Id), ("TriggerMessageId", Guid.NewGuid()), ("Intent", intent), ("Decision", dec), ("Status", st), ("AgentId", agentId), ("LatencyMs", latency), ("InjectionSuspected", inj), ("CreatedAtUtc", at), ("Model", "m"));
            db.AiInteractions.AddRange(Ai(v1, T(0, 9), "pricing", AiDecision.Draft, DraftStatus.AutoSent, sa.Id, 200), Ai(v1, T(1, 9), "pricing", AiDecision.Draft, DraftStatus.Used, sa.Id, 400),
                Ai(v3, T(1, 10), "greeting", AiDecision.Escalate, DraftStatus.Pending, null, 300, inj: true), Ai(v4, T(-1, 9), "demo_request", AiDecision.Draft, DraftStatus.Dismissed));
            db.AiToolExecutions.AddRange(w.M<AiToolExecution>(("ToolName", "BookDemo"), ("Outcome", ToolOutcome.Executed), ("AgentId", sa.Id), ("OccurredAtUtc", T(0, 9))),
                w.M<AiToolExecution>(("ToolName", "CreateTrialAccount"), ("Outcome", ToolOutcome.Denied), ("AgentId", sa.Id), ("OccurredAtUtc", T(1, 9))));
            db.AiUsageEntries.AddRange(w.M<AiUsageEntry>(("Operation", "chat"), ("Provider", "claude"), ("Model", "m"), ("InputTokens", 1000), ("OutputTokens", 200), ("EstimatedCostUsd", 0.0123m), ("Success", true), ("PeriodKey", 202601), ("OccurredAtUtc", T(0, 9))),
                w.M<AiUsageEntry>(("Operation", "chat"), ("Provider", "claude"), ("Model", "m"), ("InputTokens", 500), ("OutputTokens", 100), ("EstimatedCostUsd", 0.0100m), ("Success", true), ("PeriodKey", 202601), ("OccurredAtUtc", T(-1, 9))));
            db.LeadQualifications.AddRange(
                w.M<LeadQualification>(("LeadId", s.A1.Id), ("Fit", QualificationFit.Good), ("UpdatedAtUtc", T(0, 9))), w.M<LeadQualification>(("LeadId", s.A3.Id), ("Fit", QualificationFit.Possible), ("UpdatedAtUtc", T(0, 9))),
                w.M<LeadQualification>(("LeadId", s.A4.Id), ("Fit", QualificationFit.Poor), ("UpdatedAtUtc", T(0, 9))), w.M<LeadQualification>(("LeadId", s.A7.Id), ("Fit", QualificationFit.Good), ("UpdatedAtUtc", T(0, 9))));
            db.HandoffEvents.AddRange(
                w.M<HandoffEvent>(("ConversationId", v1.Id), ("Kind", HandoffKind.Takeover), ("Actor", ActorKind.User), ("OccurredAtUtc", T(0, 10))),
                w.M<HandoffEvent>(("ConversationId", v1.Id), ("Kind", HandoffKind.Escalated), ("Actor", ActorKind.Ai), ("OccurredAtUtc", T(1, 10))),
                w.M<HandoffEvent>(("ConversationId", v3.Id), ("Kind", HandoffKind.AiPaused), ("Actor", ActorKind.User), ("OccurredAtUtc", T(0, 11))),      // not a human handoff
                w.M<HandoffEvent>(("ConversationId", v4.Id), ("Kind", HandoffKind.AutoTakeover), ("Actor", ActorKind.System), ("OccurredAtUtc", T(1))),
                w.M<HandoffEvent>(("ConversationId", v1.Id), ("Kind", HandoffKind.Takeover), ("Actor", ActorKind.User), ("OccurredAtUtc", T(2))));            // outside

            // sales ---------------------------------------------------------------
            Demo Dm(Lead l, Guid rep, DateTime created, DemoStatus st, DateTime start, DateTime? done = null) =>
                w.M<Demo>(("LeadId", l.Id), ("RepUserId", rep), ("StartUtc", start), ("EndUtc", start.AddHours(1)), ("Status", st), ("CreatedAtUtc", created), ("CompletedAtUtc", done));
            db.Demos.AddRange(Dm(s.A1, agent, T(0, 13), DemoStatus.Completed, T(1, 14), T(1, 15)), Dm(s.A3, mgr, T(1, 8), DemoStatus.Booked, T(3, 10)), Dm(s.A4, agent, T(-1, 23, 59, 59), DemoStatus.NoShow, T(1, 10)), Dm(s.A1, agent, T(2), DemoStatus.Cancelled, T(5, 10)));
            Trial Tr(Lead l, DateTime created, TrialStatus st) => w.M<Trial>(("LeadId", l.Id), ("Status", st), ("CreatedAtUtc", created), ("DurationDays", 14));
            db.Trials.AddRange(Tr(s.A1, T(1, 16), TrialStatus.Converted), Tr(s.A3, T(0, 14), TrialStatus.Expired), Tr(s.A2, T(-1, 12), TrialStatus.Cancelled));
            s.Cust1 = w.M<Customer>(("Number", 1), ("LeadId", s.A1.Id), ("Name", "A1 Alpha School"), ("Status", CustomerStatus.Active), ("ConvertedAtUtc", T(1, 17)), ("CreatedAtUtc", T(1, 17)));
            var cust2 = w.M<Customer>(("Number", 2), ("LeadId", s.A7.Id), ("Name", "A7 Eta School"), ("Status", CustomerStatus.Active), ("ConvertedAtUtc", T(-2, 10)), ("CreatedAtUtc", T(-2, 10)));
            db.Customers.AddRange(s.Cust1, cust2);
            Opportunity Op(Lead l, Guid owner, decimal v, string cur, OpportunityStatus st, Guid stage, DateTime updated) =>
                w.M<Opportunity>(("LeadId", l.Id), ("PipelineStageId", stage), ("Name", "Deal"), ("Value", v), ("Currency", cur), ("OwnerUserId", owner), ("Status", st), ("CreatedAtUtc", updated.AddDays(-1)), ("UpdatedAtUtc", updated));
            db.Opportunities.AddRange(Op(s.A1, agent, 100m, "USD", OpportunityStatus.Lost, w.LostStageId, T(0, 15)), Op(s.A3, mgr, 500m, "PKR", OpportunityStatus.Won, w.WonStageId, T(1, 11)),
                Op(s.A1, agent, 200m, "USD", OpportunityStatus.Open, w.OpenStageId, T(-3)), Op(s.A2, mgr, 80m, "USD", OpportunityStatus.Lost, w.LostStageId, T(-1, 10)));
            Payment Pay(decimal amount, string cur, DateTime paid) =>
                w.M<Payment>(("PaymentRequestId", Guid.NewGuid()), ("CustomerId", s.Cust1.Id), ("Amount", amount), ("Currency", cur), ("EvidenceReference", "r"), ("EvidenceKey", Guid.NewGuid().ToString("N")), ("PaidAtUtc", paid), ("VerifiedAtUtc", paid));
            db.Payments.AddRange(Pay(100m, "USD", T(0, 12)), Pay(50.50m, "USD", T(1, 12)), Pay(20000m, "PKR", T(1, 13)), Pay(999m, "USD", T(2)), Pay(70m, "USD", T(-1, 12)));
            db.PaymentRequests.Add(w.M<PaymentRequest>(("CustomerId", s.Cust1.Id), ("Amount", 5000m), ("Currency", "USD"), ("Reference", "PR-UNVERIFIED"), ("CreatedAtUtc", T(0, 12))));   // never verified: not revenue

            // campaigns -------------------------------------------------------------
            var camp = w.M<Campaign>(("Name", "Spring push"), ("Status", CampaignStatus.Completed), ("StartedAtUtc", T(0, 9)), ("AudienceJson", "{}"), ("CreatedAtUtc", T(-1)));
            var camp2 = w.M<Campaign>(("Name", "Later push"), ("Status", CampaignStatus.Completed), ("StartedAtUtc", T(5, 9)), ("AudienceJson", "{}"), ("CreatedAtUtc", T(-1)));
            db.Campaigns.AddRange(camp, camp2);
            CampaignRecipient Rc(Campaign c, Lead l, Contact ct, DateTime? sent, DateTime? replied) => w.M<CampaignRecipient>(("CampaignId", c.Id), ("SnapshotId", Guid.NewGuid()), ("ContactId", ct.Id), ("LeadId", l.Id), ("Status", replied is not null ? RecipientStatus.Replied : RecipientStatus.Sent), ("SentAtUtc", sent), ("RepliedAtUtc", replied), ("CreatedAtUtc", T(0)));
            db.CampaignRecipients.AddRange(Rc(camp, s.A1, c1, T(0, 9), T(0, 11)), Rc(camp, s.A3, c3, T(1, 9), null), Rc(camp, s.A4, c4, T(2), null), Rc(camp, s.A2, c2, T(-1, 9), null));
            db.CampaignStatsRollups.Add(w.M<CampaignStatsRollup>(("CampaignId", camp.Id), ("Total", 4), ("Sent", 1), ("Delivered", 1), ("Read", 0), ("Replied", 1), ("Failed", 1), ("Conversions", 1), ("UpdatedAtUtc", T(2))));

            // discovery ----------------------------------------------------------------
            var job = w.M<DiscoveryJob>(("ConnectorType", "google_places"), ("Status", DiscoveryJobStatus.Completed), ("City", "Lahore"), ("ApiCalls", 3), ("EstimatedCostUsd", 0.051m), ("CreatedAtUtc", T(0, 8)));
            var job2 = w.M<DiscoveryJob>(("ConnectorType", "google_places"), ("Status", DiscoveryJobStatus.Completed), ("City", "Multan"), ("ApiCalls", 2), ("EstimatedCostUsd", 0.034m), ("CreatedAtUtc", T(1, 8)));
            db.DiscoveryJobs.AddRange(job, job2);
            DiscoveryResult Dr(string ext, string? city, ReviewStatus rs, DateTime at, Guid? lead = null, Guid? dup = null) =>
                w.M<DiscoveryResult>(("JobId", job.Id), ("ConnectorType", "google_places"), ("ExternalId", ext), ("Name", ext), ("City", city), ("ReviewStatus", rs), ("RetrievedAtUtc", at), ("LeadId", lead), ("DuplicateLeadId", dup), ("CreatedAtUtc", at));
            db.DiscoveryResults.AddRange(Dr("r1", "Lahore", ReviewStatus.Approved, T(0, 8), s.A3.Id), Dr("r2", "Lahore", ReviewStatus.Pending, T(0, 8)), Dr("r3", "Karachi", ReviewStatus.Rejected, T(1, 8)), Dr("r4", null, ReviewStatus.Pending, T(1, 9), dup: s.A1.Id),
                Dr("r5", "Lahore", ReviewStatus.Pending, T(2)), Dr("r6", "Lahore", ReviewStatus.Approved, T(-1, 8)));
        });
        return s;
    }
}
