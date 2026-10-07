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

**How to reproduce:** after `dotnet test` / `npm test -- --coverage`, `node perf/coverage-gate.mjs --dotnet TestResults/coverage --web web/coverage --base origin/main --baseline-ref origin/main`. Output is the job-summary table plus the JSON to commit as `perf/coverage-baseline.json`.

## Decision

Option 2.

- Test projects reference `coverlet.MTP` (not `coverlet.collector`, `Microsoft.NET.Test.Sdk`, or `xunit.runner.visualstudio`).
- Gates: new/changed coverable lines and branches ≥ 80%; overall line/branch % per project never drops vs the baseline file at the PR base (or `github.event.before` on `main`); the committed baseline must match the measured numbers so the floor only moves with the PR that earned it.
- `uses:` are commit-SHA pinned with a `# vX.Y.Z` comment. Container images used in CI/compose/Dockerfile/Testcontainers are digest-pinned. `.github/dependabot.yml` covers nuget, npm, github-actions, docker.
- Deploy/db-ops tip lookups use `commits/heads/main`. db-ops `run` re-checks the tip first. A SHA that is no longer the tip skips (neutral); a wrong ref still fails. Smoke curls take `--max-time`; every job has `timeout-minutes`. `production` concurrency: in-progress runs are not cancelled; a newer pending run cancels the older pending one.

## Consequences

- First PR against a main with no baseline file treats the floor as 0 and commits the measured numbers.
- Generated EF designer/snapshot files sit in `Desk.Data` and pull overall % down until later phases test them; the no-drop gate still applies to that honest number. Diff coverage ignores `Migrations/` because those files are generated.
- `production` environment branch policy + required reviewers, and a `main` ruleset listing the required checks, remain dashboard settings and **must** be applied before any production secret is added (ADR-0017).
- The scheduled gitleaks workflow (`--log-opts=--all`) will fail if dangling pre-rewrite commits still contain the throwaway dev password (PR #2 MUST-FIX 3). That scan is not a PR required check; rewriting history stays out of this PR.
