# ADR-0012: P3 insights: one endpoint per source, one connection per grid

- **Status:** Accepted
- **Date:** 2026-10-09
- **Phase / PR:** phase-6/insights-board

## Context

The Insights Board (README §6 P3) shows 20 small grids from five data sources: core 6, market 3, surveillance 5, pricing 3, reference 3. Each grid is one group-by over the caller's book, about 5 × 5. The page must paint progressively: a slow source shows skeletons while the others are drawn, and a failing source shows only its own error tiles.

The budgets are first tile < 500 ms and all tiles < 1.5 s warm. The constraints:

- **Free tiers:**
  - per-user token bucket of 60 requests per minute with a burst of 20;
  - a global limit of 8 concurrent `/api` requests (README §7.2);
  - a 20-connection Npgsql pool per database (`DB_MAX_POOL_SIZE`, README §13.1).
- **Correctness:**
  - EF Core and Npgsql both refuse a second command on a context or connection that already has one in flight;
  - every grid must be scoped to the caller's portfolios (ADR-0021).

Two questions are decided together:

1. how the browser asks for the 20 grids;
2. how the server runs them.

A third choice, plain tables or AG Grid tiles, is recorded at the end.

## Options considered

1. **One call, one connection or DbContext.** `GET /api/insights` returns all 20 grids, run one after another on one connection. That is what a single request-scoped DbContext forces.
2. **One call, one connection per grid.** The same single response, but the 20 queries run in parallel. Each query runs as its own task on its own pooled connection.
3. **One call per source, one connection per grid (chosen).** `GET /api/insights/{source}`, fired as five calls at once.
   - Inside a call, the source's grids run in parallel, each on its own connection.
   - Each call is capped at `INSIGHTS_PARALLELISM` (default 4).
   - The whole process is capped at `INSIGHTS_MAX_CONNECTIONS` (default 10).
4. **20 calls, one per grid.** `?grid=<id>`, the board's "naive mode". The browser opens at most 6 HTTP/1.1 connections per origin.

## Evaluation

The 20 grids are uncached, so every grid hits the database. The run uses all 12 portfolios at as-of 2026-10-06.

- **Data:** seed 42, scale 1.0.
- **Database:** PostgreSQL 17.11 in Docker (Colima).
- **Machine:** Apple M5, .NET 10.0.12.
- **Iterations:** 30, interleaved. Each iteration runs all four approaches in a rotating order, so load drift on the shared machine falls on all of them alike.

"First" is when the first grid could paint; "all" is when the last one could. A response paints only when it is complete, so a single call paints nothing until all 20 grids are done.

| Approach | requests per view | first grid p50 / p95 ms | all grids p50 / p95 ms | connections per view | largest response (JSON / br) | total br bytes |
|---|---:|---:|---:|---:|---:|---:|
| 1. One call, one connection (serial) | 1 | 282.5 / 389.4 | 282.5 / 389.4 | 1 | 13,118 / 5,716 | 5,716 |
| 2. One call, per-task connections | 1 | 112.2 / 139.9 | 112.2 / 139.9 | 20 | 13,118 / 5,716 | 5,716 |
| **3. Per-source calls, per-task connections** | **5** | **48.0 / 71.6** | **90.9 / 114.1** | 20 | 4,327 / 2,185 | 6,392 |
| 4. 20 calls, one per grid (6 at a time) | 20 | 9.7 / 20.3 | 105.4 / 193.8 | 20 | 1,224 / 712 | 8,558 |

What the numbers say, beyond the table:

