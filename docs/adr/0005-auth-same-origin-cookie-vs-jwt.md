# ADR-0005: Auth: same-origin cookie session vs JWT

- **Status:** Accepted
- **Date:** 2026-10-08
- **Phase / PR:** phase-2/auth-and-limits

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
- An endpoint filter validates every non-GET/HEAD/OPTIONS call under `/api`. A failure is a 400 titled `Missing or invalid antiforgery token`.
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
- Every `/api` request passes a chained limiter: the caller's token bucket, then one shared concurrency limiter (8, queue 32). Login is included, and new endpoints can't forget to opt in.
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

**Behind Cloudflare and Render's TLS proxy** (corrected in #116)
- A request travels client → Cloudflare edge → Render's balancer (10.0.0.0/8) → app.
- `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (render.yaml) makes the app trust `X-Forwarded-Proto`, so it sees https (HSTS, Secure cookies, antiforgery).
- **Correction:** the original text said this also gave the real client IP. It didn't. Trusting one hop of `X-Forwarded-For` yields the address that connected to Render, which is a **Cloudflare edge** shared by many clients. With the setting off, it yields Render's balancer. Either way, one client could exhaust the 5/min login window for everyone behind that address (R105-M1).
- The client IP now comes from `ClientAddress` (`src/Desk.Api/Limits/ClientAddress.cs`), and the host no longer rewrites the remote address from `X-Forwarded-For`:
  - Headers are believed only if the socket peer is Render's network. Otherwise the peer is the client.
  - The rightmost `X-Forwarded-For` entry is the hop Render saw. If it is in Cloudflare's published ranges, the client is `CF-Connecting-IP`. Cloudflare always writes that header and overwrites any value a client sends.
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
  - Each request line (the first 429 and the fallback warning) has the route template, the rule that chose the key, the peer, the `X-Forwarded-For` line and hop counts, and its shape (each entry as `cf`, `render`, `private`, `public` or `invalid`).
  - No raw client address is logged. A private peer is infrastructure and is logged whole; a public one is cut to its /24 or /48 (/40 for 6to4, which embeds an IPv4 address). `CF-Connecting-IP` and the key are HMAC-SHA256 hashes under a random per-process key, so two clients can be told apart in one process's logs but not reversed.
  - The trust rules above are unchanged. The fix follows from what these lines show in production.

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
- The per-IP login limit and the anonymous token bucket key on the client IP that `ClientAddress` resolves. Off the proxy (`ASPNETCORE_FORWARDEDHEADERS_ENABLED` unset, as in local, compose and CI), that is the socket peer. On Render, it is Cloudflare's view of the client, believed only through the Render → Cloudflare hops.
- Revisit if the API ever serves a second origin or a non-browser client (then bearer tokens for that client), or for SSO (README §16).
