namespace LeadPilot.Domain.Campaigns;

/// <summary>How a campaign starts and who it targets. Manual: started by a person. Scheduled: starts at a UTC time. FollowUp / ReEngagement: audience derived from the outcomes of a parent campaign (or, for re-engagement, from inactivity filters).</summary>
public enum CampaignType { Manual = 1, Scheduled = 2, FollowUp = 3, ReEngagement = 4 }

/// <summary>Delivery channel. Only WhatsApp exists; <c>ICampaignChannel</c> is the extension point (no placeholder implementations for other channels).</summary>
public enum CampaignChannel { WhatsApp = 1 }

public enum CampaignStatus { Draft = 1, PendingApproval = 2, Scheduled = 3, Running = 4, Paused = 5, Completed = 6, Cancelled = 7, Failed = 8 }

/// <summary>Pending → (Skipped | Queued → Sent → Delivered → Read → Replied | Failed | Cancelled). A paused campaign sends a Queued recipient back to Pending.</summary>
public enum RecipientStatus { Pending = 1, Skipped = 2, Queued = 3, Sent = 4, Delivered = 5, Read = 6, Replied = 7, Failed = 8, Cancelled = 9 }

public enum ExecutionLogLevel { Info = 1, Warning = 2, Error = 3 }

/// <summary>Reply: an inbound message inside the attribution window. StageChange: the lead moved to another pipeline stage inside the window.</summary>
public enum ConversionKind { Reply = 1, StageChange = 2 }

public enum SnapshotStatus { Building = 1, Ready = 2, Failed = 3 }

/// <summary>Rule applied to the recipients of a parent campaign when a follow-up / re-engagement campaign builds its audience.</summary>
public enum FollowUpRule
{
    /// <summary>Message was sent but the person never replied (any delivery state), N days after sending.</summary>
    SentNotReplied = 1,
    /// <summary>Delivered or read but no reply, N days after delivery.</summary>
    DeliveredNotReplied = 2,
    /// <summary>Read but no reply, N days after reading.</summary>
    ReadNotReplied = 3,
    /// <summary>Replied, but the lead is not (yet) in one of the chosen "advanced" stages (e.g. a "Demo booked" stage), N days after the reply.</summary>
    RepliedNotAdvanced = 4,
}
