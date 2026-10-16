# src/Desk.Api: API rules

Area file for `src/Desk.Api/` (ASP.NET Core 10: endpoints, auth, rate limiting, caching; serves the SPA from `wwwroot`). The root `AGENTS.md` still applies in full. Spec: README §8 and §7.

## Rules

- **OpenAPI metadata (ADR-0019):** every endpoint carries `WithName`, `WithSummary` and `WithTags`, so it shows in Swagger UI.
  - Responses (#307): an endpoint declares only what its own handler returns (`Produces` for 200/204/304, `ProducesProblem` for its own 400/404/409, etc.). The pipeline statuses come from the `PipelineResponses` operation transformer (`src/Desk.Api/Hardening/PipelineResponses.cs`): binding 400, 401, policy 403, 415, and 429 and maintenance 503 on `/api`, read from the endpoint's metadata. Don't declare those by hand.
  - Every new or changed operation gets its exact status set pinned in `OpenApiResponsesTests` (`Pinned`); its completeness check fails for an operation without a row.
  - The SPA fallback, the `/api` fallback, and the Swagger-off 404 routes (`/swagger`, `/swagger/*`, `/openapi/*` when `SWAGGER_ENABLED` is off; PR #95) use `ExcludeFromDescription()` and must not appear in the OpenAPI document.
- **Async:** pass the `CancellationToken` through every call. No `.Result`, `.Wait()` or `async void`.
- **Data access:** go through `src/Desk.Data` (column-catalog whitelist, parameters only, `IDbContextFactory` per parallel task). Read `src/Desk.Data/AGENTS.md` before you touch queries.
- **Free tiers:** `/health` never touches the DB; `/health/db` is the one DB probe (README §7.2, #365), and nothing monitors it; reads are cache-first; rate limits stay on in every environment except unit tests; no keep-awake pingers.
- **Start-up:** the app never runs DDL, migrations or seeding at start. `deploy/start.sh` checks the environment and execs the app.
- **Money:** `decimal` end to end; round only at the display edge; empty or zero weights return `null`, never `NaN`.

## Commands

| Task | Command |
|---|---|
| API | `dotnet run --project src/Desk.Api` (http://localhost:5180) |
| API tests (Docker required; never skip) | `dotnet test -c Release` (all .NET suites; `tests/Desk.Api.Tests` is the API's) |

## Tests

- `tests/Desk.Api.Tests` runs on Testcontainers Postgres 17 (README §11). Docker must be running. Never skip, disable or weaken a test to get green.
- Coverage: ≥80% on new or changed code, main never drops (gate clause 2). Use the root Coverlet and Coverage gates commands.
- Health: `SmokeTests` in `tests/Desk.Api.Tests` checks `/health` (status and version, no database), auth on `/api` and the SPA fallback; it runs with the API tests. Don't probe a running API from the shell (no curl or wget, no exception; [`workflow.md`](../../docs/agents/workflow.md#health-checks)).

## Local configuration

- The API reads `DATABASE_URL` / `ConnectionStrings__<Source>` from the environment. There is no default connection string (README §12).
- Agents don't open, print or source `.env`. If the variables aren't already set in your session, ask Tech Coordinator; don't add a default.
- Patterns and examples: [`docs/agents/guidelines/dotnet-api.md`](../../docs/agents/guidelines/dotnet-api.md); new endpoints: the `add-endpoint` skill.
