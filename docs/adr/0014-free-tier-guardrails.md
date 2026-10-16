# ADR-0014: Free-tier guardrails: rate limits, response-cache size and cache-first reads, measured on a 512 MiB stack

- **Status:** Proposed
- **Date:** 2026-10-16
- **Phase / PR:** phase-9/hardening-free-tier-guardrails (#67)

## Context

The app runs on one Render free instance (512 MB RAM, sleeps after about 15 minutes idle) in front of a Neon free
project, whose compute suspends after 5 idle minutes and bills the time it is awake (README §7.2, ADR-0002). Three
guardrails keep a public demo inside those tiers:

- **Rate limits** (README §7.2, ADR-0005, #127, #357): per user, a token bucket (60/min, burst 20), one request in
  flight with a queue of 8 (five plus fifteen for insights source reads), and one shared database limiter (8, queue
  32) sized to the Npgsql pool (#211). The numbers were set by reasoning in phase 2, not from use.
- **Response cache** (`PositionsCache`, `POSITIONS_CACHE_MB`, default 64): serialized grid, fund, insights and deal
  responses, kept until the next batch. Its limit had never been checked against the 512 MB instance, and the
  setting accepted any value.
- **Cache-first reads:** "a healthy demo session should hit Neon only on first views" was a goal, not a measurement.

Story #67 asks for the limits tuned from audit data, the cache sized against 512 MB, the cache-first claim measured,
and this record.

## Options considered

1. **Keep the phase-2 numbers** and only document them.
2. **Tune from a measured session** (chosen): drive a real demo session through the SPA, read its demand from the
   browser's call log and from `app.audit`, and change only the numbers the session actually trips; measure the
   instance's memory with the cache at its limit, under load, at several sizes.
3. **Remove the per-user burst and rely on the shared database limiter only.** Rejected: the bucket is what stops one
   client (or one leaked token) from looping on cached endpoints, which cost Render CPU but never reach the database
   limiter's queue.

## Evaluation

**Setup.** Local compose stack only (never Render, Neon or production): project `wrk67`, Postgres 17 with
`pg_stat_statements`, the app image from `deploy/Dockerfile` with `mem_limit: 512m` (the Render free instance),
production limits unless stated, SEED 42, scale 1.0, as-of 2026-10-15 (281 MB database). Apple M5, Docker 29.5.2
(colima, 4 CPUs, 8 GB). A throwaway viewer from `Desk.UserAdmin` on that database.

**The session** (`e2e/tests/demo-session.spec.ts`, opt-in with `DEMO_SESSION=1`): sign in, then Positions (first
block, three scrolls, Surveillance, All, Risk presets), Fund Performance (QTD, 1Y, ITD, YTD), Insights (20 tiles),
Deals (list and three deals), then the same pages again. `THINK_MS=2000` is a reviewer reading each view;
`THINK_MS=0` is someone clicking as fast as the pages paint. `perf/demo-session-report.mjs` reads the browser's call
log (refused and aborted calls included); `perf/audit-limits.sql` reads the same session from `app.audit`.

### Rate limits

Demand per session (admitted plus refused calls; sign-in included in the audit columns):

| Session | Requests | Busiest 60 s | Burst needed at 1 token/s | Main partition in flight | Insights in flight | DB permits in use |
|---|---|---|---|---|---|---|
| Human pace (2 s think), browser log | 49 | 39 | 6 | 2 | 5 | — |
| Human pace, `app.audit` (+ sign-in) | 53 | 43 | 11 | 1 | 5 | 5 |
| Fast (0 s think), 5 unthrottled runs, `app.audit` (+ sign-in) | 42–48 | 42–48 | **38–42** | 1 | 5 | 5 |

With the phase-2 burst of 20, the fast session was refused three times, all on the insights board (three of its five
sources answered 429, so 15 of 20 tiles painted):

```
DEMO_SESSION think=0ms [{"pass":"first","requests":20,"miss":9,"hit":6,"refused":3,...}]
first   GET   429  /api/insights/surveillance
first   GET   429  /api/insights/market
first   GET   429  /api/insights/reference
```

A burst of 40 still refused one call in one of six runs (that run needed 42). With 60, eight fast runs and two
human-paced runs on the final code had **0 refusals**. The sustained rate (60/min) is never the constraint: the
busiest minute of a human session is 39–43 requests.

The concurrency limits hold: the main partition never had more than 2–4 calls waiting (queue 8), the insights board
asks for exactly 5 at once (5 in flight, queue 15), and one user's first views hold at most 5 database permits (8).

### Cache-first reads ("Neon only on first views")

Before this change the second view of every page was served from the cache except one call: the positions page reads
`GET /api/presets/positions` on every view, and that list was never cached.

```
repeat  GET   200 MISS     6029  /api/presets/positions     (before)
```

After caching the list per user (below), the second pass is all cache:

| Session | First pass: requests / DB reads (MISS) / MiB stored | Second pass: requests / DB reads |
|---|---|---|
| Human pace | 27 / 19 / 1.2 | 22 / **0** |
| Fast | 22 / 15 / 0.7 | 16 / **0** |

`FreeTierGuardrailTests.A_demo_session_reads_the_database_only_on_first_views` pins this in CI: every page's
first-view requests, twice, on a host of its own; the second pass must open no database connection.

What still reaches Postgres during a session, from `pg_stat_statements` over the human session (188 statements):
42 data reads, 15 meta reads and 1 preset read (all first views), 56 audit rows (written in batches every 30 s,
`AUDIT_FLUSH_SECONDS`), 14 sign-in and refresh statements (one refresh every 5 minutes, ADR-0024), and 60 pool and
transaction statements around them. So Neon stays awake while someone is using the app (the audit batches), and only
first views add query work.

### Response-cache size against 512 MiB

`perf/cache-memory.mjs` fills the cache with distinct 500-row whole-catalog blocks (about 0.7 MiB each, the largest
response a user can ask for) to 2.5× its limit; `docker stats` samples the container every second. Then k6
`perf/positions.js` runs against the full cache (limits raised for the run only, as in CI).

| `POSITIONS_CACHE_MB` | Idle after start | Cache full | Cache full + k6 | k6 p95 MISS Risk / All / HIT |
|---|---|---|---|---|
| 32 | 25 MiB | 168 MiB | — | — |
| **64** (default) | 25 MiB | 226 MiB | **338 MiB (66%)** | 23.9 / 98.0 / 2.3 ms |
| **96** (new cap) | 25 MiB | 265 MiB | **389 MiB (76%)** | 22.2 / 87.7 / 2.1 ms |
| 128 | 25 MiB | 316 MiB | 422 MiB (82%) | 23.8 / 76.3 / 1.9 ms |
| 192 | 25 MiB | 411 MiB | — (not run: no room) | — |

Re-run on the branch after merging #470 (the All first view's page and totals on two connections): 64 MB full
226 → 220 MiB, with k6 324 MiB (p95 MISS Risk 23.7 / All 113.4 / HIT 6.7 ms); 96 MB full 272 MiB, with k6 382 MiB
(29.6 / 51.5 / 2.8 ms). The two fast sessions and one human-paced session on that build had 0 refusals and 0
second-view reads.

The container needs about 1.5 MiB per MiB of cache (byte arrays on the large-object heap and the GC's working
room) on top of about 120 MiB for the app under load. No run was OOM-killed or restarted. A human session stores
1.2 MiB, so 64 MiB holds the first views of dozens of sessions until the next batch, and evictions only start under
a deliberate fill like the one above.

**How to reproduce** (local stack only; DESK_EMAIL is a throwaway viewer created there; never Render or Neon):

```
DEMO_SESSION=1 THINK_MS=0 BASE_URL=http://localhost:8080 DESK_EMAIL=… DESK_PASSWORD=… npx playwright test demo-session   # in e2e/
node perf/demo-session-report.mjs e2e/test-results/demo-session.json
docker exec -i <postgres container> psql -U desk -d creditdesk -v since="'…'" -v until="'…'" -v rate=1 -f - < perf/audit-limits.sql
BASE_URL=http://localhost:8080 DESK_EMAIL=… DESK_PASSWORD=… node perf/cache-memory.mjs --mb 160   # per-user limits raised for the run
docker stats --no-stream --format '{{.MemUsage}}' <app container>
```

## Decision

- **Per-user burst: 20 → 60** (`RATE_LIMIT_PER_USER_BURST`), one minute of the unchanged sustained rate of 60/min.
  The other limits stay: per-user concurrency 1 + queue 8, insights 5 + queue 15, database 8 + queue 32, login 5/min
  per IP. The measured session never needed more.
- **Response cache: 64 MB default kept; `POSITIONS_CACHE_MB` capped at 96.** A larger value is clamped to 96, like
  `EXPORT_MAX_ROWS` is clamped to its built-in cap: at 128 the full cache under load left under 20% of the instance.
- **Preset lists cached per user** in the same cache, versioned so a read that races a save never keeps the stale
  list, dropped on that user's save or delete, and evicted after 30 idle minutes. Meta and preset reads now record
  HIT/MISS on their audit rows, so the Usage page's hit ratio (#70) counts them.

## Consequences

- A reviewer clicking through the demo quickly no longer sees insights tiles fail with 429. One client can now send
  60 requests at once before the 1/s refill applies: those are bounded by the per-user concurrency (1 in flight) and
  the shared database limiter, and an anonymous caller's bucket (keyed per client address, ADR-0024) gets the same
  burst for `/api/auth/refresh`, whose sign-in side has its own 5/min window.
- Repeat views cost no Neon compute; the database work of a session is its first views, its token refreshes and the
  audit batches.
- A preset saved on one instance is invisible to another instance's cached list for up to 30 idle minutes. The app
  runs as one instance (ADR-0002); scaling out would need the list's invalidation shared (or the cache dropped for
  presets).
- An operator can no longer raise the cache past 96 MB by mistake and get the instance OOM-killed (a cold cache and a
  Neon wake on every restart).
- Revisit when the instance size changes (re-run `perf/cache-memory.mjs` and k6 at the new limit), when a page adds
  requests to its first view (re-run the demo session: the fast tour's burst need must stay under 60), or when
  production audit data shows sessions unlike the local one (`perf/audit-limits.sql` reads any `app.audit`, but run it
  on a local copy, never against Neon).
