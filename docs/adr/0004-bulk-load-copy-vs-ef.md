# ADR-0004: Bulk load: Npgsql binary COPY vs EF Core AddRange

- **Status:** Accepted
- **Date:** 2026-10-07
- **Phase / PR:** phase-1/data

## Context

The seeder loads about **1.56 million rows** (20 tables, including the 202-column snapshot). It runs in GitHub Actions against Neon on every seed-version change (README §14.2), within a < 90 s local budget (README §10). EF Core is already the stable-model data access, so the question is whether it should do bulk loading too. README §5.5 requires this ADR to measure **the snapshot table**, not a narrower stand-in.

## Options considered

1. **Npgsql binary COPY** (`BeginBinaryImport`): rows streamed in Postgres' binary format, no SQL per row.
2. **EF Core `AddRange` + `SaveChanges`** (batched INSERTs, change tracking off).
3. **Multi-row `INSERT … VALUES` through Dapper.** Not measured: it's the same per-statement path as option 2 without EF's overhead, and still parses SQL per batch.

## Evaluation

### Snapshot table (README §5.5)

Same 20,000 generated `core.position_snapshot` rows (202 columns from `ColumnCatalog`), each loaded into an identical scratch table. One Release run, local Postgres 17 (Docker), linux-x64:

```
rows=20000  shape=core.position_snapshot (202 columns)
| Method | Rows | Seconds | Rows/s | Managed alloc (MB) |
|---|---|---|---|---|
| Npgsql binary COPY | 20,000 | 0.86 | 23,192 | 21 |
| EF Core AddRange + SaveChanges | 20,000 | 21.80 | 917 | 4207 |
COPY is 25.3x faster
```

- **COPY is 25.3× faster** and allocates **about 200× less** (21 MB vs 4.2 GB).
- At ~917 rows/s, EF would need on the order of a minute just for the two snapshot dates (40,002 rows), before history, trades and marks.

### Earlier history-table check (not the §5.5 requirement)

Same 100,000 generated `position_history` rows (10 columns), five runs, Apple M5, recorded when the bench first landed:

| Method | Median s | Range s | Rows/s (median) | Managed alloc |
|---|---|---|---|---|
| Npgsql binary COPY | 0.42 | 0.31–0.49 | ~238,000 | 23 MB |
| EF Core AddRange + SaveChanges | 4.10 | 3.92–5.11 | ~24,000 | 1,267–1,271 MB |

COPY was about 9.8× faster on that narrow shape. The snapshot measurement above is the one this ADR decides on.

### Whole seed

`--if-changed --scale 1.0 --as-of 2026-10-06` against a migrated empty Postgres 17: **1,563,791 rows**, `DB_SIZE_MB=270`, `ELAPSED_S=11.1`. A later `--force` committed at 271 MB in 11.5 s. Peak `pg_database_size` during that force reseed was **533.8 MB** (old relfilenodes retained until COMMIT; `SEED_PEAK_EST_MB=542`). First deploy after this PR starts from empty phase-1 tables, so the peak is about the committed size.

**How to reproduce:**

```
set -a; . ./.env; set +a
dotnet run -c Release --project perf/LoadBenchmark -- 20000
```

The benchmark creates and drops its own `bench` schema.

## Decision

Option 1: binary COPY for every seeded table (`Loader.Copy`), all inside **one transaction** (TRUNCATE, then reload). A failed or over-budget reseed rolls back before COMMIT, so the previous data and `app.seed_metadata` stay untouched. EF Core stays for the stable model: migrations, the metadata read, and later accounts and presets.

The seed transaction sets `lock_timeout = 15s` and `statement_timeout = 180s`. Later reseeds print `SEED_PEAK_EST_MB` because TRUNCATE in a transaction keeps old files until COMMIT (~2×). Confirm the Neon project cap before a production `--force`.

**Note (#109):** the seeder now refuses a reseed before TRUNCATE (exit 2, nothing changed) when `SEED_PEAK_EST_MB` exceeds `--cap-mb` (default 512 MB) or the current size can't be read. At that default a scale-1.0 reseed of a full book (~542 MB estimated) is refused until the cap is confirmed; see README §10.

## Consequences

- **Column lists and `NpgsqlDbType`s are kept beside each table load,** and a type mismatch fails loudly at load time. For the snapshot they are derived from the catalog.
- **COPY bypasses EF validation,** so the generators are covered by their own tests (edge cases, determinism, pinned RNG).
- **COPY is Postgres-specific.** SQL Server parity would use `SqlBulkCopy` (README §16).
