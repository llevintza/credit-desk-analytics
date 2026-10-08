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

- **Neon free tier:** 0.5 GB of storage for the whole project, and compute that suspends when idle. The tighter limit is the repo's own: the database MUST stay under 350 MB (README §10/§5.4). Anything that runs SQL on a timer wakes the database and burns compute hours. The repo rule is no keep-awake pingers, and nothing touches the database unless a user does.
- **Render free tier:** the instance sleeps after about 15 minutes without traffic and restarts cold. A process-local timer with a 24 h period may never fire on an instance that rarely stays up that long.
- **No DDL from the app.** The `IX_audit_at` index already exists (Phase 2 migration), so the purge needs no migration.
- **Command timeout** is 10 s for every app query (`ServiceCollectionExtensions.CommandTimeoutSeconds`).

## Options considered

1. **Timer service:** a `BackgroundService` with a `PeriodicTimer` (24 h) that runs the delete. It's simple, but it wakes Neon on its own, and on a sleeping Render instance it rarely fires.
2. **Purge after an audit write (chosen):** after `AuditWriter` saves a batch, it runs the delete when one is due: on the first write after start, then at most once per 24 h. The purge only runs when the database is already awake for an insert. It runs on the same pooled context, so it needs no extra connection.
3. **Database-side scheduling** (`pg_cron`, a GitHub Actions cron job in `db-ops.yml`, or a Render cron job): this keeps the app out of it, but `pg_cron` needs an extension install and provider-specific setup outside our migrations, an Actions cron wakes Neon every day and touches a governed workflow, and a Render cron job is a separate billed service.
4. **Partitioning by day** with `DROP PARTITION`: this is the cheapest delete at large volume, but it needs DDL (a migration and partition maintenance), and the volumes below don't justify it.

And for the delete itself:

- **(a) one `DELETE … WHERE at < @cutoff`** (EF `ExecuteDelete`, on `IX_audit_at`);
- **(b) batches by row count**: EF's `OrderBy(at).Take(n).ExecuteDelete()`, which emits `DELETE … WHERE id IN (SELECT id … ORDER BY at LIMIT n)`;
- **(c) batches by time range** (chosen): read the `at` of the n-th oldest row past the cutoff (`ORDER BY at OFFSET n-1 LIMIT 1`), then `DELETE … WHERE at <= edge`. Loop until the backlog is gone or a time budget runs out.

## Evaluation

| Criterion | 1 Timer | 2 After a write | 3 DB/CI cron | 4 Partitions |
|---|---|---|---|---|
| Wakes Neon on its own | yes (daily) | **no** | yes (daily) | no |
| Runs on a sleeping/cold-starting Render instance | rarely | **on the first write after each start** | n/a | n/a |
| Schema change | none | **none** | none | migration + maintenance |
| New moving parts | hosted service | **one class, 1 call site** | workflow / extension / paid service | partition manager |
| Purge while nobody uses the app | yes | no (rows age out at the next login, within one flush interval) | yes | no |

The one gap in option 2 is real but harmless. While nobody uses the app, no rows are added, and rows past the window stay until the next login. That login writes an audit row, which triggers the purge within `AUDIT_FLUSH_SECONDS`. Storage can't grow while the app is idle, and nobody reads the table then either.

