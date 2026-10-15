# tests: Testcontainers, coverlet and Vitest

Area file for `tests/` (xUnit v3 + Microsoft.Testing.Platform, Testcontainers Postgres). The root `AGENTS.md` still applies in full. Spec: README §11, ADR-0018.

## Projects

- `tests/Desk.Api.Tests` (Mvc.Testing 10.0.12, TimeProvider.Testing, Testcontainers.PostgreSql 4.15.0)
- `tests/Desk.Data.Tests`
- `tests/Desk.Seeder.Tests`
- Shared `tests/Directory.Build.props`: xunit.v3 4.0.1, coverlet.MTP 10.1.0, `OutputType Exe`. `global.json` sets runner `Microsoft.Testing.Platform`.

## Fixture

- `PostgresApiFactory` (collection `api-postgres`): Postgres 17 container, migrated, seeded SEED=42 at scale 0.1 with as-of 2026-10-06, `FakeTimeProvider`, Production environment.
- Each test creates its own accounts (`CreateUserAsync`) and never mutates shared seeded rows.
- Limits are raised in the fixture; limit tests build their own host with `WithSettings`.
- Docker is required; never skip, disable or weaken a test (gate clause 1).

## Coverage and governance

- `tests/testconfig.json` is a governance path (`[workflows]`). Coverlet exclusions live there; never add `CompilerGeneratedAttribute`.
- Thresholds: `perf/coverage-thresholds.json` (diff line/branch ≥ 80, overall must not drop, tolerance 0.5). The baseline rule is in root item 6.
- Entitlement stub tests follow the root E7 rule.
- Long-form: [`docs/agents/guidelines/tests-and-coverage.md`](../docs/agents/guidelines/tests-and-coverage.md).

## Commands

| Task | Command |
|---|---|
| All .NET tests | `dotnet test -c Release` |
| One project | `dotnet test tests/Desk.Api.Tests -c Release` |
| E2E (phase 4) | `docker compose -f docker-compose.yml -f e2e/docker-compose.e2e.yml up -d --build`, migrate, seed `--scale 0.2`, create a viewer with Desk.UserAdmin, then `cd e2e && npm ci && npx playwright install chromium && BASE_URL=http://localhost:8080 DESK_EMAIL=… DESK_PASSWORD=… npx playwright test` (CI `e2e` job; `PERF=1 … npx playwright test perf` for ADR-0009 numbers) (local compose/CI stack only; create the viewer with Desk.UserAdmin against the local or test database, never Render, Neon or production) |

Gate-script tests stay in [`.github/AGENTS.md`](../.github/AGENTS.md).
