migrations applied in 8872 ms
# Large-dataset benchmark (2026-11-14 09:20 UTC, scale 1, MySQL 8.0.46-0ubuntu0.24.04.5, 4 cores)

Data generated in 390.9 s:

| Table | Rows | ms |
|---|---:|---:|
| numbers table (1 000 000) | 1,000,000 | 3,452 |
| Leads | 200,000 | 14,964 |
| Contacts | 260,000 | 8,279 |
| ConsentRecords | 220,000 | 5,169 |
| SuppressionEntries | 5,000 | 155 |
| Conversations | 180,000 | 5,934 |
| Messages | 1,000,000 | 207,087 |
| Messages | 200,000 | 63,216 |
| MessageStatusEvents | 1,000,000 | 17,596 |
| MessageStatusEvents | 500,000 | 9,836 |
| Demos | 20,000 | 915 |
| Trials | 10,000 | 309 |
| Customers | 4,000 | 121 |
| Payments | 6,000 | 265 |
| Opportunities | 30,000 | 1,045 |
| HandoffEvents | 40,000 | 1,335 |
| AiInteractions | 100,000 | 3,529 |
| LeadQualifications | 60,000 | 1,256 |
| Campaigns | 300 | 25 |
| CampaignRecipients | 260,000 | 12,255 |
| CampaignStatsRollups | 300 | 12 |
| DiscoveryJobs | 2,000 | 107 |
| DiscoveryResults | 100,000 | 4,961 |
| AiUsageEntries | 100,000 | 2,113 |
- `Leads`: 200,000 rows
- `Contacts`: 260,000 rows
- `Messages`: 1,200,000 rows
- `MessageStatusEvents`: 1,500,000 rows
- `CampaignRecipients`: 260,000 rows
- `DiscoveryResults`: 100,000 rows
- message + status rows: 2,700,000


## Timings

| Step | cold (ms) | warm median (ms) | managed allocation | peak working set | result |
|---|---:|---:|---:|---:|---|
| stream leads.created (30 d, SQL buckets) | 200 | 39 | 12.7 MiB | 419 MiB | 16,176 events |
| stream leads.contacted (30 d, SQL buckets) | 10,125 | 10,435 | 0.9 MiB | 420 MiB | 0 events |
| stream messages.inbound (30 d, SQL buckets) | 462 | 374 | 9.8 MiB | 420 MiB | 29,472 events |
| stream messages.sent (30 d, SQL buckets) | 171 | 98 | 9.7 MiB | 420 MiB | 66,010 events |
| stream messages.delivered (30 d, SQL buckets) | 4,918 | 4,619 | 9.7 MiB | 420 MiB | 56,573 events |
| stream messages.failed (30 d, SQL buckets) | 70 | 23 | 5.3 MiB | 420 MiB | 2,083 events |
| stream demos.scheduled (30 d, SQL buckets) | 18 | 7 | 4.5 MiB | 420 MiB | 1,569 events |
| stream demos.completed (30 d, SQL buckets) | 10 | 4 | 1.5 MiB | 420 MiB | 353 events |
| stream trials.started (30 d, SQL buckets) | 16 | 6 | 2.8 MiB | 420 MiB | 847 events |
| stream customers.won (30 d, SQL buckets) | 13 | 4 | 1.4 MiB | 420 MiB | 326 events |
| stream opportunities.lost (30 d, SQL buckets) | 13 | 5 | 2.3 MiB | 420 MiB | 605 events |
| stream ai.conversations (30 d, SQL buckets) | 212 | 100 | 0.7 MiB | 420 MiB | 59 events |
| stream handoffs.human (30 d, SQL buckets) | 171 | 80 | 5.9 MiB | 420 MiB | 2,313 events |
| stream campaigns.sent (30 d, SQL buckets) | 50 | 33 | 9.7 MiB | 420 MiB | 20,897 events |
| stream campaigns.replied (30 d, SQL buckets) | 782 | 791 | 8.1 MiB | 420 MiB | 5,160 events |
| stream revenue.verified (30 d, SQL buckets) | 55 | 7 | 2.1 MiB | 420 MiB | 493 events |
| snapshot: total leads | 188 | 122 | 0.2 MiB | 420 MiB | 196,000 |
| snapshot: verified leads | 1,271 | 1,165 | 0.3 MiB | 420 MiB | 84,058 |
| snapshot: contact-eligible leads | 4,385 | 3,874 | 0.5 MiB | 420 MiB | 189,159 |
| snapshot: interested leads | 8,378 | 8,184 | 0.6 MiB | 420 MiB | 41,400 |
| snapshot: qualified leads | 162 | 124 | 0.2 MiB | 420 MiB | 15,000 |
| cohort: lead conversion (30 d) | 125 | 135 | 0.4 MiB | 420 MiB | 335/16,176 |
| DASHBOARD live, team, 30 d + previous (all 20 tiles + trends, no rollup, no cache) | 43,294 | 46,395 | 35.9 MiB | 420 MiB | Live, leads.created=16,176 |
| DASHBOARD live, own data (one sales rep), 30 d + previous | 48,657 | 43,671 | 26.7 MiB | 420 MiB | Live, own leads.total=36,000 |
| ROLLUP full rebuild (400 days, 16 streams) | 24,408 | — | 1,149.7 MiB | 504 MiB | 6,495 rows written |
| ROLLUP incremental refresh (7 days) | 10,458 | 10,076 | 20.5 MiB | 503 MiB | 114 rows |
| DASHBOARD from rollup, team, 30 d + previous | 13,355 | 12,646 | 1.5 MiB | 503 MiB | Rollup, leads.created=16,176 |
| DASHBOARD from rollup, team, 365 days + previous | 58,138 | 57,941 | 369.0 MiB | 503 MiB | Live |
| REPORT discovery-by-city (page of 50, 90 d) | 1,004 | 807 | 2.2 MiB | 503 MiB | 3,000 rows |
| REPORT lead-sources (page of 50, 90 d) | 816 | 707 | 0.9 MiB | 503 MiB | 6 rows |
| REPORT campaigns (page of 50, 90 d) | 47 | 8 | 1.7 MiB | 503 MiB | 84 rows |
| REPORT sales-pipeline (page of 50, 90 d) | 859 | 773 | 1.2 MiB | 503 MiB | 14 rows |
| REPORT conversion-funnel (page of 50, 90 d) | 6,949 | 6,758 | 2.2 MiB | 503 MiB | 9 rows |
| REPORT rep-performance (page of 50, 90 d) | 4,757 | 4,668 | 1.4 MiB | 503 MiB | 5 rows |
| REPORT ai-agent (page of 50, 90 d) | 575 | 471 | 1.3 MiB | 503 MiB | 1 rows |
| REPORT demo-conversion (page of 50, 90 d) | 152 | 113 | 0.9 MiB | 503 MiB | 8 rows |
| REPORT trial-conversion (page of 50, 90 d) | 35 | 24 | 0.3 MiB | 503 MiB | 7 rows |
| REPORT subscription-revenue (page of 50, 90 d) | 29 | 15 | 0.4 MiB | 503 MiB | 3 rows |
| REPORT messaging-usage (page of 50, 90 d) | 17 | 7 | 1.2 MiB | 503 MiB | 90 rows |
| EXPORT CSV discovery-by-city (90 d) streamed | 3,338 | — | 22.9 MiB | 503 MiB | 95 KiB written, audited 1x |
| EXPORT CSV messaging-usage (90 d) streamed | 10 | — | 2.3 MiB | 503 MiB | 3 KiB written, audited 1x |
| EXPORT CSV campaigns (90 d) streamed | 12 | — | 1.4 MiB | 503 MiB | 6 KiB written, audited 1x |

