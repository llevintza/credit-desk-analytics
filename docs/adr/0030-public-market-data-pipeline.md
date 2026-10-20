# ADR-0030: Public market data pipeline: public reference data, a daily roll of the synthetic book, retention

- **Status:** Proposed (a `[workflows]` rules PR: Helms signs off on the AGENTS.md amendment)
- **Date:** 2026-10-20
- **Phase / PR:** epic #535 (E2), story #544 (E2-S0). Leo's decisions D7 (2026-10-20) and D7b–D14 ("go with your recommendations", 2026-10-20, recorded on #535)

## Context

The database is seeded once per deploy (`Desk.Seeder --if-changed`, README §14.2), so the as-of date freezes at the last seed. A visitor next month sees month-old "start-of-day" data. Epic #535 adds a pipeline that collects public market data each business day, rolls the fictional book forward to a new as-of date, and keeps only the latest N days in Neon.

Constraints:

- **The data rule.** AGENTS.md banned every real company, employer, client and person name. Leo decided (D7, 2026-10-20) to allow **public market reference data** (issuer names, tickers, prices, curves) and to keep the ban on client, employer and person names, with each source's licence and terms of use recorded. That needs a rule change and this ADR before any code (no real-name ingestion before it merges).
- **Production writes on a schedule.** Nothing in the repo writes to production on a timer today. Deploy `release` and db-ops `run` hold `concurrency: production` (README §14.2, §14.3), and db-ops refuses while a Deploy is in flight.
- **No DDL or seeding from the app,** no keep-awake traffic, `/health` never touches the DB (AGENTS.md hard rules, README §7.2).
- **Storage.** Neon Free is planned as 0.5 GB (README §5.4; it has also been documented as 1 GB, and D13 asks Leo to confirm). The repo budget is **350 MB** (§10). Leo's figure for production today is ~150 MB; the phase-1 seed measured 270 MB locally, and today's main seeds to 279–280 MB locally (measured below). The production `size-report` that settles the real starting point is dispatched by Tech Coordinator before merge (agents don't run it).
- **The seeder TRUNCATEs every seeded table** on a SeedVersion bump, so ingested history must live outside its reach.

Related: [ADR-0003](0003-wide-snapshot-narrow-history.md) and [ADR-0004](0004-bulk-load-copy-vs-ef.md) (the snapshot shape and the seeder load), [ADR-0014](0014-free-tier-guardrails.md) (free-tier guardrails), [ADR-0016](0016-continuous-deployment.md) and [ADR-0017](0017-deploy-path-safety.md) (the production pipeline and its safety), [ADR-0022](0022-audit-retention-purge-on-write.md) (the retention pattern this follows).

## Options considered

The epic's decisions table on #535 lists each decision's options. All of them are now **decided** (Leo, 2026-10-20) and are recorded under [Decision](#decision). The two that this ADR measures:

1. **Retention mechanism (D13 a): batched DELETE + plain `VACUUM (ANALYZE)`.** The ingest deletes the oldest as-of date in 5,000-row transactions, then vacuums. Freed pages stay in the table and are reused by the next day's insert. No schema change.
2. **Retention mechanism (D13 b): partition `core.position_snapshot` by `as_of_date`.** Each as-of date is a partition; the oldest is a `DROP TABLE`, which returns its space at once. It needs an expand/contract migration on the biggest table (README §14.4) and a partition created per day.

The runner options (D11) were a GitHub Actions schedule in `production`, a Render cron job, and an in-app job. A Render cron job isn't on the free plan. An in-app job breaks "no DDL or seeding from the app", wakes the instance and Neon on a timer, and on a sleeping Render instance it rarely fires (the same reason ADR-0022 rejected a timer for the audit purge).

## Evaluation

### Sources and licensing (D8)

Prose and code use generic placeholders for sources, never vendor or agency names; the implementing story (#546) records each source's exact endpoint and the dated copy of its terms it was checked against in `market.series_catalog`. Terms are as published by each source at the time of writing, and #546 re-checks them before the first fetch.

| Source (placeholder) | Series | Storage | Display | Redistribution | Attribution | API key | Rate limits | Publication | History | Status |
|---|---|---|---|---|---|---|---|---|---|---|
| The government par-yield curve | par yields, 1M–30Y tenors | yes (government work, public domain) | yes | yes | not required; we credit the source on the freshness panel anyway | none | none published; one request per run | business days, end of the US trading day | decades | **Use now** |
| The central-bank overnight rate | the secured overnight financing rate (and its published percentiles) | yes | yes | yes, with the source's terms of use (no implied endorsement) | **required** (the source named, in the form its terms give) | none | none published; one request per run | T+1, about 08:00 ET | since the series began (2018) | **Use now** |
| The central-bank FX reference rates | daily reference rates against the euro | yes | yes | yes | **required** (source named, rates are reference only, not for transactions) | none | none published; one request per run | business days, about 16:00 CET | since 1999 | **Use now** |
| A public economic-data API | aggregated macro series | per series | per series | per series | per series | **free key** | per-key limit (low: one batch per run) | varies by series | varies | **Only after a per-series licence check**; third-party index series (the copyrighted index series aggregators redistribute) are **excluded** |
| A free end-of-day price source for listed reference securities (#552) | daily close per listed security | varies: often display-only | varies | usually forbidden | varies | varies | varies | after the close | varies | **Only if its licence allows storage and display**; otherwise #552 drops |

Anything whose terms forbid storing or redisplaying it is excluded. Attribution is a licence condition, so the freshness and source panel (#550) ships before the first scheduled run.

Keys live **only** in the GitHub `production` environment, injected on the fetch step only (the ADR-0017 step-scoped pattern). The first three sources need none.

### Storage: measured on a local stack (D9, D13)

`perf/snapshot-retention.sh` builds a throwaway Postgres 17 (trust auth, nothing persisted), applies the EF migrations, seeds scale 1.0 (`SEED=42`, as-of 2026-10-06) and runs `perf/snapshot-retention.sql` for 10 simulated business days in each mode. Each day copies the newest as-of date of `core.position_snapshot` (20,001 rows), `pricing.vendor_mark` (27,000) and `pricing.internal_mark` (9,000) to the next business day, the shape the #548 roll writes, then drops the oldest date so D9 holds (snapshot 2 as-of dates, marks 22 business days) and vacuums.

**How to reproduce:** `perf/snapshot-retention.sh 10` (Docker and the .NET SDK; about 2 minutes). Apple M5 (arm64), colima VM, `postgres:17-alpine` at the digest pinned in the script.

```
$ perf/snapshot-retention.sh 10
DB_SIZE_MB=279
ELAPSED_S=6.1
===== mode=delete, 10 business days
 snapshot_dates | snapshot_rows | mark_dates | db_mb
----------------+---------------+------------+-------
              2 |         40002 |         22 | 280.0

 day | new_as_of  | step_s | wal_mb | snapshot_mb | marks_mb | db_mb
-----+------------+--------+--------+-------------+----------+-------
 1   | 2026-10-07 |    2.4 |   53.8 |       110.2 |     94.9 | 319.9
 2   | 2026-10-08 |    2.3 |   53.8 |       115.8 |     96.3 | 327.2
 3   | 2026-10-09 |    2.2 |   53.8 |        84.9 |     96.3 | 296.6
 4   | 2026-10-12 |    2.4 |  134.5 |       116.1 |     96.3 | 328.2
 5   | 2026-10-13 |    2.3 |   87.8 |       116.2 |     96.3 | 328.6
 6   | 2026-10-14 |    2.3 |   80.4 |        84.9 |     96.3 | 297.7
 7   | 2026-10-15 |    2.3 |   55.0 |       116.2 |     96.3 | 329.3
 8   | 2026-10-16 |    2.3 |   55.0 |       116.2 |     96.3 | 329.6
 9   | 2026-10-19 |    2.3 |   55.0 |        84.9 |     96.3 | 298.7
 10  | 2026-10-20 |    2.4 |   55.0 |       116.2 |     96.3 | 330.3
 snapshot_dates | snapshot_rows | mark_dates | db_mb
----------------+---------------+------------+-------
              2 |         40002 |         22 | 330.3

DB_SIZE_MB=280
ELAPSED_S=6.5
===== mode=partition, 10 business days
--- convert core.position_snapshot to a range-partitioned table (one partition per as-of date)
 snapshot_dates | snapshot_rows | mark_dates | db_mb
----------------+---------------+------------+-------
              2 |         40002 |         22 | 282.6

 day | new_as_of  | step_s | wal_mb | snapshot_mb | marks_mb | db_mb
-----+------------+--------+--------+-------------+----------+-------
 1   | 2026-10-07 |    4.5 |   53.0 |        74.1 |     94.9 | 286.4
 2   | 2026-10-08 |    4.5 |   97.8 |        74.1 |     96.3 | 288.3
 3   | 2026-10-09 |    4.5 |   55.2 |        74.1 |     96.3 | 289.5
 4   | 2026-10-12 |    4.5 |   54.3 |        74.1 |     96.3 | 291.0
 5   | 2026-10-13 |    4.5 |   54.0 |        74.1 |     96.3 | 292.5
 6   | 2026-10-14 |    4.4 |   54.3 |        74.1 |     96.3 | 293.9
 7   | 2026-10-15 |    4.4 |   54.0 |        74.1 |     96.3 | 295.3
 8   | 2026-10-16 |    4.5 |   54.0 |        74.1 |     96.3 | 296.7
 9   | 2026-10-19 |    4.5 |   54.0 |        74.1 |     96.3 | 298.2
 10  | 2026-10-20 |    4.6 |   54.0 |        74.1 |     96.3 | 299.6
 snapshot_dates | snapshot_rows | mark_dates | db_mb
----------------+---------------+------------+-------
              2 |         40002 |         22 | 299.6
```

| Criterion | (a) DELETE + VACUUM | (b) partition by `as_of_date` |
|---|---|---|
| Snapshot size, steady (2 dates live) | 84.9–116.2 MB, a fixed three-day cycle: **31.3 MB of reusable dead space** at the peak, no growth from day 4 to day 10 | 74.1 MB, flat |
| Database, steady | 296.6–330.3 MB (seed 280.0): **+50.3 MB peak**, flat over 10 days | 286.4 → 299.6 MB: **+1.4–1.5 MB per day**, still growing at day 10 (the catalog churn of a ~200-column table created and dropped every day; it needs its own measurement before (b) is chosen) |
| Marks (`pricing`, 22 days) | 94.9 → 96.3 MB, flat | same |
| WAL per day (Neon history counts it) | 53.8–55.0 MB (134.5 and 87.8 MB around a checkpoint) | 53.0–55.2 MB (97.8 once) |
| Run time per day (local) | 2.2–2.4 s | 4.4–4.6 s |
| Schema change | none | expand/contract migration on the biggest table, a partition per day, a different TRUNCATE in the seeder |

The steady snapshot bloat under (a) is **31 MB, below the 50 MB D13 trigger**, and it doesn't grow, so (a) stands. Under (a) the free space is reused rather than returned, so the database settles about 50 MB above a fresh seed and stays there.

### Storage: steady state against the budget

The production starting size isn't confirmed yet: Tech Coordinator dispatches a db-ops `size-report` before merge and the PR records the number. Both candidate starting points, using the epic's per-table estimate for the new tables and the measured headroom above:

| Item | From ~150 MB (Leo's figure) | From ~270 MB (phase-1 local seed; 280 MB on today's main) |
|---|---|---|
| Today | 150 | 270 (280) |
| New tables (`market.public_series` ~4 MB, `public_price` ~3 MB, longer curve and spread history +1 MB, `app.ingest_run` < 1 MB, demo access < 1 MB) | +~10 | +~10 |
| Snapshot and marks headroom under (a), measured | +~50 | +~50 |
| **Steady state** | **≤ ~210 MB** | **≤ ~330 MB (≤ ~340 MB from 280)** |
| Against the 350 MB budget | ~140 MB of slack | **10–20 MB of slack** |
| `core.trade` if left unretained | +~11 MB a year | +~11 MB a year, which uses up that slack within about a year |

Without retention the roll adds about 46–54 MB per business day and crosses 500 MB in 5–8 business days. The #549 guard reads `pg_database_size` and the projected size before writing and fails the run, unchanged, when it would exceed the budget. A SeedVersion bump still peaks at about 2× (README §5.4: 533.8 MB measured), unchanged by this epic; the ingest never TRUNCATEs.

If the size-report lands near 270 MB, the slack is thin enough that #549 also retains `core.trade` (D9 allows adding it) and partitioning (b) is re-measured with its catalog growth.

**Compute:** one run is about 1–3 minutes of active Neon compute plus the autosuspend tail, about 2–4 CU-hours a month on weekdays. **Actions:** free on a public repository.

## Decision

Leo decided every item below on 2026-10-20 (D7, then D7b–D14 as recommended on #535).

1. **Data rule (D7, D7b).** Public market reference data (issuer names, tickers, prices, curves, rates) is allowed. Client, employer and person names stay banned in every form. The synthetic book (funds, portfolios, deals, issuers, servicers, trustees, bonds, positions, trades, analysts) stays fictional. Real public names live only in the ingested reference tables and the views that read them, their test fixtures, screenshots of those views and docs; never mixed into the synthetic book's issuer, deal or analyst names. ADR and rule prose uses placeholders. AGENTS.md, `src/Desk.Data/AGENTS.md` and `src/Desk.Seeder/AGENTS.md` carry the amendment.
2. **Sources (D8).** The government par-yield curve, the central-bank overnight rate and the central-bank FX reference rates first (no key, permissive terms). Economic-data API series only after a per-series licence check, never third-party index series. Priced reference securities only with a licence that allows storage and display, otherwise #552 drops. Each source's licence, terms URL, check date and attribution text are recorded in `market.series_catalog` before it is fetched.
3. **Mapping onto the synthetic book (D10).** Each business day the ingest writes a new as-of date and reprices the fictional book from the day's public moves: a bond's price change is `−eff_duration × Δy + ½ × convexity × Δy² − spread_duration × Δs` (on price), where `Δy` is the change in the government par yield interpolated at the bond's WAL and `Δs` the change of its sector × rating spread index. Floating coupons reset from the overnight rate plus the stored margin; FX moves drive `fx_pnl` for non-USD positions. Until a licence-checked public credit series exists, `Δs` stays synthetic, drawn from its own deterministic RNG stream keyed on the as-of date and correlated with `Δy` the way the seeder's spreads are. The scenario grid, stress summaries, vendor marks and P&L attribution (carry, roll-down, rates, spread, FX, residual) are recomputed from the same stored duration, spread duration and convexity, so P1 scenarios and attribution stay internally consistent (README §5.5).
4. **Runner and timing (D11).** A GitHub Actions schedule in the `production` environment, weekdays at **09:40 UTC**, before the 06:30 ET cache roll-over. It builds the last business day (D-1). Each series records its publication lag in `market.series_catalog`; the overnight rate publishes T+1 at about 08:00 ET, after the run, so it uses the latest published fixing with lag 1. The job shares `concurrency: production` with Deploy `release` and db-ops `run` and refuses while a Deploy is in flight, as db-ops does. GitHub disables a schedule after 60 days of repository inactivity, and Leo re-enables it. The workflow and its README §14 text are #547.
5. **Seeder ownership (D12).** The seeder bootstraps the synthetic universe on an empty or version-bumped database, at the latest ingested as-of date. The ingest owns the rolling dates. Ingested tables are never TRUNCATEd by the seeder (#545 separates them).
6. **Retention (D9) and mechanism (D13).** Snapshot 2 as-of dates, marks 22 business days, position history 24 month-ends (all unchanged), public series and prices **730 days**, `app.ingest_run` **400 days**. Mechanism **(a): batched DELETE + plain `VACUUM (ANALYZE)`, never `VACUUM FULL`**, measured above at 31 MB of steady snapshot bloat. Partitioning (b) only if a measurement shows more than 50 MB of steady bloat, re-measured for its catalog growth first.
7. **Cold tier (D14).** Deferred to #551 (P3). The ingest gets an exporter seam now: before a retention delete it hands the rows older than N to an archive interface whose default does nothing. When an archive is needed, start with object storage + Parquet; a cloud columnar warehouse only if SQL over the archive is wanted. Workflow artifacts (90 days, not durable) are not an archive.
8. **Agent rules.** Agents never run the ingest against Neon or production and never with the production keys. Locally it runs only against compose or Testcontainers with recorded fixtures. CI never calls live sources.

## Consequences

- The production as-of date advances every business day without a reseed, and pages show values derived from that day's public moves.
- The repo gains its first scheduled production writer. Its guards (the #549 size check before writing, the shared `production` concurrency, the Deploy refusal) land before the schedule (#547 merges last).
- Storage sits about 50 MB above a fresh seed in steady state. From a 270–280 MB start that leaves 10–20 MB under the 350 MB budget; the size-report decides whether trades get a retention window now.
- Neon's point-in-time history counts about 54 MB of WAL a day. Leo watches the Neon dashboard for the first two weeks.
- A licensing mistake means removing the data and the history it fed, so each source is checked and recorded before it is fetched, and the attribution panel is a release condition.
- Revisit: when the size-report or two weeks of production `size-report`s show steady bloat above 50 MB (partitioning), when Neon's cap or the budget changes, or when an archive is needed (#551).
