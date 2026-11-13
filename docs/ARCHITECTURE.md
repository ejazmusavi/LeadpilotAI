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