Working set after generation, before the measurements: 411 MiB; peak working set of this process at the end: 503 MiB (the process holds no record sets: only grouped result rows).

## Index justification (same query with the index and with IGNORE INDEX, median of 3 warm runs)

| Index | Query | with (ms) | without (ms) | plan with: key / rows | plan without: key / rows | verdict |
|---|---|---:|---:|---|---|---|
| `IX_Leads_Tenant_Created` | new leads (30 d) | 18.4 | 17.7 | Leads: IX_Leads_TenantId_IsArchived_CreatedAtUtc / 10865 | Leads: IX_Leads_TenantId_IsArchived_CreatedAtUtc / 10865 | NOT USED |
| `IX_Messages_Tenant_Dir_Sent` | messages sent (30 d) | 50.6 | 3,477.2 | Messages: IX_Messages_Tenant_Dir_Sent / 160670 | Messages: IX_Messages_TenantId_ChannelAccountId_ExternalMessageId / 588254 | KEEP (faster) |
| `IX_Messages_Tenant_Dir_Occurred` | responses (30 d) | 285.5 | 3,352.6 | Messages: IX_Messages_Tenant_Dir_Occurred / 58426 | Messages: IX_Messages_TenantId_ChannelAccountId_ExternalMessageId / 588254 | KEEP (faster) |
| `IX_Messages_Tenant_Failed` | failed (30 d) | 12.7 | 3,454.1 | Messages: IX_Messages_Tenant_Failed / 3756 | Messages: IX_Messages_TenantId_ChannelAccountId_ExternalMessageId / 588254 | KEEP (faster) |
| `IX_Messages_Tenant_Contact_Dir` | funnel 'contacted' (90 d cohort) | 2,685.2 | 2,813.2 | l: IX_Leads_TenantId_IsArchived_CreatedAtUtc / 97807; c: IX_Contacts_TenantId_LeadId_PhoneUniqueKey / 1; m: IX_Messages_ContactId / 6 | l: IX_Leads_TenantId_IsArchived_CreatedAtUtc / 97807; c: IX_Contacts_TenantId_LeadId_PhoneUniqueKey / 1; m: IX_Messages_ContactId / 6 | NOT USED |
| `IX_Demos_Tenant_Created` | demos scheduled (30 d) | 1.9 | 45.7 | Demos: IX_Demos_Tenant_Created / 1569 | Demos: IX_Demos_TenantId_ActiveSlot / 9900 | KEEP (faster) |
| `IX_Demos_Tenant_Completed` | demos completed (30 d) | 0.9 | 32.8 | Demos: IX_Demos_Tenant_Completed / 353 | Demos: IX_Demos_TenantId_ActiveSlot / 9900 | KEEP (faster) |
| `IX_Trials_Tenant_Created` | trials (30 d) | 1.1 | 24.7 | Trials: IX_Trials_Tenant_Created / 847 | Trials: IX_Trials_TenantId / 5014 | KEEP (faster) |
| `IX_Customers_Tenant_Converted` | customers won (30 d) | 0.6 | 6.8 | Customers: IX_Customers_Tenant_Converted / 326 | Customers: IX_Customers_TenantId_LeadId / 1969 | KEEP (faster) |
| `IX_Payments_Tenant_Paid` | revenue (30 d) | 1.6 | 9.7 | Payments: IX_Payments_Tenant_Paid / 493 | Payments: IX_Payments_TenantId_EvidenceKey / 2992 | KEEP (faster) |
| `IX_Opportunities_Tenant_Status_Updated` | lost opportunities (30 d) | 0.8 | 48.7 | Opportunities: IX_Opportunities_Tenant_Status_Updated / 605 | Opportunities: IX_Opportunities_TenantId / 14792 | KEEP (faster) |
| `IX_HandoffEvents_Tenant_Occurred` | human handoffs (30 d) | 66.3 | 65.3 | HandoffEvents: IX_HandoffEvents_TenantId / 19845 | HandoffEvents: IX_HandoffEvents_TenantId / 19845 | NOT USED |
| `IX_AiInteractions_Tenant_Created` | AI report (90 d, by agent) | 218.0 | 197.6 | AiInteractions: IX_AiInteractions_TenantId_TriggerMessageId / 49300 | AiInteractions: IX_AiInteractions_TenantId_TriggerMessageId / 49300 | NOT USED |
| `IX_CampaignRecipients_Tenant_Sent` | campaign messages sent (30 d) | 13.6 | 666.7 | CampaignRecipients: IX_CampaignRecipients_Tenant_Sent / 43206 | CampaignRecipients: IX_CampaignRecipients_TenantId / 127624 | KEEP (faster) |
| `IX_Campaigns_Tenant_Started` | campaigns started (30 d) | 0.4 | 0.8 | Campaigns: IX_Campaigns_Tenant_Started / 31 | Campaigns: (no index) / 300 | plan uses it, no measurable gain |
| `IX_DiscoveryResults_Tenant_Retrieved` | discovery by city (30 d) | 236.8 | 272.1 | DiscoveryResults: IX_DiscoveryResults_TenantId_ConnectorType_ExternalId / 49313 | DiscoveryResults: IX_DiscoveryResults_TenantId_ConnectorType_ExternalId / 49313 | NOT USED |

