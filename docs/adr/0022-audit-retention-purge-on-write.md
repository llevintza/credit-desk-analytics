# ADR-0022: Audit retention: 90-day default, purged after an audit write (not by a timer)

- **Status:** Accepted
- **Date:** 2026-10-08
- **Phase / PR:** follow-up to Phase 2 (#105), issue #114 (Helms ruling, 2026-10-07); must land before Phase 3 feature merges (#132)

## Context

Phase 2 added `app.audit` (README §7.2): one row per authenticated `/api` request and per login attempt, written in coalesced batches by `AuditWriter`. Nothing ever deleted a row. Helms ruled (#114):

- keep audit records for **90 days** by default;
- make the window configurable through config/env, so Leo can override it;
- purge records older than the window on a schedule, with a test;
- document the setting.

Constraints:

- **Neon free tier:** 0.5 GB of storage for the whole project (README §5.4), and compute that suspends when idle. Anything that runs SQL on a timer wakes the database and burns compute hours. The repo rule is no keep-awake pingers, and nothing touches the database unless a user does.
- **Render free tier:** the instance sleeps after about 15 minutes without traffic and restarts cold. A process-local timer with a 24 h period may never fire on an instance that rarely stays up that long.
- **No DDL from the app.** The `IX_audit_at` index already exists (Phase 2 migration), so the purge needs no migration.
- **Command timeout** is 10 s for every app query (`ServiceCollectionExtensions.CommandTimeoutSeconds`).

## Options considered

1. **Timer service:** a `BackgroundService` with a `PeriodicTimer` (24 h) that runs the delete. It's simple, but it wakes Neon on its own, and on a sleeping Render instance it rarely fires.
2. **Purge after an audit write (chosen):** after `AuditWriter` saves a batch, it runs the delete when one is due: on the first write after start, then at most once per 24 h. The purge only runs when the database is already awake for an insert. It runs on the same pooled context, so it needs no extra connection.
3. **Database-side scheduling** (`pg_cron`, a GitHub Actions cron job in `db-ops.yml`, or a Render cron job): this keeps the app out of it, but `pg_cron` needs an extension install and provider-specific setup outside our migrations, an Actions cron wakes Neon every day and touches a governed workflow, and a Render cron job is a separate billed service.
4. **Partitioning by day** with `DROP PARTITION`: this is the cheapest delete at large volume, but it needs DDL (a migration and partition maintenance), and the volumes below don't justify it.

And for the delete itself:

- **(a) one `DELETE … WHERE at < @cutoff`** (EF `ExecuteDelete`, on `IX_audit_at`), or
- **(b) batched deletes** (`LIMIT n` loops) to bound each statement under the 10 s timeout.

## Evaluation

| Criterion | 1 Timer | 2 After a write | 3 DB/CI cron | 4 Partitions |
|---|---|---|---|---|
| Wakes Neon on its own | yes (daily) | **no** | yes (daily) | no |
| Runs on a sleeping/cold-starting Render instance | rarely | **on the first write after each start** | n/a | n/a |
| Schema change | none | **none** | none | migration + maintenance |
| New moving parts | hosted service | **one class, 1 call site** | workflow / extension / paid service | partition manager |
| Purge while nobody uses the app | yes | no (rows age out at the next login, within one flush interval) | yes | no |

The one gap in option 2 is real but harmless. While nobody uses the app, no rows are added, and rows past the window stay until the next login. That login writes an audit row, which triggers the purge within `AUDIT_FLUSH_SECONDS`. Storage can't grow while the app is idle, and nobody reads the table then either.

**Delete cost and table size (measured).** `perf/audit-retention.sql` builds a copy of `app.audit` (same columns, PK and `IX_audit_at`). It fills the copy with 120 days at 10,000 rows a day, a busy demo: about 7 requests a minute around the clock. Then it times the exact purge statement with `EXPLAIN ANALYZE`. Postgres 17 (the pinned test image), Apple M5, two runs:

| Case | Rows deleted | Run 1 | Run 2 |
|---|---|---|---|
| Steady state: one daily purge (oldest day) | 10,000 | 2.1 ms | 3.8 ms |
| Backlog: first purge, 30 days past a 90-day window | 300,000 | 87 ms | 161 ms |
| Nothing to delete (the purge right after a purge) | 0 | 15 ms | 24 ms |

| Table size | Rows | Total (heap + PK + `IX_audit_at`) |
|---|---|---|
| 120 days | 1,200,000 | 208 MB (181.6 bytes/row) |
| 90 days, after the purge and `VACUUM` | 900,000 | 174 MB (space is reused, not returned) |

So the delete is an index range scan. Even a 30-day backlog is about two orders of magnitude under the 10 s timeout on a laptop, which leaves room for Neon's slower, cold storage. One statement (a) is enough, and batching (b) would add a loop for no measured benefit.

The size numbers matter for the window. At the busy-demo rate, 90 days costs about **160 MB of the 0.5 GB** Neon budget. At a realistic demo rate (1,000 rows a day) it is about 16 MB. If the real rate approaches the busy case, Leo can shorten the window with `AUDIT_RETENTION_DAYS` (an environment change, no code change).

**How to reproduce** (local throwaway container only, trust auth, nothing persisted; never Neon or production):

```
docker run -d --name audit-retention-bench -e POSTGRES_HOST_AUTH_METHOD=trust -v "$PWD/perf":/perf:ro postgres:17-alpine@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24
docker exec audit-retention-bench psql -U postgres -X -q -f /perf/audit-retention.sql            # -v rows_per_day=N to change the volume
docker rm -f audit-retention-bench
```

```
--- size with 120 days
  rows   | total  | bytes_per_row
---------+--------+---------------
 1200000 | 208 MB |         181.6

--- steady state: one daily purge (deletes the oldest day: 119 -> 120 days ago)
 Delete on audit (actual time=2.013..2.013 rows=0 loops=1)
   ->  Index Scan using "IX_audit_at" on audit (actual time=0.007..0.707 rows=10000 loops=1)
 Execution Time: 2.112 ms

--- backlog: first purge with a 90-day window (deletes 30 days)
 Delete on audit (actual time=87.019..87.019 rows=0 loops=1)
   ->  Index Scan using "IX_audit_at" on audit (actual time=0.034..19.905 rows=300000 loops=1)
 Execution Time: 87.052 ms

--- nothing to delete (the purge right after a purge)
 Execution Time: 14.983 ms

--- size with 90 days (after the purge and a vacuum; space is reused, not returned to the OS)
  rows  | total
--------+--------
 900000 | 174 MB
```

## Decision

- Keep audit rows for **`AUDIT_RETENTION_DAYS`** days. The default is **90**. Valid values are whole days from 1 to 36,500. Any other value logs a warning at start and keeps 90.
- `AuditWriter` runs `AuditRetention.PurgeAsync` right after a successful insert, when it's due: on the first write after start, then at most once every 24 h. The purge is one `DELETE FROM app.audit WHERE at < now - window`. A row exactly `window` old is kept.
- A failed purge is logged and never affects the batch, which is already saved. The attempt counts, so a failing purge is retried the next interval, not on every write.

## Consequences

- No timer, no extra connection, and nothing wakes Neon. The purge piggybacks on an insert that was already happening.
- Rows past the window can stay while the app is idle, until the next login. That's acceptable for a usage log. If audit retention ever becomes a compliance requirement with a hard deadline, revisit with option 3.
- Shortening the window takes effect at the next purge after a restart. `DELETE` frees space for reuse inside the table, but the table doesn't shrink on disk without `VACUUM FULL`, which we don't run.
- Revisit with partitioning (option 4) if the audit volume grows past a few million rows, or if a backlog purge approaches the 10 s command timeout.
