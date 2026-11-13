# Multi-Tenancy

## Model
Shared MySQL database, shared schema. Every tenant-owned row has `TenantId` (Guid, NOT NULL, indexed, first column of composite indexes). Design keeps a seam (`ITenantConnectionResolver`) so enterprise tenants can later get a dedicated database.

## Tenant resolution
| Entry point | Source of tenant | Notes |
|---|---|---|
| MVC | Authenticated user's active tenant (claim `tid`) validated against `UserTenant` membership | Platform users switch tenant via audited action |
| API | JWT `tid` claim, validated against membership on every request | Client-sent TenantId in body/route/header is **never trusted**; if present it must equal the authorized tenant or the request is rejected |
| Webhooks | Resolved from the connected `ChannelAccount` (unique gateway account id → tenant), after signature/secret validation | Unknown account → 404/ignored + logged |
| Hangfire jobs | Job argument `TenantId` + wrapper filter that creates a tenant scope | Jobs without tenant must be explicitly marked `PlatformJob` |
| RabbitMQ consumers | `TenantId` message header → consumer sets scope before any DB/AI/cache access | Missing header → dead-letter |

`ITenantContext { Guid? TenantId; bool IsPlatformScope; }` is the only source of tenant identity inside Application/Infrastructure. Middleware/job filters/consumer base class populate it.