## EXPLAIN of the heaviest queries

### stream leads.contacted (30 d, SQL buckets)
Query 1:
```
id=1 | select_type=PRIMARY | table=<derived3> | partitions=NULL | type=ALL | possible_keys=NULL | key=NULL | key_len=NULL | ref=NULL | rows=13235 | filtered=100 | Extra=Using temporary
id=3 | select_type=DERIVED | table=m | partitions=NULL | type=ref | possible_keys=IX_Messages_TenantId_ChannelAccountId_ExternalMessageId,IX_Messages_TenantId_IdempotencyKey,IX_Messages_ContactId,IX_Messages_TenantId,IX_Messages_TenantId_ChannelAccountId_SentAtUtc,IX_Messages_TenantId_ConversationId_OccurredAtUtc,IX_Messages_TenantId_CampaignId_Status,IX_Messages_TenantId_CampaignRecipientId,IX_Messages_Tenant_Contact_Dir,IX_Messages_Tenant_Dir_Occurred,IX_Messages_Tenant_Dir_Sent,IX_Messages_Tenant_Failed | key=IX_Messages_TenantId_ChannelAccountId_ExternalMessageId | key_len=144 | ref=const | rows=588254 | filtered=4.5 | Extra=Using index condition; Using where; Using temporary
id=3 | select_type=DERIVED | table=c | partitions=NULL | type=eq_ref | possible_keys=PRIMARY,IX_Contacts_TenantId_LeadId_EmailUniqueKey,IX_Contacts_TenantId_LeadId_PhoneUniqueKey,IX_Contacts_LeadId,IX_Contacts_TenantId,IX_Contacts_TenantId_Email,IX_Contacts_TenantId_PhoneE164 | key=PRIMARY | key_len=144 | ref=leadpilot_bench_860adbdd.m.ContactId | rows=1 | filtered=50 | Extra=Using where
```

