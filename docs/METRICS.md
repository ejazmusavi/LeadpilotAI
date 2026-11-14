# Metrics and reports — definitions, sources, audit trail

Every number on the dashboard and in the reports is computed from **recorded rows**. Nothing is hard-coded, sampled or invented: a workspace without records shows zeros, "—" for rates (no denominator, no rate) and empty states. The same text below is what the UI shows in the tooltips/definitions page (`Reports → Metric definitions`, `GET /api/v1/analytics/metrics`); a unit test fails if a metric or report is missing from this file.

## Labels

| Label | Meaning | Examples |
|---|---|---|
| **Verified** | Counted or summed directly from recorded facts (an event that was stored, a payment that was verified). | new leads, demos completed, verified revenue |
| **Calculated** | Derived from verified numbers by a stated formula (rates, rule unions). The formula is shown. | delivery rate, lead conversion rate, contact-eligible leads |
| **Estimated** | Rests on an assumption or a provider estimate. Never presented as a fact. | discovery cost per city, AI cost note in the AI report |

## Date filters

Presets (today, yesterday, last 7/30/90 days, this/last month, this year) or a custom range.

* Dates are **workspace-local calendar days** in the workspace time zone (`Demo & trial settings → time zone`; `UTC` when none is set).
* `from` and `to` are **both inclusive** (`2026-03-08`..`2026-03-09` is two whole days).
* Instants are half-open (start inclusive, end **exclusive**): **[local midnight of the first day, local midnight of the day after the last day)**, converted to UTC. An event exactly at local `00:00:00` belongs to the new day; one at `23:59:59` to the old day. DST days are 23 / 25 hours long (tested for spring-forward and fall-back).
* The **previous period** has the same number of local days and ends the day before the range starts (adjacent, no gap, no overlap). Comparison % is `(current − previous) ÷ previous × 100`; with a previous value of 0 there is no percentage ("no comparison"). For rates the comparison is the difference in **percentage points**.
* A range may cover at most 800 days; an end before the start, a missing end/start for `Custom`, or an unknown zone is refused (400), never guessed.
* Current-state metrics (total, verified, eligible, interested, qualified leads, pipeline snapshot) are **not** affected by the date filter and say so.

## Dashboard metrics

Scope (the dashboard needs `Dashboard.View`, reports `Reports.View`): users with `Reports.Team` see the whole workspace; everyone else sees **only their own leads** (leads where they are the owner and everything attached to those leads). Metrics that cannot be attributed to a person (revenue) show "Team only" — never zero.

