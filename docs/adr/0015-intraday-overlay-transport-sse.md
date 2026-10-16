# ADR-0015: Intraday overlay transport: SSE read with fetch, vs polling, vs WebSockets

- **Status:** Proposed
- **Date:** 2026-10-16
- **Phase / PR:** phase-10/intraday-overlay (#71, #72; epic #19)

## Context

P6 (README §6) overlays simulated intraday marks on the P1 grid: a stream of mark changes for a subset of bonds, each carrying `(bond_id, seq, event_ts)`. The client ignores any mark whose `seq` is at or below the one it already applied, so a reconnect or a restart never puts stale data on screen. Changed cells flash, and the totals refresh from the server periodically.

The constraints:

- **Auth (ADR-0024).** The SPA runs on GitHub Pages, cross-origin from the API, and authenticates with a bearer token in `Authorization`. There are no cookies. The browser's `EventSource` can't set request headers, and neither can the browser's `WebSocket`.
- **Free tiers (README §7.2).** Render's free instance sleeps after about 15 minutes without traffic, and Neon suspends its compute when idle. A long-lived connection must not become a keep-awake pinger for a tab nobody is looking at.
- **Rate limiter.** Each user has a token bucket of 60 requests per minute (burst 20) and one request in flight (queue 8). There are 8 shared database permits, sized to the Npgsql pool (#211). A connection open for minutes must not hold the user's one permit (that would stall their grid) or a database permit (8 streams would starve everyone).
- **Entitlements (ADR-0021).** The marked bonds are the ones the caller's portfolios hold, scoped in SQL.
- **Correctness.** `seq` must only grow per bond across reconnects, restarts and instances. Nothing may be shared between instances.

## Options considered

1. **Polling.** `GET /api/marks?after=<seq>` every N seconds. Each poll is an ordinary request: bearer header, rate limiter, audit row.
2. **WebSockets.** One socket per client, a message per mark. The browser `WebSocket` can't send `Authorization`, so it needs a query-string token, a one-time ticket endpoint, or the token smuggled in `Sec-WebSocket-Protocol`. After the upgrade the socket bypasses the per-request pipeline (limiter, audit, `SessionLimits`).
3. **Server-Sent Events.** One `text/event-stream` response per client, an event per mark, `id = seq`.
   - **(a) read with `EventSource`.** Built-in reconnect and `Last-Event-ID`, but no headers. It would need the token in the URL (logged by proxies, kept in history) or a ticket endpoint.
   - **(b) read with `fetch`.** Angular's `HttpClient` with `withFetch()` and `reportProgress` delivers the body as it arrives. The request goes through the same interceptors as every other call: bearer token, refresh on 401, API base URL. Reconnect and resume are ours to write.

## Evaluation

**Transport cost, same marks.** `perf/MarksTransport` serves the same simulated marks (Desk.Data `MarkSimulator`: 100 bonds, 1 s ticks, each bond changing every 3–14 ticks) in three ways from a separate server process. It runs 50 clients per transport for 60 s each, and every request carries a 1,115-character bearer token, the measured size of an Identity access token on the local stack. Bytes are counted at the client socket in both directions, HTTP headers and framing included. Latency is receive time minus the mark's `event_ts`, for marks made after the client connected.

| Criterion | SSE | WebSockets | Polling 1 s | Polling 5 s |
|---|---|---|---|---|
| Wire bytes per client-minute (down + up) | 138.7 KB (137.5 + 1.2) | 104.3 KB (103.0 + 1.3) | 183.4 KB (113.1 + **70.2**) | 102.2 KB (88.1 + 14.1) |
| Requests per client-minute (each spends a limiter token) | 1.0 in this 60 s run; **0.2** with the 5-minute stream lifetime | 1.0 (0.2 at a 5-minute lifetime) | **59.9**, the whole 60/min bucket | 12.0 (20% of it) |
| Mark latency p50 / p95 / max | **2 / 15 / 55 ms** | 1 / 16 / 32 ms | 513 / 944 / 1,068 ms | 2,098 / 4,633 / 5,017 ms |
| Server CPU per client-minute | 15.0 ms | 15.3 ms | 32.3 ms | 6.6 ms |
| Server allocations per client-minute | 1,812 KB | 464 KB | 231 KB | 121 KB |
| Bearer auth from the Pages origin | (b): the normal `Authorization` header | Needs a URL token, a ticket, or the subprotocol hack | Normal header | Normal header |
| Goes through the per-request pipeline (limiter, audit, `SessionLimits`) | Yes, once per stream | Only for the upgrade | Yes, every poll | Yes, every poll |
| Connections held open | 1 per client | 1 per client | none | none |

What the numbers say:

- **Polling at 1 s fails on the rate limit alone.** It spends 59.9 of the user's 60 tokens a minute, so every grid block would get a 429. It also uploads 70 KB a minute of repeated bearer headers.
- **Polling at 5 s fits the budget,** but a mark reaches the screen 2.1 s late at p50 and 4.6 s at p95. That isn't intraday flashing, and it still spends 12 tokens a minute and writes 12 audit rows a minute per user.
- **SSE and WebSockets deliver marks in milliseconds and cost the same CPU.** SSE's text framing (`event:`, `id:`, `data:` lines and chunked encoding) costs about 33% more bytes than WebSocket frames: 139 vs 104 KB a minute. The harness's SSE writer allocates more than its WebSocket writer, but that is the harness's string building. The API writes through ASP.NET Core's `SseFormatter`.
- **The deciding difference is auth and the pipeline.** SSE read with `fetch` is an ordinary authenticated GET. The limiter, entitlements, `SessionLimits` (logout, expiry) and audit all apply to it unchanged. WebSockets need a second way to authenticate and step outside all of that after the upgrade.

**The real endpoint, end to end.** `perf/marks-stream.mjs` reads `/api/marks/stream` on the local compose stack the way the SPA does: `fetch` with the bearer header, 60 s. Then it reconnects 3 s later with `lastEventId`, and calls the overlay totals 20 times on distinct views, each twice.

| Measure | Value |
|---|---|
| Time to `hello` (connect, auth, universe read) | 225.3 ms cold; 82.3 ms on the reconnect (universe cached) |
| Body bytes per minute | 139.0 KB (matches the harness's SSE row) |
| Marks per minute | 1,031, including 100 on connect |
| Live mark latency p50 / p95 / max | −6 / 6 / 28 ms. The API runs in the colima VM, whose clock is a few ms off the host's, hence the negative p50 |
| Marks at or below their bond's last seq | **0** |
| Reconnect with `lastEventId`: marks replayed / stale among them | 93 / **0** (only bonds that changed in the gap) |
| Overlay totals server time, MISS p50 / p95 (whole book, 20,001 positions) | 42.3 / 83.5 ms |
| Overlay totals server time, HIT p50 / p95 | 0.1 / 0.6 ms |

**Free tier, by construction.** These bounds are pinned by tests rather than measured:

- **The server ends every stream** after `MARKS_STREAM_SECONDS`: default and maximum 300 s, the access token's lifetime. `MarksTests` covers it.
- **The client reconnects only while the tab is visible** and its user has pressed a key, clicked or scrolled within the last 5 minutes. Otherwise it pauses until the next input, and a hidden tab closes its stream at once. `marks-overlay.spec.ts` covers this.
- **So an abandoned tab keeps traffic going for at most about 10 minutes** after the last input: 5 minutes idle plus at most one 5-minute stream. Then Render's own idle timer runs. The totals refresh stops with the stream.
- **The stream never touches the database while streaming.** Its one read, the scoped universe, is cached until the next batch, so a live stream doesn't keep Neon awake. Only the totals refresh does, at most once per 15 s window per view, and only while the overlay is live.

**How to reproduce:** Apple M5, 32 GB, macOS 27; the API in colima Docker; data `SEED=42 --scale 1.0`, as-of 2026-10-15.

```
dotnet run -c Release --project perf/MarksTransport -- --clients 50 --seconds 60 --token-bytes 1115

MarksTransport: 50 clients × 60 s per transport, 100 bonds, 1 s ticks, bearer token 1115 chars

| Transport | Bytes/client-min (down + up) | Requests/client-min | Latency p50 / p95 / max (ms) | Marks/client-min | Server CPU ms/client-min | Server alloc KB/client-min |
|---|---|---|---|---|---|---|
| sse | 138.7 KB (137.5 + 1.2) | 1.0 | 2 / 15 / 55 | 1017 | 15.0 | 1812 |
| ws | 104.3 KB (103.0 + 1.3) | 1.0 | 1 / 16 / 32 | 1003 | 15.3 | 464 |
| poll-1s | 183.4 KB (113.1 + 70.2) | 59.9 | 513 / 944 / 1068 | 1006 | 32.3 | 231 |
| poll-5s | 102.2 KB (88.1 + 14.1) | 12.0 | 2098 / 4633 / 5017 | 845 | 6.6 | 121 |
```

```
# local compose stack (docker compose -p <name> … up --build), migrated and seeded --scale 1.0, a throwaway viewer from Desk.UserAdmin
BASE_URL=http://localhost:<port> DESK_EMAIL=… DESK_PASSWORD=… node perf/marks-stream.mjs 60

Mark stream, 60 s, 100 bonds, 1000 ms ticks; access token 1115 chars

| Measure | Value |
|---|---|
| Time to hello (connect, auth, universe) | 225.3 ms |
| Body bytes per minute | 139.0 KB |
| Marks per minute | 1031 (incl. 100 on connect) |
| Live mark latency p50 / p95 / max | -6 / 6 / 28 ms |
| Marks at or below their bond's last seq | 0 |
| Reconnect with lastEventId: time to hello | 82.3 ms |
| Reconnect: marks replayed / stale among them | 93 / 0 |
| Overlay totals server time, MISS p50 / p95 (n=20) | 42.3 / 83.5 ms |
| Overlay totals server time, HIT p50 / p95 (n=20) | 0.1 / 0.6 ms |
```

## Decision

**Server-Sent Events, read with `fetch` through `HttpClient` (option 3b).**

- **`GET /api/marks/stream`** sends `hello`, then one `mark` event per changed bond (`id` = `seq`), then `bye` when its lifetime is up. On connect it sends every bond's current mark newer than `Last-Event-ID`, or the `lastEventId` query parameter the SPA uses, because a cross-origin `Last-Event-ID` header would need a CORS allowance.
- **`seq` is a tick number.** It is the tick at which the bond's mark last changed, from the wall clock and a pure function of `(bond, tick)` (`MarkSimulator`). It only grows, on every instance and across restarts, with no shared state.
- **Limits.** The stream has its own per-user limiter partition: `MARKS_STREAMS_PER_USER`, default 2 (a reload overlaps its old stream), with no queue. In the shared slot it is capped by `MARKS_STREAMS_GLOBAL` (default 16) instead of holding a database permit. Its one start-up read takes a permit from the shared `DatabasePermits` and gives it back before the first event.
- **Scope.** The marked bonds are the caller's entitled portfolios' bonds, every 4th one in the order of its first position, at most `MARKS_MAX_BONDS` (default 100). The grid's default order is position id, so the first screen always shows a few marked rows. They are read once per (as-of, data version, portfolio set) and cached until the batch.
- **`POST /api/marks/totals`** takes the positions query body. It returns the `price` and `spread_bp` |MV|-weighted totals with the marks applied, computed once per `MARKS_TOTALS_SECONDS` (default 15) window per view and cached. The grid asks for them every 30 s while live.
- **The client** (`MarksOverlay`) applies a mark only when its `seq` is above its bond's last one (`MarkBook`). It reconnects from the highest applied `seq` at the stream's lifetime and retries a dropped stream after 5 s or the 429's Retry-After, at most 5 times in a row. It pauses when the tab is hidden or the user has been idle for 5 minutes.

## Consequences

- **Easier.** Auth, refresh, logout, entitlements, rate limits and audit apply to the stream exactly as to every other `/api` call. Nothing needs a second credential path. The pipeline checks the session when the stream opens, so a logout or an expired account ends the overlay within one stream lifetime (at most 5 minutes). That is the same window a copied access token already has (ADR-0024). A stale mark can't reach the screen: the server never replays at or below `lastEventId`, and the client drops anything at or below its bond's last `seq`, both tested.
- **Harder.** We own reconnect and resume, which `EventSource` would have given us; that is `MarksOverlay`, unit-tested. `HttpClient` hands over the body as one growing string. The 5-minute lifetime bounds it: about 140 KB a minute, so about 700 KB at most per stream.
- **Over HTTP/1.1** the stream holds one of the browser's six connections to the API origin. Locally that is 1 of 6; behind an HTTP/2 edge it is one multiplexed stream.
- **The overlay moves quotes, not valuations.** Only `price` and `spread_bp` are marked. Market value, P&L, risk and the weights stay at the overnight batch. Filters and sorts run on the overnight values, like the rest of the grid.
- **SSE costs about a third more bytes than WebSocket frames.** `text/event-stream` isn't in the response-compression MIME list. Compressing it would need a flush per event, so we left it out. Revisit if the overlay ever streams many more bonds.
- **Revisit** if marks become real (README §16: Kafka), or the overlay needs client-to-server messages. Either would change the comparison toward WebSockets with a ticket.
