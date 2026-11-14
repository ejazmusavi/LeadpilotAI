# Architecture

Clean Architecture + modular monolith. .NET 10 / C# 14, MySQL 8, EF Core, Hangfire, RabbitMQ, Redis, Qdrant, Docker + Nginx.

## Solution structure
```
LeadPilotAI/
  src/
    LeadPilot.Domain/          entities, value objects, domain events, enums, domain exceptions (no dependencies)
    LeadPilot.Application/     use cases, DTOs, validation, interfaces (ports), authorization requirements
    LeadPilot.Infrastructure/  EF Core/MySQL, Redis, RabbitMQ, Hangfire jobs, AI/WhatsApp/Discovery adapters, storage
    LeadPilot.Web/             ASP.NET Core MVC dashboard (Razor + Bootstrap 5)
    LeadPilot.Api/             REST API, webhooks, mobile endpoints, Swagger
    LeadPilot.Worker/          background host: Hangfire server + RabbitMQ consumers
    LeadPilot.Shared/          cross-cutting primitives only (Result, correlation id, constants)
  tests/
    LeadPilot.UnitTests/  LeadPilot.IntegrationTests/  LeadPilot.ArchitectureTests/
  docker/  docs/  scripts/  LeadPilotAI.sln
```
No refinements to the suggested structure are needed.

## Dependency rules (enforced by ArchitectureTests)
```
Domain        -> (nothing; Shared allowed)
Application   -> Domain, Shared
Infrastructure-> Application, Domain, Shared
Web / Api / Worker -> Application, Infrastructure (composition root only), Shared
```
Domain must not reference EF Core, ASP.NET, or any SDK. Web/Api never touch `DbContext` directly — they call Application use cases.

## Module boundaries (inside the monolith)
Each module owns its entities, use cases and persistence configuration, organised as folders/namespaces per layer (e.g. `Application/Leads`, `Infrastructure/Persistence/Leads`). Cross-module calls go through Application interfaces or domain/integration events — never another module's `DbSet`.

| Module | Owns | Publishes events |
|---|---|---|
| Tenancy & Identity | Tenant, UserProfile, Role, Permission, Invitation, AuditLog | TenantSuspended |
| CRM | Lead, Contact, Pipeline, Opportunity, Consent, Suppression, Tags, Notes, Activity | LeadCreated, LeadStatusChanged, ContactEligible |
| Discovery | DiscoveryJob, DiscoveryResult, SourceConnector config | LeadDiscovered, LeadEnriched |
| Messaging | Channel, ChannelAccount, Conversation, Message, WebhookEvent, Template | MessageReceived, MessageStatusChanged |
| Campaigns | Campaign, Audience, Recipient, ExecutionLog | CampaignRecipientSent |
| AI & Knowledge | Agent, KnowledgeDocument/Chunk, RetrievalLog, ToolExecution | AiReplyReady, HandoffRequested |
| Workflows | Workflow*, Execution*, Followup tasks | — |
| Sales Conversion | Demo, Trial, Customer, PaymentRequest | DemoBooked, TrialCreated, PaymentVerified |
| Analytics | read models / aggregation queries | — |
| Billing (Ph12) | Plan, Subscription, UsageRecord, Invoice | — |

## Key ports (Application interfaces)
`ITenantContext`, `ICurrentUser`, `IPermissionEvaluator`, `IWhatsAppGateway`, `IAIProvider`, `IEmbeddingProvider`, `IVectorStore`, `IKnowledgeRetriever`, `IConversationMemory`, `IAIOrchestrator`, `IDiscoveryConnector`, `IContactEligibilityService`, `IPaymentVerifier`, `IClock`, `IEventPublisher` (writes to outbox), `ISecretProtector`.

## Process model
- **Web** (MVC) and **Api** are stateless; scale horizontally.
- **Worker** runs Hangfire server and RabbitMQ consumers. Web/Api only *enqueue*.
- Hangfire dashboard is exposed only to Platform roles behind auth, via Web.

## Background processing contract
| Concern | Tool |
|---|---|
| Scheduled/recurring jobs, delays, cleanup, scheduled reports, campaign start time, follow-up timers | Hangfire (MySQL storage) |
| Durable async events: inbound message, AI processing, enrichment, campaign delivery, integration events | RabbitMQ |
| DB change + publish atomicity | Transactional **outbox** table, dispatcher job publishes to RabbitMQ |
| Consumer idempotency | `ProcessedMessage(ConsumerName, MessageId)` unique key; side effects use idempotency keys |
| Retries / DLQ | Exponential backoff with bounded retries, then per-queue dead-letter queue + alert |
| Context propagation | Message headers carry `TenantId`, `CorrelationId`, `CausationId`; consumers establish tenant scope before any data access |