### stream messages.inbound (30 d, SQL buckets)
Query 1:
```
id=1 | select_type=SIMPLE | table=m | partitions=NULL | type=range | possible_keys=IX_Messages_TenantId_ChannelAccountId_ExternalMessageId,IX_Messages_TenantId_IdempotencyKey,IX_Messages_ContactId,IX_Messages_TenantId,IX_Messages_TenantId_ChannelAccountId_SentAtUtc,IX_Messages_TenantId_ConversationId_OccurredAtUtc,IX_Messages_TenantId_CampaignId_Status,IX_Messages_TenantId_CampaignRecipientId,IX_Messages_Tenant_Contact_Dir,IX_Messages_Tenant_Dir_Occurred,IX_Messages_Tenant_Dir_Sent,IX_Messages_Tenant_Failed | key=IX_Messages_Tenant_Dir_Occurred | key_len=234 | ref=NULL | rows=58426 | filtered=50 | Extra=Using index condition; Using where; Using temporary
```

### stream messages.sent (30 d, SQL buckets)
Query 1:
```
id=1 | select_type=SIMPLE | table=m | partitions=NULL | type=range | possible_keys=IX_Messages_TenantId_ChannelAccountId_ExternalMessageId,IX_Messages_TenantId_IdempotencyKey,IX_Messages_TenantId,IX_Messages_TenantId_ChannelAccountId_SentAtUtc,IX_Messages_TenantId_ConversationId_OccurredAtUtc,IX_Messages_TenantId_CampaignId_Status,IX_Messages_TenantId_CampaignRecipientId,IX_Messages_Tenant_Contact_Dir,IX_Messages_Tenant_Dir_Occurred,IX_Messages_Tenant_Dir_Sent,IX_Messages_Tenant_Failed | key=IX_Messages_Tenant_Dir_Sent | key_len=235 | ref=NULL | rows=160670 | filtered=100 | Extra=Using where; Using index; Using temporary
```

### stream ai.conversations (30 d, SQL buckets)
Query 1:
```
id=1 | select_type=PRIMARY | table=<derived3> | partitions=NULL | type=ALL | possible_keys=NULL | key=NULL | key_len=NULL | ref=NULL | rows=49300 | filtered=100 | Extra=Using temporary
id=3 | select_type=DERIVED | table=a | partitions=NULL | type=ref | possible_keys=IX_AiInteractions_TenantId_TriggerMessageId,IX_AiInteractions_ConversationId,IX_AiInteractions_TenantId,IX_AiInteractions_TenantId_ConversationId_CreatedAtUtc,IX_AiInteractions_TenantId_Status_CreatedAtUtc,IX_AiInteractions_Tenant_Created | key=IX_AiInteractions_TenantId_ConversationId_CreatedAtUtc | key_len=144 | ref=const | rows=49300 | filtered=100 | Extra=Using where; Using index
```

### stream revenue.verified (30 d, SQL buckets)
Query 1:
```
id=1 | select_type=SIMPLE | table=p | partitions=NULL | type=ref | possible_keys=IX_Payments_TenantId_EvidenceKey,IX_Payments_CustomerId,IX_Payments_TenantId,IX_Payments_Tenant_Paid | key=IX_Payments_TenantId_EvidenceKey | key_len=144 | ref=const | rows=2992 | filtered=11.11 | Extra=Using index condition; Using where; Using temporary
id=1 | select_type=SIMPLE | table=c | partitions=NULL | type=eq_ref | possible_keys=PRIMARY,IX_Customers_TenantId_LeadId,IX_Customers_TenantId_Number,IX_Customers_TenantId,IX_Customers_TenantId_Status,IX_Customers_Tenant_Converted | key=PRIMARY | key_len=144 | ref=leadpilot_bench_860adbdd.p.CustomerId | rows=1 | filtered=49.99 | Extra=Using where
```

### snapshot: verified leads
Query 1:
```
id=1 | select_type=SIMPLE | table=c | partitions=NULL | type=ref | possible_keys=IX_Contacts_TenantId_LeadId_EmailUniqueKey,IX_Contacts_TenantId_LeadId_PhoneUniqueKey,IX_Contacts_LeadId,IX_Contacts_TenantId,IX_Contacts_TenantId_Email,IX_Contacts_TenantId_PhoneE164 | key=IX_Contacts_TenantId_LeadId_EmailUniqueKey | key_len=144 | ref=const | rows=128325 | filtered=9.1 | Extra=Using index condition; Using where; LooseScan
id=1 | select_type=SIMPLE | table=l | partitions=NULL | type=eq_ref | possible_keys=PRIMARY,IX_Leads_TenantId,IX_Leads_TenantId_CategoryId,IX_Leads_TenantId_City,IX_Leads_TenantId_DedupKey,IX_Leads_TenantId_IsArchived_CreatedAtUtc,IX_Leads_TenantId_NormalizedName,IX_Leads_TenantId_OwnerUserId,IX_Leads_TenantId_PipelineStageId,IX_Leads_TenantId_Score,IX_Leads_TenantId_StatusId,IX_Leads_TenantId_WebsiteDomain,IX_Leads_Tenant_Created | key=PRIMARY | key_len=144 | ref=leadpilot_bench_860adbdd.c.LeadId | rows=1 | filtered=5 | Extra=Using where
```

