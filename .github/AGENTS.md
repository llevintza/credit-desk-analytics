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
- A script that decides or polices a PR's checks runs from the `_base` checkout, never the head copy: the classifier, the coverage gate and the PR-time Pages script check (#278). A base without the script fails closed or runs every job; it never falls back to the head.
- Keep image tags in step with their pins elsewhere (e.g. the gitleaks image in `deploy/ci-images/gitleaks/Dockerfile` and the `docker run` lines in `ci.yml` and `gitleaks.yml`). `gate-tests` fails when they disagree (`ci-image-pins.test.mjs`, #255).

## Reviewer checklist: the `changes` job and heavy-job wiring (#187)

The classifier runs from `_base`, but on `pull_request` the PR's own `ci.yml` executes, and the wiring tests run from the PR head too. Base-sourcing can't stop a PR from rewiring the skips or weakening the tests, so review is the control ([ADR-0023](../docs/adr/0023-per-area-ci-jobs.md)). For any diff that touches the `changes` job, or a heavy job's `needs:` or `if:`, the reviewer (the Orca reviewer, advisory, and Leo, who merges) checks every item and records the result in a review comment:

- [ ] Skips happen only on an exact `'false'`: every heavy job's `if:` uses `needs.changes.outputs.<flag> != 'false'`, never `== 'true'` or a truthiness test.
- [ ] `CLASSIFIER=_base/.github/scripts/ci-changes.mjs`, with `_base` checked out at `github.event.pull_request.base.sha`. Never the head copy.
- [ ] One pinned diff base (#213, #278): the `changes` job's `base` step has no `if:` (it runs on push too), sets `base_sha`, and writes a `::warning::` whenever a PR keeps the event base. The classifier gets `--base "$DIFF_BASE"` from `steps.base.outputs.base_sha`. The coverage gate's `DIFF_BASE` is `needs.changes.outputs.base_sha` with the event base only as the fallback, and writes a `::warning::` when that output is empty. `_base` (classifier rules, gate script, thresholds, floor) stays at the event base, never at the pinned SHA. The `#213` and `#278` tests in `ci-changes.test.mjs` are ci.yml wiring tests under the last item.
- [ ] Every job that needs `api` or `web` has a job-level `if:` with `!cancelled()` plus `contains(fromJSON('["success", "skipped"]'), needs.<job>.result)` for each of `needs.api.result` and `needs.web.result` it needs.
- [ ] No workflow-level `paths:` or `paths-ignore:` under `on:`.
- [ ] The ci.yml wiring tests in `.github/scripts/ci-changes.test.mjs` (the `ci.yml: …` tests and the dependency checker) are unchanged or only strengthened: no assertion removed, loosened or skipped.

Any unchecked item means request changes.

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
| CI/Deploy concurrency wiring tests (#207) | `node --test .github/scripts/concurrency-wiring.test.mjs` |
| k6 handleSummary and budgets jq (#271) | `node --test .github/scripts/k6-summary.test.mjs` |
| Workflow hardening tests (#137, #139, #155, #256, #257) | `node --test .github/scripts/workflow-hardening.test.mjs` |
| Perf jobs wiring and wire-bytes check (#198, #348) | `node --test .github/scripts/perf-jobs.test.mjs perf/bundle-wire-bytes.test.mjs` |
| CI image pin agreement (#255) | `node --test .github/scripts/ci-image-pins.test.mjs` |
| Seeder cap wiring tests (#224) | `node --test .github/scripts/seed-cap-wiring.test.mjs` |
| Pages untrusted-script check tests (#355) | `node --experimental-vm-modules --test .github/scripts/pages-script-check.test.mjs` |
| Pages untrusted-script check on a build (#355) | `node --experimental-vm-modules .github/scripts/pages-script-check.mjs web/dist/web/browser --base / --no-csp` |
| Deploy Swagger/OpenAPI exposure smoke (#110) | `node --test .github/scripts/swagger-exposure-smoke.test.mjs` |
| Cloudflare IP range comparison, fixture only (#161) | `node --test --experimental-test-coverage --test-coverage-lines=80 --test-coverage-branches=80 --test-coverage-include=.github/scripts/cloudflare-ips.mjs .github/scripts/cloudflare-ips.test.mjs` (never run `cloudflare-ips.mjs` itself in a session: its fetch belongs to the weekly `cloudflare-ips.yml`) |
| What CI would run for recent merges | `node perf/ci-changes-replay.mjs 15` |

A new top-level path, project or test project matches no area, so it runs every job until you add it to `AREA_PREFIXES` in `.github/scripts/ci-changes.mjs` (with a fixture in its test).
