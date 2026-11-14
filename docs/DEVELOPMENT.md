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
