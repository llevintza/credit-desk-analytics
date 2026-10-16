# ADR-0027: Admin Usage reads: one GROUP BY plus a LATERAL top-N per day on `IX_audit_at`, live and uncached

- **Status:** Proposed
- **Date:** 2026-10-16
- **Phase / PR:** phase-9/hardening-admin-usage (#70, sub-epic #437 of #18)

## Context

README §7.2 and §8 ask for an admin **Usage** page: requests per user per day, the cache hit ratio, and the slowest 20 queries per day. The data is `app.audit` (Phase 2): one row per authenticated `/api` request and per login attempt, with `at`, `user_name`, `endpoint` (method and route template), `status`, `rows`, `ms` and `cache` (`HIT`, `MISS` or null). The only index besides the primary key is `IX_audit_at`. Rows live for `AUDIT_RETENTION_DAYS` (default 90; ADR-0022).

Constraints:

- **Neon free tier.** Every read holds one of the 8 shared database permits (README §7.2) and burns compute. A query that sorts a month of rows can also spill to a temp file.
- **No DDL from the app**, and a migration on a 90-day audit table is a deploy step. An index that only this admin page uses has to earn that.
- **Volume.** ADR-0022's busy demo is 10,000 rows a day, so 30 days is about 300,000 rows and 90 days about 900,000.
- **Freshness.** The page is for watching the live demo. It must show requests up to the last audit flush, not until the next batch.

The question is how to compute "slowest 20 per day", and whether the report is one statement or several. Requests per user per day is a plain `GROUP BY day, user` over the window, and the hit ratio falls out of it, since each group counts its `HIT` and `MISS` rows.

## Options considered

1. **(a) `ROW_NUMBER()`**: rank every request in the window with `PARTITION BY day ORDER BY ms DESC` and keep `rn <= 20`. One index range scan, then a sort of the whole window.
2. **(b) LATERAL top-N per day**: `generate_series` over the window's days, then `CROSS JOIN LATERAL (… WHERE at >= d AND at < d + 1 day ORDER BY ms DESC LIMIT 20)`. One index range scan per day, each with a 20-row top-N heap.
3. **(c) The whole report in one statement**: the GROUP BY and (b) as two CTEs, so the page costs one round trip.
4. **(d) A new index**, for example on `((at AT TIME ZONE 'UTC')::date, ms DESC)`, so each day's top 20 is an index read. This needs a migration, and the audit writer would maintain a second index on every insert.

## Evaluation

`perf/admin-usage.sql` builds a copy of `app.audit` (the same columns, PK and `IX_audit_at`) in a throwaway Postgres 17 container. It fills the copy with 90 days at 10,000 rows a day (2% logins, a third each `HIT`, `MISS` and null), then times each statement with `EXPLAIN (ANALYZE, BUFFERS)`. `perf/admin-usage.sh` fills once and runs three warm passes over 1-, 7- and 30-day windows. The machine is an Apple M5 with Postgres 17-alpine (the pinned test image) and default `work_mem` (4 MB). Other agents' builds and test containers shared the machine, so read single numbers as ±50%.

Execution time in ms, passes 1 / 2 / 3:

| Window (rows) | GROUP BY per user per day | (a) ROW_NUMBER | (b) LATERAL top-N | (c) one statement |
|---|---|---|---|---|
| 1 day (~5,000; the window ends at "now", 12:00) | 3.3 / 2.8 / 2.5 | 4.3 / 8.1 / 3.8 | 91.8 / 76.9 / 65.5 | 22.8 / 42.6 / 35.0 |
| 7 days (~65,000) | 68.8 / 92.3 / 83.4 | 142.4 / 80.7 / 60.2 | 134.2 / 59.3 / 42.1 | 93.3 / 54.1 / 58.1 |
| 30 days (~290,000) | 220.9 / 113.1 / 152.6 | **542.8 / 657.2 / 455.8** | **181.9 / 98.3 / 105.6** | 311.7 / 249.0 / 244.4 |

| Plan detail, 30 days | (a) ROW_NUMBER | (b) LATERAL top-N |
|---|---|---|
| Sort | external merge, **25,960 kB on disk** | top-N heapsort, **29 kB** in memory (30 loops of ~9,600 rows) |
| Grows with | every row in the window (n log n, spills past `work_mem`) | the rows read; the sort state stays 20 rows per day |

The 1-day (b) numbers carry a fixed overhead of tens of ms on this machine, in the function scan and executor start-up, not in the index scan (2.2 ms for its ~4,900 rows). Without `EXPLAIN`, the same 1-day statements took 39, 15 and 4.7 ms for (b) and 2.7 and 1.7 ms for (a) under `\timing`. For a single day (a) is a few ms faster. That isn't the case to optimise: the page opens on 7 days and allows 30.

**How to reproduce** (local throwaway container, trust auth; never Neon or production):

```
perf/admin-usage.sh            # rows_per_day defaults to 10000
===== days=30 pass=2 (rows_per_day=10000)
--- per user per day (one GROUP BY over the window)
 Execution Time: 113.133 ms
--- slowest 20 per day, (a) ROW_NUMBER over the window
               Sort Method: external merge  Disk: 25960kB
 Execution Time: 657.155 ms
--- slowest 20 per day, (b) LATERAL top-N per day
               Sort Method: top-N heapsort  Memory: 29kB
 Execution Time: 98.265 ms
--- (c) the whole report in one statement: GROUP BY + LATERAL as two CTEs
 Execution Time: 248.951 ms
```

Option (d) wasn't built. At 30 days, (b) already reads each day's ~9,600 rows through `IX_audit_at` in about 3 ms a day. An index that turns that into a 20-row read would save roughly 100 ms on a page an admin opens a few times a day. In return, every audited request would pay a second index insert, the 90-day table would grow by an estimated 20 to 25% (ADR-0022 sizes `IX_audit_at` itself), and the change would need a migration on the deploy path.

| Criterion | (a) | (b) | (c) | (d) |
|---|---|---|---|---|
| 30-day slowest (ms) | 456–657 | **98–182** | 244–312 (whole report) | not built |
| Temp file at 30 days | 26 MB | **none** | none | none |
| Statements per page load | 2 | **2** | 1 | 2 |
| Migration / write cost | none | **none** | none | migration, a second index on every audit insert |
| Simplicity | one window function | **one LATERAL** | two CTEs with a combined result shape | index + query |

Option (c) saves one round trip, about 1 ms on Render to Neon in the same region. It wasn't faster than running the two statements on their own (244–312 ms against 113–221 plus 98–182), and its two result shapes would have to share one row type. Two statements on one pooled context are simpler to read and to test.

## Decision

`GET /api/admin/usage?days=N` (admin only) runs two parameterised statements through `UsageRepository` (EF Core `SqlQuery`, `src/Desk.Data/App/UsageRepository.cs`), one after the other on one pooled context. The first is a `GROUP BY` UTC day and user over `[from, to + 1 day)`, which also gives the hit and miss counts. The second is a `LATERAL` top-20 per UTC day ordered by `ms DESC, id`. Only `kind = 'request'` rows count. The day and overall hit ratios are summed in C# from the per-user rows, and they are `null`, never `NaN`, when nothing cacheable ran. There is no new index.

The window is `days` UTC days ending today: 7 by default, 1 to 31 (any other value is a 400 problem), and never more than `AUDIT_RETENTION_DAYS`. The response is live and never cached (`Cache-Control: no-store`). The endpoint's own audit row carries no cache status, so opening the page doesn't move the hit ratio.

## Consequences

- The page is fresh up to the last audit flush (`AUDIT_FLUSH_SECONDS`), at a measured ~100–350 ms of database time for a 30-day window on a busy demo, holding one of the 8 database permits for that long. That is acceptable for an admin page and out of the README §10 budgets, which cover the desk pages.
- With no cache, each open of the page wakes Neon. Only admins can open it, and it is never polled: the page loads once and refreshes on a button.
- The 31-day cap keeps the worst case near the measured 30-day numbers. A 90-day report would read about three times as many rows. If someone needs one, add a daily roll-up table, not a longer window.
- Days are UTC calendar days, labelled as such on the page. The desk's batch day is America/New_York (06:30), but the audit log is about request load, and UTC keeps day boundaries free of DST.
- Revisit if audit volume grows past ~10,000 rows a day (re-run `perf/admin-usage.sh` with a higher `rows_per_day`), or if a second consumer of "slowest per day" appears. Either would justify option (d) or a roll-up.
- ADR-0021: the endpoint is classified **exempt** (the 2026-10-16 amendment there). It returns user names, route templates, statuses and timings, and no portfolio ids or positions.
