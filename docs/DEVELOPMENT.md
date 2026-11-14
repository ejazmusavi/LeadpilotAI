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


## Live check of the Google Places connector (runbook)
Cost: **one** Text Search request (pageSize 5, Enterprise SKU because the field mask includes website and phone), roughly 0.03–0.04 USD at list price: check Google's current price list. It prints counts and field presence only; do not paste or commit Google responses (caching terms: see INTEGRATIONS.md).
1. In the Google Cloud project: enable **Places API (New)**, enable billing, create an API key (restrict it to that API).
2. Give the check the key in ONE of two ways: set the environment variable `GOOGLE_PLACES_API_KEY` in the shell (never in a file or in git), **or** (cloud sandbox) save it as a network secret for host `places.googleapis.com`, header `X-Goog-Api-Key`, bare value, and leave the variable unset: the tool then sends a dummy key and the proxy substitutes the real one. Environment variables and secrets only reach a NEW session or after a restart.
3. Run: `SSL_CERT_FILE=/root/.ccr/ca-bundle.crt dotnet run --project tools/LiveGoogleCheck` (drop `SSL_CERT_FILE` outside the sandbox).
4. Expected: `OK http=200 calls=1 candidates=<1..5>`, a line of field counts (name/address/website/phone/city/province/country/sourceUrl), types such as `school`, place ids starting with `ChIJ`, attribution `Place data from Google Maps`, retention 30 days.
5. Errors the adapter reports: 400 `API key not valid` = the key (or the proxy substitution) is wrong; 403 = API not enabled/billing off/key restricted; 429 = quota. In the app itself: Connectors & usage → paste the key → Discovery → New search (Pakistan / Lahore / Private schools, max results 5) and watch the usage page.


## Configuration added in Phase 5
| Key | Where | Meaning |
|---|---|---|
| `RabbitMq:Host` / `Port` (5672) / `VirtualHost` (/) / `Username` / `Password` / `Prefix` (`leadpilot.messaging`) | Api, Web, Worker | Broker. Without `Host` events stay in the outbox and `/health/ready` omits the broker check. Compose adds a `rabbitmq` service (management UI on 15672). |
| `RabbitMq:RetryDelaysSeconds` | Worker | Optional list; default 5, 30, 120, 600. Leave unset unless you need to change it. |
| `Messaging:PublicWebhookBaseUrl` | Api, Web, Worker | **Public https URL of the Api** that WAHA can reach (compose: `PUBLIC_API_URL`). Used to build the webhook URL registered in WAHA. |
| `Messaging:Waha:AllowPrivateHosts` (false) | Api, Web, Worker | Allow a WAHA on a private address (same docker network / localhost). Link-local/metadata always refused. Compose: `WAHA_ALLOW_PRIVATE_HOSTS`. |
| `Messaging:*` caps and timing | all | Defaults are conservative (30/h, 150/day, 30 s gap, warm-up); see `MessagingOptions`. Per-account overrides are limited to the hard ceilings. |
| Tenant settings `messaging.optout_keywords`, `messaging.auto_create_leads`, `messaging.service_window_hours` | per tenant | Extra opt-out words, create a lead for unknown senders (off), service window (24). |

## Owner runbook: connecting WAHA (WhatsApp)
**Read the ban-risk warning first** (SECURITY.md / the accounts page). Use a dedicated number you can afford to lose.
1. **Run WAHA** (Core, WEBJS engine): `docker run -d --name waha -p 3000:3000 -e WAHA_API_KEY=<long-random-key> -e WHATSAPP_DEFAULT_ENGINE=WEBJS devlikeapro/waha`. Check the current image/tag and engine options at https://waha.devlike.pro; keep the port private (bind to localhost or a private network).
2. **Settings you need:** base URL (e.g. `http://waha:3000` from compose, or `http://localhost:3000`), the API key from step 1, engine (WEBJS/NOWEB/GOWS), a session name (letters, digits, `-`, `_`; e.g. `school-main`; must be unique across the whole platform), and a webhook secret (generated for you by LeadPilot unless you type one; it is the HMAC key).
3. **Prerequisites:** `Messaging:PublicWebhookBaseUrl` set to an address WAHA can reach (for local WAHA with `AllowPrivateHosts=true`, `http://localhost:5100` works). RabbitMQ and the Worker running (`docker compose up`, or `dotnet run` the three projects).
4. **Add the account:** sign in as an administrator → Messaging → Channels → New account → enter the settings above. The credentials are encrypted and never shown again.
5. **Webhook registration** happens when you press **Connect** (LeadPilot calls WAHA to create/start the session with the webhook URL `…/api/v1/webhooks/waha/<key>`, events `message`, `message.ack`, `session.status`, HMAC secret and retry policy). You do not register it by hand; if you must, use the URL and secret shown on the account page.
6. **Scan the QR** shown on the account page with the phone (WhatsApp → Linked devices). Status moves to Working.
7. **Minimal manual live check** (about 5 minutes, one conversation):
   a. From another phone send "Hello" to the connected number → within seconds it appears under Conversations, and the Webhooks page lists a `message` event as Processed.
   b. Reply from the conversation page → the message shows Sent, then Delivered/Read after the other phone opens it.
   c. From the other phone send "STOP" → the conversation shows opted-out, the contact is suppressed and a further reply is refused.
   d. Stop the Worker, send "Hello" again, start the Worker → the message appears (outbox/queue durability).
   e. Compare each step with the fake-based tests; report anything that differs (response shapes, ack timing, `@lid` ids) so the adapter can be corrected.
