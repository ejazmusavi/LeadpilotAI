using LeadPilot.Domain.Common;

namespace LeadPilot.Domain.Campaigns;

/// <summary>The one definition of which campaign status may follow which. Pure and unit-tested; every mutation (API action, worker, kill switch) goes through <see cref="Ensure"/> or the equivalent guarded SQL update.</summary>
public static class CampaignStateMachine
{
    private static readonly IReadOnlyDictionary<CampaignStatus, CampaignStatus[]> Map = new Dictionary<CampaignStatus, CampaignStatus[]>
    {
        [CampaignStatus.Draft] = [CampaignStatus.PendingApproval, CampaignStatus.Scheduled, CampaignStatus.Cancelled],
        [CampaignStatus.PendingApproval] = [CampaignStatus.Scheduled, CampaignStatus.Draft, CampaignStatus.Cancelled],
        [CampaignStatus.Scheduled] = [CampaignStatus.Running, CampaignStatus.Draft, CampaignStatus.Cancelled],
        [CampaignStatus.Running] = [CampaignStatus.Paused, CampaignStatus.Completed, CampaignStatus.Cancelled, CampaignStatus.Failed],
        [CampaignStatus.Paused] = [CampaignStatus.Running, CampaignStatus.Cancelled],
        [CampaignStatus.Completed] = [],
        [CampaignStatus.Cancelled] = [],
        [CampaignStatus.Failed] = [],
    };

    public static IReadOnlyList<CampaignStatus> Allowed(CampaignStatus from) => Map[from];
    public static bool CanTransition(CampaignStatus from, CampaignStatus to) => Map[from].Contains(to);
    public static bool IsTerminal(CampaignStatus s) => Map[s].Length == 0;
    /// <summary>Statuses in which messages may leave the system.</summary>
    public static bool IsSending(CampaignStatus s) => s == CampaignStatus.Running;
    /// <summary>Statuses whose recipients may still be waiting (used by cool-down and by the kill switch).</summary>
    public static bool IsActive(CampaignStatus s) => s is CampaignStatus.Scheduled or CampaignStatus.Running or CampaignStatus.Paused;

    public static void Ensure(CampaignStatus from, CampaignStatus to)
    {
        if (!CanTransition(from, to)) throw new DomainException($"A campaign that is {from} cannot become {to}.");
    }
}

/// <summary>Recipient status rules: which statuses count as "a message left the system", and the delivery progression (never regresses).</summary>
public static class RecipientRules
{
    /// <summary>The message reached the gateway at least (Sent and everything after, including a reply).</summary>
    public static bool IsMessaged(RecipientStatus s) => s is RecipientStatus.Sent or RecipientStatus.Delivered or RecipientStatus.Read or RecipientStatus.Replied;
    /// <summary>A message was created for the recipient (Queued or beyond, not failed/cancelled/skipped).</summary>
    public static bool IsInFlightOrSent(RecipientStatus s) => s == RecipientStatus.Queued || IsMessaged(s);
    public static bool IsTerminal(RecipientStatus s) => s is RecipientStatus.Skipped or RecipientStatus.Failed or RecipientStatus.Cancelled || IsMessaged(s);
    /// <summary>Position in Sent &lt; Delivered &lt; Read &lt; Replied. Others rank 0.</summary>
    public static int Rank(RecipientStatus s) => s switch { RecipientStatus.Sent => 1, RecipientStatus.Delivered => 2, RecipientStatus.Read => 3, RecipientStatus.Replied => 4, _ => 0 };
    public static bool Advances(RecipientStatus from, RecipientStatus to) => Rank(to) > Rank(from) && (from == RecipientStatus.Queued || Rank(from) > 0);

    public sealed record FailureMapping(RecipientStatus Status, string Reason, bool ReturnToPending, bool NoChange);

    /// <summary>
    /// What a failed campaign message means for its recipient. A pause withdraws the message and the recipient waits again; a cancel / kill switch cancels it; consent, suppression and opt-out
    /// found at send time are skips (protection working), everything else is a failure that counts toward the failure-rate threshold.
    /// </summary>
    public static FailureMapping FromFailure(string? code) => code switch
    {
        "campaign_paused" or "campaign_kill_switch" => new(RecipientStatus.Pending, ExclusionCodes.CampaignStopped, true, false),   // a pause and the kill switch both wait for a person to resume
        "campaign_cancelled" or "campaign_stopped" or "recipient_superseded" => new(RecipientStatus.Cancelled, ExclusionCodes.CampaignStopped, false, false),
        "campaign_skipped" => new(RecipientStatus.Skipped, "", false, true),
        "suppressed" => new(RecipientStatus.Skipped, ExclusionCodes.Suppressed, false, false),
        "opted_out" => new(RecipientStatus.Skipped, ExclusionCodes.OptedOutConversation, false, false),
        "not_eligible" => new(RecipientStatus.Skipped, "not_eligible", false, false),
        _ => new(RecipientStatus.Failed, string.IsNullOrWhiteSpace(code) ? "send_failed" : code!, false, false),
    };
}

public enum SyncResult { None = 0, TimestampsOnly, Changed, ReturnedToPending }

/// <summary>Reason codes shared by the domain and the application layer (the Application layer maps eligibility reasons onto the same strings).</summary>
public static class ExclusionCodes
{
    public const string CampaignStopped = "campaign_stopped";
    public const string Suppressed = "suppressed";
    public const string OptedOutConversation = "opted_out_conversation";
}

/// <summary>Idempotency keys of campaign sends. One key per (campaign, recipient, round); the Phase 5 outbound queue turns a repeated key into "return the existing message", which is what makes a restarted run safe.</summary>
public static class CampaignKeys
{
    public static string ForRecipient(Guid campaignId, Guid recipientId, int round) => $"cmp:{campaignId:N}:{recipientId:N}" + (round > 0 ? $":{round}" : "");
    public static string ForTest(Guid campaignId, string e164, DateTime minuteUtc) => $"cmp-test:{campaignId:N}:{e164.TrimStart('+')}:{minuteUtc:yyyyMMddHHmm}";
    public static bool IsCampaignKey(string? key) => key is not null && key.StartsWith("cmp:", StringComparison.Ordinal);
}