## Enforcement layers (defence in depth)
1. **Authorization**: permission check + resource ownership check in each use case (`entity.TenantId == tenantContext.TenantId`).
2. **EF Core global query filters** on all `ITenantOwned` entities (`e.TenantId == _tenant.TenantId`). Platform scope must be explicit; `IgnoreQueryFilters()` is only allowed in a small allow-listed set of services (checked by an architecture test).
3. **SaveChanges interceptor**: stamps `TenantId` on insert from context; rejects inserts/updates where `TenantId` differs from context (except platform scope).
4. **Database**: composite unique indexes include `TenantId`; foreign keys between tenant-owned tables use composite keys or are validated to prevent cross-tenant references (e.g. a Contact cannot point at another tenant's Lead — enforced by an interceptor check and tests).
5. **Cache**: key format `t:{tenantId}:{area}:{key}`; shared helper only, no raw keys.
6. **Files**: path `tenants/{tenantId}/…`, accessed only through `ITenantFileStore`; downloads authorize ownership, never accept raw paths.
7. **Vector store**: one Qdrant collection per embedding model with mandatory `tenant_id` payload filter applied inside `IVectorStore` (callers cannot omit it); optionally collection-per-tenant for enterprise.
8. **AI**: tool executions receive tenant from context, never from LLM arguments; retrieval filters by tenant before similarity search; prompts/logs never include other tenants' data.
9. **Audit**: `AuditLog` is tenant-scoped; platform actions record both actor and target tenant.
10. **Logging**: all log events enriched with TenantId and CorrelationId.

## Global entities (no TenantId)
Tenant, Permission (catalog), platform Plans (Ph12), system-level templates. Everything else is tenant-owned. Identity `AspNetUsers` is global (a user can belong to several tenants via `UserTenant`); roles are assigned per tenant (`UserRole.TenantId`).

## Required isolation tests (mapped to README critical scenarios)
- MVC and API: Tenant A user requests Tenant B lead by id → 404/403; list endpoints never include B rows; spoofed TenantId in body/header rejected.
- Background jobs: job enqueued for tenant A cannot read/modify tenant B; job without tenant context fails closed.
- RabbitMQ consumer: message missing/forged `TenantId` is dead-lettered.
- AI retrieval: queries from tenant A never return tenant B chunks, including with adversarial prompts and with identical document text in both tenants.
- Cache and file store key isolation.
- Cross-tenant FK insert attempts fail.

## Phase 2 implementation status (what is actually built)
| Design item | Implementation | Verified by |
|---|---|---|
| `ITenantContext` | `Application/Abstractions/Tenancy.cs`; scoped `TenantContext` (Infrastructure). Empty by default; `RequireTenantId()` throws `TenantContextMissingException`. | unit + integration |
| MVC resolution | `tid` claim in the auth cookie, **re-validated against `UserTenant` on every request** by `TenantResolutionMiddleware` (after authentication, before authorization). Lost membership signs the cookie out; suspended tenant → 403; no workspace → `/Account/NoWorkspace`. The claim survives the periodic security-stamp refresh. | integration (stamp validated on every request in tests; mutation-checked) |
| API resolution | JWT `tid` claim, same middleware, same validation on every request. Header `X-Tenant-Id` must equal it (else 400). | integration |
| Spoofing | `RejectSpoofedTenantFilter` (global, MVC + API) rejects `tenantId` in route, query, form or bound body model unless it equals the authorized tenant. Tenant-scoped request records implement `ITenantScopedRequest`. | integration (header, query, route, body, form) |
| Query filters | Generated for every `ITenantOwned` entity in `AppDbContext`; no tenant → no rows. | integration + architecture test |
| SaveChanges guard | `TenantSaveChangesInterceptor`: stamps `TenantId`, rejects writes outside the active tenant, `TenantId` immutable, rejects FKs pointing at another tenant's rows (tracked or in DB), `AuditLog` append-only. Platform scope relaxes *which tenant*, never FK consistency. | integration |
| Explicit ownership checks | Every service query repeats `TenantId == context tenant` on top of the filter; foreign ids (roles, users, invitations) are "not found" (404). | integration (MVC + API) |
| Platform scope | `ITenantContextAccessor.BeginPlatformScope()`; used only for provisioning, seeding, invitation acceptance, audited cross-tenant audit writes. | code review + allow-list test |
| `IgnoreQueryFilters` | Allow-list of 8 files, enforced by an architecture test (and a stale-entry check). | architecture |
| Jobs / consumers | `ITenantScopeRunner.RunAsync(tenantId, ...)`: empty id, unknown or suspended tenant throw; `RunPlatformJobAsync` is the explicit unscoped path. (Hangfire/RabbitMQ wiring arrives with Phases 5-6 and must use it.) | integration + unit |
| Cache / files | `TenantCacheKeys.For(...)` (`t:{tenantId}:{area}:{key}`) and `TenantPaths.For(...)` (`tenants/{tenantId}/...`, traversal rejected). Helpers only: no cache or file store exists yet. | unit |
| Audit | `AuditLog` per tenant; platform actions record `ActorTenantId` + target `TenantId`. Tenant switch (cookie, JWT switch endpoint, and direct platform-access login) is always audited in the *target* tenant. | integration |
| Secrets | Secret tenant settings: masked in every response, encrypted at rest (`enc:v1:` Data Protection), never in audit text. | integration |

### Platform users and tenant switching
A **platform tenant** (`Tenant.IsPlatform`, slug `platform`, never suspendable) holds operator staff (roles *Platform Owner*, *SaaS Administrator*). A user whose active platform role carries `Platform.SwitchTenant` may switch into any **active** tenant without being a member; inside it they carry the permissions of their platform roles (`PermissionEvaluator`). Ordinary users may only switch among their own memberships; anything else answers 404 (same as "does not exist"). Platform users cannot enter a suspended tenant, but can still manage it from the tenant list.

### Known limits (documented, not hidden)
- Access JWTs are valid until expiry (15 min default) for signature purposes, but every request re-checks user status, membership and tenant status, so suspension/removal is immediate.
- Refresh tokens are not purged yet (no job host before Phase 6).
- Rate limiting for the API login is per IP only.

## Phase 3 additions (CRM)
| Item | Implementation | Verified by |
|---|---|---|
| 20 new tenant-owned tables | `ITenantOwned` → tenant FK, index and query filter generated automatically; architecture test requires every entity in `Domain.Crm` to be tenant-owned | architecture + `Every_crm_table_returns_nothing_without_a_tenant_and_only_own_rows_with_one` |
| Cross-tenant FKs between CRM rows | Same guard as Phase 2 (all 19 relationships); principals tracked in the same unit of work are compared directly, others are looked up once per save and memoised | `Cross_tenant_foreign_keys_are_rejected_for_every_new_entity` (mutation-checked) |
| Immutable history | `IAppendOnly` (AuditLog, ConsentRecord, LeadActivity, LeadScore, OpportunityActivity): any update/delete is rejected inside SaveChanges | `Consent_rows_cannot_be_changed_or_deleted_even_by_code`, architecture tests |
| Service-level ownership | every service query repeats `TenantId == context tenant`; foreign ids → 404, foreign references inside a body → 400, bulk ids → "Lead not found" | `CrmTenantIsolationApiTests` (list, 404 on every read, 404 on every write with B verified untouched, references, spoofing), `CrmWebTests` (same through MVC) |
| Spoofed tenant id | all request records inherit `TenantScopedRequest`; the Phase 2 filter rejects body/query/route/form/header values that name another tenant | `A_spoofed_tenant_id_is_rejected_everywhere_a_client_could_send_one` |
| Per-tenant uniqueness | tags, categories, statuses, pipelines, suppression values, versions are unique **per tenant** (the same value may exist in two tenants) | `Unique_constraints_are_scoped_per_tenant` |
| Eligible audience | computed per tenant in SQL; another tenant's contacts and suppressions are never joined | `Tenant_A_cannot_list_or_find_tenant_B_data` |
| Files | `LocalTenantFileStore` resolves only keys inside `tenants/{activeTenantId}/…` and rejects anything else | code + isolation tests (imports of B are 404 for A) |
| Provisioning | defaults are inserted in an explicit platform scope for the new/existing tenant; `PlatformSeeder` (already on the `IgnoreQueryFilters` allow-list) is the only new reader across tenants | `A_phase2_database_upgrades_and_existing_tenants_receive_crm_defaults_and_permissions` |

The `IgnoreQueryFilters` allow-list did not grow in Phase 3.


## Phase 4 — discovery under the tenancy rules
- Entities `DiscoveryJob`, `DiscoveryResult`, `DiscoveryConnectorConfig`, `DiscoveryUsageEntry`, `DiscoveryCategoryMapping`, `DiscoveryLocationPreset` are `ITenantOwned`: global query filter, TenantId stamped/immutable by the SaveChanges guard, cross-tenant FK rejection (a result pointing at another tenant's job, a job using another tenant's category, a mapping for another tenant's category all throw `CrossTenantViolationException`), usage ledger append-only.
- Services use the Phase 3 `CrmContext` (explicit `TenantId ==` predicate on every query + 404 for foreign ids). No file under `Infrastructure/Discovery` may use `IgnoreQueryFilters` (architecture test); the retention job iterates tenants and runs each inside `ITenantScopeRunner`.
- **Credentials:** the connector config row is read with the active tenant's predicate and decrypted just before the call; the key is passed to the connector in the request object. There is no ambient or shared key: tenant B without a key gets "not configured", never tenant A's key (integration-tested with a keyed test connector recording which key it received).
- **Background jobs:** the Hangfire payload is `(tenantId, jobId)`; `ITenantScopeRunner` fails closed on an empty/unknown/suspended tenant; a payload pairing tenant A with tenant B's job id finds nothing in A's scope and does nothing (tested with a real Hangfire server).
- Monthly limits and usage are per tenant and per connector; the dashboard `/hangfire` is platform-only.
- Test coverage: `DiscoverySecurityTests` (every endpoint 401/403, A↔B 404 for read/approve/reject/cancel/retry/rerun/delete, bulk, lists, spoofed `TenantId` in body/query/header, credentials, runner, DB guard on each entity).


## Phase 5 additions (messaging)
| Item | Implementation | Verified by |
|---|---|---|
| 13 new tenant-owned tables | `ITenantOwned` → FK, index, query filter and SaveChanges guard generated automatically; architecture test requires every entity in `Domain.Messaging` to be tenant-owned | `MessagingArchitectureTests`, `Every_messaging_table_returns_nothing_without_a_tenant_and_only_own_rows_with_one` |
| Cross-tenant FKs | the Phase 2 guard rejects all 11 new relationships (Conversation→Account/Contact, Message→Conversation/Account/Template, Attachment/StatusEvent→Message, WebhookEvent/Credential/Notification→Account, Assignment→Conversation, Account→Channel) | `The_database_guard_rejects_a_cross_tenant_reference_for_every_messaging_entity` |
| Webhook → tenant | secret URL key + per-account HMAC + signed session name; the lookup is the only new cross-tenant read (`MessagingDirectory`, on the `IgnoreQueryFilters` allow-list, together with the outbox/sweep scans); everything after it runs in the resolved tenant's scope | `A_webhook_for_one_tenants_account_can_never_write_into_another_tenant`, unknown-account, suspended-tenant tests |
| Global uniqueness | `ChannelAccounts.GatewayAccountId` and `WebhookKey` are unique across all tenants; every other uniqueness is per tenant (same template name, idempotency key or gateway message id may exist in two tenants) | `Uniqueness_is_per_tenant_except_for_the_global_session_and_webhook_keys…`, migration test |
| Credentials | ciphertext bound to `tenant:account`; a swapped ciphertext is not opened and the gateway is not called | `One_tenants_stored_credentials_are_never_opened_or_used_for_another_tenant` |
| Hangfire jobs | arguments are `(tenantId, messageId)`; jobs run through `ITenantScopeRunner` (fails closed for empty/unknown/suspended tenants); `AutomaticRetry` is 0 for every messaging job | architecture test, send-runner tests |
| RabbitMQ consumers | `TenantId` header parsed first; missing/forged/mismatching/unknown/suspended ⇒ dead-letter; data access only inside the tenant scope with explicit tenant predicates | `MessageProcessorTests` (unit), `Consumers_dead_letter_forged_or_missing_tenant_ids…`, `A_message_without_a_tenant_header_or_with_a_forged_one_lands_in_the_dead_letter_queue…` (real broker) |
| Outbox dispatcher | scans for tenants with due rows, then publishes **per tenant inside that tenant's scope**; suspended tenants' events wait | `A_suspended_tenants_events_wait_in_the_outbox…` |
| Dead-letter queues | the queues are shared by all tenants, so **only platform operators** (`Tenants.Manage`) can see them, and only headers (tenant id, type, reason, attempts), never payloads | permission matrix tests, `/DeadLetters` page tests |
| API/MVC | every service query repeats `TenantId == context tenant`; foreign ids answer 404 for every conversation, message, template, account and webhook-event endpoint, with the data and the gateway verified untouched | `MessagingSecurityTests`, `MessagingWebTests.The_mvc_pages_answer_404…` |
| Settings | `messaging.optout_keywords`, `messaging.auto_create_leads`, `messaging.service_window_hours` are ordinary per-tenant settings (Settings page) | opt-out / auto-create tests |

## Phase 6 additions (campaigns)
All eight new tables are `ITenantOwned` (architecture test); services use the tenant predicate (foreign ids are 404) on top of the global filter. Jobs carry ids only and run in `ITenantScopeRunner` (fails closed for unknown/empty tenants); a campaign or message id of tenant A executed in B's scope finds nothing (tested). The `campaigns.events` consumer reads the verified `TenantId` header first; a forged tenant with another tenant's campaign id is dead-lettered (`campaign_not_found_for_tenant`). A campaign cannot reference another tenant's template, account, contact or parent (service validation + DB guard). The kill switch, approval policy and cool-down are per tenant. `CampaignDirectory` is the only new `IgnoreQueryFilters` user (tenant ids only). Tests: API, MVC, jobs, consumer, DB guard for each entity, spoofed `tenantId` on every body/query.


## Phase 7 additions
- All 11 AI entities are `ITenantOwned` (global filter + save guard); cross-tenant references (a chunk of tenant A pointing at tenant B's version, an interaction for B's conversation, a tool log for B's interaction, spoofed tenant ids) are refused; `RetrievalLog`, `AiUsageEntry` and `AiToolExecution` are append-only.
- The vector store takes the tenant from `ITenantContext` itself: no method has a tenant parameter, so callers can neither omit nor change it. The retriever re-checks every hit against the database. A test replaces the store with one that returns foreign, disabled and outdated chunks: none is returned.
- Jobs (`KnowledgeJob`) and the AI consumer run inside `ITenantScopeRunner` / the verified `TenantId` header; ids of another tenant are no-ops or dead-letters. The only cross-tenant query is `KnowledgeDirectory` (ids only, allow-listed).
- Per-tenant circuit breakers: one workspace's bad key or exhausted quota never opens the breaker of another.
