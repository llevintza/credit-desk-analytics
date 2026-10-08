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

**Through the API:**
- fund 1 ITD is 2,706 B raw and **1,111 B Brotli**; YTD is 348 B Brotli (budget < 5 KB);
- ~0.45 ms per request locally, and a cache hit after the first.

**How to reproduce:**

```
DATABASE_URL=… dotnet run -c Release --project perf/FundBenchmark -- 500
```

## Decision

**Long format, pivoted in C#** (`FundRepository.MonthsAsync` + `FundPivot`).

Why not the others:
- The dynamic pivot is about 3× slower, allocates 3× more, and puts a new SQL text in the plan cache for every range. Generating SQL from data is exactly what AGENTS.md's SQL-safety rule steers away from.
- `array_agg` is about 0.15 ms faster for all four funds together, which is noise against a 300 ms budget and a response that's cached until the next batch. It ties the query to PostgreSQL arrays, while README §16 keeps SQL Server parity in view.
- The edge pivot keeps the invariant check (every row has `months.length` values) in one testable function.

## Consequences

- The pivot is a few lines of C# with unit tests: month counts per range, mid-month inception, the empty range, and the invariant.
- The SQL is one fixed, parameterised statement: one plan, and nothing generated from data.
- If P2 ever spans thousands of months per request, revisit `array_agg` (fewer rows on the wire). At ≤ 70 months it doesn't matter.
