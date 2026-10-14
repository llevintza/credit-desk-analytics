# ADR-0013: "Any one bond per deal": LEFT JOIN LATERAL … LIMIT 1 vs ROW_NUMBER() = 1

- **Status:** Accepted
- **Date:** 2026-10-13
- **Phase / PR:** phase-7/deal-explorer

## Context

The Deal Explorer (README §6 P4) lists every deal with one representative bond: the most senior tranche, ties broken by the lowest bond id. Two rules come with it:

- **Every deal appears, even with zero bonds.** The seed has three newly announced deals with no tranches yet (README §5.2). That is SQL Server's `OUTER APPLY`; an inner join or `CROSS APPLY` would drop them.
- **Deal-level totals don't double count** (README §11). The list also shows each deal's bond count and current balance and what the caller's portfolios hold of it, so child rows must be aggregated per deal before they meet the deal row.

The data, at scale 1.0: 1,500 deals, 9,319 bonds (about 6 per deal), 20,001 positions per as-of date. `core.bond` has had the index `ix_bond_deal_seniority (deal_id, seniority_rank, bond_id)` since phase 1, so "the first bond of a deal in seniority order" is the first entry of an index range.

## Options considered

1. **`LEFT JOIN LATERAL (… ORDER BY seniority_rank, bond_id LIMIT 1) ON true` (chosen).** For each deal row, a correlated subquery takes the first bond. On the index that is one seek per deal, stopping at the first entry. It is the direct Postgres spelling of `OUTER APPLY (SELECT TOP 1 …)`.
2. **`ROW_NUMBER() OVER (PARTITION BY deal_id ORDER BY seniority_rank, bond_id) = 1` + `LEFT JOIN`.** Number every bond in a subquery, keep `rn = 1`, left-join it to the deals. Portable ANSI SQL; Postgres 17 adds a run condition to stop numbering each partition after row 1, but it still reads every bond.
3. **`DISTINCT ON (deal_id) … ORDER BY deal_id, seniority_rank, bond_id` + `LEFT JOIN`.** Postgres-only; one ordered pass over all bonds, keep the first per deal. Measured for reference.

All three keep the zero-bond deals (the `LEFT JOIN` / `ON true` keeps a deal with no match, its bond columns null), and all three return the same bond per deal: `The_representative_bond_is_the_most_senior_then_lowest_id` checks the endpoint's LATERAL result against an independent `ROW_NUMBER()` query.

## Evaluation

`perf/explain-deal-representative.sql` runs each option in three shapes:

- **rep only:** README §6's snippet, every deal and its representative bond.
- **rep only, one sector:** the same, filtered to the 116 CRT deals first: the cost of a filtered or paged list.
- **list:** `DealRepository.ListSql`'s full shape, which adds per-deal bond totals and the caller's holdings over all 12 portfolios.

Each shape runs with the index (as written) and with "order not indexed" (`ORDER BY seniority_rank + 0`), the plan the query would get with an index on `deal_id` alone. Every query runs twice to warm up, prints its `EXPLAIN (ANALYZE, BUFFERS)` plan, then runs 30 more times for p50 / p95 execution time.

Seed 42, scale 1.0, as-of 2026-10-06; PostgreSQL 17.11 in Docker (Colima) on an Apple M5; the machine is shared, so the tails are noisy. Every row returns 1,500 deals (116 for one sector) with 3 (1) null-bond rows: all options keep the zero-bond deals.

