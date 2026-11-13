# Progress

## Phase 0 — Requirements, Architecture and Planning
**Status: Completed and approved by owner**

### Done
- Full specification analyzed; ambiguities/conflicts and resolutions recorded in PROJECT_OVERVIEW.md.
- Solution structure, dependency rules, module boundaries, background-processing contract: ARCHITECTURE.md.
- Initial entity model with tenant ownership, constraints and indexes: DATABASE_DESIGN.md.
- Tenant isolation strategy and required tests: MULTI_TENANCY.md.
- Security boundaries and threat list: SECURITY.md.
- Integration contracts and gateway information checklist: INTEGRATIONS.md.
- Phased plan, dependencies, MVP path, risks: IMPLEMENTATION_ROADMAP.md.

### Acceptance criteria
| Criterion | Result |
|---|---|
| All major modules identified | Passed |
| Entity relationships documented | Passed |
| Tenant isolation strategy documented | Passed |
| No major architectural conflicts remain | Passed (conflicts resolved in docs; pending owner confirmation) |
| Phases and dependencies clear | Passed |

### Environment observations
- Repository contained only README.md at start.
- (Resolved in Phase 1) .NET 10 SDK was installed in the sandbox after allowing the SDK/NuGet hosts; MySQL 8.0.46 installed via apt for verification. No Docker daemon is available here.

### Needed from owner before the relevant phase
WAHA deployment details: URL, API key, engine, webhook secret (Phase 5) · product/pricing/demo info for any product (Phase 7–8) · Google Places key (Phase 4) · Claude API key (decided) + embedding provider (Phase 7) · payment method info (Phase 10) · hosting target (Phase 15). See PROJECT_OVERVIEW.md.

## Phase 1 — Project Foundation
**Status: Partially completed** — everything verified in this sandbox passed; Docker environment could **not** be verified (no Docker daemon here).

### Implemented
- .NET 10 solution (`LeadPilotAI.sln`): Domain, Application, Infrastructure, Web (MVC), Api, Worker, Shared + Unit/Integration/Architecture test projects; DI, options-based config, Serilog structured logging (JSON outside Development) with correlation IDs, global exception handling (RFC 7807 in API, error page in MVC), FluentValidation, paged-result convention, Swagger, `/health/live` + `/health/ready` (checks MySQL).
- ASP.NET Core Identity (Guid keys): login, logout, registration (config-gated, off by default, on in Development), forgot/reset password (no account enumeration, 2h token, sessions invalidated on reset), lockout (5 attempts/15 min), 10+ char password policy, cookie session (HttpOnly, sliding 8h, Secure outside Development), antiforgery on all POSTs, rate limiting on auth endpoints, authenticated-by-default fallback policy, optional first-admin seed from secrets.
- Responsive Bootstrap 5 shell: sidebar (offcanvas on mobile), top bar, user menu, breadcrumbs, notification area (honest empty state), toasts, empty-state/error partials, form loading state, error and status pages. Dashboard shows **no fake metrics**; it shows real DB health, environment and version.
- Initial EF migration `InitialIdentity` (Identity tables) applied to MySQL 8.
- Dockerfile (multi-stage, non-root), `docker-compose.yml` (MySQL not published to host), `.env.example`.
- Bug found and fixed by tests: API fallback policy returned 500 for unauthenticated requests because no authentication scheme was registered; JWT bearer scheme added.

### Tests (actual results, MySQL 8.0.46 local)
| Suite | Result |
|---|---|
| Build | 0 warnings, 0 errors (warnings are errors) |
| Unit | 12 passed |
| Architecture | 8 passed (dependency direction, Domain/Application framework independence, no Api/Worker/Web cross-references) |
| Integration | 16 passed (migrations, health, login/logout/lockout, registration, weak password, antiforgery, enumeration, Swagger, API auth) |
| Not executed | Docker image builds / `docker compose up` (no Docker daemon in sandbox) |

