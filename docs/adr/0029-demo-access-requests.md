# ADR-0029: Demo access: an anonymous request queue, approved by Leo in an admin view

- **Status:** Accepted (Leo, 2026-10-20: D1 decided, then D2–D6 "go with your recommendations" on #534)
- **Date:** 2026-10-20
- **Phase / PR:** epic #534 (E1), story #537 (E1-S0); implemented by #538 (S1) to #542 (S5), #543 (S6) only if the hand-off changes to email

## Context

The site is public and the data isn't (README §7). Today the only ways an account comes to exist are:

- a human running `Desk.UserAdmin` locally against Neon (README §7.1, §13.3 step 8);
- an entry in `DEMO_ACCOUNTS_JSON`, applied on that email's first login.

README §7.1 says "There is no registration endpoint", and the root `AGENTS.md` says real accounts are created by a human. So a visitor who lands on the login page (Render or the GitHub Pages copy, ADR-0024) has no path forward, and every reviewer costs Leo a local CLI run pointed at Neon. Leo decided (D1, 2026-10-20) on a request queue that he approves in an admin view, with no self sign-up. This ADR writes that decision, and the numbers stories #538 to #542 implement, into the rules before any code lands.

Constraints:

- **Free tiers (README §7.2, ADR-0014).** Render's free instance sleeps after about 15 idle minutes; Neon's compute suspends after 5 idle minutes and bills the time it is awake. An anonymous POST wakes both. `/health` must keep never touching the database, and nothing may run SQL at start-up.
- **Storage.** The database must stay under 350 MB (README §5.4); the epic's estimate leaves about 25 MB of slack from the 270 MB local seed.
- **Personal data.** A request carries an email address, so the site holds personal data for the first time from people who aren't invited. Leo is the controller.
- **Auth is settled (ADR-0005, ADR-0024).** Identity bearer tokens, a 5-minute access token, an 8-hour sliding refresh, a 24 h absolute session, lockout after 5 failures for 15 minutes, a 14-character minimum. None of that changes here.
- **Entitlements (ADR-0021).** Every `/api` path is classified scoped or exempt, and adding an exempt path needs an amendment to ADR-0021 (this ADR is that amendment for the paths below).
- **Agents.** Agents never hold a Neon or production connection string and never create real accounts. The rule change must widen who can cause an account to exist (an anonymous visitor can now ask) without letting an agent cause one.

## Options considered

1. **Request queue with admin approval (chosen, D1).** An anonymous form stores a request. Leo approves or rejects it in an admin view. Approval provisions a time-limited `viewer` through a provisioning service shared with `Desk.UserAdmin`: the same `PasswordGenerator`, hash-only storage, never logged. Nobody signs themselves up.
2. **Self sign-up with email verification.** A visitor registers, confirms the address from an emailed link and gets an account at once, with an expiry. Leo isn't in the loop.
3. **Both.** Self sign-up for a short trial (say 2 days), plus the queue for a longer account.

And for the credential hand-off on approval (D2):

- **(i) shown once to the admin (chosen for launch).** The approval response carries the generated password once, with `Cache-Control: no-store`. It is never stored, logged or audited. Leo copies it and shares it out of band, as he does with the CLI's stdout today.
- **(ii) a single-use set-password link by email.** Approval sends a link carrying an Identity password-reset token (single use, 24 h). Needs an email provider (D3) and a new anonymous `POST /api/auth/set-password`.

## Evaluation

There's no code in this PR, so nothing here is a timing measurement. The numbers are the bounds the design sets, worked out from the stated caps and from the `app.access_request` columns in #538.

| Criterion | 1 Queue + approval | 2 Self sign-up + verification | 3 Both |
|---|---|---|---|
| New anonymous endpoints | 1 (`POST /api/access-requests`, plus a static `GET` probe that never touches the DB) | 3 (sign-up, verify, set-password or resend) | 4 |
| Who decides that an account exists | Leo, per request | anyone with an inbox | anyone, for the trial |
| Abuse ceiling | ≤ 200 pending rows; **no account exists without Leo** | bounded only by the account cap; each bot account can read data endpoints at 60 req/min | as 2 |
| PII held at most | ≤ 200 pending + ≤ 25 approved requests (≈ 300 KB worst case, below) | every unverified sign-up until its link expires, plus the active accounts; every address also goes to an email processor | as 2 plus 1 |
| Leo's minutes per request | about 2 with (i) (open the view, approve, paste the password into a message); about 0.5 with (ii); about 0.2 to reject junk | 0 per request; about 60 once (provider, sender domain, SPF/DKIM) | 0 to 2 |
| Neon compute per request | one wake at most (5 minutes of compute if it was asleep) for the insert; approval wakes it once more, inside Leo's own session | one wake for sign-up, one for verification, then **a live session**: a refresh every 5 minutes for up to 24 h plus page reads, unbounded across bot accounts | as 2 |
| Email provider needed | no (with (i)) | yes | yes |

**Storage bound for option 1.** A row holds an email (≤ 254 characters), an optional name and an optional purpose (each ≤ 500 characters), a status, three timestamps, `decided_by` and `account_id`. ASCII worst case: 254 + 500 + 500 bytes of text, plus about 100 bytes of fixed columns and tuple header, plus the partial unique index on `lower(email)` (about 280 bytes a row at the longest email): ≤ 1.7 KB a row. At the cap, 225 rows × 1.7 KB ≈ **0.4 MB**; a typical row (an email of about 30 characters, a short purpose) is under 200 bytes, so ≈ 45 KB. Either way it's noise against the 350 MB budget, and the purge (below) stops it growing.

**Compute bound for option 1.** A wake costs about 5 minutes of Neon compute however many requests land in it. The checks that refuse junk (honeypot, fill time, the per-IP window, `MAINTENANCE_MODE`, the switch being off) all run before any database call, so refused traffic wakes Render but not Neon. Accepted traffic is bounded by the per-IP window (3 an hour), the existing anonymous token bucket and the global database limiter (8 in flight). An attacker spread over many addresses can keep Neon awake, but no more than the anonymous `POST /api/auth/login` already lets them today; the queue adds no new class of exposure. Option 2 adds one: a signed-up bot holds a session that hits the database every 5 minutes on its own.

**Why not 2 or 3.** Self sign-up trades about 2 minutes of Leo's time per request for an abuse surface where the account cap is the only gate, a standing email dependency, and personal data sent to a third party for every visitor. The data is synthetic, so the harm of a bot account is compute, not disclosure, but compute is the scarce resource here. Request volume is expected well under 5 a week, where 2 minutes each is cheap.

**Why (i) for the hand-off.** (i) needs no provider, no DNS records, no new anonymous endpoint and no new secret, and it matches the CLI's "shown once, share it out of band" contract that `UserAdminTests` already pins. Its cost is that a password appears in an HTTP response and on Leo's screen. That response is admin-only, over HTTPS, `no-store`, and the page clears it when the panel closes (#541). (ii) becomes worth it when requests pass about 5 a week (#543).

## Decision

**The queue (option 1), with hand-off (i), the abuse controls of D5 (a), the PII and retention of D6, and the account defaults of D4.** No self sign-up and no third-party challenge widget. The numbers below are the contract #538 to #542 implement; changing one needs an amendment here.

### 1. The flow

1. A visitor opens "Request a demo account" on the login page and submits email (required), name and purpose (optional) (#539).
2. `POST /api/access-requests` stores a `pending` row in `app.access_request` (#538). It **always** answers `202 Accepted` with the same body and headers, whether the request is new, a duplicate of a pending one, or for an email that already has an account, so the endpoint can't be used to enumerate accounts. The 202 path reuses the login 401 response-time floor (#230), so timing doesn't tell the cases apart either.
3. Leo opens the admin **Access requests** page (#541) and approves, rejects or deletes. Approval calls `POST /api/admin/access-requests/{id}/approve` (#540), which provisions the account through the provisioning service that `Desk.UserAdmin add` also uses.
4. The approval response shows the generated password once (`no-store`). Leo shares it out of band. The account behaves like any other: lockout, the 14-character minimum, refresh and the 24 h absolute session (ADR-0024) are unchanged.

Admin endpoints (`admin` role only, OpenAPI metadata, read live and never cached):

| Endpoint | Does |
|---|---|
| `GET /api/admin/access-requests?status=` | lists requests by status |
| `POST /api/admin/access-requests/{id}/approve` | creates the account, links `account_id`, sets `approved`; idempotent per request |
| `POST /api/admin/access-requests/{id}/reject` | sets `rejected` |
| `DELETE /api/admin/access-requests/{id}` | erases the request (deletion on request, §4) |

Approval is refused with 409 and no side effects when the active demo accounts are at `DEMO_ACCOUNTS_MAX`, or when the email already has an account (which is left unchanged).

### 2. Off switch and the form's probe

- `ACCESS_REQUESTS_ENABLED` (default `false`; only exactly `true` turns it on). Off, `POST /api/access-requests` returns 404 and is left out of the OpenAPI document. It stays off in production until #542 (the purge) has merged: stored emails need an enforced retention first.
- The form learns whether the feature is on from `GET /api/access-requests`: 204 when on, 404 when off. It's answered from configuration, never touches the database, writes no audit row, and is left out of the OpenAPI document when off. The login page hides the link on a 404 or a network error (#539).

### 3. Abuse controls (D5 (a))

| Control | Value | Where it runs |
|---|---|---|
| Per-IP fixed window, keyed through `ClientAddress` (IPv6 by /64, README §13.2) | **3 requests per hour**; the 4th gets 429 with `Retry-After` | before any DB call |
| Honeypot field | a hidden `website` input; non-empty → the same 202, nothing stored | before any DB call |
| Minimum fill time | **3 s** from form render (a `startedAt` sent with the request); faster → the same 202, nothing stored | before any DB call |
| Global pending cap | `ACCESS_REQUESTS_MAX_PENDING`, default **200** (whole number, 1–1,000); at or over it → 503 with `Retry-After: 3600` | one count in the insert's transaction |
| Database permit | the request takes a global DB permit like any DB-bound endpoint (README §7.2) | existing limiter |
| Maintenance mode | 503 without touching the database | existing middleware |

The honeypot and fill-time refusals answer 202 on purpose: a bot learns nothing. The per-IP window answers 429 because a human who submits four times in an hour should see why. A third-party challenge widget (D5 (b)) is **not** used. Adding one later needs a CSP change on the API and on the Pages meta CSP (README §7.3), a bundle-budget measurement (README §10) and an amendment here; the trigger is abuse that gets past these controls.

### 4. Personal data and retention (D6)

- **Fields:** email (required, ≤ 254 characters, normalized to lower case), name (optional, ≤ 500), purpose (optional, ≤ 500). Nothing else: no IP address, no user agent.
- **Privacy notice** shown with the form (#539 renders exactly this text):

  > We store the email address, and the name and purpose if you give them, only to decide on your request and to run a demo account if we approve it. Pending and rejected requests are deleted after 30 days. If we approve, we keep the request until 30 days after your account expires, then delete it with the account. We don't share it with anyone or use it for anything else. To have it deleted sooner, contact the site owner through the repository page.

- **Retention**, `ACCESS_REQUEST_RETENTION_DAYS`, default **30** (whole days, 1–365; any other value keeps 30 and logs a warning, the `AUDIT_RETENTION_DAYS` shape):
  - pending or rejected requests: deleted 30 days after they were created or decided;
  - approved requests: deleted 30 days after their account expires;
  - demo accounts: deleted 30 days after they expire, with their sessions, grants and presets.
  - Their `app.audit` rows follow the existing 90-day audit retention (ADR-0022).
- **Mechanism (#542):** the ADR-0022 pattern. The purge runs after an access-request insert or an admin action, at most once per 24 h per process, in batches that each commit, plus the idle timer that touches the database only when a purge is due. Never at start-up.
- **Deletion on request:** Leo deletes the request with `DELETE /api/admin/access-requests/{id}` and, for an approved one, the account with `Desk.UserAdmin`.
- **No emails or names in logs, `app.audit` or OpenAPI examples.** The anonymous request's audit row carries the endpoint, status and ms only (no `UserName`); structured logs carry the request id. An approved account's own requests are audited under its user name, like any account's. Approval audits the action, the request id and the admin, never the password.

### 5. Account lifecycle (D4)

- `DEMO_ACCOUNT_DAYS`, default **14** (whole days, 1–90): the new account's `ExpiresAt` is approval time plus this.
- `DEMO_ACCOUNTS_MAX`, default **25** (whole number, 1–100): active (unexpired, not disabled) demo accounts; approval past it gets 409.
- Role: always `viewer`. Approval never creates an `admin`.
- **Demo marker:** the approval path sets a demo flag on the account when it creates it (#540 adds the column, expand-only, default `false`). The purge keys on that flag and never on the role, so a viewer created by `Desk.UserAdmin` or `DEMO_ACCOUNTS_JSON` is never purged, whatever its expiry.
- After approval, the account is managed like any other: `Desk.UserAdmin reset`, `disable`, `grant`/`revoke`.

### 6. Entitlements (ADR-0021 amendment)

- **Grants:** an approved account gets the portfolios in `DEMO_PORTFOLIOS` (comma-separated portfolio ids) as its grants, written with its role.
  - Under `ENTITLEMENTS_MODE=all` (today's default) every account sees every portfolio anyway, and the data is synthetic, so D4's "all portfolios" needs nothing more. Leave `DEMO_PORTFOLIOS` set regardless, so a switch to `grants` doesn't strand the demo accounts.
  - Under `grants`, set it to a 3-portfolio demo set. Approval with an empty `DEMO_PORTFOLIOS` in `grants` mode is refused with 409 rather than creating an account that sees nothing.
- **Exempt paths** (added to `EntitlementClassificationTests.Exempt`):
  - `POST /api/access-requests` and `GET /api/access-requests`: anonymous, return no portfolio-owned data.
  - `/api/admin/access-requests` and its `{id}` routes: admin-only, return request rows and a password, no portfolio-owned data. The handlers don't take `IPortfolioEntitlements` and never read the unscoped portfolio list.

### 7. Free-tier impact

- An accepted anonymous POST wakes Render and, if asleep, Neon (one insert). The caps in §3 bound how often; refused junk never reaches Neon.
- `/health` is untouched and still never touches the database. The `GET` probe doesn't either.
- Storage: ≤ 0.4 MB at the caps (Evaluation).
- No timers beyond the ADR-0022-style purge check, which touches the database only when a purge is due.

### 8. Email (D3): deferred

With hand-off (i) there is no email at launch: no provider, no sender domain, no key. When requests pass about 5 a week, #543 switches to (ii) with an addendum here that names the provider class (a transactional free tier of about 100 messages a day), the daily cap (`EMAIL_DAILY_CAP`, below the provider quota), the sender domain Leo controls with its SPF and DKIM records (he adds them by hand), and `EMAIL_API_KEY` as a Render dashboard `sync: false` key only. Approval must not depend on the send.

### 9. Who may cause an account to exist (the rule change)

Real accounts come only from a human running `Desk.UserAdmin`, from `DEMO_ACCOUNTS_JSON` set by Leo in the Render dashboard, or from Leo's approval of a demo request in the admin view. Agents never create, approve, reject, reset or delete real accounts, demo accounts included, and never call the access-request admin endpoints against Render or Neon. Against a local stack they may (e2e for #541, with a throwaway admin created locally). The root `AGENTS.md` hard rule and `src/Desk.UserAdmin/AGENTS.md` carry this wording.

## Consequences

- **Easier:** a visitor can ask for access without knowing Leo; Leo grants it from a phone in about 2 minutes instead of running the CLI against Neon; demo accounts expire and are purged on their own, and so are the emails.
- **Harder:** the site holds personal data from people it hasn't invited, so retention is a go-live blocker (#542 before `ACCESS_REQUESTS_ENABLED=true` in production) and the privacy notice must match §4. A generated password now crosses an HTTP response, admin-only and `no-store`. There's a new anonymous endpoint to keep bounded, and the shared provisioning code ties the CLI's and the API's create paths together (#540 keeps the CLI's output contract byte-stable).
- **Accepted risk:** an attacker spread over many addresses can fill the pending queue (then 503 for everyone until the purge or Leo clears it) and can wake Render and Neon as often as the login endpoint already allows.
- **Revisit when:**
  - requests pass about 5 a week: switch the hand-off to email (#543);
  - junk fills the pending cap more than once: add the challenge widget (D5 (b)) or a global hourly window on the endpoint;
  - the demo set or the defaults stop fitting: change the env vars, no code;
  - entitlements move to `grants` in production: set `DEMO_PORTFOLIOS` first.
