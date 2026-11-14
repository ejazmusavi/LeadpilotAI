# Database Design (MySQL 8, EF Core)

## Conventions
Guid PKs (stored `char(36)` / `BINARY(16)` — decide in Phase 1 after benchmarking; use ordered Guids to limit index fragmentation) · `TenantId` on every tenant-owned table · UTC `datetime(6)` · money `decimal(18,2)` · phones E.164 · soft delete (`IsDeleted`, `DeletedAtUtc`) where history matters · audit fields (`CreatedAtUtc/By`, `UpdatedAtUtc/By`) · `RowVersion` for optimistic concurrency on mutable aggregates · composite indexes lead with `TenantId` · explicit FK delete behaviour (Restrict by default; Cascade only for owned children) · all changes via migrations.

Legend — **T** = tenant-owned (has TenantId), **G** = global.

## 1. Tenancy & Identity (Phase 2 — implemented, migration `TenancyAndPermissions`)
Guid keys are `char(36)` (Oracle MySQL provider default); all timestamps `datetime(6)` UTC; enums stored as strings. Tenant-owned tables carry a mandatory FK `TenantId → Tenants(Id)` (RESTRICT) and an index on it.

| Table (entity) | Scope | Columns / constraints |
|---|---|---|
| `Tenants` (Tenant) | G | Name(200), **Slug(63) unique**, Status(Active/Suspended), IsPlatform, Industry, Timezone(64), Locale(16), SuspendedAtUtc, SuspensionReason(500), audit fields; index Status |
| `TenantSettings` | T | Key(100), Value(4000; ciphertext when secret), IsSecret; **unique (TenantId, Key)** |
| `AspNetUsers` (Identity) | G | + DisplayName, IsActive, CreatedAtUtc, LastLoginAtUtc (Phase 1) |
| `UserProfiles` | G | UserId **unique** FK→AspNetUsers (cascade), DisplayName(200), Phone(32) |
| `UserTenants` | T | UserId FK→AspNetUsers, IsDefault, Status(Active/Disabled), JoinedAtUtc, InvitedByUserId; **unique (TenantId, UserId)**, index UserId |
| `Roles` (Role) | T | Name(100), Description, IsSystem; **unique (TenantId, Name)**. (Identity's `AspNetRoles` is separate and unused for authorization.) |
| `Permissions` | G | **Code(100) unique**, Name, Description, Group, IsPlatform, SortOrder. Seeded from `PermissionCatalog` (16 codes). |
| `RolePermissions` | T | RoleId FK→Roles (cascade), PermissionId FK→Permissions (restrict); **unique (RoleId, PermissionId)** |
| `UserRoles` (UserRole) | T | UserId FK→AspNetUsers, RoleId FK→Roles (restrict), AssignedAtUtc, AssignedByUserId; **unique (TenantId, UserId, RoleId)** |
| `TenantInvitations` | T | Email(256, lower-cased), RoleId FK→Roles (restrict), **TokenHash(64) unique** (SHA-256 hex, raw token never stored), ExpiresAtUtc, Status(Pending/Accepted/Revoked; *Expired* is derived), InvitedByUserId, AcceptedAtUtc/By; index (TenantId, Email, Status) |
| `AuditLogs` | T | ActorTenantId?, ActorUserId?, ActorEmail, Action(100), EntityType, EntityId, Summary(1000), IpAddress, CorrelationId, CreatedAtUtc; **append-only (enforced in SaveChanges)**; indexes (TenantId, CreatedAtUtc), (TenantId, Action). TenantId is **not null**: platform events are stored against the target tenant with `ActorTenantId` set (a change from the Phase 0 sketch, which allowed null). |
| `RefreshTokens` | per-user (not row-filtered) | UserId FK (cascade), TenantId FK→Tenants (cascade), **TokenHash(64) unique**, ExpiresAtUtc, RevokedAtUtc, ReplacedByTokenHash, CreatedByIp |

Rules enforced in code on top of the schema: foreign keys between tenant-owned rows must stay inside one tenant (SaveChanges guard); system roles are immutable; platform permissions may only sit on platform-tenant roles; at least one active `Users.Manage` holder must remain in a tenant.

Default system roles: *Platform Owner*, *SaaS Administrator* (platform tenant only) and *Tenant Administrator*, *Tenant Staff*, *Sales Manager*, *Sales Agent*, *Marketing Manager*, *Read-only* (every customer tenant). Mapping lives in `Application/Security/Permissions.cs` and is re-synchronised on every Web start.

## 2. CRM (Phase 3 — implemented, migration `Phase3Crm`)
All tables are tenant-owned (**T**): mandatory FK `TenantId → Tenants` (RESTRICT), index on `TenantId`, global query filter. Enums are stored as strings. Foreign keys between CRM tables are single-column and are checked to stay inside one tenant by the SaveChanges guard. Rows marked *append-only* are rejected by the guard on update/delete.

| Table (entity) | Columns / constraints / indexes |
|---|---|
| `LeadCategories` | Name(100), NormalizedName; **unique (TenantId, NormalizedName)**; IsActive |
| `LeadStatuses` | Name(100), SortOrder, IsClosed, IsDefault, IsActive; **unique (TenantId, Name)** |
| `Pipelines` | Name(100), IsDefault, IsActive; **unique (TenantId, Name)** |
| `PipelineStages` | PipelineId FK, Name(100), Position, Kind(Open/Won/Lost/Suppressed); **unique (PipelineId, Name)**; index (TenantId, PipelineId, Position) |
| `Leads` | Name(300), NormalizedName, **DedupKey(300)** = normalised name \| normalised city, CategoryId? FK, Country/Province/City/District/Area(100), Address(500), Website(500), **WebsiteDomain(255)**, PipelineId FK, PipelineStageId FK, StatusId FK, OwnerUserId? (no FK, validated), Score, ScoreRuleVersion?, ScoredAtUtc?, Confidence, PrimarySourceType, IsArchived, ArchivedAtUtc, LastActivityAtUtc, **Version (concurrency token)**, audit fields. Indexes: (TenantId, DedupKey), (TenantId, WebsiteDomain), (TenantId, IsArchived, CreatedAtUtc), (TenantId, PipelineStageId), (TenantId, StatusId), (TenantId, OwnerUserId), (TenantId, CategoryId), (TenantId, City), (TenantId, Score), (TenantId, NormalizedName) |
| `LeadSources` | LeadId FK, Type, ExternalId(200)?, Name, Url, Attribution, RetrievedAtUtc; **unique (TenantId, Type, ExternalId)** (NULL external ids allowed repeatedly); index (TenantId, LeadId) |
| `Contacts` | LeadId FK, Name(200), Role(100), PhoneRaw(40), **PhoneE164(20)**, Email(256, lower-case), DataOrigin, PhoneVerifiedAtUtc, IsPrimary, IsDeleted, DeletedAtUtc, **PhoneUniqueKey / EmailUniqueKey** (value while live, NULL when removed). **Unique (TenantId, LeadId, PhoneUniqueKey)** and **(TenantId, LeadId, EmailUniqueKey)**; indexes (TenantId, PhoneE164), (TenantId, Email) |
| `ConsentRecords` | *append-only*. ContactId FK, Channel(WhatsApp/Sms/Email/Phone), Purpose(Marketing/Service), Status(Granted/Revoked), IsRevocation, Source(200, required), EvidenceRef(500), Note(1000), OccurredAtUtc, RecordedAtUtc, RecordedByUserId; index (TenantId, ContactId, Channel, Purpose, OccurredAtUtc) serves "latest decision" |
| `SuppressionEntries` | Channel(Any/WhatsApp/Sms/Email/Phone), **ValueNormalized(320)** (E.164 or lower-case e-mail), ValueHash(64, SHA-256), Reason(OptOut/Invalid/Blocked/TenantExcluded/Legal), Note, CreatedByUserId; **unique (TenantId, Channel, ValueNormalized)**; indexes (TenantId, ValueNormalized), (TenantId, ValueHash) |
| `LeadActivities` | *append-only*. LeadId FK, Type, Summary(500), Payload(JSON), ActorUserId, OccurredAtUtc; index (TenantId, LeadId, OccurredAtUtc) |
| `LeadNotes` | LeadId FK, Body(4000), AuthorUserId, CreatedAtUtc, IsDeleted/DeletedAtUtc |
| `LeadTags` / `LeadTagMaps` | Tag: Name(50), NormalizedName; **unique (TenantId, NormalizedName)**. Map: LeadId FK, TagId FK (cascade); **unique (LeadId, TagId)** |
| `LeadScores` | *append-only history*. LeadId FK, Score, RuleVersion, Breakdown(JSON), CalculatedAtUtc |
| `ScoringRuleSets` / `ScoringRules` | Set: Version, IsActive, Note; **unique (TenantId, Version)**. Rule: RuleSetId FK (cascade), Dimension, Target(200), Points(-100..100), Description, IsEnabled |
| `Opportunities` / `OpportunityActivities` | Opportunity: LeadId FK, PipelineStageId FK, Name(200), Value decimal(18,2), Currency(3), ExpectedCloseDateUtc, OwnerUserId, Status. Activity is *append-only* |
| `ImportJobs` | FileName, FileType, FileSizeBytes, StoredFileKey (tenant-relative), Status, DuplicatePolicy, TotalRows, Created/Merged/SkippedDuplicate/PendingDuplicate/Error counts, Mapping(JSON), FailureReason, Started/CompletedAtUtc |
| `ImportRowErrors` | ImportJobId FK (cascade), RowNumber, Kind(Validation/Duplicate), Column, Message(500), RowData(JSON, 8000), DuplicateLeadId? FK, Resolution(Pending/Skipped/Merged); indexes (TenantId, ImportJobId, RowNumber), (TenantId, ImportJobId, Kind) |

Relationships: Lead 1—* Contact; Lead 1—* Opportunity; Contact 1—* ConsentRecord; Contact 1—* Conversation (Messaging, Phase 5); Customers are a separate entity (Phase 10). Deviations from the Phase 0 sketch: `LeadStatus` is a configurable lookup distinct from the pipeline stage; a lead stores `PipelineId` + `PipelineStageId` instead of a status enum; `Contacts` uniqueness uses live-only keys so removed contacts release their numbers; Opportunity stage is a `PipelineStage` of the lead's pipeline; no `ImportRow` staging table (rows needing review are persisted as `ImportRowError.RowData`).

Default data per tenant (code, applied at tenant creation and by the idempotent seeder): pipeline "Sales pipeline" with the 14 stages (New, Researched, Contact Eligible, Contacted, Responded, Interested, Qualified, Demo Scheduled, Demo Completed, Trial, Negotiation, Won, Lost, Suppressed), statuses Open/Working/Nurturing/Disqualified, scoring rule set v1 (9 rules).

## 3. Discovery (Phase 4) — migration `Phase4Discovery`
All tables are tenant-owned (global filter + SaveChanges guard + cross-tenant FK check). Hangfire's own tables (prefix `Hangfire`) are created by Hangfire at runtime, not by EF.
| Table | Columns / constraints |
|---|---|
| `DiscoveryJobs` | ConnectorType(50), Status(Queued/Running/Completed/Failed/Cancelled), Country/Province/City/District/Area(100), CategoryId? FK→LeadCategories, Keywords(300), SeedUrls(4000, website connector), MaxResults, EnrichWebsites, RequestedByUserId, HangfireJobId(64), counters (PagesFetched, ResultsFound, ResultsNew, ResultsAlreadyKnown, DuplicatesFlagged, ApiCalls, EstimatedCostUsd decimal(18,6)), Attempts, **Checkpoint**(2000), CancelRequested, Warnings(1000), ErrorCode(50)/ErrorMessage(500)/ErrorRetryable, StartedAtUtc, CompletedAtUtc, Version. Indexes (TenantId, CreatedAtUtc), (TenantId, Status) |
| `DiscoveryResults` | JobId FK, ConnectorType, **ExternalId(300)**, Name(300), CategoryId? FK, CategoryLabel, Country…Area, Address(500), Website(500), PublicPhone(40), PublicEmail(256), SocialLinks(JSON), SourceName/SourceUrl/Attribution, **RetrievedAtUtc, ExpiresAtUtc, ContentPurged**, LastVerifiedAtUtc, **Confidence 0–100 + ConfidenceLevel**, ResearchNotes(2000), **Provenance**(JSON field→ProviderReported/Unverified/Corroborated), Enrichment status, **ReviewStatus**(Pending/Approved/Rejected; concurrency token), ReviewedByUserId/At, ReviewNote, DuplicateLeadId? FK→Leads, DuplicateReasons, LeadId? FK→Leads, Approval(Created/Merged). **Unique (TenantId, ConnectorType, ExternalId)** = retry/repeat idempotency; indexes (TenantId, JobId, ReviewStatus), (TenantId, ReviewStatus, Confidence), (ExpiresAtUtc, ContentPurged) |
| `DiscoveryConnectorConfigs` | ConnectorType(50), IsEnabled, **ProtectedApiKey**(2000, `enc:v1:` Data Protection ciphertext), KeyHint(last 4), MonthlyCallLimit?, MonthlyBudgetUsd?, CostPerCallUsd?. **Unique (TenantId, ConnectorType)** |
| `DiscoveryUsageEntries` | *append-only ledger*. JobId?, ConnectorType, Operation(100), Calls, EstimatedCostUsd(18,6), HttpStatus?, PeriodKey (yyyymm), OccurredAtUtc. Index (TenantId, ConnectorType, PeriodKey). Monthly usage = sum per period |
| `DiscoveryCategoryMappings` | CategoryId FK→LeadCategories (reuses the Phase 3 category), SearchTerms (≤5 phrases, newline separated), PlaceType?, IsActive. **Unique (TenantId, CategoryId)** |
| `DiscoveryLocationPresets` | Country, Province?, City?, District?, Area?, Label. **Unique (TenantId, Label)** |

Defaults are data, not code paths: the "Education — Pakistan" starter pack (the school categories — Private, Primary, Secondary, Higher secondary schools, Colleges, Academies, Educational institutions — their search phrases, and 11 Pakistani cities) is installed automatically only for workspaces whose industry is *Education*, or on demand for any workspace; installing is idempotent and never overwrites edits. Existing `LeadSources` rows get `(Discovery, "<connector>:<externalId>")`, reusing the Phase 3 unique `(TenantId, Type, ExternalId)`.

## 4. Messaging (Phase 5) — implemented, migration `Phase5Messaging`
All 13 tables are tenant-owned (`TenantId` NOT NULL + FK to `Tenants` + query filter + SaveChanges guard incl. cross-tenant FK rejection). Enums are stored as strings.

| Table | Purpose / key columns | Constraints and indexes |
|---|---|---|
| `Channels` | per-tenant catalog entry (WhatsApp via WAHA), created with the first account | unique `(TenantId, Type, Provider)` |
| `ChannelAccounts` | one WAHA session: `DisplayName`, `GatewayAccountId` (session name), `WebhookKey` (random 24 bytes hex in the webhook URL), `ConnectionStatus`, `PhoneE164`, pause state (`IsPaused`, `PausedAutomatically`, `PauseReason`), `ConsecutiveFailures`, `NewContactBlockedUntilUtc`, `HourlyCap`/`DailyCap`/`MinDelaySeconds`, `WarmupStartedAtUtc`, `LastSendAtUtc`, soft delete | **unique `GatewayAccountId` across ALL tenants**, **unique `WebhookKey`**, FK → `Channels` |
| `ChannelCredentials` | `ProtectedBaseUrl`, `ProtectedApiKey`, `ProtectedWebhookSecret` (Data Protection, ciphertext bound to tenant+account), `KeyHint` | unique `ChannelAccountId`; there is no plain-text column |
| `Conversations` | `ExternalChatId`, `PeerPhoneE164`, `PeerName`, `ContactId?`, `Status`, `AiMode` (default Paused), `AssignedUserId?`, `LastMessageAtUtc`/`LastInboundAtUtc`/`LastOutboundAtUtc`, `LastMessagePreview`, `UnreadCount`, `OptedOutAtUtc?` | unique `(TenantId, ChannelAccountId, ExternalChatId)`, index `(TenantId, LastMessageAtUtc)`, `(TenantId, ContactId)`; FK → account, contact (RESTRICT) |
| `ConversationAssignments` | append-only assignment history | index `(TenantId, ConversationId, AssignedAtUtc)` |
| `Messages` | `Direction`, `Purpose` (Service/Marketing), `Status` (Queued, Sending, Unknown, Sent, Delivered, Read, Failed, Received), `ExternalMessageId`, `Body`, `CorrelationId`, `IdempotencyKey`, `SendAttempts`, `NextAttemptAtUtc`, `SendStartedAtUtc`, sent/delivered/read/failed timestamps, `FailureCode/Message`, `Version` (concurrency token) | **unique `(TenantId, ChannelAccountId, ExternalMessageId)`** (exactly-once storage; NULLs allowed), **unique `(TenantId, IdempotencyKey)`**, index `(Status, NextAttemptAtUtc)` (sweeper), `(TenantId, ChannelAccountId, SentAtUtc)` (rate counts), `(TenantId, ConversationId, OccurredAtUtc)` |
| `MessageAttachments` | metadata only (`FileName`, `MimeType`, `SizeBytes`, `Status` NotDownloaded/Rejected, `StorageKey` stays empty until a later phase stores content through `ITenantFileStore`) | size ≤ 16 MB and a MIME allow-list are applied when the row is created |
| `MessageStatusEvents` | **append-only** history of every status (`Source` queue/send/ack/reconcile, `GatewayAck`, `Applied`) | index `(TenantId, MessageId, RecordedAtUtc)` |
| `WebhookEvents` | raw payload (`longtext`, purged after `ExpiresAtUtc` but the row stays), `ExternalEventId` (WAHA event id, or `sha256:<body hash>` when there is none), `EventType`, `Status`, `Note`, `CorrelationId`, `SourceIp` | **unique `(ChannelAccountId, ExternalEventId)`**, index `ExpiresAtUtc` |
| `MessageTemplates` | tenant-managed text with `{{placeholders}}`, `Category`, `IsActive` | unique `(TenantId, Name)` |
| `OutboxMessages` | transactional outbox: `Type` (= routing key), `PayloadJson`, `CorrelationId`, `CausationId`, `Status`, `Attempts`, `NextAttemptAtUtc` (also the dispatcher lease), `PublishedAtUtc`, `LastError` | index `(Status, NextAttemptAtUtc)` |
| `ProcessedMessages` | consumer idempotency | **unique `(TenantId, ConsumerName, MessageId)`** |
| `ChannelAccountNotifications` | auto-pause / disconnect / restriction notices shown on the account page | index `(TenantId, ChannelAccountId, CreatedAtUtc)` |

Not created on purpose: `InternalNote` (Phase 8 hand-off UI needs it, nothing in Phase 5 does), Hangfire tables (Hangfire owns them), RabbitMQ topology (declared by the broker layer).

## 5. Campaigns (Phase 6)
Campaign (T: Type, Status, ScheduleAtUtc, TemplateId, AudienceDefinition JSON, Limits) · CampaignRecipient (T: CampaignId, ContactId, Status, EligibilitySnapshot JSON, MessageId?; **unique `(CampaignId, ContactId)`** prevents duplicates) · CampaignExecutionLog (T) · CampaignStats read model (aggregations from events).

## 6. AI & Knowledge (Phases 7–8)
AIAgent (T: name, instructions, vertical config, enabled tools, model settings) · KnowledgeSource / KnowledgeDocument / DocumentVersion / DocumentChunk / EmbeddingRecord (T; vectors in Qdrant with `tenant_id` payload, record keeps vector id + model + dimension) · RetrievalLog (T: query, chunk ids, scores) · ConversationMemory/Summary (T) · ToolExecution (T: tool, args, result, status, approval) · ApprovalRequest (T).

## 7. Workflows & Follow-ups (Phase 9)
Workflow · WorkflowVersion (immutable once active) · WorkflowTrigger · WorkflowStep · WorkflowExecution (T: unique `(WorkflowId, TriggerEventId, EntityId)` for idempotency) · WorkflowExecutionLog · FollowUpTask (T: due, assignee, status).

## 8. Sales conversion (Phase 10)
DemoSlot / DemoAvailabilityRule · DemoBooking (T: unique `(TenantId, RepId, StartUtc)` prevents double-booking; slot must be within availability) · Trial (T) · Customer (T: LeadId, converted history) · CommercialAgreement · PaymentRequest · Payment (VerificationStatus; set only by IPaymentVerifier / authorized staff) · CustomerSubscription (activated only with verified Payment).

## 9. Analytics (Phase 11)
No separate warehouse initially: indexed event tables + aggregation queries + daily rollup tables (`DailyLeadMetric`, `DailyCampaignMetric`) refreshed by Hangfire; every metric labelled verified / calculated / estimated.

## 10. Platform / Billing (Phase 12–13)
Plan (G, configurable, no hardcoded price) · PlanEntitlement · TenantSubscription · UsageRecord (T) · Invoice · PaymentHistory · BusinessCategory, CatalogItem, QualificationQuestion, CustomField(+Value), ScoringRule (T, config-driven).

## Index/constraint highlights
- All tenant tables: `(TenantId, …)` leading composite indexes.
- Idempotency: Message external id, WebhookEvent external event id, CampaignRecipient, DiscoveryResult external id, WorkflowExecution, ProcessedMessage.
- Dedup: Lead normalized keys, Contact phone, SuppressionEntry value.
- Time-series: `(TenantId, CreatedAtUtc)` on events/messages/activities for date-filtered reports.

## Open items for Phase 1
Guid storage format; Pomelo provider version for .NET 10; Hangfire MySQL storage package (decided in Phase 4: see ARCHITECTURE.md); migrations workflow (design-time factory, applied by a one-shot migrator container, not at web startup in production).

## Phase 6 — campaign tables (as built, migration `Phase6Campaigns`)
`Campaigns` (status, schedule UTC + zone, template/account/parent FKs, audience JSON + version, limits, approval fields, lease, `Version` concurrency token), `CampaignAudienceSnapshots`, `CampaignRecipients` (**unique `(CampaignId, ContactId)`**, `(TenantId, CampaignId, Status, Sequence)`, `(TenantId, ContactId, QueuedAtUtc)` for the cool-down, `EligibilityJson` with selection and send decisions, `Round`), `CampaignExecutionLogs` (append-only), `CampaignStatsRollups` (unique per campaign), `CampaignConversions` (unique `(TenantId, RecipientId, Kind, ReferenceId)`), `CampaignUsageEntries` (append-only, unique `(TenantId, RecipientId, Round)`), `CampaignTenantSettings` (unique per tenant). `Messages` gained nullable `CampaignId` (FK) and `CampaignRecipientId` (indexed, no FK: it would be circular with `CampaignRecipients.MessageId`). Every table has `TenantId` (FK, index, query filter) and every FK is checked for same-tenant by the SaveChanges guard.


## Phase 7 tables (migration `Phase7AiKnowledge`, all tenant-owned with a mandatory FK to Tenants and `(TenantId, …)` indexes)
| Table | Notes |
|---|---|
| `KnowledgeSources` | upload / text / website; website URL, last sync + error |
| `KnowledgeDocuments` | title, kind, `CurrentVersionId`, `LatestVersionNumber`, index status, `IsEnabled`; index `(TenantId, SourceId, SourceUrl)` |
| `DocumentVersions` | immutable `Content` (longtext), hash, status, `IndexingStartedAtUtc`; **unique (DocumentId, VersionNumber)** |
| `DocumentChunks` | only the current version's passages exist; **unique (VersionId, Ordinal)** |
| `EmbeddingRecords` | L2-normalised float32 `Vector` (longblob), model, dimensions, store name; **unique (ChunkId, Model)** — the MySQL store searches these rows; Qdrant holds a second copy |
| `RetrievalLogs` | append-only; query hash + length (query text only for staff-typed tester questions), result ids/scores, outcome, latency |
| `AiTenantSettings` | one row per tenant (**unique TenantId**); keys stored as `enc:v1:` ciphertext + last-4 hint; models, thresholds, limits, tone, escalation keywords; concurrency token |
| `AiUsageEntries` | append-only ledger; tokens incl. cache read/write, estimated cost `decimal(18,8)` (see OWNER_DECISIONS 9), success/error code |
| `ConversationSummaries` | rolling summary; **unique (TenantId, ConversationId)** |
| `AiInteractions` | the suggestion/escalation of one AI run; **unique (TenantId, TriggerMessageId)** = one run per inbound message, FK to Conversation and Message |
| `AiToolExecutions` | append-only tool log (allowed/denied/failed + redacted arguments) |
Deleting a document removes versions, passages, embedding rows and vectors (vectors first; if the store is down nothing is deleted and the user is told). Delete behaviours are Restrict; the application deletes in dependency order inside a transaction. Applied and checked on an empty MySQL 8 database and on a database at the Phase 6 schema holding a tenant, roles, a lead and a contact (`AiMigrationTests`).

## Phase 8: sales agent, catalog, approvals, handoff (migration `Phase8SalesAgent`, additive)
New tenant-owned tables (all indexed `(TenantId, …)`, tenant FK, global query filter, save guard):
| Table | Notes |
|---|---|
| `SalesAgents` | name, instructions (text), `EnabledToolsJson`, optional model/min-confidence override, `IsDefault`, `IsSample`, `AutoReplyEnabled` (default false), off-topic reply, concurrency token |
| `AgentQualificationQuestions` | key/question/type (Text, Number, Choice + choices)/required/order; **unique (TenantId, AgentId, Key)** |
| `CatalogProducts` | name, summary, features (one per line), demo/trial process, `IsConfirmed`, `IsSample`, concurrency token |
| `PricingPlans` | `Price decimal(18,2)` **nullable** (no price set), currency, period, details, `MaxDiscountPercent`, `IsConfirmed` (needs a price), FK to product |
| `ApprovalRequests` | tool, reason, summary, the exact validated `InputJson`, status (Pending/Approved/Rejected/Expired), execution state (None/Executed/NotAvailable/Failed), expiry, decider; **unique (TenantId, DedupeKey)** |
| `ConversationNotes` | append-only internal notes (user or AI) |
| `HandoffEvents` | append-only: takeover, pause/resume, escalation (reason), auto-takeover, assignment, status, opt-out; actor kind, from/to state |
| `AgentTenantSettings` | **unique TenantId**; `AiRepliesAllowed` (false), automatic replies per conversation per day, trials per lead, approval lifetime, open follow-ups per lead |
| `LeadQualifications` | answers JSON + fit per lead; **unique (TenantId, LeadId)** |
| `FollowUps` | internal task (lead, due, note, status, created by user/AI); index `(TenantId, Status, DueAtUtc)` |
Changed tables: `Conversations` + `EscalationReason`, `EscalatedAtUtc`, `AiModeChangedAtUtc`, **`AiEpoch`** (incremented on every change of AI mode: the AI remembers the value it started with, so a takeover/pause — even pause+resume — during a generation cancels the reply); `Messages` + `IsAiGenerated`, `AiInteractionId`, `AiEpoch`; `AiInteractions` + `AgentId`, `AutoSend` (NotAttempted/Sent/Blocked), `AutoSendNote`, `SentMessageId`; **`AiToolExecutions` is the ToolExecution record**: `InteractionId` became nullable (Phase 7 rows keep it) and it gained `TriggerMessageId`, `ConversationId`, `LeadId`, `AgentId`, `InputJson`, `OutputJson`, `ApprovalRequestId`, `ExecutedByUserId`, `DedupeKey` (**unique (TenantId, DedupeKey)**: a redelivered event replays the stored result), `DurationMs`; the outcome set grew (ApprovalRequired, NotAvailable, Invalid). Still append-only: an approved action is a new row pointing at the same approval.
Applied and checked on an empty MySQL 8 database and on a database at the Phase 7 schema holding a tenant, roles, a lead, a contact and an old tool-log row (`AgentMigrationTests`).

## Phase 9 (migration `Phase9Workflows`: 8 new tables, no change to existing tables)
| Table | Notes |
|---|---|
| `Workflows` | name, status (Draft/Active/Inactive), `ActiveVersionId`, `LatestVersionNumber`, kill switch + reason, soft delete, `Version` concurrency token; index `(TenantId, IsDeleted, Status)` |
| `WorkflowVersions` | **unique (TenantId, WorkflowId, Number)**; state Draft/Active/Retired; re-entry policy, cool-down hours, frequency cap |
| `WorkflowTriggers` | event type, condition JSON, config JSON, `IsSealed`; **unique (WorkflowVersionId, EventType)**; index `(TenantId, EventType)` |
| `WorkflowSteps` | key, position, type, config JSON, run-if JSON, failure policy, max retries, `IsSealed`; **unique (WorkflowVersionId, Key)** |
| `WorkflowExecutions` | **unique (WorkflowVersionId, TriggerEventId, EntityId)** (idempotency); **unique (WorkflowId, EntityId, ActiveSlot)** (exclusive live run; NULL once ended); status, current step, state JSON, next run, lease, `Version` token; FK to the lead; index `(TenantId, Status, NextRunAtUtc)` |
| `WorkflowExecutionLogs` | append-only per-step log (outcome, attempt, message, effect reference) |
| `WorkflowTenantSettings` | **unique TenantId**; kill switch, max concurrent runs (20), monthly runs (5000), daily messages (200) |
| `WorkflowNotifications` | **unique (WorkflowExecutionId, StepKey, UserId)** so a replayed step never notifies twice |
Every table has a tenant FK (restrict), `TenantId` index and the global query filter. Applied and checked on an empty MySQL 8 database and on a database at the Phase 8 schema holding a tenant, roles and a lead (`WorkflowMigrationTests`).