### Acceptance criteria
| Criterion | Result |
|---|---|
| Solution builds successfully | Passed |
| Database connection works | Passed |
| Initial migrations apply successfully | Passed (MySQL 8.0.46, via `dotnet ef` and via app startup in tests) |
| Login and logout work | Passed (integration tests) |
| API documentation is available | Passed (`/swagger/v1/swagger.json` tested) |
| Docker environment starts successfully | **Not verified** — files written, YAML parses, but no Docker daemon available here |
| No cross-layer dependency violations | Passed (architecture tests) |

### Outstanding
- Verify Docker build/compose on a machine with Docker. Phase 1 must not be called complete until then.
- Email confirmation for self-registration is not implemented (self-registration is off by default).
- Password-reset email delivery needs SMTP configured outside Development; only the log sender exists for dev.
- Oracle MySQL provider vs Pomelo: revisit when Pomelo supports EF Core 10.

### Next
Phase 2 — started after the owner explicitly chose to proceed without Docker verification (Docker is still **not verified**; this is not a waiver of the Phase 1 outstanding item).

## Phase 2 — Tenancy, Users, Roles and Permissions
**Status: Built; every check that could be run here passed (140 automated tests, MySQL 8.0.46). Docker (inherited from Phase 1) and a visual browser pass are not verified.**

### Implemented
- **Entities / migration `TenancyAndPermissions`** (InitialIdentity untouched; applied to a clean MySQL 8 both via `dotnet ef` and via app start): Tenant, TenantSetting, UserProfile, Role (tenant-scoped), Permission (global catalog, 16 codes), RolePermission, UserRole, UserTenant, TenantInvitation, AuditLog, plus RefreshToken for API sessions. See DATABASE_DESIGN.md §1.
- **Seeding** (idempotent, every Web start): permission catalog, platform tenant, default system roles in every tenant (system roles re-synced so future permissions propagate), first Platform Owner from `Seed:*`.
- **Tenancy core**: `ITenantContext`, tenant-resolution middleware (cookie `tid` and JWT `tid`, validated against membership on every request), global query filters on every `ITenantOwned` entity, SaveChanges guard (stamp, cross-tenant write block, immutable TenantId, cross-tenant FK rejection, append-only audit), explicit ownership checks in every service, platform scope, `ITenantScopeRunner` for jobs/consumers (fails closed), tenant cache-key and file-path helpers, spoof guard (route/query/form/body/header).
- **Features** (same services behind MVC and API): tenant create/edit/suspend/activate (platform), tenant profile + key/value settings (secret values masked + encrypted), users (list, role assignment, disable/enable, remove, last-admin and self-lock guards), invitations (hashed single-use expiring tokens, resend, revoke, accept for new and existing users), roles and permissions (custom roles, immutable system roles, privilege-escalation guard), audited tenant switching (dashboard, API switch endpoint, direct platform login), audit log viewer, own-profile page.
- **Authorization**: permission-code policies enforced server-side on every MVC controller and API endpoint; real 401/403/404 semantics; sidebar shows only permitted pages (cosmetic only).
- **API auth**: `POST /api/v1/auth/login|refresh|logout|switch-tenant`; 15-min JWT + rotating hashed refresh tokens with reuse detection; `Jwt:SigningKey` mandatory outside Development; suspended tenants blocked (existing tokens → 403, refresh revoked, login 403).
- **UI**: Bootstrap pages for tenants, users, invitations (+ public accept page), roles/permissions, settings, audit log, switcher, profile; breadcrumbs, empty states, confirmation modal for every destructive action, 4/2/1-column responsive form grids, no placeholder data.
- **Docker files** updated (JWT key required for api, shared Data Protection key volume, public URL) — YAML parses; **not run**.