| Shape | Option | Index | Plan (nodes) | shared buffers | p50 ms | p95 ms |
|---|---|---|---|---:|---:|---:|
| rep only | **1. LATERAL … LIMIT 1** | yes | Nested Loop Left Join > Index Scan deal_pkey > Limit > Index Scan ix_bond_deal_seniority | 4,525 | **0.84** | **1.68** |
| rep only | 2. ROW_NUMBER() = 1 | yes | Merge Left Join > Index Scan deal_pkey > WindowAgg > Index Scan ix_bond_deal_seniority | 208 | 1.80 | 9.37 |
| rep only | 3. DISTINCT ON | yes | Merge Left Join > Index Scan deal_pkey > Unique > Index Scan ix_bond_deal_seniority | 208 | 1.08 | 1.92 |
| rep only | 1. LATERAL … LIMIT 1 | order not indexed | … > Limit > Sort > Index Scan (deal_id prefix) | 4,645 | 2.31 | 3.28 |
| rep only | 2. ROW_NUMBER() = 1 | order not indexed | … > WindowAgg > Sort > Seq Scan bond | 171 | 1.80 | 2.46 |
| rep only | 3. DISTINCT ON | order not indexed | … > Unique > Sort > Seq Scan bond | 171 | 1.55 | 1.93 |
| one sector | **1. LATERAL … LIMIT 1** | yes | Nested Loop Left Join > Seq Scan deal > Limit > Index Scan ix_bond_deal_seniority | 370 | **0.11** | **0.19** |
| one sector | 2. ROW_NUMBER() = 1 | yes | Merge Left Join > WindowAgg > Index Scan ix_bond_deal_seniority (all bonds) | 203 | 1.35 | 1.98 |
| one sector | 3. DISTINCT ON | yes | Merge Left Join > Unique > Index Scan ix_bond_deal_seniority (all bonds) | 203 | 0.79 | 0.88 |
| list | **1. LATERAL … LIMIT 1** | yes | holdings HashAggregate + bond HashAggregate, then Nested Loop > Limit > Index Scan | 12,727 | **13.72** | 23.55 |
| list | 2. ROW_NUMBER() = 1 | yes | the same aggregates, Merge Left Join > WindowAgg | 8,410 | 13.84 | 15.84 |
| list | 3. DISTINCT ON | yes | the same aggregates, Merge Left Join > Unique | 8,410 | 11.60 | 14.64 |

What the numbers say:

- **Whole table, the options are within a millisecond.** With 6 bonds per deal, one ordered pass over 9,319 bonds (options 2 and 3, about 200 buffers) costs about the same as 1,500 index seeks (option 1, about 4,500 buffer hits, all in cache). LATERAL was fastest at p50 in this run and DISTINCT ON in an earlier run (1.38 vs 1.41 ms); ROW_NUMBER() was last in both.
- **Filtered, LATERAL wins by an order of magnitude.** With the deals filtered first, LATERAL seeks only for the 116 deals that survive: 0.11 ms vs 1.35 ms for ROW_NUMBER(), which still numbers all 9,319 bonds because the join qual can't be pushed below the window. The same holds for a paged list or a single deal, and it grows with the bond table, not the page.
- **The index is what makes LATERAL cheap.** Without an index that serves the order, each seek has to sort the deal's bonds (`Limit > Sort`) and LATERAL becomes the slowest option for the whole table (2.31 ms). The index already exists for this query.
- **The full list is dominated by holdings, not by the representative bond.** About 12 ms of the 14 ms is the aggregate over the caller's 20,001 positions; the three options are within noise of each other there. The response is cached per (as-of, data version, portfolios) until the next batch, so a repeat view never runs it.
- **Grain.** Bond totals and holdings are each aggregated per deal in their own CTE before the join. Joining deal → bond → position first and then summing would repeat a deal's held MV once per tranche; `Deal_totals_are_counted_once_per_bond_and_once_per_position` shows the naive join differs on every held multi-tranche deal and the endpoint matches independent per-deal sums.

**How to reproduce** (a seeded local database only, never Render, Neon or production; read-only apart from `pg_temp`):

```
docker run -d --name desk-p7 -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=creditdesk -e POSTGRES_USER=desk -p 55437:5432 postgres:17-alpine
DATABASE_URL='Host=localhost;Port=55437;Database=creditdesk;Username=desk' dotnet ef database update --project src/Desk.Data --startup-project src/Desk.Data
DATABASE_URL='Host=localhost;Port=55437;Database=creditdesk;Username=desk' dotnet run -c Release --project src/Desk.Seeder -- --if-changed --as-of 2026-10-06
docker exec -i desk-p7 sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -q' < perf/explain-deal-representative.sql > perf/evidence/explain-deal-representative-scale1.txt
```

The full output, every plan included, is in `perf/evidence/explain-deal-representative-scale1.txt`; its summary table:

