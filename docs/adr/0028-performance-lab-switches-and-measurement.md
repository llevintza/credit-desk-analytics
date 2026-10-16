# ADR-0028: Performance Lab: header switches on the real endpoints, measured with Resource Timing (measured)

- **Status:** Proposed
- **Date:** 2026-10-16
- **Phase / PR:** phase-8/performance-lab (#64, #65)

## Context

README §6 P5 asks for a page that shows, with live numbers, why the grid is built the way it is. It runs the P1 block query with six toggles: payload format (row JSON, columnar JSON, MessagePack), block size (100, 200, 500), columns (displayed or all), cache (on or off, admin only), compression (br, gzip or none, dev only), and P3 naive 20 calls against 5 per-source calls. For each run it shows the bytes on the wire and decoded, TTFB, download, decode and grid render times, the Server-Timing breakdown and the cache status. The last 20 runs copy as markdown. The acceptance criterion is that every number comes from a real request.

Four things on `main` stood in the way:

- **There is no row-JSON representation.** ADR-0007 measured row objects offline (`perf/GridBenchmark`) and rejected them, so the API never served them.
- **Scripts can't set `Accept-Encoding`.** It is a forbidden request header in `fetch` and XHR, so the browser alone can't ask for gzip or for no compression.
- **There is no way to skip the cache.** Every positions and insights read is cache-first (README §7.2, ADR-0009). After the first run, a MISS can't be measured again without a reseed or `POST /api/admin/cache/clear`, which drops the cache for everyone.
- **MessagePack responses were sent uncompressed.** The Lab found this on its first run. ASP.NET Core's response compression only compresses its default MIME list (`text/*`, `application/json`, `application/javascript`, `application/xml`, `application/wasm`). `application/x-msgpack` isn't on that list, so every MessagePack block went out raw, although ADR-0007 compared formats by their Brotli size. `LabTests.Every_payload_format_is_compressed` fails on the old configuration (checked by removing the new line: both the MessagePack and the row-JSON cases fail, with no `Content-Encoding`).

## Options considered

**How the toggles reach the server**

1. **Request headers on the real endpoints (chosen).** `X-Lab-Cache: off` and `X-Lab-Encoding: br|gzip|identity` on `POST /api/positions/query` and `GET /api/insights/{source}`. The payload format is plain content negotiation: `Accept: application/vnd.desk.rows+json` asks for row objects, next to the existing `application/x-msgpack`. The Lab measures the same handler, cache, serializer and compression the pages use.
2. **Separate `/api/lab/*` endpoints.** These would duplicate the query path, and each new `/api` path needs an entitlement classification (ADR-0021 R4) and OpenAPI pinning. Worse, the Lab would no longer measure what the pages run.
3. **Query parameters (`?cache=off`).** These would work, but they mix measurement knobs into the resource URL. They also look like part of the query a cache key should honour, which they must not be.

**Who may use which switch**

- **Cache off is admin only, as §6 P5 says.** The handler checks `IsInRole(admin)`, and a viewer's header is ignored (`A_viewer_cannot_switch_the_cache_off`). Rate limits and database permits still apply. A bypassed response is neither read from nor stored in the response cache. The summary cache is skipped both ways, so the run pays the full MISS cost and leaves the shared cache as it was. `X-Cache` reports `BYPASS`, and so does the audit row.
- **Forced encoding is dev only.** It is honoured where the existing dev knobs are on: Development, or `DEV_FAULT_INJECTION=true`, which only the e2e compose override sets (ADR-0012). Production never sets it (`ProductionLimitsGuardTests`), and `Production_ignores_the_encoding_override` pins that. A middleware before `UseResponseCompression` replaces `Accept-Encoding` and echoes the applied value in `X-Lab-Encoding`, so the results table shows what was really applied, not what was asked.
- **CORS:** the Pages front end (ADR-0024) may send both headers and read the echo (`AllowedHeaders`, `ExposedHeaders`).

**How the client measures**

1. **Resource Timing plus performance marks (chosen).** Each request carries a unique `?lab=<tag>`, so its `PerformanceResourceTiming` entry is found exactly. A Lab-owned `PerformanceObserver` reads it, without clearing or resizing the page's buffer (the status bar reads that too). From the entry come `encodedBodySize` and `decodedBodySize`, TTFB (`responseStart − requestStart`) and download (`responseEnd − responseStart`). The body comes back as an `ArrayBuffer` through the app's `HttpClient`, so the bearer token, the session handling and the API origin apply as on the pages, with `cache: 'no-store'`. The page then times the decode (`lab:parse:<id>` marks) and the grid render (`lab:render:<id>`: `setGridOption('rowData')` until the next painted frame). Cross-origin entries need `Timing-Allow-Origin`, which the API already sends to allowed origins; without it the Lab shows n/a, never 0.
2. **`performance.now()` around `fetch` only.** This gives no bytes on the wire, and its "TTFB" would include any connection queue.
3. **Server-side timings only.** These miss the network and the client entirely, which is half of what the page is for.

**The MessagePack decoder**

1. **`@msgpack/msgpack` 3.1.3, imported on the first MessagePack run (chosen).** It is the reference JavaScript implementation, it has no dependencies, and it is the same package and version `perf/parse-bench.mjs` already uses for ADR-0007, so the Lab and the offline bench decode identically. Money arrives as exact strings (ADR-0007) and becomes numbers inside the timed decode, so the cost is counted.
2. **`msgpackr`.** It is faster on large documents, but it is a second library with different semantics from the bench's, and at 200 × 202 cells the decode is already about 1.3 ms.
3. **A hand-written decoder.** This would be about 150 lines to own and test, for a demo page.

## Evaluation

**Measured by the Lab itself** on a local compose stack (the e2e override: dev knobs on, raised limits). Seed 42, scale 1.0, as-of 2026-10-15. Release API in Docker on colima, Chromium (Playwright 1.x), Apple M5. A local admin account, so the cache could be switched off. Each scenario ran once to warm up, then 5 times; every cell is the median. Displayed = the built-in Risk preset (41 columns plus `position_id`); All = the whole catalog (202).

| Scenario | Req | Rows × cols | Wire KB | Decoded KB | Encoding | TTFB ms | Download ms | Parse ms | Render ms | Total ms | Server db / ser / total ms | Cache |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| P1 · row JSON · 200 rows · displayed 41 cols · cache off | 1 | 200 × 42 | 37.1 | 166.5 | br | 45.4 | 0.5 | 0.2 | 10.8 | 46.0 | 37.6 / 1.6 / 40.5 | BYPASS |
| P1 · row JSON · 200 rows · all 202 cols · cache off | 1 | 200 × 202 | 225.2 | 891.4 | br | 106.0 | 2.2 | 2.5 | 10.1 | 108.9 | 87.3 / 8.9 / 98.8 | BYPASS |
| P1 · columnar JSON · 200 rows · displayed 41 cols · cache off | 1 | 200 × 42 | 23.2 | 58.0 | br | 30.3 | 0.3 | 0.3 | 8.2 | 30.8 | 27.6 / 0.6 / 28.5 | BYPASS |
| P1 · columnar JSON · 200 rows · all 202 cols · cache off | 1 | 200 × 202 | 118.3 | 303.8 | br | 130.0 | 1.2 | 1.4 | 6.5 | 131.7 | 111.3 / 3.8 / 121.3 | BYPASS |
| P1 · MessagePack · 200 rows · displayed 41 cols · cache off | 1 | 200 × 42 | 34.3 | 64.5 | br | 57.8 | 0.5 | 0.6 | 6.1 | 58.6 | 47.8 / 1.5 / 49.8 | BYPASS |
| P1 · MessagePack · 200 rows · all 202 cols · cache off | 1 | 200 × 202 | 149.5 | 331.3 | br | 85.6 | 2.0 | 1.3 | 5.8 | 88.0 | 78.8 / 1.5 / 81.1 | BYPASS |
| P1 · columnar JSON · 100 rows · displayed 41 cols · cache off | 1 | 100 × 42 | 13.3 | 29.7 | br | 44.6 | 0.3 | 0.2 | 5.7 | 45.2 | 39.2 / 0.3 / 39.8 | BYPASS |
| P1 · columnar JSON · 500 rows · displayed 41 cols · cache off | 1 | 500 × 42 | 55.6 | 142.7 | br | 41.9 | 0.5 | 0.8 | 5.7 | 42.8 | 35.6 / 1.3 / 37.2 | BYPASS |
| P1 · columnar JSON · 200 rows · displayed 41 cols · cache off · gzip | 1 | 200 × 42 | 34.0 | 58.0 | gzip | 46.1 | 0.3 | 0.4 | 5.9 | 46.6 | 41.1 / 0.4 / 41.9 | BYPASS |
| P1 · columnar JSON · 200 rows · displayed 41 cols · cache off · no compression | 1 | 200 × 42 | 58.0 | 58.0 | none | 52.2 | 0.2 | 0.3 | 14.6 | 52.8 | 48.7 / 0.7 / 49.8 | BYPASS |
| P1 · columnar JSON · 200 rows · displayed 41 cols · cache on | 1 | 200 × 42 | 23.2 | 58.0 | br | 1.9 | 0.3 | 0.3 | 13.8 | 2.5 | 0.0 / 0.0 / 0.1 | HIT |
| P3 · 5 per-source calls · cache off | 5 | n/a | 6.2 | 13.0 | br | 210.0 | n/a | 0.1 | n/a | 341.6 | 1351.4 / 0.5 / 1352.4 | 5 BYPASS |
| P3 · naive 20 calls · cache off | 20 | n/a | 8.5 | 13.7 | br | 43.7 | n/a | 0.1 | n/a | 331.4 | 1349.0 / 0.2 / 1350.8 | 20 BYPASS |
| P3 · 5 per-source calls · cache on | 5 | n/a | 6.2 | 13.0 | br | 4.4 | n/a | 0.1 | n/a | 5.8 | 0.0 / 0.0 / 0.3 | 5 HIT |
| P3 · naive 20 calls · cache on | 20 | n/a | 8.5 | 13.7 | br | 3.9 | n/a | 0.1 | n/a | 14.7 | 0.0 / 0.0 / 0.1 | 20 HIT |

For P3, TTFB is the first response's first byte and Total is the last response's end, both from the run's start. Bytes, parse and server times are summed over the calls (the server's db time is per call, and each call reads its grids in parallel, so the sum exceeds the wall time).

