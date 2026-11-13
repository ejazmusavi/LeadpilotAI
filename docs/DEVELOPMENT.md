# Development guide

## Prerequisites
.NET SDK 10.0.x, MySQL 8 (local or Docker), optional Docker for the compose stack.

## Run locally
```bash
# 1. database (dev defaults used by appsettings.Development.json)
mysql -uroot -e "CREATE USER 'leadpilot'@'%' IDENTIFIED BY 'leadpilot_dev'; GRANT ALL ON *.* TO 'leadpilot'@'%';"

# 2. first Platform Owner (user-secrets, never committed). Created in the platform tenant; only when no user exists yet
#    (or when the e-mail already exists but belongs to no tenant, e.g. the Phase 1 admin).
dotnet user-secrets --project src/LeadPilot.Web set "Seed:AdminEmail" "you@example.com"
dotnet user-secrets --project src/LeadPilot.Web set "Seed:AdminPassword" "<strong password>"

# 3. run (Development applies migrations automatically)
dotnet run --project src/LeadPilot.Web      # http://localhost:5002
dotnet run --project src/LeadPilot.Api      # http://localhost:5100/swagger
dotnet run --project src/LeadPilot.Worker
```
In Development, password-reset **and invitation** e-mails are **logged, not sent** (look for `[DEV EMAIL - not sent]`; the invitation link is in the body). Outside Development, SMTP (`Email:Smtp:*`) must be configured or sending fails loudly.

## Migrations
```bash
dotnet tool restore
dotnet tool run dotnet-ef migrations add <Name> -p src/LeadPilot.Infrastructure -s src/LeadPilot.Web -o Persistence/Migrations
dotnet tool run dotnet-ef database update -p src/LeadPilot.Infrastructure -s src/LeadPilot.Web   # uses LEADPILOT_CONNECTION or dev default
```

## Tests
```bash
dotnet test tests/LeadPilot.UnitTests
dotnet test tests/LeadPilot.ArchitectureTests
# integration tests need a MySQL 8 account allowed to CREATE/DROP databases; each run uses a throw-away database
LEADPILOT_TEST_CONNECTION="Server=localhost;Port=3306;User=leadpilot;Password=leadpilot_dev;" dotnet test tests/LeadPilot.IntegrationTests
```
Without `LEADPILOT_TEST_CONNECTION` the integration tests are **skipped**, not passed.

## Docker
```bash
cp docker/.env.example docker/.env   # edit values
docker compose -f docker/docker-compose.yml --env-file docker/.env up --build
# Web http://localhost:8080 · API http://localhost:8081/swagger · MySQL is not published to the host
```

## Configuration added in Phase 2
| Key | Where | Meaning |
|---|---|---|
| `Jwt:SigningKey` | Api | HMAC key (>= 32 chars) for access tokens. **Required outside Development** (the Api refuses to start); Development uses a fixed, clearly-labelled dev key. |
| `Jwt:Issuer` / `Jwt:Audience` / `Jwt:AccessTokenMinutes` (15) / `Jwt:RefreshTokenDays` (14) | Api | Token settings. |
| `App:PublicWebUrl` | Web, Api | Base URL used for links in invitation e-mails (falls back to the request host in Web). Set it for the Api. |
| `Invitations:ExpiryDays` | Web, Api | Invitation lifetime, default 7 (1-30). |
| `DataProtection:KeysPath` | all | Directory for the shared key ring used to encrypt secret tenant settings. Compose mounts a `dp-keys` volume at `/keys`. Without it keys are ephemeral and secrets become unreadable after a restart. |
| `Security:StampValidationMinutes` | Web | Security-stamp re-validation interval (default 5). Tests set 0. |

