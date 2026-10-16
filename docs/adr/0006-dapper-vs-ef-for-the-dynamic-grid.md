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

## Addendum (#459, 2026-10-16): a second stored weight for face-weighted prices

**Problem.** Every summary average was |MV|-weighted, so the Total row's price was Σp·|MV| / Σ|MV|, which for a bond priced per 100 face is Σp²f / Σpf: not the par-weighted price a desk quotes. Leo's scope decision (2026-10-16) on #459: build only the face-weighted average for price-like columns, `Aggregation.WeightedByCurrentFace`, weighting by `abs(current_face)::float8` with the same NULL/zero rules. Per-leg (long/short, gross/net MV) footers are out of scope.

**Options considered.**
1. **A second stored generated column** `face_weight_f8 double precision GENERATED ALWAYS AS (abs(current_face)::float8) STORED`, read like `weight_f8` (chosen).
2. **An inline cast** `abs(current_face)::float8` inside each price aggregate. It repeats the numeric→float8 cast per aggregate per row, the cost #241's first addendum measured at 2.4 s for 151 averages; the All preset has 108 price columns.
3. **A `CROSS JOIN LATERAL (… OFFSET 0)`** computing both weights once per row. It brings back the nested loop that kept every aggregate in the leader (#241), which `WeightColumnTests` now fails on.

**Which columns.** Every `ColumnKind.Price` column: `price`, `book_price`, the `price_chg_1d/1w/1m` changes, the 100 `scn_*` scenario prices, `worst_case_price`, `best_case_price` and `scenario_range` (108 of the 151 averages). Leo's list names price, book price and the scenario, worst and best prices; the price changes and the range go with them so the Total row stays consistent (Total best − Total worst = Total range, and the Total price change is a change of the Total price, when no value is NULL). `GridQueryTests` pins the rule: every Price-kind average is face-weighted, every other average |MV|-weighted.

**Evaluation.** Seed 42, scale 1.0, 20,001 rows per as-of, Postgres 17 in Docker (colima, 4 vCPUs), Apple M5, on a laptop shared with other agents' workloads (load average 10–50 during the runs), so before and after always ran interleaved.

`perf/explain-summary-face-weight.sql` (committed; output in `perf/evidence/explain-summary-face-weight-scale1.txt`): the All summary (23 `SUM`s, 43 |MV|-weighted and 108 face-weighted averages) with one weight for every average (before) and with the prices' second weight (after), 30 interleaved rounds after one warm-up, the planner's own plan:

| Run | Variant | Plan | min ms | p50 ms | p95 ms |
|---|---|---|---:|---:|---:|
| 1 (load ≈ 45) | \|MV\| weight only (before) | Finalize Aggregate → Gather (2 workers) → Partial Aggregate → Parallel Seq Scan | 40.0 | 69.2 | 189.2 |
| 1 | **prices by \|face\| (after)** | the same | 39.6 | 72.4 | 139.5 |
| 2 (load ≈ 23) | \|MV\| weight only (before) | the same | 26.9 | 29.9 | 44.7 |
| 2 | **prices by \|face\| (after)** | the same | 27.0 | 30.2 | 46.8 |

Reading a second stored column costs about 0.3 ms at p50 (1%), within noise, and the plan stays parallel.

k6 end to end (`perf/positions.js`, one VU, 30 s per scenario): main @3d55669 and this branch as two compose apps, each against its own database (both migrated and seeded alike on the same Postgres, so main's app reads a catalog it knows), alternating main, branch:

| Round | Build | MISS Risk p50 / p95 ms | HIT p50 / p95 ms | MISS All p50 / p95 ms | All ≤ 150 ms? |
|---|---|---:|---:|---:|---|
| 1 | main | 27.8 / 65.1 | 2.5 / 9.3 | 131.3 / 281.4 | no |
| 1 | **branch** | 21.2 / 52.6 | 1.1 / 2.9 | **48.4 / 74.3** | **yes** |
| 2 | main | 15.6 / 26.3 | 1.0 / 1.5 | 56.8 / 153.8 | no |
| 2 | **branch** | 21.0 / 45.2 | 4.9 / 25.0 | **93.5 / 223.6** | **no** |
| 3 | main | 22.7 / 59.4 | 2.2 / 8.6 | 71.7 / 143.2 | yes |
| 3 | **branch** | 17.5 / 54.7 | 1.5 / 5.1 | **60.1 / 122.6** | **yes** |

Every check passed in all six runs (k6 exited 99 only for crossed latency thresholds). The machine's load swung between 10 and 50 within single runs, which is where the spread comes from: the branch is faster in rounds 1 and 3 and slower in round 2, with no consistent direction. The EXPLAIN rounds above isolate the summary's cost; on the quieter machine of #241's runs, All MISS p95 was 61–83 ms, and the second weight adds about 1% to the summary.

```
# local stack only: compose -p wrk459 (own ports), seed 42 --scale 1.0 into two databases, a throwaway viewer
# from Desk.UserAdmin in each, RATE_LIMIT_PER_USER_PER_MIN/BURST raised in both apps' environments only
docker exec -i <postgres container> sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -q' < perf/explain-summary-face-weight.sql
docker run --rm -i --add-host=host.docker.internal:host-gateway -e BASE_URL=http://host.docker.internal:<port> \
  -e DESK_EMAIL=… -e DESK_PASSWORD=… grafana/k6:2.3.0 run --quiet - < perf/positions.js
```

**Decision.**
- Migration `SnapshotFaceWeightColumn` adds the stored generated column; `GridSqlBuilder.Aggregate` reads `face_weight_f8` for `WeightedByCurrentFace` and `weight_f8` for every other average, with the same text and NULL/zero rules (README §8).
- `MigrationTests` pins the frozen expression to `GridSqlBuilder.FaceWeightExpression`; `WeightColumnTests` checks the stored values and the write guard, and that the All summary, which now reads both weights, still plans as a parallel partial aggregate. `WeightedAverageTests` evaluates the price aggregate in Postgres over mixed-sign, zero and NULL faces.

**Consequences.**
- **Deploy, in two steps (expand, then the data).** The API reads each column's aggregation from `app.column_catalog`, seeded data, not from `ColumnCatalog`. Deploy runs migrate → seed → Render hook, so the app that is live during a deploy reads the catalog first. That app parsed the name with `Enum.Parse` and would fail every meta load on `WeightedByCurrentFace`. And a `SeedVersion` bump would reseed a full book, which the 512 MB reseed cap refuses (README §10). So this change ships as expand only: the column, the builder, the enum value, the code catalog (fresh seeds, the tests and CI's stacks are face-weighted) and a `MetaRepository` that falls back to the |MV|-weighted average, with a warning, for a name it doesn't know. Production's catalog keeps `WeightedByMarketValue` for prices, so its Total row stays |MV|-weighted and labelled that way (the labels follow the served aggregation). A follow-up data migration then updates those `app.column_catalog` rows once this build is live. The live app reads the new catalog within its 10-minute meta revalidation, and from that read on it serves face-weighted totals: `GridQuery.SummaryKey` and `CanonicalKey` key each column as name and aggregation, so the summary cache, the block cache and the ETag all move to new keys although `DataVersion` (a reseed) hasn't changed. Without that, cached |MV|-weighted totals would have stayed under the new labels until the next batch. Entries under the old keys simply expire at `BatchEndsAt`; no cache clear is needed. From then on, an aggregation added this way can't fail `/api/meta` during a deploy.
- **The `ALTER` rewrites the snapshot** under an ACCESS EXCLUSIVE lock, as `SnapshotWeightColumn` did (0.73–1.8 s at scale 1.0), briefly blocking grid reads during the migrate step. It's the second such rewrite of the table: on Neon that is the whole ~77 MB snapshot, so seconds of blocked grid reads (queued, not failed) while production migrates. README §14.2's deploy notes are governed (`[workflows]`), so this note lives here until a `[workflows]` PR adds a line there. Derived data, filled by the `ALTER` itself, so no `SeedVersion` bump; Down drops the column.
- **Size:** `core.position_snapshot` measured 77,709,312 bytes (74 MB, `pg_total_relation_size`) at scale 1.0 both with and without `face_weight_f8`, to the byte. The whole seeded database was 286 MB either way. Seeding is unchanged.
- **SQL portability:** a second persisted computed column on SQL Server (README §16).
