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