**Delete cost and table size (measured).** `perf/audit-retention.sql` builds a copy of `app.audit` (same columns, PK and `IX_audit_at`). It fills the copy with 120 days at 10,000 rows a day, a busy demo: about 7 requests a minute around the clock. Then it times each statement with `EXPLAIN ANALYZE`. `perf/audit-retention.sh` runs it **warm** (one pass; every buffer a `shared hit` except (b)'s sequential scan, which goes through a small ring buffer), then **cold**: before each step, it restarts Postgres and drops the Docker VM's page cache. The macOS host can still cache the VM's disk, so read the cold numbers as a lower bound. On a box with a real cold read (x86, local disk), Code Reviewer measured the one-statement backlog at **2,633 ms**. Neon's pageserver reads are slower again, and the purge runs right after a cold start. Postgres 17 (the pinned test image), Apple M5, three runs of the script:

| Case (ms) | Rows | Warm, runs 1 / 2 / 3 | Cold, runs 1 / 2 / 3 |
|---|---|---|---|
| Daily purge (oldest day), one statement | 10,000 | 2.8 / 3.6 / 3.6 | 12 / 28 / 17 |
| Backlog, **(a) one statement**, 30 days past a 90-day window | 300,000 | 189 / 120 / 228 | 348 / 1,328 / 575 (2,633 on x86, by Code Reviewer) |
| Backlog, **(b) one `id IN (… LIMIT)` batch** | 50,000 | 275 / 244 / 286 | 409 / 927 / 495 |
| Backlog, **(c) one time-range batch** (edge + delete) | 50,000 | 23 / 20 / 19 | 60 / 151 / 277 |
| Nothing to delete, (c) (edge + delete) | 0 | 20 / 25 / 20 | n/a |

| Table size | Rows | Total (heap + PK + `IX_audit_at`) |
|---|---|---|
| 120 days | 1,200,000 | 208 MB (181.6 bytes/row) |
| 90 days, after the purge and `VACUUM` | 900,000 | 174 MB (about 193 bytes/row; space is reused, not returned) |

What the numbers say:

- **(a) is fine warm but has no bound.** Its cost grows with the backlog, cold it is seconds, and a statement that passes the 10 s timeout rolls back completely. Retried 24 h later at the same or larger size, it never makes progress. Shortening the window, the lever this ADR recommends for the size budget, is exactly what creates a large backlog: 90 → 30 days at the busy rate is 600,000 rows.
- **(b) bounds the rows but not the work.** Postgres plans `id IN (SELECT … LIMIT 50000)` as a hash semi join over a **sequential scan of the whole table** (1.2M rows). Each 50,000-row batch costs about as much as (a) deleting 300,000 rows, and the cost grows with the table, not the batch.
- **(c) bounds both.** The edge query is an index-only scan of n entries, and the delete is an index range scan of the same n rows: 10 to 40 times cheaper than (b) per batch. A 300,000-row backlog is six batches, each a fraction of a second even cold, and each commits on its own, so a failure keeps the progress made.

**How to reproduce** (local throwaway container only, trust auth, nothing persisted; never Neon or production):

```
perf/audit-retention.sh            # or: perf/audit-retention.sh <rows_per_day>
```

Run 3, trimmed to the plan lines:

```
===== warm (rows_per_day=10000)
--- size with 120 days
 1200000 | 208 MB |         181.6
--- steady state: one daily purge (deletes the oldest day: 119 -> 120 days ago)
   ->  Index Scan using "IX_audit_at" on audit (actual time=0.023..1.427 rows=10000 loops=1)
 Execution Time: 3.582 ms
--- backlog, one statement: 30 days past a 90-day window (300,000 rows)
   ->  Index Scan using "IX_audit_at" on audit (actual time=0.055..43.487 rows=300000 loops=1)
 Execution Time: 228.213 ms
--- backlog, first 50,000-row batch: the edge (index scan), then a time-range delete (AuditRetention.PurgeIfDueAsync)
   ->  Index Only Scan using "IX_audit_at" on audit a (actual time=0.011..5.775 rows=50000 loops=1)
 Execution Time: 7.146 ms
   ->  Index Scan using "IX_audit_at" on audit a (actual time=0.010..3.798 rows=50000 loops=1)
 Execution Time: 11.615 ms
--- for comparison, not used: the same batch as id IN (SELECT ... LIMIT), which EF emits for OrderBy.Take.ExecuteDelete
   ->  Hash Semi Join (actual time=256.752..273.197 rows=50000 loops=1)
         ->  Seq Scan on audit a (actual time=0.057..84.590 rows=1200000 loops=1)
 Execution Time: 285.930 ms
--- the 90-day purge, then the purge right after it (nothing to delete: the edge query finds no row)
 Execution Time: 19.078 ms
 Execution Time: 1.281 ms
--- size with 90 days (after the purge and a vacuum; space is reused, not returned to the OS)
 900000 | 174 MB
===== cold (rows_per_day=10000)
--- steady state: one daily purge (deletes the oldest day: 119 -> 120 days ago)
 Execution Time: 17.015 ms
--- backlog, one statement: 30 days past a 90-day window (300,000 rows)
   ->  Index Scan using "IX_audit_at" on audit (actual time=0.399..421.993 rows=300000 loops=1)
 Execution Time: 574.770 ms
--- backlog, first 50,000-row batch: the edge (index scan), then a time-range delete (AuditRetention.PurgeIfDueAsync)
   ->  Index Only Scan using "IX_audit_at" on audit a (actual time=0.571..209.498 rows=50000 loops=1)
 Execution Time: 212.446 ms
   ->  Index Scan using "IX_audit_at" on audit a (actual time=0.025..7.724 rows=50000 loops=1)
 Execution Time: 64.913 ms
--- for comparison, not used: the same batch as id IN (SELECT ... LIMIT), which EF emits for OrderBy.Take.ExecuteDelete
         ->  Seq Scan on audit a (actual time=0.105..200.860 rows=1200000 loops=1)
 Execution Time: 494.723 ms
```

<details>
<summary>Runs 1 and 2 (same script, same machine), trimmed to the plan and <code>Buffers</code> lines: cold steps show <code>read</code>; warm steps show only <code>shared hit</code>, except (b)'s sequential scan, which reads through a small ring buffer (<code>read=5077</code>) even warm</summary>

Run 1:

```
===== warm (rows_per_day=10000)
--- size with 120 days
 1200000 | 208 MB |         181.6
--- steady state: one daily purge (deletes the oldest day: 119 -> 120 days ago)
 Delete on audit (actual time=2.599..2.599 rows=0 loops=1)
   Buffers: shared hit=10343
   ->  Index Scan using "IX_audit_at" on audit (actual time=0.017..0.886 rows=10000 loops=1)
         Buffers: shared hit=197
   Buffers: shared hit=7
 Execution Time: 2.783 ms
--- backlog, one statement: 30 days past a 90-day window (300,000 rows)
 Delete on audit (actual time=189.212..189.212 rows=0 loops=1)
   Buffers: shared hit=310025
   ->  Index Scan using "IX_audit_at" on audit (actual time=0.046..29.588 rows=300000 loops=1)
         Buffers: shared hit=5822
 Execution Time: 189.236 ms
--- backlog, first 50,000-row batch: the edge (index scan), then a time-range delete (AuditRetention.PurgeIfDueAsync)
 Limit (actual time=6.415..6.415 rows=1 loops=1)
   Buffers: shared hit=974
   ->  Index Only Scan using "IX_audit_at" on audit a (actual time=0.021..5.206 rows=50000 loops=1)
         Buffers: shared hit=974
   Buffers: shared hit=12
 Execution Time: 6.441 ms
 Delete on audit a (actual time=16.135..16.135 rows=0 loops=1)
   Buffers: shared hit=50973
   ->  Index Scan using "IX_audit_at" on audit a (actual time=0.013..5.505 rows=50000 loops=1)
         Buffers: shared hit=973
   Buffers: shared hit=3
 Execution Time: 16.152 ms
--- for comparison, not used: the same batch as id IN (SELECT ... LIMIT), which EF emits for OrderBy.Take.ExecuteDelete
 Delete on audit a (actual time=274.995..274.998 rows=0 loops=1)
   Buffers: shared hit=63288 read=5077
   ->  Hash Semi Join (actual time=253.347..265.691 rows=50000 loops=1)
         Buffers: shared hit=13288 read=5077
         ->  Seq Scan on audit a (actual time=0.010..78.629 rows=1200000 loops=1)
                           ->  Index Scan using "IX_audit_at" on audit a0 (actual time=0.016..5.067 rows=50000 loops=1)
   Buffers: shared hit=50 read=2
 Execution Time: 275.237 ms
--- the 90-day purge, then the purge right after it (nothing to delete: the edge query finds no row)
 Limit (actual time=18.755..18.755 rows=0 loops=1)
   Buffers: shared hit=5823
   ->  Index Only Scan using "IX_audit_at" on audit a (actual time=18.753..18.754 rows=0 loops=1)
         Buffers: shared hit=5823
 Execution Time: 18.775 ms
 Delete on audit (actual time=0.982..0.982 rows=0 loops=1)
   Buffers: shared hit=1473
   ->  Index Scan using "IX_audit_at" on audit (actual time=0.981..0.981 rows=0 loops=1)
         Buffers: shared hit=1473
 Execution Time: 0.995 ms
--- size with 90 days (after the purge and a vacuum; space is reused, not returned to the OS)
 900000 | 174 MB
===== cold (rows_per_day=10000)
--- steady state: one daily purge (deletes the oldest day: 119 -> 120 days ago)
 Delete on audit (actual time=11.995..11.996 rows=0 loops=1)
   Buffers: shared hit=10149 read=194 dirtied=147
   ->  Index Scan using "IX_audit_at" on audit (actual time=0.151..9.128 rows=10000 loops=1)
         Buffers: shared hit=3 read=194
   Buffers: shared hit=60 read=24
 Execution Time: 12.257 ms
--- backlog, one statement: 30 days past a 90-day window (300,000 rows)
 Delete on audit (actual time=348.232..348.233 rows=0 loops=1)
   Buffers: shared hit=304202 read=5823 dirtied=4350
   ->  Index Scan using "IX_audit_at" on audit (actual time=0.158..260.549 rows=300000 loops=1)
         Buffers: shared read=5822 dirtied=146
   Buffers: shared hit=60 read=20
 Execution Time: 348.352 ms
--- backlog, first 50,000-row batch: the edge (index scan), then a time-range delete (AuditRetention.PurgeIfDueAsync)
 Limit (actual time=45.387..45.388 rows=1 loops=1)
   Buffers: shared read=974 dirtied=726
   ->  Index Only Scan using "IX_audit_at" on audit a (actual time=0.250..44.304 rows=50000 loops=1)
         Buffers: shared read=974 dirtied=726
   Buffers: shared hit=68 read=22
 Execution Time: 45.409 ms
 Delete on audit a (actual time=14.350..14.351 rows=0 loops=1)
   Buffers: shared hit=50973
   ->  Index Scan using "IX_audit_at" on audit a (actual time=0.006..3.046 rows=50000 loops=1)
         Buffers: shared hit=973
   Buffers: shared hit=6
 Execution Time: 14.521 ms
--- for comparison, not used: the same batch as id IN (SELECT ... LIMIT), which EF emits for OrderBy.Take.ExecuteDelete
 Delete on audit a (actual time=408.650..408.653 rows=0 loops=1)
   Buffers: shared hit=50727 read=17638 dirtied=4348 written=3591
   ->  Hash Semi Join (actual time=386.749..396.970 rows=50000 loops=1)
         Buffers: shared hit=727 read=17638 dirtied=4348 written=3591
         ->  Seq Scan on audit a (actual time=0.052..147.651 rows=1200000 loops=1)
                           ->  Index Scan using "IX_audit_at" on audit a0 (actual time=0.115..56.495 rows=50000 loops=1)
   Buffers: shared hit=160 read=29 dirtied=1
 Execution Time: 409.301 ms
```

Run 2:

```
===== warm (rows_per_day=10000)
--- size with 120 days
 1200000 | 208 MB |         181.6
--- steady state: one daily purge (deletes the oldest day: 119 -> 120 days ago)
 Delete on audit (actual time=3.374..3.374 rows=0 loops=1)
   Buffers: shared hit=10343
   ->  Index Scan using "IX_audit_at" on audit (actual time=0.020..1.250 rows=10000 loops=1)
         Buffers: shared hit=197
   Buffers: shared hit=7
 Execution Time: 3.636 ms
--- backlog, one statement: 30 days past a 90-day window (300,000 rows)
 Delete on audit (actual time=120.036..120.036 rows=0 loops=1)
   Buffers: shared hit=310025
   ->  Index Scan using "IX_audit_at" on audit (actual time=0.015..29.921 rows=300000 loops=1)
         Buffers: shared hit=5822
 Execution Time: 120.062 ms
--- backlog, first 50,000-row batch: the edge (index scan), then a time-range delete (AuditRetention.PurgeIfDueAsync)
 Limit (actual time=6.147..6.147 rows=1 loops=1)
   Buffers: shared hit=974
   ->  Index Only Scan using "IX_audit_at" on audit a (actual time=0.061..4.962 rows=50000 loops=1)
         Buffers: shared hit=974
   Buffers: shared hit=12
 Execution Time: 6.177 ms
 Delete on audit a (actual time=14.118..14.118 rows=0 loops=1)
   Buffers: shared hit=50973
   ->  Index Scan using "IX_audit_at" on audit a (actual time=0.011..4.757 rows=50000 loops=1)
         Buffers: shared hit=973
   Buffers: shared hit=3
 Execution Time: 14.166 ms
--- for comparison, not used: the same batch as id IN (SELECT ... LIMIT), which EF emits for OrderBy.Take.ExecuteDelete
 Delete on audit a (actual time=243.864..243.867 rows=0 loops=1)
   Buffers: shared hit=63288 read=5077
   ->  Hash Semi Join (actual time=225.767..236.190 rows=50000 loops=1)
         Buffers: shared hit=13288 read=5077
         ->  Seq Scan on audit a (actual time=0.012..77.889 rows=1200000 loops=1)
                           ->  Index Scan using "IX_audit_at" on audit a0 (actual time=0.011..5.368 rows=50000 loops=1)
   Buffers: shared hit=50 read=2
 Execution Time: 244.190 ms
--- the 90-day purge, then the purge right after it (nothing to delete: the edge query finds no row)
 Limit (actual time=22.908..22.909 rows=0 loops=1)
   Buffers: shared hit=5823
   ->  Index Only Scan using "IX_audit_at" on audit a (actual time=22.907..22.907 rows=0 loops=1)
         Buffers: shared hit=5823
 Execution Time: 22.937 ms
 Delete on audit (actual time=2.202..2.203 rows=0 loops=1)
   Buffers: shared hit=1473
   ->  Index Scan using "IX_audit_at" on audit (actual time=2.201..2.201 rows=0 loops=1)
         Buffers: shared hit=1473
 Execution Time: 2.230 ms
--- size with 90 days (after the purge and a vacuum; space is reused, not returned to the OS)
 900000 | 174 MB
===== cold (rows_per_day=10000)
--- steady state: one daily purge (deletes the oldest day: 119 -> 120 days ago)
 Delete on audit (actual time=27.642..27.643 rows=0 loops=1)
   Buffers: shared hit=10149 read=194 dirtied=147
   ->  Index Scan using "IX_audit_at" on audit (actual time=0.091..22.776 rows=10000 loops=1)
         Buffers: shared hit=3 read=194
   Buffers: shared hit=60 read=24
 Execution Time: 27.861 ms
--- backlog, one statement: 30 days past a 90-day window (300,000 rows)
 Delete on audit (actual time=1327.399..1327.399 rows=0 loops=1)
   Buffers: shared hit=304202 read=5823 dirtied=4350
   ->  Index Scan using "IX_audit_at" on audit (actual time=0.375..1075.841 rows=300000 loops=1)
         Buffers: shared read=5822 dirtied=146
   Buffers: shared hit=60 read=20
 Execution Time: 1327.837 ms
--- backlog, first 50,000-row batch: the edge (index scan), then a time-range delete (AuditRetention.PurgeIfDueAsync)
 Limit (actual time=129.669..129.670 rows=1 loops=1)
   Buffers: shared read=974 dirtied=726
   ->  Index Only Scan using "IX_audit_at" on audit a (actual time=5.999..127.867 rows=50000 loops=1)
         Buffers: shared read=974 dirtied=726
   Buffers: shared hit=68 read=22
 Execution Time: 129.698 ms
 Delete on audit a (actual time=21.415..21.416 rows=0 loops=1)
   Buffers: shared hit=50973
   ->  Index Scan using "IX_audit_at" on audit a (actual time=0.016..4.425 rows=50000 loops=1)
         Buffers: shared hit=973
   Buffers: shared hit=6
 Execution Time: 21.799 ms
--- for comparison, not used: the same batch as id IN (SELECT ... LIMIT), which EF emits for OrderBy.Take.ExecuteDelete
 Delete on audit a (actual time=924.035..924.047 rows=0 loops=1)
   Buffers: shared hit=50727 read=17638 dirtied=4348 written=3591
   ->  Hash Semi Join (actual time=866.403..892.076 rows=50000 loops=1)
         Buffers: shared hit=727 read=17638 dirtied=4348 written=3591
         ->  Seq Scan on audit a (actual time=0.874..366.567 rows=1200000 loops=1)
                           ->  Index Scan using "IX_audit_at" on audit a0 (actual time=0.418..209.980 rows=50000 loops=1)
   Buffers: shared hit=160 read=29 dirtied=1
 Execution Time: 926.961 ms
```

</details>

**Size against the database budget.** The binding limit isn't Neon's 0.5 GB cap. It's README §10/§5.4: the database MUST stay under **350 MB**, and the seeder **fails above 400 MB**. Both are measured with `pg_database_size`, and `app.audit` lives in the same Neon database as the seeded data (README §5.1). The seeded database is 271 MB (§5.4), which leaves about 79 MB of headroom under 350 MB, and about 129 MB under the seeder's hard fail. At the measured ~193 bytes per row (heap and both indexes), a 90-day window costs about **17 MB per 1,000 audit rows a day**:

| Audit rows a day | 90 days of `app.audit` | Database total (271 MB seeded) |
|---|---|---|
| 1,000 (a light demo) | ~17 MB | ~288 MB, within budget |
| ~4,500 (break-even) | ~79 MB | ~350 MB, the §10 limit |
| ~7,400 | ~129 MB | ~400 MB, the seeder fails |
| 10,000 (the busy case above) | 174 MB | ~445 MB, over both |

README §1 targets about 60 internal users, and every authenticated `/api` request writes a row, so the busy case is plausible. The 90-day default fits only while the audit rate stays under about 4,500 rows a day. `AUDIT_RETENTION_DAYS` is the lever: it's an environment change, with no code change.

## Decision

- Keep audit rows for **`AUDIT_RETENTION_DAYS`** days. The default is **90**. Valid values are whole days from 1 to 36,500. Any other value logs a warning at start and keeps 90.
- `AuditWriter` calls `AuditRetention.PurgeIfDueAsync` right after a successful insert. A purge is due on the first write after start, then once every 24 h. The due check and the claim are one compare-and-swap, so concurrent callers can't both purge. A row exactly `window` old is kept.
- The purge deletes in time-range batches (c) of about 50,000 rows, oldest first, each its own statement and commit, for up to 5 s. If the budget runs out with rows left, it releases the slot, so the next audit write (within `AUDIT_FLUSH_SECONDS`) carries on. A backlog drains over a few writes, not a few days.
- A failed purge is logged and never affects the batch of audit entries, which is already saved. The attempt counts, so a failing purge is retried the next interval, not on every write. Batches that committed before the failure stay deleted.

## Consequences

- No timer, no extra connection, and nothing wakes Neon. The purge piggybacks on an insert that was already happening.
- Rows past the window can stay while the app is idle, until the next login. That's acceptable for a usage log. If audit retention ever becomes a compliance requirement with a hard deadline, revisit with option 3.
- Shortening the window takes effect at the next purge after a restart. `DELETE` frees space for reuse inside the table, but the table doesn't shrink on disk without `VACUUM FULL`, which we don't run.
- Each statement handles at most about 50,000 rows (more only on ties at the edge), so its cost doesn't grow with the backlog. A single batch would have to slow down by more than 30× over the cold numbers above to reach the 10 s timeout. If one ever does, the batches before it stay committed and the rest is retried the next day. If purges keep timing out, lower `AuditRetention.DefaultBatchSize` (a code change), or delete in slices by hand through `db-ops`.
- If `pg_database_size` approaches 350 MB, lower `AUDIT_RETENTION_DAYS` first (the purge drains the resulting backlog in batches, above). Then take the 90-day default back to Helms in #114: at the busy rate, 90 days doesn't fit the §10 budget.
- Revisit with partitioning (option 4) if the audit volume grows past a few million rows.