## Using the API (Phase 2)
```bash
# sign in -> access token (claim tid = active tenant) + rotating refresh token
curl -s localhost:5100/api/v1/auth/login -H 'content-type: application/json' -d '{"email":"you@example.com","password":"..."}'
curl -s localhost:5100/api/v1/roles -H "authorization: Bearer $TOKEN"
```
Swagger has an *Authorize* button for the bearer token. `X-Tenant-Id` may be sent but must equal the token's tenant, otherwise 400.

## Tenancy rules for contributors
- Tenant-owned entities implement `ITenantOwned` (global filter + SaveChanges guard apply automatically). An architecture test fails if a table has `TenantId` without it.
- Never read a tenant id from a request. Use `ITenantContext`; jobs/consumers use `ITenantScopeRunner.RunAsync(tenantId, ...)`.
- `IgnoreQueryFilters()` is only allowed in the allow-listed files in `tests/LeadPilot.ArchitectureTests/TenancyArchitectureTests.cs`.
- Protect endpoints with `[Authorize(Policy = PermissionCodes.X)]`; add new permissions to `PermissionCatalog` (+ `SystemRoles` mapping). System roles are re-synchronised at every Web start.

## Configuration added in Phase 3
| Key | Where | Meaning |
|---|---|---|
| `Crm:StorageRoot` | Web, Api | Folder for uploaded import files (default `data/files` under the content root; compose mounts the shared `crm-files` volume at `/data/files`). Web and Api must share it. |
| `Crm:MaxImportFileBytes` (5 MB) / `Crm:MaxImportRows` (5000) / `Crm:MaxExportRows` (10000) / `Crm:MaxBulkItems` (500) / `Crm:MaxXlsxUncompressedBytes` (50 MB) | Web, Api | Limits. |
| `RateLimiting:ApiPermitLimit` | Api | Global per-IP requests per minute (default 300). |
| Tenant setting `Crm.DefaultCountryCode` | per tenant (Settings page / API) | Calling code (e.g. `966`) used to turn local numbers such as `0501234567` into E.164. Without it, local numbers are rejected. |

## CRM quick start
```bash
# sign in (token carries the tenant), then:
curl -s localhost:5100/api/v1/leads -H "authorization: Bearer $TOKEN" -H 'content-type: application/json' \
  -d '{"name":"Al Noor School","city":"Riyadh","contacts":[{"name":"Sara","phone":"+966 50 123 4567"}]}'
curl -s "localhost:5100/api/v1/leads?search=noor&sort=Score&descending=true" -H "authorization: Bearer $TOKEN"
curl -s localhost:5100/api/v1/imports -H "authorization: Bearer $TOKEN" -F file=@leads.csv           # then POST /imports/{id}/run
curl -s "localhost:5100/api/v1/audience/eligible-contacts?channel=WhatsApp&purpose=Marketing" -H "authorization: Bearer $TOKEN"
```
Import file: first row = headers (`Name`, `Category`, `Country`, `Province`, `City`, `District`, `Area`, `Address`, `Website`, `Tags`, `Stage`, `Owner`, `Contact`, `Role`, `Phone`, `E-mail` and common synonyms are recognised; any column can be mapped by hand in the wizard). UTF-8 CSV with `,` `;` or tab delimiters, or the first sheet of an `.xlsx`.

Permissions: `Leads.View/Create/Update/Delete/Import/Export/Assign`, `Consent.Manage`, `Suppression.Manage`, `Pipelines.Manage` (pipelines, categories, statuses), `Scoring.Manage`. Default mappings live in `Application/Security/Permissions.cs`; existing tenants receive them when the Web host starts (idempotent seeder).

Tests: integration tests that need real tenants use `TestWorld` (tenants A and B with administrators, sales manager/agent, marketing manager and read-only users) and `NewTenantAsync` for tests that change tenant-wide configuration (scoring rules, default pipeline). Phones and names come from `Crm.Phone()` / `Crm.Token()` so tests never collide in the shared database. Integration tests set `RateLimiting__ApiPermitLimit`, `Crm__StorageRoot` and `Crm__MaxExportRows=100` in `AppFixture`.

