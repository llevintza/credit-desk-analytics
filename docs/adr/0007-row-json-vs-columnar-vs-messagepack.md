# ADR-0007: Columnar JSON by default; MessagePack on request

- **Status:** Accepted
- **Date:** 2026-10-08
- **Phase / PR:** phase-3/positions-api

## Context

The P1 grid fetches blocks of 200 rows of 40–200 columns. README §10 sets the budgets on the wire: the first block of the "Risk" preset must be ≤ 60 KB compressed (CI fails above it), and the "All" preset ≤ 250 KB (CI warns). The browser must also turn each block into rows for AG Grid's Infinite Row Model quickly. Responses are compressed with Brotli or gzip at `Fastest` (`Program.cs`).

## Options considered

1. **Row JSON:** `[{ "deal_name": …, "dv01": … }, …]`. Every row repeats every property name.
2. **Columnar JSON:** `{ columns: [...], data: [[col0 values], [col1 values], …], rowCount, summary }`. It is written straight from the typed column buffers with `Utf8JsonWriter`. The other DTOs use System.Text.Json source generation (`DeskJsonContext`).
3. **Columnar MessagePack:** the same document in MessagePack (MessagePack-CSharp 3.1.11), served on `Accept: application/x-msgpack`. Money goes as float64, because MessagePack has no decimal. That is exact to the cent below ~9e13.

## Evaluation

The same 200-row block for each (seed 42, scale 1.0). Server: .NET 10, Apple M5. Client: Node 24 (V8, the same JSON engine as Chrome).

| Preset | Format | raw KB | gzip KB | **brotli KB** | serialize ms | parse ms | parse + rows ms (p50 / p95) |
|---|---|---:|---:|---:|---:|---:|---:|
| Risk (42 cols) | Row JSON | 170.4 | 46.8 | 39.4 | 1.45 | 0.26 | 0.26 / 0.37 |
| | **Columnar JSON** | **61.9** | **27.6** | **25.2** | 0.40 | 0.11 | 0.28 / 0.35 |
| | Columnar MessagePack | 70.6 | 34.4 | 36.4 | 0.21 | 0.07 | 0.21 / 0.30 |
| All (197 cols) | Row JSON | 880.5 | 269.2 | 227.3 | 2.49 | 2.89 | 2.89 / 3.30 |
| | **Columnar JSON** | **304.2** | **133.1** | **120.7** | 1.84 | 0.55 | 1.36 / 1.79 |
| | Columnar MessagePack | 336.0 | 143.3 | 147.7 | 0.95 | 0.29 | 0.95 / 1.18 |

**On the wire through the running API** (`perf/payload-size.mjs`, `Accept-Encoding: br, gzip`):

| Preset | Columns | Encoding | KB | Budget |
|---|---:|---|---:|---|
| Risk | 42 | br | **23.2** | ≤ 60 (fail) |
| All | 197 | br | **117.2** | ≤ 250 (warn) |

**Notes:**
- Columnar JSON is about a third the raw size of row JSON, and about 40% smaller compressed.
- MessagePack is *larger* than columnar JSON once compressed. Its type-tagged binary numbers compress worse than the short decimal text JSON uses for these values.
- MessagePack decodes about 0.4 ms faster on the 197-column block and costs a ~30 KB client library.

**How to reproduce:**

```
DATABASE_URL=… dotnet run -c Release --project perf/GridBenchmark -- 200 perf/out
(cd perf && npm ci) && node perf/parse-bench.mjs perf/out 500
BASE_URL=http://localhost:5181 DESK_EMAIL=… DESK_PASSWORD=… node perf/payload-size.mjs
```

## Decision

- **Columnar JSON is the default.** It is the smallest on the wire, which is what the budgets measure. It is fast enough to parse (≤ 1.8 ms p95 for 200×197 including the transpose to rows), and it needs no client dependency.
- **MessagePack stays available** on `Accept: application/x-msgpack`. The cache key and ETag include the representation. It is useful for non-browser clients, and for revisiting the decision if column counts grow. It is **not** on by default.

## Consequences

- The client must transpose `data[col][row]` into row objects (`toRows`, phase 4). The Vitest tests pin it.
- `NaN` and infinity can't appear in JSON. Non-finite doubles serialize as `null`, consistent with the "never NaN" rule (README §8).
- Revisit if the grid moves to a binary transport, or if a block grows past ~500 KB raw.