- **Serial is the floor's cost.** One shared connection means one command at a time: 2.5× option 2 and 3.1× option 3 for all tiles. With a shared DbContext or connection it can't be parallel at all. `InsightsConcurrencyTests` shows the shared context throws `InvalidOperationException` ("a second operation was started"). It also shows the shared connection throws `NpgsqlOperationInProgressException`, while one context or connection per task passes.
- **One call can't paint progressively.** Even run in parallel, its first tile waits for the slowest of 20 grids, here `rating_agency_split` at 44 ms alone. One slow or failing grid also holds back or fails the whole board, which breaks the P3 isolation acceptance.
- **20 calls paint first, but cost the most where it matters:**
  - **Rate limits:** one view uses the whole per-user burst of 20, so a second view within the minute is throttled (429). It also takes up to 8 of the global `/api` slots per user.
  - **Round trips:** the 20 requests run on 6 connections, in about 4 waves. Each wave pays a full network round trip to Render (tens of ms), which this in-process run doesn't include.
  - **Server work:** each of the 20 requests decrypts the cookie, passes both limiters, writes an audit row and gets its own ETag.
  - **Bytes:** total brotli bytes are 34% higher than per-source (8,558 vs 6,392), and the tail is the worst of the parallel options (p95 194 ms).
- **Per source is the balance:**
  - Five requests fit inside the burst.
  - The first source paints in 48 ms and all of them in 91 ms.
  - One source failing or lagging touches only its own tiles.
  - The cache key and ETag are per (source, grid, as-of, data version, portfolios), so a repeat view is five 304s or HITs.
- **Per-grid cost:** most grids take 5–10 ms alone. The slowest are `rating_agency_split` (44 ms: three passes over the scoped snapshot), `watchlist_sector` (28 ms) and the two vendor-mark grids (21 ms).

End to end in the browser (Playwright `tests/insights.spec.ts`, local stack at scale 1.0, Production mode), measured from the board's inputs settling to each source's tiles painted:

| | first tile | all tiles | budget |
|---|---:|---:|---|
| First visit (server cache cold, MISS) | 142 ms | 201 ms | < 500 ms / < 1.5 s |
| Warm (server cache HIT) | 54 ms | 57 ms | < 500 ms / < 1.5 s |

**Tiles: plain tables vs AG Grid.** README §6 P3 leaves this to measurement.

- **Bytes:** with plain tables the board's lazy chunk is **3.5 KB** transferred (`ng build`). AG Grid's shared lazy chunk is **236 KB** transferred, which a first visit to the board would otherwise download.
- **Paint:** the tables paint the whole board within the warm 57 ms above.
- **Features:** a 5 × 5 tile needs no sorting, virtualisation or column state.

AG Grid stays on P1 and P2, where those features are needed.

**How to reproduce** (a seeded local database only, never Render, Neon or production; read-only):

```
DATABASE_URL=… dotnet run -c Release --project perf/InsightsBenchmark -- 30
iterations=30  asOf=2026-10-06  portfolios=12  grids=20  .NET 10.0.12  per-request cap=4 global cap=10
| Source | Grid | rows × cols | p50 ms alone |
|---|---|---:|---:|
| core | mv_sector_rating | 5 × 7 | 6.6 |
| core | dv01_sector_duration | 5 × 6 | 7.5 |
| core | pnl_attribution_portfolio | 12 × 7 | 9.1 |
| core | top_issuers_mv | 5 × 4 | 7.3 |
| core | vintage_concentration | 9 × 4 | 5.2 |
| core | watchlist_sector | 5 × 6 | 27.6 |
| market | spread_change_1m | 5 × 8 | 5.9 |
| market | curve_moves | 5 × 6 | 0.7 |
| market | spread_percentile_2y | 5 × 8 | 18.0 |
| surveillance | dq60_sector_vintage | 5 × 6 | 9.5 |
| surveillance | cpr_cdr_servicer | 5 × 5 | 9.4 |
| surveillance | oc_cushion_buckets | 5 × 3 | 7.4 |
| surveillance | warf_clo_vintage | 10 × 5 | 7.0 |
| surveillance | ltv_fico_bands | 4 × 5 | 9.2 |
| pricing | vendor_dispersion | 5 × 3 | 20.7 |
| pricing | internal_vs_vendor | 5 × 5 | 20.6 |
| pricing | challenged_marks | 5 × 5 | 9.9 |
| reference | servicer_exposure | 5 × 4 | 8.7 |
| reference | trustee_exposure | 5 × 4 | 10.1 |
| reference | rating_agency_split | 8 × 4 | 43.7 |

| Approach | requests | first grid p50 / p95 ms | all grids p50 / p95 ms | connections per load | largest response JSON / br bytes | total br bytes |
|---|---:|---:|---:|---:|---:|---:|
| A. One call, one connection (serial) | 1 | 282.5 / 389.4 | 282.5 / 389.4 | 1 | 13118 / 5716 | 5716 |
| B. One call, per-task connections | 1 | 112.2 / 139.9 | 112.2 / 139.9 | 20 | 13118 / 5716 | 5716 |
| C. Per-source calls, per-task connections (chosen) | 5 | 48.0 / 71.6 | 90.9 / 114.1 | 20 | 4327 / 2185 | 6392 |
| D. 20 calls, one per grid (6 at a time) | 20 | 9.7 / 20.3 | 105.4 / 193.8 | 20 | 1224 / 712 | 8558 |
```

