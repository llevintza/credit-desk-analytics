# ADR-0009: AG Grid Infinite Row Model with displayed-column requests

- **Status:** Accepted
- **Date:** 2026-10-08
- **Phase / PR:** phase-4/shell-and-positions-ui

## Context

The P1 positions grid shows 20,001 rows × up to ~200 columns (README §6). It must:
- paint its first rows in under 1 s warm (README §10);
- scroll anywhere;
- keep a summary row that reflects every filtered row.

AG Grid **Community** only (AGENTS.md): the Server-Side Row Model is Enterprise. The API returns columnar blocks of up to 500 rows with only the requested columns, plus the totals (ADR-0006/0007).

## Options considered

1. **Infinite Row Model.** The grid asks for blocks of 200 rows as the user scrolls (`cacheBlockSize: 200`, `maxBlocksInCache: 50`, `blockLoadDebounceMillis: 100`).
   - Only the **displayed** columns are requested.
   - Changing the displayed *set* (preset, show/hide) purges the cache and re-requests. Moving or resizing doesn't.
   - Sort, filter and quick filter run on the server; totals come with the first block of each view.
2. **Client-side Row Model.** Download every row of the chosen columns, then let AG Grid sort, filter and aggregate in the browser.
3. **Pagination** (client- or server-side pages). Rejected up front: the user story is "like a spreadsheet, scroll anywhere".

## Evaluation

The production build was served by the API, as in production (same origin, Brotli, CSP), against a database seeded at scale 1.0 with seed 42 (20,001 positions). Chromium (Playwright 1.63), Apple M5; 5 warm runs; heap from CDP after a forced GC.

| | Infinite Row Model (chosen) | Client-side, "Risk" (42 cols) | Client-side, "All" (197 cols) |
|---|---:|---:|---:|
| First rows painted, warm (median of 5) | **167 ms** (162–172) | ≥ 768 ms\* | ≥ 1,742 ms\* |
| Requests before first paint | 1 block (+ meta) | 41 | 41 |
| Payload before first paint | 23 KB br (Risk block) | 5.6 MB raw | 28.7 MB raw |
| JS heap for the data | **10.9 MB** whole page after the first view | +23.5 MB | **+158 MB** |
| Totals over the filter | Server, with the first block | Client, every row | Client, every row |
| Sort/filter cost | Server (indexed sorts: 0.2 ms, ADR-0008) | Browser, over 20k rows | Browser, over 20k rows |

\* "Every row downloaded and turned into row objects": the floor before a client-side grid can even start rendering. Grid initialisation over 20k rows comes on top. The Risk figure is with the API's block cache cold; it drops to 142 ms with every block cached.

**Request discipline**, from the e2e assertions:
- Switching preset sends exactly **one** request with the new `columns` and nothing else.
- Rapid typing in the quick filter (300 ms debounce, `distinctUntilChanged`, `switchMap`) completes exactly **one** request; superseded in-flight blocks are aborted.
- Scrolling straight to the last row of 20,001 loads 3 blocks, not 100.

**How to reproduce:**

```
dotnet run -c Release --project src/Desk.Seeder -- --force --scale 1.0 --as-of 2026-10-06
(cd web && npm ci && npm run build)
ASPNETCORE_WEBROOT=$PWD/web/dist/web/browser RATE_LIMIT_PER_USER_PER_MIN=6000 RATE_LIMIT_PER_USER_BURST=1000 \
  ASPNETCORE_URLS=http://localhost:5182 dotnet run -c Release --project src/Desk.Api
cd e2e && npm ci && npx playwright install chromium
PERF=1 BASE_URL=http://localhost:5182 DESK_EMAIL=… DESK_PASSWORD=… npx playwright test perf   # → results/perf.json
```

## Decision

**Infinite Row Model with displayed-column requests.**
- `getRowId` is `position_id`. deal_name, class and cusip are pinned left. The bottom row is pinned and filled from each view's totals.
- Column virtualisation stays on.
- The datasource is `PositionsQuery`:
  - every view is a new value on a `BehaviorSubject`;
  - `switchMap` drops, and so aborts, the previous view's in-flight blocks;
  - blocks of the current view load in parallel (`mergeMap`);
  - a 429 is retried after `Retry-After`.

## Consequences

- First paint stays flat however wide the preset: the "All" preset is a 117 KB first block, not 28.7 MB.
- Memory stays at what's on screen plus the block cache (≤ 50 blocks).
- Every scroll into an unloaded region is a request. The server's per-view caching (ADR-0006) makes revisits cheap, and the per-user rate limit is sized for it. Bursts beyond it are retried after `Retry-After`.
- Grouping or pivoting in the browser isn't possible with this model. That's out of scope for Community (README §16), and the server already returns the totals.
- Sort and filter semantics are the server's. The Community column filters (text, number, date) map one-to-one onto the API's filter model; the set filter is Enterprise and isn't offered.
- Copy (Ctrl+C) copies the focused cell only: range selection is Enterprise (README §9.3).
