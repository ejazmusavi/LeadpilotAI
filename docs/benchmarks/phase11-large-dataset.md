migrations applied in 0 ms
# Large-dataset benchmark (2026-11-14 09:56 UTC, scale 1, MySQL 8.0.46-0ubuntu0.24.04.5, 4 cores)

Data generated in 0.0 s:

| Table | Rows | ms |
|---|---:|---:|
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
| stream leads.created (30 d, SQL buckets) | 312 | 159 | 10.2 MiB | 220 MiB | 16,176 events |
| stream leads.contacted (30 d, SQL buckets) | 5,201 | 5,093 | 1.1 MiB | 225 MiB | 0 events |
| stream messages.inbound (30 d, SQL buckets) | 350 | 271 | 10.0 MiB | 225 MiB | 29,472 events |
| stream messages.sent (30 d, SQL buckets) | 99 | 94 | 9.7 MiB | 225 MiB | 66,010 events |
| stream messages.delivered (30 d, SQL buckets) | 93 | 91 | 9.7 MiB | 225 MiB | 56,573 events |
| stream messages.failed (30 d, SQL buckets) | 33 | 18 | 5.3 MiB | 225 MiB | 2,083 events |
| stream demos.scheduled (30 d, SQL buckets) | 11 | 6 | 4.3 MiB | 225 MiB | 1,569 events |
| stream demos.completed (30 d, SQL buckets) | 8 | 3 | 1.5 MiB | 225 MiB | 353 events |
| stream trials.started (30 d, SQL buckets) | 10 | 4 | 2.8 MiB | 225 MiB | 847 events |
| stream customers.won (30 d, SQL buckets) | 8 | 3 | 1.4 MiB | 225 MiB | 326 events |
| stream opportunities.lost (30 d, SQL buckets) | 10 | 4 | 2.2 MiB | 225 MiB | 605 events |
| stream ai.conversations (30 d, SQL buckets) | 127 | 95 | 0.7 MiB | 225 MiB | 59 events |
| stream handoffs.human (30 d, SQL buckets) | 111 | 71 | 5.9 MiB | 225 MiB | 2,313 events |
| stream campaigns.sent (30 d, SQL buckets) | 47 | 31 | 9.7 MiB | 225 MiB | 20,897 events |
| stream campaigns.replied (30 d, SQL buckets) | 28 | 23 | 8.1 MiB | 225 MiB | 5,160 events |
| stream revenue.verified (30 d, SQL buckets) | 25 | 5 | 2.2 MiB | 225 MiB | 493 events |
| snapshot: total leads | 155 | 119 | 0.2 MiB | 225 MiB | 196,000 |
| snapshot: verified leads | 985 | 962 | 0.3 MiB | 225 MiB | 84,058 |
| snapshot: contact-eligible leads | 2,579 | 2,686 | 0.7 MiB | 225 MiB | 189,159 |
| snapshot: interested leads | 1,867 | 1,686 | 0.6 MiB | 225 MiB | 41,400 |
| snapshot: qualified leads | 309 | 126 | 0.2 MiB | 225 MiB | 15,000 |
| cohort: lead conversion (30 d) | 280 | 245 | 0.4 MiB | 225 MiB | 335/16,176 |
| DASHBOARD live, team, 30 d + previous (all 20 tiles + trends, no rollup, no cache) | 19,133 | 18,604 | 36.1 MiB | 225 MiB | Live, leads.created=16,176 |
| DASHBOARD live, own data (one sales rep), 30 d + previous | 41,004 | 34,572 | 26.8 MiB | 225 MiB | Live, own leads.total=36,000 |
| ROLLUP full rebuild (400 days, 16 streams) | 34,378 | — | 1,150.3 MiB | 453 MiB | 6,500 rows written |
| ROLLUP incremental refresh (7 days) | 7,491 | 7,505 | 20.9 MiB | 452 MiB | 119 rows |
| DASHBOARD from rollup, team, 30 d + previous | 537 | 547 | 1.4 MiB | 452 MiB | Rollup, leads.created=16,176 |
| DASHBOARD from rollup, team, 365 days + previous | 1,752 | 1,438 | 5.2 MiB | 452 MiB | Rollup |
| REPORT discovery-by-city (page of 50, 90 d) | 1,014 | 919 | 2.1 MiB | 452 MiB | 3,000 rows |
| REPORT lead-sources (page of 50, 90 d) | 954 | 770 | 0.9 MiB | 452 MiB | 6 rows |
| REPORT campaigns (page of 50, 90 d) | 47 | 9 | 1.7 MiB | 452 MiB | 84 rows |
| REPORT sales-pipeline (page of 50, 90 d) | 826 | 861 | 0.9 MiB | 452 MiB | 14 rows |
| REPORT conversion-funnel (page of 50, 90 d) | 5,331 | 5,242 | 2.2 MiB | 452 MiB | 9 rows |
| REPORT rep-performance (page of 50, 90 d) | 4,507 | 4,449 | 1.7 MiB | 452 MiB | 5 rows |
| REPORT ai-agent (page of 50, 90 d) | 502 | 470 | 1.3 MiB | 452 MiB | 1 rows |
| REPORT demo-conversion (page of 50, 90 d) | 141 | 90 | 0.9 MiB | 452 MiB | 8 rows |
| REPORT trial-conversion (page of 50, 90 d) | 36 | 21 | 0.3 MiB | 452 MiB | 7 rows |
| REPORT subscription-revenue (page of 50, 90 d) | 31 | 17 | 0.4 MiB | 452 MiB | 3 rows |
| REPORT messaging-usage (page of 50, 90 d) | 14 | 6 | 1.2 MiB | 452 MiB | 90 rows |
| EXPORT CSV discovery-by-city (90 d) streamed | 3,452 | — | 22.9 MiB | 452 MiB | 95 KiB written, audited 1x |
| EXPORT CSV messaging-usage (90 d) streamed | 10 | — | 2.3 MiB | 452 MiB | 3 KiB written, audited 1x |
| EXPORT CSV campaigns (90 d) streamed | 16 | — | 1.4 MiB | 452 MiB | 6 KiB written, audited 1x |