### snapshot: contact-eligible leads
Query 1:
```
id=1 | select_type=PRIMARY | table=l | partitions=NULL | type=ref | possible_keys=IX_Leads_TenantId,IX_Leads_TenantId_CategoryId,IX_Leads_TenantId_City,IX_Leads_TenantId_DedupKey,IX_Leads_TenantId_IsArchived_CreatedAtUtc,IX_Leads_TenantId_NormalizedName,IX_Leads_TenantId_OwnerUserId,IX_Leads_TenantId_PipelineStageId,IX_Leads_TenantId_Score,IX_Leads_TenantId_StatusId,IX_Leads_TenantId_WebsiteDomain,IX_Leads_Tenant_Created | key=IX_Leads_TenantId | key_len=144 | ref=const | rows=97807 | filtered=10 | Extra=Using index condition; Using where
id=1 | select_type=PRIMARY | table=<subquery2> | partitions=NULL | type=eq_ref | possible_keys=<auto_distinct_key> | key=<auto_distinct_key> | key_len=145 | ref=leadpilot_bench_860adbdd.l.PipelineStageId | rows=1 | filtered=100 | Extra=Using where; Not exists
id=2 | select_type=MATERIALIZED | table=p | partitions=NULL | type=ALL | possible_keys=PRIMARY,IX_PipelineStages_TenantId,IX_PipelineStages_TenantId_PipelineId_Position | key=NULL | key_len=NULL | ref=NULL | rows=14 | filtered=100 | Extra=Using where
id=3 | select_type=SUBQUERY | table=c | partitions=NULL | type=ref | possible_keys=IX_Contacts_TenantId_LeadId_EmailUniqueKey,IX_Contacts_TenantId_LeadId_PhoneUniqueKey,IX_Contacts_LeadId,IX_Contacts_TenantId,IX_Contacts_TenantId_Email,IX_Contacts_TenantId_PhoneE164 | key=IX_Contacts_TenantId_LeadId_EmailUniqueKey | key_len=144 | ref=const | rows=128325 | filtered=9 | Extra=Using index condition; Using where
id=3 | select_type=SUBQUERY | table=<subquery4> | partitions=NULL | type=eq_ref | possible_keys=<auto_distinct_key> | key=<auto_distinct_key> | key_len=1283 | ref=leadpilot_bench_860adbdd.c.PhoneE164 | rows=1 | filtered=100 | Extra=Using where; Not exists
id=4 | select_type=MATERIALIZED | table=s | partitions=NULL | type=ref | possible_keys=IX_SuppressionEntries_TenantId_Channel_ValueNormalized,IX_SuppressionEntries_TenantId,IX_SuppressionEntries_TenantId_ValueHash,IX_SuppressionEntries_TenantId_ValueNormalized | key=IX_SuppressionEntries_TenantId | key_len=144 | ref=const | rows=2508 | filtered=100 | Extra=Using index condition; Using where
id=5 | select_type=DEPENDENT SUBQUERY | table=c0 | partitions=NULL | type=ref | possible_keys=IX_ConsentRecords_ContactId,IX_ConsentRecords_TenantId,IX_ConsentRecords_TenantId_ContactId_Channel_Purpose_OccurredAt~,IX_ConsentRecords_TenantId_OccurredAtUtc | key=IX_ConsentRecords_ContactId | key_len=144 | ref=leadpilot_bench_860adbdd.c.Id | rows=1 | filtered=5 | Extra=Using where; Using filesort
```

