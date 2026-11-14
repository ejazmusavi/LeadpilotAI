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

## 2. CRM (Phase 3)
| Entity | Scope | Notes |
|---|---|---|
| Lead | T | Organization-level (school/company). Name, Category, Country/Province/City/District/Area, Address, Website, Status (pipeline enum), OwnerUserId, Score, Confidence, IsArchived. Dedup keys: normalized name+city, domain, phone → indexes `(TenantId, NormalizedKey)` |
| Contact | T | LeadId FK, Name, Role, Phone (E.164), Email, PhoneVerifiedAt, DataOrigin (Public/Provided/Verified). Unique `(TenantId, LeadId, Phone)` |
| LeadSource | T | Type, ExternalId, Url, RetrievedAtUtc, attribution; unique `(TenantId, Type, ExternalId)` |
| LeadActivity | T | Type, Payload, ActorId |
| LeadNote | T | Body, AuthorId |
| LeadTag / LeadTagMap | T | (TenantId, Name) unique |
| LeadScore (history) | T | Score, Breakdown(JSON), RuleVersion |
| Pipeline / PipelineStage | T | Configurable per tenant; seeded with the 14 default stages |
| Opportunity / OpportunityActivity | T | LeadId, StageId, Value decimal(18,2), ExpectedCloseDate, OwnerId |
| ConsentRecord | T | ContactId, Channel, Purpose (Marketing/Service), Status (Granted/Revoked), Source, EvidenceRef, OccurredAtUtc. **Append-only** |
| SuppressionEntry | T | Channel, Value (phone/email hash+raw), Reason (OptOut/Invalid/Blocked/TenantExcluded/Legal), CreatedAtUtc; unique `(TenantId, Channel, ValueNormalized)` |
| ImportJob / ImportRowError | T | File ref, counts, per-row error report |

Relationships: Lead 1—* Contact; Lead 1—* Opportunity; Contact 1—* Conversation (Messaging); Contact 1—* ConsentRecord. Customers are a separate entity (Phase 10), linked to Lead for history.

## 3. Discovery (Phase 4)
DiscoveryJob (T: Params JSON, Status, Progress, Attempts, RequestedBy, UsageCost) · DiscoveryResult (T: raw normalized payload, SourceId, Confidence, ReviewStatus Pending/Approved/Rejected, DuplicateOfLeadId?, RetrievedAtUtc, ExpiresAtUtc for provider caching limits) · DiscoveryConnectorConfig (T: provider, encrypted creds, quota) · UsageRecord shared with billing. Unique `(TenantId, ConnectorType, ExternalId)` makes retries idempotent.

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
Guid storage format; Pomelo provider version for .NET 10; Hangfire MySQL storage package; migrations workflow (design-time factory, applied by a one-shot migrator container, not at web startup in production).
