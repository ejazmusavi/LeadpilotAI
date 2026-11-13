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
Phase 3 — CRM (done below). Docker verification and an SMTP server are still recommended before real users are invited.

## Phase 3 — Lead Management and CRM
**Status: Built; every check that could be run here passed (341 automated tests, MySQL 8.0.46, 0 failed, 0 skipped). Docker (inherited from Phase 1) is still not verified. Built on branch `claude/phase-3-crm`, which contains the Phase 1 and Phase 2 branches (PR #1 and PR #2 are not merged yet).**

### Implemented
- **20 tenant-owned tables / migration `Phase3Crm`** (earlier migrations untouched): `LeadCategories`, `LeadStatuses`, `Pipelines`, `PipelineStages`, `Leads`, `LeadSources`, `Contacts`, `ConsentRecords`, `SuppressionEntries`, `LeadActivities`, `LeadNotes`, `LeadTags`, `LeadTagMaps`, `LeadScores`, `ScoringRuleSets`, `ScoringRules`, `Opportunities`, `OpportunityActivities`, `ImportJobs`, `ImportRowErrors`. Guid keys, UTC, `(TenantId, …)` indexes, per-tenant unique constraints, soft delete where history matters (archive for leads, soft delete for contacts/notes), optimistic concurrency on leads (`Version`). Lead, Contact, Conversation (Phase 5) and Customer (Phase 10) stay separate entities. See DATABASE_DESIGN.md §2.
- **Product-agnostic by construction**: categories, lead statuses, pipelines/stages and scoring rules are tenant data. Nothing in the code mentions schools. Every tenant gets the 14 default stages, 4 lead statuses and 9 starter scoring rules at creation; the idempotent startup seeder gives them to tenants that already existed.
- **Leads**: create (with contacts and tags), edit (concurrency-checked), view (profile, score breakdown, sources, tags), archive/restore, assign, change stage (activity + audit), search/filter/sort/paging (name, city, website, contact name/phone/e-mail, category, status, stage, owner, tag, score range, source, dates, archived), pipeline board, lead source tracking, activity timeline, notes, tags.
- **Contacts** (many per lead): E.164 phones, lower-cased e-mails, `DataOrigin` Public/Provided/Verified, primary contact, soft removal that releases the phone/e-mail for re-use, **consent revoked automatically when a phone/e-mail changes**.
- **Duplicate detection** (normalised name+city, website domain, phone) on create, on import and via `POST /api/v1/leads/duplicates/check`; manual create answers 409 `duplicate` unless `allowDuplicate=true`.
- **CSV and Excel (.xlsx) import**: size/row/column limits, zip-bomb check, content sniffing (a workbook named .csv or text named .xlsx is refused), formulas never evaluated, header auto-mapping with a mapping step, per-row validation, per-row error report (screen + CSV download), duplicate policies **Skip / Merge (fill blanks only) / Review (decide per row)**. Imported contacts carry no consent.
- **Export** (CSV/XLSX): permission-gated (`Leads.Export`), audited, row-limited, formula-injection-safe (CSV `'` prefix; XLSX quote-prefix text style).
- **Scoring**: rule-driven (category, location, website, contact quality, activity, verified info, engagement, interest), rules are versioned tenant data (each edit publishes an immutable version), every score stores its rule version and breakdown (`LeadScores` history), recalculation is explicit.
- **Consent** (append-only, auditable) and **suppression** (unique per tenant+channel+value, hashed + normalised, add/remove audited with masked values).
- **`IContactEligibilityService`** — the single gate: consent granted for channel+purpose AND value not suppressed AND contact valid (and lead neither archived nor in a *Suppressed* stage). Lead score is structurally absent from the decision. `QueryAudienceAsync` computes eligible contacts **in SQL** (paged), so suppressed/unconsented contacts never leave the database.
- **Permissions** (catalog extended, mapped into default roles, enforced server-side in MVC and API): `Leads.Import`, `Leads.Export`, `Leads.Assign`, `Consent.Manage`, `Suppression.Manage`, `Pipelines.Manage`, `Scoring.Manage` (+ the existing `Leads.View/Create/Update/Delete`). Existing tenants receive the mappings through the startup seeder (system roles are re-synchronised); custom roles are never touched.
- **Bulk operations** (assign, tag/untag, stage, archive/restore): every id is authorised individually (tenant ownership, the actor's permission, domain rules); other-tenant and unknown ids are indistinguishable "Lead not found"; capped at 500 per call.
- **REST API** (`/api/v1/leads`, `contacts`, `notes`, `tags`, `pipelines`, `categories`, `lead-statuses`, `scoring`, `imports`, `suppression`, `audience`, `opportunities`) with Swagger XML docs and the Phase 2 auth/ProblemDetails conventions (401/403/404/409/413).
- **UI**: responsive Bootstrap pages — lead list (filters, sort, paging, bulk bar, export), lead detail with Overview/Contacts/Activity/Notes/Consent tabs, create/edit forms (4/2/1 column grid), pipeline board, import wizard (upload → map → run → result/review), imports history, tags, suppression list, scoring rules, pipelines, categories & statuses. Breadcrumbs, empty states, confirm dialog on every destructive action, sidebar items only for pages the user may open. No sample data anywhere.
- **Opportunities** (`Opportunity`, `OpportunityActivity`): entities, service and API (create/update/stage change, tenant-checked). **No UI yet** — nothing in the spec asks for one in this phase.

### Existing-code changes made in this phase (and why)
| Change | Reason |
|---|---|
| `TenantSaveChangesInterceptor`: generic `IAppendOnly` rule (replaces the AuditLog special case); per-save cache for foreign-key tenant lookups | Consent, activity, score and opportunity-activity history must be immutable in code, not by convention; imports save 50 leads at a time and would otherwise issue one lookup per foreign key per row. |
| `PlatformSeeder` also provisions CRM defaults for existing tenants; `TenantAdminService.CreateAsync` provisions them for new tenants | "Works for existing tenants". |
| API global rate limit is configurable (`RateLimiting:ApiPermitLimit`, default unchanged = 300/min/IP) | The hard-coded value made a busy test run (and any shared-IP office) return 429. |
| **Bug fix (Phase 1):** `MapStaticAssets().AllowAnonymous()` | Found in the browser pass: static CSS/JS inherited the authenticated-by-default policy, so **the login page loaded without CSS or JavaScript**. Regression test added. |
| **Bug fix (Phase 1):** sidebar kept its dark background on desktop (`site.css`) | Bootstrap's `.offcanvas-lg` is transparent from the lg breakpoint, leaving light nav text on a light page (found in the browser pass). |
| `ApiControllerBase`: `duplicate` → 409, `too_large` → 413; Api serialises enums as strings | New error cases; readable API. |

### Tests (actual results, MySQL 8.0.46 local, `dotnet test LeadPilotAI.sln`, clean `--no-incremental` build: 0 warnings, 0 errors)
| Suite | Result |
|---|---|
| Unit | **200 passed** (was 60): E.164/name/domain/e-mail normalisation incl. Arabic folding, scoring engine and rule validation, eligibility rules (no consent, revoked, suppressed, invalid value, removed contact, archived lead, suppressed stage, score cannot matter), consent/contact/lead domain rules, CSV parsing (quotes, newlines, BOM, delimiters, limits, binary), XLSX reading (numeric phones, formulas not evaluated, zip bomb), column mapping/row validation, formula-injection escaping |
| Architecture | **24 passed** (was 16): every CRM entity tenant-owned; history entities append-only with no mutators; every CRM controller action has a catalog permission and an explicit HTTP verb; mutating MVC actions answer POST only; no consent edit/delete surface; new permissions in catalog/default roles and absent from Read-only; exports/imports/suppression behind their permissions |
| Integration | **117 passed** (was 64), 0 skipped — see below |
| Failed | none |
| Not executed | Docker build/compose; cross-browser and accessibility audits; load/concurrency tests; real SMTP delivery |

Integration coverage added (53 tests, real MySQL, MVC **and** API): CRUD with archive/restore and concurrency conflicts · multiple contacts per lead (normalisation, primary, duplicates, soft removal) · duplicate detection by name+city / domain / phone and the override · stage change with activity, audit and score effect · assignment rules · notes/tags · search/filter/sort/paging · bulk operations incl. mixed-tenant ids and permission checks · consent append-only (API surface, DB guard, audit, revocation on phone change) · suppression · **eligibility matrix and SQL audience query agree** (no consent, revoked, suppressed on one channel / on all channels, removed contact, wrong purpose, wrong channel, archived lead, suppressed stage; **a 100-point lead is still blocked**) · scoring versions and recalculation · pipelines/stages/default switching · categories/statuses · the tenant default country code for local phone numbers · Swagger documents the CRM endpoints · CSV and XLSX import with errors, all three duplicate policies, limits and file validation · export safety, limits, audit · **tenant isolation for every new entity**: list/get/update/delete/export/bulk through API and MVC all answer 404 or "not found", B's data verified untouched, cross-tenant references in request bodies rejected, spoofed `tenantId` (body, query, header, form) rejected, database-level cross-tenant FK rejection for all 19 foreign-key relationships and query filters on all 20 tables · 401/403/404 semantics · UI pages, forms, antiforgery, XSS encoding, open-redirect guard · **migration applies to a database at the Phase 2 schema with an existing tenant** and the seeder upgrades it idempotently · EF model has no pending changes.

Mutation checks: disabling the cross-tenant FK guard makes `Cross_tenant_foreign_keys_are_rejected_for_every_new_entity` fail (reverted).

### Smoke checks (manual, this sandbox)
Migration applied with `dotnet ef database update` on an empty MySQL 8 database. Web run on a scratch database and driven with headless Chromium (Playwright): login, tenant creation and platform switch, category and lead creation through the forms, duplicate warning, list filters, select-all + confirm dialog + bulk archive/restore, lead tabs, consent recording, board, CSV import wizard with review, suppression/scoring/pipeline/tag pages; desktop (1280 px) and phone (390 px) screenshots inspected, no horizontal overflow on the form, no JavaScript errors after the fixes above.

### Acceptance criteria
| Criterion | Result |
|---|---|
| CRM CRUD works | **Passed** (API and MVC integration tests, browser smoke) |
| Multiple contacts per lead work | **Passed** |
| CSV imports validate and report errors | **Passed** (CSV and XLSX; per-row errors on screen and as CSV) |
| Duplicate leads are detected | **Passed** (create, import, check endpoint) |
| Consent records are auditable | **Passed** (append-only at API, service and database-guard level; each grant/revocation audited; history UI) |
| Suppressed contacts cannot enter eligible campaign audiences | **Passed** (SQL audience query and single-contact gate, including channel-specific and all-channel suppression) |
| Docker environment starts successfully | **Not verified** (unchanged; compose gained a `crm-files` volume and `Crm__StorageRoot`, YAML parses, nothing was run) |

### Outstanding / known limits (documented, not hidden)
- **Import and "recalculate all scores" run inside the HTTP request** (limits: 5 000 rows / 5 MB; 200 leads per scoring batch). They move to Hangfire in Phase 6.
- **Phone validation is format-only** (E.164 shape, 8–15 digits) — no per-country numbering plan and no check that a number exists. Local numbers (leading 0) need the tenant setting `Crm.DefaultCountryCode` (e.g. `966`); otherwise they are reported as invalid, never guessed.
- Duplicate detection ignores e-mail; name+city needs the same (normalised) city text; archived leads are reported as duplicates but never merged into.
- Suppression removal is a hard delete (the audit log keeps the masked value); revoking consent does not auto-create a suppression entry; STOP-keyword handling arrives with messaging (Phase 5).
- Engagement/interest scoring derives from pipeline stage and tags only (conversation signals arrive with Phases 5/8). Search is `LIKE`-based inside a tenant (no full-text index yet).
- `OwnerUserId`/actor ids are validated against tenant membership in the services but have no database FK to `AspNetUsers` (history must survive user removal).
- Bulk "per-item authorization" is tenant ownership + the actor's permission + domain rules; there is no owner-based row-level restriction (e.g. "agents only touch their own leads") because the spec does not define one.
- Uploaded import files live on local disk (`Crm:StorageRoot`, a shared Docker volume) and are not purged yet (retention job in Phase 6).
- The Web host must have started at least once after deploying (it runs the idempotent seeder that adds new permissions to existing tenants' system roles); tenants created afterwards are provisioned at creation.
- Visual check was one browser (Chromium) on desktop and phone widths; no accessibility audit.

### Next
Phase 4 — Discovery and enrichment (done below).


## Phase 4 — Lead Discovery and Enrichment
**Status: Built; every check that could be run here passed (544 automated tests on MySQL 8.0.46: 365 unit, 32 architecture, 147 integration; 0 failed, 0 skipped). NOT verified: live Google Places calls (no API key and no outbound access to Google here) and Docker (inherited from Phase 1). Built on branch `claude/phase-3-crm`, which contains Phases 1–3 (draft PRs #1, #2, #3 are unmerged).**

### 1. Implemented
- **Provider-independent engine**: `IDiscoveryConnector` port (Application) with typed failures; adapters in Infrastructure. Search by country/province/city/district/area + business category + keywords; category phrases and locations are tenant data (`DiscoveryCategoryMappings`, `DiscoveryLocationPresets`) reusing the Phase 3 `LeadCategory`; the "Education — Pakistan" starter pack (Private/Primary/Secondary/Higher secondary schools, Colleges, Academies, Educational institutions + 11 cities) is installed automatically only for Education workspaces or on demand, idempotently, never overwriting edits.
- **Connectors**: (1) **Google Places API (New)** real HTTP adapter (field mask, paging, per-tenant encrypted key, usage tracking, attribution, retention `ExpiresAtUtc`); (2) **public website enrichment + a "Public websites" connector** (seed URLs): robots.txt (RFC 9309 semantics), strict SSRF guard, extraction of name/address/public phone/e-mail/social links only; (3) CSV/Excel import and manual entry listed as in-app connectors that link to the Phase 3 paths; (4) authorized directories, lead-gen integrations and Meta Lead Ads are **documented extension points that answer "not configured" and never return data**.
- **Workflow**: wizard → `DiscoveryJob` (tenant-scoped, permission-gated, usage-limit-checked) → **Hangfire (MySQL) job in the Worker** inside `ITenantScopeRunner` (pagination, rate limit, cancellation between pages, resume checkpoint) → normalisation → duplicate detection with the Phase 3 `DuplicateDetector` → optional website enrichment → source + retrieval time + provenance → documented confidence formula → `DiscoveryResult` (Pending) → human review (approve creates/merges lead + contacts via `ILeadIntakeService`/`ILeadService.CreateFromSourceAsync`; reject; bulk approve/reject up to 100).
- **Never consent**: discovered contacts are `DataOrigin.Public` with no consent record; `IContactEligibilityService` blocks them on every channel/purpose (test proves it, and that recorded consent opens the gate).
- **Management**: jobs list/detail with live progress, failure details with retryable flag, idempotent retry (resumes from checkpoint; unique `(TenantId, ConnectorType, ExternalId)` on results), cancel (queued immediately, running after the current page), rerun, search history, usage ledger with calls and **estimated** cost, per-tenant monthly call limit and optional budget (enforced at creation, retry and before every provider call), retention job purging provider content after its retention period.
- **Permissions**: `Discovery.View/Run/Review/Manage`, `Integrations.Manage` with default role mappings that reach existing tenants through the idempotent role sync (migration test proves it on a Phase 3 database with data).
- **UI + API**: responsive Bootstrap pages (jobs, wizard, job detail with polling progress, results review table with filters/pagination/approve/reject/bulk/confirm dialogs, result detail with provenance, categories & locations, connectors & usage, search history), sidebar entries only for existing pages; REST endpoints under `/api/v1/discovery/*` with XML docs for Swagger and ProblemDetails (409 not configured, 429 limit reached). Hangfire dashboard at `/hangfire`, platform operators only.

### 2. Files created or modified (important ones)
`src/LeadPilot.Domain/Discovery/Discovery.cs`; `src/LeadPilot.Application/Discovery/{Connectors,DiscoveryLogic,Contracts}.cs`, `Crm/{Contracts,Services}.cs`, `Security/Permissions.cs`; `src/LeadPilot.Infrastructure/Discovery/{Connectors,Website,Services,Jobs}/*`, `Discovery/DiscoveryServiceCollection.cs`, `Services/Crm/LeadIntakeService.cs`, `Services/Crm/LeadService.cs` (server-only `CreateFromSourceAsync`), `Persistence/DiscoveryModel.cs`, `Persistence/Migrations/*Phase4Discovery*`, `DependencyInjection.cs`, `LeadPilot.Infrastructure.csproj` (Hangfire, HtmlAgilityPack, pinned MySqlConnector/Newtonsoft/SqlClient); `src/LeadPilot.Worker/{Program,Worker}.cs`; `src/LeadPilot.Api/Controllers/DiscoveryControllers.cs`; `src/LeadPilot.Web/{Controllers/DiscoveryControllers.cs,Models/DiscoveryModels.cs,Views/Discovery*,Views/Connectors,Views/Shared/_Layout.cshtml,Program.cs}`; tests `Discovery*Tests.cs`, `DiscoveryKit.cs`; docs listed at the top of this report.

### 3. Database changes
Migration **`Phase4Discovery`** (earlier migrations untouched): `DiscoveryJobs`, `DiscoveryResults` (**unique (TenantId, ConnectorType, ExternalId)**), `DiscoveryConnectorConfigs` (unique per tenant+connector; key stored as `enc:v1:` ciphertext), `DiscoveryUsageEntries` (append-only), `DiscoveryCategoryMappings` (unique per tenant+category), `DiscoveryLocationPresets`. Hangfire creates its own `Hangfire*` tables at runtime. Applied and checked on (a) an empty MySQL 8 database and (b) a database at the Phase 3 schema holding a tenant, roles, CRM defaults and a lead with a contact (nothing lost; roles gained the permissions); `HasPendingModelChanges` is false. Details: DATABASE_DESIGN.md §3.

### 4. Tests (MySQL 8.0.46, `dotnet test LeadPilotAI.sln`, `LEADPILOT_TEST_CONNECTION` set)
- **Build:** succeeded, 0 warnings (TreatWarningsAsErrors).
- **Unit: 365 passed** (was 200): confidence calculation, result normalisation, category/location query building, robots.txt parsing, SSRF guard (private/loopback/link-local/metadata IPv4+IPv6, IPv4-mapped/NAT64/6to4, DNS-rebinding-style resolution incl. real sockets, redirect to internal addresses, unsafe schemes/ports/credentials), website extraction (unknown stays null, nothing invented, login/noindex pages not read), enricher with robots, usage-limit policy, domain state machines, **Google Places adapter against a local fake HTTP server** (recorded-shape responses: header/field mask/body, mapping, paging, 429/403/400/5xx, garbled body, key scrubbing, no key ⇒ not configured, unreachable), stubs return nothing.
- **Architecture: 32 passed** (was 24): + discovery entities tenant-owned, only the SSRF-safe factory may build clients for website fetching, factory settings, adapters only in Infrastructure, no `Fake*` types in shipped assemblies, Hangfire server only in Worker and dashboard platform-only, jobs go through the tenant scope runner, no `IgnoreQueryFilters` in discovery, permission/verb rules extended to the new controllers.
- **Integration: 147 passed** (was 117): full lifecycle with the TEST-ONLY fake connector (create → run → results with source/retrieval time → approve creates lead with source → reject → bulk), eligibility gate, duplicate detection and merge, retry resumes from checkpoint without duplicates, repeated search/rerun, unique index at the database, provider failures (quota/auth/permanent/unexpected) and bounded automatic retry, cancellation (queued and running), usage limits and cost tracking, not-configured connectors, website enrichment/corroboration, website connector with warnings, retention purge, starter pack/education auto-install/editability, permissions 401/403 on every endpoint for five roles, tenant isolation (API, MVC, background execution, credentials, spoofed TenantId, database guard on every new entity), MVC pages and POST-only/antiforgery rules, Hangfire dashboard authorization, **real Hangfire server on MySQL** (enqueue → execute → results; fail-closed without tenant; foreign payload no-op; recurring job registered; Worker composition validated), migrations on a clean database and on a Phase 3 database with data.
- **Failed: 0. Skipped: 0. Not executed:** nothing live — see "Not verified" below.
- **Browser check** (Playwright + Chromium, desktop 1280 px and phone 390 px, real app on a scratch MySQL database, platform operator in an Education workspace): jobs, wizard, results, categories & locations, connectors, history all returned 200 with correct titles, no console errors and **no horizontal scroll at 390 px**; the education starter pack auto-installed (7 categories, 7 mappings, 11 locations); select-all showed "3 selected"; the reject confirmation dialog appeared with its text; the failed-job page showed the error, "can be retried" text and a Retry button; the result detail showed the approve form and provenance labels; `/hangfire` opened for the platform operator. **Rows shown in the results/job pages were inserted by SQL for this render check** (labelled "RENDER CHECK ROW"); no live provider produced them. Not checked: other browsers, screen readers, the live-progress polling in a real running job (covered by status-endpoint tests, not visually).

### 5. Security and architecture review
SSRF, robots, credential handling, tenant isolation and compliance rules are described in SECURITY.md, MULTI_TENANCY.md and INTEGRATIONS.md (Phase 4 sections). Notable: the HTTP handler pins the connection to the validated address (no rebinding window); redirects re-validated per hop; keys encrypted, hinted, scrubbed from errors and absent from DTOs/audit; discovery writes no consent; the REST API still cannot create `Discovery`-sourced leads; Hangfire carries ids only and fails closed.

### 6. Outstanding issues / decisions for the owner
1. **Google live verification** — needs a key and network; the adapter is only proven against a local fake of the documented response shape. First live run should be small (maxResults 5) while watching *Connectors & usage*.
2. **Google terms for approved leads** — the review copy of provider content is purged after 30 days, but an approved lead keeps its copy (INTEGRATIONS.md explains the open question and options). The current Places policies page could not be retrieved here; the summary is from archived Service Specific Terms and secondary sources.
3. **Cost figure is an estimate** (0.035 USD/call default from a third-party summary); set it from Google's price list.
4. `Hangfire.MySqlStorage` 2.0.3 is old and unmaintained (works on .NET 10 with the pins; see ARCHITECTURE.md). RabbitMQ intentionally not added (explained there); it arrives with Phase 5.
5. The Worker must share the Data Protection key ring with Web (compose already mounts `dp-keys`), or connector keys cannot be decrypted. Put a real contact URL into `Discovery:Website:UserAgent`.
6. Website extraction reads schema.org JSON-LD, `tel:`/`mailto:` links and written-out e-mail addresses only: sites that publish contact details as images or via scripts yield nothing (by design, nothing is guessed). E-mail addresses found in page text may be personal; they are marked unverified and are never consent.
7. Country → region code is a small lookup (~25 countries); unknown countries simply send no region bias. Phones from websites need `Crm.DefaultCountryCode` to normalise local numbers, otherwise they stay on the result as text and are not copied to the lead's contact.
8. Search history groups the last 1000 searches in memory; fine for now.
9. Docker still unverified (no daemon); compose was not changed in this phase.

### 7. Acceptance criteria (README)
| Criterion | Result |
|---|---|
| A user can search for schools in a selected Pakistani city | **Passed with the fake connector** (end-to-end integration test: school category + Lahore, background job, results, approval into the CRM) and with the real Google adapter against a local fake server. **Live Google: NOT verified** (no key, no network). |
| Results are saved with source information | **Passed** (source name, URL, attribution, retrieval time, expiry, provenance, confidence on the result; `LeadSource` of type Discovery with external id, attribution and retrieval time on the lead). |
| Duplicate records are not unnecessarily created | **Passed** (DB unique key on results; repeated/retried/rerun searches add 0 results; duplicates against existing leads are flagged and merged by default; "create anyway" is explicit). |
| Failed jobs can be retried safely | **Passed** (resume from checkpoint, no duplicate results or leads, limit/permission checks on retry, bounded automatic retry). |
| Data source rules are respected | **Passed for what exists** (robots.txt, SSRF protection, public pages only, no protected platforms, no invented facts, unverified labels, no consent, stubs return nothing). Provider-terms compliance for retained Google content is partly an open owner decision (item 2). |

### Next
Phase 5 — WhatsApp gateway (WAHA) adapter, conversations, webhooks, outbox/RabbitMQ. Do not start before the owner approves Phase 4.