Working set after generation, before the measurements: 209 MiB; peak working set of this process at the end: 452 MiB (the process holds no record sets: only grouped result rows).

## Index justification (same query with the index and with IGNORE INDEX, median of 3 warm runs)

| Index | Query | with (ms) | without (ms) | plan with: key / rows | plan without: key / rows | verdict |
|---|---|---:|---:|---|---|---|
| `IX_Leads_Tenant_Created` | new leads (30 d) | — | — | — | — | dropped after round 1 (no gain, see phase11-round1-before-optimisation.md) |
| `IX_Messages_Tenant_Dir_Sent` | messages sent (30 d) | 50.6 | 792.2 | Messages: IX_Messages_Tenant_Dir_Sent / 134746 | Messages: IX_Messages_Tenant_Contact_Dir_Sent / 588254 | KEEP (faster) |
| `IX_Messages_Tenant_Dir_Occurred` | responses (30 d) | 330.4 | 3,306.2 | Messages: IX_Messages_Tenant_Dir_Occurred / 58426 | Messages: IX_Messages_TenantId_ChannelAccountId_ExternalMessageId / 588254 | KEEP (faster) |
| `IX_Messages_Tenant_Failed` | failed (30 d) | 11.5 | 3,205.5 | Messages: IX_Messages_Tenant_Failed / 3756 | Messages: IX_Messages_TenantId_ChannelAccountId_ExternalMessageId / 588254 | KEEP (faster) |
| `IX_Messages_Tenant_Contact_Dir` | funnel 'contacted' (90 d cohort) | — | — | — | — | dropped after round 1 (no gain, see phase11-round1-before-optimisation.md) |
| `IX_Messages_Tenant_Contact_Dir_Sent` | contacted leads: earlier-message check (30 d) | 1,788.8 | 2,242.1 | <derived2>: (no index) / 6534; c2: IX_Contacts_TenantId_LeadId_PhoneUniqueKey / 1; m2: IX_Messages_Tenant_Contact_Dir_Sent / 6; m: IX_Messages_Tenant_Contact_Dir_Sent / 588254; c: PRIMARY / 1 | <derived2>: (no index) / 6534; c2: IX_Contacts_TenantId_LeadId_PhoneUniqueKey / 1; m2: IX_Messages_ContactId / 6; m: IX_Messages_Tenant_Contact_Dir_Sent / 588254; c: PRIMARY / 1 | plan uses it, no measurable gain |
| `IX_Messages_Tenant_Dir_Sent` | messages delivered (30 d) | 51.0 | 3,286.9 | Messages: IX_Messages_Tenant_Dir_Sent / 134746 | Messages: IX_Messages_TenantId_ChannelAccountId_ExternalMessageId / 588254 | KEEP (faster) |
| `IX_Demos_Tenant_Created` | demos scheduled (30 d) | 3.7 | 32.0 | Demos: IX_Demos_Tenant_Created / 1569 | Demos: IX_Demos_TenantId_ActiveSlot / 9900 | KEEP (faster) |
| `IX_Demos_Tenant_Completed` | demos completed (30 d) | 0.6 | 32.7 | Demos: IX_Demos_Tenant_Completed / 353 | Demos: IX_Demos_TenantId_ActiveSlot / 9900 | KEEP (faster) |
| `IX_Trials_Tenant_Created` | trials (30 d) | 1.0 | 20.7 | Trials: IX_Trials_Tenant_Created / 847 | Trials: IX_Trials_TenantId / 5014 | KEEP (faster) |
| `IX_Customers_Tenant_Converted` | customers won (30 d) | 0.6 | 9.9 | Customers: IX_Customers_Tenant_Converted / 326 | Customers: IX_Customers_TenantId_LeadId / 1969 | KEEP (faster) |
| `IX_Payments_Tenant_Paid` | revenue (30 d) | 2.2 | 15.8 | Payments: IX_Payments_Tenant_Paid / 493 | Payments: IX_Payments_TenantId_EvidenceKey / 2992 | KEEP (faster) |
| `IX_Opportunities_Tenant_Status_Updated` | lost opportunities (30 d) | 1.4 | 61.9 | Opportunities: IX_Opportunities_Tenant_Status_Updated / 605 | Opportunities: IX_Opportunities_TenantId / 14792 | KEEP (faster) |
| `IX_HandoffEvents_Tenant_Occurred` | human handoffs (30 d) | — | — | — | — | dropped after round 1 (no gain, see phase11-round1-before-optimisation.md) |
| `IX_AiInteractions_Tenant_Created` | AI report (90 d, by agent) | — | — | — | — | dropped after round 1 (no gain, see phase11-round1-before-optimisation.md) |
| `IX_CampaignRecipients_Tenant_Sent` | campaign messages sent (30 d) | 14.1 | 637.3 | CampaignRecipients: IX_CampaignRecipients_Tenant_Sent / 43930 | CampaignRecipients: IX_CampaignRecipients_TenantId / 124373 | KEEP (faster) |
| `IX_Campaigns_Tenant_Started` | campaigns started (30 d) | — | — | — | — | dropped after round 1 (no gain, see phase11-round1-before-optimisation.md) |
| `IX_DiscoveryResults_Tenant_Retrieved` | discovery by city (30 d) | — | — | — | — | dropped after round 1 (no gain, see phase11-round1-before-optimisation.md) |