**MessagePack on the wire, before and after the MIME-type fix** (same blocks; before = no `Content-Encoding`, so the wire size is the body size):

| Block | Before (raw) KB | After (br) KB | Columnar JSON (br) KB |
|---|---:|---:|---:|
| Risk, 200 × 42 | 64.5 | **34.3** | 23.2 |
| All, 200 × 202 | 331.3 | **149.5** | 118.3 |

**What the numbers say:**

- **They confirm ADR-0007 through the running API.** Columnar JSON is the smallest on the wire: about 37% under row JSON for Risk (23.2 against 37.1 KB) and 47% under it for All (118.3 against 225.2). Compressed MessagePack is still larger than columnar JSON. Row JSON decodes to 2.9× the bytes (891 against 304 KB for All), which is the memory the browser holds per block.
- **Brotli matters most.** The Risk block is 58.0 KB raw, 34.0 KB with gzip and 23.2 KB with br at `Fastest`. Uncompressed, it would be just under the 60 KB budget (README §10).
- **The server cache is the largest lever.** The same Risk block is 30.8 ms as a MISS and 2.5 ms as a HIT. For P3 it is 342 ms against 6 ms.
- **Naive P3 wins on first byte and loses on everything else.** Its first small grid arrives at 44 ms against 210 ms, but the last arrives at about the same time (331 against 342 ms). It sends 4× the requests and 37% more bytes (8.5 against 6.2 KB of headers and envelopes), and takes 4× the rate-limit tokens. The 5 per-source calls stay (ADR-0012).
- **Decode and render are small next to the network and the database.** Decode is at most 2.5 ms (row JSON, All). Render is 6–15 ms and noisy, because it is bounded by the next frame.
- **Run-to-run spread is wide on a laptop**, as the Lab's own last-20 table shows: one naive P3 run took 626 ms. Medians of 5 are what this ADR quotes. The Lab is a demo and a first look, and k6 (`perf/positions.js`) stays the budget gate.