### snapshot: interested leads
Query 1:
```
id=1 | select_type=PRIMARY | table=l | partitions=NULL | type=ref | possible_keys=IX_Leads_TenantId,IX_Leads_TenantId_CategoryId,IX_Leads_TenantId_City,IX_Leads_TenantId_DedupKey,IX_Leads_TenantId_IsArchived_CreatedAtUtc,IX_Leads_TenantId_NormalizedName,IX_Leads_TenantId_OwnerUserId,IX_Leads_TenantId_PipelineStageId,IX_Leads_TenantId_Score,IX_Leads_TenantId_StatusId,IX_Leads_TenantId_WebsiteDomain,IX_Leads_Tenant_Created | key=IX_Leads_TenantId_IsArchived_CreatedAtUtc | key_len=145 | ref=const,const | rows=97807 | filtered=100 | Extra=Using where; Using index
id=2 | select_type=DEPENDENT SUBQUERY | table=l0 | partitions=NULL | type=eq_ref | possible_keys=IX_LeadQualifications_TenantId_LeadId,IX_LeadQualifications_LeadId,IX_LeadQualifications_TenantId | key=IX_LeadQualifications_TenantId_LeadId | key_len=288 | ref=const,func | rows=1 | filtered=20 | Extra=Using index condition; Using where
id=3 | select_type=DEPENDENT UNION | table=c1 | partitions=NULL | type=ref | possible_keys=PRIMARY,IX_Contacts_TenantId_LeadId_EmailUniqueKey,IX_Contacts_TenantId_LeadId_PhoneUniqueKey,IX_Contacts_LeadId,IX_Contacts_TenantId,IX_Contacts_TenantId_Email,IX_Contacts_TenantId_PhoneE164 | key=IX_Contacts_TenantId_LeadId_PhoneUniqueKey | key_len=288 | ref=const,func | rows=1 | filtered=100 | Extra=Using where; Using index
id=3 | select_type=DEPENDENT UNION | table=c | partitions=NULL | type=ref | possible_keys=PRIMARY,IX_Conversations_TenantId_ChannelAccountId_ExternalChatId,IX_Conversations_ContactId,IX_Conversations_TenantId,IX_Conversations_TenantId_ContactId,IX_Conversations_TenantId_LastMessageAtUtc | key=IX_Conversations_TenantId_ContactId | key_len=289 | ref=const,leadpilot_bench_860adbdd.c1.Id | rows=1 | filtered=100 | Extra=Using where; Using index
id=3 | select_type=DEPENDENT UNION | table=a | partitions=NULL | type=ref | possible_keys=IX_AiInteractions_TenantId_TriggerMessageId,IX_AiInteractions_ConversationId,IX_AiInteractions_TenantId,IX_AiInteractions_TenantId_ConversationId_CreatedAtUtc,IX_AiInteractions_TenantId_Status_CreatedAtUtc,IX_AiInteractions_Tenant_Created | key=IX_AiInteractions_TenantId_ConversationId_CreatedAtUtc | key_len=288 | ref=const,leadpilot_bench_860adbdd.c.Id | rows=2 | filtered=30 | Extra=Using index condition; Using where
id=6 | select_type=UNION RESULT | table=<union2,3> | partitions=NULL | type=ALL | possible_keys=NULL | key=NULL | key_len=NULL | ref=NULL | rows=NULL | filtered=NULL | Extra=Using temporary
```

### DASHBOARD from rollup, team, 30 d + previous
Query 1:
```
id=1 | select_type=SIMPLE | table=c | partitions=NULL | type=const | possible_keys=IX_ConversionSettings_TenantId | key=IX_ConversionSettings_TenantId | key_len=144 | ref=const | rows=1 | filtered=100 | Extra=NULL
```
Query 2:
```
id=1 | select_type=SIMPLE | table=a | partitions=NULL | type=const | possible_keys=IX_AnalyticsRefreshStates_TenantId | key=IX_AnalyticsRefreshStates_TenantId | key_len=144 | ref=const | rows=1 | filtered=100 | Extra=NULL
```
Query 3:
```
id=1 | select_type=SIMPLE | table=d | partitions=NULL | type=ref | possible_keys=UX_DailyMetrics_Tenant_Metric_Day_Dim,IX_DailyMetrics_TenantId | key=UX_DailyMetrics_Tenant_Metric_Day_Dim | key_len=144 | ref=const | rows=2909 | filtered=11.11 | Extra=Using index condition; Using temporary
```
Query 4:
```
id=1 | select_type=SIMPLE | table=d | partitions=NULL | type=ref | possible_keys=UX_DailyMetrics_Tenant_Metric_Day_Dim,IX_DailyMetrics_TenantId | key=UX_DailyMetrics_Tenant_Metric_Day_Dim | key_len=144 | ref=const | rows=2909 | filtered=11.11 | Extra=Using index condition; Using temporary
```

