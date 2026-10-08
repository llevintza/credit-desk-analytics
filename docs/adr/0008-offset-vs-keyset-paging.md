# ADR-0008: OFFSET paging with a deterministic tie-breaker, and sort indexes

- **Status:** Accepted
- **Date:** 2026-10-08
- **Phase / PR:** phase-3/positions-api

## Context

AG Grid's Infinite Row Model asks for blocks by row number (`startRow`, `endRow`). A user can drag the scrollbar to row 15,000 directly, without reading the blocks in between. Paging must therefore:

- **Be stable:** concatenating all blocks gives every position exactly once under any sort (README §11).
- **Be fast at any depth:** API p95 ≤ 150 ms.

The snapshot rows are wide: about 2.5 rows per 8 KB page, 20,001 rows in about 7,800 pages per as-of.

## Options considered

1. **OFFSET … FETCH**, with `position_id` appended to every `ORDER BY` as a tie-breaker.
2. **Keyset (seek):** `WHERE (sort_key, position_id) < (@last_key, @last_id)`. It needs the previous block's last key.
3. **A hybrid:** keyset when the grid asks for the next block in order, OFFSET after a jump.

## Evaluation

The page query alone: 200 rows × the Risk columns, sort `market_value DESC`, with the `(as_of_date, market_value, position_id)` index. Seed 42, scale 1.0, Postgres 17.11, Apple M5, p50 of 200 runs.

| Start row | OFFSET ms | keyset ms |
|---:|---:|---:|
| 0 | 1.44 | 1.42 |
| 2,000 | 2.39 | 1.46 |
| 10,000 | 24.44 | 1.45 |
| 19,801 (last block) | 14.78 | 1.38 |

**Index evidence** from `EXPLAIN (ANALYZE, BUFFERS)`, first block sorted by `market_value`:

| Plan | Buffers | Time |
|---|---:|---:|
| No sort index: seq scan + top-N heapsort | 8,070 | 26.2 ms |
| `(as_of_date, market_value DESC NULLS LAST, position_id)` | 203 | 0.6 ms, but ascending fell back to a seq scan (17.5 ms) |
| `(as_of_date, market_value, position_id)` with the tie-breaker in the first key's direction and default NULL placement | — | **0.18 ms DESC (backward scan), 0.35 ms ASC** |

**All four sort indexes** (#133, carrying #48 AC1). Seed 42, scale 1.0 (20,001 rows on 2026-10-06), Postgres 17, local. First block (200 rows) of the unfiltered Risk view across every portfolio, in the exact shape `GridSqlBuilder.Build` emits. Each plan is the second of two runs, so the cache is warm. "Before" drops the index in a transaction that is rolled back.

| Index | Direction | Before: plan | Before: buffers / ms | After: plan | After: buffers / ms |
|---|---|---|---:|---|---:|
| `ix_snapshot_sort_market_value` | DESC | seq scan + top-N heapsort | 8,064 / 29.8 | Index Scan Backward | 204 / 0.16 |
| | ASC | seq scan + top-N heapsort | 8,064 / 18.7 | Index Scan | 199 / 0.14 |
| `ix_snapshot_sort_spread_bp` | DESC | seq scan + top-N heapsort | 8,064 / 17.8 | Index Scan Backward | 205 / 0.15 |
| | ASC | seq scan + top-N heapsort | 8,064 / 20.8 | Index Scan | 204 / 0.21 |
| `ix_snapshot_sort_dv01` | DESC | seq scan + top-N heapsort | 8,064 / 20.9 | Index Scan Backward | 205 / 0.17 |
| | ASC | seq scan + top-N heapsort | 8,064 / 18.6 | Index Scan | 200 / 0.15 |
| `ix_snapshot_sort_deal_name` | DESC | seq scan + top-N heapsort | 8,064 / 18.0 | Index Scan Backward | 205 / 0.16 |
| | ASC | seq scan + top-N heapsort | 8,064 / 22.9 | Index Scan | 204 / 0.19 |

- The planner uses every index for the first block in both directions, so none is dropped (#133's rule: an index with no plan using it at production-shaped volume would be removed).
- The full plan text is in the #133 PR body.
- One btree serves both directions, at ~1.6 MB per index at scale 1.0.
- Beyond about 10k rows the planner switches OFFSET to a parallel seq scan + sort, which is why the last block (14.8 ms) is cheaper than the middle (24.4 ms).
- With all four indexes the database is 278 MB, under the 350 MB budget.

**How to reproduce:**

```
DATABASE_URL=… dotnet run -c Release --project perf/GridBenchmark -- 200
# local stack only; every index, both directions, before and after:
docker exec -i <postgres container> sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -q' < perf/explain-sort-indexes.sql
```

## Decision

**Paging**
- **OFFSET … FETCH** for every block. `position_id` is always the last sort key, in the first key's direction.
- Keyset is flat (~1.4 ms at any depth), but it can't serve a scrollbar jump without first finding the boundary key at that offset, which is the same OFFSET cost.
- Even the worst OFFSET block (24 ms) is far inside the 150 ms budget, and a hybrid would double the code paths for a saving the user can't see.

**Sorting and indexes**
- **Default NULL placement** (nulls last ascending, first descending), so one ascending btree serves both directions.
- **Sort indexes** on `(as_of_date, X, position_id)` for the columns a desk sorts by most: `market_value`, `spread_bp`, `dv01`, `deal_name`. They are in migration `SnapshotSortIndexes`, an additive change.

## Consequences

- Deep OFFSET blocks cost up to ~25 ms locally. Neon will be slower but still within budget, and every block is cached until the next batch.
- Sorting by an unindexed column costs one seq scan + sort per new view (≈ 15–26 ms locally).
- Adding an index is a migration plus about 1.6 MB per column at scale 1.0.
- Revisit with the hybrid if the snapshot grows by 10×, or if traces show deep blocks dominate.