## EXPLAIN of the heaviest queries

### stream leads.contacted (30 d, SQL buckets)
Query 1:
```
id=1 | select_type=PRIMARY | table=<derived3> | partitions=NULL | type=ALL | possible_keys=NULL | key=NULL | key_len=NULL | ref=NULL | rows=1633 | filtered=100 | Extra=Using temporary
id=3 | select_type=DERIVED | table=m | partitions=NULL | type=ref | possible_keys=IX_Messages_TenantId_ChannelAccountId_ExternalMessageId,IX_Messages_TenantId_IdempotencyKey,IX_Messages_ContactId,IX_Messages_TenantId,IX_Messages_TenantId_ChannelAccountId_SentAtUtc,IX_Messages_TenantId_ConversationId_OccurredAtUtc,IX_Messages_TenantId_CampaignId_Status,IX_Messages_TenantId_CampaignRecipientId,IX_Messages_Tenant_Dir_Occurred,IX_Messages_Tenant_Failed,IX_Messages_Tenant_Dir_Sent,IX_Messages_Tenant_Contact_Dir_Sent | key=IX_Messages_TenantId_ChannelAccountId_ExternalMessageId | key_len=144 | ref=const | rows=588254 | filtered=0.56 | Extra=Using index condition; Using where; Using temporary
id=3 | select_type=DERIVED | table=c | partitions=NULL | type=eq_ref | possible_keys=PRIMARY,IX_Contacts_TenantId_LeadId_EmailUniqueKey,IX_Contacts_TenantId_LeadId_PhoneUniqueKey,IX_Contacts_LeadId,IX_Contacts_TenantId,IX_Contacts_TenantId_Email,IX_Contacts_TenantId_PhoneE164 | key=PRIMARY | key_len=144 | ref=leadpilot_bench_860adbdd.m.ContactId | rows=1 | filtered=50 | Extra=Using where
id=5 | select_type=DEPENDENT SUBQUERY | table=c1 | partitions=NULL | type=ref | possible_keys=PRIMARY,IX_Contacts_TenantId_LeadId_EmailUniqueKey,IX_Contacts_TenantId_LeadId_PhoneUniqueKey,IX_Contacts_LeadId,IX_Contacts_TenantId,IX_Contacts_TenantId_Email,IX_Contacts_TenantId_PhoneE164 | key=IX_Contacts_TenantId_LeadId_PhoneUniqueKey | key_len=288 | ref=const,func | rows=1 | filtered=100 | Extra=Using where; Using index
id=5 | select_type=DEPENDENT SUBQUERY | table=m0 | partitions=NULL | type=ref | possible_keys=IX_Messages_TenantId_ChannelAccountId_ExternalMessageId,IX_Messages_TenantId_IdempotencyKey,IX_Messages_ContactId,IX_Messages_TenantId,IX_Messages_TenantId_ChannelAccountId_SentAtUtc,IX_Messages_TenantId_ConversationId_OccurredAtUtc,IX_Messages_TenantId_CampaignId_Status,IX_Messages_TenantId_CampaignRecipientId,IX_Messages_Tenant_Dir_Occurred,IX_Messages_Tenant_Failed,IX_Messages_Tenant_Dir_Sent,IX_Messages_Tenant_Contact_Dir_Sent | key=IX_Messages_Tenant_Contact_Dir_Sent | key_len=371 | ref=const,leadpilot_bench_860adbdd.c1.Id,const | rows=6 | filtered=33.33 | Extra=Using where; Using index; FirstMatch(c1)
```

