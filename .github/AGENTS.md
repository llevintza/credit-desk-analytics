# .github: workflows, actions and repo automation

Area file for `.github/` (workflows, composite actions, Dependabot, CODEOWNERS, PR template). The root `AGENTS.md` still applies in full. Spec: README §14. Governance detail: [`docs/agents/governance.md`](../docs/agents/governance.md).

## Every change here is governed

- Anything under `.github/` goes in its own `[workflows]` PR and gets gate clause 3 governance review. No app code in the same PR.
- `governance-paths (base)` flags these paths once it exists; it is a signal, not the control (ADR-0020 §7). CODEOWNERS is advisory only.
- Helms (CTO) signs off on `[workflows]` and rules PRs on Leo's behalf (delegated 2026-10-07); Leo can take any PR back for his own review.

## Workflow rules (gate clause 3, ADR-0020 §5, §7)

- Pin every `uses:` to a full 40-character commit SHA with a `# v…` comment.
- Keep a top-level `permissions:` block with least privilege. Never grant `statuses: write` or `checks: write`.
- `pull_request_target` is allowed only in `agents-governance.yml` (planned; arrives with the harness build PR), under Helms's scoped sign-off. Nowhere else.
- No job id or job `name:` outside `agents-governance.yml` (planned; arrives with the harness build PR) may contain `agents-drift (base)` or `governance-paths (base)`.
- Secrets live only in the `production` environment (sole exception: the capped Claude key in `claude-review`). gitleaks stays on. Nothing removed or loosened.
- Keep the `# agents-drift:` markers, `disableAllHooks` (planned; arrives with the harness build PR), the Skill/Agent/Task deny and the restore block in `claude-review.yml` ([`docs/agents/review-restore.md`](../docs/agents/review-restore.md)).
- Keep image tags in step with their pins elsewhere (e.g. the gitleaks image in `deploy/ci-images/gitleaks/Dockerfile` and the `docker run` lines in `ci.yml` and `gitleaks.yml`).

## What agents never do here

- Never dispatch, rerun, cancel, enable or disable a workflow (`deploy.yml` and `db-ops.yml` act on production). Tech Coordinator does these outside the harness.
- Never edit rulesets, environments, secrets or variables. Never rename a job that is a required check without a governance PR that says so.
- Workflows may run `curl` on the runner (e.g. `compose-smoke`); the curl/wget deny is for agent sessions. Edit those files with the edit tool, and don't search for the word from a shell.

## Commands

| Task | Command |
|---|---|
| Lint workflows | `docker run --rm -v "$PWD":/repo -w /repo rhysd/actionlint:latest` |
| Gate-script tests | `node --test --experimental-test-coverage --test-coverage-lines=80 --test-coverage-branches=80 --test-coverage-include=perf/coverage-gate.mjs perf/coverage-gate.test.mjs` |
| Change-classifier tests (#169) | `node --test --experimental-test-coverage --test-coverage-lines=80 --test-coverage-branches=80 --test-coverage-include=.github/scripts/ci-changes.mjs .github/scripts/ci-changes.test.mjs` |
| What CI would run for recent merges | `node perf/ci-changes-replay.mjs 15` |

A new top-level path, project or test project matches no area, so it runs every job until you add it to `AREA_PREFIXES` in `.github/scripts/ci-changes.mjs` (with a fixture in its test).
