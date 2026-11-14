using System.Diagnostics;
using System.Text;
using MySqlConnector;

namespace LeadPilot.Tools.Benchmark;

/// <summary>
/// Generates a realistic volume into a SCRATCH database with INSERT ... SELECT from a numbers table (seconds, not minutes). Only the columns the analytics read are given real values; every other NOT NULL column gets a type default.
/// Foreign-key checks are off for the generation session. TEST TOOL ONLY: never run against a real database.
/// </summary>
public sealed class Generator(string connectionString, Guid tenantId, Guid pipelineId, Guid openStage, Guid wonStage, Guid lostStage, Guid statusId)
{
    public sealed record Volumes(int Leads = 200_000, int Contacts = 260_000, int Consents = 220_000, int Suppressions = 5_000, int Conversations = 180_000, int Messages = 1_200_000, int StatusEvents = 1_500_000, int Demos = 20_000,
        int Trials = 10_000, int Customers = 4_000, int Payments = 6_000, int Opportunities = 30_000, int Handoffs = 40_000, int AiInteractions = 100_000, int Qualifications = 60_000, int Campaigns = 300, int Recipients = 400_000,
        int DiscoveryJobs = 2_000, int DiscoveryResults = 100_000, int AiUsage = 100_000);

    private static readonly Guid[] Owners = [Guid.Parse("00000000-0000-0000-0000-0000000000a1"), Guid.Parse("00000000-0000-0000-0000-0000000000a2"), Guid.Parse("00000000-0000-0000-0000-0000000000a3"), Guid.Parse("00000000-0000-0000-0000-0000000000a4"), Guid.Parse("00000000-0000-0000-0000-0000000000a5")];
    private const string Cities = "'Lahore','Karachi','Islamabad','Rawalpindi','Faisalabad','Multan','Peshawar','Quetta','Sialkot','Gujranwala','Hyderabad','Riyadh','Jeddah','Dammam','Dubai','Abu Dhabi','Doha','Muscat','Kuwait City','Manama','Cairo','Amman','Beirut','Nairobi','Lagos'";
    private string T => $"'{tenantId}'";
    private string Start => "DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 365 DAY)";
    private static string RandTime(string start = "DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 365 DAY)", int days = 365) => $"DATE_ADD({start}, INTERVAL FLOOR(RAND()*{days}*86400) SECOND)";

    public List<(string Step, long Rows, long Ms)> Log { get; } = [];