## Cross-cutting
Structured logging (Serilog, JSON, with TenantId/CorrelationId enrichment), global exception handling (ProblemDetails), FluentValidation, API envelope/ProblemDetails conventions, health checks (MySQL, Redis, RabbitMQ, Qdrant), Swagger/OpenAPI, options-pattern configuration, secrets via env/secret store (never committed).

## Compliance in the architecture
`IContactEligibilityService` is the single gate for any outbound marketing message. It is called (a) when building campaign audiences and (b) immediately before every send in the worker. It evaluates consent, suppression, contact validity, tenant messaging policy and channel rules. Lead score is never an input that can override it.

## Technology notes
- Package versions will be pinned in Phase 1 after verifying NuGet compatibility with .NET 10 (Pomelo.EntityFrameworkCore.MySql, Hangfire.MySqlStorage or Hangfire.Storage.MySql, RabbitMQ.Client, StackExchange.Redis, FluentValidation, Serilog, xUnit, Testcontainers, NetArchTest).
- Integration tests use Testcontainers (MySQL, RabbitMQ, Redis, Qdrant) so tenant-isolation tests run against real engines.

## Phase 1 technology decisions (verified)
| Decision | Reason |
|---|---|
| **MySQL provider: Oracle `MySql.EntityFrameworkCore` 10.0.12** with EF Core 10.0.15 | Pomelo's latest stable (9.0.0) targets EF Core 9 and has no EF Core 10 release on NuGet. Oracle's provider is the only stable EF Core 10 provider for MySQL. Trade-off: fewer provider extras than Pomelo; revisit if Pomelo ships EF 10. |
| Identity user/role live in **Infrastructure** | Keeps Domain/Application free of ASP.NET Identity; enforced by architecture tests. |
| `Hangfire`, `RabbitMQ`, `Redis`, `Qdrant` **not yet added** | Not needed by Phase 1 (spec: no unnecessary infrastructure). Added in the phase that first uses each. Compose therefore only runs MySQL + the three apps. |
| Fallback authorization policy = authenticated user everywhere | Secure by default; endpoints opt out with `[AllowAnonymous]`. |
| API auth: JWT bearer with ephemeral key until `Jwt:SigningKey` is set | Phase 1 has no token issuing, so protected API endpoints answer 401; token issuing is Phase 2. |
| Migrations applied by Web at startup only when `Database:MigrateOnStartup=true` | Single migrator avoids races; production can disable and run `dotnet ef` explicitly. |

## Phase 2 decisions (tenancy, users, permissions)
| Decision | Reason |
|---|---|
| Use cases are **interfaces in Application, implementations in Infrastructure** (`ITenantAdminService`, `IRoleService`, `IUserManagementService`, `IInvitationService`, `ITenantSettingsService`, `IAuditService`, `ITenantSwitchService`, `IAuthTokenService`, ...) | Application must stay free of EF Core (architecture test). Web and Api call the same services, so MVC and API enforce identical rules. Web/Api never touch `AppDbContext` outside `Program.cs` (architecture test). |
| Permissions are **policies named after the permission code** (`PermissionPolicyProvider` + `PermissionAuthorizationHandler`) | `[Authorize(Policy = "Users.Manage")]` on controllers; unknown names are not turned into policies. Permissions are read from the database per request (cached per request), never from the token, so changes apply immediately. |
| Object-level checks live in the services (tenant predicate + 404), plus a **privilege-escalation guard** (you can only grant/remove permissions you hold) | Permission policies alone do not stop a user-manager from making themselves an administrator. |
| Authorization failures are real **401/403** (cookie `OnRedirectToAccessDenied` → 403; unauthenticated MVC still redirects to login) | SECURITY.md semantics, testable. |
| API tokens: short-lived JWT (`sub`, `email`, `tid`) + rotating refresh tokens stored as SHA-256 hashes with reuse detection; `Jwt:SigningKey` required outside Development | Phase 1 left token issuing for Phase 2. Suspension revokes refresh tokens. |
| One tenant-resolution middleware for both hosts (`TenantResolutionMiddleware`, between authentication and authorization) | One implementation, one set of tests. |
| Platform operators live in a **platform tenant** instead of a global flag | Reuses roles/permissions/audit; "platform user" = holds `Platform.SwitchTenant`. |
| `AddTenancyServices` (HTTP + Identity dependent) is separate from `AddInfrastructure` (all hosts) | The Worker has no Identity/HTTP; `ValidateOnBuild` in Development would otherwise fail. |
| Self-registration no longer yields a usable account on its own | An account is not a workspace; the user lands on a "no workspace" page until invited. Registration stays off by default. |
| Secret tenant settings use ASP.NET Data Protection (`ISecretProtector`) with a persisted key ring (`DataProtection:KeysPath`) | Needed now (secret flag); the same protector will encrypt gateway/API credentials in Phases 4-5. No new dependency. |
| `Microsoft.AspNetCore.Authentication.JwtBearer` package added to Infrastructure (token creation) | It already shipped in the Api; same version. |

