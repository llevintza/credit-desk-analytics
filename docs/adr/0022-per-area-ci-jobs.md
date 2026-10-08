# ADR-0022: CI runs only the jobs a change touches (base-sourced per-area classifier)

- **Status:** Proposed
- **Date:** 2026-10-08
- **Phase / PR:** [workflows] follow-up (#169)

## Context

Every PR ran the whole CI: `api` (every .NET suite with Testcontainers, then migrate and seed), `web`, `db-tools`, `coverage`, `compose-smoke`, `budgets` (compose stack seeded at scale 1.0) and `e2e` (compose stack + Playwright). A docs-only PR such as #166 spent about 12.5 runner-minutes and 7 m 40 s of wall time on suites its diff could not affect. Leo (2026-10-08) asked for one rule: **run only what a change touches.** Helms's spec (#169) sets the constraints:

- No workflow-level `paths` / `paths-ignore`: a required check that never reports blocks the PR.
- One flag per area (`api`, `web`, `db`, `app`, `perf`); shared triggers and unknown paths set every flag; when in doubt, run.
- Skipped jobs keep their exact names; `secrets`, `workflows` and `gate-tests` always run.
- Coverage no-drop still holds for whatever ran; a suite that didn't run is skipped, not passed on stale data.
- Push to `main` and `deploy.yml` are unchanged.

## Options considered

1. **Workflow-level `paths` / `paths-ignore`.** Simplest, but a filtered-out workflow posts no check at all, so required checks stay pending and the PR can't merge. Ruled out by the spec.
2. **A paths-filter action (e.g. `dorny/paths-filter`), SHA-pinned.** Glob config in YAML. It adds third-party code to every run, its filter config is read from the PR head (a PR can edit the rules that judge it), and its globs can't be fixture-tested in `gate-tests`.
3. **One workflow per area.** Each workflow's own `paths` filter. Same never-reports problem as option 1, plus duplicated setup and renamed checks.
4. **An in-repo `git diff` classifier (`.github/scripts/ci-changes.mjs`), run from the BASE_SHA checkout.** It's plain Node with no dependencies. A `changes` job writes area flags and per-job `run_*` flags; each heavy job gets `needs: changes` and a job-level `if:`.

## Evaluation

| Criterion | 1 `paths` | 2 paths-filter action | 3 per-area workflows | 4 base-sourced script |
|---|---|---|---|---|
| Required checks always report | no | yes | no | yes |
| Third-party code added | none | one action | none | none |
| A PR can rewrite its own rules | yes (head YAML) | yes (head YAML) | yes | **no**: classifier runs from `_base` |
| Fixture tests in `gate-tests` | no | no | no | **yes**: 47 tests, 98.3% line / 98.5% branch |
| Fails open (runs everything) on error | n/a | config-dependent | n/a | **yes**: a job skips only on an explicit `false` |

**Measured CI cost per job** (GitHub Actions, `ubuntu-latest`, `started_at` → `completed_at`, seconds):

| Job | #166 docs-only (run 37755311150) | #116 api fix (run 37753557965) | Phase 5 (run 37750810916) |
|---|---:|---:|---:|
| api | 185 | 133 | 185 |
| e2e | 193 | 201 | 190 |
| budgets | 152 | 140 | 146 |
| compose-smoke | 73 | 73 | 72 |
| db-tools | 56 | 57 | 52 |
| web | 51 | 58 | 47 |
| workflows | 17 | 10 | 10 |
| coverage | 13 | 12 | 10 |
| gate-tests | 10 | 10 | 9 |
| secrets | 7 | 8 | 11 |
| **Total runner-seconds** | **757** | **702** | **731** |

Projected from #166's per-job numbers (the `changes` job is new and not yet measured; it is one checkout plus `git diff`, estimated at ≤ 15 s):

| PR shape | Jobs that run | Runner-seconds | Saved |
|---|---|---:|---:|
| docs only | secrets, workflows, gate-tests, changes | ≈ 49 | ≈ 708 (94%) |
| perf only (`perf/payload-size.mjs`) | + budgets | ≈ 201 | ≈ 556 (73%) |
| app only (`deploy/start.sh`, `e2e/**`) | + compose-smoke, e2e, budgets | ≈ 467 | ≈ 290 (38%) |
| web only | + web, coverage (web), compose-smoke, e2e, budgets | ≈ 531 | ≈ 226 (30%) |
| api or db, or a shared trigger | everything | ≈ 772 | none (+15 s for `changes`) |

The wall-time critical path for a web-only PR drops from `api → e2e` (≈ 378 s) to `web → e2e` (≈ 244 s).

**Replay of the last 15 merges to `main`.** This shows what the classifier would have run for each one; most still run everything, because they touch a shared trigger:

```
$ node perf/ci-changes-replay.mjs 15
| Commit | Subject | Areas | Heavy jobs run | Why every flag |
|---|---|---|---|---|
| `7cbacc7` | [workflows] agents: harness-neutral AGENTS tree, skills and  | docs only | none |  |
| `f776902` | fix(api): key login and anonymous limits on the client behin | api, web, db, app, perf | api, web, db_tools, compose_smoke, e2e, budgets, coverage | shared trigger: perf/coverage-baseline.json |
| `e4e0080` | [workflows] ci: e2e job runs Playwright on the seeded compos | api, web, db, app, perf | api, web, db_tools, compose_smoke, e2e, budgets, coverage | shared trigger: .github/workflows/ci.yml |
| `5f91d26` | Phase 4: shell and positions UI (Infinite Row Model grid, pr | api, web, db, app, perf | api, web, db_tools, compose_smoke, e2e, budgets, coverage | shared trigger: e2e/package-lock.json |
| `9b83f7e` | [workflows] ci: budgets job (P1 payload budgets on the seede | api, web, db, app, perf | api, web, db_tools, compose_smoke, e2e, budgets, coverage | shared trigger: .github/workflows/ci.yml |
| `0cc350d` | Phase 3: positions API (grid query builder, columnar block + | api, web, db, app, perf | api, web, db_tools, compose_smoke, e2e, budgets, coverage | matches no area: .env.example |
| `c91c4b5` | fix(web): drop the stale 'Scaffold · phase 0' header chip (# | api, web, app | api, web, compose_smoke, e2e, budgets, coverage |  |
| `5ce7f0e` | Phase 2: auth and limits (Identity cookie session, UserAdmin | api, web, db, app, perf | api, web, db_tools, compose_smoke, e2e, budgets, coverage | matches no area: .env.example |
| `79ceff6` | [workflows] AGENTS.md: raise-only coverage bump in feature P | docs only | none |  |
| `4baeeb9` | [workflows] chore(render): trust Render's proxy headers and  | app | compose_smoke, e2e, budgets |  |
| `99c0911` | [workflows] build(deps): bump gitleaks to v8.30.1 (image + C | api, web, db, app, perf | api, web, db_tools, compose_smoke, e2e, budgets, coverage | shared trigger: .github/workflows/ci.yml |
| `487236c` | [workflows] chore(deps): ignore postgres, typescript and nod | api, web, db, app, perf | api, web, db_tools, compose_smoke, e2e, budgets, coverage | shared trigger: .github/dependabot.yml |
| `e75af6c` | build(deps-dev): Bump typescript-eslint from 8.69.0 to 8.71. | api, web, db, app, perf | api, web, db_tools, compose_smoke, e2e, budgets, coverage | shared trigger: web/package-lock.json |
| `ea2b7dd` | [workflows] docs: phase-1 seeder commands, OpenAPI metadata  | docs only | none |  |
| `be83b19` | Merge pull request #6 from llevintza/phase-1/data | api, web, db, app, perf | api, web, db_tools, compose_smoke, e2e, budgets, coverage | shared trigger: CreditDesk.slnx |
```

**How to reproduce:** `node perf/ci-changes-replay.mjs 15` on a checkout with `origin/main` fetched. Per-job durations: `gh api repos/llevintza/credit-desk-analytics/actions/runs/<id>/jobs`, then `completed_at − started_at` per job.

## Decision

Option 4. A `changes` job runs `_base/.github/scripts/ci-changes.mjs` on `pull_request` only, and each heavy job skips only when its own `run_*` flag is exactly `false`. Final mapping:

| Area | Paths | Jobs |
|---|---|---|
| `api` | `src/Desk.Api/**`, `src/Desk.Data/**`, `src/Desk.UserAdmin/**`, `tests/Desk.Api.Tests/**` | `api`, `coverage` |
| `web` | `web/**` | `web`, `coverage` |
| `db` | `src/Desk.Data/**`, `src/Desk.Seeder/**`, `tests/Desk.Data.Tests/**`, `tests/Desk.Seeder.Tests/**` | `api`, `db-tools`, `coverage` |
| `app` | `deploy/**`, `e2e/**`, `render.yaml`, and any `api`, `web` or `db` change | `compose-smoke`, `e2e`, `budgets` |
| `perf` | `perf/**` | `budgets` |

The paths are classified in this order, and the first match wins:

1. Docs set no flag: `*.md` anywhere, including under `.github/`, plus `docs/**` and `.claude/skills/**`.
2. Shared triggers set every flag: `.github/**`, `Directory.*.props|targets`, `global.json`, `dotnet-tools.json`, `nuget.config`, `*.sln`/`*.slnx`, `package-lock.json`, `packages.lock.json`, `Dockerfile*`, `.dockerignore`, `docker-compose*.yml`/`compose*.yml`, `perf/coverage-*`, `tests/testconfig.json` and `.gitleaks.toml`.
3. Area paths set their own flags.
4. Anything else sets every flag. An empty diff also sets every flag.

The cross-area dependencies TC raised are all applied:

- `api` runs on `db`, because the job runs the Data and Seeder suites, then migrates and seeds.
- `app` includes `db`, because migrations change runtime.
- `budgets` runs on `app || perf`: `app` covers `api`, `web` and `db`.

`coverage` takes `--suites` from the flags. The gate (base-sourced, as before) skips the overall, diff and measured-baseline checks for a suite that didn't run. A skipped suite's committed baseline must still not drop below the floor. Three cases fail closed:

- a suite that ran with no coverage data;
- a skipped suite whose sources changed;
- an override without every suite measured.

## Consequences

- Docs-only PRs (#166's shape) finish in under a minute with every required check reporting.
- Every job still runs on push to `main`, because the classifier isn't invoked and the flags are empty. `deploy.yml` doesn't change. It still waits for a successful CI `push` run on `main`.
- **Bootstrap:** this PR's own CI runs everything, for two reasons: BASE_SHA has no classifier yet, and the PR changes `.github/**`. The base gate ignores the new `--suites` argument here; both suites run, so the outcome is the same. Skipping starts with the first PR after merge.
- **Self-protection:** changing the classifier is a `.github/**` change, so the base classifier runs everything for that PR. The new rules apply only after merge.
- **Risk accepted:** a new top-level path or project matches no area, so it runs everything until someone adds it to `AREA_PREFIXES`. Over-running is the safe failure mode.
- **Risk accepted:** `.md` files under `.github/` count as docs (so #166 is docs-only). No CI job executes Markdown. `claude-review` reads AGENTS.md, but it is a separate, advisory workflow.
- **Revisit:**
  - If most PRs still run everything because of `perf/coverage-baseline.json` raises, narrow that trigger to `api + web + coverage`; the replay shows `f776902` ran all jobs for that reason.
  - Revisit as well if the `changes` job's overhead grows past ~30 s.
