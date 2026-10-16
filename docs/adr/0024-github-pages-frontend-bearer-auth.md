# ADR-0024: GitHub Pages front end and bearer-token auth

- **Status:** Accepted (2026-10-11, decided by Leo, repo owner, acting for Tech Coordinator and Helms). Supersedes ADR-0005 (the session moves from an `HttpOnly` cookie to bearer tokens readable by page script); partially supersedes ADR-0002 (where the SPA is served).
- **Date:** 2026-10-11
- **Phase / PR:** cross-cutting, #349 (API and web). The Pages deploy workflow is its own `[workflows]` PR, #350.

## Context

Leo decided on 2026-10-11 to serve the Angular front end from GitHub Pages (`https://llevintza.github.io/credit-desk-analytics/`), with the ASP.NET Core API staying on Render and Neon. The reasons:

- simpler front-end deploys, with no Docker build and Render deploy for a UI-only change;
- a public portfolio URL that is up even while the API sleeps;
- Render free-tier cold starts (30–60 s, README §13.2) no longer hold back the first paint of the login page.

ADR-0002 rejected Pages because the repository was private (Pages needed a paid plan) and because of cookies. The repository is now public (2026-10-09), so only the cookie problem remains.

`llevintza.github.io` and `credit-desk-analytics.onrender.com` are **different sites**. Both `github.io` and `onrender.com` are on the public suffix list. ADR-0005's `__Host-desk` cookie (`SameSite=Strict`) would be a third-party cookie on every API call from the Pages origin, which Strict never sends. Third-party cookies are blocked by default in Safari and Firefox, and Chrome is restricting them too. The session has to travel some other way.

