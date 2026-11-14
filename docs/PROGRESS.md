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
1. **Google live verification: NOT verified.** On 2026-11-13, after you asked for it, I ran the real adapter (`tools/LiveGoogleCheck`) through the sandbox proxy with a dummy `X-Goog-Api-Key`, expecting the proxy to substitute the Network secret for `places.googleapis.com`. Three requests (`private school in Lahore, Punjab, Pakistan`, region PK, pageSize 5, spaced over several minutes) reached Google and each answered **HTTP 400 "API key not valid. Please pass a valid API key."**, so the dummy value was not replaced (secret not active in this sandbox, not matching host/header, or not a valid key). I could not inspect the environment's secrets (permission denied) and did not work around that. Nothing was returned or stored. The runbook is in DEVELOPMENT.md; with a working key the check is one request.
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
| A user can search for schools in a selected Pakistani city | **Passed with the fake connector** (end-to-end integration test: school category + Lahore, background job, results, approval into the CRM) and with the real Google adapter against a local fake server. **Live Google: NOT verified** (live attempt reached Google but got HTTP 400 "API key not valid"; see item 1 below). |
| Results are saved with source information | **Passed** (source name, URL, attribution, retrieval time, expiry, provenance, confidence on the result; `LeadSource` of type Discovery with external id, attribution and retrieval time on the lead). |
| Duplicate records are not unnecessarily created | **Passed** (DB unique key on results; repeated/retried/rerun searches add 0 results; duplicates against existing leads are flagged and merged by default; "create anyway" is explicit). |
| Failed jobs can be retried safely | **Passed** (resume from checkpoint, no duplicate results or leads, limit/permission checks on retry, bounded automatic retry). |
| Data source rules are respected | **Passed for what exists** (robots.txt, SSRF protection, public pages only, no protected platforms, no invented facts, unverified labels, no consent, stubs return nothing). Provider-terms compliance for retained Google content is partly an open owner decision (item 2). |

### Next
Phase 5 — WhatsApp gateway (WAHA) adapter, conversations, webhooks, outbox/RabbitMQ. Do not start before the owner approves Phase 4.


## Phase 5 — WhatsApp Gateway Integration (WAHA)
**Status: Built; every check that could be run here passed (see §4 for counts). NOT verified: a live WAHA instance (none available, no owner credentials), Docker/compose (no daemon).**

