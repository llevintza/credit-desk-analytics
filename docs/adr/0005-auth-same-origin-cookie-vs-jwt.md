# ADR-0005: Auth: same-origin cookie session vs JWT

- **Status:** Superseded by [ADR-0024](0024-github-pages-frontend-bearer-auth.md) (2026-10-11, Leo)
- **Date:** 2026-10-08
- **Phase / PR:** phase-2/auth-and-limits

> **Note (2026-10-11, ADR-0024):** the session now travels as an Identity bearer token (5-minute access token, 8 h refresh token, 24 h absolute), because a GitHub Pages front end calls the API from another site, where this cookie would be third-party. The cookie, the antiforgery filter and `GET /api/auth/antiforgery` are gone; CSRF doesn't apply to a header token. Still in force from this ADR: Identity and its `auth` schema, persisted data-protection keys and the removed key-ring preload, lockout and account expiry, one PBKDF2 per failed login, the login floor (#230), the rate-limit and `ClientAddress` rules behind Cloudflare (#116), and the CSP of the API-served app. The measurements below are the cookie's and stay as the baseline ADR-0024 compares against.

## Context

The site is public, but the data is not (README §7). Accounts are invite-only, reviewers get short expiries, and a disabled account must lose access quickly. The SPA and the API are served from **one origin** (ADR-0002). The free tiers matter: Render spins down when idle, and Neon autosuspends. Auth must not add database round trips to every request, and must not wake the database at boot or in maintenance mode.

## Options considered

1. **Same-origin cookie session (ASP.NET Core Identity + cookie authentication):**
   - an encrypted, `HttpOnly`, `Secure`, `SameSite=Strict` cookie;
   - data-protection keys persisted to the database;
   - antiforgery token on state-changing requests.
2. **JWT bearer in JavaScript:**
   - the SPA stores an access token (memory or `localStorage`) and sends `Authorization: Bearer`;
   - plus a refresh-token flow for the 8 h / 24 h session rules.
3. **BFF token:** a cookie to a server-side session store that holds a JWT for downstream APIs. There are no downstream APIs here.

## Evaluation

| Criterion | Cookie session | JWT in JS | BFF |
|---|---|---|---|
| Token reachable by XSS | No (`HttpOnly`) | **Yes** | No |
| CSRF exposure | Yes. Mitigated: `SameSite=Strict` + antiforgery header + JSON-only | No | Yes (same mitigation) |
| Revocation (disable/reset) | Security-stamp check every 5 min | Only at token expiry (or a deny-list lookup per request) | Immediate |
| 8 h sliding / 24 h absolute | Built in (sliding) + `auth_time` in the ticket | Refresh-token rotation, hand-written | Server-side |
| DB reads per authenticated request | **0** (one per 5 min for the stamp) | 0 | 1 (session store) |
| Extra request bytes | 1,020 B cookie (measured) | ~800 B bearer header | ~100 B cookie |
| p50 / p95 `GET /api/me`, authenticated | **1.51 / 2.01 ms** | not built | not built |
| p50 / p95 `GET /api/me`, anonymous (401) | 1.45 / 2.11 ms | — | — |
| p50 / p95 `GET /health` (no auth at all) | 1.56 / 2.04 ms | — | — |
| Login (PBKDF2 verify + sign-in), warm | 51–56 ms (first: 717 ms, cold key ring + EF model) | similar | similar |
| Moving parts | Identity + cookie (framework) | token issuance, refresh, rotation, storage | session store |

The session cookie costs nothing measurable per request. Authenticated, anonymous and unauthenticated-endpoint latencies are within noise of each other. The cookie is decrypted in memory and needs no database read.

**How to reproduce:**
- Machine: Apple M5, Node 24.18, .NET 10 Release.
- Database: Postgres 17 in Docker, migrated, one admin account created with `Desk.UserAdmin`.
- Run with per-user limits raised so 1,500 sequential requests aren't throttled.

```
RATE_LIMIT_PER_USER_PER_MIN=100000 RATE_LIMIT_PER_USER_BURST=100000 ASPNETCORE_ENVIRONMENT=Production \
  ASPNETCORE_URLS=http://localhost:5181 dotnet run -c Release --no-launch-profile --project src/Desk.Api
BASE_URL=http://localhost:5181 DESK_EMAIL=… DESK_PASSWORD=… node perf/auth-overhead.mjs 500

requests per series: 500
login ms (3 runs): 717.3, 51.1, 56.0
session cookie: 1020 bytes ("__Host-desk=<value>")

| Request                            |  p50 ms |  p95 ms |
|------------------------------------|--------:|--------:|
| GET /health (no auth)              |    1.56 |    2.04 |
| GET /api/me anonymous (401)        |    1.45 |    2.11 |
| GET /api/me with session cookie    |    1.51 |    2.01 |
```