## Phase 3 decisions (CRM)
| Decision | Reason |
|---|---|
| Module layout: `Domain/Crm` (entities, enums, `Normalizer`), `Application/Crm` (DTOs, service interfaces, **pure logic**: `LeadScoringEngine`, `ScoringRuleParser`, `EligibilityRules`, `CsvReader`, `ImportRowMapper`, `SpreadsheetSafe`), `Infrastructure/Services/Crm` (EF-based services, file store, XLSX reader/writer) | Everything with rules worth testing in isolation is a pure function with unit tests; EF stays in Infrastructure (architecture tests). Web and Api call the same service interfaces, so MVC and REST enforce identical rules. |
| `CrmContext` (scoped) bundles db, active tenant, acting user, audit/activity helpers and membership checks; services never read a tenant from a request | One place for the tenancy conventions of Phase 2 (explicit `TenantId ==` predicate on top of the global filter, 404 for foreign ids). |
| **Eligibility** is one rule (`EligibilityRules`, pure) used by `IContactEligibilityService.CheckAsync`, and the same predicate is expressed as one SQL query for `QueryAudienceAsync`; an integration test proves both agree across the whole matrix | The gate described under "Compliance in the architecture" now exists. Phase 6 must call it when building audiences **and** immediately before every send. |
| Consent history is **append-only** (`IAppendOnly`, rejected by the SaveChanges guard); the effective state is the latest record by occurrence time, a revocation winning a tie | Auditable and tamper-resistant in code; no update/delete surface in services, API or UI (architecture test). |
| Scoring rules are tenant data, **versioned** (`ScoringRuleSet` v1, v2, …, one active); every `LeadScore` stores rule version + breakdown JSON; recalculation is explicit | Reproducible scores, no silent re-ranking. |
| Pipelines/stages are per tenant; a lead stores `PipelineId` + `PipelineStageId` and may only move within its pipeline; stage *kinds* (Open/Won/Lost/Suppressed) carry the meaning, not stage names | Tenants can rename/add stages freely; "Suppressed" kind excludes a lead from every audience. |
| Imports: upload (validate + store) → map columns → run (row by row, 50-row batches, duplicate check against the database, flush before an in-file duplicate) → report. Rows needing a human decision are persisted (`ImportRowError` with the mapped row as JSON) | Review policy needs the row later; no separate staging table. Runs in-request for now (limits 5 000 rows / 5 MB), Hangfire in Phase 6. |
| Files: `ITenantFileStore` (local disk, `tenants/{tenantId}/{area}/{guid}.ext`, keys must belong to the active tenant, no client paths) | The tenant file-path helper from Phase 2 finally has a consumer; swap for object storage later without touching callers. |
| Spreadsheet libraries: **ClosedXML** (writing) and **DocumentFormat.OpenXml** (reading, never evaluates formulas); CSV is an in-house RFC 4180 reader | No formula engine on the read path; no CSV dependency to audit. |
| `Contact.PhoneUniqueKey` / `EmailUniqueKey` = value while live, `NULL` when removed, unique with `(TenantId, LeadId)` | The database enforces "one phone per lead" yet a removed contact can be re-added. |
| No `AppDbContext` in Web/Api; new controllers are covered by the architecture permission/verb rules | Same edge discipline as Phase 2. |

### Request flow reminder
`Request → authentication → TenantResolutionMiddleware (membership re-validated) → authorization policy (permission code) → controller → service (ownership predicate, domain rules, audit, activity) → SaveChanges guard (tenant stamp, FK same-tenant, append-only)`.