8. **Troubleshooting:** webhook 401 = wrong secret; nothing arrives = WAHA cannot reach the webhook URL (check `PublicWebhookBaseUrl`) or `Waha:AllowPrivateHosts`; events stuck in the outbox = broker unreachable (health check); account paused = read the notification on the account page, fix the cause, then Resume.

## Tests for Phase 5
`dotnet test` as before. Broker tests need a RabbitMQ: set `LEADPILOT_TEST_RABBITMQ=<host>` (e.g. `localhost`, default guest/guest; otherwise they are skipped and reported as skipped). The WAHA fake is test-only (`FakeWahaServer`).

## Configuration added in Phase 6
| Key | Where | Meaning |
|---|---|---|
| `Campaigns:ScanLimit` (20000) / `PageSize` (200) | Web, Api, Worker | Audience scan limit and page size (streamed, never all in memory). |
| `Campaigns:DefaultMonthlyMessageLimit` (1000) | all | Used when a workspace sets no monthly limit in the campaign settings. |
| `Campaigns:LeaseSeconds` (120) / `StaleTickMinutes` (3) | Worker | Tick lease; a Running campaign nobody ticked for this long is pushed again by the sweep. |
| `Campaigns:SampleSize` (5) | all | Sample messages shown in a preview. |
Workspace policy (cool-down, approval, four-eyes, attribution window, failure threshold, opt-out text, monthly limit, test numbers, kill switch) is edited under **Campaigns → Campaign settings**.

Hangfire queue `campaigns` is processed by the Worker; recurring job `campaign-sweep` (every minute) is registered next to the messaging jobs. RabbitMQ gets a third consumer queue `{Prefix}.campaigns` (+ retry/DLQ).

## Running a campaign locally
1. Connect a WhatsApp account (Phase 5 runbook) and create a template of the **Marketing** category.
2. Record marketing consent for contacts (or import with consent) — nobody without consent is ever messaged.
3. Campaigns → New campaign: template, account, audience (watch the live preview and the exclusion reasons), limits. Save draft → Dry run → Submit (→ approval if the workspace requires it) → Start now / wait for the schedule.
4. Add your own phone under *Campaign settings → Test numbers* and use *Send test* first. Start with a tiny audience on a disposable number.
5. Pause, resume, cancel or use the workspace **kill switch** at any time.

Tests: `CampaignKit` (integration) provides `CampaignWorld` (a tenant with a connected fake-WAHA account, a template and a worker harness that ticks and runs the Phase 5 send jobs deterministically with an adjustable clock) and a recording campaign scheduler. Two tests use the real Hangfire server and the real broker. Migration: `Phase6Campaigns` is the only new migration; `CampaignMigrationTests` upgrades a Phase 5 database with data.


## Configuration added in Phase 7
| Key | Where | Meaning |
|---|---|---|
| `Ai:Claude:BaseUrl` / `ApiVersion` / `TimeoutSeconds` (60) / `MaxRetries` (2) / `RetryBaseDelayMs` / `BreakerFailureThreshold` (5) / `BreakerOpenSeconds` (30) / `ServerSideFallback` (false) | Web, Api, Worker | Claude adapter. Keys are NOT configuration: each workspace saves its own under AI settings (encrypted). |
| `Ai:Voyage:*` | same | Embedding adapter. |
| `Ai:VectorStore:Provider` (`mysql` \| `qdrant`) / `MysqlMaxScan` (20000) / `Qdrant:BaseUrl` / `Qdrant:ApiKey` / `Qdrant:CollectionPrefix` | all | Vector store. The Qdrant key is a platform secret (environment/secret store). Switching store or embedding model needs a re-index. |
| `Ai:Ingestion:*` | all | Upload size (10 MB), PDF pages (300), DOCX limits, chunk sizes (900/1400/120), max passages per document (2000), website pages (10), documents per workspace (500), stale-indexing takeover (15 min). |
| `Ai:DefaultMonthlyTokenLimit` (2,000,000) / `MaxReplyTokens` / `MaxMessageChars` / `Ai:Prices:<model>` | all | Limits and the ESTIMATED price table of the usage page (USD per million tokens). |
Hangfire queue `knowledge` is processed by the Worker; recurring job `knowledge-sweep` (every 5 minutes). RabbitMQ gets a fourth consumer queue `{Prefix}.ai` (+ retry/DLQ) for `ai.reply.requested`. `DataProtection:KeysPath` must be shared by Web, Api and Worker (the Worker decrypts the workspace keys).