## Decision

**Same-origin cookie session** on ASP.NET Core Identity, stored in the `auth` schema of the one `AppDbContext`, so there is one migrations history and one bundle.

**Session**
- The `__Host-desk` cookie is `HttpOnly`, `Secure`, `SameSite=Strict`, with an 8 h sliding expiry.
- The 24 h absolute limit is measured from an `auth_time` value stored in the ticket at login.
- Account expiry is enforced at login (`CanSignInAsync`) and on every request (an expiry claim, refreshed by the 5-minute security-stamp check).

**Antiforgery**
- The SPA reads `XSRF-TOKEN` and echoes it in `X-XSRF-TOKEN` (Angular's defaults).
- An endpoint filter validates every non-GET/HEAD/OPTIONS call under `/api`. A failure is a 400 ProblemDetails with the stable `type` `urn:desk:problem:antiforgery` (#282; clients match this) and the title `Missing or invalid antiforgery token` (the SPA's fallback for one release).
- A stale or missing token (an open session across a deploy that renames the antiforgery cookie, #233) recovers once: on that 400 the SPA's `xsrfRefreshInterceptor` calls `GET /api/auth/antiforgery` and retries the unsafe request once. It never retries twice, and other 400s are not retried.
- Login is exempt for three reasons:
  - it is JSON-only, so a cross-site form can't produce it without CORS, which we never enable;
  - it is limited to 5/min per IP;
  - there is no session to ride.
- The antiforgery cookie depends on where the app runs (#118 N1):
  - **Behind Render's TLS proxy, outside Development** (`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, so production): `__Host-desk-af`, `SecurePolicy=Always`. The `__Host-` prefix means no other subdomain or plain-HTTP response can plant or overwrite it. Every request there is HTTPS: Render redirects plain HTTP, and the app reads `X-Forwarded-Proto`.
  - **Everywhere else** (local compose and the CI e2e and budgets stacks, which run Production over plain `http://localhost:8080`, and the dev proxy): `desk-af`, `SameAsRequest`. The framework refuses to issue an always-Secure antiforgery cookie on a plain-HTTP request; making it `Always` everywhere was a login 500 on compose. A `__Host-` cookie without `Secure` is rejected by the browser, so the prefix goes with `Always`.
  - Consequence: a plain-HTTP request that reaches the app behind the proxy without `X-Forwarded-Proto: https` gets a 500 on login, logout and the antiforgery refresh. Render never forwards one.
  - `AuthTests` proves both: a forwarded-https login gets `__Host-desk-af` (Secure, no Domain) and its token validates; a plain-HTTP login off the proxy gets `desk-af` and its token validates.

**Keys**
- Data-protection keys live in `auth.data_protection_keys`, so sessions survive restarts.
- The framework's start-up key-ring preload is removed, so boot and maintenance mode never open a connection. The ring loads on the first login or session check.

**Database touches (free tier)**
- The cookie is decrypted only on paths that use a session: `/api`, `/swagger` and `/openapi`. `/health` and the SPA never load the key ring or run the security-stamp query, even when the browser sends the cookie, so maintenance mode and platform probes stay at zero connections (asserted).
- Every `/api` request passes a chained limiter: the caller's concurrency (amended in #127, below), the caller's token bucket, then one shared concurrency limiter (8, queue 32). Login is included, and new endpoints can't forget to opt in.
  - Concurrency comes before the token bucket because a spent token is never given back. The middleware tries a synchronous acquire before it queues, so with the bucket first, a request that queues behind its own user would pay two tokens.

**Availability per user and for exports** (amended in #127)
- **Per-user concurrency:** one request in flight per signed-in user (`RATE_LIMIT_PER_USER_CONCURRENCY`, default 1), with a queue of 8 (`RATE_LIMIT_PER_USER_QUEUE`), then 429.
  - Anonymous callers (login, a signed-out `/api/me`) don't pass it. They are already bounded by their token bucket, the per-IP login window and the shared limiter. Keyed per IP, one permit would be shared by every anonymous caller whenever the client address resolves to one key (#165), and about 10 concurrent requests would turn every login into a 429.
  - It sits before the shared limiter. A request waiting for its caller's turn holds no database permit, so one busy user can't fill the shared 8.
  - The queue is 8, not 1 or 2, because the SPA's first paint fans out about 5 requests at once (session, portfolios, presets, first blocks). Those queue behind each other rather than failing.
- **Exports are capped separately**, not by the per-user limiter. Counted there, a running export would block its owner's grid for up to the export deadline.
  - `ExportGate` allows one export per user and `EXPORT_GLOBAL_SLOTS` (default 2) in total, both without waiting. Either limit answers 429 with `Retry-After`.
  - Exports can never take every database permit from interactive reads.
- **Export deadline:** `EXPORT_TIMEOUT_SECONDS` (default 60; a value that isn't positive or is above 3600 falls back to 60) bounds the whole stream, including a slow reader's TCP backpressure. The command timeout doesn't bound reading a streamed result.
  - Before any output, the export answers 503, without the file's `Content-Disposition` and without `Retry-After`: the same export would hit the same deadline, so the problem says to narrow it instead. Mid-stream, it aborts the connection, so a truncated download fails instead of ending like a complete CSV.
  - On deadline or client cancel, the stream's connection is disposed and both export slots are released in `finally`. The deadline is armed before the slots are taken, so nothing can throw between taking them and that `finally`.
- **Cold meta load:** the four reference reads run one after another, so the request that loads the snapshot holds one connection under its one permit. In parallel, they held up to four connections under one permit.

**Insights source reads** (amended in #357)
- **Own per-user partition:** `GET /api/insights/{source}` runs in a second per-user concurrency partition, 5 in flight (`RATE_LIMIT_PER_USER_INSIGHTS_CONCURRENCY`, default 5), queue 15 (`RATE_LIMIT_PER_USER_INSIGHTS_QUEUE`, default 15), then 429 with `Retry-After`. A value that isn't a positive integer falls back to the default, as for every limit. Every other `/api` route stays at 1 in flight, queue 8.
  - Why 5: the P3 board (README §6) asks for its five sources at once and paints each as it lands. The acceptance is that a slow source doesn't hold the other four back (ADR-0012). At 1 in flight, they ran one after another behind the slowest, and the P3 e2e failed on `main` after #174 (#357).
  - Why 15: three boards' worth of calls (a reload or an as-of change while one is loading) wait rather than fail. The client cancels superseded calls, so the queue drains.
  - The partition key is `insights:` plus the caller's key. Main keys start with `u:` or `ip:`, so the two never collide, and they share no permits or queue places: a full board doesn't block the user's grid, and a busy grid doesn't block the board.
- **Bounded and read-only:** the endpoint is a cache-first `GET`. The partition sits where the main one does, before the user's token bucket and the shared database limiter, so a queued insights call holds no database permit and spends one token (both asserted for insights in `LimitsTests`). The worst case per user is 5 database permits of the shared 8, for the length of one board load. The shared limiter (8, queue 32) and the insights connection cap (`INSIGHTS_MAX_CONNECTIONS`, ADR-0012) still bound the process, so users together can't exceed the database budget.
- **Matched on the routed endpoint:** the endpoint carries `InsightsSourceEndpoint` metadata, the same way exports carry `ExportEndpoint`. A path string alone moves nothing, and only the insights `MapGet` carries the marker (asserted).
- **Measured** (local stack: the Release API and SPA on `localhost:8080` with the e2e override's limits and `DEV_FAULT_INJECTION=true`, a trust-auth `postgres:17-alpine` seeded at scale 0.2, throwaway viewers from Desk.UserAdmin; paint ms from the board's `insights:painted:*` marks). Surveillance is held 2 s on the server. At one permit per user the result depends on which call arrives first: when Playwright's interception makes surveillance arrive last, the four paint first even on `main`; in CI it arrived first and all six attempts failed. `e2e/tests/insights-arrival-order.spec.ts` (new, now in the e2e job) makes the order deterministic by holding the other four 300 ms in the browser:

| Surveillance reaches the server first (3 runs each) | `main` 3bc0f18 (one permit per user) | This PR (insights partition) |
|---|---|---|
| core / market / pricing / reference painted, ms | 2071–2094 (after surveillance) | 362–381 (300 ms browser hold + ~65 ms) |
| surveillance painted, ms | 2063–2087 | 2056–2061 |
| Result | 3 of 3 fail | 3 of 3 pass |

  `insights.spec.ts:22` as written, on this PR: 3 of 3 pass (the four at 61–184 ms, surveillance at 2058–2090 ms). The whole e2e suite: 24 of 24 pass.

```
# local stack only; a viewer created locally with Desk.UserAdmin (never Render, Neon or production)
cd e2e && BASE_URL=http://localhost:8080 DESK_EMAIL=… DESK_PASSWORD=… npx playwright test tests/insights-arrival-order.spec.ts --repeat-each=3 --retries=0 --reporter=list,json
main 3bc0f18  {"surveillance":2087,"core":2094,"market":2094,"pricing":2094,"reference":2094}   ✘ ×3
this PR       {"core":364,"market":364,"pricing":370,"reference":370,"surveillance":2061}      ✓ ×3
```

- Options considered: **A** keep 1 in flight and change the P3 acceptance, the e2e and ADR-0012 (drops a README acceptance); **C** raise the cap in the e2e override only (hides production behaviour); reverting #174 (drops the #127 protection for every route). B keeps #127 for every other route and meets P3 as written. It was chosen by the CTO on #357.

| Measurement (local, Release, seed 42 at scale 1.0, 2 rounds) | Before (`main` 7cbacc7) | After |
|---|---|---|
| Cold meta load, `db` p50 / p95 (200 loads per round) | 5.3 / 12.7 ms; 4.8 / 9.5 ms | 8.9 / 16.0 ms; 6.3 / 22.2 ms |
| `GET /api/me` with a session, p50 / p95 (1,000 requests per round) | 1.60 / 3.34 ms; 1.61 / 2.79 ms | 1.53 / 2.01 ms; 1.49 / 2.79 ms |

- The sequential cold load costs about 1.5–3.6 ms at p50. It happens at most once per revalidation (10 min) or after a cache clear, and buys one connection per permit.
- The extra limiter on every request is within noise.

```
# local stack only (two local API processes on one local database; admin account created locally)
BASE_URL=http://localhost:5183 DESK_EMAIL=… DESK_PASSWORD=… node perf/meta-cold-load.mjs 200
cold meta loads: 200  db p50 5.30 ms  p95 12.70 ms  max 68.30 ms     # before, round 1
BASE_URL=http://localhost:5184 DESK_EMAIL=… DESK_PASSWORD=… node perf/meta-cold-load.mjs 200
cold meta loads: 200  db p50 8.90 ms  p95 16.00 ms  max 84.70 ms     # after, round 1
BASE_URL=http://localhost:518x DESK_EMAIL=… DESK_PASSWORD=… node perf/auth-overhead.mjs 1000
```

**P1 first paint with one request in flight per user** (README §10: first rows painted < 1.0 s warm). The SPA's first paint (session, as-of and portfolios, columns and presets, the first blocks) now queues per user instead of running in parallel. Measured with the ADR-0009 Playwright perf spec (`positions:first-rows` mark, 5 warm navigations per round), 3 alternating rounds per image, on the same local database.

| Local stack (Release images, seed 42 at scale 1.0, one throwaway viewer) | Before (`main` 7d1cafc) | After (this PR) |
|---|---|---|
| First rows painted, warm, p50 / p95 / max (15 navigations) | 208 / 308 / 308 ms | 186 / 251 / 251 ms |
| Per-round medians | 202, 216, 208 ms | 220, 186, 176 ms |

- No measurable cost: the difference is within run-to-run noise, and both are far under the 1.0 s budget. Most first-paint calls are cache hits of a few ms, so serialising them adds little. `RATE_LIMIT_PER_USER_CONCURRENCY` stays at 1.
- Per-user rate limits were raised for both runs, as in the e2e override (the spec's client-side comparison fetches every block back to back); the per-user concurrency cap stayed at its default of 1.

```
# local stack only: the app image of each commit on a private docker network with a local, trust-auth Postgres
# (migrated, seeded --scale 1.0); a viewer created locally with Desk.UserAdmin
cd e2e && PERF=1 BASE_URL=http://localhost:58174 DESK_EMAIL=… DESK_PASSWORD=… npx playwright test perf
main  round 1  firstRowsPaintedMs median 202  all 215,188,185,202,247
pr174 round 1  firstRowsPaintedMs median 220  all 210,231,251,220,194
main  round 2  firstRowsPaintedMs median 216  all 238,216,197,194,244
pr174 round 2  firstRowsPaintedMs median 186  all 199,186,187,179,169
main  round 3  firstRowsPaintedMs median 208  all 229,203,208,308,184
pr174 round 3  firstRowsPaintedMs median 176  all 181,172,176,173,176
```

**P2 fund page cold first paint under the cap (#338).** The P1 run above was measured before phase 5. The fund page sends little at first paint: `ScopeService.load()` asks for the as-of date and the portfolios together (2 requests, now queued one behind the other), and then the page sends one `GET /api/funds/{id}/performance`. A new range or fund replaces that request through `switchMap`, so the page never has more than one in flight. Measured with the opt-in Playwright perf test `fund page first paint, cold API caches`. Each navigation first clears the API's caches (`POST /api/admin/cache/clear`), then times navigation start → first fund-grid row. That's 20 cold navigations per round, 3 rounds per setting, interleaved on one app image and one local database.

| Local stack (Release image at e180f03, seed 42 at scale 1.0, as of 2026-10-06, one throwaway admin) | Cap 1, queue 8 (default) | Cap raised to 8 |
|---|---|---|
| Fund first row painted, cold API caches, p50 / p95 / max (60 navigations) | 90 / 238 / 345 ms | 101 / 259 / 635 ms |
| Per-round medians | 158, 69, 91 ms | 94, 96, 137 ms |

- No measurable cost: the two settings are within run-to-run noise, and both are far under the 1.0 s budget. The cold path is three small requests, and two of them (as-of and portfolios) are cache misses of a few ms each. `RATE_LIMIT_PER_USER_CONCURRENCY` stays at 1.

```
# local stack only: docker compose -p wrk338 with the e2e override (per-user rate limits raised), Postgres migrated and
# seeded --scale 1.0 --as-of 2026-10-06, an admin created locally with Desk.UserAdmin; the "cap raised" rounds add
# RATE_LIMIT_PER_USER_CONCURRENCY=8 to the app service and recreate only the app container
cd e2e && PERF=1 BASE_URL=http://localhost:58338 DESK_EMAIL=… DESK_PASSWORD=… npx playwright test perf --grep 'fund page first paint'
cap 1  round 1  fundFirstPaintColdMs p50 158  p95 293  max 345
cap 8  round 1  fundFirstPaintColdMs p50  94  p95 165  max 635
cap 8  round 2  fundFirstPaintColdMs p50  96  p95 288  max 347
cap 1  round 2  fundFirstPaintColdMs p50  69  p95  82  max  88
cap 8  round 3  fundFirstPaintColdMs p50 137  p95 237  max 259
cap 1  round 3  fundFirstPaintColdMs p50  91  p95 158  max 193
```
- Audit rows are written after the rate limiter (a 429 is never written), and coalesced: one insert per `AUDIT_FLUSH_SECONDS` (default 30 s), or sooner at 500 rows.
- Every failed login runs the same PBKDF2 work: exactly one verification (#118, R105-F2). The paths are an unknown email, a wrong, empty or missing password, a locked, disabled or expired account, an account with no stored password, and the attempt that triggers lockout (which used to hash twice). `DeskSignInManager.CheckPasswordSignInAsync` checks the account once, then verifies either the stored hash or `TimingGuard`'s decoy, never both. The decoy is a v3 hash with the configured iteration count, verified through the app's own `IPasswordHasher`, so it costs the same as a real check. `LoginTimingTests` counts the verifications per path.
  - **Correction (#230):** this bullet used to say that response time "doesn't reveal which emails exist or what state an account is in". Equal hashing alone doesn't give that. The DB work still differs: a wrong password on an active account runs `AccessFailedAsync` (about 3 more round trips) where an unknown email or a locked, disabled or expired account runs 1 SELECT, and the switch to fast answers after the 5th failure shows that lockout began.
- **Every login 401 answers after a fixed floor (#230):** `LoginFloor` holds back any 401 from the login endpoint (`POST /api/auth/login`, chosen by a `LoginFloorMetadata` marker on the routed endpoint, so every spelling routing accepts is covered, including a trailing slash in any casing) until 500 ms, plus 0–50 ms of random jitter, have passed since the request reached it. Success is never delayed.
  - **Why 500 ms:** it has to sit above the p99 of the slowest failed path (a wrong password on an active account). Locally that p99 is 74 ms (below). On Render's free tier the CPU is a fraction of that and every query crosses to Neon, so the floor leaves several times that margin. A user who mistypes waits half a second, and an attacker's 5/min per IP is unchanged.
  - **Jitter:** each padded 401 adds 0–50 ms from `RandomNumberGenerator`, so the floor isn't a sharp edge that a slow outlier could stand out against.
  - **Outside the concurrency limiter:** it is a middleware before `UseRateLimiter`, so the wait holds none of the 8 shared `/api` permits. The response is buffered until its status is known (a few hundred bytes).
  - **Clock:** the wait runs on the injected `TimeProvider` (`Task.Delay(…, time, RequestAborted)`), and it ends when the client goes away. The value is the constant `LoginFloorOptions.Default`, with no setting that turns it off. Only tests replace it, through `ConfigureTestServices`: the shared fixture's fake clock never advances by itself, so the fixture sets a zero floor, and `LoginTimingTests` puts the production value back.
  - **Tests:** `LoginTimingTests` proves, on a fake clock that records each wait, that every failed path (all 11 of them) waits the floor, that success doesn't, and that 9 parked failures with the global limiter at 1 permit and 1 queue slot neither queue nor get a 429. `LoginFloorTests` covers the middleware on its own: the exact edge, a 401 already slower than the floor, cancellation, and pass-through for everything else.
  - **Residual risk:** a failed path slower than the floor (a cold start, or the database stalling) isn't padded further, so the floor hides the DB difference only while it stays above the slowest path. A configured demo account's first login runs 2 PBKDF2 (the hash at creation and the verify) plus the inserts. That leaks nothing, since the demo emails are shared and the creation happens once, and a failed first login is padded anyway.
  - **Measured** with `perf/login-floor.mjs` (60 interleaved samples per failed path after 5 warm-up rounds, sequential): Apple M5, .NET 10 Release, Production mode, Postgres 17 in Docker on the same machine, one viewer from `Desk.UserAdmin`, and the login limit raised for that local process only. The script logs in correctly every 4th round, so no wrong-password sample is past lockout.

    ```
    BASE_URL=http://localhost:5230 DESK_EMAIL=… DESK_PASSWORD=… SAMPLES=60 node perf/login-floor.mjs
    ```

    | Path (ms) | Before: median | p90 | p99 | After: median | p90 | p99 |
    |---|---|---|---|---|---|---|
    | Unknown email (401) | 33.7 | 47.2 | 183.8 | 524.0 | 546.4 | 552.4 |
    | Wrong password, active account (401) | 38.9 | 50.2 | 74.3 | 527.2 | 545.3 | 550.9 |
    | Success (200, 15 samples) | 44.1 | 85.2 | 92.5 | 66.4 | 80.2 | 123.4 |
    | **Median gap, wrong − unknown** | **5.3** | | | **3.2** | | |

    Before, the wrong password was slower at the median, p90 and minimum (the unknown-email p99 is a single outlier, the slowest of 60). After, both sit on the floor, and the 3.2 ms gap is about one standard error of the difference between the two medians (the jitter alone has σ ≈ 14 ms), so it is noise. On a local database the DB difference is only about 5 ms. Against Neon each extra round trip costs a network hop, which is why the floor sits far above it.
  - **Measured on the compose stack (#306, #230 AC2).** The numbers above came from `dotnet run`. These come from the image `docker compose` builds (`deploy/Dockerfile`, Production mode) with its own `postgres:17-alpine` service, so every query crosses the compose network. Same machine (Apple M5; Docker 29.5.2 with 4 CPUs and 8 GB), one viewer from `Desk.UserAdmin` in that compose database, and the login and anonymous per-caller limits (`RATE_LIMIT_LOGIN_PER_IP_PER_MIN`, `RATE_LIMIT_PER_USER_PER_MIN`, `RATE_LIMIT_PER_USER_BURST`) raised in a local, uncommitted override of the app service only. Two runs of 100 interleaved samples per failed path after 5 warm-up rounds:

    ```
    BASE_URL=http://localhost:<app port> DESK_EMAIL=… DESK_PASSWORD=… SAMPLES=100 node perf/login-floor.mjs
    ```

    | Path (ms), run 1 / run 2 | p50 | p95 | p99 | min |
    |---|---|---|---|---|
    | Unknown email (401) | 532.4 / 533.4 | 555.2 / 555.7 | 559.9 / 557.6 | 506.2 / 504.6 |
    | Wrong password, active account (401) | 532.1 / 532.4 | 552.1 / 557.0 | 559.8 / 559.0 | 502.0 / 502.7 |
    | Success (200, 25 samples) | 49.1 / 47.9 | 53.4 / 52.1 | 59.9 / 56.6 | 29.1 / 29.9 |
    | **Median gap, wrong − unknown** | **−0.3 / −1.0** | | | |

    Both failed paths sit on the floor (500 ms plus 0–50 ms jitter, so a p50 near 525 ms and a p95 near 550 ms plus the request itself), and the gap changes sign between runs, so it is noise. Success stays under 60 ms at p99 and is never padded.

**Behind Cloudflare and Render's TLS proxy** (corrected in #116)
- A request travels client → Cloudflare edge → Render's balancer (a private address) → app.
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (render.yaml) makes the app trust `X-Forwarded-Proto`, so it sees https (HSTS, Secure cookies, antiforgery).
- **Correction:** the original text said this also gave the real client IP. It didn't. Trusting one hop of `X-Forwarded-For` yields the address that connected to Render, which is a **Cloudflare edge** shared by many clients. With the setting off, it yields Render's balancer. Either way, one client could exhaust the 5/min login window for everyone behind that address (R105-M1).
- The client IP now comes from `ClientAddress` (`src/Desk.Api/Limits/ClientAddress.cs`), and the host no longer rewrites the remote address from `X-Forwarded-For`:
  - Headers are believed only if the socket peer is Render's network: since #165, any private address (RFC 1918, CGNAT, ULA, loopback, link-local), because Render doesn't document the range and a peer outside 10.0.0.0/8 shares one key. No client on the internet connects from a private address. Otherwise the peer is the client.
  - Trailing private entries in `X-Forwarded-For` are Render's internal hops and are skipped (#165). A client can only write to the left of the hop Render appends, so skipping them never reaches a value it chose. Ranges in `CLIENT_ADDRESS_TRUSTED_HOPS` (CIDRs, set in the dashboard) are trusted as a peer and skipped as a hop the same way, so a public proxy hop the diagnostics show can be trusted without a release. Never list a range clients connect from; start-up refuses an entry wider than IPv4 /8 or IPv6 /32, one that isn't a CIDR, or an IPv4-mapped IPv6 range (#418).
  - The first remaining `X-Forwarded-For` entry is the hop Render saw. If it is in Cloudflare's published ranges, the client is `CF-Connecting-IP`. Cloudflare always writes that header and overwrites any value a client sends.
  - **Without `CF-Connecting-IP`, the key is the edge.** `True-Client-IP` and the entries left of the edge in `X-Forwarded-For` are not used: unless the zone enables them, a client can write them and Cloudflare passes them through. Believing them would let a caller rotate its key, or pin a victim's (R160-01). Keying on the edge fails closed: it brings back the shared window, but the caller can't choose its key.
  - If that hop is not Cloudflare, it is the client, and any `CF-*` header or `X-Forwarded-For` prefix it sent is ignored.
- **Rejected: `ForwardedHeadersOptions` with `ForwardLimit = 2` and `KnownNetworks` set to Render plus Cloudflare.**
  - It gives the same answer when Cloudflare appends the client to `X-Forwarded-For`.
  - It can't use `CF-Connecting-IP`, which is Cloudflare's own statement of the client.
  - It hides the socket peer from the rest of the app.
  - The explicit resolver is a pure function, unit-tested chain by chain.
- The Cloudflare ranges are a constant, checked against cloudflare.com/ips on 2026-10-08. If Cloudflare adds a range that isn't in the list, requests through it can't be spoofed, but they fall back to keying on the edge, which is the #116 shared window, until the list is updated. A scheduled check keeps the list current (#161).
- **Diagnostics (#165).** After #160 deployed, two real clients still shared one login window in production, so the resolver now reports what it sees instead of failing silently (`ClientAddressDiagnostics`):
  - At start-up: `BehindProxy`, Render's networks, the Cloudflare range count, and the host's effective `ForwardedHeaders`, `KnownIPNetworks`, `KnownProxies` and `ForwardLimit`.
  - Once per process and endpoint policy (`login`, or `global` for the rest), the first rate-limited request (`EndpointPolicy` is the endpoint's policy, not necessarily the limiter that rejected it). At most every 10 minutes for each kind (a monotonic clock), a request behind the proxy whose key every client shares (`UntrustedPeer`, `NoForwardedFor`, `InternalHop`, `Edge`, and `DirectHop` when `CF-Connecting-IP` is present: a real direct client has none, so the hop is a Cloudflare or Render range missing from the lists, #161), with the count of requests suppressed since the last line.
  - Each request line (the first 429 and the fallback warning) has the route template, the rule that chose the key, the peer, the `X-Forwarded-For` line and hop counts, and its shape (each entry as `cf`, `render` (any private address), `trusted`, `public` or `invalid`). The start-up line also lists `TrustedHops`.
  - No raw client address is logged. A private peer is infrastructure and is logged whole; a public one is cut to its /24 or /48 (/40 for 6to4, which embeds an IPv4 address). `CF-Connecting-IP` and the key are HMAC-SHA256 hashes under a random per-process key, so two clients can be told apart in one process's logs but not reversed.
  - **Fix (#165 step b).** No production log lines were captured, so the fix covers each shared-key cause #165 names that the logs would have shown, without widening what a client can choose: a Render peer outside 10.0.0.0/8 (now any private peer), internal hops after the edge (skipped), and a public hop in between (`CLIENT_ADDRESS_TRUSTED_HOPS`). `CF-Connecting-IP` stays the only source of the client behind Cloudflare, and R160-01 stands. If the production two-client check still fails after deploy, the fallback line's `Source` and `ForwardedForShape` show which case is left.

**Swagger UI and the CSP (#94)**
- `/openapi/v1.json` and `/swagger` are admin-only.
- Swagger UI's `requestInterceptor` option is evaluated with `Function()`, which needs `'unsafe-eval'`. Instead, a same-origin script (`/swagger/desk-xsrf.js`) wraps `fetch` to add the XSRF header.
- `/swagger` gets its own CSP, which adds `data:` images.

**CSP for the app**
- `script-src 'self'` with no inline scripts. The Angular build's critical-CSS inliner (`inlineCritical`) emits an inline `<script>`, so it is turned off.
- `style-src 'unsafe-inline'` stays, because Angular's runtime component styles and AG Grid's positioning `style` attributes need it. Styles can't execute code.

## Consequences

- No token ever reaches JavaScript, and there is no refresh-token code to get wrong.
- The cost is a CSRF defence on every state-changing endpoint. New endpoints get it automatically from the `/api` group filter.
- Disabling an account takes effect within 5 minutes (the stamp interval), not instantly. This is accepted for an invite-only demo.
- The per-IP login limit and the anonymous token bucket key on the client IP that `ClientAddress` resolves. Off the proxy (`ASPNETCORE_FORWARDEDHEADERS_ENABLED` unset, as in local, compose and CI), that is the socket peer. On Render, it is Cloudflare's view of the client, believed only through the Render → Cloudflare hops. An IPv6 client is keyed on its /64, the normal end-user allocation, and an IPv4-mapped IPv6 address on its IPv4 address (#164).
- Revisit if the API ever serves a second origin or a non-browser client (then bearer tokens for that client), or for SSO (README §16).

## Addendum (2026-10-07, #115): the `viewer` and `admin` roles are seeded by a migration

**Ruling (Helms, 2026-10-07, on #105).** README §14.4 says seed data is never written by migrations, with the column catalog as the one exception. The two Identity roles are a second, narrow exception: they're reference data the schema needs in order to work (every account gets one through `auth.user_roles`), so the AuthAndAudit migration creates them and nothing at runtime has to. The exception holds only while the seed stays additive and idempotent. This addendum stays in force under ADR-0024, which changed how the session travels but left the role model alone.

**Conditions, and where main meets them** (all from #105, `5ce7f0e`):

1. **Fixed ids.** `Roles.ViewerId` and `Roles.AdminId` are constant GUIDs (`src/Desk.Data/Auth/Roles.cs:11-14`). The model seeds them through EF `HasData` with fixed `ConcurrencyStamp` values too (`src/Desk.Data/App/AppDbContext.cs:107-113`), so a model diff never sees a changed row.
2. **Insert only if missing.** `HasData` becomes a single `InsertData` into `auth.roles` in the AuthAndAudit migration (`src/Desk.Data/App/Migrations/20261008014846_AuthAndAudit.cs:216-224`). EF's migration history runs it once per database, and a database that already has the migration applied never runs it again.
3. **Never update or delete.** No migration touches the role rows after that: there is no `UpdateData` or `DeleteData` anywhere under `src/Desk.Data/App/Migrations/`. Renaming, re-keying or removing a role needs a new ADR, not an edited `HasData` call.

**Future seeded reference rows.** Any other reference rows a migration seeds must meet the same three conditions (fixed keys, one insert that runs once, never updated or deleted by a later migration) and be recorded in an ADR like this one. Everything else is seed data and belongs to the seeder (README §14.4, `src/Desk.Seeder/AGENTS.md`).

README §14.4 (`README.md:995`) still names only the column catalog; adding the roles there is a governed change and goes in its own `[workflows]` PR.
