# ADR-0018: Coverage gates, action pinning, and CI hardening

- **Status:** Accepted
- **Date:** 2026-10-07
- **Phase / PR:** phase-0 follow-up (coverage + CI hardening)

## Context

PR #2's post-merge review and PR #4's re-review left a cluster of CI/governance items that are not app features: no coverage collection (coverlet.collector cannot run on Microsoft.Testing.Platform), no in-repo coverage gates, tag-pinned Actions (Node 20 runtime), unpinned images, no Dependabot, `web/tsconfig.json` not strict, and a handful of deploy-path residuals (N1–N7). Thresholds must live in git, not a GitHub UI.

## Options considered

1. **VSTest + coverlet.collector / Codecov app.** Switch the runner back so the existing package works, and keep gates in a SaaS dashboard.
2. **coverlet.MTP (cobertura) + Vitest `@vitest/coverage-v8` (lcov) + an in-repo gate script.** Diff coverage vs the PR base; overall floor vs `perf/coverage-baseline.json` at that base. SHA-pin `uses:` and digest-pin images; Dependabot updates the pins.
3. **Microsoft.Testing.Extensions.CodeCoverage** instead of coverlet.MTP. Same MTP shape; Cobertura is supported. Closed-source coverage engine.

## Evaluation

| Criterion | 1. VSTest + Codecov | 2. coverlet.MTP + in-repo gates | 3. MS CodeCoverage |
|---|---|---|---|
| Works with `global.json` MTP runner | No (collector is VSTest-only) | **Yes** (`dotnet test --coverlet`) | Yes (`--coverage`) |
| Thresholds in git | No (UI) | **Yes** (`perf/coverage-thresholds.json`) | Yes, if we still write the gate |
| Diff + no-drop vs main | Codecov patch/project | **Script vs base SHA + committed baseline** | Same script |
| Reproducible locally | Needs token | **`node perf/coverage-gate.mjs`** | Same |
| License | n/a | Apache-2.0 | MS .NET library (free, closed) |

Action majors with a Node 24 runtime (checkout v7, setup-dotnet v6, setup-node v7, upload-artifact v7, download-artifact v8) replace the Node 20 deprecation warnings on v4. Images are pinned by digest at the same tags already in use (`gitleaks:v8.30.0`, `postgres:17-alpine`, `node:22-alpine`, `dotnet/sdk:10.0`, `dotnet/aspnet:10.0`).

**How to reproduce:** after `dotnet test` / `npm test -- --coverage`, `node perf/coverage-gate.mjs --dotnet TestResults/coverage --web web/coverage --base origin/main`. Output is the job-summary table plus the JSON to commit as `perf/coverage-baseline.json`. The floor and thresholds used by the gate are those at `--base`, not the working tree.

## Decision

Option 2.

- Test projects reference `coverlet.MTP` (not `coverlet.collector`, `Microsoft.NET.Test.Sdk`, or `xunit.runner.visualstudio`). Coverlet include is `[Desk.*]*` so a new `src/Desk.*` project is measured instead of silently skipped.
- Gates (`perf/coverage-gate.mjs`):
  - Thresholds, tolerance, and `coverage-baseline.json` are loaded with `git show $BASE_SHA:…`. The PR head cannot lower a min, turn off `overallMustNotDrop`, widen the tolerance, or drop the committed floor below the base.
  - Diff coverage is vs `git merge-base $BASE_SHA HEAD`. Overall no-drop is vs the baseline **at BASE_SHA**.
  - **Bootstrap (this PR only):** `main` has neither file. The gate uses hardcoded defaults (diff ≥ 80/80, `overallMustNotDrop: true`, tolerance 0.5) and a 0/0 floor while this PR writes the files. After merge, future PRs read main's copies and cannot set their own floor. Only a push to `main` ratchets the baseline, and only upward.
  - Unresolvable BASE_SHA / merge-base / `git show` / `git diff` **fails closed**. Missing base is never floor 0 or "no changed lines".
  - Coverage is keyed by repo-relative path (`src/<Project>/…`, `web/src/…`). A Cobertura filename that cannot be canonicalized fails the job; there is no basename merge (`Desk.Api/Program.cs` vs `Desk.Seeder/Program.cs`).
  - A changed `src/` `.cs` or `web/src/` `.ts` (except specs, tests, `Migrations/`) with no coverage data counts as 0% toward the diff gate; it is never skipped.
  - No-drop compares at 1-decimal precision with a small epsilon. The committed baseline must match measured at that precision (round-down on the behind check so the floor cannot be parked below real coverage). `baselineMatchTolerancePercent` stays 0.5 and a PR may not widen it.
  - The committed floor numbers are the CI-measured 1-decimal overalls from the path-keyed reports (not a local merge of old artifacts).
- `uses:` are commit-SHA pinned with a `# vX.Y.Z` comment. Container images used in CI/compose/Dockerfile are digest-pinned. Testcontainers' **postgres** image is digest-pinned; **Ryuk** is chosen by the Testcontainers library version (the NuGet pin is Dependabot-covered; the Ryuk tag is not). `.github/dependabot.yml` covers nuget, npm, github-actions (`/` and `/.github/actions/build-db-tools`), docker (`/deploy` plus `deploy/ci-images/*` Dockerfiles that track `run:` images), and docker-compose (`/`). Images referenced only inside `run:` (gitleaks, actionlint, shellcheck) are pinned in those Dockerfiles so Dependabot's docker ecosystem can bump them; the matching `docker run` line must stay in lockstep. The Testcontainers postgres string in `SeedAppTests.cs` is a C# literal Dependabot does not scan — keep it in sync with `docker-compose.yml`.
- Deploy/db-ops tip lookups use `commits/heads/main`. db-ops `run` re-checks the tip first. A SHA that is no longer the tip skips (neutral); a wrong ref still fails. Smoke curls take `--max-time`; every job has `timeout-minutes`. `production` concurrency: in-progress runs are not cancelled; a newer pending run cancels the older pending one.

## Consequences

- **Bootstrap is a one-time exception**, documented in the gate output and README §14.1. After this PR merges, a follow-up PR that deletes or rewrites `perf/coverage-baseline.json` still has its floor read from `main` at BASE_SHA.
- Overall .NET % **includes** EF migration / designer / snapshot code when tests apply migrations (Seeder Testcontainers). That is the honest floor. Diff coverage still ignores `Migrations/` because those files are generated.
- `production` environment branch policy + required reviewers, and a `main` ruleset of **required status checks only** (`secrets`, `api`, `web`, `coverage`, `compose-smoke`, `workflows`; squash only; no force-push or deletion of `main`; **no required approving reviews**), remain dashboard settings and **must** be applied before any production secret is added (ADR-0017). Bots and Claude Code act as the repo owner and cannot self-approve.
- The scheduled gitleaks workflow fetches `refs/pull/*/head` then runs `--log-opts=--all`. That covers branches, tags, and PR heads. A clone never contains unreachable objects, so a green scan does **not** prove rewritten history is gone. The throwaway dev password from PR #2 MUST-FIX 3 **must still be rotated**. That scan is not a PR required check; rewriting history stays out of this PR.