### Tests (actual results, MySQL 8.0.46 local, `dotnet test LeadPilotAI.sln`)
| Suite | Result |
|---|---|
| Build | 0 warnings, 0 errors (warnings are errors), `--no-incremental` |
| Unit | **60 passed** (was 12): permission evaluation, escalation guard, system role mapping, tenant/slug/state rules, role/invitation/refresh-token rules, tenant context + platform scope, cache-key/file-path helpers, JWT key policy, token hashing |
| Architecture | **16 passed** (was 8): dependency rules + `IgnoreQueryFilters` allow-list (and stale-entry check), Web/Api never touch `AppDbContext` (except `Program.cs`), every tenant-owned entity has a query filter and tenant FK, no `TenantId` table without `ITenantOwned`, every tenancy controller action declares authorization with a real catalog permission |
| Integration | **64 passed** (was 16), 0 skipped: see list below |
| Failed | none |
| Not executed | Docker build/compose; browser rendering check (responsive layouts, modal behaviour in a real browser); load/concurrency tests; real SMTP delivery |

Integration coverage (all against real MySQL, MVC **and** API): two tenants with identical role names/setting keys operate independently · A cannot list/get/update/delete B's roles, users, invitations (404, B untouched) · spoofed TenantId in header/query/route/body/form → 400 · other tenant's role cannot be assigned or invited into → 400 · missing permission → 403, anonymous → 401 (also forged, expired, wrong-`tid`, no-`tid`, unknown-user tokens) · privilege escalation blocked · last admin/self cannot be removed · system roles immutable · invitation flow (hashed token, single use, expiry, revoke, resend, supersede, existing-user path, multi-tenant membership, suspended-tenant refusal) · suspension blocks MVC sessions, API tokens, refresh, login and platform switch; reactivation restores · tenant switching audited in the target tenant; non-platform users get 404 and leave no audit trail · refresh rotation + reuse revokes all sessions · secret settings masked and encrypted at rest · query filters return nothing without a tenant · writes without/for another tenant rejected, TenantId stamped/immutable · cross-tenant FK inserts rejected (DB-resident and same-unit-of-work principals, even in platform scope) · audit append-only · job/consumer scope runner fails closed (empty, unknown, suspended tenant; unscoped DbContext sees nothing and cannot write) · seeded catalog/roles/mappings. Mutation check: removing the security-stamp tenant-claim preservation makes the switching test fail.

### Smoke checks (manual, this sandbox)
Web starts, migrates a fresh DB and seeds it (platform tenant, 16 permissions, platform roles); Api in Production without `Jwt:SigningKey` exits with a clear error; Worker starts (DI validation passes) and connects to the DB.

### Acceptance criteria
| Criterion | Result |
|---|---|
| Two tenants can operate independently | **Passed** (integration tests) |
| Tenant users cannot access other tenant data | **Passed** (MVC + API: list/get/update/delete/spoofed id; DB-level guards) |
| Permissions are checked at the server | **Passed** (403 tests on every admin page/endpoint, POSTs included; architecture test on declarations) |
| Unauthorized API requests return appropriate responses | **Passed** (401 / 403 / 404 / 409 semantics tested) |
| Tenant isolation tests pass | **Passed** (140/140 overall, 0 skipped) |

### Outstanding / known limits
- Docker still unverified (Phase 1 carry-over) — compose now additionally needs `JWT_SIGNING_KEY`.
- UI not inspected in a real browser; responsive column classes (4/2/1) and the confirm modal script are covered only by server-rendered HTML assertions.
- Invitation/password-reset e-mail needs SMTP outside Development (only the log sender exists for dev).
- Refresh tokens are never purged (needs the Phase 6 job host). No 2FA yet (planned for platform roles).
- Login auditing records successful sign-ins and lock-outs only in the user's default workspace; failed attempts are not audited.
- API login rate limit is per IP only; Web `auth` limiter unchanged.
- Self-registration (off by default) creates an account without workspace; users must be invited.
- Soft delete/RowVersion conventions from DATABASE_DESIGN.md are not applied to the Phase 2 tables yet (no concurrent editing hot spots); revisit with the CRM.

### Next
Phase 3 — CRM (leads, contacts, pipeline, import/export, scoring, consent, suppression). Prerequisites: none external; Docker verification and an SMTP server are recommended before real users are invited.
