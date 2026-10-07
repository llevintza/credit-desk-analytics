# ADR-0017: Deploy-path safety (pipefail, main-only release, step-scoped DATABASE_URL)

- **Status:** Accepted
- **Date:** 2026-10-07
- **Phase / PR:** phase-0 follow-up (#4)

## Context

ADR-0016 put migrate → seed → Render hook → smoke in GitHub Actions so a failed migration never takes the site down (README §14.2, §14.4). A post-merge review of PR #2 found that guarantee was not actually enforced, and that the privileged deploy path could be reached from non-main code:

1. Piped `migrate | tee` / `seed | tee` ran under `bash -e` **without** `pipefail`, so the step exit code was `tee`'s (always 0). A failed migration still seeded and still called the Render hook. Seeder safety exits (1 = pending migrations, 2 = over the 400 MB budget) were swallowed the same way. The pattern was in `deploy.yml`, `db-ops.yml`, and `ci.yml`.
2. `workflow_run` `branches: [main]` matches the **triggering run's `head_branch`**, so a `pull_request` CI run from a branch named `main` (including a fork's `main`) also starts Deploy. `workflow_dispatch` had no ref check. The release job then checked out that SHA with Neon `DATABASE_URL` in job env.
3. `DATABASE_URL` was injected at job level, so checkout, tool-build, curl, and smoke steps all received the production secret.

Production secrets are still unset (README §13.3); this must land before anyone configures them. No real deploy is triggered by this change.

## Options considered

1. **Leave the workflows as merged in PR #2.** Relies on operators never hitting a failing migration and on `branches: [main]` being a sufficient gate.
2. **`set -o pipefail` only in the tee steps; keep `workflow_run` as the only filter; leave job-level `DATABASE_URL`.** Smallest diff, easy to miss the next piped command, and the ref/secret issues remain.
3. **Workflow `defaults.run.shell: bash` (enables `-eo pipefail`), explicit `if` guards on every job that uses the `production` environment or `DATABASE_URL`, and step-scoped `DATABASE_URL`.** Keep `branches: [main]`, `permissions: contents: read`, no `pull_request_target`, secrets only in the `production` environment.

## Evaluation

| Criterion | 1. As merged | 2. Local pipefail only | 3. Defaults + guards + scoped secret |
|---|---|---|---|
| Failed `cmd \| tee` fails the step | No (`tee` exits 0) | Yes, in the steps we remember | **Yes, every `run` step** |
| Seeder exits 1/2 stop deploy | No | Yes, if those steps stay piped | **Yes** |
| Deploy from a PR whose head branch is named `main` | Yes | Yes | **No** (requires `workflow_run.event == 'push'` + same-repo + `head_branch == 'main'`) |
| Dispatch from a non-main ref | Yes | Yes | **No** (`github.ref == 'refs/heads/main'`; otherwise a no-secrets job fails loudly) |
| `DATABASE_URL` on checkout / tool-build / smoke | Yes | Yes | **No** (migrate/seed/run steps only) |
| Existing checks loosened | n/a | No | **No** |

**How to reproduce (pipefail):** `bash perf/pipefail-demo.sh`

```
=== GitHub default Linux shell: bash -e (no pipefail) ===
pipeline_status=0 (tee won)
script_exit=0

=== GitHub shell: bash (bash --noprofile --norc -eo pipefail) ===
script_exit=1
```

GitHub's unspecified Linux default is `bash -e {0}`. Setting `defaults.run.shell: bash` switches to `bash --noprofile --norc -eo pipefail {0}`. Job-level `defaults.run` replaces the workflow-level map, so `ci.yml`'s `web` job repeats `shell: bash` next to `working-directory: web`.

The `if` guards are expressions on `github.event.workflow_run.{event,head_branch,head_repository.full_name,conclusion}` and `github.ref`; they are not performance-sensitive. They cannot be exercised against Neon/Render here (secrets are not configured; this PR must not fire a real deploy).

## Decision

Option 3.

Deploy/release (and any job that receives `DATABASE_URL`) runs only when:

- **`workflow_run`:** `conclusion == success` **and** `event == 'push'` **and** `head_branch == 'main'` **and** `head_repository.full_name == github.repository`
- **`workflow_dispatch`:** ref is exactly `refs/heads/main`
- **SHA** (case-sensitive bash in the no-secrets `gate` job, repeated at the start of `release`): equals the current tip of `main` (`gh api repos/$GITHUB_REPOSITORY/commits/main --jq .sha`). Re-running an old CI or deploy run is refused.

GitHub expression `==` is case-insensitive, so `MAIN` would pass `== 'main'`. The `gate` job uses `[ "$REF" = refs/heads/main ]` / `[ "$HEAD_BRANCH" = main ]` with those values passed through `env:` (not interpolated into `run:`). `db-ops.yml` is dispatch-only and uses the same gate.

## Consequences

- A failed migration or seed fails its step and stops the job in CI, deploy, and db-ops. The running app keeps serving the old schema, which is the ADR-0016 / §14.4 promise.
- `workflow_run` still *starts* for any completed CI run whose `head_branch` is `main` (GitHub's filter cannot see `event` or the head repository). Jobs that use the `production` environment `need` the no-secrets `gate` job. A human **must** set the GitHub `production` environment to the `main` branch **before any production secret is added**: `workflow_dispatch` runs the selected ref's YAML, so a branch that deletes the gate would otherwise still see those secrets. **No required reviewer** exists on `production`; merges to `main` auto-deploy. Controls are the pre-merge review gate, required status checks, `deploy.yml` migrate/smoke, and README §14.4. A ruleset on `main` (required status checks only — `secrets`, `api`, `web`, `coverage`, `compose-smoke`, `workflows`, `db-tools`, `gate-tests`; squash only; no force-push or deletion of `main`; **no required approving reviews**) is the same class of dashboard setting. Those cannot be expressed in workflow YAML. Every bot and Claude Code acts as the repo owner and cannot self-approve, so a required PR review would deadlock every merge.
- SHA-pinning Actions, Dependabot, coverage, and a scheduled full-history gitleaks scan stay out of this PR; they landed in ADR-0018.