### stream messages.inbound (30 d, SQL buckets)
Query 1:
```
id=1 | select_type=SIMPLE | table=m | partitions=NULL | type=range | possible_keys=IX_Messages_TenantId_ChannelAccountId_ExternalMessageId,IX_Messages_TenantId_IdempotencyKey,IX_Messages_ContactId,IX_Messages_TenantId,IX_Messages_TenantId_ChannelAccountId_SentAtUtc,IX_Messages_TenantId_ConversationId_OccurredAtUtc,IX_Messages_TenantId_CampaignId_Status,IX_Messages_TenantId_CampaignRecipientId,IX_Messages_Tenant_Dir_Occurred,IX_Messages_Tenant_Failed,IX_Messages_Tenant_Dir_Sent,IX_Messages_Tenant_Contact_Dir_Sent | key=IX_Messages_Tenant_Dir_Occurred | key_len=234 | ref=NULL | rows=58426 | filtered=50 | Extra=Using index condition; Using where; Using temporary
```

### stream messages.sent (30 d, SQL buckets)
Query 1:
```
id=1 | select_type=SIMPLE | table=m | partitions=NULL | type=range | possible_keys=IX_Messages_TenantId_ChannelAccountId_ExternalMessageId,IX_Messages_TenantId_IdempotencyKey,IX_Messages_TenantId,IX_Messages_TenantId_ChannelAccountId_SentAtUtc,IX_Messages_TenantId_ConversationId_OccurredAtUtc,IX_Messages_TenantId_CampaignId_Status,IX_Messages_TenantId_CampaignRecipientId,IX_Messages_Tenant_Dir_Occurred,IX_Messages_Tenant_Failed,IX_Messages_Tenant_Dir_Sent,IX_Messages_Tenant_Contact_Dir_Sent | key=IX_Messages_Tenant_Dir_Sent | key_len=235 | ref=NULL | rows=134746 | filtered=100 | Extra=Using where; Using index; Using temporary
```

