# ADR-0004: Bulk load: Npgsql binary COPY vs EF Core AddRange

- **Status:** Accepted
- **Date:** 2026-10-07
- **Phase / PR:** phase-1/data

## Context

The seeder loads about **1.56 million rows** (20 tables, including the 202-column snapshot). It runs in GitHub Actions against Neon on every seed-version change (README §14.2), within a < 90 s local budget (README §10). EF Core is already the stable-model data access, so the question is whether it should do bulk loading too.

## Options considered

1. **Npgsql binary COPY** (`BeginBinaryImport`): rows streamed in Postgres' binary format, no SQL per row.
2. **EF Core `AddRange` + `SaveChanges`** (batched INSERTs, change tracking off).
3. **Multi-row `INSERT … VALUES` through Dapper.** Not measured: it's the same per-statement path as option 2 without EF's overhead, and still parses SQL per batch.

## Evaluation

Same 100,000 generated `position_history` rows (10 columns: date, bigint, 5 numeric, 3 double), each loaded into an identical scratch table. Five runs, Release build, local Postgres 17 (Docker), Apple M5:

| Method | Median s | Range s | Rows/s (median) | Managed alloc |
|---|---|---|---|---|
| **Npgsql binary COPY** | **0.42** | 0.31–0.49 | **~238,000** | **23 MB** |
| EF Core AddRange + SaveChanges | 4.10 | 3.92–5.11 | ~24,000 | 1,267–1,271 MB |

- **COPY is about 9.8× faster at the median** (8–16× across runs) and allocates **about 55× less**.
- One cold first run had COPY at 1.16 s (JIT warm-up). It's excluded from the median and noted here for honesty.
- **The whole seed with COPY** (scale 1.0, 1,563,791 rows across 20 tables, including the 202-column snapshot) completes in **7.9–9.6 s** end to end, including generation.
- **Extrapolated, not measured:** EF at the measured ~24k rows/s would need ~66 s just to insert the rows, and about 20 GB of allocations. Over the network to Neon the per-statement round trips make it worse.

**How to reproduce:**

```
set -a; . ./.env; set +a
dotnet run -c Release --project perf/LoadBenchmark -- 100000
```

The benchmark creates and drops its own `bench` schema.

## Decision

Option 1: binary COPY for every seeded table (`Loader.Copy`), all inside **one transaction** (TRUNCATE, then reload). A failed reseed leaves the previous data intact. EF Core stays for the stable model: migrations, the metadata read, and later accounts and presets.

## Consequences

- **Column lists and `NpgsqlDbType`s are kept beside each table load,** and a type mismatch fails loudly at load time. For the snapshot they are derived from the catalog.
- **COPY bypasses EF validation,** so the generators are covered by their own tests (edge cases, determinism, pinned RNG).
- **COPY is Postgres-specific.** SQL Server parity would use `SqlBulkCopy` (README §16).
