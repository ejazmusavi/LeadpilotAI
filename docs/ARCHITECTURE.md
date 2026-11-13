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