### stream ai.conversations (30 d, SQL buckets)
Query 1:
```
id=1 | select_type=PRIMARY | table=<derived3> | partitions=NULL | type=ALL | possible_keys=NULL | key=NULL | key_len=NULL | ref=NULL | rows=49300 | filtered=100 | Extra=Using temporary
id=3 | select_type=DERIVED | table=a | partitions=NULL | type=ref | possible_keys=IX_AiInteractions_TenantId_TriggerMessageId,IX_AiInteractions_ConversationId,IX_AiInteractions_TenantId,IX_AiInteractions_TenantId_ConversationId_CreatedAtUtc,IX_AiInteractions_TenantId_Status_CreatedAtUtc | key=IX_AiInteractions_TenantId_ConversationId_CreatedAtUtc | key_len=144 | ref=const | rows=49300 | filtered=100 | Extra=Using where; Using index
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
id=1 | select_type=SIMPLE | table=l | partitions=NULL | type=eq_ref | possible_keys=PRIMARY,IX_Leads_TenantId,IX_Leads_TenantId_CategoryId,IX_Leads_TenantId_City,IX_Leads_TenantId_DedupKey,IX_Leads_TenantId_IsArchived_CreatedAtUtc,IX_Leads_TenantId_NormalizedName,IX_Leads_TenantId_OwnerUserId,IX_Leads_TenantId_PipelineStageId,IX_Leads_TenantId_Score,IX_Leads_TenantId_StatusId,IX_Leads_TenantId_WebsiteDomain | key=PRIMARY | key_len=144 | ref=leadpilot_bench_860adbdd.c.LeadId | rows=1 | filtered=5 | Extra=Using where
```

