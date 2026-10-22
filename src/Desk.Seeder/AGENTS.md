# src/Desk.Seeder: synthetic data generator

Area file for `src/Desk.Seeder/` (deterministic synthetic data generator, bulk COPY). The root `AGENTS.md` still applies in full. Spec: README §5.4 and §5.5.

## Rules

- **Synthetic only.** No real company, employer, client or person names in generated data, fixtures or screenshots. The seeder generates only the fictional book; public market reference data (ADR-0030) comes from the ingest, never from a generator, and its names never feed the synthetic issuer, deal or analyst names.
- **Never TRUNCATE ingested tables.** The seeder bootstraps an empty or version-bumped database at the latest ingested as-of date; the ingest owns the rolling dates and the public series (ADR-0030).
- **Bump `SeedVersion`** (`SeedVersion.cs`) only when the shape of the seeded data changes: a generator change, or a seeded table or column added, dropped, renamed or retyped. A constraint- or index-only migration that the existing data satisfies (#286) needs no bump. Without a needed bump, production keeps the old data (deploy seeds only when the version changed, README §14.2).
- **Budgets (README §10):** seeder runtime < 90 s locally at scale 1.0; DB size < 350 MB; the seeder fails above 400 MB.
- **Never run `--force` seeding or the load benchmark against production or Neon.** `--force` wipes the seeded tables. The load benchmark drops and recreates a `bench` schema in whatever `DATABASE_URL` points to.
- The app never seeds at start-up; deploy runs the seeder as its own step.
- Use the `seeding` skill for generator changes.

## Commands

| Task | Command |
|---|---|
| Seed (local) | `dotnet run --project src/Desk.Seeder -- --if-changed --scale 1.0` (`--force` to reseed, `--size-report`) |
| Seed at a fixed date (local) | `dotnet run -c Release --project src/Desk.Seeder -- --force --scale 1.0 --as-of 2026-10-06` |
| Load benchmark (local, ADR-0004) | `dotnet run -c Release --project perf/LoadBenchmark -- 100000` |

## Tests

- `tests/Desk.Seeder.Tests` (generator unit tests and Testcontainers seeding tests). Docker required; never skip.
