# ADR-0001: Stack: .NET 10 + Postgres + Angular 22

- **Status:** Accepted
- **Date:** 2026-10-07
- **Phase / PR:** phase-0/scaffold

## Context

The product is an internal analytics app for a structured-credit desk (README §1). Its data is large in both rows and columns, accuracy matters more than anything, and changes ship weekly. The stack has to:
- handle dynamic-shape SQL reads well, as well as a stable model for accounts and presets
- render very large grids in the browser
- run within free-tier hosting (Render 512 MB instance, Neon 0.5 GB)

## Options considered

1. **.NET 10 + Postgres + Angular 22:** typed end to end. Dapper for dynamic reads, EF Core for the stable model. Angular signals/OnPush and AG Grid Community for the grid.
2. **Node (NestJS) + Postgres + React:** one language, but weaker numeric typing (`decimal`) and no Dapper/EF split.
3. **Python (FastAPI) + Postgres + React:** fast to prototype, but slower for serializing large columnar payloads, and further from the desk's Microsoft-centric tooling.

## Evaluation

| Criterion | .NET 10 + Angular | NestJS + React | FastAPI + React |
|---|---|---|---|
| Exact money arithmetic | `decimal` built in | needs a library | `Decimal`, slower |
| Dynamic SQL + stable model | Dapper + EF Core, side by side | Kysely/Prisma, partial | SQLAlchemy core/ORM |
| Large-grid front end | Angular + AG Grid Community, OnPush + signals | React + AG Grid | React + AG Grid |
| Idle memory (measured, phase-0 image) | **20 MiB** API process; image 379 MB | n/a | n/a |
| Initial JS (measured, phase-0 shell) | **215.6 kB raw / 59.3 kB transfer** | n/a | n/a |

**How to reproduce:**
- `docker compose up -d --build`, then `docker stats --no-stream` and `docker images`.
- `cd web && npx ng build` for the bundle numbers.

## Decision

.NET 10 (minimal APIs, Dapper + EF Core 10 with Npgsql) on Postgres 17, with Angular 22 (standalone, zoneless, OnPush, signals) and AG Grid Community.

## Consequences

- Two data-access styles to keep disciplined: AGENTS.md defines which goes where.
- Tests use xUnit v3 on Microsoft.Testing.Platform (opted in via `global.json`), because the .NET 10 SDK no longer runs VSTest for xUnit v3.
- EF Core packages are pinned to the same patch version (10.0.12). The Npgsql provider otherwise pulls an older EF at runtime, which we hit as a `FileNotFoundException` in the first test run.
