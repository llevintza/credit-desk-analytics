# EF Core Postgres migrations

Linked from [`src/Desk.Data/AGENTS.md`](../../../src/Desk.Data/AGENTS.md) and the `ef-migration-safety` skill. Local only; never against Neon or production (root rule).

## Context

- One context, `AppDbContext` (`src/Desk.Data/App/`), plus `AppDbContextDesignFactory`.
- Migrations live in `src/Desk.Data/App/Migrations`. History table `__ef_migrations_history` in `AppDbContext.Schema` (`app`).
- EF Core 10.0.12, Npgsql EF 10.0.3, Postgres 17 (CI service image).

## Naming and files

- `dotnet ef migrations add <PascalCaseName> --project src/Desk.Data --startup-project src/Desk.Data --output-dir App/Migrations`
- Produces `yyyyMMddHHmmss_<Name>.cs` (e.g. `20261008024514_SnapshotSortIndexes.cs`).
- Commit the migration, its Designer file and the updated `AppDbContextModelSnapshot.cs`.

## CI and deploy

- CI `api` job: `dotnet ef migrations has-pending-model-changes` must pass.
- `./dbtools/efbundle` runs twice (the second run is a no-op).
- The `db-tools` job builds the bundle from a clean checkout.
- Deploy runs migrations before the new app starts (README §14.2), so expand → deploy → contract (README §14.4): nullable or defaulted columns, no rename or drop in the same release.

## Data

- No seed data in migrations. A seeded-schema change bumps `SeedVersion` (`src/Desk.Seeder/SeedVersion.cs`).