### 1. Implemented
- **WAHA docs read first** (https://waha.devlike.pro, 2026-11-13) and recorded in INTEGRATIONS.md *before* the adapter was written: endpoints, `X-Api-Key`, webhook HMAC-SHA512 (`X-Webhook-Hmac`), ack values, engines, Core/Plus, error 463/475, `@lid`, and six ambiguities with the defensive choice for each.
- **Provider-independent port `IWhatsAppGateway`** + single adapter `WahaGateway` (SSRF-safe HTTP client, circuit breaker, typed failures, reads retried, **sends never retried**). Architecture tests fail if WAHA specifics appear outside the adapter.
- **13 entities / tables**: Channel, ChannelAccount, ChannelCredential, Conversation, ConversationAssignment, Message, MessageAttachment, MessageStatusEvent, WebhookEvent, MessageTemplate, OutboxMessage, ProcessedMessage (+ ChannelAccountNotification). Migration `Phase5Messaging`.
- **Inbound:** `POST /api/v1/webhooks/waha/{key}` → secret key + HMAC verification → tenant/account resolution → normalise → ONE transaction (WebhookEvent + Conversation + Message + OutboxMessage) → Hangfire outbox dispatcher → RabbitMQ (publisher confirms, topology with retry queues and DLQs) → idempotent Worker consumers (tenant scope first; missing/forged TenantId ⇒ DLQ) → conversation update, contact link by E.164, opt-out detection (STOP / UNSUBSCRIBE, English/Roman Urdu/Urdu) with suppression + consent revocation, and an `ai.reply.requested` extension event (no AI logic; Phase 7/8).
- **Outbound:** eligibility gate (24 h service window vs consent-gated marketing, suppression/opt-out), per-account warm-up, hourly/daily caps and minimum gap, queued message with idempotency key, Hangfire send job (`AutomaticRetry` 0) with an atomic `Queued→Sending` claim, acks applied monotonically (Sent < Delivered < Read), reconcile of unknown outcomes (never auto-resent), sweep for lost jobs, auto-pause (3 consecutive failures, session FAILED/STOPPED, unconfirmed delivery, 463/475 blocks new contacts).
- **Policy/ban-risk:** conservative defaults and hard ceilings, visible warning on the account pages, docs in SECURITY.md / INTEGRATIONS.md.
- **Permissions:** `Channels.View`, `Channels.Manage`, `Messages.Send`, `Webhooks.View` added to the catalog and role defaults (existing tenants receive them when Web starts).
- **UI + REST API:** accounts (add/test/connect/QR/pause/resume/credentials/delete), minimal inbox (list, thread, reply, mark read, close/reopen, assign), templates, webhook events, platform-only dead-letter page; matching `/api/v1` controllers.
- **Docs updated:** INTEGRATIONS, ARCHITECTURE, DATABASE_DESIGN, MULTI_TENANCY, SECURITY, IMPLEMENTATION_ROADMAP, DEVELOPMENT (owner runbook), this file.

### 2. Files created or modified (important ones)
`src/LeadPilot.Domain/Messaging/*`; `src/LeadPilot.Application/Messaging/{Gateway,Rules,Contracts}.cs`, `Security/Permissions.cs`; `src/LeadPilot.Infrastructure/Messaging/{Waha,Broker,Services,Jobs}/*`, `Messaging/{MessagingOptions,MessagingServiceCollection}.cs`, `Persistence/{MessagingModel,AppDbContext}.cs`, `Persistence/Migrations/*Phase5Messaging*`, `Discovery/{DiscoveryServiceCollection,Jobs/HangfireIntegration}.cs`, `DependencyInjection.cs`, `LeadPilot.Infrastructure.csproj` (RabbitMQ.Client 7.2.2); `src/LeadPilot.Api/{Controllers/MessagingControllers.cs,ApiControllerBase.cs,Program.cs}`; `src/LeadPilot.Web/{Controllers/MessagingControllers.cs,Models/MessagingModels.cs,Views/{Channels,Conversations,Templates,Webhooks,DeadLetters}/*,Views/Shared/{_WhatsAppRisk,_Layout}.cshtml}`; `docker/docker-compose.yml` (rabbitmq service, env vars); appsettings.Development.json (3 hosts); tests `Messaging*Tests.cs`, `FakeWahaServer.cs`, `MessagingKit.cs`, `Infrastructure.cs`.

### 3. Database changes
Migration **`Phase5Messaging`** (earlier migrations untouched): 13 tables, all tenant-owned with FK, query filter and cross-tenant FK guard. Global uniques: `ChannelAccounts.GatewayAccountId`, `WebhookKey`; per-tenant uniques on message gateway id, idempotency key, template name, conversation chat id, processed-message consumer key. Applied and checked on (a) an empty MySQL 8 database and (b) a database at the Phase 4 schema holding data (nothing lost; roles gained the new permissions); `HasPendingModelChanges` is false. Details: DATABASE_DESIGN.md §4.

### 4. Tests (MySQL 8.0.46 and RabbitMQ 3.12.1 locally, `dotnet test LeadPilotAI.sln`)
- **Build:** succeeded, 0 warnings (TreatWarningsAsErrors).
- **Unit: 685 passed** (was 365): HMAC-SHA512 signature, webhook processing and normalisation (incl. non-numeric ack), error classification (463/475, timeouts, 5xx), endpoint/SSRF policy, circuit breaker, opt-out detector, ack monotonicity, rate/warm-up policy, chat ids, idempotency keys, template renderer, outbound policy, `MessageProcessor` ack/retry/DLQ decisions, RabbitMq options regression.
- **Architecture: 41 passed** (was 32): messaging entities tenant-owned, WAHA specifics only in the adapter, sends never retried, jobs use the tenant scope runner with no automatic retry, `IgnoreQueryFilters` allow-list, no secrets in DTOs, permission/verb rules for the new controllers.
- **Integration: 244 passed** (was 147; run with `LEADPILOT_TEST_CONNECTION` and `LEADPILOT_TEST_RABBITMQ` set): inbound flow end to end, duplicate/concurrent webhook storage, outbound (gate, caps, warm-up, idempotency, no duplicate on retry, unknown-outcome reconcile, acks), opt-out and consent, security (bad/missing HMAC, foreign session, suspended tenant, size cap, credential swap), permissions for five roles, tenant isolation (API, MVC, database guard on every new entity, webhooks, consumers), outbox durability, **real RabbitMQ** (confirms, retry queues, DLQ, forged/missing TenantId), Phase 4→5 migration with data and on a clean database, MVC pages.
- **Failed: 0. Skipped: 0.** One test assertion was wrong on the first full run (it counted deliberate wrong-key calls from other tests on the shared fake); the assertion was corrected and the full suite re-run green.
- **Browser check** (Playwright + Chromium, 1280 px and 390 px, real Web app on a scratch MySQL database, platform operator switched into an Education workspace): accounts, add-account form, account details, credentials, conversations, conversation thread, templates, new template, webhook events and dead-letter pages all returned 200 with correct titles, **no console errors and no horizontal scroll at either width**; the ban-risk warning shows on the account pages; connection test against an unreachable address showed a clear error. A scratch stand-in answered the connection test, and **the conversation, message and webhook-event rows were inserted by SQL** (labelled "RENDER CHECK") — no real WAHA produced them. The thread correctly said replies are blocked because the account is not connected. Not checked: other browsers, screen readers, QR display, live inbox refresh.

### 5. Security and architecture review
See SECURITY.md, MULTI_TENANCY.md and ARCHITECTURE.md (Phase 5 sections): two-secret webhook authentication, SSRF-safe adapter, credentials bound to tenant+account, consumer tenant scope, platform-only DLQ, no-duplicate-send design.

### 6. Outstanding issues / decisions for the owner
1. **Live WAHA: NOT verified.** No instance or credentials were available. The adapter follows the documentation and passes against a fake we wrote from it; run the manual live check in DEVELOPMENT.md and report differences (response shape of `sendText`, ack timing, `@lid` ids, session update verb).
2. **Ban/policy risk is inherent to WAHA.** Use a dedicated number; consider the official Business Platform for volume (second adapter behind the same port).
3. **WAHA does not sign the webhook timestamp**, so replays are possible but harmless (idempotent). Keep the endpoint on HTTPS.
4. **Unconfirmed sends are never re-sent automatically**; after 10 minutes unconfirmed they are marked failed and the account is paused, a person decides.
5. Attachments are metadata only; content download/storage and sending media are later work. Groups, status and channels are ignored by design.
6. Webhook payloads are stored for a retention period then purged (row kept); AI behaviour is only the `ai.reply.requested` event.
7. `Messaging:PublicWebhookBaseUrl` must be set to a URL WAHA can reach, and the Worker must share the Data Protection key ring with Web.
8. **Docker still unverified** (no daemon); compose gained a `rabbitmq` service and env vars that were only YAML-validated. The Phase 4 live Google check is untouched.

### 7. Acceptance criteria
| Criterion | Result |
|---|---|
| Gateway connection works using actual documentation | **Passed against the fake only** (written from the docs); **live: NOT verified** |
| Incoming messages are stored once and update conversations | **Passed** (duplicates and concurrent deliveries tested, incl. real RabbitMQ) |
| Outgoing messages queued with idempotency, no duplicates on retry | **Passed** (atomic claim, no adapter retry, reconcile) |
| Delivery status updates applied monotonically | **Passed** |
| Opt-out stops further sends | **Passed** |
| Tenant isolation for all new data, webhooks and consumers | **Passed** |
| Failed/forged events visible and dead-lettered | **Passed** |
| Ban risk documented with conservative defaults and UI warning | **Passed** |

### Next
Phase 6 — campaigns: done, see below.

## Phase 6 — Campaign management and marketing automation
**Verified against the fake WAHA only.** No live WAHA instance and no live Google Places key were available; no real message was sent. Docker was never verified (no daemon).

### What was built
- **Campaign types**: Manual (started by hand), Scheduled (UTC start, tenant zone recorded), FollowUp and ReEngagement (audience derived from a parent campaign's outcomes, or from inactivity filters). `ICampaignChannel` is the channel abstraction; **WhatsApp is the only implementation** (no placeholder e-mail channel).
- **Entities** (all tenant-owned, indexed `(TenantId, …)`): `Campaign` (state machine Draft → PendingApproval → Scheduled → Running ⇄ Paused → Completed/Cancelled/Failed, validated in `CampaignStateMachine`), `CampaignAudienceSnapshot`, `CampaignRecipient` (unique `(CampaignId, ContactId)`; eligibility decision at selection **and** at send time as JSON; `Round` for the idempotency key), `CampaignExecutionLog` (append-only), `CampaignStatsRollup` (read model, always recomputed), `CampaignConversion`, `CampaignUsageEntry` (append-only monthly ledger), `CampaignTenantSettings` (cool-down, approval policy, attribution window, failure threshold, opt-out text, monthly limit, test numbers, kill switch). Migration `Phase6Campaigns` (new; earlier migrations untouched). `Message` gained `CampaignId` / `CampaignRecipientId`.
- **One send pipeline**: the campaign runner hands each recipient to the Phase 5 `OutboundQueueService` (forced `Marketing` purpose, even inside a service window) with the idempotency key `cmp:{campaign}:{recipient}[:round]`; the Phase 5 `OutboundSendRunner` still does pacing, caps, warm-up, the atomic claim, reconcile. Added hooks only: `ICampaignSendGuard` before the gateway call, `ICampaignEvents` after sent/failed/ack/inbound/stage change/account trouble.
- **Marketing permission is re-validated three times**: audience selection, immediately before each recipient is queued (runner), and immediately before the gateway call (guard + the existing Phase 5 check). Tests prove it for consent revoked, suppression added, lead archived, contact edited, contact removed, and an opt-out arriving through the Phase 5 inbound flow mid-run.
- **Execution**: Hangfire only (`CampaignJob` start/tick on queue `campaigns`, `CampaignSweepJob` every minute for crash recovery and parent-completion timers). One tick = one batch, lease-protected, followed by a random pause between the campaign's bounds (ban-risk mitigation, not evasion). RabbitMQ/outbox: `campaign.recipient` / `campaign.status` events are consumed by `campaigns.events` (new queue) which recomputes the rollup idempotently.
- **Controls**: approval workflow (`Campaigns.Approve`, optional four-eyes), pause/resume/cancel (queued messages withdrawn atomically, a message already `Sending` finishes), auto-pause on account auto-pause/disconnect/removal, failure-rate threshold, monthly usage limit, per-campaign daily limit, kill switch, test-send to an allow-listed number (labelled, through the normal pipeline, not part of statistics), dry run.
- **Audience**: versioned JSON definition (status, stage, category, location, tags in/out, score range, source, owner, last contacted, never contacted, has reply, explicit contacts, one-per-lead, follow-up rule); translated to one SQL query and streamed in pages of 200 (scan limit 20 000); eligibility through the Phase 3 gate (`IContactEligibilityService.CheckManyAsync`, same `EligibilityRules`); exclusion breakdown with one primary reason per contact.
- **Content**: shared `TemplateRenderer` extended (fallback `{{first_name|there}}`, `city`, `country`, brace validation, `Sanitize`); missing-variable policy: inline fallback or the recipient is skipped (`missing_variable`); values stripped of braces, formatting marks, control/bidi characters, 100-char limit; opt-out text per tenant, required to contain a word the opt-out detector understands.
- **UI + API**: Campaigns list/wizard/details (recipients, log, conversions)/stats/dry-run, Campaign settings with kill switch; `/api/v1/campaigns/*` with XML docs and ProblemDetails. Permissions added: `Campaigns.Update/Approve/Cancel/Delete/Settings`, `Audiences.Preview` (existing tenants receive them when Web starts).

### Tests (actual results, MySQL 8.0.46 + RabbitMQ 3.12.1, `dotnet test`, clean build 0 warnings 0 errors)
| Suite | Result |
|---|---|
| Unit | **795 passed** (was 685): state machine (every allowed and every refused transition), recipient sync rules, audience definition (normalise, version, hostile JSON, validation), exclusion precedence, content building incl. hostile values, pacing/batch/cap math, failure-rate, attribution window, follow-up rules, idempotency keys |
| Architecture | **53 passed** (was 41): entities tenant-owned, only the Phase 5 runner calls `IWhatsAppGateway.Send*`, campaign code never touches the gateway/WAHA/`HttpClient`, no timers/threads/other schedulers, Hangfire jobs declare queue + bounded retry, one channel implementation and no fakes shipped, permission + verb on every action, mutations POST-only, new permissions in catalog/roles |
| Integration | **308 passed** (was 244), 0 skipped: lifecycle with approval/four-eyes/scheduling; send-time re-validation (7 cases + opt-out mid-run); no duplicates (repeat ticks, repeat send jobs, crash after queueing, restart with a new worker, gateway "sent but errored" + reconcile); pause/resume/cancel; caps, warm-up (20 on day one), campaign daily limit; account auto-pause, FAILED session, manual account pause; failure threshold; kill switch (and isolation between tenants); monthly usage limit; follow-up/re-engagement/delayed follow-up; cool-down; filters; test-send; dry run; settings; stats == raw rows == rollup (with late/out-of-order acks); 401/403/404 matrix for 5 roles × 24 endpoints; tenant isolation (API, MVC, jobs, consumer header, DB guard on all 8 new entities, spoofed `tenantId`); MVC pages incl. XSS encoding and antiforgery; **real Hangfire server** running a campaign end to end; **real RabbitMQ** campaign events repairing the rollup; migration on a clean DB and on a Phase 5 DB with data |
| Failed / skipped | none / none |
| Not executed | Docker build/compose; live WAHA; live Google Places; load/concurrency tests beyond the duplicate-prevention tests; cross-browser/accessibility audits |

### Playwright render check (Chromium, scratch database)
Pages checked at 1366 px and 390 px: campaign list, new-campaign wizard (live preview worked in the browser), details with the recipient/log/conversion tabs, statistics, settings. All answered 200, **no horizontal overflow at either width**, status badges, progress bar, breadcrumbs and confirm buttons rendered; on the phone the wide tables scroll inside their card and the audience preview sits below the form. The details/stats pages were populated with **throwaway rows written by SQL into a scratch database** (a campaign cannot really run without a WAHA); nothing like that exists in the product.

### Acceptance criteria
| Criterion | Result |
|---|---|
| Campaign scheduling works | **Passed** (Hangfire start job at the UTC time; real Hangfire server run; delayed follow-ups via the sweep) — gateway part: fake WAHA only |
| Campaign execution is recoverable | **Passed** (lease expiry + sweep, restart with a new worker, interrupted snapshot, crash between queue and link) — fake WAHA only |
| Suppressed contacts are never sent marketing messages | **Passed** (asserted on the fake WAHA's received requests, at selection and at send time) |
| Campaign status and statistics are accurate | **Passed** (stats, rollup and raw rows reconcile) |
| Retries do not create duplicate messages | **Passed** (one message per recipient and round; fake WAHA request counts) |

### Outstanding / known limits (not hidden)
1. **Live WAHA never exercised** (ack timing, `@lid` ids, real send errors). Run the owner runbook in DEVELOPMENT.md before trusting campaigns with real numbers.
2. The audience snapshot is taken when the campaign **starts**, not when it is approved; the approver sees the definition and a preview, not a frozen list. Re-validation at send time covers the drift.
3. "Replied but no demo" is modelled as "replied and not in the stages you mark as advanced" (there is no Demo entity before Phase 10).
4. A stage change counts as a conversion only for non-Lost/non-Suppressed stages, last touch inside the window; opt-outs are never counted as replies.
5. Message `CampaignRecipientId` has no database FK (it would make Messages ↔ CampaignRecipients circular); `CampaignId` has one. Only server code writes it.
6. Monthly usage counts messages that left (ledger written on the first Sent), minus nothing for withdrawn ones; in-flight queued campaign messages are subtracted from the remaining allowance.
7. Scan offsets page by `OFFSET`, adequate to the 20 000 limit; a huge audience would need keyset paging.
8. Unknown outcomes (`Unknown` messages) are never re-sent: the recipient stays Queued until reconcile decides (Sent or Failed), as in Phase 5.

### Next
Phase 7 — AI agent (Claude API). Not started. Merge order: PRs #1–#5 first, then this one.


---
# Phase 7 — AI engine and knowledge base

**Status: Completed against test doubles; live Claude, live Voyage and a real Qdrant are NOT verified.**

### Implemented
- Ports `IAIProvider`, `IEmbeddingProvider`, `IVectorStore`, `IKnowledgeRetriever`, `IConversationMemory`, `IAIOrchestrator`; adapters for Claude (Messages API, prompt caching, retries/timeouts/circuit breaker, usage metering), Voyage AI, a MySQL-backed vector store (default) and Qdrant; per-tenant encrypted keys.
- Knowledge base: sources (upload, written text, website via the Phase 4 SSRF-safe fetcher), documents, immutable versions, passages, embeddings; txt/md/html/pdf/docx extraction with safe limits; Hangfire indexing with atomic claim, retry, stale takeover and sweep; re-index, disable, delete (removes vectors).
- RAG workflow on the `ai.reply.requested` event (new queue `{prefix}.ai`): history + rolling summary, deterministic triage, intent/query call, tenant-isolated retrieval, quarantine of instruction-like passages, grounded generation, deterministic validation, permitted actions (`escalate_to_human`, `add_lead_note`) with a tool log, stored as a staff-visible suggestion. **Nothing is ever sent by the AI** (architecture test: the AI module cannot reach the send pipeline).
- UI + API: knowledge base (sources, documents, versions, passages, re-index, delete, confirm dialogs, empty states, breadcrumbs), retrieval tester, AI settings, AI usage, AI activity, suggestion panel on the conversation page; `/api/v1/knowledge/*` and `/api/v1/ai/*`.
- Permissions `Knowledge.View/Manage`, `Ai.View/Test/Settings` with default role mappings that reach existing tenants (migration test on a Phase 6 database).
- First evaluation harness (12 sales scenarios, JSON format for Phase 8's 100) with a live mode behind explicit environment variables.

### Files (important ones)
`src/LeadPilot.Domain/Ai/Ai.cs`; `src/LeadPilot.Application/Ai/{Ports,Contracts,Logic,Options}.cs`; `src/LeadPilot.Infrastructure/Ai/{Providers,Stores,Extraction,Services,Jobs}/*`; `Persistence/AiModel.cs` + migration `Phase7AiKnowledge`; `Messaging/Broker/Topology.cs` (AI queue); `Api/Controllers/AiControllers.cs`; `Web/Controllers/AiControllers.cs`, `Models/AiModels.cs`, `Views/{Knowledge,AiTester,AiSettings,Ai}/*`, `Views/Shared/_AiSuggestion.cshtml`; tests `Ai*Tests.cs`, `AiKit.cs`, `QdrantVectorStoreTests.cs`, `Evaluation/sales-scenarios.json`; docs listed in the PR. One Phase 5 test (`The_ai_extension_event_is_published_only_for_active_conversations…`) was updated because the event now has a consumer.

### Database changes
Migration `Phase7AiKnowledge` (earlier migrations untouched): 11 tenant-owned tables, uniques for one AI run per inbound message, document versions, passages, embeddings per model, one settings row per tenant, one summary per conversation; append-only `RetrievalLogs`, `AiUsageEntries`, `AiToolExecutions`. Verified on an empty MySQL 8 database (no pending model changes) and on a Phase 6 database with data. Details: DATABASE_DESIGN.md.

### Tests (MySQL 8.0.46 + RabbitMQ 3.12.1, `dotnet test LeadPilotAI.sln`)
| Suite | Result |
|---|---|
| Build | succeeded, 0 warnings, 0 errors |
| Unit | **930 passed** (was 795): chunker, prompt guard (15 attacks, 8 benign), sensitivity rules, response validator (price/percent/discount/completion/leak/link/contact), model-output parsing, Claude/Voyage request+response shapes, breaker, HTTP pipeline (429/5xx/401/timeout/connection), extractors (docx XXE and zip bomb, pdf limit, type-by-content), Qdrant adapter incl. tenant filter on every request and a server that ignores filters |
| Architecture | **70 passed** (was 53) |
| Integration | **368 passed, 1 skipped, 0 failed** (was 308): ingestion/versions/re-index/delete/disable/website sync/SSRF; RAG end to end through the real webhook, consumers and orchestrator; no invented prices/discounts/demo or payment claims; injection in customer text and in documents; unauthorized actions denied and logged; history, summary, stale and duplicate events; provider failures and retries; usage ledger and limits; retrieval isolation with identical text in two tenants, adversarial cross-tenant prompts, tampered vector store, forged ids on API/MVC/jobs/consumer, DB guard on all 11 entities; permission matrix for 6 roles; MVC pages; real Claude/Voyage adapters against a local server; real Hangfire server; migrations; evaluation skeleton (12/12 with the scripted model) |
| Skipped (not passed) | `LIVE_the_skeleton_suite_against_the_real_claude_and_voyage_services` (needs `LEADPILOT_EVAL_LIVE=1` and real keys) |
| Not executed | live Claude, live Voyage, real Qdrant (a binary was downloaded but the sandbox refused to run it), Docker build/compose, live WAHA, load tests |

### Playwright render check (Chromium, scratch database)
Knowledge base (empty and with documents), add (text/upload/website), document, retrieval tester (error state), AI settings, usage, activity at 1366 px and 390 px: all 200, no horizontal overflow, breadcrumbs present; HTML in a document title is shown encoded. The conversation suggestion panel was not screenshotted (it needs a connected WhatsApp account); its HTML is covered by integration tests. In the tester check a real request with a throwaway key went to Anthropic and returned 401 (no cost).

### Acceptance criteria
| Criterion | Result |
|---|---|
| AI answers from tenant-specific knowledge | **Passed** (scripted model, local provider server): answers quote and cite the tenant's own passages; prices only from cited passages. Real model quality not verified |
| Unsupported questions trigger an appropriate response | **Passed**: configured safe reply, no model call, escalation |
| Tenant isolation works for retrieval | **Passed** (store, retriever, API, MVC, jobs, consumer, DB guard) — MySQL store; Qdrant only against a fake |
| AI cannot execute unauthorized actions | **Passed**: two permitted actions, all others denied and logged; the AI cannot reach the send pipeline |
| Conversation history is maintained correctly | **Passed**: ordered, bounded, failed messages excluded, rolling summary, no cross-conversation leakage |

### Outstanding / known limits
1. Live Claude/Voyage never run (runbook in DEVELOPMENT.md); `ServerSideFallback` stays off until then.
2. Qdrant adapter verified only against a local fake; the MySQL store is brute force (fine up to a few thousand passages, refuses beyond 20,000).
3. Validation proves numbers, offers, actions and links are backed by cited text, not that every sentence is true; a false statement inside the tenant's own document is "grounded".
4. PDFs that are scans need OCR (refused with a clear message). Website sources read ≤ 10 pages of one host.
5. Per-agent model choice, automatic sending, demo scheduling and trial creation are Phase 8.
6. Owner decisions taken by default: docs/OWNER_DECISIONS.md.


---
# Phase 8 — AI sales agent and controlled tool calling

**Status: Completed against test doubles; live Claude is NOT verified; demo, trial and payment services do not exist yet (Phase 10), so those tools honestly answer "not available".** No real message was sent and no money spent.

### Implemented
- **Configurable agent framework** (tenant data): `SalesAgent` (instructions, enabled tools, model/confidence overrides, opt-in), qualification questions, product/pricing catalog (`Confirmed` flag; the AI only sees confirmed, active entries). **School ERP is a SAMPLE** created by a button: `[PLACEHOLDER]` text only, nothing confirmed, so the agent says it does not know.
- **Controlled tools** (`AgentToolRunner`): GetProductInformation, GetPricingPlans, GetLeadProfile, UpdateLeadStatus, QualifyLead (the spec's QualifySchool, product-agnostic), CheckDemoAvailability, BookDemo, ScheduleFollowUp, CreateTrialAccount, CreatePaymentRequest, TransferToHuman, StopMarketingMessages (+ AddLeadNote from Phase 7). Allow-list, schema validation, tenant/lead/conversation from server context only (forbidden arguments refused), no SQL/query tool, one append-only execution row (inputs, outputs, outcome, approval link), replay on redelivery.
- **Approvals:** payment requests always; discounts above the plan limit, custom terms, trials over the limit. Atomic decision, expiry, honest execution state ("not carried out: service not connected"). No tool activates a subscription or confirms a payment.
- **Handoff:** takeover, AI pause/resume, assignment, internal notes, status, escalation reason, handoff history; a staff reply takes control; AI epoch makes takeover/pause during an in-flight generation cancel the reply; queued AI replies are withdrawn; the send runner re-checks before the atomic claim.
- **AI reply sending:** OFF by default (workspace switch + agent switch), only via `IAiReplySender` into the Phase 5 pipeline, only as an answer inside the service window, per-conversation daily cap; AI cannot start a conversation or send marketing. Opt-out by meaning runs StopMarketingMessages (classifier ≥ 0.8).
- **UI + API:** Sales agent, Product catalog, Approvals, Tool log, Follow-ups, inbox controls; `/api/v1/agents|catalog|approvals|tool-executions|follow-ups` and conversation takeover/pause/resume/notes/history. Multilingual (Urdu, Roman Urdu) sensitivity rules added.

### Tests (MySQL 8.0.46 + RabbitMQ 3.12.1, `dotnet test LeadPilotAI.sln`, build 0 warnings 0 errors)
| Suite | Result |
|---|---|
| Unit | **985 passed** (was 930): tool arguments/catalog, agent-output parsing, validator with tool results and verified completions, multilingual sensitivity, injection signals, domain rules |
| Architecture | **84 passed** (was 70): 13-tool catalog (no activate/query/delete/tenant parameters), single AI sender, no fake Phase 10 ports, permission + verb on every action, tenant-owned entities |
| Integration | **421 passed, 1 skipped, 0 failed** (was 368/1): tools one by one, approvals (concurrent approve, reject, expiry), conversation flows (opt-in, takeover races, withdraw, window, cap, opt-out, injection), **196 scenarios** (en 92 / ur 43 / ru 61), security (401 matrix, 6-role permission matrix, API/MVC/consumer/DB-guard isolation), migration on a clean DB and on a Phase 7 DB with data |
| Skipped (not passed) | `LIVE_the_skeleton_suite_against_the_real_claude_and_voyage_services` |
| Not executed | live Claude/Voyage/Qdrant, live WAHA, Docker, load tests |

Phase 7 tests changed on purpose: tool names are canonical in the log, the unknown-tool reason is `not_a_permitted_tool`, and the history test resumes the AI after staff replies.

### Playwright render check (Chromium, scratch DB)
Sales agent (empty and with the sample), agent edit, tool log, approvals, catalog, product, follow-ups at 1366 px and 390 px: all 200, no horizontal overflow, breadcrumbs present. The inbox controls are covered by integration tests (HTML asserted), not screenshotted (needs a WhatsApp account).

### Acceptance criteria
| Criterion | Result |
|---|---|
| AI can qualify a school using verified information | **Passed** (scripted model): answers validated against the tenant's questions; catalog facts only when confirmed |
| AI can retrieve actual pricing | **Passed** (scripted model): price only from the confirmed catalog via the tool; an invented or unconfirmed price is refused |
| AI can book a demo only through an authorized tool | **Passed** with a test double for the scheduler: only available slots, claims only after success; **real scheduling: Phase 10** |
| Human staff can take over | **Passed** (UI, API) |
| No further automatic replies while human control is active | **Passed**, incl. takeover/pause during a generation and queued replies |

### Outstanding / known limits
1. Live Claude never run (runbook in DEVELOPMENT.md): the JSON tool protocol is unverified against the real model.
2. Demo/trial/payment: ports only; honest "not available".
3. The validator proves numbers/offers/actions are backed by tool results or knowledge, not that every sentence is true.
4. No automatic acknowledgement on escalation (owner decision 14). A message already `Sending` when a person takes over still finishes.
5. Decisions taken by default: docs/OWNER_DECISIONS.md items 11–20.

### Next
Phase 9 (automation workflow engine). Not started.

---

## Phase 9: Automation workflow engine

**Status: completed against fakes and real infrastructure in the sandbox.** Tested with real MySQL 8, real RabbitMQ, real Hangfire-independent engine runs and the test-only fake WAHA. **Never run against a live WAHA, live Claude/Voyage or a real Qdrant; Docker never verified (no daemon).** No real message was sent and no money spent.

### 1. Implemented
- Workflow, immutable WorkflowVersion (draft → active → retired; steps/triggers sealed on activation, enforced by the save guard), WorkflowTrigger, WorkflowStep, WorkflowExecution (unique per version + event + lead), append-only WorkflowExecutionLog, plus WorkflowTenantSettings (workspace kill switch and limits) and WorkflowNotification.
- Triggers from real outbox events: lead created (manual, import), lead enriched (discovery merge), contact eligible (WhatsApp marketing consent that really makes the contact eligible), incoming WhatsApp message, customer replies, lead interested (stage "Interested"), lead inactive (hourly Hangfire evaluation). Demo booked/completed, trial created/expires, payment confirmed are declared and wired to the contract, shown as "available when Phase 10 ships", and have no producer.
- Actions: assign lead, change status, change stage, send approved message, wait, create follow-up task, notify (in-app notice), schedule demo (Phase 8 port; activation refused while not connected), escalate to human (shared Phase 8 handoff), update CRM field, add tag, if/else, stop.
- Declarative condition language (all/any/not, comparisons, business-hours windows in tenant time zone); validation on save (unknown fields/actions/settings, loops, max steps/triggers/depth, references, repeat rules); re-entry policy, cool-down, per-lead frequency cap.
- Engine: persisted step state, step-level idempotency keys, bounded retry with backoff and per-step failure policy (retry/skip/stop), safe manual retry, cancellation, crash recovery (lease + minute sweep), per-tenant concurrency / monthly / daily-message limits, workflow and workspace kill switches, simulation without side effects.
- UI: list, builder (no JSON editing: trigger picker, step add/reorder/remove, condition builder, action forms, template variable preview with sample data, "Check workflow"), details with version history/activate/deactivate/kill switch, version pages, simulation, runs list/detail with retry/cancel and step log, limits and kill switch, My notices; breadcrumbs, empty states, confirm dialogs. API with Swagger.

### 2. Files
`Domain/Workflows`, `Application/Workflows` (Conditions, Definition, Contracts), `Infrastructure/Workflows` (Engine, Actions, Triggers + Hangfire jobs, Services, RunServices, Support, Events), `Infrastructure/Messaging/Services/WorkflowMessageSender.cs`, `Infrastructure/Agents/HandoffActions.cs` (extracted from the AI tool, behaviour unchanged), `Persistence/WorkflowModel.cs` + migration `Phase9Workflows`, event emission in `LeadService`, `LeadIntakeService`, `ImportExportServices`, `ContactServices`, `InboundMessageHandler`, Api/Web controllers, models, views, `workflow-builder.js`, `workflow-leadpicker.js`. Tests: `WorkflowFlowTests`, `WorkflowSendTests`, `WorkflowControlTests`, `WorkflowSecurityTests`, `WorkflowMigrationTests`, `WorkflowWebTests`, `WorkflowLogicTests`, `WorkflowArchitectureTests`, `WorkflowKit`.

### 3. Database
Migration `Phase9Workflows`: 8 new tables, no change to existing tables (see DATABASE_DESIGN.md). Verified on an empty database and on a database at the Phase 8 schema holding a tenant, roles and a lead.

### 4. Tests (actual results)
| | |
|---|---|
| Build | succeeded, 0 warnings, 0 errors (TreatWarningsAsErrors) |
| Unit | 1022 passed, 0 failed, 0 skipped |
| Architecture | 97 passed, 0 failed |
| Integration | see the final line of the report; includes 29 new workflow tests; 1 skipped (live Claude/Voyage), as before |
| Not executed | live WAHA, live Claude/Voyage, real Qdrant, Docker, load tests |

Existing tests changed on purpose: the worker harness now delivers an event to every queue bound to its routing key (as the real topic exchange does), and the real-broker campaign test drains the outbox in rounds because the shared workspace now also holds Phase 9 events. A first full run exposed two real defects that were fixed: a Swagger schema-name clash (`KillSwitchRequest`) and a builder form model that failed validation on server-side properties.

### Playwright render check (Chromium, scratch DB, desktop 1280 px and 390 px)
Workflow list (empty), builder with five step types and a condition row, "Check workflow" showing the real server-side validation: no JS errors, no horizontal overflow. The builder's save and the other pages are covered by `WorkflowWebTests` (not by screenshots).

### 5. Security and architecture review
See SECURITY.md / MULTI_TENANCY.md. Highlights: no code execution (closed catalogs, bounded parser, architecture-tested); workflows reach customers only through `WorkflowMessageSender` (always marketing, eligibility re-checked, then the Phase 5 pipeline); consent revoked mid-run or after queueing stops the message; retries/redelivery/concurrent triggers/crash recovery never duplicate (asserted on the fake WAHA's received requests); tenant isolation tested through API, MVC, consumer, job and DB guard.

### 6. Outstanding / known limits
1. A step that already ran is never undone (no compensation); a message already `Sending` when a run is cancelled still finishes.
2. A workflow message counts as done when the Phase 5 pipeline has queued it; a later send-time refusal (e.g. suppression after queueing) is visible on the message, not on the run.
3. Condition builder in the UI edits one level (ALL/ANY of rows, business hours); nested/NOT conditions saved through the API are shown read-only and kept.
4. Notification of staff is an in-app notice only (no e-mail/push channel exists).
5. Schedule demo and the five Phase 10 triggers are unavailable until Phase 10; the booking path was only tested with the test-only scheduler double.
6. Decisions taken by default: docs/OWNER_DECISIONS.md items 21–29.

### 7. Acceptance criteria
| Criterion | Result |
|---|---|
| A workflow can be created and activated | **Passed** (API and UI; versions immutable) |
| Trigger conditions work | **Passed** (conditions, hostile input, all producible triggers through the real outbox/consumer) |
| Steps execute in the intended order | **Passed** (order, branches, waits via the scheduler and clock) |
| Failed steps can be retried safely | **Passed** (no duplicate message on the fake WAHA across retry, redelivery, crash recovery) |
| A workflow cannot send messages to suppressed contacts | **Passed** (also no consent, revoked, opted out, revoked mid-run and after queueing, archived lead) |

### Next
Phase 10 (demo, trial and customer conversion). Prerequisite: register real `IDemoScheduler`, `ITrialProvisioner`, `IPaymentRequestGateway` and publish the five reserved events through `WorkflowEvents.Emit`. Not started.