```
        shape         |           approach           |      variant      | rows_out | null_bond_rows | buffers | p50_ms | p95_ms | max_ms
----------------------+------------------------------+-------------------+----------+----------------+---------+--------+--------+--------
 rep only             | LATERAL ... LIMIT 1          | index             |     1500 |              3 |    4525 |   0.84 |   1.68 |   1.89
 rep only             | ROW_NUMBER() = 1 + LEFT JOIN | index             |     1500 |              3 |     208 |   1.80 |   9.37 |  15.81
 rep only             | DISTINCT ON + LEFT JOIN      | index             |     1500 |              3 |     208 |   1.08 |   1.92 |   2.05
 rep only             | LATERAL ... LIMIT 1          | order not indexed |     1500 |              3 |    4645 |   2.31 |   3.28 |   3.55
 rep only             | ROW_NUMBER() = 1 + LEFT JOIN | order not indexed |     1500 |              3 |     171 |   1.80 |   2.46 |   2.57
 rep only             | DISTINCT ON + LEFT JOIN      | order not indexed |     1500 |              3 |     171 |   1.55 |   1.93 |   2.15
 rep only, one sector | LATERAL ... LIMIT 1          | index             |      116 |              1 |     370 |   0.11 |   0.19 |   0.32
 rep only, one sector | ROW_NUMBER() = 1 + LEFT JOIN | index             |      116 |              1 |     203 |   1.35 |   1.98 |   2.16
 rep only, one sector | DISTINCT ON + LEFT JOIN      | index             |      116 |              1 |     203 |   0.79 |   0.88 |   0.99
 rep only, one sector | LATERAL ... LIMIT 1          | order not indexed |      116 |              1 |     371 |   0.20 |   0.34 |   0.49
 rep only, one sector | ROW_NUMBER() = 1 + LEFT JOIN | order not indexed |      116 |              1 |     166 |   1.64 |   2.20 |   2.66
 rep only, one sector | DISTINCT ON + LEFT JOIN      | order not indexed |      116 |              1 |     166 |   1.44 |   1.58 |   1.71
 list                 | LATERAL ... LIMIT 1          | index             |     1500 |              3 |   12727 |  13.72 |  23.55 |  31.51
 list                 | ROW_NUMBER() = 1 + LEFT JOIN | index             |     1500 |              3 |    8410 |  13.84 |  15.84 |  17.96
 list                 | DISTINCT ON + LEFT JOIN      | index             |     1500 |              3 |    8410 |  11.60 |  14.64 |  17.16
 list                 | LATERAL ... LIMIT 1          | order not indexed |     1500 |              3 |   12847 |  13.74 |  15.73 |  16.10
 list                 | ROW_NUMBER() = 1 + LEFT JOIN | order not indexed |     1500 |              3 |    8373 |  14.16 |  21.96 |  25.47
 list                 | DISTINCT ON + LEFT JOIN      | order not indexed |     1500 |              3 |    8373 |  14.92 |  20.65 |  22.39
```

## Decision

`LEFT JOIN LATERAL (… ORDER BY seniority_rank, bond_id LIMIT 1) ON true` on `ix_bond_deal_seniority`, as README §6 sketches. It ties on the whole table, it costs only the deals that survive a filter or a page (10× faster for one sector), and it keeps the "most senior, then lowest id" rule in one readable subquery. Child totals (bond count and balance, holdings) are aggregated per deal in CTEs before they join the deal row.

## Consequences

- **Easier:** a later filtered, paged or single-deal list costs only its rows; the rule for "representative" lives in one `ORDER BY`.
- **Depends on the index.** Dropping or reordering `ix_bond_deal_seniority` makes LATERAL the slowest option (each seek sorts). A migration that touches it must re-run this script.
- **Dialect.** `LATERAL` maps to SQL Server's `OUTER APPLY (SELECT TOP 1 …)` (README §16); `ROW_NUMBER()` would have been the portable form. `DISTINCT ON` has no SQL Server equivalent, which is why it is measured but not chosen even where it ties.
- **Scope.** Deal and tranche terms are desk-wide reference data and every deal is listed; only what the portfolios hold (position count, held face and MV, and the detail's positions) is portfolio-owned and scoped in SQL with `portfolio_id = ANY(@portfolios)` (ADR-0021). With an empty entitlement the list still shows the deal universe, with no position rows and null held columns.
- **Revisit** if deals grow by orders of magnitude (then the list itself should page, which favours LATERAL further) or if `core` moves to SQL Server.