**Bundle** (`npm run build`, Brotli q1 as the API streams it, #252):

| | main | this PR |
|---|---:|---:|
| Initial (main + styles) | 116.7 KB | 116.7 KB (raw +0.4 KB: the route's lazy import) |
| Lab chunk (lazy) | n/a (placeholder) | 8.1 KB |
| `@msgpack/msgpack` chunk (lazy, first MessagePack run only) | n/a | 4.7 KB |

**How to reproduce** (a local stack only, never Render, Neon or production; the accounts are throwaway ones created with Desk.UserAdmin against the local database):

```
docker compose -f docker-compose.yml -f e2e/docker-compose.e2e.yml up -d --build --wait
dotnet ef database update --project src/Desk.Data --startup-project src/Desk.Data
dotnet run -c Release --project src/Desk.Seeder -- --force --scale 1.0 --cap-mb 1024
dotnet run --project src/Desk.UserAdmin -- add --email <local admin> --role admin --expires <date>
cd e2e && PERF=1 BASE_URL=http://localhost:8080 DESK_EMAIL=<local admin> DESK_PASSWORD=… npx playwright test perf-lab
# writes e2e/results/perf-lab.md (the medians above) and perf-lab-last20.md (the Lab's own "copy as markdown")
```

The script is `e2e/tests/perf-lab.spec.ts`. It drives the Lab page, so the numbers are the ones a person sees there.

## Decision

- The Lab measures the real endpoints. Its switches are request headers: cache off for admins only, forced encoding only with the dev knobs on. Row JSON is served on request, as the Lab's baseline only.
- MessagePack and row JSON are compressed like JSON (`ResponseCompressionOptions.MimeTypes`), which fixes MessagePack going out raw.
- The client reads bytes and network phases from Resource Timing, and decode and render from performance marks. It decodes MessagePack with `@msgpack/msgpack` 3.1.3, loaded lazily.

## Consequences

- Each number on the page traces back to one request and one timing entry, and the markdown copy goes straight into a PR or an ADR.
- An admin can make the database do real work on demand. That is bounded by the same rate limits and permits as every other read, and audited as `BYPASS`.
- `X-Cache` can now say `BYPASS` as well as HIT and MISS. The status bar treats any value other than those two as unknown.
- Render time is frame-bound and noisy. Comparisons between formats should use bytes and decode, not render.
- Revisit if the grid moves to a binary transport (ADR-0007), or if a new representation or endpoint should appear in the Lab.