### REPORT discovery-by-city (page of 50, 90 d)
Query 1:
```
id=1 | select_type=SIMPLE | table=c | partitions=NULL | type=const | possible_keys=IX_ConversionSettings_TenantId | key=IX_ConversionSettings_TenantId | key_len=144 | ref=const | rows=1 | filtered=100 | Extra=NULL
```
Query 2:
```
id=1 | select_type=SIMPLE | table=a | partitions=NULL | type=const | possible_keys=IX_AnalyticsRefreshStates_TenantId | key=IX_AnalyticsRefreshStates_TenantId | key_len=144 | ref=const | rows=1 | filtered=100 | Extra=NULL
```
Query 3:
```
id=1 | select_type=PRIMARY | table=<derived2> | partitions=NULL | type=ALL | possible_keys=NULL | key=NULL | key_len=NULL | ref=NULL | rows=5478 | filtered=100 | Extra=NULL
id=2 | select_type=DERIVED | table=d | partitions=NULL | type=ref | possible_keys=IX_DiscoveryResults_TenantId_ConnectorType_ExternalId,IX_DiscoveryResults_TenantId,IX_DiscoveryResults_TenantId_JobId_ReviewStatus,IX_DiscoveryResults_TenantId_ReviewStatus_Confidence,IX_DiscoveryResults_Tenant_Retrieved | key=IX_DiscoveryResults_TenantId_ConnectorType_ExternalId | key_len=144 | ref=const | rows=49313 | filtered=11.11 | Extra=Using index condition; Using where; Using temporary
```
Query 4:
```
id=1 | select_type=SIMPLE | table=d | partitions=NULL | type=ref | possible_keys=IX_DiscoveryResults_TenantId_ConnectorType_ExternalId,IX_DiscoveryResults_TenantId,IX_DiscoveryResults_TenantId_JobId_ReviewStatus,IX_DiscoveryResults_TenantId_ReviewStatus_Confidence,IX_DiscoveryResults_Tenant_Retrieved | key=IX_DiscoveryResults_TenantId_ConnectorType_ExternalId | key_len=144 | ref=const | rows=49313 | filtered=11.11 | Extra=Using index condition; Using where; Using temporary; Using filesort
```

### REPORT campaigns (page of 50, 90 d)
Query 1:
```
id=1 | select_type=SIMPLE | table=c | partitions=NULL | type=const | possible_keys=IX_ConversionSettings_TenantId | key=IX_ConversionSettings_TenantId | key_len=144 | ref=const | rows=1 | filtered=100 | Extra=NULL
```
Query 2:
```
id=1 | select_type=SIMPLE | table=a | partitions=NULL | type=const | possible_keys=IX_AnalyticsRefreshStates_TenantId | key=IX_AnalyticsRefreshStates_TenantId | key_len=144 | ref=const | rows=1 | filtered=100 | Extra=NULL
```
Query 3:
```
id=1 | select_type=SIMPLE | table=c | partitions=NULL | type=range | possible_keys=IX_Campaigns_TenantId,IX_Campaigns_TenantId_CreatedAtUtc,IX_Campaigns_TenantId_ParentCampaignId,IX_Campaigns_TenantId_Status_ScheduledAtUtc,IX_Campaigns_Tenant_Started | key=IX_Campaigns_Tenant_Started | key_len=153 | ref=NULL | rows=84 | filtered=100 | Extra=Using where; Using index
id=1 | select_type=SIMPLE | table=c0 | partitions=NULL | type=eq_ref | possible_keys=IX_CampaignStatsRollups_CampaignId,IX_CampaignStatsRollups_TenantId | key=IX_CampaignStatsRollups_CampaignId | key_len=144 | ref=leadpilot_bench_860adbdd.c.Id | rows=1 | filtered=100 | Extra=Using where
```
Query 4:
```
id=1 | select_type=SIMPLE | table=c | partitions=NULL | type=ref | possible_keys=IX_Campaigns_TenantId,IX_Campaigns_TenantId_CreatedAtUtc,IX_Campaigns_TenantId_ParentCampaignId,IX_Campaigns_TenantId_Status_ScheduledAtUtc,IX_Campaigns_Tenant_Started | key=IX_Campaigns_TenantId | key_len=144 | ref=const | rows=300 | filtered=10 | Extra=Using index condition; Using where; Using filesort
id=1 | select_type=SIMPLE | table=c0 | partitions=NULL | type=eq_ref | possible_keys=IX_CampaignStatsRollups_CampaignId,IX_CampaignStatsRollups_TenantId | key=IX_CampaignStatsRollups_CampaignId | key_len=144 | ref=leadpilot_bench_860adbdd.c.Id | rows=1 | filtered=100 | Extra=Using where
```