    private async Task<long> Exec(MySqlConnection c, string sql, int timeout = 3600)
    {
        await using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.CommandTimeout = timeout; return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<Dictionary<string, (string Type, bool Nullable)>> ColumnsAsync(MySqlConnection c, string table)
    {
        var d = new Dictionary<string, (string, bool)>();
        await using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT column_name, data_type, is_nullable FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = @t"; cmd.Parameters.AddWithValue("@t", table);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) d[r.GetString(0)] = (r.GetString(1), r.GetString(2) == "YES");
        return d;
    }

    private static string DefaultFor(string type) => type switch
    {
        "char" or "varchar" or "text" or "longtext" or "tinytext" or "mediumtext" => "''",
        "tinyint" or "int" or "bigint" or "smallint" or "decimal" or "double" or "float" => "0",
        "datetime" or "timestamp" => "'2000-01-01 00:00:00'", "date" => "'2000-01-01'",
        _ => "''"
    };

    /// <summary>INSERT ... SELECT of <paramref name="rows"/> rows. <c>s.n</c> is the row number (0-based). <paramref name="from"/> may join mapping tables (m1, m2 ...).</summary>
    private async Task Fill(MySqlConnection c, string table, long rows, Dictionary<string, string> over, string from = "seq s", string? where = null, bool tenant = true)
    {
        var sw = Stopwatch.StartNew(); var cols = await ColumnsAsync(c, table);
        if (tenant && cols.ContainsKey("TenantId")) over.TryAdd("TenantId", T);
        over.TryAdd("Id", "UUID()");
        var names = new List<string>(); var exprs = new List<string>();
        foreach (var (name, (type, nullable)) in cols)
        {
            if (over.TryGetValue(name, out var e)) { names.Add($"`{name}`"); exprs.Add(e); }
            else if (!nullable) { names.Add($"`{name}`"); exprs.Add(DefaultFor(type)); }
        }
        var sql = $"INSERT INTO `{table}` ({string.Join(',', names)}) SELECT {string.Join(',', exprs)} FROM {from} WHERE s.n < {rows}{(where is null ? "" : " AND " + where)}";
        var n = await Exec(c, sql); Log.Add((table, n, sw.ElapsedMilliseconds));
    }

    private async Task MapAsync(MySqlConnection c, string name, string table, string idColumn = "Id", string extra = "", string? whereTenant = null)
    {
        await Exec(c, $"DROP TABLE IF EXISTS `{name}`");
        await Exec(c, $"CREATE TABLE `{name}` (n INT AUTO_INCREMENT PRIMARY KEY, id CHAR(36) NOT NULL{(extra.Length > 0 ? ", " + extra + " CHAR(36)" : "")})");
        await Exec(c, $"INSERT INTO `{name}` (id{(extra.Length > 0 ? ", " + extra : "")}) SELECT `{idColumn}`{(extra.Length > 0 ? ", `" + extra + "`" : "")} FROM `{table}` WHERE TenantId = {T}{(whereTenant is null ? "" : " AND " + whereTenant)} ORDER BY `{idColumn}`");
    }

    public async Task RunAsync(Volumes v)
    {
        await using var c = new MySqlConnection(connectionString); await c.OpenAsync();
        await Exec(c, "SET SESSION FOREIGN_KEY_CHECKS=0"); await Exec(c, "SET SESSION unique_checks=1"); await Exec(c, "SET SESSION sql_mode=''"); await Exec(c, "SET SESSION cte_max_recursion_depth=2000000");
        var sw = Stopwatch.StartNew();
        await Exec(c, "DROP TABLE IF EXISTS digits"); await Exec(c, "CREATE TABLE digits (d INT PRIMARY KEY)"); await Exec(c, "INSERT INTO digits VALUES (0),(1),(2),(3),(4),(5),(6),(7),(8),(9)");
        await Exec(c, "DROP TABLE IF EXISTS seq"); await Exec(c, "CREATE TABLE seq (n INT PRIMARY KEY)");
        await Exec(c, "INSERT INTO seq SELECT a.d + b.d*10 + c.d*100 + d.d*1000 + e.d*10000 + f.d*100000 FROM digits a, digits b, digits c, digits d, digits e, digits f");
        Log.Add(("numbers table (1 000 000)", 1_000_000, sw.ElapsedMilliseconds));
        string Owner(string n) => $"ELT(1 + MOD({n}, 5), {string.Join(',', Owners.Select(o => $"'{o}'"))})";

        await Fill(c, "Leads", v.Leads, new()
        {
            ["Name"] = "CONCAT('School ', s.n)", ["NormalizedName"] = "CONCAT('school ', s.n)", ["DedupKey"] = "CONCAT('school ', s.n, '|', ELT(1 + MOD(s.n, 25), " + Cities + "))", ["PipelineId"] = $"'{pipelineId}'",
            ["PipelineStageId"] = $"IF(MOD(s.n, 40) = 0, '{wonStage}', IF(MOD(s.n, 25) = 0, '{lostStage}', '{openStage}'))", ["StatusId"] = $"'{statusId}'", ["OwnerUserId"] = Owner("s.n"),
            ["City"] = "ELT(1 + MOD(s.n, 25), " + Cities + ")", ["PrimarySourceType"] = "ELT(1 + MOD(s.n, 6), 'Manual','Import','Api','Discovery','Referral','Website')", ["IsArchived"] = "IF(MOD(s.n, 50) = 0, 1, 0)",
            ["CreatedAtUtc"] = RandTime(), ["Version"] = "1", ["Score"] = "MOD(s.n, 100)", ["Confidence"] = "60"
        });
        await MapAsync(c, "m_lead", "Leads");
        await Fill(c, "Contacts", v.Contacts, new()
        {
            ["LeadId"] = "l.id", ["Name"] = "'Contact'", ["PhoneRaw"] = "CONCAT('+92300', LPAD(s.n, 7, '0'))", ["PhoneE164"] = "CONCAT('+92300', LPAD(s.n, 7, '0'))", ["PhoneUniqueKey"] = "CONCAT('+92300', LPAD(s.n, 7, '0'))",
            ["DataOrigin"] = "ELT(1 + MOD(s.n, 3), 'Public','Provided','Verified')", ["IsPrimary"] = "IF(s.n < 200000, 1, 0)", ["IsDeleted"] = "IF(MOD(s.n, 97) = 0, 1, 0)", ["CreatedAtUtc"] = RandTime()
        }, from: $"seq s JOIN m_lead l ON l.n = 1 + MOD(s.n, {v.Leads})");
        await MapAsync(c, "m_contact", "Contacts");
        await Fill(c, "ConsentRecords", v.Consents, new()
        {
            ["ContactId"] = "k.id", ["Channel"] = "'WhatsApp'", ["Purpose"] = "'Marketing'", ["Status"] = $"IF(s.n >= {(int)(v.Consents * 0.91)}, 'Revoked', 'Granted')", ["IsRevocation"] = $"IF(s.n >= {(int)(v.Consents * 0.91)}, 1, 0)",
            ["Source"] = "'bench'", ["OccurredAtUtc"] = RandTime(), ["RecordedAtUtc"] = RandTime()
        }, from: $"seq s JOIN m_contact k ON k.n = 1 + MOD(s.n, {v.Contacts})");
        await Fill(c, "SuppressionEntries", v.Suppressions, new()
        {
            ["Channel"] = "'WhatsApp'", ["ValueNormalized"] = "CONCAT('+92300', LPAD(s.n * 7, 7, '0'))", ["ValueHash"] = "SHA2(CONCAT('+92300', LPAD(s.n * 7, 7, '0')), 256)", ["Reason"] = "'OptOut'", ["CreatedAtUtc"] = RandTime()
        });
        await Fill(c, "Conversations", v.Conversations, new()
        {
            ["ChannelAccountId"] = "'11111111-1111-1111-1111-111111111111'", ["ExternalChatId"] = "UUID()", ["ContactId"] = "k.id", ["Status"] = "'Open'", ["AiMode"] = "'Off'", ["CreatedAtUtc"] = RandTime()
        }, from: $"seq s JOIN m_contact k ON k.n = 1 + MOD(s.n, {v.Contacts})");
        await MapAsync(c, "m_conv", "Conversations", extra: "ContactId");
        // messages: 1.2 M in two statements (the numbers table has 1 M rows); 70 % outbound, 85 % of those delivered, 3 % failed
        foreach (var (take, offset) in new[] { (Math.Min(v.Messages, 1_000_000), 0), (Math.Max(0, v.Messages - 1_000_000), 1_000_000) })
        {
            if (take == 0) continue;
            await Fill(c, "Messages", take, new Dictionary<string, string>
            {
                ["ConversationId"] = "cv.id", ["ChannelAccountId"] = "'11111111-1111-1111-1111-111111111111'", ["ContactId"] = "cv.ContactId", ["Direction"] = $"IF(MOD(s.n + {offset}, 10) < 7, 'Outbound', 'Inbound')",
                ["Status"] = $"IF(MOD(s.n + {offset}, 10) < 7, 'Delivered', 'Received')", ["Body"] = "''", ["CorrelationId"] = "''", ["CreatedAtUtc"] = "@t0", ["OccurredAtUtc"] = "@t0",
                ["SentAtUtc"] = $"IF(MOD(s.n + {offset}, 10) < 7 AND MOD(s.n, 33) <> 0, @t0, NULL)", ["DeliveredAtUtc"] = $"IF(MOD(s.n + {offset}, 10) < 7 AND MOD(s.n, 33) <> 0 AND MOD(s.n, 7) <> 0, @t0, NULL)",
                ["FailedAtUtc"] = $"IF(MOD(s.n + {offset}, 10) < 7 AND MOD(s.n, 33) = 0, @t0, NULL)", ["IsAiGenerated"] = "IF(MOD(s.n, 5) = 0, 1, 0)", ["CreatedByUserId"] = Owner("s.n")
            }.ToDictionary(kv => kv.Key, kv => kv.Value.Replace("@t0", RandTime())), from: $"seq s JOIN m_conv cv ON cv.n = 1 + MOD(s.n + {offset}, {v.Conversations})");
        }
        await MapAsync(c, "m_msg", "Messages");
        foreach (var (take, offset) in new[] { (Math.Min(v.StatusEvents, 1_000_000), 0), (Math.Max(0, v.StatusEvents - 1_000_000), 1_000_000) })
        {
            if (take == 0) continue;
            await Fill(c, "MessageStatusEvents", take, new()
            {
                ["MessageId"] = "m.id", ["Status"] = $"ELT(1 + MOD(s.n, 3), 'Sent','Delivered','Read')", ["Source"] = "'bench'", ["Applied"] = "1", ["OccurredAtUtc"] = RandTime(), ["RecordedAtUtc"] = RandTime()
            }, from: $"seq s JOIN m_msg m ON m.n = 1 + MOD(s.n + {offset}, {v.Messages})");
        }
        await Fill(c, "Demos", v.Demos, new()
        {
            ["LeadId"] = "l.id", ["RepUserId"] = Owner("s.n"), ["StartUtc"] = RandTime(), ["EndUtc"] = RandTime(), ["Status"] = "ELT(1 + MOD(s.n, 5), 'Booked','Confirmed','Completed','NoShow','Cancelled')", ["CreatedAtUtc"] = RandTime(),
            ["CompletedAtUtc"] = $"IF(MOD(s.n, 5) = 2, {RandTime()}, NULL)", ["Generation"] = "1", ["ActiveSlot"] = "NULL"
        }, from: $"seq s JOIN m_lead l ON l.n = 1 + MOD(s.n * 7, {v.Leads})");
        await Fill(c, "Trials", v.Trials, new() { ["LeadId"] = "l.id", ["Status"] = "ELT(1 + MOD(s.n, 4), 'Active','Expired','Converted','Cancelled')", ["CreatedAtUtc"] = RandTime(), ["DurationDays"] = "14" }, from: $"seq s JOIN m_lead l ON l.n = 1 + MOD(s.n * 11, {v.Leads})");
        await Fill(c, "Customers", v.Customers, new() { ["Number"] = "s.n + 1", ["LeadId"] = "l.id", ["Name"] = "'Customer'", ["Status"] = "'Active'", ["ConvertedAtUtc"] = RandTime(), ["CreatedAtUtc"] = RandTime() }, from: $"seq s JOIN m_lead l ON l.n = 1 + s.n");
        await MapAsync(c, "m_cust", "Customers");
        await Fill(c, "Payments", v.Payments, new()
        {
            ["PaymentRequestId"] = "UUID()", ["CustomerId"] = "cu.id", ["Amount"] = "ROUND(50 + RAND()*950, 2)", ["Currency"] = "ELT(1 + MOD(s.n, 3), 'USD','PKR','SAR')", ["EvidenceReference"] = "'r'", ["EvidenceKey"] = "UUID()", ["PaidAtUtc"] = RandTime(), ["VerifiedAtUtc"] = RandTime()
        }, from: $"seq s JOIN m_cust cu ON cu.n = 1 + MOD(s.n, {v.Customers})");
        await Fill(c, "Opportunities", v.Opportunities, new()
        {
            ["LeadId"] = "l.id", ["PipelineStageId"] = $"'{openStage}'", ["Name"] = "'Deal'", ["Value"] = "ROUND(100 + RAND()*9900, 2)", ["Currency"] = "ELT(1 + MOD(s.n, 3), 'USD','PKR','SAR')", ["OwnerUserId"] = Owner("s.n"),
            ["Status"] = "ELT(1 + MOD(s.n, 4), 'Open','Open','Won','Lost')", ["CreatedAtUtc"] = RandTime(), ["UpdatedAtUtc"] = RandTime()
        }, from: $"seq s JOIN m_lead l ON l.n = 1 + MOD(s.n * 13, {v.Leads})");
        await Fill(c, "HandoffEvents", v.Handoffs, new() { ["ConversationId"] = "cv.id", ["Kind"] = "ELT(1 + MOD(s.n, 4), 'Takeover','Escalated','AiPaused','AutoTakeover')", ["Actor"] = "'User'", ["OccurredAtUtc"] = RandTime() }, from: $"seq s JOIN m_conv cv ON cv.n = 1 + MOD(s.n * 3, {v.Conversations})");
        await Fill(c, "AiInteractions", v.AiInteractions, new()
        {
            ["ConversationId"] = "cv.id", ["TriggerMessageId"] = "UUID()", ["Intent"] = "ELT(1 + MOD(s.n, 4), 'pricing','greeting','demo_request','product_question')", ["Decision"] = "ELT(1 + MOD(s.n, 3), 'Draft','Escalate','NoReply')",
            ["Status"] = "ELT(1 + MOD(s.n, 4), 'Pending','Used','Dismissed','AutoSent')", ["LatencyMs"] = "100 + MOD(s.n, 900)", ["CreatedAtUtc"] = RandTime(), ["Model"] = "'m'", ["AgentId"] = "NULL"
        }, from: $"seq s JOIN m_conv cv ON cv.n = 1 + MOD(s.n * 5, {v.Conversations})");
        await Fill(c, "LeadQualifications", v.Qualifications, new() { ["LeadId"] = "l.id", ["Fit"] = "ELT(1 + MOD(s.n, 4), 'Unknown','Poor','Possible','Good')", ["UpdatedAtUtc"] = RandTime(), ["Version"] = "1" }, from: $"seq s JOIN m_lead l ON l.n = 1 + s.n");
        await Fill(c, "Campaigns", v.Campaigns, new() { ["Name"] = "CONCAT('Campaign ', s.n)", ["Status"] = "'Completed'", ["StartedAtUtc"] = RandTime(), ["AudienceJson"] = "'{}'", ["CreatedAtUtc"] = RandTime(), ["Version"] = "1" });
        await MapAsync(c, "m_camp", "Campaigns");
        await Fill(c, "CampaignRecipients", v.Recipients, new()
        {
            ["CampaignId"] = "ca.id", ["SnapshotId"] = "UUID()", ["ContactId"] = "k.id", ["LeadId"] = "k.id", ["Status"] = "ELT(1 + MOD(s.n, 4), 'Sent','Delivered','Replied','Failed')", ["SentAtUtc"] = RandTime(), ["RepliedAtUtc"] = $"IF(MOD(s.n, 4) = 2, {RandTime()}, NULL)", ["CreatedAtUtc"] = RandTime(), ["Version"] = "1"
        }, from: $"seq s JOIN m_camp ca ON ca.n = 1 + MOD(s.n, {v.Campaigns}) JOIN m_contact k ON k.n = 1 + (s.n DIV {v.Campaigns})");
        await Exec(c, $"UPDATE CampaignRecipients r JOIN Contacts k ON k.Id = r.ContactId SET r.LeadId = k.LeadId WHERE r.TenantId = {T}");
        await Fill(c, "CampaignStatsRollups", v.Campaigns, new() { ["CampaignId"] = "ca.id", ["Total"] = "1333", ["Sent"] = "300", ["Delivered"] = "300", ["Replied"] = "300", ["Failed"] = "300", ["UpdatedAtUtc"] = "UTC_TIMESTAMP(6)" }, from: $"seq s JOIN m_camp ca ON ca.n = 1 + s.n");
        await Fill(c, "DiscoveryJobs", v.DiscoveryJobs, new() { ["ConnectorType"] = "'google_places'", ["Status"] = "'Completed'", ["City"] = "CONCAT('City ', MOD(s.n, 3000))", ["ApiCalls"] = "3", ["EstimatedCostUsd"] = "0.051", ["CreatedAtUtc"] = RandTime() });
        await MapAsync(c, "m_job", "DiscoveryJobs");
        await Fill(c, "DiscoveryResults", v.DiscoveryResults, new()
        {
            ["JobId"] = "j.id", ["ConnectorType"] = "'google_places'", ["ExternalId"] = "UUID()", ["Name"] = "'r'", ["City"] = "CONCAT('City ', MOD(s.n, 3000))", ["ReviewStatus"] = "ELT(1 + MOD(s.n, 3), 'Pending','Approved','Rejected')",
            ["RetrievedAtUtc"] = RandTime(), ["CreatedAtUtc"] = RandTime(), ["ConfidenceLevel"] = "'Medium'", ["Enrichment"] = "'None'", ["LeadId"] = "IF(MOD(s.n, 9) = 0, UUID(), NULL)"
        }, from: $"seq s JOIN m_job j ON j.n = 1 + MOD(s.n, {v.DiscoveryJobs})");
        await Fill(c, "AiUsageEntries", v.AiUsage, new() { ["Operation"] = "'chat'", ["Provider"] = "'claude'", ["Model"] = "'m'", ["InputTokens"] = "1000", ["OutputTokens"] = "200", ["EstimatedCostUsd"] = "0.0123", ["Success"] = "1", ["PeriodKey"] = "202601", ["OccurredAtUtc"] = RandTime() });
        foreach (var t in new[] { "Leads", "Contacts", "ConsentRecords", "Conversations", "Messages", "MessageStatusEvents", "Demos", "Trials", "Customers", "Payments", "Opportunities", "HandoffEvents", "AiInteractions", "LeadQualifications", "Campaigns", "CampaignRecipients", "DiscoveryResults", "AiUsageEntries" })
            await Exec(c, $"ANALYZE TABLE `{t}`");
    }
}
