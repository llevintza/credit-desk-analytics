# RCA: insights board e2e red on main after the per-user limiter (#357)

As of 2026-10-15. Fix: branch `fix/357-insights-limiter-partition` (Fixes #357; refs #127, #174, #183, #339, #364).

## Summary

- **What broke:** from 2026-10-13 22:10 ET, `main` failed the e2e job on `e2e/tests/insights.spec.ts:22` ("with a 2 s delay injected into one source, the other four render first"). The cause was two PRs that were each green on their own: #174 (per-user limiter, 1 request in flight) and #339 (insights board, 5 parallel source calls). They merged 6 min 40 s apart.
- **Impact:** every PR based on `main` inherited a red e2e job. In production, the insights board would load its five sources one after another behind the slowest one. The P3 progressive-render acceptance (README §6) was not met. There was no data exposure, and the database and free-tier protections stayed intact.
- **Fix:** `GET /api/insights/{source}` gets its own per-user limiter partition: 5 in flight, queue 15, configurable. Every other `/api` route keeps 1 in flight and a queue of 8. Nothing was reverted. `insights.spec.ts` was not changed.

## Timeline (US Eastern)

| When | Event |
|---|---|
| Oct 8, 05:39 | #174 opened (per-user concurrency cap, #127) |
| Oct 9, 17:13 | #339 opened (Phase 6 insights board: 5 per-source endpoints, loaded in parallel, painted as each lands) |
| Oct 13, 22:04 | **#174 merged** (e7eceee). Main CI run 38499091106 is green: nothing on `main` yet makes parallel per-user reads |
| Oct 13, 22:10 | **#339 merged** (09111cf). Its last PR CI ran on 9921ef2, which does not contain e7eceee. Main CI run 38499681101 fails the e2e job; so do 38499818196 (342dce8) and 38500311043 (3bc0f18) |
| Oct 13, 22:22 | #357 opened (P1). First ruling: size the client queue to the cap or batch (#183); time box Oct 14, 10:00 |
| Oct 13, 22:39 | #364 opened: tests that pin insights to the cap of 1; lays out options A, B and C |
| Oct 13, 22:43 | Helms (CTO) rules option B: an insights partition (5 in flight / queue 15) plus an ADR-0005 amendment |
| Oct 15, 08:55 | The time box passed; fallback ask to revert #174 |
| Oct 15, 08:56 | Revert cancelled; option B confirmed with no time box. Standing rule from Leo: nothing is reverted, fix forward only |
| Oct 15 | Fix PR opened from `fix/357-insights-limiter-partition` |

## Root cause

#174 added a per-user concurrency limiter at the front of the global `/api` limiter chain: per-user concurrency (1 in flight, queue 8), then the per-user token bucket (60/min, burst 20), then the shared database limiter (8, queue 32). It applies to every `/api` route by design.

#339's board sends five `GET /api/insights/{source}` calls at once. Its acceptance test holds `surveillance` back 2 s on the server (dev-only `X-Debug-Delay-Ms`, which runs inside the handler and so holds the request's permits). The test then expects the other four to paint while surveillance is still loading.

With one permit per user, the outcome depends on **which call reaches the server first**:

- If surveillance arrives last, the other four are served one after another in about 60 ms and paint first. The test passes even under the cap. That is what happens on a fast local machine, where Playwright's interception of the surveillance call delays its dispatch.
- If surveillance arrives first, it holds the user's only permit for 2 s and the other four wait in the user's queue. They paint just after surveillance, and the test fails. That is what happened in CI, on all 6 attempts across 3 runs (each test plus its retry): `[data-source=surveillance][data-state=loading]` expected 5, received 0.

Reproduced locally with the order forced (other four held 300 ms in the browser so surveillance arrives first; same Release build, database and settings):

| Surveillance first, 3 runs each | `main` 3bc0f18 | Fix |
|---|---|---|
| Other four painted, ms | 2071–2094 (after surveillance) | 362–381 |
| Surveillance painted, ms | 2063–2087 | 2056–2061 |
| Result | 3 of 3 fail | 3 of 3 pass |

## Why it wasn't caught before merge

- **Stale base:** #339's PR CI never ran with #174 in its base. Each PR was green on its own base.
- **Back-to-back merges:** #174 and #339 merged 6 min 40 s apart. Nothing required re-testing #339 on the new `main`: there was no up-to-date-branch requirement and no merge queue.
- **Order-dependent test:** `insights.spec.ts:22` can pass under the cap when the delayed source happens to arrive last. A local run on `main` passes 5 of 5. Only CI's ordering exposed it. The new `insights-arrival-order.spec.ts` removes that luck.
- **The guard still worked:** the P3 e2e caught the regression on the first `main` run after the merge.

## Options considered

| Option | Outcome |
|---|---|
| **B: insights partition (chosen)** | Keeps #127 on every other route; meets README §6 P3 unchanged; bounded and configurable |
| A: keep 1 in flight for insights | Rejected: drops a README acceptance; the board loads one source at a time |
| C: raise the cap in the e2e override only | Rejected: hides production behaviour from the e2e |
| Size the client queue to the cap, or batch (#183) | Rejected: still serial under a cap of 1, so P3 can't be met |
| Revert #174 | Cancelled: drops #127 for every route; standing rule since Oct 15 is fix forward only |

## The fix

- `src/Desk.Api/Limits/RateLimiting.cs`: inside the per-user concurrency limiter, a routed endpoint carrying `InsightsSourceEndpoint` gets a concurrency partition keyed `insights:` plus the user's key (5 in flight, queue 15). Main keys start with `u:` or `ip:`, so the two partitions never collide or share capacity. The chain order is unchanged, so a queued request still holds no database permit (R174-04) and spends one token (R174-01).
- `src/Desk.Api/Insights/InsightsEndpoints.cs`: the `MapGet` carries the marker (matched on endpoint metadata, like exports, never on the path string).
- `src/Desk.Api/Limits/LimitsOptions.cs`: `RATE_LIMIT_PER_USER_INSIGHTS_CONCURRENCY` (5) and `RATE_LIMIT_PER_USER_INSIGHTS_QUEUE` (15); non-positive or garbage values fall back to the defaults. Empty keys are in `.env.example`.
- Bounds: per user, at most 5 of the 8 shared database permits for one board load. The shared limiter and the insights connection cap (ADR-0012) still bound the process.
- Docs: the ADR-0005 amendment (read-only, bounded, per-user; why 5 and 15; measurements), plus README §7.2 and §17.

## Verification

- .NET: `dotnet test -c Release` gave 1291 passed, 0 failed, 0 skipped. New in `LimitsTests`: the partition's cap, queue and 429 with `Retry-After` (2/1, and the 5/15 defaults); cap 1 / queue 8 elsewhere while insights reads are in flight; no shared capacity in either direction; only the routed insights endpoint is marked; the key never collides; and R174-01 and R174-04 for insights. With the partition switched off, the 3 partition integration tests fail.
- Coverage gate vs `origin/main`: changed .NET lines 100% (29/29), branches 100% (2/2); overall .NET 99.8% / 98.9% and web 99.6% / 100%, no drop.
- E2E on a local stack (never Render or Neon): the whole suite 24/24, `insights.spec.ts:22` 3/3, `insights-arrival-order.spec.ts` 3/3 (fails 3/3 on `main`).

## Follow-ups

- Branch protection: require branches to be up to date before merging, or use a merge queue, so a PR's last green run includes `main`'s tip. This is Tech Coordinator's decision and a settings change; agents don't change it.
- When a PR changes a cross-cutting limit (the `/api` limiter chain), re-run the e2e on open PRs that add parallel per-user calls.
- Separate finding, Oct 15: new NuGet advisories against `System.Security.Cryptography.Pkcs`/`Xml` 10.0.12 (NU1902/NU1903, treated as errors) fail every .NET restore, including the Docker image build. This is unrelated to #357; the 10.0.13 Dependabot bumps (#378, #379) appear to address it.
