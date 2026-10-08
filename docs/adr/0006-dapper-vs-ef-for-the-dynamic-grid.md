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

**Scrolling:**
- The totals depend on the filter, not on paging or sort, so they are cached per view (`GridQuery.SummaryKey`).
- Every block after a view's first reads only its page: the 2–6 ms rows above.

**k6 against the running API** (`perf/positions.js`, Risk preset, one VU, 30 s each):

| Request | p50 ms | p95 ms | Budget |
|---|---:|---:|---|
| MISS: **the whole book's first view** (every row, a filter value never seen before; #129) | 42.7 | **71.0** | ≤ 150 |
| HIT | 1.7 | 4.3 | ≤ 15 |

**Corrected in #129 (R121-F5).** The MISS row above used to say 11.7 / 13.9 ms. That run's filter, `spread_bp > ~1,100`, selected only the B/CCC tail, not the whole-book first view the 150 ms budget is about. The script now uses a threshold below every spread, unique per iteration, so every row is counted and summarised, and a `whole book` check asserts `rowCount ≥ 19,000` at scale 1.0. The same API build and local database were used for both runs:

| MISS filter | rows | p50 ms | p95 ms |
|---|---:|---:|---:|
| `spread_bp > ~1,100` (before) | the B/CCC tail | 14.0 | 18.6 |
| `spread_bp > −1e6·VU − n` (after) | every row (~20k) | 42.7 | 71.0 |

```
# local stack only; seed 42, scale 1.0; per-user limits raised in the local API process
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
# Local stack only. API with RATE_LIMIT_PER_USER_PER_MIN/BURST raised in its own process (one k6 user), then:
docker run --rm -i --add-host=host.docker.internal:host-gateway -e BASE_URL=http://host.docker.internal:5181 \
  -e DESK_EMAIL=… -e DESK_PASSWORD=… grafana/k6:1.3.0 run - < perf/positions.js
```

## Decision

- **Dapper + a typed columnar reader** for the dynamic grid (`GridRepository`).
  - It matches Dapper `dynamic` on time.
  - It allocates 40–45% less than Dapper `dynamic`, and 5–20× less than EF.
  - It feeds the columnar serializer without boxing.
- **The page and the summary go in one batch** for the first block of a view (README §6 "one round trip"). After that, the cached summary means only the page is read.
- **The weight is cast once per row** through a non-flattenable LATERAL.
- **EF Core stays for the stable model:** accounts, presets (`AsNoTracking` projections, `ExecuteUpdate`/`ExecuteDelete`), catalog and seed metadata.

## Consequences

- SQL text is built by hand. The safety rests on `GridQueryNormalizer`: catalog-only identifiers, quoted, with every value a parameter. It has a dedicated unit test suite, including SQL snapshot tests and injection tests.
- The SQL is close to ANSI. Two things are Postgres-specific: `ILIKE`, and `LATERAL … OFFSET 0`, which would become `CROSS APPLY` on SQL Server (README §16).
- The summary still scans every filtered row: 33 ms (Risk) and 85 ms (All) locally for the first view of the whole book. On Neon's fractional CPU expect several times that, still well under the 10 s guard.
- Cache keys are the JSON form of the normalized query: values are escaped by the serializer, so a crafted filter can't collide with another query's key or ETag in the shared cache.
- The reference data behind every key (catalog, as-of dates, data version) is re-read every 10 minutes while traffic continues, and every 30 seconds while the database is still empty. A reseed is therefore picked up without a restart. `POST /api/admin/cache/clear` drops everything at once.
- Revisit if the snapshot grows past ~100k rows per as-of. Options then: materialized per-filter totals, or a narrower aggregate table.
