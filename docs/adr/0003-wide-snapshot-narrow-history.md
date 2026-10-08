# ADR-0003: Wide position snapshot + narrow history

- **Status:** Accepted
- **Date:** 2026-10-07
- **Phase / PR:** phase-1/data

## Context

The start-of-day grid (P1) shows 20,000 positions × ~200 measures for **today**: Excel-like, sortable and filterable on any column (README §6). Trend views need a few measures over **many** month-ends. Everything must fit Neon's free 0.5 GB (README §5.4), and the grid query must stay a single indexed read.

## Options considered

1. **Wide snapshot (one row per position per as-of date, ~200 typed columns) + narrow history** (one row per position per month-end, the 8 key measures).
2. **Entity–attribute–value (EAV):** one row per (position, measure), pivoted at query time.
3. **JSONB:** one row per position with the measures in a `jsonb` document.
4. **Wide for every date:** keep all 24 month-ends at full width.

## Evaluation

| Criterion | 1. Wide + narrow | 2. EAV | 3. JSONB | 4. Wide for every date |
|---|---|---|---|---|
| Grid read for one page | One index range scan on `(as_of_date, portfolio_id)`, columns read directly | Pivot of ~200 rows per position (~4M rows per as-of) | Extracting and casting each measure from JSON | Same as 1 |
| Sort/filter on any measure | Native typed column; indexable | Join or pivot first | Expression index per measure | Native |
| Type safety (money as `numeric`) | Yes | Every value shares one column type | Only by convention | Yes |
| **Measured size** (scale 1.0) | **271 MB whole DB** (snapshot 40,002 rows × 202 columns; history 480,024 rows) | Estimated: ~8M rows per as-of × ~40 B ≈ 320 MB for the snapshot alone | Larger than 1 (keys stored per row) | Estimated: 24 × ~38 MB ≈ 900 MB (over budget) |
| Schema evolution | New column = migration (pinned to the catalog by a test) | No DDL | No DDL | Migration |

**How to reproduce:**
- `dotnet run -c Release --project src/Desk.Seeder -- --force --scale 1.0 --as-of 2026-10-06`. It prints `DB_SIZE_MB=271`.
- Postgres 17 (compose), Apple M5.

## Decision

Option 1:
- `core.position_snapshot` is wide, with today and the prior business day: 202 columns, primary key `(as_of_date, position_id)`, index `(as_of_date, portfolio_id) INCLUDE (position_id)`.
- `core.position_history` is narrow: 24 month-ends × 10 columns.

The columns come from a single **column catalog** (`ColumnCatalog.cs`). The same catalog produces:
- the DDL (pinned in the migration by a unit test)
- `app.column_catalog`
- the API whitelist (phase 3)
- the UI column definitions (phase 4)

## Consequences

- **Adding a measure takes a migration plus a catalog entry.** The pin test fails if only one of them changes.
- **Trends are limited to the measures in the narrow table.** A new trend measure means widening history deliberately.
- **Only two snapshot dates are kept.** That keeps the database at about 271 MB of the 350 MB budget, with headroom.
- **Business days are weekend-only.** README §5.1 lists a `reference` holiday calendar; this phase uses Saturday/Sunday only (`PreviousBusinessDay` / `LastBusinessDayOfMonth`). A synthetic US holiday table is deferred until a blotter needs it.
- **Fact-table FKs are logical, not declared.** Snapshot, history, trades, remittances and marks carry ids that match the dimension tables, but the DDL omits those foreign keys so binary COPY is not blocked by check order. Portfolio→fund, bond→deal and the fund child tables are declared.
