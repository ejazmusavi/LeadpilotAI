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