### snapshot: contact-eligible leads
Query 1:
```
id=1 | select_type=PRIMARY | table=<derived2> | partitions=NULL | type=ALL | possible_keys=NULL | key=NULL | key_len=NULL | ref=NULL | rows=11549 | filtered=100 | Extra=NULL
id=1 | select_type=PRIMARY | table=l | partitions=NULL | type=eq_ref | possible_keys=PRIMARY,IX_Leads_TenantId,IX_Leads_TenantId_CategoryId,IX_Leads_TenantId_City,IX_Leads_TenantId_DedupKey,IX_Leads_TenantId_IsArchived_CreatedAtUtc,IX_Leads_TenantId_NormalizedName,IX_Leads_TenantId_OwnerUserId,IX_Leads_TenantId_PipelineStageId,IX_Leads_TenantId_Score,IX_Leads_TenantId_StatusId,IX_Leads_TenantId_WebsiteDomain | key=PRIMARY | key_len=144 | ref=c1.LeadId | rows=1 | filtered=5 | Extra=Using where
id=1 | select_type=PRIMARY | table=<subquery5> | partitions=NULL | type=eq_ref | possible_keys=<auto_distinct_key> | key=<auto_distinct_key> | key_len=145 | ref=leadpilot_bench_860adbdd.l.PipelineStageId | rows=1 | filtered=100 | Extra=Using where; Not exists
id=5 | select_type=MATERIALIZED | table=p | partitions=NULL | type=ALL | possible_keys=PRIMARY,IX_PipelineStages_TenantId,IX_PipelineStages_TenantId_PipelineId_Position | key=NULL | key_len=NULL | ref=NULL | rows=14 | filtered=100 | Extra=Using where
id=2 | select_type=DERIVED | table=c | partitions=NULL | type=ref | possible_keys=IX_Contacts_TenantId_LeadId_EmailUniqueKey,IX_Contacts_TenantId_LeadId_PhoneUniqueKey,IX_Contacts_LeadId,IX_Contacts_TenantId,IX_Contacts_TenantId_Email,IX_Contacts_TenantId_PhoneE164 | key=IX_Contacts_TenantId_LeadId_EmailUniqueKey | key_len=144 | ref=const | rows=128325 | filtered=9 | Extra=Using index condition; Using where
id=2 | select_type=DERIVED | table=<subquery3> | partitions=NULL | type=eq_ref | possible_keys=<auto_distinct_key> | key=<auto_distinct_key> | key_len=1283 | ref=leadpilot_bench_860adbdd.c.PhoneE164 | rows=1 | filtered=100 | Extra=Using where; Not exists
id=3 | select_type=MATERIALIZED | table=s | partitions=NULL | type=ref | possible_keys=IX_SuppressionEntries_TenantId_Channel_ValueNormalized,IX_SuppressionEntries_TenantId,IX_SuppressionEntries_TenantId_ValueHash,IX_SuppressionEntries_TenantId_ValueNormalized | key=IX_SuppressionEntries_TenantId | key_len=144 | ref=const | rows=2508 | filtered=100 | Extra=Using index condition; Using where
id=4 | select_type=DEPENDENT SUBQUERY | table=c0 | partitions=NULL | type=ref | possible_keys=IX_ConsentRecords_ContactId,IX_ConsentRecords_TenantId,IX_ConsentRecords_TenantId_ContactId_Channel_Purpose_OccurredAt~,IX_ConsentRecords_TenantId_OccurredAtUtc | key=IX_ConsentRecords_ContactId | key_len=144 | ref=leadpilot_bench_860adbdd.c.Id | rows=1 | filtered=5 | Extra=Using where; Using filesort
```