| Key | Name | Label | Definition | Source tables |
|---|---|---|---|---|
| `leads.total` | Total leads | Verified | Leads that exist and are not archived, now. Snapshot. | Leads |
| `leads.created` | New leads | Verified | Leads created in the period, counted by creation time (including ones archived later). | Leads.CreatedAtUtc |
| `leads.verified` | Verified leads | Verified | Non-archived leads with ≥ 1 non-deleted contact whose data origin is Verified or whose phone was verified. Snapshot. | Leads, Contacts |
| `leads.eligible` | Contact-eligible leads | Calculated | Non-archived leads (not in a Suppressed stage) with a non-deleted contact that has a phone, is not on the suppression list (WhatsApp or any channel) and whose latest WhatsApp-marketing consent decision is Granted (a revocation wins a tie). Same rules as the audience builder (`ConsentService.QueryAudienceAsync`; a test proves the two agree). Snapshot. | Leads, PipelineStages, Contacts, ConsentRecords, SuppressionEntries |
| `leads.contacted` | Contacted leads | Verified | Leads whose **first** outbound message was sent in the period (a lead counts once, ever). | Messages (Outbound, SentAtUtc) → Contacts.LeadId |
| `messages.inbound` | Responses | Verified | Inbound messages from known contacts received in the period (unknown numbers have no lead and are not counted). | Messages (Inbound, OccurredAtUtc) |
| `leads.interested` | Interested prospects | Calculated | Non-archived leads whose qualification fit is Possible/Good, **or** whose conversation had a pricing, demo-request or trial-request AI intent. Snapshot. | LeadQualifications, AiInteractions → Conversations → Contacts |
| `leads.qualified` | Qualified leads | Verified | Non-archived leads whose recorded qualification fit is Good. Snapshot. | LeadQualifications |
| `demos.scheduled` | Demos scheduled | Verified | Demo bookings created in the period (any status afterwards). | Demos.CreatedAtUtc |
| `demos.completed` | Demos completed | Verified | Demos marked completed in the period. | Demos.CompletedAtUtc |
| `trials.started` | Trials | Verified | Trials created in the period. | Trials.CreatedAtUtc |
| `customers.won` | Customers won | Verified | Leads converted to customers in the period. | Customers.ConvertedAtUtc |
| `opportunities.lost` | Lost opportunities | Verified | Opportunities with status Lost, counted at their last change (the move to a Lost stage). | Opportunities.UpdatedAtUtc |
| `campaigns.sent` | Campaign messages sent | Verified | Campaign recipients whose message was sent in the period. | CampaignRecipients.SentAtUtc |
| `campaigns.replied` | (input of the reply rate) | Verified | Of those, recipients that replied. | CampaignRecipients.RepliedAtUtc |
| `rate.campaign_reply` | Campaign reply rate | Calculated | `replied ÷ sent × 100`; no rate when nothing was sent. | CampaignRecipients |
| `ai.conversations` | AI conversations | Verified | Conversations in which the AI first acted in the period (each conversation counts once, ever). | AiInteractions by conversation |
| `handoffs.human` | Human handoffs | Verified | Takeover, escalation or automatic takeover events in the period (a pause/resume is not a handoff). | HandoffEvents |
| `rate.delivery` | Message delivery rate | Calculated | `delivered ÷ sent × 100` over outbound messages **sent** in the period (a late receipt counts once the rollup is refreshed). | Messages |
| `rate.lead_conversion` | Lead conversion rate | Calculated | A **cohort** rate: leads created in the period that are (today) customers ÷ leads created in the period × 100. | Leads, Customers |
| `revenue.verified` | Verified revenue | Verified | Payments verified by staff or by a signed provider event, **per currency**; never added across currencies. A payment request that was never verified is not revenue (a payment row only exists after verification). | Payments.PaidAtUtc/Amount/Currency |
| `messages.sent` | Messages sent | Verified | Outbound messages the gateway accepted (status Sent or later). Feeds the rate and the messaging report. | Messages.SentAtUtc |
| `messages.delivered` | Messages delivered | Verified | Of those sent in the period, ones with a delivered or read receipt. | Messages.DeliveredAtUtc (a read receipt also sets it) |
| `messages.failed` | Messages failed | Verified | Messages whose attempt failed in the period. | Messages.FailedAtUtc |

Duplicates: events are rows, so duplicate *rows* are separate events (two handoff events are two handoffs); "first" metrics (`leads.contacted`, `ai.conversations`) are deliberately de-duplicated per lead / conversation. Archived leads stay in period counts of things that happened (they were created, they were messaged) but leave all current-state totals; soft-deleted contacts never make a lead verified or eligible.

## Drill-down

Metric cards link to the underlying records where a list page exists: *Total leads* and *New leads* open the leads list with the same filters (creation range, archived = all, and the owner for an own-data view). Trials, customers, revenue (payments), campaigns, responses / AI conversations / handoffs (conversations) and demos open their list **without** a date filter — the card says "Open list (not date-filtered)". The other metrics (verified, eligible, interested, qualified, contacted, lost, rates) have no list page that can express their definition, so they have no link rather than a misleading one.

## Reports

All reports are paginated (`page`, `pageSize` ≤ 200) with a totals row computed over **all** rows, and exportable as CSV. `Team` = needs `Reports.Team`; the others show the viewer's own leads without it.