The browser numbers come from `BASE_URL=http://localhost:8080 DESK_EMAIL=… DESK_PASSWORD=… npx playwright test tests/insights.spec.ts`. They run against the compose e2e stack, whose override sets `DEV_FAULT_INJECTION=true`.

The shared machine is noisy. An earlier five-iteration, non-interleaved run under heavy load measured the same order: the per-source first tile beat both single-call options. The absolute numbers in that run were 2–4× higher.

## Decision

The board makes one request per data source, five in all, and each source is its own stream in the browser. On the server, each grid of a source runs as its own task on its own pooled connection, never a shared DbContext or connection. Fan-out is capped at 4 per request and 10 per process. Tiles are plain tables.

## Consequences

- **Easier:**
  - Progressive rendering and failure isolation follow from the transport: each source's request lands on its own.
  - Per-source caching and ETags make repeat views free.
  - Naive mode (`?grid=<id>`, dev or admin only) reuses the same endpoint for the Performance Lab comparison.
- **Pool and limiter headroom.** With the caps, two concurrent board loads hold at most 10 connections. That leaves half of the default 20-connection pool for P1 and P2. The global concurrency limiter still bounds requests; the process cap bounds connections. `INSIGHTS_PARALLELISM` and `INSIGHTS_MAX_CONNECTIONS` tune both; out-of-range values keep the defaults.
- **Scoping joins the core book from every source.** Every grid is scoped in SQL with `portfolio_id = ANY(@portfolios)` on `core.position_snapshot`, including the market, surveillance, pricing and reference grids. It shows market data only for what the book holds, so an empty entitlement is zero rows everywhere, and an empty scope never opens a connection.
  - Each source's queries run on that source's connection (`ConnectionStrings__<Source>`) and join `core`. That assumes, as production does (README §5.1), that the sources share one database.
  - **Revisit:** moving a source to its own database means passing the scoped keys (bond ids, deal ids) into its queries as array parameters instead of joining `core`.
- **Grain:** parent fields (deal original balance) are summed over distinct deals, and child measures (position MV) are aggregated per deal or bond before a join. `Deal_original_balance_by_sector_is_counted_once_per_deal` checks it against an independent query and against the naive join, which double counts.
- **Fault injection:** the progressive-render e2e needs one slow source. `X-Debug-Delay-Ms` (≤ 5 s) is honoured only in Development or with `DEV_FAULT_INJECTION=true`, which only the e2e compose override sets. Production ignores the header.
- **Cold cost:** each grid scans the scoped slice of the wide snapshot. The heaviest grid alone is 44 ms. If the book grows past the budget, the first step is a narrow, per-source covering index or a materialised per-as-of aggregate, not a change of transport.
