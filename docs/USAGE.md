# incident-sla-tracker: How to use

## 1. Run it

### Docker (easiest)

Requires Docker with the Compose plugin.

```bash
git clone https://github.com/sllhddn/incident-sla-tracker.git
cd incident-sla-tracker
docker compose up --build
```

SQL Server 2022 starts first; when it is healthy the API starts on port 8080, creates the `SlaTracker`
database and applies the scripts in `src/SlaTracker.Api/Data/Scripts`.

| URL | Purpose |
|---|---|
| http://localhost:8080/health | `{"status":"UP"}` when the database answers |
| http://localhost:8080/openapi/v1.json | OpenAPI document (import into Postman or Insomnia) |

### Locally with the .NET SDK

Requires the .NET 10 SDK and a SQL Server (Docker, LocalDB on Windows, or a server you have).

```bash
export ConnectionStrings__Tracker="Server=localhost,1433;Database=SlaTracker;User Id=sa;Password=...;TrustServerCertificate=True"
dotnet run --project src/SlaTracker.Api
```

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionStrings__Tracker` | see `appsettings.json` | SQL Server connection string; the database is created if missing |
| `Database__MigrateOnStartup` | `true` | Apply pending scripts at start-up |
| `ASPNETCORE_HTTP_PORTS` | `8080` in Docker | Listening port |

## 2. Walk through the workflow

`requests.http` has every call below for the VS Code REST Client or JetBrains HTTP Client.

**Plan and deploy a release**

```bash
curl -s -X POST localhost:8080/api/releases -H 'Content-Type: application/json' \
  -d '{"version":"1.4.2","scheduledAt":"2026-11-05T22:00:00+08:00"}'
curl -s -X POST localhost:8080/api/releases/1.4.2/deploy
```

**Open an incident** (optionally naming the release that caused it)

```bash
curl -s -X POST localhost:8080/api/incidents -H 'Content-Type: application/json' \
  -d '{"title":"Payment gateway rejects transactions","service":"Payments","severity":"P1","openedBy":"service.desk","causedByReleaseVersion":"1.4.2"}'
```

Response `201 Created` with a reference such as `INC-1000`, `responseDueAt` (15 minutes for P1) and
`resolveDueAt` (4 hours for P1).

**Move it through the workflow**

```bash
curl -s -X POST localhost:8080/api/incidents/INC-1000/acknowledge -H 'Content-Type: application/json' -d '{"by":"duty.engineer"}'
curl -s -X POST localhost:8080/api/incidents/INC-1000/resolve -H 'Content-Type: application/json' -d '{"by":"duty.engineer","summary":"Renewed certificate"}'
curl -s -X POST localhost:8080/api/incidents/INC-1000/close -H 'Content-Type: application/json' -d '{"by":"lead"}'
```

**Find incidents past their deadline and read the reports**

```bash
curl -s 'localhost:8080/api/incidents?breachingOnly=true'
curl -s 'localhost:8080/api/reports/sla?from=2026-10-01T00:00:00Z&to=2026-11-01T00:00:00Z'
curl -s localhost:8080/api/reports/change-failure-rate
```

## 3. Rules

**SLA targets** (table `dbo.SlaPolicy`, change with SQL without a release)

| Severity | Respond within | Resolve within |
|---|---|---|
| P1 | 15 min | 4 h |
| P2 | 30 min | 8 h |
| P3 | 4 h | 2 days |
| P4 | 1 day | 5 days |

**Workflow**

| From | Allowed |
|---|---|
| Open | acknowledge, resolve |
| Acknowledged | resolve |
| Resolved | close, reopen (back to Acknowledged; deadlines unchanged) |
| Closed | nothing |

Resolving without acknowledging counts the resolution time as the response time.

## 4. Endpoints

| Method | Path | Success | Errors |
|---|---|---|---|
| POST | `/api/incidents` | 201 | 400, 404 release |
| GET | `/api/incidents?status=&severity=&breachingOnly=&page=&size=` | 200 paged | |
| GET | `/api/incidents/{ref}` | 200 with timeline | 404 |
| POST | `/api/incidents/{ref}/acknowledge` · `/resolve` · `/close` · `/reopen` | 200 | 400, 404, 409 |
| GET, POST | `/api/releases` | 200 / 201 | 400, 409 duplicate |
| POST | `/api/releases/{version}/deploy` · `/rollback` | 200 | 404, 409 |
| GET | `/api/reports/sla?from=&to=` | 200 (default last 30 days) | 400 |
| GET | `/api/reports/change-failure-rate?from=&to=` | 200 (default last 180 days) | 400 |
| GET | `/health` | 200 | 503 |

Errors are `application/problem+json`. Validation errors list fields under `errors`.

## 5. Run the tests

```bash
dotnet test
```

Unit tests need nothing. Integration tests start SQL Server 2022 in Docker through Testcontainers, so Docker
must be running. The first run downloads the SQL Server image (about 1.5 GB).

## 6. Change the schema

1. Add `src/SlaTracker.Api/Data/Scripts/V003__your_change.sql`. Separate batches with `GO` lines.
2. Update the matching entity and `TrackerDbContext` mapping.
3. Run `dotnet test`. Never edit an applied script: the migrator compares checksums and refuses to start.

## 7. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `Migration V001__tables.sql was changed after it was applied` | An old script was edited | Revert it; put the change in a new script |
| `Login failed for user 'sa'` | Password does not meet SQL Server rules or does not match | Use the compose password, or update both places |
| Integration tests fail with Docker errors | Docker not running | Start Docker Desktop / the Docker service |
| 409 "Edited by someone else" | Another request changed the incident first | Reload and retry |
| 409 on a workflow step | Step not allowed from the current status | Check the workflow table |
