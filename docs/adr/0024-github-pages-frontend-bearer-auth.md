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
  - Refresh tokens are **not single-use**. They are stateless data-protected tickets, so the one a refresh replaces stays valid until its own 8 h run out. There is no reuse detection.
  - The refresh endpoint is anonymous and has no policy of its own: it draws on the global limiter's per-client-IP token bucket (`ClientAddress`, 60/min, burst 20) and the shared concurrency limit, like any anonymous `/api` call (tested).
- `POST /api/auth/refresh` (anonymous, keyed on the client IP by the rate limiter) renews both tokens only if all of these hold:
  - the refresh token unprotects and hasn't expired;
  - the session is under 24 h and not logged out;
  - the security stamp is unchanged, so a disable or reset by `Desk.UserAdmin` ends it;
  - `CanSignInAsync` passes (not disabled, not expired, read from the database).
- The new tokens keep the original sign-in time (`desk:auth_time`) and session id (`desk:sid`) claims, so refreshing can't stretch the 24 h absolute limit.
- Roles, account expiry and (once ADR-0021's grants exist) entitlement claims are re-read at every refresh: a change applies within 5 minutes, as with the cookie's stamp refresh.
- Lockout doesn't end a live session. Failed passwords are something anyone can send, so it must not let a stranger sign a user out. That matches the cookie behaviour.
- `POST /api/auth/logout` adds the session id to an in-memory revocation list until that session's 24 h limit. Both the access token and the refresh token stop working at once.

**CSRF**
- Antiforgery is removed: `AntiforgeryFilter`, the XSRF cookie, `GET /api/auth/antiforgery` and the SPA's `xsrfRefreshInterceptor`.
- CSRF rides on credentials the browser attaches by itself (cookies, HTTP auth). Nothing here does: the token only goes out when the SPA's own code puts it in `Authorization`, and no cookie authenticates anything. A cross-site form or `fetch` can't add that header without the token, and with CORS it can't read a response either.

**CORS**
- `CORS_ALLOWED_ORIGINS` lists the allowed origins, comma or space separated (scheme, host and port only). Anything with a path, query, credentials, another scheme or a wildcard is dropped, never widened.
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
  - A script injected into the SPA can read the access token from memory and the refresh token from `sessionStorage`. It can then use them from anywhere until the refresh token's 8 h (at most 24 h) runs out, or until a disable or reset.
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
  - Revisit with a custom domain (option 1), which gives this app an origin of its own.
- **No HTTP security headers on Pages:**
  - GitHub Pages doesn't let a site set response headers, so the Pages copy gets no CSP, `frame-ancestors`, HSTS preload, `Permissions-Policy` or `X-Frame-Options` from us.
  - The deploy workflow adds a `<meta http-equiv="Content-Security-Policy">` with `script-src 'self'`, `connect-src 'self' <API origin>`, `object-src 'none'` and `base-uri 'self'`.
  - A meta CSP can't set `frame-ancestors`, so the Pages copy can be framed (clickjacking). The risk is low: there are no state-changing one-click actions, and the token never goes into a frame's URL.
  - The API-served copy keeps the full header set (README §7.3).
- **A stolen refresh token:**
  - Whoever holds it can keep refreshing until the session's 24 h cap from sign-in. Each refresh renews the 8 h idle limit, and rotation doesn't kill the stolen copy.
  - What ends it sooner: a logout from the session it belongs to (on the running instance only; see below), or `Desk.UserAdmin disable` or `reset`, which rotate the security stamp and end every session of that account at its next refresh.
  - Reuse detection (one-time refresh tokens, revoking the whole session when an old one comes back) would need server-side token state. It is left out for the same reason as a revocation table, and should be revisited together with it.
- **Logout limits:**
  - The revocation list is per process. A restart, a redeploy, or a free-tier spin-down after about 15 min idle forgets it. A logged-out token copied elsewhere then works again until it expires: up to 5 min for an access token, up to 8 h (and never past 24 h from sign-in) for a refresh token.
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
- **Revisit** if the project gets a custom domain (option 1 then gives back `HttpOnly` with a same-site cookie), if the API ever runs more than one instance (move revocation to the database), or if SSO is added (README §16).