| Key | Title | Scope | What a row is | Notes |
|---|---|---|---|---|
| `discovery-by-city` | Lead discovery by city | Team | Discovery results grouped by city (paged in SQL) with approved/rejected/pending, leads created, duplicates, searches, provider calls and **Estimated** cost | Job columns count all discovery jobs created in the period (also for cities with no results); cost is an estimate from provider prices |
| `lead-sources` | Lead source performance | Team | Leads created in the period by primary source type: qualified, customers, conversion rate | Customers = leads that are customers today |
| `campaigns` | Campaign performance | Team | Campaigns started in the period (paged in SQL) from the Phase 6 recomputed statistics rollup | Sent = sent+delivered+read+replied, Delivered = delivered+read+replied |
| `sales-pipeline` | Sales pipeline | Own/Team | Pipeline stage: current non-archived leads, open opportunities, open value per currency | Snapshot; values never added across currencies |
| `conversion-funnel` | Conversion funnel | Own/Team | Steps for the cohort of leads created in the period: contacted, replied, interested, qualified, demo booked, demo completed, trial, customer | Steps are independent; % of previous step and % of cohort |
| `rep-performance` | Sales representative performance | Own (self row) / Team (everyone) | Per person: leads owned, messages sent (not AI), demos completed, no-shows, opportunities won/lost, won value per currency | |
| `ai-agent` | AI agent performance | Team | Per sales agent: interactions, drafts, escalations, no-reply, auto-sent, used, dismissed, injection suspected, avg latency, tool outcomes, auto-send and escalation rates | **Estimated** AI cost of the period in the notes |
| `demo-conversion` | Demo conversion | Own/Team | Demos created in the period by outcome; leads with a completed demo that started a trial / became a customer afterwards | Each rate states its basis |
| `trial-conversion` | Trial conversion | Own/Team | Trials created in the period by status; conversion rate overall and of ended trials | |
| `subscription-revenue` | Subscription revenue | Team | Per currency: verified payments, paying customers, revenue, average payment | No grand total across currencies |
| `messaging-usage` | Messaging usage | Team | Per workspace-local day: sent, delivered, failed, inbound, delivery rate | Delivered is attributed to the day the message was sent |

## Freshness, rollup and cache

* **Daily rollup** (`DailyMetrics`, one row per workspace × metric × local day × currency, sparse): refreshed by the Hangfire job `analytics-refresh` every 10 minutes (last 7 local days, to catch late receipts) and rebuilt fully every night at 03:30 UTC (400 days); a full rebuild also runs when nothing exists yet or the workspace time zone changed. Recomputed from the raw tables, **never incremented** (same pattern as the Phase 6 campaign rollup). A test proves rollup == live query for every stream and day.
* The dashboard reads the rollup when it covers the range and shows **"Rollup — data as of …"**; otherwise (new workspace, range older than the retention, own-data view) it computes live and says **"Live"**. A rollup older than 45 minutes is flagged stale.
* **Cache**: in-process, tenant-scoped (`TenantCacheKeys`), 60-second TTL, 2000 entries; the key contains the rollup's refresh stamp and a per-tenant version that a refresh bumps. Each process (Web, Api) has its own cache; there is no shared cache service in the product.
* **Indexes** added for these queries and their EXPLAIN evidence: `docs/DATABASE_DESIGN.md` → Phase 11.

## Export

CSV (`GET /api/v1/analytics/reports/{key}/export`, `Reports → Export CSV`): permission `Reports.Export` (and `Reports.Team` for workspace-wide reports); UTF-8 with BOM; every text cell is passed through the shared formula-injection guard (`=`, `+`, `-`, `@`, tab and CR prefixes are neutralised); streamed to the response in chunks of 1 000 rows; **row cap 50 000** (`Analytics:MaxExportRows`) — larger reports answer 413 before anything is audited or sent. Each export writes an audit entry **before any data is sent**: action `Report.Exported`, who, report key, row count, date range and zone, scope, time (and IP/correlation id as for every audit entry) — visible in the audit log.
