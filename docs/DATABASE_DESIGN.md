# Database Design (MySQL 8, EF Core)

## Conventions
Guid PKs (stored `char(36)` / `BINARY(16)` — decide in Phase 1 after benchmarking; use ordered Guids to limit index fragmentation) · `TenantId` on every tenant-owned table · UTC `datetime(6)` · money `decimal(18,2)` · phones E.164 · soft delete (`IsDeleted`, `DeletedAtUtc`) where history matters · audit fields (`CreatedAtUtc/By`, `UpdatedAtUtc/By`) · `RowVersion` for optimistic concurrency on mutable aggregates · composite indexes lead with `TenantId` · explicit FK delete behaviour (Restrict by default; Cascade only for owned children) · all changes via migrations.

Legend — **T** = tenant-owned (has TenantId), **G** = global.

## 1. Tenancy & Identity (Phase 2)
| Entity | Scope | Key fields / constraints |
|---|---|---|
| Tenant | G | Name, Slug (unique), Status (Active/Suspended/…), Industry, Timezone, Locale |
| TenantSetting | T | (TenantId, Key) unique, Value, IsSecret |
| AspNetUsers (Identity) | G | Email unique |
| UserProfile | G | UserId (1:1), DisplayName, Phone |
| UserTenant | T | (UserId, TenantId) unique, IsDefault, Status |
| Role | T | (TenantId, Name) unique; system roles seeded per tenant |
| Permission | G | Code unique (e.g. `Leads.View`) |
| RolePermission | T | (RoleId, PermissionId) unique |
| UserRole | T | (UserId, RoleId) unique within tenant |
| TenantInvitation | T | Email, RoleId, TokenHash, ExpiresAtUtc, Status |
| AuditLog | T (nullable for platform events) | Actor, Action, EntityType/Id, Summary, Ip, CorrelationId; index (TenantId, CreatedAtUtc) |

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