## Phase 4 decisions (discovery and enrichment)
| Decision | Reason |
|---|---|
| **Hangfire introduced now, in the Worker**, with MySQL storage: `Hangfire.Core/NetCore/AspNetCore 1.8.25` + `Hangfire.MySqlStorage 2.0.3` | Phase 4 needs background jobs with retries and a daily maintenance job. **Compatibility check (run, not assumed):** a scratch console app on .NET 10 + MySQL 8.0.46 enqueued and processed a job. Findings: (1) `Hangfire.MySqlStorage` 2.0.3 dates from 2019 (built for MySqlConnector 1.0, Newtonsoft.Json 11, System.Data.SqlClient 4.4) and works with **MySqlConnector 3.0.0**; (2) the connection string needs `Allow User Variables=true` (added automatically); (3) the default 15 s queue poll made a first test look broken, so polling is configured (`Discovery:Hangfire:PollSeconds`, default 5); (4) `Newtonsoft.Json` 11.0.2 and `System.Data.SqlClient` 4.4.0 carry known vulnerabilities (NU1903/NU1902, build errors with TreatWarningsAsErrors), so they are pinned to 13.0.4 and 4.9.0 in `LeadPilot.Infrastructure.csproj`. `Hangfire.Storage.MySql` (2.1.0-beta) was rejected: still beta, same age. **Risk:** the storage package is unmaintained; revisit if it breaks (alternatives: a Pomelo/EF-based storage, or Quartz). The integration tests run a real Hangfire server against MySQL (`DiscoveryHangfireTests`). |
| Hangfire tables are created by Hangfire itself (prefix `Hangfire`, same database), **not** by EF migrations | Hangfire owns its schema and upgrades it. Web/Api/Worker all construct the storage lazily; EF migrations are still applied only by Web. |
| Web and Api register the Hangfire **client** only; the **server** runs only in the Worker. The dashboard is mapped in Web at `/hangfire` behind authentication with a filter that requires `Tenants.Manage` or `Platform.SwitchTenant` (platform operators); the default local-only filter is removed | Web/Api stay stateless; a tenant administrator can never open the dashboard (403), anonymous users are redirected to sign in. |
| Job entry point `DiscoveryJobExecutor.ExecuteAsync(tenantId, jobId)` runs `ITenantScopeRunner.RunAsync`, which **fails closed** (empty tenant id, unknown or suspended tenant throw). The runner re-reads the job with the tenant predicate, so a payload pointing at another tenant's job is a no-op | Argument is only ids; no tenant data is serialised. |
| Job state lives in `DiscoveryJobs` (Queued/Running/Completed/Failed/Cancelled) with an atomic claim (`UPDATE … WHERE Status = Queued OR stale Running`), a resume **checkpoint** (query index + page token) saved after every page, `CancelRequested` checked between pages, and bounded automatic retry (`[AutomaticRetry(Attempts = 2)]`; unexpected errors re-queue the job up to 3 attempts, then it is marked Failed). Provider errors are *not* retried automatically; they are reported (code, message, retryable flag) and the user retries | Idempotent, restartable, cancellable; retries never pay for a page twice and never duplicate results. |
| **RabbitMQ is not added** | Phase 4 has exactly one producer and one consumer of work, both inside one process boundary the job table already makes durable; Hangfire gives the retries and scheduling. RabbitMQ would add an outbox, consumers and a broker with no Phase 4 benefit. It is introduced in Phase 5 (inbound webhooks, messaging events), where the documented outbox/consumer contract applies. |
| `IDiscoveryConnector` is the provider-independent **port** (Application); adapters live in Infrastructure (`Discovery/Connectors`). Connectors never read credentials themselves: the engine decrypts the ACTIVE tenant's key and passes it with the request | A connector cannot reach another tenant's key. Typed failures (`ConnectorQuotaException`, `ConnectorAuthException`, `ConnectorTransientException`, `ConnectorPermanentException`, `ConnectorNotConfiguredException`) carry the failed call so it is still counted in usage. |
| Website fetching goes only through `ISafeHttpClientFactory` (SSRF-safe handler) and `SafeWebFetcher` (per-hop validation); an architecture test enforces that no other type in that namespace touches `HttpClient`/`IHttpClientFactory` | One audited path to arbitrary hosts. See SECURITY.md. |
| Approval goes through `ILeadIntakeService` (Phase 3 `DuplicateDetector` + `ILeadService.CreateFromSourceAsync`); `LeadSourceType.Discovery` stays reserved (the REST API still rejects it) | No second dedup implementation; the only server-side path that writes a discovery source. |
| Test-only fake connector lives in the **test assembly**; an architecture test fails if any `Fake*`/`Mock*` type exists in a shipped assembly | A fake can never be registered in production or development hosts. |
| Worker composition is one method (`AddWorkerServices`) shared by `Program.cs` and the integration tests | The tests exercise the real registration and `ValidateOnBuild`. |


