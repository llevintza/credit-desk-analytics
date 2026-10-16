# ADR-0026: Protected-ref check for the CI classifier wiring tests (ruleset required workflow vs base-sourced `_target` check vs checklist)

- **Status:** Proposed (evaluation only; any ruleset change is Leo's)
- **Date:** 2026-10-16
- **Phase / PR:** cross-cutting docs (#451, follow-up to #187 and #441)

## Context

[ADR-0023](0023-per-area-ci-jobs.md) made the `changes` classifier run from `_base`, so a PR can't rewrite the rules that decide which heavy jobs it runs. The **wiring** around the classifier is not protected the same way. On `pull_request`, GitHub runs the PR's own `ci.yml`, and the tests that check that wiring (the `ci.yml: …` and `dependency checker: …` tests in `.github/scripts/ci-changes.test.mjs`, run by `gate-tests`) also come from the PR head. A PR could:

- point `CLASSIFIER` at the head copy instead of `_base`;
- change a heavy job's `!= 'false'` to `== 'true'`, so a job skips whenever the classifier fails or is missing;
- drop `!cancelled()`, so a dependent job silently doesn't run;
- weaken or delete the tests in the same diff, so `gate-tests` still passes.

#441 (merged, `Fixes #187`) made **review** the control: a five-item reviewer checklist in `.github/AGENTS.md`. #451 asks whether a stronger control is possible: a repository-ruleset **required workflow** that runs a protected-ref copy of the wiring tests, whatever the PR head's `ci.yml` says.

Constraints:

- **Agents never change rulesets or settings.** This ADR evaluates and documents. It adds no workflow and changes no setting; Leo makes any ruleset change.
- **The repo.** `llevintza/credit-desk-analytics` is owned by a **personal account** (not an organization) and is **public**. On 2026-10-16 it had **no ruleset** and **no branch protection** on `main` (read-only checks: `gh repo view --json owner,isInOrganization,visibility` gave `isInOrganization: false`, `visibility: PUBLIC`; the rulesets listing was empty; branch protection returned 404 "Branch not protected"). The `main` ruleset (DEP-R1 in [`docs/agents/governance.md`](../agents/governance.md)) is still PENDING.
- **Free tier.** Actions minutes must stay free. `pull_request_target` is allowed only in `agents-governance.yml` (planned; arrives with the harness build PR), under Helms's scoped sign-off (`.github/AGENTS.md`).

### How GitHub ruleset required workflows work (GitHub docs, fetched 2026-10-16)

- **Where they exist.** The "Require workflows to pass before merging" rule is documented only in the **GitHub Enterprise Cloud** version of [Available rules for rulesets](https://docs.github.com/en/enterprise-cloud@latest/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets#require-workflows-to-pass-before-merging). The same page for Free, Pro and Team ([github.com version](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/available-rules-for-rulesets)) has no such section. The GHEC page says ruleset workflows "can be configured at the organization or enterprise level". GitHub's launch post ([Enforcing code reliability by requiring workflows with GitHub repository rules](https://github.blog/2023-10-11-enforcing-code-reliability-by-requiring-workflows-with-github-repository-rules)) says "GitHub Enterprise Cloud customers can require that certain workflows need to run successfully" in "an organization's repositories". Org-wide rulesets themselves need GitHub Team or Enterprise ([About rulesets](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets)).
- **This repo can't use them.** A personal-account repository has no organization ruleset, so the rule isn't available here on any plan. The REST schema for repository rulesets ([Repository rules](https://docs.github.com/en/rest/repos/rules)) does list `workflows` among the rule types, but it shares one rule schema with the org endpoint. That listing isn't evidence the rule works on a user-owned repo, and the UI docs say otherwise. Leo can confirm with a read-only look (Steps, option A) without saving anything.
- **Which ref it runs from.** The org ruleset names the workflow by `repository_id` and `path`, plus an optional `ref` ("the ref (branch or tag) of the workflow file to use") or `sha` ("the commit SHA of the workflow file to use") ([Organization rules REST](https://docs.github.com/en/rest/orgs/rules)). The launch post says you can "choose a workflow by specific branch, tag, current commit or specific SHA". That pin is the protection: the PR can't edit the workflow that judges it. Pinning a SHA means bumping the ruleset whenever the tests change.
- **Events.** A ruleset workflow must list `pull_request`, `pull_request_target` or `merge_group` under `on:`. **Filters are ignored** (branches, paths, `types`). It always runs on the default activity types: `opened`, `synchronize`, `reopened` for the PR events, and `checks_requested` for `merge_group` (GHEC "Supported event triggers"). On `pull_request` the run is in the merge-commit context (`GITHUB_SHA` is the last merge commit on `refs/pull/N/merge`; [Events that trigger workflows](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows)). The pinned workflow file runs, but any `actions/checkout` without `ref:` checks out the merge commit, which is the PR's code. A protected-ref copy of the **tests** therefore needs an explicit checkout of the protected ref, as in the draft below.
- **Other rules.** Applying the rule blocks direct pushes to the targeted branches, so it belongs only on rulesets for branches changed through PRs. "Evaluate" mode is Enterprise-only (`enforcement: evaluate` "is only available with GitHub Enterprise", REST docs).

### `pull_request_target` (what this repo *can* use)

[Events that trigger workflows](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows): `pull_request_target` "runs in the context of the default branch of the base repository": `GITHUB_SHA` is the last commit on the default branch, and the workflow file is the default branch's copy. A PR can't edit the workflow that runs for it. Its default activity types are `opened`, `synchronize`, `reopened`, and repo workflows may set `types:`. The docs warn: "Avoid using this event if you need to build or run code from the pull request." The draft below never executes head code. It reads the head's `ci.yml` as **data** with `git show`.

## Options considered

1. **A. Org ruleset required workflow** (what #451 names). Transfer the repo to an organization on GitHub Enterprise Cloud. Add an org ruleset with "Require workflows to pass before merging" that points at the wiring workflow at a pinned `sha` in a source repo (this repo's `main` or a separate, locked-down repo).
2. **B. Base-sourced `pull_request_target` job plus a repository ruleset required status check** (the same protection within this repo's plan). A job `ci-wiring (base)` runs from the default branch's workflow file, checks out the PR's base SHA, overlays the PR head's `ci.yml` as data, and runs the base copy of the wiring tests. Leo's `main` ruleset (DEP-R1) lists `ci-wiring (base)` as a required status check with the source set to GitHub Actions. It goes into `agents-governance.yml` when that workflow lands, where `pull_request_target` is already allowed under Helms's sign-off.
3. **C. CODEOWNERS plus "Require review from Code Owners"** on `.github/workflows/ci.yml` and `.github/scripts/ci-changes*.mjs`.
4. **D. Status quo: the #441 reviewer checklist** in `.github/AGENTS.md`, applied by the Orca reviewer (advisory) and Leo (merges).

## Evaluation

**Does a protected copy of the tests catch the weakenings?** Simulated locally (A and B run the same commands): only `ci-changes.mjs` and `ci-changes.test.mjs` from `main` at `3d55669`, plus the "head" `ci.yml` copied next to them, then the wiring subset.

| Head `ci.yml` | Wiring subset (8 tests) | Failing test |
|---|---|---|
| unchanged (`3d55669`) | 8 pass, 0 fail, 66 ms | none |
| `run_e2e != 'false'` → `== 'true'` | 7 pass, **1 fail**, 127 ms | `ci.yml: each heavy job needs changes and skips only on its own flag being 'false'` |
| `CLASSIFIER=_base/…` → head copy | 7 pass, **1 fail**, 103 ms | `ci.yml: no workflow-level paths filters; classifier runs from the base checkout on PRs only` |

Removing a test or an assertion from the head copy has no effect on A or B, because the tests come from the protected ref. That is the gap D can't close by itself. The subset needs nothing else from the repo (no `CreditDesk.slnx`, no test projects), so the protected copy is three files.

| Criterion | A. Org required workflow | B. `_target` job + required check | C. CODEOWNERS | D. Checklist (#441) |
|---|---|---|---|---|
| Available for this user-owned public repo | **no**: org/enterprise only, GHEC | **yes**: repository ruleset + `pull_request_target` | yes, but see next row | yes (in place) |
| Workable with one human owner | n/a | yes | **no**: agents push as `llevintza` and GitHub doesn't let a PR's author approve it (`.github/CODEOWNERS` header), so every such PR needs Leo's bypass | yes |
| Tests come from a ref the PR can't edit | yes (pinned `sha`/`ref`) | yes (workflow: default branch; tests: `pull_request.base.sha`) | n/a (people, not tests) | no (head) |
| Catches a head weakening of ci.yml (measured above) | yes | yes | only if the reviewer spots it | only if the reviewer spots it |
| Spoofable by a same-named head job | not documented; the rule is tied to the workflow, not a check name | **yes, residual**: a required status check is matched by name, "Required status checks do not take workflow, matrix, or event trigger types into account" ([Troubleshooting rules](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/troubleshooting-rules)); same as U18 for `agents-drift (base)` | n/a | n/a |
| Legit wiring change (rename/add a heavy job) | blocks until the pinned ruleset `sha` is bumped | blocks until the tests change on `main` first (two-step PR) or Leo's break-glass | review only | review only |
| Extra runner time per PR | ≈ 10 s | ≈ 10 s (like `gate-tests`: 9–10 s in ADR-0023) | 0 | 0 |
| Actions minutes cost | 0 (public repo) | 0 (public repo) | 0 | 0 |
| Money / migration cost | GHEC seat plus a repo transfer (Pages URL, Render deploy hook, `gh` remotes, Cursor/Orca app installs) | none | none | none |
| Settings work for Leo | org, plan, transfer, org ruleset | one required check on the DEP-R1 `main` ruleset | ruleset "Require review from Code Owners" | none |

**Minutes and the `changes` job.** Standard GitHub-hosted runners are free for public repositories ([GitHub Actions billing](https://docs.github.com/en/billing/concepts/product-billing/github-actions)), so A and B cost no minutes. On a private repo on Free, the 2,000-minute allowance would cover about 12,000 runs at ≈ 10 s each. The new check is **independent of `changes`**: it has no `needs:` and no `if:`, it always runs and it always reports. A required check must never be skippable (governance.md, ruleset sync invariant). It doesn't reuse the classifier's flags, because the thing it judges is the wiring that consumes them. A docs-only PR still gets the check, at ≈ 10 s.

**Events.** `ci.yml` listens to `pull_request` `[opened, synchronize, reopened, edited]`. Option B uses `pull_request_target` `[opened, synchronize, reopened]`. `edited` changes no file, so the check on the head SHA stays valid. Under A, `types:` would be ignored anyway. B doesn't touch `ci.yml`, so the existing guard `assert.doesNotMatch(CI, /pull_request_target/)` in `ci-changes.test.mjs` still holds. Neither A nor B runs on `push` to `main`. There, `gate-tests` already runs the full suite on the merged tree.

**How to reproduce** (any checkout with `origin/main` fetched; `<S>` is a scratch directory; Node 22 or later):

```
mkdir -p <S>/protected/.github/workflows <S>/protected/.github/scripts
git show origin/main:.github/scripts/ci-changes.mjs > <S>/protected/.github/scripts/ci-changes.mjs
git show origin/main:.github/scripts/ci-changes.test.mjs > <S>/protected/.github/scripts/ci-changes.test.mjs
git show origin/main:.github/workflows/ci.yml > <S>/head-ci.yml
sed "s/needs.changes.outputs.run_e2e != 'false'/needs.changes.outputs.run_e2e == 'true'/" <S>/head-ci.yml > <S>/weak1.yml
sed "s#CLASSIFIER=_base/.github/scripts/ci-changes.mjs#CLASSIFIER=.github/scripts/ci-changes.mjs#" <S>/head-ci.yml > <S>/weak2.yml
cp <S>/weak1.yml <S>/protected/.github/workflows/ci.yml   # repeat with head-ci.yml and weak2.yml
node --test --test-name-pattern='^(ci\.yml: |dependency checker: )' <S>/protected/.github/scripts/ci-changes.test.mjs
```

```
== head-ci        ℹ pass 8  ℹ fail 0  ℹ duration_ms 65.631291
== weak1          ✖ ci.yml: each heavy job needs changes and skips only on its own flag being 'false'
                  ℹ pass 7  ℹ fail 1  ℹ duration_ms 127.002417
== weak2          ✖ ci.yml: no workflow-level paths filters; classifier runs from the base checkout on PRs only
                  ℹ pass 7  ℹ fail 1  ℹ duration_ms 102.521584
```

Machine: Apple Silicon laptop, Node v24.18.0, 2026-10-16. The runner-time estimate (≈ 10 s) is the `gate-tests` job duration in ADR-0023, which runs the same file plus more, after checkout and `setup-node`.

### Draft workflow (option B; not committed as a file)

It goes in `agents-governance.yml` as one more job when that workflow lands, or as its own workflow if Helms extends the `pull_request_target` allowance. Under option A the same steps work as an org required workflow. Change `on:` to `pull_request` (ruleset workflows ignore `types:`) and keep the explicit base checkout, because a plain checkout there would be the PR's merge commit.

```yaml
name: CI wiring (base)

# #451: base-sourced copy of the ci.yml wiring tests. pull_request_target runs this
# file from the default branch, and the tests come from the PR's base SHA, so a PR
# can't weaken either. The head ci.yml is read as data (git show), never executed.
on:
  pull_request_target:
    types: [opened, synchronize, reopened]
    branches: [main]

permissions:
  contents: read

concurrency:
  group: ci-wiring-${{ github.event.pull_request.number }}
  cancel-in-progress: true

jobs:
  ci-wiring:
    # Required status check on the main ruleset. No needs, no if: it always reports.
    name: ci-wiring (base)
    runs-on: ubuntu-24.04
    timeout-minutes: 5
    permissions:
      contents: read
    steps:
      - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1 # v7.0.1
        with:
          ref: ${{ github.event.pull_request.base.sha }}
          persist-credentials: false
      - uses: actions/setup-node@949feb2413d6458794dcd2491c4babbbce0c15c1 # v7.1.0
        with:
          node-version: 22
      - name: Overlay the PR head's ci.yml (data only)
        env:
          HEAD_SHA: ${{ github.event.pull_request.head.sha }}
          PR_NUMBER: ${{ github.event.pull_request.number }}
        run: |
          git fetch --no-tags --depth=1 origin "refs/pull/${PR_NUMBER}/head"
          git cat-file -e "${HEAD_SHA}^{commit}"
          git show "${HEAD_SHA}:.github/workflows/ci.yml" > .github/workflows/ci.yml
          echo "Base tests \`${{ github.event.pull_request.base.sha }}\` against head ci.yml \`${HEAD_SHA}\`." >> "$GITHUB_STEP_SUMMARY"
      - name: Wiring tests from the base SHA
        run: >
          node --test --test-name-pattern='^(ci\.yml: |dependency checker: )'
          .github/scripts/ci-changes.test.mjs
```

Notes on the draft:

- actionlint 1.7.12 (the image `ci.yml` pins, with its bundled shellcheck) reports no findings on this block, read from stdin: exit 0.

- `git cat-file -e` fails the job if the fetched head isn't the event's head SHA (for example, a push landed between the event and the fetch). The next `synchronize` reruns it.
- A head that deletes `ci.yml` makes `git show` fail, which fails closed.
- The job never checks out, installs or runs head code, so the `pull_request_target` warning doesn't apply. It holds no secrets and its token is read-only.
- Fork PRs work the same way (the repo is public), since `refs/pull/N/head` is fetchable without credentials.
- `${{ github.event.pull_request.base.sha }}` in the summary line is a 40-hex SHA set by GitHub, not PR-controlled text.

## Decision

Proposed, for Leo and Helms to rule on:

1. **Option A is not available**: ruleset required workflows are an organization/enterprise (GHEC) feature, and this repo is owned by a personal account. Moving to an org on GHEC to get it costs money and a repo transfer for a P3 hardening. Not recommended.
2. **Recommend option B** as the protected-ref control. Add the `ci-wiring (base)` job to `agents-governance.yml` in the harness build PR (or in its own `[workflows]` PR once Helms extends the `pull_request_target` allowance). When Leo creates the DEP-R1 `main` ruleset, he adds `ci-wiring (base)` to its required status checks.
3. **Keep option D** (the #441 checklist) in any case. It stays the control until B is live, and afterwards it covers what B can't: a same-named head job, and changes to the tests themselves that land on `main`.
4. **Reject option C**: with agents pushing as `llevintza`, required code-owner review can't be satisfied without Leo's bypass on every such PR.

## Consequences

- **Easier:** a PR can't weaken the `changes`/heavy-job wiring *and* its tests in one diff. The base copy of the tests judges it, and the check is cheap (≈ 10 s, free on a public repo).
- **Harder:** a PR that legitimately changes the wiring (adds, renames or removes a heavy job or flag) fails `ci-wiring (base)` until the tests on `main` accept the new shape. Make those changes in two PRs (tests tolerant first, then the wiring), or use Leo's documented break-glass. This is the same "tightened base invariant" cost as `agents-drift (base)` (governance.md).
- **Residual risk:** a required status check is matched by name, so a head `ci.yml` job named `ci-wiring (base)` could satisfy it. Reserve the name the way `agents-drift (base)` and `governance-paths (base)` are reserved: `.github/AGENTS.md` rule plus a drift fixture. Reviewers treat two runs with the same check name as a fail (U18).
- **Ruleset sync:** `ci-wiring (base)` becomes one more literal name in the DEP-R1 required list (governance.md "Ruleset sync"). Renaming it fails closed until Leo updates the ruleset.
- **Revisit** if the repo moves to an organization on GHEC (option A then works, with no name spoofing), if GitHub makes ruleset workflows available to personal-account repositories, or if `agents-governance.yml` is dropped.

## Steps Leo would take

Agents do none of these. Every step is a settings change or a `[workflows]` PR that Leo or Helms approves.

**Option B (recommended):**

1. Approve a `[workflows]` PR that adds the `ci-wiring (base)` job from the draft above. Put it in `agents-governance.yml` when that file lands; otherwise use a standalone workflow, with Helms's sign-off extending the `pull_request_target` allowance in `.github/AGENTS.md`. The same PR reserves the job name (`.github/AGENTS.md` rule plus a test in `workflow-hardening.test.mjs`).
2. After it merges, open any PR (for example, a docs PR) and confirm a `ci-wiring (base)` check run appears on the head SHA and passes.
3. Settings → Rules → Rulesets → the `main` ruleset (create it per DEP-R1 if it doesn't exist yet: target `refs/heads/main` by name, enforcement Active, empty bypass list):
   - Enable **Require status checks to pass**, then **Add checks** → `ci-wiring (base)`, with the source set to **GitHub Actions**.
   - Save, and give Tech Coordinator the new `updated_at` (governance.md "Ruleset sync").
4. Expect open PRs to need a new push (or a `main` merge) before the check reports on them.
5. Canary: on a throwaway branch, change one heavy job's `!= 'false'` to `== 'true'` in `ci.yml` and open a draft PR. `ci-wiring (base)` must fail, and merging must be blocked. Close the canary.

**Option A (for reference; only after a move to an org on GHEC):**

1. Read-only check first: on this repo, open Settings → Rules → Rulesets → New branch ruleset and look for **Require workflows to pass before merging**. If it isn't listed, option A needs the org move. Discard the draft without saving.
2. Create or choose an organization on GitHub Enterprise Cloud and transfer the repository. Then update the Pages URL, the Render deploy hook, git remotes and the app installations (Cursor, Orca).
3. Commit the draft workflow with `on: pull_request` to the source repository. Use the same visibility as the target (public works for any repo in the org), and allow Actions access from outside the repository if the source is private or internal.
4. Org Settings → Repository → Rulesets → New branch ruleset: target this repository's `main`, add **Require workflows to pass before merging**, pick the source repository, the path and a **pinned SHA**, then set enforcement to Active (or Evaluate first, GHEC only).
5. Push to each open PR to trigger the first run (the docs say a new rule doesn't run on already-open PRs until a new commit, a branch update or a reopen). Bump the pinned SHA whenever the wiring tests change.
