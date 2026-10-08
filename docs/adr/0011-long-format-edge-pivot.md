# ADR-0011: P2 fund performance in long format, pivoted at the edge

- **Status:** Accepted
- **Date:** 2026-10-08
- **Phase / PR:** phase-5/fund-performance

## Context

P2 shows two rows (balance and IRR) across N month-end columns, where N depends on the range: QTD, YTD, 1Y, ITD or a custom span (README §6 P2). `core.fund_performance` is stored long: one row per fund per month-end. Somewhere the months have to become columns.

Budgets: < 300 ms and < 5 KB. Every row must line up with `months`; a mismatch is a 500 with a logged invariant violation.

## Options considered

1. **Long rows + pivot in C#.**
   - One fixed, parameterised query: `… WHERE fund_id = @f AND as_of_month BETWEEN @from AND @to ORDER BY as_of_month`.
   - `FundPivot` builds `months` and one array per measure, then checks the invariant.
2. **SQL `array_agg`.** The database returns three arrays (months, balance, IRR) in one row; C# only maps them.
3. **Dynamic SQL pivot.** One `max(...) FILTER (WHERE as_of_month = '…')` column per month, generated from the range. It's PostgreSQL's equivalent of `PIVOT`/`crosstab`.

## Evaluation

Every fund's ITD range (69 / 64 / 49 / 43 months). Seed 42, scale 1.0, Postgres 17.11 in Docker, Apple M5, .NET 10.0.12, 500 iterations after warm-up.

| Approach | p50 ms (4 funds) | p95 ms | alloc KB/op | payload (fund 1, raw) | distinct SQL texts | portable (README §16) |
|---|---:|---:|---:|---:|---:|---|
| **Long rows + pivot in C#** | **1.37** | 1.85 | 91 | 2,706 B | **1** | yes, plain SQL |
| SQL `array_agg` | 1.20 | 1.40 | 50 | 2,706 B | 1 | no: PostgreSQL arrays |
| Dynamic SQL pivot | 4.03 | 4.85 | 258 | 2,698 B | **one per range** | no: generated SQL text |

**How to reproduce** (local compose stack only, never Render/Neon/production):

```
DATABASE_URL=… dotnet run -c Release --project perf/FundBenchmark -- 500
```

**Through the API** (budgets < 300 ms, < 5 KB; same machine, Release build, `ASPNETCORE_ENVIRONMENT=Production`):
- Fund 1 ITD, the largest P2 response (69 months), is 2,706 B raw and **1.1 KB Brotli**. The CI `budgets` job now checks it.
- k6: 400 MISS requests (CUSTOM month pairs never asked for before, 4 funds) and 1,000 HIT requests paced at 50/s.
  - **MISS p95 0.94 ms**, p50 0.58 ms.
  - **HIT p95 2.85 ms**, p50 1.37 ms. HIT measured no faster than MISS, because the database share of a MISS is under a millisecond. Both scenarios are about 100× under budget; the gap wasn't investigated further. What the cache saves here is database connections on the free tier, not latency.

```
BASE_URL=http://localhost:5182 DESK_EMAIL=… DESK_PASSWORD=… node perf/payload-size.mjs
| P2 fund 1 ITD | – | br | 1.1 | 5 | ok |

# admin account: setup() clears the response cache so MISS really misses
docker run --rm -i --add-host=host.docker.internal:host-gateway -e BASE_URL=http://host.docker.internal:5182 \
  -e DESK_EMAIL=… -e DESK_PASSWORD=… grafana/k6:1.3.0 run - < perf/funds.js
✓ 'p(95)<300' http_req_duration{scenario:hit}  p(95)=2.85ms
✓ 'p(95)<300' http_req_duration{scenario:miss} p(95)=939µs
✓ checks rate=100.00% (2,788 of 2,788: login, cache cleared, 200, MISS, HIT)
```

## Decision

**Long format, pivoted in C#** (`FundRepository.MonthsAsync` + `FundPivot`).

Why not the others:
- The dynamic pivot is about 3× slower, allocates 3× more, and puts a new SQL text in the plan cache for every range. Generating SQL from data is exactly what AGENTS.md's SQL-safety rule steers away from.
- `array_agg` is about 0.15 ms faster for all four funds together, which is noise against a 300 ms budget and a response that's cached until the next batch. It ties the query to PostgreSQL arrays, while README §16 keeps SQL Server parity in view.
- The edge pivot keeps the invariant check (every row has `months.length` values) in one testable function.

## Visibility

Balance and IRR aggregate every portfolio in the fund, so a fund is visible only when the user is entitled to **all** of its portfolios. Otherwise the response is the same 404 as for a fund that doesn't exist: no ETag, and checked before the response cache or `If-None-Match`, so neither a 304 nor a cached body can leak. Showing a whole-fund aggregate to someone who holds only one of its portfolios would disclose the others' balances.

The cached response is then the same for everyone who may see it, so the cache key stays per fund, data version and range. The per-user grants model being designed in #124 (ADR-0021) plugs in through `IPortfolioEntitlements` without changing this rule.

`CUSTOM` bounds are normalised to month-ends before they are compared or cached: any day of June is June, so `from=2025-06-15&to=2025-06-01` is one month, and the days of a month share one cache entry.

## Consequences

- The pivot is a few lines of C# with unit tests: month counts per range, mid-month inception, the empty range, and the invariant.
- The SQL is one fixed, parameterised statement: one plan, and nothing generated from data.
- If P2 ever spans thousands of months per request, revisit `array_agg` (fewer rows on the wire). At ≤ 70 months it doesn't matter.