## Using the AI locally
1. Sign in as an administrator → **AI → AI settings**: paste your Anthropic key and Voyage AI key (stored encrypted), press *Test connection* on each, then *Turn on*.
2. **AI → Knowledge base → Add knowledge**: write text, upload a file or add your website. The Worker indexes it (status Waiting → Indexed).
3. **AI → Retrieval tester**: ask a question; check passages, scores and the answer.
4. Open a conversation, set **AI mode: Active**, and have a customer write: a suggested reply (or an escalation) appears above the reply box; *Use as reply draft* puts it into the box for a person to check and send. Nothing is sent automatically.

## Runbook: live check of Claude and Voyage (NOT performed; needs real keys and spends real money)
Cost: roughly 40 short Claude calls (about 0.3–1 USD with the default models) and a few hundred embedding tokens. Use a disposable workspace.
1. Put the keys in the **environment of the shell only** (never in a file or in git): `ANTHROPIC_API_KEY`, `VOYAGE_API_KEY`.
2. Quick manual check: AI settings → paste each key → *Test connection* → expect "Connected: model … answered" / "returned a 1024-dimension vector". 401 = wrong key, 400 = unknown model name, 429 = quota.
3. Evaluation: `LEADPILOT_EVAL_LIVE=1 ANTHROPIC_API_KEY=… VOYAGE_API_KEY=… LEADPILOT_TEST_CONNECTION=… dotnet test tests/LeadPilot.IntegrationTests --filter LIVE_` runs the 12-scenario skeleton (`tests/LeadPilot.IntegrationTests/Evaluation/sales-scenarios.json`) against the real services and prints a per-category report (passes when at least 80% of the scenarios pass; without the variables it is reported as SKIPPED, not passed).
4. Verify in the usage page that cache read tokens appear on the second request of the same workspace (prompt caching) and that costs look plausible; then consider `Ai:Claude:ServerSideFallback=true`.
5. Report anything that differs from the fake (response shapes, model names, latency) so the adapter can be corrected.

## Tests for Phase 7
`dotnet test` as before (MySQL and RabbitMQ variables as in Phase 5). Test doubles live in the test projects only: `ScriptedLlm` (grounded by default; overridden per test to invent prices, obey injections, leak, fail), `FakeEmbeddingProvider` (hashed bag of words), `RecordingAiScheduler`, `ScriptedWebsite`, `FakeProviderServer` (speaks the Claude/Voyage shapes so the REAL adapters run end to end), `FakeQdrantServer`, `TamperedVectorStore`. Migration: `Phase7AiKnowledge` is the only new migration; `AiMigrationTests` upgrades a Phase 6 database that holds data.

## Phase 8: sales agent
No new service, queue or job: the agent runs inside the existing `ai.reply.requested` consumer (queue `{prefix}.ai`) and the existing send job. Migration `Phase8SalesAgent` is the only new one (`dotnet tool run dotnet-ef migrations add …` as before; applied on start-up in Development).
**Try it without a model:** Sales agent → "Create the sample School ERP agent" → fill in and **confirm** the catalog → AI settings (keys) → turn a conversation's AI mode to Active. Automatic replies stay off until you allow them in the agent settings AND on the agent.
**Tests:** `dotnet test` as before. Test doubles (test projects only): `ScriptedLlm` plus `AgentScript` (a scripted agent: tool calls then an answer), `Phase10Doubles` (stand-ins for the demo/trial/payment services, "not connected" by default). `AgentScenarioTests` runs **196 scenarios** (English 92, Urdu 43, Roman Urdu 61) through the real pipeline and asserts tool calls, refusals, escalations, no invented prices/features/bookings/payments, nothing outside the allow-list, injection attempts, opt-outs and that nothing leaves while a person is in control.
**Live check of the agent (owner runbook, costs real money, not run in the sandbox):** set the keys in AI settings, create the sample agent, enter your real product facts and confirm them, put a test conversation to AI mode Active with automatic replies OFF, send "How much is the Standard plan?" from a test number and look at: the Tool log (GetPricingPlans executed, the exact output), AI activity (the suggestion cites the tool result) and the prompt behaviour. Only then switch automatic replies on for a test number. Report differences between the real model's JSON and the protocol in `AiPrompts.Agent` so the parser can be adjusted.
