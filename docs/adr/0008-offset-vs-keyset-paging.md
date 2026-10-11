# ADR-0008: OFFSET paging with a deterministic tie-breaker, and sort indexes

- **Status:** Accepted
- **Date:** 2026-10-08
- **Phase / PR:** phase-3/positions-api

## Context

AG Grid's Infinite Row Model asks for blocks by row number (`startRow`, `endRow`). A user can drag the scrollbar to row 15,000 directly, without reading the blocks in between. Paging must therefore:

- **Be stable:** concatenating all blocks gives every position exactly once under any sort (README §11).
- **Be fast at any depth:** API p95 ≤ 150 ms.

The snapshot rows are wide: about 5 rows per 8 KB page. The table's two as-of dates (40,002 rows at scale 1.0) fill about 8,060 pages, which a seq scan reads in full.

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

**All four sort indexes** (#133, carrying #48 AC1). Seed 42, scale 1.0 (20,001 rows on the latest as-of date, 2026-10-06), PostgreSQL 17.11 in Docker, Apple M5, local. First block (200 rows) of the unfiltered Risk view across every portfolio, in the exact shape `GridSqlBuilder.Build` emits. Each plan is the second of two runs, so the cache is warm. "Before" drops the index in a transaction that is rolled back.

| Index | Direction | Before: plan | Before: buffers / ms | After: plan | After: buffers / ms |
|---|---|---|---:|---|---:|
| `ix_snapshot_sort_market_value` | DESC | seq scan + top-N heapsort | 8,064 / 25.6 | Index Scan Backward | 204 / 0.15 |
| | ASC | seq scan + top-N heapsort | 8,064 / 16.3 | Index Scan | 199 / 0.14 |
| `ix_snapshot_sort_spread_bp` | DESC | seq scan + top-N heapsort | 8,064 / 16.7 | Index Scan Backward | 205 / 0.14 |
| | ASC | seq scan + top-N heapsort | 8,064 / 18.5 | Index Scan | 204 / 0.15 |
| `ix_snapshot_sort_dv01` | DESC | seq scan + top-N heapsort | 8,064 / 19.9 | Index Scan Backward | 205 / 0.15 |
| | ASC | seq scan + top-N heapsort | 8,064 / 38.6 | Index Scan | 200 / 1.66 |
| `ix_snapshot_sort_deal_name` | DESC | seq scan + top-N heapsort | 8,064 / 17.8 | Index Scan Backward | 205 / 0.15 |
| | ASC | seq scan + top-N heapsort | 8,064 / 18.5 | Index Scan | 204 / 0.13 |

- The planner uses every index for the first block in both directions, so none is dropped (#133's rule: an index with no plan using it at production-shaped volume would be removed). The dv01 ASC rows (38.6 ms before, 1.66 ms after) are a slower run of the same plans; buffers are unchanged.
- **Scope:** unfiltered, every portfolio, first block. The Index Cond is `as_of_date` only; `portfolio_id` and any grid filter are heap Filters, so a portfolio subset, a filtered view or a deep page reads more rows than this. Those cases weren't measured here (a follow-up for Tech Coordinator to file).
- The full plan text is committed: [`perf/evidence/explain-sort-indexes-scale1.txt`](../../perf/evidence/explain-sort-indexes-scale1.txt).
- One btree serves both directions, at ~1.6 MB per index at scale 1.0.
- Beyond about 10k rows the planner switches OFFSET to a parallel seq scan + sort, which is why the last block (14.8 ms) is cheaper than the middle (24.4 ms).
- With all four indexes the database is 278 MB, under the 350 MB budget.

**How to reproduce:**

```
DATABASE_URL=… dotnet run -c Release --project perf/GridBenchmark -- 200
# local compose stack only (the script refuses any other database); every index, both directions, before and after:
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

## Addendum (2026-10-08, #188): sort plans beyond the unfiltered first block

#133 measured only the unfiltered, all-portfolio first block. Here the four sort indexes run against the other shapes the grid can reach: portfolio subsets, set and range filters, and deep OFFSET blocks.

**Setup.** Seed 42, scale 1.0 (20,001 rows on 2026-10-06), PostgreSQL 17.11 in Docker, Apple M5, local, default `work_mem` 4 MB and `random_page_cost` 4. The database was a throwaway seeded for the run and dropped afterwards. Each case is the page query `GridSqlBuilder.Build` emits (Risk columns, 200 rows), run for all 4 sort columns × both directions, as the second of two runs (warm cache).

Each case also runs a **no index** twin, sorted by `X + 0` (or `X || ''`), so no sort index can serve it. That shows what the case would cost without the index, with no DDL. The script runs 304 plans in about 5 s.

```
docker exec -i <postgres container> sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -q' < perf/explain-sort-plans.sql
```

Full plans and the per-plan summary table: [`perf/evidence/explain-sort-plans-scale1.txt`](../../perf/evidence/explain-sort-plans-scale1.txt). Ranges below cover the 8 sorts. Buffers are shared hits on the top node.

| Shape | Rows in scope | Planner's plan (indexes present) | Buffers | ms | No index: ms |
|---|---:|---|---:|---:|---:|
| All books, first block (#133) | 20,001 | sort index | 200–207 | 0.12–0.17 | 19.2–24.9 |
| Book 12 (smallest) | 703 | bitmap on `ix_snapshot_portfolio` + top-N | 146 | 0.52–0.62 | same plan |
| Book 1 (largest) | 5,634 | sort index, ~500 rows filtered | 687–778 | 0.19–0.23 | 4.1–4.5 |
| Books [3,7] (README §6) | 2,462 | sort index, 1.4k–1.7k rows filtered | 1,574–1,884 | 0.29–0.37 | 1.8–2.5 |
| Book 12 + rating {BB,B,CCC} | 166 | bitmap + quicksort | 146 | 0.21–0.23 | same plan |
| Set rating {BB,B,CCC} | 4,736 | sort index | 206–11,052 | 0.12–2.08 | 8.6–10.3 |
| Set sector {CRT} | 981 | seq scan + top-N | 8,064 | 5.5–6.0 | 5.3–9.5 |
| Set rating {CCC} (rarest) | 137 | seq scan + quicksort | 8,064 | 4.8–8.5 | 4.6–9.5 |
| Range dv01 < 1.77 (bottom quartile) | 4,991 | sort index | 200–11,366 | 0.12–2.83 | 4.3–4.8 |
| Range spread_bp in (500, 1000) | 3,782 | sort index | 204–1,189 | 0.11–0.28 | 3.3–3.6 |
| OFFSET 2,000 | 20,001 | sort index | 2,209–2,234 | 1.15–1.37 | 18.7–22.3 |
| OFFSET 10,000 | 20,001 | seq scan + external merge (6.3 MB to disk) | 8,064 | 21.3–29.0 | 22.7–30.0 |
| Last block (OFFSET 19,801) | 20,001 | parallel seq scan + external merge (2.4 MB) | 8,156–8,160 | 14.5–23.9 | 15.9–21.3 |
| OFFSET 10,000, `work_mem` 16 MB | 20,001 | seq scan + quicksort (no spill) | 8,064 | 19.1–23.7 | 20.3–28.2 |
| Last block, `work_mem` 16 MB | 20,001 | seq scan + quicksort (no parallel workers) | 8,064 | 18.2–26.3 | 20.4–27.6 |
| OFFSET 10,000, `random_page_cost` 1.1 | 20,001 | sort index | 10,259–10,347 | 5.7–6.3 | 21.4–26.7 |
| Last block, `random_page_cost` 1.1 | 20,001 | sort index | 20,118–20,279 | 12.4–14.9 | 23.4–30.5 |

**Findings**
- **No shape is clearly bad.** Wherever the planner picks a sort index, it is faster than the same query without it: the worst indexed shape is 2.8 ms against 4.8 ms. Where the index wouldn't help (one small book, a very selective set filter), the planner doesn't use it.
- **Portfolio subsets.** A single small book uses `ix_snapshot_portfolio` (146 buffers). A larger book or a subset walks the sort index and filters out the other books' rows: up to 1.9k buffers, still under 0.4 ms.
- **Filters correlated with the sort key** are the expensive index case. Two of them read more buffers than the whole-table seq scan (8,064), yet still finish in 2–3 ms warm:
  - sorting `spread_bp ASC` under the high-spread ratings {BB,B,CCC}: 11,052 buffers, 10,774 rows filtered;
  - sorting `market_value DESC` under the bottom dv01 quartile: 11,366 buffers.

  An index walk is bounded by the date's 20,001 entries (~20k buffers, ~13 ms; see the last block under `random_page_cost` 1.1).
- **Plan choice for mid-selective filters depends on statistics.** In a first run on an identical database (same seed 42, seeded fresh, so `ANALYZE` drew a different sample), sector {CRT} picked the sort index (2.2k–5.8k buffers, 0.4–1.0 ms) where this run picked the seq scan (5.5–6.0 ms). Either plan is fast.
- **Deep OFFSET is still the cost centre, as #133 already found.** From about 10k rows the planner drops the index for a seq scan and an external merge sort (21–29 ms). Raising `work_mem` to 16 MB removes the spill but saves nothing: the sort was never the bottleneck, and the last block loses its parallel workers. Lowering `random_page_cost` to 1.1 keeps the index: 5.7–6.3 ms at 10k and 12.4–14.9 ms at the last block. It also makes deep blocks on an **unindexed** sort column slower (via `ix_snapshot_portfolio` + external sort, 21–31 ms against 16–21 ms), and on Neon the heap fetches may be cold reads.
- **Budget.** The worst block is 29 ms locally, against P1 p95 ≤ 150 ms on a MISS. That leaves room for Neon compute several times slower. The MISS also runs the summary aggregate over the filter, which this addendum doesn't measure.

**Decision**
- **No index change.** Targeted `(as_of_date, portfolio_id, X)` indexes would save under 2 ms on any shape measured here, and each costs about the size of a sort index again within the 350 MB budget.
- **No `work_mem` change**, for the reason above.
- **`random_page_cost` 1.1 for the grid role is a candidate, not a decision.** It wins 4× on indexed deep blocks and loses on unindexed ones, and it needs Neon's real setting and cold-read behaviour first. Proposed in #326; nothing is implemented here.
