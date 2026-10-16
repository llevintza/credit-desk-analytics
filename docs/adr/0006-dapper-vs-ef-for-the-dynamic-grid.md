# ADR-0006: Dapper with a columnar reader for the dynamic grid; EF Core for the stable model

- **Status:** Accepted
- **Date:** 2026-10-08
- **Phase / PR:** phase-3/positions-api

## Context

The P1 positions grid (README §6) asks for an arbitrary subset of ~200 snapshot columns, any sort and any filter, per request. Block size is 200 rows. Two requirements sit underneath:

- **SQL safety:** every identifier comes from the column catalog and every value is a parameter (AGENTS.md).
- **Speed:** API p95 ≤ 150 ms on a cache miss and ≤ 15 ms on a hit (README §10). The response is columnar (`data[col][row]`, ADR-0007).

The page is not the expensive part. The **summary**, a COUNT plus a SUM or market-value-weighted average per displayed column over every filtered row, is the expensive part.

## Options considered

1. **Dapper + typed columnar reader.** `GridSqlBuilder` emits whitelisted SQL, and Dapper binds the parameters and runs the batch. The reader fills one typed buffer per column (`double?`, `decimal?`, `string?`, …) with no per-cell boxing. The buffers are serialized straight to the wire.
2. **Dapper `Query<dynamic>` + transpose.** Same SQL. Dapper materializes `DapperRow`s, and we transpose them into object arrays (boxed).
3. **EF Core.** The snapshot is mapped as a shared-type property bag (`Dictionary<string, object>`) built from the catalog. LINQ handles the filter, sort and paging. EF cannot project a column set chosen at runtime without hand-built expression trees, so it materializes every column. The summary (dynamic aggregates, weighted averages) has no LINQ form and would need raw SQL in a second round trip.

## Evaluation

Seed 42, scale 1.0, as-of 2026-10-06. 20,001 rows per as-of, 202 columns, about 63 MB of heap. Postgres 17.11 in Docker, Apple M5, .NET 10.0.12, 200 iterations after warm-up.

