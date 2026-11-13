# Development guide

## Prerequisites
.NET SDK 10.0.x, MySQL 8 (local or Docker), optional Docker for the compose stack.

## Run locally
```bash
# 1. database (dev defaults used by appsettings.Development.json)
mysql -uroot -e "CREATE USER 'leadpilot'@'%' IDENTIFIED BY 'leadpilot_dev'; GRANT ALL ON *.* TO 'leadpilot'@'%';"

# 2. first admin (user-secrets, never committed)
dotnet user-secrets --project src/LeadPilot.Web set "Seed:AdminEmail" "you@example.com"
dotnet user-secrets --project src/LeadPilot.Web set "Seed:AdminPassword" "<strong password>"

# 3. run (Development applies migrations automatically)
dotnet run --project src/LeadPilot.Web      # http://localhost:5002
dotnet run --project src/LeadPilot.Api      # http://localhost:5100/swagger
dotnet run --project src/LeadPilot.Worker
```
In Development, password-reset emails are **logged, not sent** (look for `[DEV EMAIL - not sent]`). Outside Development, SMTP (`Email:Smtp:*`) must be configured or sending fails loudly.

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