### snapshot: interested leads
Query 1:
```
id=1 | select_type=PRIMARY | table=<derived2> | partitions=NULL | type=ALL | possible_keys=NULL | key=NULL | key_len=NULL | ref=NULL | rows=9647 | filtered=100 | Extra=NULL
id=1 | select_type=PRIMARY | table=l | partitions=NULL | type=eq_ref | possible_keys=PRIMARY,IX_Leads_TenantId,IX_Leads_TenantId_CategoryId,IX_Leads_TenantId_City,IX_Leads_TenantId_DedupKey,IX_Leads_TenantId_IsArchived_CreatedAtUtc,IX_Leads_TenantId_NormalizedName,IX_Leads_TenantId_OwnerUserId,IX_Leads_TenantId_PipelineStageId,IX_Leads_TenantId_Score,IX_Leads_TenantId_StatusId,IX_Leads_TenantId_WebsiteDomain | key=PRIMARY | key_len=144 | ref=u.LeadId | rows=1 | filtered=5 | Extra=Using where
id=2 | select_type=DERIVED | table=l0 | partitions=NULL | type=ref | possible_keys=IX_LeadQualifications_TenantId_LeadId,IX_LeadQualifications_TenantId | key=IX_LeadQualifications_TenantId_LeadId | key_len=144 | ref=const | rows=29750 | filtered=20 | Extra=Using index condition; Using where
id=3 | select_type=UNION | table=a | partitions=NULL | type=ref | possible_keys=IX_AiInteractions_TenantId_TriggerMessageId,IX_AiInteractions_ConversationId,IX_AiInteractions_TenantId,IX_AiInteractions_TenantId_ConversationId_CreatedAtUtc,IX_AiInteractions_TenantId_Status_CreatedAtUtc | key=IX_AiInteractions_TenantId_TriggerMessageId | key_len=144 | ref=const | rows=49300 | filtered=30 | Extra=Using index condition; Using where
id=3 | select_type=UNION | table=c | partitions=NULL | type=eq_ref | possible_keys=PRIMARY,IX_Conversations_TenantId_ChannelAccountId_ExternalChatId,IX_Conversations_ContactId,IX_Conversations_TenantId,IX_Conversations_TenantId_ContactId,IX_Conversations_TenantId_LastMessageAtUtc | key=PRIMARY | key_len=144 | ref=leadpilot_bench_860adbdd.a.ConversationId | rows=1 | filtered=50 | Extra=Using where
id=3 | select_type=UNION | table=c1 | partitions=NULL | type=eq_ref | possible_keys=PRIMARY,IX_Contacts_TenantId_LeadId_EmailUniqueKey,IX_Contacts_TenantId_LeadId_PhoneUniqueKey,IX_Contacts_TenantId,IX_Contacts_TenantId_Email,IX_Contacts_TenantId_PhoneE164 | key=PRIMARY | key_len=144 | ref=leadpilot_bench_860adbdd.c.ContactId | rows=1 | filtered=50 | Extra=Using where
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
id=1 | select_type=SIMPLE | table=d | partitions=NULL | type=ref | possible_keys=UX_DailyMetrics_Tenant_Metric_Day_Dim,IX_DailyMetrics_TenantId | key=UX_DailyMetrics_Tenant_Metric_Day_Dim | key_len=144 | ref=const | rows=3137 | filtered=11.11 | Extra=Using index condition; Using temporary
```
Query 4:
```
id=1 | select_type=SIMPLE | table=d | partitions=NULL | type=ref | possible_keys=UX_DailyMetrics_Tenant_Metric_Day_Dim,IX_DailyMetrics_TenantId | key=UX_DailyMetrics_Tenant_Metric_Day_Dim | key_len=144 | ref=const | rows=3137 | filtered=11.11 | Extra=Using index condition; Using temporary
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
id=2 | select_type=DERIVED | table=d | partitions=NULL | type=ref | possible_keys=IX_DiscoveryResults_TenantId_ConnectorType_ExternalId,IX_DiscoveryResults_TenantId,IX_DiscoveryResults_TenantId_JobId_ReviewStatus,IX_DiscoveryResults_TenantId_ReviewStatus_Confidence | key=IX_DiscoveryResults_TenantId_ConnectorType_ExternalId | key_len=144 | ref=const | rows=49313 | filtered=11.11 | Extra=Using index condition; Using where; Using temporary
```
Query 4:
```
id=1 | select_type=SIMPLE | table=d | partitions=NULL | type=ref | possible_keys=IX_DiscoveryResults_TenantId_ConnectorType_ExternalId,IX_DiscoveryResults_TenantId,IX_DiscoveryResults_TenantId_JobId_ReviewStatus,IX_DiscoveryResults_TenantId_ReviewStatus_Confidence | key=IX_DiscoveryResults_TenantId_ConnectorType_ExternalId | key_len=144 | ref=const | rows=49313 | filtered=11.11 | Extra=Using index condition; Using where; Using temporary; Using filesort
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
id=1 | select_type=SIMPLE | table=c | partitions=NULL | type=ALL | possible_keys=IX_Campaigns_TenantId,IX_Campaigns_TenantId_CreatedAtUtc,IX_Campaigns_TenantId_ParentCampaignId,IX_Campaigns_TenantId_Status_ScheduledAtUtc | key=NULL | key_len=NULL | ref=NULL | rows=300 | filtered=10 | Extra=Using where
id=1 | select_type=SIMPLE | table=c0 | partitions=NULL | type=eq_ref | possible_keys=IX_CampaignStatsRollups_CampaignId,IX_CampaignStatsRollups_TenantId | key=IX_CampaignStatsRollups_CampaignId | key_len=144 | ref=leadpilot_bench_860adbdd.c.Id | rows=1 | filtered=100 | Extra=Using where
```
Query 4:
```
id=1 | select_type=SIMPLE | table=c | partitions=NULL | type=ALL | possible_keys=IX_Campaigns_TenantId,IX_Campaigns_TenantId_CreatedAtUtc,IX_Campaigns_TenantId_ParentCampaignId,IX_Campaigns_TenantId_Status_ScheduledAtUtc | key=NULL | key_len=NULL | ref=NULL | rows=300 | filtered=10 | Extra=Using where; Using filesort
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
id=1 | select_type=SIMPLE | table=l | partitions=NULL | type=range | possible_keys=IX_Leads_TenantId,IX_Leads_TenantId_CategoryId,IX_Leads_TenantId_City,IX_Leads_TenantId_DedupKey,IX_Leads_TenantId_IsArchived_CreatedAtUtc,IX_Leads_TenantId_NormalizedName,IX_Leads_TenantId_OwnerUserId,IX_Leads_TenantId_PipelineStageId,IX_Leads_TenantId_Score,IX_Leads_TenantId_StatusId,IX_Leads_TenantId_WebsiteDomain | key=IX_Leads_TenantId_IsArchived_CreatedAtUtc | key_len=153 | ref=NULL | rows=10865 | filtered=100 | Extra=Using where; Using index for skip scan
```
Query 4:
```
id=1 | select_type=SIMPLE | table=l | partitions=NULL | type=ref | possible_keys=PRIMARY,IX_Leads_TenantId,IX_Leads_TenantId_CategoryId,IX_Leads_TenantId_City,IX_Leads_TenantId_DedupKey,IX_Leads_TenantId_IsArchived_CreatedAtUtc,IX_Leads_TenantId_NormalizedName,IX_Leads_TenantId_OwnerUserId,IX_Leads_TenantId_PipelineStageId,IX_Leads_TenantId_Score,IX_Leads_TenantId_StatusId,IX_Leads_TenantId_WebsiteDomain | key=IX_Leads_TenantId_IsArchived_CreatedAtUtc | key_len=144 | ref=const | rows=97807 | filtered=11.11 | Extra=Using where; Using index
id=1 | select_type=SIMPLE | table=c | partitions=NULL | type=ref | possible_keys=PRIMARY,IX_Contacts_TenantId_LeadId_EmailUniqueKey,IX_Contacts_TenantId_LeadId_PhoneUniqueKey,IX_Contacts_LeadId,IX_Contacts_TenantId,IX_Contacts_TenantId_Email,IX_Contacts_TenantId_PhoneE164 | key=IX_Contacts_TenantId_LeadId_PhoneUniqueKey | key_len=288 | ref=const,leadpilot_bench_860adbdd.l.Id | rows=1 | filtered=100 | Extra=Using where; Using index
id=1 | select_type=SIMPLE | table=m | partitions=NULL | type=ref | possible_keys=IX_Messages_TenantId_ChannelAccountId_ExternalMessageId,IX_Messages_TenantId_IdempotencyKey,IX_Messages_ContactId,IX_Messages_TenantId,IX_Messages_TenantId_ChannelAccountId_SentAtUtc,IX_Messages_TenantId_ConversationId_OccurredAtUtc,IX_Messages_TenantId_CampaignId_Status,IX_Messages_TenantId_CampaignRecipientId,IX_Messages_Tenant_Dir_Occurred,IX_Messages_Tenant_Failed,IX_Messages_Tenant_Dir_Sent,IX_Messages_Tenant_Contact_Dir_Sent | key=IX_Messages_Tenant_Contact_Dir_Sent | key_len=371 | ref=const,leadpilot_bench_860adbdd.c.Id,const | rows=6 | filtered=90 | Extra=Using where; Using index; FirstMatch(l)
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
id=1 | select_type=SIMPLE | table=l | partitions=NULL | type=range | possible_keys=IX_Leads_TenantId,IX_Leads_TenantId_CategoryId,IX_Leads_TenantId_City,IX_Leads_TenantId_DedupKey,IX_Leads_TenantId_IsArchived_CreatedAtUtc,IX_Leads_TenantId_NormalizedName,IX_Leads_TenantId_OwnerUserId,IX_Leads_TenantId_PipelineStageId,IX_Leads_TenantId_Score,IX_Leads_TenantId_StatusId,IX_Leads_TenantId_WebsiteDomain | key=IX_Leads_TenantId_OwnerUserId | key_len=289 | ref=NULL | rows=97807 | filtered=10 | Extra=Using index condition; Using where
```

