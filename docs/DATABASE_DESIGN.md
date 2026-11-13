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

## 4. Messaging (Phase 5)
Channel (G catalog: WhatsApp…) · ChannelAccount (T: gateway account id **unique globally** for webhook→tenant resolution, Status) · ChannelCredential (T: encrypted) · Conversation (T: ContactId, ChannelAccountId, Status Open/Pending/Closed, AiMode Active/Paused/HumanControl, AssignedUserId, last message timestamps) · Message (T: ConversationId, Direction, ExternalMessageId, Body, Status, CorrelationId, IdempotencyKey; unique `(TenantId, ChannelAccountId, ExternalMessageId)` and unique outbound idempotency key) · MessageAttachment · MessageStatusEvent (Sent/Delivered/Read/Failed) · WebhookEvent (T: payload, `(ChannelAccountId, ExternalEventId)` unique, ProcessedAt) · MessageTemplate (T) · OutboxMessage (T) · ProcessedMessage (consumer idempotency) · ConversationAssignment (history), InternalNote.

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