### REPORT conversion-funnel (page of 50, 90 d)
Query 1:
```
id=1 | select_type=SIMPLE | table=c | partitions=NULL | type=const | possible_keys=IX_ConversionSettings_TenantId | key=IX_ConversionSettings_TenantId | key_len=144 | ref=const | rows=1 | filtered=100 | Extra=NULL
```
Query 2:
```
id=1 | select_type=SIMPLE | table=a | partitions=NULL | type=const | possible_keys=IX_AnalyticsRefreshStates_TenantId | key=IX_AnalyticsRefreshStates_TenantId | key_len=144 | ref=const | rows=1 | filtered=100 | Extra=NULL
```
Query 3:
```
id=1 | select_type=SIMPLE | table=l | partitions=NULL | type=range | possible_keys=IX_Leads_TenantId,IX_Leads_TenantId_CategoryId,IX_Leads_TenantId_City,IX_Leads_TenantId_DedupKey,IX_Leads_TenantId_IsArchived_CreatedAtUtc,IX_Leads_TenantId_NormalizedName,IX_Leads_TenantId_OwnerUserId,IX_Leads_TenantId_PipelineStageId,IX_Leads_TenantId_Score,IX_Leads_TenantId_StatusId,IX_Leads_TenantId_WebsiteDomain,IX_Leads_Tenant_Created | key=IX_Leads_TenantId_IsArchived_CreatedAtUtc | key_len=153 | ref=NULL | rows=10865 | filtered=100 | Extra=Using where; Using index for skip scan
```
Query 4:
```
id=1 | select_type=SIMPLE | table=l | partitions=NULL | type=ref | possible_keys=PRIMARY,IX_Leads_TenantId,IX_Leads_TenantId_CategoryId,IX_Leads_TenantId_City,IX_Leads_TenantId_DedupKey,IX_Leads_TenantId_IsArchived_CreatedAtUtc,IX_Leads_TenantId_NormalizedName,IX_Leads_TenantId_OwnerUserId,IX_Leads_TenantId_PipelineStageId,IX_Leads_TenantId_Score,IX_Leads_TenantId_StatusId,IX_Leads_TenantId_WebsiteDomain,IX_Leads_Tenant_Created | key=IX_Leads_TenantId_IsArchived_CreatedAtUtc | key_len=144 | ref=const | rows=97807 | filtered=11.11 | Extra=Using where; Using index
id=1 | select_type=SIMPLE | table=c | partitions=NULL | type=ref | possible_keys=PRIMARY,IX_Contacts_TenantId_LeadId_EmailUniqueKey,IX_Contacts_TenantId_LeadId_PhoneUniqueKey,IX_Contacts_LeadId,IX_Contacts_TenantId,IX_Contacts_TenantId_Email,IX_Contacts_TenantId_PhoneE164 | key=IX_Contacts_TenantId_LeadId_PhoneUniqueKey | key_len=288 | ref=const,leadpilot_bench_860adbdd.l.Id | rows=1 | filtered=100 | Extra=Using where; Using index
id=1 | select_type=SIMPLE | table=m | partitions=NULL | type=ref | possible_keys=IX_Messages_TenantId_ChannelAccountId_ExternalMessageId,IX_Messages_TenantId_IdempotencyKey,IX_Messages_ContactId,IX_Messages_TenantId,IX_Messages_TenantId_ChannelAccountId_SentAtUtc,IX_Messages_TenantId_ConversationId_OccurredAtUtc,IX_Messages_TenantId_CampaignId_Status,IX_Messages_TenantId_CampaignRecipientId,IX_Messages_Tenant_Contact_Dir,IX_Messages_Tenant_Dir_Occurred,IX_Messages_Tenant_Dir_Sent,IX_Messages_Tenant_Failed | key=IX_Messages_ContactId | key_len=145 | ref=leadpilot_bench_860adbdd.c.Id | rows=6 | filtered=4.5 | Extra=Using where; FirstMatch(l)
```

### REPORT rep-performance (page of 50, 90 d)
Query 1:
```
id=1 | select_type=SIMPLE | table=c | partitions=NULL | type=const | possible_keys=IX_ConversionSettings_TenantId | key=IX_ConversionSettings_TenantId | key_len=144 | ref=const | rows=1 | filtered=100 | Extra=NULL
```
Query 2:
```
id=1 | select_type=SIMPLE | table=a | partitions=NULL | type=const | possible_keys=IX_AnalyticsRefreshStates_TenantId | key=IX_AnalyticsRefreshStates_TenantId | key_len=144 | ref=const | rows=1 | filtered=100 | Extra=NULL
```
Query 3:
```
id=1 | select_type=SIMPLE | table=o | partitions=NULL | type=ref | possible_keys=IX_Opportunities_TenantId,IX_Opportunities_TenantId_LeadId,IX_Opportunities_TenantId_PipelineStageId,IX_Opportunities_Tenant_Status_Updated | key=IX_Opportunities_TenantId | key_len=144 | ref=const | rows=14792 | filtered=2 | Extra=Using index condition; Using where; Using temporary
```
Query 4:
```
id=1 | select_type=SIMPLE | table=l | partitions=NULL | type=range | possible_keys=IX_Leads_TenantId,IX_Leads_TenantId_CategoryId,IX_Leads_TenantId_City,IX_Leads_TenantId_DedupKey,IX_Leads_TenantId_IsArchived_CreatedAtUtc,IX_Leads_TenantId_NormalizedName,IX_Leads_TenantId_OwnerUserId,IX_Leads_TenantId_PipelineStageId,IX_Leads_TenantId_Score,IX_Leads_TenantId_StatusId,IX_Leads_TenantId_WebsiteDomain,IX_Leads_Tenant_Created | key=IX_Leads_TenantId_OwnerUserId | key_len=289 | ref=NULL | rows=97807 | filtered=10 | Extra=Using index condition; Using where
```