Migrations: `Phase3Crm` is the only new migration. To check an upgrade by hand: run the Phase 2 build against an empty database (migrate + seed), then `dotnet ef database update` with the Phase 3 build and start the Web host (the integration test `A_phase2_database_upgrades_and_existing_tenants_receive_crm_defaults_and_permissions` automates exactly this).


## Configuration added in Phase 4
| Key | Where | Meaning |
|---|---|---|
| `Discovery:PageSize` (20) / `MaxPagesPerQuery` (5) / `MaxResultsPerJob` (200) / `MaxBulkItems` (100) | all | Search limits. |
| `Discovery:DefaultMonthlyCallLimit` (1000) | all | Used when a tenant sets no limit for a connector (tenants override under *Connectors & usage*). |
| `Discovery:DefaultCostPerCallUsd` (0.035) | all | **Estimate** shown on the usage page; set it from Google's current price list. |
| `Discovery:PageDelayMs` (500) | Worker | Minimum delay between provider calls of one job (rate limit). Tests set 0. |
| `Discovery:GoogleContentRetentionDays` (30) | Worker | How long provider content of a result is kept (see INTEGRATIONS.md). |
| `Discovery:StaleRunningMinutes` (30) | Worker | A Running job older than this may be taken over after a worker crash. |
| `Discovery:GooglePlaces:BaseUrl` / `TimeoutSeconds` | all | Google endpoint (tests point it at a local fake). |
| `Discovery:Website:UserAgent` / `RobotsToken` / `MaxBytes` (1 MB) / `TimeoutSeconds` (10) / `MaxRedirects` (3) / `MaxPagesPerSite` (2) / `MaxWebsitesPerJob` (100) | Worker | Website fetching limits. Put a real contact URL in the User-Agent before production. |
| `Discovery:Hangfire:PollSeconds` (5) / `Workers` (2) | Worker | Hangfire queue polling and worker threads. |
| `ConnectionStrings:Default` | Web, Api, Worker | Hangfire reuses it (adds `Allow User Variables=true`); its tables use the prefix `Hangfire` in the same database. |
| `DataProtection:KeysPath` | Web, Api, **Worker** | Must be the SAME key ring everywhere, otherwise the Worker cannot decrypt connector keys saved in Web. |

## Running discovery locally
```bash
dotnet run --project src/LeadPilot.Web      # migrates (EF) first; creates the Hangfire tables on first use
dotnet run --project src/LeadPilot.Worker   # Hangfire server: processes discovery searches, runs the daily retention job
# 1. sign in as an administrator -> Discovery -> Connectors & usage: paste your own Google Places API key (stored encrypted)
# 2. Discovery -> New search: pick a saved location + category (education workspaces get the school starter pack automatically)
# 3. watch it on the search page, then Review results: approve/reject/bulk. Approved results become leads (source = Discovery, public contacts, no consent)
# Hangfire dashboard (platform operators only): http://localhost:5002/hangfire
```
Without a Google key you can still search public websites you provide (connector *Public websites*). Nothing in the app returns sample data.

Permissions: `Discovery.View/Run/Review/Manage`, `Integrations.Manage` (defaults: Sales Manager View/Run/Review/Manage; Marketing Manager View/Run; Sales Agent and Staff View; Tenant Administrator all; Read-only none). Existing tenants receive them when Web starts (idempotent role sync).

Tests: `DiscoveryKit` (integration) holds the TEST-ONLY fake connector (`test-fake`, `test-fake-keyed`), a recording scheduler and a no-network website enricher; they are registered by the test hosts only. `DiscoveryHangfireTests` build the real Worker composition (`AddWorkerServices`, Development ⇒ `ValidateOnBuild`) with a real Hangfire server on the test database. Migration: `Phase4Discovery` is the only new migration; `DiscoveryMigrationTests` upgrades a Phase 3 database that holds data.