**These are local measurements.** In CI, the `budgets` job enforces the payload budget (#125) on a scale-1.0 compose stack and runs the k6 p95 checks (#135); the k6 latency results are reported but non-blocking until #87 (see the Addendum and #241).

**The page (200 rows, sort `market_value DESC`, indexed):**

| Preset | Path | p50 ms | p95 ms | alloc KB/op |
|---|---|---:|---:|---:|
| Risk (42 cols) | **Dapper + columnar reader** | **2.2** | **3.4** | **253** |
| | Dapper `dynamic` + transpose | 1.9 | 2.2 | 414 |
| | EF property bag | 6.9 | 7.9 | 5,575 |
| All (197 cols) | **Dapper + columnar reader** | **6.2** | **7.2** | **944** |
| | Dapper `dynamic` + transpose | 6.4 | 8.0 | 1,704 |
| | EF property bag | 7.0 | 9.4 | 5,843 |

**The first block of a view (page + summary in one round trip, the same SQL for any path):**

| Preset | Before the fix | After the fix (p50 / p95) |
|---|---:|---:|
| Risk (≈30 aggregates) | 113.5 / 125.8 ms | **33.2 / 34.4 ms** |
| All (174 aggregates) | 845.9 / 865.3 ms (2,428 ms serial) | **85.1 / 86.6 ms** |

The fix came from an `EXPLAIN ANALYZE` of the summary variants (174 aggregates, serial, at scale 1.0):

| Summary variant | ms |
|---|---:|
| `SUM` of every column, no weighting | 44 |
| weighted averages with `market_value::float8` inline (the first version) | **2,428** |
| weighted averages over a float weight column, no cast | 63 |
| cast once per row: `CROSS JOIN LATERAL (SELECT market_value::float8 AS weight_f8 OFFSET 0)` | **96** |

Almost all of the cost was the `numeric → float8` cast, repeated twice per weighted column per row. `OFFSET 0` stops Postgres from flattening the LATERAL back into the expressions.

**The weight is the position's size** (#131). The lateral now computes `abs(market_value)::float8`. A signed weight is right only while every position is long: a short would cancel longs in the denominator and could flip its sign. The README §8 (and §6 P1) rule is ABS-weighted, with zero or no weight giving `null`. `abs()` runs once per row in the same lateral and costs nothing measurable. `GridBenchmark`, 80 iterations, page + summary p50 at scale 1.0, alternating builds. Two earlier rounds were discarded (other load on the machine swung both builds by ±40%):

| Round | Risk before / after | All before / after |
|---|---:|---:|
| 3–4 | 37.0, 36.5 / 38.2, 36.2 ms | 89.8, 97.1 / 93.9, 96.3 ms |

```
# local stack only
DATABASE_URL=… dotnet run -c Release --project perf/GridBenchmark -- 80    # "page + summary" rows
```

**Scrolling:**
- The totals depend on the filter, not on paging or sort, so they are cached per view (`GridQuery.SummaryKey`).
- Every block after a view's first reads only its page: the 2–6 ms rows above.

**k6 against the running API** (`perf/positions.js`, Risk preset, one VU, 30 s each):

| Request | p50 ms | p95 ms | Budget |
|---|---:|---:|---|
| MISS: **the whole book's first view** (every row, a filter value never seen before; #129) | 42.7 | **71.0** | ≤ 150 |
| HIT | 1.7 | 4.3 | ≤ 15 |

The k6 figure is end-to-end (DB + columnar serialization + br compression + HTTP); the 34.4 ms above is the DB round trip alone.

**Corrected in #129 (R121-F5).** The MISS row above used to say 11.7 / 13.9 ms. That run's filter, `spread_bp > ~1,100`, selected only the B/CCC tail, not the whole-book first view the 150 ms budget is about. That run's script used a threshold below every spread, unique per VU and iteration, so every row was counted and summarised, with a `whole book` check of `rowCount ≥ 19,000` at scale 1.0. Since #202 the threshold also carries a per-run offset (`−1e12·VU − 1e6·run − n`), and `whole book` asserts that `rowCount` equals the unfiltered count from `setup()`, with a separate `book >= MIN_BOOK` guard (`MIN_BOOK`, a whole number, default 19,000; a blank or non-numeric value stops the run). Since #244 the All-preset scenario's `All whole book` check uses the same exact count. The same API build and local database were used for both runs:

| MISS filter | rows | p50 ms | p95 ms |
|---|---:|---:|---:|
| `spread_bp > ~1,100` (before) | the B/CCC tail | 14.0 | 18.6 |
| `spread_bp > −1e6·VU − n` (after) | every row (~20k) | 42.7 | 71.0 |

```
# local stack only; abridged k6 1.3.0 summary; seed 42, scale 1.0; API: dotnet run -c Release --no-launch-profile, ASPNETCORE_ENVIRONMENT=Production, ASPNETCORE_URLS=http://localhost:5185 (the reproduce block uses the script's default :5181), per-user limits raised in that process
docker run --rm -i --add-host=host.docker.internal:host-gateway -e BASE_URL=http://host.docker.internal:5185 \
  -e DESK_EMAIL=… -e DESK_PASSWORD=… grafana/k6:1.3.0 run - < perf/positions.js
✓ 'p(95)<150' http_req_duration{scenario:miss} p(95)=70.98ms
✓ 'p(95)<15'  http_req_duration{scenario:hit}  p(95)=4.25ms
✓ checks rate=100.00% (28,568 of 28,568: login, 200, MISS, whole book, X-Cache present)
  { scenario:miss }: p(50)=42.71ms p(95)=70.98ms max=146.3ms
  { scenario:hit }:  p(50)=1.69ms  p(95)=4.25ms  max=143.14ms
```

**How to reproduce:**

```
DATABASE_URL=… dotnet run -c Release --project src/Desk.Seeder -- --force --scale 1.0 --as-of 2026-10-06
DATABASE_URL=… dotnet run -c Release --project perf/GridBenchmark -- 200 perf/out
# Local stack only. API: ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS=http://localhost:5181 dotnet run -c Release --no-launch-profile --project src/Desk.Api, with RATE_LIMIT_PER_USER_PER_MIN/BURST raised in its own process (one k6 user), then:
docker run --rm -i --add-host=host.docker.internal:host-gateway -e BASE_URL=http://host.docker.internal:5181 \
  -e DESK_EMAIL=… -e DESK_PASSWORD=… grafana/k6:1.3.0 run - < perf/positions.js
```

## Decision

- **Dapper + a typed columnar reader** for the dynamic grid (`GridRepository`).
  - It matches Dapper `dynamic` on time.
  - It allocates 40–45% less than Dapper `dynamic`, and 5–20× less than EF.
  - It feeds the columnar serializer without boxing.
- **The page and the summary go in one batch** for the first block of a view (README §6 "one round trip"). After that, the cached summary means only the page is read.
- **The weight is cast once per row** through a non-flattenable LATERAL. Superseded by the #241 addendum: a stored generated column, which also lets the summary aggregate in parallel workers.
- **EF Core stays for the stable model:** accounts, presets (`AsNoTracking` projections, `ExecuteUpdate`/`ExecuteDelete`), catalog and seed metadata.

## Consequences

- SQL text is built by hand. The safety rests on `GridQueryNormalizer`: catalog-only identifiers, quoted, with every value a parameter. It has a dedicated unit test suite, including SQL snapshot tests and injection tests.
- The SQL is close to ANSI. Two things are Postgres-specific: `ILIKE`, and `LATERAL … OFFSET 0`, which would become `CROSS APPLY` on SQL Server (README §16). Since #241 the weight is a stored generated column (a persisted computed column on SQL Server) instead of the LATERAL.
- The summary still scans every filtered row: 33 ms (Risk) and 85 ms (All) of DB time locally for the first view of the whole book (k6 end-to-end, Risk: p95 71 ms). On Neon's fractional CPU expect several times that, still well under the 10 s guard.
- Cache keys are the JSON form of the normalized query: values are escaped by the serializer, so a crafted filter can't collide with another query's key or ETag in the shared cache.
- The reference data behind every key (catalog, as-of dates, data version) is re-read every 10 minutes while traffic continues, and every 30 seconds while the database is still empty. A reseed is therefore picked up without a restart. `POST /api/admin/cache/clear` drops everything at once.
- Revisit if the snapshot grows past ~100k rows per as-of. Options then: materialized per-filter totals, or a narrower aggregate table.

## Addendum (#135, 2026-10-08): All-preset whole-book MISS

README §10's 150 ms MISS budget is not scoped to a preset, so `perf/positions.js` now also measures the All preset (every column of the built-in All preset, ~200) under the same whole-book, never-asked filter, as its own `all_miss` trend with `p(95)<150`. The CI `budgets` job reports it (#135). Local numbers, labelled as such: compose stack (`docker compose up --build`), seed 42, scale 1.0, per-user limits raised in that app's environment only, one VU, 30 s per scenario, on a laptop shared with other workloads (so the Risk MISS p95 also ran higher than the 71.0 ms above):

| Run | MISS Risk p50 / p95 ms | HIT p50 / p95 ms | MISS All p50 / p95 ms | All within 150 ms? |
|---|---:|---:|---:|---|
| 1 | 40.9 / 80.6 | 1.5 / 6.3 | 119.8 / 227.7 | **no** |
| 2 | 55.1 / 117.6 | 1.3 / 4.9 | 117.0 / 272.8 | **no** |
| 3 | 57.2 / 118.6 | 2.3 / 6.1 | 114.8 / 188.0 | **no** |

The All-preset whole-book first view is **over the 150 ms budget** locally (p50 ~115–120 ms, p95 188–273 ms), while Risk and HIT are within theirs. The budget stays as written; whether to optimise the All first view or rescope the MISS budget to the Risk preset is open for Leo/Helms (#241).

```
# local stack only (compose, -p desk135, its own ports); a throwaway viewer from Desk.UserAdmin
docker run --rm -i --add-host=host.docker.internal:host-gateway -e BASE_URL=http://host.docker.internal:18135 \
  -e DESK_EMAIL=… -e DESK_PASSWORD=… grafana/k6:1.3.0 run - < perf/positions.js
```

## Addendum (#241, 2026-10-16): the weight as a stored column, and a parallel summary

**Problem.** The All preset's whole-book first view was over the 150 ms MISS budget: locally p95 188–273 ms (above), and on the CI runner p95 ≈ 249 ms (#256). Server-Timing put almost all of it in the database: the summary, `COUNT` plus 174 aggregates (23 `SUM`s, 151 weighted averages) over every row of the as-of date. Serialization was 2–3 ms.

**Cause.** The `CROSS JOIN LATERAL (SELECT abs(market_value)::float8 AS weight_f8 OFFSET 0) w` that casts the weight once per row (above) has two costs. It adds a nested loop and a one-row subquery per row. And the planner can't aggregate below it, so the whole summary ran in the leader. With parallel plans made free, the plan was `Aggregate → Nested Loop → Gather → Parallel Seq Scan`: only the scan went to the workers, and all 174 aggregates stayed above the Gather.

**Options considered.**
1. **A stored generated column** `weight_f8 double precision GENERATED ALWAYS AS (abs(market_value)::float8) STORED` on `core.position_snapshot`, so the summary is a plain aggregate over the table (chosen).
2. **A plain column the seeder writes.** Same plan, but a second write path that could drift from `market_value`. The generated column can't be written at all (SQLSTATE 428C9), and Postgres recomputes it on every insert and update.
3. **Parallel settings, keeping the LATERAL.** Session `parallel_*` costs don't help: the fence keeps the aggregates in the leader whatever the costs (`WeightColumnTests` pins the new plan shape).
4. **JIT** (`jit_above_cost = 0`) for the 174-aggregate expression. Slower: compiling cost more than it saved (280–1,600 ms against 140–430 ms without, on the same loaded machine).
5. **Rescoping the budget** to the Risk preset (#241's other option). Not needed once the All preset is under budget.

**Evaluation.** Seed 42, scale 1.0, 20,001 rows per as-of, Postgres 17.11 in Docker (colima, 4 vCPUs), Apple M5, on a laptop shared with other agents' workloads, so before and after always ran interleaved against the same database.

`perf/explain-summary-weight.sql` (committed; output in `perf/evidence/explain-summary-weight-scale1.txt`): the All summary's execution time, 30 interleaved rounds after one warm-up:

| Variant | Plan | min ms | p50 ms | p95 ms |
|---|---|---:|---:|---:|
| LATERAL weight (before) | Aggregate → Nested Loop → Seq Scan | 80.1 | 89.8 | 103.9 |
| stored column, serial | Aggregate → Seq Scan | 64.8 | 71.4 | 88.6 |
| **stored column, the planner's own plan (after)** | Finalize Aggregate → Gather (2 workers) → Partial Aggregate → Parallel Seq Scan | **28.8** | **32.6** | **44.8** |

About a third of the gain (p50 89.8 → 71.4 ms) is dropping the nested loop. The rest is the two parallel workers the default settings (`max_parallel_workers_per_gather = 2`) already allow.

k6 end to end (`perf/positions.js`, one VU, 30 s per scenario): the compose stack's app (main @91a8d7e) on one port and the branch image on another, both against the same migrated database, alternating main, branch, main, branch:

| Run | Build | MISS Risk p50 / p95 ms | HIT p50 / p95 ms | MISS All p50 / p95 ms | All ≤ 150 ms? |
|---|---|---:|---:|---:|---|
| 1 | main | 36.8 / 58.4 | 1.0 / 1.5 | 106.1 / 154.4 | no |
| 1 | **branch** | 15.7 / 27.8 | 1.0 / 1.7 | **53.8 / 82.6** | **yes** |
| 2 | main | 36.4 / 45.5 | 1.0 / 1.6 | 99.5 / 115.6 | yes |
| 2 | **branch** | 15.4 / 22.0 | 1.0 / 1.4 | **51.1 / 71.4** | **yes** |
| 3 | main | 36.8 / 50.0 | 1.0 / 1.3 | 97.4 / 111.6 | yes |
| 3 | **branch** | 15.1 / 19.5 | 1.0 / 1.4 | **50.1 / 60.7** | **yes** |

The All whole-book MISS halves (p50 ~100 → ~52 ms; p95 112–154 → 61–83 ms), and the Risk MISS improves by about 2.4× too. Every check passed in all six runs. The machine was quieter for this round than for the earlier ones (load average 5–10, not 20–75), which is why main's own p95 is lower than in #135's table. The comparison holds because the two builds alternated.

```
# local stack only: compose -p wrk241 (own ports), seed 42 --scale 1.0, a throwaway viewer from Desk.UserAdmin,
# RATE_LIMIT_PER_USER_PER_MIN/BURST raised in both apps' environments only
docker exec -i <postgres container> sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -q' < perf/explain-summary-weight.sql
docker run --rm -i --add-host=host.docker.internal:host-gateway -e BASE_URL=http://host.docker.internal:<port> \
  -e DESK_EMAIL=… -e DESK_PASSWORD=… grafana/k6:2.3.0 run --quiet - < perf/positions.js
```

**A script fix that the faster MISS exposed.** At ~2.5× the MISS rate, the 30 s MISS phase stores more blocks than `POSITIONS_CACHE_MB` (64 MB) holds. The cache's compaction then evicts the HIT key that `setup()` warmed at t=0, so the first one or two HIT requests missed, and `checks: rate==1.0` failed (1–2 of ~50,000 checks in each branch run). `hit()` now warms the key once per VU before it measures, tagged `scenario: hit_warm` so the warm-up isn't in the HIT trend and has no check on it. No budget or threshold changed.

**Decision.**
- Migration `SnapshotWeightColumn` adds the stored generated column. `GridSqlBuilder` reads `weight_f8` from the table, with no LATERAL. The aggregate text and the NULL/zero rules (README §8) are unchanged.
- `MigrationTests` pins the migration's frozen expression to `GridSqlBuilder.WeightExpression`. `WeightColumnTests` checks the stored values (shorts weigh by their size, zero stays zero, NULL stays NULL, an update recomputes) and that the All summary plans as a parallel partial aggregate (that test fails on the LATERAL builder).

**Consequences.**
- **Deploy:** expand only (README §14.4). The running app never names the column, and the seeder's COPY lists its columns, so both work before and after the migration. The `ALTER` rewrites the snapshot under an ACCESS EXCLUSIVE lock: 0.73 s at scale 1.0 on a quiet machine (1.8 s under load), which briefly blocks grid reads during the deploy's migrate step. Derived data is filled by the `ALTER` itself, so there's no `SeedVersion` bump. Down drops the column, and the old builder doesn't need it.
- **Size:** `core.position_snapshot` measured 74 MB at scale 1.0 both before and after (`pg_total_relation_size`). The rewrite briefly needs a second copy of the table (~74 MB), well under the 512 MB reseed cap. Seeding is unchanged: the snapshot COPY took 2.1 s, and the whole scale-1.0 seed 9.5 s (budget < 90 s).
- **Parallel workers share the CPU.** A first view now uses up to three processes for ~30 ms instead of one for ~90 ms. Under many concurrent MISSes, Postgres caps the workers (`max_parallel_workers`, default 8). A Gather that gets fewer workers, or none, does the rest in the leader, which is still the faster serial path without the LATERAL. On Neon's fractional CPU, the serial gain (no nested loop) applies whatever the worker count.
- **SQL portability:** on SQL Server this would be a persisted computed column (README §16).
- **Hosted CI runner** (`budgets` job, run 38934774373 on PR #460, ubuntu-24.04): MISS All p50 / p95 / max 143.0 / 155.5 / 186.3 ms (was 217.3 / 249.0 / 418.1 ms, #256), MISS Risk 45.5 / 57.2 ms (was 75.2 / 134.7 ms), HIT 1.4 / 2.0 ms, 39,969 checks passed and 0 failed. The hosted runner is ~2.5× slower than the local stack, so All is still 5.5 ms over the 150 ms p95 there. README §10 states the budget as "local, warm".
- **What's left for #241:** decide whether the local budget also applies on hosted runners (Helms' 2026-10-08 ruling suggested hosted thresholds of about 1.3× local), or take a further optimisation. The summary is now near its scan-and-deform floor locally: the 23 numeric `SUM`s alone take 19 ms, and the full summary 30 ms. The next lever is running the page and the summary concurrently on two connections, worth roughly the page read (~6 ms locally). It takes two pooled connections per first view against a pool sized for Neon (`DB_MAX_POOL_SIZE`, default 20), so it needs its own decision.

## Addendum (#241 follow-up, 2026-10-16): a first view reads its page and totals on two connections

**Context.** After the stored weight column (above), the All preset's whole-book MISS p95 on the hosted `budgets` runner was 155.5 ms against the 150 ms budget. The page and the summary still ran one after the other, in one batch on one connection, so a first view waited for the page read (~6 ms locally) plus the summary (~30–35 ms). This addendum adopts the next step the addendum above named: run the two at once, on two pooled connections.

**Options.**
1. Keep one batch (status quo).
2. Always open a second connection for a first view. That's simple, but each first view would then hold two connections on one permit. The 8 shared permits could hold 23 connections, past the 20 that `DB_MAX_POOL_SIZE` and the `ConnectionBudget` sum (#211) allow. Under load the pool, not the limiter, would refuse requests.
3. **Take a second shared database permit for the second connection, without waiting (chosen).** The rate limiter's `db` partition and the endpoint use one `ConcurrencyLimiter`, owned by `DatabasePermits`. `TryTakeSpare()` grants a spare only when nobody is queued and at least one other permit stays free, and it never waits. Without a spare, the first view is the one batch of option 1.
4. A separate pool or semaphore for summaries, like the insights fan-out (`INSIGHTS_MAX_CONNECTIONS`). That's a new setting plus a new term in the budget sum, for no gain over option 3.

**Why option 3 can't deadlock or starve.** A request holding a permit never waits for a second one (the attempt is synchronous and fails at once), so there's no hold-and-wait cycle. The limiter serves its queue oldest first, and a spare is refused while anyone is queued, so a spare never jumps the queue. Because the last free permit is never taken, a request that arrives next still finds one. Under saturation no spares are granted, and the endpoint behaves exactly as before. "Never the last free permit" is best-effort: the statistics check and `AttemptAcquire(1)` are two steps, so two first views racing with exactly two permits free can both get a spare and take the last one. The worst case is that the next request waits in the queue for one summary read (~35 ms locally). It can't deadlock, because nothing that holds a permit waits, and it doesn't break the pool sum, because every connection still holds a permit. A lock around the two steps would serialise every first view to remove a rare, bounded wait, so it isn't worth it. Every connection a request holds is still one permit, so the README §7.2 permits-vs-pool sum is unchanged (15 + 5 headroom = 20 at the defaults).

**Correctness.** The page and the totals run on separate connections, never one connection or `DbContext` shared by two commands. The summary statement is the batch's second statement (`GridSqlBuilder.BuildSummary`: same text, same parameter names, the same `portfolio_id = ANY(@portfolios)` scope in SQL). If one read fails, the other is cancelled, and the caller sees the failure, not the sibling's cancellation. The caller's `CancellationToken` cancels both reads. Under READ COMMITTED each statement of the old batch already took its own snapshot, so reading on two connections changes no consistency guarantee, and the data changes only at a reseed (which also changes the ETag and the cache keys).

**Evaluation** (local stack only: compose `-p wrk241c` with its own ports, seed 42, scale 1.0, as-of 2026-10-15, Postgres 17 in colima with 4 vCPUs, Apple M5, a shared machine. Throwaway viewer from Desk.UserAdmin; per-user limits raised in both apps' environments only. Base = #460's head image @a25afc1, branch = this change, both on **the same database**, alternating.)

`perf/GridBenchmark` (committed; it now reads the latest as-of in the database), first block of a view, 200 rows, 50 timed iterations per row:

| Run | Risk: one batch p50 / p95 ms | Risk: two connections | All: one batch p50 / p95 ms | All: two connections | All page alone p50 |
|---|---:|---:|---:|---:|---:|
| 1 | 12.8 / 15.2 | 11.8 / 13.9 | 40.5 / 49.4 | **34.8 / 41.5** | 6.1 |
| 2 | 13.1 / 16.8 | 11.6 / 13.2 | 45.0 / 51.8 | **37.0 / 46.5** | 6.3 |
| 3 | 13.5 / 14.8 | 12.3 / 14.0 | 43.1 / 52.0 | **38.0 / 57.5** | 6.8 |

The All first view gains 5–8 ms at p50, about the page read, as predicted. Risk gains about 1 ms.

`perf/all-miss-timing.js` (committed; k6, one VU, 30 s, the All whole-book MISS, with the API's own `Server-Timing` next to k6's wall time):

| Round | Build | db p50 / p95 ms | total p50 / p95 ms | wall p50 / p95 ms |
|---|---|---:|---:|---:|
| 1 | base | 41.3 / 53.6 | 43.1 / 56.1 | 47.6 / 62.7 |
| 1 | **branch** | **35.8 / 45.2** | 37.7 / 47.1 | **42.2 / 52.5** |
| 2 | base | 42.0 / 65.2 | 44.0 / 67.3 | 48.5 / 75.2 |
| 2 | **branch** | **35.0 / 43.6** | 36.8 / 46.0 | **41.3 / 51.2** |

`perf/positions.js` (the budgets script, one VU, 30 s per scenario):

| Run | Build | MISS Risk p50 / p95 ms | HIT p50 / p95 ms | MISS All p50 / p95 ms |
|---|---|---:|---:|---:|
| 1 | base | 14.9 / 22.2 | 1.0 / 1.8 | 48.2 / 64.3 |
| 1 | branch | 14.0 / 20.6 | 1.0 / 1.7 | 44.0 / 63.7 |
| 2 | base | 15.2 / 23.1 | 1.0 / 1.6 | 47.0 / 56.1 |
| 2 | branch | 13.9 / 18.2 | 1.0 / 1.9 | 46.0 / 89.0 |
| 3 | base | 15.5 / 22.9 | 1.1 / 3.2 | 55.5 / 91.7 |
| 3 | branch | 14.9 / 23.3 | 1.1 / 2.8 | 52.6 / 107.9 |

The k6 rows were measured on this branch's code before `BuildSummary` was changed to reuse the batch's parameter names, so the summary SQL text then differed only in those names. A later round with the final image ran at load average 25–45 (other workers' stacks): both builds doubled, and it isn't used. Every positions.js run passed its thresholds except base run 1, which failed 1 check of 56,049. The p50 is lower in every pair (1–4 ms). Locally the p95 is dominated by noise on the shared machine: run 3 ran while another worker's stack was busy. The first two tables isolate the change and show it consistently.

```
# local stack only (see above); DATABASE_URL points at the compose Postgres on its own port
DATABASE_URL=… dotnet run -c Release --project perf/GridBenchmark -- 200 <outDir>
docker run --rm -i --add-host=host.docker.internal:host-gateway -e BASE_URL=http://host.docker.internal:<port> \
  -e DESK_EMAIL=… -e DESK_PASSWORD=… grafana/k6:2.3.0 run --quiet - < perf/all-miss-timing.js
docker run --rm -i --add-host=host.docker.internal:host-gateway -e BASE_URL=http://host.docker.internal:<port> \
  -e DESK_EMAIL=… -e DESK_PASSWORD=… grafana/k6:2.3.0 run --quiet - < perf/positions.js
```

**Decision.** Option 3. `GridRepository.ReadBlockAsync(…, concurrentSummary)` reads both at once when the endpoint holds a spare permit from `DatabasePermits.TryTakeSpare()`, and the spare is released once both connections are back in the pool. No setting, budget or threshold changed.

**Consequences.**
- A first view can hold two permits for ~35 ms instead of one for ~41 ms. That's more permit-time per first view, but only while permits are idle. When requests queue, no spares are granted.
- On Neon (`DB_MAX_POOL_SIZE` 20, a fractional CPU) the pool budget is unchanged. The page read is index-driven and small, so the second backend adds little CPU next to the summary's scan.
- Tests: `DatabasePermitsTests` (a spare leaves one permit free, none with a single permit, none while a request is queued, one shared count); `ConcurrentSummaryTests` (two connections at once with the same block and totals as the batch; portfolio scope in SQL, an empty scope gives 0 rows; a failing page or summary surfaces its own error with both connections returned; a cancelled caller cancels both; the endpoint uses two connections for a first view and one for a later block; with one permit it uses one; six simultaneous first views on three permits all finish and never hold more than three connections).
- **Hosted CI runner** (`budgets` job, run 38942923656 on PR #470 @7ec525e, ubuntu-24.04): MISS All p50 / p95 / max **120.2 / 134.9 / 171.8 ms** (after #460: 143.0 / 155.5 / 186.3 ms), MISS Risk 40.5 / 53.2 ms (was 45.5 / 57.2 ms), HIT 1.4 / 2.0 ms, 39,970 checks passed and 0 failed, all thresholds passed. The All whole-book MISS was within the 150 ms p95 budget on the hosted runner in both runs, with no budget or threshold changed. A second run on the docs-only head (run 38944173331 @cc00da6) measured MISS All p50 / p95 / max 93.4 / 101.7 / 150.0 ms, MISS Risk p95 42.4 ms, HIT p95 1.5 ms, 52,705 checks passed and 0 failed. Hosted runs have varied by about ±20 ms between runs, so whether two runs are enough to close #241 is Leo's call. The hosted gain (~20 ms at p95) is larger than the local one (~6–10 ms), as expected: on the slower runner the page read that now overlaps the summary is longer too.