## Phase 5 decisions (WhatsApp gateway: WAHA, conversations, outbox, RabbitMQ)
| Decision | Reason |
|---|---|
| **`RabbitMQ.Client` 7.2.2 added** (stable; 7.3.0 is beta). Compatibility with .NET 10 was **run, not assumed**: the solution builds with it, and the integration tests (`MessagingBrokerTests`) use it against a real RabbitMQ 3.12.1 broker for publisher confirms, topology, consumers, retry queues, dead-letter queues, header propagation and the health check. | Phase 4 had deliberately not added it; this is the first phase with durable async events. Only the async API is used. |
| **Port `IWhatsAppGateway`** (Application, `Messaging/Gateway.cs`) is provider independent: `GetAccountsAsync`, `GetAccountStatusAsync`, `ConnectAccountAsync`, `GetQrCodeAsync`, `DisconnectAccountAsync`, `SendTextAsync`, `SendTemplateAsync`, `MarkAsReadAsync`, `SetTypingAsync`, `GetMessageStatusAsync`, `FindSentMessageAsync`, `ProcessWebhookAsync`. The **only** implementation is `WahaGateway` in `Infrastructure/Messaging/Waha`; architecture tests fail if a WAHA endpoint, header or type name appears anywhere else. Calls take a `GatewayConnection` (decrypted per call from the account's own credentials): the adapter holds no tenant state. | "Do not replace the gateway" and "no gateway specifics outside Infrastructure". The official Business Platform could be a second adapter later. |
| **Templates:** WAHA has no official templates. `SendTemplateAsync` renders the tenant's own text (`{{name}}`, `{{first_name}}`, `{{business_name}}`, `{{phone}}`) locally and sends plain text; unknown placeholders are rejected at save time and a missing value rejects the send (a literal `{{nmae}}` can never reach a customer). | Honest mapping of the port onto what WAHA can do. |
| **HTTP resilience in the adapter:** one `HttpClient` whose `ConnectCallback` resolves and validates the address itself (same SSRF design as Phase 4; link-local/metadata addresses are refused even when private hosts are allowed); per-call timeout; **GET/management calls retry with bounded backoff**, **`sendText` is never retried** (`idempotent: false`, asserted by an architecture test); per-host circuit breaker; every failure is a typed `GatewayException` (`Unreachable`/`RateLimited` = nothing was sent, safe to retry; `OutcomeUnknown` = may have been sent; `Unauthorized`, `NotFound`, `Rejected`, `NewContactRestricted` (463/475), `UnsafeEndpoint`). | The no-duplicate guarantee starts with never repeating a send the gateway may have processed. |
| **Credentials:** `ISecretProtector` (Data Protection) as in Phase 4, plus the plaintext is `tenant:account:value`, so a ciphertext copied to another tenant/account fails to open (`CredentialVault`). Base URL, API key and webhook secret are all encrypted; nothing secret is returned by any DTO (architecture test) or written to audit text (integration test). | "A's credentials never used for B", also against database tampering. |
| **Webhook → tenant:** `POST /api/v1/webhooks/waha/{key}`. The secret 192-bit `{key}` identifies the account (`MessagingDirectory`, the **only** new `IgnoreQueryFilters` user, allow-listed); the account's own webhook secret then verifies **HMAC-SHA512 over the raw body** in constant time; the session name inside the signed payload must equal the account's session; only then is the tenant scope set. Unknown key → 200 `ignored`, nothing written. Bad signature → 401. Suspended tenant → 403. > 256 KB → 413. Rate limit per (key, IP). | Anonymous endpoint, authenticated by two independent secrets; never trusts a client-sent tenant. |
| **Idempotent storage:** one transaction inserts `WebhookEvent` (unique on account + event id; the id is WAHA's `evt_…` or a SHA-256 of the body), the conversation shell, the `Message` (unique on account + gateway message id) and the `OutboxMessage`. Duplicate delivery, concurrent identical deliveries and `message` + `message.any` for the same message all store exactly once (tested with 12 racing requests). | Exactly-once persistence without a distributed transaction. |
| **Outbox → RabbitMQ:** `OutboxDispatcher` (Hangfire recurring job every minute **plus** an immediate best-effort trigger after each webhook, debounced) finds tenants with due rows, claims each row with an atomic lease `UPDATE`, publishes with **publisher confirms**, marks it published; failures back off 5 s·2ⁿ (max 15 min) and park as `Failed` after 12 attempts (shown on the webhook page). At-least-once: a crash between publish and "mark published" republishes the same message id, which consumers absorb. | README §8: do not assume publish and commit are atomic. |
| **Topology** (`RabbitMq:Prefix`, default `leadpilot.messaging`): topic exchange; queues `….inbound` (`message.received`) and `….status` (`message.status`, `account.status`); per queue `.dlq` and one TTL retry queue per backoff step (default 5 s, 30 s, 2 min, 10 min) that dead-letters back into the queue. Headers on every message: `TenantId`, `CorrelationId`, `CausationId`, `x-attempt`; the body is `EventEnvelope { MessageId (= outbox id), TenantId, CorrelationId, CausationId, Type, OccurredAtUtc, SchemaVersion, Payload }`. `ai.reply.requested` is published for conversations whose `AiMode` is Active (**extension point for Phase 7/8; no queue is bound yet and no AI logic exists**). | Per-queue DLQ, bounded retries, correlation end to end. |
| **Consumers** (`MessageProcessor` + `MessagingConsumerHost`, Worker only): the verified `TenantId` header is parsed **before anything else**; missing/garbled/zero header, header ≠ envelope tenant, unexpected event type, unknown or suspended tenant, or a handler `PoisonMessageException` ⇒ **dead-letter** (never processed); other failures ⇒ retry queue with a higher `x-attempt`; after the last attempt ⇒ DLQ. Manual acks; a delivery is acked only after the handler finished or the message was re-published (with confirms) to a retry queue/DLQ. Handlers look everything up with the tenant of the header, so a forged tenant with another tenant's ids finds nothing. Effects and the `ProcessedMessage` row commit together (unique key), so redelivery is a no-op. | The `MULTI_TENANCY.md` consumer rule, implemented and tested. |
| **Outgoing pipeline:** `OutboundQueueService` validates (account state → conversation → idempotency key → template → compliance → new-contact restriction → queue limits), persists `Message(Queued)` + status event + audit in one transaction and schedules a Hangfire job. `OutboundSendRunner` (Hangfire job, **`AutomaticRetry` = 0**) re-validates everything immediately before sending, paces (min gap, hourly/daily caps, warm-up), then **claims `Queued → Sending` with one atomic UPDATE** (a retried/concurrent job gets 0 rows). | A worker retry can never produce a duplicate. |
| **The "sent but crashed before saving the id" window:** the message stays `Sending` (crash) or becomes `Unknown` (timeout, 5xx, dropped connection, missing id in a 2xx). A reconcile job asks the gateway for our recent messages in that chat (`GET /api/{session}/chats/{chat}/messages?filter.fromMe=true&filter.timestamp.gte=…`) and matches the exact text: found ⇒ `Sent` with its id (and ack state); not found for 10 minutes ⇒ `Failed(delivery_unconfirmed)`. **The system never re-sends an Unknown message by itself**; a person can send again. A once-a-minute sweep re-schedules lost jobs and reconciles stuck messages. | Documented choice: a visible missed message is recoverable, a duplicate WhatsApp message to a customer is not. |
| **Acks:** `message.ack` → outbox → status consumer → `Message.ApplyDelivery` (rank Sent < Delivered < Read; PLAYED = Read; late lower acks are recorded with `Applied=false`, never regress; an ack that overtakes the stored gateway id is retried a few times, then ignored). `session.status` → `AccountHealthService`. | Monotonic delivery tracking. |
| **Account health / auto-pause:** 3 consecutive hard failures, a FAILED/STOPPED session that was WORKING, or an unconfirmed delivery ⇒ the account pauses (`PausedAutomatically`), a notification row is written and audited; resuming is a deliberate human act (a reconnect does not resume). A WhatsApp reachout timelock / message capping (`session.status` data, send error 463/475) blocks **new contacts only** until the reported time; the session is not restarted. | WAHA's guidance and README reliability list. |
| **Hangfire queue `messaging`** added to the Worker's server; recurring jobs `messaging-outbox`, `messaging-sweep` (every minute) and `messaging-retention` (daily 03:30). | Hangfire stays the only scheduler (README §8). |
| **Config binding pitfall found by the broker tests:** .NET configuration binding **appends** array values to a non-empty default array (`1,1,1` became `5,30,120,600,1,1,1`). `RabbitMq:RetryDelaysSeconds` therefore has no initial value and `RetryDelays` falls back to the defaults. Regression-tested. | Real bug caught by the real-broker test. |
| **Health:** `rabbitmq` check (tag `ready`) is registered when `RabbitMq:Host` is set; unreachable ⇒ unhealthy. Not configured ⇒ events stay in the outbox and the accounts/webhook pages say so. | Broker is optional for hosts that do not run messaging yet. |

### Message flows
```
INBOUND   WAHA ──POST /api/v1/webhooks/waha/{key} (HMAC)──▶ Api: verify → resolve tenant+account → ONE TX: WebhookEvent + Conversation + Message + OutboxMessage → 200
          Hangfire OutboxDispatchJob ──confirm──▶ RabbitMQ (headers TenantId/CorrelationId/CausationId)
          Worker consumer: header → tenant scope → ProcessedMessage check → link Contact by E.164 → update Conversation → opt-out? (SuppressionEntry + consent revoked + audit) → [ai.reply.requested] → commit → ack
OUTBOUND  Web/Api: validate (tenant, eligibility, suppression, account, limits) → Message(Queued, idempotency key) → Hangfire MessageSendJob
          runner: re-validate → pace → claim Queued→Sending (atomic) → seen/typing → sendText → Sent + gateway id | Unknown → reconcile | Failed
          WAHA ──message.ack──▶ webhook ──▶ outbox ──▶ status consumer ──▶ monotonic Sent/Delivered/Read
```

## Phase 6 decisions (campaigns)
| Decision | Reason |
|---|---|
| **No second send pipeline, scheduler, eligibility check or rate limiter.** Campaign sends call `OutboundQueueService` (Phase 5) with a forced Marketing purpose, a campaign idempotency key and the campaign/recipient ids; `OutboundSendRunner` calls two small ports (`ICampaignSendGuard` before the gateway call, `ICampaignEvents` after the outcome). An architecture test fails if anything else calls `IWhatsAppGateway.Send*`. | README Rule: one outbound path. |
| **Hangfire is the only scheduler**: `CampaignJob.StartAsync/TickAsync` (queue `campaigns`, bounded automatic retry because both are idempotent), `CampaignSweepJob` every minute. An architecture test forbids timers/threads/other schedulers in the module. | README §8. |
| **One tick = one batch**, protected by a DB lease (`LeaseUntilUtc`); the next tick is scheduled after a random pause between the campaign's bounds. The recipient table is the checkpoint: a restart continues from recipient statuses; the idempotency key `cmp:{campaign}:{recipient}[:round]` makes a repeated hand-over return the existing message. A pause sends withdrawn recipients back to Pending with `Round+1` (new key); the old message is already Failed. | Recoverable, no duplicates. |
| **Marketing permission is checked at selection, before queueing and immediately before the gateway call** (guard stores the send-time decision next to the selection-time one). | README Phase 6 critical rule. |
| **Status changes use one state machine**; the runner changes status with guarded SQL (`WHERE Status = …`) that also bumps `Version`, API actions use the entity and the concurrency token. | A pause racing a tick wins or conflicts, never silently loses. |
| **Rollup is recomputed, never incremented.** Recipient rows are updated from real message states (hooks, consumer, and a repair pass in each tick); `campaign.recipient` / `campaign.status` outbox events feed the `campaigns.events` consumer (new queue `…campaigns`, DLQ/retry like the others) which recomputes the rollup idempotently. | Stats can only equal the raw data. |
| **Audience = one SQL query + streamed paging**; eligibility through `IContactEligibilityService.CheckManyAsync` (bulk loader, same `EligibilityRules`); preview and snapshot builder share one scanner. | Never load everything; no second gate. |
| **Settings live in a dedicated typed row (`CampaignTenantSettings`)**, not generic tenant settings. | Anyone with `Settings.Manage` could otherwise flip the kill switch or the approval policy; here only `Campaigns.Settings` can. |
| Cross-tenant sweep uses `CampaignDirectory` (the only new `IgnoreQueryFilters`, allow-listed): it returns tenant ids only; all work runs in `ITenantScopeRunner`. | Same rule as `MessagingDirectory`. |
| `TemplateRenderer` extended in place (fallback syntax, `city`/`country`, brace validation, `Sanitize`) rather than a second renderer. | One renderer for replies and campaigns. |

### Campaign flow
```
Web/Api: Draft → submit (freeze text + footer) → [PendingApproval → approve] → Scheduled (+ Hangfire start job at the UTC time)
Hangfire start → atomic Scheduled→Running → tick job
tick: lease → account/kill-switch/limit checks → snapshot (resumable) → repair recipients from messages → complete? → batch allowance (account caps, warm-up, campaign daily, monthly)
      → per recipient: fresh status check → eligibility → cool-down/duplicate/content → OutboundQueueService (Phase 5) → recipient Queued → next tick after a random pause
Phase 5 send runner: guard (campaign running? kill switch? recipient still waiting? eligibility again) → pacing → atomic claim → gateway
acks / replies / stage changes → recipient + conversions + outbox events → campaigns.events consumer → rollup
```


## Phase 7 decisions (AI engine and knowledge base)
- **Ports (Application/Ai):** `IAIProvider`, `IEmbeddingProvider`, `IVectorStore`, `IKnowledgeRetriever`, `IConversationMemory`, `IAIOrchestrator`. Business code depends on these only (architecture tests: vendor endpoints/headers appear only in `Infrastructure/Ai/Providers` and the options defaults; use cases never name an adapter).
- **Adapters (Infrastructure/Ai):** `ClaudeAIProvider` (Messages API over HTTP, prompt caching on the stable system block), `VoyageEmbeddingProvider`, `MySqlVectorStore`, `QdrantVectorStore`. They sit BEHIND metering decorators (`MeteredAIProvider`, `MeteredEmbeddingProvider`) that check the monthly limit/budget before and write the ledger after every call. Shared `AiHttpPipeline`: bounded retries with backoff + jitter, `Retry-After`, per-attempt timeout, per-tenant circuit breaker (`AiCircuitBreaker`). Keys are decrypted only in `AiCredentials` and never logged.
- **Tenant isolation of retrieval, two independent barriers:** (1) `IVectorStore` has NO tenant parameter; implementations take the tenant from `ITenantContext` and apply it to every search/upsert/delete/count (Qdrant: one place builds filters and always adds `tenant_id`; hits whose payload tenant differs are dropped). (2) The retriever re-validates every hit in the database with the tenant filter and returns only chunks of ENABLED documents whose CURRENT, indexed version they belong to.
- **Ingestion:** upload/paste/website → `DocumentTextExtractor` (txt, md, html, pdf via PdfPig, docx via zip+XmlReader with DTD off and size/entry limits; type decided by extension AND leading bytes) → `DocumentVersion` (immutable text, hash) → Hangfire queue `knowledge` (`KnowledgeJob`, bounded retry, atomic claim, stale takeover, 5-minute `knowledge-sweep`) → `TextChunker` → embeddings → `EmbeddingRecord` rows (+ vector store) → switch-over (older versions' passages/vectors removed only after the new version is indexed). Website sources use the Phase 4 `SafeWebFetcher`/`UrlGuard` (SSRF-safe), robots.txt, noindex, same host, 10 pages.
- **RAG workflow (`AiOrchestrator`):** consumer of `ai.reply.requested` (RabbitMQ queue `{prefix}.ai`) → ids verified against the tenant header → stale/duplicate/paused/opt-out checks → history (`ConversationMemory`: recent window + rolling summary) → deterministic triage (sensitivity rules, injection signals) → intent/query model call → retrieval → instruction-like passages quarantined → one generation call (JSON) → `ResponseValidator` (citations, amounts/percentages only from cited passages, offers/guarantees, completion claims, leaks, unsupported links/contacts) → permitted actions through `AiActionExecutor` → stored `AiInteraction` (a suggestion for staff). One suggestion per inbound message is enforced by a unique index.
- **Untrusted input:** customer text, summaries and knowledge are placed in delimited data blocks with `<`/`>` neutralised, control/bidi characters removed and length capped; the stable instructions contain no tenant or customer data, so they are identical for every request (cacheable) and cannot be edited by a customer.
- **Actions:** only `escalate_to_human` and `add_lead_note` exist (`AiActionCatalog`); everything else is denied and logged in the append-only `AiToolExecutions`. No AI code can reach the send pipeline or the WhatsApp gateway (architecture test).
- **Messaging stays the only send path:** staff use a suggestion as the start of a normal conversation reply (consent/window checks unchanged).

## Phase 8: the sales agent
- **Framework, not a product:** `SalesAgent` (instructions, enabled tools, model/threshold overrides, opt-in), `AgentQualificationQuestion`, `CatalogProduct`/`PricingPlan` are tenant data. The School ERP is a clearly-marked SAMPLE created by a button, with placeholders only.
- **Flow (extends the Phase 7 orchestrator, no second one):** inbound → `ai.reply.requested` → checks (AI mode, opt-out, duplicate, newer message) → triage (intent, sensitivity rules in English/Urdu/Roman Urdu, injection signals; opt-out intent → `StopMarketingMessages`; off-topic → the agent's redirect) → retrieval → **`AgentLoop`** (≤ 3 rounds: the model returns `tool_calls`, `AgentToolRunner` executes them, results come back as untrusted `<tool_result id="T1">` data that can be cited) → `ResponseValidator` (now also against tool results and verified completions) → decision (Draft / Escalate / NoReply) → stored `AiInteraction` → optional automatic reply.
- **`AgentToolRunner` + `AgentToolHandlers`:** catalog → allow-list → schema validation → replay check → handler (works on the conversation's lead) → approval request when sensitive → one `AiToolExecution` row; handlers call the Phase 10 ports (default "not connected").
- **Replying:** the AI module depends only on the port `IAiReplySender`; its single implementation lives in the messaging module (`AiReplySender`) and calls `OutboundQueueService` (forced Service purpose, idempotency key `ai:{interaction}`, `IsAiGenerated` + the AI epoch on the message). `OutboundSendRunner` re-checks the epoch/mode right before the atomic claim.
- **Handoff:** `Conversation.AiMode` (Active/Paused/HumanControl) + `AiEpoch`, `EscalationReason`; `ConversationService` gained takeover, pause, resume, notes, history (and records `HandoffEvent`s for assignment/status); a staff reply auto-takes control. `OptOutProcessor` is the one implementation of "stop" (used by the inbound keyword rule and the AI tool).
- **UI/API:** Sales agent (agents, questions, opt-in, settings), Product catalog, Approvals, Tool log, Follow-ups, inbox controls (who answers, take over, pause/resume, assign, notes, history); `/api/v1/agents`, `/catalog`, `/approvals`, `/tool-executions`, `/follow-ups`, `/conversations/{id}/takeover|ai/pause|ai/resume|notes|handoff-history`.