Constraints carried over unchanged: invite-only accounts with `ExpiresAt`, lockout, the 14-character password policy, the PBKDF2 timing guard and login floor (#118, #230), a disable or password reset ending sessions, the per-user and per-IP rate limits keyed through `ClientAddress` behind Cloudflare (#116), no database read per request (free tier), entitlements through `IPortfolioEntitlements` (ADR-0021).

## Options considered

1. **Custom domain for both, keeping the cookie.** Put the SPA at `desk.<domain>` and the API at `api.<domain>`. These are same-site, so a `SameSite=Lax` (or `Strict`) cookie with `Domain=<domain>` works, plus CORS with credentials. It needs a domain Leo owns and DNS for both GitHub Pages and Render. It also gives up the `__Host-` prefix, because a `Domain` attribute is required.
2. **`SameSite=None; Secure` cookie on the API origin, CORS with credentials.** It keeps `HttpOnly`, but it is exactly the third-party cookie browsers block: Safari (ITP) and Firefox (Total Cookie Protection) partition or drop it, and Chrome's third-party cookie controls can do the same. It needs CSRF protection again, and the antiforgery cookie would be third-party too.
3. **Bearer tokens in the `Authorization` header** (ASP.NET Core Identity's built-in `AddBearerToken` / `IdentityConstants.BearerScheme`, the scheme behind `MapIdentityApi`): login returns an access token and a refresh token, the SPA sends `Authorization: Bearer`, and CORS allows exactly the Pages origin with no credentials.
4. **Proxy the API through the Pages origin.** Not possible: Pages serves static files only.

## Evaluation

| Criterion | 1. Custom domain + cookie | 2. `SameSite=None` cookie | 3. Bearer tokens |
|---|---|---|---|
| Works on `*.github.io` → `*.onrender.com` | Only with an owned domain and DNS | No: blocked or partitioned as third-party (Safari, Firefox; Chrome by setting) | **Yes** |
| Token readable by XSS | No (`HttpOnly`) | No (`HttpOnly`) | **Yes**: access token in memory, refresh token in `sessionStorage` |
| CSRF | Lax/Strict + antiforgery | Needs antiforgery again (and its cookie is third-party too) | **None**: the browser never attaches the header by itself |
| CORS | With credentials | With credentials | Without credentials, one exact origin |
| Revocation (disable, reset) | Stamp check every 5 min | Same | At the next refresh: access tokens last **5 min** |
| Logout | Server-side sign-out | Same | This sign-in revoked in memory; see Consequences |
| DB reads per authenticated request | 0 | 0 | **0**; one stamp and account read per refresh (every 5 min per active user) |
| Extra cost or setup | Domain, DNS, certificates | None, but it doesn't work | None |
| Framework code | Cookie handler | Cookie handler | Identity bearer handler + one refresh endpoint |

**Measured: bearer path, same machine and method as ADR-0005's cookie numbers.**

- Machine: Apple M5, Node 24, .NET 10 Release, Production mode.
- Database: Postgres 17 in Docker, migrated, seeded at `--scale 0.2`, one viewer from `Desk.UserAdmin`.
- Limits: the per-user limit was raised for this local process, and `CORS_ALLOWED_ORIGINS=https://pages.example.test` set so the preflight is a real allowed one.
- Script: `perf/auth-overhead.mjs` (now the bearer version), 500 sequential requests per series.

```
BASE_URL=http://localhost:55480 DESK_EMAIL=… DESK_PASSWORD=… node perf/auth-overhead.mjs 500

requests per series: 500
login ms (3 runs): 52.9, 35.8, 33.1
refresh ms (20 runs): p50 3.6, p95 4.2
Authorization header: 1052 bytes; refresh token: 1030 bytes

| Request                            |  p50 ms |  p95 ms |
|------------------------------------|--------:|--------:|
| GET /health (no auth)              |    1.66 |    2.18 |
| GET /api/me anonymous (401)        |    1.48 |    1.95 |
| GET /api/me with bearer token      |    1.68 |    2.28 |
| OPTIONS preflight (CORS)           |    1.50 |    1.77 |
```

| Per request | Cookie (ADR-0005) | Bearer (this ADR) |
|---|---|---|
| Credential bytes on the wire | 1,020 B cookie | 1,052 B header |
| Authenticated `GET /api/me` p50 / p95 | 1.51 / 2.01 ms | 1.68 / 2.28 ms |
| Anonymous 401 p50 / p95 (same run) | 1.45 / 2.11 ms | 1.48 / 1.95 ms |
| Session renewal | stamp check every 5 min, inside a request | `POST /api/auth/refresh` every 5 min: p50 3.6 ms |
| Cross-origin extra | none | one preflight per URL and method, cached 10 min (`Access-Control-Max-Age: 600`): p50 1.5 ms locally |

Both are within noise of an unauthenticated request. The bearer token is decrypted in memory with the same data-protection keys, so it costs about the same as the cookie. The new costs are the refresh call (one per 5 minutes of activity) and, cross-origin only, the preflights.

**First paint: static shell from Pages vs Render (measured locally, argued for production).** Script: `perf/shell-ttfb.mjs` (200 samples, each on a new connection, after 5 warm-ups). It compares the same build served by the API process with the same build served by a gzip static server shaped like Pages (deep links get `index.html`, as `404.html` will).

```
node perf/shell-ttfb.mjs --cold http://localhost:55480        # started together with the API process
API process: first 200 from /health after 260 ms; first index.html after that: 2.3 ms

node perf/shell-ttfb.mjs http://localhost:55480 200            # the API, warm
| index.html first byte | 0.29 | 0.40 |
| index.html + initial JS/CSS | 2.29 | 2.52 |

node perf/shell-ttfb.mjs --static web/dist/web/browser 200     # static host, warm
| index.html first byte | 0.27 | 0.35 |
| index.html + initial JS/CSS | 4.20 | 4.72 |
```

Warm, both hosts serve the shell in a few milliseconds locally. The static server gzips on every request, while the API serves Brotli, so the static total is slower here; on Pages the CDN caches compressed files. What can't be measured locally is the case that matters:

- **Render free, after about 15 min idle:** the first request waits for the instance to start. README §13.2 records about **30–60 s**. That is against 260 ms for the process alone on this machine; the rest is Render's container scheduling and image pull. Until then the browser shows nothing, because the shell itself comes from the sleeping service.
- **Pages:** the shell comes from GitHub's CDN whether the API is awake or not. The login page paints at once and shows its "Waking the server…" progress (it polls `/health` on the API origin) for those 30–60 s.

The API's cold start itself is unchanged: the first API call still waits for Render. What changes is that the page is up and says so.

## Decision

**Option 3: bearer tokens from ASP.NET Core Identity's built-in bearer scheme, with the SPA on GitHub Pages and the API on Render.** The API keeps serving the same SPA from `wwwroot`, so the Render URL, local compose and the e2e stack stay same-origin.

**Tokens**
- `POST /api/auth/login` returns Identity's `AccessTokenResponse`: `tokenType` `Bearer`, `accessToken`, `expiresIn` 300, `refreshToken`. No cookie is set. Lockout, `ExpiresAt`, the timing guard and the login floor are unchanged.
- The access token lasts **5 minutes**, the old stamp interval. It is checked without the database. On every authenticated request, `SessionLimits` also drops a token whose account expiry has passed (from the claim), whose sign-in is 24 h old, or whose session was logged out.
- The refresh token lasts **8 hours**, and each refresh issues a new one. That is the old sliding limit.
  - Refresh tokens are **single-use** (#353). Each token pair carries a refresh id (`desk:rid`), and `RefreshRotations` kept, in memory per session, the one id that may still be swapped (since #354 the `auth.sessions` table holds it; see the 2026-10-16 amendment). A refresh that presents a token already swapped is a replay: a stolen copy, or the owner's own after a thief swapped it first. The session is then revoked (as by a logout), so every token of that sign-in stops working for both holders.
  - **Grace window** (amendment below, 2026-10-15): for `AUTH_REFRESH_REUSE_GRACE_SECONDS` (default 30 s, at most 120 s) after a swap, the token swapped last gets a pair for the session's *current* refresh id, and nothing is swapped. Of two concurrent refreshes with one token, exactly one swaps and both get a pair for the same id (tested).
  - The refresh endpoint is anonymous and has no policy of its own: it draws on the global limiter's per-client-IP token bucket (`ClientAddress`, 60/min, burst 20) and the shared concurrency limit, like any anonymous `/api` call (tested).
- `POST /api/auth/refresh` (anonymous, keyed on the client IP by the rate limiter) renews both tokens only if all of these hold:
  - the refresh token unprotects and hasn't expired;
  - the session is under 24 h and not logged out;
  - the security stamp is unchanged, so a disable or reset by `Desk.UserAdmin` ends it;
  - `CanSignInAsync` passes (not disabled, not expired, read from the database);
  - its refresh id is the session's current one. This is checked last, so a dead token (expired, logged out) is just refused and never counts as a replay.
- The new tokens keep the original sign-in time (`desk:auth_time`) and session id (`desk:sid`) claims, so refreshing can't stretch the 24 h absolute limit.
- Roles, account expiry and (once ADR-0021's grants exist) entitlement claims are re-read at every refresh: a change applies within 5 minutes, as with the cookie's stamp refresh.
- Lockout doesn't end a live session. Failed passwords are something anyone can send, so it must not let a stranger sign a user out. That matches the cookie behaviour.
- `POST /api/auth/logout` adds the session id to an in-memory revocation list until that session's 24 h limit. Both the access token and the refresh token stop working at once. (Since #354 the revocation is also stored in `auth.sessions`; see the 2026-10-16 amendment.)

**CSRF**
- Antiforgery is removed: `AntiforgeryFilter`, the XSRF cookie, `GET /api/auth/antiforgery` and the SPA's `xsrfRefreshInterceptor`.
- CSRF rides on credentials the browser attaches by itself (cookies, HTTP auth). Nothing here does: the token only goes out when the SPA's own code puts it in `Authorization`, and no cookie authenticates anything. A cross-site form or `fetch` can't add that header without the token, and with CORS it can't read a response either.

**CORS**
- `CORS_ALLOWED_ORIGINS` lists the allowed origins, comma or space separated (scheme, host and port only). Only `https` origins count outside Development, where `http://localhost` is allowed for the dev server. Anything with a path, query, credentials, another scheme or a wildcard is dropped, never widened. `Vary: Origin` is always sent.
- It is unset by default, which allows no origin. `render.yaml` sets `https://llevintza.github.io`.
- Allowed methods: GET, POST, PUT, DELETE. Allowed request headers: `Authorization`, `Content-Type`, `If-None-Match`. Exposed response headers: `Retry-After`, `X-Cache`, `Server-Timing`, `ETag`.
- No `Access-Control-Allow-Credentials`. Preflights are cached for 10 minutes.
- An allowed origin also gets `Timing-Allow-Origin`, so the status bar can still read transfer sizes.
- The CORS middleware runs before maintenance mode and the rate limiter. A preflight never spends a token or opens a connection, and a 429 or 503 is readable cross-origin, so the SPA can show "Too many attempts" or the maintenance banner.

**SPA**
- **API origin:** `DESK_API_BASE_URL` is set at build time (`ng build --define`). angular.json defines it as `""`, meaning the page's own origin. `apiBaseInterceptor` points `/api/…` and `/health` at it.
- **Token storage:** the access token is kept only in memory (a signal). The refresh token is in `sessionStorage` (`desk.refresh`), so a reload or deep link in the same tab keeps the session and closing the tab ends it.
- **Interceptor behaviour:** `authInterceptor` adds the bearer token. After a reload it refreshes first. On a 401 it refreshes once, shared by concurrent calls, and retries once.
  - A refused refresh clears the tokens, and `sessionInterceptor` signs out.
  - A refresh that fails for another reason (cold start, 429, 503) keeps them.
- **Under the base href:** the router works under `/credit-desk-analytics/` (`ng build --base-href`). Deep links on Pages are served by a `404.html` copy of `index.html`, built by the deploy workflow in the `[workflows]` PR.

**Swagger UI** (admin, off in production unless `SWAGGER_ENABLED`)
- A page navigation can't carry a bearer token. The stock Swagger UI shell (no API data) therefore loads without a session, and `/openapi/v1.json` stays admin-only.
- `/swagger/desk-auth.js` signs the UI's same-origin requests with the refresh token the SPA left in that tab's `sessionStorage`. It replaces the XSRF wrapper, which also avoided `'unsafe-eval'`. Open `/swagger` in the tab you signed in in.

## Consequences

- **The front end deploys on its own**, to a CDN, and stays up while the API sleeps. The login page paints at once on a cold start.
- **XSS exposure (accepted, the main cost):**
  - A script injected into the SPA can read the access token from memory and the refresh token from `sessionStorage`. It can then use them from anywhere until the owner's next refresh ends the session as a replay (#353), or until a disable or reset.
  - With `HttpOnly` cookies, XSS could act only inside the open page.
  - Mitigations: Angular's template escaping; no `innerHTML` with API data; `script-src 'self'` and no inline scripts; access tokens that last 5 minutes; refresh tokens in `sessionStorage`, not `localStorage` (gone when the tab closes, never shared across tabs); and `Desk.UserAdmin disable` / `reset` to end a stolen session at its next refresh.
- **One origin for every Pages site on the account (accepted by Leo, 2026-10-11):**
  - An origin has no path, so every GitHub Pages site under `https://llevintza.github.io` shares it. Today that is this app and `llevintza.github.io/energlens`.
  - Any of those sites passes the API's CORS check. In the same tab it can also read `sessionStorage`, including `desk.refresh`.
  - So a script running on any of them (an XSS hole, or a compromised dependency) could take a signed-in user's refresh token and call the API until the session ends.
  - Leo accepts this because he controls every Pages site on the account.
  - **Mitigations:**
    - The refresh token lasts **8 hours** idle and never past **24 hours** from sign-in. The access token lasts **5 minutes**.
    - Rule: **no Pages site on the `llevintza` account loads untrusted third-party scripts.**
      - Enforced for this app's site since #355 (Helms, 2026-10-16): `.github/scripts/pages-script-check.mjs` fails `pages.yml` before the upload, and CI's web job on every PR, when the site references a script that isn't one of its own files, ships an inline script outside an (empty) allowlist, or a meta CSP whose `script-src` isn't `'self'`. It can't see the account's other Pages repos (e.g. `/energlens`); for those the rule stays a manual check.
  - Revisit with a custom domain (option 1), which gives this app an origin of its own.
- **No HTTP security headers on Pages:**
  - GitHub Pages doesn't let a site set response headers, so the Pages copy gets no CSP, `frame-ancestors`, HSTS preload, `Permissions-Policy` or `X-Frame-Options` from us.
  - The deploy workflow adds a `<meta http-equiv="Content-Security-Policy">` with `script-src 'self'`, `connect-src 'self' <API origin>`, `object-src 'none'` and `base-uri 'self'`.
  - A meta CSP can't set `frame-ancestors`, so the Pages copy can be framed (clickjacking). The risk is low: there are no state-changing one-click actions, and the token never goes into a frame's URL.
    - Since #66 (amendment below) the app refuses to start inside a frame, and a meta tag sets `Referrer-Policy: same-origin`.
  - The API-served copy keeps the full header set (README §7.3).
- **A stolen refresh token:**
  - Each refresh token works once (#353). If the thief refreshes first, the owner's next refresh presents a swapped token, and the whole session ends for both. If the owner refreshes first, the thief's copy is the replay and ends it. Either way a stolen token buys at most the time until the owner's next refresh (5 min of activity), not the rest of the 24 h.
  - What else ends it: a logout from the session it belongs to (on the running instance only; see below), or `Desk.UserAdmin disable` or `reset`, which rotate the security stamp and end every session of that account at its next refresh.
  - Limits of the detection (lifted by the 2026-10-16 amendment, #354): `RefreshRotations` is per process, like the revocation list. After a restart, a redeploy or a free-tier spin-down, a session's first refresh is believed and becomes the current one, so a thief who refreshes first after a restart is caught only when the owner refreshes. A second instance would need the state in the database (see Revisit).
  - The grace window (amendment below) is not detected: a thief who presents the token swapped last within 30 s of the owner's refresh gets a working pair. The replay is still caught at the session's next swap, when one of the two holders presents a swapped token after the window.
  - The cost to a real user: a refresh whose response is lost (a reload aborts it, or the network drops after the server swapped the token) is covered by the grace window if the tab refreshes again within 30 s, which a reload does at once. Later than that, the tab holds a dead token, its next refresh ends the session and the user signs in again. The same happens to a duplicated browser tab, which copies `sessionStorage` and so shares one refresh token with the original, once the two tabs refresh more than 30 s apart. The SPA's own concurrent 401s already share one refresh call.
  - The e2e run restores one saved session in every test, so it writes each renewed refresh token back to `.auth/session.json` (`e2e/tests/session.ts`).
- **Logout limits:**
  - (Lifted by the 2026-10-16 amendment, #354, for refresh tokens.) The revocation list is per process. A restart, a redeploy, or a free-tier spin-down after about 15 min idle forgets it. A logged-out token copied elsewhere then works again until it expires: up to 5 min for an access token, up to 8 h (and never past 24 h from sign-in) for a refresh token.
  - The SPA always forgets the tokens locally, so the browser that signed out is signed out.
  - To end every session of an account for good, `Desk.UserAdmin disable` or `reset` rotates the security stamp.
  - A database-backed revocation table would close the gap. It is left out because logout already ends the browser's session, and the extra table and write aren't worth it for an invite-only demo.
- **Disable or reset takes effect at the next refresh:** within the 5-minute access-token life (tested), the same window as the cookie's stamp check. Account expiry is still enforced on every request.
- **Rate limiting is unchanged in shape:**
  - Signed-in calls key on the user. Login keys on the client IP (5/min) through `ClientAddress`, behind Cloudflare.
  - Refresh is anonymous, so it draws on the caller's IP bucket.
  - A token that `SessionLimits` rejects (logged out, past 24 h, account expired) is anonymous, so it can't spend or hide behind a user's bucket (tested).
- **Cold start is unchanged for the API:** the first API call after idle still waits 30–60 s on Render free. Only the shell's paint moved off the critical path.
- **Two copies of the SPA:** the Render-served one (same origin, full headers) and the Pages one (cross-origin). Both come from one source tree and differ only in `--base-href` and `DESK_API_BASE_URL`.
- **Perf and e2e tooling sends bearer tokens:**
  - `payload-size.mjs`, `LastBlockCheck` and the k6 scripts finish well inside the 5-minute access token.
  - The e2e setup saves the tab's `sessionStorage` next to Playwright's storage state, and a fixture restores it in every test.
- **Revisit** if the project gets a custom domain (option 1 then gives back `HttpOnly` with a same-site cookie), if the API ever runs more than one instance (move revocation and the refresh-token rotation state to the database), or if SSO is added (README §16).

## Amendment 2026-10-15: a grace window for the token swapped last (#353, PR #366)

- **Status:** Accepted (2026-10-15, decided by Leo).
- **Context:** with strict single-use tokens the e2e suite failed on a real user path. A page reload aborts an in-flight `POST /api/auth/refresh` after the server has already swapped the token. The reloaded page still has the old token in `sessionStorage` and sends it again. That is a replay, so the session ended (401) and the user landed on `/login`. A lost response on a flaky network, or two tabs of one session refreshing together, end the same way.
- **Options:**
  1. Keep strict reuse detection and make the client survive it (for example, write the token before the request). The client can't: the new token exists only in the response the reload threw away.
  2. Drop reuse detection (back to stateless tokens). That gives up #353.
  3. **Grace window (chosen):** for a short time after a swap, accept the token swapped last and answer with the session's current generation, without a new swap.
- **Decision:**
  - `RefreshRotations` keeps, per session id, the current refresh id, the id swapped for it and when. That's all: ids and times, never token values (and the log line on a replay names only the user id).
  - A refresh that presents the current id swaps it for a new one. The previous id, within `AUTH_REFRESH_REUSE_GRACE_SECONDS` of that swap, gets a new pair carrying the *current* id: nothing is swapped, so the answer is idempotent and two racing refreshes converge on one id. Any older id, or the previous one once the window is over, is a replay and revokes the session, as before.
  - The pair is freshly signed rather than byte-identical to the one the lost response held, because that would mean keeping token values. It is the same generation: it carries the same refresh id, so the two are interchangeable, and only one of them can be swapped.
  - The swap is a compare-and-swap on a `ConcurrentDictionary` entry. Of concurrent refreshes with one token exactly one swaps; the others see the id they hold as the previous one and get the id it swapped to (tested with 4 threads released together, 200 rounds, for a known and for a forgotten session).
  - `AUTH_REFRESH_REUSE_GRACE_SECONDS`: default **30**. A missing, zero, negative or unparseable value falls back to 30, as in `LimitsOptions` (a typo can't turn the window off). Above **120** it is clamped to 120, so a typo can't stretch the undetected window either. Fractions are allowed.
- **Threat model trade-off:**
  - Not detected: a thief holding the token swapped last who refreshes within 30 s of the owner's refresh. Both then hold tokens of the current generation.
  - Why that is acceptable here: the thief has to have the token *and* hit a 30 s window that opens only when the owner refreshes (every 5 minutes of activity), so a stolen token alone still dies at the owner's next refresh. And the race isn't forgiven, only deferred: at the next swap, whichever holder comes second presents a swapped token, almost always outside the window, and the session ends for both. The theft can last one more access-token period (5 minutes) than under strict detection, and never past 24 h from sign-in. A script injected into the page (the main threat in this ADR) reads the current token anyway, so the window adds nothing for it.
  - Against that, strict detection signs real users out on every reload that lands during a refresh: a bug users would hit, for an attack the window delays by minutes.
- **Consequences:** the e2e specs need no waits around reloads. The per-process limits above still apply: the window, like the rotation state, is forgotten on a restart. Tests: the previous token within the window gets the current generation and the session goes on; at the window's end it revokes; a token two swaps old revokes inside the window; concurrent refreshes converge; the setting falls back and clamps.

## Amendment 2026-10-16: logout and the refresh rotation in Postgres (#354)

- **Status:** Proposed (PR for #354; Helms ranked it P2 security on 2026-10-13: every deploy restarts the process and forgot logouts).
- **Context:** the revocation list and `RefreshRotations` were per process. Every Render deploy, restart or free-tier spin-down forgot them: a logged-out refresh token worked again for up to 8 h, and a session's first refresh after a restart was believed, so a thief who refreshed first was caught only at the owner's next refresh. A second instance would have had the same gaps all the time.
- **Options:**
  1. Keep the state in memory and add a guard test for the single-instance assumption. Doesn't fix the restart gap, which happens on every deploy.
  2. Check the database on every authenticated request. Closes every gap, but costs a query per request and wakes Neon for calls that don't otherwise touch it (`/api/me`, cached grid blocks), against README §7.2.
  3. **Store the session state in Postgres, read it only at login, refresh and logout (chosen).** Those three already make database calls. The per-request check stays in memory.
- **Decision:**
  - A new table `auth.sessions` (migration `AuthSessions`; expand-only, so the running version ignores it during a deploy): `sid` (primary key), `rid`, `previous_rid`, `rotated_at`, `expires_at` (the session's 24 h limit, indexed) and `revoked_at`. Ids and times only, never token values. It isn't seeded, so `SeedVersion` is unchanged.
  - `SessionStore` replaces `RefreshRotations` with the same rules (#353 and the grace window above):
    - Login inserts the session's row, and first deletes rows past their 24 h limit, so the table holds at most the last 24 h of sign-ins.
    - Refresh swaps with one conditional `UPDATE … WHERE sid = @sid AND revoked_at IS NULL AND rid = @presented`. Postgres's row lock makes it the compare-and-swap across instances: of concurrent refreshes with one token, on any instance, exactly one swaps and the rest see the id they hold as the previous one (tested: 4 racers over two store instances, 25 rounds, for a known session and for one without a row).
    - If the swap misses, one `SELECT` reads the row. No row (a token from before this change, or a pruned one) is believed once and starts the chain (`INSERT … ON CONFLICT DO NOTHING`). A revoked row is refused without counting as a replay. The previous id within the window gets the current id. Anything else is a replay: the row is revoked.
    - Logout and a replay upsert `revoked_at` (the first time is kept).
  - `SessionRevocations` stays the in-memory per-request check, now a cache. A logout or replay on this instance fills it at once, and so does a refresh that finds its session revoked in the table (another instance's logout, or one from before a restart).
  - The cache-first order at refresh: the in-memory check refuses a session it knows is over without a database read; otherwise the store decides in the table.
- **Measured (local; same script, method and machine class as above):** `perf/auth-overhead.mjs` against a throwaway Postgres 17 in Docker (trust auth, migrated, one viewer from `Desk.UserAdmin`), the API from `dotnet run -c Release --no-launch-profile` in Production, before (`main` at `46f69be`) and after (this branch), one after the other on the same database:

  ```
  BASE_URL=http://localhost:55481 node --env-file=<scratch creds> perf/auth-overhead.mjs 500   # before
  login ms (3 runs): 698.5, 48.1, 44.2
  refresh ms (20 runs): p50 6.6, p95 14.8

  BASE_URL=http://localhost:55482 node --env-file=<scratch creds> perf/auth-overhead.mjs 500   # after
  login ms (3 runs): 671.6, 49.9, 49.0
  refresh ms (20 runs): p50 6.1, p95 11.4
  BASE_URL=http://localhost:55482 node --env-file=<scratch creds> perf/auth-overhead.mjs 200   # after, again
  login ms (3 runs): 71.8, 40.1, 39.3
  refresh ms (20 runs): p50 6.5, p95 8.2
  ```

  The refresh chain takes the swap path (one `UPDATE` on the primary key), and the difference is inside run-to-run noise. Login adds a `DELETE` on the indexed `expires_at` and an `INSERT`, small next to its PBKDF2 hash. The per-request rows were unchanged (authenticated `GET /api/me` p50 1.43 → 1.41 ms), because nothing on that path changed.
- **Consequences:**
  - A restart or a second instance no longer forgets a logout or a swap: a logged-out or replayed refresh token is refused everywhere (tested with two hosts on one database, and with a third built afterwards as the restarted instance).
  - **Remaining limit:** an access token logged out on another instance, or before a restart, still works on an instance that hasn't seen the revocation, until it expires (at most 5 min) or that instance refuses the session's refresh, whichever is first. Closing it would need a database read per request (option 2). The test pins this bound.
  - Database load: one more statement at login and at logout; refresh's statement count is unchanged on the swap path (one `UPDATE` instead of nothing, next to the existing security-stamp read), plus one `SELECT` when the swap misses. Nothing per request, and nothing at start-up.
  - **Revisit** (replaces the second-instance clause above): if access tokens must end everywhere at once, either shorten their life or add a per-request check with a short-lived cache.

## Amendment 2026-10-16: the Pages copy's header gaps, and HSTS for a year (#66)

- **Status:** Proposed. Leo to accept the gaps that remain (listed under Consequences).
- **Context:** the #66 security-header review (README §7.3) found the API-served copy complete except for HSTS, which sent ASP.NET Core's 30-day default. The Pages copy can't send response headers (Consequences above). Its meta CSP covers scripts, connections, objects and the base URI, but it was still framable, and it sent no `Referrer-Policy`.
- **Options for the framing gap:**
  1. Accept it (the position above). This is still the fallback for a browser with script off, which shows nothing anyway.
  2. Put a header-setting proxy (for example a CDN worker) in front of Pages. It would be a new service and a new origin, and the custom-domain revisit (option 1 of this ADR) covers that better.
  3. Serve the SPA from Render again. That undoes this ADR.
  4. **The app refuses to start in a frame (chosen).** `web/src/app/core/frame-guard.ts` compares `window.top` with `window.self` before `bootstrapApplication`. When they differ, or `top` can't be read, it replaces the body with a plain notice and never starts the app, so a framer gets no login form and no grid. A sandboxed frame without `allow-scripts` runs no script, so it shows nothing either. This isn't the classic frame-busting redirect that a `sandbox` attribute can defeat: nothing navigates, and the page simply doesn't render.
- **Referrer-Policy:** `<meta name="referrer" content="same-origin">` in `web/src/index.html` serves both copies. The API copy also sends the same value as a header. On Pages this stops the page URL leaking in the `Referer` of the cross-origin API calls (CORS still sends `Origin`).
- **HSTS:** `AddHsts(MaxAge = 365 days)` (`SecurityHeaders.HstsMaxAge`). There is no `includeSubDomains` and no `preload`, because the API's host is a subdomain of a shared platform domain.
- **Measured (local only: compose project `wrk66`, the API in Production on `:8066`, and a local copy of the Pages build served without app headers on `:4366`; never Render or Pages):**

  ```
  node perf/security-headers.mjs --api http://localhost:8066 --pages http://127.0.0.1:4366/credit-desk-analytics/
  api /health, api /: CSP (default-src, script-src 'self' only, object-src 'none', base-uri, connect-src 'self',
    frame-ancestors 'none', no unsafe-eval, 'unsafe-inline' only in style-src), nosniff, X-Frame-Options DENY,
    Referrer-Policy same-origin, Permissions-Policy, COOP same-origin: ok; HSTS: info (not sent over http)
  pages: meta CSP (connect-src 'self' http://localhost:8066), meta referrer same-origin, frame guard in main bundle: ok
  All checks passed.

  Chromium (Playwright), each copy framed from another origin:
    Pages copy -> "Credit Desk Analytics cannot be shown inside a frame. Open it in its own tab." (no app-root)
    API copy   -> blocked: Framing 'http://localhost:8066/' violates ... "frame-ancestors 'none'"
    Pages copy at top level -> the sign-in page renders, no CSP violation

  Initial bundle (npm run build, br q1, 16 KB writes): 116.7 KB before, 116.8 KB after, of 500 KB (main.js +275 B raw)
  ```

  The HSTS value is pinned by `HardeningTests.Hsts_is_sent_over_https_outside_development` (`max-age=31536000`; main sent `max-age=2592000`).
- **Consequences:**
  - The README §7.3 header set now covers both copies wherever a page can carry it. What Pages still can't have:
    - `Permissions-Policy`, `Cross-Origin-Opener-Policy` and `X-Content-Type-Options` have no meta form. GitHub Pages' own headers apply instead; the live check records them.
    - `frame-ancestors` is still missing as a header. The frame guard covers it while script runs.
    - HSTS on `github.io` is GitHub's (the domain is preloaded).
  - **Live-site check:** `perf/security-headers.mjs --live --api <Render origin> --pages <Pages URL>` is the recorded check. Without `--live` the script refuses any non-local host (README §7.3). Over https it also fails an HSTS shorter than a year. A human runs it against the live site (agents measure only local stacks) and posts the table on #66.
  - A future page that must be framed (none is planned) would need an allowlist in the guard and a matching `frame-ancestors`.

